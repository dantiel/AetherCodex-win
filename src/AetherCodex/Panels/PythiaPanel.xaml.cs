using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AetherCodex.Bridge;
using AetherCodex.Editor;

namespace AetherCodex.Panels;

public partial class PythiaPanel : UserControl
{
    private readonly RubyBridge _bridge = RubyBridge.Shared;
    private readonly List<JsonObject> _history = new();
    private SyntaxTheme _theme;
    private string _activeCompanion = "pythia";

    // Each window breathes its own session: a fresh conversation id that scopes
    // the chronicle (history) while the Aegis course stays shared across windows.
    private string _sessionId = Guid.NewGuid().ToString("N");
    // The project root this session belongs to; sessions are scoped per project
    // (the daemon's memory db follows activate_context). Entering a new root
    // mints a fresh session — continuing a previous one is an explicit choice.
    private string? _sessionRoot;
    private int _turnCount;
    // True while THIS window drives its own turn. Suppresses notify → pull → sync
    // (we already render the full stream); only a foreign driver (CLI/other
    // window) on our session needs the notify path.
    private bool _streamingOwnTurn;
    // Durable session-event cursor: the highest session_events revision already
    // rendered into this window. Advancing it (notify → pull) keeps a foreign
    // driver's (CLI/other window) messages flowing into this conversation.
    private int _lastSessionRevision;
    private bool _syncingLiveEvents;
    private bool _liveResyncPending;
    private string SessionShort => _sessionId.Length > 8 ? _sessionId[..8] : _sessionId;

    // Scope + newest-Mnemosyne marker of what Pythia has already rendered. Used to
    // decide whether a rebuild is warranted (see RestoreHistoryAsync).
    private string? _lastSyncedRoot;
    private string? _lastSyncedTimestamp;

    private Border? _currentStatusCard;
    private UiColors _ui = ThemePalette.Build(new SyntaxTheme());

    // Instrumenta of the in-flight turn are collected into one group so they
    // read as a single block; the pending tool remembers the args echoed at
    // `tool_starting` until `tool_completed` arrives.
    private InstrumentGroup? _currentGroup;
    private readonly List<InstrumentumCard> _instrumenta = new();
    private (string Name, JsonObject? Args)? _pendingTool;
    private PythiaMessageCard? _lastPythiaCard;
    private Border? _askUserCard;
    // The most recent `info` (tell_user) card, so a final answer that echoes it
    // can drop the duplicate instead of showing the same text twice.
    private Border? _infoCard;
    private string? _infoPlain;

    // Window caption requests — the Pythia header hosts the window buttons
    // while the sidebar is visible; MainWindow subscribes to these.
    public event EventHandler? MinimizeRequested;
    public event EventHandler? MaximizeRestoreRequested;
    public event EventHandler? CloseRequested;

    public PythiaPanel()
    {
        InitializeComponent();
        _theme = new SyntaxTheme(); // defaults until a theme is applied

        _bridge.Ready += OnReady;
        _bridge.StreamReceived += OnStream;
        _bridge.NotifyReceived += OnNotify;
        _bridge.EventReceived += OnEvent;

        UpdateSessionHeader();
        _ = RefreshAegisHeaderAsync();
    }

    public void ApplyTheme(SyntaxTheme theme)
    {
        _theme = theme;
        _ui = ThemePalette.Build(theme);
        Background = new SolidColorBrush(theme.PythiaBackground);
        Foreground = new SolidColorBrush(theme.PythiaForeground);
        StatusDot.Fill = _bridge.IsRunning
            ? new SolidColorBrush(theme.PythiaAccent)
            : new SolidColorBrush(_ui.MutedForeground);
        ReThemeMessages(theme, _ui);
    }

    /// <summary>Re-skin every already-rendered message (bubbles, pythia cards,
    /// instrumentum groups, status rows, ask card) after a live theme switch.
    /// Cards built in code capture brushes at construction time, so without this
    /// pass they would keep the old palette while the rest of the shell changes.</summary>
    private void ReThemeMessages(SyntaxTheme theme, UiColors ui)
    {
        foreach (var child in Messages.Children)
        {
            switch (child)
            {
                case PythiaMessageCard card: card.ReTheme(theme, ui); break;
                case InstrumentGroup grp: grp.ReTheme(theme, ui); break;
                case InstrumentumCard ic: ic.ReTheme(theme, ui); break;
                case Border b when (string?)b.Tag == "user":
                    b.Background = new SolidColorBrush(ui.UserBubble);
                    if (b.Child is DockPanel dock)
                        foreach (var el in dock.Children)
                        {
                            if (el is TextBlock tb) tb.Foreground = new SolidColorBrush(theme.PythiaForeground);
                            else if (el is Button copy) copy.Foreground = new SolidColorBrush(ui.MutedForeground);
                        }
                    break;
                case Border b when (string?)b.Tag == "status-card":
                    b.Background = new SolidColorBrush(theme.PythiaCodeBackground);
                    if (b.Child is TextBlock tb2) tb2.Foreground = new SolidColorBrush(theme.PythiaComplementary);
                    break;
                case Border b when (string?)b.Tag == "ask":
                    b.Background = new SolidColorBrush(theme.PythiaCodeBackground);
                    if (b.Child is Panel p) ReThemeAskPanel(p, theme);
                    break;
                case TextBlock tb3:
                    tb3.Foreground = new SolidColorBrush(theme.PythiaComplementary);
                    break;
                case DockPanel dp:
                    ReThemeCompanionRow(dp, theme);
                    break;
            }
        }
    }

    private static void ReThemeAskPanel(Panel panel, SyntaxTheme theme)
    {
        foreach (var el in panel.Children)
        {
            switch (el)
            {
                case TextBlock tb:
                    tb.Foreground = new SolidColorBrush(theme.PythiaForeground);
                    break;
                case TextBox box:
                    box.Foreground = new SolidColorBrush(theme.PythiaForeground);
                    box.Background = new SolidColorBrush(theme.PythiaBackground);
                    box.BorderBrush = new SolidColorBrush(theme.PythiaAccent);
                    break;
                case Button btn:
                    btn.Background = new SolidColorBrush(theme.PythiaAccent);
                    btn.Foreground = new SolidColorBrush(theme.PythiaBackground);
                    break;
                case WrapPanel wrap:
                    ReThemeAskPanel(wrap, theme);
                    break;
            }
        }
    }

