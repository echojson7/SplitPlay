// SPDX-License-Identifier: MIT
using SplitPlay.Core.Api;
using SplitPlay.Core.Host;
using SplitPlay.Service.Platform;

namespace SplitPlay.Service.Hosting;

/// <summary>
/// Runs the TermWrap check (<see cref="IDeskPlatform.CheckTermWrap"/>) when the service starts and every few minutes
/// after, so that a Windows update that reset <c>ServiceDll</c> or brought a <c>termsrv.dll</c> TermWrap cannot patch
/// shows up as one line in the UI and the tray (<see cref="Warning"/>) instead of an obscure desk failure. The check is
/// cheap when nothing changed (the probe runs once per <c>termsrv.dll</c> build).
/// </summary>
public sealed class TermWrapMonitor : BackgroundService
{
    private readonly IDeskPlatform _platform;
    private readonly TimeProvider _time;
    private readonly ILogger<TermWrapMonitor> _logger;
    private volatile TermWrapHealth? _current;

    public TermWrapMonitor(IDeskPlatform platform, TimeProvider time, ILogger<TermWrapMonitor> logger)
    {
        _platform = platform;
        _time = time;
        _logger = logger;
    }

    /// <summary>Time between checks (tests shorten it).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The last result; null before the first check finished.</summary>
    public TermWrapHealth? Current => _current;

    /// <summary>Raised after a check whose state differs from the previous one.</summary>
    public event Action<TermWrapHealth>? Changed;

    /// <summary>The host warning for the current state, or null when TermWrap is fine or not managed.</summary>
    public HostWarningDto? Warning => ToWarning(_current);

    public static HostWarningDto? ToWarning(TermWrapHealth? health) => health is null ? null : health.State switch
    {
        TermWrapState.RebootRequired => new HostWarningDto(HostWarningCodes.TermWrapReboot) { Repaired = health.Repaired },
        TermWrapState.Unsupported => new HostWarningDto(HostWarningCodes.TermWrapUnsupported) { Version = health.TermsrvVersion },
        TermWrapState.Missing => new HostWarningDto(HostWarningCodes.TermWrapMissing),
        TermWrapState.RepairFailed => new HostWarningDto(HostWarningCodes.TermWrapRepairFailed),
        _ => null,
    };

    /// <summary>One check (also called by tests).</summary>
    public async Task<TermWrapHealth> CheckAsync()
    {
        var health = await Task.Run(() => _platform.CheckTermWrap());
        var previous = _current;
        if (health.State == TermWrapState.RebootRequired && previous is { State: TermWrapState.RebootRequired, Repaired: true })
        {
            // Still waiting for the reboot after the repair: keep saying why (the next check finds nothing to repair).
            health = health with { Repaired = true };
        }

        _current = health;
        if (health.Repaired && previous?.Repaired != true)
        {
            _logger.LogWarning("TermWrap: TermService's ServiceDll had been reset (Windows Update) and was written back; {State}", health.State);
        }

        if (previous?.State != health.State || previous?.TermsrvVersion != health.TermsrvVersion)
        {
            Log(health);
            Changed?.Invoke(health);
        }

        return health;
    }

    private void Log(TermWrapHealth health)
    {
        switch (health.State)
        {
            case TermWrapState.Ok:
                _logger.LogInformation("TermWrap OK (termsrv.dll {Version}){Detail}", health.TermsrvVersion ?? "?",
                    health.Detail is { } d ? $"; probe notes: {d}" : "");
                break;
            case TermWrapState.NotManaged:
                _logger.LogDebug("TermWrap not managed by SplitPlay on this host");
                break;
            default:
                _logger.LogWarning("TermWrap: {Problem} ({Detail})", health.Problem, health.Detail ?? "no detail");
                break;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("TermWrap check failed: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(Interval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
