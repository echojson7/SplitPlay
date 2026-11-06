// SPDX-License-Identifier: MIT
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Host;

namespace SplitPlay.Core.Tests;

public sealed class DeskCoresTests
{
    private const long Mb = 1L << 20;

    /// <summary>A Ryzen with <paramref name="ccds"/> CCDs of 8 cores / 16 threads (SMT siblings next to each other).</summary>
    private static CpuTopology Ryzen(int ccds, params long[] l3Mb)
    {
        var cpus = new List<LogicalCpu>();
        var sizes = new Dictionary<(int, int), long>();
        for (var ccd = 0; ccd < ccds; ccd++)
        {
            for (var t = 0; t < 16; t++)
            {
                cpus.Add(new LogicalCpu(0, (ccd * 16) + t, (ccd * 8) + (t / 2), ccd, 0));
            }

            sizes[(0, ccd)] = (l3Mb.Length > ccd ? l3Mb[ccd] : 32) * Mb;
        }

        return new CpuTopology("Ryzen", cpus, sizes);
    }

    /// <summary>An Intel hybrid: 8 P-cores with HT (0-15) and 16 E-cores (16-31), one shared L3.</summary>
    private static CpuTopology Intel()
    {
        var cpus = new List<LogicalCpu>();
        for (var i = 0; i < 16; i++)
        {
            cpus.Add(new LogicalCpu(0, i, i / 2, 0, 1));
        }

        for (var i = 16; i < 32; i++)
        {
            cpus.Add(new LogicalCpu(0, i, i - 8, 0, 0));
        }

        return new CpuTopology("Core i9", cpus, new Dictionary<(int, int), long> { [(0, 0)] = 36 * Mb });
    }

    private static DeskCoreRequest[] Desks(params string[] ids) => [.. ids.Select(id => new DeskCoreRequest(id, null))];

    private static string Fmt(IReadOnlyDictionary<string, IReadOnlyList<int>?> plan, string id) => DeskCorePlanner.Format(plan[id]);

    [Fact]
    public void Kinds()
    {
        Assert.Equal(CpuKind.Uniform, Ryzen(1).Kind);
        Assert.Equal(CpuKind.Ccd, Ryzen(2).Kind);
        Assert.Equal(CpuKind.X3d, Ryzen(2, 32, 96).Kind);
        Assert.Equal(CpuKind.Hybrid, Intel().Kind);
        Assert.Equal(CpuKind.Uniform, CpuTopology.Empty.Kind);
        Assert.Equal(16, Ryzen(2).Cores);
    }

    [Fact]
    public void Off_restricts_nobody()
    {
        var plan = DeskCorePlanner.Plan(Ryzen(2), DeskCoresMode.Off, Desks("a", "b"));
        Assert.Null(plan["a"]);
        Assert.Null(plan["b"]);
    }

    [Fact]
    public void Auto_gives_each_desk_its_own_ccd_in_config_order()
    {
        var plan = DeskCorePlanner.Plan(Ryzen(2), DeskCoresMode.Auto, Desks("a", "b", "c"));
        Assert.Equal("0-15", Fmt(plan, "a"));
        Assert.Equal("16-31", Fmt(plan, "b"));
        Assert.Equal("0-15", Fmt(plan, "c"));
    }

    [Fact]
    public void Auto_on_x3d_starts_with_the_v_cache_ccd()
    {
        // The 3D V-Cache sits on the second CCD here (96 MB): the first desk gets it.
        var plan = DeskCorePlanner.Plan(Ryzen(2, 32, 96), DeskCoresMode.Auto, Desks("a", "b"));
        Assert.Equal("16-31", Fmt(plan, "a"));
        Assert.Equal("0-15", Fmt(plan, "b"));
    }

    [Fact]
    public void Auto_on_hybrid_puts_every_desk_on_the_p_cores()
    {
        var plan = DeskCorePlanner.Plan(Intel(), DeskCoresMode.Auto, Desks("a", "b"));
        Assert.Equal("0-15", Fmt(plan, "a"));
        Assert.Equal("0-15", Fmt(plan, "b"));
    }

    [Fact]
    public void Auto_on_a_single_ccd_has_nothing_to_split()
    {
        var plan = DeskCorePlanner.Plan(Ryzen(1), DeskCoresMode.Auto, Desks("a"));
        Assert.Null(plan["a"]);
    }

    [Fact]
    public void An_explicit_cpu_set_wins_and_drops_missing_processors()
    {
        DeskCoreRequest[] desks = [new("a", [3, 2, 99]), new("b", null), new("c", [100]), new("d", [.. Enumerable.Range(0, 32)])];
        var plan = DeskCorePlanner.Plan(Ryzen(2), DeskCoresMode.Off, desks);
        Assert.Equal("2-3", Fmt(plan, "a"));
        Assert.Null(plan["b"]);
        Assert.Null(plan["c"]);
        Assert.Null(plan["d"]);

        // With auto, a desk with its own set takes no turn (b gets the first CCD); one whose set names no existing
        // processor is treated as having none (c takes the next turn).
        plan = DeskCorePlanner.Plan(Ryzen(2), DeskCoresMode.Auto, desks);
        Assert.Equal("2-3", Fmt(plan, "a"));
        Assert.Equal("0-15", Fmt(plan, "b"));
        Assert.Equal("16-31", Fmt(plan, "c"));
        Assert.Null(plan["d"]);
    }

    [Fact]
    public void Format_writes_ranges()
    {
        Assert.Equal("all", DeskCorePlanner.Format(null));
        Assert.Equal("0-3, 8, 10-11", DeskCorePlanner.Format([11, 0, 1, 2, 3, 8, 10, 3]));
    }

    [Fact]
    public void Config_validates_desk_cores()
    {
        var config = new SplitPlayConfig { Host = { DeskCores = "max" } };
        Assert.Contains(ConfigValidator.Validate(config), e => e.Path == "host.deskCores");
        config.Host.DeskCores = DeskCoresMode.Auto;
        Assert.DoesNotContain(ConfigValidator.Validate(config), e => e.Path == "host.deskCores");
        Assert.Equal(DeskCoresMode.Off, new HostConfig().DeskCores);
    }

    [Fact]
    public void Config_rejects_huge_cpu_indices()
    {
        var config = new SplitPlayConfig { Desks = [new DeskConfig { Id = "a", Profile = { CpuSet = [4096] } }] };
        Assert.Contains(ConfigValidator.Validate(config), e => e.Path == "desks[0].profile.cpuSet");
    }
}
