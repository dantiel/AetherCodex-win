using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AetherCodex;

public partial class GoToFileWindow : Window
{
    public sealed record FileMatch(string Path, string Glyph, string Display);

    public string? SelectedPath { get; private set; }

    private readonly string _root;
    private List<(string Path, string Glyph)> _all = new();

    public GoToFileWindow(string projectRoot, string? currentFile)
    {
        InitializeComponent();
        _root = ResolveRoot(projectRoot, currentFile);
        ScopeText.Text = _root;
        Hint.Visibility = Visibility.Visible;
        Loaded += (_, _) => { Query.Focus(); };
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
            foreach (var file in EnumerateFilesSafe(_root))
            {
                var rel = Path.GetRelativePath(_root, file);
                _all.Add((file, GlyphFor(file)));
            }
        }
        catch { }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".git", ".aether", ".vendor_bundle", ".bundle", "node_modules", "bin", "obj", ".vs", "vendored" };
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch { continue; }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (skip.Contains(name)) continue;
                try
                {
                    if (Directory.Exists(entry)) stack.Push(entry);
                    else _ = name; // files are yielded below
                }
                catch { continue; }
            }
            // Yield files for this directory (directories handled above).
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (skip.Contains(name)) continue;
                yield return file;
            }
        }
    }

    private static string GlyphFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".rb" => "◆", ".cs" => "♯", ".swift" => "🕊", ".py" => "🐍",
        ".js" or ".ts" or ".jsx" or ".tsx" => "§", ".json" or ".yml" or ".yaml" => "≡",
        ".md" => "✎", ".html" or ".css" or ".scss" => "◇", _ => "·",
    };

    private void RefreshResults()
    {
        var q = Query.Text.Trim();
        IEnumerable<(string Path, string Glyph)> matches = _all;
        if (q.Length > 0)
        {
            var parts = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            matches = _all.Where(f =>
                parts.All(p => f.Path.Contains(p, StringComparison.OrdinalIgnoreCase)));
        }
        var items = matches
            .OrderBy(f => f.Path.Length)
            .Take(200)
            .Select(f => new FileMatch(f.Path, f.Glyph, Path.GetRelativePath(_root, f.Path)))
            .ToList();
        Results.ItemsSource = items;
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
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