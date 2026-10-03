using System.Text.Json;
using System.Text.Json.Nodes;

namespace AetherCodex.Bridge;

/// <summary>
/// Wire-protocol models for the framed JSON stream shared with the Ruby daemon
/// (<c>Ruby/standalone_daemon.rb</c>). Newline-delimited frames, one JSON object
/// per line, UTF-8, on the daemon's STDIN/STDOUT.
/// </summary>
public static class DaemonFrames
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    /// <summary>Frames the Swift side understands. We mirror the exact enum.</summary>
    public static class Type
    {
        public const string Ready = "ready";
        public const string Stream = "stream";
        public const string Result = "result";
        public const string RecoverSuggestion = "recover_suggestion";
    }

    /// <summary>A status stream payload's inner kind.</summary>
    public static class Status
    {
        public const string Thinking = "thinking";
        public const string ToolCall = "tool_call";
        public const string ToolStarting = "tool_starting";
        public const string ToolCompleted = "tool_completed";
        public const string CommandExecuting = "command_executing";
        public const string CommandCompleted = "command_completed";
        public const string AskUser = "ask_user";
        public const string Answer = "answer";
        public const string Chunk = "chunk";
        public const string Progress = "progress";
        public const string ServerError = "server_error";
    }
}

/// <summary>A companion voice shipped in the daemon's `ready` frame.</summary>
public sealed record CompanionInfo(
    string Key,
    string Glyph,
    string Name,
    double? Temperature = null,
    string? Thinking = null);

/// <summary>Parsed `result` frame. The result payload may be a string, array, or object.</summary>
public sealed class RubyBridgeResponse
{
    public bool Success { get; }
    public string? ResultString { get; }
    public JsonArray? ResultArray { get; }
    public JsonObject? ResultDict { get; }
    public string? Error { get; }

    public RubyBridgeResponse(JsonObject frame)
    {
        Success = frame["success"]?.GetValue<bool>() ?? false;
        Error = frame["error"]?.GetValue<string>();

        var result = frame["result"];
        switch (result)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                ResultString = s;
                break;
            case JsonValue v when v.TryGetValue<bool>(out var b):
                ResultString = b ? "true" : "false";
                break;
            case JsonValue v when v.TryGetValue<long>(out var l):
                ResultString = l.ToString();
                break;
            case JsonArray a:
                ResultArray = a;
                break;
            case JsonObject o:
                ResultDict = o;
                break;
            case null:
                break;
            default:
                ResultString = result.ToJsonString();
                break;
        }
    }

    /// <summary>Best-effort string form, matching Swift's `\(result)` fallback.</summary>
    public string? AsString() =>
        ResultString
        ?? ResultArray?.ToJsonString()
        ?? ResultDict?.ToJsonString();
}

/// <summary>A parsed stream frame (`method` + `payload`).</summary>
public sealed class StreamEvent
{
    public string? Method { get; }
    public string? StatusType { get; }
    public JsonObject? Data { get; }
    public int? RequestId { get; }
    public JsonObject Raw { get; }

    public StreamEvent(JsonObject frame)
    {
        Raw = frame;
        Method = frame["method"]?.GetValue<string>();
        RequestId = frame["request_id"]?.GetValue<int>();

        var payload = frame["payload"] as JsonObject;
        StatusType = payload?["type"]?.GetValue<string>();
        Data = payload?["data"] as JsonObject;
    }

    public string? StringData(string key) => Data?[key]?.GetValue<string>();
}

/// <summary>Structured recover offer emitted when a turn breaks (Riss).</summary>
public sealed record RecoverSuggestion(
    int? RequestId,
    string Glyph,
    string Prompt,
    string Action,
    string? LastIntent);

/// <summary>Non-request frame the daemon emits outside the request/response cycle.</summary>
public abstract record DaemonEvent;

public sealed record RecoverEvent(RecoverSuggestion Suggestion) : DaemonEvent;
public sealed record OtherEvent(string Type, JsonObject Json) : DaemonEvent;
