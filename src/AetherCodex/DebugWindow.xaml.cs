using System.Windows;
using System.Windows.Threading;
using AetherCodex.Bridge;

namespace AetherCodex;

public partial class DebugWindow : Window
{
    private readonly RubyBridge _bridge;
    private readonly DispatcherTimer _timer;

    public DebugWindow(RubyBridge bridge)
    {
        InitializeComponent();
        _bridge = bridge;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var log = _bridge.DaemonLog;
        Log.Text = string.Join(Environment.NewLine, log);
        Log.ScrollToEnd();
        StatusDot.Fill = _bridge.IsRunning
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8C, 0x7A, 0xB8))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5A, 0x60, 0x68));
    }

    private async void OnRestart(object sender, RoutedEventArgs e)
    {
        try { await _bridge.RestartAsync(); }
        catch (Exception ex) { Log.Text += Environment.NewLine + "⚠ " + ex.Message; }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _bridge.ClearDaemonLog();
        Log.Clear();
    }
}
