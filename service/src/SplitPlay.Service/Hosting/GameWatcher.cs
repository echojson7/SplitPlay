// SPDX-License-Identifier: MIT
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Games;
using SplitPlay.Service.Configuration;
using SplitPlay.Service.Platform;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Hosting;

/// <summary>A desk as the game watcher sees it.</summary>
/// <param name="Session">The desk's own Windows session while it runs; null otherwise.</param>
/// <param name="ClientFps">The frame rate the desk's Moonlight client asked for; null without a client.</param>
public sealed record GameDeskView(string Id, int? Session, int? ClientFps);

/// <summary>
/// Finds the games in running desks and keeps their FPS limits in the driver (<see cref="GameLimits"/>):
/// <list type="bullet">
/// <item>Every <see cref="Interval"/> it reads the per-process GPU load (<see cref="GpuUsageMonitor"/>, which samples
/// only while somebody reads) of the desks whose limit is not off. A process that renders at
/// <see cref="LoadPercent"/> or more in <see cref="HitsToDetect"/> reads within <see cref="HitWindow"/>, and that <see cref="GameLimits.MayBeGame"/>
/// accepts, is put on record in <c>games.json</c>.</item>
/// <item>It remembers the frame rate each desk's Moonlight client asked for (Auto).</item>
/// <item>When the resulting limits differ from the ones last applied, <see cref="NvidiaDriverSettings"/> applies them.</item>
/// </list>
/// Nothing is read while no desk runs or <c>host.nvidiaProfiles</c> is off, so an idle service costs nothing.
/// </summary>
public sealed class GameWatcher : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>3D load (busiest non-video engine) that counts as rendering like a game.</summary>
    public const double LoadPercent = 10;

    /// <summary>Reads at <see cref="LoadPercent"/> or more before a process counts as a game (not necessarily in a row).</summary>
    public const int HitsToDetect = 3;

    /// <summary>The <see cref="HitsToDetect"/> reads must fall within this time.</summary>
    public static readonly TimeSpan HitWindow = TimeSpan.FromMinutes(1);

    private readonly ConfigService _config;
    private readonly DeskSupervisor _supervisor;
    private readonly GpuUsageMonitor _gpu;
    private readonly IProcessImages _images;
    private readonly GameCatalog _catalog;
    private readonly NvidiaDriverSettings _nvidia;
    private readonly TimeProvider _time;
    private readonly ILogger<GameWatcher> _logger;
    private readonly object _tickGate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Processes being watched: pid → (session, reads with load, decided). Dropped when they disappear.</summary>
    private readonly Dictionary<int, Watched> _watched = [];

    public GameWatcher(ConfigService config, DeskSupervisor supervisor, GpuUsageMonitor gpu, IProcessImages images, GameCatalog catalog,
        NvidiaDriverSettings nvidia, TimeProvider time, ILogger<GameWatcher> logger)
    {
        _config = config;
        _supervisor = supervisor;
        _gpu = gpu;
        _images = images;
        _catalog = catalog;
        _nvidia = nvidia;
        _time = time;
        _logger = logger;
        if (catalog.LoadError is { } error)
        {
            logger.LogWarning("Game FPS limits: {Error}", error);
        }
    }

    /// <summary>Checks at once (after a change in the API) instead of at the next interval.</summary>
    public void Nudge()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already nudged.
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Off the caller's thread: opening the driver database takes a moment.
                await Task.Run(Tick, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Game FPS limits: check failed");
            }

            try
            {
                await _wake.WaitAsync(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One check (internal for tests).</summary>
    internal void Tick()
    {
        lock (_tickGate)
        {
            TickLocked();
        }
    }

    private void TickLocked()
    {
        var config = _config.Snapshot();
        var desks = _supervisor.GameDesks();
        _catalog.Prune([.. config.Desks.Select(d => d.Id)]);
        if (config.Host.NvidiaProfiles)
        {
            foreach (var desk in desks)
            {
                if (desk.ClientFps is int fps)
                {
                    _catalog.SetClientFps(desk.Id, fps);
                }
            }

            Detect(config, desks);
        }
        else
        {
            _watched.Clear();
        }

        var limits = _nvidia.WantedLimits();
        if (_nvidia.NeedsApply(limits))
        {
            _nvidia.EnsureApplied("game FPS limits");
        }
    }

    private void Detect(SplitPlayConfig config, IReadOnlyList<GameDeskView> desks)
    {
        // Desks that run in their own session with the limit on; no NVIDIA driver: nothing to limit, nothing to read.
        var sessions = desks
            .Where(d => d.Session is not null && config.Desks.FirstOrDefault(c => c.Id == d.Id)?.GameFpsLimit != GameLimits.Off)
            .GroupBy(d => d.Session!.Value)
            .ToDictionary(g => g.Key, g => g.First().Id);
        if (sessions.Count == 0 || _nvidia.DriverPresent == false)
        {
            _watched.Clear();
            return;
        }

        var usage = _gpu.Current();
        if (usage is null)
        {
            return;
        }

        var seen = new HashSet<int>();
        foreach (var p in usage.Processes)
        {
            if (!sessions.TryGetValue(p.Session, out var deskId))
            {
                continue;
            }

            seen.Add(p.Pid);
            if (!_watched.TryGetValue(p.Pid, out var w) || w.Session != p.Session)
            {
                w = new Watched(p.Session);
                _watched[p.Pid] = w;
            }

            if (w.Decided || p.GpuPercent < LoadPercent)
            {
                continue;
            }

            // The reads with load must fall within one window: short spikes of an editor or a tool over hours do not add up.
            var now = _time.GetTimestamp();
            if (w.Hits == 0 || _time.GetElapsedTime(w.FirstHit, now) > HitWindow)
            {
                w.Hits = 0;
                w.FirstHit = now;
            }

            if (++w.Hits < HitsToDetect)
            {
                continue;
            }

            var path = _images.PathOf(p.Pid);
            if (path is null)
            {
                // Not readable now (exiting, access): tried again a few times before the process is given up.
                w.Decided = ++w.PathFailures >= 3;
                continue;
            }

            // Decided once per process: a non-game is not looked at again.
            w.Decided = true;
            if (!GameLimits.MayBeGame(path, _images.WindowsDirectory))
            {
                continue;
            }

            var exe = GameLimits.FileName(path);
            if (_catalog.Record(exe, path, deskId, _time.GetUtcNow()))
            {
                _logger.LogInformation("Desk {Desk}: game {Exe} found ({Path}, GPU {Percent} %); its FPS limit applies from its next start",
                    deskId, exe, path, p.GpuPercent);
            }
        }

        // Gone processes are forgotten (a pid can be reused).
        foreach (var pid in _watched.Keys.Where(pid => !seen.Contains(pid)).ToList())
        {
            _watched.Remove(pid);
        }
    }

    private sealed class Watched(int session)
    {
        public int Session { get; } = session;

        public int Hits { get; set; }

        public long FirstHit { get; set; }

        public int PathFailures { get; set; }

        public bool Decided { get; set; }
    }
}
