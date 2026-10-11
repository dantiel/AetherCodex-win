using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Runtime.InteropServices;
using AetherCodex.Bridge;
using AetherCodex.Editor;
using AetherCodex.Models;
using AetherCodex.Panels;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;

namespace AetherCodex;

public partial class MainWindow : Window
{
    private readonly RubyBridge _bridge = RubyBridge.Shared;
    private readonly AppSettings _settings = AppSettings.Load();
    private IReadOnlyList<SyntaxTheme> _themes = Array.Empty<SyntaxTheme>();
    private SyntaxTheme _theme = new();
    private LanguageDefinition _language = Languages.FromPath("x.txt");
    private OpenDocument? _activeDoc;
    private readonly List<OpenDocument> _documents = new();
    private bool _suppressDirty;
    private RegexHighlighter? _highlighter;
    private bool _sidebarOpen = true;
    private bool _pythiaOpen = true;
    private readonly MenuItem _themeMenu = new() { Header = "Theme" };

    // ── Hermetic chord table — window-scoped commands with default gestures ──
    private static readonly RoutedCommand NewFileCommand = new(nameof(NewFileCommand), typeof(MainWindow));
    private static readonly RoutedCommand OpenFileCommand = new(nameof(OpenFileCommand), typeof(MainWindow));
    private static readonly RoutedCommand OpenFolderCommand = new(nameof(OpenFolderCommand), typeof(MainWindow));
    private static readonly RoutedCommand SaveCommand = new(nameof(SaveCommand), typeof(MainWindow));
    private static readonly RoutedCommand SaveAsCommand = new(nameof(SaveAsCommand), typeof(MainWindow));
    private static readonly RoutedCommand GoToFileCommand = new(nameof(GoToFileCommand), typeof(MainWindow));
    private static readonly RoutedCommand FindInProjectCommand = new(nameof(FindInProjectCommand), typeof(MainWindow));
    private static readonly RoutedCommand CloseFileCommand = new(nameof(CloseFileCommand), typeof(MainWindow));
    private static readonly RoutedCommand ToggleSidebarCommand = new(nameof(ToggleSidebarCommand), typeof(MainWindow));
    private static readonly RoutedCommand TogglePythiaCommand = new(nameof(TogglePythiaCommand), typeof(MainWindow));
    private static readonly RoutedCommand TogglePreviewCommand = new(nameof(TogglePreviewCommand), typeof(MainWindow));
    private static readonly RoutedCommand ShowDebugCommand = new(nameof(ShowDebugCommand), typeof(MainWindow));
    private static readonly RoutedCommand CloseWindowCommand = new(nameof(CloseWindowCommand), typeof(MainWindow));
    private static readonly RoutedCommand SettingsCommand = new(nameof(SettingsCommand), typeof(MainWindow));

    private string _projectRoot = "";
    private MultiCursorController? _multi;

    private string CurrentFile => _activeDoc?.Path ?? "";

    public MainWindow()
    {
        InitializeComponent();

        LoadThemes();
        ApplyTheme(_theme);
        ApplyEditorSettings();
        UpdateWindowTitle();
        RailSymbolText.Text = _settings.RailSymbol;

        OverflowButton.ContextMenu = BuildOverflowMenu();

        // Incremental search (Ctrl+F) — AvalonEdit's built-in search panel.
        SearchPanel.Install(Editor.TextArea);

        // TextMate-style multi-cursor (Ctrl+W / Ctrl+Shift+W grow cursors).
        _multi = new MultiCursorController(Editor);

        Browser.FileOpened += path => _ = OpenFileAsync(path);

        // The Pythia header hosts the window caption buttons while visible;
        // route its requests back to the window-level handlers.
        Pythia.MinimizeRequested += (_, _) => OnMinimize(this, new RoutedEventArgs());
        Pythia.MaximizeRestoreRequested += (_, _) => OnMaximizeRestore(this, new RoutedEventArgs());
        Pythia.CloseRequested += (_, _) => OnClose(this, new RoutedEventArgs());

        SourceInitialized += (_, _) => ApplyNativeChrome();
        StateChanged += (_, _) => UpdateCaptionGlyph();
        Closed += (_, _) => { _bridge.Stop(); _settings.Save(); };
        Closing += OnWindowClosing;

        Editor.TextChanged += (_, _) => MarkDirty();
        RichEditor.DocumentChanged += (_, _) => MarkDirty();

        BindShortcuts();

        if (_settings.LastDirectory.Length > 0)
            SetProjectRoot(_settings.LastDirectory);

        if (_settings.AutoStartDaemon)
            _ = StartDaemonSafeAsync();
    }

