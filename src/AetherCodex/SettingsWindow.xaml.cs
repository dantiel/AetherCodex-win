using System.Windows;

namespace AetherCodex;

public partial class SettingsWindow : Window
{
    private static readonly string[] FontSizes =
        { "10", "11", "12", "13", "14", "15", "16", "18", "20", "22", "24", "28", "32" };

    private static readonly string[] MonoFonts =
    {
        "Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono", "Fira Code",
        "Source Code Pro", "SF Mono", "Menlo", "DejaVu Sans Mono", "Courier New"
    };

    private static readonly string[] TabSizes = { "2", "4", "8" };

    private static readonly string[] Symbols =
        { "✦", "✧", "✶", "Æ", "☿", "☉", "☽", "⚚" };

    public string SelectedTheme => ThemeBox.Text;
    public string WindowTitleValue => WindowTitleBox.Text.Trim();
    public double FontSizeValue => double.TryParse(FontSizeBox.Text, out var v) ? v : 14.0;
    public string FontFamilyValue => FontFamilyBox.Text.Trim();
    public bool ShowLineNumbers => LineNumbersCheck.IsChecked == true;
    public bool WordWrap => WordWrapCheck.IsChecked == true;
    public int TabSizeValue => int.TryParse(TabSizeBox.Text, out var v) ? Math.Clamp(v, 1, 16) : 4;
    public bool ConvertTabsToSpaces => TabsToSpacesCheck.IsChecked == true;
    public bool ShowWhitespace => WhitespaceCheck.IsChecked == true;
    public bool HighlightCurrentLine => HighlightLineCheck.IsChecked == true;
    public bool AutoStartDaemon => AutoDaemonCheck.IsChecked == true;
    public string RailSymbolValue => SymbolBox.SelectedItem as string ?? "✦";

    public SettingsWindow(Models.AppSettings current, IReadOnlyList<string> themeNames)
    {
        InitializeComponent();

        ThemeBox.ItemsSource = themeNames;
        ThemeBox.SelectedItem = themeNames.FirstOrDefault(
            n => n.Equals(current.Theme, StringComparison.OrdinalIgnoreCase));

        FontSizeBox.ItemsSource = FontSizes;
        FontSizeBox.Text = current.FontSize.ToString("0.##");

        FontFamilyBox.ItemsSource = MonoFonts;
        FontFamilyBox.Text = current.FontFamily;

        LineNumbersCheck.IsChecked = current.ShowLineNumbers;
        WordWrapCheck.IsChecked = current.WordWrap;

        TabSizeBox.ItemsSource = TabSizes;
        TabSizeBox.Text = current.TabSize.ToString();
        TabsToSpacesCheck.IsChecked = current.ConvertTabsToSpaces;
        WhitespaceCheck.IsChecked = current.ShowWhitespace;
        HighlightLineCheck.IsChecked = current.HighlightCurrentLine;

        WindowTitleBox.Text = current.WindowTitle;
        AutoDaemonCheck.IsChecked = current.AutoStartDaemon;

        SymbolBox.ItemsSource = Symbols;
        SymbolBox.SelectedItem = Symbols.FirstOrDefault(s => s == current.RailSymbol) ?? "✦";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FontFamilyValue))
        {
            MessageBox.Show("Font family cannot be empty.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }
}