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
        Dispatcher.Invoke(() => HandleStatus(evt.StatusType, evt.Data));
    }

    private void HandleStatus(string? type, JsonObject? data)
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
                SetStatus($"→ {tool}");
                break;
            case DaemonFrames.Status.ToolCompleted:
                var t = data?["execution_time"] is JsonValue v && v.TryGetValue<double>(out var d) ? $"{d:0}ms" : "";
                AddToolCard(tool, data?["result"], t);
                SetStatus(null);
                break;
            case DaemonFrames.Status.CommandExecuting:
                SetStatus($"$ {data?["command"]?.GetValue<string>()}");
                break;
            case DaemonFrames.Status.CommandCompleted:
                SetStatus(null);
                break;
            default:
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

    private void AddToolCard(string? tool, JsonNode? result, string timing)
    {
        if (string.IsNullOrWhiteSpace(tool)) return;
        var preview = Preview(result);
        var stack = new StackPanel { Margin = new Thickness(4, 2, 4, 2) };
        stack.Children.Add(new TextBlock
        {
            Text = $"✓ {tool}  {timing}",
            Foreground = new SolidColorBrush(_theme.PythiaAccent),
            FontWeight = FontWeights.Bold,
            FontSize = 11,
        });
        if (!string.IsNullOrWhiteSpace(preview))
        {
            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(_theme.PythiaCodeBackground),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 2, 0, 0),
                Child = new TextBlock
                {
                    Text = preview,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(_theme.PythiaForeground),
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 160,
                },
            });
        }
        Messages.Children.Add(stack);
        ScrollToEnd();
    }

    private static string Preview(JsonNode? result)
    {
        if (result is null) return "";
        if (result is JsonValue v) return v.ToString();
        var obj = result as JsonObject;
        if (obj?["content"] is JsonValue content) return Truncate(content.ToString(), 4000);
        if (obj?["error"] is JsonValue err) return "⚠ " + err.ToString();
        return Truncate(result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }), 4000);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

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

    private void AddUserMessage(string text)
    {
        var bubble = new Border
        {
            Background = new SolidColorBrush(_ui.UserBubble),
            CornerRadius = new CornerRadius(8, 2, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 460,
            Margin = new Thickness(24, 4, 4, 4),
            Padding = new Thickness(10, 6, 10, 6),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(_theme.PythiaForeground) },
        };
        Messages.Children.Add(bubble);
        ScrollToEnd();
    }

    private void AddPythiaMessage(string markdown)
    {
        var doc = MarkdownRenderer.Render(markdown, _theme.PythiaForeground, _theme.PythiaCodeBackground, 13);

        var viewer = new RichTextBox
        {
            Document = doc,
            IsReadOnly = true,
            IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(_theme.PythiaForeground),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 480,
            Margin = new Thickness(0),
        };

        var bubble = new Border
        {
            Background = new SolidColorBrush(_theme.PythiaBackground),
            BorderBrush = new SolidColorBrush(_theme.PythiaAccent) { Opacity = 0.35 },
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(2, 8, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 560,
            Margin = new Thickness(4, 4, 24, 4),
            Padding = new Thickness(10, 4, 10, 4),
            Child = viewer,
        };
        Messages.Children.Add(bubble);
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