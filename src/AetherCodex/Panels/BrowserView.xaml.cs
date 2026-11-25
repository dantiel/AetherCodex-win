using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace AetherCodex.Panels;

/// <summary>
/// A Chromium (WebView2) browser surface shown as an editor tab. Shared across all
/// browser documents — like <see cref="Editor.RichTextEditor"/> and the sheet editor,
/// the active document re-renders into this single surface.
/// </summary>
public partial class BrowserView : UserControl
{
    /// <summary>Raised when the page title changes (drives the tab caption).</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>Raised when the committed URL changes (drives the path label).</summary>
    public event EventHandler<string>? UrlChanged;

    private string _homeUrl = "about:blank";
    private string _title = "";
    private bool _initialized;

    public string CurrentUrl => WebView.Source?.ToString() ?? _homeUrl;
    public string CurrentTitle => _title;

    public BrowserView()
    {
        InitializeComponent();
        BackButton.IsEnabled = false;
        ForwardButton.IsEnabled = false;
    }

    /// <summary>Set the page loaded by the home button (and default for empty navigations).</summary>
    public void SetHome(string url) => _homeUrl = NormalizeUrl(url);

    /// <summary>Navigate to <paramref name="url"/>; empty/blank targets the home page.</summary>
    public void Navigate(string? url)
    {
        var target = string.IsNullOrWhiteSpace(url) ? _homeUrl : NormalizeUrl(url);
        if (_initialized)
        {
            WebView.CoreWebView2.Navigate(target);
            return;
        }

        _ = InitAndNavigateAsync(target);
    }

    public void GoHome() => Navigate(_homeUrl);

    public void FocusAddress()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private async Task InitAndNavigateAsync(string url)
    {
        try
        {
            WebView.DefaultBackgroundColor = ResolveEditorBackground();
            var userDataFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AetherCodex", "WebView2");

            var environment = await CoreWebView2Environment.CreateAsync(
                null, userDataFolder, new CoreWebView2EnvironmentOptions());

            await WebView.EnsureCoreWebView2Async(environment);
            WireCore();
            _initialized = true;
            WebView.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            ErrorOverlay.Text = "The WebView2 runtime is not available, so this browser tab cannot render pages.\n\n"
                + ex.Message;
            ErrorOverlay.Visibility = Visibility.Visible;
        }
    }

    private void WireCore()
    {
        var core = WebView.CoreWebView2;
        core.Settings.AreDefaultScriptDialogsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;

        core.SourceChanged += (_, _) =>
        {
            AddressBox.Text = core.Source;
            UpdateNavState();
            UrlChanged?.Invoke(this, core.Source);
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            _title = core.DocumentTitle;
            TitleChanged?.Invoke(this, _title);
        };
        core.NavigationCompleted += (_, _) => UpdateNavState();

        // Open target="_blank" links in this same tab.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Navigate(e.Uri);
        };
    }

    private void UpdateNavState()
    {
        if (!_initialized) return;
        BackButton.IsEnabled = WebView.CoreWebView2.CanGoBack;
        ForwardButton.IsEnabled = WebView.CoreWebView2.CanGoForward;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_initialized && WebView.CoreWebView2.CanGoBack) WebView.CoreWebView2.GoBack();
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        if (_initialized && WebView.CoreWebView2.CanGoForward) WebView.CoreWebView2.GoForward();
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (_initialized) WebView.CoreWebView2.Reload();
    }

    private void OnHome(object sender, RoutedEventArgs e) => GoHome();

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Navigate(AddressBox.Text);
        AddressBox.SelectAll();
        e.Handled = true;
    }

    /// <summary>Normalize a user-typed address: bare words become https:// URLs.</summary>
    private static string NormalizeUrl(string url)
    {
        var value = url.Trim();
        if (value.Length == 0) return "about:blank";
        if (value.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return "about:blank";
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;

        // A bare host with a dot or localhost → https; anything else → web search.
        if (value.Contains('.') && !value.Contains(' '))
            return "https://" + value;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(value);
    }

    private static System.Drawing.Color ResolveEditorBackground()
    {
        if (Application.Current.TryFindResource("EditorBackground") is SolidColorBrush brush)
        {
            var c = brush.Color;
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        return System.Drawing.Color.White;
    }

    // ── Host-control surface ───────────────────────────────────────────────────
    // The Ruby daemon drives these through the host-bridge (host_call/host_result)
    // from the bridge reader thread, so every entry marshals onto the UI thread
    // before touching the WebView2 control.

    private static async Task<T> UiAsync<T>(Func<Task<T>> action)
    {
        var op = Application.Current.Dispatcher.InvokeAsync(action);
        return await op.Task.Unwrap();
    }

    private static async Task UiAsync(Func<Task> action)
    {
        var op = Application.Current.Dispatcher.InvokeAsync(action);
        await op.Task.Unwrap();
    }

    /// <summary>Thread-safe navigate that awaits WebView2 initialization.</summary>
    public Task NavigateHostAsync(string? url) =>
        UiAsync(async () =>
        {
            var target = string.IsNullOrWhiteSpace(url) ? _homeUrl : NormalizeUrl(url);
            if (!_initialized)
                await InitAndNavigateAsync(target);
            else
                WebView.CoreWebView2.Navigate(target);
            return true;
        });

    public Task ReloadHostAsync() =>
        UiAsync(async () =>
        {
            if (_initialized) WebView.CoreWebView2.Reload();
            return true;
        });

    public Task<string> GetUrlAsync() => UiAsync(() => Task.FromResult(CurrentUrl));

    /// <summary>Execute arbitrary JavaScript; returns WebView2's JSON-encoded result.</summary>
    public Task<string> EvaluateJavaScriptAsync(string script) =>
        UiAsync(async () =>
        {
            if (!_initialized) throw new InvalidOperationException("browser not initialized");
            return await WebView.CoreWebView2.ExecuteScriptAsync(script);
        });

    public async Task<string> GetHtmlAsync()
    {
        var json = await EvaluateJavaScriptAsync("document.documentElement.outerHTML");
        return JsonSerializer.Deserialize<string>(json) ?? json;
    }

    public async Task<string> GetTextAsync()
    {
        var json = await EvaluateJavaScriptAsync("document.body ? document.body.innerText : ''");
        return JsonSerializer.Deserialize<string>(json) ?? json;
    }

    /// <summary>Render a raw HTML string without a temp file.</summary>
    public Task SetHtmlAsync(string html) =>
        UiAsync(async () =>
        {
            if (!_initialized)
                await InitAndNavigateAsync("about:blank");
            WebView.CoreWebView2.NavigateToString(html);
            return true;
        });

    /// <summary>Capture the rendered DOM as PNG (exact pixels, no window chrome) and
    /// write it to the same directory <c>captura_visus</c> uses.</summary>
    public async Task<string> CapturePngAsync()
    {
        var bytes = await UiAsync(async () =>
        {
            if (!_initialized) throw new InvalidOperationException("browser not initialized");
            using var ms = new MemoryStream();
            await WebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return ms.ToArray();
        });
        var dir = Path.Combine(Path.GetTempPath(), "aethercodex_capturae");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"captura_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}