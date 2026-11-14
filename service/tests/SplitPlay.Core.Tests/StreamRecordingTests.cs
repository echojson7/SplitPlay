// SPDX-License-Identifier: MIT
using SplitPlay.Core.Streaming;

namespace SplitPlay.Core.Tests;

public sealed class StreamRecordingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 17, 0, 0, TimeSpan.Zero);

    private static StreamRecording New(int minutes = 30) => new(Start, TimeSpan.FromMinutes(minutes), "SplitPlay test", TimeZoneInfo.Utc);

    private static RecordingSample At(int second, string? stream = "Laptop 192.168.1.106 2560x1440@180 hevc 100 Mbit/s") => new()
    {
        At = Start.AddSeconds(second),
        Desk = "player2",
        Stream = stream,
        Fps = 114,
        ComposeHz = 115,
        EncodeMsP50 = 3.3,
        EncodeMsP99 = 7.2,
        RttMs = 12,
        PacketLossPct = 0,
        FecPercent = 5,
        WireKbps = 69_000,
        IdrRequests = 0,
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
        Assert.Contains("17:00:00 player2 client connected", report, StringComparison.Ordinal);
        // Small enough to paste into a chat message.
        Assert.InRange(report.Length, 500, 6000);
    }

    [Fact]
    public void Key_frame_requests_pauses_and_losses_become_events_and_repeats_are_merged()
    {
        var r = New();
        r.Add(At(0));
        r.Add(At(1) with { IdrRequests = 2 });
        r.Add(At(2) with { IdrRequests = 3, FrameGapMsMax = 180 });
        r.Add(At(3) with { IdrRequests = 3, PacketLossPct = 2.5 });
        r.Add(At(4) with { IdrRequests = 3, FecFailed = 1, PacketLossPct = 6, FrameLossPct = 4 });
        r.Add(At(5) with { IdrRequests = 3, BitrateCapKbps = 75_000, CapReason = "loss" });
        r.Add(At(70) with { IdrRequests = 3, FrameGapMsMax = 400, Fps = 0 });
        r.Add(At(71, stream: null));

        var report = r.Report(Start.AddMinutes(2));
        Assert.Contains("17:00:01 player2 IDR requested (client dropped frames: slow decoder or unrepaired loss) ×3", report, StringComparison.Ordinal);
        Assert.Contains("17:00:02 player2 pause between frames 180 ms", report, StringComparison.Ordinal);
        Assert.Contains("17:00:03 player2 packet loss 2.50 %", report, StringComparison.Ordinal);
        Assert.Contains("FEC could not repair 1 block(s)", report, StringComparison.Ordinal);
        Assert.Contains("frames lost 4.0 %", report, StringComparison.Ordinal);
        Assert.Contains("bitrate capped at 75 Mbit/s (loss)", report, StringComparison.Ordinal);
        Assert.Contains("client disconnected", report, StringComparison.Ordinal);
        // A long pause on a stream that sends nothing (static picture) is not a stall.
        Assert.DoesNotContain("400 ms", report, StringComparison.Ordinal);
        Assert.Contains("IDR requests 3", report, StringComparison.Ordinal);
        Assert.Contains("stalls ≥50 ms 1", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_stream_counts_its_key_frame_requests_from_zero()
    {
        var r = New();
        r.Add(At(0) with { IdrRequests = 4 });
        r.Add(At(1, stream: null));
        r.Add(At(2, stream: "Phone 192.168.1.50 1920x1080@60 h264 20 Mbit/s") with { IdrRequests = 1 });

        var report = r.Report(Start.AddMinutes(1));
        Assert.Contains("IDR requests 5", report, StringComparison.Ordinal);
        Assert.Contains("stream Phone", report, StringComparison.Ordinal);
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
        Assert.Contains("… 11 more", report, StringComparison.Ordinal);
    }
}
