using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using AetherCodex.Bridge;
using AetherCodex.Models;
using AetherCodex.Util;

namespace AetherCodex;

public partial class SettingsWindow : Window
{
    private static readonly string[] FontSizes =
        { "10", "11", "12", "13", "14", "15", "16", "18", "20", "22", "24", "28", "32" };

    private static readonly string[] TabSizes = { "2", "4", "8" };

    private static readonly string[] Symbols =
        { "âœ¦", "âœ§", "âœ¶", "Ã†", "â˜¿", "â˜‰", "â˜½", "âšš" };

    private string _apiKey = "";
    private bool _loading = true;
    private double _themeDefaultOpacity = 1.0;
    private double _overrideOpacity = 1.0;

    private string _origOracleModel = "";
    private string _origOracleFastModel = "";
    private string _origOracleApiType = "";
    private string _origOracleApiUrl = "";
    private string _origOracleApiKey = "";

    private readonly RubyBridge _bridge;
    private readonly string? _projectRoot;
    private readonly Action<string> _openConfigFile;
    private ProjectConfig? _projectConfig;
    private string _projectApiKey = "";
    private readonly Dictionary<string, string> _originalProjectValues = new();

    public string SelectedTheme => ThemeBox.Text;
    public string WindowTitleValue => WindowTitleBox.Text.Trim();
    public double FontSizeValue => double.TryParse(FontSizeBox.Text, out var v) ? v : 14.0;
    public string FontFamilyValue => FontFamilyBox.Text.Trim();
    public string UiFontFamilyValue => UiFontFamilyBox.Text.Trim();
    public double UiFontSizeValue => double.TryParse(UiFontSizeBox.Text, out var v) ? v : 14.0;
    public bool ShowLineNumbers => LineNumbersCheck.IsChecked == true;
    public bool WordWrap => WordWrapCheck.IsChecked == true;
    public int TabSizeValue => int.TryParse(TabSizeBox.Text, out var v) ? Math.Clamp(v, 1, 16) : 4;
    public bool ConvertTabsToSpaces => TabsToSpacesCheck.IsChecked == true;
    public bool ShowWhitespace => WhitespaceCheck.IsChecked == true;
    public bool HighlightCurrentLine => HighlightLineCheck.IsChecked == true;
    public bool AutoStartDaemon => AutoDaemonCheck.IsChecked == true;
    public string RailSymbolValue => SymbolBox.SelectedItem as string ?? "âœ¦";

    public bool UseThemeOpacity => OpacityUseDefaultCheck.IsChecked == true;
    public double WindowOpacityValue => Math.Clamp(_overrideOpacity, 0.15, 1.0);

    public string OracleProviderKey => (OracleProviderBox.SelectedItem as OracleProvider)?.Key ?? "";
    public string OracleModelValue => OracleModelBox.Text.Trim();
    public string OracleApiKeyValue => _apiKey;
    public string OracleApiUrlValue => OracleUrlBox.Text.Trim();

    private OracleModel? SelectedModel => OracleProviders.FindModel(OracleModelBox.Text.Trim());

    public string OracleApiTypeValue => SelectedModel?.ApiType ?? "";
    public string OracleFastModelValue => SelectedModel?.FastModel ?? OracleModelBox.Text.Trim();

    /// <summary>Personal AI-provider keys the user actually changed relative to the
    /// snapshot taken on open. Used by MainWindow to write only real edits to
    /// <c>~/.aethercodex</c> — an auto-filled default is not an edit.</summary>
    public Dictionary<string, string> OracleChanges()
    {
        var changes = new Dictionary<string, string>();

        void Track(string key, string? before, string? after)
        {
            if (string.IsNullOrWhiteSpace(after)) return;
            if (string.Equals(before ?? "", after, StringComparison.Ordinal)) return;
            changes[key] = after;
        }

        Track("model", _origOracleModel, OracleModelValue);
        Track("fast-model", _origOracleFastModel, OracleFastModelValue);
        Track("api-type", _origOracleApiType, OracleApiTypeValue);
        Track("api-url", _origOracleApiUrl, OracleApiUrlValue);
        Track("api-key", _origOracleApiKey, OracleApiKeyValue);
        return changes;
    }

