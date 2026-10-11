using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AetherCodex.Editor;

/// <summary>FlowDocument → Markdown. The inverse of <c>MarkdownRenderer</c>: reconstructs
/// markdown from the rendered document structure (headings, emphasis, inline code, fenced
/// code blocks, lists, blockquotes, links, hr). Faithful for common prose; best-effort for
/// exotic WYSIWYG formatting.</summary>
public static class MarkdownSerializer
{
    public static string ToMarkdown(FlowDocument doc)
    {
        var lines = new List<string>();
        foreach (var block in doc.Blocks)
            AppendBlock(lines, block, 0);

        // Collapse runs of blank lines to at most one (loose list spacing).
        var sb = new StringBuilder();
        var blanks = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                blanks++;
                if (blanks <= 2) sb.AppendLine();
            }
            else
            {
                sb.AppendLine(line);
                blanks = 0;
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendBlock(List<string> lines, Block block, int depth)
    {
        switch (block)
        {
            case Paragraph p: AppendParagraph(lines, p, depth); break;
            case List l: AppendList(lines, l, depth); break;
            case Section s:
                foreach (var b in s.Blocks) AppendBlock(lines, b, depth);
                break;
            case Table t: AppendTable(lines, t); break;
            case BlockUIContainer: break; // images / embedded UI are not serialized
        }
    }

    private static void AppendParagraph(List<string> lines, Paragraph p, int depth)
    {
        // Tagged blocks (MarkdownRenderer round-trip) take precedence: the tag
        // carries the exact structure, so no heuristics are needed.
        var tag = p.Tag as string;
        if (tag == "md-hr") { lines.Add("---"); lines.Add(""); return; }
        if (tag == "md-quote") { lines.Add("> " + SerializeInlines(p.Inlines)); lines.Add(""); return; }
        if (tag == "md-list")
        {
            // The renderer prefixes the item with a bold "• " or "N. " run; recover
            // the marker so ordered lists survive the round-trip.
            var marker = p.Inlines.FirstInline is Run first ? first.Text.Trim() : "";
            var prefix = marker.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(marker, @"^\d+\.$") ? marker + " " : "- ";
            lines.Add(prefix + SerializeInlinesFrom(p.Inlines, 1));
            lines.Add("");
            return;
        }
        if (tag == "md-table")
        {
            var cells = new List<string>();
            foreach (var inline in FlattenInlines(p.Inlines))
            {
                if (inline is not Run r) continue;
                var t = r.Text ?? "";
                if (t == " │ ") continue;
                if (t.Length > 0) cells.Add(t.Trim());
            }
            lines.Add("| " + string.Join(" | ", cells) + " |");
            lines.Add("");
            return;
        }
        if (tag is string s && s.StartsWith("md-heading-"))
        {
            var lvl = int.TryParse(s["md-heading-".Length..], out var l) ? Math.Clamp(l, 1, 6) : 1;
            lines.Add(new string('#', lvl) + " " + ExtractText(p.Inlines).Trim());
            lines.Add("");
            return;
        }
        if (tag is string cs && cs.StartsWith("code:"))
        {
            lines.Add("```" + cs["code:".Length..]);
            foreach (var codeLine in ExtractText(p.Inlines).Split('\n'))
                lines.Add(codeLine.TrimEnd());
            lines.Add("```");
            lines.Add("");
            return;
        }

        // Horizontal rule: empty paragraph with a bottom border.
        if (p.Inlines.Count == 0)
        {
            if (p.BorderThickness.Bottom > 0) lines.Add("---");
            lines.Add("");
            return;
        }

        // Fenced code block: monospace paragraph with an opaque background.
        if (IsMono(p.FontFamily) && p.Background != null)
        {
            lines.Add("```");
            foreach (var codeLine in ExtractText(p.Inlines).Split('\n'))
                lines.Add(codeLine.TrimEnd());
            lines.Add("```");
            lines.Add("");
            return;
        }

        // Blockquote: left border introduced by the renderer.
        if (p.BorderThickness.Left > 0)
        {
            lines.Add("> " + SerializeInlines(p.Inlines));
            lines.Add("");
            return;
        }

        // Renderer-style list item: indented paragraph beginning with a bold bullet run.
        var bullet = TryRendererListPrefix(p);
        if (bullet is not null)
        {
            lines.Add(bullet + " " + SerializeInlinesFrom(p.Inlines, 1));
            lines.Add("");
            return;
        }

        // Heading: bold lead run with a large font size.
        if (TryHeadingLevel(p, out var level))
        {
            lines.Add(new string('#', level) + " " + ExtractText(p.Inlines).Trim());
            lines.Add("");
            return;
        }

        lines.Add(SerializeInlines(p.Inlines));
        lines.Add("");
    }

    private static void AppendList(List<string> lines, List list, int depth)
    {
        var ordered = list.MarkerStyle == TextMarkerStyle.Decimal;
        var indent = new string(' ', depth * 2);
        foreach (var item in list.ListItems)
        {
            foreach (var block in item.Blocks)
            {
                if (block is Paragraph p)
                    lines.Add(indent + (ordered ? "1. " : "- ") + SerializeInlines(p.Inlines));
                else
                    AppendBlock(lines, block, depth + 1);
            }
        }
        lines.Add("");
    }

    private static void AppendTable(List<string> lines, Table t)
    {
        foreach (var group in t.RowGroups)
        foreach (var row in group.Rows)
        {
            var cells = row.Cells.Select(c => ExtractBlocksText(c.Blocks).Trim()).ToArray();
            lines.Add("| " + string.Join(" | ", cells) + " |");
        }
        lines.Add("");
    }

    // ── Inline serialization ────────────────────────────────────────────────────

    private static string SerializeInlines(InlineCollection inlines) => SerializeInlinesFrom(inlines, 0);

    private static string SerializeInlinesFrom(InlineCollection inlines, int start)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines.Skip(start)) SerializeInline(sb, inline);
        return sb.ToString();
    }

    private static IEnumerable<Inline> FlattenInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Span span)
                foreach (var child in FlattenInlines(span.Inlines)) yield return child;
            else
                yield return inline;
        }
    }

    private static void SerializeInline(StringBuilder sb, Inline inline)
    {
        switch (inline)
        {
            case Run run: SerializeRun(sb, run); break;
            case Hyperlink link:
                sb.Append('[').Append(ExtractText(link.Inlines)).Append("](")
                  .Append(link.NavigateUri?.ToString() ?? "").Append(')');
                break;
            case Span span:
                foreach (var child in span.Inlines) SerializeInline(sb, child);
                break;
            case LineBreak: sb.Append('\n'); break;
        }
    }

    private static void SerializeRun(StringBuilder sb, Run run)
    {
        var text = run.Text;
        if (text.Length == 0) return;

        if (IsMono(run.FontFamily))
        {
            sb.Append('`').Append(text.Trim('`')).Append('`');
            return;
        }

        var bold = run.FontWeight == FontWeights.Bold;
        var italic = run.FontStyle == FontStyles.Italic;
        if (bold && italic) sb.Append("***").Append(text).Append("***");
        else if (bold) sb.Append("**").Append(text).Append("**");
        else if (italic) sb.Append('*').Append(text).Append('*');
        else sb.Append(text);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string ExtractText(InlineCollection inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run r: sb.Append(r.Text); break;
                case Hyperlink h: sb.Append(ExtractText(h.Inlines)); break;
                case Span s: sb.Append(ExtractText(s.Inlines)); break;
                case LineBreak: sb.Append('\n'); break;
            }
        }
        return sb.ToString();
    }

    private static string ExtractBlocksText(BlockCollection blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
            if (block is Paragraph p) sb.Append(ExtractText(p.Inlines));
        return sb.ToString();
    }

    private static string? TryRendererListPrefix(Paragraph p)
    {
        if (p.Margin.Left < 20) return null;
        if (p.Inlines.FirstInline is not Run first) return null;
        if (first.FontWeight != FontWeights.Bold) return null;
        var t = first.Text;
        if (t == "• ") return "-";
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+\. $")) return t.Trim();
        return null;
    }

    private static bool TryHeadingLevel(Paragraph p, out int level)
    {
        level = 0;
        if (p.Inlines.FirstInline is not Run first) return false;
        if (first.FontWeight != FontWeights.Bold) return false;
        var size = first.FontSize;
        if (size < 14.5) return false;
        // MarkdownRenderer sizes headings as base * (2.2 - level * 0.18), base = 13.
        level = (int)Math.Round((26.26 - size) / 2.34) + 1;
        level = Math.Clamp(level, 1, 6);
        return true;
    }

    private static bool IsMono(FontFamily? family)
    {
        var source = family?.Source ?? "";
        return source.Contains("Cascadia", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Mono", StringComparison.OrdinalIgnoreCase);
    }
}