using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace AetherCodex.Editor;

/// <summary>Line-prefix colouriser for unified diffs: added / removed / hunk / header.</summary>
public sealed class DiffHighlighter : DocumentColorizingTransformer
{
    private readonly SyntaxTheme _theme;

    public DiffHighlighter(SyntaxTheme theme) => _theme = theme;

    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        if (text.Length == 0) return;

        ScopeStyle style;
        if (text.StartsWith("+++") || text.StartsWith("---"))
            style = _theme.Resolve("diff.header", Header);
        else if (text.StartsWith("@@"))
            style = _theme.Resolve("diff.hunk", Hunk);
        else if (text.StartsWith("+"))
            style = _theme.Resolve("diff.inserted", Inserted);
        else if (text.StartsWith("-"))
            style = _theme.Resolve("diff.deleted", Deleted);
        else if (IsMeta(text))
            style = _theme.Resolve("diff.header", Header);
        else
            return;

        var brush = new SolidColorBrush(style.Color);
        brush.Freeze();
        ChangeLinePart(line.Offset, line.EndOffset, element =>
        {
            element.TextRunProperties.SetForegroundBrush(brush);
            if (style.Bold)
                element.TextRunProperties.SetTypeface(new Typeface(
                    element.TextRunProperties.Typeface.FontFamily,
                    FontStyles.Normal,
                    FontWeights.Bold,
                    element.TextRunProperties.Typeface.Stretch));
        });
    }

    private static bool IsMeta(string text) =>
        text.StartsWith("diff ") || text.StartsWith("index ") ||
        text.StartsWith("new file") || text.StartsWith("deleted file") ||
        text.StartsWith("similarity") || text.StartsWith("rename ") ||
        text.StartsWith("copy ") || text.StartsWith("old mode") ||
        text.StartsWith("new mode") || text.StartsWith("Binary files") ||
        text.StartsWith("GIT binary") || text.StartsWith("\\ No newline");

    private static readonly ScopeStyle Inserted = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly ScopeStyle Deleted = new(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly ScopeStyle Hunk = new(Color.FromRgb(0x58, 0xA6, 0xFF), Bold: true);
    private static readonly ScopeStyle Header = new(Color.FromRgb(0x8B, 0x94, 0x9E));
}