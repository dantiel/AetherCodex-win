using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace AetherCodex.Util;

/// <summary>
/// Bundled typeface manager — the Windows analogue of the macOS FontBundler.
/// Registers Syne + Syne Mono (shipped under Resources/Fonts) so they render on
/// every machine, and installs them per-user (no admin rights) so they persist
/// across sessions. Per-user fonts live in %LOCALAPPDATA%\Microsoft\Windows\Fonts
/// + HKCU\...\Fonts.
/// </summary>
public static class FontManager
{
    private const uint FR_PRIVATE = 0x10;
    private const uint WM_FONTCHANGE = 0x001D;
    private const IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceExW(string lpszFilename, uint fl, IntPtr pdv);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceW(string lpszFilename);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Candidate directories that may hold bundled typefaces. The fonts
    /// ship with the AetherOracle submodule (Ruby/resources/Fonts) and are copied
    /// to the output root under Ruby/resources; the flat Resources/Fonts surface
    /// is also probed for future flat-layout packaging.</summary>
    private static IEnumerable<string> FontDirectories()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "Resources", "Fonts");
        yield return Path.Combine(baseDir, "Ruby", "resources", "Fonts");
    }

    public static bool IsFontAvailable(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return false;
        var wanted = family.Trim();
        foreach (var f in Fonts.SystemFontFamilies)
            if (f.Source.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Font files bundled with the app (full paths).</summary>
    public static IReadOnlyList<string> BundledFontFiles()
    {
        var files = new List<string>();
        foreach (var dir in FontDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            files.AddRange(Directory.EnumerateFiles(dir, "*.ttf", SearchOption.TopDirectoryOnly));
            files.AddRange(Directory.EnumerateFiles(dir, "*.otf", SearchOption.TopDirectoryOnly));
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Register bundled typefaces for this process (private, session-scoped,
    /// no filesystem mutation). Call before any window resolves a font.</summary>
    public static void RegisterBundledFonts()
    {
        foreach (var file in BundledFontFiles())
            AddFontResourceExW(file, FR_PRIVATE, IntPtr.Zero);
    }

    /// <summary>Install bundled fonts per-user so they survive a reboot and are
    /// visible to other applications. Idempotent — skips already-installed files.</summary>
    public static void EnsureBundledFontsInstalled()
    {
        var userFontsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts");

        var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows NT\CurrentVersion\Fonts");
        if (key is null) return;

        bool changed = false;
        foreach (var file in BundledFontFiles())
        {
            try
            {
                var fileName = Path.GetFileName(file);
                var family = FamilyNameFromFile(file);
                var valueName = $"{family} (TrueType)";
                if (string.Equals(key.GetValue(valueName) as string, fileName, StringComparison.OrdinalIgnoreCase))
                    continue; // already registered

                var target = Path.Combine(userFontsDir, fileName);
                Directory.CreateDirectory(userFontsDir);
                if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(file).Length)
                    File.Copy(file, target, overwrite: true);

                key.SetValue(valueName, fileName);
                AddFontResourceW(target);
                changed = true;
            }
            catch { /* best-effort per-user install — private registration still covers the session */ }
        }

        if (changed)
            SendMessageW(HWND_BROADCAST, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero);
    }

    private static string FamilyNameFromFile(string path)
    {
        try
        {
            var gf = new GlyphTypeface(new Uri(path));
            if (gf.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-us"), out var en) && !string.IsNullOrWhiteSpace(en))
                return en;
            var any = gf.FamilyNames.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(any)) return any;
        }
        catch { /* fall through to filename heuristic */ }
        return Path.GetFileNameWithoutExtension(path)
            .Replace("-Variable", "").Replace("-Regular", "").Trim();
    }

    /// <summary>Installed + bundled family names for the settings picker.</summary>
    public static IReadOnlyList<string> FamilyNames()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Fonts.SystemFontFamilies)
            set.Add(f.Source);
        foreach (var file in BundledFontFiles())
        {
            var fam = FamilyNameFromFile(file);
            if (fam.Length > 0) set.Add(fam);
        }
        return set.ToList();
    }
}

/// <summary>
/// The live typeface pairing the shell renders with. Set once from settings; the
/// markdown renderer and instrumentum cards read it so every surface follows the
/// user's font choice without threading families through each initialiser.
/// </summary>
public static class AppFonts
{
    public static string Ui { get; set; } = "Syne";
    public static string Mono { get; set; } = "Cascadia Mono, Consolas";
    /// <summary>Base prose size for Pythia markdown + UI copy.</summary>
    public static double UiSize { get; set; } = 14.0;

    public static FontFamily UiFamily => Safe(Ui);
    public static FontFamily MonoFamily => Safe(Mono);

    private static FontFamily Safe(string name)
    {
        try { return new FontFamily(name); }
        catch { return new FontFamily("Segoe UI"); }
    }
}