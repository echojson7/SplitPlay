// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Options;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Api;
using SplitPlay.Service.Configuration;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Supervision;

/// <summary>
/// Owns every desk's lifecycle: drives the <see cref="DeskStateMachine"/>, creates the control pipe, launches and
/// watches the child, restarts it with backoff and shuts it down. Also coordinates desk CRUD with the configuration.
/// </summary>
/// <remarks>
/// Stage 2 maps the lifecycle onto a single child (the worker or stub worker):
/// Provisioning is a no-op, the child's <c>hello</c> stands in for "session ready" (seat), sending <c>welcome</c> for
/// "display ready" (IDD) and the child's <c>ready</c> for Ready. Stage 3 inserts the seat and the IDD steps.
/// </remarks>
public sealed class DeskSupervisor : IHostedService
{
    private readonly ConcurrentDictionary<string, DeskRuntime> _desks = new(StringComparer.Ordinal);
    private readonly object _registryGate = new();
    private readonly ConfigService _config;
    private readonly DataPaths _paths;
    private readonly IChildLauncher _launcher;
    private readonly IOptions<SupervisorOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DeskSupervisor> _logger;
    private readonly ILogger _childLogger;
    private readonly GpuInventory _gpus;
    private readonly DateTimeOffset _startedAt;
    private TimeSpan _lastCpu;
    private DateTimeOffset _lastCpuAt;

    public DeskSupervisor(ConfigService config, DataPaths paths, IChildLauncher launcher, IOptions<SupervisorOptions> options,
        TimeProvider time, ILogger<DeskSupervisor> logger, ILoggerFactory loggerFactory, GpuInventory gpus)
    {
        _gpus = gpus;
        _childLogger = loggerFactory.CreateLogger(Logging.LogStoreLoggerProvider.ChildCategory);
        _config = config;
        _paths = paths;
        _launcher = launcher;
        _options = options;
        _time = time;
        _logger = logger;
        _startedAt = time.GetUtcNow();
        _lastCpuAt = _startedAt;
        _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;

        foreach (var desk in config.Snapshot().Desks)
        {
            AddRuntime(desk);
        }
    }

    /// <summary>Raised (synchronously, possibly under a desk lock) whenever a desk's state or config changes.</summary>
    public event Action<DeskDto>? DeskChanged;

    /// <summary>Time since the service started.</summary>
    public TimeSpan Uptime => _time.GetUtcNow() - _startedAt;

