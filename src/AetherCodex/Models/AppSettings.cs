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