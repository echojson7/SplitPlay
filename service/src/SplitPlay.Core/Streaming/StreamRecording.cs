// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;

namespace SplitPlay.Core.Streaming;

/// <summary>
/// One worker heartbeat of a streaming desk as a <see cref="StreamRecording"/> keeps it (about once a second). Values a
/// worker or the GPU counters do not report are null.
/// </summary>
public sealed record RecordingSample
{
    public required DateTimeOffset At { get; init; }

    public required string Desk { get; init; }

    /// <summary>The stream(s) as one line ("Laptop 192.168.1.106 2560x1440@180 hevc 100 Mbit/s"); null without a client.</summary>
    public string? Stream { get; init; }

    public double Fps { get; init; }

    public double? ComposeHz { get; init; }

    public double EncodeMsP50 { get; init; }

    public double EncodeMsP99 { get; init; }

    public double? RttMs { get; init; }

    public double FrameLossPct { get; init; }

    public double? PacketLossPct { get; init; }

    public double? FecFailed { get; init; }

    public double? FecPercent { get; init; }

    public double WireKbps { get; init; }

    /// <summary>Key frames the clients asked for since their streams started (cumulative).</summary>
    public double? IdrRequests { get; init; }

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
}

/// <summary>
/// "Запись потока": collects the heartbeats of every streaming desk for a while and turns them into a short text that
/// fits in a chat message: one line per desk and minute (min, average and worst values) and a list of notable moments
/// (key frame requests, pauses, losses, bitrate cuts, clients coming and going). Thread-safe.
/// </summary>
public sealed class StreamRecording
{
    /// <summary>Notable moments kept; later ones are only counted.</summary>
    public const int MaxEvents = 60;

    /// <summary>A pause between frames that counts as a stall while the stream otherwise runs (at least 20 fps).</summary>
    public const double StallMs = 50;

    /// <summary>Host processing time (p99) that counts as a spike.</summary>
    public const double EncodeSpikeMs = 12;

    /// <summary>Events of one kind and desk this close together are merged into one line ("×3").</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly TimeZoneInfo _zone;
    private readonly string _header;
    private readonly SortedDictionary<string, DeskTrack> _desks = new(StringComparer.Ordinal);
    private readonly List<Event> _events = [];
    private int _droppedEvents;

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
    public DateTimeOffset? StoppedAt { get; private set; }

    /// <summary>Samples taken so far.</summary>
    public int Samples { get; private set; }

    public bool IsRunning(DateTimeOffset now)
    {
        lock (_gate)
        {
            return StoppedAt is null && now < EndsAt;
        }
    }

    public void Stop(DateTimeOffset now)
    {
        lock (_gate)
        {
            StoppedAt ??= now < EndsAt ? now : EndsAt;
        }
    }

