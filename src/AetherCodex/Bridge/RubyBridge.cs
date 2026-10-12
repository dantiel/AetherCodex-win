using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AetherCodex.Bridge;

/// <summary>
/// C# port of the macOS <c>RubyBridge.swift</c>: a Swift-to-Ruby bridge using a
/// framed JSON protocol over pipes. The daemon (<c>Ruby/standalone_daemon.rb</c>)
/// emits newline-delimited frames:
///   - {"type":"ready", ...}                          at startup
///   - {"type":"stream","method":...,"payload":...}    during execution
///   - {"type":"result","request_id":N,...,"result":...}  per command
/// A background reader dispatches frames; <see cref="SendAsync"/> awaits the
/// matching result while streaming frames are forwarded to
/// <see cref="StreamReceived"/>.
/// </summary>
public sealed class RubyBridge : IDisposable
{
    public static RubyBridge Shared { get; } = new();

    private const double RequestTimeoutSeconds = 600.0;

    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _readerTask;
    private Task? _errorReaderTask;
    private readonly object _writeLock = new();
    private readonly object _pendingLock = new();
    private int _nextRequestId;
    private readonly Dictionary<int, TaskCompletionSource<RubyBridgeResponse>> _pending = new();
    private TaskCompletionSource<bool>? _startupTcs;

    // Monotonic "daemon is alive" clock, bumped by every inbound frame. SendAsync
    // reads it as an *inactivity* timeout (not a total-turn deadline) so a long
    // multi-tool turn that keeps streaming progress never trips it.
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    private readonly StringBuilder _readerBuffer = new();
    private readonly StringBuilder _errorBuffer = new();
    private readonly List<string> _daemonLog = new();
    private const int MaxDaemonLogLines = 2000;

    /// <summary>Raised when the daemon's `ready` frame arrives.</summary>
    public event Action<IReadOnlyList<CompanionInfo>>? Ready;

    /// <summary>Raised for each `stream` frame (on the reader thread).</summary>
    public event Action<StreamEvent>? StreamReceived;

    /// <summary>Raised for non-request frames (recover_suggestion, ...).</summary>
    public event Action<DaemonEvent>? EventReceived;

    /// <summary>Raised after a wedged/timed-out turn is recovered.</summary>
    public event Action? Recovered;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<CompanionInfo> CompanionRoster { get; private set; } = Array.Empty<CompanionInfo>();
    public IReadOnlyList<string> DaemonLog => _daemonLog;

    private RubyBridge() { }

    private string RubyPath => FindRuby();

