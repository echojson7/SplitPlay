// SPDX-License-Identifier: MIT
using System.Text.Json;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Tests;

/// <summary>The connected Moonlight device and live stream stats: heartbeat metrics, sessions and client events.</summary>
public sealed class LiveStatsTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static IpcStreamSession Session(long id, string address = "192.168.1.42", string started = "2026-10-29T12:00:00Z") =>
        IpcStreamSession.FromJson(Json($$"""
            {"id":{{id}},"client":"Laptop","client_uuid":"u-{{id}}","address":"{{address}}","width":1920,"height":1080,
             "fps_requested":120,"codec":"hevc","bitrate_kbps_requested":50000,"hdr":false,"started_at":"{{started}}"}
            """))!;

    private static DeskRuntime Runtime() =>
        new(new DeskConfig { Id = "desk-1" }, new DeskStateMachine(TimeProvider.System, new RecoveryPolicy()));

    [Fact]
    public void Heartbeat_metric_keys_are_camel_cased_and_unknown_keys_pass_through()
    {
        var metrics = new Dictionary<string, JsonElement>
        {
            ["fps"] = Json("119.8"),
            ["bitrate_kbps"] = Json("41250"),
            ["encode_ms_p50"] = Json("3.4"),
            ["encode_ms_p99"] = Json("6.1"),
            ["loss_pct"] = Json("0.5"),
            ["rtt_ms"] = Json("4"),
            ["clients"] = Json("1"),
            ["future_key"] = Json("\"x\""),
        };

        var dto = DeskRuntime.MapMetrics(metrics);

        Assert.Equal(119.8, dto.Fps);
        Assert.Equal(41250, dto.BitrateKbps);
        Assert.Equal(3.4, dto.EncodeMsP50);
        Assert.Equal(6.1, dto.EncodeMsP99);
        Assert.Equal(0.5, dto.LossPct);
        Assert.Equal(4, dto.RttMs);
        Assert.Equal(1, dto.Clients);
        Assert.Equal(["future_key"], dto.Extra!.Keys);

        var json = JsonSerializer.Serialize(DeskMetricsEntryDto.From("desk-1", dto), Core.Json.SplitPlayJsonContext.Default.DeskMetricsEntryDto);
        Assert.Contains("\"encodeMsP50\":3.4", json, StringComparison.Ordinal);
        Assert.Contains("\"lossPct\":0.5", json, StringComparison.Ordinal);
        Assert.Contains("\"rttMs\":4", json, StringComparison.Ordinal);
        Assert.DoesNotContain("_", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Events_add_and_remove_streams_and_the_desk_shows_the_oldest()
    {
        var rt = Runtime();
        Assert.Null(rt.ToDto().Stream);

        Assert.True(rt.AddStream(Session(2, "192.168.1.50", "2026-10-29T12:05:00Z")));
        Assert.True(rt.AddStream(Session(1)));
        Assert.False(rt.AddStream(Session(1)));

        var stream = rt.ToDto().Stream!;
        Assert.Equal("Laptop", stream.Client);
        Assert.Equal("u-1", stream.ClientId);
        Assert.Equal("192.168.1.42", stream.Address);
        Assert.Equal((1920, 1080, 120), (stream.Width, stream.Height, stream.Fps));
        Assert.Equal("hevc", stream.Codec);
        Assert.Equal(50000, stream.BitrateKbps);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), stream.StartedAt);

        Assert.True(rt.RemoveStream(Session(1)));
        Assert.Equal("192.168.1.50", rt.ToDto().Stream!.Address);
        Assert.True(rt.RemoveStream(Session(2)));
        Assert.Null(rt.ToDto().Stream);
        Assert.False(rt.RemoveStream(Session(2)));
    }

    [Fact]
    public void A_heartbeat_replaces_the_list_but_never_revives_an_ended_stream()
    {
        var rt = Runtime();
        rt.AddStream(Session(1));
        rt.RemoveStream(Session(1));

        // Written by the worker just before the stream ended, read after the event.
        Assert.False(rt.ReplaceStreams([Session(1)]));
        Assert.Empty(rt.Streams);

        Assert.True(rt.ReplaceStreams([Session(3)]));
        Assert.False(rt.ReplaceStreams([Session(3)]));
        Assert.True(rt.ReplaceStreams([]));
        Assert.Empty(rt.Streams);
    }

    [Fact]
    public void Clearing_drops_every_stream()
    {
        var rt = Runtime();
        rt.AddStream(Session(1));
        rt.AddStream(Session(2, "10.0.0.2"));
        Assert.True(rt.ClearStreams());
        Assert.False(rt.ClearStreams());
        Assert.Null(rt.ToDto().Stream);
    }

    [Fact]
    public async Task A_streaming_fake_worker_shows_its_device_and_live_metrics()
    {
        await using var svc = await TestService.StartAsync(setup: l => l.SimulateClient = true);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");

        var streaming = await svc.WaitForAsync("desk-1",
            d => d.State == DeskState.Streaming && d.Stream is not null && d.Metrics.Fps > 0, what: "streaming with metrics");
        var stream = streaming.Stream!;
        Assert.Equal("Laptop", stream.Client);
        Assert.Equal("192.168.1.42", stream.Address);
        Assert.Equal((1920, 1080, 120), (stream.Width, stream.Height, stream.Fps));
        Assert.Equal("hevc", stream.Codec);
        Assert.Equal(50000, stream.BitrateKbps);
        Assert.NotNull(stream.StartedAt);
        Assert.InRange(streaming.Metrics.Fps, 100, 121);
        Assert.InRange(streaming.Metrics.RttMs, 1, 10);
        Assert.InRange(streaming.Metrics.EncodeMsP50, 1, streaming.Metrics.EncodeMsP99);
        Assert.Equal(1, streaming.Metrics.Clients);

        var metrics = await svc.Client.GetStringAsync("/api/v1/metrics");
        Assert.Contains("\"encodeMsP50\":", metrics, StringComparison.Ordinal);
        Assert.Contains("\"lossPct\":", metrics, StringComparison.Ordinal);
        Assert.Contains("\"rttMs\":", metrics, StringComparison.Ordinal);

        await svc.PostDeskActionAsync("desk-1", "stop");
        var stopped = await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.Null(stopped.Stream);
        Assert.Equal(0, stopped.Metrics.Fps);
    }

    [Fact]
    public async Task Client_disconnected_returns_the_desk_to_ready()
    {
        await using var svc = await TestService.StartAsync(setup: l =>
        {
            l.SimulateClient = true;
            l.Customize = (_, o) => o with { SimulatedClientDuration = TimeSpan.FromMilliseconds(600) };
        });
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");

        await svc.WaitForAsync("desk-1", d => d.State == DeskState.Streaming && d.Stream is not null, what: "streaming");
        var ready = await svc.WaitForAsync("desk-1", d => d.State == DeskState.Ready, what: "ready after the client left");
        Assert.Null(ready.Stream);

        // Later heartbeats do not bring it back.
        await Task.Delay(300);
        var later = await svc.GetDeskAsync("desk-1");
        Assert.Equal(DeskState.Ready, later.State);
        Assert.Null(later.Stream);
        Assert.Equal(0, later.Metrics.Clients);
    }

    [Fact]
    public async Task A_worker_restart_clears_the_stream()
    {
        // The worker streams, then hangs: the supervisor kills it and recovers without the old stream.
        await using var svc = await TestService.StartAsync(
            ["--Supervisor:InitialBackoffMs=1000", "--Supervisor:MaxBackoffMs=1000"],
            setup: l =>
            {
                l.SimulateClient = true;
                l.Behavior = r => r.Role == IpcProtocol.Roles.Worker && r.Attempt == 1 ? FakeChildBehavior.StopHeartbeats : FakeChildBehavior.Normal;
            });
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");

        await svc.WaitForAsync("desk-1", d => d.State == DeskState.Streaming && d.Stream is not null, what: "streaming");
        var recovering = await svc.WaitForStateAsync("desk-1", DeskState.Recovering);
        Assert.Null(recovering.Stream);
        Assert.Equal(0, recovering.Metrics.Clients);

        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
    }
}
