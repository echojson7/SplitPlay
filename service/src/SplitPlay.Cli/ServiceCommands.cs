// SPDX-License-Identifier: MIT
using System.Runtime.Versioning;
using System.ServiceProcess;
using SplitPlay.Core;

namespace SplitPlay.Cli;

/// <summary><c>splitplay service install|uninstall|start|stop|status</c>.</summary>
[SupportedOSPlatform("windows")]
internal static class ServiceCommands
{
    public const string ServiceExe = "SplitPlay.Service.exe";

    public static async Task<int> RunAsync(string verb, Args args, Func<ApiClient> api)
    {
        switch (verb)
        {
            case "install":
                return Install(args);
            case "uninstall":
                args.EnsureNoUnknownOptions();
                return Uninstall();
            case "start":
            {
                var timeout = TimeSpan.FromSeconds(args.IntOption("timeout") ?? 60);
                args.EnsureNoUnknownOptions();
                return await StartAsync(timeout, api).ConfigureAwait(false);
            }

            case "stop":
            {
                var timeout = TimeSpan.FromSeconds(args.IntOption("timeout") ?? 60);
                args.EnsureNoUnknownOptions();
                return Stop(timeout);
            }

            case "status":
                args.EnsureNoUnknownOptions();
                return await StatusAsync(api).ConfigureAwait(false);
            default:
                throw new UsageException($"unknown service command '{verb}'");
        }
    }

    private static int Install(Args args)
    {
        var bin = args.Option("bin") ?? FindServiceExe();
        var dataRoot = args.Option("data-root");
        var start = args.Flag("start");
        args.EnsureNoUnknownOptions();

        bin = Path.GetFullPath(bin);
        if (!File.Exists(bin))
        {
            Console.Error.WriteLine($"error: {bin} not found (use --bin)");
            return 1;
        }

        var commandLine = Quote(bin);
        if (dataRoot is not null)
        {
            commandLine += " --data-root " + Quote(Path.GetFullPath(dataRoot));
        }

        var created = ServiceControlManager.InstallOrUpdate(new ServiceInstallSpec(
            BuildInfo.ServiceName,
            BuildInfo.ServiceDisplayName,
            "Runs SplitPlay desks: isolated Windows sessions streamed to Moonlight clients.",
            commandLine,
            DelayedAutoStart: false, // the tray opens at sign-in and must find the service up
            RestartDelays: [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)],
            FailureResetPeriod: TimeSpan.FromDays(1)));

        Console.WriteLine(created
            ? $"Installed service '{BuildInfo.ServiceName}' ({BuildInfo.ServiceDisplayName}): {commandLine}"
            : $"Updated service '{BuildInfo.ServiceName}' ({BuildInfo.ServiceDisplayName}): {commandLine}");

        if (start)
        {
            using var controller = new ServiceController(BuildInfo.ServiceName);
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            Console.WriteLine("Service started.");
        }

        return 0;
    }

    private static int Uninstall()
    {
        if (!ServiceControlManager.Exists(BuildInfo.ServiceName))
        {
            Console.WriteLine($"Service '{BuildInfo.ServiceName}' is not installed.");
            return 0;
        }

        using (var controller = new ServiceController(BuildInfo.ServiceName))
        {
            if (controller.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            {
                controller.Stop();
            }

            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
        }

        ServiceControlManager.Remove(BuildInfo.ServiceName);

        // Deletion completes once the last handle to the service is closed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (ServiceControlManager.Exists(BuildInfo.ServiceName) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
        }

        Console.WriteLine($"Uninstalled service '{BuildInfo.ServiceName}'.");
        return 0;
    }

    private static async Task<int> StartAsync(TimeSpan timeout, Func<ApiClient> api)
    {
        using var controller = new ServiceController(BuildInfo.ServiceName);
        if (controller.Status != ServiceControllerStatus.Running)
        {
            if (controller.Status != ServiceControllerStatus.StartPending)
            {
                controller.Start();
            }

            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
        }

        // The SCM reports Running before Kestrel listens; wait for the health endpoint too.
        using var client = api();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                var health = await client.HealthAsync().ConfigureAwait(false);
                Console.WriteLine($"Service running: {health.Status}, version {health.Version}.");
                return 0;
            }
            catch (ServiceUnreachableException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    private static int Stop(TimeSpan timeout)
    {
        using var controller = new ServiceController(BuildInfo.ServiceName);
        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            Console.WriteLine("Service is already stopped.");
            return 0;
        }

        if (controller.Status != ServiceControllerStatus.StopPending)
        {
            controller.Stop();
        }

        controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        Console.WriteLine("Service stopped.");
        return 0;
    }

    private static async Task<int> StatusAsync(Func<ApiClient> api)
    {
        if (!ServiceControlManager.Exists(BuildInfo.ServiceName))
        {
            Console.WriteLine($"{BuildInfo.ServiceName}: not installed");
            return 1;
        }

        using var controller = new ServiceController(BuildInfo.ServiceName);
        var status = controller.Status;
        var line = $"{BuildInfo.ServiceName}: {status}";
        if (status == ServiceControllerStatus.Running)
        {
            try
            {
                using var client = api();
                var health = await client.HealthAsync().ConfigureAwait(false);
                line += $" (health {health.Status}, version {health.Version}, up {health.UptimeSeconds} s)";
            }
            catch (ServiceUnreachableException)
            {
                line += " (web API not reachable)";
            }
        }

        Console.WriteLine(line);
        return 0;
    }

    /// <summary>SplitPlay.Service.exe next to the CLI, or in a sibling <c>service\</c> folder (CI artifact layout).</summary>
    private static string FindServiceExe()
    {
        var dir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(dir, ServiceExe),
            Path.Combine(dir, "..", "service", ServiceExe),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string Quote(string path) => path.Contains(' ', StringComparison.Ordinal) || path.Contains('"', StringComparison.Ordinal)
        ? "\"" + path.Replace("\"", "", StringComparison.Ordinal) + "\""
        : "\"" + path + "\"";
}