    private static string FindRuby()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "..", "Ruby33-x64", "bin", "ruby.exe"),
            @"C:\Ruby33-x64\bin\ruby.exe",
            @"C:\Ruby32-x64\bin\ruby.exe",
            @"C:\Ruby31-x64\bin\ruby.exe",
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return "ruby"; // fall back to PATH
    }

    private string DaemonScript =>
        Path.Combine(AppContext.BaseDirectory, "Ruby", "ruby", "standalone_daemon.rb");

    /// <summary>Project root passed to the daemon (resolves .aethercodex + data dirs).</summary>
    public string ProjectRoot =>
        Environment.GetEnvironmentVariable("AETHER_PROJECT_ROOT") is { Length: > 0 } env
            ? env
            : Directory.GetCurrentDirectory();

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        if (IsRunning) return;

        if (!File.Exists(DaemonScript))
            throw new InvalidOperationException($"Ruby daemon script not found at: {DaemonScript}");
        if (!File.Exists(RubyPath) && RubyPath != "ruby")
            throw new InvalidOperationException($"Ruby not executable at: {RubyPath}");

        var psi = new ProcessStartInfo
        {
            FileName = RubyPath,
            Arguments = $"\"{DaemonScript}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["AETHER_PROJECT_ROOT"] = ProjectRoot;
        // Windows does not set LANG/LC_ALL — the indexer throws KeyNotFoundException
        // ("The given key 'LANG' was not present in the dictionary.") on a missing
        // key, so guard with ContainsKey instead of relying on ??=.
        if (!psi.Environment.ContainsKey("LANG"))
            psi.Environment["LANG"] = "en_US.UTF-8";
        if (!psi.Environment.ContainsKey("LC_ALL"))
            psi.Environment["LC_ALL"] = "en_US.UTF-8";

        var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException("Failed to launch Ruby daemon.");

        _process = process;
        _stdin = process.StandardInput;
        _startupTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsRunning = true;

        _readerTask = Task.Run(() => ReaderLoop(process.StandardOutput));
        _errorReaderTask = Task.Run(() => ErrorLoop(process.StandardError));

        var startup = _startupTcs.Task;
        var winner = await Task.WhenAny(startup, Task.Delay(TimeSpan.FromSeconds(15)));
        if (winner != startup)
        {
            LastError = "Ruby daemon did not emit a ready frame within 15s.";
            throw new TimeoutException(LastError);
        }
        await startup; // rethrow any fault
    }

    public void Stop()
    {
        IsRunning = false;
        try { _stdin?.Dispose(); } catch { /* already gone */ }
        try { if (!_process?.HasExited ?? false) _process?.Kill(entireProcessTree: true); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
        _stdin = null;
        ResumePendingWithError(new InvalidOperationException("Ruby daemon stopped."));
        _startupTcs?.TrySetResult(false);
    }

    public async Task RestartAsync()
    {
        Stop();
        await StartAsync();
    }

    public void ClearDaemonLog() => _daemonLog.Clear();

    public void Dispose() => Stop();

    // ── Frame IO ───────────────────────────────────────────────────────────

    private void ReaderLoop(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                HandleLine(line);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void ErrorLoop(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lock (_daemonLog)
                {
                    _daemonLog.Add(line);
                    if (_daemonLog.Count > MaxDaemonLogLines)
                        _daemonLog.RemoveAt(0);
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void TouchActivity() =>
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    private void HandleLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return;
        JsonObject? json;
        try { json = JsonNode.Parse(trimmed) as JsonObject; }
        catch (JsonException) { return; }
        if (json is null) return;

        TouchActivity();

        var type = json["type"]?.GetValue<string>();
        switch (type)
        {
            case DaemonFrames.Type.Ready:
                var roster = new List<CompanionInfo>();
                if (json["companions"] is JsonArray arr)
                {
                    foreach (var e in arr)
                    {
                        if (e is not JsonObject o) continue;
                        var key = o["key"]?.GetValue<string>();
                        if (key is null) continue;
                        roster.Add(new CompanionInfo(
                            key,
                            o["glyph"]?.GetValue<string>() ?? "✦",
                            o["name"]?.GetValue<string>() ?? key,
                            o["temperature"]?.GetValue<double>(),
                            o["thinking"]?.GetValue<string>()));
                    }
                }
                CompanionRoster = roster;
                var tcs = _startupTcs;
                _startupTcs = null;
                tcs?.TrySetResult(true);
                Ready?.Invoke(roster);
                break;

            case DaemonFrames.Type.Stream:
                StreamReceived?.Invoke(new StreamEvent(json));
                break;

            case DaemonFrames.Type.Result:
                var response = new RubyBridgeResponse(json);
                if (json["request_id"]?.GetValue<int>() is int requestId)
                {
                    TaskCompletionSource<RubyBridgeResponse>? cont;
                    lock (_pendingLock)
                    {
                        _pending.Remove(requestId, out cont);
                    }
                    cont?.TrySetResult(response);
                }
                break;

            case DaemonFrames.Type.RecoverSuggestion:
                var suggestion = new RecoverSuggestion(
                    json["request_id"]?.GetValue<int>(),
                    json["glyph"]?.GetValue<string>() ?? "🦊",
                    json["prompt"]?.GetValue<string>() ?? "Hier riss der Faden — weiterweben?",
                    json["action"]?.GetValue<string>() ?? "recover_last_intent",
                    json["last_intent"]?.GetValue<string>());
                EventReceived?.Invoke(new RecoverEvent(suggestion));
                break;

            default:
                EventReceived?.Invoke(new OtherEvent(type ?? "unknown", json));
                break;
        }
    }

    private void ResumePendingWithError(Exception error)
    {
        List<TaskCompletionSource<RubyBridgeResponse>> snapshot;
        lock (_pendingLock)
        {
            snapshot = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var tcs in snapshot)
            tcs.TrySetException(error);
    }

    // ── Send ───────────────────────────────────────────────────────────────

    private int AllocateRequestId() => Interlocked.Increment(ref _nextRequestId);

    private void SendInterrupt()
    {
        var frame = new JsonObject
        {
            ["command"] = "interrupt",
            ["arguments"] = new JsonObject(),
            ["request_id"] = AllocateRequestId(),
        };
        WriteFrame(frame);
    }

    private void WriteFrame(JsonObject frame)
    {
        lock (_writeLock)
        {
            _stdin?.WriteLine(frame.ToJsonString(DaemonFrames.Json));
            _stdin?.Flush();
        }
    }

    public async Task<RubyBridgeResponse> SendAsync(string command, JsonObject? arguments = null)
    {
        if (!IsRunning)
            await StartAsync();

        var requestId = AllocateRequestId();
        var tcs = new TaskCompletionSource<RubyBridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending[requestId] = tcs;

        var frame = new JsonObject
        {
            ["command"] = command,
            ["arguments"] = arguments ?? new JsonObject(),
            ["request_id"] = requestId,
        };
        WriteFrame(frame);

        // Inactivity timeout: a long turn that keeps streaming progress (tool
        // start/complete, thinking, …) must not be cut off mid-flight. Only when
        // the daemon falls silent for RequestTimeoutSeconds do we give up.
        var deadline = TimeSpan.FromSeconds(RequestTimeoutSeconds);
        const int pollMs = 5_000;
        while (true)
        {
            var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActivityTicks));
            var remaining = deadline - idle;
            if (remaining <= TimeSpan.Zero)
            {
                lock (_pendingLock) _pending.Remove(requestId);
                SendInterrupt();
                throw new TimeoutException($"Ruby daemon request '{command}' timed out after {RequestTimeoutSeconds:0}s of inactivity.");
            }
            var wait = remaining < TimeSpan.FromMilliseconds(pollMs)
                ? remaining
                : TimeSpan.FromMilliseconds(pollMs);
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(wait));
            if (winner == tcs.Task)
                return await tcs.Task;
            // No result yet — loop and re-evaluate against fresh activity.
        }
    }

    // ── High-level commands (mirrors RubyBridge.swift) ─────────────────────

    public async Task<int?> RememberAsync(string content, IReadOnlyList<string>? tags = null, IReadOnlyList<string>? links = null)
    {
        var response = await SendAsync("remember", new JsonObject
        {
            ["content"] = content,
            ["tags"] = JsonArrayFrom(tags),
            ["links"] = JsonArrayFrom(links),
        });
        ThrowIfError(response);
        if (response.ResultDict is { } dict && dict["id"] is JsonValue v && v.TryGetValue<int>(out var id)) return id;
        if (response.ResultString is { } s && int.TryParse(s, out var sid)) return sid;
        return null;
    }

    public async Task<JsonArray?> RecallNotesAsync(string query, int limit = 5, IReadOnlyList<string>? tags = null)
    {
        var response = await SendAsync("recall", new JsonObject
        {
            ["query"] = query,
            ["limit"] = limit,
            ["tags"] = JsonArrayFrom(tags),
        });
        ThrowIfError(response);
        return response.ResultArray;
    }

    public async Task<JsonArray?> FetchHistoryAsync(int limit = 20)
    {
        var response = await SendAsync("history", new JsonObject { ["limit"] = limit });
        ThrowIfError(response);
        return response.ResultArray;
    }

    public async Task<OracleReply?> OracleAsync(string message, JsonObject? context = null)
    {
        var args = new JsonObject { ["message"] = message, ["history"] = false };
        if (context is { Count: > 0 }) args["context"] = context;
        var response = await SendAsync("oracle", args);
        return ExtractReply(response);
    }

    public async Task<OracleReply?> ChatAsync(
        IReadOnlyList<JsonObject> messages,
        string? system = null,
        string? model = null,
        IReadOnlyList<string>? activeCompanions = null)
    {
        var args = new JsonObject
        {
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["history"] = false,
        };
        if (system is not null) args["system"] = system;
        if (model is not null) args["model"] = model;
        if (activeCompanions is { Count: > 0 }) args["active_companions"] = JsonArrayFrom(activeCompanions);
        var response = await SendAsync("oracle", args);
        return ExtractReply(response);
    }

    public async Task<OracleReply?> AskCompanionAsync(string name, string question)
    {
        var response = await SendAsync("companion", new JsonObject
        {
            ["name"] = name,
            ["message"] = question,
            ["history"] = false,
        });
        return ExtractReply(response);
    }

    public async Task<JsonObject?> GetAegisAsync()
    {
        var response = await SendAsync("aegis", new JsonObject { ["action"] = "get" });
        ThrowIfError(response);
        return response.ResultDict;
    }

    public async Task<JsonObject?> ActivateContextAsync(string path)
    {
        var response = await SendAsync("activate_context", new JsonObject { ["path"] = path });
        ThrowIfError(response);
        return response.ResultDict;
    }

    public async Task<JsonObject?> InterruptAsync()
    {
        var response = await SendAsync("interrupt", new JsonObject());
        ThrowIfError(response);
        return response.ResultDict;
    }

    /// <summary>Answers a pending `ask_user` confirmation (blocked command / destructive
    /// op). `user_response` is a control command, so it is routed inline on the daemon's
    /// reader thread and unblocks the worker's <c>await_user_response</c> immediately.</summary>
    public async Task<JsonObject?> UserResponseAsync(string uuid, string response)
    {
        var r = await SendAsync("user_response", new JsonObject
        {
            ["uuid"] = uuid,
            ["response"] = response,
        });
        ThrowIfError(r);
        return r.ResultDict;
    }

    public async Task<JsonObject?> CliAsync(string command, int timeout = 30)
    {
        var response = await SendAsync("cli", new JsonObject { ["cmd"] = command, ["timeout"] = timeout });
        ThrowIfError(response);
        return response.ResultDict;
    }

    public async Task<string?> FetchCwdAsync()
    {
        var response = await SendAsync("ping", new JsonObject());
        ThrowIfError(response);
        return response.ResultDict?["cwd"]?.GetValue<string>();
    }

    public async Task<JsonObject?> UpdateAegisAsync(string summary, IReadOnlyList<string>? tags = null)
    {
        var response = await SendAsync("aegis", new JsonObject
        {
            ["action"] = "update",
            ["summary"] = summary,
            ["tags"] = JsonArrayFrom(tags),
        });
        ThrowIfError(response);
        return response.ResultDict;
    }

    public async Task<JsonObject?> CreateTaskAsync(string title, string plan, string workflowType = "full")
    {
        var response = await SendAsync("create_task", new JsonObject
        {
            ["title"] = title,
            ["plan"] = plan,
            ["workflow_type"] = workflowType,
        });
        ThrowIfError(response);
        return response.ResultDict;
    }

    public async Task<JsonObject?> ExecuteTaskAsync(int id)
    {
        var response = await SendAsync("execute_task", new JsonObject { ["id"] = id });
        ThrowIfError(response);
        return response.ResultDict;
    }

    public async Task<JsonObject?> EvaluateTaskAsync(int id)
    {
        var response = await SendAsync("evaluate_task", new JsonObject { ["id"] = id });
        ThrowIfError(response);
        return response.ResultDict;
    }

    public async Task<JsonArray?> ListTasksAsync()
    {
        var response = await SendAsync("list_tasks", new JsonObject());
        ThrowIfError(response);
        return response.ResultArray;
    }

    public async Task<JsonObject?> RemoveTaskAsync(int id)
    {
        var response = await SendAsync("remove_task", new JsonObject { ["id"] = id });
        ThrowIfError(response);
        return response.ResultDict;
    }

    // ── Decoding helpers ───────────────────────────────────────────────────

    private static void ThrowIfError(RubyBridgeResponse response)
    {
        if (response.Error is { } err)
            throw new InvalidOperationException(err);
    }

    private static OracleReply? ExtractReply(RubyBridgeResponse response)
    {
        if (response.Error is { } err)
            throw new InvalidOperationException(err);

        if (response.ResultDict is { } dict &&
            dict["response"] is JsonObject resp &&
            resp["answer"] is JsonValue answer)
        {
            return new OracleReply(
                answer.GetValue<string>(),
                resp["reasoning_content"]?.GetValue<string>(),
                (resp["companion_suggestions"] as JsonArray) ?? new JsonArray(),
                CompanionSays(resp["logs"] as JsonArray));
        }

        // Cooperative interrupt: worker returned a divine-interrupt array.
        if (response.ResultArray is { Count: > 0 } arr &&
            arr[0] is JsonObject first &&
            first["__divine_interrupt"] is not null)
        {
            return new OracleReply("🛑 (paused)", null);
        }

        return new OracleReply(response.AsString(), null);
    }

    private static List<CompanionSay> CompanionSays(JsonArray? logs)
    {
        var result = new List<CompanionSay>();
        if (logs is null) return result;
        foreach (var e in logs)
        {
            if (e is not JsonObject log) continue;
            if (log["type"]?.GetValue<string>() != "say") continue;
            if (log["data"] is not JsonObject data) continue;
            var message = data["message"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(message)) continue;
            result.Add(new CompanionSay(
                log["glyph"]?.GetValue<string>() ?? "✦",
                log["companion"]?.GetValue<string>() ?? "",
                message));
        }
        return result;
    }

    private static JsonArray JsonArrayFrom(IReadOnlyList<string>? items)
    {
        var arr = new JsonArray();
        if (items is not null)
            foreach (var i in items) arr.Add(i);
        return arr;
    }
}

/// <summary>A decoded Oracle turn (mirrors the Swift OracleReply).</summary>
public sealed record OracleReply(
    string? Text,
    string? ReasoningContent,
    JsonArray? CompanionSuggestions = null,
    IReadOnlyList<CompanionSay>? CompanionSays = null);

/// <summary>A companion voice surfaced into the chat flow.</summary>
public sealed record CompanionSay(string Glyph, string Name, string Message);