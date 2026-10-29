// SPDX-License-Identifier: MIT
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace SplitPlay.Tray;

/// <summary>
/// A window hosting WebView2 on the service's web UI. Closing it hides it; Quit closes it for real.
/// <para>
/// The UI is loaded only once <c>/api/v1/health</c> answers. Until then, and whenever loading it fails or the service
/// stops answering, the window shows <see cref="FallbackPage"/> (service status and Start) and retries on its own.
/// Loading at start-up or a failed load also starts a stopped service (elevated once if the user may not start it).
/// The WebView stays hidden while the UI loads, so its own error page is never seen.
/// </para>
/// </summary>
internal sealed class DashboardForm : Form
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>After this long running without answering, the service counts as "not responding".</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);

    private readonly Uri _url;
    private readonly Uri _health;
    private readonly WebView2 _webView;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = ProbeTimeout };

    /// <summary>Fallback page: checks the API and the service.</summary>
    private readonly System.Windows.Forms.Timer _retry = new() { Interval = 1500 };

    /// <summary>UI shown: notices when the service stops answering.</summary>
    private readonly System.Windows.Forms.Timer _monitor = new() { Interval = 3000 };

    private bool _closing;
    private bool _fallback;
    private bool _starting;
    private bool _probing;
    private bool _autoElevated;
    private int _misses;
    private string? _error;
    private string _status = "unknown";
    private DateTime? _runningSince;

    public DashboardForm(Uri url)
    {
        _url = url;
        _health = new Uri(url, "api/v1/health");
        Text = "SplitPlay";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1200, 800);
        MinimumSize = new Size(480, 360);
        BackColor = AppsUseDarkTheme() ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xf3, 0xf3, 0xf3);

        _webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_webView);
        Load += async (_, _) => await InitializeAsync();
        _retry.Tick += async (_, _) => await SafeAsync(RetryAsync);
        _monitor.Tick += async (_, _) => await SafeAsync(MonitorAsync);
    }

    public void CloseForReal()
    {
        _closing = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SplitPlay", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.NewWindowRequested += (_, args) =>
            {
                // External links open in the default browser, not in the dashboard window.
                args.Handled = true;
                OpenInBrowser(new Uri(args.Uri));
            };
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;

            if (await ApiAnswersAsync())
            {
                NavigateToUi();
            }
            else
            {
                await ShowFallbackAsync(startService: true);
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is not installed. The dashboard opens in your browser instead.",
                "SplitPlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OpenInBrowser(_url);
            Hide();
        }
    }

    private bool IsUi(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
        && string.Equals(u.Authority, _url.Authority, StringComparison.OrdinalIgnoreCase);

    private void NavigateToUi()
    {
        _fallback = false;
        _retry.Stop();
        _misses = 0;
        _monitor.Start();
        _webView.CoreWebView2.Navigate(_url.ToString());
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Hidden until the UI has loaded: a failed load must not show WebView2's error page (reloads included).
        if (IsUi(e.Uri))
        {
            _webView.Visible = false;
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _webView.Visible = true;
            _webView.Focus();
            Push();
            return;
        }

        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            // Replaced by another navigation, which completes on its own.
            return;
        }

        await SafeAsync(() => ShowFallbackAsync(startService: true));
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_fallback && e.TryGetWebMessageAsString() == "start")
        {
            await SafeAsync(() => StartServiceAsync(automatic: false));
        }
    }

    /// <summary>Runs an event handler's work; the window closing under it is not an error.</summary>
    private static async Task SafeAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
    }

    /// <summary>Shows the fallback page and polls until the UI answers.</summary>
    private async Task ShowFallbackAsync(bool startService)
    {
        _monitor.Stop();
        if (!_fallback)
        {
            _fallback = true;
            _status = await Task.Run(ServiceStarter.Query);
            _webView.CoreWebView2.NavigateToString(FallbackPage.Build(PageStatus(), _starting, _error));
            _retry.Start();
        }

        if (startService && _status is "stopped" or "unknown")
        {
            await StartServiceAsync(automatic: true);
        }
    }

    /// <summary>
    /// Starts the service. Without permission it asks for elevation: always from the button, at most once a session
    /// on its own.
    /// </summary>
    private async Task StartServiceAsync(bool automatic)
    {
        if (_starting)
        {
            return;
        }

        _starting = true;
        _error = null;
        Push();
        try
        {
            var (outcome, message) = await Task.Run(ServiceStarter.TryStart);
            switch (outcome)
            {
                case StartOutcome.AccessDenied when !automatic || !_autoElevated:
                    _autoElevated = true;
                    var (ok, error) = await ServiceStarter.StartElevatedAsync();
                    if (!ok)
                    {
                        _error = error is null ? FallbackPage.Text("errorElevation") : FallbackPage.Text("errorStart", error);
                    }

                    break;
                case StartOutcome.AccessDenied:
                    _error = FallbackPage.Text("errorElevation");
                    break;
                case StartOutcome.Disabled:
                    _error = FallbackPage.Text("errorDisabled");
                    break;
                case StartOutcome.Failed:
                    _error = FallbackPage.Text("errorStart", message ?? "");
                    break;
            }
        }
        finally
        {
            _starting = false;
        }

        _status = await Task.Run(ServiceStarter.Query);
        Push();
    }

    private async Task RetryAsync()
    {
        if (!_fallback || _probing)
        {
            return;
        }

        _probing = true;
        try
        {
            if (await ApiAnswersAsync())
            {
                NavigateToUi();
                return;
            }

            _status = await Task.Run(ServiceStarter.Query);
            Push();
        }
        finally
        {
            _probing = false;
        }
    }

    private async Task MonitorAsync()
    {
        if (_fallback || _probing || !Visible)
        {
            return;
        }

        _probing = true;
        try
        {
            _misses = await ApiAnswersAsync() ? 0 : _misses + 1;
        }
        finally
        {
            _probing = false;
        }

        // Two misses in a row: the service stopped (for example from the Services panel). No automatic start here.
        if (_misses >= 2 && !_fallback)
        {
            await ShowFallbackAsync(startService: false);
        }
    }

    /// <summary>The page status: a running service that does not answer is still starting for a while.</summary>
    private string PageStatus()
    {
        if (_status != "running")
        {
            _runningSince = null;
            return _starting && _status is "stopped" or "unknown" ? "starting" : _status;
        }

        _runningSince ??= DateTime.UtcNow;
        return DateTime.UtcNow - _runningSince < StartupGrace ? "starting" : "notResponding";
    }

    private void Push()
    {
        if (_fallback && _webView.CoreWebView2 is { } core)
        {
            core.PostWebMessageAsJson(FallbackPage.StateJson(PageStatus(), _starting, _error));
        }
    }

    private async Task<bool> ApiAnswersAsync()
    {
        try
        {
            using var response = await _http.GetAsync(_health);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static bool AppsUseDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    private static void OpenInBrowser(Uri uri)
    {
        if (uri.Scheme is "http" or "https")
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true })?.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _retry.Dispose();
            _monitor.Dispose();
            _http.Dispose();
            _webView.Dispose();
        }

        base.Dispose(disposing);
    }
}
