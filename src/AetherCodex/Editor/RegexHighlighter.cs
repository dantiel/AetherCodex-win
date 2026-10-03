using System.Text.RegularExpressions;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace AetherCodex.Editor;

/// <summary>
/// Line-based regex colouriser that maps tokens to the active theme's TextMate
/// scopes. Mirrors the macOS <c>TemplateSyntaxEngine</c>/<c>SyntaxEngine</c>
/// (the tree-sitter engine is a later refinement). Multi-line block comments are
/// resolved with a per-version cache scan.
/// </summary>
public sealed class RegexHighlighter : DocumentColorizingTransformer
{
    private readonly SyntaxTheme _theme;
    private readonly LanguageDefinition _language;

    private object? _cachedVersion;
    private HashSet<int> _blockCommentLines = new();

    public RegexHighlighter(SyntaxTheme theme, LanguageDefinition language)
    {
        _theme = theme;
        _language = language;
    }

    public void Invalidate()
    {
        _cachedVersion = null;
        _blockCommentLines.Clear();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var doc = CurrentContext.Document;
        if (!ReferenceEquals(doc.Version, _cachedVersion))
            RecomputeBlockComments(doc);

        var text = doc.GetText(line);
        if (text.Length == 0) return;

        bool lineStartsInComment = _blockCommentLines.Contains(line.LineNumber);
        var excluded = new List<(int Start, int Length)>();

        // 1. Block comment.
        if (_language.BlockComments.Length > 0)
        {
            foreach (var (start, end) in _language.BlockComments)
            {
                int searchFrom = lineStartsInComment ? 0 : text.IndexOf(start, StringComparison.Ordinal);
                if (lineStartsInComment || searchFrom >= 0)
                {
                    int from = lineStartsInComment ? 0 : searchFrom;
                    int endIdx = text.IndexOf(end, from + (lineStartsInComment ? 0 : start.Length), StringComparison.Ordinal);
                    int len = endIdx >= 0 ? (endIdx + end.Length - from) : (text.Length - from);
                    Colorize(line, from, len, _theme.Resolve("comment", Fallback.Comment));
                    excluded.Add((from, len));
                    break;
                }
            }
        }

        // 2. Line comments.
        if (_language.LineComments.Length > 0)
        {
            foreach (Match m in _language.CommentRegex.Matches(text))
            {
                if (IsExcluded(m.Index, m.Length, excluded)) continue;
                Colorize(line, m.Index, m.Length, _theme.Resolve("comment", Fallback.Comment));
                excluded.Add((m.Index, m.Length));
            }
        }

        // 3. Strings.
        foreach (Match m in StringRegex.Matches(text))
        {
            if (IsExcluded(m.Index, m.Length, excluded)) continue;
            Colorize(line, m.Index, m.Length, _theme.Resolve("string", Fallback.String));
            excluded.Add((m.Index, m.Length));
        }

        // 4. Structural tokens (numbers, keywords, types, constants, builtins, calls).
        ApplyRegex(line, text, excluded, _language.NumberRegex, "number", Fallback.Number);
        ApplyRegex(line, text, excluded, _language.ConstantRegex, "constant.language", Fallback.Constant);
        ApplyRegex(line, text, excluded, _language.KeywordRegex, "keyword", Fallback.Keyword);
        ApplyRegex(line, text, excluded, _language.TypeRegex, "type", Fallback.Type);
        ApplyRegex(line, text, excluded, _language.BuiltinRegex, "support.function", Fallback.Function);
        ApplyRegex(line, text, excluded, _language.FunctionCallRegex, "entity.name.function", Fallback.Function);
    }

    private void ApplyRegex(
        DocumentLine line, string text, List<(int Start, int Length)> excluded,
        Regex regex, string scope, ScopeStyle fallback)
    {
        foreach (Match m in regex.Matches(text))
        {
            if (m.Length == 0) continue;
            if (IsExcluded(m.Index, m.Length, excluded)) continue;
            Colorize(line, m.Index, m.Length, _theme.Resolve(scope, fallback));
        }
    }

