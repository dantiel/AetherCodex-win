using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AetherCodex.Panels;

/// <summary>Markdown → WPF FlowDocument. A competent subset: headings, emphasis,
/// inline code, fenced code blocks, lists, blockquotes, links, tables, hr.</summary>
public static class MarkdownRenderer
{
    public static FlowDocument Render(string markdown, Color foreground, Color codeBackground, double baseFontSize = 13)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = baseFontSize,
            Foreground = new SolidColorBrush(foreground),
            PagePadding = new Thickness(4, 4, 8, 4),
            LineHeight = baseFontSize * 1.5,
        };

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var para = new Paragraph();
        var inCode = false;
        var codeLang = "";
        var codeBuilder = new System.Text.StringBuilder();

        void FlushCode()
        {
            if (codeBuilder.Length == 0) return;
            var block = new Paragraph
            {
                Background = new SolidColorBrush(codeBackground),
                Margin = new Thickness(0, 4, 0, 8),
                Padding = new Thickness(10, 8, 10, 8),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = baseFontSize - 1,
            };
            block.Inlines.Add(new Run(codeBuilder.ToString().TrimEnd('\n')));
            doc.Blocks.Add(block);
            codeBuilder.Clear();
        }

        void FlushParagraph()
        {
            if (para.Inlines.Count == 0) return;
            doc.Blocks.Add(para);
            para = new Paragraph();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```"))
            {
                if (inCode) { FlushCode(); inCode = false; }
                else { FlushParagraph(); inCode = true; codeLang = line.TrimStart()[3..].Trim(); }
                continue;
            }
            if (inCode) { codeBuilder.AppendLine(raw); continue; }

            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { FlushParagraph(); continue; }

            // Headings
            if (TryHeading(trimmed, out var level, out var headingText))
            {
                FlushParagraph();
                var h = new Paragraph { Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 4) };
                var size = baseFontSize * (2.2 - level * 0.18);
                h.Inlines.Add(new Run(headingText)
                {
                    FontSize = size,
                    FontWeight = FontWeights.Bold,
                });
                doc.Blocks.Add(h);
                continue;
            }

            // Horizontal rule
            if (trimmed == "---" || trimmed == "***" || trimmed == "___")
            {
                FlushParagraph();
                doc.Blocks.Add(new Paragraph { BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(foreground) { Opacity = 0.25 }, Margin = new Thickness(0, 8, 0, 8) });
                continue;
            }

            // Blockquote
            if (trimmed.StartsWith("> "))
            {
                FlushParagraph();
                var q = new Paragraph
                {
                    Margin = new Thickness(12, 2, 0, 6),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    BorderBrush = new SolidColorBrush(foreground) { Opacity = 0.5 },
                    Padding = new Thickness(10, 0, 0, 0),
                };
                AddInlineMarkdown(q, trimmed[2..], foreground);
                doc.Blocks.Add(q);
                continue;
            }

            // Lists
            if (TryList(trimmed, out var bullet, out var itemText))
            {
                FlushParagraph();
                var listItem = new Paragraph { Margin = new Thickness(24, 1, 0, 1) };
                listItem.Inlines.Add(new Run(bullet + " ") { FontWeight = FontWeights.Bold });
                AddInlineMarkdown(listItem, itemText, foreground);
                doc.Blocks.Add(listItem);
                continue;
            }

            // Table row
            if (trimmed.StartsWith('|') && trimmed.EndsWith('|'))
            {
                FlushParagraph();
                AddTable(doc, trimmed, foreground, codeBackground, baseFontSize);
                continue;
            }

            // Normal paragraph
            if (para.Inlines.Count > 0) para.Inlines.Add(new LineBreak());
            AddInlineMarkdown(para, line, foreground);
        }

        if (inCode) FlushCode();
        FlushParagraph();
        return doc;
    }

    private static bool TryHeading(string trimmed, out int level, out string text)
    {
        level = 0; text = "";
        int i = 0;
        while (i < trimmed.Length && trimmed[i] == '#') i++;
        if (i > 0 && i <= 6 && i < trimmed.Length && trimmed[i] == ' ')
        {
            level = i;
            text = trimmed[i..].Trim();
            return true;
        }
        return false;
    }

    private static bool TryList(string trimmed, out string bullet, out string text)
    {
        bullet = ""; text = "";
        if (trimmed.Length > 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
        {
            bullet = "•"; text = trimmed[2..]; return true;
        }
        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\d+[.)]\s+(.*)$");
        if (match.Success)
        {
            bullet = match.Value[..match.Value.IndexOf(' ')].TrimEnd('.', ')') + ".";
            text = match.Groups[1].Value;
            return true;
        }
        return false;
    }

    private static void AddInlineMarkdown(Paragraph para, string text, Color foreground)
    {
        AddInlineMarkdown(para.Inlines, text, foreground);
    }

    private static void AddInlineMarkdown(InlineCollection inlines, string text, Color foreground)
    {
        var span = new Span();
        var tokenizer = new InlineTokenizer(text);
        while (tokenizer.Next(out var kind, out var content))
        {
            switch (kind)
            {
                case InlineKind.Bold: span.Inlines.Add(new Run(content) { FontWeight = FontWeights.Bold }); break;
                case InlineKind.Italic: span.Inlines.Add(new Run(content) { FontStyle = FontStyles.Italic }); break;
                case InlineKind.Code: span.Inlines.Add(CodeRun(content, foreground)); break;
                case InlineKind.Link: span.Inlines.Add(new Hyperlink(new Run(content)) { NavigateUri = new Uri(content, UriKind.RelativeOrAbsolute) }); break;
                case InlineKind.Text: span.Inlines.Add(new Run(content)); break;
            }
        }
        inlines.Add(span);
    }

    private static Run CodeRun(string content, Color foreground)
    {
        return new Run(content)
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Background = new SolidColorBrush(foreground) { Opacity = 0.10 },
        };
    }

    private static void AddTable(FlowDocument doc, string line, Color fg, Color codeBg, double size)
    {
        var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = size - 1 };
        foreach (var c in cells)
        {
            para.Inlines.Add(new Run(" " + c + " "));
            if (c != cells[^1]) para.Inlines.Add(new Run(" │ ") { Foreground = new SolidColorBrush(fg) { Opacity = 0.4 } });
        }
        doc.Blocks.Add(para);
    }

    private enum InlineKind { Text, Bold, Italic, Code, Link }

    private ref struct InlineTokenizer
    {
        private readonly string _text;
        private int _pos;

        public InlineTokenizer(string text) { _text = text; _pos = 0; }

        public bool Next(out InlineKind kind, out string content)
        {
            kind = InlineKind.Text; content = "";
            if (_pos >= _text.Length) return false;

            if (_text[_pos] == '`')
            {
                int end = _text.IndexOf('`', _pos + 1);
                if (end > _pos) { kind = InlineKind.Code; content = _text[(_pos + 1)..end]; _pos = end + 1; return true; }
            }
            if (_pos + 1 < _text.Length && _text[_pos] == '*' && _text[_pos + 1] == '*')
            {
                int end = _text.IndexOf("**", _pos + 2, StringComparison.Ordinal);
                if (end > _pos) { kind = InlineKind.Bold; content = _text[(_pos + 2)..end]; _pos = end + 2; return true; }
            }
            if (_text[_pos] == '*' || _text[_pos] == '_')
            {
                char delim = _text[_pos];
                int end = _text.IndexOf(delim, _pos + 1);
                if (end > _pos) { kind = InlineKind.Italic; content = _text[(_pos + 1)..end]; _pos = end + 1; return true; }
            }
            if (_text[_pos] == '[')
            {
                int close = _text.IndexOf("](", _pos + 1, StringComparison.Ordinal);
                if (close > _pos)
                {
                    int end = _text.IndexOf(')', close + 2);
                    if (end > close)
                    {
                        kind = InlineKind.Link;
                        content = _text[(close + 2)..end];
                        _pos = end + 1;
                        return true;
                    }
                }
            }

            // Accumulate plain text up to the next special char.
            int start = _pos;
            while (_pos < _text.Length && !IsSpecial(_text, _pos)) _pos++;
            content = _text[start.._pos];
            return content.Length > 0;
        }

        private static bool IsSpecial(string t, int i)
        {
            char c = t[i];
            if (c == '`' || c == '*' || c == '_' || c == '[') return true;
            return false;
        }
    }
}