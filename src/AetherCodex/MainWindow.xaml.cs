using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AetherCodex.Bridge;
using AetherCodex.Editor;
using AetherCodex.Git;
using AetherCodex.Models;
using AetherCodex.Panels;
using AetherCodex.Spreadsheet;
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

    // File watching: one FileSystemWatcher per open real file, plus the last-known
    // disk signature used to distinguish external edits from our own writes.
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Ticks, long Length)> _diskState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _reloadAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingReloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _syncingLanguage;
    private DocumentColorizingTransformer? _highlighter;
    private GutterMargin? _gutter;
    private bool _sidebarOpen = false;
    private string _gitRoot = "";
    private Favourites _favourites = Favourites.Load();
    private bool _pythiaOpen = true;
    private readonly MenuItem _themeMenu = new() { Header = "Theme" };
    private SearchInProjectWindow? _searchWindow;
    private SearchPanel? _searchPanel;
    private bool _linkExplorer;

    // ── Hermetic chord table — window-scoped commands with default gestures ──
    private static readonly RoutedCommand NewFileCommand = new(nameof(NewFileCommand), typeof(MainWindow));
    private static readonly RoutedCommand NewWindowCommand = new(nameof(NewWindowCommand), typeof(MainWindow));
    private static readonly RoutedCommand OpenFileCommand = new(nameof(OpenFileCommand), typeof(MainWindow));
    private static readonly RoutedCommand OpenFolderCommand = new(nameof(OpenFolderCommand), typeof(MainWindow));
    private static readonly RoutedCommand SaveCommand = new(nameof(SaveCommand), typeof(MainWindow));
    private static readonly RoutedCommand SaveAsCommand = new(nameof(SaveAsCommand), typeof(MainWindow));
    private static readonly RoutedCommand GoToFileCommand = new(nameof(GoToFileCommand), typeof(MainWindow));
    private static readonly RoutedCommand FindInProjectCommand = new(nameof(FindInProjectCommand), typeof(MainWindow));
    private static readonly RoutedCommand FindInFileCommand = new(nameof(FindInFileCommand), typeof(MainWindow));
    private static readonly RoutedCommand FindInFolderCommand = new(nameof(FindInFolderCommand), typeof(MainWindow));
    private static readonly RoutedCommand FindNextCommand = new(nameof(FindNextCommand), typeof(MainWindow));
    private static readonly RoutedCommand FindPreviousCommand = new(nameof(FindPreviousCommand), typeof(MainWindow));
    private static readonly RoutedCommand UseSelectionForFindCommand = new(nameof(UseSelectionForFindCommand), typeof(MainWindow));
    private static readonly RoutedCommand CloseFileCommand = new(nameof(CloseFileCommand), typeof(MainWindow));
    private static readonly RoutedCommand ToggleSidebarCommand = new(nameof(ToggleSidebarCommand), typeof(MainWindow));
    private static readonly RoutedCommand TogglePythiaCommand = new(nameof(TogglePythiaCommand), typeof(MainWindow));
    private static readonly RoutedCommand TogglePreviewCommand = new(nameof(TogglePreviewCommand), typeof(MainWindow));
    private static readonly RoutedCommand ShowDebugCommand = new(nameof(ShowDebugCommand), typeof(MainWindow));
    private static readonly RoutedCommand CloseWindowCommand = new(nameof(CloseWindowCommand), typeof(MainWindow));
    private static readonly RoutedCommand SettingsCommand = new(nameof(SettingsCommand), typeof(MainWindow));
    private static readonly RoutedCommand BrowserTabCommand = new(nameof(BrowserTabCommand), typeof(MainWindow));
    private static readonly RoutedCommand ReloadBrowserCommand = new(nameof(ReloadBrowserCommand), typeof(MainWindow));

    private string _projectRoot = "";
    private string _bridgeBoundRoot = "";
    private MultiCursorController? _multi;

    /// <summary>The browser document currently loaded into the shared <c>BrowserView</c> surface.</summary>
    private OpenDocument? _browserLoadedDoc;

    // Font-size zoom: session-scoped, relative to the configured base size.
    private const double EditorMinFontSize = 6.0;
    private const double EditorMaxFontSize = 72.0;
    private const double EditorZoomStep = 1.1;
    private readonly DispatcherTimer _zoomTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };
    private readonly DispatcherTimer _markdownRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private double EditorBaseFontSize => _settings.FontSize > 0 ? _settings.FontSize : 14.0;

    private string CurrentFile => _activeDoc?.Path ?? "";

    public MainWindow()
    {
        InitializeComponent();

        _linkExplorer = _settings.LinkExplorerToFile;

        LoadThemes();
        ApplyTheme(_theme);
        ApplyEditorSettings();
        ApplyFontSettings();
        UpdateWindowTitle();
        RailSymbolText.Text = _settings.RailSymbol;
        UpdateLanguageCombo();
        ExplorerToggle.IsChecked = true; // establish initial sidebar mode (Files) after both toggles exist
        ApplySidebar(); // notepad-first: sidebar collapsed until the user summons it

        OverflowButton.ContextMenu = BuildOverflowMenu();
        SetupTabStripDrag();

        // Incremental search (Alt+S forward / Alt+Shift+S backward) — AvalonEdit's built-in
        // search panel. Ctrl+F is reclaimed for the full find-in-file search below, so the
        // panel's default Ctrl+F keybinding is removed.
        _searchPanel = SearchPanel.Install(Editor.TextArea);
        foreach (var binding in Editor.TextArea.InputBindings.OfType<KeyBinding>()
                     .Where(b => b.Command == ApplicationCommands.Find).ToList())
            Editor.TextArea.InputBindings.Remove(binding);

        // TextMate-style multi-cursor (Ctrl+W / Ctrl+Shift+W grow cursors).
        _multi = new MultiCursorController(Editor);

        // Font-size zoom: Ctrl+wheel / Ctrl+± / Ctrl+0 with a floating indicator.
        Editor.PreviewMouseWheel += OnEditorPreviewMouseWheel;
        Editor.PreviewKeyDown += OnEditorPreviewKeyDown;
        _zoomTimer.Tick += (_, _) => ZoomPopup.IsOpen = false;

        Browser.FileOpened += path => _ = OpenFileAsync(path);
        Browser.RevealInExplorer += RevealInExplorer;
        Browser.RenameRequested += OnBrowserRename;
        Browser.DeleteRequested += OnBrowserDelete;
        Browser.DuplicateRequested += OnBrowserDuplicate;
        Browser.NewItemRequested += OnBrowserNewItem;
        Browser.FavouriteRequested += OnBrowserAddFavourite;

        GitPanel.DiffRequested += OpenDiffAsync;
        GitPanel.FileOpenRequested += path => _ = OpenFileAsync(path);

        FavPanel.OpenRequested += OpenFavourite;
        FavPanel.AddRequested += AddFavourite;
        FavPanel.RemoveRequested += RemoveFavourite;
        FavPanel.RefreshRequested += () => _ = RefreshProjectsAsync();

        // Rendered markdown links open as editor tabs (file, file://, or web) — not the OS browser.
        MarkdownRenderer.FileLinkRequested += (path, line, col) => _ = OpenFileAtAsync(path, line, col, 0);
        MarkdownRenderer.WebLinkRequested += target => _ = OpenLinkTargetAsync(target);

        // The Pythia header hosts the window caption buttons while visible;
        // route its requests back to the window-level handlers.
        Pythia.MinimizeRequested += (_, _) => OnMinimize(this, new RoutedEventArgs());
        Pythia.MaximizeRestoreRequested += (_, _) => OnMaximizeRestore(this, new RoutedEventArgs());
        Pythia.CloseRequested += (_, _) => OnClose(this, new RoutedEventArgs());

        SourceInitialized += (_, _) => ApplyNativeChrome();
        StateChanged += (_, _) => UpdateCaptionGlyph();
        Closed += (_, _) => { DisposeWatchers(); _settings.Save(); if (IsLastMainWindow()) _bridge.Stop(); };

        // Re-wrap rendered markdown when the window, or any pane that bounds a
        // markdown surface (sidebar / preview / Pythia columns), changes size.
        SizeChanged += (_, _) => ScheduleMarkdownRefresh();
        RichEditor.SizeChanged += (_, _) => ScheduleMarkdownRefresh();
        Pythia.SizeChanged += (_, _) => ScheduleMarkdownRefresh();
        _markdownRefreshTimer.Tick += (_, _) => { _markdownRefreshTimer.Stop(); RefreshMarkdownSurfaces(); };
        _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); ProcessPendingReloads(); };
        Closing += OnWindowClosing;

        Editor.TextChanged += (_, _) => MarkDirty();
        RichEditor.DocumentChanged += (_, _) => MarkDirty();
        RichEditor.SourceModeChanged += (_, _) => UpdateSourceButton();
        SheetEditor.Modified += MarkDirty;

        BrowserView.TitleChanged += (_, title) =>
        {
            if (_activeDoc is { IsBrowser: true } doc)
            {
                doc.BrowserTitle = title;
                UpdateFilePath();
                RebuildTabs();
            }
        };
        BrowserView.UrlChanged += (_, url) =>
        {
            if (_activeDoc is { IsBrowser: true } doc)
            {
                doc.Url = url;
                UpdateFilePath();
            }
        };

        // The daemon drives the editor's browser tabs through this host-bridge.
        _bridge.HostCallHandler = HandleHostCall;

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
        Bind(NewWindowCommand, new KeyGesture(Key.N, ctrl | ModifierKeys.Shift), (s, e) => OnNewWindow(s, e));
        Bind(OpenFileCommand, new KeyGesture(Key.O, ctrl), (s, e) => OnOpenFile(s, e));
        Bind(OpenFolderCommand, new KeyGesture(Key.O, ctrl | ModifierKeys.Shift), (s, e) => OnOpenFolder(s, e));
        Bind(SaveCommand, new KeyGesture(Key.S, ctrl), (s, e) => OnSave(s, e));
        Bind(SaveAsCommand, new KeyGesture(Key.S, ctrl | ModifierKeys.Shift), (s, e) => OnSaveAs(s, e));
        Bind(GoToFileCommand, new KeyGesture(Key.P, ctrl), (s, e) => OnGoToFile(s, e));
        Bind(FindInFileCommand, new KeyGesture(Key.F, ctrl), (s, e) => OnFindInFile(s, e));
        Bind(FindInProjectCommand, new KeyGesture(Key.F, ctrl | ModifierKeys.Shift), (s, e) => OnFindInProject(s, e));
        Bind(FindInFolderCommand, new KeyGesture(Key.F, ctrl | ModifierKeys.Alt), (s, e) => OnFindInFolder(s, e));
        Bind(FindNextCommand, new KeyGesture(Key.S, ModifierKeys.Alt), (s, e) => OnFindNext(s, e));
        Bind(FindPreviousCommand, new KeyGesture(Key.S, ModifierKeys.Alt | ModifierKeys.Shift), (s, e) => OnFindPrevious(s, e));
        Bind(UseSelectionForFindCommand, new KeyGesture(Key.E, ModifierKeys.Alt), (s, e) => OnUseSelectionForFind(s, e));
        Bind(CloseFileCommand, new KeyGesture(Key.W, ctrl), (s, e) => OnCloseFile(s, e));
        Bind(ToggleSidebarCommand, new KeyGesture(Key.B, ctrl), (s, e) => OnToggleSidebar(s, e));
        Bind(TogglePythiaCommand, new KeyGesture(Key.J, ctrl), (s, e) => OnTogglePythia(s, e));
        Bind(TogglePreviewCommand, new KeyGesture(Key.V, ctrl | ModifierKeys.Shift), (s, e) => OnTogglePreview(s, e));
        Bind(ShowDebugCommand, new KeyGesture(Key.D, ctrl | ModifierKeys.Shift), (s, e) => OnShowDebug(s, e));
        Bind(SettingsCommand, new KeyGesture(Key.OemComma, ctrl), (s, e) => OnShowSettings(s, e));
        Bind(BrowserTabCommand, new KeyGesture(Key.B, ctrl | ModifierKeys.Shift), (s, e) => OnNewBrowserTab(s, e));
        Bind(ReloadBrowserCommand, new KeyGesture(Key.F5), (s, e) => OnBrowserReload(s, e));
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
        Add("New Window", OnNewWindow, "Ctrl+Shift+N");
        Add("New Browser Tab", OnNewBrowserTab, "Ctrl+Shift+B");
        Add("Open Folder…", OnOpenFolder, "Ctrl+Shift+O");
        Add("Open File…", OnOpenFile, "Ctrl+O");
        Add("Go to File…", OnGoToFile, "Ctrl+P");
        Add("Find in Current File…", OnFindInFile, "Ctrl+F");
        Add("Find in Project…", OnFindInProject, "Ctrl+Shift+F");
        Add("Find in Folder…", OnFindInFolder, "Ctrl+Alt+F");
        Add("Use Selection for Find", OnUseSelectionForFind, "Alt+E");
        var linkExplorer = Add("Link Explorer to Active File", OnToggleLinkExplorer);
        linkExplorer.IsCheckable = true;
        linkExplorer.IsChecked = _linkExplorer;
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

    private void OnToggleLinkExplorer(object sender, RoutedEventArgs e)
    {
        _linkExplorer = sender is MenuItem { IsChecked: true };
        _settings.LinkExplorerToFile = _linkExplorer;
        _settings.Save();
        if (_linkExplorer && _activeDoc is { Path.Length: > 0 } doc)
            Browser.Reveal(doc.Path);
    }

    /// <summary>Keep exactly one sidebar mode (Explorer / Git) engaged and swap the surface.</summary>
    private void OnSidebarModeChanged(object sender, RoutedEventArgs e)
    {
        // Three mutually-exclusive sidebar modes: Files / Git / Favourites.
        var toggles = new[] { ExplorerToggle, GitToggle, FavouritesToggle };
        var clicked = (ToggleButton)sender;

        if (clicked.IsChecked == true)
        {
            foreach (var t in toggles)
                if (!ReferenceEquals(t, clicked)) t.IsChecked = false;
        }
        else if (toggles.All(t => t.IsChecked != true))
        {
            clicked.IsChecked = true; // don't allow unchecking the active toggle
        }

        bool git = GitToggle.IsChecked == true;
        bool fav = FavouritesToggle.IsChecked == true;
        Browser.Visibility = (git || fav) ? Visibility.Collapsed : Visibility.Visible;
        GitPanel.Visibility = git ? Visibility.Visible : Visibility.Collapsed;
        FavPanel.Visibility = fav ? Visibility.Visible : Visibility.Collapsed;
        if (git) GitPanel.Refresh();
        if (fav) _ = RefreshProjectsAsync();
    }

    /// <summary>Rebuild the Projects view: global favourites plus ÆtherLink peers
    /// (deduplicated against favourites). ÆtherLink discovery is asynchronous and
    /// quietly no-ops when the daemon isn't running yet.</summary>
    private async Task RefreshProjectsAsync()
    {
        _favourites = Favourites.Load();
        var favourites = _favourites.Items;
        var peers = await LoadAetherLinkPeersAsync();
        FavPanel.SetProjects(favourites, peers);
    }

    /// <summary>Fetch live ÆtherLink peers from the Ruby soul and map them onto the
    /// project-item model, skipping the local context and anything already starred.</summary>
    private async Task<List<Favourite>> LoadAetherLinkPeersAsync()
    {
        var starred = new HashSet<string>(
            _favourites.Items.Where(f => f.Path.Length > 0).Select(f => NormalizeProjectPath(f.Path)),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            var result = await _bridge.GetAetherLinkContextsAsync();
            if (result?["contexts"] is not JsonArray contexts) return new();

            var peers = new List<Favourite>();
            foreach (var node in contexts)
            {
                if (node is not JsonObject c) continue;
                var name = c["name"]?.GetValue<string>() ?? "";
                var path = c["path"]?.GetValue<string>() ?? "";
                var own = c["own"]?.GetValue<bool>() ?? false;
                var online = c["online"]?.GetValue<bool>() ?? false;
                var annotated = c["annotated"]?.GetValue<bool>() ?? false;

                if (name.Length == 0 || own) continue;
                if (!online && !annotated) continue;
                if (path.Length > 0 && starred.Contains(NormalizeProjectPath(path))) continue;

                var tags = new List<string>();
                if (c["tags"] is JsonArray tagArr)
                {
                    foreach (var t in tagArr)
                        if (t is not null && t.GetValue<string>() is { Length: > 0 } s)
                            tags.Add(s);
                }

                peers.Add(new Favourite
                {
                    Name = name,
                    Path = path,
                    Source = "aetherlink",
                    Online = online,
                    Port = c["port"]?.GetValue<int>() ?? 0,
                    Description = c["description"]?.GetValue<string>(),
                    Tags = tags,
                });
            }
            return peers;
        }
        catch
        {
            return new();
        }
    }

    private static string NormalizeProjectPath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path.TrimEnd(); }
    }

    private void OpenFavourite(Favourite f)
    {
        if (string.IsNullOrWhiteSpace(f.Path)) return;
        if (Directory.Exists(f.Path))
            SetProjectRoot(f.Path);
    }

    private void AddFavourite(Favourite f)
    {
        if (string.IsNullOrWhiteSpace(f.Path)) return;
        if (_favourites.Items.Any(x => string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase))) return;
        _favourites.Items.Add(new Favourite { Name = f.Name, Path = f.Path });
        _favourites.Save();
        _ = RefreshProjectsAsync();
    }

    private void RemoveFavourite(Favourite f)
    {
        var existing = _favourites.Items.FirstOrDefault(x =>
            string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) _favourites.Items.Remove(existing);
        _favourites.Save();
        _ = RefreshProjectsAsync();
    }

    /// <summary>Toggle a folder's membership in the global favourites (star entry).</summary>
    private void OnBrowserAddFavourite(string path)
    {
        if (!Directory.Exists(path)) return;
        var existing = _favourites.Items.FirstOrDefault(f =>
            string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _favourites.Items.Remove(existing);
        }
        else
        {
            _favourites.Items.Add(new Favourite
            {
                Name = new DirectoryInfo(path).Name,
                Path = path,
            });
        }
        _favourites.Save();
        _ = RefreshProjectsAsync();
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
        Editor.TextArea.SelectionBrush = new SolidColorBrush(theme.Selection);
        Editor.TextArea.SelectionForeground = new SolidColorBrush(theme.Foreground);
        Editor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(theme.PythiaAccent);

        Pythia.ApplyTheme(theme);
        RichEditor.ApplyTheme(theme);
        ReattachHighlighter();
        ApplyWindowOpacity();
        RefreshMarkdownSurfaces();
    }

    private void ReattachHighlighter()
    {
        var transformers = Editor.TextArea.TextView.LineTransformers;
        if (_highlighter is not null)
            transformers.Remove(_highlighter);
        _highlighter = _language.Key == "diff"
            ? new DiffHighlighter(_theme)
            : new RegexHighlighter(_theme, _language);
        transformers.Add(_highlighter);
        Editor.TextArea.TextView.Redraw();
    }

    private void ApplyFontSettings()
    {
        Util.AppFonts.Ui = string.IsNullOrWhiteSpace(_settings.UiFontFamily) ? "Syne" : _settings.UiFontFamily;
        Util.AppFonts.Mono = string.IsNullOrWhiteSpace(_settings.FontFamily) ? "Cascadia Mono, Consolas" : _settings.FontFamily;
        Util.AppFonts.UiSize = _settings.UiFontSize > 0 ? _settings.UiFontSize : 14.0;
        App.ApplyUiFont();
    }

    private void ApplyEditorSettings()
    {
        Editor.FontSize = _settings.FontSize;
        Editor.FontFamily = new FontFamily(_settings.FontFamily);
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

        SyncGutter();
    }

    private void SyncGutter()
    {
        if (_settings.ShowLineNumbers)
        {
            _gutter ??= new GutterMargin(Editor.TextArea);
            if (!Editor.TextArea.LeftMargins.Contains(_gutter))
                Editor.TextArea.LeftMargins.Insert(0, _gutter);

            var ui = ThemePalette.Build(_theme);
            var separator = _theme.RulerSeparator is { } rs
                ? new SolidColorBrush(rs)
                : new SolidColorBrush(ui.Border);
            var selectionEdge = new SolidColorBrush(ThemePalette.Mix(_theme.Selection, ui.Foreground, 0.35));
            _gutter.Configure(
                Editor.FontFamily,
                Editor.FontSize,
                new SolidColorBrush(ui.MutedForeground),
                new SolidColorBrush(_theme.LineHighlight),
                new SolidColorBrush(_theme.Selection),
                separator,
                selectionEdge);
        }
        else if (_gutter is not null)
        {
            Editor.TextArea.LeftMargins.Remove(_gutter);
        }
    }

    private void ApplyWindowOpacity()
    {
        var target = _settings.UseThemeOpacity ? _theme.WindowOpacity : _settings.WindowOpacity;
        Opacity = Math.Clamp(target, 0.15, 1.0);
    }

    // ── Font-size zoom ──────────────────────────────────────────────

    private void OnEditorPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        ZoomEditor(e.Delta > 0 ? EditorZoomStep : 1.0 / EditorZoomStep);
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        switch (e.Key)
        {
            case Key.OemPlus:
            case Key.Add:
                e.Handled = true;
                ZoomEditor(EditorZoomStep);
                break;
            case Key.OemMinus:
            case Key.Subtract:
                e.Handled = true;
                ZoomEditor(1.0 / EditorZoomStep);
                break;
            case Key.D0:
            case Key.NumPad0:
                e.Handled = true;
                ResetEditorZoom();
                break;
        }
    }

    private void ZoomEditor(double factor)
    {
        double next = Math.Clamp(Editor.FontSize * factor, EditorMinFontSize, EditorMaxFontSize);
        if (Math.Abs(next - Editor.FontSize) >= 0.05)
        {
            Editor.FontSize = next;
            SyncGutter();
        }
        ShowZoomIndicator();
    }

    private void ResetEditorZoom()
    {
        Editor.FontSize = EditorBaseFontSize;
        SyncGutter();
        ShowZoomIndicator();
    }

    private void ShowZoomIndicator()
    {
        ZoomPercentText.Text = $"{(int)Math.Round(Editor.FontSize / EditorBaseFontSize * 100.0)}%";
        ZoomAbsoluteText.Text = $"{Editor.FontSize:0.#} pt";
        ZoomPopup.IsOpen = true;
        _zoomTimer.Stop();
        _zoomTimer.Start();
    }

    private void OnZoomReset(object sender, RoutedEventArgs e)
    {
        ResetEditorZoom();
        _zoomTimer.Stop();
        ZoomPopup.IsOpen = false;
    }

    private async void OnShowSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(
            _settings,
            _themes.Select(t => t.Name).ToList(),
            _theme.WindowOpacity,
            _bridge,
            Directory.Exists(_projectRoot) ? _projectRoot : null,
            OpenConfigFile) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        bool themeChanged = !string.Equals(_settings.Theme, dialog.SelectedTheme, StringComparison.OrdinalIgnoreCase);

        _settings.Theme = dialog.SelectedTheme;
        _settings.FontSize = dialog.FontSizeValue;
        _settings.FontFamily = dialog.FontFamilyValue;
        _settings.UiFontFamily = dialog.UiFontFamilyValue;
        _settings.UiFontSize = dialog.UiFontSizeValue;
        _settings.ShowLineNumbers = dialog.ShowLineNumbers;
        _settings.WordWrap = dialog.WordWrap;
        _settings.TabSize = dialog.TabSizeValue;
        _settings.ConvertTabsToSpaces = dialog.ConvertTabsToSpaces;
        _settings.ShowWhitespace = dialog.ShowWhitespace;
        _settings.HighlightCurrentLine = dialog.HighlightCurrentLine;
        _settings.AutoStartDaemon = dialog.AutoStartDaemon;
        _settings.WindowTitle = dialog.WindowTitleValue;
        _settings.RailSymbol = dialog.RailSymbolValue;
        _settings.UseThemeOpacity = dialog.UseThemeOpacity;
        _settings.WindowOpacity = dialog.WindowOpacityValue;

        _settings.OracleProvider = dialog.OracleProviderKey;
        _settings.OracleModel = dialog.OracleModelValue;
        _settings.OracleApiKey = dialog.OracleApiKeyValue;
        _settings.OracleApiUrl = dialog.OracleApiUrlValue;
        _settings.OracleApiType = dialog.OracleApiTypeValue;
        _settings.OracleFastModel = dialog.OracleFastModelValue;

        if (themeChanged)
        {
            _theme = _themes.FirstOrDefault(t => t.Name.Equals(_settings.Theme, StringComparison.OrdinalIgnoreCase)) ?? _theme;
            ApplyTheme(_theme);
            CheckTheme();
        }

        ApplyEditorSettings();
        ApplyFontSettings();
        ApplyWindowOpacity();
        // Re-render markdown surfaces with the new prose font + size.
        Pythia.ApplyTheme(_theme);
        RefreshMarkdownSurfaces();
        UpdateWindowTitle();
        RailSymbolText.Text = _settings.RailSymbol;
        _settings.Save();

        // Persist only the personal AI-provider keys the user actually changed to
        // `~/.aethercodex` (the daemon's global default layer); project values in
        // `<root>/.aethercodex` override them. An auto-filled default is not an
        // edit, so a plain theme tweak never clobbers a real home value.
        var oracleChanges = dialog.OracleChanges();
        if (oracleChanges.Count > 0)
        {
            try
            {
                foreach (var (key, value) in oracleChanges)
                    await _bridge.SetConfigAsync(key, value, "home");
            }
            catch { /* non-fatal: defaults still apply on the next daemon start */ }
        }
    }

    /// <summary>Open (or materialise) a config file in the editor — the
    /// "Open .aethercodex" action from the Settings window.</summary>
    private void OpenConfigFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path,
                    "# ÆtherCodex project configuration\n" +
                    "# Keys here override your personal defaults (~/.aethercodex) and the\n" +
                    "# built-in defaults. See `aether config --default` for the full reference.\n");
            }
            _ = OpenFileAsync(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open config", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StartDaemonSafeAsync()
    {
        try { await _bridge.StartAsync(); }
        catch { /* daemon unavailable — the user can start it manually later */ }
    }

    private async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path)) return;

        // Resolve the file's project syntax overrides before its language is chosen.
        ReloadSyntaxOverrides(path);

        // A file may only be open once; re-focus the existing buffer if present.
        var existing = FindOpenDocument(path);
        if (existing is not null)
        {
            ActivateDocument(existing);
            _ = ActivateContextAndSyncAsync(path);
            return;
        }

        try
        {
            var doc = new OpenDocument { Path = path };
            ApplyLoadedContent(doc, ReadDocumentContent(path));

            _documents.Add(doc);
            ActivateDocument(doc);
            AttachWatcher(doc);

            // Open the file's project context in the daemon (polymorphic window) and
            // sync Pythia to that project's Mnemosyne history when the scope changed.
            _ = ActivateContextAndSyncAsync(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file:\n{ex.Message}", "ÆtherCodex", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Open a target reachable from rendered content — a relative path, a
    /// <c>file://</c> URI, or an http(s) URL — as the right kind of tab. Web pages and
    /// renderable project documents (HTML/SVG) open in a browser tab; everything else
    /// opens as a normal editor file. This is the single choke point for "treat weblinks
    /// like files".</summary>
    private async Task OpenLinkTargetAsync(string target)
    {
        target = target.Trim();
        if (target.Length == 0 || target.StartsWith('#')) return;
        if (target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return;

        // file:// URI → local path.
        if (Uri.TryCreate(target, UriKind.Absolute, out var abs) && abs.IsFile)
        {
            var local = abs.LocalPath;
            if (File.Exists(local))
                await OpenLinkFileAsync(local);
            return;
        }

        // http(s) URL → browser tab.
        if (Uri.TryCreate(target, UriKind.Absolute, out var web)
            && (web.Scheme == Uri.UriSchemeHttp || web.Scheme == Uri.UriSchemeHttps))
        {
            OpenBrowserTab(web.AbsoluteUri);
            return;
        }

        // Relative path → resolve against the active document's directory, then the project root.
        var resolved = ResolveRelativeTarget(target);
        if (File.Exists(resolved))
            await OpenLinkFileAsync(resolved);
        else if (File.Exists(target))
            await OpenLinkFileAsync(target);
    }

    private string ResolveRelativeTarget(string target)
    {
        var docDir = _activeDoc is { Path.Length: > 0 } d
            ? Path.GetDirectoryName(d.Path)
            : null;
        if (docDir is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(docDir, target));
            if (File.Exists(candidate)) return candidate;
        }
        if (_projectRoot.Length > 0)
            return Path.GetFullPath(Path.Combine(_projectRoot, target));
        return Path.GetFullPath(target);
    }

    private async Task OpenLinkFileAsync(string path)
    {
        if (IsRenderableLink(path))
            OpenBrowserTab(new Uri(path).AbsoluteUri);
        else
            await OpenFileAsync(path);
    }

    private static bool IsRenderableLink(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".html" or ".htm" or ".svg" or ".xml";
    }

    private void OpenBrowserTab(string url)
    {
        // Dedupe: an already-open browser tab on the same URL just re-focuses.
        var existing = _documents.FirstOrDefault(d => d.IsBrowser
            && !string.IsNullOrEmpty(d.Url)
            && string.Equals(d.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateDocument(existing);
            return;
        }

        var doc = new OpenDocument { IsBrowser = true, Language = "Browser", Url = url };
        _documents.Add(doc);
        ActivateDocument(doc);
    }

    /// <summary>Daemon host-bridge entry point. Runs on the bridge reader thread; the
    /// whole dispatch is marshalled onto the UI thread so <c>_documents</c> and the
    /// WebView2 surface stay single-threaded.</summary>
    private async Task<JsonObject?> HandleHostCall(string method, JsonObject? args)
    {
        return await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try { return await HandleHostCallOnUi(method, args); }
            catch (Exception ex) { return new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
        }).Task.Unwrap();
    }

    private async Task<JsonObject?> HandleHostCallOnUi(string method, JsonObject? args)
    {
        var a = args ?? new JsonObject();
        string S(string k) => a[k]?.GetValue<string>() ?? "";

        switch (method)
        {
            case "browser_open":
            {
                var tgt = S("target") is { Length: > 0 } t ? t : S("url");
                if (tgt.Length == 0) OpenBrowserTab("about:blank");
                else await OpenLinkTargetAsync(tgt);
                return new JsonObject { ["ok"] = true, ["url"] = BrowserView.CurrentUrl };
            }

            case "browser_navigate":
                await BrowserView.NavigateHostAsync(S("url"));
                return new JsonObject { ["ok"] = true, ["url"] = BrowserView.CurrentUrl };

            case "browser_reload":
                await BrowserView.ReloadHostAsync();
                return new JsonObject { ["ok"] = true };

            case "browser_get_url":
                return new JsonObject { ["ok"] = true, ["url"] = await BrowserView.GetUrlAsync() };

            case "browser_get_html":
                return new JsonObject { ["ok"] = true, ["html"] = await BrowserView.GetHtmlAsync() };

            case "browser_get_text":
                return new JsonObject { ["ok"] = true, ["text"] = await BrowserView.GetTextAsync() };

            case "browser_eval":
            {
                var js = S("js") is { Length: > 0 } j ? j : S("script");
                var value = await BrowserView.EvaluateJavaScriptAsync(js);
                return new JsonObject { ["ok"] = true, ["value"] = value };
            }

            case "browser_set_html":
                await BrowserView.SetHtmlAsync(S("html"));
                return new JsonObject { ["ok"] = true };

            case "browser_screenshot":
            {
                var path = await BrowserView.CapturePngAsync();
                return new JsonObject
                {
                    ["ok"] = true,
                    ["path"] = path,
                    ["bytes"] = new FileInfo(path).Length,
                    ["format"] = "png",
                };
            }

            case "browser_list":
            {
                var tabs = new JsonArray();
                foreach (var d in _documents.Where(d => d.IsBrowser))
                    tabs.Add(new JsonObject { ["title"] = d.BrowserTitle, ["url"] = d.Url });
                return new JsonObject { ["ok"] = true, ["tabs"] = tabs };
            }

            default:
                return new JsonObject { ["ok"] = false, ["error"] = $"unknown host method: {method}" };
        }
    }

    /// <summary>Open a working-tree change's unified diff as a read-only tab in the main view.</summary>
    private void OpenDiffAsync(string path)
    {
        if (_gitRoot.Length == 0) return;
        var change = GitService.GetStatus(_gitRoot).FirstOrDefault(c => c.Path == path);
        if (change is null) return;

        var (old, @new) = GitService.GetContents(_gitRoot, change);
        var title = change.OldPath.Length > 0 ? $"{change.Path} ← {change.OldPath}" : change.Path;

        // Dedupe: an already-open diff tab for the same change just re-focuses.
        var existing = _documents.FirstOrDefault(d => d.IsDiff && d.DiffChange is { } ec
            && string.Equals(ec.Path, change.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateDocument(existing);
            return;
        }

        var doc = new OpenDocument
        {
            IsDiff = true,
            IsReadOnly = true,
            Language = "Diff",
            DiffTitle = title,
            DiffOld = old,
            DiffNew = @new,
            DiffRepoRoot = _gitRoot,
            DiffChange = change,
        };
        _documents.Add(doc);
        ActivateDocument(doc);
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
        UpdateLanguageCombo();
        UpdateSourceButton();
        RebuildTabs();

        // Only link the file browser to the active tab when "Link Explorer to
        // Active File" is engaged.
        if (_linkExplorer && doc.Path.Length > 0)
            Browser.Reveal(doc.Path);
    }

    /// <summary>Flush the live editor surface back into the active document's buffer.</summary>
    private void CommitActiveDocument()
    {
        if (_activeDoc is null) return;
        if (_activeDoc.IsDiff) return;
        if (_activeDoc.IsSheet)
        {
            SheetEditor.CommitEdit();
            return;
        }
        if (_activeDoc.IsBrowser)
        {
            _activeDoc.Url = BrowserView.CurrentUrl;
            _activeDoc.BrowserTitle = BrowserView.CurrentTitle;
            return;
        }
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
            if (doc.IsSheet)
            {
                SheetEditor.LoadSheet(doc.Sheet ?? new Sheet());
                PreviewButton.Visibility = Visibility.Collapsed;
                SetPreviewOpen(false);
                Editor.Visibility = Visibility.Collapsed;
                RichEditor.Visibility = Visibility.Collapsed;
                SheetEditor.Visibility = Visibility.Visible;
                BrowserView.Visibility = Visibility.Collapsed;
            }
            else if (doc.IsBrowser)
            {
                PreviewButton.Visibility = Visibility.Collapsed;
                SetPreviewOpen(false);
                Editor.Visibility = Visibility.Collapsed;
                RichEditor.Visibility = Visibility.Collapsed;
                SheetEditor.Visibility = Visibility.Collapsed;
                BrowserView.Visibility = Visibility.Visible;

                if (!ReferenceEquals(_browserLoadedDoc, doc))
                {
                    BrowserView.Navigate(doc.Url);
                    _browserLoadedDoc = doc;
                }
            }
            else if (doc.IsDiff)
            {
                PreviewButton.Visibility = Visibility.Collapsed;
                SetPreviewOpen(false);
                Editor.Visibility = Visibility.Collapsed;
                RichEditor.Visibility = Visibility.Collapsed;
                SheetEditor.Visibility = Visibility.Collapsed;
                BrowserView.Visibility = Visibility.Collapsed;
                DiffView.Visibility = Visibility.Visible;
                DiffView.Load(doc, doc.DiffRepoRoot, doc.DiffChange, doc.DiffOld, doc.DiffNew, doc.DiffTitle);
            }
            else if (doc.IsRich)
            {
                if (doc.IsMarkdown)
                {
                    doc.Rich = MarkdownRenderer.Render(doc.Markdown, _theme,
                        Util.AppFonts.UiSize > 0 ? Util.AppFonts.UiSize : 14.0,
                        RichEditor.MarkdownSurfaceWidth());
                    RichEditor.LoadMarkdown(doc.Markdown, doc.Rich, _theme);
                }
                else
                    RichEditor.LoadDocument(doc.Rich ?? new FlowDocument());
                PreviewButton.Visibility = Visibility.Collapsed;
                SetPreviewOpen(false);
                Editor.Visibility = Visibility.Collapsed;
                RichEditor.Visibility = Visibility.Visible;
                SheetEditor.Visibility = Visibility.Collapsed;
                BrowserView.Visibility = Visibility.Collapsed;
            }
            else
            {
                Editor.Text = doc.Text;
                _language = doc.IsDiff ? Languages.FromKey("diff") : Languages.FromPath(doc.Path);
                Editor.IsReadOnly = doc.IsReadOnly;
                ReattachHighlighter();
                Editor.ScrollToHome();

                // Markdown preview affordance.
                bool isMarkdown = _language.Key == "markdown";
                PreviewButton.Visibility = isMarkdown ? Visibility.Visible : Visibility.Collapsed;
                if (!isMarkdown) SetPreviewOpen(false);
                if (isMarkdown && Preview.Visibility == Visibility.Visible) RenderPreview();

                Editor.Visibility = Visibility.Visible;
                RichEditor.Visibility = Visibility.Collapsed;
                SheetEditor.Visibility = Visibility.Collapsed;
                BrowserView.Visibility = Visibility.Collapsed;
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

    // ── File watching ──
    // Each open real file gets a FileSystemWatcher. External edits are detected by
    // comparing a disk signature (last-write ticks + length); when the signature
    // changes we reload — asking first if the buffer has unsaved edits.

    private static string? TryCanonical(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private OpenDocument? FindOpenDocument(string path)
    {
        string? key = TryCanonical(path);
        if (key is null) return null;
        foreach (var d in _documents)
        {
            if (d.Path.Length == 0 || d.IsDiff) continue;
            if (string.Equals(TryCanonical(d.Path), key, StringComparison.OrdinalIgnoreCase))
                return d;
        }
        return null;
    }

    /// <summary>Parsed disk content — read once, applied only after conflict resolution.</summary>
    private sealed record LoadedContent(bool IsRich, string Language, string Text, string Markdown, FlowDocument? Rich,
        bool IsSheet = false, Sheet? Sheet = null);

    private LoadedContent ReadDocumentContent(string path)
    {
        if (SheetIO.IsSheetDocument(path))
            return new LoadedContent(false, "Spreadsheet", "", "", null, true, SheetIO.Load(path));
        if (DocumentIO.IsRichDocument(path) || DocumentIO.IsMarkdown(path))
        {
            if (DocumentIO.IsMarkdown(path))
            {
                var markdown = File.ReadAllText(path, DetectEncoding(path));
                return new LoadedContent(true, "Markdown", "", markdown, MarkdownRenderer.Render(markdown, _theme));
            }
            var rich = DocumentIO.Load(path);
            DocumentIO.NormalizeAutoColors(rich);
            return new LoadedContent(true, "Rich Text", "", "", rich);
        }
        return new LoadedContent(false, Languages.FromPath(path).Name, File.ReadAllText(path, DetectEncoding(path)), "", null);
    }

    private static void ApplyLoadedContent(OpenDocument doc, LoadedContent c)
    {
        doc.IsSheet = c.IsSheet;
        doc.Sheet = c.Sheet;
        doc.IsRich = c.IsRich;
        doc.Language = c.Language;
        if (c.IsSheet) return;
        if (c.IsRich)
        {
            doc.Markdown = c.Markdown;
            doc.Rich = c.Rich;
        }
        else
        {
            doc.Text = c.Text;
        }
    }

    private static (long Ticks, long Length) DiskSnapshot(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.LastWriteTimeUtc.Ticks, fi.Length) : (0, -1);
        }
        catch { return (0, -1); }
    }

    private void AttachWatcher(OpenDocument doc)
    {
        if (doc.IsDiff || doc.Path.Length == 0) return;

        string? key = TryCanonical(doc.Path);
        if (key is null) return;
        _diskState[key] = DiskSnapshot(doc.Path);
        if (_watchers.ContainsKey(key)) return;

        string dir = Path.GetDirectoryName(doc.Path) ?? "";
        string file = Path.GetFileName(doc.Path);
        if (dir.Length == 0 || !Directory.Exists(dir)) return;

        var watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        watcher.Changed += OnFileSystemChange;
        watcher.Created += OnFileSystemChange;
        watcher.Deleted += OnFileSystemChange;
        watcher.Renamed += OnFileSystemChange;
        _watchers[key] = watcher;
    }

    private void DetachWatcher(string path)
    {
        if (path.Length == 0) return;
        string? key = TryCanonical(path);
        if (key is null) return;
        if (_watchers.Remove(key, out var watcher)) watcher.Dispose();
        _diskState.Remove(key);
        _reloadAttempts.Remove(key);
        _pendingReloads.Remove(key);
    }

    private void DisposeWatchers()
    {
        _reloadTimer.Stop();
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
        _diskState.Clear();
        _reloadAttempts.Clear();
        _pendingReloads.Clear();
    }

    private void OnFileSystemChange(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            string? key = TryCanonical(e.FullPath);
            if (key is null || !_watchers.ContainsKey(key)) return;
            _pendingReloads.Add(key);
            _reloadTimer.Stop();
            _reloadTimer.Start();
        }));
    }

    private void ProcessPendingReloads()
    {
        var keys = _pendingReloads.ToArray();
        _pendingReloads.Clear();
        foreach (var key in keys)
            HandleExternalChange(key);
    }

    private void HandleExternalChange(string key)
    {
        if (!_watchers.ContainsKey(key)) return;
        var doc = _documents.FirstOrDefault(d =>
            d.Path.Length > 0 && !d.IsDiff &&
            string.Equals(TryCanonical(d.Path), key, StringComparison.OrdinalIgnoreCase));
        if (doc is null) return;

        // Deleted externally — keep the in-memory buffer; nothing to reload.
        if (!File.Exists(doc.Path)) return;

        var now = DiskSnapshot(doc.Path);
        _diskState.TryGetValue(key, out var prev);
        if (prev.Ticks == now.Ticks && prev.Length == now.Length) return; // our own write / spurious event

        // Read the new content first; a still-writing process may lock the file briefly.
        LoadedContent loaded;
        try { loaded = ReadDocumentContent(doc.Path); }
        catch (IOException) { RequeueReload(key); return; }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not reload file:\n{ex.Message}", "ÆtherCodex", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        bool wasActive = ReferenceEquals(doc, _activeDoc);
        if (wasActive) CommitActiveDocument();

        if (doc.Dirty)
        {
            var result = MessageBox.Show(
                $"“{doc.DisplayName}” changed on disk.\n\nOverwrite your unsaved changes with the version on disk?",
                "File changed on disk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                // Keep the in-program view; stop prompting until the next disk change.
                _diskState[key] = now;
                return;
            }
        }

        ApplyLoadedContent(doc, loaded);
        doc.Dirty = false;
        _reloadAttempts.Remove(key);
        _diskState[key] = DiskSnapshot(doc.Path);

        if (wasActive)
        {
            RenderDocument(doc);
            RefreshActiveChrome();
        }
        else
        {
            RebuildTabs();
        }
    }

    private void RequeueReload(string key)
    {
        _reloadAttempts.TryGetValue(key, out int attempts);
        if (attempts >= 6) { _reloadAttempts.Remove(key); return; } // give up silently
        _reloadAttempts[key] = attempts + 1;
        _pendingReloads.Add(key);
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private void RefreshActiveChrome()
    {
        UpdateFilePath();
        UpdateLanguageCombo();
        UpdateSourceButton();
        RebuildTabs();
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
        double width = Preview.ActualWidth - Preview.Padding.Left - Preview.Padding.Right;
        Preview.Document = MarkdownRenderer.Render(Editor.Text, _theme,
            Util.AppFonts.UiSize > 0 ? Util.AppFonts.UiSize : 14.0, width);
    }

    private void ScheduleMarkdownRefresh()
    {
        _markdownRefreshTimer.Stop();
        _markdownRefreshTimer.Start();
    }

    /// <summary>Re-render all markdown surfaces after the window / a pane resized or a
    /// preference changed. Skips a dirty markdown buffer so live edits aren't clobbered.</summary>
    private void RefreshMarkdownSurfaces()
    {
        if (_activeDoc is { IsMarkdown: true } doc && !doc.Dirty)
        {
            doc.Rich = MarkdownRenderer.Render(doc.Markdown, _theme,
                Util.AppFonts.UiSize > 0 ? Util.AppFonts.UiSize : 14.0,
                RichEditor.MarkdownSurfaceWidth());
            RichEditor.RefreshRenderedMarkdown(doc.Rich);
        }

        if (_previewOpen && _language.Key == "markdown")
            RenderPreview();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_activeDoc is null) return;
        CommitActiveDocument();

        var doc = _activeDoc;
        if (doc.IsDiff || doc.IsBrowser) return;
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

    /// <summary>Open a fresh window with one untitled buffer (notepad-style).</summary>
    private void OnNewWindow(object sender, RoutedEventArgs e)
    {
        var w = new MainWindow();
        w.Show();
        w.OnNewFile(this, new RoutedEventArgs());
        w.Activate();
    }

    private void OnNewBrowserTab(object sender, RoutedEventArgs e)
    {
        var doc = new OpenDocument
        {
            IsBrowser = true,
            Language = "Browser",
        };
        _documents.Add(doc);
        ActivateDocument(doc);
        BrowserView.FocusAddress();
    }

    private void OnNewSheet(object sender, RoutedEventArgs e)
    {
        var doc = new OpenDocument
        {
            IsSheet = true,
            Language = "Spreadsheet",
            Sheet = new Sheet()
        };
        _documents.Add(doc);
        ActivateDocument(doc);
        SheetEditor.Focus();
    }

    private static void RevealInExplorer(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = false
            });
        }
        catch { /* explorer unavailable */ }
    }

    private void OnBrowserDuplicate(string path)
    {
        if (!File.Exists(path)) return;
        var dir = Path.GetDirectoryName(path) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        string target;
        int n = 1;
        do
        {
            target = Path.Combine(dir, n == 1 ? $"{baseName} - copy{ext}" : $"{baseName} - copy {n}{ext}");
            n++;
        } while (File.Exists(target));

        try
        {
            File.Copy(path, target, overwrite: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Duplicate failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Browser.Refresh(dir);
        Browser.Reveal(target);
    }

    private void OnBrowserRename(string path)
    {
        bool isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) return;

        var name = PromptForName("Rename", "New name:", Path.GetFileName(path));
        if (string.IsNullOrWhiteSpace(name) || name == Path.GetFileName(path)) return;

        var dir = Path.GetDirectoryName(path) ?? "";
        var target = Path.Combine(dir, name);
        if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(target) || Directory.Exists(target))
        {
            MessageBox.Show("A file or folder with that name already exists.", "Rename",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (isDir) Directory.Move(path, target);
            else File.Move(path, target);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Re-point any open documents at/under the old path.
        var prefix = path + Path.DirectorySeparatorChar;
        foreach (var doc in _documents)
        {
            string oldPath = doc.Path;
            if (string.Equals(doc.Path, path, StringComparison.OrdinalIgnoreCase))
                doc.Path = target;
            else if (isDir && doc.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                doc.Path = target + Path.DirectorySeparatorChar + doc.Path[prefix.Length..];
            else
                continue;

            DetachWatcher(oldPath);
            AttachWatcher(doc);
        }

        // If the active tab changed extension (e.g. .txt → .rb), re-derive its language.
        if (_activeDoc is { IsRich: false } active)
        {
            _language = Languages.FromPath(active.Path);
            active.Language = _language.Name;
            ReattachHighlighter();
        }

        Browser.Refresh(dir);
        if (!isDir) Browser.Reveal(target);
        UpdateFilePath();
        UpdateLanguageCombo();
        RebuildTabs();
    }

    private void OnBrowserDelete(string path)
    {
        bool isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) return;

        var confirm = MessageBox.Show(
            $"Delete {(isDir ? "folder" : "file")} \"{Path.GetFileName(path)}\" permanently?",
            "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            if (isDir) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Close open documents that lived at/under the deleted path.
        var prefix = path + Path.DirectorySeparatorChar;
        foreach (var doc in _documents.ToList())
        {
            if (string.Equals(doc.Path, path, StringComparison.OrdinalIgnoreCase)
                || (isDir && doc.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                DetachWatcher(doc.Path);
                _documents.Remove(doc);
                if (ReferenceEquals(doc, _activeDoc)) _activeDoc = null;
            }
        }

        Browser.Refresh(Path.GetDirectoryName(path) ?? "");

        if (_documents.Count == 0)
            ClearEditor();
        else if (_activeDoc is null)
            ActivateDocument(_documents[0]);
        else
            RebuildTabs();
    }

    private void OnBrowserNewItem(string directory, bool isFolder)
    {
        string? name = isFolder
            ? PromptForName("New Folder", "Folder name:", "New Folder")
            : PromptForName("New File", "File name:", "Untitled.txt");
        if (string.IsNullOrWhiteSpace(name)) return;

        var path = Path.Combine(directory, name);
        try
        {
            if (isFolder)
            {
                if (Directory.Exists(path))
                {
                    MessageBox.Show("Folder already exists.", "New Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Directory.CreateDirectory(path);
            }
            else
            {
                if (File.Exists(path))
                {
                    MessageBox.Show("File already exists.", "New File", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                File.WriteAllText(path, "", new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Create failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Browser.Refresh(directory);
        if (!isFolder)
        {
            Browser.Reveal(path);
            _ = OpenFileAsync(path);
        }
    }

    private static string? PromptForName(string title, string label, string initial)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow
        };

        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        var box = new TextBox { Text = initial };
        stack.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);

        window.Content = stack;
        ok.Click += (_, _) => { window.DialogResult = true; };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return window.ShowDialog() == true ? box.Text.Trim() : null;
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

    /// <summary>Open the full find window with a given scope: 0 = project, 1 = current file,
    /// 2 = folder. Reuses a single modeless instance, re-pointing its scope each time.</summary>
    private void OpenFind(int scope)
    {
        string initial = CurrentSelectionOrWord();

        if (_searchWindow is { IsVisible: true })
        {
            _searchWindow.SetScope(scope);
            _searchWindow.SetQuery(initial);
            _searchWindow.Activate();
            return;
        }

        var dialog = new SearchInProjectWindow(ProjectRoot, CurrentFile, initial, scope) { Owner = this };
        dialog.ResultChosen += row => _ = OpenFileAtAsync(row.File, row.Line, row.Column, row.Length);
        dialog.Closed += (_, _) => _searchWindow = null;
        _searchWindow = dialog;
        dialog.Show();
    }

    private void OnFindInFile(object sender, RoutedEventArgs e) => OpenFind(scope: 1);

    private void OnFindInProject(object sender, RoutedEventArgs e) => OpenFind(scope: 0);

    private void OnFindInFolder(object sender, RoutedEventArgs e) => OpenFind(scope: 2);

    private void OnFindNext(object sender, RoutedEventArgs e) => _searchPanel?.FindNext();

    private void OnFindPrevious(object sender, RoutedEventArgs e) => _searchPanel?.FindPrevious();

    private void OnUseSelectionForFind(object sender, RoutedEventArgs e)
    {
        string selection = CurrentSelectionOrWord();
        if (selection.Length == 0) return;

        // The "normal" (full) search window — not the incremental quick search.
        if (_searchWindow is { IsVisible: true })
        {
            _searchWindow.SetQuery(selection);
            _searchWindow.Activate();
            return;
        }

        var dialog = new SearchInProjectWindow(ProjectRoot, CurrentFile, selection, scope: 1) { Owner = this };
        dialog.ResultChosen += row => _ = OpenFileAtAsync(row.File, row.Line, row.Column, row.Length);
        dialog.Closed += (_, _) => _searchWindow = null;
        _searchWindow = dialog;
        dialog.Show();
    }

    private string CurrentSelectionOrWord()
    {
        var ta = Editor.TextArea;
        if (ta.Selection.Length > 0)
            return Editor.Document.GetText(ta.Selection.SurroundingSegment.Offset, ta.Selection.Length);

        var line = Editor.Document.GetLineByNumber(ta.Caret.Line);
        var text = Editor.Document.GetText(line.Offset, line.Length);
        return WordAtCaret(text, ta.Caret.Column - 1);
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
            Filter = "Rich Text Format (*.rtf)|*.rtf|Word Document (*.docx)|*.docx|OpenDocument Text (*.odt)|*.odt",
            FileName = "Untitled.rtf",
            DefaultExt = ".rtf"
        };
        if (dialog.ShowDialog() != true) return;

        var existing = FindOpenDocument(dialog.FileName);
        if (existing is not null)
        {
            ActivateDocument(existing);
            return;
        }

        var doc = new OpenDocument
        {
            Path = dialog.FileName,
            IsRich = true,
            Language = "Rich Text",
            Rich = new FlowDocument()
        };
        _documents.Add(doc);
        ActivateDocument(doc);
        AttachWatcher(doc);
        _ = ActivateContextAndSyncAsync(dialog.FileName);
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_activeDoc is null) return;
        CommitActiveDocument();

        var doc = _activeDoc;
        if (doc.IsBrowser) return;
        string oldPath = doc.Path;

        if (doc.IsSheet)
        {
            var sheetDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save As",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx|OpenDocument Spreadsheet (*.ods)|*.ods|ÆtherCalc Spreadsheet (*.aecalc)|*.aecalc|CSV (*.csv)|*.csv|TSV (*.tsv)|*.tsv",
                FileName = doc.Path.Length > 0
                    ? Path.GetFileNameWithoutExtension(doc.Path) + ".aecalc"
                    : "Untitled.aecalc",
                DefaultExt = ".aecalc"
            };
            if (sheetDialog.ShowDialog() != true) return;
            try
            {
                SheetIO.Save(sheetDialog.FileName, doc.Sheet ?? new Sheet());
                doc.Path = sheetDialog.FileName;
                doc.Language = "Spreadsheet";
                doc.Dirty = false;
                DetachWatcher(oldPath);
                AttachWatcher(doc);
                UpdateFilePath();
                UpdateLanguageCombo();
                UpdateSourceButton();
                RebuildTabs();
                _ = ActivateContextAndSyncAsync(sheetDialog.FileName);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); }
            return;
        }

        bool rich = doc.IsRich;
        var name = doc.Path.Length > 0
            ? Path.GetFileNameWithoutExtension(doc.Path) + (rich ? ".rtf" : ".txt")
            : (rich ? "Untitled.rtf" : "Untitled.txt");
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save As",
            Filter = rich
                ? "Rich Text Format (*.rtf)|*.rtf|Word Document (*.docx)|*.docx|OpenDocument Text (*.odt)|*.odt|Markdown (*.md)|*.md|Plain Text (*.txt)|*.txt"
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
                        doc.Rich = MarkdownRenderer.Render(doc.Markdown, _theme);
                    DocumentIO.Save(path, doc.Rich ?? new FlowDocument());
                }
                doc.Language = targetMarkdown ? "Markdown" : "Rich Text";
            }
            else
            {
                File.WriteAllText(path, doc.Text, new UTF8Encoding(false));
                _language = Languages.FromPath(path);
                doc.Language = _language.Name;
                ReattachHighlighter();
            }
            doc.Path = path;
            doc.Dirty = false;
            DetachWatcher(oldPath);
            AttachWatcher(doc);
            UpdateFilePath();
            UpdateLanguageCombo();
            UpdateSourceButton();
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
        DetachWatcher(doc.Path);
        _documents.Remove(doc);

        // Release the shared WebView2 surface if the browser tab holding it is gone.
        if (doc.IsBrowser && ReferenceEquals(_browserLoadedDoc, doc))
        {
            _browserLoadedDoc = null;
            BrowserView.Navigate("about:blank");
        }

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
        DisposeWatchers();
        _documents.Clear();
        _activeDoc = null;
        _suppressDirty = true;
        try
        {
            _language = Languages.FromPath("x.txt");
            Editor.Text = "";
            RichEditor.NewDocument();
            SheetEditor.LoadSheet(new Sheet());
            Editor.Visibility = Visibility.Visible;
            RichEditor.Visibility = Visibility.Collapsed;
            SheetEditor.Visibility = Visibility.Collapsed;
            BrowserView.Visibility = Visibility.Collapsed;
            DiffView.Visibility = Visibility.Collapsed;
            PreviewButton.Visibility = Visibility.Collapsed;
            SetPreviewOpen(false);
            ReattachHighlighter();
            _browserLoadedDoc = null;
            BrowserView.Navigate("about:blank");
        }
        finally
        {
            _suppressDirty = false;
        }
        UpdateFilePath();
        UpdateLanguageCombo();
        UpdateSourceButton();
        RebuildTabs();
    }

    /// <summary>Write a document to <paramref name="path"/> and mark it clean.</summary>
    private void PersistDocument(OpenDocument doc, string path)
    {
        if (doc.IsSheet)
        {
            SheetIO.Save(path, doc.Sheet ?? new Sheet());
        }
        else if (doc.IsRich)
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
        AttachWatcher(doc);
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
                ReattachHighlighter();
            }
            else
            {
                doc.Language = DocumentIO.IsMarkdown(doc.Path) ? "Markdown" : "Rich Text";
            }
        }
        UpdateFilePath();
        UpdateLanguageCombo();
        UpdateSourceButton();
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
        close.Click += (_, e) => { e.Handled = true; CloseDocument(doc); };

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
        tab.ContextMenu = BuildTabContextMenu(doc);

        // Drag to reorder within this strip, into another window's strip, or out
        // of the window entirely (tear-off into a new window).
        var start = new Point();
        tab.PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(tab); };
        tab.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(tab);
            if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (ReferenceEquals(doc, _activeDoc)) CommitActiveDocument();
            var data = new DataObject("AetherCodex.Tab", new TabDragPayload(doc, this));
            var result = DragDrop.DoDragDrop(tab, data, DragDropEffects.Move);
            if (result == DragDropEffects.None)
                TryTearOffTab(doc);
        };
        return tab;
    }

    private ContextMenu BuildTabContextMenu(OpenDocument doc)
    {
        var menu = new ContextMenu();
        var move = new MenuItem { Header = "Move to New Window" };
        move.Click += (_, _) => MoveDocumentToNewWindow(doc);
        menu.Items.Add(move);
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => CloseDocument(doc);
        menu.Items.Add(close);
        return menu;
    }

    // ── Tab drag / tear-off plumbing ──────────────────────────────────────────
    private sealed record TabDragPayload(OpenDocument Doc, MainWindow Source);

    private void SetupTabStripDrag()
    {
        TabStrip.AllowDrop = true;
        TabStrip.Background = System.Windows.Media.Brushes.Transparent;
        TabStrip.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent("AetherCodex.Tab"))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
        };
        TabStrip.Drop += OnTabStripDrop;
    }

    private void OnTabStripDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("AetherCodex.Tab") is not TabDragPayload payload) return;
        var doc = payload.Doc;
        int target = TabDropIndex(e);

        if (ReferenceEquals(payload.Source, this))
        {
            int from = _documents.IndexOf(doc);
            if (from < 0 || from == target) return;
            _documents.RemoveAt(from);
            if (target > from) target--;
            _documents.Insert(Math.Clamp(target, 0, _documents.Count), doc);
            RebuildTabs();
        }
        else
        {
            payload.Source.RemoveDocument(doc);
            _documents.Insert(Math.Clamp(target, 0, _documents.Count), doc);
            ActivateDocument(doc);
        }
    }

    private int TabDropIndex(DragEventArgs e)
    {
        var pos = e.GetPosition(TabStrip);
        int index = 0;
        foreach (var child in TabStrip.Children.OfType<ToggleButton>())
        {
            var p = child.TranslatePoint(new Point(0, 0), TabStrip);
            if (pos.X < p.X + child.ActualWidth / 2) return index;
            index++;
        }
        return index;
    }

    /// <summary>Remove a document from this window, restoring a neighbour if it was active.</summary>
    private void RemoveDocument(OpenDocument doc)
    {
        int index = _documents.IndexOf(doc);
        if (index < 0) return;
        bool wasActive = ReferenceEquals(doc, _activeDoc);
        if (wasActive) { CommitActiveDocument(); _activeDoc = null; }
        DetachWatcher(doc.Path);
        _documents.Remove(doc);
        if (_documents.Count == 0) { ClearEditor(); return; }
        if (wasActive) ActivateDocument(_documents[Math.Clamp(index, 0, _documents.Count - 1)]);
        else RebuildTabs();
    }

    /// <summary>Adopt a document already populated elsewhere (move-to-new-window).</summary>
    public void AdoptDocument(OpenDocument doc)
    {
        _documents.Add(doc);
        ActivateDocument(doc);
        if (doc.Path.Length > 0) AttachWatcher(doc);
    }

    private void MoveDocumentToNewWindow(OpenDocument doc)
    {
        var w = new MainWindow();
        w.Show();
        RemoveDocument(doc);
        w.AdoptDocument(doc);
        w.Activate();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>When a tab drag ends outside the window, tear it off into a new window.</summary>
    private void TryTearOffTab(OpenDocument doc)
    {
        GetCursorPos(out var cursor);
        var topLeft = PointToScreen(new Point(0, 0));
        var rect = new Rect(topLeft.X, topLeft.Y, ActualWidth, ActualHeight);
        if (!rect.Contains(new Point(cursor.X, cursor.Y)))
            MoveDocumentToNewWindow(doc);
    }

    private bool IsLastMainWindow()
        => !Application.Current.Windows.OfType<MainWindow>().Any(w => !ReferenceEquals(w, this));

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
        {
            FileIconText.Text = "·";
            FileNameText.Text = "no file open";
            FilePathText.Text = "";
            return;
        }

        var doc = _activeDoc;
        if (doc.IsBrowser)
        {
            FileIconText.Text = "🌐";
            FileNameText.Text = doc.BrowserTitle.Length > 0 ? doc.BrowserTitle : "New Tab";
            FilePathText.Text = doc.Url.Length > 0 ? doc.Url : "";
            return;
        }

        if (doc.IsDiff)
        {
            FileIconText.Text = "⇄";
            FileNameText.Text = doc.DiffTitle;
            FilePathText.Text = "";
            return;
        }

        FileIconText.Text = doc.IsSheet
            ? "▦"
            : doc.IsRich
            ? (doc.IsMarkdown ? "✎" : "📄")
            : GlyphForLanguage(_language.Key);

        string dot = doc.Dirty ? "● " : "";
        if (doc.Path.Length == 0)
        {
            FileNameText.Text = dot + "untitled";
            FilePathText.Text = "";
            return;
        }

        FileNameText.Text = dot + Path.GetFileName(doc.Path);
        FilePathText.Text = RelativeDirectory(doc.Path);
    }

    private static string GlyphForLanguage(string key) => key switch
    {
        "ruby" => "◆",
        "python" => "🐍",
        "javascript" => "§",
        "typescript" => "§",
        "csharp" => "♯",
        "c" => "◈",
        "cpp" => "◈",
        "json" => "≡",
        "yaml" => "≡",
        "xml" => "◇",
        "css" => "◇",
        "sass" => "◇",
        "haml" => "◇",
        "shell" => "▷",
        "haskell" => "λ",
        "markdown" => "✎",
        _ => "·",
    };

    private string RelativeDirectory(string path)
    {
        if (_projectRoot.Length == 0)
            return Path.GetDirectoryName(path) ?? "";

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_projectRoot));
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            if (dir.Equals(root, StringComparison.OrdinalIgnoreCase))
                return "";
            if (dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return dir[(root.Length + 1)..];
            return dir;
        }
        catch
        {
            return Path.GetDirectoryName(path) ?? "";
        }
    }

    private void UpdateLanguageCombo()
    {
        _syncingLanguage = true;
        try
        {
            LanguageCombo.Items.Clear();
            foreach (var lang in Languages.All)
                LanguageCombo.Items.Add(lang.Name);

            if (_activeDoc is null)
            {
                LanguageCombo.SelectedItem = null;
                LanguageCombo.IsEnabled = false;
                return;
            }

            if (_activeDoc.IsRich || _activeDoc.IsSheet || _activeDoc.IsBrowser || _activeDoc.IsDiff)
            {
                LanguageCombo.IsEnabled = false;
                if (!LanguageCombo.Items.Contains(_activeDoc.Language))
                    LanguageCombo.Items.Insert(0, _activeDoc.Language);
                LanguageCombo.SelectedItem = _activeDoc.Language;
            }
            else
            {
                LanguageCombo.IsEnabled = true;
                LanguageCombo.SelectedItem = _language.Name;
            }
        }
        finally
        {
            _syncingLanguage = false;
        }
    }

    private void UpdateSourceButton()
    {
        bool markdown = _activeDoc is { IsRich: true, IsMarkdown: true };
        SourceButton.Visibility = markdown ? Visibility.Visible : Visibility.Collapsed;
        SourceButton.IsChecked = RichEditor.SourceMode;
        SourceButton.Content = RichEditor.SourceMode ? "Rich" : "Source";
    }

    private void OnToggleSource(object sender, RoutedEventArgs e)
    {
        RichEditor.ToggleSource();
        UpdateSourceButton();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLanguage) return;
        if (_activeDoc is null || _activeDoc.IsRich || _activeDoc.IsBrowser || _activeDoc.IsDiff) return;
        if (LanguageCombo.SelectedItem is not string name) return;

        var lang = Languages.All.FirstOrDefault(l => l.Name == name);
        if (lang is null) return;

        _language = lang;
        _activeDoc.Language = lang.Name;
        ReattachHighlighter();
        UpdateFilePath();
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
        ReloadSyntaxOverrides(directory);
        Browser.SetRoot(directory);
        _gitRoot = GitService.FindRoot(directory);
        GitPanel.SetRepository(directory);
        UpdateWindowTitle();
        Title = WindowTitleText.Text;
        UpdateFilePath();
        _ = ActivateContextAndSyncAsync(directory);
    }

    private void OnBrowserReload(object sender, RoutedEventArgs e)
    {
        Browser.Reload();
    }

    /// <summary>Re-load per-project `.aether-properties` syntax overrides for the given
    /// file/folder context and install them into the language registry.</summary>
    private static void ReloadSyntaxOverrides(string? context)
    {
        Languages.SetCustomExtensions(ProjectProperties.LoadSyntaxOverrides(context));
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
            // Each project owns its own daemon + ÆtherLimen socket and memory db,
            // so re-pointing the window means resolving the nearest project root on
            // the C# side and re-binding to that project's socket. We deliberately
            // do NOT send `activate_context` (which re-scopes the *current* daemon
            // in place) — that leaves the old daemon mis-scoped and the oracle
            // answering the previous project while the UI shows the new one.
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            var root = string.IsNullOrEmpty(dir) ? null : RubyBridge.ResolveProjectRoot(dir);
            if (string.IsNullOrEmpty(root)) return;

            Environment.SetEnvironmentVariable("AETHER_PROJECT_ROOT", root);
            _projectRoot = root;
            ReloadSyntaxOverrides(root);

            // Re-bind only when the scope actually moved; otherwise the shared
            // bridge stays on its current connection.
            if (!string.Equals(_bridgeBoundRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                _bridgeBoundRoot = root;
                await _bridge.RestartAsync();
            }

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

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private DebugWindow? _debug;
    private void OnShowDebug(object sender, RoutedEventArgs e)
    {
        if (_debug is null)
        {
            _debug = new DebugWindow(_bridge);
            _debug.Closed += (_, _) => _debug = null;
        }
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

    // ── Maximize within the work area (respect the taskbar) ──
    // With WindowStyle="None" + WindowChrome, WPF maximizes to the full monitor
    // rectangle, sliding the window under the taskbar. Clamp the maximize bounds
    // to the monitor's work area via WM_GETMINMAXINFO.
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ClampMaximizeToWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ClampMaximizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var monitorRect = info.rcMonitor;
                var work = info.rcWork;
                mmi.ptMaxPosition.X = work.Left - monitorRect.Left;
                mmi.ptMaxPosition.Y = work.Top - monitorRect.Top;
                mmi.ptMaxSize.X = work.Right - work.Left;
                mmi.ptMaxSize.Y = work.Bottom - work.Top;
            }
        }
        Marshal.StructureToPtr(mmi, lParam, true);
    }

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

            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

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