    public SettingsWindow(
        Models.AppSettings current,
        IReadOnlyList<string> themeNames,
        double themeDefaultOpacity,
        RubyBridge bridge,
        string? projectRoot,
        Action<string> openConfigFile)
    {
        InitializeComponent();
        _bridge = bridge;
        _projectRoot = projectRoot;
        _openConfigFile = openConfigFile;

        ThemeBox.ItemsSource = themeNames;
        ThemeBox.SelectedItem = themeNames.FirstOrDefault(
            n => n.Equals(current.Theme, StringComparison.OrdinalIgnoreCase));

        FontSizeBox.ItemsSource = FontSizes;
        FontSizeBox.Text = current.FontSize.ToString("0.##");

        var families = FontManager.FamilyNames();
        UiFontFamilyBox.ItemsSource = families;
        UiFontFamilyBox.Text = string.IsNullOrWhiteSpace(current.UiFontFamily) ? "Syne" : current.UiFontFamily;
        UiFontSizeBox.ItemsSource = FontSizes;
        UiFontSizeBox.Text = current.UiFontSize.ToString("0.##");

        FontFamilyBox.ItemsSource = families;
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
        SymbolBox.SelectedItem = Symbols.FirstOrDefault(s => s == current.RailSymbol) ?? "âœ¦";

        // Transparency: theme default vs per-user override.
        _themeDefaultOpacity = Math.Clamp(themeDefaultOpacity, 0.15, 1.0);
        _overrideOpacity = current.WindowOpacity > 0 ? Math.Clamp(current.WindowOpacity, 0.15, 1.0) : _themeDefaultOpacity;
        OpacityUseDefaultCheck.Content = $"Use theme default ({_themeDefaultOpacity * 100:0}%)";
        OpacityUseDefaultCheck.IsChecked = current.UseThemeOpacity;
        SyncOpacityControls(current.UseThemeOpacity);

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

        // Snapshot the personal defaults so the apply path can write only keys the
        // user actually changed to `~/.aethercodex` (never clobber a real home
        // value with a UI default that merely auto-filled the box).
        _origOracleModel = current.OracleModel ?? "";
        _origOracleFastModel = current.OracleFastModel ?? "";
        _origOracleApiType = current.OracleApiType ?? "";
        _origOracleApiUrl = current.OracleApiUrl ?? "";
        _origOracleApiKey = current.OracleApiKey ?? "";

        _loading = false;

        // Project config tab: populated asynchronously from the Ruby soul.
        ProjectRootText.Text = _projectRoot ?? "(no project open)";
        _ = LoadProjectConfigAsync();
    }

    // â”€â”€ Project config tab â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task LoadProjectConfigAsync()
    {
        try
        {
            var dict = await _bridge.GetConfigAsync();
            _projectConfig = ParseProjectConfig(dict);
            PopulateProjectTab();
        }
        catch (Exception ex)
        {
            _projectConfig = null;
            ProjectHintText.Text = $"Daemon unavailable â€” project config not loaded ({ex.Message}).";
        }
    }

    private static ProjectConfig? ParseProjectConfig(JsonObject? dict)
    {
        if (dict is null) return null;

        var cfg = new ProjectConfig
        {
            ProjectRoot = dict["project_root"]?.GetValue<string>(),
            Name = dict["name"]?.GetValue<string>(),
            ConfigPath = dict["config_path"]?.GetValue<string>(),
            Format = dict["format"]?.GetValue<string>() ?? "yaml",
        };

        if (dict["values"] is JsonObject values)
        {
            foreach (var (key, node) in values)
            {
                if (node is not JsonObject obj) continue;
                cfg.Values[key] = new ProjectConfigValue(
                    ValueToString(obj["value"]),
                    obj["source"]?.GetValue<string>() ?? "default");
            }
        }
        return cfg;
    }

