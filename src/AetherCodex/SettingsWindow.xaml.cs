using System.Windows;
using System.Windows.Controls;
using AetherCodex.Models;

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

    private string _apiKey = "";
    private bool _loading = true;

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

    public string OracleProviderKey => (OracleProviderBox.SelectedItem as OracleProvider)?.Key ?? "";
    public string OracleModelValue => OracleModelBox.Text.Trim();
    public string OracleApiKeyValue => _apiKey;
    public string OracleApiUrlValue => OracleUrlBox.Text.Trim();

    private OracleModel? SelectedModel => OracleProviders.FindModel(OracleModelBox.Text.Trim());

    public string OracleApiTypeValue => SelectedModel?.ApiType ?? "";
    public string OracleFastModelValue => SelectedModel?.FastModel ?? OracleModelBox.Text.Trim();

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

        // Oracle section.
        OracleProviderBox.ItemsSource = OracleProviders.All;
        var provider = OracleProviders.FindProvider(current.OracleProvider)
                       ?? OracleProviders.All.First(p =>
                           p.Key.Equals(OracleProviders.ProviderKeyForModel(current.OracleModel), StringComparison.OrdinalIgnoreCase));
        OracleProviderBox.SelectedItem = provider;
        PopulateModels(provider);

        if (!string.IsNullOrWhiteSpace(current.OracleModel))
            OracleModelBox.Text = current.OracleModel;

        _apiKey = current.OracleApiKey ?? "";
        OracleKeyPassword.Password = _apiKey;
        OracleKeyPlain.Text = _apiKey;

        OracleUrlBox.Text = current.OracleApiUrl ?? "";

        _loading = false;
    }

    private void PopulateModels(OracleProvider provider)
    {
        OracleModelBox.ItemsSource = provider.Models;
        if (provider.Models.Count > 0)
            OracleModelBox.SelectedItem = provider.Models[0];
    }

    private void OnOracleProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (OracleProviderBox.SelectedItem is not OracleProvider provider) return;

        PopulateModels(provider);
        if (provider.Models.Count > 0 && SelectedModel is { } m)
            OracleUrlBox.Text = m.ApiUrl;
    }

    private void OnOracleModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SelectedModel is { } m)
            OracleUrlBox.Text = m.ApiUrl;
    }

    private void OnOracleKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        _apiKey = OracleKeyPassword.Password;
        OracleKeyPlain.Text = _apiKey;
    }

    private void OnOracleKeyPlainChanged(object sender, TextChangedEventArgs e)
    {
        _apiKey = OracleKeyPlain.Text;
        OracleKeyPassword.Password = _apiKey;
    }

    private void OnShowKeyChanged(object sender, RoutedEventArgs e)
    {
        bool show = ShowKeyCheck.IsChecked == true;
        if (show)
        {
            OracleKeyPlain.Text = OracleKeyPassword.Password;
            OracleKeyPlain.Visibility = Visibility.Visible;
            OracleKeyPassword.Visibility = Visibility.Collapsed;
            OracleKeyPlain.Focus();
            OracleKeyPlain.CaretIndex = OracleKeyPlain.Text.Length;
        }
        else
        {
            OracleKeyPassword.Password = OracleKeyPlain.Text;
            OracleKeyPassword.Visibility = Visibility.Visible;
            OracleKeyPlain.Visibility = Visibility.Collapsed;
        }
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
