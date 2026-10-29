// SPDX-License-Identifier: MIT
using System.Text.Json;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;

namespace SplitPlay.Service.Supervision;

/// <summary>Live state of one desk. All fields are guarded by <see cref="Gate"/>.</summary>
internal sealed class DeskRuntime
{
    public DeskRuntime(DeskConfig config, DeskStateMachine machine)
    {
        Config = config;
        Machine = machine;
    }

    /// <summary>Guards every field below. Re-entrant (Monitor).</summary>
    public object Gate { get; } = new();

    public string Id => Config.Id;

    /// <summary>Current configuration. Never mutated in place: replaced with a fresh copy on change.</summary>
    public DeskConfig Config { get; set; }

    public DeskStateMachine Machine { get; }

    public int? SessionId { get; set; }

    public int? ChildPid { get; set; }

    /// <summary>Free-form state from the last heartbeat (<c>idle</c>, <c>streaming</c>, …).</summary>
    public string? ChildState { get; set; }

    public DeskMetricsDto Metrics { get; set; } = new();

    /// <summary>
    /// The worker's running Moonlight streams, oldest first: added by <c>client_connected</c>, removed by
    /// <c>client_disconnected</c>, replaced by every heartbeat that carries <c>sessions</c>. Cleared when the worker
    /// stops or restarts.
    /// </summary>
    public List<IpcStreamSession> Streams { get; } = [];

    /// <summary>Ids of streams that ended recently: a heartbeat written before the event must not bring them back.</summary>
    private readonly Queue<long> _endedStreams = new();

    private const int EndedStreamsKept = 32;

    public DateTimeOffset LastHeartbeat { get; set; }

    /// <summary>The worker's control connection while it is connected (used for pair, list_clients, …).</summary>
    public IpcConnection? Connection { get; set; }

    /// <summary>The seat while it runs. It outlives worker restarts and is replaced only when it fails.</summary>
    public ChildChannel? Seat { get; set; }

    /// <summary>Cancelled when the seat's session ends (stops the per-session janitor).</summary>
    public CancellationTokenSource? SeatScope { get; set; }

    /// <summary>Set to end the current worker run without a worker fault (e.g. the session changed after a reconnect).</summary>
    public TaskCompletionSource<string>? WorkerAbort { get; set; }

    /// <summary>The seat reported <c>session_lost</c> and has not reported <c>session_restored</c> yet.</summary>
    public bool SessionLost { get; set; }

    /// <summary>Counts <c>session_lost</c> reports, so that a timer started for one loss ignores later ones.</summary>
    public int SessionLosses { get; set; }

    /// <summary>The last provisioning call; one that timed out may still be running and blocks the next.</summary>
    public Task? ProvisionCall { get; set; }

    /// <summary>The one-time relogon after the first keyboard layout copy has been done in this run.</summary>
    public bool RelogonDone { get; set; }

    public CancellationTokenSource? RunCts { get; set; }

    public Task? RunTask { get; set; }

    /// <summary>True while the run loop owns the desk (set by Start, cleared by the loop when it ends).</summary>
    public bool Running { get; set; }

    /// <summary>Worker launch attempts since the last start.</summary>
    public int Attempt { get; set; }

    /// <summary>Seat launches since the service started (a field for <see cref="Interlocked"/>).</summary>
    public int SeatAttempts;

    /// <summary>True when a password for the desk user is stored (mirrors the secret file; updated by the supervisor).</summary>
    public bool HasPassword { get; set; }

    /// <summary>A non-fatal provisioning problem (e.g. the picked account's password can expire), shown as lastError.</summary>
    public string? Warning { get; set; }

    /// <summary>The profile's resolution changed while running and the seat could not resize the session.</summary>
    public bool ResolutionPendingRestart { get; set; }