    private static string? ValueToString(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(),
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("0.##"),
        _ => node.ToJsonString(),
    };

    private void PopulateProjectTab()
    {
        if (_projectConfig is null)
        {
            ResetProjectTab();
            return;
        }

        ProjectRootText.Text = _projectConfig.ProjectRoot ?? "";
        ConfigPathText.Text = _projectConfig.ConfigPath ?? "(none â€” will create .aethercodex on save)";
        ConfigFormatText.Text = _projectConfig.Format == "properties"
            ? "Format: .aether_properties (flat key = value)"
            : "Format: .aethercodex (YAML)";
        OpenConfigButton.Content = _projectConfig.ConfigPath is null ? "Create" : "Open";

        SetField(NameBox, NameSource, "name");
        SetField(ModelBox, ModelSource, "model");
        SetField(FastModelBox, FastModelSource, "fast-model");
        SetField(ApiTypeBox, ApiTypeSource, "api-type");
        SetField(ApiUrlBox, ApiUrlSource, "api-url");
        SetApiKeyField();
        SetField(PortBox, PortSource, "port");
        SetCheck(DevModeCheck, DevModeSource, "dev_mode");
        SetCheck(ReasoningModelCheck, ReasoningModelSource, "reasoning-model");

        ProjectHintText.Text =
            "Values shown are the effective configuration the oracle resolves. " +
            "Edits are written to this project's config file and reloaded live â€” " +
            "project values override your personal defaults (home) and the built-in defaults.";
    }

    private void ResetProjectTab()
    {
        ConfigPathText.Text = "";
        ConfigFormatText.Text = "";
        OpenConfigButton.Content = "Open";
        ProjectHintText.Text = "Start the Ruby oracle daemon to read the project config.";
    }

    private void SetField(TextBox box, TextBlock badge, string key)
    {
        if (_projectConfig!.Values.TryGetValue(key, out var v))
        {
            box.Text = v.Value ?? "";
            badge.Text = SourceLabel(v.Source);
            badge.ToolTip = SourceTooltip(v.Source);
            _originalProjectValues[key] = v.Value ?? "";
        }
        else
        {
            box.Text = "";
            badge.Text = "";
            _originalProjectValues[key] = "";
        }
    }

    private void SetCheck(CheckBox box, TextBlock badge, string key)
    {
        if (_projectConfig!.Values.TryGetValue(key, out var v))
        {
            box.IsChecked = ParseBool(v.Value);
            badge.Text = SourceLabel(v.Source);
            badge.ToolTip = SourceTooltip(v.Source);
            _originalProjectValues[key] = v.Value ?? "";
        }
        else
        {
            box.IsChecked = false;
            badge.Text = "";
            _originalProjectValues[key] = "";
        }
    }

    private void SetApiKeyField()
    {
        if (_projectConfig!.Values.TryGetValue("api-key", out var v))
        {
            _projectApiKey = v.Value ?? "";
            ProjectKeyPassword.Password = _projectApiKey;
            ProjectKeyPlain.Text = _projectApiKey;
            ApiKeySource.Text = SourceLabel(v.Source);
            ApiKeySource.ToolTip = SourceTooltip(v.Source);
            _originalProjectValues["api-key"] = v.Value ?? "";
        }
        else
        {
            _projectApiKey = "";
            ProjectKeyPassword.Password = "";
            ProjectKeyPlain.Text = "";
            ApiKeySource.Text = "";
            _originalProjectValues["api-key"] = "";
        }
    }

    private static string SourceLabel(string source) => source switch
    {
        "env" => "env",
        "project" => "project",
        "home" => "home",
        "bundle" => "bundle",
        "preset" => "preset",
        _ => "default",
    };

    private static string SourceTooltip(string source) => source switch
    {
        "env" => "Overridden by an environment variable.",
        "project" => "Set in this project's config file.",
        "home" => "Inherited from your personal ~/.aethercodex.",
        "bundle" => "Bundled default from the oracle.",
        "preset" => "Derived from the chosen model preset.",
        _ => "Built-in default â€” not set anywhere.",
    };

