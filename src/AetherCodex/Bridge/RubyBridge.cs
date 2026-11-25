using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
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
    private Socket? _socket;
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

    /// <summary>Raised for each minimal `notify` frame (live-state change ping).</summary>
    public event Action<NotifyEvent>? NotifyReceived;

    /// <summary>Raised for non-request frames (recover_suggestion, ...).</summary>
    public event Action<DaemonEvent>? EventReceived;

    /// <summary>Raised after a wedged/timed-out turn is recovered.</summary>
    public event Action? Recovered;

    /// <summary>Handler for daemon-emitted <c>host_call</c> frames (browser control,
    /// screenshots, tab open/list). Returns the <c>host_result</c> payload.</summary>
    public Func<string, JsonObject?, Task<JsonObject?>>? HostCallHandler { get; set; }

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
            : ResolveProjectRoot();

    /// <summary>Walk up from <paramref name="start"/> (file or directory) to the
    /// nearest <c>.aethercodex</c>/<c>.aether_properties</c> ancestor, mirroring
    /// the daemon's <c>find_project_root</c>. Falls back to the start directory
    /// (or its parent for a file path) when no config marker exists up the tree.</summary>
    public static string ResolveProjectRoot(string start)
    {
        if (string.IsNullOrWhiteSpace(start))
            return Directory.GetCurrentDirectory();

        var dir = File.Exists(start)
            ? new DirectoryInfo(start).Parent
            : new DirectoryInfo(start);
        var current = dir;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, ".aethercodex")) ||
                File.Exists(Path.Combine(current.FullName, ".aether_properties")))
                return current.FullName;
            current = current.Parent;
        }
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    /// <summary>Walk up from the process working directory to the nearest
    /// <c>.aethercodex</c>/<c>.aether_properties</c>, mirroring the daemon's own
    /// <c>derive_project_root</c>. Without this, a first-run launch (empty
    /// <c>LastDirectory</c>) from a directory that is not itself a project would
    /// seed <c>AETHER_PROJECT_ROOT</c> with the wrong cwd, so the daemon would
    /// never read the project's <c>allowed-commands: "*"</c> — every
    /// non-allowlisted command then trips the permission gate and the turn
    /// stalls after the first tool.</summary>
    private static string ResolveProjectRoot() =>
        ResolveProjectRoot(Directory.GetCurrentDirectory());

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        if (IsRunning) return;

        if (!File.Exists(DaemonScript))
            throw new InvalidOperationException($"Ruby daemon script not found at: {DaemonScript}");
        if (!File.Exists(RubyPath) && RubyPath != "ruby")
            throw new InvalidOperationException($"Ruby not executable at: {RubyPath}");

        // One daemon per project, shared by every window of the editor. The
        // ÆtherLimen socket is the transport: the first window spawns the
        // background service, subsequent windows connect to it. Each connection
        // is a session; the single daemon keeps one Mnemosyne/Aegis that every
        // window breathes together.
        var socketPath = SocketPath();
        var socket = await ConnectWithRetryAsync(socketPath, spawnGrace: false);
        if (socket is null)
        {
            try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { /* stale */ }
            SpawnDaemon(socketPath);
            socket = await ConnectWithRetryAsync(socketPath, spawnGrace: true);
        }
        if (socket is null)
            throw new InvalidOperationException("Could not connect to the ÆtherLimen daemon socket.");

        _socket = socket;
        var stream = new NetworkStream(socket, ownsSocket: true);
        _stdin = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _startupTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsRunning = true;

        _readerTask = Task.Run(() => ReaderLoop(new StreamReader(stream, new UTF8Encoding(false))));

        var startup = _startupTcs.Task;
        var winner = await Task.WhenAny(startup, Task.Delay(TimeSpan.FromSeconds(15)));
        if (winner != startup)
        {
            LastError = "Ruby daemon did not emit a ready frame within 15s.";
            throw new TimeoutException(LastError);
        }
        await startup; // rethrow any fault
    }

    /// <summary>The per-project ÆtherLimen socket (Windows AF_UNIX). One daemon
    /// binds it; every window connects to the same path.</summary>
    private string SocketPath()
    {
        var dir = Path.Combine(ProjectRoot, ".aether");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "aether-limen.sock").Replace('\\', '/');
    }

    private async Task<Socket?> ConnectWithRetryAsync(string socketPath, bool spawnGrace)
    {
        if (!File.Exists(socketPath) && !spawnGrace)
            return null;
        var deadline = DateTime.UtcNow.AddSeconds(spawnGrace ? 15 : 2);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
                return socket;
            }
            catch { await Task.Delay(150); }
        }
        return null;
    }

    private void SpawnDaemon(string socketPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RubyPath,
            Arguments = $"\"{DaemonScript}\" --listen \"{socketPath}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.Environment["AETHER_PROJECT_ROOT"] = ProjectRoot;
        if (!psi.Environment.ContainsKey("LANG"))
            psi.Environment["LANG"] = "en_US.UTF-8";
        if (!psi.Environment.ContainsKey("LC_ALL"))
            psi.Environment["LC_ALL"] = "en_US.UTF-8";

        var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException("Failed to launch Ruby daemon.");
        _process = process;
        _errorReaderTask = Task.Run(() => ErrorLoop(process.StandardError));
    }

    public void Stop()
    {
        IsRunning = false;
        try { _stdin?.Dispose(); } catch { /* already gone */ }
        try { _socket?.Dispose(); } catch { }
        // The daemon is a shared background service (one per project); other
        // windows may still be connected, so never kill it here.
        _process = null;
        _stdin = null;
        _socket = null;
        ResumePendingWithError(new InvalidOperationException("Ruby daemon disconnected."));
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
        var sock = _socket;
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
        finally
        {
            // Reaching EOF means the daemon dropped the socket (crash / restart /
            // kill), not a clean Stop(). If this reader still owns the current
            // connection, mark the bridge dead and fail pending turns so the next
            // request lazily reconnects instead of hanging on the inactivity timer.
            if (sock is not null && ReferenceEquals(_socket, sock))
                OnConnectionLost();
        }
    }

    private void OnConnectionLost()
    {
        IsRunning = false;
        LastError = "Ruby daemon connection lost.";
        ResumePendingWithError(new IOException(LastError));
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

            case DaemonFrames.Type.Notify:
                if (NotifyEvent.TryParse(json) is { } notify)
                    NotifyReceived?.Invoke(notify);
                break;

            case "host_call":
                _ = HandleHostCallAsync(
                    json["method"]?.GetValue<string>(),
                    json["arguments"] as JsonObject,
                    json["request_id"]?.GetValue<int>());
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

    private async Task HandleHostCallAsync(string? method, JsonObject? args, int? requestId)
    {
        JsonObject? result;
        try
        {
            var handler = HostCallHandler;
            if (handler is null) throw new InvalidOperationException("no host-call handler registered");
            result = await handler(method ?? "", args);
        }
        catch (Exception ex)
        {
            result = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }
        WriteFrame(new JsonObject
        {
            ["type"] = "host_result",
            ["request_id"] = requestId,
            ["result"] = result,
        });
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

    public async Task<JsonArray?> FetchHistoryAsync(int limit = 20, string? sessionId = null, bool includeToolCalls = false)
    {
        var args = new JsonObject { ["limit"] = limit };
        if (sessionId is not null) args["session_id"] = sessionId;
        if (includeToolCalls) args["include_tool_calls"] = true;
        var response = await SendAsync("history", args);
        ThrowIfError(response);
        return response.ResultArray;
    }

    /// <summary>List this project's recorded sessions (id · turns · first-prompt
    /// preview), newest activity first, for the session-continue selector.</summary>
    public async Task<JsonArray?> ListSessionsAsync()
    {
        var response = await SendAsync("sessions", new JsonObject());
        ThrowIfError(response);
        return response.ResultArray;
    }

    /// <summary>Pull the durable in-flight live state (the daemon's
    /// <c>live_turns</c> table). With a session id: one live turn; without: every
    /// live turn in this project. Called after a <c>notify</c> frame arrives.</summary>
    public async Task<JsonArray?> GetLiveStateAsync(string? sessionId = null)
    {
        var args = new JsonObject();
        if (sessionId is not null) args["session_id"] = sessionId;
        var response = await SendAsync("live_state", args);
        ThrowIfError(response);
        return response.ResultArray;
    }

    /// <summary>Pull the durable session message timeline (the daemon's
    /// <c>session_events</c> table). <paramref name="afterRevision"/> advances the
    /// cursor so a subscriber receives only what it missed. Returns
    /// <c>{ session_id, revision, events: [...] }</c> — the current revision plus
    /// every event with a revision greater than <paramref name="afterRevision"/>.</summary>
    public async Task<JsonObject?> GetSessionEventsAsync(string sessionId, int afterRevision = 0, int? limit = null)
    {
        var args = new JsonObject { ["session_id"] = sessionId };
        if (afterRevision > 0) args["after_revision"] = afterRevision;
        if (limit is int l) args["limit"] = l;
        var response = await SendAsync("session_events", args);
        ThrowIfError(response);
        return response.ResultDict;
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
        IReadOnlyList<string>? activeCompanions = null,
        string? sessionId = null)
    {
        var args = new JsonObject
        {
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["history"] = false,
        };
        if (system is not null) args["system"] = system;
        if (model is not null) args["model"] = model;
        if (activeCompanions is { Count: > 0 }) args["active_companions"] = JsonArrayFrom(activeCompanions);
        if (sessionId is not null) args["session_id"] = sessionId;
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

    /// <summary>Runs a companion suggestion: the companion executes the prompt with
    /// its full specialist tool set (the suggestion-execution tier), honouring any
    /// temperature/thinking overrides carried by the suggestion payload.</summary>
    public async Task<OracleReply?> ExecuteSuggestionAsync(string companion, string prompt, double? temperature = null, string? thinking = null)
    {
        var args = new JsonObject
        {
            ["message"] = prompt,
            ["companion"] = companion,
            ["suggestion_execution"] = true,
            ["history"] = false,
        };
        if (temperature is not null) args["temperature"] = temperature.Value;
        if (thinking is not null) args["thinking"] = thinking;
        var response = await SendAsync("oracle", args);
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

    /// <summary>Effective project config from the Ruby soul — each curated key
    /// carries its resolved value plus the source layer (env/project/home/bundle/
    /// preset/default). Null when the daemon is unavailable.</summary>
    public async Task<JsonObject?> GetConfigAsync()
    {
        var response = await SendAsync("config", new JsonObject());
        ThrowIfError(response);
        return response.ResultDict;
    }

    /// <summary>Derived context overview from the Ruby soul — project root, name,
    /// config path, memory db, the shared shell cwd, plus the annotated sibling
    /// contexts from the config's <c>contexts:</c> section. Null when the daemon
    /// is unavailable.</summary>
    public async Task<JsonObject?> GetContextsAsync()
    {
        var response = await SendAsync("contexts", new JsonObject());
        ThrowIfError(response);
        return response.ResultDict;
    }

    /// <summary>Live ÆtherLink discovery from the Ruby soul — scanned peers plus
    /// annotated sibling contexts. Returns <c>{ own, contexts:[ { name, port, path,
    /// description, tags, annotated, stale, busy, online, own } ] }</c>.
    /// Null when the daemon is unavailable.</summary>
    public async Task<JsonObject?> GetAetherLinkContextsAsync()
    {
        var response = await SendAsync("aether_contexts", new JsonObject());
        ThrowIfError(response);
        return response.ResultDict;
    }

    /// <summary>Write one top-level key into a config file and reload in place.
    /// <paramref name="target"/> is <c>project</c> (default — the local
    /// <c>.aethercodex</c>/<c>.aether_properties</c>) or <c>home</c> (the global
    /// personal <c>~/.aethercodex</c>). A null value clears the key.</summary>
    public async Task<JsonObject?> SetConfigAsync(string key, string? value, string target = "project")
    {
        var response = await SendAsync("set_config", new JsonObject
        {
            ["key"] = key,
            ["value"] = value is null ? null : JsonValue.Create(value),
            ["target"] = target,
        });
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

    /// <summary>Structured two-text diff from the Ruby soul (DiffCrepusculum).
    /// <paramref name="strategy"/> is <c>line</c>, <c>word</c> or <c>crepusculum</c>.
    /// Returns <c>{ strategy, hunks:[ { old_start, old_count, new_start, new_count,
    /// lines:[ { type, text, old_no, new_no, spans:[{start,len}] } ] } ] }</c>.</summary>
    public async Task<JsonObject?> DiffAsync(string oldText, string newText, string strategy = "line", int context = 3)
    {
        var response = await SendAsync("diff", new JsonObject
        {
            ["old"] = oldText,
            ["new"] = newText,
            ["strategy"] = strategy,
            ["context"] = context,
        });
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
                IntermediaryMessages(resp["logs"] as JsonArray));
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

    private static List<IntermediaryMessage> IntermediaryMessages(JsonArray? logs)
    {
        var result = new List<IntermediaryMessage>();
        if (logs is null) return result;
        foreach (var e in logs)
        {
            if (e is not JsonObject log) continue;
            var type = log["type"]?.GetValue<string>();
            if (type == "prelude")
            {
                var text = log["text"]?.GetValue<string>()
                    ?? log["data"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(new IntermediaryMessage("prelude", "", "", text));
            }
            else if (type == "say")
            {
                if (log["data"] is not JsonObject data) continue;
                var message = data["message"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(message)) continue;
                result.Add(new IntermediaryMessage(
                    "say",
                    log["glyph"]?.GetValue<string>() ?? "✦",
                    log["companion"]?.GetValue<string>() ?? "",
                    message));
            }
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
    IReadOnlyList<IntermediaryMessage>? IntermediaryMessages = null);

/// <summary>An intermediary agent message surfaced into the chat flow before the
/// final answer. <c>Kind</c> is <c>prelude</c> (running reasoning) or <c>say</c>
/// (a companion voice).</summary>
public sealed record IntermediaryMessage(string Kind, string Glyph, string Name, string Message);