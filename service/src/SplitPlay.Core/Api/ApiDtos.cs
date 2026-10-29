// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Lifecycle;

namespace SplitPlay.Core.Api;

// Web API v1 data transfer objects (docs/contracts/web-api.md). camelCase on the wire.

/// <summary><c>GET /health</c>.</summary>
public sealed record HealthDto(string Status, string Version, long UptimeSeconds);

/// <summary>Error body: <c>{"error":{"code":"...","message":"..."}}</c>.</summary>
public sealed record ApiErrorResponse(ApiError Error)
{
    public static ApiErrorResponse Of(string code, string message) => new(new ApiError(code, message));
}

public sealed record ApiError(string Code, string Message);

/// <summary>Error codes used by the service.</summary>
public static class ApiErrorCodes
{
    public const string NotFound = "not_found";
    public const string AlreadyExists = "already_exists";
    public const string ValidationFailed = "validation_failed";
    public const string InvalidState = "invalid_state";
    public const string BadRequest = "bad_request";
    public const string Forbidden = "forbidden";
    public const string Unauthorized = "unauthorized";
    public const string InvalidPassword = "invalid_password";
    public const string CsrfFailed = "csrf_failed";
    public const string NotImplemented = "not_implemented";
    public const string ChildUnavailable = "child_unavailable";

    /// <summary>The desk's Windows user is the one signed in at the console (409).</summary>
    public const string ConsoleUser = "console_user";

    /// <summary><c>deleteUser=true</c> on an account SplitPlay did not create (409).</summary>
    public const string UserNotManaged = "user_not_managed";
    public const string ChildError = "child_error";
    public const string Internal = "internal";
}

/// <summary>Live per-desk metrics. Known keys are camelCase; unknown keys reported by the worker pass through as-is.</summary>
public sealed class DeskMetricsDto
{
    public double Fps { get; set; }

    public double BitrateKbps { get; set; }

    /// <summary>Median host capture-to-encoded time over the last heartbeat interval, ms.</summary>
    public double EncodeMsP50 { get; set; }

    public double EncodeMsP99 { get; set; }

    /// <summary>Frames the client reported lost, percent of the frames sent in the last interval.</summary>
    public double LossPct { get; set; }

    /// <summary>Round-trip time to the Moonlight client (ENet control peer), ms; null while nothing measured it.</summary>
    public double? RttMs { get; set; }

