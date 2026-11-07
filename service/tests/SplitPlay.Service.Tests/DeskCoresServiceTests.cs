// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using SplitPlay.Core.Api;
using SplitPlay.Core.Host;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Platform;

namespace SplitPlay.Service.Tests;

public sealed class DeskCoresServiceTests
{
    /// <summary>A Ryzen 9 X3D: two CCDs of 8 cores / 16 threads, the second one with 96 MB of L3.</summary>
    private static CpuTopology X3d()
    {
        var cpus = Enumerable.Range(0, 32).Select(i => new LogicalCpu(0, i, i / 2, i / 16, 0)).ToList();
        return new CpuTopology("AMD Ryzen 9 7950X3D", cpus, new Dictionary<(int, int), long> { [(0, 0)] = 32L << 20, [(0, 1)] = 96L << 20 });
    }

    private static async Task SetDeskCoresAsync(TestService svc, string mode)
    {
        var host = (await svc.Client.GetFromJsonAsync("/api/v1/host", SplitPlayJsonContext.Default.HostConfig))!;
        host.DeskCores = mode;
        var put = await svc.Client.PutAsJsonAsync("/api/v1/host", host, SplitPlayJsonContext.Default.HostConfig);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    private static async Task WaitForAppliedAsync(TestService svc, string desk, string cpus)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(svc.Cpu.Applied.TryGetValue(desk, out var applied) && applied == cpus))
        {
            Assert.True(DateTime.UtcNow < until, $"desk {desk} never got processors {cpus} (has {svc.Cpu.Applied.GetValueOrDefault(desk)})");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Auto_moves_running_desks_to_their_own_ccd_and_off_releases_them()
    {
        await using var svc = await TestService.StartAsync();
        svc.Cpu.Current = X3d();
        await svc.CreateDeskAsync("desk-1");
        await svc.CreateDeskAsync("desk-2");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.PostDeskActionAsync("desk-2", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        await svc.WaitForStateAsync("desk-2", DeskState.Ready);

        // Off (the default): swept, but not restricted.
        await WaitForAppliedAsync(svc, "desk-1", "all");
        Assert.Equal(DeskCoresMode.Off, svc.ReadConfigFile().Host.DeskCores);

        await SetDeskCoresAsync(svc, DeskCoresMode.Auto);
        await WaitForAppliedAsync(svc, "desk-1", "16-31");
        await WaitForAppliedAsync(svc, "desk-2", "0-15");

        var cpu = (await svc.Client.GetFromJsonAsync("/api/v1/host/cpu", SplitPlayJsonContext.Default.HostCpuDto))!;
        Assert.Equal(CpuKind.X3d, cpu.Kind);
        Assert.Equal(DeskCoresMode.Auto, cpu.Mode);
        Assert.Equal(16, cpu.Cores);
        Assert.Equal(32, cpu.Threads);
        Assert.Equal([96.0, 32.0], cpu.Caches.Select(c => c.L3Mb));
        Assert.Equal(Enumerable.Range(16, 16), cpu.Desks.Single(d => d.Id == "desk-1").Cpus!);

        await SetDeskCoresAsync(svc, DeskCoresMode.Off);
        await WaitForAppliedAsync(svc, "desk-1", "all");
        cpu = (await svc.Client.GetFromJsonAsync("/api/v1/host/cpu", SplitPlayJsonContext.Default.HostCpuDto))!;
        Assert.All(cpu.Desks, d => Assert.Null(d.Cpus));
        Assert.Equal(Enumerable.Range(0, 16), cpu.Auto.Single(d => d.Id == "desk-2").Cpus!);

        // The scope ends with the session.
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (svc.Cpu.Open.ContainsKey("desk-1"))
        {
            Assert.True(DateTime.UtcNow < until, "the CPU scope of a stopped desk stayed open");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Unknown_desk_cores_mode_is_rejected()
    {
        await using var svc = await TestService.StartAsync();
        var host = (await svc.Client.GetFromJsonAsync("/api/v1/host", SplitPlayJsonContext.Default.HostConfig))!;
        host.DeskCores = "all";
        var put = await svc.Client.PutAsJsonAsync("/api/v1/host", host, SplitPlayJsonContext.Default.HostConfig);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    // ------------------------------------------------------------------ buffer parsers

    private static byte[] CpuSetEntry(uint id, ushort group, byte number, byte core, byte cache, byte efficiency)
    {
        var e = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(e, 32);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), id);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(12), group);
        e[14] = number;
        e[15] = core;
        e[16] = cache;
        e[18] = efficiency;
        return e;
    }

    private static byte[] CacheEntry(byte level, uint size, ulong mask, ushort groupCount = 1)
    {
        var e = new byte[56];
        BinaryPrimitives.WriteInt32LittleEndian(e, 2);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(4), 56);
        e[8] = level;
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), size);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(38), groupCount);
        BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(40), mask);
        return e;
    }

    [Fact]
    public void Cpu_set_and_cache_buffers_are_parsed()
    {
        byte[] sets = [.. CpuSetEntry(256, 0, 0, 0, 0, 1), .. CpuSetEntry(257, 0, 1, 0, 0, 1), .. CpuSetEntry(258, 0, 2, 1, 1, 0),
            .. CpuSetEntry(259, 0, 3, 1, 1, 0)];
        var parsed = CpuTopologyReader.ParseCpuSets(sets);
        Assert.Equal([256u, 257u, 258u, 259u], parsed.Select(p => p.Id));
        Assert.Equal(new LogicalCpu(0, 2, 1, 1, 0), parsed[2].Cpu);

        var cpus = parsed.Select(p => p.Cpu).ToList();
        // An L2 entry is ignored; GroupCount 0 (Windows 10) means one mask.
        byte[] caches = [.. CacheEntry(2, 1 << 20, 0b0011), .. CacheEntry(3, 32u << 20, 0b0011), .. CacheEntry(3, 96u << 20, 0b1100, 0)];
        var sizes = CpuTopologyReader.ParseL3Sizes(caches, cpus);
        Assert.Equal(32L << 20, sizes[(0, 0)]);
        Assert.Equal(96L << 20, sizes[(0, 1)]);

        // Two L3 caches of different size: X3D, whatever the efficiency classes say.
        var topology = new CpuTopology(null, cpus, sizes);
        Assert.Equal(CpuKind.X3d, topology.Kind);
        Assert.Equal([2, 3], topology.CacheGroups[0].Cpus);

        // A truncated entry stops the parse instead of reading past the buffer.
        Assert.Single(CpuTopologyReader.ParseCpuSets(sets.AsSpan(0, 40)));
    }
}
