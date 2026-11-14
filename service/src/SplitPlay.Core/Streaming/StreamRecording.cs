// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;

namespace SplitPlay.Core.Streaming;

/// <summary>One Moonlight stream of a <see cref="RecordingSample"/>.</summary>
/// <param name="Key">Stable while the stream runs (the worker's launch id, or address and start time).</param>
/// <param name="Line">What the report shows ("Laptop 192.168.1.106 2560x1440@180 hevc 100 Mbit/s").</param>
/// <param name="IdrRequests">Key frames the client asked for since the stream started; null when not measured.</param>
public sealed record RecordingStream(string Key, string Line, double? IdrRequests);

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

    /// <summary>A ping this much above the lowest one seen (and at least twice it) is a spike.</summary>
    public const double PingRiseMs = 30;

    /// <summary>Events of one kind and desk this close together are merged into one line ("×3", worst value kept).</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> Minor = new(StringComparer.Ordinal) { "loss", "ping", "enc" };

    private readonly object _gate = new();
    private readonly TimeZoneInfo _zone;
    private readonly string _header;
    private readonly SortedDictionary<string, DeskTrack> _desks = new(StringComparer.Ordinal);
    private readonly List<Event> _events = [];
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

            foreach (var (desk, track) in _desks)
            {
                sb.Append('\n').Append("desk ").Append(desk).Append('\n');
                foreach (var line in track.Lines)
                {
                    sb.Append("  stream ").Append(line).Append('\n');
                }

                sb.Append("  min  fps min/avg  hz   enc p50/p99max  ping avg/max  pkt% avg/max  fec%  Mbit/s  idr  gap max  gpu/enc%  host gpu%\n");
                foreach (var (minute, b) in track.Minutes)
                {
                    sb.Append(Inv($"  {minute,3}  {F0(b.Fps.Min)}/{F0(b.Fps.Avg),-6} {F0(b.Compose.Avg),4}  {F1(b.EncP50.Avg)}/{F1(b.EncP99.Max),-9}  {F0(b.Rtt.Avg)}/{F0(b.Rtt.Max),-9}  {F2(b.Packet.Avg)}/{F2(b.Packet.Max),-7}  {F0(b.Fec.Avg),4}  {F0(b.Mbps.Avg),6}  {b.Idr,3}  {F0(b.Gap.Max),7}  {F0(b.Gpu.Avg)}/{F0(b.Enc.Avg),-6}  {F0(b.HostGpu.Avg)}\n"));
                }

                var t = track.Total;
                sb.Append(Inv($"  total: fps avg {F0(t.Fps.Avg)} (min {F0(t.Fps.Min)}), hz avg {F0(t.Compose.Avg)}, enc p99 max {F1(t.EncP99.Max)} ms, ping avg {F0(t.Rtt.Avg)} max {F0(t.Rtt.Max)} ms, "));
                sb.Append(Inv($"packet loss avg {F2(t.Packet.Avg)} % (seconds with loss {track.LossSeconds}), frames lost max {F1(track.FrameLossMax)} %, FEC failed {F0(track.FecFailed)}, "));
                sb.Append(Inv($"IDR requests {track.IdrTotal}, stalls {track.Stalls}, gap max {F0(t.Gap.Max)} ms\n"));
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
            b.Gap.Add(moving ? s.FrameGapMsMax : null);
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

        if (s.EncodeMsP99 >= EncodeSpikeMs)
        {
            AddEvent(s, "enc", "host processing p99 up to {0:0.0} ms", s.EncodeMsP99);
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
    private void AddEvent(RecordingSample s, string kind, string format, double? value = null, int count = 1)
    {
        if (kind is not ("connect" or "disconnect" or "cap"))
        {
            // Events are in time order and only the last few seconds can merge.
            for (var i = _events.Count - 1; i >= 0 && s.At - _events[i].At <= TimeSpan.FromMinutes(1); i--)
            {
                var e = _events[i];
                if (e.Desk == s.Desk && e.Kind == kind && s.At - e.Last <= MergeWindow)
                {
                    e.Count += count;
                    e.Value = Math.Max(e.Value, value ?? 0);
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

    private sealed class Event(DateTimeOffset at, string desk, string kind, string format)
    {
        public DateTimeOffset At { get; } = at;

        public string Desk { get; } = desk;

        public string Kind { get; } = kind;

        public int Count { get; set; }

        /// <summary>The worst value of the merged moments, put into the format's <c>{0}</c>.</summary>
        public double Value { get; set; }

        public DateTimeOffset Last { get; set; }

        /// <summary>The line: the format with the worst value, or the format itself when it carries no value.</summary>
        public string Text() => HasValue ? string.Format(CultureInfo.InvariantCulture, format, Value) : format;

        public bool HasValue { get; init; }
    }

    private sealed class DeskTrack
    {
        public SortedDictionary<int, Bucket> Minutes { get; } = [];

        public Bucket Total { get; } = new();

        public List<string> Lines { get; } = [];

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
