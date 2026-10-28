// SPDX-License-Identifier: MIT
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SplitPlay.Tray;

/// <summary>A window hosting WebView2 on the service's web UI. Closing it hides it; Quit closes it for real.</summary>
internal sealed class DashboardForm : Form
{
    private readonly Uri _url;
    private readonly WebView2 _webView;
    private bool _closing;

    public DashboardForm(Uri url)
    {
        _url = url;
        Text = "SplitPlay";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1200, 800);
        MinimumSize = new Size(480, 360);

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);
        Load += async (_, _) => await InitializeAsync();
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
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                // External links open in the default browser, not in the dashboard window.
                args.Handled = true;
                OpenInBrowser(new Uri(args.Uri));
            };
            _webView.Source = _url;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is not installed. The dashboard opens in your browser instead.",
                "SplitPlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OpenInBrowser(_url);
            Hide();
        }
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
            _webView.Dispose();
        }

        base.Dispose(disposing);
    }
}
