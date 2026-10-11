using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace AetherCodex.Editor;

/// <summary>
/// Load/save for the rich-text editor. RTF and plain text are handled by WPF's
/// native <see cref="TextRange"/>; DOCX is read/written directly against the
/// OOXML zip package using only the BCL (no external dependencies).
/// </summary>
public static class DocumentIO
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static bool IsRichDocument(string path) =>
        path.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);

    public static bool IsMarkdown(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    public static bool IsPlainText(string path) =>
        path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    public static FlowDocument Load(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".rtf" => LoadRtf(path),
            ".docx" => LoadDocx(path),
            _ => LoadText(path)
        };
    }

    public static void Save(string path, FlowDocument doc)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".rtf": SaveRtf(path, doc); break;
            case ".docx": SaveDocx(path, doc); break;
            case ".md" or ".markdown": SaveMarkdown(path, doc); break;
            default: SaveText(path, doc); break;
        }
    }

    // ── RTF / TXT ──────────────────────────────────────────────────────────────

    private static FlowDocument LoadRtf(string path)
    {
        var doc = new FlowDocument();
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var fs = File.OpenRead(path);
        range.Load(fs, DataFormats.Rtf);
        return doc;
    }

    private static void SaveRtf(string path, FlowDocument doc)
    {
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var fs = File.Create(path);
        range.Save(fs, DataFormats.Rtf);
    }

    private static FlowDocument LoadText(string path)
    {
        var doc = new FlowDocument();
        var para = new Paragraph();
        doc.Blocks.Add(para);
        var lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) para.Inlines.Add(new LineBreak());
            para.Inlines.Add(new Run(lines[i]));
        }
        return doc;
    }

    private static void SaveText(string path, FlowDocument doc)
    {
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var fs = File.Create(path);
        range.Save(fs, DataFormats.Text);
    }

    private static void SaveMarkdown(string path, FlowDocument doc)
    {
        File.WriteAllText(path, MarkdownSerializer.ToMarkdown(doc), new UTF8Encoding(false));
    }

    // ── DOCX read ──────────────────────────────────────────────────────────────

    private static FlowDocument LoadDocx(string path)
    {
        var doc = new FlowDocument();
        using var zip = ZipFile.OpenRead(path);
        var numFmt = LoadNumbering(zip);
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
            if (el.Name.LocalName == "p")
            {
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
                    AppendRuns(el, para);
                    item.Blocks.Add(para);
                    currentList.ListItems.Add(item);
                }
                else
                {
                    currentList = null;
                    var para = new Paragraph();
                    ApplyParagraphProps(el, para);
                    AppendRuns(el, para);
                    doc.Blocks.Add(para);
                }
            }
            else if (el.Name.LocalName == "tbl")
            {
                currentList = null;
                // v1: flatten table cell text into paragraphs.
                foreach (var p in el.Descendants(W + "p"))
                {
                    var para = new Paragraph();
                    AppendRuns(p, para);
                    doc.Blocks.Add(para);
                }
            }
            else
            {
                currentList = null;
            }
        }
        return doc;
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
        return (true, false); // has a numPr but unknown mapping → default to bullet
    }

    private static void ApplyParagraphProps(XElement p, Paragraph para)
    {
        var jc = p.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val")?.Value;
        para.TextAlignment = jc switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            "both" or "distribute" => TextAlignment.Justify,
            _ => TextAlignment.Left
        };
    }

    private static void AppendRuns(XElement p, Paragraph para)
    {
        foreach (var r in p.Elements(W + "r"))
        {
            if (r.Element(W + "br") is not null) { para.Inlines.Add(new LineBreak()); continue; }
            if (r.Element(W + "tab") is not null) { para.Inlines.Add(new Run("\t")); continue; }
            var run = ParseRun(r);
            if (run is not null) para.Inlines.Add(run);
        }
    }

    private static Run? ParseRun(XElement r)
    {
        var run = new Run();
        var rPr = r.Element(W + "rPr");
        if (rPr is not null)
        {
            if (rPr.Element(W + "b") is not null) run.FontWeight = FontWeights.Bold;
            if (rPr.Element(W + "i") is not null) run.FontStyle = FontStyles.Italic;
            if (rPr.Element(W + "u") is not null) run.TextDecorations = TextDecorations.Underline;
            if (rPr.Element(W + "strike") is not null) run.TextDecorations = TextDecorations.Strikethrough;

            var vert = rPr.Element(W + "vertAlign")?.Attribute(W + "val")?.Value;
            if (vert == "subscript") run.BaselineAlignment = BaselineAlignment.Subscript;
            else if (vert == "superscript") run.BaselineAlignment = BaselineAlignment.Superscript;

            var color = rPr.Element(W + "color")?.Attribute(W + "val")?.Value;
            if (ParseColor(color) is { } fg) run.Foreground = fg;

            var highlight = rPr.Element(W + "highlight")?.Attribute(W + "val")?.Value;
            if (ParseColor(highlight) is { } hl) run.Background = hl;

            var sz = rPr.Element(W + "sz")?.Attribute(W + "val")?.Value;
            if (double.TryParse(sz, NumberStyles.Integer, CultureInfo.InvariantCulture, out var half) && half > 0)
                run.FontSize = half / 2.0;

            var ascii = rPr.Element(W + "rFonts")?.Attribute(W + "ascii")?.Value;
            if (!string.IsNullOrEmpty(ascii)) run.FontFamily = new FontFamily(ascii);
        }

        var text = string.Concat(r.Elements(W + "t").Select(t => t.Value));
        if (text.Length == 0) return null;
        run.Text = text;
        return run;
    }

    // ── DOCX write ─────────────────────────────────────────────────────────────

    private sealed record Fmt(
        bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false,
        string? Font = null, double? Size = null, Color? Color = null,
        BaselineAlignment Baseline = BaselineAlignment.Baseline);

    private static void SaveDocx(string path, FlowDocument doc)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(zip, "[Content_Types].xml", ContentTypesXml);
        WriteEntry(zip, "_rels/.rels", RootRelsXml);
        WriteEntry(zip, "word/_rels/document.xml.rels", DocumentRelsXml);
        WriteEntry(zip, "word/numbering.xml", NumberingXml);

        var body = new XElement(W + "body");
        foreach (var block in doc.Blocks)
            SerializeBlock(block, body);

        var root = new XElement(W + "document",
            new XAttribute(XNamespace.Xmlns + "w", W),
            new XAttribute(XNamespace.Xmlns + "r", RelNs),
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
            // Table / BlockUIContainer: not produced by this editor in v1.
        }
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
                case Span span:
                    WriteInlines(span.Inlines, parent, Merge(f, span));
                    break;
                // InlineUIContainer (images) etc. — not serialized in v1.
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
            rPr.Add(new XElement(W + "color", new XAttribute(W + "val", c.ToString().TrimStart('#'))));
        if (f.Size is { } size)
            rPr.Add(new XElement(W + "sz", new XAttribute(W + "val", (int)Math.Round(size * 2))));
        if (!string.IsNullOrEmpty(f.Font))
            rPr.Add(new XElement(W + "rFonts", new XAttribute(W + "ascii", f.Font)));

        var r = new XElement(W + "r");
        if (rPr.HasElements) r.Add(rPr);
        r.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));
        return r;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var sw = new StreamWriter(s, new UTF8Encoding(false));
        sw.Write(content);
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    private static Brush? ParseColor(string? val)
    {
        if (string.IsNullOrEmpty(val) || val == "auto") return null;
        string hex;
        if (val.StartsWith('#')) hex = val;
        else if (val.Length == 6 && val.All(Uri.IsHexDigit)) hex = "#" + val;
        else
        {
            var named = new Dictionary<string, string>
            {
                ["black"] = "#000000", ["white"] = "#FFFFFF", ["gray"] = "#808080",
                ["red"] = "#FF0000", ["green"] = "#008000", ["blue"] = "#0000FF",
                ["yellow"] = "#FFFF00", ["cyan"] = "#00FFFF", ["magenta"] = "#FF00FF"
            };
            if (!named.TryGetValue(val.ToLowerInvariant(), out hex)) return null;
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    /// <summary>Clear auto text-colour (pure black/white) so documents inherit the
    /// active theme foreground instead of baking in Word's default black.</summary>
    public static void NormalizeAutoColors(FlowDocument doc)
    {
        foreach (var run in EnumerateRuns(doc))
        {
            if (run.Foreground is SolidColorBrush fg && (fg.Color == Colors.Black || fg.Color == Colors.White))
                run.ClearValue(TextElement.ForegroundProperty);
        }
    }

    private static IEnumerable<Run> EnumerateRuns(FlowDocument doc)
    {
        foreach (var block in doc.Blocks)
            foreach (var run in RunsInBlock(block))
                yield return run;
    }

    private static IEnumerable<Run> RunsInBlock(Block block)
    {
        switch (block)
        {
            case Paragraph p: return RunsInInlines(p.Inlines);
            case List l: return l.ListItems.SelectMany(li => li.Blocks.SelectMany(RunsInBlock));
            case Section s: return s.Blocks.SelectMany(RunsInBlock);
            case Table t: return t.RowGroups.SelectMany(g => g.Rows)
                .SelectMany(row => row.Cells).SelectMany(c => c.Blocks.SelectMany(RunsInBlock));
            default: return Enumerable.Empty<Run>();
        }
    }

    private static IEnumerable<Run> RunsInInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run) yield return run;
            else if (inline is Span span)
                foreach (var r in RunsInInlines(span.Inlines)) yield return r;
        }
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