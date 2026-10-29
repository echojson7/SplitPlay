// SPDX-License-Identifier: MIT
using SplitPlay.Core.Configuration;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service;

/// <summary>
/// Entry point.
/// <code>
/// SplitPlay.Service.exe [--console] [--data-root DIR] [--web-port N] [--web-root DIR] [--Section:Key=value …]
/// SplitPlay.Service.exe fake-child --pipe NAME --desk ID [--heartbeat-ms N] [--behavior normal|…] [--role R]
/// SplitPlay.Service.exe cleanup [--data-root DIR] [--delete-accounts]   (the uninstaller, after the service is gone)
/// </code>
/// Without <c>--console</c> and when started by the SCM it runs as the <c>SplitPlay</c> Windows service.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "fake-child")
        {
            return await FakeChildCommand.RunAsync(args[1..]).ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "cleanup")
        {
            return UninstallCleanup.Command(args[1..]);
        }

        ServiceHostOptions options;
        try
        {
            options = ServiceHostOptions.Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return 64;
        }

        try
        {
            var app = ServiceHost.Build(options);
            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (ConfigException ex)
        {
            // Refuse to start on a bad or too-new config (config.md); leave a note for the operator.
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            TryWriteStartupError(options.DataRoot, ex.Message);
            return 78;
        }
    }

    private static void TryWriteStartupError(string dataRoot, string message)
    {
        try
        {
            var logs = Path.Combine(dataRoot, "logs");
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "startup-error.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
