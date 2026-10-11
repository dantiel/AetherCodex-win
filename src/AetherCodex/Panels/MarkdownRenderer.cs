using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace AetherCodex.Panels;

/// <summary>Markdown → WPF FlowDocument. A competent subset: headings, emphasis,
/// inline code, fenced code blocks, lists, blockquotes, links, tables, hr, and
/// &lt;file&gt; references rendered as clickable links.</summary>
public static class MarkdownRenderer
{
    /// <summary>Target of a &lt;file&gt; reference: a repo-relative path, an optional
    /// 1-based line/column to jump to, and the display label.</summary>
    public sealed record FileLink(string Path, int Line = 0, int Column = 0, string? Label = null);

    /// <summary>Raised when a rendered &lt;file&gt; link is clicked. The host window
    /// subscribes and opens the file. Surfaces opt in via <see cref="WireFileLinks"/>.</summary>
    public static event Action<string, int, int>? FileLinkRequested;

    /// <summary>Attach click routing for rendered &lt;file&gt; links to a markdown
    /// surface (Pythia cards, the markdown preview).</summary>
    public static void WireFileLinks(RichTextBox rtb)
        => rtb.AddHandler(Hyperlink.RequestNavigateEvent,
            new RequestNavigateEventHandler(HandleFileLink), handledEventsToo: true);

    public static void WireFileLinks(FlowDocumentScrollViewer viewer)
        => viewer.AddHandler(Hyperlink.RequestNavigateEvent,
            new RequestNavigateEventHandler(HandleFileLink), handledEventsToo: true);

