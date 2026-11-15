// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;

namespace SplitPlay.Core.Streaming;

/// <summary>One Moonlight stream of a <see cref="RecordingSample"/>.</summary>
/// <param name="Key">Stable while the stream runs (the worker's launch id, or address and start time).</param>
/// <param name="Line">What the report shows ("Laptop 192.168.1.106 2560x1440@180 hevc 100 Mbit/s").</param>
/// <param name="IdrRequests">Key frames the client asked for since the stream started; null when not measured.</param>
public sealed record RecordingStream(string Key, string Line, double? IdrRequests);

/// <summary>The host's network adapter towards a desk's client, as a <see cref="RecordingSample"/> carries it.</summary>
/// <param name="Line">What the report shows ("Ethernet Realtek PCIe 2.5GbE 2500 Mbit/s").</param>
/// <param name="OutDiscards">Outgoing packets the adapter threw away since it came up; null when unknown.</param>
/// <param name="WifiSignalPercent">Wi-Fi signal quality, 0-100; null on a wired adapter.</param>
/// <param name="WifiRssiDbm">Wi-Fi signal strength, dBm.</param>
/// <param name="WifiTxMbps">Wi-Fi transmit rate, Mbit/s.</param>
public sealed record RecordingLink(string Line, long? OutDiscards, int? WifiSignalPercent = null, int? WifiRssiDbm = null, double? WifiTxMbps = null);

/// <summary>
/// One worker heartbeat of a desk as a <see cref="StreamRecording"/> keeps it (about once a second). Values a worker
/// or the GPU counters do not report are null.
/// </summary>
public sealed record RecordingSample
{
    public required DateTimeOffset At { get; init; }

    public required string Desk { get; init; }

    /// <summary>The desk's running streams; empty without a client.</summary>
    public IReadOnlyList<RecordingStream> Streams { get; init; } = [];

    public double Fps { get; init; }

    /// <summary>The idle frame rate the worker keeps on a static picture (profile min fps); null when unknown.</summary>
    public double? MinFps { get; init; }

    public double? ComposeHz { get; init; }

    public double EncodeMsP50 { get; init; }

    public double EncodeMsP99 { get; init; }

    public double? RttMs { get; init; }

    public double FrameLossPct { get; init; }

    public double? PacketLossPct { get; init; }

    public double? FecFailed { get; init; }

    public double? FecPercent { get; init; }

    public double WireKbps { get; init; }

    public double? FrameGapMsMax { get; init; }

    /// <summary>The bitrate cap the adaptation holds, kbit/s; null when none.</summary>
    public int? BitrateCapKbps { get; init; }

    /// <summary>Why the cap is held (<c>loss</c>, <c>network</c>); null without one.</summary>
    public string? CapReason { get; init; }

    /// <summary>The desk session's GPU load, percent.</summary>
    public double? GpuPercent { get; init; }

    /// <summary>The desk session's NVENC load, percent.</summary>
    public double? EncoderPercent { get; init; }

    /// <summary>The busiest GPU of the host (all sessions, the console's game included), percent.</summary>
    public double? HostGpuPercent { get; init; }

    // Where a frame's time goes (worker patch 0028), p99 over the heartbeat interval, ms.

    /// <summary>Present to encode start: Desktop Duplication, copy, capture queue.</summary>
    public double? CaptureMsP99 { get; init; }

    /// <summary>Encode start to bitstream ready: colour conversion on the GPU and the encoder.</summary>
    public double? EncoderMsP99 { get; init; }

    /// <summary>Bitstream ready to the sender taking it.</summary>
    public double? QueueMsP99 { get; init; }

    /// <summary>Packetising, FEC and paced sending.</summary>
    public double? SendMsP99 { get; init; }

    /// <summary>Present to the last packet sent: what the host adds to the picture's way to the client.</summary>
    public double? SentMsP50 { get; init; }

    public double? SentMsP99 { get; init; }

    public double? SentMsMax { get; init; }

