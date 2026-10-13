using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AetherCodex.Editor;

/// <summary>
/// Lightweight token colouriser for fenced code blocks rendered inside markdown.
/// Maps tokens to the active theme's TextMate scopes (the same vocabulary the
/// editor's <see cref="RegexHighlighter"/> uses) so code previews share the
/// theme identity. Multi-line block comments are carried across lines.
/// </summary>
public static class CodeColorizer
{
    private static readonly Regex NumberRx = new(
        @"\b(0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*\.?\d*(?:[eE][+-]?\d+)?)\b",
        RegexOptions.Compiled);

    public static void Append(
        InlineCollection inlines, SyntaxTheme theme, LanguageDefinition lang,
        string code, FontFamily mono, double fontSize, Color fallback, int wrapWidth = 110)
    {
        bool inBlock = false;
        var lines = code.Replace("\r\n", "\n").Split('\n');
        bool first = true;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (!first) inlines.Add(new LineBreak());
                first = false;
                continue;
            }

            var rest = line;
            while (true)
            {
                var chunk = rest.Length > wrapWidth ? rest[..wrapWidth] : rest;
                if (!first) inlines.Add(new LineBreak());
                first = false;
                Colorize(inlines, theme, lang, chunk, mono, fontSize, fallback, ref inBlock);
                if (rest.Length <= wrapWidth) break;
                rest = rest[wrapWidth..];
            }
        }
    }

    private static void Colorize(
        InlineCollection inlines, SyntaxTheme theme, LanguageDefinition lang,
        string text, FontFamily mono, double size, Color fallback, ref bool inBlock)
    {
        var tokens = new List<(int Start, int Length, string Scope)>();

        if (inBlock)
        {
            // The line begins inside a block comment; close it first.
            var (_, end) = lang.BlockComments[0];
            int e = text.IndexOf(end, StringComparison.Ordinal);
            if (e < 0)
            {
                Emit(inlines, text, theme, "comment", mono, size, fallback);
                return;
            }
            tokens.Add((0, e + end.Length, "comment"));
            inBlock = false;
            AddTokens(text[(e + end.Length)..], tokens, lang, ref inBlock, e + end.Length);
        }
        else
        {
            AddTokens(text, tokens, lang, ref inBlock, 0);
        }

        int pos = 0;
        foreach (var (start, len, scope) in tokens.OrderBy(t => t.Start))
        {
            if (start < pos) continue; // shadowed by an earlier (higher-priority) token
            if (start > pos) Emit(inlines, text[pos..start], theme, null, mono, size, fallback);
            Emit(inlines, text.Substring(start, len), theme, scope, mono, size, fallback);
            pos = start + len;
        }
        if (pos < text.Length) Emit(inlines, text[pos..], theme, null, mono, size, fallback);
    }

    private static void AddTokens(
        string text, List<(int, int, string)> tokens, LanguageDefinition lang, ref bool inBlock, int offset)
    {
        // Line comments (rest of the physical line).
        foreach (var lc in lang.LineComments)
        {
            int i = text.IndexOf(lc, StringComparison.Ordinal);
            if (i >= 0) { tokens.Add((offset + i, text.Length - i, "comment")); break; }
        }

        // Inline block comments.
        if (!inBlock && lang.BlockComments.Length > 0)
        {
            var (start, end) = lang.BlockComments[0];
            int i = text.IndexOf(start, StringComparison.Ordinal);
            if (i >= 0)
            {
                int e = text.IndexOf(end, i + start.Length, StringComparison.Ordinal);
                if (e >= 0) tokens.Add((offset + i, e + end.Length - i, "comment"));
                else { tokens.Add((offset + i, text.Length - i, "comment")); inBlock = true; }
            }
        }

        // Strings.
        foreach (var q in lang.StringQuotes)
        {
            int i = 0;
            while (i < text.Length)
            {
                int s = text.IndexOf(q, i);
                if (s < 0) break;
                int e = s + 1;
                while (e < text.Length && text[e] != q) { if (text[e] == '\\') e++; e++; }
                if (e < text.Length) e++;
                tokens.Add((offset + s, e - s, "string"));
                i = e;
            }
        }

        foreach (Match m in NumberRx.Matches(text)) tokens.Add((offset + m.Index, m.Length, "number"));
        AddRegexTokens(text, tokens, offset, lang.KeywordRegex, "keyword");
        AddRegexTokens(text, tokens, offset, lang.TypeRegex, "type");
        AddRegexTokens(text, tokens, offset, lang.ConstantRegex, "constant.language");
        AddRegexTokens(text, tokens, offset, lang.BuiltinRegex, "support.function");
        AddRegexTokens(text, tokens, offset, lang.FunctionCallRegex, "entity.name.function");
    }

    private static void AddRegexTokens(string text, List<(int, int, string)> tokens, int offset, Regex rx, string scope)
    {
        foreach (Match m in rx.Matches(text))
            if (m.Length > 0) tokens.Add((offset + m.Index, m.Length, scope));
    }

    private static void Emit(
        InlineCollection inlines, string text, SyntaxTheme theme, string? scope,
        FontFamily mono, double size, Color fallback)
    {
        if (text.Length == 0) return;
        var run = new Run(text) { FontFamily = mono, FontSize = size };

        if (scope is null)
        {
            run.Foreground = new SolidColorBrush(fallback);
        }
        else
        {
            var style = theme.Resolve(scope, new ScopeStyle(fallback));
            run.Foreground = new SolidColorBrush(style.Color);
            if (style.Bold) run.FontWeight = FontWeights.Bold;
            if (style.Italic) run.FontStyle = FontStyles.Italic;
            if (style.Underline) run.TextDecorations = TextDecorations.Underline;
            if (style.Strikethrough) run.TextDecorations = TextDecorations.Strikethrough;
            if (style.Background is Color bg) run.Background = new SolidColorBrush(bg);
        }
        inlines.Add(run);
    }
}
