using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AetherCodex.Spreadsheet;

/// <summary>
/// OOXML SpreadsheetML (.xlsx) read/write against the zip package using only the
/// BCL. One worksheet round-trips; cells keep their formula (the <c>&lt;f&gt;</c> element)
/// plus cached value, literal numbers/booleans stay typed, and literal text is
/// stored in the shared-strings table exactly as Excel expects.
/// </summary>
internal static class XlsxFormat
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public const string MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // ── Read ───────────────────────────────────────────────────────────────────

    public static Sheet Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = LoadSharedStrings(zip);
        var styleTable = XlsxStyleTable.Load(zip);
        string target = ResolveFirstSheet(zip);

        XDocument ws;
        var entry = zip.GetEntry(target) ?? zip.GetEntry("xl/worksheets/sheet1.xml");
        if (entry is null) return new Sheet();
        using (var s = entry.Open()) ws = XDocument.Load(s);

        var rows = ws.Root!.Descendants(Main + "row").ToList();
        var anchors = ScanFormulaAnchors(rows);

        var cells = new List<(int Row, int Col, string Raw)>();
        var styles = new List<(int Row, int Col, CellStyle Style)>();
        int rowIndex = 0;
        foreach (var row in rows)
        {
            if (row.Attribute("r") is { } ra && int.TryParse(ra.Value, out int rr))
                rowIndex = rr - 1;

            int colIndex = 0;
            foreach (var cell in row.Elements(Main + "c"))
            {
                string? refAttr = cell.Attribute("r")?.Value;
                int r = rowIndex, c = colIndex;
                if (refAttr is not null && SheetRef.TryParse(refAttr, out r, out c)) { /* absolute */ }

                string raw = CellRaw(cell, shared, anchors);
                if (raw.Length > 0) cells.Add((r, c, raw));
                if (int.TryParse(cell.Attribute("s")?.Value, out int si))
                {
                    if (styleTable.StyleFor(si) is CellStyle style) styles.Add((r, c, style));
                }
                colIndex = c + 1;
            }
            rowIndex++;
        }

        var sheet = new Sheet();
        sheet.ReplaceAll(cells);
        sheet.ApplyStyles(styles);
        return sheet;
    }

    // ── shared / array formula resolution ──────────────────────────────────────

    private sealed class FormulaAnchor
    {
        public int Row, Col;
        public string Text = "";
    }

    private sealed class FormulaContext
    {
        public readonly Dictionary<int, FormulaAnchor> Shared = new();
        public readonly Dictionary<string, FormulaAnchor> Array = new(StringComparer.Ordinal);
    }

    /// <summary>Excel stores dragged ("shared") and array formulas once, on the anchor
    /// cell; follower cells carry an empty <c>&lt;f&gt;</c> plus a cached value. Collect the
    /// anchors first so followers can be reconstructed by row/column offset.</summary>
    private static FormulaContext ScanFormulaAnchors(List<XElement> rows)
    {
        var ctx = new FormulaContext();
        int rowIndex = 0;
        foreach (var row in rows)
        {
            if (row.Attribute("r") is { } ra && int.TryParse(ra.Value, out int rr)) rowIndex = rr - 1;
            int colIndex = 0;
            foreach (var cell in row.Elements(Main + "c"))
            {
                int r = rowIndex, c = colIndex;
                if (cell.Attribute("r") is { } rAttr && SheetRef.TryParse(rAttr.Value, out r, out c)) { }
                var f = cell.Element(Main + "f");
                if (f is not null && f.Value.Length > 0)
                {
                    string t = f.Attribute("t")?.Value ?? "";
                    var anchor = new FormulaAnchor { Row = r, Col = c, Text = f.Value };
                    if (t == "shared" && int.TryParse(f.Attribute("si")?.Value, out int si))
                        ctx.Shared[si] = anchor;
                    else if (t == "array" && f.Attribute("ref")?.Value is { } aref)
                        ctx.Array[aref] = anchor;
                }
                colIndex = c + 1;
            }
            rowIndex++;
        }
        return ctx;
    }

    private static string CellRaw(XElement cell, string[] shared, FormulaContext anchors)
    {
        var f = cell.Element(Main + "f");
        if (f is not null)
        {
            string t = f.Attribute("t")?.Value ?? "";
            string text = f.Value;

            if (text.Length == 0 && t == "shared" && int.TryParse(f.Attribute("si")?.Value, out int si))
            {
                if (anchors.Shared.TryGetValue(si, out var anchor))
                    text = OffsetFromAnchor(anchor, cell);
            }
            else if (text.Length == 0 && t == "array" && f.Attribute("ref")?.Value is { } aref)
            {
                if (anchors.Array.TryGetValue(aref, out var anchor))
                    text = OffsetFromAnchor(anchor, cell);
            }

            if (text.Length > 0) return "=" + text;

            // Unresolved formula — keep the cached value so the cell isn't blank.
            return cell.Element(Main + "v")?.Value ?? "";
        }

        string type = cell.Attribute("t")?.Value ?? "";
        switch (type)
        {
            case "s":
            {
                int idx = int.TryParse(cell.Element(Main + "v")?.Value, out var i) ? i : -1;
                return idx >= 0 && idx < shared.Length ? shared[idx] : "";
            }
            case "inlineStr":
            {
                var sb = new StringBuilder();
                foreach (var t in cell.Descendants(Main + "t")) sb.Append(t.Value);
                return sb.ToString();
            }
            case "b":
                return cell.Element(Main + "v")?.Value == "1" ? "TRUE" : "FALSE";
            case "str": // cached formula string result (no <f> present here, but tolerate)
                return cell.Element(Main + "v")?.Value ?? "";
            case "e": // error value
                return cell.Element(Main + "v")?.Value ?? "#ERROR!";
            case "d": // ISO date
                return cell.Element(Main + "v")?.Value ?? "";
            default: // number (t="n" or absent)
                return cell.Element(Main + "v")?.Value ?? "";
        }
    }

    private static string OffsetFromAnchor(FormulaAnchor anchor, XElement cell)
    {
        if (cell.Attribute("r") is { } ra && SheetRef.TryParse(ra.Value, out int cr, out int cc))
            return OffsetFormula(anchor.Text, anchor.Row, anchor.Col, cr, cc);
        return anchor.Text;
    }

    /// <summary>Recomputes a formula body for a follower cell by shifting every relative
    /// cell/range reference by (row, col) delta; absolute ($) parts are left alone.</summary>
    private static string OffsetFormula(string formula, int anchorRow, int anchorCol, int row, int col)
    {
        int dr = row - anchorRow;
        int dc = col - anchorCol;
        if (dr == 0 && dc == 0) return formula;
        return RefRx.Replace(formula, m => OffsetRef(m, dr, dc));
    }

    private static readonly Regex RefRx = new(@"(\$?)([A-Za-z]{1,3})(\$?)(\d+)(?!\s*\()", RegexOptions.Compiled);

    private static string OffsetRef(Match m, int dr, int dc)
    {
        string colAbs = m.Groups[1].Value;
        string col = m.Groups[2].Value;
        string rowAbs = m.Groups[3].Value;
        if (!int.TryParse(m.Groups[4].Value, out int row)) return m.Value;

        int c = 0;
        foreach (char ch in col) c = c * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        c -= 1;

        c += colAbs == "$" ? 0 : dc;
        row += rowAbs == "$" ? 0 : dr;
        if (c < 0 || row < 1) return m.Value;

        return colAbs + SheetRef.ColumnLabel(c) + rowAbs + row;
    }

    private static string[] LoadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return Array.Empty<string>();
        XDocument xdoc;
        using (var s = entry.Open()) xdoc = XDocument.Load(s);
        var list = new List<string>();
        foreach (var si in xdoc.Root!.Elements(Main + "si"))
        {
            var sb = new StringBuilder();
            foreach (var t in si.Descendants(Main + "t")) sb.Append(t.Value);
            list.Add(sb.ToString());
        }
        return list.ToArray();
    }

    private static string ResolveFirstSheet(ZipArchive zip)
    {
        // Default target; refined by workbook.xml + rels when present.
        string target = "xl/worksheets/sheet1.xml";

        var wbEntry = zip.GetEntry("xl/workbook.xml");
        if (wbEntry is null) return target;

        XDocument wb;
        using (var s = wbEntry.Open()) wb = XDocument.Load(s);
        var sheetEl = wb.Root?.Elements(Main + "sheets").Elements(Main + "sheet").FirstOrDefault();
        if (sheetEl is null) return target;

        var id = sheetEl.Attribute(RelNs + "id")?.Value ?? sheetEl.Attribute("id")?.Value;
        if (id is null) return target;

        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (relsEntry is null) return target;

        XDocument rels;
        using (var s = relsEntry.Open()) rels = XDocument.Load(s);
        foreach (var rel in rels.Root!.Elements(RelNs + "Relationship"))
        {
            if (rel.Attribute("Id")?.Value == id && rel.Attribute("Target")?.Value is { } t)
            {
                target = NormalizeTarget(t);
                break;
            }
        }
        return target;
    }

    private static string NormalizeTarget(string t)
    {
        t = t.Replace('\\', '/');
        if (t.StartsWith("/")) return t.TrimStart('/');
        return "xl/" + t.TrimStart('/');
    }

    // ── Write ──────────────────────────────────────────────────────────────────

    public static void Save(string path, Sheet sheet)
    {
        var sharedIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        var sharedList = new List<string>();
        var styleReg = new XlsxStyleRegistry();

        var coords = new SortedSet<(int Row, int Col)>(CellCoordComparer.Instance);
        foreach (var (r, c, _) in sheet.Cells) coords.Add((r, c));
        foreach (var (r, c, _) in sheet.StyledCells) coords.Add((r, c));

        var rows = new List<XElement>();
        int currentRow = -1;
        XElement? rowEl = null;
        foreach (var (r, c) in coords)
        {
            if (r != currentRow)
            {
                currentRow = r;
                rowEl = new XElement(Main + "row", new XAttribute("r", (r + 1).ToString(CultureInfo.InvariantCulture)));
                rows.Add(rowEl);
            }
            int styleIdx = sheet.GetStyle(r, c) is { } st ? styleReg.StyleIndex(st) : 0;
            rowEl!.Add(SerializeCell(sheet, r, c, sheet.GetRaw(r, c), sharedList, sharedIdx, styleIdx));
        }

        string dimension = coords.Count == 0
            ? "A1"
            : "A1:" + SheetRef.ToRef(sheet.MaxRow, sheet.MaxCol);

        var sheetData = new XElement(Main + "sheetData", rows);
        var worksheet = new XElement(Main + "worksheet",
            new XAttribute(XNamespace.Xmlns + "r", RelNs.NamespaceName),
            new XElement(Main + "dimension", new XAttribute("ref", dimension)),
            sheetData);

        var sharedRoot = new XElement(Main + "sst",
            new XAttribute("count", sharedList.Count.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("uniqueCount", sharedList.Count.ToString(CultureInfo.InvariantCulture)),
            sharedList.Select(s => new XElement(Main + "si", new XElement(Main + "t", s))));

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(zip, "[Content_Types].xml", ContentTypesXml);
        WriteEntry(zip, "_rels/.rels", RootRelsXml);
        WriteEntry(zip, "docProps/core.xml", CoreXml);
        WriteEntry(zip, "docProps/app.xml", AppXml);
        WriteEntry(zip, "xl/workbook.xml", WorkbookXml);
        WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
        WriteEntry(zip, "xl/styles.xml", BuildStylesXml(styleReg));
        WriteEntry(zip, "xl/sharedStrings.xml", new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), sharedRoot).ToString(SaveOptions.DisableFormatting));
        WriteEntry(zip, "xl/worksheets/sheet1.xml", new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), worksheet).ToString(SaveOptions.DisableFormatting));
    }

    private static int RegisterShared(string s, List<string> sharedList, Dictionary<string, int> sharedIdx)
    {
        if (!sharedIdx.TryGetValue(s, out int i))
        {
            i = sharedList.Count;
            sharedIdx[s] = i;
            sharedList.Add(s);
        }
        return i;
    }

    private static string Num(double n) => n.ToString("G15", CultureInfo.InvariantCulture);

    private static XElement SerializeCell(Sheet sheet, int r, int c, string raw,
        List<string> sharedList, Dictionary<string, int> sharedIdx, int styleIdx)
    {
        string refName = SheetRef.ToRef(r, c);
        XElement cell;

        if (raw.Length == 0)
        {
            cell = new XElement(Main + "c", new XAttribute("r", refName));
        }
        else if (raw.StartsWith("=", StringComparison.Ordinal))
        {
            var formula = new XElement(Main + "f", raw.Substring(1));
            var val = sheet.GetValue(r, c);
            if (val.IsError)
                cell = new XElement(Main + "c", new XAttribute("r", refName), new XAttribute("t", "e"),
                    formula, new XElement(Main + "v", val.Text));
            else if (val.IsNumber)
                cell = new XElement(Main + "c", new XAttribute("r", refName),
                    formula, new XElement(Main + "v", Num(val.Number)));
            else
                cell = new XElement(Main + "c", new XAttribute("r", refName), new XAttribute("t", "str"),
                    formula, new XElement(Main + "v", val.Display));
        }
        else if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            cell = new XElement(Main + "c", new XAttribute("r", refName), new XElement(Main + "v", Num(n)));
        }
        else if (raw.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || raw.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            cell = new XElement(Main + "c", new XAttribute("r", refName), new XAttribute("t", "b"),
                new XElement(Main + "v", raw.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? "1" : "0"));
        }
        else
        {
            int idx = RegisterShared(raw, sharedList, sharedIdx);
            cell = new XElement(Main + "c", new XAttribute("r", refName), new XAttribute("t", "s"),
                new XElement(Main + "v", idx.ToString(CultureInfo.InvariantCulture)));
        }

        if (styleIdx > 0) cell.Add(new XAttribute("s", styleIdx));
        return cell;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var sw = new StreamWriter(s, new UTF8Encoding(false));
        sw.Write(content);
    }

    // ── styles ─────────────────────────────────────────────────────────────────

    private static readonly Dictionary<int, string> BuiltinNumFmts = new()
    {
        [0] = "General", [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00",
        [9] = "0%", [10] = "0.00%", [11] = "0.00E+00", [14] = "mm-dd-yy",
        [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy", [18] = "h:mm AM/PM",
        [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [22] = "m/d/yy h:mm",
        [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mmss.0", [48] = "##0.0E+0", [49] = "@"
    };

    private static readonly Dictionary<string, int> BuiltinNumFmtIds = BuildNumFmtIds();

    private static Dictionary<string, int> BuildNumFmtIds()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in BuiltinNumFmts) d[kv.Value] = kv.Key;
        return d;
    }

    private sealed class XlsxFont
    {
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public string? Name;
        public double? Size;
        public string? Color;
    }

    private static XlsxFont FontFromStyle(CellStyle st) => new()
    {
        Bold = st.Bold,
        Italic = st.Italic,
        Underline = st.Underline,
        Name = st.FontName ?? "Calibri",
        Size = st.FontSize ?? 11,
        Color = st.FontColor
    };

    private static string? ColorFromX(XElement? color)
    {
        if (color is null) return null;
        string? rgb = color.Attribute("rgb")?.Value;
        if (!string.IsNullOrEmpty(rgb) && rgb.Length >= 6)
            return "#" + rgb.Substring(rgb.Length - 6);
        return null;
    }

    private sealed class XlsxStyleTable
    {
        public readonly List<XlsxFont> Fonts = new();
        public readonly List<string> Fills = new();
        public readonly Dictionary<int, string> NumFmts = new();
        public readonly List<(int NumFmtId, int FontId, int FillId, string? Align)> CellXfs = new();

        public static XlsxStyleTable Load(ZipArchive zip)
        {
            var table = new XlsxStyleTable();
            var entry = zip.GetEntry("xl/styles.xml");
            if (entry is null) return table;
            XDocument xdoc;
            using (var s = entry.Open()) xdoc = XDocument.Load(s);
            var root = xdoc.Root;
            if (root is null) return table;

            foreach (var nf in root.Descendants(Main + "numFmt"))
                if (int.TryParse(nf.Attribute("numFmtId")?.Value, out int id) && nf.Attribute("formatCode")?.Value is { } code)
                    table.NumFmts[id] = code;

            foreach (var font in root.Descendants(Main + "font"))
            {
                var f = new XlsxFont
                {
                    Bold = font.Element(Main + "b") != null,
                    Italic = font.Element(Main + "i") != null,
                    Underline = font.Element(Main + "u") != null && font.Element(Main + "u")?.Attribute("val")?.Value != "none",
                    Name = font.Element(Main + "name")?.Attribute("val")?.Value,
                    Color = ColorFromX(font.Element(Main + "color"))
                };
                if (double.TryParse(font.Element(Main + "sz")?.Attribute("val")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sz))
                    f.Size = sz;
                table.Fonts.Add(f);
            }

            foreach (var fill in root.Descendants(Main + "fill"))
            {
                string color = "";
                var pf = fill.Element(Main + "patternFill");
                if (pf?.Attribute("patternType")?.Value == "solid")
                    color = ColorFromX(pf.Element(Main + "fgColor")) ?? "";
                table.Fills.Add(color);
            }

            foreach (var xf in root.Descendants(Main + "cellXfs").Elements(Main + "xf"))
            {
                int numFmtId = 0, fontId = 0, fillId = 0;
                int.TryParse(xf.Attribute("numFmtId")?.Value, out numFmtId);
                int.TryParse(xf.Attribute("fontId")?.Value, out fontId);
                int.TryParse(xf.Attribute("fillId")?.Value, out fillId);
                string? align = xf.Element(Main + "alignment")?.Attribute("horizontal")?.Value;
                table.CellXfs.Add((numFmtId, fontId, fillId, align));
            }
            return table;
        }

        public CellStyle? StyleFor(int s)
        {
            if (s < 0 || s >= CellXfs.Count) return null;
            var (numFmtId, fontId, fillId, align) = CellXfs[s];
            var st = new CellStyle();

            var font = fontId >= 0 && fontId < Fonts.Count ? Fonts[fontId] : null;
            // Only capture a font when it actually *emphasises*: plain body-font
            // variations (name/size alone) are the sheet's default and would
            // otherwise mark every cell as styled.
            bool fontMeaningful = font != null && (font.Bold || font.Italic || font.Underline || font.Color != null);
            if (fontMeaningful && font != null)
            {
                st.Bold = font.Bold;
                st.Italic = font.Italic;
                st.Underline = font.Underline;
                st.FontName = font.Name;
                st.FontSize = font.Size;
                st.FontColor = font.Color;
            }

            string? fill = fillId >= 0 && fillId < Fills.Count ? Fills[fillId] : null;
            if (fillId > 1 && !string.IsNullOrEmpty(fill)) st.FillColor = fill;

            st.Align = align switch
            {
                "left" => CellHAlign.Left,
                "center" => CellHAlign.Center,
                "right" => CellHAlign.Right,
                _ => CellHAlign.General
            };

            string? nf = NumberFormatFor(numFmtId);
            if (!string.IsNullOrEmpty(nf) && nf != "General" && nf != "@") st.NumberFormat = nf;

            return st.IsDefault ? null : st;
        }

        private string? NumberFormatFor(int id) =>
            BuiltinNumFmts.TryGetValue(id, out var b) ? b : NumFmts.TryGetValue(id, out var c) ? c : null;
    }

    private sealed class CellCoordComparer : IComparer<(int Row, int Col)>
    {
        public static readonly CellCoordComparer Instance = new();
        public int Compare((int Row, int Col) x, (int Row, int Col) y)
        {
            int d = x.Row.CompareTo(y.Row);
            return d != 0 ? d : x.Col.CompareTo(y.Col);
        }
    }

    private sealed class XlsxStyleRegistry
    {
        public readonly List<XlsxFont> Fonts = new() { FontFromStyle(new CellStyle()) };
        public readonly List<string> Fills = new() { "", "gray125" };
        public readonly Dictionary<string, int> NumFmtIds = new(StringComparer.Ordinal);
        public readonly List<(int NumFmtId, int FontId, int FillId, string? Align)> Xfs = new() { (0, 0, 0, null) };
        private readonly Dictionary<string, int> _fontIdx = new();
        private readonly Dictionary<string, int> _fillIdx = new();
        private readonly Dictionary<string, int> _xfIdx = new();

        public int StyleIndex(CellStyle style)
        {
            int numFmtId = RegisterNumFmt(style.NumberFormat);
            bool hasFont = style.Bold || style.Italic || style.Underline ||
                           style.FontName is not null || style.FontSize is not null || style.FontColor is not null;
            int fontId = hasFont ? RegisterFont(FontFromStyle(style)) : 0;
            int fillId = style.FillColor is not null ? RegisterFill(style.FillColor) : 0;
            string? align = style.Align switch
            {
                CellHAlign.Left => "left",
                CellHAlign.Center => "center",
                CellHAlign.Right => "right",
                _ => null
            };
            return RegisterXf(numFmtId, fontId, fillId, align);
        }

        private int RegisterFont(XlsxFont font)
        {
            string key = (font.Bold ? 1 : 0) + "|" + (font.Italic ? 1 : 0) + "|" + (font.Underline ? 1 : 0) + "|" +
                         (font.Name ?? "") + "|" + font.Size?.ToString(CultureInfo.InvariantCulture) + "|" + (font.Color ?? "");
            if (!_fontIdx.TryGetValue(key, out int idx)) { idx = Fonts.Count; _fontIdx[key] = idx; Fonts.Add(font); }
            return idx;
        }

        private int RegisterFill(string? color)
        {
            string c = string.IsNullOrEmpty(color) ? "" : color.TrimStart('#');
            if (!_fillIdx.TryGetValue(c, out int idx)) { idx = Fills.Count; _fillIdx[c] = idx; Fills.Add(c); }
            return idx;
        }

        private int RegisterNumFmt(string? format)
        {
            if (string.IsNullOrEmpty(format)) return 0;
            if (BuiltinNumFmtIds.TryGetValue(format, out int builtin)) return builtin;
            if (!NumFmtIds.TryGetValue(format, out int id)) { id = 164 + NumFmtIds.Count; NumFmtIds[format] = id; }
            return id;
        }

        private int RegisterXf(int numFmtId, int fontId, int fillId, string? align)
        {
            string key = numFmtId + "|" + fontId + "|" + fillId + "|" + (align ?? "");
            if (!_xfIdx.TryGetValue(key, out int idx)) { idx = Xfs.Count; _xfIdx[key] = idx; Xfs.Add((numFmtId, fontId, fillId, align)); }
            return idx;
        }
    }

    private static string BuildStylesXml(XlsxStyleRegistry reg)
    {
        var fonts = new XElement(Main + "fonts", new XAttribute("count", reg.Fonts.Count),
            reg.Fonts.Select(SerializeFont));
        var fills = new XElement(Main + "fills", new XAttribute("count", reg.Fills.Count),
            reg.Fills.Select(SerializeFill));
        var numFmts = new XElement(Main + "numFmts", new XAttribute("count", reg.NumFmtIds.Count),
            reg.NumFmtIds.Select(kv => new XElement(Main + "numFmt",
                new XAttribute("numFmtId", kv.Value), new XAttribute("formatCode", kv.Key))));
        var borders = new XElement(Main + "borders", new XAttribute("count", 1), new XElement(Main + "border"));
        var cellStyleXfs = new XElement(Main + "cellStyleXfs", new XAttribute("count", 1),
            new XElement(Main + "xf",
                new XAttribute("numFmtId", 0), new XAttribute("fontId", 0),
                new XAttribute("fillId", 0), new XAttribute("borderId", 0)));
        var cellXfs = new XElement(Main + "cellXfs", new XAttribute("count", reg.Xfs.Count),
            reg.Xfs.Select(SerializeXf));

        var root = new XElement(Main + "styleSheet", numFmts, fonts, fills, borders, cellStyleXfs, cellXfs);
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).ToString(SaveOptions.DisableFormatting);
    }

    private static XElement SerializeFont(XlsxFont f)
    {
        var el = new XElement(Main + "font");
        if (f.Bold) el.Add(new XElement(Main + "b"));
        if (f.Italic) el.Add(new XElement(Main + "i"));
        if (f.Underline) el.Add(new XElement(Main + "u"));
        if (f.Size is { } sz) el.Add(new XElement(Main + "sz", new XAttribute("val", sz.ToString("0.##", CultureInfo.InvariantCulture))));
        if (!string.IsNullOrEmpty(f.Color)) el.Add(new XElement(Main + "color", new XAttribute("rgb", "FF" + f.Color.TrimStart('#'))));
        if (!string.IsNullOrEmpty(f.Name)) el.Add(new XElement(Main + "name", new XAttribute("val", f.Name)));
        return el;
    }

    private static XElement SerializeFill(string color)
    {
        if (string.IsNullOrEmpty(color))
            return new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "none")));
        if (color == "gray125")
            return new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "gray125")));
        return new XElement(Main + "fill",
            new XElement(Main + "patternFill", new XAttribute("patternType", "solid"),
                new XElement(Main + "fgColor", new XAttribute("rgb", "FF" + color.TrimStart('#')))));
    }

    private static XElement SerializeXf((int NumFmtId, int FontId, int FillId, string? Align) x)
    {
        var el = new XElement(Main + "xf",
            new XAttribute("numFmtId", x.NumFmtId),
            new XAttribute("fontId", x.FontId),
            new XAttribute("fillId", x.FillId),
            new XAttribute("borderId", 0));
        if (x.NumFmtId != 0) el.Add(new XAttribute("applyNumberFormat", 1));
        if (x.FontId != 0) el.Add(new XAttribute("applyFont", 1));
        if (x.FillId != 0) el.Add(new XAttribute("applyFill", 1));
        if (x.Align is { } a)
        {
            el.Add(new XAttribute("applyAlignment", 1));
            el.Add(new XElement(Main + "alignment", new XAttribute("horizontal", a)));
        }
        return el;
    }

    // ── static package parts ──────────────────────────────────────────────────

    private const string ContentTypesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
        "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
        "</Types>";

    private const string RootRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
        "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
        "</Relationships>";

    private const string WorkbookXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    private const string WorkbookRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>" +
        "</Relationships>";

    private const string CoreXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
        "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
        "<dc:creator>AetherCodex</dc:creator></cp:coreProperties>";

    private const string AppXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" " +
        "xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
        "<Application>AetherCodex</Application></Properties>";
}