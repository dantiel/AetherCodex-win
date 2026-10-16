using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace AetherCodex.Editor;

/// <summary>
/// The rich-text "Lineal" (horizontal ruler) shown above the rich editor surface,
/// mirroring the macOS NSTextView ruler: a centimetre scale with draggable
/// first-line / left / right indent markers that edit the caret paragraph.
/// </summary>
public sealed class RulerControl : FrameworkElement
{
    // 1 cm in device-independent pixels at the 96 DPI baseline.
    private const double DipPerCm = 96.0 / 2.54;
    private const double MarkerSize = 10.0;
    private const double MarkerHit = 8.0;

    private RichTextBox? _editor;
    private Brush _background = Brushes.Transparent;
    private Pen _separatorPen = new(Brushes.Gray, 1.0);
    private Brush _scaleBrush = Brushes.Gray;
    private Brush _markerBrush = Brushes.Gray;

    private enum DragKind { None, FirstLine, Left, Right }
    private DragKind _drag = DragKind.None;
    private Point _dragStart;

    public RulerControl()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Height = 22;
    }

    public void Attach(RichTextBox editor)
    {
        if (_editor is not null)
        {
            _editor.SelectionChanged -= OnEditorChanged;
            _editor.TextChanged -= OnEditorChanged;
            _editor.LayoutUpdated -= OnEditorChanged;
        }
        _editor = editor;
        if (_editor is not null)
        {
            _editor.SelectionChanged += OnEditorChanged;
            _editor.TextChanged += OnEditorChanged;
            _editor.LayoutUpdated += OnEditorChanged;
        }
        InvalidateVisual();
    }

    public void Configure(Color background, Color separator, Color scale, Color marker)
    {
        _background = Freeze(new SolidColorBrush(background));
        _separatorPen = Freeze(new Pen(new SolidColorBrush(separator), 1.0));
        _scaleBrush = Freeze(new SolidColorBrush(scale));
        _markerBrush = Freeze(new SolidColorBrush(marker));
        InvalidateVisual();
    }

    private static T Freeze<T>(T obj) where T : Freezable
    {
        obj.Freeze();
        return obj;
    }

    private void OnEditorChanged(object? sender, EventArgs e) => InvalidateVisual();

    private Paragraph? CurrentParagraph()
    {
        if (_editor?.Document is null) return null;
        var caret = _editor.CaretPosition;
        if (caret is null) return null;
        if (caret.Paragraph is Paragraph p) return p;

        // Walk up from the caret's parent block when the caret sits on a run/inline.
        DependencyObject? el = caret.Parent;
        while (el is not null && el is not Paragraph)
            el = el is TextElement te ? te.Parent : null;
        return el as Paragraph;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(_background, null, new Rect(0, 0, w, h));

        double leftInset = _editor?.Padding.Left ?? 0;
        double rightInset = _editor?.Padding.Right ?? 0;
        double contentRight = Math.Max(leftInset, w - rightInset);

        DrawScale(dc, leftInset, contentRight, h);

        var para = CurrentParagraph();
        if (para is not null)
        {
            double marginLeft = para.Margin.Left;
            double textIndent = para.TextIndent;
            double marginRight = para.Margin.Right;

            double firstLineX = leftInset + marginLeft + textIndent;
            double leftX = leftInset + marginLeft;
            double rightX = contentRight - marginRight;

            DrawFirstLineMarker(dc, firstLineX, h);
            DrawLeftMarker(dc, leftX, h);
            DrawRightMarker(dc, rightX, h);
        }

        // Baseline rule separating the ruler from the document.
        dc.DrawLine(_separatorPen, new Point(0, h - 0.5), new Point(w, h - 0.5));
    }

    private void DrawScale(DrawingContext dc, double left, double right, double height)
    {
        // Scale zero aligns with the text area's left edge (the paragraph
        // indent origin); major tick every cm, minor ticks at 0.25 cm.
        double span = right - left;
        int minorCount = (int)Math.Floor(span / (DipPerCm * 0.25));
        var typeface = new Typeface("Segoe UI");
        double fontSize = 9.0;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = 0; i <= minorCount; i++)
        {
            double x = left + i * DipPerCm * 0.25;
            bool major = i % 4 == 0;
            double tickTop = major ? height * 0.20 : height * 0.45;
            double tickBottom = height - 4;
            dc.DrawLine(_separatorPen, new Point(x, tickTop), new Point(x, tickBottom));

            if (major)
            {
                var ft = new FormattedText(
                    (i / 4).ToString(CultureInfo.InvariantCulture),
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, fontSize, _scaleBrush, dpi);
                dc.DrawText(ft, new Point(x + 2, 1));
            }
        }
    }

    private void DrawFirstLineMarker(DrawingContext dc, double x, double height)
    {
        // Down-pointing triangle in the top half (first-line indent).
        double cx = x, top = 1, bottom = height / 2.0 - 1;
        var geo = Triangle(cx, top, bottom, pointDown: true);
        dc.DrawGeometry(_markerBrush, null, geo);
    }

    private void DrawLeftMarker(DrawingContext dc, double x, double height)
    {
        // Up-pointing triangle in the bottom half (left/hanging indent).
        double cx = x, top = height / 2.0 + 1, bottom = height - 4;
        var geo = Triangle(cx, top, bottom, pointDown: false);
        dc.DrawGeometry(_markerBrush, null, geo);
    }

    private void DrawRightMarker(DrawingContext dc, double x, double height)
    {
        // Down-pointing triangle in the bottom half near the right edge.
        double cx = x, top = height / 2.0 + 1, bottom = height - 4;
        var geo = Triangle(cx, top, bottom, pointDown: true);
        dc.DrawGeometry(_markerBrush, null, geo);
    }

    private static Geometry Triangle(double cx, double top, double bottom, bool pointDown)
    {
        double half = MarkerSize / 2.0;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            if (pointDown)
            {
                ctx.BeginFigure(new Point(cx - half, top), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(cx + half, top), false, false);
                ctx.LineTo(new Point(cx, bottom), false, false);
            }
            else
            {
                ctx.BeginFigure(new Point(cx - half, bottom), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(cx + half, bottom), false, false);
                ctx.LineTo(new Point(cx, top), false, false);
            }
        }
        geo.Freeze();
        return geo;
    }

    // ── Indent-marker dragging ────────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var para = CurrentParagraph();
        if (para is null) return;

        var pos = e.GetPosition(this);
        double leftInset = _editor?.Padding.Left ?? 0;
        double rightInset = _editor?.Padding.Right ?? 0;
        double contentRight = Math.Max(leftInset, ActualWidth - rightInset);
        double h = ActualHeight;

        double firstLineX = leftInset + para.Margin.Left + para.TextIndent;
        double leftX = leftInset + para.Margin.Left;
        double rightX = contentRight - para.Margin.Right;

        if (Math.Abs(pos.X - firstLineX) <= MarkerHit) _drag = DragKind.FirstLine;
        else if (Math.Abs(pos.X - leftX) <= MarkerHit) _drag = DragKind.Left;
        else if (Math.Abs(pos.X - rightX) <= MarkerHit) _drag = DragKind.Right;
        else return;

        _dragStart = pos;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == DragKind.None || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        double dx = pos.X - _dragStart.X;
        var para = CurrentParagraph();
        if (para is null) return;

        switch (_drag)
        {
            case DragKind.FirstLine:
                para.TextIndent = Math.Max(para.TextIndent + dx, -para.Margin.Left);
                break;
            case DragKind.Left:
                double newLeft = Math.Max(para.Margin.Left + dx, 0);
                // Move first-line indent together with the left indent, matching
                // the TextEdit behaviour of the bottom (left) marker.
                para.TextIndent += newLeft - para.Margin.Left;
                para.Margin = new Thickness(newLeft, para.Margin.Top, para.Margin.Right, para.Margin.Bottom);
                break;
            case DragKind.Right:
                para.Margin = new Thickness(para.Margin.Left, para.Margin.Top,
                    Math.Max(para.Margin.Right - dx, 0), para.Margin.Bottom);
                break;
        }

        _dragStart = pos;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == DragKind.None) return;
        _drag = DragKind.None;
        ReleaseMouseCapture();
        InvalidateVisual();
        e.Handled = true;
    }
}