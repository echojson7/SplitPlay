// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Streaming;

/// <summary>One worker heartbeat of a desk, as the stream adaptation reads it (docs/contracts/ipc.md).</summary>
public sealed record AdaptiveSample
{
    /// <summary>Running Moonlight streams.</summary>
    public int Clients { get; init; }

    /// <summary>Packets lost on the way, percent of the packets sent (<c>packet_loss_pct</c>, from the clients' FEC reports).</summary>
    public double PacketLossPct { get; init; }

    /// <summary>The worst FEC block of the interval: lost packets per data packet, percent (<c>burst_loss_pct</c>).</summary>
    public double BurstLossPct { get; init; }

    /// <summary>FEC blocks the client could not recover (<c>fec_failed</c>).</summary>
    public double FecFailed { get; init; }

    /// <summary>Frames the client reported lost, percent of the frames sent (<c>loss_pct</c>).</summary>
    public double FrameLossPct { get; init; }

    /// <summary>Round-trip time to the client, ms; null while not measured.</summary>
    public double? RttMs { get; init; }

    /// <summary>Video traffic the worker sent, kbit/s, FEC included (<c>bitrate_kbps</c>).</summary>
    public double WireKbps { get; init; }

    /// <summary>The bitrate the clients asked for (sum of the streams' Moonlight settings), kbit/s; 0 when unknown.</summary>
    public int RequestedKbps { get; init; }

    /// <summary>Every stream's client sends FEC reports (<c>fec_feedback</c>); without them packet loss is invisible.</summary>
    public bool FecFeedback { get; init; }

    /// <summary>The bitrate the worker's encoder runs at (<c>encoder_kbps</c>); null when it cannot change it live.</summary>
    public double? EncoderKbps { get; init; }
}

/// <summary>Why the adaptation holds a desk's bitrate below what the client asked for.</summary>
public enum AdaptiveLimit
{
    /// <summary>Not limited.</summary>
    None,

    /// <summary>The network lost packets FEC could not make up for, or the round trip grew: the link is full.</summary>
    Loss,

    /// <summary>The desks on one network adapter together asked for more than it carries.</summary>
    Network,
}

/// <summary>What the adaptation sends a desk's worker: the FEC share and the cap on its video traffic (null: none).</summary>
public sealed record AdaptiveState(int FecPercent, int? BitrateCapKbps, AdaptiveLimit Limit);

/// <summary>
/// Adaptive FEC and bitrate for one desk ("Адаптивный поток", stage 4 PR 11), driven by the worker's heartbeats (about
/// once a second). Not thread-safe: the caller serialises calls (the desk's gate).
/// </summary>
/// <remarks>
/// <para>
/// <b>FEC</b> follows the worst FEC block of the last <see cref="FecWindow"/> heartbeats: the parity share that block
/// needed, plus a quarter and two points, rounded up to <see cref="FecStep"/>, between <see cref="MinFecPercent"/>
/// and <see cref="MaxFecPercent"/>. It goes up at once and down by one step every <see cref="FecLowerEvery"/>
/// heartbeats, so a clean link drifts from the start value (Sunshine's 20 %) to 5 % in half a minute and saves the
/// parity traffic. A block lost although it was within reach of FEC adds a step. A block that lost more packets than it
/// has data (a Wi-Fi stall) is out of reach of any FEC and is left to the bitrate. Without the clients' FEC reports
/// FEC stays where it is.
/// </para>
/// <para>
/// <b>Bitrate</b> (only when the worker's encoder can change it live) is AIMD: a heartbeat is congested when 5 % or
/// more of the packets were lost, when frames were lost and at least 1 % of packets (so not a single stall), or when
/// the round trip is 50 ms and twice above the lowest seen. After <see cref="CongestedToCut"/> congested heartbeats in
/// a row the cap drops to <see cref="CutFactor"/> of the current rate (not below <see cref="MinCapKbps"/> or a fifth of
/// what the client asked for), at most every <see cref="CutHold"/> heartbeats. After <see cref="CleanBeforeRaise"/>
/// clean heartbeats it grows by <see cref="RaiseShare"/> of the requested rate per heartbeat and is lifted once it
/// reaches it.
/// </para>
/// </remarks>
public sealed class StreamAdaptation
{
    public const int MinFecPercent = 5;
    public const int MaxFecPercent = 50;

    /// <summary>FEC share a new worker starts with (Sunshine's default).</summary>
    public const int StartFecPercent = 20;

    public const int FecStep = 5;
    public const int FecWindow = 15;
    public const int FecLowerEvery = 10;
    public const double FecHeadroom = 1.25;
    public const double FecMargin = 2;

    public const double HeavyPacketLossPct = 5;
    public const double LossWithFrameLossPct = 1;
    public const double RttRiseMs = 50;
    public const int CongestedToCut = 2;
    public const int CutHold = 3;
    public const double CutFactor = 0.75;
    public const int MinCapKbps = 2000;
    public const double MinCapShare = 0.2;
    public const int CleanBeforeRaise = 10;
    public const double RaiseShare = 0.05;