    private static void ReThemeCompanionRow(DockPanel row, SyntaxTheme theme)
    {
        foreach (var child in row.Children)
        {
            if (child is not TextBlock tb) continue;
            foreach (var inline in tb.Inlines)
            {
                if (inline is Run r)
                    r.Foreground = new SolidColorBrush(theme.PythiaForeground);
            }
            if (tb.Inlines.FirstInline is Run first && first.FontWeight == FontWeights.Bold)
                first.Foreground = new SolidColorBrush(theme.PythiaComplementary);
        }
    }

    /// <summary>
    /// Restore the conversation from Mnemosyne's latest recorded exchanges so the
    /// Pythia panel stays in sync with the CLI for the *currently active* project.
    /// Called after the daemon re-points its scope (activate_context) — the memory
    /// db has already been switched, so <c>history</c> returns this project's data.
    /// <para>
    /// The rebuild is gated: it only re-renders when there is nothing to show yet
    /// (a fresh/empty project) or when Mnemosyne has advanced past what Pythia
    /// already rendered for this same root. Otherwise the panel is left untouched,
    /// avoiding a needless wipe on every file-open in the same project.
    /// </para>
    /// </summary>
    public async Task RestoreHistoryAsync(string? contextRoot, int limit = 20)
    {
        // Entering a new project mints a fresh session (new session at start is
        // the default); continuing a previous session is an explicit choice via
        // the session selector.
        if (!string.IsNullOrWhiteSpace(contextRoot))
            EnsureSessionForRoot(contextRoot);

        JsonArray? entries = await FetchHistorySafeAsync(limit);
        await RenderHistoryAsync(entries, contextRoot, gated: true);
    }

    private async Task<JsonArray?> FetchHistorySafeAsync(int limit)
    {
        try { return await _bridge.FetchHistoryAsync(limit, _sessionId, includeToolCalls: true); }
        catch { return null; }
    }

    /// <summary>Breathe a fresh session when the window enters a project it has
    /// not been scoped to yet. Sessions are per-project: the daemon's memory db
    /// follows <c>activate_context</c>, and every turn is stamped with the
    /// session id that minted it.</summary>
    private void EnsureSessionForRoot(string root)
    {
        if (string.Equals(_sessionRoot, root, StringComparison.Ordinal)) return;

        _sessionRoot = root;
        _sessionId = Guid.NewGuid().ToString("N");
        _turnCount = 0;
        _lastSessionRevision = 0;
        _lastSyncedRoot = null;
        _lastSyncedTimestamp = null;
        UpdateSessionHeader();
    }