    public DeskDto ToDto()
    {
        lock (Gate)
        {
            return new DeskDto
            {
                Id = Config.Id,
                Name = Config.Name,
                User = Config.User,
                UserManaged = Config.UserManaged,
                HasPassword = HasPassword,
                ResolutionPendingRestart = ResolutionPendingRestart,
                State = Machine.State,
                SessionId = SessionId,
                BasePort = Config.BasePort,
                Autostart = Config.Autostart,
                Gpu = Config.Gpu,
                Profile = Config.Profile,
                DisableBluetoothUserService = Config.DisableBluetoothUserService,
                SteamIsolation = Config.SteamIsolationEnabled,
                HideFromLogon = Config.HidesFromLogon(Config.UserManaged != false),
                WorkerSettings = Config.WorkerSettings,
                Metrics = Metrics,
                Stream = Streams.Count > 0 ? ToStreamDto(Streams[0]) : null,
                LastError = Machine.LastError ?? Warning,
            };
        }
    }

    /// <summary>
    /// A worker heartbeat: liveness, child state, metrics and (when present) the authoritative stream list. Ignored
    /// unless <paramref name="from"/> is the desk's current worker connection: a torn-down channel can still deliver
    /// buffered messages after <see cref="ClearStreams"/>.
    /// </summary>
    public WorkerUpdate ApplyHeartbeat(IpcConnection from, HeartbeatMessage heartbeat, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        lock (Gate)
        {
            if (!IsCurrent(from))
            {
                return WorkerUpdate.Ignored;
            }

            LastHeartbeat = now;
            ChildState = heartbeat.State;
            Metrics = MapMetrics(heartbeat.Metrics);
            var changed = false;
            if (heartbeat.Sessions is { } sessions)
            {
                changed = ReplaceStreams(IpcStreamSession.ListFromJson(sessions));
            }
            else if (heartbeat.Metrics?.ContainsKey("clients") == true && Metrics.Clients == 0)
            {
                // A worker without "sessions" that reports no clients has no stream either.
                changed = ClearStreams();
            }

            return Settle(changed);
        }
    }

    /// <summary><c>client_connected</c> from <paramref name="from"/>; ignored unless it is the current worker connection.</summary>
    public WorkerUpdate ApplyClientConnected(IpcConnection from, IpcStreamSession stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (Gate)
        {
            return IsCurrent(from) ? Settle(AddStream(stream)) : WorkerUpdate.Ignored;
        }
    }

    /// <summary><c>client_disconnected</c> from <paramref name="from"/>; ignored unless it is the current worker connection.</summary>
    public WorkerUpdate ApplyClientDisconnected(IpcConnection from, IpcStreamSession stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (Gate)
        {
            if (!IsCurrent(from))
            {
                return WorkerUpdate.Ignored;
            }

            var changed = RemoveStream(stream);
            if (!changed && stream.Id is null && stream.Address is null && Streams.Count == 1)
            {
                // An event without identifying fields (an older worker): the only stream is the one that ended.
                changed = ClearStreams();
            }

            return Settle(changed);
        }
    }

    /// <summary>True while <paramref name="from"/> is the desk's connected worker. Caller holds <see cref="Gate"/>.</summary>
    public bool IsCurrent(IpcConnection from) => Connection is not null && ReferenceEquals(Connection, from);

    /// <summary>
    /// Ready and Streaming follow the stream list: Streaming while at least one Moonlight stream runs. A transition
    /// publishes the desk itself, so <see cref="WorkerUpdate.Publish"/> is returned only for a list change without one.
    /// </summary>
    private WorkerUpdate Settle(bool listChanged)
    {
        var transitioned = Machine.State switch
        {
            DeskState.Ready when Streams.Count > 0 => Machine.TryFire(DeskTrigger.ClientConnected, out _),
            DeskState.Streaming when Streams.Count == 0 => Machine.TryFire(DeskTrigger.ClientDisconnected, out _),
            _ => false,
        };
        return listChanged && !transitioned ? WorkerUpdate.Publish : WorkerUpdate.Applied;
    }

    /// <summary><c>client_connected</c>: adds the stream, or updates it when it is already listed. True when the list changed.</summary>
    public bool AddStream(IpcStreamSession stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (Gate)
        {
            var index = Streams.FindIndex(s => s.SameStream(stream));
            if (index >= 0)
            {
                if (Streams[index] == stream)
                {
                    return false;
                }

                Streams[index] = stream;
                return true;
            }

            Streams.Add(stream);
            SortStreams();
            return true;
        }
    }