    private static void HandleFileLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.OriginalSource is Hyperlink h && h.Tag is FileLink fl)
            FileLinkRequested?.Invoke(fl.Path, fl.Line, fl.Column);
    }

    public static FlowDocument Render(string markdown, Color foreground, Color codeBackground, Color linkColor, double baseFontSize = 13)
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
        var codeBuilder = new StringBuilder();
        List? currentList = null;
        var currentOrdered = false;

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
            block.Tag = "code:" + codeLang;
            // Wrap long code lines so a fenced block can never demand horizontal
            // space: the Pythia pane renders markdown without a scroll area, so
            // every block must reflow within the panel width.
            foreach (var rawLine in codeBuilder.ToString().TrimEnd('\n').Split('\n'))
            {
                if (block.Inlines.Count > 0) block.Inlines.Add(new LineBreak());
                AppendWrapped(block.Inlines, rawLine.TrimEnd(), 120);
            }
            doc.Blocks.Add(block);
            codeBuilder.Clear();
        }

        static void AppendWrapped(InlineCollection inlines, string line, int width)
        {
            var rest = line;
            while (rest.Length > width)
            {
                inlines.Add(new Run(rest[..width]));
                inlines.Add(new LineBreak());
                rest = rest[width..];
            }
            inlines.Add(new Run(rest));
        }

        void FlushParagraph()
        {
            if (para.Inlines.Count == 0) return;
            doc.Blocks.Add(para);
            para = new Paragraph();
        }

        void FlushList()
        {
            if (currentList is null) return;
            doc.Blocks.Add(currentList);
            currentList = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```"))
            {
                if (inCode) { FlushCode(); inCode = false; }
                else { FlushList(); FlushParagraph(); inCode = true; codeLang = line.TrimStart()[3..].Trim(); }
                continue;
            }
            if (inCode) { codeBuilder.AppendLine(raw); continue; }

            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { FlushList(); FlushParagraph(); continue; }

            // Headings
            if (TryHeading(trimmed, out var level, out var headingText))
            {
                FlushList(); FlushParagraph();
                var h = new Paragraph { Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 4) };
                var size = baseFontSize * (2.2 - level * 0.18);
                h.Inlines.Add(new Run(headingText)
                {
                    FontSize = size,
                    FontWeight = FontWeights.Bold,
                });
                h.Tag = "md-heading-" + level;
                doc.Blocks.Add(h);
                continue;
            }

            // Horizontal rule
            if (trimmed == "---" || trimmed == "***" || trimmed == "___")
            {
                FlushList(); FlushParagraph();
                doc.Blocks.Add(new Paragraph { Tag = "md-hr", BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(foreground) { Opacity = 0.25 }, Margin = new Thickness(0, 8, 0, 8) });
                continue;
            }

            // Blockquote
            if (trimmed.StartsWith("> "))
            {
                FlushList(); FlushParagraph();
                var q = new Paragraph
                {
                    Tag = "md-quote",
                    Margin = new Thickness(12, 2, 0, 6),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    BorderBrush = new SolidColorBrush(foreground) { Opacity = 0.5 },
                    Padding = new Thickness(10, 0, 0, 0),
                };
                AddInlineMarkdown(q, trimmed[2..], foreground, linkColor);
                doc.Blocks.Add(q);
                continue;
            }

            // Lists → native WPF List/ListItem so bullets/numbering match the RTF
            // importer's output (real markers, hanging indent, grouped items).
            if (TryList(trimmed, out var ordered, out var itemText))
            {
                FlushParagraph();
                if (currentList is null || currentOrdered != ordered)
                {
                    FlushList();
                    currentOrdered = ordered;
                    currentList = new List
                    {
                        MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(24, 2, 0, 8),
                    };
                }
                var item = new ListItem();
                var itemPara = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddInlineMarkdown(itemPara, itemText, foreground, linkColor);
                item.Blocks.Add(itemPara);
                currentList.ListItems.Add(item);
                continue;
            }

            // Table row
            if (trimmed.StartsWith('|') && trimmed.EndsWith('|'))
            {
                FlushList(); FlushParagraph();
                AddTable(doc, trimmed, foreground, codeBackground, baseFontSize);
                continue;
            }

            // Normal paragraph (ends any open list).
            FlushList();
            if (para.Inlines.Count > 0) para.Inlines.Add(new LineBreak());
            AddInlineMarkdown(para, line, foreground, linkColor);
        }

        if (inCode) FlushCode();
        FlushList();
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

    private static bool TryList(string trimmed, out bool ordered, out string text)
    {
        ordered = false; text = "";
        if (trimmed.Length > 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
        {
            text = trimmed[2..]; return true;
        }
        var match = Regex.Match(trimmed, @"^\d+[.)]\s+(.*)$");
        if (match.Success)
        {
            ordered = true;
            text = match.Groups[1].Value;
            return true;
        }
        return false;
    }

    private static void AddInlineMarkdown(Paragraph para, string text, Color foreground, Color linkColor)
    {
        AddInlineMarkdown(para.Inlines, text, foreground, linkColor);
    }

    private static void AddInlineMarkdown(InlineCollection inlines, string text, Color foreground, Color linkColor)
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
                case InlineKind.FileLink:
                    var fl = tokenizer.FileLink ?? new FileLink(content);
                    span.Inlines.Add(new Hyperlink(new Run(fl.Label ?? fl.Path))
                    {
                        NavigateUri = new Uri("aetherfile:///open"),
                        ToolTip = fl.Line > 0 ? $"{fl.Path}:{fl.Line}" : fl.Path,
                        Tag = fl,
                        Foreground = new SolidColorBrush(linkColor),
                        TextDecorations = null,
                    });
                    break;
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
        var para = new Paragraph { Tag = "md-table", Margin = new Thickness(0, 2, 0, 2), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = size - 1 };
        foreach (var c in cells)
        {
            para.Inlines.Add(new Run(" " + c + " "));
            if (c != cells[^1]) para.Inlines.Add(new Run(" │ ") { Foreground = new SolidColorBrush(fg) { Opacity = 0.4 } });
        }
        doc.Blocks.Add(para);
    }

    private enum InlineKind { Text, Bold, Italic, Code, Link, FileLink }

    private ref struct InlineTokenizer
    {
        private readonly string _text;
        private int _pos;

        public InlineTokenizer(string text) { _text = text; _pos = 0; }

        public FileLink? FileLink { get; private set; }

        public bool Next(out InlineKind kind, out string content)
        {
            kind = InlineKind.Text; content = ""; FileLink = null;
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
            if (_text[_pos] == '<')
            {
                var link = TryParseFileTag();
                if (link is not null)
                {
                    kind = InlineKind.FileLink;
                    FileLink = link;
                    return true;
                }
                // Not a <file> tag — emit the angle bracket as plain text so other
                // inline HTML-ish markup isn't silently swallowed.
                kind = InlineKind.Text;
                content = "<";
                _pos++;
                return true;
            }

            // Accumulate plain text up to the next special char.
            int start = _pos;
            while (_pos < _text.Length && !IsSpecial(_text, _pos)) _pos++;
            content = _text[start.._pos];
            return content.Length > 0;
        }

        private FileLink? TryParseFileTag()
        {
            // _text[_pos] == '<'
            int gt = _text.IndexOf('>', _pos);
            if (gt < 0) return null;
            var open = _text[(_pos + 1)..gt].Trim();
            if (!IsFileOpenTag(open)) return null;
            int close = _text.IndexOf("</file>", gt + 1, StringComparison.OrdinalIgnoreCase);
            if (close < 0) return null;

            var inner = _text[(gt + 1)..close];
            var label = inner.Trim();

            var path = Attr(open, "path") ?? "";
            int.TryParse(Attr(open, "line"), out var line);
            int.TryParse(Attr(open, "column"), out var column);

            if (string.IsNullOrWhiteSpace(path)) path = label;
            if (string.IsNullOrWhiteSpace(label)) label = path;

            _pos = close + "</file>".Length;
            return new FileLink(path, line, column, label);
        }

        private static bool IsFileOpenTag(string open)
            => open.Equals("file", StringComparison.OrdinalIgnoreCase)
            || (open.Length > 4 && open.StartsWith("file", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(open[4]));

        private static string? Attr(string openTag, string name)
        {
            var m = Regex.Match(openTag, $@"\b{name}\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static bool IsSpecial(string t, int i)
        {
            char c = t[i];
            if (c == '`' || c == '*' || c == '_' || c == '[' || c == '<') return true;
            return false;
        }
    }
}
