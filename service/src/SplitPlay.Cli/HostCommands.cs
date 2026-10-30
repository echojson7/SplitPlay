// SPDX-License-Identifier: MIT
using System.Runtime.Versioning;
using System.Text.Json;
using SplitPlay.Core;
using SplitPlay.Core.Host;

namespace SplitPlay.Cli;

/// <summary>
/// <c>splitplay host prepare|revert|status</c>: RDP host preparation (logic in <see cref="HostTuner"/>). The installer
/// runs <c>prepare</c> after installing and <c>revert</c> before uninstalling.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HostCommands
{
    /// <summary>ERROR_SUCCESS_REBOOT_REQUIRED: done, but a reboot is needed (the installer asks for one).</summary>
    public const int RebootRequired = 3010;

    public static int Run(string verb, Args args, string? dataRoot)
    {
        var root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), BuildInfo.ServiceName);
        var json = args.Flag("json");
        var options = new HostTuningOptions
        {
            TermWrapDirectory = Path.GetFullPath(args.Option("termwrap-dir") ?? DefaultTermWrapDirectory()),
            SkipTermWrap = args.Flag("skip-termwrap"),
            ComposeIntervalMs = args.IntOption("compose-interval-ms") ?? ComposeIntervalFromConfig(root),
            WorkerExecutable = Path.GetFullPath(args.Option("worker-exe") ?? DefaultWorkerExecutable()),
        };
        args.EnsureNoUnknownOptions();

        var tuner = new HostTuner(new WindowsHostRegistry(), new WindowsHostServices(), HostTuner.StateFileIn(root), power: new WindowsHostPower());
        try
        {
            switch (verb)
            {
                case "prepare":
                    return Report("prepare", tuner.Prepare(options), json);
                case "revert":
                    return Report("revert", tuner.Revert(), json);
                case "status":
                    return Status(tuner.Status(options), json);
                default:
                    throw new UsageException($"unknown host command '{verb}'");
            }
        }
        catch (HostTuningException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary><c>{app}\termwrap</c> for the installed CLI (<c>{app}\service\splitplay.exe</c>).</summary>
    private static string DefaultTermWrapDirectory() => Path.Combine(AppContext.BaseDirectory, "..", "termwrap");

    /// <summary><c>{app}\worker\splitplay-worker.exe</c> for the installed CLI.</summary>
    private static string DefaultWorkerExecutable() => Path.Combine(AppContext.BaseDirectory, "..", "worker", "splitplay-worker.exe");

    private static int ComposeIntervalFromConfig(string root)
    {
        try
        {
            var file = Path.Combine(root, Core.Configuration.ConfigStore.FileName);
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("host", out var host) && host.TryGetProperty("composeIntervalMs", out var v) && v.TryGetInt32(out var ms))
                {
                    return ms;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new Core.Configuration.HostConfig().ComposeIntervalMs;
    }

    private static int Report(string what, HostTuningResult result, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, HostTuningJsonContext.Default.HostTuningResult));
        }
        else
        {
            foreach (var change in result.Changes)
            {
                Console.WriteLine($"  {change}");
            }

            foreach (var note in result.Notes)
            {
                Console.WriteLine($"  note: {note}");
            }

            Console.WriteLine(result.Changes.Count == 0 ? $"host {what}: nothing to change" : $"host {what}: {result.Changes.Count} change(s)");
            Console.WriteLine($"reboot required: {(result.RebootRequired ? "yes" : "no")}");
        }

        return result.RebootRequired ? RebootRequired : 0;
    }

    private static int Status(HostStatus status, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(status, HostTuningJsonContext.Default.HostStatus));
        }
        else
        {
            Console.WriteLine($"edition: {status.Edition}");
            foreach (var item in status.Items)
            {
                var current = item.Current?.ToString() ?? "(missing)";
                var desired = item.Desired is null ? "" : $" (want {item.Desired})";
                var shown = item.State is HostItemStatus.Ok or HostItemStatus.NotApplicable ? "" : desired;
                var tuning = item.Optional && item.State == HostItemStatus.Differs ? " (performance only, desks still start)" : "";
                var name = item.Id == "PowerThrottling" ? $"{item.Id} {item.Name}" : item.Id;
                Console.WriteLine($"  {item.State,-8} {name,-24} {current}{shown}{tuning}");
            }

            foreach (var note in status.Notes)
            {
                Console.WriteLine($"  note: {note}");
            }

            Console.WriteLine($"termwrap: {(status.TermWrapSkipped ? "skipped" : "managed")}");
            Console.WriteLine($"prepared: {(status.Prepared ? "yes" : "no")}");
            Console.WriteLine($"reboot required: {(status.RebootRequired ? "yes" : "no")}");
        }

        return status.RebootRequired ? RebootRequired : status.Prepared ? 0 : 1;
    }
}
