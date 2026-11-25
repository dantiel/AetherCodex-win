using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AetherCodex.Spreadsheet;

/// <summary>
/// Persistence for spreadsheet documents. Native .aecalc is a JSON package that
/// preserves formulas; .csv / .tsv are value-only delimited interchange formats
/// (delimiter sniffed from the first non-empty line, honouring ';' as well).
/// </summary>
public static class SheetIO
{
    public static bool IsSheetDocument(string path) =>
        IsNative(path) || IsDelimited(path) || IsXlsx(path) || IsOds(path);

    public static bool IsNative(string path) =>
        path.EndsWith(".aecalc", StringComparison.OrdinalIgnoreCase);

    public static bool IsDelimited(string path) =>
        path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase);

    public static bool IsXlsx(string path) =>
        path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    public static bool IsOds(string path) =>
        path.EndsWith(".ods", StringComparison.OrdinalIgnoreCase);

    public static Sheet Load(string path)
    {
        if (IsXlsx(path)) return XlsxFormat.Load(path);
        if (IsOds(path)) return OdsFormat.Load(path);
        if (IsNative(path)) return LoadNative(path);
        if (IsDelimited(path))
        {
            char delim = path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase)
                ? '\t'
                : DetectDelimiter(path);
            return LoadDelimited(path, delim);
        }
        throw new NotSupportedException("Not a spreadsheet file");
    }

    public static void Save(string path, Sheet sheet)
    {
        if (IsXlsx(path)) { XlsxFormat.Save(path, sheet); return; }
        if (IsOds(path)) { OdsFormat.Save(path, sheet); return; }
        if (IsNative(path)) { SaveNative(path, sheet); return; }
        if (IsDelimited(path))
        {
            char delim = path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
            SaveDelimited(path, sheet, delim);
            return;
        }
        throw new NotSupportedException("Not a spreadsheet file");
    }

    // ── native JSON ──
    private sealed class SheetDto
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, string> Cells { get; set; } = new();
        public Dictionary<string, Dictionary<string, string>> Styles { get; set; } = new();
    }

    private static void SaveNative(string path, Sheet sheet)
    {
        var dto = new SheetDto();
        foreach (var (r, c, raw) in sheet.Cells)
            dto.Cells[SheetRef.ToRef(r, c)] = raw;
        foreach (var (r, c, style) in sheet.StyledCells)
            dto.Styles[SheetRef.ToRef(r, c)] = StyleToDict(style);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static Sheet LoadNative(string path)
    {
        var dto = JsonSerializer.Deserialize<SheetDto>(File.ReadAllText(path)) ?? new SheetDto();
        var cells = new List<(int, int, string)>();
        foreach (var kv in dto.Cells)
            if (SheetRef.TryParse(kv.Key, out int r, out int c))
                cells.Add((r, c, kv.Value));
        var sheet = new Sheet();
        sheet.ReplaceAll(cells);

        if (dto.Styles is { } styleMap)
        {
            var styles = new List<(int, int, CellStyle)>();
            foreach (var kv in styleMap)
                if (SheetRef.TryParse(kv.Key, out int r, out int c) && DictToStyle(kv.Value) is { } st)
                    styles.Add((r, c, st));
            sheet.ApplyStyles(styles);
        }
        return sheet;
    }

    private static Dictionary<string, string> StyleToDict(CellStyle st)
    {
        var d = new Dictionary<string, string>();
        if (st.Bold) d["bold"] = "1";
        if (st.Italic) d["italic"] = "1";
        if (st.Underline) d["underline"] = "1";
        if (st.FontName is not null) d["font"] = st.FontName;
        if (st.FontSize is not null) d["size"] = st.FontSize.Value.ToString(CultureInfo.InvariantCulture);
        if (st.FontColor is not null) d["color"] = st.FontColor;
        if (st.FillColor is not null) d["fill"] = st.FillColor;
        if (st.Align != CellHAlign.General) d["align"] = st.Align.ToString();
        if (st.NumberFormat is not null) d["numfmt"] = st.NumberFormat;
        return d;
    }

    private static CellStyle? DictToStyle(Dictionary<string, string> d)
    {
        var st = new CellStyle
        {
            Bold = d.ContainsKey("bold"),
            Italic = d.ContainsKey("italic"),
            Underline = d.ContainsKey("underline")
        };
        if (d.TryGetValue("font", out var font)) st.FontName = font;
        if (d.TryGetValue("size", out var size) && double.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out var sz)) st.FontSize = sz;
        if (d.TryGetValue("color", out var color)) st.FontColor = color;
        if (d.TryGetValue("fill", out var fill)) st.FillColor = fill;
        if (d.TryGetValue("align", out var align) && Enum.TryParse<CellHAlign>(align, out var a)) st.Align = a;
        if (d.TryGetValue("numfmt", out var nf)) st.NumberFormat = nf;
        return st.IsDefault ? null : st;
    }

    // ── delimited ──
    private static char DetectDelimiter(string path)
    {
        using var r = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line = null;
        while ((line = r.ReadLine()) is not null && line.Trim().Length == 0) { }
        if (line is null) return ',';
        int commas = line.Count(ch => ch == ',');
        int semis = line.Count(ch => ch == ';');
        int tabs = line.Count(ch => ch == '\t');
        int m = Math.Max(commas, Math.Max(semis, tabs));
        if (m == tabs) return '\t';
        if (m == semis) return ';';
        return ',';
    }

    private static Sheet LoadDelimited(string path, char delim)
    {
        var rows = ParseDelimited(File.ReadAllText(path), delim);
        var cells = new List<(int, int, string)>();
        for (int r = 0; r < rows.Count; r++)
        {
            var cols = rows[r];
            for (int c = 0; c < cols.Length; c++)
                if (!string.IsNullOrEmpty(cols[c]))
                    cells.Add((r, c, cols[c]));
        }
        var sheet = new Sheet();
        sheet.ReplaceAll(cells);
        return sheet;
    }

    private static void SaveDelimited(string path, Sheet sheet, char delim)
    {
        int rows = sheet.MaxRow + 1;
        int cols = sheet.MaxCol + 1;
        var sb = new StringBuilder();
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (c > 0) sb.Append(delim);
                sb.Append(EscapeField(sheet.GetValue(r, c).Display, delim));
            }
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string EscapeField(string value, char delim)
    {
        if (value.IndexOfAny(new[] { delim, '"', '\r', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static List<string[]> ParseDelimited(string text, char delim)
    {
        var rows = new List<string[]>();
        var field = new StringBuilder();
        var row = new List<string>();
        bool inQuotes = false;
        int i = 0, n = text.Length;

        while (i < n)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < n && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                field.Append(c); i++; continue;
            }

            if (c == '"') { inQuotes = true; i++; continue; }
            if (c == delim) { row.Add(field.ToString()); field.Clear(); i++; continue; }
            if (c == '\r') { i++; continue; }
            if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row.ToArray()); row.Clear(); i++; continue; }
            field.Append(c); i++;
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }
}