    /// <summary>Encoded frame size, KiB.</summary>
    public double? FrameKbAvg { get; init; }

    public double? FrameKbMax { get; init; }

    /// <summary>Key frames sent in the interval.</summary>
    public double? KeyFrames { get; init; }

    public double? KeyFrameKbMax { get; init; }

    /// <summary>Frames sent without new content in the interval (about one second).</summary>
    public double? RepeatedFrames { get; init; }

    /// <summary>The desk session's dedicated VRAM, MB.</summary>
    public double? DeskVramMb { get; init; }

    /// <summary>Dedicated VRAM in use on the host's busiest GPU, MB, and its size.</summary>
    public double? HostVramUsedMb { get; init; }

    public double? HostVramTotalMb { get; init; }

    /// <summary>The whole CPU, percent.</summary>
    public double? CpuPercent { get; init; }

    /// <summary>The busiest logical processor of the host, percent.</summary>
    public double? CpuMaxCorePercent { get; init; }

    /// <summary>The busiest of the desk's processors (<c>host.deskCores</c>; all of them when not restricted), percent.</summary>
    public double? DeskCpuMaxPercent { get; init; }

    /// <summary>The host's adapter towards the desk's (first) client; null when not found (client behind a router).</summary>
    public RecordingLink? Link { get; init; }
}

/// <summary>What a recording is now, read in one step.</summary>
public sealed record RecordingSnapshot(bool Running, DateTimeOffset StartedAt, DateTimeOffset EndsAt, string Report, int Samples);

/// <summary>
/// "Запись потока": collects the heartbeats of every streaming desk for a while and turns them into a short text that
/// fits in a chat message: one line per desk and minute (min, average and worst values), totals, and a list of notable
/// moments (key frame requests, pauses, losses, bitrate cuts, clients coming and going). Thread-safe.
/// </summary>
public sealed class StreamRecording
{
    /// <summary>Notable moments kept. Beyond it a serious one replaces the oldest minor one; the rest are only counted.</summary>
    public const int MaxEvents = 60;

    /// <summary>The shortest pause between frames that counts as a stall while the stream runs.</summary>
    public const double StallMs = 50;

    /// <summary>
    /// A stream below this rate, or below twice its idle rate (<see cref="RecordingSample.MinFps"/>), is (nearly)
    /// static: its pauses say nothing about pacing.
    /// </summary>
    public const double MovingFps = 20;

    /// <summary>Host processing time (p99) that counts as a spike.</summary>
    public const double EncodeSpikeMs = 12;

    /// <summary>Host latency (present to last packet sent, p99) that counts as a spike.</summary>
    public const double SentSpikeMs = 16;

    /// <summary>Dedicated VRAM use (share of the GPU's) at which games start to page and stutter.</summary>
    public const double VramFullShare = 0.95;

    /// <summary>Host Wi-Fi signal quality below this is worth a line.</summary>
    public const int WeakWifiPercent = 60;

    /// <summary>A ping this much above the lowest one seen (and at least twice it) is a spike.</summary>
    public const double PingRiseMs = 30;

    /// <summary>Events of one kind and desk this close together are merged into one line ("×3", worst value kept).</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> Minor = new(StringComparer.Ordinal) { "loss", "ping", "enc", "wifi" };

    private readonly object _gate = new();
    private readonly TimeZoneInfo _zone;
    private readonly string _header;
    private readonly SortedDictionary<string, DeskTrack> _desks = new(StringComparer.Ordinal);
    private readonly List<Event> _events = [];
    private readonly SortedDictionary<int, HostBucket> _host = [];
    private readonly SortedDictionary<string, int> _dropped = new(StringComparer.Ordinal);
    private DateTimeOffset? _stoppedAt;
    private int _samples;

