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
            ? (System.Windows.Media.Brush)(TryFindResource("Accent") ?? System.Windows.Media.Brushes.Gray)
            : (System.Windows.Media.Brush)(TryFindResource("MutedForeground") ?? System.Windows.Media.Brushes.Gray);
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