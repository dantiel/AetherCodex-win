using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AetherCodex.Editor;
using AetherCodex.Util;

namespace AetherCodex;

public partial class GoToFileWindow : Window
{
    public sealed record Segment(string Text, bool Match);

    public sealed record FileMatch(string Path, string Glyph, IReadOnlyList<Segment> Segments);

    public string? SelectedPath { get; private set; }

    private readonly string _root;
    private readonly List<(string Path, string Glyph, string Rel)> _all = new();

    public GoToFileWindow(string projectRoot, string? currentFile)
    {
        InitializeComponent();
        _root = ResolveRoot(projectRoot, currentFile);
        ScopeText.Text = _root;
        Hint.Visibility = Visibility.Visible;
        Loaded += (_, _) => Query.Focus();
        Scan();
        RefreshResults();
    }

    private static string ResolveRoot(string projectRoot, string? currentFile)
    {
        if (Directory.Exists(projectRoot)) return projectRoot;
        if (!string.IsNullOrEmpty(currentFile))
            return Path.GetDirectoryName(currentFile) ?? Directory.GetCurrentDirectory();
        return Directory.GetCurrentDirectory();
    }

    private void Scan()
    {
        _all.Clear();
        if (!Directory.Exists(_root)) return;
        try
        {
            foreach (var file in ProjectSearch.EnumerateFiles(_root, new[] { "*" }, component: FilterComponent.FileChooser))
                _all.Add((file, GlyphFor(file), Path.GetRelativePath(_root, file).Replace('\\', '/')));
        }
        catch { /* unreadable tree */ }
    }

    private static string GlyphFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".rb" => "◆",
        ".cs" => "♯",
        ".swift" => "🕊",
        ".py" => "🐍",
        ".js" or ".ts" or ".jsx" or ".tsx" => "§",
        ".json" or ".yml" or ".yaml" => "≡",
        ".md" => "✎",
        ".haml" => "◇",
        ".sass" or ".scss" => "◇",
        ".html" or ".css" => "◇",
        _ => "·",
    };

    private void RefreshResults()
    {
        var q = Query.Text.Trim();
        List<FileMatch> items;
        if (q.Length == 0)
        {
            items = _all
                .OrderBy(f => f.Rel.Length)
                .ThenBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .Select(f => new FileMatch(f.Path, f.Glyph, new[] { new Segment(f.Rel, false) }))
                .ToList();
        }
        else
        {
            var scored = new List<(string Path, string Glyph, string Rel, double Score, IReadOnlyList<int> Matches)>();
            foreach (var f in _all)
            {
                var r = FuzzyMatcher.Match(q, f.Rel);
                if (r is { } m) scored.Add((f.Path, f.Glyph, f.Rel, m.Score, m.Matches));
            }
            items = scored
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Rel.Length)
                .ThenBy(x => x.Rel, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .Select(x => new FileMatch(x.Path, x.Glyph, BuildSegments(x.Rel, x.Matches)))
                .ToList();
        }
        Results.ItemsSource = items;
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
    }

    private static IReadOnlyList<Segment> BuildSegments(string rel, IReadOnlyList<int> matches)
    {
        var segs = new List<Segment>();
        int start = 0;
        foreach (var idx in matches)
        {
            if (idx > start) segs.Add(new Segment(rel.Substring(start, idx - start), false));
            segs.Add(new Segment(rel.Substring(idx, 1), true));
            start = idx + 1;
        }
        if (start < rel.Length) segs.Add(new Segment(rel.Substring(start), false));
        return segs;
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        Hint.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshResults();
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; Close(); return; }
        if (e.Key == Key.Enter) { AcceptSelection(); return; }
        if (e.Key == Key.Down && Results.Items.Count > 0)
            Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, Results.Items.Count - 1);
        else if (e.Key == Key.Up && Results.Items.Count > 0)
            Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (Results.SelectedItem is FileMatch match)
        {
            SelectedPath = match.Path;
            DialogResult = true;
        }
        Close();
    }
}