    public double Clients { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// The Moonlight stream a desk is serving (<c>desk.stream</c>): who is connected and what the client asked for. Live
/// numbers are in <see cref="DeskMetricsDto"/>.
/// </summary>
public sealed record DeskStreamDto
{
    /// <summary>The paired client's name, null when the worker does not know it.</summary>
    public string? Client { get; init; }

    /// <summary>The paired client's id (as in <c>GET /desks/{id}/clients</c>), null when unknown.</summary>
    public string? ClientId { get; init; }

    /// <summary>Client IP address.</summary>
    public string? Address { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Requested frame rate.</summary>
    public int Fps { get; init; }

    /// <summary><c>h264</c>, <c>hevc</c> or <c>av1</c>.</summary>
    public string? Codec { get; init; }

    /// <summary>Requested bitrate (Moonlight's bitrate setting).</summary>
    public int BitrateKbps { get; init; }

    public bool Hdr { get; init; }

    public DateTimeOffset? StartedAt { get; init; }
}

/// <summary>Desk object returned by the desk endpoints.</summary>
public sealed class DeskDto
{
    public string Id { get; set; } = "";

    public string? Name { get; set; }

    public string? User { get; set; }

    /// <summary>See <see cref="DeskConfig.UserManaged"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UserManaged { get; set; }

    /// <summary>True when a password for <see cref="User"/> is stored. The password itself is never returned.</summary>
    public bool HasPassword { get; set; }

    /// <summary>
    /// True when <c>profile.width/height</c> changed while the desk was running and the seat could not resize the
    /// RDP session (no <c>set_mode</c> support): the new resolution applies after a restart.
    /// </summary>
    public bool ResolutionPendingRestart { get; set; }

    public DeskState State { get; set; }

    public int? SessionId { get; set; }

    public int? BasePort { get; set; }

    public bool Autostart { get; set; }

    public string Gpu { get; set; } = "auto";

    public ProfileConfig Profile { get; set; } = new();

    public bool DisableBluetoothUserService { get; set; } = true;

    /// <summary>See <see cref="DeskConfig.SteamIsolation"/>; always set in responses.</summary>
    public bool SteamIsolation { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? WorkerSettings { get; set; }

    public DeskMetricsDto Metrics { get; set; } = new();

    /// <summary>The stream the desk is serving, null when no Moonlight client is connected (the earliest one if several are).</summary>
    public DeskStreamDto? Stream { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// One entry of <c>GET /host/users</c>: an enabled local account that can be picked as a desk user (built-in accounts
/// such as Administrator, Guest, DefaultAccount and WDAGUtilityAccount are left out).
/// </summary>
public sealed record HostUserDto(string Name, string? FullName, bool IsAdmin, bool IsConsoleUser, bool CreatedBySplitPlay);

/// <summary><c>GET /host/network</c>: LAN IPv4 addresses of the host, those with a default gateway first.</summary>
public sealed record HostNetworkDto(IReadOnlyList<string> Addresses);

/// <summary><c>GET /auth/session</c>: <c>{setupRequired, authenticated, csrfToken?}</c>.</summary>
public sealed record AuthSessionDto(
    bool SetupRequired,
    bool Authenticated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CsrfToken);

/// <summary><c>POST /auth/login</c> body.</summary>
public sealed record LoginRequest(string? Password);

/// <summary><c>POST /auth/login</c> (and first-time <c>POST /auth/password</c>) answer.</summary>
public sealed record LoginResponse(string CsrfToken);

/// <summary><c>POST /auth/password</c> body; <c>currentPassword</c> is omitted when setting the first password.</summary>
public sealed record PasswordRequest(string? NewPassword, string? CurrentPassword);

/// <summary><c>POST /desks/{id}/pair</c>.</summary>
public sealed record PairRequest(string? Pin, string? Name);

/// <summary>A paired Moonlight client.</summary>
public sealed record ClientDto(
    string Id,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PairedAt = null);

/// <summary>One GPU in <see cref="HostMetricsDto"/>.</summary>
public sealed record GpuDto(
    string Luid,
    string Name,
    double? UtilizationPercent,
    double? EncoderPercent,
    double? VramUsedMb,
    double? VramTotalMb);

/// <summary>Host-level metrics. Optional values are null when not measured yet.</summary>
public sealed record HostMetricsDto(
    double? CpuPercent,
    double? MemoryUsedMb,
    double? MemoryTotalMb,
    double? ComposeHz,
    IReadOnlyList<GpuDto> Gpus);

/// <summary>Metrics of one desk inside <see cref="MetricsDto"/>.</summary>
public sealed record DeskMetricsEntryDto(
    string Id,
    double Fps,
    double BitrateKbps,
    double EncodeMsP50,
    double EncodeMsP99,
    double LossPct,
    double? RttMs,
    double Clients)
{
    public static DeskMetricsEntryDto From(string id, DeskMetricsDto m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(id, m.Fps, m.BitrateKbps, m.EncodeMsP50, m.EncodeMsP99, m.LossPct, m.RttMs, m.Clients);
    }
}

/// <summary><c>GET /metrics</c>.</summary>
public sealed record MetricsDto(HostMetricsDto Host, IReadOnlyList<DeskMetricsEntryDto> Desks);

/// <summary>One line of <c>GET /logs</c>.</summary>
public sealed record LogLineDto(DateTimeOffset Time, string Level, string Component, string? Desk, string Message);

/// <summary>WebSocket push: <c>{"type":"desk","desk":{...}}</c>.</summary>
public sealed record WsDeskEvent(DeskDto Desk)
{
    [JsonPropertyOrder(-1)]
    public string Type => "desk";
}

/// <summary>WebSocket push: <c>{"type":"metrics","host":{...},"desks":[...]}</c>.</summary>
public sealed record WsMetricsEvent(HostMetricsDto Host, IReadOnlyList<DeskMetricsEntryDto> Desks)
{
    [JsonPropertyOrder(-1)]
    public string Type => "metrics";
}
