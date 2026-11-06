// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.RegularExpressions;

namespace SplitPlay.Service.Hosting;

/// <summary>One <c>\GPU Engine(*)\Utilization Percentage</c> instance: how busy one engine was for one process.</summary>
public readonly record struct GpuEngineSample(int Pid, string Luid, int Phys, int Engine, string EngineType, double Percent);

/// <summary>One <c>\GPU Process Memory(*)\Dedicated Usage</c> instance, bytes.</summary>
public readonly record struct GpuProcessMemorySample(int Pid, string Luid, double DedicatedBytes);

/// <summary>One <c>\GPU Adapter Memory(*)\Dedicated Usage</c> instance, bytes.</summary>
public readonly record struct GpuAdapterMemorySample(string Luid, double DedicatedBytes);

/// <summary>One read of the three GPU counter sets. Engine samples are empty until the counters have two collections.</summary>
public sealed record GpuRawSample(
    IReadOnlyList<GpuEngineSample> Engines,
    IReadOnlyList<GpuProcessMemorySample> ProcessMemory,
    IReadOnlyList<GpuAdapterMemorySample> AdapterMemory);

/// <summary>Live load of one adapter, as Task Manager shows it.</summary>
/// <param name="UtilizationPercent">The busiest engine (all processes summed), like Task Manager's "GPU" column.</param>
/// <param name="EncoderPercent">The busiest video encode engine (NVENC); null when the adapter reports none.</param>
public sealed record GpuAdapterUsage(double UtilizationPercent, double? EncoderPercent, double? VramUsedMb);

/// <summary>Live GPU use of all processes in one Windows session (one desk), summed over adapters.</summary>
public sealed record GpuSessionUsage(double GpuPercent, double? EncoderPercent, double VramMb);

/// <summary>Adapters by LUID (as <see cref="GpuInventory.FormatLuid"/>) and sessions by id.</summary>
public sealed record GpuUsageSnapshot(
    IReadOnlyDictionary<string, GpuAdapterUsage> Adapters,
    IReadOnlyDictionary<int, GpuSessionUsage> Sessions)
{
    public static GpuUsageSnapshot Empty { get; } = new(new Dictionary<string, GpuAdapterUsage>(), new Dictionary<int, GpuSessionUsage>());
}

