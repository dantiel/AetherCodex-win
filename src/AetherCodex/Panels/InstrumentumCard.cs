using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AetherCodex.Editor;

namespace AetherCodex.Panels;

public enum InstrumentumDensity
{
    /// <summary>Just the header line: glyph, name and the coloured digest.</summary>
    OneLiner,
    /// <summary>Header plus a capped body (a handful of lines).</summary>
    Compact,
    /// <summary>Header plus the full body.</summary>
    Expanded,
}

/// <summary>
/// An illuminated panel for a single instrumentum result, mirroring the macOS
/// InstrumentumRenderer. The detail level degrades with history distance: the
/// newest instrumentum rests expanded, older ones rest at a medium (compact)
/// or one-liner level. A single click toggles between the resting level and the
/// full expanded body — never a three-state cycle. Never demands horizontal
/// space — every body line wraps.
/// </summary>
public sealed class InstrumentumCard : Border
{
    private const int CompactLineCount = 6;

    private static FontFamily MonoFont => AetherCodex.Util.AppFonts.MonoFamily;
    private static FontFamily ProseFont => AetherCodex.Util.AppFonts.UiFamily;

    private SyntaxTheme _theme;
    private UiColors _ui;
    private readonly List<string> _lines;
    private readonly bool _mono;
    private readonly string _name;
    private readonly JsonNode? _args;
    private readonly bool _isReadFile;

    private readonly TextBlock _chevron;
    private readonly Button _close;
    private readonly Border _bodyHost;
    private readonly StackPanel _body;

    private readonly JsonNode? _result;
    private Color _statusColor;
    private readonly TextBlock _glyph;
    private readonly TextBlock _title;
    private readonly TextBlock _summaryText;
    private readonly Border _accentBar;

    private InstrumentumDensity _density = InstrumentumDensity.Compact;

