// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SplitPlay.Service.Hosting;

/// <summary>Where <see cref="GpuUsageMonitor"/> reads GPU counters from. Not thread-safe: one sampler calls it.</summary>
public interface IGpuCounterSource : IDisposable
{
    /// <summary>One collection, or null when the counters are not available (no WDDM 2.x driver, no PDH, not Windows).</summary>
    GpuRawSample? Collect();

    /// <summary>The Windows session a process runs in, null when it has exited or cannot be opened.</summary>
    int? SessionOf(int pid);

    /// <summary>Drops the counters; the next <see cref="Collect"/> starts over.</summary>
    void Reset();
}

/// <summary>The fake platform and tests: no counters until a test sets <see cref="Sample"/> and <see cref="Sessions"/>.</summary>
public sealed class FakeGpuCounterSource : IGpuCounterSource
{
    private readonly object _gate = new();
    private GpuRawSample? _sample;

    public GpuRawSample? Sample
    {
        get
        {
            lock (_gate)
            {
                return _sample;
            }
        }
        set
        {
            lock (_gate)
            {
                _sample = value;
            }
        }
    }

    public System.Collections.Concurrent.ConcurrentDictionary<int, int> Sessions { get; } = new();

    public GpuRawSample? Collect() => Sample;

    public int? SessionOf(int pid) => Sessions.TryGetValue(pid, out var s) ? s : null;