    private static bool ParseBool(string? value) =>
        value is "true" or "1" or "yes" or "on";

    private async Task SaveProjectConfigAsync()
    {
        if (_projectConfig is null) return;

        var changes = new List<(string Key, string? Value)>();

        void TrackString(string key, string? value)
        {
            var v = value?.Trim();
            if (string.IsNullOrEmpty(v)) return; // clearing a string keeps the inherited value
            if (_originalProjectValues.TryGetValue(key, out var orig) && orig == v) return;
            changes.Add((key, v));
        }

        void TrackBool(string key, bool value)
        {
            var s = value ? "true" : "false";
            if (_originalProjectValues.TryGetValue(key, out var orig) && orig == s) return;
            changes.Add((key, s));
        }

        TrackString("name", NameBox.Text);
        TrackString("model", ModelBox.Text);
        TrackString("fast-model", FastModelBox.Text);
        TrackString("api-type", ApiTypeBox.Text);
        TrackString("api-url", ApiUrlBox.Text);
        TrackString("api-key", _projectApiKey);
        TrackString("port", PortBox.Text);
        TrackBool("dev_mode", DevModeCheck.IsChecked == true);
        TrackBool("reasoning-model", ReasoningModelCheck.IsChecked == true);

        foreach (var (key, value) in changes)
            await _bridge.SetConfigAsync(key, value);
    }

    private void OnOpenConfig(object sender, RoutedEventArgs e)
    {
        var path = _projectConfig?.ConfigPath
                   ?? Path.Combine(_projectRoot ?? Directory.GetCurrentDirectory(), ".aethercodex");
        _openConfigFile(path);
    }

    private async void OnRefreshConfig(object sender, RoutedEventArgs e) =>
        await LoadProjectConfigAsync();

    private void OnProjectKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        _projectApiKey = ProjectKeyPassword.Password;
        ProjectKeyPlain.Text = _projectApiKey;
    }

    private void OnProjectKeyPlainChanged(object sender, TextChangedEventArgs e)
    {
        _projectApiKey = ProjectKeyPlain.Text;
        ProjectKeyPassword.Password = _projectApiKey;
    }

    private void OnProjectShowKeyChanged(object sender, RoutedEventArgs e)
    {
        bool show = ProjectShowKeyCheck.IsChecked == true;
        if (show)
        {
            ProjectKeyPlain.Text = ProjectKeyPassword.Password;
            ProjectKeyPlain.Visibility = Visibility.Visible;
            ProjectKeyPassword.Visibility = Visibility.Collapsed;
            ProjectKeyPlain.Focus();
            ProjectKeyPlain.CaretIndex = ProjectKeyPlain.Text.Length;
        }
        else
        {
            ProjectKeyPassword.Password = ProjectKeyPlain.Text;
            ProjectKeyPassword.Visibility = Visibility.Visible;
            ProjectKeyPlain.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncOpacityControls(bool useDefault)
    {
        OpacitySlider.IsEnabled = !useDefault;
        OpacityValueText.IsEnabled = !useDefault;
        OpacitySlider.Value = (useDefault ? _themeDefaultOpacity : _overrideOpacity) * 100.0;
        OpacityValueText.Text = $"{(int)Math.Round(OpacitySlider.Value)}%";
    }

    private void OnUseDefaultOpacityChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SyncOpacityControls(OpacityUseDefaultCheck.IsChecked == true);
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        if (OpacityUseDefaultCheck.IsChecked != true)
            _overrideOpacity = Math.Clamp(OpacitySlider.Value / 100.0, 0.15, 1.0);
        OpacityValueText.Text = $"{(int)Math.Round(OpacitySlider.Value)}%";
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

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FontFamilyValue))
        {
            MessageBox.Show("Font family cannot be empty.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await SaveProjectConfigAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Project config could not be saved: {ex.Message}", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        DialogResult = true;
        Close();
    }
}