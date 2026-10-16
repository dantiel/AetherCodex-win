using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AetherCodex.Panels;

namespace AetherCodex.Editor;

public partial class RichTextEditor : UserControl
{
    private static readonly string[] FontNames =
    {
        "Syne", "Syne Mono",
        "Segoe UI", "Calibri", "Cambria", "Georgia", "Times New Roman", "Arial",
        "Consolas", "Courier New", "Verdana", "Tahoma", "Trebuchet MS",
        "Comic Sans MS", "Garamond", "Palatino Linotype", "Impact"
    };

    private static readonly string[] Sizes = { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "32", "36", "48", "64", "72" };

    private static readonly string[] ColorNames = { "Auto", "Black", "White", "Gray", "Red", "Orange", "Yellow", "Green", "Teal", "Blue", "Indigo", "Purple", "Pink", "Brown" };

    private bool _syncing;
    private SyntaxTheme? _theme;
    private bool _isMarkdown;
    private bool _sourceMode;
    private bool _loadingSource;

    /// <summary>True when the loaded buffer is markdown (enables the source/rich toggle).</summary>
    public bool IsMarkdown => _isMarkdown;

    /// <summary>True when the raw-source pane is showing (markdown only).</summary>
    public bool SourceMode => _sourceMode;

    /// <summary>Raised whenever the document text changes (dirty tracking).</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>Raised when the source/rich view toggles (markdown only).</summary>
    public event EventHandler? SourceModeChanged;

    public RichTextEditor()
    {
        InitializeComponent();

        FontBox.ItemsSource = FontNames;
        SizeBox.ItemsSource = Sizes;
        ColorBox.ItemsSource = ColorNames;
        HighlightBox.ItemsSource = ColorNames;

        RichText.Document = new FlowDocument();
        Ruler.Attach(RichText);
        RichText.SelectionChanged += (_, _) => SyncToolbarState();
        RichText.TextChanged += (_, _) => { SyncToolbarState(); DocumentChanged?.Invoke(this, EventArgs.Empty); };
        SourceBox.TextChanged += (_, _) =>
        {
            if (!_loadingSource) DocumentChanged?.Invoke(this, EventArgs.Empty);
        };

        RichText.InputBindings.Add(new KeyBinding(EditingCommands.ToggleBold, new KeyGesture(Key.B, ModifierKeys.Control)));
        RichText.InputBindings.Add(new KeyBinding(EditingCommands.ToggleItalic, new KeyGesture(Key.I, ModifierKeys.Control)));
        RichText.InputBindings.Add(new KeyBinding(EditingCommands.ToggleUnderline, new KeyGesture(Key.U, ModifierKeys.Control)));
    }

    public FlowDocument Document => RichText.Document;

    public void LoadDocument(FlowDocument doc)
    {
        _isMarkdown = false;
        SetSourceMode(false);
        RichText.Document = doc;
        SyncToolbarState();
    }

    /// <summary>Load a markdown buffer: <paramref name="source"/> is canonical,
    /// <paramref name="rendered"/> is its cached FlowDocument rendering.</summary>
    public void LoadMarkdown(string source, FlowDocument rendered, SyntaxTheme theme)
    {
        _isMarkdown = true;
        _theme = theme;
        _loadingSource = true;
        SourceBox.Text = source;
        _loadingSource = false;
        RichText.Document = rendered;
        SetSourceMode(false);
        SyncToolbarState();
    }

    /// <summary>The canonical markdown text, synced from the rich surface when needed.</summary>
    public string GetMarkdown()
    {
        if (!_isMarkdown) return "";
        return _sourceMode ? SourceBox.Text : MarkdownSerializer.ToMarkdown(RichText.Document);
    }

    /// <summary>Toggle between the raw markdown source pane and the rich surface.</summary>
    public void ToggleSource() => SetSourceMode(!_sourceMode);

    private void SetSourceMode(bool source)
    {
        if (_sourceMode == source) return;
        _sourceMode = source;
        SourceModeChanged?.Invoke(this, EventArgs.Empty);

        if (source)
        {
            // Rich → source: serialize the live FlowDocument back to markdown.
            _loadingSource = true;
            SourceBox.Text = MarkdownSerializer.ToMarkdown(RichText.Document);
            _loadingSource = false;
            RichText.Visibility = Visibility.Collapsed;
            SourceBox.Visibility = Visibility.Visible;
            Ruler.Visibility = Visibility.Collapsed;
            FormatBar.IsEnabled = false;
        }
        else
        {
            // Source → rich: re-render from the raw source.
            var markdown = SourceBox.Text;
            RichText.Document = MarkdownRenderer.Render(markdown, _theme ?? new SyntaxTheme());
            SourceBox.Visibility = Visibility.Collapsed;
            RichText.Visibility = Visibility.Visible;
            Ruler.Visibility = Visibility.Visible;
            FormatBar.IsEnabled = true;
            SyncToolbarState();
        }
    }

    public void NewDocument()
    {
        _isMarkdown = false;
        SetSourceMode(false);
        RichText.Document = new FlowDocument();
    }

