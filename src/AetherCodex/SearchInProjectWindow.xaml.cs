using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AetherCodex.Editor;
using AetherCodex.Models;

namespace AetherCodex;

public partial class SearchInProjectWindow : Window
{
    public sealed record ResultRow(string Glyph, string Header, string Snippet, string File, int Line, int Column, int Length);

    /// <summary>Raised (modeless) when the user commits a result; the window closes itself.</summary>
    public event Action<ResultRow>? ResultChosen;

    public string? OpenPath { get; private set; }
    public int OpenLine { get; private set; }
    public int OpenColumn { get; private set; }
    public int OpenLength { get; private set; }

    private readonly string _root;
    private readonly string? _currentFile;
    private readonly SearchHistory _history;
    private CancellationTokenSource? _searchCts;
    private string? _folderScope;
    private bool _suppress;
    private bool _initialized;

    public SearchInProjectWindow(string projectRoot, string? currentFile, string initialQuery = "")
    {
        InitializeComponent();
        _root = projectRoot;
        _currentFile = currentFile;
        _history = SearchHistory.Load();

        ScopeBox.SelectedIndex = 0;
        Footer.Text = "Press Enter to search";

        Query.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnQueryChanged));
        RestoreLastState();
        if (initialQuery.Length > 0)
            Query.Text = initialQuery;

        Loaded += (_, _) =>
        {
            Query.Focus();
            PopulateHistory();
        };

        _initialized = true;
    }

    /// <summary>Rehydrate the previous search run — query, options and result list.</summary>
    private void RestoreLastState()
    {
        var last = _history.Last;
        if (last is null) return;

        RegexToggle.IsChecked = last.Regex;
        CaseToggle.IsChecked = last.CaseSensitive;
        WordToggle.IsChecked = last.WholeWord;
        ScopeBox.SelectedIndex = Math.Clamp(last.Scope, 0, 2);
        _folderScope = last.FolderPath;
        FileTypes.Text = last.FileTypes;
        Query.Text = last.Query;
        Footer.Text = last.Footer;

        if (last.Results.Count > 0)
        {
            Results.ItemsSource = last.Results
                .Select(r => new ResultRow(r.Glyph, r.Header, r.Snippet, r.File, r.Line, r.Column, r.Length))
                .ToList();
        }
    }

    private void PopulateHistory()
    {
        Query.ItemsSource = _history.QueriesFor(_root, RegexToggle.IsChecked == true);
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        Hint.Visibility = string.IsNullOrEmpty(Query.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }
        if (e.Key == Key.Enter) { RunSearch(); e.Handled = true; return; }
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
        if (!_initialized) return;
        PopulateHistory();
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _suppress) return;
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

    private async void RunSearch()
    {
        var q = Query.Text;
        if (string.IsNullOrWhiteSpace(q))
        {
            Results.ItemsSource = null;
            Footer.Text = "Press Enter to search";
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

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var token = cts.Token;

        Footer.Text = $"Searching {scopeLabel}…";

        List<ResultRow> rows;
        try
        {
            rows = await Task.Run(() =>
            {
                var list = new List<ResultRow>();
                foreach (var file in files)
                {
                    if (token.IsCancellationRequested) break;
                    if (list.Count >= ProjectSearch.MaxResults) break;
                    try
                    {
                        foreach (var m in ProjectSearch.MatchFile(file, options, token))
                        {
                            if (token.IsCancellationRequested) break;
                            list.Add(new ResultRow(
                                m.Glyph,
                                $"{Rel(_root, m.File)}:{m.Line}",
                                m.LineText,
                                m.File, m.Line, m.Column, m.Length));
                            if (list.Count >= ProjectSearch.MaxResults) break;
                        }
                    }
                    catch { /* unreadable file */ }
                }
                return list;
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts != _searchCts) return; // superseded by a newer search

        Results.ItemsSource = rows;
        Footer.Text = rows.Count >= ProjectSearch.MaxResults
            ? $"{rows.Count}+ matches in {scopeLabel}"
            : $"{rows.Count} match{(rows.Count == 1 ? "" : "es")} in {scopeLabel}";
        if (rows.Count > 0) Results.SelectedIndex = 0;

        _history.Last = new SearchHistory.LastState
        {
            Query = q,
            Regex = options.Regex,
            CaseSensitive = options.CaseSensitive,
            WholeWord = options.WholeWord,
            Scope = ScopeBox.SelectedIndex,
            FolderPath = _folderScope,
            FileTypes = FileTypes.Text,
            Footer = Footer.Text,
            Results = rows.Select(r => new SearchHistory.LastResult
            {
                Glyph = r.Glyph,
                Header = r.Header,
                Snippet = r.Snippet,
                File = r.File,
                Line = r.Line,
                Column = r.Column,
                Length = r.Length,
            }).ToList(),
        };

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
            ResultChosen?.Invoke(row);
        }
        Close();
    }
}