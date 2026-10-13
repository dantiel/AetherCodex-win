using System.Text.Json;

namespace AetherCodex.Models;

/// <summary>User-facing preferences, persisted to <c>%APPDATA%\AetherCodex\settings.json</c>.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "AetherNight";
    public string WindowTitle { get; set; } = "ÆtherCodex";
    public string RailSymbol { get; set; } = "✦";
    public double FontSize { get; set; } = 14.0;
    public string FontFamily { get; set; } = "Cascadia Mono, Consolas";
    /// <summary>Proportional font for the shell chrome + Pythia markdown prose.
    /// Defaults to Syne — the bundled display face, registered at startup.</summary>
    public string UiFontFamily { get; set; } = "Syne";
    public double UiFontSize { get; set; } = 14.0;
    /// <summary>When true, window transparency follows the active theme's declared
    /// opacity. When false, <see cref="WindowOpacity"/> wins.</summary>
    public bool UseThemeOpacity { get; set; } = true;
    /// <summary>Per-user window opacity override (0.0..1.0). Only applied when
    /// <see cref="UseThemeOpacity"/> is false.</summary>
    public double WindowOpacity { get; set; } = 1.0;
    public bool ShowLineNumbers { get; set; } = true;
    public bool WordWrap { get; set; } = false;
    public int TabSize { get; set; } = 4;
    public bool ConvertTabsToSpaces { get; set; } = true;
    public bool ShowWhitespace { get; set; } = false;
    public bool HighlightCurrentLine { get; set; } = true;
    public bool AutoStartDaemon { get; set; } = false;
    public string LastDirectory { get; set; } = "";

    // Oracle / AI provider credentials. Persisted in settings.json; handed to the
    // Ruby daemon as AETHER_* environment variables (the highest-priority channel
    // in AetherOracle's CONFIG hierarchy) so the soul picks them up on launch.
    public string OracleProvider { get; set; } = "";
    public string OracleModel { get; set; } = "";
    public string OracleApiKey { get; set; } = "";
    public string OracleApiUrl { get; set; } = "";
    public string OracleApiType { get; set; } = "";
    public string OracleFastModel { get; set; } = "";

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AetherCodex", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}