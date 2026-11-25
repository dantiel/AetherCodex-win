using System.Globalization;
using System.Text;

namespace AetherCodex.Spreadsheet;

/// <summary>The computed value of a cell: a number, text, empty, or a #error.</summary>
public readonly struct CellValue
{
    public enum Kind { Empty, Number, Text, Error }

    public Kind Type { get; }
    public double Number { get; }
    public string Text { get; }

    public bool IsEmpty => Type == Kind.Empty;
    public bool IsNumber => Type == Kind.Number;
    public bool IsError => Type == Kind.Error;

    private CellValue(Kind type, double number, string text)
    {
        Type = type;
        Number = number;
        Text = text;
    }

    public static readonly CellValue Empty = new(Kind.Empty, 0, "");

    public static CellValue OfNumber(double n)
    {
        if (double.IsNaN(n)) return OfError("#NUM!");
        if (double.IsInfinity(n)) return OfError("#NUM!");
        return new CellValue(Kind.Number, n, n.ToString("G15", CultureInfo.InvariantCulture));
    }

    public static CellValue OfText(string s) => new(Kind.Text, 0, s);
    public static CellValue OfError(string msg) => new(Kind.Error, 0, msg);

    public string Display => Type == Kind.Empty ? "" : Text;
}

/// <summary>Column-letter ↔ index conversion for A1-style references.</summary>
public static class SheetRef
{
    public static string ColumnLabel(int col) // 0-based → "A", "B", …, "AA"
    {
        int c = col + 1;
        var sb = new StringBuilder();
        while (c > 0)
        {
            int rem = (c - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            c = (c - 1) / 26;
        }
        return sb.ToString();
    }

    public static string ToRef(int row, int col) => ColumnLabel(col) + (row + 1);

    public static bool TryParse(string s, out int row, out int col)
    {
        row = -1;
        col = -1;
        if (string.IsNullOrEmpty(s)) return false;

        string t = s.Replace("$", "");
        int i = 0;
        while (i < t.Length && char.IsLetter(t[i])) i++;
        if (i == 0 || i >= t.Length) return false;
        int colEnd = i;
        while (i < t.Length && char.IsDigit(t[i])) i++;
        if (i != t.Length) return false;

        int c = 0;
        for (int j = 0; j < colEnd; j++)
        {
            c = c * 26 + (char.ToUpperInvariant(t[j]) - 'A' + 1);
            if (c > 16384) return false; // XFD — beyond Excel's grid, treat as invalid
        }
        col = c - 1;

        if (!int.TryParse(t.Substring(colEnd), out int r1)) return false;
        row = r1 - 1;
        return row >= 0 && col >= 0;
    }
}

/// <summary>
/// Sparse spreadsheet model. Cells store their raw source — a literal or a formula
/// (leading '='); computed values are memoized and invalidated on any write. Cycle
/// detection lives here so self- and mutually-referential formulas degrade to #CYCLE!.
/// </summary>
public sealed class Sheet
{
    private readonly Dictionary<(int Row, int Col), string> _raw = new();
    private readonly Dictionary<(int Row, int Col), CellStyle> _styles = new();
    private readonly Dictionary<(int Row, int Col), CellValue> _cache = new();
    private readonly HashSet<(int Row, int Col)> _computing = new();

    /// <summary>Fired after every mutation (single-cell edit or bulk replace).</summary>
    public event Action? Changed;

    public int MaxRow { get; private set; } = -1;
    public int MaxCol { get; private set; } = -1;

    public IEnumerable<(int Row, int Col, string Raw)> Cells =>
        _raw.Select(kv => (kv.Key.Row, kv.Key.Col, kv.Value));

    public IEnumerable<(int Row, int Col, CellStyle Style)> StyledCells =>
        _styles.Select(kv => (kv.Key.Row, kv.Key.Col, kv.Value));

    public string GetRaw(int row, int col) =>
        _raw.TryGetValue((row, col), out var s) ? s : "";

    public CellStyle? GetStyle(int row, int col) =>
        _styles.TryGetValue((row, col), out var s) ? s : null;

    public void SetRaw(int row, int col, string? raw)
    {
        raw = string.IsNullOrWhiteSpace(raw) ? null : raw;
        var key = (row, col);
        _raw.TryGetValue(key, out var prev);
        if (prev == raw) return;

        if (raw is null) _raw.Remove(key);
        else _raw[key] = raw;

        RecomputeBounds();
        Invalidate();
        Changed?.Invoke();
    }

    public void Clear()
    {
        _raw.Clear();
        _styles.Clear();
        MaxRow = -1;
        MaxCol = -1;
        Invalidate();
        Changed?.Invoke();
    }

    public void ReplaceAll(IEnumerable<(int Row, int Col, string Raw)> cells)
    {
        _raw.Clear();
        _styles.Clear();
        MaxRow = -1;
        MaxCol = -1;
        foreach (var (r, c, raw) in cells)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            _raw[(r, c)] = raw;
            if (r > MaxRow) MaxRow = r;
            if (c > MaxCol) MaxCol = c;
        }
        Invalidate();
        Changed?.Invoke();
    }

    /// <summary>Single-cell style mutation (fires <see cref="Changed"/>).</summary>
    public void SetStyle(int row, int col, CellStyle? style)
    {
        var key = (row, col);
        if (style is null || style.IsDefault)
        {
            if (_styles.Remove(key)) Changed?.Invoke();
            return;
        }
        _styles[key] = style;
        Changed?.Invoke();
    }

    /// <summary>Bulk style application used by format loaders; does not fire
    /// <see cref="Changed"/> (callers apply it before the sheet is bound).</summary>
    public void ApplyStyles(IEnumerable<(int Row, int Col, CellStyle Style)> styles)
    {
        foreach (var (r, c, style) in styles)
        {
            if (style.IsDefault) _styles.Remove((r, c));
            else _styles[(r, c)] = style;
        }
    }

    private void RecomputeBounds()
    {
        MaxRow = -1;
        MaxCol = -1;
        foreach (var k in _raw.Keys)
        {
            if (k.Row > MaxRow) MaxRow = k.Row;
            if (k.Col > MaxCol) MaxCol = k.Col;
        }
    }

    public void Invalidate() => _cache.Clear();

    public CellValue GetValue(int row, int col)
    {
        var key = (row, col);
        if (_cache.TryGetValue(key, out var v)) return v;

        var raw = GetRaw(row, col);
        if (raw.Length == 0)
        {
            _cache[key] = CellValue.Empty;
            return CellValue.Empty;
        }

        if (raw[0] != '=')
        {
            v = Literal(raw);
            _cache[key] = v;
            return v;
        }

        if (!_computing.Add(key))
        {
            _cache[key] = CellValue.OfError("#CYCLE!");
            return _cache[key];
        }

        try
        {
            v = FormulaEngine.Evaluate(raw.Substring(1), this);
            _cache[key] = v;
            return v;
        }
        catch (FormulaException fe)
        {
            v = CellValue.OfError(fe.Message);
            _cache[key] = v;
            return v;
        }
        finally
        {
            _computing.Remove(key);
        }
    }

    private static CellValue Literal(string raw)
    {
        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return CellValue.OfNumber(n);
        return CellValue.OfText(raw);
    }
}