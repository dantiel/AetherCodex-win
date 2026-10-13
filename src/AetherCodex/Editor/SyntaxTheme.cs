using System.Globalization;
using System.Windows.Media;

namespace AetherCodex.Editor;

/// <summary>A decoded syntax theme, mirrored from the macOS SyntaxTheme + ThemeLoader.</summary>
public sealed class SyntaxTheme
{
    public string Name { get; init; } = "AetherNight";
    public string Kind { get; init; } = "dark";
    public string FilePath { get; init; } = "";

    public Color Background { get; init; } = Color.FromRgb(0x06, 0x07, 0x0D);
    public Color Foreground { get; init; } = Color.FromRgb(0x8E, 0x94, 0xA0);
    public Color Caret { get; init; } = Color.FromRgb(0xB4, 0x8C, 0xFF);
    public Color Selection { get; init; } = Color.FromRgb(0x1B, 0x24, 0x33);
    public Color LineHighlight { get; init; } = Color.FromRgb(0x0C, 0x0E, 0x15);
    /// <summary>Whole-window translucency declared by the theme (0.0..1.0).
    /// Overridable per-user in the settings dialog.</summary>
    public double WindowOpacity { get; init; } = 1.0;

    public Color PythiaBackground { get; init; } = Color.FromRgb(0x06, 0x07, 0x0D);
    public Color PythiaForeground { get; init; } = Color.FromRgb(0x8E, 0x94, 0xA0);
    public Color PythiaAccent { get; init; } = Color.FromRgb(0x8C, 0x7A, 0xB8);
    public Color PythiaComplementary { get; init; } = Color.FromRgb(0xB0, 0xA0, 0x6E);
    /// <summary>The oracle's primary voice — message-body prose. Distinct from
    /// <see cref="PythiaForeground"/> (muted chrome/secondary). Argonaut declares
    /// golden amber; other themes derive it from their complementary tone.</summary>
    public Color PythiaBaseText { get; init; } = Color.FromRgb(0xB0, 0xA0, 0x6E);
    public Color PythiaCodeBackground { get; init; } = Color.FromRgb(0x08, 0x0A, 0x10);
    public Color PythiaCodeForeground { get; init; } = Color.FromRgb(0x8E, 0x94, 0xA0);

    // Optional top-level chrome hints present in the richer themes (sidebar/ruler
    // surfaces + pythia "shine"). Null means "derive from the core colours".
    public Color? SidebarBackground { get; init; }
    public Color? SidebarAccent { get; init; }
    public Color? SidebarForeground { get; init; }
    public Color? RulerBackground { get; init; }
    public Color? RulerForeground { get; init; }
    public Color? RulerSeparator { get; init; }
    public Color? Shine { get; init; }

    /// <summary>TextMate scope name → color (and optional style).</summary>
    public IReadOnlyDictionary<string, ScopeStyle> Scopes { get; init; }
        = new Dictionary<string, ScopeStyle>();

    public bool IsDark => Kind.Equals("dark", StringComparison.OrdinalIgnoreCase);

    public ScopeStyle StyleFor(string scope, ScopeStyle fallback)
        => Scopes.TryGetValue(scope, out var s) ? s : fallback;

    /// <summary>Resolve the best style for a scope by trying progressively shorter prefixes.</summary>
    public ScopeStyle Resolve(string scope, ScopeStyle fallback)
    {
        if (Scopes.TryGetValue(scope, out var exact)) return exact;
        var dot = scope.LastIndexOf('.');
        while (dot > 0)
        {
            if (Scopes.TryGetValue(scope[..dot], out var parent)) return parent;
            dot = scope.LastIndexOf('.', dot - 1);
        }
        return fallback;
    }
}

/// <summary>A scope's visual style (color + optional decorations).</summary>
public readonly record struct ScopeStyle(
    Color Color,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    double? FontSize = null,
    Color? Background = null,
    bool Strikethrough = false)
{
    public static ScopeStyle FromColor(Color c) => new(c);
}

public static class ThemeColor
{
    /// <summary>Parse a theme colour literal: "0xRRGGBB", "0xRRGGBBAA", or "#RRGGBB".</summary>
    public static Color Parse(object? value, Color fallback)
    {
        if (value is not string s) return fallback;
        s = s.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        try
        {
            if (s.Length == 6)
                return Color.FromRgb(
                    byte.Parse(s[..2], NumberStyles.HexNumber),
                    byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber),
                    byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber));
            if (s.Length == 8)
                return Color.FromArgb(
                    byte.Parse(s[..2], NumberStyles.HexNumber),
                    byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber),
                    byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber),
                    byte.Parse(s.Substring(6, 2), NumberStyles.HexNumber));
        }
        catch (FormatException) { }
        return fallback;
    }
}