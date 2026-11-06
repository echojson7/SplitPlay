// SPDX-License-Identifier: MIT
namespace SplitPlay.Service.Hosting;

/// <summary>
/// Samples GPU load, encoder load and VRAM once a second while someone reads them (the UI's metrics push, GET /metrics),
/// and stops reading counters <see cref="IdleAfter"/> after the last read, so an idle service costs nothing.
/// </summary>
public sealed class GpuUsageMonitor : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>No reads for this long: the counters are closed until the next read.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(30);

    /// <summary>A snapshot older than this (sampling stopped or stalled) is not shown.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(5);

    private readonly IGpuCounterSource _source;
    private readonly TimeProvider _time;
    private readonly ILogger<GpuUsageMonitor> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private GpuUsageSnapshot? _snapshot;
    private DateTimeOffset _snapshotAt;
    private DateTimeOffset _lastRead = DateTimeOffset.MinValue;
    private bool _disabled;

    public GpuUsageMonitor(IGpuCounterSource source, TimeProvider time, ILogger<GpuUsageMonitor> logger)
    {
        _source = source;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The latest numbers, or null until two samples exist (about a second after the first read) or when the counters
    /// are unavailable. Each call keeps sampling going for another <see cref="IdleAfter"/>.
    /// </summary>
    public GpuUsageSnapshot? Current()
    {
        bool wake;
        GpuUsageSnapshot? result;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            wake = now - _lastRead >= IdleAfter;
            _lastRead = now;
            result = _snapshot is not null && now - _snapshotAt <= Stale ? _snapshot : null;
        }

        if (wake && _wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another reader woke the sampler first.
            }
        }

        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested && !_disabled)
            {
                bool active;
                lock (_gate)
                {
                    active = _time.GetUtcNow() - _lastRead < IdleAfter;
                }

                if (!active)
                {
                    _source.Reset();
                    lock (_gate)
                    {
                        _snapshot = null;
                    }

                    await _wake.WaitAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                Sample();
                await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _source.Dispose();
        }
    }

    private void Sample()
    {
        try
        {
            var raw = _source.Collect();
            var snapshot = raw is null ? null : GpuUsageMath.Aggregate(raw, _source.SessionOf);
            lock (_gate)
            {
                // The first collection after (re)opening has no engine data yet: keep showing nothing rather than 0 %.
                if (raw is not null && raw.Engines.Count == 0 && _snapshot is null)
                {
                    return;
                }

                _snapshot = snapshot;
                _snapshotAt = _time.GetUtcNow();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _logger.LogWarning("GPU counters are not available ({Message}); GPU load, encoder and VRAM stay empty", ex.Message);
            _disabled = true;
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
