using System.Windows;

namespace AetherCodex;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
}