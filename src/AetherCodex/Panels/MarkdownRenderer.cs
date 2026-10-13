using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using AetherCodex.Editor;
using AetherCodex.Util;

namespace AetherCodex.Panels;

/// <summary>
/// Markdown → WPF FlowDocument. A theme-aware subset: headings, emphasis,
/// strikethrough, inline code, syntax-highlighted fenced code blocks, lists
/// (bullet / ordered / task), blockquotes, links, real tables, hr, and
/// &lt;file&gt; references rendered as clickable links. Every block resolves its
/// colour/style from the active theme's <c>markup.*</c> scopes — the same
/// vocabulary AetherOracle themes declare — so the Pythia chamber follows the
/// chosen theme rather than a hard-coded skin.
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>Target of a &lt;file&gt; reference.</summary>
    public sealed record FileLink(string Path, int Line = 0, int Column = 0, string? Label = null);

    public static event Action<string, int, int>? FileLinkRequested;

    public static void WireFileLinks(RichTextBox rtb)
        => rtb.AddHandler(Hyperlink.RequestNavigateEvent,
            new RequestNavigateEventHandler(HandleFileLink), handledEventsToo: true);

    public static void WireFileLinks(FlowDocumentScrollViewer viewer)
        => viewer.AddHandler(Hyperlink.RequestNavigateEvent,
            new RequestNavigateEventHandler(HandleFileLink), handledEventsToo: true);

    private static void HandleFileLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.OriginalSource is not Hyperlink h) return;

        if (h.Tag is FileLink fl)
        {
            FileLinkRequested?.Invoke(fl.Path, fl.Line, fl.Column);
            return;
        }

        var uri = h.NavigateUri;
        if (uri is not null && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* non-fatal */ }
        }
    }

    /// <summary>Render with the user's configured prose size (<see cref="AppFonts.UiSize"/>).</summary>
    public static FlowDocument Render(string markdown, SyntaxTheme theme)
        => Render(markdown, theme, AppFonts.UiSize > 0 ? AppFonts.UiSize : 14.0);

    public static FlowDocument Render(string markdown, SyntaxTheme theme, double baseFontSize)
    {
        var proseFg = theme.PythiaBaseText;
        var mutedFg = theme.PythiaForeground;
        var codeBg = theme.PythiaCodeBackground;
        var codeFg = theme.PythiaCodeForeground;
        var mono = AppFonts.MonoFamily;
        var prose = AppFonts.UiFamily;

        var doc = new FlowDocument
        {
            FontFamily = prose,
            FontSize = baseFontSize,
            Foreground = new SolidColorBrush(proseFg),
            PagePadding = new Thickness(4, 6, 8, 10),
            LineHeight = baseFontSize * 1.5,
        };

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var para = new Paragraph();
        var inCode = false;
        var codeLang = "";
        var codeBuilder = new StringBuilder();
        List? currentList = null;
        var currentOrdered = false;
        bool? currentTask = false;
        var quoteLines = new List<string>();
        var tableRows = new List<string>();

        void FlushCode()
        {
            if (codeBuilder.Length == 0) { codeBuilder.Clear(); return; }
            var lang = Languages.FromKey(codeLang);
            var block = new Paragraph
            {
                Background = new SolidColorBrush(codeBg),
                BorderBrush = new SolidColorBrush(theme.PythiaAccent) { Opacity = 0.45 },
                BorderThickness = new Thickness(3, 0, 0, 0),
                Margin = new Thickness(0, 6, 0, 12),
                Padding = new Thickness(12, 10, 12, 10),
                FontFamily = mono,
                FontSize = baseFontSize - 1,
                LineHeight = baseFontSize * 1.35,
                Tag = "code:" + codeLang,
            };
            CodeColorizer.Append(block.Inlines, theme, lang,
                codeBuilder.ToString().TrimEnd('\n'), mono, baseFontSize - 1, codeFg);
            doc.Blocks.Add(block);
            codeBuilder.Clear();
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

        void FlushQuote()
        {
            if (quoteLines.Count == 0) return;
            var q = new Paragraph
            {
                Tag = "md-quote",
                Margin = new Thickness(4, 4, 0, 10),
                BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = new SolidColorBrush(theme.PythiaAccent) { Opacity = 0.5 },
                Padding = new Thickness(12, 2, 2, 2),
            };
            for (int i = 0; i < quoteLines.Count; i++)
            {
                if (i > 0) q.Inlines.Add(new LineBreak());
                AddInlineMarkdown(q.Inlines, quoteLines[i], theme, proseFg, mutedFg);
            }
            doc.Blocks.Add(q);
            quoteLines.Clear();
        }

        void FlushTable()
        {
            if (tableRows.Count == 0) return;
            AddTable(doc, tableRows, theme, baseFontSize);
            tableRows.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```"))
            {
                if (inCode) { FlushCode(); inCode = false; }
                else { FlushList(); FlushParagraph(); FlushQuote(); FlushTable(); inCode = true; codeLang = line.TrimStart()[3..].Trim(); }
                continue;
            }
            if (inCode) { codeBuilder.AppendLine(raw); continue; }

            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { FlushList(); FlushParagraph(); FlushQuote(); FlushTable(); continue; }

            if (TryHeading(trimmed, out var level, out var headingText))
            {
                FlushList(); FlushParagraph(); FlushQuote(); FlushTable();
                AddHeading(doc, theme, headingText, level, baseFontSize, proseFg);
                continue;
            }

            if (trimmed == "---" || trimmed == "***" || trimmed == "___")
            {
                FlushList(); FlushParagraph(); FlushQuote(); FlushTable();
                var rule = theme.Resolve("markup.rule", new ScopeStyle(mutedFg));
                doc.Blocks.Add(new Paragraph
                {
                    Tag = "md-hr",
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush = new SolidColorBrush(rule.Color) { Opacity = 0.35 },
                    Margin = new Thickness(0, 10, 0, 10),
                });
                continue;
            }

            if (trimmed.StartsWith("> "))
            {
                FlushList(); FlushParagraph(); FlushTable();
                quoteLines.Add(trimmed[2..]);
                continue;
            }

            if (trimmed.StartsWith('|') && trimmed.EndsWith('|'))
            {
                FlushList(); FlushParagraph(); FlushQuote();
                tableRows.Add(trimmed);
                continue;
            }

            if (TryList(trimmed, out var ordered, out var task, out var itemText))
            {
                FlushParagraph(); FlushQuote(); FlushTable();
                if (currentList is null || currentOrdered != ordered || currentTask != task)
                {
                    FlushList();
                    currentOrdered = ordered;
                    currentTask = task;
                    currentList = new List
                    {
                        MarkerStyle = task == true ? TextMarkerStyle.None
                            : ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(24, 3, 0, 10),
                    };
                }
                var item = new ListItem();
                var itemPara = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                if (task == true)
                {
                    var checkedStyle = theme.Resolve("markup.list.checked", new ScopeStyle(mutedFg, Bold: true));
                    itemPara.Inlines.Add(new Run(task.Value ? "✔ " : "◯ ")
                    {
                        FontFamily = mono,
                        Foreground = new SolidColorBrush(checkedStyle.Color),
                        FontWeight = checkedStyle.Bold ? FontWeights.Bold : FontWeights.Normal,
                    });
                }
                AddInlineMarkdown(itemPara.Inlines, itemText, theme, proseFg, mutedFg);
                item.Blocks.Add(itemPara);
                currentList.ListItems.Add(item);
                continue;
            }

            FlushList(); FlushQuote(); FlushTable();
            if (para.Inlines.Count > 0) para.Inlines.Add(new LineBreak());
            AddInlineMarkdown(para.Inlines, line, theme, proseFg, mutedFg);
        }

        if (inCode) FlushCode();
        FlushList();
        FlushQuote();
        FlushTable();
        FlushParagraph();
        return doc;
    }

    private static void AddHeading(FlowDocument doc, SyntaxTheme theme, string text, int level, double baseSize, Color proseFg)
    {
        var scope = theme.Resolve($"markup.heading.{level}", theme.Resolve("markup.heading", new ScopeStyle(proseFg, Bold: true)));
        double size;
        if (scope.FontSize is double m && m > 0 && m <= 4) size = baseSize * m;
        else
        {
            var ramp = new[] { 1.6, 1.45, 1.3, 1.18, 1.08, 1.0 };
            size = baseSize * ramp[Math.Clamp(level, 1, 6) - 1];
        }

        var h = new Paragraph
        {
            Tag = "md-heading-" + level,
            Margin = new Thickness(0, level <= 2 ? 14 : 8, 0, 4),
            KeepWithNext = true,
            LineHeight = size * 1.3,
        };
        h.Inlines.Add(new Run(text)
        {
            FontSize = size,
            FontWeight = scope.Bold ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = new SolidColorBrush(scope.Color),
        });
        doc.Blocks.Add(h);
    }

    private static void AddTable(FlowDocument doc, List<string> rows, SyntaxTheme theme, double baseSize)
    {
        var all = rows.Select(ParseRow).ToList();
        if (all.Count == 0) return;

        int cols = all.Max(r => r.Length);
        bool hasHeader = rows.Count >= 2 && IsSeparator(rows[1]);

        var headerStyle = theme.Resolve("markup.table.header",
            theme.Resolve("markup.table", new ScopeStyle(theme.PythiaBaseText, Bold: true)));
        var cellStyle = theme.Resolve("markup.table.td",
            theme.Resolve("markup.table", new ScopeStyle(theme.PythiaBaseText)));
        var border = new SolidColorBrush(theme.PythiaForeground) { Opacity = 0.16 };

        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 12) };
        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        int bodyStart = hasHeader ? 2 : 0;
        if (hasHeader)
            group.Rows.Add(MakeRow(Pad(all[0], cols), headerStyle, border, baseSize, header: true));
        for (int i = bodyStart; i < all.Count; i++)
            group.Rows.Add(MakeRow(Pad(all[i], cols), cellStyle, border, baseSize, header: false));

        doc.Blocks.Add(table);
    }

    private static TableRow MakeRow(string[] cells, ScopeStyle style, Brush border, double size, bool header)
    {
        var row = new TableRow();
        foreach (var c in cells)
        {
            var cell = new TableCell
            {
                BorderBrush = border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 5, 8, 5),
            };
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run(c)
            {
                FontSize = size - 1,
                FontWeight = header || style.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(style.Color),
            });
            cell.Blocks.Add(p);
            row.Cells.Add(cell);
        }
        return row;
    }

    private static string[] Pad(string[] cells, int cols)
    {
        if (cells.Length >= cols) return cells;
        var padded = new string[cols];
        for (int i = 0; i < cols; i++) padded[i] = i < cells.Length ? cells[i] : "";
        return padded;
    }

    private static string[] ParseRow(string line)
        => line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    private static bool IsSeparator(string line)
    {
        var cells = ParseRow(line);
        if (cells.Length == 0) return false;
        return cells.All(c => Regex.IsMatch(c, @"^:?-{3,}:?$"));
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

    private static bool TryList(string trimmed, out bool ordered, out bool? task, out string text)
    {
        ordered = false; task = null; text = "";
        if (trimmed.Length > 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
        {
            var rest = trimmed[2..];
            var m = Regex.Match(rest, @"^\[([ xX])\]\s+(.*)$");
            if (m.Success)
            {
                task = m.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase);
                text = m.Groups[2].Value;
                return true;
            }
            text = rest;
            return true;
        }
        var om = Regex.Match(trimmed, @"^\d+[.)]\s+(.*)$");
        if (om.Success)
        {
            ordered = true;
            text = om.Groups[1].Value;
            return true;
        }
        return false;
    }

    private static void AddInlineMarkdown(InlineCollection inlines, string text, SyntaxTheme theme, Color proseFg, Color mutedFg)
    {
        var span = new Span();
        var tokenizer = new InlineTokenizer(text);
        while (tokenizer.Next(out var kind, out var content))
        {
            switch (kind)
            {
                case InlineKind.Bold:
                {
                    var s = theme.Resolve("markup.bold", new ScopeStyle(proseFg, Bold: true));
                    span.Inlines.Add(new Run(content) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(s.Color) });
                    break;
                }
                case InlineKind.Italic:
                {
                    var s = theme.Resolve("markup.italic", new ScopeStyle(proseFg, Italic: true));
                    span.Inlines.Add(new Run(content) { FontStyle = FontStyles.Italic, Foreground = new SolidColorBrush(s.Color) });
                    break;
                }
                case InlineKind.Strike:
                {
                    var s = theme.Resolve("markup.strikethrough", new ScopeStyle(mutedFg, Strikethrough: true));
                    span.Inlines.Add(new Run(content) { TextDecorations = TextDecorations.Strikethrough, Foreground = new SolidColorBrush(s.Color) });
                    break;
                }
                case InlineKind.Code:
                {
                    var s = theme.Resolve("markup.code.inline", new ScopeStyle(proseFg));
                    span.Inlines.Add(CodeRun(content, s));
                    break;
                }
                case InlineKind.Link:
                {
                    var s = theme.Resolve("markup.link", new ScopeStyle(theme.PythiaAccent, Underline: true));
                    span.Inlines.Add(new Hyperlink(new Run(content))
                    {
                        NavigateUri = new Uri(content, UriKind.RelativeOrAbsolute),
                        Foreground = new SolidColorBrush(s.Color),
                        TextDecorations = s.Underline ? TextDecorations.Underline : null,
                    });
                    break;
                }
                case InlineKind.FileLink:
                    var fl = tokenizer.FileLink ?? new FileLink(content);
                    span.Inlines.Add(new Hyperlink(new Run(fl.Label ?? fl.Path))
                    {
                        NavigateUri = new Uri("aetherfile:///open"),
                        ToolTip = fl.Line > 0 ? $"{fl.Path}:{fl.Line}" : fl.Path,
                        Tag = fl,
                        Foreground = new SolidColorBrush(theme.PythiaAccent),
                        TextDecorations = null,
                    });
                    break;
                case InlineKind.Text: span.Inlines.Add(new Run(content)); break;
            }
        }
        inlines.Add(span);
    }

    private static Run CodeRun(string content, ScopeStyle style)
    {
        return new Run(content)
        {
            FontFamily = AppFonts.MonoFamily,
            Background = style.Background is Color bg
                ? new SolidColorBrush(bg)
                : new SolidColorBrush(style.Color) { Opacity = 0.10 },
            Foreground = new SolidColorBrush(style.Color),
        };
    }

    private enum InlineKind { Text, Bold, Italic, Strike, Code, Link, FileLink }

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
            if (_pos + 1 < _text.Length && _text[_pos] == '~' && _text[_pos + 1] == '~')
            {
                int end = _text.IndexOf("~~", _pos + 2, StringComparison.Ordinal);
                if (end > _pos) { kind = InlineKind.Strike; content = _text[(_pos + 2)..end]; _pos = end + 2; return true; }
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
                kind = InlineKind.Text;
                content = "<";
                _pos++;
                return true;
            }

            int start = _pos;
            while (_pos < _text.Length && !IsSpecial(_text, _pos)) _pos++;
            content = _text[start.._pos];
            return content.Length > 0;
        }

        private FileLink? TryParseFileTag()
        {
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
            return c is '`' or '*' or '_' or '[' or '<' or '~';
        }
    }
}