using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using AetherCodex.Bridge;
using AetherCodex.Git;

namespace AetherCodex;

/// <summary>
/// Embedded diff view: renders a two-text diff with a selectable strategy
/// (Line / Word / Crepusculum). The comparison lives in the Ruby soul
/// (DiffCrepusculum.compare, bridged via <see cref="RubyBridge.DiffAsync"/>);
/// word marks changed words inside each line; crepusculum unites a paired
/// modification into a single inline word diff (deletions struck through).
/// Falls back to the git CLI unified diff when the daemon is unavailable.
/// </summary>
public partial class DiffView : UserControl
{
    private readonly RubyBridge _bridge = RubyBridge.Shared;
    private string _repoRoot = "";
    private GitChange? _change;
    private string _old = "";
    private string _new = "";
    private bool _loaded;
    private object? _loadedDoc;

    private string _strategy = "line";

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");

    private static readonly Color InsertColor = Color.FromRgb(0x3F, 0xB9, 0x50);
    private static readonly Color DeleteColor = Color.FromRgb(0xF8, 0x51, 0x49);
    private static readonly Color HunkColor = Color.FromRgb(0x58, 0xA6, 0xFF);
    private static readonly Color ContextColor = Color.FromRgb(0x8E, 0x94, 0xA0);
    private static readonly Color GutterColor = Color.FromRgb(0x5A, 0x60, 0x68);
    private static readonly Color InsertSpanColor = Color.FromRgb(0x9C, 0xE6, 0xB4);
    private static readonly Color DeleteSpanColor = Color.FromRgb(0xFF, 0xB4, 0xB4);

    private static readonly Color InsertBg = Color.FromRgb(0x12, 0x2B, 0x1A);
    private static readonly Color DeleteBg = Color.FromRgb(0x2B, 0x15, 0x13);
    private static readonly Color HunkBg = Color.FromRgb(0x12, 0x18, 0x26);
    private static readonly Color ModifyBg = Color.FromRgb(0x1A, 0x16, 0x1E);

    private static readonly Brush InsertBrush = Freeze(InsertColor);
    private static readonly Brush InsertSpanBrush = Freeze(InsertSpanColor);
    private static readonly Brush InsertBgBrush = Freeze(InsertBg);
    private static readonly Brush DeleteBrush = Freeze(DeleteColor);
    private static readonly Brush DeleteSpanBrush = Freeze(DeleteSpanColor);
    private static readonly Brush DeleteBgBrush = Freeze(DeleteBg);
    private static readonly Brush HunkBrush = Freeze(HunkColor);
    private static readonly Brush HunkBgBrush = Freeze(HunkBg);
    private static readonly Brush ContextBrush = Freeze(ContextColor);
    private static readonly Brush GutterBrush = Freeze(GutterColor);
    private static readonly Brush ModifyBgBrush = Freeze(ModifyBg);

    // Hunk headers, kept in render order so the parallax sticky header can pin
    // the chunk currently scrolled into view.
    private readonly List<Border> _hunkHeaderBorders = new();
    private readonly List<string> _hunkHeaderLabels = new();
    private readonly List<(double Offset, string Label)> _hunkHeaders = new();

    public DiffView()
    {
        InitializeComponent();
    }

    /// <summary>Bind the view to a document's diff and render it. Re-activating
    /// the same <paramref name="key"/> is a no-op (preserves scroll + strategy).</summary>
    public void Load(object key, string repoRoot, GitChange? change, string oldText, string newText, string title)
    {
        if (ReferenceEquals(_loadedDoc, key)) return;
        _loadedDoc = key;
        _loaded = true;
        _repoRoot = repoRoot;
        _change = change;
        _old = oldText;
        _new = newText;
        TitleText.Text = title;
        _ = RenderAsync();
    }

