// SPDX-License-Identifier: MIT
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Core.Streaming;
using SplitPlay.Service.Platform;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Tests;

/// <summary>"Адаптивный поток": loss telemetry from the worker drives FEC, the bitrate cap and the network share.</summary>
public sealed class AdaptiveStreamTests
{
    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static Dictionary<string, JsonElement> Metrics(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static async Task<TestService> StartAsync(double packetLossPct, NetworkLink? link = null)
    {
        var svc = await TestService.StartAsync(setup: l =>
        {
            l.SimulateClient = true;
            l.Customize = (r, o) => r.Role == IpcProtocol.Roles.Worker ? o with { SimulatedPacketLossPct = packetLossPct } : o;
        });
        // The runner's own adapters must not decide the tests.
        svc.App.Services.GetRequiredService<DeskSupervisor>().FindLink = address => address == "192.168.1.42" ? link : null;
        return svc;
    }

    private static List<IpcProfile> Pushed(TestService svc) =>
        svc.Launcher.Received.Where(m => m.Role != IpcProtocol.Roles.Seat).Select(m => m.Message).OfType<SetProfileMessage>()
            .Select(m => m.Profile!).ToList();

    [Fact]
    public async Task Heavy_loss_raises_fec_and_caps_the_bitrate_live()
    {
        await using var svc = await StartAsync(packetLossPct: 8);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");

        var welcome = svc.Launcher.Received.Select(m => m.Message).OfType<WelcomeMessage>().First(w => w.Rdp is null);
        Assert.Equal((20, 0), (welcome.Profile!.FecPercent, welcome.Profile.BitrateCapKbps));

        var limited = await svc.WaitForAsync("desk-1", d => d.Adaptive is { Limit: "loss" }, what: "a loss-limited bitrate");
        Assert.InRange(limited.Adaptive!.FecPercent, 25, StreamAdaptation.MaxFecPercent);
        Assert.InRange(limited.Adaptive.BitrateCapKbps ?? 0, 10_000, 49_999);
        Assert.Equal(8.0, limited.Metrics.PacketLossPct);
        Assert.NotNull(limited.Metrics.EncoderKbps);

        // The worker got the new FEC share and the cap.
        var pushed = Pushed(svc);
        Assert.Contains(pushed, p => p.FecPercent > 20);
        Assert.Contains(pushed, p => p.BitrateCapKbps is > 0 and < 50_000);

        var json = await svc.Client.GetStringAsync("/api/v1/desks/desk-1");
        Assert.Contains("\"adaptive\":{\"fecPercent\":", json, StringComparison.Ordinal);
        Assert.Contains("\"packetLossPct\":8", json, StringComparison.Ordinal);

        // Turned off: the desk's own values at once, nothing adapts any more.
        var put = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"profile":{"adaptiveStream":false}}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var off = await svc.WaitForAsync("desk-1", d => d.Adaptive is null, what: "adaptation off");
        Assert.False(off.Profile.AdaptiveStreamEnabled);
        var last = Pushed(svc)[^1];
        Assert.Equal((20, 0), (last.FecPercent, last.BitrateCapKbps));
        var count = Pushed(svc).Count;
        await Task.Delay(500);
        Assert.Equal(count, Pushed(svc).Count);

        await svc.PostDeskActionAsync("desk-1", "stop");
        var stopped = await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.Null(stopped.Adaptive);
    }

    [Fact]
    public async Task A_clean_link_brings_fec_down_and_never_caps()
    {
        await using var svc = await StartAsync(packetLossPct: 0);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");

        // 100 ms heartbeats: one step every second.
        var low = await svc.WaitForAsync("desk-1", d => d.Adaptive is { FecPercent: StreamAdaptation.MinFecPercent }, what: "FEC at its minimum");
        Assert.Null(low.Adaptive!.BitrateCapKbps);
        Assert.Equal("none", low.Adaptive.Limit);
        Assert.Contains(Pushed(svc), p => p.FecPercent == StreamAdaptation.MinFecPercent && p.BitrateCapKbps == 0);
        await svc.WaitForAsync("desk-1", d => d.Metrics.FecPercent == StreamAdaptation.MinFecPercent, what: "the worker at 5 % FEC");
    }

