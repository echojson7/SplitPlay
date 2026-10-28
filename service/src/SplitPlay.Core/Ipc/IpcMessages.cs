// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplitPlay.Core.Ipc;

/// <summary>
/// Base of all control-pipe messages (docs/contracts/ipc.md, protocol version 1). The <c>"type"</c> field is written
/// and read by <see cref="IpcCodec"/>; field names are snake_case on the wire.
/// </summary>
public abstract record IpcMessage
{
    /// <summary>The wire <c>"type"</c> of this message.</summary>
    [JsonIgnore]
    public string Type => IpcCodec.TypeNameOf(this);
}

/// <summary>A message that expects a <see cref="ResultMessage"/> with the same id.</summary>
public interface IIpcRequest
{
    long Id { get; }
}

/// <summary>Well-known values used by the protocol.</summary>
public static class IpcProtocol
{
    public const int Version = 1;

    /// <summary>Maximum length of one line in bytes, excluding the terminating <c>\n</c>.</summary>
    public const int MaxLineBytes = 64 * 1024;

    public static class Roles
    {
        public const string Worker = "worker";
        public const string Seat = "seat";
        public const string StubWorker = "stubworker";
    }

    public static class ErrorCodes
    {
        public const string UnknownType = "unknown_type";
        public const string Unsupported = "unsupported";
        public const string BadRequest = "bad_request";
        public const string Internal = "internal";
    }

    public static class Events
    {
        public const string ClientConnected = "client_connected";
        public const string ClientDisconnected = "client_disconnected";

        /// <summary>Worker: a Moonlight client asked for a mode, <c>data: {width, height, fps}</c>; forwarded to the seat as <c>set_mode</c>.</summary>
        public const string ModeRequested = "mode_requested";

        /// <summary>Seat: the RDP connection dropped and the seat is reconnecting, <c>data: {reason}</c>.</summary>
        public const string SessionLost = "session_lost";

        /// <summary>Seat: the RDP connection is back, <c>data: {session}</c>.</summary>
        public const string SessionRestored = "session_restored";
    }

    /// <summary>Exit codes shared by the children (ipc.md "Child command lines").</summary>
    public static class ExitCodes
    {
        public const int Shutdown = 0;
        public const int InvalidCommandLine = 2;
        public const int HandshakeFailed = 3;

        /// <summary>Seat: gave up reconnecting the RDP session.</summary>
        public const int SeatGaveUp = 4;
    }
}

// ---------------------------------------------------------------- child → service

public sealed record HelloMessage : IpcMessage
{
    public int Protocol { get; init; } = IpcProtocol.Version;

    public string Role { get; init; } = "";

    public string Desk { get; init; } = "";

    public int Pid { get; init; }

    public string Version { get; init; } = "";

    /// <summary>Windows session id the child runs in, if known.</summary>
    public int? Session { get; init; }
}

public sealed record ReadyPorts
{
    public int? Base { get; init; }
}

public sealed record ReadyMessage : IpcMessage
{
    /// <summary>Worker: <c>ports.base</c>.</summary>
    public ReadyPorts? Ports { get; init; }

    /// <summary>Seat: name of the input ring section (input_ring.h).</summary>
    public string? InputRing { get; init; }

    /// <summary>Seat: <c>freerdp</c>, or <c>null</c> for the null connector.</summary>
    public string? Connector { get; init; }

    /// <summary>Seat: the Windows session id of the logged-on desk session.</summary>
    public int? Session { get; init; }
}

public sealed record HeartbeatMessage : IpcMessage
{
    public long Seq { get; init; }

    /// <summary>Free-form short state for the UI (<c>idle</c>, <c>streaming</c>, <c>connecting</c>, <c>logged_on</c>).</summary>
    public string? State { get; init; }

    /// <summary>Optional metrics (<c>fps</c>, <c>bitrate_kbps</c>, <c>encode_ms_p50</c>, …); unknown keys are passed through.</summary>
    public Dictionary<string, JsonElement>? Metrics { get; init; }
}