    /// <summary>Adds one heartbeat; ignored once the recording has stopped or ended.</summary>
    public void Add(RecordingSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            if (StoppedAt is not null || sample.At < StartedAt)
            {
                return;
            }

            if (sample.At >= EndsAt)
            {
                StoppedAt = EndsAt;
                return;
            }

            Samples++;
            if (!_desks.TryGetValue(sample.Desk, out var track))
            {
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
            var end = StoppedAt ?? (now < EndsAt ? now : EndsAt);
            var sb = new StringBuilder();
            sb.Append(_header).Append('\n');
            sb.Append(Inv($"recorded {Clock(StartedAt)}–{Clock(end)} ({Minutes(end - StartedAt)} min), {Samples} samples\n"));
            if (_desks.Count == 0)
            {
                sb.Append("no stream was running\n");
            }

            foreach (var (desk, track) in _desks)
            {
                sb.Append('\n').Append("desk ").Append(desk).Append('\n');
                foreach (var stream in track.Streams)
                {
                    sb.Append("  stream ").Append(stream).Append('\n');
                }

                sb.Append("  min  fps min/avg  hz   enc p50/p99max  ping avg/max  pkt% avg/max  fec%  Mbit/s  idr  gap max  gpu/enc%  host gpu%\n");
                foreach (var (minute, b) in track.Minutes)
                {
                    sb.Append(Inv($"  {minute,3}  {F0(b.Fps.Min)}/{F0(b.Fps.Avg),-6} {F0(b.Compose.Avg),4}  {F1(b.EncP50.Avg)}/{F1(b.EncP99.Max),-9}  {F0(b.Rtt.Avg)}/{F0(b.Rtt.Max),-9}  {F2(b.Packet.Avg)}/{F2(b.Packet.Max),-7}  {F0(b.Fec.Avg),4}  {F0(b.Mbps.Avg),6}  {b.Idr,3}  {F0(b.Gap.Max),7}  {F0(b.Gpu.Avg)}/{F0(b.Enc.Avg),-6}  {F0(b.HostGpu.Avg)}\n"));
                }

                var t = track.Total;
                sb.Append(Inv($"  total: fps avg {F0(t.Fps.Avg)} (min {F0(t.Fps.Min)}), hz avg {F0(t.Compose.Avg)}, enc p99 max {F1(t.EncP99.Max)} ms, ping avg {F0(t.Rtt.Avg)} max {F0(t.Rtt.Max)} ms, "));
                sb.Append(Inv($"packet loss avg {F2(t.Packet.Avg)} % (seconds with loss {track.LossSeconds}), frames lost max {F1(track.FrameLossMax)} %, FEC failed {F0(track.FecFailed)}, "));
                sb.Append(Inv($"IDR requests {track.IdrTotal}, stalls ≥{StallMs:0} ms {track.Stalls}, gap max {F0(t.Gap.Max)} ms\n"));
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
                    sb.Append(Clock(e.At)).Append(' ').Append(e.Desk).Append(' ').Append(e.Text);
                    if (e.Count > 1)
                    {
                        sb.Append(Inv($" ×{e.Count}"));
                    }

                    sb.Append('\n');
                }

                if (_droppedEvents > 0)
                {
                    sb.Append(Inv($"… {_droppedEvents} more\n"));
                }
            }

            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ collection

    private void Track(DeskTrack track, RecordingSample s)
    {
        if (s.Stream != track.LastStream)
        {
            if (s.Stream is not null)
            {
                if (!track.Streams.Contains(s.Stream, StringComparer.Ordinal))
                {
                    track.Streams.Add(s.Stream);
                }

                AddEvent(s, "connect", $"client connected: {s.Stream}");
            }
            else
            {
                AddEvent(s, "disconnect", "client disconnected");
            }

            track.LastStream = s.Stream;
            track.LastIdr = null;
        }

        if (s.Stream is null)
        {
            return;
        }

        var minute = (int)((s.At - StartedAt).TotalMinutes);
        if (!track.Minutes.TryGetValue(minute, out var bucket))
        {
            bucket = new Bucket();
            track.Minutes[minute] = bucket;
        }

        // A new stream counts its key frame requests from 0 again.
        var idrNew = 0;
        if (s.IdrRequests is double idr)
        {
            var before = track.LastIdr ?? 0;
            idrNew = idr >= before ? (int)(idr - before) : (int)idr;
            track.LastIdr = idr;
        }

        foreach (var b in new[] { bucket, track.Total })
        {
            b.Fps.Add(s.Fps);
            b.Compose.Add(s.ComposeHz);
            b.EncP50.Add(s.EncodeMsP50);
            b.EncP99.Add(s.EncodeMsP99);
            b.Rtt.Add(s.RttMs);
            b.Packet.Add(s.PacketLossPct);
            b.Fec.Add(s.FecPercent);
            b.Mbps.Add(s.WireKbps / 1000);
            // A static picture sends few frames: its pauses say nothing about pacing.
            b.Gap.Add(s.Fps >= 20 ? s.FrameGapMsMax : null);
            b.Gpu.Add(s.GpuPercent);
            b.Enc.Add(s.EncoderPercent);
            b.HostGpu.Add(s.HostGpuPercent);
            b.Idr += idrNew;
        }

        track.IdrTotal += idrNew;
        track.FecFailed += s.FecFailed ?? 0;
        track.FrameLossMax = Math.Max(track.FrameLossMax, s.FrameLossPct);
        if (s.PacketLossPct > 0)
        {
            track.LossSeconds++;
        }

        if (idrNew > 0)
        {
            AddEvent(s, "idr", "IDR requested (client dropped frames: slow decoder or unrepaired loss)", idrNew);
        }

        if (s.FrameLossPct > 0)
        {
            AddEvent(s, "frames", Inv($"frames lost {F1(s.FrameLossPct)} %"));
        }

        if (s.FecFailed > 0)
        {
            AddEvent(s, "fecfail", Inv($"FEC could not repair {F0(s.FecFailed)} block(s), packet loss {F2(s.PacketLossPct)} %"));
        }
        else if (s.PacketLossPct >= 1)
        {
            AddEvent(s, "loss", Inv($"packet loss {F2(s.PacketLossPct)} %"));
        }

        if (s.FrameGapMsMax >= StallMs && s.Fps >= 20)
        {
            track.Stalls++;
            AddEvent(s, "gap", Inv($"pause between frames {F0(s.FrameGapMsMax)} ms (fps {F0(s.Fps)})"));
        }

        if (s.EncodeMsP99 >= EncodeSpikeMs)
        {
            AddEvent(s, "enc", Inv($"host processing p99 {F1(s.EncodeMsP99)} ms"));
        }

        if (s.RttMs is double rtt)
        {
            track.MinRtt = Math.Min(track.MinRtt ?? rtt, rtt);
            if (rtt >= 2 * track.MinRtt + 10)
            {
                AddEvent(s, "ping", Inv($"ping {F0(rtt)} ms (lowest {F0(track.MinRtt)})"));
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

    private void AddEvent(RecordingSample s, string kind, string text, int count = 1)
    {
        for (var i = _events.Count - 1; i >= 0; i--)
        {
            var e = _events[i];
            if (s.At - e.Last > MergeWindow)
            {
                break;
            }

            if (e.Desk == s.Desk && e.Kind == kind && kind is not ("connect" or "disconnect" or "cap"))
            {
                e.Count += count;
                e.Last = s.At;
                return;
            }
        }

        if (_events.Count >= MaxEvents)
        {
            _droppedEvents++;
            return;
        }

        _events.Add(new Event(s.At, s.Desk, kind, text) { Count = count, Last = s.At });
    }

    // ------------------------------------------------------------------ formatting

    private string Clock(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, _zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Minutes(TimeSpan span) => Math.Max(0, span.TotalMinutes).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static string F0(double? v) => v is double d ? d.ToString("0", CultureInfo.InvariantCulture) : "-";

    private static string F1(double? v) => v is double d ? d.ToString("0.0", CultureInfo.InvariantCulture) : "-";

    private static string F2(double? v) => v is double d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "-";

    private sealed class Event(DateTimeOffset at, string desk, string kind, string text)
    {
        public DateTimeOffset At { get; } = at;

        public string Desk { get; } = desk;

        public string Kind { get; } = kind;

        public string Text { get; } = text;

        public int Count { get; set; }

        public DateTimeOffset Last { get; set; }
    }

    private sealed class DeskTrack
    {
        public SortedDictionary<int, Bucket> Minutes { get; } = [];

        public Bucket Total { get; } = new();

        public List<string> Streams { get; } = [];

        public string? LastStream { get; set; }

        public double? LastIdr { get; set; }

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

        public int Idr { get; set; }
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
