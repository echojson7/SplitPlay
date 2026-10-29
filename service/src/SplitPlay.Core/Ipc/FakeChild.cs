// SPDX-License-Identifier: MIT
using System.Buffers;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;

namespace SplitPlay.Core.Ipc;

/// <summary>How a <see cref="FakeChild"/> behaves; used to exercise the supervisor's failure handling.</summary>
public enum FakeChildBehavior
{
    /// <summary>hello → welcome → ready → heartbeats; answers requests; exits on shutdown.</summary>
    Normal,

    /// <summary>Connects but never says hello.</summary>
    NoHello,

    /// <summary>Says hello and sends heartbeats but never ready.</summary>
    NoReady,

    /// <summary>Becomes ready, sends two heartbeats and then goes silent while keeping the pipe open (a hang).</summary>
    StopHeartbeats,

    /// <summary>Becomes ready and then exits with code 3 (a crash).</summary>
    ExitAfterReady,

    /// <summary>Like <see cref="Normal"/> but ignores <c>shutdown</c> (must be killed).</summary>
    IgnoreShutdown,

    /// <summary>Seat: becomes ready, then reports <c>session_lost</c> and exits with code 4 (gave up reconnecting).</summary>
    GiveUpAfterReady,
}

/// <summary>Options for <see cref="FakeChild.RunAsync"/>.</summary>
public sealed record FakeChildOptions
{
    /// <summary>Pipe name as given with <c>--pipe</c> (full <c>\\.\pipe\…</c> or local form).</summary>
    public required string PipeName { get; init; }

    public required string DeskId { get; init; }

    public string Role { get; init; } = IpcProtocol.Roles.StubWorker;

    public int Pid { get; init; } = Environment.ProcessId;

    public int? Session { get; init; }

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(1000);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public FakeChildBehavior Behavior { get; init; } = FakeChildBehavior.Normal;

    /// <summary>
    /// Worker: after ready, a Moonlight client connects: <c>client_connected</c> with the session fields, then heartbeats
    /// with plausible live metrics and <c>sessions</c> (drives the desk to Streaming).
    /// </summary>
    public bool SimulateClient { get; init; }

    /// <summary>The simulated client's name (<see cref="SimulateClient"/>).</summary>
    public string SimulatedClientName { get; init; } = "Laptop";

    /// <summary>With <see cref="SimulateClient"/>: the client disconnects after this long (<c>client_disconnected</c>).</summary>
    public TimeSpan? SimulatedClientDuration { get; init; }

    /// <summary>Worker: send <c>mode_requested</c> with this mode right after ready.</summary>
    public (int Width, int Height, int Fps)? RequestMode { get; init; }

    /// <summary>
    /// Seat: after ready, report <c>session_lost</c> and then <c>session_restored</c> with this session id
    /// (the same id as <see cref="Session"/> models a reconnect into the same session).
    /// </summary>
    public int? RestoreIntoSession { get; init; }

    /// <summary>Observes every message the child receives (tests).</summary>
    public Action<IpcMessage>? OnMessage { get; init; }

    /// <summary>Optional diagnostic sink.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// A protocol-conformant stand-in for the worker/seat used by tests, by the in-memory launcher and by
/// <c>SplitPlay.Service.exe fake-child --pipe … --desk …</c> in CI smoke tests. Contains no streaming logic.
/// </summary>
public static class FakeChild
{
    /// <summary>Exit codes returned by <see cref="RunAsync"/>.</summary>
    public static class ExitCodes
    {
        public const int Shutdown = 0;
        public const int PipeBroken = 1;
        public const int ProtocolError = 2;
        public const int Crash = 3;
    }

