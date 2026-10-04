using System.Windows;
using System.Windows.Media;

namespace AetherCodex.Editor;

/// <summary>
/// The derived chrome palette for a <see cref="SyntaxTheme"/>. YAML themes only
/// describe editor + pythia surfaces; everything else in the shell (menu, sidebar,
/// splitters, textboxes, buttons, selection chrome) is synthesised here so the
/// whole window follows the active theme instead of a single hard-coded night skin.
/// </summary>
public sealed record UiColors(
    Color WindowBackground,
    Color PanelBackground,
    Color SidebarBackground,
    Color EditorBackground,
    Color ChromeBackground,
    Color Foreground,
    Color MutedForeground,
    Color Accent,
    Color AccentBright,
    Color Complementary,
    Color Border,
    Color Selection,
    Color LineHighlight,
    Color CodeBackground,
    Color UserBubble,
    Color PythiaBubble,
    Color Danger,
    Color MenuBackground,
    Color MenuForeground,
    Color MenuHighlight)
{
    /// <summary>Resource keys mirroring <c>Themes/Dark.xaml</c>.</summary>
    public IEnumerable<(string Key, Color Color)> Entries()
    {
        yield return ("WindowBackground", WindowBackground);
        yield return ("PanelBackground", PanelBackground);
        yield return ("SidebarBackground", SidebarBackground);
        yield return ("EditorBackground", EditorBackground);
        yield return ("ChromeBackground", ChromeBackground);
        yield return ("Foreground", Foreground);
        yield return ("MutedForeground", MutedForeground);
        yield return ("Accent", Accent);
        yield return ("AccentBright", AccentBright);
        yield return ("Complementary", Complementary);
        yield return ("Border", Border);
        yield return ("Selection", Selection);
        yield return ("LineHighlight", LineHighlight);
        yield return ("CodeBackground", CodeBackground);
        yield return ("UserBubble", UserBubble);
        yield return ("PythiaBubble", PythiaBubble);
        yield return ("Danger", Danger);
        yield return ("MenuBackground", MenuBackground);
        yield return ("MenuForeground", MenuForeground);
        yield return ("MenuHighlight", MenuHighlight);
    }
}

public static class ThemePalette
{
    public static UiColors Build(SyntaxTheme t)
    {
        bool dark = t.IsDark;
        var bg = t.Background;
        var fg = t.Foreground;
        var accent = t.PythiaAccent;

        var sidebar = t.SidebarBackground ?? Mix(bg, fg, 0.04);
        var chrome = Mix(bg, fg, 0.05);
        var border = Mix(bg, fg, 0.14);
        var muted = t.RulerForeground ?? Mix(bg, fg, 0.52);
        var accentBright = t.Shine
            ?? (dark ? Mix(accent, Colors.White, 0.32) : Mix(accent, Colors.Black, 0.22));

        return new UiColors(
            WindowBackground: bg,
            PanelBackground: Mix(bg, fg, 0.03),
            SidebarBackground: sidebar,
            EditorBackground: bg,
            ChromeBackground: chrome,
            Foreground: fg,
            MutedForeground: muted,
            Accent: accent,
            AccentBright: accentBright,
            Complementary: t.PythiaComplementary,
            Border: border,
            Selection: t.Selection,
            LineHighlight: t.LineHighlight,
            CodeBackground: t.PythiaCodeBackground,
            UserBubble: t.Selection,
            PythiaBubble: t.PythiaBackground,
            Danger: dark ? Color.FromRgb(0xD0, 0x5A, 0x6E) : Color.FromRgb(0xC0, 0x39, 0x2B),
            MenuBackground: chrome,
            MenuForeground: fg,
            MenuHighlight: t.Selection);
    }

    /// <summary>Push the palette into the application resources so every
    /// <c>{DynamicResource ...}</c> surface re-skins live.</summary>
    public static void Apply(SyntaxTheme t)
    {
        var app = Application.Current;
        if (app is null) return;

        var ui = Build(t);
        foreach (var (key, color) in ui.Entries())
            app.Resources[key] = new SolidColorBrush(color);

        // System-colour overrides: the stock templates (menu hover, list selection)
        // resolve these keys dynamically, so retheme them alongside the named palette.
        app.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(ui.Selection);
        app.Resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(ui.Foreground);
        app.Resources[SystemColors.ControlBrushKey] = new SolidColorBrush(ui.ChromeBackground);
        app.Resources[SystemColors.ControlTextBrushKey] = new SolidColorBrush(ui.Foreground);
        app.Resources[SystemColors.WindowBrushKey] = new SolidColorBrush(ui.WindowBackground);
        app.Resources[SystemColors.WindowTextBrushKey] = new SolidColorBrush(ui.Foreground);
        app.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(ui.Selection);
    }

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }
}
