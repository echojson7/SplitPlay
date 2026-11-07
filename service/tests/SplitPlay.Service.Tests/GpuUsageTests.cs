// SPDX-License-Identifier: MIT
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Tests;

/// <summary>GPU load, encoder and VRAM from the PDH GPU counters: instance names, aggregation and the metrics API.</summary>
public sealed class GpuUsageTests
{
    private const string Luid = "0x000000000000D1B4";

    private static GpuEngineSample Engine(int pid, int eng, string type, double percent) =>
        new(pid, Luid, 0, eng, type, percent);

    [Fact]
    public void Engine_instance_names_are_parsed_and_the_luid_matches_the_dxgi_form()
    {
        Assert.True(GpuUsageMath.TryParseEngine("pid_1234_luid_0x00000000_0x0000d1b4_phys_0_eng_5_engtype_VideoEncode", 12.5, out var e));
        Assert.Equal(1234, e.Pid);
        Assert.Equal(GpuInventory.FormatLuid(0xD1B4, 0), e.Luid);
        Assert.Equal(Luid, e.Luid);
        Assert.Equal(5, e.Engine);
        Assert.Equal("VideoEncode", e.EngineType);
        Assert.Equal(12.5, e.Percent);

        Assert.True(GpuUsageMath.TryParseEngine("pid_8_luid_0x00000001_0x0000ABCD_phys_1_eng_12_engtype_Compute_0", 1, out var c));
        Assert.Equal("Compute_0", c.EngineType);
        Assert.Equal(1, c.Phys);
        Assert.Equal("0x000000010000ABCD", c.Luid);

        Assert.False(GpuUsageMath.TryParseEngine("_Total", 1, out _));
        Assert.False(GpuUsageMath.TryParseEngine("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 1, out _));
        Assert.False(GpuUsageMath.TryParseEngine("pid_1234_luid_0x00000000_0x0000D1B4_phys_0_eng_0_engtype_3D", double.NaN, out _));

        Assert.True(GpuUsageMath.TryParseProcessMemory("pid_42_luid_0x00000000_0x0000D1B4_phys_0", 1024, out var pm));
        Assert.Equal(42, pm.Pid);
        Assert.Equal(Luid, pm.Luid);
        Assert.False(GpuUsageMath.TryParseProcessMemory("luid_0x00000000_0x0000D1B4_phys_0", 1024, out _));

        Assert.True(GpuUsageMath.TryParseAdapterMemory("luid_0x00000000_0x0000D1B4_phys_0", 2048, out var am));
        Assert.Equal(Luid, am.Luid);
        Assert.False(GpuUsageMath.TryParseAdapterMemory("pid_42_luid_0x00000000_0x0000D1B4_phys_0", 1, out _));
    }

    [Theory]
    [InlineData("VideoEncode", true)]
    [InlineData("VideoEncode_1", true)]
    [InlineData("VideoCodec", true)]
    [InlineData("Encode", true)]
    [InlineData("VideoDecode", false)]
    [InlineData("3D", false)]
    [InlineData("Copy", false)]
    public void Encoder_engines_are_recognised(string type, bool encoder) => Assert.Equal(encoder, GpuUsageMath.IsEncoder(type));

    [Fact]
    public void Load_is_the_busiest_engine_summed_over_processes_per_adapter_and_per_session()
    {
        const double Mb = 1024 * 1024;
        var sample = new GpuRawSample(
            [
                // Desk session 7: a game on 3D and the worker on NVENC.
                Engine(100, 0, "3D", 30),
                Engine(101, 5, "VideoEncode", 20),
                Engine(101, 0, "3D", 2),
                // Console session 1: a game on 3D.
                Engine(200, 0, "3D", 40),
                Engine(200, 5, "VideoEncode", 0),
                // A process that exited (no session): counts for the adapter only.
                Engine(300, 0, "3D", 1),
                Engine(0, 0, "3D", 1),
            ],
            [
                new(100, Luid, 1500 * Mb),
                new(101, Luid, 300 * Mb),
                new(200, Luid, 2000 * Mb),
            ],
            [new(Luid, 5000 * Mb)]);
        var sessions = new Dictionary<int, int> { [100] = 7, [101] = 7, [200] = 1 };

        var usage = GpuUsageMath.Aggregate(sample, pid => sessions.TryGetValue(pid, out var s) ? s : null);

        var adapter = usage.Adapters[Luid];
        Assert.Equal(74, adapter.UtilizationPercent); // 30 + 2 + 40 + 1 + 1 on 3D
        Assert.Equal(20, adapter.EncoderPercent);
        Assert.Equal(5000, adapter.VramUsedMb);

        var desk = usage.Sessions[7];
        Assert.Equal(32, desk.GpuPercent);
        Assert.Equal(20, desk.EncoderPercent);
        Assert.Equal(1800, desk.VramMb);

        var console = usage.Sessions[1];
        Assert.Equal(40, console.GpuPercent);
        Assert.Equal(0, console.EncoderPercent);
        Assert.Equal(2000, console.VramMb);
        Assert.Equal(2, usage.Sessions.Count);
    }

    [Fact]
    public void Load_is_capped_at_100_and_an_adapter_without_an_encoder_reports_none()
    {
        var sample = new GpuRawSample([Engine(1, 0, "3D", 70), Engine(2, 0, "3D", 60)], [], []);

        var usage = GpuUsageMath.Aggregate(sample, _ => 3);

        Assert.Equal(100, usage.Adapters[Luid].UtilizationPercent);
        Assert.Null(usage.Adapters[Luid].EncoderPercent);
        Assert.Null(usage.Adapters[Luid].VramUsedMb);
        Assert.Equal(100, usage.Sessions[3].GpuPercent);
        Assert.Null(usage.Sessions[3].EncoderPercent);
        Assert.Equal(0, usage.Sessions[3].VramMb);
    }

    [Fact]
    public async Task Monitor_samples_only_after_a_read_and_shows_nothing_until_engines_report()
    {
        var source = new FakeGpuCounterSource();
        using var monitor = new GpuUsageMonitor(source, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<GpuUsageMonitor>.Instance);
        await monitor.StartAsync(CancellationToken.None);
        try
        {
            Assert.Null(monitor.Current());

            // The first collection after opening carries memory only: still nothing to show.
            source.Sample = new GpuRawSample([], [], [new(Luid, 1024 * 1024)]);
            await Task.Delay(1500);
            Assert.Null(monitor.Current());

            source.Sample = new GpuRawSample([Engine(1, 0, "3D", 25)], [], [new(Luid, 1024 * 1024)]);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            GpuUsageSnapshot? snapshot;
            while ((snapshot = monitor.Current()) is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.NotNull(snapshot);
            Assert.Equal(25, snapshot.Adapters[Luid].UtilizationPercent);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Processes_carry_their_render_load_and_vram_but_not_their_video_engines()
    {
        const double Mb = 1024 * 1024;
        var sample = new GpuRawSample(
            [Engine(10, 0, "3D", 40), Engine(10, 1, "Copy", 5), Engine(20, 5, "VideoEncode", 30), Engine(20, 6, "VideoDecode", 10),
             Engine(30, 0, "3D", 99)],
            [new(10, Luid, 512 * Mb), new(20, Luid, 100 * Mb)],
            [new(Luid, 1024 * Mb)]);
        var snapshot = GpuUsageMath.Aggregate(sample, pid => pid == 30 ? null : 7);

        Assert.Equal(2, snapshot.Processes.Count);
        Assert.Equal(new GpuProcessUsage(10, 7, 40, 512), snapshot.Processes[0]);
        Assert.Equal(new GpuProcessUsage(20, 7, 0, 100), snapshot.Processes[1]);
    }

    [Fact]
    public async Task Metrics_carry_gpu_encoder_and_vram_per_adapter_and_per_desk()
    {
        await using var svc = await TestService.StartAsync();
        var gpus = svc.App.Services.GetRequiredService<FakeGpuCounterSource>();
        const double Mb = 1024 * 1024;
        gpus.Sessions[100] = 7; // the fake seat's session
        gpus.Sessions[200] = 1; // the console
        gpus.Sample = new GpuRawSample(
            [Engine(100, 0, "3D", 34), Engine(100, 5, "VideoEncode", 12), Engine(200, 0, "3D", 50)],
            [new(100, Luid, 1200 * Mb), new(200, Luid, 800 * Mb)],
            [new(Luid, 2000 * Mb)]);

        await svc.CreateDeskAsync("desk-1");
        await svc.CreateDeskAsync("desk-2");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        Core.Api.MetricsDto metrics;
        while (true)
        {
            metrics = (await svc.Client.GetFromJsonAsync("/api/v1/metrics", SplitPlayJsonContext.Default.MetricsDto))!;
            if (metrics.Desks.Single(d => d.Id == "desk-1").GpuPercent is not null || DateTime.UtcNow > deadline)
            {
                break;
            }

            await Task.Delay(100);
        }

        var running = metrics.Desks.Single(d => d.Id == "desk-1");
        Assert.Equal(34, running.GpuPercent);
        Assert.Equal(12, running.EncoderPercent);
        Assert.Equal(1200, running.VramMb);

        // A stopped desk has no session: nothing to show.
        var stopped = metrics.Desks.Single(d => d.Id == "desk-2");
        Assert.Null(stopped.GpuPercent);
        Assert.Null(stopped.VramMb);

        var raw = await svc.Client.GetStringAsync("/api/v1/metrics");
        Assert.Contains("\"gpuPercent\":34", raw, StringComparison.Ordinal);
        Assert.Contains("\"vramMb\":1200", raw, StringComparison.Ordinal);
    }
}