    public InstrumentumCard(SyntaxTheme theme, UiColors ui, string name, JsonNode? args, JsonNode? result, string? timing)
    {
        _theme = theme;
        _ui = ui;
        _name = name;
        _args = args;
        _result = result;
        _isReadFile = name == "read_file";
        _lines = InstrumentumRenderer.BuildBody(name, args, result, out _mono);
        _statusColor = InstrumentumRenderer.IsOk(result) ? ui.Accent : ui.Danger;

        var summary = InstrumentumRenderer.Summary(name, args, result);
        if (!string.IsNullOrWhiteSpace(timing))
            summary = summary.Length == 0 ? timing : $"{summary} · {timing}";

        // Header row: glyph · name · summary · chevron.
        _glyph = new TextBlock
        {
            Text = InstrumentumRenderer.Glyph(name) + " ",
            FontFamily = MonoFont,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(_statusColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _title = new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(theme.PythiaForeground),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _summaryText = new TextBlock
        {
            Text = summary.Length == 0 ? "" : "  " + summary,
            FontSize = 11,
            Foreground = new SolidColorBrush(ui.MutedForeground),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _chevron = new TextBlock
        {
            Text = "▾",
            FontSize = 10,
            Foreground = new SolidColorBrush(ui.MutedForeground),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new DockPanel
        {
            Margin = new Thickness(8, 5, 8, 5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        DockPanel.SetDock(_chevron, Dock.Right);
        header.Children.Add(_chevron);
        _close = new Button
        {
            Content = "×",
            FontSize = 10,
            Foreground = new SolidColorBrush(ui.MutedForeground),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 4, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Instrumentum schließen",
        };
        _close.Click += (_, _) => CloseRequested?.Invoke(this);
        DockPanel.SetDock(_close, Dock.Right);
        header.Children.Add(_close);
        DockPanel.SetDock(_glyph, Dock.Left);
        header.Children.Add(_glyph);
        DockPanel.SetDock(_title, Dock.Left);
        header.Children.Add(_title);
        header.Children.Add(_summaryText);
        header.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is Button) return;
            Toggle();
        };

        // Body host — hidden entirely in the one-liner state.
        _body = new StackPanel();
        _bodyHost = new Border
        {
            Background = new SolidColorBrush(theme.PythiaCodeBackground),
            BorderBrush = new SolidColorBrush(ui.Border),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 6, 10, 8),
            Child = _body,
        };

        var content = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);
        content.Children.Add(_bodyHost);

        _accentBar = new Border
        {
            Width = 3,
            Background = new SolidColorBrush(_statusColor),
        };

        var root = new DockPanel();
        DockPanel.SetDock(_accentBar, Dock.Left);
        root.Children.Add(_accentBar);
        root.Children.Add(content);

        Child = root;
        Background = new SolidColorBrush(ThemePalette.Mix(theme.PythiaBackground, ui.Foreground, 0.02));
        BorderBrush = new SolidColorBrush(ui.Border);
        BorderThickness = new Thickness(0, 0, 0, 1);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Thickness(0, 0, 0, 1);

        ApplyDensity();
    }

    /// <summary>The auto-degraded resting level (one-liner / compact / expanded)
    /// assigned from the instrumentum's age in history. Clicking toggles between
    /// this resting level and the full expanded body.</summary>
    /// <summary>Raised when the user dismisses this card via the ✕ button.</summary>
    public event Action<InstrumentumCard>? CloseRequested;

    public InstrumentumDensity Resting { get; private set; } = InstrumentumDensity.Compact;

    /// <summary>Assign the recency-degraded resting level and whether the card is
    /// currently shown fully expanded (the newest instrumentum is).</summary>
    public void SetResting(InstrumentumDensity resting, bool expanded)
    {
        Resting = resting;
        _density = expanded ? InstrumentumDensity.Expanded : resting;
        ApplyDensity();
    }

    /// <summary>Re-skin every brush after a live theme switch. Recomputes the
    /// status colour (success/danger) and rebuilds the body lines with the new
    /// palette, preserving the current density + collapsed state.</summary>
    public void ReTheme(SyntaxTheme theme, UiColors ui)
    {
        _theme = theme;
        _ui = ui;
        _statusColor = InstrumentumRenderer.IsOk(_result) ? ui.Accent : ui.Danger;

        _glyph.Foreground = new SolidColorBrush(_statusColor);
        _title.Foreground = new SolidColorBrush(theme.PythiaForeground);
        _summaryText.Foreground = new SolidColorBrush(ui.MutedForeground);
        _chevron.Foreground = new SolidColorBrush(ui.MutedForeground);
        _close.Foreground = new SolidColorBrush(ui.MutedForeground);
        _accentBar.Background = new SolidColorBrush(_statusColor);
        _bodyHost.Background = new SolidColorBrush(theme.PythiaCodeBackground);
        _bodyHost.BorderBrush = new SolidColorBrush(ui.Border);
        Background = new SolidColorBrush(ThemePalette.Mix(theme.PythiaBackground, ui.Foreground, 0.02));
        BorderBrush = new SolidColorBrush(ui.Border);

        ApplyDensity();
    }

    private void Toggle()
    {
        _density = _density == InstrumentumDensity.Expanded ? Resting : InstrumentumDensity.Expanded;
        ApplyDensity();
    }

    private void ApplyDensity()
    {
        _body.Children.Clear();
        _bodyHost.Visibility = _density == InstrumentumDensity.OneLiner
            ? Visibility.Collapsed
            : Visibility.Visible;
        _chevron.Text = _density == InstrumentumDensity.OneLiner ? "▸" : "▾";

        if (_density == InstrumentumDensity.OneLiner) return;

        // read_file gets a line-numbered, syntax-highlighted view.
        if (_isReadFile)
        {
            _body.Children.Add(BuildReadFileBody(_density == InstrumentumDensity.Compact));
            return;
        }

        var cap = _density == InstrumentumDensity.Compact ? CompactLineCount : _lines.Count;
        foreach (var line in _lines.Take(cap))
            _body.Children.Add(MakeLine(line));
        if (_lines.Count > cap)
            _body.Children.Add(MakeHint($"… {_lines.Count - cap} weitere Zeilen"));
    }

    private FrameworkElement BuildReadFileBody(bool compact)
    {
        var host = new StackPanel();
        var path = S(_args?["path"]);
        var header = FileHeader(path, _args?["range"]);
        if (header.Length > 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = header,
                FontFamily = MonoFont,
                FontSize = 11,
                Foreground = new SolidColorBrush(_ui.MutedForeground),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        var o = AsObj(_result);
        var content = o is null ? S(_result) : S(o["content"]);
        var lang = Languages.FromPath(path);
        var lines = SplitLines(content);
        var cap = compact ? CompactLineCount : lines.Count;

        // A range read returns absolute lines, so number from the range start.
        var startLine = 1;
        if (_args?["range"] is JsonArray { Count: 2 } r
            && int.TryParse(S(r[0]), out var s0) && s0 > 0)
            startLine = s0;

        var gutter = new SolidColorBrush(_ui.MutedForeground);
        var fallback = _theme.PythiaForeground;
        bool inBlock = false;

        for (int i = 0; i < cap && i < lines.Count; i++)
        {
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontFamily = MonoFont,
                FontSize = 11,
                Foreground = new SolidColorBrush(fallback),
                Margin = new Thickness(0, 0, 0, 1),
            };
            tb.Inlines.Add(new Run((startLine + i).ToString().PadLeft(4) + " │ ")
            {
                FontFamily = MonoFont,
                FontSize = 11,
                Foreground = gutter,
            });
            CodeColorizer.ColorizeLine(tb.Inlines, _theme, lang, lines[i], MonoFont, 11, fallback, ref inBlock);
            host.Children.Add(tb);
        }

        if (lines.Count > cap)
            host.Children.Add(MakeHint($"… {lines.Count - cap} weitere Zeilen"));
        return host;
    }

    private TextBlock MakeLine(string line)
    {
        return new TextBlock
        {
            Text = line.Length == 0 ? " " : line,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = _mono ? MonoFont : ProseFont,
            FontSize = _mono ? 11 : 12,
            Foreground = new SolidColorBrush(_theme.PythiaForeground),
            Margin = new Thickness(0, 0, 0, 1),
        };
    }

    private TextBlock MakeHint(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11,
        FontStyle = FontStyles.Italic,
        Foreground = new SolidColorBrush(_ui.MutedForeground),
        Margin = new Thickness(0, 2, 0, 0),
    };

    private static JsonObject? AsObj(JsonNode? node)
    {
        if (node is JsonObject o) return o;
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            var t = s.TrimStart();
            if (t.Length > 0 && (t[0] == '{' || t[0] == '['))
            {
                try { return JsonNode.Parse(s) as JsonObject; }
                catch (System.Text.Json.JsonException) { }
            }
        }
        return null;
    }

    private static string S(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            return v.ToString();
        }
        return node.ToJsonString();
    }

    private static string FileHeader(string path, JsonNode? range)
    {
        if (path.Length == 0) return "";
        if (range is JsonArray { Count: 2 } r)
            return $"{path}[{S(r[0])}..{S(r[1])}]";
        return path;
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        foreach (var l in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            lines.Add(l);
        return lines;
    }
}