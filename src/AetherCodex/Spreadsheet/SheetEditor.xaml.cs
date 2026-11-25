using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AetherCodex.Spreadsheet;

public partial class SheetEditor : UserControl
{
    private const double ColWidth = 96;
    private const double RowHeight = 22;

    private Sheet? _sheet;
    private int _selRow, _selCol;
    private bool _editing;
    private TextBox? _editBox;
    private bool _suppressScroll;
    private (int Row, int Col)? _prevSel;

    private readonly Dictionary<(int Row, int Col), Border> _cells = new();

    public event Action? Modified;

    public SheetEditor()
    {
        InitializeComponent();
        Loaded += (_, _) => SurfaceScroll.Focus();
        FormulaBox.GotKeyboardFocus += (_, _) => FormulaBox.SelectAll();
    }

    public void LoadSheet(Sheet sheet)
    {
        if (_sheet is not null) _sheet.Changed -= OnSheetChanged;
        CommitEditBox(cancel: true);
        _sheet = sheet;
        _sheet.Changed += OnSheetChanged;
        _selRow = 0;
        _selCol = 0;
        Rebuild();
        UpdateNameBox();
    }

    public Sheet CurrentSheet => _sheet ??= new Sheet();

    public void CommitEdit() => CommitEditBox(cancel: false);

    private void OnSheetChanged()
    {
        Rebuild();
        Modified?.Invoke();
    }

    private int GridRows => _sheet is null ? 200 : Math.Max(200, _sheet.MaxRow + 40);
    private int GridCols => _sheet is null ? 26 : Math.Max(26, _sheet.MaxCol + 5);

    // ── layout ──
    private void Rebuild()
    {
        int rows = GridRows, cols = GridCols;
        CellCanvas.Width = cols * ColWidth;
        CellCanvas.Height = rows * RowHeight;
        BuildHeaderCanvas(cols);
        BuildRowCanvas(rows);
        _cells.Clear();
        CellCanvas.Children.Clear();
        RenderVisibleCells();
        UpdateSelectionVisual();
    }