/// <summary>
/// Parses the GPU performance counter instance names (the ones Task Manager reads) and turns them into per-adapter and
/// per-session numbers. Instance names look like <c>pid_1234_luid_0x00000000_0x0000D1B4_phys_0_eng_3_engtype_VideoEncode</c>.
/// </summary>
public static partial class GpuUsageMath
{
    [GeneratedRegex(@"^pid_(\d+)_luid_0x([0-9A-Fa-f]{8})_0x([0-9A-Fa-f]{8})_phys_(\d+)_eng_(\d+)_engtype_(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex EngineRegex();

    [GeneratedRegex(@"^pid_(\d+)_luid_0x([0-9A-Fa-f]{8})_0x([0-9A-Fa-f]{8})_phys_\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessMemoryRegex();

    [GeneratedRegex(@"^luid_0x([0-9A-Fa-f]{8})_0x([0-9A-Fa-f]{8})_phys_\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterMemoryRegex();

    /// <summary>The counter's <c>luid_0xHIGH_0xLOW</c> pair in the config's form: <c>0x</c> + HIGH + LOW, upper case.</summary>
    private static string Luid(string high, string low) => "0x" + high.ToUpperInvariant() + low.ToUpperInvariant();

    private static int Int(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1;

    public static bool TryParseEngine(string instance, double value, out GpuEngineSample sample)
    {
        var m = EngineRegex().Match(instance);
        if (!m.Success || !double.IsFinite(value))
        {
            sample = default;
            return false;
        }

        sample = new GpuEngineSample(Int(m.Groups[1].Value), Luid(m.Groups[2].Value, m.Groups[3].Value), Int(m.Groups[4].Value),
            Int(m.Groups[5].Value), m.Groups[6].Value, value);
        return sample.Pid >= 0;
    }

    public static bool TryParseProcessMemory(string instance, double value, out GpuProcessMemorySample sample)
    {
        var m = ProcessMemoryRegex().Match(instance);
        if (!m.Success || !double.IsFinite(value))
        {
            sample = default;
            return false;
        }

        sample = new GpuProcessMemorySample(Int(m.Groups[1].Value), Luid(m.Groups[2].Value, m.Groups[3].Value), value);
        return sample.Pid >= 0;
    }

    public static bool TryParseAdapterMemory(string instance, double value, out GpuAdapterMemorySample sample)
    {
        var m = AdapterMemoryRegex().Match(instance);
        if (!m.Success || !double.IsFinite(value))
        {
            sample = default;
            return false;
        }

        sample = new GpuAdapterMemorySample(Luid(m.Groups[1].Value, m.Groups[2].Value), value);
        return true;
    }

    /// <summary>
    /// A video encoder engine: NVIDIA and Intel report <c>VideoEncode</c> (sometimes numbered), AMD a shared
    /// <c>VideoCodec</c> engine (VCN), which is the encoder as far as a stream is concerned.
    /// </summary>
    public static bool IsEncoder(string engineType) =>
        engineType.Contains("Encode", StringComparison.OrdinalIgnoreCase)
        || engineType.StartsWith("VideoCodec", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Per-adapter and per-session numbers. An engine's load is the sum over processes (capped at 100); a load figure
    /// is the busiest engine, as in Task Manager. Processes whose session <paramref name="sessionOf"/> cannot tell are
    /// counted for the adapter only.
    /// </summary>
    public static GpuUsageSnapshot Aggregate(GpuRawSample sample, Func<int, int?> sessionOf)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(sessionOf);
        var sessions = new Dictionary<int, int?>();
        int? Session(int pid)
        {
            if (!sessions.TryGetValue(pid, out var s))
            {
                s = pid == 0 ? null : sessionOf(pid);
                sessions[pid] = s;
            }

            return s;
        }

        // Engine load summed over processes: per adapter, and per (session, adapter).
        var engines = new Dictionary<(string Luid, int Phys, int Engine), (double Sum, bool Encoder)>();
        var sessionEngines = new Dictionary<(int Session, string Luid, int Phys, int Engine), (double Sum, bool Encoder)>();
        foreach (var e in sample.Engines)
        {
            var value = Math.Max(0, e.Percent);
            var encoder = IsEncoder(e.EngineType);
            var key = (e.Luid, e.Phys, e.Engine);
            engines[key] = (engines.GetValueOrDefault(key).Sum + value, encoder);
            if (Session(e.Pid) is int s)
            {
                var skey = (s, e.Luid, e.Phys, e.Engine);
                sessionEngines[skey] = (sessionEngines.GetValueOrDefault(skey).Sum + value, encoder);
            }
        }

        var vram = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var a in sample.AdapterMemory)
        {
            vram[a.Luid] = vram.GetValueOrDefault(a.Luid) + Math.Max(0, a.DedicatedBytes);
        }

        var sessionVram = new Dictionary<int, double>();
        foreach (var p in sample.ProcessMemory)
        {
            if (Session(p.Pid) is int s)
            {
                sessionVram[s] = sessionVram.GetValueOrDefault(s) + Math.Max(0, p.DedicatedBytes);
            }
        }

        var adapters = new Dictionary<string, GpuAdapterUsage>(StringComparer.Ordinal);
        foreach (var luid in engines.Keys.Select(k => k.Luid).Concat(vram.Keys).Distinct(StringComparer.Ordinal))
        {
            var mine = engines.Where(kv => kv.Key.Luid == luid).Select(kv => kv.Value).ToList();
            adapters[luid] = new GpuAdapterUsage(
                Percent(mine.Select(v => v.Sum).DefaultIfEmpty(0).Max()),
                mine.Any(v => v.Encoder) ? Percent(mine.Where(v => v.Encoder).Max(v => v.Sum)) : null,
                vram.TryGetValue(luid, out var bytes) ? Mb(bytes) : null);
        }

        var result = new Dictionary<int, GpuSessionUsage>();
        foreach (var s in sessionEngines.Keys.Select(k => k.Session).Concat(sessionVram.Keys).Distinct())
        {
            var mine = sessionEngines.Where(kv => kv.Key.Session == s).Select(kv => kv.Value).ToList();
            // The session's encoder figure exists when its adapter has an encoder at all, even if the session leaves it idle.
            var hasEncoder = engines.Any(kv => kv.Value.Encoder && sessionEngines.Keys.Any(k => k.Session == s && k.Luid == kv.Key.Luid));
            result[s] = new GpuSessionUsage(
                Percent(mine.Select(v => v.Sum).DefaultIfEmpty(0).Max()),
                hasEncoder ? Percent(mine.Where(v => v.Encoder).Select(v => v.Sum).DefaultIfEmpty(0).Max()) : null,
                Mb(sessionVram.GetValueOrDefault(s)));
        }

        return new GpuUsageSnapshot(adapters, result);
    }

    private static double Percent(double value) => Math.Round(Math.Clamp(value, 0, 100));

    private static double Mb(double bytes) => Math.Round(bytes / 1024.0 / 1024.0);
}