    private void Colorize(DocumentLine line, int start, int length, ScopeStyle style)
    {
        var brush = BrushCache.Get(style.Color);
        ChangeLinePart(
            line.Offset + start,
            line.Offset + start + length,
            element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
                if (style.Bold)
                    element.TextRunProperties.SetTypeface(new System.Windows.Media.Typeface(
                        element.TextRunProperties.Typeface.FontFamily,
                        System.Windows.FontStyles.Normal,
                        System.Windows.FontWeights.Bold,
                        element.TextRunProperties.Typeface.Stretch));
                if (style.Italic)
                    element.TextRunProperties.SetTypeface(new System.Windows.Media.Typeface(
                        element.TextRunProperties.Typeface.FontFamily,
                        System.Windows.FontStyles.Italic,
                        element.TextRunProperties.Typeface.Weight,
                        element.TextRunProperties.Typeface.Stretch));
            });
    }

    private static bool IsExcluded(int start, int length, List<(int Start, int Length)> excluded)
    {
        foreach (var (s, l) in excluded)
            if (start >= s && start + length <= s + l) return true;
        return false;
    }

    private void RecomputeBlockComments(TextDocument doc)
    {
        _blockCommentLines.Clear();
        _cachedVersion = doc.Version;

        if (_language.BlockComments.Length == 0) return;

        var (startDelim, endDelim) = _language.BlockComments[0];
        bool inBlock = false;
        int lineNumber = 1;
        foreach (DocumentLine line in doc.Lines)
        {
            var text = doc.GetText(line);
            if (inBlock)
            {
                _blockCommentLines.Add(lineNumber);
                int end = text.IndexOf(endDelim, StringComparison.Ordinal);
                if (end >= 0)
                {
                    // Comment may reopen after the end on the same line.
                    int after = end + endDelim.Length;
                    int reopen = text.IndexOf(startDelim, after, StringComparison.Ordinal);
                    if (reopen >= 0) { /* still in block */ }
                    else inBlock = false;
                }
            }
            else
            {
                int start = text.IndexOf(startDelim, StringComparison.Ordinal);
                if (start >= 0)
                {
                    _blockCommentLines.Add(lineNumber);
                    int end = text.IndexOf(endDelim, start + startDelim.Length, StringComparison.Ordinal);
                    if (end < 0) inBlock = true;
                }
            }
            lineNumber++;
        }
    }

    private static readonly Regex StringRegex = new(
        @"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`",
        RegexOptions.Compiled);

    /// <summary>Fallback scope palette used when a theme omits a scope.</summary>
    private static class Fallback
    {
        public static readonly ScopeStyle Comment = new(Color.FromRgb(0x5A, 0x60, 0x68), Italic: true);
        public static readonly ScopeStyle String = new(Color.FromRgb(0x8F, 0xB5, 0x73));
        public static readonly ScopeStyle Number = new(Color.FromRgb(0xB0, 0x96, 0x5E));
        public static readonly ScopeStyle Keyword = new(Color.FromRgb(0xC5, 0x86, 0xC0));
        public static readonly ScopeStyle Type = new(Color.FromRgb(0x4E, 0xC9, 0xB0));
        public static readonly ScopeStyle Constant = new(Color.FromRgb(0x6A, 0x99, 0x55));
        public static readonly ScopeStyle Function = new(Color.FromRgb(0x61, 0xAF, 0xEF));
    }

    /// <summary>Freeze brushes so the same color isn't re-allocated per token.</summary>
    private static class BrushCache
    {
        private static readonly Dictionary<Color, Brush> Cache = new();
        public static Brush Get(Color c)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(c, out var b))
                {
                    b = new SolidColorBrush(c);
                    b.Freeze();
                    Cache[c] = b;
                }
                return b;
            }
        }
    }
}