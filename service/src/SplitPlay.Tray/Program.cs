// SPDX-License-Identifier: MIT
using Microsoft.Web.WebView2.Core;
using SplitPlay.Core;

namespace SplitPlay.Tray;

/// <summary>
/// Tray app: an icon with "Open dashboard" and "Quit", and a window hosting WebView2 on the service's web UI.
/// <c>SplitPlay.Tray.exe [--url URL] [--autostart|--minimized] [--smoke-test RESULT_FILE]</c>.
/// Started by hand (Start menu, installer) it opens the dashboard; with <c>--autostart</c> (the HKLM Run entry) it
/// stays an icon, and exits at once in a remote (RDP) session such as a desk. One instance per user session: a second
/// start brings the running instance's window to the front.
/// </summary>
internal static class Program
{
    public const string DefaultUrl = "http://127.0.0.1:38299/";

    /// <summary>Per session (<c>Local\</c>): every signed-in user has their own tray.</summary>
    private const string MutexName = @"Local\SplitPlay.Tray";

    private const string ShowEventName = @"Local\SplitPlay.Tray.Show";

    [STAThread]
    private static int Main(string[] args)
    {
        var url = DefaultUrl;
        string? smokeFile = null;
        var minimized = false;
        var autostart = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--url" && i + 1 < args.Length)
            {
                url = args[++i];
            }
            else if (args[i] is "--autostart" or "--minimized")
            {
                minimized = true;
                autostart |= args[i] == "--autostart";
            }
            else if (args[i] == "--smoke-test" && i + 1 < args.Length)
            {
                smokeFile = args[++i];
            }
        }

        if (smokeFile is not null)
        {
            return SmokeTest(smokeFile);
        }

        // The HKLM Run entry also fires for desk users, whose sessions are RDP sessions: no tray there (it would only
        // hold files open in a session the installer's Restart Manager cannot reach).
        if (autostart && SystemInformation.TerminalServerSession)
        {
            return 0;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            // Already running in this session: ask it to show its window (unless this is the autostart entry).
            if (!minimized && EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
            {
                using (show)
                {
                    show.Set();
                }
            }

            return 0;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ApplicationConfiguration.Initialize();
        using var context = new TrayContext(new Uri(url));
        var registration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => context.ShowDashboardFromAnyThread(), null,
            Timeout.Infinite, executeOnlyOnce: false);
        try
        {
            if (!minimized)
            {
                context.ShowDashboardFromAnyThread();
            }

            Application.Run(context);
        }
        finally
        {
            registration.Unregister(null);
        }

        return 0;
    }

    /// <summary>
    /// Headless check used by CI: WinForms initializes, the tray objects can be built and the WebView2 loader
    /// resolves. Writes a short report to <paramref name="resultFile"/> (a WinExe has no console).
    /// </summary>
    private static int SmokeTest(string resultFile)
    {
        var lines = new List<string> { $"SplitPlay.Tray {BuildInfo.Version}" };
        var code = 0;
        try
        {
            ApplicationConfiguration.Initialize();
            using (var context = new TrayContext(new Uri(DefaultUrl), visible: false))
            {
                lines.Add($"tray menu items: {context.MenuItemCount}");
            }

            try
            {
                lines.Add($"webview2 runtime: {CoreWebView2Environment.GetAvailableBrowserVersionString()}");
            }
            catch (WebView2RuntimeNotFoundException)
            {
                lines.Add("webview2 runtime: not installed (the installer bootstraps it)");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"error: {ex}");
            code = 1;
        }

        File.WriteAllLines(resultFile, lines);
        return code;
    }
}
