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
    private (string Name, JsonObject? Args)? _pendingTool;
    private PythiaMessageCard? _lastPythiaCard;
    private Border? _askUserCard;

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
        _bridge.EventReceived += OnEvent;
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
                    if (b.Child is TextBlock tb) tb.Foreground = new SolidColorBrush(theme.PythiaForeground);
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
        JsonArray? entries;
        try { entries = await _bridge.FetchHistoryAsync(limit); }
        catch { return; }
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

        bool sameRoot = string.Equals(contextRoot, _lastSyncedRoot, StringComparison.Ordinal);
        bool hasHistory = messages.Count > 0;

        // Skip the rebuild when Pythia is already showing this root's history and
        // Mnemosyne hasn't moved since we last synced it.
        if (sameRoot && hasHistory
            && newest is not null && _lastSyncedTimestamp is not null
            && string.CompareOrdinal(newest, _lastSyncedTimestamp) <= 0)
        {
            return;
        }

        _lastSyncedRoot = contextRoot;
        _lastSyncedTimestamp = newest;

        await Dispatcher.InvokeAsync(() =>
        {
            _history.Clear();
            _history.AddRange(messages);
            Messages.Children.Clear();
            _currentGroup = null;
            _lastPythiaCard = null;
            _askUserCard = null;
            foreach (var m in messages)
            {
                var role = m["role"]?.GetValue<string>();
                var content = m["content"]?.GetValue<string>() ?? "";
                if (role == "user") AddUserMessage(content);
                else if (role == "assistant") AddPythiaMessage(content);
            }
            if (messages.Count > 0)
                AddStatus($"↻ {messages.Count / 2} exchanges restored from Mnemosyne");
        });
    }

    private void OnReady(IReadOnlyList<CompanionInfo> roster)
    {
        Dispatcher.Invoke(() =>
        {
            StatusDot.Fill = new SolidColorBrush(_theme.PythiaAccent);
            Roster.ItemsSource = null;
            Roster.ItemsSource = roster;
        });
    }

    private void OnEvent(DaemonEvent evt)
    {
        if (evt is RecoverEvent recover)
            Dispatcher.Invoke(() => AddStatus($"🛑 Thread tore — {recover.Suggestion.Glyph} {recover.Suggestion.Prompt}"));
    }

    private void OnStream(StreamEvent evt)
    {
        if (evt.Method != "status") return;
        // A rendering hiccup must never kill the reader thread: the daemon's
        // `result` frame still has to reach the awaiting SendAsync, otherwise the
        // turn looks stuck at "tool starting" forever.
        try { Dispatcher.Invoke(() => HandleStatus(evt.StatusType, evt.Data, evt.Uuid)); }
        catch (Exception) { /* swallow — never wedge the frame pump */ }
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
        var group = EnsureGroup();
        group.Add(new InstrumentumCard(_theme, _ui, name, args, result, timing));
        ScrollToEnd();
    }

    private InstrumentGroup EnsureGroup()
    {
        if (_currentGroup is null)
        {
            _currentGroup = new InstrumentGroup(_theme, _ui);
            Messages.Children.Add(_currentGroup);
        }
        return _currentGroup;
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
        if (string.IsNullOrWhiteSpace(uuid)) return;
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

        StartTurn();
        AddUserMessage(text);
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        SetStatus("… summoning the oracle");

        try
        {
            var companions = _activeCompanion == "pythia" ? Array.Empty<string>() : new[] { _activeCompanion };
            var reply = await _bridge.ChatAsync(_history, activeCompanions: companions);
            if (reply?.Text is { } answer)
            {
                _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer });
                if (reply.ReasoningContent is { } reasoning)
                    _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = $"__reasoning__: {reasoning}" });
                AddPythiaMessage(answer);
            }
            if (reply?.CompanionSays is { } says)
                foreach (var s in says)
                    AddCompanionSay(s.Glyph, s.Name, s.Message);
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
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(_theme.PythiaForeground),
            },
        };
        Messages.Children.Add(bubble);
        ScrollToEnd();
    }

    private void AddPythiaMessage(string markdown)
    {
        var card = new PythiaMessageCard(_theme, _ui, markdown, startExpanded: true);
        _lastPythiaCard?.Collapse();
        _lastPythiaCard = card;
        Messages.Children.Add(card);
        ScrollToEnd();
    }

    private void AddCompanionSay(string glyph, string name, string message)
    {
        var row = new DockPanel { Margin = new Thickness(4, 3, 4, 3) };
        var glyphText = new TextBlock
        {
            Text = glyph + " ",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Top,
        };
        DockPanel.SetDock(glyphText, Dock.Left);
        row.Children.Add(glyphText);
        row.Children.Add(new TextBlock
        {
            Inlines = {
                new Run(name + ": ") { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(_theme.PythiaComplementary) },
                new Run(message) { Foreground = new SolidColorBrush(_theme.PythiaForeground) },
            },
            TextWrapping = TextWrapping.Wrap,
        });
        Messages.Children.Add(row);
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

    private void ScrollToEnd() => Scroll.ScrollToEnd();
}