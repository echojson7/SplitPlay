// SPDX-License-Identifier: MIT
using SplitPlay.Core;
using SplitPlay.Core.Api;
using SplitPlay.Service.Api;

namespace SplitPlay.Service.Hosting;

/// <summary>
/// The Windows services SplitPlay depends on (<c>GET /host/services</c>) and the only ones the Web API may start or
/// stop. Stopping SplitPlay itself answers first and stops through the SCM a moment later.
/// </summary>
public sealed class RelatedServices(IServiceControl control, ILogger<RelatedServices> logger, TimeProvider time)
{
    /// <summary>Delay between the 202 answer and stopping the SplitPlay service itself.</summary>
    public static readonly TimeSpan SelfStopDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>One allowed service.</summary>
    /// <param name="Name">Service name.</param>
    /// <param name="DefaultDisplayName">Shown when Windows has no display name (the service is missing).</param>
    /// <param name="HideWhenMissing">Left out of the list when not installed (optional software such as Steam).</param>
    public sealed record Entry(string Name, string DefaultDisplayName, bool HideWhenMissing = false);

    /// <summary>The allow-list, in display order.</summary>
    public static IReadOnlyList<Entry> Catalog { get; } =
    [
        new(BuildInfo.ServiceName, BuildInfo.ServiceDisplayName),
        new("TermService", "Remote Desktop Services"),
        new("UmRdpService", "Remote Desktop Services UserMode Port Redirector"),
        new("ViGEmBus", "Virtual Gamepad Emulation Bus"),
        new("Audiosrv", "Windows Audio"),
        new("Steam Client Service", "Steam Client Service", HideWhenMissing: true),
    ];

    /// <summary>Services desk sessions need; started when the SplitPlay service starts (<see cref="RequiredServicesStarter"/>).</summary>
    public static IReadOnlyList<string> DeskDependencies { get; } = ["TermService", "UmRdpService"];

    /// <summary>The allow-list entry for <paramref name="name"/> (case-insensitive), or null.</summary>
    public static Entry? Find(string name) =>
        Catalog.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<HostServiceDto> List()
    {
        var list = new List<HostServiceDto>();
        foreach (var entry in Catalog)
        {
            var info = control.Query(entry.Name);
            if (!info.Exists && entry.HideWhenMissing)
            {
                continue;
            }

            list.Add(ToDto(entry, info));
        }

        return list;
    }

    /// <summary>Starts an allowed service; 404 for a name outside the allow-list, 409 when it cannot start now.</summary>
    public HostServiceDto Start(string name)
    {
        var entry = Find(name) ?? throw NotAllowed(name);
        var info = control.Query(entry.Name);
        if (!CanStart(info))
        {
            throw ApiException.Conflict(ApiErrorCodes.InvalidState, $"{entry.Name} cannot be started ({Describe(info)})");
        }

        try
        {
            control.Start(entry.Name);
        }
        catch (ServiceControlException ex)
        {
            throw new ApiException(StatusCodes.Status500InternalServerError, ApiErrorCodes.Internal, ex.Message);
        }

        logger.LogInformation("Service {Service} started from the web UI", entry.Name);
        var after = control.Query(entry.Name);
        return ToDto(entry, after.Status == HostServiceStatus.Stopped ? after with { Status = HostServiceStatus.Starting } : after);
    }

    /// <summary>
    /// Stops an allowed service in the background (dependents first); the answer shows it as stopping. The SplitPlay
    /// service is stopped after <see cref="SelfStopDelay"/> so that the answer reaches the browser.
    /// </summary>
    public HostServiceDto Stop(string name)
    {
        var entry = Find(name) ?? throw NotAllowed(name);
        var info = control.Query(entry.Name);
        if (!CanStop(info))
        {
            throw ApiException.Conflict(ApiErrorCodes.InvalidState, $"{entry.Name} cannot be stopped ({Describe(info)})");
        }

        var self = string.Equals(entry.Name, BuildInfo.ServiceName, StringComparison.OrdinalIgnoreCase);
        logger.LogWarning("Stopping service {Service} from the web UI", entry.Name);
        _ = Task.Run(async () =>
        {
            try
            {
                if (self)
                {
                    await Task.Delay(SelfStopDelay, time).ConfigureAwait(false);
                }

                control.Stop(entry.Name);
            }
            catch (ServiceControlException ex)
            {
                logger.LogWarning("{Message}", ex.Message);
            }
        });

        return ToDto(entry, info with { Status = HostServiceStatus.Stopping, AcceptsStop = false });
    }

    public static HostServiceDto ToDto(Entry entry, ServiceInfo info)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(info);
        return new HostServiceDto(entry.Name, string.IsNullOrEmpty(info.DisplayName) ? entry.DefaultDisplayName : info.DisplayName,
            info.Status, info.StartType, CanStart(info), CanStop(info));
    }

    /// <summary>Stopped, not disabled and not a driver (drivers start with their device).</summary>
    public static bool CanStart(ServiceInfo info) =>
        info.Status == HostServiceStatus.Stopped && info.StartType != "disabled" && !info.IsDriver;

    public static bool CanStop(ServiceInfo info) => info.Status == HostServiceStatus.Running && info.AcceptsStop && !info.IsDriver;

    private static string Describe(ServiceInfo info) =>
        info.Status == HostServiceStatus.Stopped && info.StartType == "disabled" ? "disabled" : info.IsDriver ? "driver" : info.Status;

    private static ApiException NotAllowed(string name) =>
        ApiException.NotFound($"service '{name}' is not one of the services SplitPlay manages");
}

/// <summary>
/// On service start, starts the services desk sessions need (<see cref="RelatedServices.DeskDependencies"/>) when they
/// are stopped and not disabled. Runs in the background and never fails start-up; what it did is logged.
/// </summary>
public sealed class RequiredServicesStarter(IServiceControl control, ILogger<RequiredServicesStarter> logger, TimeProvider time) : IHostedService
{
    /// <summary>How long to wait for a started service to report running before logging that it did not.</summary>
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(30);

    private Task? _run;

    /// <summary>Completes when the check has finished (tests).</summary>
    public Task Completion => _run ?? Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _run = Task.Run(() => EnsureRunningAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        foreach (var name in RelatedServices.DeskDependencies)
        {
            try
            {
                await EnsureOneAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ServiceControlException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning("Could not start {Service}: {Message}", name, ex.Message);
            }
        }
    }

    private async Task EnsureOneAsync(string name, CancellationToken cancellationToken)
    {
        var info = control.Query(name);
        if (!info.Exists)
        {
            if (name == "TermService")
            {
                logger.LogWarning("TermService is not installed; desks cannot start");
            }
            else
            {
                logger.LogDebug("{Service} is not installed", name);
            }

            return;
        }

        if (info.Status != HostServiceStatus.Stopped)
        {
            logger.LogDebug("{Service} is {Status}", name, info.Status);
            return;
        }

        if (info.StartType == "disabled")
        {
            logger.LogWarning("{Service} is disabled; desks cannot start until it is enabled", name);
            return;
        }

        logger.LogInformation("{Service} is stopped; starting it", name);
        control.Start(name);
        var deadline = time.GetUtcNow() + StartWait;
        while (control.Query(name).Status != HostServiceStatus.Running)
        {
            if (time.GetUtcNow() > deadline)
            {
                logger.LogWarning("{Service} did not reach the running state within {Seconds} s", name, StartWait.TotalSeconds);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), time, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("{Service} started", name);
    }
}
