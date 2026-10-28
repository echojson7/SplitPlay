// SPDX-License-Identifier: MIT
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

    /// <summary>Send <c>client_connected</c> right after ready (drives the desk to Streaming).</summary>
    public bool SimulateClient { get; init; }

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
        var heartbeats = HeartbeatLoopAsync(connection, options.HeartbeatInterval, maxHeartbeats, heartbeatCts.Token);

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
                using var data = JsonDocument.Parse("""{"client":"Fake client"}""");
                await connection.SendAsync(new EventMessage { Name = IpcProtocol.Events.ClientConnected, Data = data.RootElement.Clone() },
                    cancellationToken).ConfigureAwait(false);
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

    private static async Task HeartbeatLoopAsync(IpcConnection connection, TimeSpan interval, long max, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        var metrics = new Dictionary<string, JsonElement>
        {
            ["fps"] = JsonSerializer.SerializeToElement(0, Json.IpcJsonContext.Default.Int32),
            ["bitrate_kbps"] = JsonSerializer.SerializeToElement(0, Json.IpcJsonContext.Default.Int32),
            ["encode_ms_p50"] = JsonSerializer.SerializeToElement(0, Json.IpcJsonContext.Default.Int32),
            ["encode_ms_p99"] = JsonSerializer.SerializeToElement(0, Json.IpcJsonContext.Default.Int32),
            ["loss_pct"] = JsonSerializer.SerializeToElement(0, Json.IpcJsonContext.Default.Int32),
            ["clients"] = JsonSerializer.SerializeToElement(0, Json.IpcJsonContext.Default.Int32),
        };

        for (long seq = 1; seq <= max; seq++)
        {
            try
            {
                await connection.SendAsync(new HeartbeatMessage { Seq = seq, State = "idle", Metrics = metrics }, ct).ConfigureAwait(false);
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
