using System.Text.Json;

namespace AetherCodex.Models;

/// <summary>User-facing preferences, persisted to <c>%APPDATA%\AetherCodex\settings.json</c>.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "AetherNight";
    public double FontSize { get; set; } = 14.0;
    public string FontFamily { get; set; } = "Cascadia Mono, Consolas";
    public bool ShowLineNumbers { get; set; } = true;
    public bool WordWrap { get; set; } = false;
    public bool AutoStartDaemon { get; set; } = false;
    public string LastDirectory { get; set; } = "";

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
