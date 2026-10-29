// SPDX-License-Identifier: MIT
using System.Text.Json;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Core.Tests;

public sealed class IpcStreamSessionTests
{
    private const string WorkerHeartbeat =
        """
        {"type":"heartbeat","seq":42,"state":"streaming",
         "metrics":{"fps":119.9,"bitrate_kbps":41250,"encode_ms_p50":3.4,"encode_ms_p99":6.1,"loss_pct":0.2,"rtt_ms":3,"clients":1},
         "sessions":[{"id":1234567890,"client":"Laptop","client_uuid":"0f5c3a52-6a3e-4c1b-9a51-000000000001","address":"192.168.1.42",
                      "width":2560,"height":1440,"fps_requested":120,"codec":"av1","bitrate_kbps_requested":80000,"hdr":true,
                      "started_at":"2026-10-29T12:00:00Z","fps":119.9,"bitrate_kbps":41250,"encode_ms_p50":3.4,"encode_ms_p99":6.1,
                      "loss_pct":0.2,"rtt_ms":3}]}
        """;

    [Fact]
    public void A_worker_heartbeat_decodes_with_its_sessions()
    {
        var heartbeat = Assert.IsType<HeartbeatMessage>(IpcCodec.Decode(WorkerHeartbeat.ReplaceLineEndings("")));
        Assert.Equal(42, heartbeat.Seq);
        Assert.Equal(3, heartbeat.Metrics!["rtt_ms"].GetInt32());

        var session = Assert.Single(IpcStreamSession.ListFromJson(heartbeat.Sessions));
        Assert.Equal(1234567890, session.Id);
        Assert.Equal("Laptop", session.Client);
        Assert.Equal("0f5c3a52-6a3e-4c1b-9a51-000000000001", session.ClientUuid);
        Assert.Equal("192.168.1.42", session.Address);
        Assert.Equal((2560, 1440, 120), (session.Width, session.Height, session.FpsRequested));
        Assert.Equal("av1", session.Codec);
        Assert.Equal(80000, session.BitrateKbpsRequested);
        Assert.True(session.Hdr);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), session.StartedAt);
    }

    [Fact]
    public void A_heartbeat_without_sessions_has_none()
    {
        var heartbeat = Assert.IsType<HeartbeatMessage>(IpcCodec.Decode("""{"type":"heartbeat","seq":1,"state":"idle","metrics":{"clients":0}}"""));
        Assert.Null(heartbeat.Sessions);
        Assert.Empty(IpcStreamSession.ListFromJson(heartbeat.Sessions));
    }

    [Fact]
    public void Fields_of_the_wrong_type_are_treated_as_absent()
    {
        using var doc = JsonDocument.Parse("""
            [{"id":"x","client":null,"client_uuid":7,"address":"10.0.0.5","width":"1920","height":-1,"fps_requested":59.94,
              "codec":"","hdr":1,"started_at":"yesterday"},
             42, "text", {}]
            """);
        var sessions = IpcStreamSession.ListFromJson(doc.RootElement);

        Assert.Equal(2, sessions.Count);
        var odd = sessions[0];
        Assert.Null(odd.Id);
        Assert.Null(odd.Client);
        Assert.Null(odd.ClientUuid);
        Assert.Equal("10.0.0.5", odd.Address);
        Assert.Equal(0, odd.Width);
        Assert.Equal(0, odd.Height);
        Assert.Equal(60, odd.FpsRequested);
        Assert.Null(odd.Codec);
        Assert.False(odd.Hdr);
        Assert.Null(odd.StartedAt);
        Assert.Equal(new IpcStreamSession(), sessions[1]);
    }

    [Fact]
    public void Streams_are_matched_by_id_or_by_address_and_start_time()
    {
        var t = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        Assert.True(new IpcStreamSession { Id = 1, Address = "a" }.SameStream(new IpcStreamSession { Id = 1, Address = "b" }));
        Assert.False(new IpcStreamSession { Id = 1, Address = "a", StartedAt = t }.SameStream(new IpcStreamSession { Id = 2, Address = "a", StartedAt = t }));
        Assert.True(new IpcStreamSession { Address = "a", StartedAt = t }.SameStream(new IpcStreamSession { Id = 2, Address = "a", StartedAt = t }));
        Assert.False(new IpcStreamSession { Address = "a", StartedAt = t }.SameStream(new IpcStreamSession { Address = "a", StartedAt = t.AddSeconds(1) }));
    }

    [Fact]
    public void Sessions_survive_a_round_trip()
    {
        var heartbeat = Assert.IsType<HeartbeatMessage>(IpcCodec.Decode(WorkerHeartbeat.ReplaceLineEndings("")));
        var encoded = IpcCodec.EncodeToString(heartbeat);
        Assert.Contains("\"sessions\":[{\"id\":1234567890", encoded, StringComparison.Ordinal);
    }
}