    /// <summary>Runs the fake child until shutdown, pipe loss or cancellation. Returns the process exit code.</summary>
    public static async Task<int> RunAsync(FakeChildOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var log = options.Log ?? (_ => { });
        var pipe = new NamedPipeClientStream(".", PipeNames.ToLocal(options.PipeName), PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var connection = new IpcConnection(pipe);

        try
        {
            await pipe.ConnectAsync(options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log($"could not connect to {options.PipeName}");
            return ExitCodes.PipeBroken;
        }

        log($"connected to {options.PipeName}");
        if (options.Behavior == FakeChildBehavior.NoHello)
        {
            return await DrainAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        await connection.SendAsync(new HelloMessage
        {
            Protocol = IpcProtocol.Version,
            Role = options.Role,
            Desk = options.DeskId,
            Pid = options.Pid,
            Version = BuildInfo.Version,
            Session = options.Session,
        }, cancellationToken).ConfigureAwait(false);

        var first = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return ExitCodes.PipeBroken;
        }

        options.OnMessage?.Invoke(first);

        if (first is not WelcomeMessage welcome || welcome.Protocol != IpcProtocol.Version)
        {
            log($"expected welcome, got {first.Type}");
            return ExitCodes.ProtocolError;
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var maxHeartbeats = options.Behavior == FakeChildBehavior.StopHeartbeats ? 2 : long.MaxValue;
        using var stream = new FakeStream(options);
        var heartbeats = HeartbeatLoopAsync(connection, options.HeartbeatInterval, maxHeartbeats, stream, heartbeatCts.Token);

        try
        {
            var seat = options.Role == IpcProtocol.Roles.Seat;
            if (options.Behavior != FakeChildBehavior.NoReady)
            {
                var ready = seat
                    ? new ReadyMessage { InputRing = $"Global\\SplitPlay.InputRing.{options.DeskId}", Connector = "fake", Session = options.Session }
                    : new ReadyMessage();
                await connection.SendAsync(ready, cancellationToken).ConfigureAwait(false);
                log("ready");
            }

            if (options.Behavior == FakeChildBehavior.GiveUpAfterReady)
            {
                await Task.Delay(options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                await SendEventAsync(connection, IpcProtocol.Events.SessionLost, """{"reason":"simulated"}""", cancellationToken).ConfigureAwait(false);
                return IpcProtocol.ExitCodes.SeatGaveUp;
            }

            if (options.RequestMode is { } mode)
            {
                await SendEventAsync(connection, IpcProtocol.Events.ModeRequested,
                    $$"""{"width":{{mode.Width}},"height":{{mode.Height}},"fps":{{mode.Fps}}}""", cancellationToken).ConfigureAwait(false);
            }

            if (options.RestoreIntoSession is { } restored)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                        await SendEventAsync(connection, IpcProtocol.Events.SessionLost, """{"reason":"simulated"}""", cancellationToken).ConfigureAwait(false);
                        await Task.Delay(options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                        await SendEventAsync(connection, IpcProtocol.Events.SessionRestored, $$"""{"session":{{restored}}}""", cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                    {
                    }
                }, CancellationToken.None);
            }

            if (options.Behavior == FakeChildBehavior.ExitAfterReady)
            {
                // Give the service a moment to observe ready before the "crash".
                await Task.Delay(options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                return ExitCodes.Crash;
            }

            if (options.SimulateClient)
            {
                await stream.ConnectAsync(connection, cancellationToken).ConfigureAwait(false);
                if (options.SimulatedClientDuration is { } duration)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
                            await stream.DisconnectAsync(connection, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                        {
                        }
                    }, CancellationToken.None);
                }
            }

            return await ServeAsync(connection, options, log, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return ExitCodes.PipeBroken;
        }
        catch (IpcProtocolException ex)
        {
            log($"protocol error: {ex.Message}");
            return ExitCodes.ProtocolError;
        }
        finally
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeats.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task<int> ServeAsync(IpcConnection connection, FakeChildOptions options, Action<string> log, CancellationToken ct)
    {
        while (true)
        {
            var message = await connection.ReceiveAsync(ct).ConfigureAwait(false);
            if (message is not null)
            {
                options.OnMessage?.Invoke(message);
            }

            switch (message)
            {
                case null:
                    log("pipe closed");
                    return ExitCodes.PipeBroken;

                case ShutdownMessage shutdown when options.Behavior != FakeChildBehavior.IgnoreShutdown:
                    await connection.ReplyAsync(shutdown.Id, ok: true, cancellationToken: ct).ConfigureAwait(false);
                    log("shutdown");
                    return ExitCodes.Shutdown;

                case ShutdownMessage:
                    log("ignoring shutdown");
                    break;

                case ListClientsMessage list:
                {
                    using var data = JsonDocument.Parse("""{"clients":[]}""");
                    await connection.ReplyAsync(list.Id, ok: true, data.RootElement.Clone(), cancellationToken: ct).ConfigureAwait(false);
                    break;
                }

                case PairMessage pair:
                    await connection.ReplyAsync(pair.Id, ok: true, cancellationToken: ct).ConfigureAwait(false);
                    break;

                case UnpairMessage unpair:
                    await connection.ReplyAsync(unpair.Id, ok: true, cancellationToken: ct).ConfigureAwait(false);
                    break;

                case SetProfileMessage setProfile:
                    await connection.ReplyAsync(setProfile.Id, ok: true, cancellationToken: ct).ConfigureAwait(false);
                    break;

                case SendSasMessage sas when options.Role == IpcProtocol.Roles.Seat:
                    await connection.ReplyAsync(sas.Id, ok: true, cancellationToken: ct).ConfigureAwait(false);
                    break;

                case SetModeMessage setMode when options.Role == IpcProtocol.Roles.Seat:
                    log($"set_mode {setMode.Width}x{setMode.Height}");
                    await connection.ReplyAsync(setMode.Id, ok: true, cancellationToken: ct).ConfigureAwait(false);
                    break;

                case IIpcRequest request:
                    await connection.ReplyAsync(request.Id, ok: false, errorCode: IpcProtocol.ErrorCodes.Unsupported,
                        errorMessage: $"{options.Role} does not support '{((IpcMessage)request).Type}'", cancellationToken: ct).ConfigureAwait(false);
                    break;

                case UnknownIpcMessage { Id: long id } unknown:
                    await connection.ReplyAsync(id, ok: false, errorCode: IpcProtocol.ErrorCodes.UnknownType,
                        errorMessage: $"unknown message type '{unknown.TypeName}'", cancellationToken: ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task SendEventAsync(IpcConnection connection, string name, string json, CancellationToken ct)
    {
        using var data = JsonDocument.Parse(json);
        await connection.SendAsync(new EventMessage { Name = name, Data = data.RootElement.Clone() }, ct).ConfigureAwait(false);
    }

    private static async Task HeartbeatLoopAsync(IpcConnection connection, TimeSpan interval, long max, FakeStream stream, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        for (long seq = 1; seq <= max; seq++)
        {
            try
            {
                await stream.SendHeartbeatAsync(connection, seq, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return;
            }

            if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                return;
            }
        }

        // Hang: keep the pipe open without heartbeats until cancelled.
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The simulated Moonlight stream of a fake worker: a 1920x1080 HEVC stream at 120 fps with the jitter of a good
    /// wired LAN. Thread-safe: the heartbeat loop and the client task share it. As in the worker, a heartbeat and a
    /// client event never cross on the pipe: each is built and sent under <see cref="_order"/>, so a heartbeat always
    /// agrees with the events before it.
    /// </summary>
    private sealed class FakeStream(FakeChildOptions options) : IDisposable
    {
        private readonly SemaphoreSlim _order = new(1, 1);
        private readonly Lock _gate = new();
        private readonly Random _random = new();
        private readonly long _id = Random.Shared.NextInt64(1, uint.MaxValue);
        private bool _streaming;
        private DateTimeOffset _startedAt;

        private readonly record struct Live(double Fps, int BitrateKbps, double EncodeMsP50, double EncodeMsP99, double LossPct, int RttMs);

        public void Dispose() => _order.Dispose();

        /// <summary>The client connects: listed from now on, then <c>client_connected</c>.</summary>
        public async Task ConnectAsync(IpcConnection connection, CancellationToken ct)
        {
            await _order.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    _streaming = true;
                    _startedAt = DateTimeOffset.UtcNow;
                }

                await connection.SendAsync(new EventMessage { Name = IpcProtocol.Events.ClientConnected, Data = SessionEvent() }, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _order.Release();
            }
        }

        /// <summary>The client leaves: no longer listed, then <c>client_disconnected</c>.</summary>
        public async Task DisconnectAsync(IpcConnection connection, CancellationToken ct)
        {
            await _order.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var ended = SessionEvent();
                lock (_gate)
                {
                    _streaming = false;
                }

                await connection.SendAsync(new EventMessage { Name = IpcProtocol.Events.ClientDisconnected, Data = ended }, ct).ConfigureAwait(false);
            }
            finally
            {
                _order.Release();
            }
        }

        public async Task SendHeartbeatAsync(IpcConnection connection, long seq, CancellationToken ct)
        {
            await _order.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await connection.SendAsync(Heartbeat(seq), ct).ConfigureAwait(false);
            }
            finally
            {
                _order.Release();
            }
        }

        private HeartbeatMessage Heartbeat(long seq)
        {
            lock (_gate)
            {
                if (!_streaming)
                {
                    return new HeartbeatMessage { Seq = seq, State = "idle", Metrics = Metrics(new Live(), clients: 0) };
                }

                var live = Next();
                return new HeartbeatMessage
                {
                    Seq = seq,
                    State = "streaming",
                    Metrics = Metrics(live, clients: 1),
                    Sessions = ToElement(w =>
                    {
                        w.WriteStartArray();
                        WriteSession(w, live);
                        w.WriteEndArray();
                    }),
                };
            }
        }

        /// <summary>The <c>client_connected</c>/<c>client_disconnected</c> data.</summary>
        private JsonElement SessionEvent()
        {
            lock (_gate)
            {
                return ToElement(w => WriteSession(w, null));
            }
        }

        private void WriteSession(Utf8JsonWriter w, Live? live)
        {
            w.WriteStartObject();
            w.WriteNumber("id", _id);
            w.WriteString("client", options.SimulatedClientName);
            w.WriteString("client_uuid", string.Create(CultureInfo.InvariantCulture, $"0f5c3a52-6a3e-4c1b-9a51-{_id % 1_000_000_000_000:D12}"));
            w.WriteString("address", "192.168.1.42");
            w.WriteNumber("width", 1920);
            w.WriteNumber("height", 1080);
            w.WriteNumber("fps_requested", 120);
            w.WriteString("codec", "hevc");
            w.WriteNumber("bitrate_kbps_requested", 50_000);
            w.WriteBoolean("hdr", false);
            w.WriteString("started_at", _startedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            if (live is { } l)
            {
                WriteLive(w, l);
            }

            w.WriteEndObject();
        }

        private static void WriteLive(Utf8JsonWriter w, Live l)
        {
            w.WriteNumber("fps", l.Fps);
            w.WriteNumber("bitrate_kbps", l.BitrateKbps);
            w.WriteNumber("encode_ms_p50", l.EncodeMsP50);
            w.WriteNumber("encode_ms_p99", l.EncodeMsP99);
            w.WriteNumber("loss_pct", l.LossPct);
            w.WriteNumber("rtt_ms", l.RttMs);
        }

        /// <summary>One heartbeat interval's numbers.</summary>
        private Live Next()
        {
            var p50 = Math.Round(3.1 + (_random.NextDouble() * 0.8), 1);
            return new Live(
                Fps: Math.Round(120 - (_random.NextDouble() * 1.6), 1),
                BitrateKbps: 31_000 + _random.Next(0, 12_000),
                EncodeMsP50: p50,
                EncodeMsP99: Math.Round(p50 + 1.2 + (_random.NextDouble() * 2.4), 1),
                LossPct: _random.Next(0, 10) == 0 ? Math.Round(_random.NextDouble() * 0.8, 1) : 0,
                RttMs: _random.Next(2, 6));
        }

        private static Dictionary<string, JsonElement> Metrics(Live live, int clients)
        {
            var element = ToElement(w =>
            {
                w.WriteStartObject();
                WriteLive(w, live);
                w.WriteNumber("clients", clients);
                w.WriteEndObject();
            });
            return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        }

        private static JsonElement ToElement(Action<Utf8JsonWriter> write)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                write(w);
            }

            using var doc = JsonDocument.Parse(buffer.WrittenMemory);
            return doc.RootElement.Clone();
        }
    }

    private static async Task<int> DrainAsync(IpcConnection connection, CancellationToken ct)
    {
        try
        {
            while (await connection.ReceiveAsync(ct).ConfigureAwait(false) is not null)
            {
            }
        }
        catch (IOException)
        {
        }

        return ExitCodes.PipeBroken;
    }
}