    [Fact]
    public async Task A_fixed_fec_share_is_kept()
    {
        await using var svc = await StartAsync(packetLossPct: 8);
        await svc.CreateDeskAsync("desk-1", ""","profile":{"fecPercent":10}""");
        await svc.PostDeskActionAsync("desk-1", "start");

        var limited = await svc.WaitForAsync("desk-1", d => d.Adaptive is { Limit: "loss" }, what: "a loss-limited bitrate");
        Assert.Equal(10, limited.Adaptive!.FecPercent);
        Assert.All(Pushed(svc), p => Assert.Equal(10, p.FecPercent));
    }

    [Fact]
    public async Task Desks_share_a_slow_network_adapter()
    {
        await using var svc = await StartAsync(packetLossPct: 0, new NetworkLink("eth-slow", 30_000_000));
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");

        // 80 % of 30 Mbit/s for a client that asks for 50.
        var shared = await svc.WaitForAsync("desk-1", d => d.Adaptive is { Limit: "network" }, what: "the network share");
        Assert.Equal(24_000, shared.Adaptive!.BitrateCapKbps);
        Assert.Contains(Pushed(svc), p => p.BitrateCapKbps == 24_000);
    }

    [Fact]
    public async Task An_older_worker_without_telemetry_keeps_the_desk_values()
    {
        await using var svc = await TestService.StartAsync(setup: l => l.SimulateClient = true);
        svc.App.Services.GetRequiredService<DeskSupervisor>().FindLink = _ => null;
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");

        var streaming = await svc.WaitForAsync("desk-1", d => d.State == DeskState.Streaming && d.Metrics.Fps > 0, what: "streaming");
        await Task.Delay(1500);
        var desk = await svc.GetDeskAsync("desk-1");
        Assert.Equal(new DeskAdaptiveDto { FecPercent = 20, BitrateCapKbps = null, Limit = "none" }, desk.Adaptive);
        Assert.Null(streaming.Metrics.PacketLossPct);
        Assert.Empty(Pushed(svc));
    }

    [Fact]
    public void Telemetry_keys_are_mapped_and_the_cap_is_sent_as_zero_when_lifted()
    {
        var dto = DeskRuntime.MapMetrics(Metrics(
            """{"packet_loss_pct":0.42,"burst_loss_pct":12.5,"fec_recovered":7,"fec_failed":1,"fec_percent":15,"encoder_kbps":38000}"""));
        Assert.Equal((0.42, 12.5, 7.0, 1.0, 15.0, 38_000.0),
            (dto.PacketLossPct, dto.BurstLossPct, dto.FecRecovered, dto.FecFailed, dto.FecPercent, dto.EncoderKbps));
        Assert.Null(dto.Extra);
        Assert.Null(DeskRuntime.MapMetrics(Metrics("""{"encoder_kbps":null}""")).EncoderKbps);

        var profile = new ProfileConfig { FecPercent = 10 };
        Assert.Equal((10, 0), (DeskRuntime.ToIpcProfile(profile).FecPercent, DeskRuntime.ToIpcProfile(profile).BitrateCapKbps));
        var adapted = DeskRuntime.ToIpcProfile(profile, new AdaptiveState(30, 25_000, AdaptiveLimit.Loss));
        Assert.Equal((30, 25_000), (adapted.FecPercent, adapted.BitrateCapKbps));
        Assert.Equal(0, DeskRuntime.ToIpcProfile(profile, new AdaptiveState(30, null, AdaptiveLimit.None)).BitrateCapKbps);

        var json = IpcCodec.EncodeToString(new SetProfileMessage { Id = 1, Profile = adapted });
        Assert.Contains("\"bitrate_cap_kbps\":25000", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_subnet_compares_the_prefix_only()
    {
        Assert.True(NetworkLinks.SameSubnet(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.1.42"), 24));
        Assert.False(NetworkLinks.SameSubnet(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.2.42"), 24));
        Assert.True(NetworkLinks.SameSubnet(IPAddress.Parse("10.1.130.1"), IPAddress.Parse("10.1.150.9"), 18));
        Assert.False(NetworkLinks.SameSubnet(IPAddress.Parse("10.1.130.1"), IPAddress.Parse("10.1.200.9"), 18));
        Assert.False(NetworkLinks.SameSubnet(IPAddress.Parse("fe80::1"), IPAddress.Parse("192.168.1.42"), 0));
        Assert.True(NetworkLinks.SameSubnet(IPAddress.Parse("fd00::1"), IPAddress.Parse("fd00::abcd"), 64));
        Assert.Null(new NetworkLinks(TimeProvider.System).Find("127.0.0.1"));
        Assert.Null(new NetworkLinks(TimeProvider.System).Find("not an address"));
    }
}
