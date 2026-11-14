// SPDX-License-Identifier: MIT
using SplitPlay.Core.Streaming;

namespace SplitPlay.Core.Tests;

public sealed class StreamRecordingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 17, 0, 0, TimeSpan.Zero);

    private static StreamRecording New(int minutes = 30) => new(Start, TimeSpan.FromMinutes(minutes), "SplitPlay test", TimeZoneInfo.Utc);

    private const string Laptop = "Laptop 192.168.1.106 2560x1440@180 hevc 100 Mbit/s";

    private const string Phone = "Phone 192.168.1.50 1920x1080@60 h264 20 Mbit/s";

    private static RecordingStream[] One(double idr = 0, string key = "1", string line = Laptop) => [new(key, line, idr)];

    private static RecordingSample At(int second, RecordingStream[]? streams = null) => new()
    {
        At = Start.AddSeconds(second),
        Desk = "player2",
        Streams = streams ?? One(),
        MinFps = 20,
        Fps = 114,
        ComposeHz = 115,
        EncodeMsP50 = 3.3,
        EncodeMsP99 = 7.2,
        RttMs = 12,
        PacketLossPct = 0,
        FecPercent = 5,
        WireKbps = 69_000,
        FrameGapMsMax = 12,
        GpuPercent = 68,
        EncoderPercent = 16,
        HostGpuPercent = 80,
    };

    [Fact]
    public void A_clean_half_hour_is_one_line_per_minute_and_short()
    {
        var r = New();
        for (var s = 0; s < 30 * 60; s++)
        {
            r.Add(At(s));
        }

        var report = r.Report(Start.AddMinutes(31));
        Assert.False(r.IsRunning(Start.AddMinutes(31)));
        Assert.Equal(1800, r.Samples);
        Assert.Contains("desk player2", report, StringComparison.Ordinal);
        Assert.Contains("stream Laptop 192.168.1.106 2560x1440@180", report, StringComparison.Ordinal);
        Assert.Contains("    0  114/114", report, StringComparison.Ordinal);
        Assert.Contains("   29  114/114", report, StringComparison.Ordinal);
        Assert.DoesNotContain("   30  ", report, StringComparison.Ordinal);
        Assert.Contains("IDR requests 0", report, StringComparison.Ordinal);
        Assert.Contains("17:00:00 player2 streaming: Laptop", report, StringComparison.Ordinal);
        // Small enough to paste into a chat message.
        Assert.InRange(report.Length, 500, 6000);
    }

    [Fact]
    public void Key_frame_requests_pauses_and_losses_become_events_and_repeats_are_merged()
    {
        var r = New();
        r.Add(At(0, One(idr: 4)));
        r.Add(At(1, One(idr: 6)));
        r.Add(At(2, One(idr: 7)) with { FrameGapMsMax = 180 });
        r.Add(At(3, One(idr: 7)) with { FrameGapMsMax = 90, PacketLossPct = 2.5 });
        r.Add(At(4, One(idr: 7)) with { FecFailed = 1, PacketLossPct = 6, FrameLossPct = 4 });
        r.Add(At(5, One(idr: 7)) with { BitrateCapKbps = 75_000, CapReason = "loss" });
        r.Add(At(70, One(idr: 7)) with { FrameGapMsMax = 400, Fps = 25 });
        r.Add(At(71, []));

        var report = r.Report(Start.AddMinutes(2));
        // The 4 requests from before the recording are not counted.
        Assert.Contains("17:00:01 player2 IDR requested (Moonlight dropped frames: slow decoder, unrepaired loss or window change) ×3", report, StringComparison.Ordinal);
        Assert.Contains("IDR requests 3,", report, StringComparison.Ordinal);
        // Merged pauses keep the worst value.
        Assert.Contains("17:00:02 player2 pause between frames up to 180 ms ×2", report, StringComparison.Ordinal);
        Assert.Contains("17:00:03 player2 packet loss up to 2.50 %", report, StringComparison.Ordinal);
        Assert.Contains("FEC could not repair a block, packet loss up to 6.00 %", report, StringComparison.Ordinal);
        Assert.Contains("frames lost 4.0 %", report, StringComparison.Ordinal);
        Assert.Contains("bitrate capped at 75 Mbit/s (loss)", report, StringComparison.Ordinal);
        Assert.Contains("client disconnected", report, StringComparison.Ordinal);
        // A pause on a nearly static picture (25 fps with a 20 fps idle rate) is not a stall.
        Assert.DoesNotContain("400 ms", report, StringComparison.Ordinal);
        Assert.Contains("stalls 2,", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Key_frame_requests_are_counted_per_stream()
    {
        var r = New();
        r.Add(At(0, One(idr: 4)));
        r.Add(At(1, [new("1", Laptop, 5), new("2", Phone, 1)]));
        r.Add(At(2, [new("2", Phone, 1)]));
        // The laptop comes back with the same settings: a new stream, counted from 0.
        r.Add(At(3, [new("2", Phone, 1), new("3", Laptop, 2)]));

        var report = r.Report(Start.AddMinutes(1));
        Assert.Contains("IDR requests 4,", report, StringComparison.Ordinal);
        Assert.Contains("17:00:01 player2 client connected: Phone", report, StringComparison.Ordinal);
        Assert.Contains("17:00:02 player2 client disconnected", report, StringComparison.Ordinal);
        Assert.Contains("17:00:03 player2 client connected: Laptop", report, StringComparison.Ordinal);
        Assert.Single(report.Split('\n'), l => l == "  stream " + Laptop);
    }

    [Fact]
    public void A_desk_that_never_streams_stays_out_of_the_report()
    {
        var r = New();
        r.Add(At(0, []) with { Desk = "idle" });
        r.Add(At(1));
        var report = r.Report(Start.AddMinutes(1));
        Assert.DoesNotContain("desk idle", report, StringComparison.Ordinal);
        Assert.Equal(1, r.Samples);
    }

    [Fact]
    public void Stop_and_the_end_close_the_recording()
    {
        var r = New(minutes: 1);
        r.Add(At(10));
        r.Add(At(61));
        Assert.Equal(1, r.Samples);
        Assert.Equal(Start.AddMinutes(1), r.StoppedAt);
        r.Add(At(20));
        Assert.Equal(1, r.Samples);

        var stopped = New();
        stopped.Stop(Start.AddSeconds(30));
        stopped.Add(At(40));
        Assert.Equal(0, stopped.Samples);
        Assert.Contains("no stream was running", stopped.Report(Start.AddMinutes(5)), StringComparison.Ordinal);
        Assert.Contains("(0.5 min)", stopped.Report(Start.AddMinutes(5)), StringComparison.Ordinal);
    }

    [Fact]
    public void Events_beyond_the_limit_are_counted()
    {
        var r = New();
        r.Add(At(0));
        for (var i = 1; i <= StreamRecording.MaxEvents + 10; i++)
        {
            // Ten seconds apart: never merged.
            r.Add(At(i * 10) with { PacketLossPct = 3 });
        }

        var report = r.Report(Start.AddMinutes(20));
        Assert.Contains("… 11 more: loss 11", report, StringComparison.Ordinal);

        // A serious moment still gets in: it pushes out the oldest minor one.
        r.Add(At(800) with { FrameGapMsMax = 300 });
        report = r.Report(Start.AddMinutes(20));
        Assert.Contains("pause between frames up to 300 ms", report, StringComparison.Ordinal);
        Assert.Contains("… 12 more: loss 12", report, StringComparison.Ordinal);
    }
}
