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
    private HashSet<int> _markdownFenceLines = new();

    public RegexHighlighter(SyntaxTheme theme, LanguageDefinition language)
    {
        _theme = theme;
        _language = language;
    }

    public void Invalidate()
    {
        _cachedVersion = null;
        _blockCommentLines.Clear();
        _markdownFenceLines.Clear();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var doc = CurrentContext.Document;
        if (!ReferenceEquals(doc.Version, _cachedVersion))
        {
            _cachedVersion = doc.Version;
            _blockCommentLines.Clear();
            _markdownFenceLines.Clear();
            RecomputeBlockComments(doc);
            if (_language.Key == "markdown")
                RecomputeMarkdownFences(doc);
        }

        var text = doc.GetText(line);
        if (text.Length == 0) return;

        if (_language.Key == "markdown")
        {
            ColorizeMarkdownLine(line, text);
            return;
        }

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
                    ApplyCommentMarkup(line, text, from, len);
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
                ApplyCommentMarkup(line, text, m.Index, m.Length);
            }
        }

        // 3. Strings.
        foreach (Match m in StringRegex.Matches(text))
        {
            if (IsExcluded(m.Index, m.Length, excluded)) continue;
            Colorize(line, m.Index, m.Length, _theme.Resolve("string", Fallback.String));
            excluded.Add((m.Index, m.Length));
        }

        // 3.5 Markup: tag names + attribute/class/id names (HTML/XML, HAML %tag / #id / .class).
        if (_language.TagRegex is { } tagRx)
            ApplyRegex(line, text, excluded, tagRx, "entity.name.tag", Fallback.Tag);
        if (_language.AttributeRegex is { } attrRx)
            ApplyRegex(line, text, excluded, attrRx, "entity.other.attribute-name", Fallback.Attribute);

        // 4. Structural tokens (numbers, keywords, types, constructors, builtins, calls, application).
        ApplyRegex(line, text, excluded, _language.NumberRegex, "number", Fallback.Number);
        ApplyRegex(line, text, excluded, _language.ConstantRegex, "constant.language", Fallback.Constant);
        ApplyRegex(line, text, excluded, _language.KeywordRegex, "keyword", Fallback.Keyword);
        ApplyRegex(line, text, excluded, _language.TypeRegex, "type", Fallback.Type);
        if (_language.ConstructorRegex is { } ctorRx)
            ApplyRegex(line, text, excluded, ctorRx, "type", Fallback.Type);
        ApplyRegex(line, text, excluded, _language.BuiltinRegex, "support.function", Fallback.Function);
        if (_language.FunctionDefinitionRegex is { } defRx)
            ApplyRegex(line, text, excluded, defRx, "entity.name.function", Fallback.Function);
        ApplyRegex(line, text, excluded, _language.FunctionCallRegex, "entity.name.function", Fallback.Function);
        if (_language.FunctionApplicationRegex is { } appRx)
            ApplyRegex(line, text, excluded, appRx, "entity.name.function", Fallback.Function);
    }

    private void ApplyCommentMarkup(DocumentLine line, string text, int from, int length)
    {
        if (length <= 0) return;
        foreach (var (start, len, kind) in CommentMarkup.Tokenize(text.Substring(from, length)))
            Colorize(line, from + start, len, ResolveCommentMarkup(kind));
    }

    private ScopeStyle ResolveCommentMarkup(CommentMarkupKind kind)
    {
        var comment = _theme.Resolve("comment", Fallback.Comment);
        return kind switch
        {
            CommentMarkupKind.Todo => _theme.StyleFor("comment.keyword", CommentMarkup.TodoStyle(_theme.IsDark)),
            CommentMarkupKind.Code => _theme.Resolve("markup.code.inline",
                new ScopeStyle(_theme.Resolve("string", Fallback.String).Color)),
            CommentMarkupKind.Bold => _theme.Resolve("markup.bold", new ScopeStyle(comment.Color, Bold: true)),
            CommentMarkupKind.Italic => _theme.Resolve("markup.italic", new ScopeStyle(comment.Color, Italic: true)),
            _ => comment,
        };
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
            excluded.Add((m.Index, m.Length));
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
                if (style.Strikethrough)
                    element.TextRunProperties.SetTextDecorations(System.Windows.TextDecorations.Strikethrough);
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

    // ── Markdown ──────────────────────────────────────────────────────────────
    private static readonly Regex MarkdownFenceRx = new(@"^\s*(```|~~~)", RegexOptions.Compiled);
    private static readonly Regex MarkdownHeadingRx = new(@"^#{1,6}\s", RegexOptions.Compiled);
    private static readonly Regex MarkdownThematicBreakRx = new(@"^\s*([-*_])\s*\1\s*\1[\s\1]*$", RegexOptions.Compiled);
    private static readonly Regex MarkdownBlockquoteRx = new(@"^\s*>\s?", RegexOptions.Compiled);
    private static readonly Regex MarkdownListRx = new(@"^\s*([-*+]|\d+[.)])\s+", RegexOptions.Compiled);
    private static readonly Regex MarkdownInlineCodeRx = new(@"`[^`\n]+`", RegexOptions.Compiled);
    private static readonly Regex MarkdownBoldRx = new(@"\*\*[^*\n]+\*\*|__[^_\n]+__", RegexOptions.Compiled);
    private static readonly Regex MarkdownItalicRx = new(@"(?<!\*)\*[^*\n]+\*(?!\*)|(?<!_)_[^_\n]+_(?!_)", RegexOptions.Compiled);
    private static readonly Regex MarkdownStrikeRx = new(@"~~[^~\n]+~~", RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkRx = new(@"!?(\[[^\]\n]*\])\(([^)\s]+)\)", RegexOptions.Compiled);

    private void RecomputeMarkdownFences(TextDocument doc)
    {
        _markdownFenceLines.Clear();
        bool inFence = false;
        int lineNumber = 1;
        foreach (DocumentLine line in doc.Lines)
        {
            var text = doc.GetText(line);
            bool fence = MarkdownFenceRx.IsMatch(text);
            if (inFence)
            {
                _markdownFenceLines.Add(lineNumber);
                if (fence) inFence = false;
            }
            else if (fence)
            {
                inFence = true;
            }
            lineNumber++;
        }
    }

    private void ColorizeMarkdownLine(DocumentLine line, string text)
    {
        // Fenced code block content (monospace block, highlighted as raw markup).
        if (_markdownFenceLines.Contains(line.LineNumber))
        {
            Colorize(line, 0, text.Length, _theme.Resolve("markup.raw", Fallback.MarkupRaw));
            return;
        }
        if (MarkdownFenceRx.IsMatch(text))
        {
            Colorize(line, 0, text.Length, _theme.Resolve("markup.raw.delimiter", Fallback.Comment));
            return;
        }
        // Headings and thematic breaks colour the whole line.
        if (MarkdownHeadingRx.IsMatch(text))
        {
            Colorize(line, 0, text.Length, _theme.Resolve("markup.heading", Fallback.MarkupHeading));
            return;
        }
        if (MarkdownThematicBreakRx.IsMatch(text))
        {
            Colorize(line, 0, text.Length, _theme.Resolve("comment", Fallback.Comment));
            return;
        }

        var excluded = new List<(int Start, int Length)>();

        var quote = MarkdownBlockquoteRx.Match(text);
        if (quote.Success)
        {
            Colorize(line, quote.Index, quote.Length, _theme.Resolve("markup.quote", Fallback.Comment));
            excluded.Add((quote.Index, quote.Length));
        }

        var list = MarkdownListRx.Match(text);
        if (list.Success)
        {
            Colorize(line, list.Index, list.Length, _theme.Resolve("markup.list", Fallback.Keyword));
            excluded.Add((list.Index, list.Length));
        }

        foreach (Match m in MarkdownInlineCodeRx.Matches(text))
        {
            if (IsExcluded(m.Index, m.Length, excluded)) continue;
            Colorize(line, m.Index, m.Length, _theme.Resolve("markup.raw", Fallback.MarkupRaw));
            excluded.Add((m.Index, m.Length));
        }
        foreach (Match m in MarkdownBoldRx.Matches(text))
        {
            if (IsExcluded(m.Index, m.Length, excluded)) continue;
            Colorize(line, m.Index, m.Length, _theme.Resolve("markup.bold", Fallback.MarkupBold));
            excluded.Add((m.Index, m.Length));
        }
        foreach (Match m in MarkdownItalicRx.Matches(text))
        {
            if (IsExcluded(m.Index, m.Length, excluded)) continue;
            Colorize(line, m.Index, m.Length, _theme.Resolve("markup.italic", Fallback.MarkupItalic));
            excluded.Add((m.Index, m.Length));
        }
        foreach (Match m in MarkdownStrikeRx.Matches(text))
        {
            if (IsExcluded(m.Index, m.Length, excluded)) continue;
            Colorize(line, m.Index, m.Length, _theme.Resolve("markup.strikethrough", Fallback.MarkupStrike));
            excluded.Add((m.Index, m.Length));
        }
        foreach (Match m in MarkdownLinkRx.Matches(text))
        {
            var label = m.Groups[1];
            var url = m.Groups[2];
            if (!IsExcluded(label.Index, label.Length, excluded))
            {
                Colorize(line, label.Index, label.Length, _theme.Resolve("markup.link", Fallback.MarkupLink));
                excluded.Add((label.Index, label.Length));
            }
            if (!IsExcluded(url.Index, url.Length, excluded))
            {
                Colorize(line, url.Index, url.Length, _theme.Resolve("string.other.link", Fallback.String));
                excluded.Add((url.Index, url.Length));
            }
        }
    }

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
        public static readonly ScopeStyle Tag = new(Color.FromRgb(0xE0, 0x6C, 0x75));
        public static readonly ScopeStyle Attribute = new(Color.FromRgb(0xD1, 0x9A, 0x66));
        public static readonly ScopeStyle MarkupHeading = new(Color.FromRgb(0xB4, 0x8C, 0xFF), Bold: true);
        public static readonly ScopeStyle MarkupBold = new(Color.FromRgb(0xE6, 0xE6, 0xE6), Bold: true);
        public static readonly ScopeStyle MarkupItalic = new(Color.FromRgb(0xE6, 0xE6, 0xE6), Italic: true);
        public static readonly ScopeStyle MarkupStrike = new(Color.FromRgb(0x8E, 0x94, 0xA0), Strikethrough: true);
        public static readonly ScopeStyle MarkupLink = new(Color.FromRgb(0x61, 0xAF, 0xEF));
        public static readonly ScopeStyle MarkupRaw = new(Color.FromRgb(0x8F, 0xB5, 0x73));
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