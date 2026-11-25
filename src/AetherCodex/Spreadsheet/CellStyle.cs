using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AetherCodex.Spreadsheet;

/// <summary>Horizontal alignment of a cell's content (General = auto by value type).</summary>
public enum CellHAlign
{
    General,
    Left,
    Center,
    Right
}

/// <summary>
/// Per-cell visual style. Colour values are kept as <c>#RRGGBB</c> strings so the model
/// stays WPF-free; the editor converts them to brushes. <see cref="IsDefault"/> is true
/// when nothing is set, which is how writers decide whether to emit a style at all.
/// </summary>
public sealed class CellStyle
{
    public bool Bold;
    public bool Italic;
    public bool Underline;
    public string? FontName;
    public double? FontSize;      // points
    public string? FontColor;     // #RRGGBB
    public string? FillColor;     // #RRGGBB
    public CellHAlign Align = CellHAlign.General;
    public string? NumberFormat;  // Excel-style format code, e.g. "0.00", "0%", "#,##0.00"

    public CellStyle Clone() => (CellStyle)MemberwiseClone();

    public bool IsDefault =>
        !Bold && !Italic && !Underline &&
        FontName is null && FontSize is null &&
        FontColor is null && FillColor is null &&
        Align == CellHAlign.General &&
        string.IsNullOrEmpty(NumberFormat);

    /// <summary>Canonical identity string used to deduplicate styles when writing.</summary>
    public string Key
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Bold ? 'B' : '-');
            sb.Append(Italic ? 'I' : '-');
            sb.Append(Underline ? 'U' : '-');
            sb.Append('|').Append(FontName ?? "-");
            sb.Append('|').Append(FontSize?.ToString(CultureInfo.InvariantCulture) ?? "-");
            sb.Append('|').Append(FontColor ?? "-");
            sb.Append('|').Append(FillColor ?? "-");
            sb.Append('|').Append((int)Align);
            sb.Append('|').Append(NumberFormat ?? "-");
            return sb.ToString();
        }
    }
}

/// <summary>
/// Best-effort Excel-style number-format rendering for the editor. Handles percentages,
/// currency symbols, thousands grouping, fixed decimals and a practical subset of date
/// tokens (yyyy/yy/mmmm/mmm/mm/m/dd/d/hh/h/ss) against OLE-Automation serial dates.
/// Anything it cannot interpret falls back to the raw <c>G15</c> string.
/// </summary>
internal static class NumberFormatEngine
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Format(CellValue value, string? format)
    {
        if (!value.IsNumber) return value.Display;
        double n = value.Number;
        if (string.IsNullOrEmpty(format) || format.Equals("General", StringComparison.OrdinalIgnoreCase) || format == "@")
            return value.Display;

        if (LooksLikeDate(format))
            return FormatDate(n, format);
        return FormatNumeric(n, format);
    }

    private static bool LooksLikeDate(string f)
    {
        if (f.Contains('%') || f.Contains('#') || f.Contains('0')) return false;
        return Regex.IsMatch(f, @"(^|[^A-Za-z])(yyyy|yy|mmmm|mmm|mm|m|dddd|ddd|dd|d|hh|h|ss|s)([^A-Za-z]|$)", RegexOptions.IgnoreCase);
    }

    private static string FormatDate(double serial, string f)
    {
        DateTime dt;
        try { dt = DateTime.FromOADate(serial); }
        catch { return serial.ToString("G15", Inv); }

        bool twelve = f.Contains("AM/PM", StringComparison.OrdinalIgnoreCase);
        int hour = dt.Hour;
        string ampm = hour < 12 ? "AM" : "PM";
        if (twelve) hour = hour % 12 == 0 ? 12 : hour % 12;

        var sb = new StringBuilder();
        int i = 0;
        while (i < f.Length)
        {
            char ch = f[i];
            if (char.IsLetter(ch))
            {
                int run = 1;
                while (i + run < f.Length && f[i + run] == ch) run++;
                string token = f.Substring(i, run).ToLowerInvariant();
                string? rep = token switch
                {
                    "yyyy" => dt.Year.ToString("D4", Inv),
                    "yy" => (dt.Year % 100).ToString("D2", Inv),
                    "mmmm" => dt.ToString("MMMM", Inv),
                    "mmm" => dt.ToString("MMM", Inv),
                    "mm" => dt.Month.ToString("D2", Inv),
                    "m" => dt.Month.ToString(Inv),
                    "dddd" => dt.ToString("dddd", Inv),
                    "ddd" => dt.ToString("ddd", Inv),
                    "dd" => dt.Day.ToString("D2", Inv),
                    "d" => dt.Day.ToString(Inv),
                    "hh" => hour.ToString("D2", Inv),
                    "h" => hour.ToString(Inv),
                    "ss" => dt.Second.ToString("D2", Inv),
                    "s" => dt.Second.ToString(Inv),
                    _ => null
                };
                if (rep is not null) { sb.Append(rep); i += run; continue; }
            }
            // "AM/PM" and "A/P" literals
            if (f.Substring(i).StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase)) { sb.Append(ampm); i += 5; continue; }
            if (f.Substring(i).StartsWith("A/P", StringComparison.OrdinalIgnoreCase)) { sb.Append(ampm[0]); i += 3; continue; }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    private static string FormatNumeric(double n, string f)
    {
        bool percent = f.Contains('%');
        double v = percent ? n * 100.0 : n;
        bool negative = v < 0;
        double av = Math.Abs(v);

        string currency = ExtractCurrency(f);
        int decimals = DecimalPlaces(f);
        bool thousands = f.Contains(',');
        bool parens = f.Contains('(') && f.Contains(')');

        string body = av.ToString("F" + decimals, Inv);
        if (thousands) body = InsertThousands(body);

        string result = currency + body + (percent ? "%" : "");
        if (negative && parens) result = "(" + result + ")";
        else if (negative) result = "-" + result;
        return result;
    }

    private static string ExtractCurrency(string f)
    {
        foreach (var sym in new[] { "€", "£", "¥", "$" })
            if (f.Contains(sym)) return sym;
        return "";
    }

    private static int DecimalPlaces(string f)
    {
        // find the first '.' not inside quotes; count '0'/'#' after it
        bool inQuote = false;
        for (int i = 0; i < f.Length; i++)
        {
            char c = f[i];
            if (c == '"') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (c == '.')
            {
                int count = 0;
                for (int j = i + 1; j < f.Length; j++)
                {
                    if (f[j] == '0' || f[j] == '#') count++;
                    else break;
                }
                return count;
            }
        }
        return 0;
    }

    private static string InsertThousands(string body)
    {
        int dot = body.IndexOf('.');
        string intPart = dot >= 0 ? body[..dot] : body;
        string frac = dot >= 0 ? body[dot..] : "";
        var sb = new StringBuilder();
        int n = intPart.Length;
        for (int i = 0; i < n; i++)
        {
            if (i > 0 && (n - i) % 3 == 0) sb.Append(',');
            sb.Append(intPart[i]);
        }
        return sb + frac;
    }
}