    /// <summary><c>client_disconnected</c>: removes the stream. True when the list changed.</summary>
    public bool RemoveStream(IpcStreamSession stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (Gate)
        {
            if (stream.Id is long id)
            {
                _endedStreams.Enqueue(id);
                while (_endedStreams.Count > EndedStreamsKept)
                {
                    _endedStreams.Dequeue();
                }
            }

            return Streams.RemoveAll(s => s.SameStream(stream)) > 0;
        }
    }

    /// <summary>A heartbeat with <c>sessions</c>: the worker's list is authoritative. True when the list changed.</summary>
    public bool ReplaceStreams(IReadOnlyList<IpcStreamSession> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        lock (Gate)
        {
            var next = streams.Where(s => s.Id is not long id || !_endedStreams.Contains(id)).ToList();
            var before = Streams.ToList();
            Streams.Clear();
            Streams.AddRange(next);
            SortStreams();
            return !before.SequenceEqual(Streams);
        }
    }

    /// <summary>The worker stopped or restarts: no stream survives it. True when the list changed.</summary>
    public bool ClearStreams()
    {
        lock (Gate)
        {
            _endedStreams.Clear();
            if (Streams.Count == 0)
            {
                return false;
            }

            Streams.Clear();
            return true;
        }
    }

    private void SortStreams()
    {
        // OrderBy is stable: streams without a start time keep their order, after the timed ones.
        var sorted = Streams.OrderBy(s => s.StartedAt ?? DateTimeOffset.MaxValue).ToList();
        Streams.Clear();
        Streams.AddRange(sorted);
    }

    public static DeskStreamDto ToStreamDto(IpcStreamSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new DeskStreamDto
        {
            Client = s.Client,
            ClientId = s.ClientUuid,
            Address = s.Address,
            Width = s.Width,
            Height = s.Height,
            Fps = s.FpsRequested,
            Codec = s.Codec,
            BitrateKbps = s.BitrateKbpsRequested,
            Hdr = s.Hdr,
            StartedAt = s.StartedAt,
        };
    }

    /// <summary>Maps heartbeat metrics: known keys to camelCase fields, unknown keys passed through unchanged.</summary>
    public static DeskMetricsDto MapMetrics(IReadOnlyDictionary<string, JsonElement>? metrics)
    {
        var dto = new DeskMetricsDto();
        if (metrics is null)
        {
            return dto;
        }

        foreach (var (key, value) in metrics)
        {
            var number = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : (double?)null;
            switch (key)
            {
                case "fps" when number is not null:
                    dto.Fps = number.Value;
                    break;
                case "bitrate_kbps" when number is not null:
                    dto.BitrateKbps = number.Value;
                    break;
                case "encode_ms_p50" when number is not null:
                    dto.EncodeMsP50 = number.Value;
                    break;
                case "encode_ms_p99" when number is not null:
                    dto.EncodeMsP99 = number.Value;
                    break;
                case "loss_pct" when number is not null:
                    dto.LossPct = number.Value;
                    break;
                case "rtt_ms":
                    // null (or anything but a number): no stream has a measured round trip yet.
                    dto.RttMs = number;
                    break;
                case "clients" when number is not null:
                    dto.Clients = number.Value;
                    break;
                default:
                    dto.Extra ??= [];
                    dto.Extra[key] = value.Clone();
                    break;
            }
        }

        return dto;
    }

    /// <summary>Converts the configuration profile to the pipe's snake_case profile object.</summary>
    public static IpcProfile ToIpcProfile(ProfileConfig p) => new()
    {
        Fps = p.Fps,
        Width = p.Width,
        Height = p.Height,
        BitrateKbps = p.BitrateKbps,
        Codec = p.Codec,
        FecPercent = p.FecPercent,
        MinFps = p.MinFps,
        DpbFrames = p.DpbFrames,
        PacingMbps = p.PacingMbps,
        GpuPriority = p.GpuPriority,
        GpuLuid = p.GpuLuid,
        CpuSet = p.CpuSet,
    };
}

/// <summary>What applying a worker message did.</summary>
internal enum WorkerUpdate
{
    /// <summary>The message came from a worker connection that is no longer the desk's; nothing changed.</summary>
    Ignored,

    /// <summary>Applied; nothing to publish (or a state transition already published the desk).</summary>
    Applied,

    /// <summary>Applied and the stream list changed: publish the desk.</summary>
    Publish,
}
