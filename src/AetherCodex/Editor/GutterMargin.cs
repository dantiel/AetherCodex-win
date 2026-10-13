using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace AetherCodex.Editor;

/// <summary>
/// Line-number gutter for the AvalonEdit editor. Replaces the stock
/// <c>LineNumberMargin</c> so the numbers carry horizontal padding and the
/// gutter mirrors the code surface: the active line and every selected line
/// are tinted with the theme's line-highlight and selection colours.
/// </summary>
public sealed class GutterMargin : AbstractMargin
{
    private readonly TextArea _textArea;
    private Typeface _typeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private double _emSize = 14.0;
    private Brush _foreground = Brushes.Gray;
    private Brush? _currentLineBackground;
    private Brush? _selectionBackground;
    private const double LeftPadding = 12.0;
    private const double RightPadding = 16.0;

    public GutterMargin(TextArea textArea)
    {
        _textArea = textArea ?? throw new ArgumentNullException(nameof(textArea));
        ClipToBounds = true;
        // Caret movement covers both the active line and (since selecting moves the
        // caret) selection changes — a single invalidation hook for both.
        _textArea.Caret.PositionChanged += (_, _) => InvalidateVisual();
    }

    public void Configure(FontFamily family, double fontSize, Brush foreground, Brush? currentLine, Brush? selection)
    {
        _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _emSize = fontSize > 0 ? fontSize : 14.0;
        _foreground = foreground ?? Brushes.Gray;
        _currentLineBackground = currentLine;
        _selectionBackground = selection;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double PixelsPerDip
    {
        get
        {
            try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
            catch { return 1.0; }
        }
    }

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnLayoutChanged;
            oldTextView.ScrollOffsetChanged -= OnLayoutChanged;
            oldTextView.DocumentChanged -= OnDocumentChanged;
            oldTextView.OptionChanged -= OnOptionChanged;
        }
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnLayoutChanged;
            newTextView.ScrollOffsetChanged += OnLayoutChanged;
            newTextView.DocumentChanged += OnDocumentChanged;
            newTextView.OptionChanged += OnOptionChanged;
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var doc = _textArea.Document;
        var digits = doc is not null ? Math.Max(1, doc.LineCount).ToString().Length : 1;
        return new Size(LeftPadding + RightPadding + MeasureDigits(digits), 0);
    }

    private double MeasureDigits(int digits)
    {
        var ft = new FormattedText(
            new string('8', Math.Max(1, digits)),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface, _emSize, Brushes.Black, PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var textView = TextView;
        if (textView is null || textView.Document is null) return;
        if (!textView.VisualLinesValid)
            textView.EnsureVisualLines();

        bool highlightCurrentLine = _textArea.Options?.HighlightCurrentLine ?? false;
        int caretLine = _textArea.Caret.Line;

        int selStart = -1, selEnd = -1;
        var seg = _textArea.Selection?.SurroundingSegment;
        if (seg is not null && seg.Length > 0)
        {
            selStart = textView.Document.GetLineByOffset(seg.Offset).LineNumber;
            selEnd = textView.Document.GetLineByOffset(Math.Max(seg.Offset, seg.EndOffset - 1)).LineNumber;
        }

        double width = ActualWidth;

        foreach (var visualLine in textView.VisualLines)
        {
            // A VisualLine spans exactly one document line (even when word-wrap
            // splits it into several physical text lines), so one number per
            // visual line is correct — the highlight spans the full height.
            if (visualLine.FirstDocumentLine is null) continue;

            int lineNumber = visualLine.FirstDocumentLine.LineNumber;
            double y = visualLine.VisualTop;
            double h = visualLine.Height;

            bool isCurrent = highlightCurrentLine && lineNumber == caretLine;
            bool isSelected = lineNumber >= selStart && lineNumber <= selEnd;

            if (isCurrent && _currentLineBackground is not null)
                dc.DrawRectangle(_currentLineBackground, null, new Rect(0, y, width, h));
            else if (isSelected && _selectionBackground is not null)
                dc.DrawRectangle(_selectionBackground, null, new Rect(0, y, width, h));

            DrawNumber(dc, lineNumber, y, h);
        }
    }

    private void DrawNumber(DrawingContext dc, int lineNumber, double y, double height)
    {
        var ft = new FormattedText(
            lineNumber.ToString(CultureInfo.InvariantCulture),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface, _emSize, _foreground, PixelsPerDip);

        double x = ActualWidth - RightPadding - ft.Width;
        double ty = y + (height - ft.Height) / 2.0;
        dc.DrawText(ft, new Point(x, ty));
    }
}