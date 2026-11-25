using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AetherCodex.Spreadsheet;

/// <summary>
/// OpenDocument Spreadsheet (.ods) read/write against the zip package using only
/// the BCL. The first table round-trips; cell formulas are translated between
/// ODF OpenFormula syntax (<c>of:=SUM([.A1:.A10])</c>) and the internal A1 syntax
/// (<c>=SUM(A1:A10)</c>), and literal values keep their value-type.
/// </summary>
internal static class OdsFormat
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Number = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";

    public const string MimeType = "application/vnd.oasis.opendocument.spreadsheet";

    // ── Read ───────────────────────────────────────────────────────────────────

    public static Sheet Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.xml");
        if (entry is null) return new Sheet();

        XDocument xdoc;
        using (var s = entry.Open()) xdoc = XDocument.Load(s);

        var styleTable = OdsStyleTable.Load(xdoc);
        var table = xdoc.Root!.Descendants(Table + "table").FirstOrDefault();
        var cells = new List<(int Row, int Col, string Raw)>();
        var styles = new List<(int Row, int Col, CellStyle Style)>();
        int r = 0;

        foreach (var row in table?.Elements(Table + "table-row") ?? Enumerable.Empty<XElement>())
        {
            int rowRepeat = 1;
            int.TryParse(row.Attribute(Table + "number-rows-repeated")?.Value, out rowRepeat);
            if (rowRepeat < 1) rowRepeat = 1;

            for (int rr = 0; rr < rowRepeat; rr++)
            {
                int c = 0;
                foreach (var cell in row.Elements().Where(e => e.Name == Table + "table-cell" || e.Name == Table + "covered-table-cell"))
                {
                    int colRepeat = 1;
                    int.TryParse(cell.Attribute(Table + "number-columns-repeated")?.Value, out colRepeat);
                    if (colRepeat < 1) colRepeat = 1;

                    string raw = CellRaw(cell);
                    var st = styleTable.Resolve(cell.Attribute(Table + "style-name")?.Value);
                    for (int cc = 0; cc < colRepeat; cc++)
                    {
                        if (raw.Length > 0) cells.Add((r, c, raw));
                        if (st is not null) styles.Add((r, c, st));
                        c++;
                    }
                }
                r++;
            }
        }

        var sheet = new Sheet();
        sheet.ReplaceAll(cells);
        sheet.ApplyStyles(styles);
        return sheet;
    }

    private static string CellRaw(XElement cell)
    {
        var formula = cell.Attribute(Table + "formula");
        if (formula is not null) return "=" + OdfFormulaToA1(formula.Value);

        string? vt = cell.Attribute(Office + "value-type")?.Value;
        switch (vt)
        {
            case "float":
            case "percentage":
            case "currency":
                return cell.Attribute(Office + "value")?.Value ?? "";
            case "boolean":
                return cell.Attribute(Office + "boolean-value")?.Value == "true" ? "TRUE" : "FALSE";
            case "string":
                return TextContent(cell);
            case "date":
                return cell.Attribute(Office + "date-value")?.Value ?? "";
            case "time":
                return cell.Attribute(Office + "time-value")?.Value ?? "";
            default:
                return TextContent(cell);
        }
    }

    private static string TextContent(XElement cell)
    {
        var p = cell.Element(Text + "p");
        if (p is null) return "";
        var sb = new StringBuilder();
        foreach (var node in p.DescendantNodes())
        {
            switch (node)
            {
                case XText t: sb.Append(t.Value); break;
                case XElement e when e.Name == Text + "s":
                    int count = 1;
                    int.TryParse(e.Attribute(Text + "c")?.Value, out count);
                    sb.Append(' ', Math.Max(1, count));
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>OpenFormula → A1. Strips the <c>of:=</c> prefix, unwraps <c>[.A1]</c>
    /// bracket references and converts the ODF argument separator ';' to ','.</summary>
    private static string OdfFormulaToA1(string formula)
    {
        int eq = formula.IndexOf('=');
        string f = eq >= 0 ? formula[(eq + 1)..] : formula;

        f = f.Replace(";", ",");
        f = BracketRx.Replace(f, m => InnerRef(m.Groups[1].Value));
        f = Regex.Replace(f, @"\bCONCATENATE\b", "CONCAT", RegexOptions.IgnoreCase);
        return f;
    }

    private static string InnerRef(string inner)
    {
        inner = inner.Trim();
        int dot = inner.LastIndexOf('.');
        return dot >= 0 ? inner[(dot + 1)..] : inner;
    }

    private static readonly Regex BracketRx = new(@"\[([^\]]+)\]", RegexOptions.Compiled);

    // ── styles ─────────────────────────────────────────────────────────────────

    private static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrEmpty(color) || color.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return null;
        return color.StartsWith("#") ? color : "#" + color;
    }

    private sealed class OdsStyleTable
    {
        public readonly Dictionary<string, CellStyle> CellStyles = new();
        public readonly Dictionary<string, string?> Parents = new();
        public readonly Dictionary<string, string> DataStyles = new();

        public static OdsStyleTable Load(XDocument xdoc)
        {
            var t = new OdsStyleTable();

            foreach (var el in xdoc.Root!.Descendants().Where(e => e.Name.Namespace == Number))
            {
                if (el.Attribute(Style + "name")?.Value is not { } name) continue;
                if (DataStyleToFormat(el) is { } fmt) t.DataStyles[name] = fmt;
            }

            foreach (var el in xdoc.Root.Descendants(Style + "style").Where(e => e.Attribute(Style + "family")?.Value == "table-cell"))
            {
                if (el.Attribute(Style + "name")?.Value is not { } name) continue;
                if (ParseCellStyle(t, el) is { } cs) t.CellStyles[name] = cs;
                t.Parents[name] = el.Attribute(Style + "parent-style-name")?.Value;
            }
            return t;
        }

        public CellStyle? Resolve(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // Collect the chain own → root, then merge root → own so the cell's
            // own attributes win over inherited ones.
            var chain = new List<CellStyle>();
            var visited = new HashSet<string>();
            string? cur = name;
            int depth = 0;
            while (cur is not null && depth++ < 10 && visited.Add(cur))
            {
                if (!CellStyles.TryGetValue(cur, out var st)) break;
                chain.Add(st);
                cur = Parents.TryGetValue(cur, out var p) ? p : null;
            }
            if (chain.Count == 0) return null;

            var baseline = chain[^1];
            var merged = new CellStyle();
            for (int i = chain.Count - 1; i >= 0; i--) Merge(merged, chain[i]);

            // Strip the baseline ("Default") font identity so every cell isn't
            // marked styled just for inheriting the document's body font.
            if (merged.FontName == baseline.FontName) merged.FontName = null;
            if (merged.FontSize == baseline.FontSize) merged.FontSize = null;
            if (merged.FontColor == baseline.FontColor) merged.FontColor = null;

            return merged.IsDefault ? null : merged;
        }

        private static void Merge(CellStyle dst, CellStyle src)
        {
            if (src.Bold) dst.Bold = true;
            if (src.Italic) dst.Italic = true;
            if (src.Underline) dst.Underline = true;
            if (src.FontName is not null) dst.FontName = src.FontName;
            if (src.FontSize is not null) dst.FontSize = src.FontSize;
            if (src.FontColor is not null) dst.FontColor = src.FontColor;
            if (src.FillColor is not null) dst.FillColor = src.FillColor;
            if (src.Align != CellHAlign.General) dst.Align = src.Align;
            if (src.NumberFormat is not null) dst.NumberFormat = src.NumberFormat;
        }

        private static CellStyle? ParseCellStyle(OdsStyleTable t, XElement el)
        {
            var st = new CellStyle();

            var tp = el.Element(Style + "text-properties");
            if (tp is not null)
            {
                string? w = tp.Attribute(Fo + "font-weight")?.Value;
                if (w is not null && (w == "bold" || (int.TryParse(w, out int wn) && wn >= 600))) st.Bold = true;
                if (tp.Attribute(Fo + "font-style")?.Value == "italic") st.Italic = true;
                string? ul = tp.Attribute(Style + "text-underline-style")?.Value;
                if (ul is not null && ul != "none") st.Underline = true;
                st.FontColor = NormalizeColor(tp.Attribute(Fo + "color")?.Value);
                st.FontName = tp.Attribute(Style + "font-name")?.Value;
                if (tp.Attribute(Fo + "font-size")?.Value is { } fs &&
                    double.TryParse(fs.TrimEnd('p', 't'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fsv))
                    st.FontSize = fsv;
            }

            var tcp = el.Element(Style + "table-cell-properties");
            if (tcp is not null) st.FillColor = NormalizeColor(tcp.Attribute(Fo + "background-color")?.Value);

            var pp = el.Element(Style + "paragraph-properties");
            if (pp is not null)
            {
                st.Align = pp.Attribute(Fo + "text-align")?.Value switch
                {
                    "left" or "start" => CellHAlign.Left,
                    "center" => CellHAlign.Center,
                    "right" or "end" => CellHAlign.Right,
                    _ => CellHAlign.General
                };
            }

            if (el.Attribute(Style + "data-style-name")?.Value is { } dsn && t.DataStyles.TryGetValue(dsn, out var fmt))
                st.NumberFormat = fmt;

            return st.IsDefault ? null : st;
        }

        private static string? DataStyleToFormat(XElement el)
        {
            switch (el.Name.LocalName)
            {
                case "number-style": return NumberStyleToFormat(el, "");
                case "percentage-style": return NumberStyleToFormat(el, "%");
                case "currency-style": return CurrencyStyleToFormat(el);
                case "date-style": return DateStyleToFormat(el);
                case "time-style": return DateStyleToFormat(el);
                default: return null;
            }
        }

        private static string? NumberStyleToFormat(XElement el, string suffix)
        {
            var num = el.Element(Number + "number");
            if (num is null) return null;
            int decimals = 0;
            int.TryParse(num.Attribute(Number + "decimal-places")?.Value, out decimals);
            bool grouping = num.Attribute(Number + "grouping")?.Value == "true";
            string prefix = grouping ? "#,##0" : "0";
            string fmt = decimals > 0 ? prefix + "." + new string('0', decimals) : prefix;
            return fmt + suffix;
        }

        private static string? CurrencyStyleToFormat(XElement el)
        {
            string? sym = el.Elements(Number + "currency-symbol").Select(e => e.Value).FirstOrDefault();
            int decimals = 0;
            if (el.Element(Number + "number") is { } num)
                int.TryParse(num.Attribute(Number + "decimal-places")?.Value, out decimals);
            return (sym ?? "") + "#,##0" + (decimals > 0 ? "." + new string('0', decimals) : "");
        }

        private static string? DateStyleToFormat(XElement el)
        {
            var sb = new StringBuilder();
            foreach (var node in el.Elements())
            {
                switch (node.Name.LocalName)
                {
                    case "year": sb.Append(node.Attribute(Number + "style")?.Value == "long" ? "yyyy" : "yy"); break;
                    case "month": sb.Append(node.Attribute(Number + "style")?.Value == "long" ? "mmmm" : node.Attribute(Number + "textual")?.Value == "true" ? "mmm" : "mm"); break;
                    case "day": sb.Append(node.Attribute(Number + "style")?.Value == "long" ? "dddd" : "dd"); break;
                    case "hours": sb.Append("hh"); break;
                    case "minutes": sb.Append("mm"); break;
                    case "seconds": sb.Append("ss"); break;
                    case "text": sb.Append(node.Value); break;
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
    }

    private sealed class OdsStyleRegistry
    {
        public readonly List<CellStyle> Styles = new();
        public readonly Dictionary<string, string> StyleNames = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> DataStyleNames = new(StringComparer.Ordinal);
        public readonly List<(string Format, string Kind)> DataStyles = new();

        public string StyleNameFor(CellStyle st)
        {
            if (StyleNames.TryGetValue(st.Key, out var existing))
                return existing;

            string name = "ce" + (StyleNames.Count + 1);
            StyleNames[st.Key] = name;
            Styles.Add(st);
            if (!string.IsNullOrEmpty(st.NumberFormat)) RegisterDataStyle(st.NumberFormat);
            return name;
        }

        private void RegisterDataStyle(string fmt)
        {
            if (DataStyleNames.ContainsKey(fmt)) return;
            string name = "N" + (DataStyleNames.Count + 1);
            DataStyleNames[fmt] = name;
            DataStyles.Add((fmt, DataStyleKind(fmt)));
        }

        private static string DataStyleKind(string fmt)
        {
            if (fmt.Contains('%')) return "percentage";
            if (fmt.Contains('$') || fmt.Contains('€') || fmt.Contains('£') || fmt.Contains('¥')) return "currency";
            if (Regex.IsMatch(fmt, @"[ymdhs]", RegexOptions.IgnoreCase) && !fmt.Contains('#') && !fmt.Contains('0')) return "date";
            return "number";
        }
    }

    private static XElement BuildAutomaticStyles(OdsStyleRegistry reg)
    {
        var auto = new XElement(Office + "automatic-styles");
        foreach (var st in reg.Styles)
        {
            string name = reg.StyleNames[st.Key];
            var styleEl = new XElement(Style + "style",
                new XAttribute(Style + "name", name),
                new XAttribute(Style + "family", "table-cell"));
            if (!string.IsNullOrEmpty(st.NumberFormat))
                styleEl.Add(new XAttribute(Style + "data-style-name", reg.DataStyleNames[st.NumberFormat]));

            var tp = new XElement(Style + "text-properties");
            if (st.Bold) tp.Add(new XAttribute(Fo + "font-weight", "bold"));
            if (st.Italic) tp.Add(new XAttribute(Fo + "font-style", "italic"));
            if (st.Underline) tp.Add(new XAttribute(Style + "text-underline-style", "solid"));
            if (!string.IsNullOrEmpty(st.FontColor)) tp.Add(new XAttribute(Fo + "color", st.FontColor));
            if (!string.IsNullOrEmpty(st.FontName)) tp.Add(new XAttribute(Style + "font-name", st.FontName));
            if (st.FontSize is { } fs) tp.Add(new XAttribute(Fo + "font-size", fs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "pt"));
            if (tp.HasAttributes) styleEl.Add(tp);

            if (!string.IsNullOrEmpty(st.FillColor))
                styleEl.Add(new XElement(Style + "table-cell-properties", new XAttribute(Fo + "background-color", st.FillColor)));

            if (st.Align != CellHAlign.General)
            {
                string ta = st.Align switch { CellHAlign.Left => "left", CellHAlign.Center => "center", CellHAlign.Right => "right", _ => "left" };
                styleEl.Add(new XElement(Style + "paragraph-properties", new XAttribute(Fo + "text-align", ta)));
            }

            auto.Add(styleEl);
        }

        foreach (var (fmt, kind) in reg.DataStyles)
            auto.Add(DataStyleElement(reg.DataStyleNames[fmt], kind, fmt));
        return auto;
    }

    private static XElement DataStyleElement(string name, string kind, string fmt)
    {
        switch (kind)
        {
            case "percentage":
                return new XElement(Number + "percentage-style", new XAttribute(Style + "name", name),
                    NumberElement(fmt), new XElement(Number + "text", "%"));
            case "currency":
                return new XElement(Number + "currency-style", new XAttribute(Style + "name", name),
                    new XElement(Number + "currency-symbol", CurrencySymbol(fmt)),
                    NumberElement(fmt));
            case "date":
                return DateStyleElement(name, fmt, fmt.Contains(':') && !fmt.Contains('y') && !fmt.Contains('d') && !fmt.Contains('M'));
            default:
                return new XElement(Number + "number-style", new XAttribute(Style + "name", name),
                    NumberElement(fmt));
        }
    }

    private static XElement NumberElement(string fmt)
    {
        int decimals = 0;
        bool grouping = fmt.Contains(',');
        int dot = fmt.IndexOf('.');
        if (dot >= 0)
            for (int j = dot + 1; j < fmt.Length && (fmt[j] == '0' || fmt[j] == '#'); j++) decimals++;
        var num = new XElement(Number + "number",
            new XAttribute(Number + "decimal-places", decimals),
            new XAttribute(Number + "min-integer-digits", 1));
        if (grouping) num.Add(new XAttribute(Number + "grouping", "true"));
        return num;
    }

    private static string CurrencySymbol(string fmt)
    {
        foreach (var sym in new[] { "€", "£", "¥", "$" })
            if (fmt.Contains(sym)) return sym;
        return "";
    }

    private static XElement DateStyleElement(string name, string fmt, bool isTime)
    {
        var el = new XElement(Number + (isTime ? "time-style" : "date-style"), new XAttribute(Style + "name", name));
        int i = 0;
        while (i < fmt.Length)
        {
            char ch = fmt[i];
            if (char.IsLetter(ch))
            {
                int run = 1;
                while (i + run < fmt.Length && fmt[i + run] == ch) run++;
                string token = fmt.Substring(i, run).ToLowerInvariant();
                XElement? part = token switch
                {
                    "yyyy" => new XElement(Number + "year", new XAttribute(Number + "style", "long")),
                    "yy" => new XElement(Number + "year"),
                    "mmmm" => new XElement(Number + "month", new XAttribute(Number + "style", "long")),
                    "mmm" => new XElement(Number + "month", new XAttribute(Number + "textual", "true")),
                    "mm" => new XElement(Number + "month"),
                    "dd" => new XElement(Number + "day"),
                    "d" => new XElement(Number + "day"),
                    "hh" => new XElement(Number + "hours"),
                    "h" => new XElement(Number + "hours"),
                    "ss" => new XElement(Number + "seconds"),
                    "s" => new XElement(Number + "seconds"),
                    _ => null
                };
                if (part is not null) { el.Add(part); i += run; continue; }
            }
            el.Add(new XElement(Number + "text", ch.ToString()));
            i++;
        }
        return el;
    }

    // ── Write ──────────────────────────────────────────────────────────────────

    public static void Save(string path, Sheet sheet)
    {
        var styleReg = new OdsStyleRegistry();

        var coords = new List<(int Row, int Col)>();
        var seen = new HashSet<(int Row, int Col)>();
        foreach (var (r, c, _) in sheet.Cells) { if (seen.Add((r, c))) coords.Add((r, c)); }
        foreach (var (r, c, _) in sheet.StyledCells) { if (seen.Add((r, c))) coords.Add((r, c)); }
        coords.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));

        var rows = new List<XElement>();
        int currentRow = -1;
        var rowCells = new List<XElement>();
        int prevCol = -1;

        foreach (var (r, c) in coords)
        {
            if (r != currentRow)
            {
                if (currentRow >= 0) rows.Add(new XElement(Table + "table-row", rowCells));
                currentRow = r;
                rowCells = new List<XElement>();
                prevCol = -1;
            }

            // ODF has no explicit cell refs — skipped columns must be marked as
            // repeated empty cells or the gap collapses on reload.
            int gap = c - prevCol - 1;
            if (gap > 0)
                rowCells.Add(new XElement(Table + "table-cell", new XAttribute(Table + "number-columns-repeated", gap)));

            string raw = sheet.GetRaw(r, c);
            string? styleName = sheet.GetStyle(r, c) is { } st ? styleReg.StyleNameFor(st) : null;
            rowCells.Add(SerializeCell(sheet, r, c, raw, styleName));
            prevCol = c;
        }
        if (currentRow >= 0) rows.Add(new XElement(Table + "table-row", rowCells));

        var table = new XElement(Table + "table",
            new XAttribute(Table + "name", "Sheet1"),
            rows);

        var spreadsheet = new XElement(Office + "spreadsheet", table);
        var body = new XElement(Office + "body", spreadsheet);
        var autoStyles = BuildAutomaticStyles(styleReg);

        var content = new List<object>();
        if (autoStyles.HasElements) content.Add(autoStyles);
        content.Add(body);

        var root = new XElement(Office + "document-content",
            new XAttribute(Office + "version", "1.2"),
            new XAttribute(XNamespace.Xmlns + "office", Office.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "table", Table.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "text", Text.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "style", Style.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "fo", Fo.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "number", Number.NamespaceName),
            content.ToArray());

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        // mimetype MUST be first, uncompressed, per ODF spec.
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var ms = mime.Open())
        {
            var bytes = Encoding.ASCII.GetBytes(MimeType);
            ms.Write(bytes, 0, bytes.Length);
        }
        WriteEntry(zip, "content.xml", new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).ToString(SaveOptions.DisableFormatting));
        WriteEntry(zip, "META-INF/manifest.xml", ManifestXml);
    }

    private static XElement SerializeCell(Sheet sheet, int r, int c, string raw, string? styleName)
    {
        var display = new XElement(Text + "p", sheet.GetValue(r, c).Display);
        XElement cell;

        if (raw.StartsWith("=", StringComparison.Ordinal))
        {
            var val = sheet.GetValue(r, c);
            if (val.IsNumber)
                cell = new XElement(Table + "table-cell",
                    new XAttribute(Table + "formula", A1ToOdfFormula(raw)),
                    new XAttribute(Office + "value-type", "float"),
                    new XAttribute(Office + "value", val.Number.ToString("G15", System.Globalization.CultureInfo.InvariantCulture)),
                    display);
            else
                cell = new XElement(Table + "table-cell",
                    new XAttribute(Table + "formula", A1ToOdfFormula(raw)),
                    new XAttribute(Office + "value-type", "string"),
                    new XElement(Text + "p", val.Display));
        }
        else if (double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            cell = new XElement(Table + "table-cell",
                new XAttribute(Office + "value-type", "float"),
                new XAttribute(Office + "value", n.ToString("G15", System.Globalization.CultureInfo.InvariantCulture)),
                display);
        else if (raw.Length == 0)
            cell = new XElement(Table + "table-cell");
        else
            cell = new XElement(Table + "table-cell",
                new XAttribute(Office + "value-type", "string"),
                new XElement(Text + "p", raw));

        if (styleName is not null) cell.Add(new XAttribute(Table + "style-name", styleName));
        return cell;
    }

    /// <summary>A1 → OpenFormula. Prefixes <c>of:=</c>, wraps cell/range references in
    /// <c>[.A1]</c> brackets, maps CONCAT → CONCATENATE and ',' → ';'.</summary>
    private static string A1ToOdfFormula(string raw)
    {
        string f = raw.Substring(1);
        f = Regex.Replace(f, @"\bCONCAT\b", "CONCATENATE", RegexOptions.IgnoreCase);
        f = RefRx.Replace(f, m => "[" + (m.Value.StartsWith(".", StringComparison.Ordinal) ? m.Value : "." + m.Value) + "]");
        f = f.Replace(",", ";");
        return "of:=" + f;
    }

    private static readonly Regex RefRx = new(
        @"\$?[A-Za-z]{1,3}\$?\d+(?:\s*:\s*\$?[A-Za-z]{1,3}\$?\d+)?",
        RegexOptions.Compiled);

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var sw = new StreamWriter(s, new UTF8Encoding(false));
        sw.Write(content);
    }

    private const string ManifestXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">" +
        "<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.spreadsheet\"/>" +
        "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>" +
        "</manifest:manifest>";
}