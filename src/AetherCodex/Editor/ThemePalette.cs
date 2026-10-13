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
    Color SidebarForeground,
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
        yield return ("SidebarForeground", SidebarForeground);
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

        // Sidebar/file-browser text: honour the theme's dedicated sidebar/ruler hue
        // when present, but always guarantee legibility against the (possibly tinted)
        // sidebar background — a dim code fg on a tinted sidebar is the classic
        // unreadable pairing.
        var sidebarForeground = EnsureContrast(
            t.SidebarForeground ?? t.RulerForeground ?? fg, sidebar, dark);

        // Popup menus ride the chrome surface; guarantee their text against it too.
        var menuBackground = Mix(bg, fg, dark ? 0.08 : 0.04);
        var menuForeground = EnsureContrast(fg, menuBackground, dark);

        return new UiColors(
            WindowBackground: bg,
            PanelBackground: Mix(bg, fg, 0.03),
            SidebarBackground: sidebar,
            SidebarForeground: sidebarForeground,
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
            MenuBackground: menuBackground,
            MenuForeground: menuForeground,
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
        // Popup menu chrome: the stock MenuItem template resolves these keys, so
        // without them a dark theme still pops a white context menu with light text.
        app.Resources[SystemColors.MenuBrushKey] = new SolidColorBrush(ui.MenuBackground);
        app.Resources[SystemColors.MenuTextBrushKey] = new SolidColorBrush(ui.MenuForeground);
        app.Resources[SystemColors.MenuHighlightBrushKey] = new SolidColorBrush(ui.Selection);
    }

    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>Nudge <paramref name="fg"/> toward white (dark surfaces) or black
    /// (light surfaces) until it clears WCAG AA (4.5:1) against <paramref name="bg"/>.</summary>
    private static Color EnsureContrast(Color fg, Color bg, bool dark)
    {
        const double min = 4.5;
        if (ContrastRatio(fg, bg) >= min) return fg;
        var target = dark ? Colors.White : Colors.Black;
        var t = 0.0;
        for (var i = 0; i < 40; i++)
        {
            var candidate = Mix(fg, target, t);
            if (ContrastRatio(candidate, bg) >= min) return candidate;
            t += 1.0 / 40.0;
        }
        return dark ? Colors.White : Colors.Black;
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