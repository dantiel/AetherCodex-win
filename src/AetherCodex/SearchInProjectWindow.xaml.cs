using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AetherCodex.Editor;
using AetherCodex.Models;

namespace AetherCodex;

public partial class SearchInProjectWindow : Window
{
    public sealed record ResultRow(string Glyph, string Header, string Snippet, string File, int Line, int Column, int Length);

    public string? OpenPath { get; private set; }
    public int OpenLine { get; private set; }
    public int OpenColumn { get; private set; }
    public int OpenLength { get; private set; }

    private readonly string _root;
    private readonly string? _currentFile;
    private readonly SearchHistory _history;
    private readonly DispatcherTimer _debounce;
    private string? _folderScope;
    private bool _suppress;

    public SearchInProjectWindow(string projectRoot, string? currentFile, string initialQuery = "")
    {
        InitializeComponent();
        _root = projectRoot;
        _currentFile = currentFile;
        _history = SearchHistory.Load();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };

        ScopeBox.SelectedIndex = 0;
        Query.Text = initialQuery;
        Query.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnQueryChanged));

        Loaded += (_, _) =>
        {
            Query.Focus();
            PopulateHistory();
            if (initialQuery.Length > 0) RunSearch();
        };
    }

    private void PopulateHistory()
    {
        Query.ItemsSource = _history.QueriesFor(_root, RegexToggle.IsChecked == true);
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        Hint.Visibility = string.IsNullOrEmpty(Query.Text) ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; Close(); return; }
        if (e.Key == Key.Enter) { _debounce.Stop(); RunSearch(); e.Handled = true; return; }
        if (e.Key == Key.Down && Results.Items.Count > 0)
        {
            Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, Results.Items.Count - 1);
            Results.ScrollIntoView(Results.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && Results.Items.Count > 0)
        {
            Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0);
            Results.ScrollIntoView(Results.SelectedItem);
            e.Handled = true;
        }
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        PopulateHistory();
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (ScopeBox.SelectedIndex == 2)
        {
            _suppress = true;
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Search folder" };
            if (dialog.ShowDialog() == true)
                _folderScope = dialog.FolderName;
            else
                ScopeBox.SelectedIndex = 0;
            _suppress = false;
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private static IReadOnlyList<string> ParseFileTypes(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new[] { "*" };
        var tokens = input.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var globs = new List<string>();
        foreach (var raw in tokens)
        {
            var t = raw.Trim();
            if (t == "*") { globs.Add("*"); continue; }
            if (t.StartsWith("*.")) { globs.Add(t); continue; }
            if (t.StartsWith('.')) { globs.Add("*" + t); continue; }

            var lang = Languages.All.FirstOrDefault(l =>
                l.Key.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                l.Name.Equals(t, StringComparison.OrdinalIgnoreCase));
            if (lang != null)
            {
                foreach (var ext in lang.Extensions)
                    globs.Add(ext.StartsWith('.') ? "*" + ext : ext);
                continue;
            }

            globs.Add("*." + t.TrimStart('.'));
        }
        return globs.Distinct().ToList();
    }

    private void RunSearch()
    {
        var q = Query.Text;
        if (string.IsNullOrWhiteSpace(q))
        {
            Results.ItemsSource = null;
            Footer.Text = string.Empty;
            return;
        }

        var globs = ParseFileTypes(FileTypes.Text);
        IEnumerable<string> files;
        string scopeLabel;

        if (ScopeBox.SelectedIndex == 1)
        {
            files = _currentFile != null && File.Exists(_currentFile)
                ? new[] { _currentFile }
                : Array.Empty<string>();
            scopeLabel = _currentFile ?? "no file";
        }
        else if (ScopeBox.SelectedIndex == 2)
        {
            files = _folderScope != null && Directory.Exists(_folderScope)
                ? ProjectSearch.EnumerateFiles(_folderScope, globs)
                : Array.Empty<string>();
            scopeLabel = _folderScope ?? "no folder";
        }
        else
        {
            files = ProjectSearch.EnumerateFiles(_root, globs);
            scopeLabel = _root;
        }

        var options = new ProjectSearch.Options(
            q,
            RegexToggle.IsChecked == true,
            CaseToggle.IsChecked == true,
            WordToggle.IsChecked == true);

        var rows = new List<ResultRow>();
        foreach (var file in files)
        {
            if (rows.Count >= ProjectSearch.MaxResults) break;
            try
            {
                foreach (var m in ProjectSearch.MatchFile(file, options))
                {
                    rows.Add(new ResultRow(
                        m.Glyph,
                        $"{Rel(_root, m.File)}:{m.Line}",
                        m.LineText,
                        m.File, m.Line, m.Column, m.Length));
                    if (rows.Count >= ProjectSearch.MaxResults) break;
                }
            }
            catch { /* unreadable file */ }
        }

        Results.ItemsSource = rows;
        Footer.Text = rows.Count >= ProjectSearch.MaxResults
            ? $"{rows.Count}+ matches in {scopeLabel}"
            : $"{rows.Count} match{(rows.Count == 1 ? "" : "es")} in {scopeLabel}";
        if (rows.Count > 0) Results.SelectedIndex = 0;

        _history.Remember(_root, q, options.Regex);
        _history.Save();
        PopulateHistory();
    }

    private static string Rel(string root, string file)
    {
        try { return Path.GetRelativePath(root, file); }
        catch { return file; }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (Results.SelectedItem is ResultRow row)
        {
            OpenPath = row.File;
            OpenLine = row.Line;
            OpenColumn = row.Column;
            OpenLength = row.Length;
            DialogResult = true;
        }
        Close();
    }
}
