using System.Globalization;
using System.Windows.Media;
using AetherCodex.Util;

namespace AetherCodex.Editor;

/// <summary>Loads the YAML syntax themes from <c>Resources/Themes</c>.</summary>
public static class ThemeLoader
{
    public static string ThemesDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "Themes");

    public static IReadOnlyList<SyntaxTheme> LoadAll()
    {
        var dir = ThemesDirectory;
        var themes = new List<SyntaxTheme>();
        if (!Directory.Exists(dir)) return themes;

        foreach (var file in Directory.EnumerateFiles(dir, "*.yml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var theme = Load(file);
                if (theme is not null) themes.Add(theme);
            }
            catch
            {
                // A malformed theme is skipped; the athanor does not shatter on a flawed pigment.
            }
        }
        return themes.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static SyntaxTheme? Load(string path)
    {
        var text = File.ReadAllText(path);
        var root = SimpleYaml.Parse(text);

        string GetString(string key, string fallback = "")
            => root.TryGetValue(key, out var v) && v is string s ? s : fallback;

        double GetDouble(string key, double fallback)
            => root.TryGetValue(key, out var v)
               && double.TryParse(v?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d
                : fallback;

        Color EditorColor(string key, Color fallback)
            => root.TryGetValue("editor", out var e) && e is Dictionary<string, object?> ed
                ? ThemeColor.Parse(ed.GetValueOrDefault(key), fallback)
                : fallback;

        Color PythiaColor(string key, Color fallback)
            => root.TryGetValue("pythia", out var p) && p is Dictionary<string, object?> pd
                ? ThemeColor.Parse(pd.GetValueOrDefault(key), fallback)
                : fallback;

        Color? TopColorOrNull(string key)
            => root.TryGetValue(key, out var v) && v is string s
                ? ThemeColor.Parse(s, default)
                : null;

        Color? PythiaColorOrNull(string key)
            => root.TryGetValue("pythia", out var p) && p is Dictionary<string, object?> pd
               && pd.TryGetValue(key, out var v) && v is string s
                ? ThemeColor.Parse(s, default)
                : null;

        var background = EditorColor("background", Color.FromRgb(0x06, 0x07, 0x0D));
        var foreground = EditorColor("foreground", Color.FromRgb(0x8E, 0x94, 0xA0));

        var scopes = new Dictionary<string, ScopeStyle>(StringComparer.Ordinal);
        if (root.TryGetValue("scopes", out var sc) && sc is Dictionary<string, object?> scopeMap)
        {
            foreach (var (name, style) in scopeMap)
            {
                scopes[name] = ParseScopeStyle(style, foreground);
            }
        }

        return new SyntaxTheme
        {
            Name = GetString("name", Path.GetFileNameWithoutExtension(path)),
            Kind = GetString("kind", "dark"),
            FilePath = path,
            Background = background,
            Foreground = foreground,
            Caret = EditorColor("caret", Color.FromRgb(0xB4, 0x8C, 0xFF)),
            Selection = EditorColor("selection", Color.FromRgb(0x1B, 0x24, 0x33)),
            LineHighlight = EditorColor("lineHighlight", Color.FromRgb(0x0C, 0x0E, 0x15)),
            PythiaBackground = PythiaColor("background", background),
            PythiaForeground = PythiaColor("foreground", foreground),
            PythiaAccent = PythiaColor("accent", Color.FromRgb(0x8C, 0x7A, 0xB8)),
            PythiaComplementary = PythiaColor("complementary", Color.FromRgb(0xB0, 0xA0, 0x6E)),
            PythiaBaseText = PythiaColor("baseText", PythiaColor("complementary", Color.FromRgb(0xB0, 0xA0, 0x6E))),
            PythiaCodeBackground = PythiaColor("codeBackground", background),
            PythiaCodeForeground = PythiaColor("codeForeground", foreground),
            SidebarBackground = TopColorOrNull("sidebarBackground"),
            SidebarAccent = TopColorOrNull("sidebarAccent"),
            SidebarForeground = TopColorOrNull("sidebarForeground"),
            RulerBackground = TopColorOrNull("rulerBackground"),
            RulerForeground = TopColorOrNull("rulerForeground"),
            RulerSeparator = TopColorOrNull("rulerSeparator"),
            Shine = PythiaColorOrNull("shine"),
            WindowOpacity = Math.Clamp(GetDouble("opacity", GetDouble("windowOpacity", 1.0)), 0.15, 1.0),
            Scopes = scopes,
        };
    }

    private static ScopeStyle ParseScopeStyle(object? value, Color fallback)
    {
        if (value is string colorString)
            return new ScopeStyle(ThemeColor.Parse(colorString, fallback));

        if (value is Dictionary<string, object?> map)
        {
            var color = ThemeColor.Parse(map.GetValueOrDefault("color"), fallback);
            double? fontSize = map.GetValueOrDefault("fontSize") is string fs && double.TryParse(fs, out var fv)
                ? fv
                : map.GetValueOrDefault("fontSize") is double fd ? fd : null;
            Color? background = map.GetValueOrDefault("background") is string bg
                ? ThemeColor.Parse(bg, default)
                : null;
            return new ScopeStyle(
                color,
                map.GetValueOrDefault("bold") is bool b && b,
                map.GetValueOrDefault("italic") is bool i && i,
                map.GetValueOrDefault("underline") is bool u && u,
                fontSize,
                background,
                map.GetValueOrDefault("strikethrough") is bool st && st);
        }

        return new ScopeStyle(fallback);
    }
}