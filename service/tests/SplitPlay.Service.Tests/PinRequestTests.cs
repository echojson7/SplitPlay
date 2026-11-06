// SPDX-License-Identifier: MIT
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Tests;

/// <summary>A Moonlight client waiting for its PIN: heartbeat <c>pairing</c> → <c>desk.pinRequest</c> (patch 0021).</summary>
public sealed class PinRequestTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static HeartbeatMessage Beat(long seq, string? pairing) => new()
    {
        Seq = seq,
        State = "idle",
        Sessions = Json("[]"),
        Pairing = pairing is null ? null : Json(pairing),
    };

    private static DeskRuntime Runtime() =>
        new(new DeskConfig { Id = "desk-1" }, new DeskStateMachine(TimeProvider.System, new RecoveryPolicy()));

    [Fact]
    public void Pairing_is_parsed_leniently()
    {
        Assert.Null(IpcPinRequest.FromJson(null));
        Assert.Null(IpcPinRequest.FromJson(Json("null")));
        Assert.Null(IpcPinRequest.FromJson(Json("true")));
        Assert.Equal(new IpcPinRequest(null, null), IpcPinRequest.FromJson(Json("""{"address":7,"since":"soon"}""")));
        Assert.Equal(
            new IpcPinRequest("192.168.1.37", new DateTimeOffset(2026, 11, 6, 21, 0, 0, TimeSpan.Zero)),
            IpcPinRequest.FromJson(Json("""{"address":"192.168.1.37","since":"2026-11-06T21:00:00Z"}""")));
    }

    [Fact]
    public async Task Heartbeats_set_and_clear_the_request()
    {
        await using var connection = new IpcConnection(new MemoryStream());
        var rt = Runtime();
        rt.Connection = connection;
        var t0 = DateTimeOffset.UnixEpoch;

        Assert.Equal(WorkerUpdate.Applied, rt.ApplyHeartbeat(connection, Beat(1, null), t0));
        Assert.Null(rt.ToDto().PinRequest);

        const string Waiting = """{"address":"192.168.1.37","since":"2026-11-06T21:00:00Z"}""";
        Assert.Equal(WorkerUpdate.Publish, rt.ApplyHeartbeat(connection, Beat(2, Waiting), t0.AddSeconds(1)));
        var request = rt.ToDto().PinRequest;
        Assert.NotNull(request);
        Assert.Equal("192.168.1.37", request.Address);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 21, 0, 0, TimeSpan.Zero), request.Since);

        // The same request again changes nothing; its end is published.
        Assert.Equal(WorkerUpdate.Applied, rt.ApplyHeartbeat(connection, Beat(3, Waiting), t0.AddSeconds(2)));
        Assert.Equal(WorkerUpdate.Publish, rt.ApplyHeartbeat(connection, Beat(4, "null"), t0.AddSeconds(3)));
        Assert.Null(rt.ToDto().PinRequest);
    }

    [Fact]
    public async Task A_request_without_a_time_keeps_the_time_it_was_first_seen()
    {
        await using var connection = new IpcConnection(new MemoryStream());
        var rt = Runtime();
        rt.Connection = connection;
        var t0 = DateTimeOffset.UnixEpoch.AddHours(1);

        rt.ApplyHeartbeat(connection, Beat(1, """{"address":"10.0.0.5"}"""), t0);
        Assert.Equal(WorkerUpdate.Applied, rt.ApplyHeartbeat(connection, Beat(2, """{"address":"10.0.0.5"}"""), t0.AddSeconds(1)));
        Assert.Equal(t0, rt.ToDto().PinRequest!.Since);
    }

    [Fact]
    public async Task A_delivered_pin_is_not_brought_back_by_an_older_heartbeat()
    {
        await using var connection = new IpcConnection(new MemoryStream());
        var rt = Runtime();
        rt.Connection = connection;
        var t0 = DateTimeOffset.UnixEpoch;
        const string Waiting = """{"address":"192.168.1.37","since":"2026-11-06T21:00:00Z"}""";
        rt.ApplyHeartbeat(connection, Beat(1, Waiting), t0);

        Assert.True(rt.ClearPinRequest(t0.AddSeconds(1)));
        Assert.False(rt.ClearPinRequest(t0.AddSeconds(1)));

        // A heartbeat written before the worker took the PIN still reports it: ignored for a few seconds.
        Assert.Equal(WorkerUpdate.Applied, rt.ApplyHeartbeat(connection, Beat(2, Waiting), t0.AddSeconds(2)));
        Assert.Null(rt.ToDto().PinRequest);

        // A new request (another time) shows at once.
        Assert.Equal(WorkerUpdate.Publish, rt.ApplyHeartbeat(connection, Beat(3, """{"address":"192.168.1.37","since":"2026-11-06T21:00:03Z"}"""), t0.AddSeconds(3)));
        Assert.NotNull(rt.ToDto().PinRequest);
    }

    [Fact]
    public async Task A_waiting_client_shows_on_the_desk_until_the_pin_is_sent()
    {
        await using var svc = await TestService.StartAsync(setup: l => l.SimulatePinRequest = true);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");

        var waiting = await svc.WaitForAsync("desk-1", d => d.PinRequest is not null, what: "a PIN request");
        Assert.Equal("192.168.1.42", waiting.PinRequest!.Address);

        var json = await svc.Client.GetStringAsync("/api/v1/desks/desk-1");
        Assert.Contains("\"pinRequest\":{\"address\":\"192.168.1.42\",\"since\":", json, StringComparison.Ordinal);

        using var body = new StringContent("""{"pin":"1234","name":"Laptop"}""", Encoding.UTF8, "application/json");
        var pair = await svc.Client.PostAsync("/api/v1/desks/desk-1/pair", body);
        Assert.Equal(HttpStatusCode.NoContent, pair.StatusCode);

        // Cleared at once by the pair, and the worker no longer reports it.
        Assert.Null(svc.App.Services.GetRequiredService<DeskSupervisor>().Get("desk-1").PinRequest);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.Null(svc.App.Services.GetRequiredService<DeskSupervisor>().Get("desk-1").PinRequest);

        await svc.PostDeskActionAsync("desk-1", "stop");
        var stopped = await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.Null(stopped.PinRequest);
        Assert.Contains("\"pinRequest\":null", await svc.Client.GetStringAsync("/api/v1/desks/desk-1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_the_desk_drops_a_waiting_request()
    {
        await using var svc = await TestService.StartAsync(setup: l => l.SimulatePinRequest = true);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForAsync("desk-1", d => d.PinRequest is not null, what: "a PIN request");

        await svc.PostDeskActionAsync("desk-1", "stop");
        var stopped = await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.Null(stopped.PinRequest);
    }
}