public sealed record LogMessage : IpcMessage
{
    public string Level { get; init; } = "info";

    public string Message { get; init; } = "";
}

public sealed record EventMessage : IpcMessage
{
    public string Name { get; init; } = "";

    public JsonElement? Data { get; init; }
}

// ---------------------------------------------------------------- service → child

public sealed record WelcomeMessage : IpcMessage
{
    public int Protocol { get; init; } = IpcProtocol.Version;

    public IpcProfile Profile { get; init; } = new();

    /// <summary>Seat only: where and as whom to open the loopback RDP session.</summary>
    public IpcRdpTarget? Rdp { get; init; }
}

/// <summary>
/// The seat's RDP target and credentials (<c>welcome.rdp</c>). The password must never reach a log, a command line or
/// an environment block, so <see cref="ToString"/> masks it.
/// </summary>
public sealed record IpcRdpTarget
{
    public string Host { get; init; } = "127.0.0.2";

    public int Port { get; init; } = 3389;

    public string User { get; init; } = "";

    public string Domain { get; init; } = ".";

    public string Password { get; init; } = "";

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public override string ToString() => $"rdp {{ {Domain}\\{User}@{Host}:{Port} {Width}x{Height}, password ***** }}";
}

/// <summary>Service → seat: resize the session through the RDP display-control channel.</summary>
public sealed record SetModeMessage : IpcMessage, IIpcRequest
{
    public long Id { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

public sealed record SetProfileMessage : IpcMessage, IIpcRequest
{
    public long Id { get; init; }

    public IpcProfile Profile { get; init; } = new();
}

public sealed record PairMessage : IpcMessage, IIpcRequest
{
    public long Id { get; init; }

    public string Pin { get; init; } = "";

    public string Name { get; init; } = "";
}

public sealed record ListClientsMessage : IpcMessage, IIpcRequest
{
    public long Id { get; init; }
}

public sealed record UnpairMessage : IpcMessage, IIpcRequest
{
    public long Id { get; init; }

    public string Client { get; init; } = "";
}

public sealed record SendSasMessage : IpcMessage, IIpcRequest
{
    public long Id { get; init; }
}

public sealed record ShutdownMessage : IpcMessage, IIpcRequest
{
    public long Id { get; init; }
}

// ---------------------------------------------------------------- both directions

public sealed record IpcError
{
    public string Code { get; init; } = "";

    public string Message { get; init; } = "";
}

/// <summary>Answer to a request: <c>ok:true</c> with optional data, or <c>ok:false</c> with an error.</summary>
public sealed record ResultMessage : IpcMessage
{
    public long Id { get; init; }

    public bool Ok { get; init; }

    public JsonElement? Data { get; init; }

    public IpcError? Error { get; init; }

    public static ResultMessage Success(long id, JsonElement? data = null) => new() { Id = id, Ok = true, Data = data };

    public static ResultMessage Failure(long id, string code, string message) =>
        new() { Id = id, Ok = false, Error = new IpcError { Code = code, Message = message } };
}

/// <summary>A message whose type this build does not know. Produced by decoding only.</summary>
public sealed record UnknownIpcMessage : IpcMessage
{
    public string TypeName { get; init; } = "";

    /// <summary>The request id, if the message carried one (it must then be answered with <c>unknown_type</c>).</summary>
    public long? Id { get; init; }
}

/// <summary>
/// Tuning parameters applied live (docs/contracts/ipc.md "Profile object"). All fields optional; absent = keep current.
/// </summary>
public sealed record IpcProfile
{
    public int? Fps { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int? BitrateKbps { get; init; }

    public string? Codec { get; init; }

    public int? FecPercent { get; init; }

    public int? MinFps { get; init; }

    public int? DpbFrames { get; init; }

    public int? PacingMbps { get; init; }

    public string? GpuPriority { get; init; }

    public string? GpuLuid { get; init; }

    public IReadOnlyList<int>? CpuSet { get; init; }
}
