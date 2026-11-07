// SPDX-License-Identifier: MIT
using System.Text.Json;
using SplitPlay.Core.Api;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;

namespace SplitPlay.Core.Tests;

public sealed class TrayDesksTests
{
    private const string Desks =
        """
        [{"id":"desk-1","name":"Аня","state":"Ready","pinRequest":{"address":"192.168.1.37","since":"2026-11-06T21:00:00+00:00"},"future":1},
         {"id":"desk-2","name":null,"state":"Stopped","pinRequest":null},
         {"id":"desk-3","name":"","state":"SomethingNew"},
         {"name":"no id"},
         7]
        """;

    [Fact]
    public void The_desk_list_is_parsed_leniently()
    {
        var desks = TrayDesks.Parse(Desks);

        Assert.Equal(["desk-1", "desk-2", "desk-3"], desks.Select(d => d.Id));
        Assert.Equal(["Аня", "desk-2", "desk-3"], desks.Select(d => d.Name));
        Assert.Equal(new DeskState?[] { DeskState.Ready, DeskState.Stopped, null }, desks.Select(d => d.State));
        Assert.False(desks[2].CanStart);
        Assert.False(desks[2].CanStop);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 21, 0, 0, TimeSpan.Zero), desks[0].PinSince);
        Assert.Null(desks[1].PinSince);
        Assert.Throws<JsonException>(() => TrayDesks.Parse("""{"error":{}}"""));
    }

    [Fact]
    public void Start_and_stop_follow_the_state()
    {
        TrayDesk Desk(DeskState s) => new("d", "d", s, null);
        Assert.True(Desk(DeskState.Stopped).CanStart);
        Assert.True(Desk(DeskState.Defined).CanStart);
        Assert.False(Desk(DeskState.Stopped).CanStop);
        Assert.True(Desk(DeskState.Streaming).CanStop);
        Assert.True(Desk(DeskState.Faulted).CanStop);
        Assert.False(Desk(DeskState.Faulted).CanStart);
        Assert.False(Desk(DeskState.Stopping).CanStop);
        Assert.False(Desk(DeskState.Stopping).CanStart);
    }

    [Fact]
    public void Only_new_pin_requests_are_reported()
    {
        var t = new DateTimeOffset(2026, 11, 6, 21, 0, 0, TimeSpan.Zero);
        TrayDesk Desk(string id, DateTimeOffset? since) => new(id, id, DeskState.Ready, since);

        Assert.Equal(["a"], TrayDesks.NewPinRequests(null, [Desk("a", t), Desk("b", null)]).Select(d => d.Id));
        Assert.Empty(TrayDesks.NewPinRequests([Desk("a", t)], [Desk("a", t)]));
        Assert.Equal(["a"], TrayDesks.NewPinRequests([Desk("a", t)], [Desk("a", t.AddSeconds(30))]).Select(d => d.Id));
        Assert.Equal(["a"], TrayDesks.NewPinRequests([Desk("a", null)], [Desk("a", t)]).Select(d => d.Id));
        Assert.Empty(TrayDesks.NewPinRequests([Desk("a", t)], [Desk("a", null)]));
    }

    [Fact]
    public void A_worker_heartbeat_with_a_waiting_client_decodes()
    {
        var heartbeat = Assert.IsType<HeartbeatMessage>(IpcCodec.Decode(
            """{"type":"heartbeat","seq":3,"state":"idle","metrics":{},"sessions":[],"pairing":{"address":"192.168.1.37","since":"2026-11-06T21:00:00Z"}}"""));
        var request = IpcPinRequest.FromJson(heartbeat.Pairing);
        Assert.Equal("192.168.1.37", request!.Address);

        var idle = Assert.IsType<HeartbeatMessage>(IpcCodec.Decode("""{"type":"heartbeat","seq":4,"state":"idle","pairing":null}"""));
        Assert.Null(IpcPinRequest.FromJson(idle.Pairing));
        var old = Assert.IsType<HeartbeatMessage>(IpcCodec.Decode("""{"type":"heartbeat","seq":5,"state":"idle"}"""));
        Assert.Null(IpcPinRequest.FromJson(old.Pairing));
    }
}