    /// <summary>Render a fetched entry list (oldest → newest) into the message
    /// panel, rebuilding <c>_history</c> so a continued session carries its full
    /// context into the next turn. When <paramref name="gated"/> is true, the
    /// rebuild is skipped if nothing changed since the last sync.</summary>
    private async Task RenderHistoryAsync(JsonArray? entries, string? root, bool gated)
    {
        if (entries is null) return;

        // fetch_history returns entries newest-first, then the daemon reverses them
        // to chronological order (oldest → newest). Map each {prompt,answer} → messages.
        var messages = new List<JsonObject>();
        foreach (var e in entries)
        {
            if (e is not JsonObject obj) continue;
            var prompt = obj["prompt"]?.GetValue<string>();
            var answer = obj["answer"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(prompt))
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });
            if (!string.IsNullOrWhiteSpace(answer))
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer });
        }

        // The newest Mnemosyne marker (SQLite CURRENT_TIMESTAMP, lexicographically sortable).
        string? newest = null;
        if (entries.Count > 0 && entries[^1] is JsonObject last)
            newest = last["created_at"]?.GetValue<string>() ?? last["timestamp"]?.GetValue<string>();

        bool sameRoot = string.Equals(root, _lastSyncedRoot, StringComparison.Ordinal);
        bool hasHistory = messages.Count > 0;

        // Skip the rebuild when Pythia is already showing this root's history and
        // Mnemosyne hasn't moved since we last synced it.
        if (gated && sameRoot && hasHistory
            && newest is not null && _lastSyncedTimestamp is not null
            && string.CompareOrdinal(newest, _lastSyncedTimestamp) <= 0)
        {
            return;
        }

        _lastSyncedRoot = root;
        _lastSyncedTimestamp = newest;

        await Dispatcher.InvokeAsync(() =>
        {
            _history.Clear();
            _history.AddRange(messages);
            Messages.Children.Clear();
            _currentGroup = null;
            _instrumenta.Clear();
            _lastPythiaCard = null;
            _askUserCard = null;
            _infoCard = null;
            _infoPlain = null;
            foreach (var e in entries)
            {
                if (e is not JsonObject obj) continue;
                var prompt = obj["prompt"]?.GetValue<string>();
                var answer = obj["answer"]?.GetValue<string>();

                // One tool group per turn, mirroring the live `StartTurn`.
                _currentGroup = null;
                if (!string.IsNullOrWhiteSpace(prompt))
                    AddUserMessage(prompt);
                if (obj["tool_calls"] is JsonArray calls)
                    foreach (var c in calls)
                        if (c is JsonObject tc)
                            AddRestoredInstrumentum(tc);
                if (obj["logs"] is JsonArray logs)
                    foreach (var l in logs)
                        if (l is JsonObject lo)
                            AddRestoredIntermediary(lo);
                // A still-running turn has no answer yet; mark it so the restored
                // history reads as live rather than a truncated exchange.
                if (obj["in_flight"] is JsonValue fv && fv.TryGetValue<bool>(out var flying) && flying)
                    AddStatus("⟳ live turn in progress — subscribed");
                if (!string.IsNullOrWhiteSpace(answer))
                    AddPythiaMessage(answer);
            }

            var turns = 0;
            foreach (var m in messages)
                if (m["role"]?.GetValue<string>() == "user") turns++;
            _turnCount = turns;

            if (messages.Count > 0)
                AddStatus($"↻ {messages.Count / 2} exchanges restored from Mnemosyne");
            UpdateSessionHeader();
        });
    }

    /// <summary>Open the session selector: a "new session" entry plus every
    /// session recorded in this project's chronicle, newest first. Choosing one
    /// re-points the window at that session and restores its conversation.</summary>
    private async void OnSessions(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        var newItem = new MenuItem { Header = "✚  New session" };
        newItem.Click += async (_, _) => await NewSessionAsync();
        menu.Items.Add(newItem);
        menu.Items.Add(new Separator());

        JsonArray? sessions = null;
        try { sessions = await _bridge.ListSessionsAsync(); }
        catch { /* daemon unreachable — show an empty selector */ }

        if (sessions is not { Count: > 0 })
        {
            menu.Items.Add(new MenuItem { Header = "(no previous sessions)", IsEnabled = false });
        }
        else
        {
            foreach (var s in sessions)
            {
                if (s is not JsonObject o) continue;
                var id = o["session_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) continue;

                var item = new MenuItem { Header = BuildSessionHeader(o, id), Tag = id };
                if (string.Equals(id, _sessionId, StringComparison.Ordinal))
                    item.IsChecked = true;
                item.Click += async (_, _) => await LoadSessionAsync(id);
                menu.Items.Add(item);
            }
        }

        menu.IsOpen = true;
    }

    private static string BuildSessionHeader(JsonObject o, string id)
    {
        var preview = (o["first_prompt"]?.GetValue<string>() ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (preview.Length > 48) preview = preview[..48] + "…";

        var turns = o["turns"] is JsonValue tv && tv.TryGetValue<int>(out var n) ? n : 0;
        var shortId = id.Length > 8 ? id[..8] : id;
        var live = o["live"] is JsonValue lv && lv.TryGetValue<bool>(out var isLive) && isLive;
        var marker = live ? "●" : "◈";

        return string.IsNullOrWhiteSpace(preview)
            ? $"{marker} {shortId} · {turns} turn{(turns == 1 ? "" : "s")}"
            : $"{marker} {shortId} · {turns}t · {preview}";
    }

    /// <summary>Mint a fresh session in place — new session at start is the
    /// default, and this makes it available mid-flight as well.</summary>
    private async Task NewSessionAsync()
    {
        _sessionId = Guid.NewGuid().ToString("N");
        _turnCount = 0;
        _lastSessionRevision = 0;
        _lastSyncedRoot = null;
        _lastSyncedTimestamp = null;

        await Dispatcher.InvokeAsync(() =>
        {
            _history.Clear();
            Messages.Children.Clear();
            _currentGroup = null;
            _instrumenta.Clear();
            _lastPythiaCard = null;
            _askUserCard = null;
            _infoCard = null;
            _infoPlain = null;
            UpdateSessionHeader();
            AddStatus("✦ new session — fresh conversation");
        });
    }

    /// <summary>Continue a previous session: re-point the window at it and
    /// restore its recorded conversation, carrying its full context forward.</summary>
    private async Task LoadSessionAsync(string sessionId)
    {
        _sessionId = sessionId;
        _turnCount = 0;
        _lastSessionRevision = 0;
        _lastSyncedRoot = null;
        _lastSyncedTimestamp = null;
        UpdateSessionHeader();

        JsonArray? entries = await FetchHistorySafeAsync(20);
        await RenderHistoryAsync(entries, _sessionRoot, gated: false);
        // Advance the event cursor past everything already rendered, so a later
        // notify only pulls messages that arrived after this restore.
        await SyncSessionRevisionAsync(sessionId);
    }

    /// <summary>Adopt the session's current event revision without rendering (the
    /// history restore already rendered the completed turns). Keeps the notify →
    /// pull cursor from replaying what we already show.</summary>
    private async Task SyncSessionRevisionAsync(string sessionId)
    {
        try
        {
            var state = await _bridge.GetSessionEventsAsync(sessionId, afterRevision: 0, limit: 0);
            if (state?["revision"] is JsonValue rv && rv.TryGetValue<int>(out var rev))
                _lastSessionRevision = rev;
        }
        catch (Exception) { /* daemon unreachable — cursor stays at 0 */ }
    }

    /// <summary>Re-renders one stored tool call as an instrumentum card using its
    /// full result (read_file / run_command stay structured, the rest collapse to
    /// their content echo), so restored history degrades exactly like a live turn.</summary>
    private void AddRestoredInstrumentum(JsonObject tc)
    {
        var name = tc["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name)) return;
        AddInstrumentum(name, ParseArgs(tc["args"]), tc["result"],
            tc["execution_time"] is JsonValue tv && tv.TryGetValue<double>(out var d) ? $"{d:0.00}s" : "");
    }

    /// <summary>Restores one persisted intermediary agent message (prelude
    /// reasoning or a companion say-log) into the flow between tool calls, so
    /// a continued session reads like the live turn instead of dropping them.</summary>
    private void AddRestoredIntermediary(JsonObject lo)
    {
        var type = lo["type"]?.GetValue<string>();
        if (type == "say")
        {
            var data = lo["data"] as JsonObject;
            var message = data?["message"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(message)) return;
            AddCompanionSay(lo["glyph"]?.GetValue<string>() ?? "✦",
                lo["companion"]?.GetValue<string>() ?? "", message);
        }
        else
        {
            var text = lo["text"]?.GetValue<string>() ?? lo["data"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text))
                AddOraclePrelude(text);
        }
    }

    private void OnReady(IReadOnlyList<CompanionInfo> roster)
    {
        Dispatcher.Invoke(() =>
        {
            StatusDot.Fill = new SolidColorBrush(_theme.PythiaAccent);
            Roster.ItemsSource = null;
            Roster.ItemsSource = roster;
        });
        _ = RefreshAegisHeaderAsync();
    }

    private void OnNotify(NotifyEvent evt)
    {
        if (evt.SessionId is not { } sid) return;
        // One æther, many eyes: a notify is only actionable when it breathes the
        // session THIS window has loaded. A foreign session's live turn surfaces
        // through the session selector (which reads the durable live_turns table)
        // rather than being injected into this conversation.
        if (!string.Equals(sid, _sessionId, StringComparison.Ordinal)) return;

        // While WE drive our own turn, the full stream renders it; the notify's
        // only job then is to keep our event cursor in step so a later foreign
        // turn does not re-render what we already showed.
        if (_streamingOwnTurn)
        {
            if (evt.Revision is int rev && rev > _lastSessionRevision)
                _lastSessionRevision = rev;
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(() => _ = SyncFromLiveNotifyAsync(evt));
        }
        catch (Exception) { /* swallow — dispatcher shutting down */ }
    }

    /// <summary>Pull the durable session-event timeline after a foreign driver's
    /// notify and render only what we missed. A drain-loop re-checks when a notify
    /// lands mid-pull, so no event is lost to a race.</summary>
    private async Task SyncFromLiveNotifyAsync(NotifyEvent evt)
    {
        if (evt.SessionId is not { } sid) return;
        if (_syncingLiveEvents) { _liveResyncPending = true; return; }
        _syncingLiveEvents = true;
        try
        {
            do
            {
                _liveResyncPending = false;
                await PullAndApplySessionEventsAsync(sid);
            } while (_liveResyncPending && string.Equals(sid, _sessionId, StringComparison.Ordinal));
        }
        finally
        {
            _syncingLiveEvents = false;
        }
    }

    private async Task PullAndApplySessionEventsAsync(string sid)
    {
        try
        {
            var state = await _bridge.GetSessionEventsAsync(sid, _lastSessionRevision);
            if (state is null) return;
            if (state["events"] is not JsonArray events) return;

            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var e in events)
                    if (e is JsonObject o)
                        ApplySessionEvent(o);
                if (state["revision"] is JsonValue rv && rv.TryGetValue<int>(out var rev))
                    _lastSessionRevision = rev;
            });
        }
        catch (Exception) { /* daemon transient — the next notify re-pulls */ }
    }

    /// <summary>Render one durable session event into the conversation. Kinds are
    /// <c>user_message</c>, <c>tool_call</c>, <c>intermediary</c> and
    /// <c>assistant_message</c> — mirroring the daemon's <c>session_event_append</c>.</summary>
    private void ApplySessionEvent(JsonObject evt)
    {
        var kind = evt["kind"]?.GetValue<string>();
        var payload = evt["payload"] as JsonObject ?? new JsonObject();
        switch (kind)
        {
            case "user_message":
            {
                var message = payload["message"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(message)) break;
                AddUserMessage(message);
                _history.Add(new JsonObject { ["role"] = "user", ["content"] = message });
                _turnCount++;
                UpdateSessionHeader();
                break;
            }
            case "tool_call":
                AddRestoredInstrumentum(payload);
                break;
            case "intermediary":
            {
                var type = payload["type"]?.GetValue<string>();
                var data = payload["data"] as JsonObject;
                if (type == "say")
                {
                    var message = data?["message"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(message))
                        AddCompanionSay(payload["glyph"]?.GetValue<string>() ?? "✦",
                            payload["companion"]?.GetValue<string>() ?? "", message);
                }
                else if (type == "info")
                {
                    var message = data?["raw_message"]?.GetValue<string>() ?? data?["message"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(message))
                        AddInfoMessage(message);
                }
                else
                {
                    var text = payload["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                        AddOraclePrelude(text);
                }
                break;
            }
            case "assistant_message":
            {
                var answer = payload["answer"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(answer)) break;
                _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer });
                AddPythiaMessage(answer);
                break;
            }
        }
    }

    private void OnEvent(DaemonEvent evt)
    {
        if (evt is RecoverEvent recover)
            Dispatcher.Invoke(() => AddStatus($"🛑 Thread tore — {recover.Suggestion.Glyph} {recover.Suggestion.Prompt}"));
    }

    private void OnStream(StreamEvent evt)
    {
        if (evt.Method != "status") return;
        // One æther, many eyes: status frames are broadcast to every connected
        // window. Each panel renders only the turn its own session is breathing;
        // foreign sessions (and un-tagged control chatter) pass through untouched
        // when there is no session tag to disambiguate.
        if (evt.SessionId is { } sid && !string.Equals(sid, _sessionId, StringComparison.Ordinal))
            return;
        // A rendering hiccup must never kill the reader thread: the daemon's
        // `result` frame still has to reach the awaiting SendAsync, otherwise the
        // turn looks stuck at "tool starting" forever. BeginInvoke (async) keeps
        // the reader thread unblocked so it keeps draining daemon stdout even
        // while the UI thread renders — a synchronous Invoke here can stall the
        // frame pump (and thus the `user_response` round-trip for ask_user).
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try { HandleStatus(evt.StatusType, evt.Data, evt.Uuid); }
                catch (Exception) { /* swallow — never wedge the frame pump */ }
            });
        }
        catch (Exception) { /* swallow — dispatcher shutting down */ }
    }

    private void HandleStatus(string? type, JsonObject? data, string? uuid = null)
    {
        string? tool = data?["tool"]?.GetValue<string>() ?? data?["name"]?.GetValue<string>();
        switch (type)
        {
            case DaemonFrames.Status.Thinking:
                var message = data?["message"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(message)) SetStatus("… " + message);
                break;
            case DaemonFrames.Status.ToolStarting:
            case DaemonFrames.Status.ToolCall:
                _pendingTool = (tool ?? "", ParseArgs(data?["args"]));
                SetStatus($"→ {InstrumentumRenderer.Glyph(_pendingTool.Value.Name)} {_pendingTool.Value.Name}{InstrumentumRenderer.ArgsSummary(_pendingTool.Value.Name, _pendingTool.Value.Args)}");
                break;
            case DaemonFrames.Status.ToolCompleted:
                var t = data?["execution_time"] is JsonValue v && v.TryGetValue<double>(out var d) ? $"{d:0.00}s" : "";
                AddInstrumentum(tool ?? _pendingTool?.Name ?? "", _pendingTool?.Args, data?["result"], t);
                _pendingTool = null;
                SetStatus(null);
                break;
            case DaemonFrames.Status.CommandExecuting:
                SetStatus($"$ {data?["command"]?.GetValue<string>()}");
                break;
            case DaemonFrames.Status.CommandCompleted:
                SetStatus(null);
                break;
            case DaemonFrames.Status.AskUser:
                AddAskUserCard(uuid, data);
                break;
            case DaemonFrames.Status.Info:
            {
                var info = data?["raw_message"]?.GetValue<string>()
                        ?? data?["message"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(info))
                    AddInfoMessage(info);
                break;
            }
            default:
                // Informational statuses the PythiaPanel doesn't render (they flow
                // into the final answer / tool cards). Never show the raw type
                // string as a status line — that read like a stuck tool call.
                if (type is "thinking_complete" or "oracle_revelation" or "completed"
                    or "system_error" or "token_usage" or "file_reading" or "file_read_complete"
                    or "file_read_fail" or "file_creating" or "file_created" or "file_patching"
                    or "file_patched" or "memory_searching" or "memory_found" or "notes_recalled"
                    or "aegis_unveiled" or "screenshot_capturing" or "screenshot_captured"
                    or "heartbeat")
                    break;
                if (!string.IsNullOrWhiteSpace(type)) SetStatus(type);
                break;
        }
    }

    private void SetStatus(string? text)
    {
        if (_currentStatusCard is not null)
        {
            Messages.Children.Remove(_currentStatusCard);
            _currentStatusCard = null;
        }
        if (string.IsNullOrWhiteSpace(text)) return;

        _currentStatusCard = new Border
        {
            Tag = "status-card",
            Background = new SolidColorBrush(_theme.PythiaCodeBackground),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(4, 2, 4, 2),
            Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(_theme.PythiaComplementary),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        Messages.Children.Add(_currentStatusCard);
        ScrollToEnd();
    }

    private void AddInstrumentum(string? name, JsonObject? args, JsonNode? result, string timing)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        // Voice/info instruments (tell_user, *_say, *_ask) are already rendered as
        // chat cards; their tool echo would just duplicate them as a JSON blob.
        if (name == "tell_user"
            || name.EndsWith("_say", StringComparison.Ordinal)
            || name.EndsWith("_ask", StringComparison.Ordinal))
            return;
        var group = EnsureGroup();
        var card = new InstrumentumCard(_theme, _ui, name, args, result, timing);
        card.CloseRequested += OnInstrumentumClosed;
        group.Add(card);
        _instrumenta.Add(card);
        RefreshInstrumentaDensity();
        ScrollToEnd();
    }

    private void OnInstrumentumClosed(InstrumentumCard card)
    {
        _instrumenta.Remove(card);
        RefreshInstrumentaDensity();
    }

    /// <summary>Degrade every instrumentum by history distance: the newest rests
    /// expanded, the previous one rests compact (medium), everything older rests
    /// as a one-liner. A card's single click toggles between its resting level and
    /// the full body, so this re-rank only changes what "minimised" means per card.</summary>
    private void RefreshInstrumentaDensity()
    {
        for (int i = 0; i < _instrumenta.Count; i++)
        {
            var age = _instrumenta.Count - 1 - i;
            var resting = age switch
            {
                0 => InstrumentumDensity.Expanded,
                1 => InstrumentumDensity.Compact,
                _ => InstrumentumDensity.OneLiner,
            };
            _instrumenta[i].SetResting(resting, expanded: age == 0);
        }
    }

    private InstrumentGroup EnsureGroup()
    {
        if (_currentGroup is null)
        {
            _currentGroup = new InstrumentGroup(_theme, _ui);
            _currentGroup.Emptied += OnGroupEmptied;
            Messages.Children.Add(_currentGroup);
        }
        return _currentGroup;
    }

    private void OnGroupEmptied(InstrumentGroup group)
    {
        Messages.Children.Remove(group);
        if (ReferenceEquals(_currentGroup, group)) _currentGroup = null;
    }

    private void StartTurn()
    {
        _currentGroup = null;
        _pendingTool = null;
        RemoveAskUserCard();
    }

    private void RemoveAskUserCard()
    {
        if (_askUserCard is null) return;
        Messages.Children.Remove(_askUserCard);
        _askUserCard = null;
    }

    /// <summary>
    /// Renders the daemon's `ask_user` gate as an interactive card. The daemon is
    /// blocked in <c>await_user_response</c> until the frontend answers, so the
    /// buttons must call <c>UserResponseAsync(uuid, …)</c> — otherwise the turn
    /// hangs on "tool starting" with no forward progress.
    /// </summary>
    private void AddAskUserCard(string? uuid, JsonObject? data)
    {
        RemoveAskUserCard();

        var message = data?["message"]?.GetValue<string>();
        var kind = data?["type"]?.GetValue<string>() ?? "confirm";
        var options = new List<string>();
        if (data?["options"] is JsonArray arr)
            foreach (var o in arr)
                if (o is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    options.Add(s);
        if (options.Count == 0)
            options.AddRange(new[] { "Confirm", "Abort" });

        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = "❓ " + (string.IsNullOrWhiteSpace(message) ? "confirmation requested" : message),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(_theme.PythiaForeground),
            FontWeight = FontWeights.SemiBold,
        });

        if (kind == "prompt")
        {
            var input = new TextBox
            {
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = new SolidColorBrush(_theme.PythiaForeground),
                Background = new SolidColorBrush(_theme.PythiaBackground),
                BorderBrush = new SolidColorBrush(_theme.PythiaAccent),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 3, 6, 3),
            };
            panel.Children.Add(input);

            var send = BuildAskButton("Send");
            send.Click += async (_, _) =>
            {
                var value = input.Text.Trim();
                if (value.Length == 0) return;
                RemoveAskUserCard();
                AddStatus("↩ " + value);
                await AnswerAskAsync(uuid, value);
            };
            input.KeyDown += async (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                var value = input.Text.Trim();
                if (value.Length == 0) return;
                RemoveAskUserCard();
                AddStatus("↩ " + value);
                await AnswerAskAsync(uuid, value);
            };
            panel.Children.Add(send);
        }
        else
        {
            var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var opt in options)
            {
                var chosen = opt;
                var btn = BuildAskButton(opt);
                btn.Click += async (_, _) =>
                {
                    RemoveAskUserCard();
                    AddStatus("↩ " + chosen);
                    await AnswerAskAsync(uuid, chosen);
                };
                buttons.Children.Add(btn);
            }
            panel.Children.Add(buttons);
        }

        _askUserCard = new Border
        {
            Tag = "ask",
            Background = new SolidColorBrush(_theme.PythiaCodeBackground),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = panel,
        };
        Messages.Children.Add(_askUserCard);
        ScrollToEnd();
    }

    private Button BuildAskButton(string label)
    {
        return new Button
        {
            Content = label,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(12, 4, 12, 4),
            Background = new SolidColorBrush(_theme.PythiaAccent),
            Foreground = new SolidColorBrush(_theme.PythiaBackground),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
    }

    private async Task AnswerAskAsync(string? uuid, string response)
    {
        // A missing uuid means the daemon never registered a pending response —
        // answering would be silently dropped and the turn would time out. Surface
        // that instead of swallowing it, so a broken ask_user round-trip is visible.
        if (string.IsNullOrWhiteSpace(uuid))
        {
            RemoveAskUserCard();
            AddStatus("⚠ cannot answer — ask_user request uuid missing");
            return;
        }
        try { await _bridge.UserResponseAsync(uuid, response); }
        catch (Exception ex) { AddStatus("⚠ " + ex.Message); }
    }

    private static JsonObject? ParseArgs(JsonNode? args)
    {
        if (args is JsonObject o) return o;
        if (args is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
        {
            try { return JsonNode.Parse(s) as JsonObject; }
            catch (System.Text.Json.JsonException) { return null; }
        }
        return null;
    }

    private double AvailableWidth()
    {
        var w = Scroll.ActualWidth;
        if (w < 80) w = 400;
        return Math.Max(120, w - 24);
    }

    private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync();

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0 || _bridge == null) return;
        Input.Clear();

        // Built-in slash overviews (/config, /context) are answered directly by
        // the daemon — no oracle turn, no API cost. Everything else falls
        // through to the oracle.
        if (TryHandleSlashCommand(text))
            return;

        StartTurn();
        _streamingOwnTurn = true;
        AddUserMessage(text);
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        _turnCount++;
        UpdateSessionHeader();
        SetStatus("… summoning the oracle");

        try
        {
            var companions = _activeCompanion == "pythia" ? Array.Empty<string>() : new[] { _activeCompanion };
            var reply = await _bridge.ChatAsync(_history, activeCompanions: companions, sessionId: _sessionId);
            var answer = reply?.Text;
            var answerPlain = answer is null ? null : PythiaMessageCard.PlainPreview(answer, 1200).Trim();
            // Intermediary agent messages (running reasoning + companion voices)
            // precede the final answer, mirroring the daemon's ordered `logs`. A
            // companion "say" that merely echoes the answer is skipped so the last
            // message is not shown twice.
            if (reply?.IntermediaryMessages is { } intermediary)
                foreach (var m in intermediary)
                {
                    var plain = m.Message.Trim();
                    if (answerPlain is not null && plain.Length > 0 && plain == answerPlain)
                        continue;
                    AddIntermediaryMessage(m);
                }
            if (answer is { } a)
            {
                _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = a });
                if (reply.ReasoningContent is { } reasoning)
                    _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = $"__reasoning__: {reasoning}" });
                AddPythiaMessage(a);
                AddCompanionSuggestions(reply.CompanionSuggestions);
            }
        }
        catch (Exception ex)
        {
            AddStatus("⚠ " + ex.Message);
        }
        finally
        {
            _streamingOwnTurn = false;
            SetStatus(null);
            // The oracle may have re-veiled Aegis during the turn; keep the
            // course strip in step with the shared orientation.
            _ = RefreshAegisHeaderAsync();
        }
    }

    /// <summary>Consumes a built-in slash command and renders its overview card.
    /// Returns true when the input was consumed; false falls through to the
    /// oracle turn. The daemon remains the single authority for the values.</summary>
    private bool TryHandleSlashCommand(string text)
    {
        var cmd = text.Split(' ', 2)[0].Trim();
        switch (cmd)
        {
            case "/config":
                _ = ShowConfigOverview();
                return true;
            case "/context":
                _ = ShowContextOverview();
                return true;
            case "/aegis":
                _ = ShowAegisOverview();
                return true;
            default:
                return false;
        }
    }

    /// <summary>The `/config` overview: every curated key the Ruby soul resolves,
    /// with its value and the layer that produced it (env/project/home/bundle/
    /// preset/default) — plus the run_command permission gate.</summary>
    private async Task ShowConfigOverview()
    {
        AddUserMessage("/config");
        if (_bridge == null) return;
        SetStatus("… reading effective configuration");
        try
        {
            var cfg = await _bridge.GetConfigAsync();
            if (cfg == null)
            {
                AddStatus("⚠ no configuration received");
                return;
            }

            var md = new StringBuilder();
            md.AppendLine("# ⚙ Konfiguration");
            AppendKv(md, "Projekt", GetString(cfg, "name"));
            AppendKv(md, "Root", GetString(cfg, "project_root"));
            AppendKv(md, "Config", GetString(cfg, "config_path") + " (" + (GetString(cfg, "format") ?? "yaml") + ")");
            if (cfg["values"] is JsonObject values)
            {
                md.AppendLine();
                md.AppendLine("| Key | Wert | Quelle |");
                md.AppendLine("|---|---|---|");
                foreach (var kv in values)
                {
                    var v = kv.Value as JsonObject;
                    var val = v?["value"]?.ToString() ?? "—";
                    if (val.Length > 72) val = val[..72] + "…";
                    md.AppendLine($"| **{kv.Key}** | `{val}` | {v?["source"]?.ToString() ?? "?"} |");
                }
            }
            AddPythiaMessage(md.ToString());
        }
        catch (Exception ex)
        {
            AddStatus("⚠ " + ex.Message);
        }
        finally
        {
            SetStatus(null);
        }
    }

    /// <summary>The `/context` overview: where the soul lives (root, config file,
    /// memory db), where the shared shell stands, and the annotated sibling
    /// contexts from the config's `contexts:` section.</summary>
    private async Task ShowContextOverview()
    {
        AddUserMessage("/context");
        if (_bridge == null) return;
        SetStatus("… reading derived context");
        try
        {
            var ctx = await _bridge.GetContextsAsync();
            if (ctx == null)
            {
                AddStatus("⚠ no context received");
                return;
            }

            var md = new StringBuilder();
            md.AppendLine("# 🌐 Kontext");
            AppendKv(md, "Projekt", GetString(ctx, "name"));
            AppendKv(md, "Root", GetString(ctx, "project_root"));
            AppendKv(md, "Config", GetString(ctx, "config_path"));
            AppendKv(md, "Memory-DB", GetString(ctx, "memory_db"));
            AppendKv(md, "Shell-CWD", GetString(ctx, "shell_cwd"));
            if (ctx["contexts"] is JsonObject contexts && contexts.Count > 0)
            {
                md.AppendLine();
                md.AppendLine("## Verknüpfte Kontexte");
                foreach (var kv in contexts)
                {
                    var c = kv.Value as JsonObject;
                    var port = c?["port"]?.ToString();
                    var desc = c?["description"]?.ToString();
                    md.AppendLine($"- **{kv.Key}**" + (port is { Length: > 0 } ? $" (Port {port})" : "") + (desc is { Length: > 0 } ? $" — {desc}" : ""));
                }
            }
            AddPythiaMessage(md.ToString());
        }
        catch (Exception ex)
        {
            AddStatus("⚠ " + ex.Message);
        }
        finally
        {
            SetStatus(null);
        }
    }

    /// <summary>The `/aegis` overview: the shared orientation every window breathes —
    /// summary, temperature, thinking depth, working dir and tags.</summary>
    private async Task ShowAegisOverview()
    {
        AddUserMessage("/aegis");
        if (_bridge == null) return;
        SetStatus("… reading Aegis orientation");
        try
        {
            var aegis = await _bridge.GetAegisAsync();
            if (aegis == null)
            {
                AddStatus("⚠ no Aegis orientation received");
                return;
            }

            var md = new StringBuilder();
            md.AppendLine("# ◈ Aegis — Orientierung");
            md.AppendLine($"**Session** — {SessionShort} · {_turnCount} turn{(_turnCount == 1 ? "" : "s")}");
            AppendKv(md, "Summary", GetString(aegis, "summary"));
            AppendKv(md, "Temperature", GetString(aegis, "temperature"));
            AppendKv(md, "Thinking", GetString(aegis, "thinking"));
            AppendKv(md, "Working dir", GetString(aegis, "working_dir"));
            if (aegis["tags"] is JsonArray tags)
            {
                var parts = new List<string>();
                foreach (var t in tags)
                    if (t is JsonValue tv && !string.IsNullOrWhiteSpace(tv.ToString()))
                        parts.Add(tv.ToString());
                if (parts.Count > 0) md.AppendLine("**Tags** — " + string.Join(", ", parts));
            }
            foreach (var kv in aegis)
            {
                if (kv.Key is "summary" or "temperature" or "thinking" or "working_dir" or "tags")
                    continue;
                if (kv.Value is JsonValue v && !string.IsNullOrWhiteSpace(v.ToString()))
                    md.AppendLine($"**{kv.Key}** — {v}");
            }
            AddPythiaMessage(md.ToString());
        }
        catch (Exception ex)
        {
            AddStatus("⚠ " + ex.Message);
        }
        finally
        {
            SetStatus(null);
        }
    }

    private static void AppendKv(StringBuilder md, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            md.AppendLine($"**{key}** — {value}");
    }

    private static string? GetString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is not null ? node.ToString() : null;

    /// <summary>Refresh the session strip: the window's own session id + turn
    /// count on the left, the shared Aegis course (summary · temperature ·
    /// thinking) on the right. The course is the single authority every window
    /// reads; the session is this window's private conversation.</summary>
    private void UpdateSessionHeader()
    {
        if (SessionLabel is null) return;
        SessionLabel.Text = $"◈ Session {SessionShort} · {_turnCount} turn{(_turnCount == 1 ? "" : "s")}";
    }

    private async Task RefreshAegisHeaderAsync()
    {
        if (AegisLabel is null) return;
        try
        {
            var aegis = await _bridge.GetAegisAsync();
            if (aegis is null)
            {
                AegisLabel.Text = "course — (no orientation)";
                return;
            }

            var summary = aegis["summary"]?.ToString() ?? "";
            var temp = aegis["temperature"]?.ToString() ?? "";
            var thinking = aegis["thinking"]?.ToString() ?? "";
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(summary))
                parts.Add(summary.Length > 90 ? summary[..90] + "…" : summary);
            if (!string.IsNullOrWhiteSpace(temp)) parts.Add($"T={temp}");
            if (!string.IsNullOrWhiteSpace(thinking)) parts.Add(thinking);
            AegisLabel.Text = parts.Count > 0 ? "course — " + string.Join(" · ", parts) : "course — (empty)";
        }
        catch
        {
            AegisLabel.Text = "course — (daemon unreachable)";
        }
    }

    private void OnInterrupt(object sender, RoutedEventArgs e)
    {
        _ = _bridge.InterruptAsync();
        AddStatus("🛑 interrupt requested");
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) => MaximizeRestoreRequested?.Invoke(this, EventArgs.Empty);

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void AddUserMessage(string text)
    {
        var bubble = new Border
        {
            Tag = "user",
            Background = new SolidColorBrush(_ui.UserBubble),
            CornerRadius = new CornerRadius(8, 2, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = Math.Min(460, AvailableWidth()),
            Margin = new Thickness(24, 4, 4, 4),
            Padding = new Thickness(10, 6, 10, 6),
        };

        var copy = CopyButton.Create(text, bubble, new SolidColorBrush(_ui.MutedForeground));
        var dock = new DockPanel();
        DockPanel.SetDock(copy, Dock.Right);
        dock.Children.Add(copy);
        dock.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(_theme.PythiaForeground),
        });
        bubble.Child = dock;

        Messages.Children.Add(bubble);
        ScrollToEnd();
    }

    private void AddPythiaMessage(string markdown)
    {
        // An info message that already carried the exact answer text is dropped so
        // the final message isn't rendered twice (once as info, once as answer).
        var plain = PythiaMessageCard.PlainPreview(markdown, 1200).Trim();
        if (_infoCard is not null && _infoPlain is not null && plain.Length > 0 && plain == _infoPlain)
            RemoveInfoCard();

        var card = new PythiaMessageCard(_theme, _ui, markdown, startExpanded: true, availableWidth: AvailableWidth());
        _lastPythiaCard?.Collapse();
        _lastPythiaCard = card;
        Messages.Children.Add(card);
        ScrollToEnd();
    }

    private void RemoveInfoCard()
    {
        if (_infoCard is null) return;
        Messages.Children.Remove(_infoCard);
        _infoCard = null;
        _infoPlain = null;
    }

    private void AddIntermediaryMessage(IntermediaryMessage m)
    {
        if (m.Kind == "say")
            AddCompanionSay(m.Glyph, m.Name, m.Message);
        else
            AddOraclePrelude(m.Message);
    }

    /// <summary>Renders the oracle's running reasoning between tool calls as a
    /// subtle, dimmed block — visually distinct from the final answer.</summary>
    private void AddOraclePrelude(string text)
    {
        var block = new Border
        {
            Tag = "prelude",
            Background = new SolidColorBrush(_theme.PythiaCodeBackground),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(28, 3, 4, 3),
            Padding = new Thickness(8, 5, 8, 5),
            Child = new TextBlock
            {
                Text = "◈ " + PythiaMessageCard.PlainPreview(text, 4000),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(_theme.PythiaComplementary),
                FontSize = 11.5,
            },
        };
        Messages.Children.Add(block);
        ScrollToEnd();
    }

    private void AddCompanionSay(string glyph, string name, string message)
    {
        var card = new Border
        {
            Tag = "say",
            Background = new SolidColorBrush(_theme.PythiaCodeBackground),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(4, 3, 24, 3),
            Padding = new Thickness(10, 6, 10, 6),
        };
        var header = new TextBlock
        {
            Text = glyph + " " + (string.IsNullOrWhiteSpace(name) ? "" : name),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(_theme.PythiaComplementary),
            Margin = new Thickness(0, 0, 0, 3),
        };
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(BuildMarkdown(message));
        card.Child = panel;
        Messages.Children.Add(card);
        ScrollToEnd();
    }

    private void AddInfoMessage(string message)
    {
        RemoveInfoCard();
        var card = new Border
        {
            Tag = "info",
            Background = new SolidColorBrush(_theme.PythiaCodeBackground),
            BorderBrush = new SolidColorBrush(_theme.PythiaAccent) { Opacity = 0.22 },
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(28, 3, 4, 3),
            Padding = new Thickness(10, 6, 10, 6),
        };
        var header = new TextBlock
        {
            Text = "💬 Info",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(_theme.PythiaComplementary),
            Margin = new Thickness(0, 0, 0, 2),
        };
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(BuildMarkdown(message));
        card.Child = panel;
        Messages.Children.Add(card);
        _infoCard = card;
        _infoPlain = PythiaMessageCard.PlainPreview(message, 1200).Trim();
        ScrollToEnd();
    }

    private FrameworkElement BuildMarkdown(string markdown)
    {
        var baseSize = AetherCodex.Util.AppFonts.UiSize > 0 ? AetherCodex.Util.AppFonts.UiSize : 14.0;
        var doc = MarkdownRenderer.Render(markdown, _theme, baseSize, AvailableWidth());
        var rtb = new RichTextBox
        {
            Document = doc,
            IsReadOnly = true,
            IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(_theme.PythiaForeground),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        MarkdownRenderer.WireFileLinks(rtb);
        return rtb;
    }

    // ── Companion suggestions ───────────────────────────────────────────────

    private sealed record CompanionSuggestion(string Glyph, string Name, string Prompt, double? Temperature, string? Thinking);

    private static List<CompanionSuggestion> ParseSuggestions(JsonArray? arr)
    {
        var list = new List<CompanionSuggestion>();
        if (arr is null) return list;
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            var prompt = o["prompt"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(prompt)) continue;
            double? temperature = o["temperature"] is JsonValue tv && tv.TryGetValue<double>(out var d) ? d : null;
            list.Add(new CompanionSuggestion(
                o["glyph"]?.GetValue<string>() ?? "✦",
                o["name"]?.GetValue<string>() ?? "",
                prompt,
                temperature,
                o["thinking"]?.GetValue<string>()));
        }
        return list;
    }

    private void AddCompanionSuggestions(JsonArray? suggestions)
    {
        var items = ParseSuggestions(suggestions);
        if (items.Count == 0) return;

        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        var buttons = new List<Button>();
        foreach (var s in items)
        {
            var button = BuildSuggestionButton(s);
            buttons.Add(button);
            wrap.Children.Add(button);
        }

        // Explicit keyboard navigation: TAB steps to the next suggestion, Shift+TAB
        // back to the previous one. Native WPF focus traversal can skip these
        // dynamically-added buttons when moving in reverse, so both directions are
        // driven here (boundary keys fall through to the native loop).
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            buttons[i].PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Tab) return;
                var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                var target = shift ? index - 1 : index + 1;
                if (target < 0 || target >= buttons.Count) return;
                e.Handled = true;
                buttons[target].Focus();
            };
        }

        var header = new TextBlock
        {
            Text = "✨ Begleiter schlagen vor",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_theme.PythiaComplementary),
            Margin = new Thickness(0, 0, 0, 4),
        };

        var host = new Border
        {
            Tag = "suggestions",
            Background = new SolidColorBrush(_theme.PythiaCodeBackground),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(4, 4, 24, 4),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new StackPanel { Children = { header, wrap } },
        };
        Messages.Children.Add(host);
        ScrollToEnd();
    }

    private Button BuildSuggestionButton(CompanionSuggestion s)
    {
        var btn = new Button
        {
            Content = $"{s.Glyph}  {s.Prompt}",
            ToolTip = $"{(string.IsNullOrWhiteSpace(s.Name) ? s.Glyph : s.Name)} — {s.Prompt}",
            Margin = new Thickness(0, 0, 6, 4),
            Padding = new Thickness(10, 5, 10, 5),
            Background = new SolidColorBrush(_theme.PythiaBackground),
            Foreground = new SolidColorBrush(_theme.PythiaForeground),
            BorderBrush = new SolidColorBrush(_ui.Border),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        btn.Click += async (_, _) => await RunSuggestionAsync(s);
        return btn;
    }

    private async Task RunSuggestionAsync(CompanionSuggestion s)
    {
        var key = ResolveCompanionKey(s);
        StartTurn();
        AddSuggestionInvocation(s);
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = s.Prompt });
        _turnCount++;
        UpdateSessionHeader();
        SetStatus($"… {s.Glyph} {s.Name} führt aus");

        try
        {
            var reply = await _bridge.ExecuteSuggestionAsync(key, s.Prompt, s.Temperature, s.Thinking);
            if (reply?.IntermediaryMessages is { } intermediary)
                foreach (var m in intermediary)
                    AddIntermediaryMessage(m);
            if (reply?.Text is { } answer)
            {
                _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer });
                AddPythiaMessage(answer);
                AddCompanionSuggestions(reply.CompanionSuggestions);
            }
        }
        catch (Exception ex)
        {
            AddStatus("⚠ " + ex.Message);
        }
        finally
        {
            SetStatus(null);
            _ = RefreshAegisHeaderAsync();
        }
    }

    private string ResolveCompanionKey(CompanionSuggestion s)
    {
        var roster = _bridge.CompanionRoster;
        var match = roster.FirstOrDefault(c => c.Glyph == s.Glyph)
                 ?? roster.FirstOrDefault(c => c.Name == s.Name)
                 ?? roster.FirstOrDefault(c => c.Key == s.Name);
        return match?.Key ?? s.Name;
    }

    private void AddSuggestionInvocation(CompanionSuggestion s)
    {
        var bubble = new Border
        {
            Tag = "suggestion",
            Background = new SolidColorBrush(_theme.PythiaCodeBackground),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(4, 4, 24, 4),
            Padding = new Thickness(10, 6, 10, 6),
            Child = new TextBlock
            {
                Inlines =
                {
                    new Run(s.Glyph + " ") { FontWeight = FontWeights.Bold },
                    new Run((string.IsNullOrWhiteSpace(s.Name) ? "" : s.Name + ": ")) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(_theme.PythiaComplementary) },
                    new Run(s.Prompt) { Foreground = new SolidColorBrush(_theme.PythiaForeground) },
                },
                TextWrapping = TextWrapping.Wrap,
            },
        };
        Messages.Children.Add(bubble);
        ScrollToEnd();
    }

    private void AddStatus(string text)
    {
        var row = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(_theme.PythiaComplementary),
            FontStyle = FontStyles.Italic,
            FontSize = 11,
            Margin = new Thickness(4, 2, 4, 2),
            TextWrapping = TextWrapping.Wrap,
        };
        Messages.Children.Add(row);
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        // Force a synchronous layout pass so the extent includes the freshly added
        // child; otherwise ScrollToEnd() targets a stale extent and the newest
        // message/suggestion stays below the fold until the next layout tick.
        Scroll.UpdateLayout();
        Scroll.ScrollToEnd();
    }
}