using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace AetherCodex.Editor;

/// <summary>
/// OpenDocument Text (.odt) read/write against the zip package using only the BCL.
/// This is the native AetherOffice / LibreOffice exchange format. Paragraphs,
/// headings, runs, character formatting, lists and tables are handled; the writer
/// emits automatic styles so formatting survives the round-trip. Headings
/// currently round-trip as styled paragraphs (outline level is not re-emitted).
/// </summary>
internal static class OdtFormat
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text   = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Style  = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo     = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Table  = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Xlink  = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace ManifestNs = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    private const string MimeType = "application/vnd.oasis.opendocument.text";

    // ── Read ───────────────────────────────────────────────────────────────────

    public static FlowDocument Load(string path)
    {
        var doc = new FlowDocument();
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.xml");
        if (entry is null) return doc;

        XDocument xdoc;
        using (var s = entry.Open()) xdoc = XDocument.Load(s);

        var styles = LoadStyles(zip, xdoc);
        var text = xdoc.Descendants(Office + "text").FirstOrDefault();
        if (text is null) return doc;

        foreach (var el in text.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "p": doc.Blocks.Add(BuildParagraph(el, styles, new OdtStyle())); break;
                case "h": doc.Blocks.Add(BuildHeading(el, styles)); break;
                case "list": doc.Blocks.Add(BuildList(el, styles)); break;
                case "table": doc.Blocks.Add(BuildTable(el, styles)); break;
            }
        }
        return doc;
    }

    private static Paragraph BuildParagraph(XElement p, Dictionary<string, OdtStyle> styles, OdtStyle inherited)
    {
        var para = new Paragraph();
        var st = ResolveStyle(p, styles, inherited);
        if (st.Align is { } a) para.TextAlignment = a;
        AppendInlines(p, para.Inlines, styles, st);
        return para;
    }

    private static Paragraph BuildHeading(XElement h, Dictionary<string, OdtStyle> styles)
    {
        var para = BuildParagraph(h, styles, new OdtStyle());
        var level = h.Attribute(Text + "outline-level")?.Value ?? "1";
        int lvl = int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
        double size = lvl switch { 1 => 24, 2 => 20, 3 => 17, 4 => 14, _ => 13 };
        foreach (var run in DocumentIO.RunsInBlock(para))
        {
            run.FontSize = size;
            run.FontWeight = FontWeights.Bold;
        }
        return para;
    }

    private static List BuildList(XElement list, Dictionary<string, OdtStyle> styles)
    {
        var l = new List { MarkerStyle = IsDecimalList(list, styles) ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc };
        foreach (var li in list.Elements(Text + "list-item"))
        {
            var item = new ListItem();
            foreach (var p in li.Elements(Text + "p"))
                item.Blocks.Add(BuildParagraph(p, styles, new OdtStyle()));
            foreach (var nested in li.Elements(Text + "list"))
                item.Blocks.Add(BuildList(nested, styles));
            l.ListItems.Add(item);
        }
        return l;
    }

    private static Table BuildTable(XElement tbl, Dictionary<string, OdtStyle> styles)
    {
        var table = new Table();
        var rg = new TableRowGroup();
        foreach (var rowEl in tbl.Elements(Table + "table-row"))
        {
            var row = new TableRow();
            foreach (var cellEl in rowEl.Elements(Table + "table-cell"))
            {
                var cell = new TableCell();
                foreach (var p in cellEl.Elements(Text + "p"))
                    cell.Blocks.Add(BuildParagraph(p, styles, new OdtStyle()));
                if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph());
                row.Cells.Add(cell);
            }
            rg.Rows.Add(row);
        }
        table.RowGroups.Add(rg);
        return table;
    }

    private static bool IsDecimalList(XElement list, Dictionary<string, OdtStyle> styles)
    {
        var name = list.Attribute(Text + "list-style-name")?.Value;
        if (name is null) return false;
        if (!styles.TryGetValue(name, out var st)) return false;
        return st.DecimalList == true;
    }

    private static void AppendInlines(XElement p, InlineCollection target, Dictionary<string, OdtStyle> styles, OdtStyle inherited)
    {
        foreach (var node in p.Nodes())
        {
            if (node is XText xt)
            {
                var t = xt.Value;
                if (t.Length > 0) target.Add(new Run(t));
            }
            else if (node is XElement el)
            {
                switch (el.Name.LocalName)
                {
                    case "span":
                        var st = ResolveStyle(el, styles, inherited);
                        var span = new Span();
                        ApplyTextStyle(st, span);
                        AppendInlines(el, span.Inlines, styles, st);
                        if (span.Inlines.Count > 0) target.Add(span);
                        break;

                    case "a":
                        var href = el.Attribute(Xlink + "href")?.Value;
                        var link = new Hyperlink();
                        if (Uri.TryCreate(href, UriKind.Absolute, out var u)) link.NavigateUri = u;
                        AppendInlines(el, link.Inlines, styles, inherited);
                        if (link.Inlines.Count > 0) target.Add(link);
                        break;

                    case "s":
                        var count = el.Attribute(Text + "c")?.Value;
                        int spaces = 1;
                        if (count is not null && int.TryParse(count, out var sp)) spaces = sp;
                        target.Add(new Run(new string(' ', spaces)));
                        break;

                    case "tab":
                        target.Add(new Run("\t"));
                        break;

                    case "line-break":
                        target.Add(new LineBreak());
                        break;
                }
            }
        }
    }

    private static void ApplyTextStyle(OdtStyle st, Span span)
    {
        if (st.Bold == true) span.FontWeight = FontWeights.Bold;
        if (st.Italic == true) span.FontStyle = FontStyles.Italic;
        if (st.Underline == true) span.TextDecorations = TextDecorations.Underline;
        if (st.Strike == true) span.TextDecorations = TextDecorations.Strikethrough;
        if (st.Color is { } c) span.Foreground = new SolidColorBrush(c);
        if (st.Size is { } sz) span.FontSize = sz;
        if (!string.IsNullOrEmpty(st.Font)) span.FontFamily = new FontFamily(st.Font);
    }

    // ── Style resolution ───────────────────────────────────────────────────────

    private sealed class OdtStyle
    {
        public bool? Bold;
        public bool? Italic;
        public bool? Underline;
        public bool? Strike;
        public Color? Color;
        public double? Size;
        public string? Font;
        public TextAlignment? Align;
        public bool? DecimalList;

        public OdtStyle Clone() => new()
        {
            Bold = Bold, Italic = Italic, Underline = Underline, Strike = Strike,
            Color = Color, Size = Size, Font = Font, Align = Align, DecimalList = DecimalList
        };
    }

    private static Dictionary<string, OdtStyle> LoadStyles(ZipArchive zip, XDocument content)
    {
        var map = new Dictionary<string, OdtStyle>(StringComparer.Ordinal);
        CollectStyles(content.Root?.Element(Office + "automatic-styles"), map);
        var se = zip.GetEntry("styles.xml");
        if (se is not null)
        {
            XDocument sx;
            using (var s = se.Open()) sx = XDocument.Load(s);
            CollectStyles(sx.Root?.Element(Office + "styles"), map);
            CollectStyles(sx.Root?.Element(Office + "automatic-styles"), map);
        }
        return map;
    }

    private static void CollectStyles(XElement? root, Dictionary<string, OdtStyle> map)
    {
        if (root is null) return;
        foreach (var st in root.Elements(Style + "style"))
        {
            var name = st.Attribute(Style + "name")?.Value;
            if (name is null) continue;
            var o = new OdtStyle();
            var tp = st.Element(Style + "text-properties");
            if (tp is not null) ReadTextProps(tp, o);
            var pp = st.Element(Style + "paragraph-properties");
            if (pp is not null) ReadParaProps(pp, o);
            map[name] = o;
        }
        foreach (var ls in root.Elements(Text + "list-style"))
        {
            var name = ls.Attribute(Style + "name")?.Value;
            if (name is null) continue;
            var o = new OdtStyle { DecimalList = ls.Elements(Text + "list-level-style-number").Any() };
            map[name] = o;
        }
    }

    private static OdtStyle ResolveStyle(XElement el, Dictionary<string, OdtStyle> styles, OdtStyle parent)
    {
        var merged = parent.Clone();
        var name = el.Attribute(Text + "style-name")?.Value;
        if (name is not null && styles.TryGetValue(name, out var named))
            merged = Merge(merged, named);
        var direct = new OdtStyle();
        ReadTextProps(el, direct);
        ReadParaProps(el, direct);
        return Merge(merged, direct);
    }

    private static OdtStyle Merge(OdtStyle dst, OdtStyle src)
    {
        var r = dst.Clone();
        if (src.Bold is not null) r.Bold = src.Bold;
        if (src.Italic is not null) r.Italic = src.Italic;
        if (src.Underline is not null) r.Underline = src.Underline;
        if (src.Strike is not null) r.Strike = src.Strike;
        if (src.Color is not null) r.Color = src.Color;
        if (src.Size is not null) r.Size = src.Size;
        if (src.Font is not null) r.Font = src.Font;
        if (src.Align is not null) r.Align = src.Align;
        if (src.DecimalList is not null) r.DecimalList = src.DecimalList;
        return r;
    }

    private static void ReadTextProps(XElement el, OdtStyle o)
    {
        var w = el.Attribute(Fo + "font-weight")?.Value;
        if (w is not null) o.Bold = w == "bold" || (int.TryParse(w, out var n) && n >= 600);

        var it = el.Attribute(Fo + "font-style")?.Value;
        if (it is not null) o.Italic = it is "italic" or "oblique";

        var ul = el.Attribute(Style + "text-underline-style")?.Value;
        if (ul is not null) o.Underline = ul != "none";

        var lt = el.Attribute(Style + "text-line-through-style")?.Value;
        if (lt is not null) o.Strike = lt != "none";

        var col = el.Attribute(Fo + "color")?.Value;
        if (col is not null && DocumentIO.ParseColor(col) is SolidColorBrush cb) o.Color = cb.Color;

        var sz = el.Attribute(Fo + "font-size")?.Value;
        if (sz is not null) o.Size = ParseLength(sz);

        var fam = el.Attribute(Fo + "font-family")?.Value;
        if (!string.IsNullOrEmpty(fam)) o.Font = fam.Trim('\'', '"');
    }

    private static void ReadParaProps(XElement el, OdtStyle o)
    {
        var align = el.Attribute(Fo + "text-align")?.Value;
        if (align is not null)
        {
            o.Align = align switch
            {
                "center" => TextAlignment.Center,
                "end" or "right" => TextAlignment.Right,
                "justify" => TextAlignment.Justify,
                _ => TextAlignment.Left
            };
        }
    }

    private static double? ParseLength(string? val)
    {
        if (string.IsNullOrEmpty(val)) return null;
        var end = 0;
        while (end < val.Length && (char.IsDigit(val[end]) || val[end] is '.' or '-')) end++;
        if (end == 0) return null;
        if (double.TryParse(val.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }

    // ── Write ──────────────────────────────────────────────────────────────────

    public static void Save(string path, FlowDocument doc)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        // mimetype MUST be the first entry, uncompressed, per ODF spec.
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var ms = mime.Open())
        {
            var bytes = Encoding.ASCII.GetBytes(MimeType);
            ms.Write(bytes, 0, bytes.Length);
        }

        var sw = new StyleWriter();
        var body = new XElement(Office + "body");
        var text = new XElement(Office + "text");
        body.Add(text);
        foreach (var block in doc.Blocks)
            SerializeBlock(block, text, sw);

        var auto = new XElement(Office + "automatic-styles", sw.Build());
        var root = new XElement(Office + "document-content",
            Namespace("office", Office),
            Namespace("style", Style),
            Namespace("text", Text),
            Namespace("table", Table),
            Namespace("fo", Fo),
            Namespace("xlink", Xlink),
            new XAttribute(Office + "version", "1.2"),
            auto,
            body);

        DocumentIO.WriteEntry(zip, "content.xml",
            new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).ToString(SaveOptions.DisableFormatting));

        DocumentIO.WriteEntry(zip, "styles.xml", StylesXml);
        DocumentIO.WriteEntry(zip, "META-INF/manifest.xml", ManifestXml);
    }

    private static XAttribute Namespace(string prefix, XNamespace ns) =>
        new(XNamespace.Xmlns + prefix, ns.NamespaceName);

    private static void SerializeBlock(Block block, XElement text, StyleWriter sw)
    {
        switch (block)
        {
            case Paragraph p:
                text.Add(SerializeParagraph(p, sw));
                break;
            case List list:
                text.Add(SerializeList(list, sw));
                break;
            case Section s:
                foreach (var b in s.Blocks) SerializeBlock(b, text, sw);
                break;
            case Table table:
                text.Add(SerializeTable(table, sw));
                break;
        }
    }

    private static XElement SerializeParagraph(Paragraph p, StyleWriter sw)
    {
        var el = new XElement(Text + "p");
        if (p.TextAlignment != TextAlignment.Left)
        {
            var key = "align:" + p.TextAlignment;
            el.Add(new XAttribute(Text + "style-name", sw.ParaStyleFor(key, p.TextAlignment)));
        }
        SerializeInlines(p.Inlines, el, new OFmt(), sw);
        return el;
    }

    private static XElement SerializeList(List list, StyleWriter sw)
    {
        bool numbered = list.MarkerStyle == TextMarkerStyle.Decimal;
        var styleName = sw.ListStyleFor(numbered);
        var el = new XElement(Text + "list", new XAttribute(Text + "style-name", styleName));
        foreach (var item in list.ListItems)
        {
            var li = new XElement(Text + "list-item");
            foreach (var block in item.Blocks)
            {
                if (block is Paragraph p) li.Add(SerializeParagraph(p, sw));
                else if (block is List nested) li.Add(SerializeList(nested, sw));
                else SerializeBlock(block, li, sw);
            }
            el.Add(li);
        }
        return el;
    }

    private static XElement SerializeTable(Table table, StyleWriter sw)
    {
        var tbl = new XElement(Table + "table", new XAttribute(Table + "name", "Table1"));
        foreach (var rg in table.RowGroups)
        {
            foreach (var row in rg.Rows)
            {
                var tr = new XElement(Table + "table-row");
                foreach (var cell in row.Cells)
                {
                    var tc = new XElement(Table + "table-cell",
                        new XAttribute(Office + "value-type", "string"));
                    foreach (var block in cell.Blocks)
                    {
                        if (block is Paragraph p) tc.Add(SerializeParagraph(p, sw));
                        else SerializeBlock(block, tc, sw);
                    }
                    if (!tc.HasElements) tc.Add(new XElement(Text + "p"));
                    tr.Add(tc);
                }
                tbl.Add(tr);
            }
        }
        return tbl;
    }

    private sealed record OFmt(
        bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false,
        string? Font = null, double? Size = null, Color? Color = null);

    private static void SerializeInlines(InlineCollection inlines, XElement parent, OFmt f, StyleWriter sw)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    WriteRun(run.Text, parent, Merge(run, f), sw);
                    break;
                case LineBreak:
                    parent.Add(new XElement(Text + "line-break"));
                    break;
                case Hyperlink link:
                    var a = new XElement(Text + "a");
                    if (link.NavigateUri is { } u) a.Add(new XAttribute(Xlink + "href", u.AbsoluteUri));
                    SerializeInlines(link.Inlines, a, f, sw);
                    parent.Add(a);
                    break;
                case Span span:
                    SerializeInlines(span.Inlines, parent, Merge(span, f), sw);
                    break;
            }
        }
    }

    private static OFmt Merge(Inline inline, OFmt f)
    {
        var deco = inline.TextDecorations;
        return new OFmt(
            Bold: f.Bold || inline.FontWeight == FontWeights.Bold,
            Italic: f.Italic || inline.FontStyle == FontStyles.Italic,
            Underline: f.Underline || HasDecoration(deco, TextDecorationLocation.Underline),
            Strike: f.Strike || HasDecoration(deco, TextDecorationLocation.Strikethrough),
            Font: inline.FontFamily?.Source is { Length: > 0 } src ? src : f.Font,
            Size: inline.FontSize > 0 ? inline.FontSize : f.Size,
            Color: inline.Foreground is SolidColorBrush sb ? sb.Color : f.Color);
    }

    private static bool HasDecoration(TextDecorationCollection? deco, TextDecorationLocation loc) =>
        deco is not null && deco.Any(d => d.Location == loc);

    private static void WriteRun(string text, XElement parent, OFmt f, StyleWriter sw)
    {
        if (string.IsNullOrEmpty(text)) return;
        var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) parent.Add(new XElement(Text + "line-break"));
            if (parts[i].Length == 0) continue;
            if (IsEmpty(f))
                parent.Add(new XText(parts[i]));
            else
                parent.Add(new XElement(Text + "span",
                    new XAttribute(Text + "style-name", sw.TextStyleFor(parts[i], f)),
                    new XText(parts[i])));
        }
    }

    private static bool IsEmpty(OFmt f) =>
        !f.Bold && !f.Italic && !f.Underline && !f.Strike &&
        f.Font is null && f.Size is null && f.Color is null;

    // ── Automatic-style writer ─────────────────────────────────────────────────

    private sealed class StyleWriter
    {
        private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _para = new(StringComparer.Ordinal);
        private readonly List<string> _lists = new();
        private int _t, _p;

        public string TextStyleFor(string sample, OFmt f)
        {
            var key = BuildKey(f);
            if (_text.TryGetValue(key, out var name)) return name;
            name = "T" + (++_t);
            _text[key] = name;
            return name;
        }

        public string ParaStyleFor(string key, TextAlignment align)
        {
            if (_para.TryGetValue(key, out var name)) return name;
            name = "P" + (++_p);
            _para[key] = name;
            return name;
        }

        public string ListStyleFor(bool numbered)
        {
            var name = numbered ? "LDecimal" : "LBullet";
            if (_lists.Contains(name)) return name;
            _lists.Add(name);
            return name;
        }

        private static string BuildKey(OFmt f) =>
            $"{(f.Bold ? 1 : 0)}{(f.Italic ? 1 : 0)}{(f.Underline ? 1 : 0)}{(f.Strike ? 1 : 0)}|{f.Font}|{f.Size}|{f.Color}";

        public IEnumerable<XElement> Build()
        {
            foreach (var kv in _text)
            {
                var f = ParseKey(kv.Key);
                var tp = new XElement(Style + "text-properties");
                if (f.Bold) tp.Add(new XAttribute(Fo + "font-weight", "bold"));
                if (f.Italic) tp.Add(new XAttribute(Fo + "font-style", "italic"));
                if (f.Underline) tp.Add(new XAttribute(Style + "text-underline-style", "solid"));
                if (f.Strike) tp.Add(new XAttribute(Style + "text-line-through-style", "solid"));
                if (f.Color is { } c) tp.Add(new XAttribute(Fo + "color", DocumentIO.ToHex(c)));
                if (f.Size is { } sz) tp.Add(new XAttribute(Fo + "font-size", sz.ToString(CultureInfo.InvariantCulture) + "pt"));
                if (!string.IsNullOrEmpty(f.Font)) tp.Add(new XAttribute(Fo + "font-family", f.Font));
                yield return new XElement(Style + "style",
                    new XAttribute(Style + "name", kv.Value),
                    new XAttribute(Style + "family", "text"),
                    tp);
            }

            foreach (var kv in _para)
            {
                var align = kv.Key["align:".Length..];
                var pp = new XElement(Style + "paragraph-properties");
                var fo = align switch
                {
                    "Center" => "center",
                    "Right" => "end",
                    "Justify" => "justify",
                    _ => null
                };
                if (fo is not null) pp.Add(new XAttribute(Fo + "text-align", fo));
                yield return new XElement(Style + "style",
                    new XAttribute(Style + "name", kv.Value),
                    new XAttribute(Style + "family", "paragraph"),
                    new XAttribute(Style + "parent-style-name", "Standard"),
                    pp);
            }

            foreach (var listName in _lists)
            {
                var numbered = listName == "LDecimal";
                var level = numbered
                    ? new XElement(Text + "list-level-style-number",
                        new XAttribute(Text + "level", "1"),
                        new XAttribute(Style + "num-format", "1"),
                        new XAttribute(Style + "num-suffix", "."),
                        new XElement(Style + "list-level-properties", new XAttribute(Fo + "margin-left", "0.5in")))
                    : new XElement(Text + "list-level-style-bullet",
                        new XAttribute(Text + "level", "1"),
                        new XAttribute(Text + "bullet-char", "\u2022"),
                        new XElement(Style + "list-level-properties", new XAttribute(Fo + "margin-left", "0.5in")));
                yield return new XElement(Text + "list-style",
                    new XAttribute(Style + "name", listName),
                    level);
            }
        }

        private static OFmt ParseKey(string key)
        {
            // Bold/Italic/Underline/Strike|Font|Size|Color
            var parts = key.Split('|');
            var flags = parts[0];
            return new OFmt(
                Bold: flags[0] == '1',
                Italic: flags[1] == '1',
                Underline: flags[2] == '1',
                Strike: flags[3] == '1',
                Font: parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null,
                Size: parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sz) ? sz : null,
                Color: parts.Length > 3 && parts[3].Length > 0 ? ParseHex(parts[3]) : null);
        }

        private static Color? ParseHex(string s) =>
            DocumentIO.ParseColor(s) is SolidColorBrush b ? b.Color : null;
    }

    // ── Static package parts ───────────────────────────────────────────────────

    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<office:document-styles xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
        "xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
        "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" " +
        "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" office:version=\"1.2\">\n" +
        "<office:styles>\n" +
        "<style:style style:name=\"Standard\" style:family=\"paragraph\" style:class=\"text\"/>\n" +
        "</office:styles>\n" +
        "<office:automatic-styles/>\n" +
        "<office:master-styles/>\n" +
        "</office:document-styles>";

    private const string ManifestXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">\n" +
        "<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/>\n" +
        "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>\n" +
        "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>\n" +
        "</manifest:manifest>";
}