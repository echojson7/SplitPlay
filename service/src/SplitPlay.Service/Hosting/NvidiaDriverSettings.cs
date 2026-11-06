// SPDX-License-Identifier: MIT
using SplitPlay.Core.Nvidia;
using SplitPlay.Service.Configuration;

namespace SplitPlay.Service.Hosting;

/// <summary>
/// The service's NVIDIA driver settings (<see cref="NvidiaProfiles"/>; workers leave them alone since patch 0020):
/// <list type="bullet">
/// <item>The "SplitPlay worker" profile (prefer maximum performance for <c>splitplay-worker.exe</c>) is created at
/// service start and kept while SplitPlay is installed; the uninstaller deletes it.</item>
/// <item>The global Vulkan/OpenGL present method is set at service start and given back when the service stops, as
/// upstream Sunshine does for its own lifetime.</item>
/// <item>Both are checked again before every worker start, so a driver update or a reset in NVIDIA Control Panel
/// does not last until the next service start.</item>
/// </list>
/// <c>host.nvidiaProfiles=false</c> gives everything back and leaves the driver alone. Failures are logged and never
/// keep a desk from starting.
/// </summary>
public sealed class NvidiaDriverSettings : IHostedService
{
    private readonly ConfigService _config;
    private readonly ILogger<NvidiaDriverSettings> _logger;
    private readonly NvidiaProfiles _profiles;
    private Task _started = Task.CompletedTask;

    public NvidiaDriverSettings(ConfigService config, DataPaths paths, IDrsProvider drs, ILogger<NvidiaDriverSettings> logger,
        string? legacyUndoFile = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _config = config;
        _logger = logger;
        _profiles = new NvidiaProfiles(drs, NvidiaProfiles.StateFileIn(paths.Root), legacyUndoFile);
    }

    /// <summary>The settings SplitPlay wants while the service runs.</summary>
    public IReadOnlyList<DrsProfileSpec> Wanted(bool running) =>
        !_config.Snapshot().Host.NvidiaProfiles ? []
        : running ? [NvidiaProfiles.Worker, NvidiaProfiles.Global]
        : [NvidiaProfiles.Worker];

    /// <summary>What SplitPlay holds now (record of <c>nvidia-profiles.json</c>).</summary>
    public NvidiaProfilesState State() => _profiles.LoadState();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Off the start-up path: loading the driver database can take a moment.
        _started = Task.Run(() => Apply("service start", running: true), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Registered before the supervisor, so this runs after every worker has stopped.
        await _started.ConfigureAwait(false);
        Apply("service stop", running: false);
    }

    /// <summary>Before a worker starts, and when <c>host.nvidiaProfiles</c> changes. Never throws.</summary>
    public void EnsureApplied(string reason) => Apply(reason, running: true);

    private void Apply(string reason, bool running)
    {
        try
        {
            var result = _profiles.Apply(Wanted(running));
            if (!result.Driver)
            {
                _logger.LogDebug("NVIDIA driver settings ({Reason}): no NVIDIA driver", reason);
                return;
            }

            foreach (var change in result.Changes)
            {
                _logger.LogInformation("NVIDIA driver settings ({Reason}): {Change}", reason, change);
            }

            foreach (var note in result.Notes)
            {
                _logger.LogInformation("NVIDIA driver settings ({Reason}): {Note}", reason, note);
            }

            if (result.Changes.Count == 0 && result.Notes.Count == 0)
            {
                _logger.LogDebug("NVIDIA driver settings ({Reason}): unchanged", reason);
            }
        }
        catch (Exception ex) when (ex is NvidiaDrsException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("NVIDIA driver settings ({Reason}) failed: {Message}", reason, ex.Message);
        }
    }
}
