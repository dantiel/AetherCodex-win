using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
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
/// InstrumentumRenderer: the collapsed state is a glyph + tool name + one-line
/// digest; compact shows a capped body; expanded shows the full body. Clicking
/// the header cycles the density. Never demands horizontal space — every body
/// line wraps.
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

    private readonly TextBlock _chevron;
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
        _result = result;
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
        DockPanel.SetDock(_glyph, Dock.Left);
        header.Children.Add(_glyph);
        DockPanel.SetDock(_title, Dock.Left);
        header.Children.Add(_title);
        header.Children.Add(_summaryText);
        header.MouseLeftButtonUp += (_, _) => CycleDensity();

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

    public InstrumentumDensity Density
    {
        get => _density;
        set { _density = value; ApplyDensity(); }
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
        _accentBar.Background = new SolidColorBrush(_statusColor);
        _bodyHost.Background = new SolidColorBrush(theme.PythiaCodeBackground);
        _bodyHost.BorderBrush = new SolidColorBrush(ui.Border);
        Background = new SolidColorBrush(ThemePalette.Mix(theme.PythiaBackground, ui.Foreground, 0.02));
        BorderBrush = new SolidColorBrush(ui.Border);

        ApplyDensity();
    }

    private void CycleDensity()
    {
        _density = _density switch
        {
            InstrumentumDensity.OneLiner => InstrumentumDensity.Compact,
            InstrumentumDensity.Compact => InstrumentumDensity.Expanded,
            _ => InstrumentumDensity.OneLiner,
        };
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

        var cap = _density == InstrumentumDensity.Compact ? CompactLineCount : _lines.Count;
        foreach (var line in _lines.Take(cap))
            _body.Children.Add(MakeLine(line));
        if (_lines.Count > cap)
            _body.Children.Add(MakeHint($"… {_lines.Count - cap} weitere Zeilen"));
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
}