    private readonly Queue<double> _needs = new();
    private int _fec = StartFecPercent;
    private int _sinceFecChange;
    private int? _cap;
    private int _congested;
    private int _clean;
    private int _sinceCut = int.MaxValue / 2;
    private double? _minRtt;

    /// <summary>The FEC share for the next frames.</summary>
    public int FecPercent => _fec;

    /// <summary>The cap the losses ask for, kbit/s; null when the stream may use what its client asked for.</summary>
    public int? LossCapKbps => _cap;

    /// <summary>A new worker: everything back to the start values.</summary>
    public void Reset()
    {
        _needs.Clear();
        _fec = StartFecPercent;
        _sinceFecChange = 0;
        StreamsChanged();
    }

    /// <summary>
    /// A stream started or ended: the bitrate starts again from what the clients ask for. FEC keeps what it learned,
    /// since the network is likely the same.
    /// </summary>
    public void StreamsChanged()
    {
        _cap = null;
        _congested = 0;
        _clean = 0;
        _sinceCut = int.MaxValue / 2;
        _minRtt = null;
    }

    /// <summary>One heartbeat. A heartbeat without a stream changes nothing.</summary>
    /// <param name="sample">The heartbeat.</param>
    /// <param name="adaptFec">False when the desk sets a fixed FEC share: only the bitrate adapts.</param>
    public void Update(AdaptiveSample sample, bool adaptFec)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Clients <= 0)
        {
            return;
        }

        if (adaptFec && sample.FecFeedback)
        {
            UpdateFec(sample);
        }

        UpdateBitrate(sample);
    }

    /// <summary>What to send, with the cap of the desk's share of its network adapter (null: no limit there).</summary>
    public AdaptiveState State(int? networkShareKbps)
    {
        if (networkShareKbps is int share && (_cap is not int cap || share < cap))
        {
            return new AdaptiveState(_fec, share, AdaptiveLimit.Network);
        }

        return new AdaptiveState(_fec, _cap, _cap is null ? AdaptiveLimit.None : AdaptiveLimit.Loss);
    }

    private void UpdateFec(AdaptiveSample s)
    {
        // More lost than the block had data: no FEC share would have saved it.
        var reachable = s.BurstLossPct <= 100;
        _needs.Enqueue(reachable ? Math.Min(s.BurstLossPct, MaxFecPercent) : 0);
        while (_needs.Count > FecWindow)
        {
            _needs.Dequeue();
        }

        var peak = _needs.Max();
        var target = peak > 0 ? RoundUp(peak * FecHeadroom + FecMargin) : MinFecPercent;
        if (s.FecFailed > 0 && reachable && s.BurstLossPct > 0)
        {
            target = Math.Max(target, _fec + FecStep);
        }

        target = Math.Clamp(target, MinFecPercent, MaxFecPercent);
        if (target > _fec)
        {
            _fec = target;
            _sinceFecChange = 0;
            return;
        }

        _sinceFecChange++;
        if (target < _fec && _sinceFecChange >= FecLowerEvery)
        {
            _fec = Math.Max(target, _fec - FecStep);
            _sinceFecChange = 0;
        }
    }

    private void UpdateBitrate(AdaptiveSample s)
    {
        if (s.EncoderKbps is null)
        {
            // The encoder keeps the client's bitrate whatever the cap: a cap would only drift down to the floor.
            _cap = null;
            return;
        }

        var requested = s.RequestedKbps > 0 ? s.RequestedKbps : (int)Math.Max(0, s.WireKbps);
        if (requested <= 0)
        {
            return;
        }

        if (s.RttMs is double rtt && rtt >= 0)
        {
            _minRtt = Math.Min(_minRtt ?? rtt, rtt);
        }

        var rttHigh = s.RttMs is double r && _minRtt is double min && r > min + RttRiseMs && r > 2 * min;
        var unrecoverable = (s.FecFailed > 0 || s.FrameLossPct > 0) && s.PacketLossPct >= LossWithFrameLossPct;
        var congested = s.PacketLossPct >= HeavyPacketLossPct || unrecoverable || rttHigh;

        if (_sinceCut < int.MaxValue / 2)
        {
            _sinceCut++;
        }

        if (!congested)
        {
            _congested = 0;
            _clean++;
            if (_cap is int cap && _clean >= CleanBeforeRaise)
            {
                var next = cap + Math.Max(1, (int)(requested * RaiseShare));
                _cap = next >= requested ? null : next;
            }

            return;
        }

        _clean = 0;
        _congested++;
        if (_congested < CongestedToCut || _sinceCut < CutHold)
        {
            return;
        }

        // Cut from the rate the stream really sends, unless the picture is just quiet (then from half the allowance).
        var allowed = Math.Min(_cap ?? requested, requested);
        var basis = s.WireKbps > 0 && s.WireKbps < allowed ? Math.Max(s.WireKbps, allowed / 2.0) : allowed;
        var floor = Math.Min(requested, Math.Max(MinCapKbps, (int)(requested * MinCapShare)));
        var cut = Math.Max(floor, (int)(basis * CutFactor));
        _congested = 0;
        if (cut < allowed)
        {
            _cap = cut;
            _sinceCut = 0;
        }
    }

    private static int RoundUp(double percent) => (int)Math.Ceiling(percent / FecStep) * FecStep;
}
