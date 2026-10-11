using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace AetherCodex.Editor;

/// <summary>
/// TextMate-style multi-cursor for AvalonEdit. Ctrl+W / Ctrl+Shift+W grow the cursor set
/// to the next / previous occurrence of the selection; typing edits every caret at once;
/// ←/→ move all carets synchronously; ↑/↓ retain only the first/last caret.
/// </summary>
internal sealed class MultiCursorController
{
    private readonly struct Cursor
    {
        public readonly int Anchor;
        public readonly int Head;
        public Cursor(int anchor, int head) { Anchor = Math.Min(anchor, head); Head = Math.Max(anchor, head); }
        public int Length => Head - Anchor;
    }

    private readonly TextEditor _editor;
    private readonly List<Cursor> _cursors = new();
    private int _active;
    private string _searchText = "";
    private bool _inBatchEdit;

    public MultiCursorController(TextEditor editor)
    {
        _editor = editor;
        var view = editor.TextArea.TextView;
        view.BackgroundRenderers.Add(new ExtraSelectionRenderer(this));
        view.BackgroundRenderers.Add(new ExtraCaretRenderer(this));
        editor.PreviewKeyDown += OnPreviewKeyDown;
        editor.PreviewTextInput += OnPreviewTextInput;
        editor.TextChanged += OnEditorTextChanged;
        editor.TextArea.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
    }

    private bool IsActive => _cursors.Count > 1;

    // ── Entry / growth ──────────────────────────────────────────────

    private (int Start, int End, int Length, string Text) NativeSelection()
    {
        var sel = _editor.TextArea.Selection;
        var seg = sel.SurroundingSegment;
        int start = seg.Offset, end = seg.EndOffset;
        return (start, end, end - start, _editor.Document.GetText(start, end - start));
    }

    private bool TrySelectNextOrEnter()
    {
        var sel = NativeSelection();
        if (sel.Length == 0)
        {
            if (IsActive) ExitMulti();
            return false; // fall through → Ctrl+W = close file
        }
        if (!IsActive)
        {
            _searchText = sel.Text;
            _cursors.Add(new Cursor(sel.Start, sel.End));
            _active = 0;
        }
        int from = _cursors[^1].Head;
        int found = _editor.Document.Text.IndexOf(_searchText, from, StringComparison.Ordinal);
        if (found < 0) return true;
        if (_cursors.Any(c => found < c.Head && found + _searchText.Length > c.Anchor)) return true;
        _cursors.Add(new Cursor(found, found + _searchText.Length));
        _active = _cursors.Count - 1;
        SyncNative();
        _editor.TextArea.Caret.BringCaretToView();
        return true;
    }

    private bool TrySelectPrevOrEnter()
    {
        var sel = NativeSelection();
        if (sel.Length == 0)
        {
            if (IsActive) ExitMulti();
            return false; // no window binding → no-op
        }
        if (!IsActive)
        {
            _searchText = sel.Text;
            _cursors.Add(new Cursor(sel.Start, sel.End));
            _active = 0;
        }
        int from = _cursors[0].Anchor;
        int searchFrom = from - _searchText.Length;
        if (searchFrom < 0) return true;
        int found = _editor.Document.Text.LastIndexOf(_searchText, searchFrom, StringComparison.Ordinal);
        if (found < 0) return true;
        if (_cursors.Any(c => found < c.Head && found + _searchText.Length > c.Anchor)) return true;
        _cursors.Insert(0, new Cursor(found, found + _searchText.Length));
        _active = 0;
        SyncNative();
        _editor.TextArea.Caret.BringCaretToView();
        return true;
    }

    // ── Key handling ───────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        if (ctrl && !shift && e.Key == Key.W) { e.Handled = TrySelectNextOrEnter(); return; }
        if (ctrl && shift && e.Key == Key.W) { e.Handled = TrySelectPrevOrEnter(); return; }

        if (!IsActive) return;