    private void BuildHeaderCanvas(int cols)
    {
        HeaderCanvas.Children.Clear();
        HeaderCanvas.Width = cols * ColWidth;
        for (int c = 0; c < cols; c++)
        {
            var tb = new TextBlock
            {
                Text = SheetRef.ColumnLabel(c),
                Width = ColWidth,
                Height = 24,
                TextAlignment = TextAlignment.Center,
                FontWeight = FontWeights.Bold
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedForeground");
            Canvas.SetLeft(tb, c * ColWidth);
            HeaderCanvas.Children.Add(tb);
        }
    }

    private void BuildRowCanvas(int rows)
    {
        RowCanvas.Children.Clear();
        RowCanvas.Height = rows * RowHeight;
        for (int r = 0; r < rows; r++)
        {
            var tb = new TextBlock
            {
                Text = (r + 1).ToString(),
                Width = 48,
                Height = RowHeight,
                TextAlignment = TextAlignment.Center
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedForeground");
            Canvas.SetTop(tb, r * RowHeight);
            RowCanvas.Children.Add(tb);
        }
    }

    private void RenderVisibleCells()
    {
        if (_sheet is null) return;

        int firstRow = Math.Max(0, (int)(SurfaceScroll.VerticalOffset / RowHeight) - 1);
        int lastRow = Math.Min(GridRows - 1, firstRow + (int)(SurfaceScroll.ViewportHeight / RowHeight) + 2);
        int firstCol = Math.Max(0, (int)(SurfaceScroll.HorizontalOffset / ColWidth) - 1);
        int lastCol = Math.Min(GridCols - 1, firstCol + (int)(SurfaceScroll.ViewportWidth / ColWidth) + 2);

        var offscreen = _cells.Keys
            .Where(k => k.Row < firstRow || k.Row > lastRow || k.Col < firstCol || k.Col > lastCol)
            .ToList();
        foreach (var k in offscreen)
        {
            CellCanvas.Children.Remove(_cells[k]);
            _cells.Remove(k);
        }

        for (int r = firstRow; r <= lastRow; r++)
            for (int c = firstCol; c <= lastCol; c++)
                if (!_cells.ContainsKey((r, c)))
                    CreateCell(r, c);
    }

    private void CreateCell(int r, int c)
    {
        var value = _sheet?.GetValue(r, c) ?? CellValue.Empty;
        var style = _sheet?.GetStyle(r, c);

        var text = new TextBlock
        {
            Text = DisplayOf(value, style),
            Width = ColWidth,
            Height = RowHeight,
            Padding = new Thickness(4, 2, 4, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Foreground");

        var border = new Border
        {
            Width = ColWidth,
            Height = RowHeight,
            Child = text,
            BorderThickness = new Thickness(0, 0, 1, 1)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "Border");

        ApplyStyle(text, border, value, style);

        Canvas.SetLeft(border, c * ColWidth);
        Canvas.SetTop(border, r * RowHeight);
        CellCanvas.Children.Add(border);
        _cells[(r, c)] = border;
    }

    private static string DisplayOf(CellValue value, CellStyle? style) =>
        NumberFormatEngine.Format(value, style?.NumberFormat);

    private void ApplyStyle(TextBlock text, Border border, CellValue value, CellStyle? style)
    {
        if (style is null) return;

        text.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
        text.FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal;
        text.TextDecorations = style.Underline ? TextDecorations.Underline : null;

        if (!string.IsNullOrEmpty(style.FontName))
            text.FontFamily = new FontFamily(style.FontName);
        if (style.FontSize is { } fs)
            text.FontSize = fs;

        if (TryBrush(style.FontColor, out var fg))
            text.Foreground = fg;
        if (TryBrush(style.FillColor, out var bg))
            border.Background = bg;

        text.TextAlignment = style.Align switch
        {
            CellHAlign.Left => TextAlignment.Left,
            CellHAlign.Center => TextAlignment.Center,
            CellHAlign.Right => TextAlignment.Right,
            _ => value.IsNumber ? TextAlignment.Right : TextAlignment.Left
        };
    }

    private static bool TryBrush(string? hex, out Brush brush)
    {
        brush = null!;
        if (string.IsNullOrEmpty(hex)) return false;
        hex = hex.TrimStart('#');
        if (hex.Length != 6 && hex.Length != 8) return false;
        try
        {
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            return true;
        }
        catch { return false; }
    }

    private void UpdateSelectionVisual()
    {
        if (_prevSel is { } p && _cells.TryGetValue((p.Row, p.Col), out var pb))
        {
            pb.BorderThickness = new Thickness(0, 0, 1, 1);
            pb.SetResourceReference(Border.BorderBrushProperty, "Border");
        }
        if (_cells.TryGetValue((_selRow, _selCol), out var b))
        {
            b.BorderThickness = new Thickness(2);
            b.SetResourceReference(Border.BorderBrushProperty, "Accent");
        }
        _prevSel = (_selRow, _selCol);
    }

    private void UpdateNameBox()
    {
        NameBox.Text = SheetRef.ToRef(_selRow, _selCol);
        FormulaBox.Text = _sheet?.GetRaw(_selRow, _selCol) ?? "";
    }

    // ── selection / navigation ──
    private void SelectCell(int r, int c)
    {
        CommitFormulaBox();
        _selRow = r;
        _selCol = c;
        UpdateNameBox();
        UpdateSelectionVisual();
        ScrollSelectionIntoView();
        SurfaceScroll.Focus();
    }

    private void MoveSelection(int dr, int dc)
    {
        _selRow = Math.Clamp(_selRow + dr, 0, GridRows - 1);
        _selCol = Math.Clamp(_selCol + dc, 0, GridCols - 1);
        UpdateNameBox();
        UpdateSelectionVisual();
        ScrollSelectionIntoView();
    }

    private void ScrollSelectionIntoView()
    {
        double x = _selCol * ColWidth, y = _selRow * RowHeight;
        double sx = SurfaceScroll.HorizontalOffset, sy = SurfaceScroll.VerticalOffset;
        double vw = SurfaceScroll.ViewportWidth, vh = SurfaceScroll.ViewportHeight;
        double nx = sx, ny = sy;
        if (x < sx) nx = x;
        else if (x + ColWidth > sx + vw) nx = x + ColWidth - vw;
        if (y < sy) ny = y;
        else if (y + RowHeight > sy + vh) ny = y + RowHeight - vh;
        SurfaceScroll.ScrollToHorizontalOffset(nx);
        SurfaceScroll.ScrollToVerticalOffset(ny);
    }

    // ── editing ──
    private void StartEdit(string? initial = null)
    {
        if (_sheet is null || _editing) return;
        _editing = true;

        _editBox = new TextBox
        {
            Width = ColWidth,
            Height = RowHeight,
            Padding = new Thickness(2, 1, 2, 1),
            BorderThickness = new Thickness(2),
            Text = initial ?? _sheet.GetRaw(_selRow, _selCol)
        };
        _editBox.SetResourceReference(Control.BorderBrushProperty, "Accent");
        _editBox.SetResourceReference(Control.BackgroundProperty, "EditorBackground");
        _editBox.SetResourceReference(TextBoxBase.CaretBrushProperty, "Foreground");
        _editBox.SetResourceReference(TextBoxBase.CaretBrushProperty, "Foreground");
        _editBox.KeyDown += OnEditKeyDown;
        _editBox.LostKeyboardFocus += OnEditLostFocus;

        Canvas.SetLeft(_editBox, _selCol * ColWidth);
        Canvas.SetTop(_editBox, _selRow * RowHeight);
        CellCanvas.Children.Add(_editBox);
        _editBox.Focus();
        _editBox.SelectAll();
    }

    private void CommitEditBox(bool cancel)
    {
        if (!_editing || _editBox is null) return;
        _editing = false;
        var box = _editBox;
        _editBox = null;
        box.KeyDown -= OnEditKeyDown;
        box.LostKeyboardFocus -= OnEditLostFocus;

        if (!cancel && _sheet is not null)
        {
            var text = box.Text;
            if (text != _sheet.GetRaw(_selRow, _selCol))
                _sheet.SetRaw(_selRow, _selCol, text);
        }

        CellCanvas.Children.Remove(box);
        SurfaceScroll.Focus();
    }

    private void CommitFormulaBox()
    {
        if (_sheet is null) return;
        var raw = FormulaBox.Text;
        if (raw != _sheet.GetRaw(_selRow, _selCol))
            _sheet.SetRaw(_selRow, _selCol, raw);
    }

    private void ClearCell()
    {
        if (_sheet is not null) _sheet.SetRaw(_selRow, _selCol, null);
    }

    // ── input ──
    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(CellCanvas);
        int r = (int)(pos.Y / RowHeight);
        int c = (int)(pos.X / ColWidth);
        if (r < 0 || c < 0 || r >= GridRows || c >= GridCols) return;
        SelectCell(r, c);
        if (e.ClickCount == 2) StartEdit();
        e.Handled = true;
    }

    private void OnSurfaceKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up: MoveSelection(-1, 0); e.Handled = true; break;
            case Key.Down: MoveSelection(1, 0); e.Handled = true; break;
            case Key.Left: MoveSelection(0, -1); e.Handled = true; break;
            case Key.Right: MoveSelection(0, 1); e.Handled = true; break;
            case Key.Tab: MoveSelection(0, 1); e.Handled = true; break;
            case Key.Enter:
            case Key.F2: StartEdit(); e.Handled = true; break;
            case Key.Delete:
            case Key.Back: ClearCell(); e.Handled = true; break;
            case Key.Home: SelectCell(_selRow, 0); e.Handled = true; break;
            default:
                var ch = KeyToChar(e);
                if (ch is not null) { StartEdit(ch); e.Handled = true; }
                break;
        }
    }

    private void OnEditKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitEditBox(false); MoveSelection(1, 0); e.Handled = true; }
        else if (e.Key == Key.Tab) { CommitEditBox(false); MoveSelection(0, 1); e.Handled = true; }
        else if (e.Key == Key.Escape) { CommitEditBox(true); e.Handled = true; }
    }

    private void OnEditLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_editing) CommitEditBox(false);
    }

    private void OnFormulaKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitFormulaBox(); MoveSelection(1, 0); e.Handled = true; }
        else if (e.Key == Key.Tab) { CommitFormulaBox(); MoveSelection(0, 1); e.Handled = true; }
        else if (e.Key == Key.Escape) { UpdateNameBox(); e.Handled = true; }
    }

    private static string? KeyToChar(KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers != ModifierKeys.None && e.KeyboardDevice.Modifiers != ModifierKeys.Shift)
            return null;
        if (e.Key >= Key.A && e.Key <= Key.Z)
        {
            char c = (char)('a' + (e.Key - Key.A));
            return (e.KeyboardDevice.Modifiers == ModifierKeys.Shift) ? char.ToUpperInvariant(c).ToString() : c.ToString();
        }
        if (e.Key >= Key.D0 && e.Key <= Key.D9) return ((char)('0' + (e.Key - Key.D0))).ToString();
        if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9) return ((char)('0' + (e.Key - Key.NumPad0))).ToString();
        if (e.Key == Key.Space) return " ";
        if (e.Key == Key.OemMinus) return "-";
        if (e.Key == Key.OemPlus) return e.KeyboardDevice.Modifiers == ModifierKeys.Shift ? "=" : "+";
        if (e.Key == Key.OemPeriod) return ".";
        if (e.Key == Key.OemComma) return ",";
        if (e.Key == Key.OemQuestion) return "/";
        return null;
    }

    // ── scroll sync ──
    private void OnSurfaceScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        RenderVisibleCells();
        UpdateSelectionVisual();
        _suppressScroll = true;
        HeaderScroll.ScrollToHorizontalOffset(SurfaceScroll.HorizontalOffset);
        RowScroll.ScrollToVerticalOffset(SurfaceScroll.VerticalOffset);
        _suppressScroll = false;
    }

    private void OnHeaderScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressScroll) return;
        _suppressScroll = true;
        SurfaceScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        _suppressScroll = false;
    }

    private void OnRowScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressScroll) return;
        _suppressScroll = true;
        SurfaceScroll.ScrollToVerticalOffset(e.VerticalOffset);
        _suppressScroll = false;
    }
}