    public StreamRecording(DateTimeOffset startedAt, TimeSpan duration, string header, TimeZoneInfo? zone = null)
    {
        StartedAt = startedAt;
        EndsAt = startedAt + duration;
        _header = header;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public DateTimeOffset StartedAt { get; }

    /// <summary>When the recording stops by itself.</summary>
    public DateTimeOffset EndsAt { get; }

    /// <summary>Set by <see cref="Stop"/> or by the first sample past <see cref="EndsAt"/>.</summary>
    public DateTimeOffset? StoppedAt
    {
        get
        {
            lock (_gate)
            {
                return _stoppedAt;
            }
        }
    }

    /// <summary>Heartbeats of streaming desks taken so far.</summary>
    public int Samples
    {
        get
        {
            lock (_gate)
            {
                return _samples;
            }
        }
    }

    public bool IsRunning(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _stoppedAt is null && now < EndsAt;
        }
    }

    public void Stop(DateTimeOffset now)
    {
        lock (_gate)
        {
            _stoppedAt ??= now < EndsAt ? now : EndsAt;
        }
    }

    /// <summary>State and report, consistent with each other.</summary>
    public RecordingSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            return new RecordingSnapshot(_stoppedAt is null && now < EndsAt, StartedAt, _stoppedAt ?? EndsAt, Report(now), _samples);
        }
    }

    /// <summary>Adds one heartbeat; ignored once the recording has stopped or ended.</summary>
    public void Add(RecordingSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            if (_stoppedAt is not null || sample.At < StartedAt)
            {
                return;
            }

            if (sample.At >= EndsAt)
            {
                _stoppedAt = EndsAt;
                return;
            }

            if (!_desks.TryGetValue(sample.Desk, out var track))
            {
                // A desk enters the report with its first stream: a desk that runs without a client stays out.
                if (sample.Streams.Count == 0)
                {
                    return;
                }

                track = new DeskTrack();
                _desks[sample.Desk] = track;
            }

            Track(track, sample);
        }
    }

    /// <summary>The text to copy: header, one line per desk and minute, totals and the notable moments.</summary>
    public string Report(DateTimeOffset now)
    {
        lock (_gate)
        {
            var end = _stoppedAt ?? (now < EndsAt ? now : EndsAt);
            var sb = new StringBuilder();
            sb.Append(_header).Append('\n');
            sb.Append(Inv($"recorded {Clock(StartedAt)}–{Clock(end)} ({Minutes(end - StartedAt)} min), {_samples} samples\n"));
            if (_desks.Count == 0)
            {
                sb.Append("no stream was running\n");
            }

            if (_desks.Count > 0)
            {
                sb.Append("legend: new = frames with new content/s, rep = repeated; host ms p99: cap = present→encode start, enc = GPU convert + encoder, ")
                    .Append("q = wait for the sender, send = FEC + paced send, total = present→last packet sent (p50/p99/max); ")
                    .Append("cpu = busiest core of the desk; drop = packets the host adapter discarded\n");
            }

            foreach (var (desk, track) in _desks)
            {
                sb.Append('\n').Append("desk ").Append(desk).Append('\n');
                foreach (var line in track.Lines)
                {
                    sb.Append("  stream ").Append(line).Append('\n');
                }

                foreach (var line in track.Links)
                {
                    sb.Append("  host link ").Append(line).Append('\n');
                }

                sb.Append("  min  fps min/avg  new  rep  Mbit/s  KB avg/max  key  ping avg/max  pkt% avg/max  fec%  idr  gap max\n");
                foreach (var (minute, b) in track.Minutes)
                {
                    sb.Append(Inv($"  {minute,3}  {F0(b.Fps.Min)}/{F0(b.Fps.Avg),-6} {F0(b.NewFps.Avg),4} {F0(b.Repeated.Avg),4}  {F0(b.Mbps.Avg),6}  {F0(b.FrameKb.Avg)}/{F0(b.FrameKbMax.Max),-6} {b.Keys,4}  {F0(b.Rtt.Avg)}/{F0(b.Rtt.Max),-9}  {F2(b.Packet.Avg)}/{F2(b.Packet.Max),-7}  {F0(b.Fec.Avg),4}  {b.Idr,3}  {F0(b.Gap.Max),7}\n"));
                }

                var wifi = track.Total.WifiSignal.Max is not null;
                sb.Append("  min   hz  cap  enc    q  send  total p50/p99/max  gpu/enc%  vram GB  cpu%  drop").Append(wifi ? "  wifi %/dBm/Mbit" : "").Append('\n');
                foreach (var (minute, b) in track.Minutes)
                {
                    // An older worker has no stage times: its host latency (present to hand-off) stands in for the total.
                    var total = b.SentP99.Max is not null
                        ? Inv($"{F1(b.SentP50.Avg)}/{F1(b.SentP99.Max)}/{F0(b.SentMax.Max)}")
                        : Inv($"{F1(b.EncP50.Avg)}/{F1(b.EncP99.Max)}/-");
                    sb.Append(Inv($"  {minute,3}  {F0(b.Compose.Avg),3} {F1(b.CapP99.Max),4} {F1(b.EncoderP99.Max),4} {F1(b.QueueP99.Max),4} {F1(b.SendP99.Max),5}  {total,-17}  {F0(b.Gpu.Avg)}/{F0(b.Enc.Avg),-6} {F1(b.DeskVramMb.Avg / 1024),7}  {F0(b.DeskCpu.Max),4}  {Drops(b),4}"));
                    if (wifi)
                    {
                        sb.Append(Inv($"  {F0(b.WifiSignal.Min)}/{F0(b.WifiRssi.Min)}/{F0(b.WifiTx.Min)}"));
                    }

                    sb.Append('\n');
                }

                var t = track.Total;
                sb.Append(Inv($"  total: fps avg {F0(t.Fps.Avg)} (min {F0(t.Fps.Min)}, new content {F0(t.NewFps.Avg)}), hz avg {F0(t.Compose.Avg)}, "));
                if (t.SentP99.Max is not null)
                {
                    sb.Append(Inv($"host p99 max: cap {F1(t.CapP99.Max)} enc {F1(t.EncoderP99.Max)} q {F1(t.QueueP99.Max)} send {F1(t.SendP99.Max)} total {F1(t.SentP99.Max)} ms (worst frame {F0(t.SentMax.Max)} ms), "));
                    sb.Append(Inv($"frames avg {F0(t.FrameKb.Avg)} KB max {F0(t.FrameKbMax.Max)} KB, key frames {t.Keys} (max {F0(t.KeyKbMax.Max)} KB), "));
                }
                else
                {
                    sb.Append(Inv($"enc p99 max {F1(t.EncP99.Max)} ms, "));
                }

                sb.Append(Inv($"ping avg {F0(t.Rtt.Avg)} max {F0(t.Rtt.Max)} ms, "));
                sb.Append(Inv($"packet loss avg {F2(t.Packet.Avg)} % (seconds with loss {track.LossSeconds}), frames lost max {F1(track.FrameLossMax)} %, FEC failed {F0(track.FecFailed)}, "));
                sb.Append(Inv($"IDR requests {track.IdrTotal}, stalls {track.Stalls}, gap max {F0(t.Gap.Max)} ms, host adapter drops {track.DropsTotal}\n"));
            }

            if (_host.Count > 0)
            {
                sb.Append('\n').Append("host\n");
                sb.Append("  min  cpu avg/core max  gpu%  vram GB used/total\n");
                foreach (var (minute, h) in _host)
                {
                    sb.Append(Inv($"  {minute,3}  {F0(h.Cpu.Avg),3}/{F0(h.CpuCore.Max),-12} {F0(h.Gpu.Avg),4}  {F1(h.VramUsed.Max / 1024)}/{F1(h.VramTotal.Max / 1024)}\n"));
                }
            }

            sb.Append('\n').Append("events");
            if (_events.Count == 0)
            {
                sb.Append(": none\n");
            }
            else
            {
                sb.Append('\n');
                foreach (var e in _events)
                {
                    sb.Append(Clock(e.At)).Append(' ').Append(e.Desk).Append(' ').Append(e.Text());
                    if (e.Count > 1)
                    {
                        sb.Append(Inv($" ×{e.Count}"));
                    }

                    sb.Append('\n');
                }
            }

            if (_dropped.Count > 0)
            {
                sb.Append(Inv($"… {_dropped.Values.Sum()} more: "))
                    .Append(string.Join(", ", _dropped.Select(d => Inv($"{d.Key} {d.Value}"))))
                    .Append('\n');
            }

            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ collection

    private void Track(DeskTrack track, RecordingSample s)
    {
        // Streams coming and going; key frame requests counted per stream from what it had when the recording first
        // saw it (a stream that starts while recording counts from 0).
        var idrNew = 0;
        var now = new Dictionary<string, RecordingStream>(StringComparer.Ordinal);
        foreach (var stream in s.Streams)
        {
            now[stream.Key] = stream;
        }

        foreach (var (key, stream) in now)
        {
            if (!track.Lines.Contains(stream.Line, StringComparer.Ordinal))
            {
                track.Lines.Add(stream.Line);
            }

            if (!track.Idr.TryGetValue(key, out var before))
            {
                AddEvent(s, "connect", track.Started ? $"client connected: {stream.Line}" : $"streaming: {stream.Line}");
                before = track.Started ? 0 : stream.IdrRequests ?? 0;
            }

            var current = stream.IdrRequests ?? before;
            idrNew += (int)Math.Max(0, current - before);
            track.Idr[key] = Math.Max(before, current);
        }

        foreach (var gone in track.Idr.Keys.Where(k => !now.ContainsKey(k)).ToList())
        {
            track.Idr.Remove(gone);
            AddEvent(s, "disconnect", "client disconnected");
        }

        track.Started = true;
        if (now.Count == 0)
        {
            return;
        }

        _samples++;
        var minute = (int)(s.At - StartedAt).TotalMinutes;
        if (!track.Minutes.TryGetValue(minute, out var bucket))
        {
            bucket = new Bucket();
            track.Minutes[minute] = bucket;
        }

        // A static picture sends few frames (the idle rate, min fps): its pauses say nothing about pacing. A stream counts
        // as moving at twice the idle rate, so a picture that only changes now and then is not taken for stalls.
        var moving = s.Fps >= Math.Max(MovingFps, 2 * (s.MinFps ?? 0));
        var stallMs = StallMs;
        // The host adapter: a new one (or its first sight) is a line of its own; dropped packets counted per sample.
        long? drops = null;
        if (s.Link is { } link)
        {
            if (!track.Links.Contains(link.Line, StringComparer.Ordinal))
            {
                if (track.Links.Count > 0)
                {
                    AddEvent(s, "link", $"host link now {link.Line}");
                }

                track.Links.Add(link.Line);
            }

            if (link.OutDiscards is long now0)
            {
                drops = track.LastDiscards is long before0 && now0 >= before0 && track.LastLink == link.Line ? now0 - before0 : 0;
                track.LastDiscards = now0;
            }

            track.LastLink = link.Line;
        }

        var hostMinute = minute;
        if (!_host.TryGetValue(hostMinute, out var host))
        {
            host = new HostBucket();
            _host[hostMinute] = host;
        }

        host.Cpu.Add(s.CpuPercent);
        host.CpuCore.Add(s.CpuMaxCorePercent);
        host.Gpu.Add(s.HostGpuPercent);
        host.VramUsed.Add(s.HostVramUsedMb);
        host.VramTotal.Add(s.HostVramTotalMb);

        var repeated = s.RepeatedFrames;
        foreach (var b in new[] { bucket, track.Total })
        {
            b.Fps.Add(s.Fps);
            b.NewFps.Add(repeated is double rep ? Math.Max(0, s.Fps - rep) : null);
            b.Repeated.Add(repeated);
            b.FrameKb.Add(s.FrameKbAvg);
            b.FrameKbMax.Add(s.FrameKbMax);
            b.KeyKbMax.Add(s.KeyFrameKbMax);
            b.Keys += (int)(s.KeyFrames ?? 0);
            b.CapP99.Add(s.CaptureMsP99);
            b.EncoderP99.Add(s.EncoderMsP99);
            b.QueueP99.Add(s.QueueMsP99);
            b.SendP99.Add(s.SendMsP99);
            b.SentP50.Add(s.SentMsP50);
            b.SentP99.Add(s.SentMsP99);
            b.SentMax.Add(s.SentMsMax);
            b.DeskVramMb.Add(s.DeskVramMb);
            b.DeskCpu.Add(s.DeskCpuMaxPercent);
            b.WifiSignal.Add(s.Link?.WifiSignalPercent);
            b.WifiRssi.Add(s.Link?.WifiRssiDbm);
            b.WifiTx.Add(s.Link?.WifiTxMbps);
            if (drops is long d)
            {
                b.Drops = (b.Drops ?? 0) + d;
            }

            b.Compose.Add(s.ComposeHz);
            b.EncP50.Add(s.EncodeMsP50);
            b.EncP99.Add(s.EncodeMsP99);
            b.Rtt.Add(s.RttMs);
            b.Packet.Add(s.PacketLossPct);
            b.Fec.Add(s.FecPercent);
            b.Mbps.Add(s.WireKbps / 1000);
            b.Gap.Add(moving ? s.FrameGapMsMax : null);
            b.Gpu.Add(s.GpuPercent);
            b.Enc.Add(s.EncoderPercent);
            b.HostGpu.Add(s.HostGpuPercent);
            b.Idr += idrNew;
        }

        track.IdrTotal += idrNew;
        track.DropsTotal += drops ?? 0;
        track.FecFailed += s.FecFailed ?? 0;
        track.FrameLossMax = Math.Max(track.FrameLossMax, s.FrameLossPct);
        if (s.PacketLossPct > 0)
        {
            track.LossSeconds++;
        }

        if (idrNew > 0)
        {
            AddEvent(s, "idr", "IDR requested (Moonlight dropped frames: slow decoder, unrepaired loss or window change)", count: idrNew);
        }

        if (s.FrameLossPct > 0)
        {
            AddEvent(s, "frames", "frames lost {0:0.0} %", s.FrameLossPct);
        }

        if (s.FecFailed > 0)
        {
            AddEvent(s, "fecfail", "FEC could not repair a block, packet loss up to {0:0.00} %", s.PacketLossPct ?? 0, count: (int)s.FecFailed.Value);
        }
        else if (s.PacketLossPct >= 1)
        {
            AddEvent(s, "loss", "packet loss up to {0:0.00} %", s.PacketLossPct.Value);
        }

        if (moving && s.FrameGapMsMax >= stallMs)
        {
            track.Stalls++;
            AddEvent(s, "gap", "pause between frames up to {0:0} ms", s.FrameGapMsMax.Value);
        }

        if (s.SentMsP99 is double sent)
        {
            // Where the time went, from the worst second of the merged moments.
            if (sent >= SentSpikeMs)
            {
                AddEvent(s, "enc", Inv($"host latency p99 up to {{0:0.0}} ms (cap {F1(s.CaptureMsP99)} enc {F1(s.EncoderMsP99)} q {F1(s.QueueMsP99)} send {F1(s.SendMsP99)}, frames up to {F0(s.FrameKbMax)} KB)"), sent);
            }
        }
        else if (s.EncodeMsP99 >= EncodeSpikeMs)
        {
            AddEvent(s, "enc", "host processing p99 up to {0:0.0} ms", s.EncodeMsP99);
        }

        if (drops > 0)
        {
            AddEvent(s, "drop", "host adapter discarded {0:0} outgoing packets", drops.Value, merge: Merge.Sum);
        }

        if (s.Link?.WifiSignalPercent is int signal && signal < WeakWifiPercent)
        {
            AddEvent(s, "wifi", Inv($"host Wi-Fi signal down to {{0:0}} % ({F0(s.Link.WifiRssiDbm)} dBm, {F0(s.Link.WifiTxMbps)} Mbit/s)"), signal, merge: Merge.Min);
        }

        if (s.HostVramUsedMb is double used && s.HostVramTotalMb is double size && size > 0 && used >= VramFullShare * size)
        {
            AddEvent(s, "vram", Inv($"VRAM nearly full: {{0:0.0}} of {size / 1024:0.0} GB (games page to system memory and stutter)"), used / 1024);
        }

        if (s.RttMs is double rtt)
        {
            var lowest = Math.Min(track.MinRtt ?? rtt, rtt);
            track.MinRtt = lowest;
            if (rtt >= lowest + PingRiseMs && rtt >= 2 * lowest)
            {
                AddEvent(s, "ping", Inv($"ping up to {{0:0}} ms (lowest {lowest:0})"), rtt);
            }
        }

        if (s.BitrateCapKbps != track.LastCap)
        {
            AddEvent(s, "cap", s.BitrateCapKbps is int cap
                ? Inv($"bitrate capped at {cap / 1000.0:0.#} Mbit/s ({s.CapReason ?? "?"})")
                : "bitrate cap lifted");
            track.LastCap = s.BitrateCapKbps;
        }
    }

    /// <param name="format">The line; with <paramref name="value"/> a composite format whose <c>{0}</c> takes the worst value.</param>
    private void AddEvent(RecordingSample s, string kind, string format, double? value = null, int count = 1, Merge merge = Merge.Max)
    {
        if (kind is not ("connect" or "disconnect" or "cap" or "link"))
        {
            // Events are in time order and only the last few seconds can merge.
            for (var i = _events.Count - 1; i >= 0 && s.At - _events[i].At <= TimeSpan.FromMinutes(1); i--)
            {
                var e = _events[i];
                if (e.Desk == s.Desk && e.Kind == kind && s.At - e.Last <= MergeWindow)
                {
                    e.Count += count;
                    var v = value ?? 0;
                    var worse = merge switch
                    {
                        Merge.Min => v < e.Value,
                        _ => v > e.Value,
                    };
                    // The line of the worst moment is kept with its value (its details belong to it).
                    if (merge == Merge.Sum)
                    {
                        e.Value += v;
                    }
                    else if (worse)
                    {
                        e.Value = v;
                        e.Format = format;
                    }

                    e.Last = s.At;
                    return;
                }
            }
        }

        if (_events.Count >= MaxEvents)
        {
            // Over the budget a serious moment pushes out the oldest minor one; anything else is only counted.
            var minor = Minor.Contains(kind) ? -1 : _events.FindIndex(e => Minor.Contains(e.Kind));
            if (minor < 0)
            {
                _dropped[kind] = _dropped.GetValueOrDefault(kind) + count;
                return;
            }

            var pushed = _events[minor];
            _dropped[pushed.Kind] = _dropped.GetValueOrDefault(pushed.Kind) + pushed.Count;
            _events.RemoveAt(minor);
        }

        _events.Add(new Event(s.At, s.Desk, kind, format) { Count = count, Value = value ?? 0, HasValue = value is not null, Last = s.At });
    }

    // ------------------------------------------------------------------ formatting

    private string Clock(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, _zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Minutes(TimeSpan span) => Math.Max(0, span.TotalMinutes).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static string F0(double? v) => v is double d ? d.ToString("0", CultureInfo.InvariantCulture) : "-";

    private static string F1(double? v) => v is double d ? d.ToString("0.0", CultureInfo.InvariantCulture) : "-";

    private static string F2(double? v) => v is double d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "-";

    /// <summary>How merged events combine their values: the worst is the largest or the smallest, or they add up.</summary>
    private enum Merge
    {
        Max,
        Min,
        Sum,
    }

    private static string Drops(Bucket b) => b.Drops is long d ? d.ToString(CultureInfo.InvariantCulture) : "-";

    private sealed class Event(DateTimeOffset at, string desk, string kind, string format)
    {
        public string Format { get; set; } = format;

        public DateTimeOffset At { get; } = at;

        public string Desk { get; } = desk;

        public string Kind { get; } = kind;

        public int Count { get; set; }

        /// <summary>The worst value of the merged moments, put into the format's <c>{0}</c>.</summary>
        public double Value { get; set; }

        public DateTimeOffset Last { get; set; }

        /// <summary>The line: the format with the worst value, or the format itself when it carries no value.</summary>
        public string Text() => HasValue ? string.Format(CultureInfo.InvariantCulture, Format, Value) : Format;

        public bool HasValue { get; init; }
    }

    private sealed class DeskTrack
    {
        public SortedDictionary<int, Bucket> Minutes { get; } = [];

        public Bucket Total { get; } = new();

        public List<string> Lines { get; } = [];

        /// <summary>The host adapters seen towards the desk's client, in order.</summary>
        public List<string> Links { get; } = [];

        public string? LastLink { get; set; }

        public long? LastDiscards { get; set; }

        public long DropsTotal { get; set; }

        /// <summary>The key frame count each running stream had at its last sample.</summary>
        public Dictionary<string, double> Idr { get; } = new(StringComparer.Ordinal);

        /// <summary>False until the first sample: streams seen then were already running.</summary>
        public bool Started { get; set; }

        public int? LastCap { get; set; }

        public double? MinRtt { get; set; }

        public int IdrTotal { get; set; }

        public double FecFailed { get; set; }

        public double FrameLossMax { get; set; }

        public int LossSeconds { get; set; }

        public int Stalls { get; set; }
    }

    private sealed class Bucket
    {
        public Stat Fps { get; } = new();

        public Stat Compose { get; } = new();

        public Stat EncP50 { get; } = new();

        public Stat EncP99 { get; } = new();

        public Stat Rtt { get; } = new();

        public Stat Packet { get; } = new();

        public Stat Fec { get; } = new();

        public Stat Mbps { get; } = new();

        public Stat Gap { get; } = new();

        public Stat Gpu { get; } = new();

        public Stat Enc { get; } = new();

        public Stat HostGpu { get; } = new();

        public Stat NewFps { get; } = new();

        public Stat Repeated { get; } = new();

        public Stat FrameKb { get; } = new();

        public Stat FrameKbMax { get; } = new();

        public Stat KeyKbMax { get; } = new();

        public Stat CapP99 { get; } = new();

        public Stat EncoderP99 { get; } = new();

        public Stat QueueP99 { get; } = new();

        public Stat SendP99 { get; } = new();

        public Stat SentP50 { get; } = new();

        public Stat SentP99 { get; } = new();

        public Stat SentMax { get; } = new();

        public Stat DeskVramMb { get; } = new();

        public Stat DeskCpu { get; } = new();

        public Stat WifiSignal { get; } = new();

        public Stat WifiRssi { get; } = new();

        public Stat WifiTx { get; } = new();

        public int Idr { get; set; }

        public int Keys { get; set; }

        /// <summary>Packets the host adapter discarded; null while its counter is unknown.</summary>
        public long? Drops { get; set; }
    }

    /// <summary>Host-wide numbers of one minute (every desk's samples carry them).</summary>
    private sealed class HostBucket
    {
        public Stat Cpu { get; } = new();

        public Stat CpuCore { get; } = new();

        public Stat Gpu { get; } = new();

        public Stat VramUsed { get; } = new();

        public Stat VramTotal { get; } = new();
    }

    /// <summary>Min, average and max of the values seen (nulls skipped).</summary>
    private sealed class Stat
    {
        private double _sum;
        private int _count;

        public double? Min { get; private set; }

        public double? Max { get; private set; }

        public double? Avg => _count > 0 ? _sum / _count : null;

        public void Add(double? value)
        {
            if (value is not double v || double.IsNaN(v))
            {
                return;
            }

            _sum += v;
            _count++;
            Min = Math.Min(Min ?? v, v);
            Max = Math.Max(Max ?? v, v);
        }
    }
}
