// SPDX-License-Identifier: MIT
using SplitPlay.Core.Streaming;

namespace SplitPlay.Core.Tests;

public sealed class StreamAdaptationTests
{
    /// <summary>A clean 50 Mbit/s HEVC stream from a client that sends FEC reports, on an encoder that can change its bitrate.</summary>
    private static AdaptiveSample Clean(double wireKbps = 48_000) => new()
    {
        Clients = 1,
        RequestedKbps = 50_000,
        WireKbps = wireKbps,
        RttMs = 4,
        FecFeedback = true,
        EncoderKbps = 40_000,
    };

    private static AdaptiveSample Lossy(double packetLossPct, double burstPct, double failed = 0, double frameLossPct = 0) =>
        Clean() with { PacketLossPct = packetLossPct, BurstLossPct = burstPct, FecFailed = failed, FrameLossPct = frameLossPct };

    private static void Run(StreamAdaptation a, AdaptiveSample s, int times, bool adaptFec = true)
    {
        for (var i = 0; i < times; i++)
        {
            a.Update(s, adaptFec);
        }
    }

    [Fact]
    public void A_clean_link_lowers_fec_step_by_step_to_the_minimum()
    {
        var a = new StreamAdaptation();
        Assert.Equal(StreamAdaptation.StartFecPercent, a.FecPercent);

        Run(a, Clean(), StreamAdaptation.FecLowerEvery - 1);
        Assert.Equal(20, a.FecPercent);
        Run(a, Clean(), 1);
        Assert.Equal(15, a.FecPercent);
        Run(a, Clean(), 3 * StreamAdaptation.FecLowerEvery);
        Assert.Equal(StreamAdaptation.MinFecPercent, a.FecPercent);
        Assert.Equal(new AdaptiveState(5, null, AdaptiveLimit.None), a.State(null));
    }

    [Fact]
    public void Fec_follows_the_worst_block_at_once_and_forgets_it_slowly()
    {
        var a = new StreamAdaptation();
        Run(a, Clean(), 40);
        Assert.Equal(5, a.FecPercent);

        // A block lost 12 % of its data packets: 12 * 1.25 + 2 = 17 -> 20 %.
        a.Update(Lossy(0.8, 12), adaptFec: true);
        Assert.Equal(20, a.FecPercent);

        // A block that FEC could not repair although it was within reach: one more step on top.
        a.Update(Lossy(1.5, 12, failed: 1), adaptFec: true);
        Assert.Equal(25, a.FecPercent);

        // The extra step goes after FecLowerEvery heartbeats; the peak itself is remembered for FecWindow heartbeats,
        // then FEC steps down every FecLowerEvery heartbeats.
        Run(a, Clean(), StreamAdaptation.FecLowerEvery - 1);
        Assert.Equal(25, a.FecPercent);
        a.Update(Clean(), adaptFec: true);
        Assert.Equal(20, a.FecPercent);
        Run(a, Clean(), StreamAdaptation.FecLowerEvery);
        Assert.Equal(15, a.FecPercent);
        Run(a, Clean(), 4 * StreamAdaptation.FecLowerEvery);
        Assert.Equal(5, a.FecPercent);
    }

    [Fact]
    public void Fec_never_leaves_its_range()
    {
        var a = new StreamAdaptation();
        a.Update(Lossy(10, 90, failed: 3), adaptFec: true);
        Assert.Equal(StreamAdaptation.MaxFecPercent, a.FecPercent);
        a.Update(Lossy(10, 90, failed: 3), adaptFec: true);
        Assert.Equal(StreamAdaptation.MaxFecPercent, a.FecPercent);
    }

    [Fact]
    public void A_stall_out_of_reach_of_fec_does_not_raise_it()
    {
        var a = new StreamAdaptation();
        Run(a, Clean(), 40);

        // Every packet of a block and more lost (a Wi-Fi stall): no FEC share helps.
        a.Update(Lossy(0.5, 120, failed: 1), adaptFec: true);
        Assert.Equal(5, a.FecPercent);
    }

    [Fact]
    public void Fec_stays_put_without_reports_or_when_the_desk_fixes_it()
    {
        var a = new StreamAdaptation();
        Run(a, Clean() with { FecFeedback = false }, 50);
        Assert.Equal(20, a.FecPercent);

        Run(a, Clean(), 50, adaptFec: false);
        Assert.Equal(20, a.FecPercent);

        // Idle heartbeats (no stream) change nothing.
        Run(a, new AdaptiveSample(), 50);
        Assert.Equal(20, a.FecPercent);
    }

    [Fact]
    public void Heavy_loss_cuts_the_bitrate_after_two_heartbeats_and_not_faster_than_the_hold()
    {
        var a = new StreamAdaptation();
        a.Update(Lossy(6, 20), adaptFec: true);
        Assert.Null(a.LossCapKbps);

        a.Update(Lossy(6, 20), adaptFec: true);
        Assert.Equal(36_000, a.LossCapKbps); // 75 % of the 48 Mbit/s actually sent
        Assert.Equal(new AdaptiveState(a.FecPercent, 36_000, AdaptiveLimit.Loss), a.State(null));

        // Still congested: the next cut waits for CutHold heartbeats and two congested ones in a row.
        a.Update(Lossy(6, 20), adaptFec: true);
        a.Update(Lossy(6, 20), adaptFec: true);
        Assert.Equal(36_000, a.LossCapKbps);
        a.Update(Lossy(6, 20) with { WireKbps = 35_000 }, adaptFec: true);
        Assert.Equal(26_250, a.LossCapKbps);
    }

