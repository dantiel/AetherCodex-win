using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace AetherCodex.Editor;

/// <summary>
/// OOXML Word (.docx) read/write against the zip package using only the BCL.
/// Paragraphs, runs, character formatting, lists, real tables, images and
/// hyperlinks are handled; the writer covers paragraphs/lists/tables and their
/// inline formatting (images are read-only for now).
/// </summary>
internal static class DocxFormat
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace Vml = "urn:schemas-microsoft-com:vml";

    // ── Read ───────────────────────────────────────────────────────────────────

    public static FlowDocument Load(string path)
    {
        var doc = new FlowDocument();
        using var zip = ZipFile.OpenRead(path);
        var numFmt = LoadNumbering(zip);
        var rels = LoadRelationships(zip);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null) return doc;

        XDocument xdoc;
        using (var s = entry.Open()) xdoc = XDocument.Load(s);

        var body = xdoc.Root?.Element(W + "body");
        if (body is null) return doc;

        List? currentList = null;
        bool currentDecimal = false;

        foreach (var el in body.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "p":
                    var (isList, isDecimal) = DetectList(el, numFmt);
                    if (isList)
                    {
                        if (currentList is null || currentDecimal != isDecimal)
                        {
                            currentList = new List
                            {
                                MarkerStyle = isDecimal ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc
                            };
                            doc.Blocks.Add(currentList);
                            currentDecimal = isDecimal;
                        }
                        var item = new ListItem();
                        var para = new Paragraph();
                        AppendInlines(el, para, zip, rels);
                        item.Blocks.Add(para);
                        currentList.ListItems.Add(item);
                    }
                    else
                    {
                        currentList = null;
                        var para = new Paragraph();
                        ApplyParagraphProps(el, para);
                        AppendInlines(el, para, zip, rels);
                        doc.Blocks.Add(para);
                    }
                    break;

                case "tbl":
                    currentList = null;
                    doc.Blocks.Add(LoadTable(el, zip, rels));
                    break;

                default:
                    currentList = null;
                    break;
            }
        }
        return doc;
    }

    private static Table LoadTable(XElement tbl, ZipArchive zip, Dictionary<string, string> rels)
    {
        var table = new Table();
        var rg = new TableRowGroup();
        foreach (var tr in tbl.Elements(W + "tr"))
        {
            var row = new TableRow();
            foreach (var tc in tr.Elements(W + "tc"))
            {
                var cell = new TableCell();
                foreach (var p in tc.Elements(W + "p"))
                {
                    var para = new Paragraph();
                    ApplyParagraphProps(p, para);
                    AppendInlines(p, para, zip, rels);
                    cell.Blocks.Add(para);
                }
                if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph());
                row.Cells.Add(cell);
            }
            rg.Rows.Add(row);
        }
        table.RowGroups.Add(rg);
        return table;
    }

    private static void AppendInlines(XElement p, Paragraph para, ZipArchive zip, Dictionary<string, string> rels)
        => AppendInlines(p, para.Inlines, zip, rels);

    private static void AppendInlines(XElement p, InlineCollection target, ZipArchive zip, Dictionary<string, string> rels)
    {
        foreach (var node in p.Nodes())
        {
            if (node is not XElement el) continue;
            switch (el.Name.LocalName)
            {
                case "r":
                    AppendRun(el, target, zip, rels);
                    break;

                case "hyperlink":
                    var link = new Hyperlink();
                    var rid = el.Attribute(RelNs + "id")?.Value;
                    if (rid is not null && rels.TryGetValue(rid, out var url))
                        if (Uri.TryCreate(url, UriKind.Absolute, out var u)) link.NavigateUri = u;
                    AppendInlines(el, link.Inlines, zip, rels);
                    if (link.Inlines.Count > 0) target.Add(link);
                    break;

                // Content controls and inline wrappers that hold further content.
                case "sdt":
                    if (el.Element(W + "sdtContent") is { } sdtContent)
                        AppendInlines(sdtContent, target, zip, rels);
                    break;

                case "ins":
                case "del":
                case "moveFrom":
                case "moveTo":
                case "smartTag":
                case "dir":
                    AppendInlines(el, target, zip, rels);
                    break;
            }
        }
    }

    /// <summary>Emits a run's content in document order — text, line breaks, tabs,
    /// symbols — instead of reducing a run that happens to contain a <c>w:br</c> to a
    /// single break (which used to drop all of its text).</summary>
    private static void AppendRun(XElement r, InlineCollection target, ZipArchive zip, Dictionary<string, string> rels)
    {
        if (r.Descendants(A + "blip").Any() || r.Descendants(Vml + "imagedata").Any())
            TryAddImage(target, r, zip, rels);

        var rPr = r.Element(W + "rPr");
        var segments = new List<string>();
        foreach (var child in r.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "t":
                case "instrText":
                case "delText":
                    segments.Add(child.Value);
                    break;
                case "br":
                case "cr":
                    FlushSegments(segments, target, rPr);
                    target.Add(new LineBreak());
                    break;
                case "tab":
                    FlushSegments(segments, target, rPr);
                    target.Add(new Run("\t"));
                    break;
                case "noBreakHyphen":
                    segments.Add("-");
                    break;
                case "sym":
                    segments.Add(child.Attribute(W + "char")?.Value ?? "");
                    break;
            }
        }
        FlushSegments(segments, target, rPr);
    }

    private static void FlushSegments(List<string> segments, InlineCollection target, XElement? rPr)
    {
        if (segments.Count == 0) return;
        string text = string.Concat(segments);
        segments.Clear();
        if (text.Length == 0) return;
        target.Add(BuildRun(text, rPr));
    }

    private static void TryAddImage(InlineCollection target, XElement r, ZipArchive zip, Dictionary<string, string> rels)
    {
        var blip = r.Descendants(A + "blip").FirstOrDefault();
        var imagedata = r.Descendants(Vml + "imagedata").FirstOrDefault();
        var rid = blip?.Attribute(RelNs + "embed")?.Value ?? imagedata?.Attribute(RelNs + "id")?.Value;
        if (rid is null || !rels.TryGetValue(rid, out var relTarget)) return;

        var entry = zip.GetEntry("word/" + relTarget);
        if (entry is null) return;

        try
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            var img = new Image { Source = bmp, Stretch = Stretch.Uniform };
            img.MaxWidth = 600;
            target.Add(new InlineUIContainer(img));
        }
        catch
        {
            // Unsupported/corrupt image part — skip it, keep the surrounding text.
        }
    }

    private static Dictionary<string, string> LoadRelationships(ZipArchive zip)
    {
        var map = new Dictionary<string, string>();
        var entry = zip.GetEntry("word/_rels/document.xml.rels");
        if (entry is null) return map;
        XDocument xdoc;
        using (var s = entry.Open()) xdoc = XDocument.Load(s);
        foreach (var rel in xdoc.Root?.Elements(RelNs + "Relationship") ?? Enumerable.Empty<XElement>())
        {
            var id = rel.Attribute("Id")?.Value;
            var target = rel.Attribute("Target")?.Value;
            if (id is not null && target is not null) map[id] = target;
        }
        return map;
    }

    private static Dictionary<int, bool> LoadNumbering(ZipArchive zip)
    {
        var map = new Dictionary<int, bool>();
        var entry = zip.GetEntry("word/numbering.xml");
        if (entry is null) return map;

        XDocument xdoc;
        using (var s = entry.Open()) xdoc = XDocument.Load(s);
        var root = xdoc.Root;
        if (root is null) return map;

        var abs = new Dictionary<int, string>();
        foreach (var a in root.Elements(W + "abstractNum"))
        {
            var id = a.Attribute(W + "abstractNumId")?.Value;
            var lvl = a.Elements(W + "lvl").FirstOrDefault();
            var fmt = lvl?.Element(W + "numFmt")?.Attribute(W + "val")?.Value ?? "bullet";
            if (id is not null && int.TryParse(id, out var aid)) abs[aid] = fmt;
        }

        foreach (var n in root.Elements(W + "num"))
        {
            var numId = n.Attribute(W + "numId")?.Value;
            var absId = n.Element(W + "abstractNumId")?.Attribute(W + "val")?.Value;
            if (numId is null || absId is null) continue;
            if (!int.TryParse(numId, out var nid) || !int.TryParse(absId, out var aid)) continue;
            abs.TryGetValue(aid, out var fmt);
            map[nid] = fmt == "decimal";
        }
        return map;
    }

    private static (bool IsList, bool IsDecimal) DetectList(XElement p, Dictionary<int, bool> numFmt)
    {
        var numId = p.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val")?.Value;
        if (numId is null) return (false, false);
        if (int.TryParse(numId, out var id) && numFmt.TryGetValue(id, out var isDecimal))
            return (true, isDecimal);
        return (true, false);
    }

    private static void ApplyParagraphProps(XElement p, Paragraph para)
    {
        var pPr = p.Element(W + "pPr");
        if (pPr is null) return;

        var jc = pPr.Element(W + "jc")?.Attribute(W + "val")?.Value;
        para.TextAlignment = jc switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            "both" or "distribute" => TextAlignment.Justify,
            _ => TextAlignment.Left
        };

        // Paragraph style → heading level (larger + bold).
        var pStyle = pPr.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
        if (HeadingLevel(pStyle) is { } lvl)
        {
            para.FontSize = HeadingSize(lvl);
            para.FontWeight = FontWeights.Bold;
        }

        var spacing = pPr.Element(W + "spacing");
        if (spacing is not null)
        {
            var margin = para.Margin;
            if (double.TryParse(spacing.Attribute(W + "before")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var before))
                margin.Top = TwipsToDip(before);
            if (double.TryParse(spacing.Attribute(W + "after")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var after))
                margin.Bottom = TwipsToDip(after);
            para.Margin = margin;

            if (double.TryParse(spacing.Attribute(W + "line")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) && line > 0)
            {
                string rule = spacing.Attribute(W + "lineRule")?.Value ?? "auto";
                para.LineHeight = rule is "exact" or "atLeast"
                    ? TwipsToDip(line)
                    : para.FontSize * (line / 240.0);
            }
        }

        var ind = pPr.Element(W + "ind");
        if (ind is not null)
        {
            var margin = para.Margin;
            if (double.TryParse(ind.Attribute(W + "left")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var left))
                margin.Left = TwipsToDip(left);
            para.Margin = margin;

            if (double.TryParse(ind.Attribute(W + "firstLine")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var first))
                para.TextIndent = TwipsToDip(first);
            else if (double.TryParse(ind.Attribute(W + "hanging")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hang))
                para.TextIndent = -TwipsToDip(hang);
        }
    }

    private static double TwipsToDip(double twips) => twips / 15.0; // 1 twip = 1/20 pt = 1/15 DIP

    private static int? HeadingLevel(string style)
    {
        string s = style.ToLowerInvariant();
        if (s.Contains("heading") || s.Contains("überschrift") || s.Contains("titolo") || s.Contains("titre") || s.Contains("título"))
        {
            var digits = new string(s.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? Math.Clamp(n, 1, 9) : 1;
        }
        if (s is "title" or "titel" or "titre" or "título") return 0;
        return null;
    }

    private static double HeadingSize(int level) => level switch
    {
        1 => 32, 2 => 26, 3 => 22, 4 => 20, 5 => 18, 6 => 16, _ => 14
    };

    private static Run BuildRun(string text, XElement? rPr)
    {
        var run = new Run(text);
        if (rPr is null) return run;

        if (IsToggleOn(rPr.Element(W + "b"))) run.FontWeight = FontWeights.Bold;
        if (IsToggleOn(rPr.Element(W + "i"))) run.FontStyle = FontStyles.Italic;
        if (IsUnderlineOn(rPr.Element(W + "u"))) run.TextDecorations = TextDecorations.Underline;
        if (IsToggleOn(rPr.Element(W + "strike"))) run.TextDecorations = TextDecorations.Strikethrough;

        var vert = rPr.Element(W + "vertAlign")?.Attribute(W + "val")?.Value;
        if (vert == "subscript") run.BaselineAlignment = BaselineAlignment.Subscript;
        else if (vert == "superscript") run.BaselineAlignment = BaselineAlignment.Superscript;

        var color = rPr.Element(W + "color")?.Attribute(W + "val")?.Value;
        if (DocumentIO.ParseColor(color) is { } fg) run.Foreground = fg;

        var highlight = rPr.Element(W + "highlight")?.Attribute(W + "val")?.Value;
        if (DocumentIO.ParseColor(highlight) is { } hl) run.Background = hl;

        var shd = rPr.Element(W + "shd")?.Attribute(W + "fill")?.Value;
        if (DocumentIO.ParseColor(shd) is { } fill) run.Background = fill;

        var sz = rPr.Element(W + "sz")?.Attribute(W + "val")?.Value;
        if (double.TryParse(sz, NumberStyles.Integer, CultureInfo.InvariantCulture, out var half) && half > 0)
            run.FontSize = half / 2.0;

        var ascii = rPr.Element(W + "rFonts")?.Attribute(W + "ascii")?.Value
                 ?? rPr.Element(W + "rFonts")?.Attribute(W + "hAnsi")?.Value;
        if (!string.IsNullOrEmpty(ascii)) run.FontFamily = new FontFamily(ascii);

        return run;
    }

    private static bool IsToggleOn(XElement? el)
    {
        if (el is null) return false;
        var v = el.Attribute(W + "val")?.Value;
        return v is null || (v != "0" && v != "false" && v != "off");
    }

    private static bool IsUnderlineOn(XElement? el)
    {
        if (el is null) return false;
        var v = el.Attribute(W + "val")?.Value;
        return v is null || (v != "none" && v != "0" && v != "false");
    }

    // ── Write ──────────────────────────────────────────────────────────────────

    private sealed record Fmt(
        bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false,
        string? Font = null, double? Size = null, Color? Color = null,
        BaselineAlignment Baseline = BaselineAlignment.Baseline);

    public static void Save(string path, FlowDocument doc)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        DocumentIO.WriteEntry(zip, "[Content_Types].xml", ContentTypesXml);
        DocumentIO.WriteEntry(zip, "_rels/.rels", RootRelsXml);
        DocumentIO.WriteEntry(zip, "word/_rels/document.xml.rels", DocumentRelsXml);
        DocumentIO.WriteEntry(zip, "word/numbering.xml", NumberingXml);

        var body = new XElement(W + "body");
        foreach (var block in doc.Blocks)
            SerializeBlock(block, body);

        var root = new XElement(W + "document",
            new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "r", RelNs.NamespaceName),
            body);

        var entry = zip.CreateEntry("word/document.xml");
        using (var s = entry.Open())
            new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(s);
    }

    private static void SerializeBlock(Block block, XElement body)
    {
        switch (block)
        {
            case Paragraph p:
                body.Add(SerializeParagraph(p));
                break;
            case List list:
                int numId = list.MarkerStyle == TextMarkerStyle.Decimal ? 2 : 1;
                foreach (var item in list.ListItems)
                {
                    var p = new XElement(W + "p");
                    p.Add(new XElement(W + "pPr",
                        new XElement(W + "numPr",
                            new XElement(W + "ilvl", new XAttribute(W + "val", "0")),
                            new XElement(W + "numId", new XAttribute(W + "val", numId)))));
                    foreach (var b in item.Blocks)
                    {
                        if (b is Paragraph ip) WriteInlines(ip.Inlines, p, new Fmt());
                        else SerializeBlock(b, p);
                    }
                    body.Add(p);
                }
                break;
            case Section s:
                foreach (var b in s.Blocks) SerializeBlock(b, body);
                break;
            case Table table:
                body.Add(SerializeTable(table));
                break;
        }
    }

    private static XElement SerializeTable(Table table)
    {
        var tbl = new XElement(W + "tbl");
        tbl.Add(new XElement(W + "tblPr",
            new XElement(W + "tblW", new XAttribute(W + "w", "0"), new XAttribute(W + "type", "auto")),
            new XElement(W + "tblBorders",
                new XElement(W + "top", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4")),
                new XElement(W + "left", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4")),
                new XElement(W + "bottom", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4")),
                new XElement(W + "right", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4")))));

        foreach (var rg in table.RowGroups)
        {
            foreach (var row in rg.Rows)
            {
                var tr = new XElement(W + "tr");
                foreach (var cell in row.Cells)
                {
                    var tc = new XElement(W + "tc");
                    foreach (var block in cell.Blocks)
                    {
                        if (block is Paragraph cp) tc.Add(SerializeParagraph(cp));
                        else SerializeBlock(block, tc);
                    }
                    if (!tc.HasElements) tc.Add(SerializeParagraph(new Paragraph()));
                    tr.Add(tc);
                }
                tbl.Add(tr);
            }
        }
        return tbl;
    }

    private static XElement SerializeParagraph(Paragraph p)
    {
        var el = new XElement(W + "p");
        var pPr = new XElement(W + "pPr");
        switch (p.TextAlignment)
        {
            case TextAlignment.Center: pPr.Add(new XElement(W + "jc", new XAttribute(W + "val", "center"))); break;
            case TextAlignment.Right: pPr.Add(new XElement(W + "jc", new XAttribute(W + "val", "right"))); break;
            case TextAlignment.Justify: pPr.Add(new XElement(W + "jc", new XAttribute(W + "val", "both"))); break;
        }
        if (pPr.HasElements) el.Add(pPr);
        WriteInlines(p.Inlines, el, new Fmt());
        return el;
    }

    private static void WriteInlines(InlineCollection inlines, XElement parent, Fmt f)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    WriteRunText(run.Text, parent, Merge(f, run));
                    break;
                case LineBreak:
                    parent.Add(new XElement(W + "r", new XElement(W + "br")));
                    break;
                case Hyperlink link:
                    var hl = new XElement(W + "hyperlink",
                        new XAttribute(RelNs + "id", "rIdLink"),
                        new XAttribute(W + "history", "1"));
                    WriteInlines(link.Inlines, hl, f);
                    parent.Add(hl);
                    break;
                case Span span:
                    WriteInlines(span.Inlines, parent, Merge(f, span));
                    break;
                // InlineUIContainer (images) — not serialized by the writer.
            }
        }
    }

    private static Fmt Merge(Fmt f, Inline inline)
    {
        var deco = inline.TextDecorations;
        return new Fmt(
            Bold: f.Bold || inline.FontWeight == FontWeights.Bold,
            Italic: f.Italic || inline.FontStyle == FontStyles.Italic,
            Underline: f.Underline || HasDecoration(deco, TextDecorationLocation.Underline),
            Strike: f.Strike || HasDecoration(deco, TextDecorationLocation.Strikethrough),
            Font: inline.FontFamily?.Source is { Length: > 0 } src ? src : f.Font,
            Size: inline.FontSize > 0 ? inline.FontSize : f.Size,
            Color: inline.Foreground is SolidColorBrush sb ? sb.Color : f.Color,
            Baseline: inline.BaselineAlignment != BaselineAlignment.Baseline ? inline.BaselineAlignment : f.Baseline);
    }

    private static bool HasDecoration(TextDecorationCollection? deco, TextDecorationLocation loc) =>
        deco is not null && deco.Any(d => d.Location == loc);

    private static void WriteRunText(string text, XElement parent, Fmt f)
    {
        if (string.IsNullOrEmpty(text)) return;
        var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) parent.Add(new XElement(W + "r", new XElement(W + "br")));
            if (parts[i].Length > 0) parent.Add(MakeRun(parts[i], f));
        }
    }

    private static XElement MakeRun(string text, Fmt f)
    {
        var rPr = new XElement(W + "rPr");
        if (f.Bold) rPr.Add(new XElement(W + "b"));
        if (f.Italic) rPr.Add(new XElement(W + "i"));
        if (f.Underline) rPr.Add(new XElement(W + "u", new XAttribute(W + "val", "single")));
        if (f.Strike) rPr.Add(new XElement(W + "strike"));
        if (f.Baseline == BaselineAlignment.Subscript)
            rPr.Add(new XElement(W + "vertAlign", new XAttribute(W + "val", "subscript")));
        else if (f.Baseline == BaselineAlignment.Superscript)
            rPr.Add(new XElement(W + "vertAlign", new XAttribute(W + "val", "superscript")));
        if (f.Color is { } c && c != Colors.Black && c != Colors.White)
            rPr.Add(new XElement(W + "color", new XAttribute(W + "val", DocumentIO.ToHex(c).TrimStart('#'))));
        if (f.Size is { } size)
            rPr.Add(new XElement(W + "sz", new XAttribute(W + "val", (int)Math.Round(size * 2))));
        if (!string.IsNullOrEmpty(f.Font))
            rPr.Add(new XElement(W + "rFonts", new XAttribute(W + "ascii", f.Font)));

        var r = new XElement(W + "r");
        if (rPr.HasElements) r.Add(rPr);
        r.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));
        return r;
    }

    // ── Static package parts ───────────────────────────────────────────────────

    private const string ContentTypesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\n" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\n" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>\n" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>\n" +
        "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>\n" +
        "</Types>";

    private const string RootRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\n" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>\n" +
        "</Relationships>";

    private const string DocumentRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\n" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>\n" +
        "</Relationships>";

    private const string NumberingXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<w:numbering xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">\n" +
        "<w:abstractNum w:abstractNumId=\"0\">\n" +
        "<w:multiLevelType w:val=\"hybridMultilevel\"/>\n" +
        "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"\u2022\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr></w:lvl>\n" +
        "</w:abstractNum>\n" +
        "<w:abstractNum w:abstractNumId=\"1\">\n" +
        "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr></w:lvl>\n" +
        "</w:abstractNum>\n" +
        "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>\n" +
        "<w:num w:numId=\"2\"><w:abstractNumId w:val=\"1\"/></w:num>\n" +
        "</w:numbering>";
}