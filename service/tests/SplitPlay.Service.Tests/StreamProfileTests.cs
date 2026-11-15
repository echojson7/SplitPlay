// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Json;
using System.Text;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Platform;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Tests;

/// <summary>The stream settings the worker applies (FEC, minimum fps, DPB, pacing): defaults and live changes.</summary>
public sealed class StreamProfileTests
{
    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public void Unset_stream_settings_are_sent_as_the_service_defaults()
    {
        var p = DeskRuntime.ToIpcProfile(new ProfileConfig { Fps = 120 });
        Assert.Equal(ProfileConfig.DefaultFecPercent, p.FecPercent);
        Assert.Equal(20, p.MinFps);
        Assert.Equal(0, p.DpbFrames); // the codec default; a larger DPB is opt-in
        Assert.Equal(800, p.PacingMbps);
        Assert.Equal("slot", p.CaptureMode); // arrival is opt-in until the lab measures it
        Assert.Equal("arrival", DeskRuntime.ToIpcProfile(new ProfileConfig { CaptureMode = "arrival" }).CaptureMode);

        // The idle rate never exceeds the desk's frame rate; set values pass through.
        Assert.Equal(10, DeskRuntime.ToIpcProfile(new ProfileConfig { Fps = 10 }).MinFps);
        var set = DeskRuntime.ToIpcProfile(new ProfileConfig { Fps = 60, FecPercent = 5, MinFps = 30, DpbFrames = 4, PacingMbps = 2500 });
        Assert.Equal((5, 30, 4, 2500), (set.FecPercent, set.MinFps, set.DpbFrames, set.PacingMbps));
    }

    [Fact]
    public async Task Welcome_carries_the_defaults_and_a_cleared_value_goes_back_to_its_default_live()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        var welcome = svc.Launcher.Received.Select(m => m.Message).OfType<WelcomeMessage>().First(w => w.Rdp is null);
        Assert.Equal((20, 20, 0, 800), (welcome.Profile!.FecPercent, welcome.Profile.MinFps, welcome.Profile.DpbFrames, welcome.Profile.PacingMbps));

