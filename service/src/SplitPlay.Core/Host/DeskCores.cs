// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Host;

/// <summary>Values of <c>host.deskCores</c>.</summary>
public static class DeskCoresMode
{
    /// <summary>Desk processes run where Windows puts them (default until measured on real hardware).</summary>
    public const string Off = "off";

    /// <summary>Per-CPU policy (<see cref="DeskCorePlanner"/>): one L3 cache (CCD) per desk, or the performance cores of a hybrid CPU.</summary>
    public const string Auto = "auto";

    public static readonly IReadOnlyList<string> All = [Off, Auto];
}

/// <summary>
/// One logical processor. <see cref="Index"/> is the number used in config and the API (<c>group * 64 + number</c>,
/// the processor number Task Manager shows on PCs with up to 64 logical processors).
/// </summary>
/// <param name="Group">Processor group.</param>
/// <param name="Number">Number inside the group.</param>
/// <param name="Core">Physical core (SMT siblings share it).</param>
/// <param name="Cache">Last level (L3) cache the processor uses; on Ryzen one per CCD.</param>
/// <param name="EfficiencyClass">Higher is faster; equal on non-hybrid CPUs (Intel P-cores have 1, E-cores 0).</param>
public sealed record LogicalCpu(int Group, int Number, int Core, int Cache, int EfficiencyClass)
{
    public int Index => (Group * 64) + Number;
}

/// <summary>How <see cref="DeskCorePlanner"/> splits a CPU.</summary>
public static class CpuKind
{
    /// <summary>One L3 cache, one core type: nothing to split, desks are left alone.</summary>
    public const string Uniform = "uniform";

    /// <summary>Intel hybrid (P- and E-cores): desk processes go to the P-cores.</summary>
    public const string Hybrid = "hybrid";

    /// <summary>Several L3 caches of the same size (e.g. Ryzen 9 with two CCDs): each desk gets its own.</summary>
    public const string Ccd = "ccd";

    /// <summary>Several L3 caches, one larger (Ryzen X3D): each desk gets its own, the 3D V-Cache CCD first.</summary>
    public const string X3d = "x3d";
}

/// <summary>A group of logical processors that share a last level cache, as the planner hands them to desks.</summary>
public sealed record CpuCacheGroup(int Group, int Cache, long L3Bytes, IReadOnlyList<int> Cpus);

/// <summary>The CPU as the planner sees it.</summary>
/// <param name="Name">Marketing name from the registry, or null.</param>
/// <param name="Cpus">Every logical processor the system lets processes use.</param>
/// <param name="L3Bytes">Size of each L3 cache by (group, cache); missing entries count as unknown (0).</param>
public sealed record CpuTopology(string? Name, IReadOnlyList<LogicalCpu> Cpus, IReadOnlyDictionary<(int Group, int Cache), long> L3Bytes)
{
    public static CpuTopology Empty { get; } = new(null, [], new Dictionary<(int, int), long>());

    public int Cores => Cpus.Select(c => (c.Group, c.Core)).Distinct().Count();

    /// <summary>The processors desks may use: all of them on a non-hybrid CPU, the fastest class on a hybrid one.</summary>
    public IReadOnlyList<LogicalCpu> PerformanceCpus
    {
        get
        {
            if (Cpus.Count == 0)
            {
                return [];
            }

            var top = Cpus.Max(c => c.EfficiencyClass);
            return [.. Cpus.Where(c => c.EfficiencyClass == top).OrderBy(c => c.Index)];
        }
    }

    public bool IsHybrid => Cpus.Select(c => c.EfficiencyClass).Distinct().Count() > 1;

    /// <summary>The performance processors grouped by L3 cache: larger cache first, then in processor order.</summary>
    public IReadOnlyList<CpuCacheGroup> CacheGroups =>
    [
        .. PerformanceCpus
            .GroupBy(c => (c.Group, c.Cache))
            .Select(g => new CpuCacheGroup(g.Key.Group, g.Key.Cache, L3Bytes.GetValueOrDefault(g.Key), [.. g.Select(c => c.Index)]))
            .OrderByDescending(g => g.L3Bytes)
            .ThenBy(g => g.Cpus[0]),
    ];