    private void BindShortcuts()
    {
        void Bind(RoutedCommand cmd, KeyGesture gesture, ExecutedRoutedEventHandler handler)
        {
            CommandBindings.Add(new CommandBinding(cmd, handler));
            InputBindings.Add(new KeyBinding(cmd, gesture));
        }

        var ctrl = ModifierKeys.Control;
        Bind(NewFileCommand, new KeyGesture(Key.N, ctrl), (s, e) => OnNewFile(s, e));
        Bind(OpenFileCommand, new KeyGesture(Key.O, ctrl), (s, e) => OnOpenFile(s, e));
        Bind(OpenFolderCommand, new KeyGesture(Key.O, ctrl | ModifierKeys.Shift), (s, e) => OnOpenFolder(s, e));
        Bind(SaveCommand, new KeyGesture(Key.S, ctrl), (s, e) => OnSave(s, e));
        Bind(SaveAsCommand, new KeyGesture(Key.S, ctrl | ModifierKeys.Shift), (s, e) => OnSaveAs(s, e));
        Bind(GoToFileCommand, new KeyGesture(Key.P, ctrl), (s, e) => OnGoToFile(s, e));
        Bind(FindInProjectCommand, new KeyGesture(Key.F, ctrl | ModifierKeys.Shift), (s, e) => OnFindInProject(s, e));
        Bind(CloseFileCommand, new KeyGesture(Key.W, ctrl), (s, e) => OnCloseFile(s, e));
        Bind(ToggleSidebarCommand, new KeyGesture(Key.B, ctrl), (s, e) => OnToggleSidebar(s, e));
        Bind(TogglePythiaCommand, new KeyGesture(Key.J, ctrl), (s, e) => OnTogglePythia(s, e));
        Bind(TogglePreviewCommand, new KeyGesture(Key.V, ctrl | ModifierKeys.Shift), (s, e) => OnTogglePreview(s, e));
        Bind(ShowDebugCommand, new KeyGesture(Key.D, ctrl | ModifierKeys.Shift), (s, e) => OnShowDebug(s, e));
        Bind(SettingsCommand, new KeyGesture(Key.OemComma, ctrl), (s, e) => OnShowSettings(s, e));
        Bind(CloseWindowCommand, new KeyGesture(Key.F4, ModifierKeys.Alt), (s, e) => OnClose(s, e));
    }