        var put = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"profile":{"fps":120,"pacingMbps":400,"minFps":10}}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var first = await NextSetProfileAsync(svc, 0);
        Assert.Equal((400, 10), (first.Profile!.PacingMbps, first.Profile.MinFps));

        put = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"profile":{"fps":120}}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var second = await NextSetProfileAsync(svc, 1);
        Assert.Equal((800, 20), (second.Profile!.PacingMbps, second.Profile.MinFps));
    }

    [Fact]
    public async Task Capture_mode_reaches_a_running_worker_live()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        var welcome = svc.Launcher.Received.Select(m => m.Message).OfType<WelcomeMessage>().First(w => w.Rdp is null);
        Assert.Equal("slot", welcome.Profile!.CaptureMode);

        var put = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"profile":{"captureMode":"arrival"}}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("arrival", (await NextSetProfileAsync(svc, 0)).Profile!.CaptureMode);

        var bad = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"profile":{"captureMode":"soon"}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Rdp_input_is_opt_in_and_reaches_a_running_worker_live()
    {
        Assert.Equal("windows", DeskRuntime.ToIpcProfile(new ProfileConfig()).Input); // RDP input waits for lab E
        Assert.Equal("rdp", DeskRuntime.ToIpcProfile(new ProfileConfig { Input = "rdp" }).Input);

        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        var welcome = svc.Launcher.Received.Select(m => m.Message).OfType<WelcomeMessage>().First(w => w.Rdp is null);
        Assert.Equal("windows", welcome.Profile!.Input);

        var put = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"profile":{"input":"rdp"}}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("rdp", (await NextSetProfileAsync(svc, 0)).Profile!.Input);

        var bad = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"profile":{"input":"usb"}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Ctrl_alt_del_goes_to_the_seat()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        var stopped = await svc.Client.PostAsync("/api/v1/desks/desk-1/sas", null);
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);

        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var sent = await svc.Client.PostAsync("/api/v1/desks/desk-1/sas", null);
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        Assert.Contains(svc.Launcher.Received, m => m.Message is SendSasMessage);
        Assert.Equal(HttpStatusCode.NotFound, (await svc.Client.PostAsync("/api/v1/desks/nope/sas", null)).StatusCode);
    }

    [Fact]
    public void The_input_path_metric_is_a_desk_metric()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"input":"rdp_mixed","input_fallbacks":0}""");
        var metrics = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var dto = DeskRuntime.MapMetrics(metrics);
        Assert.Equal("rdp_mixed", dto.Input);
        Assert.True(dto.Extra!.ContainsKey("input_fallbacks"));
        Assert.Null(DeskRuntime.MapMetrics(new Dictionary<string, System.Text.Json.JsonElement>()).Input);
    }

    [Fact]
    public void Stage_times_and_frame_sizes_are_desk_metrics()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"capture_ms_p50":1.1,"capture_ms_p99":2.2,"encoder_ms_p50":3.3,"encoder_ms_p99":4.4,"queue_ms_p50":0,"queue_ms_p99":0.1,
             "send_ms_p50":0.8,"send_ms_p99":1.9,"sent_ms_p50":5.5,"sent_ms_p99":8.1,"sent_ms_max":12.5,"frame_kb_avg":40.2,
             "frame_kb_max":310,"key_frames":1,"key_frame_kb_max":310,"repeated_frames":3}
            """);
        var metrics = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var dto = DeskRuntime.MapMetrics(metrics);
        Assert.Null(dto.Extra);
        Assert.Equal(2.2, dto.CaptureMsP99);
        Assert.Equal(4.4, dto.EncoderMsP99);
        Assert.Equal(0.1, dto.QueueMsP99);
        Assert.Equal(1.9, dto.SendMsP99);
        Assert.Equal(8.1, dto.SentMsP99);
        Assert.Equal(12.5, dto.SentMsMax);
        Assert.Equal(310, dto.KeyFrameKbMax);
        Assert.Equal(3, dto.RepeatedFrames);

        using var nulls = System.Text.Json.JsonDocument.Parse("""{"capture_ms_p99":null}""");
        Assert.Null(DeskRuntime.MapMetrics(nulls.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())).CaptureMsP99);
    }

    [Fact]
    public void Cpu_load_comes_from_the_processor_time_deltas()
    {
        var before = new CpuTimes[] { new(100, 200), new(50, 100), new(0, 0) };
        var after = new CpuTimes[] { new(150, 300), new(50, 200), new(0, 0) };
        var load = CpuLoadSampler.Load(before, after)!;
        Assert.Equal([50.0, 100.0, 0.0], load.PerCpu);
        Assert.Equal(75.0, load.TotalPercent);
        Assert.Equal(100.0, load.MaxOf(null));
        Assert.Equal(50.0, load.MaxOf([0, 2, 9]));
        Assert.Null(load.MaxOf([9]));
        Assert.Null(CpuLoadSampler.Load(before, after[..2]));

        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var reads = new Queue<IReadOnlyList<CpuTimes>?>([before, after]);
        var sampler = new CpuLoadSampler(() => reads.Dequeue(), time);
        Assert.Null(sampler.Current());
        // Asked again within the interval: the previous result, no new read.
        Assert.Null(sampler.Current());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(75.0, sampler.Current()!.TotalPercent);
    }

    [Fact]
    public void A_link_describes_itself_in_one_line()
    {
        Assert.Equal("Ethernet Realtek 2500 Mbit/s", new LinkStatus("Realtek", "ethernet", 2500, 0, 0).Describe());
        Assert.Equal("Wi-Fi Intel AX210 ch 36 ax 1201 Mbit/s",
            new LinkStatus("Intel AX210", "wifi", 1201, 0, 0, 80, -55, 1201, 960, 36, "ax").Describe());
    }

    [Fact]
    public async Task Auto_composition_follows_the_fastest_desk()
    {
        await using var svc = await TestService.StartAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!svc.Platform.Actions.Contains("compose 4"))
        {
            Assert.True(DateTime.UtcNow < deadline, "Auto was not applied at start-up: " + string.Join(", ", svc.Platform.Actions));
            await Task.Delay(20);
        }

        await svc.CreateDeskAsync("desk-1", ""","profile":{"fps":240}""");
        Assert.Contains("compose 2", svc.Platform.Actions);
        var metrics = await svc.Client.GetFromJsonAsync("/api/v1/metrics", Core.Json.SplitPlayJsonContext.Default.MetricsDto);
        Assert.Equal(500, metrics!.Host.ComposeHz);

        // A fixed value wins over Auto.
        var put = await svc.Client.PutAsync("/api/v1/host",
            JsonBody("""{"webPort":38299,"lanAccess":false,"basePortStart":48089,"basePortStride":100,"composeIntervalMs":3,"logLevel":"info"}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("compose 3", svc.Platform.Actions.Last(a => a.StartsWith("compose ", StringComparison.Ordinal)));
    }

    private static async Task<SetProfileMessage> NextSetProfileAsync(TestService svc, int index)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var pushed = svc.Launcher.Received.Select(m => m.Message).OfType<SetProfileMessage>().ToList();
            if (pushed.Count > index)
            {
                return pushed[index];
            }

            Assert.True(DateTime.UtcNow < deadline, "the profile was not pushed to the worker");
            await Task.Delay(25);
        }
    }
}
