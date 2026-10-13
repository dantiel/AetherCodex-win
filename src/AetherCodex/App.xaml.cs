using System.Windows;
using System.Windows.Media;
using AetherCodex.Util;

namespace AetherCodex;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Register the bundled typefaces (Syne + Syne Mono) before any window
        // resolves a font, then install them per-user for persistence.
        FontManager.RegisterBundledFonts();
        FontManager.EnsureBundledFontsInstalled();
        ApplyUiFont();

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            var ex = args.Exception;
            MessageBox.Show(
                $"An unhandled fault escaped the athanor:\n\n{ex.Message}\n\n{ex.StackTrace}",
                "ÆtherCodex",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };
    }

    /// <summary>Apply the user's proportional (UI + markdown) font to the whole
    /// shell. MainWindow re-invokes this after a settings change.</summary>
    public static void ApplyUiFont()
    {
        var s = Models.AppSettings.Load();
        var family = string.IsNullOrWhiteSpace(s.UiFontFamily) ? "Syne" : s.UiFontFamily;
        AppFonts.Ui = family;
        AppFonts.Mono = string.IsNullOrWhiteSpace(s.FontFamily) ? "Cascadia Mono, Consolas" : s.FontFamily;

        try
        {
            var app = Current;
            if (app is null) return;
            app.Resources["UiFont"] = new FontFamily(family);
            app.Resources["MonoFont"] = new FontFamily(AppFonts.Mono);
        }
        catch { /* a missing family falls back to Segoe UI */ }
    }
}
