using System.Text.RegularExpressions;
using System.Windows.Media;

namespace AetherCodex.Editor;

/// <summary>Kinds of light-weight markup recognised inside code comments.</summary>
public enum CommentMarkupKind { Todo, Code, Bold, Italic }

/// <summary>
/// Tokenises markdown-ish decoration inside code comments: TODO/FIXME markers,
/// `inline code`, **bold** and *italic*. Shared by the editor highlighter
/// (<see cref="RegexHighlighter"/>) and the markdown fenced-code colouriser
/// (<see cref="CodeColorizer"/>) so both treat comment prose the same way.
/// </summary>
public static class CommentMarkup
{
    private static readonly Regex TodoRx = new(
        @"(?i)\b(TODO|FIXME|HACK|XXX|BUG|NOTE|OPTIMIZE|WIP|REVIEW|WARNING|DEPRECATED)\b",
        RegexOptions.Compiled);

    private static readonly Regex CodeRx = new(@"`[^`\r\n]+`", RegexOptions.Compiled);

    private static readonly Regex BoldRx = new(
        @"\*\*[^*\r\n]+\*\*|__[^_\r\n]+__", RegexOptions.Compiled);

    // Single * / _ emphasis. The lookarounds stop the markers of /* */ block
    // comments (the '*' in '/*' is preceded by '/'), **bold**, and underscore-
    // heavy identifiers like foo_bar_baz from matching.
    private static readonly Regex ItalicRx = new(
        @"(?<![*\w/])\*[^*\r\n]+\*(?![*\w/])|(?<![\w*/])_[^_\r\n]+_(?![\w*/])",
        RegexOptions.Compiled);

    /// <summary>TextMate scope used for a given markup kind (resolved against the theme).</summary>
    public static string ScopeFor(CommentMarkupKind kind) => kind switch
    {
        CommentMarkupKind.Todo => "comment.keyword",
        CommentMarkupKind.Code => "markup.code.inline",
        CommentMarkupKind.Bold => "markup.bold",
        CommentMarkupKind.Italic => "markup.italic",
        _ => "comment",
    };

    /// <summary>Distinct, legible TODO-marker style for dark vs light themes.</summary>
    public static ScopeStyle TodoStyle(bool isDark) => isDark
        ? new ScopeStyle(Color.FromRgb(0xFF, 0xB4, 0x54), Bold: true)
        : new ScopeStyle(Color.FromRgb(0xC8, 0x5A, 0x00), Bold: true);

    /// <summary>Returns non-overlapping (start, length, kind) spans, sorted by position.
    /// Earlier start wins; longer match wins ties (so `**bold**` outranks any
    /// nested TODO, and a code span swallows a marker inside backticks).</summary>
    public static List<(int Start, int Length, CommentMarkupKind Kind)> Tokenize(string text)
    {
        var spans = new List<(int Start, int Length, CommentMarkupKind Kind)>();

        foreach (Match m in TodoRx.Matches(text)) spans.Add((m.Index, m.Length, CommentMarkupKind.Todo));
        foreach (Match m in CodeRx.Matches(text)) spans.Add((m.Index, m.Length, CommentMarkupKind.Code));
        foreach (Match m in BoldRx.Matches(text)) spans.Add((m.Index, m.Length, CommentMarkupKind.Bold));
        foreach (Match m in ItalicRx.Matches(text)) spans.Add((m.Index, m.Length, CommentMarkupKind.Italic));

        spans.Sort((a, b) => a.Start != b.Start
            ? a.Start.CompareTo(b.Start)
            : b.Length.CompareTo(a.Length));

        var result = new List<(int, int, CommentMarkupKind)>();
        int lastEnd = 0;
        foreach (var (start, length, kind) in spans)
        {
            if (start < lastEnd) continue;
            result.Add((start, length, kind));
            lastEnd = start + length;
        }
        return result;
    }
}
