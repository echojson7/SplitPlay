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

    public DeskDto ToDto()
    {
        lock (Gate)
        {
            return new DeskDto
            {
                Id = Config.Id,
                Name = Config.Name,
                User = Config.User,
                State = Machine.State,
                SessionId = SessionId,
                BasePort = Config.BasePort,
                Autostart = Config.Autostart,
                Gpu = Config.Gpu,
                Profile = Config.Profile,
                DisableBluetoothUserService = Config.DisableBluetoothUserService,
                WorkerSettings = Config.WorkerSettings,
                Metrics = Metrics,
                LastError = Machine.LastError,
            };
        }
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
                case "encode_ms_p99" when number is not null:
                    dto.EncodeMsP99 = number.Value;
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