    // ------------------------------------------------------------------ hosted service

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.Autostart)
        {
            return Task.CompletedTask;
        }

        foreach (var desk in _config.Snapshot().Desks.Where(d => d.Autostart))
        {
            try
            {
                Start(desk.Id);
            }
            catch (ApiException ex)
            {
                _logger.LogWarning("Autostart of desk {Desk} failed: {Message}", desk.Id, ex.Message);
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        foreach (var rt in _desks.Values)
        {
            StopRuntime(rt);
            lock (rt.Gate)
            {
                if (rt.RunTask is { } task)
                {
                    tasks.Add(task);
                }
            }
        }

        var limit = TimeSpan.FromMilliseconds(_options.Value.ShutdownTimeoutMs + 2000);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("Not all desks stopped within {Limit}", limit);
        }
    }

    // ------------------------------------------------------------------ queries

    public IReadOnlyList<DeskDto> List() =>
        _config.Snapshot().Desks.Select(d => _desks.TryGetValue(d.Id, out var rt) ? rt.ToDto() : null).OfType<DeskDto>().ToList();

    public DeskDto Get(string id) => Find(id).ToDto();

    public MetricsDto Metrics()
    {
        var now = _time.GetUtcNow();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        double cpuPercent;
        lock (_registryGate)
        {
            var wall = (now - _lastCpuAt).TotalMilliseconds;
            cpuPercent = wall > 0 ? (cpu - _lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100 : 0;
            _lastCpu = cpu;
            _lastCpuAt = now;
        }

        var memory = GC.GetGCMemoryInfo();
        var composeMs = _config.Snapshot().Host.ComposeIntervalMs;
        var host = new HostMetricsDto(
            Math.Round(Math.Clamp(cpuPercent, 0, 100), 1),
            memory.MemoryLoadBytes > 0 ? Math.Round(memory.MemoryLoadBytes / 1024.0 / 1024.0) : null,
            memory.TotalAvailableMemoryBytes > 0 ? Math.Round(memory.TotalAvailableMemoryBytes / 1024.0 / 1024.0) : null,
            composeMs > 0 ? Math.Round(1000.0 / composeMs, 1) : null,
            _gpus.Get());
        var desks = List().Select(d => new DeskMetricsEntryDto(d.Id, d.Metrics.Fps, d.Metrics.BitrateKbps, d.Metrics.EncodeMsP99, d.Metrics.Clients)).ToList();
        return new MetricsDto(host, desks);
    }

    // ------------------------------------------------------------------ CRUD

    public DeskDto Create(DeskConfig desk)
    {
        ArgumentNullException.ThrowIfNull(desk);
        if (!ConfigValidator.IsValidDeskId(desk.Id))
        {
            throw ApiException.Validation("desk.id: must match [a-z0-9-]{1,32}");
        }

        lock (_registryGate)
        {
            if (_desks.ContainsKey(desk.Id))
            {
                throw ApiException.Conflict(ApiErrorCodes.AlreadyExists, $"desk '{desk.Id}' already exists");
            }

            var saved = _config.Update(cfg =>
            {
                cfg.Desks.Add(desk);
                return desk;
            });

            var rt = AddRuntime(saved);
            _logger.LogInformation("Created desk {Desk} (base port {Port}, user {User})", saved.Id, saved.BasePort, saved.User);
            Publish(rt);
            return rt.ToDto();
        }
    }

    public DeskDto Replace(string id, DeskConfig desk)
    {
        ArgumentNullException.ThrowIfNull(desk);
        lock (_registryGate)
        {
            var rt = Find(id);
            if (!string.IsNullOrEmpty(desk.Id) && desk.Id != id)
            {
                throw ApiException.BadRequest($"body id '{desk.Id}' does not match '{id}'");
            }

            DeskConfig current;
            bool idle;
            lock (rt.Gate)
            {
                current = rt.Config;
                idle = rt.Machine.State.IsIdle();
            }

            desk.Id = id;
            desk.BasePort ??= current.BasePort;
            desk.User ??= current.User;
            desk.Name ??= current.Name;
            desk.Profile ??= new ProfileConfig();

            if (!idle && (desk.Name != current.Name || !string.Equals(desk.User, current.User, StringComparison.OrdinalIgnoreCase)
                          || desk.BasePort != current.BasePort || desk.Autostart != current.Autostart || desk.Gpu != current.Gpu))
            {
                throw ApiException.Conflict(ApiErrorCodes.InvalidState, $"desk '{id}' is running: only 'profile' can change; stop it first");
            }

            var saved = _config.Update(cfg =>
            {
                var index = cfg.Desks.FindIndex(d => d.Id == id);
                cfg.Desks[index] = desk;
                return desk;
            });

            IpcConnection? connection;
            lock (rt.Gate)
            {
                rt.Config = saved;
                connection = rt.Connection;
            }

            if (connection is not null)
            {
                _ = PushProfileAsync(id, connection, saved.Profile);
            }

            Publish(rt);
            return rt.ToDto();
        }
    }

    public void Delete(string id, bool deleteUser)
    {
        lock (_registryGate)
        {
            var rt = Find(id);
            lock (rt.Gate)
            {
                if (!rt.Machine.State.IsIdle())
                {
                    throw ApiException.Conflict(ApiErrorCodes.InvalidState, $"desk '{id}' is {rt.Machine.State}; stop it first");
                }
            }

            if (deleteUser)
            {
                throw ApiException.NotImplemented("deleting the Windows account arrives with account provisioning (Stage 3)");
            }

            _config.Update(cfg => cfg.Desks.RemoveAll(d => d.Id == id));
            _desks.TryRemove(id, out _);
            TryDeleteDirectory(_paths.DeskDirectory(id));
            _logger.LogInformation("Deleted desk {Desk}", id);
        }
    }

    /// <summary>Replaces the whole configuration (import). All desks must be idle.</summary>
    public IReadOnlyList<DeskDto> Import(SplitPlayConfig config)
    {
        lock (_registryGate)
        {
            foreach (var rt in _desks.Values)
            {
                lock (rt.Gate)
                {
                    if (!rt.Machine.State.IsIdle())
                    {
                        throw ApiException.Conflict(ApiErrorCodes.InvalidState, $"desk '{rt.Id}' is {rt.Machine.State}; stop all desks before importing");
                    }
                }
            }

            _config.Replace(config);
            _desks.Clear();
            foreach (var desk in _config.Snapshot().Desks)
            {
                Publish(AddRuntime(desk));
            }

            _logger.LogInformation("Imported configuration with {Count} desk(s)", config.Desks.Count);
        }

        return List();
    }

    // ------------------------------------------------------------------ lifecycle commands

    public DeskDto Start(string id)
    {
        var rt = Find(id);
        lock (rt.Gate)
        {
            if (!rt.Machine.CanFire(DeskTrigger.Start))
            {
                throw ApiException.Conflict(ApiErrorCodes.InvalidState,
                    rt.Machine.State == DeskState.Faulted
                        ? $"desk '{id}' is Faulted ({rt.Machine.LastError}); stop it before starting again"
                        : $"desk '{id}' is already {rt.Machine.State}");
            }

            rt.Machine.Fire(DeskTrigger.Start);
            rt.Attempt = 0;
            rt.Running = true;
            rt.RunCts?.Dispose();
            rt.RunCts = new CancellationTokenSource();
            var token = rt.RunCts.Token;
            rt.RunTask = Task.Run(() => RunAsync(rt, token), CancellationToken.None);
        }

        return rt.ToDto();
    }

    public DeskDto Stop(string id)
    {
        var rt = Find(id);
        StopRuntime(rt);
        return rt.ToDto();
    }

    public async Task<DeskDto> RestartAsync(string id)
    {
        var rt = Find(id);
        StopRuntime(rt);
        Task? task;
        lock (rt.Gate)
        {
            task = rt.RunTask;
        }

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromMilliseconds(_options.Value.ShutdownTimeoutMs + 2000)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw ApiException.Conflict(ApiErrorCodes.InvalidState, $"desk '{id}' did not stop in time");
            }
        }

        return Start(id);
    }

    /// <summary>Sends a request to the desk's connected child and returns its result.</summary>
    public async Task<ResultMessage> RequestAsync(string id, Func<long, IpcMessage> build)
    {
        var rt = Find(id);
        IpcConnection? connection;
        lock (rt.Gate)
        {
            connection = rt.Connection;
        }

        if (connection is null)
        {
            throw ApiException.Conflict(ApiErrorCodes.ChildUnavailable, $"desk '{id}' is not running");
        }

        try
        {
            return await connection.RequestAsync(build, TimeSpan.FromMilliseconds(_options.Value.RequestTimeoutMs)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ApiException(StatusCodes.Status504GatewayTimeout, ApiErrorCodes.ChildUnavailable, $"desk '{id}' did not answer in time");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException or InvalidOperationException)
        {
            throw ApiException.Conflict(ApiErrorCodes.ChildUnavailable, $"desk '{id}' connection lost: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ internals

    private DeskRuntime Find(string id) =>
        _desks.TryGetValue(id, out var rt) ? rt : throw ApiException.NotFound($"desk '{id}' not found");

    private DeskRuntime AddRuntime(DeskConfig desk)
    {
        var machine = new DeskStateMachine(_time, _options.Value.ToRecoveryPolicy());
        var rt = new DeskRuntime(desk, machine);
        machine.Transitioned += t => OnTransition(rt, t);
        _desks[desk.Id] = rt;
        return rt;
    }

    private void OnTransition(DeskRuntime rt, DeskTransition t)
    {
        if (t.To is DeskState.Recovering or DeskState.Faulted)
        {
            _logger.LogWarning("Desk {Desk}: {From} -> {To} ({Reason})", rt.Id, t.From, t.To, t.Reason);
        }
        else
        {
            _logger.LogInformation("Desk {Desk}: {From} -> {To}", rt.Id, t.From, t.To);
        }

        Publish(rt);
    }

    private void Publish(DeskRuntime rt)
    {
        var handler = DeskChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(rt.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeskChanged handler failed");
        }
    }

    private void StopRuntime(DeskRuntime rt)
    {
        CancellationTokenSource? cts = null;
        lock (rt.Gate)
        {
            var state = rt.Machine.State;
            if (state.IsIdle() || state == DeskState.Stopping)
            {
                return;
            }

            rt.Machine.Fire(DeskTrigger.Stop);
            if (!rt.Running)
            {
                // Nothing runs (e.g. Faulted after the loop gave up): finish right away.
                rt.Machine.Fire(DeskTrigger.Stopped);
                return;
            }

            cts = rt.RunCts;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool Fire(DeskRuntime rt, DeskTrigger trigger, string? reason = null)
    {
        lock (rt.Gate)
        {
            return rt.Machine.TryFire(trigger, out _, reason);
        }
    }

    private async Task RunAsync(DeskRuntime rt, CancellationToken stop)
    {
        try
        {
            // Provisioning: account, RDU rights and LSA password arrive in Stage 3.
            Directory.CreateDirectory(_paths.DeskDirectory(rt.Id));
            if (!Fire(rt, DeskTrigger.Provisioned))
            {
                return;
            }

            while (true)
            {
                var reason = await RunChildOnceAsync(rt, stop).ConfigureAwait(false);
                lock (rt.Gate)
                {
                    if (!rt.Machine.TryFire(DeskTrigger.Fail, out _, reason) || rt.Machine.State == DeskState.Faulted)
                    {
                        return;
                    }
                }

                if (!await WaitForRetryAsync(rt, stop).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Desk {Desk} run loop failed", rt.Id);
            lock (rt.Gate)
            {
                rt.Machine.TryFire(DeskTrigger.Fail, out _, $"internal error: {ex.Message}");
            }
        }
        finally
        {
            lock (rt.Gate)
            {
                rt.Running = false;
                rt.Connection = null;
                rt.SessionId = null;
                rt.ChildPid = null;
                rt.ChildState = null;
                rt.Metrics = new DeskMetricsDto();
                if (rt.Machine.State == DeskState.Stopping)
                {
                    rt.Machine.Fire(DeskTrigger.Stopped);
                }
            }
        }
    }

    /// <summary>
    /// Waits out the backoff and fires RetryDue. Timers may fire slightly before the wall clock reaches
    /// <see cref="DeskStateMachine.RetryAt"/>, so this loops until the machine accepts the retry.
    /// Returns false if the desk left Recovering (e.g. it was stopped).
    /// </summary>
    private async Task<bool> WaitForRetryAsync(DeskRuntime rt, CancellationToken stop)
    {
        while (true)
        {
            DateTimeOffset at;
            lock (rt.Gate)
            {
                if (rt.Machine.State != DeskState.Recovering || rt.Machine.RetryAt is not { } retryAt)
                {
                    return false;
                }

                if (rt.Machine.TryFire(DeskTrigger.RetryDue, out _))
                {
                    return true;
                }

                at = retryAt;
            }

            var delay = at - _time.GetUtcNow();
            await Task.Delay(delay > TimeSpan.FromMilliseconds(1) ? delay : TimeSpan.FromMilliseconds(1), _time, stop).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one child from launch to failure. Returns the failure reason; throws when stopped.</summary>
    private async Task<string> RunChildOnceAsync(DeskRuntime rt, CancellationToken stop)
    {
        var o = _options.Value;
        int attempt;
        DeskConfig config;
        lock (rt.Gate)
        {
            attempt = ++rt.Attempt;
            config = rt.Config;
        }

        var localPipe = PipeNames.Local(o.ChildRole, rt.Id, o.PipeNamespace);
        var fullPipe = PipeNames.WindowsPrefix + localPipe;
        var deskDirectory = _paths.DeskDirectory(rt.Id);
        Directory.CreateDirectory(deskDirectory);

        NamedPipeServerStream server;
        try
        {
            server = PipeServerFactory.Create(localPipe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"cannot create pipe {fullPipe}: {ex.Message}";
        }

        await using var serverGuard = server.ConfigureAwait(false);

        IChildProcess child;
        try
        {
            child = await _launcher.LaunchAsync(new ChildLaunchRequest(rt.Id, o.ChildRole, fullPipe, deskDirectory, attempt), stop)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"launch failed: {ex.Message}";
        }

        await using var childGuard = child.ConfigureAwait(false);
        lock (rt.Gate)
        {
            rt.ChildPid = child.Pid;
        }

        var connection = new IpcConnection(server, ownsStream: false);
        using var io = new CancellationTokenSource();
        try
        {
            var hello = await HandshakeAsync(rt, server, connection, child, stop).ConfigureAwait(false);
            if (hello.Failure is not null)
            {
                return hello.Failure;
            }

            lock (rt.Gate)
            {
                rt.SessionId = hello.Message!.Session;
                rt.ChildPid = hello.Message.Pid;
                rt.LastHeartbeat = _time.GetUtcNow();
                if (!rt.Machine.TryFire(DeskTrigger.SessionReady, out _))
                {
                    stop.ThrowIfCancellationRequested();
                    return $"unexpected state {rt.Machine.State}";
                }
            }

            await connection.SendAsync(new WelcomeMessage { Profile = DeskRuntime.ToIpcProfile(config.Profile) }, stop).ConfigureAwait(false);

            lock (rt.Gate)
            {
                rt.LastHeartbeat = _time.GetUtcNow();
                if (!rt.Machine.TryFire(DeskTrigger.DisplayReady, out _))
                {
                    stop.ThrowIfCancellationRequested();
                    return $"unexpected state {rt.Machine.State}";
                }

                rt.Connection = connection;
            }

            var readyDeadline = _time.GetUtcNow() + TimeSpan.FromMilliseconds(o.ReadyTimeoutMs);
            var reader = ReadLoopAsync(rt, connection, io.Token);
            var watchdog = WatchdogAsync(rt, readyDeadline, io.Token);
            var stopped = Task.Delay(Timeout.Infinite, stop);
            var winner = await Task.WhenAny(reader, watchdog, child.Exited, stopped).ConfigureAwait(false);

            if (winner == stopped)
            {
                await ShutdownChildAsync(rt, connection, child).ConfigureAwait(false);
                stop.ThrowIfCancellationRequested();
            }

            if (winner == child.Exited)
            {
                return $"child exited with code {await child.Exited.ConfigureAwait(false)}";
            }

            return await ((Task<string>)winner).ConfigureAwait(false);
        }
        catch (IOException ex) when (!stop.IsCancellationRequested)
        {
            return $"pipe broken: {ex.Message}";
        }
        finally
        {
            lock (rt.Gate)
            {
                if (ReferenceEquals(rt.Connection, connection))
                {
                    rt.Connection = null;
                }

                rt.SessionId = null;
                rt.Metrics = new DeskMetricsDto();
            }

            await io.CancelAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private readonly record struct HandshakeResult(HelloMessage? Message, string? Failure);

    private async Task<HandshakeResult> HandshakeAsync(DeskRuntime rt, NamedPipeServerStream server, IpcConnection connection,
        IChildProcess child, CancellationToken stop)
    {
        var o = _options.Value;
        using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        helloCts.CancelAfter(TimeSpan.FromMilliseconds(o.HelloTimeoutMs));

        try
        {
            var connect = server.WaitForConnectionAsync(helloCts.Token);
            if (await Task.WhenAny(connect, child.Exited).ConfigureAwait(false) != connect)
            {
                return new(null, $"child exited with code {await child.Exited.ConfigureAwait(false)} before connecting");
            }

            await connect.ConfigureAwait(false);

            var receive = connection.ReceiveAsync(helloCts.Token);
            if (await Task.WhenAny(receive, child.Exited).ConfigureAwait(false) != receive)
            {
                return new(null, $"child exited with code {await child.Exited.ConfigureAwait(false)} before hello");
            }

            var first = await receive.ConfigureAwait(false);
            var allowedRoles = o.ChildRole == IpcProtocol.Roles.Worker
                ? new[] { IpcProtocol.Roles.Worker, IpcProtocol.Roles.StubWorker }
                : new[] { o.ChildRole };

            return first switch
            {
                null => new(null, "pipe closed before hello"),
                HelloMessage { Protocol: not IpcProtocol.Version } h => new(null, $"child speaks protocol {h.Protocol}, expected {IpcProtocol.Version}"),
                HelloMessage h when h.Desk != rt.Id => new(null, $"child announced desk '{h.Desk}', expected '{rt.Id}'"),
                HelloMessage h when !allowedRoles.Contains(h.Role) => new(null, $"child announced role '{h.Role}', expected {string.Join(" or ", allowedRoles)}"),
                HelloMessage h => new(h, null),
                _ => new(null, $"expected hello, got '{first.Type}'"),
            };
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return new(null, $"child did not connect and say hello within {o.HelloTimeoutMs} ms");
        }
        catch (IpcProtocolException ex)
        {
            return new(null, $"protocol error: {ex.Message}");
        }
        catch (IOException ex) when (!stop.IsCancellationRequested)
        {
            return new(null, $"pipe broken before hello: {ex.Message}");
        }
    }

    private async Task<string> ReadLoopAsync(DeskRuntime rt, IpcConnection connection, CancellationToken ct)
    {
        while (true)
        {
            IpcMessage? message;
            try
            {
                message = await connection.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return "cancelled";
            }
            catch (IpcProtocolException ex)
            {
                return $"protocol error: {ex.Message}";
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return $"pipe broken: {ex.Message}";
            }

            if (message is null)
            {
                return "pipe closed by child";
            }

            try
            {
                await HandleMessageAsync(rt, connection, message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return "cancelled";
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return $"pipe broken: {ex.Message}";
            }
        }
    }

    private async Task HandleMessageAsync(DeskRuntime rt, IpcConnection connection, IpcMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case HeartbeatMessage heartbeat:
                lock (rt.Gate)
                {
                    rt.LastHeartbeat = _time.GetUtcNow();
                    rt.ChildState = heartbeat.State;
                    rt.Metrics = DeskRuntime.MapMetrics(heartbeat.Metrics);
                }

                break;

            case ReadyMessage:
                lock (rt.Gate)
                {
                    rt.LastHeartbeat = _time.GetUtcNow();
                    if (!rt.Machine.TryFire(DeskTrigger.WorkerReady, out _))
                    {
                        _logger.LogDebug("Desk {Desk}: ignoring ready in state {State}", rt.Id, rt.Machine.State);
                    }
                }

                break;

            case EventMessage { Name: IpcProtocol.Events.ClientConnected }:
                Fire(rt, DeskTrigger.ClientConnected);
                break;

            case EventMessage { Name: IpcProtocol.Events.ClientDisconnected }:
                Fire(rt, DeskTrigger.ClientDisconnected);
                break;

            case EventMessage other:
                _logger.LogInformation("Desk {Desk} event {Event}", rt.Id, other.Name);
                break;

            case LogMessage log:
                _childLogger.Log(MapLevel(log.Level), "[{Desk}] {Message}", rt.Id, log.Message);
                break;

            case ResultMessage:
                // Completed by IpcConnection; a result nobody waits for (late answer) is dropped.
                break;

            case UnknownIpcMessage { Id: long id } unknown:
                await connection.ReplyAsync(id, ok: false, errorCode: IpcProtocol.ErrorCodes.UnknownType,
                    errorMessage: $"unknown message type '{unknown.TypeName}'", cancellationToken: ct).ConfigureAwait(false);
                break;

            case IIpcRequest request:
                await connection.ReplyAsync(request.Id, ok: false, errorCode: IpcProtocol.ErrorCodes.Unsupported,
                    errorMessage: $"the service does not accept '{message.Type}'", cancellationToken: ct).ConfigureAwait(false);
                break;

            default:
                _logger.LogDebug("Desk {Desk}: ignoring '{Type}'", rt.Id, message.Type);
                break;
        }
    }

    private async Task<string> WatchdogAsync(DeskRuntime rt, DateTimeOffset readyDeadline, CancellationToken ct)
    {
        var o = _options.Value;
        var period = TimeSpan.FromMilliseconds(Math.Max(10, o.HeartbeatIntervalMs / 4));
        using var timer = new PeriodicTimer(period, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = _time.GetUtcNow();
                lock (rt.Gate)
                {
                    var silence = now - rt.LastHeartbeat;
                    if (silence > o.HeartbeatTimeout)
                    {
                        return $"no heartbeat for {(long)silence.TotalMilliseconds} ms ({o.MissedHeartbeats} missed): child hung";
                    }

                    if (rt.Machine.State == DeskState.DisplayReady && now > readyDeadline)
                    {
                        return $"child not ready within {o.ReadyTimeoutMs} ms";
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return "cancelled";
    }

    private async Task ShutdownChildAsync(DeskRuntime rt, IpcConnection connection, IChildProcess child)
    {
        var timeout = TimeSpan.FromMilliseconds(_options.Value.ShutdownTimeoutMs);
        var deadline = _time.GetUtcNow() + timeout;
        try
        {
            var result = await connection.RequestAsync(id => new ShutdownMessage { Id = id }, timeout).ConfigureAwait(false);
            _logger.LogDebug("Desk {Desk}: shutdown answered ok={Ok}", rt.Id, result.Ok);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or EndOfStreamException)
        {
            _logger.LogDebug("Desk {Desk}: shutdown request failed: {Message}", rt.Id, ex.Message);
        }

        var remaining = deadline - _time.GetUtcNow();
        try
        {
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException();
            }

            await child.Exited.WaitAsync(remaining).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Desk {Desk}: child {Pid} did not exit within {Timeout}; killing it", rt.Id, child.Pid, timeout);
            child.Kill();
        }
    }

    private async Task PushProfileAsync(string id, IpcConnection connection, ProfileConfig profile)
    {
        try
        {
            var result = await connection.RequestAsync(rid => new SetProfileMessage { Id = rid, Profile = DeskRuntime.ToIpcProfile(profile) },
                TimeSpan.FromMilliseconds(_options.Value.RequestTimeoutMs)).ConfigureAwait(false);
            if (!result.Ok)
            {
                _logger.LogWarning("Desk {Desk} rejected the new profile: {Code} {Message}", id, result.Error?.Code, result.Error?.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Desk {Desk}: could not push the new profile: {Message}", id, ex.Message);
        }
    }

    private static LogLevel MapLevel(string? level) => level switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "fatal" or "critical" => LogLevel.Critical,
        _ => LogLevel.Information,
    };

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete {Path}: {Message}", path, ex.Message);
        }
    }
}