    public void Reset()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Reads the GPU counters Task Manager uses through PDH: <c>\GPU Engine(*)\Utilization Percentage</c>,
/// <c>\GPU Process Memory(*)\Dedicated Usage</c> and <c>\GPU Adapter Memory(*)\Dedicated Usage</c> (English names, so it
/// works on any display language). The query is rebuilt every <see cref="RebuildEvery"/> so that new processes show up.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PdhGpuCounterSource : IGpuCounterSource
{
    /// <summary>Wildcard instances are fixed when a counter is added; rebuilding picks up processes started since.</summary>
    public static readonly TimeSpan RebuildEvery = TimeSpan.FromSeconds(10);

    private const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
    private const string ProcessMemoryPath = @"\GPU Process Memory(*)\Dedicated Usage";
    private const string AdapterMemoryPath = @"\GPU Adapter Memory(*)\Dedicated Usage";

    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private Query? _current;
    private Query? _next;
    private DateTimeOffset _builtAt;
    private bool _warned;
    private byte[] _buffer = new byte[64 * 1024];

    public PdhGpuCounterSource(TimeProvider time, ILogger<PdhGpuCounterSource> logger)
    {
        _time = time;
        _logger = logger;
    }

    public GpuRawSample? Collect()
    {
        var now = _time.GetUtcNow();
        if (_current is null)
        {
            _current = Open();
            if (_current is null)
            {
                return null;
            }

            _builtAt = now;
            // A rate counter needs two collections; the first one only sets the baseline.
            PdhCollectQueryData(_current.Handle);
            return new GpuRawSample([], ReadProcessMemory(_current), ReadAdapterMemory(_current));
        }

        // Swap in the query opened on the previous tick (it has its baseline now), so a rebuild leaves no gap.
        if (_next is not null)
        {
            _current.Dispose();
            _current = _next;
            _next = null;
            _builtAt = now;
        }
        else if (now - _builtAt >= RebuildEvery)
        {
            _next = Open();
            if (_next is not null && PdhCollectQueryData(_next.Handle) != 0)
            {
                // No baseline: keep the current query and try again on the next tick.
                _next.Dispose();
                _next = null;
            }
        }

        var status = PdhCollectQueryData(_current.Handle);
        if (status != 0)
        {
            Warn("PdhCollectQueryData", status);
            Reset();
            return null;
        }

        var engines = new List<GpuEngineSample>();
        foreach (var (name, value) in Read(_current.Engine))
        {
            if (GpuUsageMath.TryParseEngine(name, value, out var s))
            {
                engines.Add(s);
            }
        }

        return new GpuRawSample(engines, ReadProcessMemory(_current), ReadAdapterMemory(_current));
    }

    private List<GpuProcessMemorySample> ReadProcessMemory(Query q)
    {
        var list = new List<GpuProcessMemorySample>();
        foreach (var (name, value) in Read(q.ProcessMemory))
        {
            if (GpuUsageMath.TryParseProcessMemory(name, value, out var s))
            {
                list.Add(s);
            }
        }

        return list;
    }

    private List<GpuAdapterMemorySample> ReadAdapterMemory(Query q)
    {
        var list = new List<GpuAdapterMemorySample>();
        foreach (var (name, value) in Read(q.AdapterMemory))
        {
            if (GpuUsageMath.TryParseAdapterMemory(name, value, out var s))
            {
                list.Add(s);
            }
        }

        return list;
    }

    public int? SessionOf(int pid) => ProcessIdToSessionId((uint)pid, out var session) ? (int)session : null;

    private Query? Open()
    {
        var status = PdhOpenQueryW(null, IntPtr.Zero, out var handle);
        if (status != 0)
        {
            Warn("PdhOpenQuery", status);
            return null;
        }

        // Missing counter sets (an old or non-WDDM 2 driver) leave that counter unset; the others still work.
        var q = new Query(handle) { Engine = Add(handle, EnginePath) };
        q.ProcessMemory = Add(handle, ProcessMemoryPath);
        q.AdapterMemory = Add(handle, AdapterMemoryPath);
        if (q.Engine == IntPtr.Zero && q.ProcessMemory == IntPtr.Zero && q.AdapterMemory == IntPtr.Zero)
        {
            q.Dispose();
            return null;
        }

        return q;
    }

    private IntPtr Add(IntPtr query, string path)
    {
        var status = PdhAddEnglishCounterW(query, path, IntPtr.Zero, out var counter);
        if (status != 0)
        {
            Warn("PdhAddEnglishCounter " + path, status);
            return IntPtr.Zero;
        }

        return counter;
    }

    private void Warn(string what, uint status)
    {
        if (_warned)
        {
            _logger.LogDebug("GPU counters: {What} failed (0x{Status:X8})", what, status);
            return;
        }

        _warned = true;
        _logger.LogWarning("GPU counters: {What} failed (0x{Status:X8}); GPU load, encoder and VRAM may stay empty", what, status);
    }

    private unsafe List<(string Name, double Value)> Read(IntPtr counter)
    {
        var result = new List<(string, double)>();
        if (counter == IntPtr.Zero)
        {
            return result;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = (uint)_buffer.Length;
            uint count;
            uint status;
            fixed (byte* buffer = _buffer)
            {
                status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out count, (IntPtr)buffer);
                if (status == 0)
                {
                    var items = (PdhFmtCounterValueItem*)buffer;
                    for (var i = 0; i < count; i++)
                    {
                        var item = items[i];
                        // VALID_DATA (0) or NEW_DATA (1); anything else is a missing baseline or an exited process.
                        if (item.CStatus <= 1 && item.Name != IntPtr.Zero)
                        {
                            result.Add((new string((char*)item.Name), item.DoubleValue));
                        }
                    }

                    return result;
                }
            }

            if (status != PdhMoreData)
            {
                // PDH_NO_DATA / PDH_CSTATUS_INVALID_DATA right after a rebuild: nothing to read yet.
                return result;
            }

            _buffer = new byte[Math.Max(size, (uint)_buffer.Length * 2)];
        }

        return result;
    }

    public void Reset()
    {
        _current?.Dispose();
        _next?.Dispose();
        _current = _next = null;
    }

    private sealed class Query(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; private set; } = handle;

        public IntPtr Engine { get; set; }

        public IntPtr ProcessMemory { get; set; }

        public IntPtr AdapterMemory { get; set; }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                _ = PdhCloseQuery(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }

    public void Dispose() => Reset();

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;

    /// <summary><c>PDH_FMT_COUNTERVALUE_ITEM_W</c> on x64: name pointer, then <c>CStatus</c> and the 8-byte aligned value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public uint CStatus;
        public uint Padding;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