    private void OnStrategyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StrategyBox is null || StrategyBox.SelectedIndex < 0) return;
        _strategy = StrategyBox.SelectedIndex switch
        {
            1 => "word",
            2 => "crepusculum",
            _ => "line",
        };
        if (!_loaded) return;
        _ = RenderAsync();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RenderAsync();

    private async Task RenderAsync()
    {
        try
        {
            var diff = await _bridge.DiffAsync(_old, _new, _strategy);
            if (diff is not null && diff["hunks"] is JsonArray hunks)
            {
                RenderStructured(hunks);
                return;
            }
        }
        catch { /* daemon unavailable — fall through to the git CLI diff */ }

        RenderFallback();
    }

    private void RenderStructured(JsonArray hunks)
    {
        Rows.Children.Clear();
        _hunkHeaderBorders.Clear();
        _hunkHeaderLabels.Clear();
        _hunkHeaders.Clear();
        foreach (var node in hunks)
        {
            if (node is not JsonObject hunk) continue;

            AddHunkHeader(
                Int(hunk["old_start"]), Int(hunk["old_count"]),
                Int(hunk["new_start"]), Int(hunk["new_count"]));

            if (hunk["lines"] is not JsonArray lines) continue;
            foreach (var ln in lines)
            {
                if (ln is not JsonObject line) continue;
                var type = line["type"]?.GetValue<string>() ?? "context";
                var text = line["text"]?.GetValue<string>() ?? "";
                var spans = new List<(int Start, int Len, string Kind)>();
                if (line["spans"] is JsonArray sa)
                    foreach (var s in sa)
                        if (s is JsonObject so)
                            spans.Add((Int(so["start"]) ?? 0, Int(so["len"]) ?? 0,
                                       so["kind"]?.GetValue<string>() ?? ""));

                AddLineRow(type, text, Int(line["old_no"]), Int(line["new_no"]), spans);
            }
        }

        if (Rows.Children.Count == 0)
            Rows.Children.Add(StatusRow("No differences"));

        ScheduleOffsetRefresh();
    }

    private void RenderFallback()
    {
        Rows.Children.Clear();
        _hunkHeaderBorders.Clear();
        _hunkHeaderLabels.Clear();
        _hunkHeaders.Clear();

        if (_change is null)
        {
            Rows.Children.Add(StatusRow("No diff available"));
            ScheduleOffsetRefresh();
            return;
        }

        var diff = GitService.GetDiff(_repoRoot, _change);
        var lines = diff.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            if (raw.Length == 0) { AddLineRow("context", "", null, null, new()); continue; }
            if (IsMeta(raw)) continue;

            if (raw.StartsWith("@@"))
                AddHunkHeader(null, null, null, null);
            else if (raw.StartsWith("+++") || raw.StartsWith("---"))
                AddLineRow("header", raw, null, null, new());
            else if (raw.StartsWith('+'))
                AddLineRow("insert", raw, null, null, new());
            else if (raw.StartsWith('-'))
                AddLineRow("delete", raw, null, null, new());
            else
                AddLineRow("context", raw, null, null, new());
        }

        if (Rows.Children.Count == 0)
            Rows.Children.Add(StatusRow("No diff available"));

        ScheduleOffsetRefresh();
    }

    private static bool IsMeta(string text) =>
        text.StartsWith("diff ") || text.StartsWith("index ") ||
        text.StartsWith("new file") || text.StartsWith("deleted file") ||
        text.StartsWith("similarity") || text.StartsWith("rename ") ||
        text.StartsWith("copy ") || text.StartsWith("old mode") ||
        text.StartsWith("new mode") || text.StartsWith("Binary files") ||
        text.StartsWith("GIT binary") || text.StartsWith("\\ No newline");

    private static int? Int(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<int>(); }
        catch { return null; }
    }

    private void AddHunkHeader(int? oldStart, int? oldCount, int? newStart, int? newCount)
    {
        var label = oldStart is null
            ? "@@"
            : $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@";

        var tb = new TextBlock
        {
            Text = label,
            FontFamily = MonoFont,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = HunkBrush,
        };
        var border = new Border
        {
            Background = HunkBgBrush,
            Padding = new Thickness(8, 3, 8, 3),
            Child = tb,
        };
        _hunkHeaderBorders.Add(border);
        _hunkHeaderLabels.Add(label);
        Rows.Children.Add(border);
    }

    private void AddLineRow(string type, string text, int? oldNo, int? newNo, List<(int Start, int Len, string Kind)> spans)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var oldTb = Gutter(oldNo?.ToString() ?? "");
        var newTb = Gutter(newNo?.ToString() ?? "");
        Grid.SetColumn(oldTb, 0);
        Grid.SetColumn(newTb, 1);

        var textTb = new TextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12.5,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetColumn(textTb, 2);

        Brush bg = type switch
        {
            "insert" => InsertBgBrush,
            "delete" => DeleteBgBrush,
            "modify" => ModifyBgBrush,
            _ => Brushes.Transparent,
        };

        BuildRuns(textTb, text, spans, type);

        grid.Children.Add(oldTb);
        grid.Children.Add(newTb);
        grid.Children.Add(textTb);

        var border = new Border
        {
            Background = bg,
            Padding = new Thickness(8, 1, 8, 1),
            Child = grid,
        };
        Rows.Children.Add(border);
    }

    private static void BuildRuns(TextBlock tb, string text, List<(int Start, int Len, string Kind)> spans, string type)
    {
        var sorted = spans.Where(s => s.Len > 0).OrderBy(s => s.Start).ToList();
        int pos = 0;
        foreach (var (start, len, kind) in sorted)
        {
            if (start < pos) continue;
            if (start > pos)
                AddRun(tb, text[pos..Math.Min(start, text.Length)], type, "plain");
            int end = Math.Min(start + len, text.Length);
            if (end > start)
                AddRun(tb, text[start..end], type, kind.Length == 0 ? (type == "delete" ? "del" : "ins") : kind);
            pos = Math.Max(pos, end);
        }
        if (pos < text.Length)
            AddRun(tb, text[pos..], type, "plain");
        if (tb.Inlines.Count == 0)
            AddRun(tb, text, type, "plain");
    }

    private static void AddRun(TextBlock tb, string s, string type, string kind)
    {
        var run = new Run(s);
        switch (type)
        {
            case "delete":
                run.Foreground = DeleteBrush;
                run.TextDecorations = TextDecorations.Strikethrough;
                if (kind == "del") { run.Background = DeleteSpanBrush; run.FontWeight = FontWeights.Bold; }
                break;
            case "insert":
                run.Foreground = InsertBrush;
                if (kind == "ins") { run.Background = InsertSpanBrush; run.FontWeight = FontWeights.Bold; }
                break;
            case "modify":
                if (kind == "del")
                {
                    run.Foreground = DeleteBrush;
                    run.Background = DeleteSpanBrush;
                    run.TextDecorations = TextDecorations.Strikethrough;
                }
                else if (kind == "ins")
                {
                    run.Foreground = InsertBrush;
                    run.Background = InsertSpanBrush;
                    run.FontWeight = FontWeights.Bold;
                }
                else
                {
                    run.Foreground = ContextBrush;
                }
                break;
            default:
                run.Foreground = ContextBrush;
                break;
        }
        tb.Inlines.Add(run);
    }

    private static TextBlock Gutter(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = MonoFont,
            FontSize = 11,
            Foreground = GutterBrush,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 10, 0),
        };
    }

    private TextBlock StatusRow(string message)
    {
        return new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = GutterBrush,
            Margin = new Thickness(8, 8, 0, 0),
        };
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ── Parallax sticky hunk header ─────────────────────────────────────────

    private void ScheduleOffsetRefresh()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateHunkOffsets));
    }

    private void UpdateHunkOffsets()
    {
        _hunkHeaders.Clear();
        if (Rows.Children.Count == 0) { UpdateStickyHeader(); return; }
        for (int i = 0; i < _hunkHeaderBorders.Count && i < _hunkHeaderLabels.Count; i++)
        {
            var y = _hunkHeaderBorders[i].TranslatePoint(new Point(0, 0), Rows).Y;
            _hunkHeaders.Add((y, _hunkHeaderLabels[i]));
        }
        UpdateStickyHeader();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateStickyHeader();

    private void UpdateStickyHeader()
    {
        if (_hunkHeaders.Count == 0)
        {
            StickyHeader.Visibility = Visibility.Collapsed;
            return;
        }

        double top = Scroller.VerticalOffset;
        int idx = 0;
        for (int i = 0; i < _hunkHeaders.Count; i++)
        {
            if (_hunkHeaders[i].Offset <= top + 1) idx = i;
            else break;
        }

        // While the current hunk's header is still naturally visible at/below the
        // top edge, don't paint a duplicate pinned copy.
        if (_hunkHeaders[idx].Offset > top + 1)
        {
            StickyHeader.Visibility = Visibility.Collapsed;
            return;
        }

        StickyHeaderText.Text = _hunkHeaders[idx].Label;
        StickyHeader.Visibility = Visibility.Visible;

        // Parallax push-out: when the next header slides up under the pinned one,
        // nudge the current header out of the way so they trade places cleanly.
        double push = 0;
        if (idx + 1 < _hunkHeaders.Count)
        {
            double next = _hunkHeaders[idx + 1].Offset;
            double height = StickyHeader.ActualHeight > 0 ? StickyHeader.ActualHeight : 28;
            if (next - top < height)
                push = height - (next - top);
        }
        StickyTranslate.Y = -push;
    }
}
