using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AetherCodex.Editor;

namespace AetherCodex.Panels;

/// <summary>
/// A collapsible assistant message. Collapsed it shows a one-to-few-line plain
/// preview (older content cropped); expanded it renders the full markdown in
/// theme colours with no inner scroll area — the panel's own vertical scroller
/// owns overflow and code blocks wrap instead of demanding horizontal space.
/// </summary>
public sealed class PythiaMessageCard : Border
{
    private const int PreviewChars = 300;

    private readonly TextBlock _preview;
    private readonly TextBlock _chevron;
    private readonly TextBlock _label;
    private FrameworkElement _full;
    private SyntaxTheme _theme;
    private UiColors _ui;
    private readonly string _markdown;
    private bool _expanded;

    public PythiaMessageCard(SyntaxTheme theme, UiColors ui, string markdown, bool startExpanded)
    {
        _expanded = startExpanded;
        _theme = theme;
        _ui = ui;
        _markdown = markdown;

        _preview = new TextBlock
        {
            Text = PlainPreview(markdown, PreviewChars),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = AetherCodex.Util.AppFonts.UiFamily,
            Foreground = new SolidColorBrush(theme.PythiaBaseText),
            FontSize = 12.5,
            MaxHeight = 58, // ~3 lines
        };
        _full = BuildMarkdown(theme, markdown);

        _chevron = new TextBlock
        {
            Text = startExpanded ? "▾" : "▸",
            FontSize = 10,
            Foreground = new SolidColorBrush(ui.MutedForeground),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _label = new TextBlock
        {
            Text = "PYTHIA",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(theme.PythiaAccent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        var header = new DockPanel
        {
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, 0, 3),
        };
        DockPanel.SetDock(_chevron, Dock.Right);
        header.Children.Add(_chevron);
        DockPanel.SetDock(_label, Dock.Left);
        header.Children.Add(_label);
        header.MouseLeftButtonUp += (_, _) => Toggle();

        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(_expanded ? _full : _preview);

        Child = body;
        Background = new SolidColorBrush(theme.PythiaBackground);
        BorderBrush = new SolidColorBrush(theme.PythiaAccent) { Opacity = 0.30 };
        BorderThickness = new Thickness(0, 0, 0, 1);
        CornerRadius = new CornerRadius(2, 8, 8, 8);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Thickness(4, 6, 24, 6);
        Padding = new Thickness(10, 6, 10, 8);
    }

    public bool IsExpanded => _expanded;

    public void Collapse()
    {
        if (_expanded) Toggle();
    }

    /// <summary>Re-skin the card after a live theme switch. Rebuilds the full
    /// markdown document and refreshes the visible element, preserving the
    /// collapsed/expanded state.</summary>
    public void ReTheme(SyntaxTheme theme, UiColors ui)
    {
        _theme = theme;
        _ui = ui;

        _preview.Foreground = new SolidColorBrush(theme.PythiaBaseText);
        _chevron.Foreground = new SolidColorBrush(ui.MutedForeground);
        _label.Foreground = new SolidColorBrush(theme.PythiaAccent);
        Background = new SolidColorBrush(theme.PythiaBackground);
        BorderBrush = new SolidColorBrush(theme.PythiaAccent) { Opacity = 0.30 };

        _full = BuildMarkdown(theme, _markdown);
        if (_expanded)
        {
            var body = (DockPanel)Child;
            body.Children.RemoveAt(body.Children.Count - 1);
            body.Children.Add(_full);
        }
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        _chevron.Text = _expanded ? "▾" : "▸";
        var body = (DockPanel)Child;
        body.Children.RemoveAt(body.Children.Count - 1);
        body.Children.Add(_expanded ? _full : _preview);
    }

    private static FrameworkElement BuildMarkdown(SyntaxTheme theme, string markdown)
    {
        var doc = MarkdownRenderer.Render(markdown, theme);
        var rtb = new RichTextBox
        {
            Document = doc,
            IsReadOnly = true,
            IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(theme.PythiaForeground),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        MarkdownRenderer.WireFileLinks(rtb);
        return rtb;
    }

    /// <summary>Strip markdown down to a single flowing preview line.</summary>
    internal static string PlainPreview(string markdown, int max)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var t = markdown;
        t = Regex.Replace(t, "```.*?```", " ", RegexOptions.Singleline);
        t = t.Replace("**", "").Replace("__", "").Replace("`", "");
        t = Regex.Replace(t, @"<file\b[^>]*>(.*?)</file>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        t = Regex.Replace(t, @"<file\b[^>]*>(.*?)</file>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        t = Regex.Replace(t, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        t = t.Replace("> ", "");
        t = Regex.Replace(t, @"^\s*[-*+]\s+", "", RegexOptions.Multiline);
        t = Regex.Replace(t, @"^\s*\d+[.)]\s+", "", RegexOptions.Multiline);
        t = t.Replace("---", " ").Replace("***", " ").Replace("___", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}