        switch (e.Key)
        {
            case Key.Left: e.Handled = true; MoveHorizontal(-1); return;
            case Key.Right: e.Handled = true; MoveHorizontal(1); return;
            case Key.Up: e.Handled = true; CollapseVertical(true); return;
            case Key.Down: e.Handled = true; CollapseVertical(false); return;
            case Key.Escape: e.Handled = true; ExitMulti(); return;
            case Key.Back: e.Handled = true; DeleteBackward(); return;
            case Key.Delete: e.Handled = true; DeleteForward(); return;
            case Key.Enter: e.Handled = true; InsertText(Environment.NewLine); return;
            case Key.Tab: e.Handled = true; InsertText("\t"); return;
        }

        if (ctrl && !shift)
        {
            switch (e.Key)
            {
                case Key.C: e.Handled = true; CopySelection(); return;
                case Key.X: e.Handled = true; CutSelection(); return;
                case Key.V: e.Handled = true; PasteClipboard(); return;
            }
        }

        // Any other Ctrl chord (select-all, undo, save, find, …): collapse so the
        // native/window handler sees a single caret, then let it proceed.
        if (ctrl) { ExitMulti(); return; }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!IsActive) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        InsertText(e.Text);
    }

    private void OnPreviewMouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (!IsActive) return;
        _cursors.Clear();
        _active = 0;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_inBatchEdit) return;
        _cursors.Clear();
        _active = 0;
    }

    // ── Mutations ──────────────────────────────────────────────────

    private void MoveHorizontal(int dir)
    {
        int docLen = _editor.Document.TextLength;
        for (int i = 0; i < _cursors.Count; i++)
        {
            var c = _cursors[i];
            int caret = dir > 0 ? c.Head + 1 : c.Anchor - 1;
            caret = Math.Clamp(caret, 0, docLen);
            _cursors[i] = new Cursor(caret, caret);
        }
        SyncNative();
        _editor.TextArea.Caret.BringCaretToView();
    }

    private void CollapseVertical(bool up)
    {
        var keep = up ? _cursors[0] : _cursors[^1];
        int caret = keep.Head;
        var line = _editor.Document.GetLineByOffset(caret);
        int newLine = Math.Clamp(line.LineNumber + (up ? -1 : 1), 1, _editor.Document.LineCount);
        int col = caret - line.Offset + 1;
        var target = _editor.Document.GetLineByNumber(newLine);
        int newOffset = target.Offset + Math.Min(Math.Max(col - 1, 0), target.Length);
        _cursors.Clear();
        _active = 0;
        SetNativeCaret(newOffset);
    }

    private void InsertText(string text)
    {
        var edits = new List<(int start, int length, string replacement)>();
        foreach (var c in _cursors)
            edits.Add((c.Anchor, c.Length, text));
        BatchReplace(edits);
    }

    private void DeleteBackward()
    {
        var edits = new List<(int start, int length, string replacement)>();
        foreach (var c in _cursors)
        {
            if (c.Length > 0) edits.Add((c.Anchor, c.Length, ""));
            else if (c.Anchor > 0) edits.Add((c.Anchor - 1, 1, ""));
        }
        if (edits.Count > 0) BatchReplace(edits);
    }

    private void DeleteForward()
    {
        var edits = new List<(int start, int length, string replacement)>();
        foreach (var c in _cursors)
        {
            if (c.Length > 0) edits.Add((c.Anchor, c.Length, ""));
            else if (c.Anchor < _editor.Document.TextLength) edits.Add((c.Anchor, 1, ""));
        }
        if (edits.Count > 0) BatchReplace(edits);
    }

    private void CopySelection()
    {
        var texts = new List<string>();
        foreach (var c in _cursors)
            texts.Add(c.Length > 0 ? _editor.Document.GetText(c.Anchor, c.Length) : "");
        Clipboard.SetText(string.Join("\n", texts));
    }

    private void CutSelection()
    {
        CopySelection();
        var edits = new List<(int start, int length, string replacement)>();
        foreach (var c in _cursors)
            if (c.Length > 0) edits.Add((c.Anchor, c.Length, ""));
        if (edits.Count > 0) BatchReplace(edits);
        else ExitMulti();
    }

    private void PasteClipboard()
    {
        var text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text)) return;
        var lines = text.Split('\n');
        if (lines.Length == _cursors.Count)
        {
            var edits = new List<(int start, int length, string replacement)>();
            for (int i = 0; i < _cursors.Count; i++)
                edits.Add((_cursors[i].Anchor, _cursors[i].Length, lines[i].TrimEnd('\r')));
            BatchReplace(edits);
        }
        else
        {
            InsertText(text);
        }
    }

    // ── Core batch edit (descending apply + offset remap) ──────────

    private void BatchReplace(List<(int start, int length, string replacement)> edits)
    {
        if (edits.Count == 0) return;
        var doc = _editor.Document;

        var desc = edits.OrderByDescending(e => e.start).ToList();
        var asc = edits.OrderBy(e => e.start).ToList();

        _inBatchEdit = true;
        try
        {
            using (doc.RunUpdate())
            {
                doc.UndoStack.StartUndoGroup();
                try
                {
                    foreach (var e in desc)
                        doc.Replace(e.start, e.length, e.replacement);
                }
                finally
                {
                    doc.UndoStack.EndUndoGroup();
                }
            }
        }
        finally
        {
            _inBatchEdit = false;
        }

        var newCursors = new List<Cursor>();
        foreach (var e in edits)
        {
            int delta = 0;
            foreach (var a in asc)
            {
                if (a.start < e.start) delta += a.replacement.Length - a.length;
                else break;
            }
            int caret = e.start + e.replacement.Length + delta;
            newCursors.Add(new Cursor(caret, caret));
        }

        // Deduplicate degenerate adjacent-collapse results.
        var seen = new HashSet<int>();
        _cursors.Clear();
        foreach (var c in newCursors)
            if (seen.Add(c.Anchor))
                _cursors.Add(c);

        _active = _cursors.Count - 1;
        SyncNative();
        _editor.TextArea.Caret.BringCaretToView();
    }

    private void SyncNative()
    {
        if (_cursors.Count == 0) return;
        var c = _cursors[_active];
        var ta = _editor.TextArea;
        ta.Selection = Selection.Create(ta, c.Anchor, c.Head);
        ta.Caret.Offset = c.Head;
    }

    private void SetNativeCaret(int offset)
    {
        var ta = _editor.TextArea;
        ta.Selection = Selection.Create(ta, offset, offset);
        ta.Caret.Offset = offset;
        ta.Caret.BringCaretToView();
        _editor.Focus();
    }

    private void ExitMulti()
    {
        if (!IsActive) return;
        int caret = _cursors[_active].Head;
        caret = Math.Clamp(caret, 0, _editor.Document.TextLength);
        _cursors.Clear();
        _active = 0;
        SetNativeCaret(caret);
    }

    // ── Renderers ──────────────────────────────────────────────────

    private sealed class ExtraSelectionRenderer : IBackgroundRenderer
    {
        private readonly MultiCursorController _owner;
        public ExtraSelectionRenderer(MultiCursorController owner) { _owner = owner; }
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (!_owner.IsActive) return;
            var brush = _owner._editor.TextArea.SelectionBrush
                        ?? new SolidColorBrush(Color.FromArgb(0x80, 0x33, 0x99, 0xFF));
            for (int i = 0; i < _owner._cursors.Count; i++)
            {
                if (i == _owner._active) continue;
                var c = _owner._cursors[i];
                if (c.Length <= 0) continue;
                var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 0 };
                builder.AddSegment(textView, new TextSegment { StartOffset = c.Anchor, Length = c.Length });
                dc.DrawGeometry(brush, null, builder.CreateGeometry());
            }
        }
    }

    private sealed class ExtraCaretRenderer : IBackgroundRenderer
    {
        private readonly MultiCursorController _owner;
        public ExtraCaretRenderer(MultiCursorController owner) { _owner = owner; }
        public KnownLayer Layer => KnownLayer.Caret;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (!_owner.IsActive) return;
            var brush = _owner._editor.Foreground ?? Brushes.White;
            for (int i = 0; i < _owner._cursors.Count; i++)
            {
                if (i == _owner._active) continue;
                var c = _owner._cursors[i];
                var loc = textView.Document.GetLocation(c.Head);
                var top = textView.GetVisualPosition(new TextViewPosition(loc), VisualYPosition.TextTop);
                var bottom = textView.GetVisualPosition(new TextViewPosition(loc), VisualYPosition.TextBottom);
                double h = Math.Max(1.0, bottom.Y - top.Y);
                dc.DrawRectangle(brush, null, new Rect(top.X, top.Y, 1.5, h));
            }
        }
    }
}