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
    public const string TooManyAttempts = "too_many_attempts";
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

    /// <summary>
    /// Frames DWM composed per second in the desk session, as Desktop Duplication reports them: the real composition
    /// rate (at most 1000 / <c>DWMFRAMEINTERVAL</c>, less when the game renders slower). Null without a stream.
    /// </summary>
    public double? CaptureFps { get; set; }

    /// <summary>
    /// Where the desk's keyboard and mouse go now (worker metric <c>input</c>): <c>windows</c>, <c>rdp</c>,
    /// <c>rdp_mixed</c> (relative motion stays on SendInput: the RDP server has no relative mouse PDUs) or
    /// <c>rdp_unavailable</c> (the seat cannot send input now, SendInput is used). Null from an older worker.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Input { get; set; }

    /// <summary>
    /// Video packets lost on the way, percent of the packets sent, from the client's FEC reports (lost or not, before
    /// FEC repairs them). Null from a worker that does not measure it.
    /// </summary>
    public double? PacketLossPct { get; set; }

    /// <summary>The worst FEC block of the interval: lost packets per data packet, percent (the FEC share it needed).</summary>
    public double? BurstLossPct { get; set; }

    /// <summary>FEC blocks the client repaired with parity in the interval.</summary>
    public double? FecRecovered { get; set; }

    /// <summary>FEC blocks the client could not repair in the interval.</summary>
    public double? FecFailed { get; set; }

    /// <summary>FEC share the worker sends now, percent.</summary>
    public double? FecPercent { get; set; }

    /// <summary>The bitrate the encoder runs at, kbit/s; null when it cannot change it live (or no stream runs).</summary>
    public double? EncoderKbps { get; set; }

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

/// <summary>
/// The HDR state of a desk session's display (<c>desk.hdr</c>), from the worker's <c>display_hdr</c> probe.
/// <see cref="State"/> is <c>enabled</c> (Windows composes the display in HDR10, so a Moonlight client that asks for HDR
/// gets HDR), <c>off</c> (the display supports HDR but it is switched off), <c>unsupported</c> or <c>unknown</c>; see
/// <see cref="DeskHdrStates"/>. The other fields describe the display that decides the state (one in HDR, else one that
/// supports it, else the first); null when not reported.
/// </summary>
public sealed record DeskHdrDto
{
    public string State { get; init; } = DeskHdrStates.Unknown;

    /// <summary>The display's name as Windows shows it (an RDP session's display usually has none).</summary>
    public string? Monitor { get; init; }

    /// <summary>The GPU that renders the session.</summary>
    public string? Adapter { get; init; }

    public int? BitsPerColor { get; init; }

    /// <summary>The display's peak luminance as DXGI reports it, nits.</summary>
    public double? MaxNits { get; init; }

    /// <summary>When the worker last reported a change.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>The stream adaptation of a desk (<c>desk.adaptive</c>).</summary>
public sealed record DeskAdaptiveDto
{
    /// <summary>FEC share sent to the worker, percent.</summary>
    public int FecPercent { get; init; }

    /// <summary>Cap on the video traffic of each stream, kbit/s; null when the client's bitrate is used.</summary>
    public int? BitrateCapKbps { get; init; }

    /// <summary><c>none</c>, <c>loss</c> (the network loses packets) or <c>network</c> (the adapter's share).</summary>
    public string Limit { get; init; } = "none";
}

/// <summary>Values of <see cref="DeskHdrDto.State"/>.</summary>
public static class DeskHdrStates
{
    public const string Enabled = "enabled";
    public const string Off = "off";
    public const string Unsupported = "unsupported";
    public const string Unknown = "unknown";
}

/// <summary><c>desk.pinRequest</c>: a Moonlight client that shows a PIN and waits for it to be typed in.</summary>
/// <param name="Address">Client IP address, null when unknown.</param>
/// <param name="Since">When the client started pairing.</param>
public sealed record DeskPinRequestDto(string? Address, DateTimeOffset Since);

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

    /// <summary>See <see cref="DeskConfig.GameFpsLimit"/>: null Auto, 0 off.</summary>
    public int? GameFpsLimit { get; set; }