    /// <summary>One of <see cref="CpuKind"/>.</summary>
    public string Kind
    {
        get
        {
            var groups = CacheGroups;
            if (groups.Count > 1)
            {
                var sizes = groups.Select(g => g.L3Bytes).Where(b => b > 0).Distinct().Count();
                return sizes > 1 ? CpuKind.X3d : CpuKind.Ccd;
            }

            return IsHybrid ? CpuKind.Hybrid : CpuKind.Uniform;
        }
    }
}

/// <summary>A desk as the planner needs it: its id and its explicit <c>profile.cpuSet</c> (null: none).</summary>
public sealed record DeskCoreRequest(string Id, IReadOnlyList<int>? CpuSet);

/// <summary>
/// Decides which logical processors each desk's processes may run on (<c>host.deskCores</c>, Stage 4 PR 12).
/// <list type="bullet">
/// <item><c>off</c>: no desk is restricted, except one with an explicit <c>profile.cpuSet</c>.</item>
/// <item><c>auto</c> on a CPU with several L3 caches (Ryzen with two or more CCDs): desks take the caches in turn, in
/// config order, the largest cache (3D V-Cache) first; with more desks than caches they share round-robin. Only the
/// performance cores are used if the CPU is also hybrid.</item>
/// <item><c>auto</c> on a hybrid CPU with one L3 (Intel 12th gen and later): every desk gets the P-cores, so games stay
/// off the E-cores. The console keeps all cores.</item>
/// <item><c>auto</c> on any other CPU: nothing to split, no desk is restricted.</item>
/// </list>
/// An explicit <c>profile.cpuSet</c> always wins (also with <c>off</c>) and takes no turn; processors it names that do not
/// exist are dropped, and a set with none left counts as not set. A restriction that covers every processor is
/// reported as null (not restricted).
/// </summary>
public static class DeskCorePlanner
{
    /// <summary>The processors of each desk (null: not restricted), keyed by desk id.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<int>?> Plan(CpuTopology topology, string? mode, IReadOnlyList<DeskCoreRequest> desks)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(desks);
        var all = topology.Cpus.Select(c => c.Index).ToHashSet();
        var plan = new Dictionary<string, IReadOnlyList<int>?>(StringComparer.Ordinal);
        var auto = string.Equals(mode, DeskCoresMode.Auto, StringComparison.Ordinal) ? AutoSets(topology) : [];
        var turn = 0;
        foreach (var desk in desks)
        {
            // An explicit profile.cpuSet is used as it is, and the desk takes no turn.
            IReadOnlyList<int>? cpus = desk.CpuSet is { Count: > 0 } explicitSet ? [.. explicitSet.Where(all.Contains).Distinct().Order()] : null;
            if (cpus is not { Count: > 0 })
            {
                cpus = auto.Count > 0 ? auto[turn++ % auto.Count] : null;
            }

            plan[desk.Id] = cpus is null || cpus.Count == 0 || cpus.Count >= all.Count ? null : cpus;
        }

        return plan;
    }

    /// <summary>The sets <c>auto</c> hands out in turn (empty: nothing to split).</summary>
    private static List<IReadOnlyList<int>> AutoSets(CpuTopology topology) => topology.Kind switch
    {
        CpuKind.Ccd or CpuKind.X3d => [.. topology.CacheGroups.Select(g => g.Cpus)],
        CpuKind.Hybrid => [[.. topology.PerformanceCpus.Select(c => c.Index)]],
        _ => [],
    };

    /// <summary>"0-7, 16-23": processor indices as ranges, for logs and the UI.</summary>
    public static string Format(IReadOnlyList<int>? cpus)
    {
        if (cpus is null || cpus.Count == 0)
        {
            return "all";
        }

        var sorted = cpus.Distinct().Order().ToList();
        var parts = new List<string>();
        var start = sorted[0];
        var prev = start;
        foreach (var c in sorted.Skip(1).Append(int.MinValue))
        {
            if (c == prev + 1)
            {
                prev = c;
                continue;
            }

            parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
            start = prev = c;
        }

        return string.Join(", ", parts);
    }
}