    [Fact]
    public void The_cut_never_goes_below_the_floor()
    {
        var a = new StreamAdaptation();
        Run(a, Lossy(30, 40, failed: 5) with { WireKbps = 1_000 }, 200);
        Assert.Equal(10_000, a.LossCapKbps); // a fifth of the 50 Mbit/s asked for
    }

    [Fact]
    public void Single_frame_losses_without_packet_loss_are_not_congestion()
    {
        var a = new StreamAdaptation();
        Run(a, Lossy(0.2, 100, failed: 1, frameLossPct: 0.8), 20);
        Assert.Null(a.LossCapKbps);

        // Unrecoverable blocks together with 1 % of packets lost are.
        Run(a, Lossy(1.2, 30, failed: 2, frameLossPct: 0.8), 2);
        Assert.NotNull(a.LossCapKbps);
    }

    [Fact]
    public void A_growing_round_trip_is_congestion()
    {
        var a = new StreamAdaptation();
        Run(a, Clean(), 3);
        Run(a, Clean() with { RttMs = 40 }, 3);
        Assert.Null(a.LossCapKbps); // +36 ms: within the margin

        Run(a, Clean() with { RttMs = 70 }, 2);
        Assert.NotNull(a.LossCapKbps);
    }

    [Fact]
    public void A_clean_link_raises_the_cap_again_and_lifts_it()
    {
        var a = new StreamAdaptation();
        Run(a, Lossy(6, 20), 2);
        Assert.Equal(36_000, a.LossCapKbps);

        Run(a, Clean(), StreamAdaptation.CleanBeforeRaise - 1);
        Assert.Equal(36_000, a.LossCapKbps);
        a.Update(Clean(), adaptFec: true);
        Assert.Equal(38_500, a.LossCapKbps); // + 5 % of 50 Mbit/s per clean heartbeat

        Run(a, Clean(), 5);
        Assert.Null(a.LossCapKbps);
        Assert.Equal(AdaptiveLimit.None, a.State(null).Limit);
    }

    [Fact]
    public void No_cap_when_the_encoder_cannot_change_its_bitrate()
    {
        var a = new StreamAdaptation();
        Run(a, Lossy(8, 20) with { EncoderKbps = null }, 10);
        Assert.Null(a.LossCapKbps);
    }

    [Fact]
    public void A_new_stream_starts_from_the_requested_bitrate_and_keeps_fec()
    {
        var a = new StreamAdaptation();
        Run(a, Lossy(6, 20), 2);
        var fec = a.FecPercent;
        Assert.NotNull(a.LossCapKbps);

        a.StreamsChanged();
        Assert.Null(a.LossCapKbps);
        Assert.Equal(fec, a.FecPercent);

        a.Reset();
        Assert.Equal(StreamAdaptation.StartFecPercent, a.FecPercent);
    }

    [Fact]
    public void The_network_share_wins_when_it_is_lower()
    {
        var a = new StreamAdaptation();
        Assert.Equal(new AdaptiveState(20, 30_000, AdaptiveLimit.Network), a.State(30_000));

        Run(a, Lossy(6, 20), 2);
        Assert.Equal(new AdaptiveState(a.FecPercent, 36_000, AdaptiveLimit.Loss), a.State(40_000));
        Assert.Equal(new AdaptiveState(a.FecPercent, 20_000, AdaptiveLimit.Network), a.State(20_000));
    }
}

public sealed class NetworkBudgetTests
{
    [Fact]
    public void Desks_within_the_link_are_not_limited()
    {
        var b = new NetworkBudget();
        b.Set("a", "eth0", 1_000_000_000, 150_000);
        b.Set("b", "eth0", 1_000_000_000, 150_000);
        Assert.Null(b.ShareKbps("a"));
        Assert.Null(b.ShareKbps("unknown"));
    }

    [Fact]
    public void Desks_over_the_link_share_80_percent_of_it_by_demand()
    {
        var b = new NetworkBudget();
        b.Set("a", "eth0", 100_000_000, 60_000);
        b.Set("b", "eth0", 100_000_000, 20_000);
        b.Set("c", "wifi", 100_000_000, 60_000); // another adapter: not counted
        Assert.Equal(60_000, b.ShareKbps("a"));
        Assert.Equal(20_000, b.ShareKbps("b"));
        Assert.Null(b.ShareKbps("c"));

        b.Set("d", "eth0", 100_000_000, 80_000);
        Assert.Equal(30_000, b.ShareKbps("a")); // 80 Mbit/s * 60 / 160
        Assert.Equal(10_000, b.ShareKbps("b"));
        Assert.Equal(40_000, b.ShareKbps("d"));

        b.Remove("d");
        b.Set("b", null, 0, 20_000); // no known link: removed too
        Assert.Null(b.ShareKbps("a"));
        Assert.Null(b.ShareKbps("b"));
    }

    [Fact]
    public void A_share_never_falls_below_the_minimum()
    {
        var b = new NetworkBudget();
        b.Set("a", "slow", 10_000_000, 50_000);
        b.Set("b", "slow", 10_000_000, 1_000);
        Assert.Equal(7_843, b.ShareKbps("a"));
        Assert.Equal(1_000, b.ShareKbps("b")); // at most what it asks for
        b.Set("c", "slow", 10_000_000, 500_000);
        Assert.Equal(NetworkBudget.MinShareKbps, b.ShareKbps("a")); // 8 Mbit/s * 50 / 551 is less
        Assert.Equal(1_000, b.ShareKbps("b"));
    }
}
