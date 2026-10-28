// SPDX-License-Identifier: MIT
using Microsoft.Web.WebView2.Core;
using SplitPlay.Core;

namespace SplitPlay.Tray;

/// <summary>
/// Tray app: an icon with "Open dashboard" and "Quit", and a window hosting WebView2 on the service's web UI.
/// <c>SplitPlay.Tray.exe [--url URL] [--smoke-test RESULT_FILE]</c>.
/// </summary>
internal static class Program
{
    public const string DefaultUrl = "http://127.0.0.1:38299/";

    [STAThread]
    private static int Main(string[] args)
    {
        var url = DefaultUrl;
        string? smokeFile = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--url" && i + 1 < args.Length)
            {
                url = args[++i];
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

        using var mutex = new Mutex(initiallyOwned: true, @"Local\SplitPlay.Tray", out var first);
        if (!first)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(new Uri(url)));
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
