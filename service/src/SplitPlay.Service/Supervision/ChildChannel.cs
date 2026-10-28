// SPDX-License-Identifier: MIT
using System.IO.Pipes;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Service.Supervision;

/// <summary>
/// One supervised child (worker or seat): its pipe server, process and control connection. Handles the handshake,
/// heartbeats, the ready deadline, unknown requests and shutdown; everything else goes to the owner's handler.
/// <see cref="Completion"/> finishes with a failure reason when the child exits, hangs, breaks the pipe or is not
/// ready in time.
/// </summary>
internal sealed class ChildChannel : IAsyncDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly SupervisorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _io = new();
    private readonly object _gate = new();
    private DateTimeOffset _lastHeartbeat;
    private Task<string>? _completion;
    private bool _ready;
    private int _disposed;

    private ChildChannel(string deskId, string role, NamedPipeServerStream server, IChildProcess process, IpcConnection connection,
        SupervisorOptions options, TimeProvider time, ILogger logger)
    {
        DeskId = deskId;
        Role = role;
        _server = server;
        Process = process;
        Connection = connection;
        _options = options;
        _time = time;
        _logger = logger;
        _lastHeartbeat = time.GetUtcNow();
    }

    public string DeskId { get; }

    public string Role { get; }

    public IChildProcess Process { get; }

    public IpcConnection Connection { get; }

    public HelloMessage Hello { get; private set; } = new();

    /// <summary>Seat: the session from <c>ready</c> (updated by <c>session_restored</c>).</summary>
    public int? Session { get; set; }

    /// <summary>Completes with the failure reason once <see cref="Run"/> was called; never completes before.</summary>
    public Task<string> Completion => _completion ?? Task.FromResult("not running");

    public bool IsAlive => _completion is { IsCompleted: false };

    /// <summary>Called by the owner when the child's <c>ready</c> arrived (stops the ready deadline).</summary>
    public void MarkReady()
    {
        lock (_gate)
        {
            _ready = true;
            _lastHeartbeat = _time.GetUtcNow();
        }
    }

    /// <summary>Result of <see cref="OpenAsync"/>.</summary>
    public readonly record struct OpenResult(ChildChannel? Channel, string? Failure);

    /// <summary>
    /// Creates the pipe server, launches the child and waits for its <c>hello</c>. On failure everything is cleaned up
    /// and the reason is returned; throws only <see cref="OperationCanceledException"/> when stopped.
    /// </summary>
    public static async Task<OpenResult> OpenAsync(ChildLaunchRequest request, string localPipe, IReadOnlyCollection<string> allowedRoles,
        IChildLauncher launcher, SupervisorOptions options, TimeProvider time, ILogger logger, CancellationToken stop)
    {
        NamedPipeServerStream server;
        try
        {
            server = PipeServerFactory.Create(localPipe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, $"cannot create pipe {request.PipeName}: {ex.Message}");
        }

        IChildProcess process;
        try
        {
            process = await launcher.LaunchAsync(request, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            return new(null, $"{request.Role} launch failed: {ex.Message}");
        }

        var channel = new ChildChannel(request.DeskId, request.Role, server, process, new IpcConnection(server, ownsStream: false), options, time, logger);
        try
        {
            var failure = await channel.HandshakeAsync(allowedRoles, stop).ConfigureAwait(false);
            if (failure is null)
            {
                return new(channel, null);
            }

            await channel.DisposeAsync().ConfigureAwait(false);
            return new(null, failure);
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string?> HandshakeAsync(IReadOnlyCollection<string> allowedRoles, CancellationToken stop)
    {
        using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        helloCts.CancelAfter(TimeSpan.FromMilliseconds(_options.HelloTimeoutMs));
        try
        {
            var connect = _server.WaitForConnectionAsync(helloCts.Token);
            if (await Task.WhenAny(connect, Process.Exited).ConfigureAwait(false) != connect)
            {
                return $"{Role} exited with code {await Process.Exited.ConfigureAwait(false)} before connecting";
            }

            await connect.ConfigureAwait(false);
            var receive = Connection.ReceiveAsync(helloCts.Token);
            if (await Task.WhenAny(receive, Process.Exited).ConfigureAwait(false) != receive)
            {
                return $"{Role} exited with code {await Process.Exited.ConfigureAwait(false)} before hello";
            }

            var first = await receive.ConfigureAwait(false);
            switch (first)
            {
                case null:
                    return $"{Role} pipe closed before hello";
                case HelloMessage { Protocol: not IpcProtocol.Version } h:
                    return $"{Role} speaks protocol {h.Protocol}, expected {IpcProtocol.Version}";
                case HelloMessage h when h.Desk != DeskId:
                    return $"{Role} announced desk '{h.Desk}', expected '{DeskId}'";
                case HelloMessage h when !allowedRoles.Contains(h.Role):
                    return $"child announced role '{h.Role}', expected {string.Join(" or ", allowedRoles)}";
                case HelloMessage h:
                    Hello = h;
                    lock (_gate)
                    {
                        _lastHeartbeat = _time.GetUtcNow();
                    }

                    return null;
                default:
                    return $"{Role}: expected hello, got '{first.Type}'";
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return $"{Role} did not connect and say hello within {_options.HelloTimeoutMs} ms";
        }
        catch (IpcProtocolException ex)
        {
            return $"{Role} protocol error: {ex.Message}";
        }
        catch (IOException ex) when (!stop.IsCancellationRequested)
        {
            return $"{Role} pipe broken before hello: {ex.Message}";
        }
    }

    /// <summary>Starts reading. <paramref name="handler"/> sees every message except results and unknown requests.</summary>
    public void Run(Func<IpcMessage, CancellationToken, Task> handler, TimeSpan readyTimeout)
    {
        lock (_gate)
        {
            _lastHeartbeat = _time.GetUtcNow();
        }

        var readyDeadline = _time.GetUtcNow() + readyTimeout;
        var reader = ReadLoopAsync(handler, _io.Token);
        var watchdog = WatchdogAsync(readyDeadline, readyTimeout, _io.Token);
        var exited = ExitReasonAsync();
        _completion = FirstAsync(reader, watchdog, exited);
    }

    private static async Task<string> FirstAsync(params Task<string>[] tasks) => await (await Task.WhenAny(tasks).ConfigureAwait(false)).ConfigureAwait(false);

    private async Task<string> ExitReasonAsync()
    {
        var code = await Process.Exited.ConfigureAwait(false);
        return Role == IpcProtocol.Roles.Seat && code == IpcProtocol.ExitCodes.SeatGaveUp
            ? $"seat gave up reconnecting the RDP session (exit code {code})"
            : $"{Role} exited with code {code}";
    }

    private async Task<string> ReadLoopAsync(Func<IpcMessage, CancellationToken, Task> handler, CancellationToken ct)
    {
        while (true)
        {
            IpcMessage? message;
            try
            {
                message = await Connection.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return "cancelled";
            }
            catch (IpcProtocolException ex)
            {
                return $"{Role} protocol error: {ex.Message}";
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return $"{Role} pipe broken: {ex.Message}";
            }

            if (message is null)
            {
                // Give the exit code a moment to win: "exited with code 3" says more than "pipe closed".
                try
                {
                    var code = await Process.Exited.WaitAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                    return Role == IpcProtocol.Roles.Seat && code == IpcProtocol.ExitCodes.SeatGaveUp
                        ? $"seat gave up reconnecting the RDP session (exit code {code})"
                        : $"{Role} exited with code {code}";
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    return $"{Role} closed the pipe";
                }
            }

            try
            {
                switch (message)
                {
                    case HeartbeatMessage:
                        lock (_gate)
                        {
                            _lastHeartbeat = _time.GetUtcNow();
                        }

                        await handler(message, ct).ConfigureAwait(false);
                        break;

                    case ResultMessage:
                        // Completed by IpcConnection; a result nobody waits for (late answer) is dropped.
                        break;

                    case UnknownIpcMessage { Id: long id } unknown:
                        await Connection.ReplyAsync(id, ok: false, errorCode: IpcProtocol.ErrorCodes.UnknownType,
                            errorMessage: $"unknown message type '{unknown.TypeName}'", cancellationToken: ct).ConfigureAwait(false);
                        break;

                    case IIpcRequest request:
                        await Connection.ReplyAsync(request.Id, ok: false, errorCode: IpcProtocol.ErrorCodes.Unsupported,
                            errorMessage: $"the service does not accept '{message.Type}'", cancellationToken: ct).ConfigureAwait(false);
                        break;

                    default:
                        await handler(message, ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                return "cancelled";
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return $"{Role} pipe broken: {ex.Message}";
            }
        }
    }

    private async Task<string> WatchdogAsync(DateTimeOffset readyDeadline, TimeSpan readyTimeout, CancellationToken ct)
    {
        var period = TimeSpan.FromMilliseconds(Math.Max(10, _options.HeartbeatIntervalMs / 4));
        using var timer = new PeriodicTimer(period, _time);
        var lastTick = _time.GetUtcNow();
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = _time.GetUtcNow();
                var late = now - lastTick - period;
                lastTick = now;
                lock (_gate)
                {
                    // A tick far behind schedule means the service itself stalled (thread pool starvation, GC, a
                    // suspended VM): the reader could not take heartbeats either, so the stalled time is not the
                    // child's silence. Without this, a busy host restarts healthy children.
                    if (late > _options.HeartbeatInterval)
                    {
                        _lastHeartbeat = _lastHeartbeat + late > now ? now : _lastHeartbeat + late;
                        continue;
                    }

                    var silence = now - _lastHeartbeat;
                    if (silence > _options.HeartbeatTimeout)
                    {
                        return $"{Role}: no heartbeat for {(long)silence.TotalMilliseconds} ms ({_options.MissedHeartbeats} missed): child hung";
                    }

                    if (!_ready && now > readyDeadline)
                    {
                        return $"{Role} not ready within {(long)readyTimeout.TotalMilliseconds} ms";
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return "cancelled";
    }

    /// <summary>Sends <c>shutdown</c>, waits for the exit and kills the child when it does not leave in time.</summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        var deadline = _time.GetUtcNow() + timeout;
        if (!Process.Exited.IsCompleted)
        {
            try
            {
                var result = await Connection.RequestAsync(id => new ShutdownMessage { Id = id }, timeout).ConfigureAwait(false);
                _logger.LogDebug("Desk {Desk}: {Role} answered shutdown ok={Ok}", DeskId, Role, result.Ok);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or EndOfStreamException or InvalidOperationException)
            {
                _logger.LogDebug("Desk {Desk}: shutdown request to {Role} failed: {Message}", DeskId, Role, ex.Message);
            }
        }

        var remaining = deadline - _time.GetUtcNow();
        try
        {
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException();
            }

            await Process.Exited.WaitAsync(remaining).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Desk {Desk}: {Role} {Pid} did not exit within {Timeout}; killing it", DeskId, Role, Process.Pid, timeout);
            Process.Kill();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _io.CancelAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
        await Process.DisposeAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
        if (_completion is not null)
        {
            try
            {
                await _completion.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        _io.Dispose();
    }
}