    public void ApplyTheme(SyntaxTheme theme)
    {
        _theme = theme;
        RichText.SelectionBrush = new SolidColorBrush(theme.Selection);
        RichText.SelectionOpacity = 0.85;
        RichText.CaretBrush = new SolidColorBrush(theme.PythiaAccent);

        var ui = ThemePalette.Build(theme);
        Ruler.Configure(
            theme.RulerBackground ?? ui.ChromeBackground,
            theme.RulerSeparator ?? ui.Border,
            theme.RulerForeground ?? ui.MutedForeground,
            ui.AccentBright);
    }

    // ── Formatting commands ────────────────────────────────────────────────────

    private void OnUndo(object sender, RoutedEventArgs e) => RichText.Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => RichText.Redo();

    private void OnToggleBold(object sender, RoutedEventArgs e) => EditingCommands.ToggleBold.Execute(null, RichText);
    private void OnToggleItalic(object sender, RoutedEventArgs e) => EditingCommands.ToggleItalic.Execute(null, RichText);
    private void OnToggleUnderline(object sender, RoutedEventArgs e) => EditingCommands.ToggleUnderline.Execute(null, RichText);
    private void OnToggleStrike(object sender, RoutedEventArgs e) => ToggleDecoration(TextDecorationLocation.Strikethrough);
    private void OnToggleSub(object sender, RoutedEventArgs e) => ToggleBaseline(BaselineAlignment.Subscript);
    private void OnToggleSuper(object sender, RoutedEventArgs e) => ToggleBaseline(BaselineAlignment.Superscript);

    private void OnAlignLeft(object sender, RoutedEventArgs e) => EditingCommands.AlignLeft.Execute(null, RichText);
    private void OnAlignCenter(object sender, RoutedEventArgs e) => EditingCommands.AlignCenter.Execute(null, RichText);
    private void OnAlignRight(object sender, RoutedEventArgs e) => EditingCommands.AlignRight.Execute(null, RichText);
    private void OnAlignJustify(object sender, RoutedEventArgs e) => EditingCommands.AlignJustify.Execute(null, RichText);

    private void OnToggleBullets(object sender, RoutedEventArgs e) => EditingCommands.ToggleBullets.Execute(null, RichText);
    private void OnToggleNumbering(object sender, RoutedEventArgs e) => EditingCommands.ToggleNumbering.Execute(null, RichText);

    private void OnOutdent(object sender, RoutedEventArgs e) => EditingCommands.DecreaseIndentation.Execute(null, RichText);
    private void OnIndent(object sender, RoutedEventArgs e) => EditingCommands.IncreaseIndentation.Execute(null, RichText);

    private void OnClearFormatting(object sender, RoutedEventArgs e)
    {
        var sel = RichText.Selection;
        if (sel.IsEmpty) return;
        sel.ClearAllProperties();
    }