    private void LoadThemes()
    {
        _themes = ThemeLoader.LoadAll();
        var wanted = _settings.Theme;
        _theme = _themes.FirstOrDefault(t => t.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                 ?? _themes.FirstOrDefault(t => t.IsDark)
                 ?? _theme;

        _themeMenu.Items.Clear();
        foreach (var theme in _themes)
        {
            var item = new MenuItem { Header = theme.Name, IsCheckable = true, IsChecked = theme == _theme };
            var captured = theme;
            item.Click += (_, _) => { _settings.Theme = captured.Name; _theme = captured; ApplyTheme(captured); CheckTheme(); };
            _themeMenu.Items.Add(item);
        }
    }

    private void CheckTheme()
    {
        foreach (MenuItem item in _themeMenu.Items)
            item.IsChecked = item.Header?.ToString() == _theme.Name;
    }

    private ContextMenu BuildOverflowMenu()
    {
        var menu = new ContextMenu();

        MenuItem Add(string header, RoutedEventHandler onClick, string? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += onClick;
            menu.Items.Add(item);
            return item;
        }

        Add("New File", OnNewFile, "Ctrl+N");
        Add("Open Folder…", OnOpenFolder, "Ctrl+Shift+O");
        Add("Open File…", OnOpenFile, "Ctrl+O");
        Add("Go to File…", OnGoToFile, "Ctrl+P");
        Add("Find in Project…", OnFindInProject, "Ctrl+Shift+F");
        menu.Items.Add(new Separator());
        Add("New Document…", OnNewDocument);
        menu.Items.Add(new Separator());
        Add("Save", OnSave, "Ctrl+S");
        Add("Save As…", OnSaveAs, "Ctrl+Shift+S");
        Add("Close File", OnCloseFile, "Ctrl+W");
        menu.Items.Add(new Separator());
        menu.Items.Add(_themeMenu);
        Add("Settings…", OnShowSettings, "Ctrl+,");
        Add("Æther Debug Window", OnShowDebug, "Ctrl+Shift+D");
        menu.Items.Add(new Separator());
        Add("Start / Restart daemon", OnRestartDaemon);
        Add("Stop daemon", OnStopDaemon);
        Add("Interrupt", OnInterrupt);
        menu.Items.Add(new Separator());
        Add("Exit", OnExit, "Alt+F4");

        return menu;
    }

    private void OpenOverflowMenu(object sender, RoutedEventArgs e)
    {
        if (OverflowButton.ContextMenu is not ContextMenu menu) return;
        menu.PlacementTarget = sender as UIElement ?? OverflowButton;
        menu.Placement = ReferenceEquals(sender, OverflowButton) ? PlacementMode.Top : PlacementMode.Bottom;
        menu.IsOpen = true;
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
        RichEditor.ApplyTheme(theme);
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

    private void ApplyEditorSettings()
    {
        Editor.FontSize = _settings.FontSize;
        Editor.FontFamily = new FontFamily(_settings.FontFamily);
        Editor.ShowLineNumbers = _settings.ShowLineNumbers;
        Editor.WordWrap = _settings.WordWrap;
        Editor.HorizontalScrollBarVisibility = _settings.WordWrap
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;

        var o = Editor.Options;
        o.IndentationSize = _settings.TabSize;
        o.ConvertTabsToSpaces = _settings.ConvertTabsToSpaces;
        o.ShowSpaces = _settings.ShowWhitespace;
        o.ShowTabs = _settings.ShowWhitespace;
        o.ShowEndOfLine = _settings.ShowWhitespace;
        o.HighlightCurrentLine = _settings.HighlightCurrentLine;
    }

    private void OnShowSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, _themes.Select(t => t.Name).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        bool themeChanged = !string.Equals(_settings.Theme, dialog.SelectedTheme, StringComparison.OrdinalIgnoreCase);

        _settings.Theme = dialog.SelectedTheme;
        _settings.FontSize = dialog.FontSizeValue;
        _settings.FontFamily = dialog.FontFamilyValue;
        _settings.ShowLineNumbers = dialog.ShowLineNumbers;
        _settings.WordWrap = dialog.WordWrap;
        _settings.TabSize = dialog.TabSizeValue;
        _settings.ConvertTabsToSpaces = dialog.ConvertTabsToSpaces;
        _settings.ShowWhitespace = dialog.ShowWhitespace;
        _settings.HighlightCurrentLine = dialog.HighlightCurrentLine;
        _settings.AutoStartDaemon = dialog.AutoStartDaemon;
        _settings.WindowTitle = dialog.WindowTitleValue;
        _settings.RailSymbol = dialog.RailSymbolValue;

        if (themeChanged)
        {
            _theme = _themes.FirstOrDefault(t => t.Name.Equals(_settings.Theme, StringComparison.OrdinalIgnoreCase)) ?? _theme;
            ApplyTheme(_theme);
            CheckTheme();
        }

        ApplyEditorSettings();
        UpdateWindowTitle();
        RailSymbolText.Text = _settings.RailSymbol;
        _settings.Save();
    }

    private async Task StartDaemonSafeAsync()
    {
        try { await _bridge.StartAsync(); }
        catch { /* daemon unavailable — the user can start it manually later */ }
    }

    private async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path)) return;

        var existing = _documents.FirstOrDefault(
            d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateDocument(existing);
            _ = ActivateContextAndSyncAsync(path);
            return;
        }

        try
        {
            var doc = new OpenDocument { Path = path };
            if (DocumentIO.IsRichDocument(path) || DocumentIO.IsMarkdown(path))
            {
                doc.IsRich = true;
                if (DocumentIO.IsMarkdown(path))
                {
                    // Render markdown to a FlowDocument (inverse of MarkdownSerializer),
                    // themed against the active palette so the editor shows it styled.
                    var markdown = File.ReadAllText(path, DetectEncoding(path));
                    doc.Markdown = markdown;
                    doc.Rich = MarkdownRenderer.Render(markdown, _theme.Foreground, _theme.PythiaCodeBackground, _theme.PythiaAccent);
                    doc.Language = "Markdown";
                }
                else
                {
                    doc.Rich = DocumentIO.Load(path);
                    DocumentIO.NormalizeAutoColors(doc.Rich);
                    doc.Language = "Rich Text";
                }
            }
            else
            {
                doc.Text = File.ReadAllText(path, DetectEncoding(path));
                doc.Language = Languages.FromPath(path).Name;
            }

            _documents.Add(doc);
            ActivateDocument(doc);

            // Open the file's project context in the daemon (polymorphic window) and
            // sync Pythia to that project's Mnemosyne history when the scope changed.
            _ = ActivateContextAndSyncAsync(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file:\n{ex.Message}", "ÆtherCodex", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Make <paramref name="doc"/> the active tab and render it into the editor surface.</summary>
    private void ActivateDocument(OpenDocument doc)
    {
        if (_activeDoc is not null && !ReferenceEquals(_activeDoc, doc))
            CommitActiveDocument();

        _activeDoc = doc;
        RenderDocument(doc);
        ApplyEditorSettings();
        UpdateFilePath();
        RebuildTabs();
    }

    /// <summary>Flush the live editor surface back into the active document's buffer.</summary>
    private void CommitActiveDocument()
    {
        if (_activeDoc is null) return;
        if (_activeDoc.IsRich)
        {
            if (_activeDoc.IsMarkdown)
            {
                _activeDoc.Markdown = RichEditor.GetMarkdown();
                _activeDoc.Rich = RichEditor.Document;
            }
            else
                _activeDoc.Rich = RichEditor.Document;
        }
        else
            _activeDoc.Text = Editor.Text;
    }

    private void RenderDocument(OpenDocument doc)
    {
        _suppressDirty = true;
        try
        {
            if (doc.IsRich)
            {
                if (doc.IsMarkdown)
                {
                    doc.Rich = MarkdownRenderer.Render(doc.Markdown, _theme.Foreground, _theme.PythiaCodeBackground, _theme.PythiaAccent);
                    RichEditor.LoadMarkdown(doc.Markdown, doc.Rich, _theme);
                }
                else
                    RichEditor.LoadDocument(doc.Rich ?? new FlowDocument());
                LanguageText.Text = doc.Language;
                PreviewButton.Visibility = Visibility.Collapsed;
                SetPreviewOpen(false);
                Editor.Visibility = Visibility.Collapsed;
                RichEditor.Visibility = Visibility.Visible;
            }
            else
            {
                Editor.Text = doc.Text;
                _language = Languages.FromPath(doc.Path);
                LanguageText.Text = _language.Name;
                ReattachHighlighter();
                Editor.ScrollToHome();

                // Markdown preview affordance.
                bool isMarkdown = _language.Key == "markdown";
                PreviewButton.Visibility = isMarkdown ? Visibility.Visible : Visibility.Collapsed;
                if (!isMarkdown) SetPreviewOpen(false);
                if (isMarkdown && Preview.Visibility == Visibility.Visible) RenderPreview();

                Editor.Visibility = Visibility.Visible;
                RichEditor.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _suppressDirty = false;
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
        Preview.Document = MarkdownRenderer.Render(Editor.Text, _theme.PythiaForeground, _theme.PythiaCodeBackground, _theme.PythiaAccent);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_activeDoc is null) return;
        CommitActiveDocument();

        var doc = _activeDoc;
        if (doc.Path.Length == 0) { OnSaveAs(sender, e); return; }

        try
        {
            PersistDocument(doc, doc.Path);
            AfterSave(doc);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnNewFile(object sender, RoutedEventArgs e)
    {
        var doc = new OpenDocument();
        _documents.Add(doc);
        ActivateDocument(doc);
        Editor.Focus();
    }

    /// <summary>
    /// Search scope for "Go to File". Prefers the opened project root, then the
    /// daemon-resolved `.aethercodex` root, then the active file's directory, and only
    /// finally the process working directory — so the finder is always project-scoped.
    /// </summary>
    private string ProjectRoot =>
        Directory.Exists(_projectRoot) ? _projectRoot
        : Directory.Exists(_settings.LastDirectory) ? _settings.LastDirectory
        : !string.IsNullOrEmpty(CurrentFile) ? (Path.GetDirectoryName(CurrentFile) ?? Directory.GetCurrentDirectory())
        : Directory.GetCurrentDirectory();

    private void OnGoToFile(object sender, RoutedEventArgs e)
    {
        var dialog = new GoToFileWindow(ProjectRoot, CurrentFile) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedPath is { } path)
            _ = OpenFileAsync(path);
    }

    private void OnFindInProject(object sender, RoutedEventArgs e)
    {
        string initial = "";
        var ta = Editor.TextArea;
        if (ta.Selection.Length > 0)
        {
            initial = Editor.Document.GetText(ta.Selection.SurroundingSegment.Offset, ta.Selection.Length);
        }
        else
        {
            var line = Editor.Document.GetLineByNumber(ta.Caret.Line);
            var text = Editor.Document.GetText(line.Offset, line.Length);
            initial = WordAtCaret(text, ta.Caret.Column - 1);
        }

        var dialog = new SearchInProjectWindow(ProjectRoot, CurrentFile, initial) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.OpenPath is { } path)
            _ = OpenFileAtAsync(path, dialog.OpenLine, dialog.OpenColumn, dialog.OpenLength);
    }

    private async Task OpenFileAtAsync(string path, int line, int column, int length)
    {
        await OpenFileAsync(path);
        if (Editor.Visibility != Visibility.Visible) return; // rich document: no code caret

        var ta = Editor.TextArea;
        var docLine = ta.Document.GetLineByNumber(Math.Clamp(line, 1, ta.Document.LineCount));
        int start = docLine.Offset + Math.Clamp(column - 1, 0, docLine.Length);
        int end = Math.Min(start + Math.Max(length, 0), docLine.Offset + docLine.Length);
        ta.Selection = Selection.Create(ta, start, end);
        ta.Caret.Offset = end;
        ta.Caret.BringCaretToView();
        Editor.Focus();
    }

    private static string WordAtCaret(string text, int column)
    {
        if (string.IsNullOrEmpty(text) || column < 0) return "";
        int start = column;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        int end = column;
        while (end < text.Length && IsWordChar(text[end])) end++;
        return text.Substring(start, end - start);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void OnNewDocument(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "New Rich Document",
            Filter = "Rich Text Format (*.rtf)|*.rtf|Word Document (*.docx)|*.docx",
            FileName = "Untitled.rtf",
            DefaultExt = ".rtf"
        };
        if (dialog.ShowDialog() != true) return;

        var doc = new OpenDocument
        {
            Path = dialog.FileName,
            IsRich = true,
            Language = "Rich Text",
            Rich = new FlowDocument()
        };
        _documents.Add(doc);
        ActivateDocument(doc);
        _ = ActivateContextAndSyncAsync(dialog.FileName);
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_activeDoc is null) return;
        CommitActiveDocument();

        var doc = _activeDoc;
        bool rich = doc.IsRich;
        var name = doc.Path.Length > 0
            ? Path.GetFileNameWithoutExtension(doc.Path) + (rich ? ".rtf" : ".txt")
            : (rich ? "Untitled.rtf" : "Untitled.txt");
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save As",
            Filter = rich
                ? "Rich Text Format (*.rtf)|*.rtf|Word Document (*.docx)|*.docx|Markdown (*.md)|*.md|Plain Text (*.txt)|*.txt"
                : "All files|*.*|Text (*.txt)|*.txt|Markdown (*.md)|*.md|C# (*.cs)|*.cs|Ruby (*.rb)|*.rb|Python (*.py)|*.py|JSON (*.json)|*.json",
            FileName = name
        };
        if (dialog.ShowDialog() != true) return;

        string path = dialog.FileName;
        try
        {
            if (rich)
            {
                bool targetMarkdown = DocumentIO.IsMarkdown(path);
                if (targetMarkdown)
                {
                    if (!doc.IsMarkdown)
                        doc.Markdown = MarkdownSerializer.ToMarkdown(doc.Rich ?? new FlowDocument());
                    File.WriteAllText(path, doc.Markdown, new UTF8Encoding(false));
                }
                else
                {
                    if (doc.IsMarkdown)
                        doc.Rich = MarkdownRenderer.Render(doc.Markdown, _theme.Foreground, _theme.PythiaCodeBackground, _theme.PythiaAccent);
                    DocumentIO.Save(path, doc.Rich ?? new FlowDocument());
                }
                doc.Language = targetMarkdown ? "Markdown" : "Rich Text";
            }
            else
            {
                File.WriteAllText(path, doc.Text, new UTF8Encoding(false));
                _language = Languages.FromPath(path);
                doc.Language = _language.Name;
                LanguageText.Text = _language.Name;
                ReattachHighlighter();
            }
            doc.Path = path;
            doc.Dirty = false;
            UpdateFilePath();
            RebuildTabs();
            _ = ActivateContextAndSyncAsync(path);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmCloseAll())
            e.Cancel = true;
    }

    /// <summary>Prompt for every dirty tab before the window closes.</summary>
    private bool ConfirmCloseAll()
    {
        CommitActiveDocument();
        foreach (var doc in _documents.ToList())
        {
            if (!doc.Dirty) continue;
            var result = MessageBox.Show(
                $"Save changes to {doc.DisplayName} before closing?",
                "ÆtherCodex",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            switch (result)
            {
                case MessageBoxResult.Yes:
                    if (doc.Path.Length == 0)
                    {
                        ActivateDocument(doc);
                        OnSaveAs(this, new RoutedEventArgs());
                        if (doc.Dirty) return false;
                    }
                    else
                    {
                        try { PersistDocument(doc, doc.Path); }
                        catch (Exception ex) { MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
                    }
                    break;
                case MessageBoxResult.No:
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private void OnCloseFile(object sender, RoutedEventArgs e)
    {
        if (_activeDoc is null) return;
        CloseDocument(_activeDoc);
    }

    private void CloseDocument(OpenDocument doc)
    {
        if (ReferenceEquals(doc, _activeDoc)) CommitActiveDocument();

        if (doc.Dirty)
        {
            var result = MessageBox.Show(
                $"Save changes to {doc.DisplayName} before closing?",
                "ÆtherCodex",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            switch (result)
            {
                case MessageBoxResult.Yes:
                    if (doc.Path.Length == 0)
                    {
                        ActivateDocument(doc);
                        OnSaveAs(this, new RoutedEventArgs());
                        if (doc.Dirty) return; // Save As cancelled
                    }
                    else
                    {
                        try { PersistDocument(doc, doc.Path); }
                        catch (Exception ex) { MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); return; }
                    }
                    break;
                case MessageBoxResult.No:
                    break;
                default:
                    return;
            }
        }

        int index = _documents.IndexOf(doc);
        bool wasActive = ReferenceEquals(doc, _activeDoc);
        if (wasActive) _activeDoc = null;
        _documents.Remove(doc);

        if (_documents.Count == 0)
        {
            ClearEditor();
            return;
        }

        if (wasActive)
            ActivateDocument(_documents[Math.Clamp(index, 0, _documents.Count - 1)]);
        else
            RebuildTabs();
    }

    private void ClearEditor()
    {
        _documents.Clear();
        _activeDoc = null;
        _suppressDirty = true;
        try
        {
            _language = Languages.FromPath("x.txt");
            LanguageText.Text = _language.Name;
            Editor.Text = "";
            RichEditor.NewDocument();
            Editor.Visibility = Visibility.Visible;
            RichEditor.Visibility = Visibility.Collapsed;
            PreviewButton.Visibility = Visibility.Collapsed;
            SetPreviewOpen(false);
            ReattachHighlighter();
        }
        finally
        {
            _suppressDirty = false;
        }
        UpdateFilePath();
        RebuildTabs();
    }

    /// <summary>Write a document to <paramref name="path"/> and mark it clean.</summary>
    private void PersistDocument(OpenDocument doc, string path)
    {
        if (doc.IsRich)
        {
            if (doc.IsMarkdown)
                File.WriteAllText(path, doc.Markdown, new UTF8Encoding(false));
            else
                DocumentIO.Save(path, doc.Rich ?? new FlowDocument());
        }
        else
            File.WriteAllText(path, doc.Text, new UTF8Encoding(false));
        doc.Path = path;
        doc.Dirty = false;
    }

    /// <summary>Refresh the chrome after a save (language label, highlighter, tab titles).</summary>
    private void AfterSave(OpenDocument doc)
    {
        if (ReferenceEquals(doc, _activeDoc))
        {
            if (!doc.IsRich)
            {
                _language = Languages.FromPath(doc.Path);
                doc.Language = _language.Name;
                LanguageText.Text = _language.Name;
                ReattachHighlighter();
            }
            else
            {
                doc.Language = DocumentIO.IsMarkdown(doc.Path) ? "Markdown" : "Rich Text";
                LanguageText.Text = doc.Language;
            }
        }
        UpdateFilePath();
        RebuildTabs();
    }

    /// <summary>Re-draw the tab strip; visible only when more than one file is open.</summary>
    private void RebuildTabs()
    {
        TabStrip.Children.Clear();
        TabBar.Visibility = _documents.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var doc in _documents)
            TabStrip.Children.Add(CreateTab(doc));
    }

    private ToggleButton CreateTab(OpenDocument doc)
    {
        bool active = ReferenceEquals(doc, _activeDoc);

        var title = new TextBlock
        {
            Text = doc.DisplayName,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, active ? "Foreground" : "MutedForeground");

        var close = new Button
        {
            Style = (Style)FindResource("TabCloseButton"),
            ToolTip = "Close",
            Margin = new Thickness(6, 0, 0, 0)
        };
        var glyph = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), Text = "\uE711", FontSize = 9 };
        close.Content = glyph;
        close.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true; // don't activate the tab
        close.Click += (_, _) => CloseDocument(doc);

        var panel = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        panel.Children.Add(close);
        panel.Children.Add(title);

        var tab = new ToggleButton
        {
            Style = (Style)FindResource("TabItemStyle"),
            Content = panel,
            IsChecked = active,
            Tag = doc
        };
        tab.Click += (_, _) => ActivateDocument(doc);
        return tab;
    }

    private void MarkDirty()
    {
        if (_suppressDirty || _activeDoc is null || _activeDoc.Dirty) return;
        _activeDoc.Dirty = true;
        UpdateFilePath();
        RebuildTabs();
    }

    private void UpdateFilePath()
    {
        if (_activeDoc is null)
            FilePathText.Text = "no file open";
        else if (_activeDoc.Path.Length == 0)
            FilePathText.Text = _activeDoc.Dirty ? "● untitled" : "untitled";
        else
            FilePathText.Text = (_activeDoc.Dirty ? "● " : "") + _activeDoc.Path;
    }

    private void UpdateWindowTitle()
    {
        string baseTitle = string.IsNullOrWhiteSpace(_settings.WindowTitle) ? "ÆtherCodex" : _settings.WindowTitle;
        string project = Directory.Exists(_projectRoot) ? new DirectoryInfo(_projectRoot).Name : "";
        WindowTitleText.Text = project.Length == 0 ? baseTitle : $"{baseTitle} — {project}";
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Open Project Folder" };
        if (dialog.ShowDialog() == true)
            SetProjectRoot(dialog.FolderName);
    }

    private void SetProjectRoot(string directory)
    {
        _projectRoot = directory;
        _settings.LastDirectory = directory;
        Browser.SetRoot(directory);
        UpdateWindowTitle();
        Title = WindowTitleText.Text;
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
            // The daemon's `chdir` requires a directory — a file path (the active
            // document) would seed AETHER_PROJECT_ROOT with a file and crash the
            // daemon at startup (Errno::EINVAL). Resolve to the parent dir first.
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Environment.SetEnvironmentVariable("AETHER_PROJECT_ROOT", dir);

            var ctx = await _bridge.ActivateContextAsync(path);
            var root = ctx?["project_root"]?.GetValue<string>();
            if (root is not null)
            {
                // Prefer the daemon-resolved root (nearest .aethercodex ancestor).
                Environment.SetEnvironmentVariable("AETHER_PROJECT_ROOT", root);
                _projectRoot = root;
                await Pythia.RestoreHistoryAsync(root);
            }
        }
        catch { /* daemon not started yet — will inherit root on start */ }
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Open File" };
        if (dialog.ShowDialog() == true)
            _ = OpenFileAsync(dialog.FileName);
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

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void UpdateCaptionGlyph()
    {
        var glyph = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        var tip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";

        EditorMaxRestoreGlyph.Text = glyph;
        EditorMaxRestoreButton.ToolTip = tip;
        Pythia.PythiaMaxRestoreGlyph.Text = glyph;
        Pythia.PythiaMaxRestoreButton.ToolTip = tip;

        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is not null)
            chrome.CornerRadius = new CornerRadius(WindowState == WindowState.Maximized ? 0 : 8);
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        _sidebarOpen = !_sidebarOpen;
        ApplySidebar();
    }

    private void OnTogglePythia(object sender, RoutedEventArgs e)
    {
        _pythiaOpen = !_pythiaOpen;
        ApplyPythia();
    }

    private void ApplySidebar()
    {
        SidebarColumn.MinWidth = _sidebarOpen ? 160 : 0;
        SidebarColumn.Width = _sidebarOpen ? new GridLength(260) : new GridLength(0);
        SidebarSplitterColumn.Width = _sidebarOpen ? new GridLength(6) : new GridLength(0);
        ExplorerRail.IsChecked = _sidebarOpen;
    }

    private void ApplyPythia()
    {
        PythiaColumn.MinWidth = _pythiaOpen ? 240 : 0;
        PythiaColumn.Width = _pythiaOpen ? new GridLength(400) : new GridLength(0);
        PythiaSplitterColumn.Width = _pythiaOpen ? new GridLength(6) : new GridLength(0);
        OracleRail.IsChecked = _pythiaOpen;
        // The middle titlebar hosts the window buttons only when the Pythia
        // sidebar is collapsed; its own header carries them otherwise.
        EditorCaptionButtons.Visibility = _pythiaOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Borderless drag (native Aero snap) + double-click maximize ──
    private const int WmNclButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    private Point _dragOrigin;
    private bool _dragStarted;

    private void DragRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        _dragOrigin = e.GetPosition(this);
        _dragStarted = false;
    }

    private void DragRegion_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStarted || e.LeftButton != MouseButtonState.Pressed || WindowState == WindowState.Maximized)
            return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragStarted = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, WmNclButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ── Native Windows 11 chrome (immersive dark mode, rounded corners, Mica) ──
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmcpRound = 2;
    private const int DwmsbtMainWindow = 2; // Mica

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyNativeChrome()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int on = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
            int corner = DwmcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            int backdrop = DwmsbtMainWindow;
            DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        }
        catch
        {
            // Pre-Windows 11: the corner/backdrop attributes are simply unavailable.
        }
    }
}