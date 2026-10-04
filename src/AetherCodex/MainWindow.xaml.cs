using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AetherCodex.Bridge;
using AetherCodex.Editor;
using AetherCodex.Models;
using AetherCodex.Panels;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;

namespace AetherCodex;

public partial class MainWindow : Window
{
    private readonly RubyBridge _bridge = RubyBridge.Shared;
    private readonly AppSettings _settings = AppSettings.Load();
    private IReadOnlyList<SyntaxTheme> _themes = Array.Empty<SyntaxTheme>();
    private SyntaxTheme _theme = new();
    private string _currentFile = "";
    private LanguageDefinition _language = Languages.FromPath("x.txt");
    private RegexHighlighter? _highlighter;

    public MainWindow()
    {
        InitializeComponent();

        LoadThemes();
        ApplyTheme(_theme);

        // Incremental search (Ctrl+F) — AvalonEdit's built-in search panel.
        SearchPanel.Install(Editor.TextArea);

        Browser.FileOpened += path => _ = OpenFileAsync(path);
        Closed += (_, _) => { _bridge.Stop(); _settings.Save(); };

        if (_settings.LastDirectory.Length > 0)
            SetProjectRoot(_settings.LastDirectory);
    }

    private void LoadThemes()
    {
        _themes = ThemeLoader.LoadAll();
        var wanted = _settings.Theme;
        _theme = _themes.FirstOrDefault(t => t.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                 ?? _themes.FirstOrDefault(t => t.IsDark)
                 ?? _theme;

        ThemeMenu.Items.Clear();
        foreach (var theme in _themes)
        {
            var item = new MenuItem { Header = theme.Name, IsCheckable = true, IsChecked = theme == _theme };
            var captured = theme;
            item.Click += (_, _) => { _settings.Theme = captured.Name; _theme = captured; ApplyTheme(captured); CheckTheme(); };
            ThemeMenu.Items.Add(item);
        }
    }

    private void CheckTheme()
    {
        foreach (MenuItem item in ThemeMenu.Items)
            item.IsChecked = item.Header?.ToString() == _theme.Name;
    }

    private void ApplyTheme(SyntaxTheme theme)
    {
        _theme = theme;
        ThemePalette.Apply(theme);

        var bg = new SolidColorBrush(theme.Background);
        var fg = new SolidColorBrush(theme.Foreground);
        var ui = ThemePalette.Build(theme);

        Editor.Background = bg;
        Editor.Foreground = fg;
        Editor.LineNumbersForeground = new SolidColorBrush(ui.MutedForeground);
        Editor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(theme.PythiaAccent);

        Pythia.ApplyTheme(theme);
        ReattachHighlighter();
    }

    private void ReattachHighlighter()
    {
        var transformers = Editor.TextArea.TextView.LineTransformers;
        if (_highlighter is not null)
            transformers.Remove(_highlighter);
        _highlighter = new RegexHighlighter(_theme, _language);
        transformers.Add(_highlighter);
        Editor.TextArea.TextView.Redraw();
    }

    private async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = File.ReadAllText(path, DetectEncoding(path));
            Editor.Text = text;
            _currentFile = path;
            _language = Languages.FromPath(path);
            FilePathText.Text = path;
            LanguageText.Text = _language.Name;
            Editor.FontSize = _settings.FontSize;
            Editor.WordWrap = _settings.WordWrap;
            ReattachHighlighter();
            Editor.ScrollToHome();

            // Markdown preview affordance.
            bool isMarkdown = _language.Key == "markdown";
            PreviewButton.Visibility = isMarkdown ? Visibility.Visible : Visibility.Collapsed;
            if (!isMarkdown) SetPreviewOpen(false);
            if (isMarkdown && Preview.Visibility == Visibility.Visible) RenderPreview();

            // Open the file's project context in the daemon (polymorphic window) and
            // sync Pythia to that project's Mnemosyne history when the scope changed.
            _ = ActivateContextAndSyncAsync(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file:\n{ex.Message}", "ÆtherCodex", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static Encoding DetectEncoding(string path)
    {
        using var fs = File.OpenRead(path);
        var bom = new byte[4];
        int read = fs.Read(bom, 0, 4);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return new UTF8Encoding(true);
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        return new UTF8Encoding(false);
    }

    private bool _previewOpen;

    private void OnTogglePreview(object sender, RoutedEventArgs e) => SetPreviewOpen(!_previewOpen);

    private void SetPreviewOpen(bool open)
    {
        _previewOpen = open;
        if (open)
        {
            PreviewSplitterColumn.Width = new GridLength(4);
            PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
            PreviewSplitter.Visibility = Visibility.Visible;
            Preview.Visibility = Visibility.Visible;
            RenderPreview();
        }
        else
        {
            PreviewSplitterColumn.Width = new GridLength(0);
            PreviewColumn.Width = new GridLength(0);
            PreviewSplitter.Visibility = Visibility.Collapsed;
            Preview.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderPreview()
    {
        if (_language.Key != "markdown") return;
        Preview.Document = MarkdownRenderer.Render(Editor.Text, _theme.PythiaForeground, _theme.PythiaCodeBackground);
    }

    private async void OnGoToFile(object sender, RoutedEventArgs e)
    {
        var win = new GoToFileWindow(_currentFile.Length > 0 ? _currentFile : _settings.LastDirectory) { Owner = this };
        if (win.ShowDialog() == true && win.SelectedPath is { } path)
            await OpenFileAsync(path);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Open Project Folder" };
        if (dialog.ShowDialog() == true)
            SetProjectRoot(dialog.FolderName);
    }

    private void SetProjectRoot(string directory)
    {
        _settings.LastDirectory = directory;
        Browser.SetRoot(directory);
        Title = $"ÆtherCodex — {directory}";
        _ = ActivateContextAndSyncAsync(directory);
    }

    /// <summary>
    /// Re-point the daemon at the nearest project root for <paramref name="path"/>
    /// (file or directory), then ask Pythia to reconcile its conversation with that
    /// project's Mnemosyne history. The rebuild itself is gated inside
    /// <see cref="PythiaPanel.RestoreHistoryAsync"/> — only "no history yet" or
    /// "Mnemosyne newer than Pythia's log" triggers an actual re-render.
    /// </summary>
    private async Task ActivateContextAndSyncAsync(string path)
    {
        try
        {
            Environment.SetEnvironmentVariable("AETHER_PROJECT_ROOT", path);
            var ctx = await _bridge.ActivateContextAsync(path);
            var root = ctx?["project_root"]?.GetValue<string>();
            if (root is not null)
                await Pythia.RestoreHistoryAsync(root);
        }
        catch { /* daemon not started yet — will inherit root on start */ }
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Open File" };
        if (dialog.ShowDialog() == true)
            _ = OpenFileAsync(dialog.FileName);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_currentFile.Length == 0) return;
        try { File.WriteAllText(_currentFile, Editor.Text, new UTF8Encoding(false)); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private DebugWindow? _debug;
    private void OnShowDebug(object sender, RoutedEventArgs e)
    {
        _debug ??= new DebugWindow(_bridge);
        _debug.Show();
        _debug.Activate();
    }

    private async void OnRestartDaemon(object sender, RoutedEventArgs e)
    {
        try { await _bridge.RestartAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Daemon", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnStopDaemon(object sender, RoutedEventArgs e) => _bridge.Stop();

    private void OnInterrupt(object sender, RoutedEventArgs e) => _ = _bridge.InterruptAsync();
}