    private void OnInsertImage(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert Image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var image = new Image
            {
                Source = new BitmapImage(new Uri(dlg.FileName)),
                Stretch = Stretch.Uniform,
                MaxWidth = 600
            };
            new InlineUIContainer(image, RichText.CaretPosition.GetInsertionPosition(LogicalDirection.Forward));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Insert image", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Font / size / colour ───────────────────────────────────────────────────

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || FontBox.SelectedItem is not string fam) return;
        ApplyToSelection(TextElement.FontFamilyProperty, new FontFamily(fam));
    }

    private void OnFontBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyToSelection(TextElement.FontFamilyProperty, new FontFamily(FontBox.Text));
    }

    private void OnSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SizeBox.SelectedItem is not string s) return;
        if (double.TryParse(s, out var size)) ApplyToSelection(TextElement.FontSizeProperty, size);
    }

    private void OnSizeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (double.TryParse(SizeBox.Text, out var size)) ApplyToSelection(TextElement.FontSizeProperty, size);
    }

    private void OnTextColorChanged(object sender, SelectionChangedEventArgs e) => ApplyColor(TextElement.ForegroundProperty, ColorBox);
    private void OnHighlightChanged(object sender, SelectionChangedEventArgs e) => ApplyColor(TextElement.BackgroundProperty, HighlightBox);

    private void ApplyColor(DependencyProperty prop, ComboBox box)
    {
        if (_syncing || box.SelectedItem is not string name) return;
        if (name == "Auto")
            ApplyToSelection(prop, null);
        else
            ApplyToSelection(prop, new SolidColorBrush(NameToColor(name)));
    }

    private void ApplyToSelection(DependencyProperty prop, object? value)
    {
        var sel = RichText.Selection;
        if (sel.IsEmpty) return;
        sel.ApplyPropertyValue(prop, value);
    }

    private static Color NameToColor(string name) => name switch
    {
        "Black" => Colors.Black,
        "White" => Colors.White,
        "Gray" => Colors.Gray,
        "Red" => Colors.Red,
        "Orange" => Colors.Orange,
        "Yellow" => Colors.Yellow,
        "Green" => Colors.Green,
        "Teal" => Colors.Teal,
        "Blue" => Colors.Blue,
        "Indigo" => Colors.Indigo,
        "Purple" => Colors.Purple,
        "Pink" => Colors.Pink,
        "Brown" => Colors.Brown,
        _ => Colors.White
    };

    private void ToggleDecoration(TextDecorationLocation loc)
    {
        var sel = RichText.Selection;
        if (sel.IsEmpty) return;
        var deco = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        bool has = deco is not null && deco.Any(d => d.Location == loc);
        if (has)
            sel.ApplyPropertyValue(Inline.TextDecorationsProperty, null);
        else
            sel.ApplyPropertyValue(Inline.TextDecorationsProperty,
                loc == TextDecorationLocation.Strikethrough ? TextDecorations.Strikethrough : TextDecorations.Underline);
    }

    private void ToggleBaseline(BaselineAlignment align)
    {
        var sel = RichText.Selection;
        if (sel.IsEmpty) return;
        var current = sel.GetPropertyValue(Inline.BaselineAlignmentProperty) as BaselineAlignment? ?? BaselineAlignment.Baseline;
        sel.ApplyPropertyValue(Inline.BaselineAlignmentProperty, current == align ? BaselineAlignment.Baseline : align);
    }

    // ── Toolbar state sync ─────────────────────────────────────────────────────

    private void SyncToolbarState()
    {
        var sel = RichText.Selection;
        if (sel is null) return;
        bool has = !sel.IsEmpty;

        _syncing = true;
        BoldBtn.IsChecked = has && IsBold(sel.GetPropertyValue(Inline.FontWeightProperty));
        ItalicBtn.IsChecked = has && IsItalic(sel.GetPropertyValue(Inline.FontStyleProperty));
        UnderlineBtn.IsChecked = has && HasDecoration(sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection, TextDecorationLocation.Underline);
        StrikeBtn.IsChecked = has && HasDecoration(sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection, TextDecorationLocation.Strikethrough);

        var baseline = sel.GetPropertyValue(Inline.BaselineAlignmentProperty) as BaselineAlignment? ?? BaselineAlignment.Baseline;
        SubBtn.IsChecked = has && baseline == BaselineAlignment.Subscript;
        SuperBtn.IsChecked = has && baseline == BaselineAlignment.Superscript;

        var align = sel.GetPropertyValue(Block.TextAlignmentProperty) as TextAlignment? ?? TextAlignment.Left;
        AlignLeftBtn.IsChecked = align == TextAlignment.Left;
        AlignCenterBtn.IsChecked = align == TextAlignment.Center;
        AlignRightBtn.IsChecked = align == TextAlignment.Right;
        AlignJustifyBtn.IsChecked = align == TextAlignment.Justify;

        // Best-effort list state via the current block's parent list.
        var block = sel.Start.Paragraph;
        BulletsBtn.IsChecked = block?.Parent is List { MarkerStyle: TextMarkerStyle.Disc or TextMarkerStyle.Box or TextMarkerStyle.Circle };
        NumberingBtn.IsChecked = block?.Parent is List { MarkerStyle: TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin };

        SyncFontBoxes(sel);
        _syncing = false;
    }

    private void SyncFontBoxes(TextSelection sel)
    {
        var family = sel.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily;
        if (family?.Source is { Length: > 0 } src) FontBox.Text = src;

        var size = sel.GetPropertyValue(TextElement.FontSizeProperty) as double? ?? 0;
        if (size > 0) SizeBox.Text = size.ToString("0.#");

        var fg = sel.GetPropertyValue(TextElement.ForegroundProperty) as SolidColorBrush;
        ColorBox.SelectedItem = fg is null ? "Auto" : ColorToName(fg.Color);

        var bg = sel.GetPropertyValue(TextElement.BackgroundProperty) as SolidColorBrush;
        HighlightBox.SelectedItem = bg is null ? "Auto" : ColorToName(bg.Color);
    }

    private static string ColorToName(Color c)
    {
        var map = new Dictionary<Color, string>
        {
            [Colors.Black] = "Black", [Colors.White] = "White", [Colors.Gray] = "Gray",
            [Colors.Red] = "Red", [Colors.Orange] = "Orange", [Colors.Yellow] = "Yellow",
            [Colors.Green] = "Green", [Colors.Teal] = "Teal", [Colors.Blue] = "Blue",
            [Colors.Indigo] = "Indigo", [Colors.Purple] = "Purple", [Colors.Pink] = "Pink",
            [Colors.Brown] = "Brown"
        };
        return map.TryGetValue(c, out var name) ? name : "Auto";
    }

    private static bool IsBold(object value) => value is FontWeight fw && fw == FontWeights.Bold;
    private static bool IsItalic(object value) => value is FontStyle fs && fs == FontStyles.Italic;
    private static bool HasDecoration(TextDecorationCollection? deco, TextDecorationLocation loc) =>
        deco is not null && deco.Any(d => d.Location == loc);
}