    /// <summary>See <see cref="DeskConfig.HideFromLogon"/>; the effective value, always set in responses.</summary>
    public bool HideFromLogon { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? WorkerSettings { get; set; }

    public DeskMetricsDto Metrics { get; set; } = new();

    /// <summary>The stream the desk is serving, null when no Moonlight client is connected (the earliest one if several are).</summary>
    public DeskStreamDto? Stream { get; set; }

    /// <summary>The HDR state of the desk session's display; null while no worker has reported it.</summary>
    public DeskHdrDto? Hdr { get; set; }

    /// <summary>What "Адаптивный поток" does now; null when it is off or no worker runs.</summary>
    public DeskAdaptiveDto? Adaptive { get; set; }
    /// <summary>A Moonlight client waiting for its PIN on this desk, null when none is (docs/contracts/web-api.md).</summary>
    public DeskPinRequestDto? PinRequest { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// One entry of <c>GET /host/users</c>: an enabled local account that can be picked as a desk user (built-in accounts
/// such as Administrator, Guest, DefaultAccount and WDAGUtilityAccount are left out).
/// </summary>
public sealed record HostUserDto(string Name, string? FullName, bool IsAdmin, bool IsConsoleUser, bool CreatedBySplitPlay);

/// <summary>
/// <c>GET /host/network</c>: LAN IPv4 addresses of the host, those with a default gateway first; the Windows network
/// profile of the LAN (<c>Public</c>, <c>Private</c>, <c>Domain</c>) and whether inbound mDNS reaches Windows' mDNS
/// responder on it (the firewall rule "mDNS (UDP-In)"). Null: unknown.
/// </summary>
public sealed record HostNetworkDto(IReadOnlyList<string> Addresses, string? Profile = null, bool? MdnsAllowed = null);

/// <summary>
/// <c>GET /host/cpu</c>: the CPU, how <c>host.deskCores</c> splits it (<see cref="Kind"/> is one of
/// <see cref="Host.CpuKind"/>) and the processors each desk gets now. <see cref="Auto"/> is what <c>auto</c> would give
/// each desk, so that the settings page can say what switching it on does.
/// </summary>
public sealed record HostCpuDto(
    string? Name,
    int Cores,
    int Threads,
    string Kind,
    string Mode,
    IReadOnlyList<CpuCacheGroupDto> Caches,
    IReadOnlyList<DeskCpuDto> Desks,
    IReadOnlyList<DeskCpuDto> Auto);

/// <summary>
/// One entry of <c>GET /games</c>: a game the service saw in a desk and what its FPS limit is. <see cref="Status"/>:
/// <c>limited</c> (the driver holds <see cref="Limit"/>), <c>owner</c> (a limit set in NVIDIA Control Panel is left
/// alone), <c>failed</c>, <c>pending</c> (not applied yet), <c>off</c> (its desks have the limit off), <c>excluded</c>
/// (turned off for this game), <c>noDriver</c> (no NVIDIA driver), <c>disabled</c> (<c>host.nvidiaProfiles</c> off).
/// </summary>
/// <param name="Exe">The executable's file name, the game's id in <c>/games/{exe}</c>.</param>
/// <param name="Limit">The limit SplitPlay wants for it; null when none.</param>
/// <param name="Profile">The driver profile that holds the limit (NVIDIA's own game profile, or "SplitPlay game …").</param>
public sealed record GameDto(string Exe, string? Path, IReadOnlyList<string> Desks, int? Limit, string Status, bool Excluded,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string? Profile);

/// <summary>Body of <c>PUT /games/{exe}</c>.</summary>
public sealed class GameUpdateRequest
{
    /// <summary>True: no limit for this game; false: the desks' limit again.</summary>
    public bool? Excluded { get; set; }
}

/// <summary>The processors that share one L3 cache (a CCD on Ryzen).</summary>
public sealed record CpuCacheGroupDto(IReadOnlyList<int> Cpus, double L3Mb);

/// <summary>The processors of one desk; null: every processor (not restricted).</summary>
public sealed record DeskCpuDto(string Id, IReadOnlyList<int>? Cpus);

/// <summary>
/// One entry of <c>GET /host/services</c>: a Windows service SplitPlay depends on. <see cref="Status"/> is one of
/// <c>running</c>, <c>stopped</c>, <c>starting</c>, <c>stopping</c>, <c>missing</c> (see <see cref="HostServiceStatus"/>);
/// <see cref="StartType"/> is <c>automatic</c>, <c>automaticDelayed</c>, <c>manual</c>, <c>disabled</c>, <c>boot</c> or
/// <c>system</c>, null when the service is missing.
/// </summary>
public sealed record HostServiceDto(string Name, string DisplayName, string Status, string? StartType, bool CanStart, bool CanStop);

/// <summary>Values of <see cref="HostServiceDto.Status"/>.</summary>
public static class HostServiceStatus
{
    public const string Running = "running";
    public const string Stopped = "stopped";
    public const string Starting = "starting";
    public const string Stopping = "stopping";
    public const string Missing = "missing";
}

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
    IReadOnlyList<GpuDto> Gpus)
{
    /// <summary>Problems worth a line in the UI right now (GPU overload, a recent driver reset); empty when all is well.</summary>
    public IReadOnlyList<HostWarningDto> Warnings { get; init; } = [];
}

/// <summary>
/// One host warning. <see cref="Code"/> is one of <see cref="HostWarningCodes"/>; <see cref="Gpu"/> names the adapter
/// (encoder and VRAM warnings), <see cref="Percent"/> is the load behind it, <see cref="At"/> when it happened (driver
/// reset) and <see cref="Desks"/> how many desks were restarted.
/// </summary>
public sealed record HostWarningDto(string Code, string? Gpu = null, double? Percent = null, DateTimeOffset? At = null, int? Desks = null)
{
    /// <summary><see cref="HostWarningCodes.TermWrapReboot"/>: SplitPlay just wrote TermWrap's <c>ServiceDll</c> back.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Repaired { get; init; }

    /// <summary><see cref="HostWarningCodes.TermWrapUnsupported"/>: the <c>termsrv.dll</c> version.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }
}

public static class HostWarningCodes
{
    /// <summary>The video encoder (NVENC) of an adapter has been nearly fully busy for a few seconds.</summary>
    public const string EncoderBusy = "encoderBusy";

    /// <summary>An adapter's dedicated video memory has been nearly full for a few seconds.</summary>
    public const string VramFull = "vramFull";

    /// <summary>The graphics driver was reset recently; the desks' workers were restarted.</summary>
    public const string GpuReset = "gpuReset";

    /// <summary>TermWrap is set up but TermService runs without it until the PC restarts (often after Windows Update).</summary>
    public const string TermWrapReboot = "termWrapReboot";

    /// <summary>The installed <c>termsrv.dll</c> cannot be patched by this TermWrap: desks cannot start.</summary>
    public const string TermWrapUnsupported = "termWrapUnsupported";

    /// <summary>TermWrap's files are gone: reinstall.</summary>
    public const string TermWrapMissing = "termWrapMissing";

    /// <summary>TermWrap's <c>ServiceDll</c> could not be written back.</summary>
    public const string TermWrapRepairFailed = "termWrapRepairFailed";
}

/// <summary>Metrics of one desk inside <see cref="MetricsDto"/>.</summary>
public sealed record DeskMetricsEntryDto(
    string Id,
    double Fps,
    double BitrateKbps,
    double EncodeMsP50,
    double EncodeMsP99,
    double LossPct,
    double? RttMs,
    double Clients,
    double? CaptureFps)
{
    /// <summary>The desk session's GPU load (busiest engine, all its processes), percent; null when not measured.</summary>
    public double? GpuPercent { get; init; }

    /// <summary>The desk session's video encoder load (NVENC), percent; null when not measured or no encoder.</summary>
    public double? EncoderPercent { get; init; }

    /// <summary>Dedicated video memory the desk session's processes use, MB; null when not measured.</summary>
    public double? VramMb { get; init; }

    public static DeskMetricsEntryDto From(string id, DeskMetricsDto m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(id, m.Fps, m.BitrateKbps, m.EncodeMsP50, m.EncodeMsP99, m.LossPct, m.RttMs, m.Clients, m.CaptureFps);
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
