// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Tray;

/// <summary>
/// A window hosting WebView2 on the service's web UI. Closing it hides it; Quit closes it for real.
/// <para>
/// The UI is loaded only once <c>/api/v1/health</c> answers. Until then, and whenever loading it fails or the service
/// stops answering, the window shows <see cref="FallbackPage"/> (service status and Start) and retries on its own while
/// the window is visible. Only the first load after the tray starts also starts a stopped service (elevated once if the
/// user may not start it): a service the user stopped later stays stopped until they press Start. When the API answers
/// but the UI itself does not load (an HTTP error), the page shows the error and waits for Retry.
/// The WebView stays hidden while the UI loads, so its own error page is never seen.
/// </para>
/// <para>
/// Sign-in: before each load of the UI the tray asks the service's admin pipe for a session and puts it into the
/// WebView as the HttpOnly session cookie, so the owner never sees a login. The UI posts <c>auth</c> when the API
/// answers 401 (session expired, service restarted); the tray then gets a new session and reloads. A user the
/// service refuses (not an administrator at the console) gets the UI's password login.
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

    /// <summary>The automatic start is used by the first dashboard load of this tray process only.</summary>
    private static bool s_autoStartUsed;

    private bool _closing;
    private bool _fallback;
    private bool _starting;
    private bool _probing;
    private bool _autoElevated;
    private int _misses;
    private string? _error;

    /// <summary>The API answers but the UI did not load: the HTTP status or WebView error; no automatic retries.</summary>
    private string? _uiError;
    private string _status = "unknown";
    private DateTime? _runningSince;

    /// <summary>The service's uptime at the last health check: a smaller one means it restarted (sessions are gone).</summary>
    private long? _uptime;
    private bool _restarted;
    private readonly TraySignInGate _signInGate = new(TimeProvider.System);
    private int _signInRetries;

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

            var autoStart = !s_autoStartUsed;
            s_autoStartUsed = true;
            if (await ApiAnswersAsync())
            {
                NavigateToUi();
            }
            else
            {
                await ShowFallbackAsync(startService: autoStart);
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

    private void NavigateToUi() => _ = SafeAsync(NavigateToUiAsync);

    private async Task NavigateToUiAsync()
    {
        _fallback = false;
        _uiError = null;
        _restarted = false;
        _retry.Stop();
        _misses = 0;
        _monitor.Start();
        _signInGate.Reset();
        var signedIn = await SignInAsync();
        if (signedIn)
        {
            _signInGate.Reloaded();
        }

        _webView.CoreWebView2.Navigate(_url.ToString());
        if (!signedIn)
        {
            // The service may still be starting its pipe: keep trying for a minute so that the console owner does not
            // stay on the password page (the page also asks again through the "auth" message).
            var run = ++_signInRetries;
            _ = SafeAsync(() => RetrySignInAsync(run));
        }
    }

    private async Task RetrySignInAsync(int run)
    {
        for (var attempt = 0; attempt < 12 && run == _signInRetries && !_fallback && !IsDisposed; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (run != _signInRetries || _fallback || IsDisposed)
            {
                return;
            }

            if (await SignInAsync())
            {
                _signInGate.Reloaded();
                _webView.CoreWebView2.Reload();
                return;
            }
        }
    }

    /// <summary>
    /// Gets a session from the admin pipe and stores it as the WebView's session cookie. Without one (pipe refused,
    /// older service) the UI shows its password login.
    /// </summary>
    private async Task<bool> SignInAsync()
    {
        AdminToken? session;
        try
        {
            session = await Task.Run(() => AdminPipe.RequestAsync(AdminPipe.Kinds.Session, "tray", TimeSpan.FromSeconds(3)));
        }
        catch (AdminPipeException ex)
        {
            Debug.WriteLine($"SplitPlay: no session from the admin pipe: {ex.Message}");
            return false;
        }

        if (session is null || _webView.CoreWebView2 is not { } core)
        {
            return false;
        }

        var cookie = core.CookieManager.CreateCookie(AdminPipe.SessionCookie, session.Token, _url.Host, "/");
        cookie.IsHttpOnly = true;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        cookie.IsSecure = _url.Scheme == Uri.UriSchemeHttps;
        cookie.Expires = session.ExpiresAt.LocalDateTime;
        core.CookieManager.AddOrUpdateCookie(cookie);
        return true;
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

        if (_fallback)
        {
            return;
        }

        // Never starts the service: a reload after the user stopped it must not bring it back.
        var status = e.HttpStatusCode;
        var reason = status > 0 ? $"HTTP {status}" : e.WebErrorStatus.ToString();
        await SafeAsync(async () =>
        {
            if (await ApiAnswersAsync())
            {
                // The service is up but serves no UI (missing wwwroot, a 5xx): retrying on a timer would loop forever.
                _uiError = FallbackPage.Text("errorUi", reason);
            }

            await ShowFallbackAsync(startService: false);
        });
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!_fallback)
        {
            // The UI is not signed in: sign in again and reload, at most every 10 s. When the API keeps rejecting what the
            // pipe grants, the gate stops after a few reloads and the page stays on its password login.
            if (IsUi(e.Source) && e.TryGetWebMessageAsString() == "auth" && _signInGate.ShouldSignIn())
            {
                await SafeAsync(async () =>
                {
                    if (await SignInAsync())
                    {
                        _signInGate.Reloaded();
                        _webView.CoreWebView2.Reload();
                    }
                });
            }

            return;
        }

        switch (e.TryGetWebMessageAsString())
        {
            case "start":
                await SafeAsync(() => StartServiceAsync(automatic: false));
                break;
            case "retry":
                NavigateToUi();
                break;
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _fallback)
        {
            // Polling pauses while the window is hidden; check right away when it comes back.
            _ = SafeAsync(RetryAsync);
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
            _webView.CoreWebView2.NavigateToString(FallbackPage.Build(PageStatus(), _starting, Error(), Button()));
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
                    var (ok, error) = await ServiceStarter.StartElevatedAsync(Handle);
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
        // Nothing to poll for while the window is hidden (OnVisibleChanged checks once when it is shown).
        if (!_fallback || _probing || !Visible)
        {
            return;
        }

        _probing = true;
        try
        {
            if (_uiError is null && await ApiAnswersAsync())
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

        // The service restarted between two checks: its sessions are gone, so sign in again and reload the UI.
        if (_restarted && !_fallback)
        {
            NavigateToUi();
            return;
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
        if (_uiError is not null)
        {
            return _status;
        }

        if (_status != "running")
        {
            _runningSince = null;
            return _starting && _status is "stopped" or "unknown" ? "starting" : _status;
        }

        _runningSince ??= DateTime.UtcNow;
        return DateTime.UtcNow - _runningSince < StartupGrace ? "starting" : "notResponding";
    }

    private string? Error() => _uiError ?? _error;

    /// <summary>The page's one button: Retry after a UI error, Start for a stopped service, else none.</summary>
    private string? Button() =>
        _uiError is not null ? "retry"
        : _status is "stopped" or "unknown" ? "start"
        : null;

    private void Push()
    {
        if (_fallback && _webView.CoreWebView2 is { } core)
        {
            core.PostWebMessageAsJson(FallbackPage.StateJson(PageStatus(), _starting, Error(), Button()));
        }
    }

    private async Task<bool> ApiAnswersAsync()
    {
        try
        {
            using var response = await _http.GetAsync(_health);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            try
            {
                using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (health.RootElement.TryGetProperty("uptimeSeconds", out var u) && u.TryGetInt64(out var uptime))
                {
                    _restarted |= _uptime is long before && uptime + 5 < before;
                    _uptime = uptime;
                }
            }
            catch (JsonException)
            {
            }

            return true;
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
