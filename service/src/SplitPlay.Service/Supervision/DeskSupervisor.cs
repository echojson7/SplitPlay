// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Options;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Core.Steam;
using SplitPlay.Service.Api;
using SplitPlay.Service.Configuration;
using SplitPlay.Service.Hosting;
using SplitPlay.Service.Platform;

namespace SplitPlay.Service.Supervision;

/// <summary>
/// Owns every desk's lifecycle: drives the <see cref="DeskStateMachine"/>, provisions the account, launches and
/// watches the seat and the worker, restarts them with backoff and shuts them down. Also coordinates desk CRUD with the
/// configuration.
/// </summary>
/// <remarks>
/// Lifecycle (brief "Service lifecycle"): Provisioning (account, DPAPI password, host check, firewall rules) →
/// Connecting (seat launched, welcome with the RDP target) → SessionReady (seat <c>ready</c> with a session) →
/// DisplayReady (immediate in the MVP) → worker launched in the session → Ready (worker <c>ready</c>).
/// A worker failure relaunches only the worker; a seat failure kills the worker and reconnects. Stop shuts the worker
/// down first, then the seat, which logs the session off. With <c>Supervisor:Seat=none</c> the seat steps are skipped.
/// </remarks>
public sealed class DeskSupervisor : IHostedService
{
    /// <summary>Desk session size when the profile has none.</summary>
    private const int DefaultWidth = 1920;

    private const int DefaultHeight = 1080;

    /// <summary>What <see cref="RunOnceAsync"/> returns when the worker was stopped because the PC goes to sleep (compared by reference).</summary>
    private static readonly string PausedForSleep = new("the PC is going to sleep".AsSpan());

    private readonly ConcurrentDictionary<string, DeskRuntime> _desks = new(StringComparer.Ordinal);
    private readonly object _registryGate = new();
    private readonly ConfigService _config;
    private readonly DataPaths _paths;
    private readonly IChildLauncher _launcher;
    private readonly IOptions<SupervisorOptions> _options;
    private readonly TimeProvider _time;

    /// <summary>How long <see cref="IsDeskSession"/> keeps whether a user is a desk user.</summary>
    internal static readonly TimeSpan UserKindLifetime = TimeSpan.FromSeconds(5);

    private readonly object _userKindGate = new();
    private readonly Dictionary<string, (bool Desk, DateTimeOffset At)> _userKinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<DeskSupervisor> _logger;
    private readonly ILogger _childLogger;
    private readonly GpuInventory _gpus;
    private readonly IDeskPlatform _platform;
    private readonly DateTimeOffset _startedAt;
    private TimeSpan _lastCpu;
    private DateTimeOffset _lastCpuAt;

    // Power state: _sleep completes when the PC goes to sleep, _wake when it is awake (again).
    private readonly RequiredServicesStarter _requiredServices;
    private Task _startup = Task.CompletedTask;
    private readonly object _powerGate = new();
    private TaskCompletionSource _sleep = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _wake = Completed();

    public DeskSupervisor(ConfigService config, DataPaths paths, IChildLauncher launcher, IOptions<SupervisorOptions> options,
        TimeProvider time, ILogger<DeskSupervisor> logger, ILoggerFactory loggerFactory, GpuInventory gpus, IDeskPlatform platform,
        PowerEvents power, RequiredServicesStarter requiredServices)
    {
        ArgumentNullException.ThrowIfNull(power);
        power.Changed += OnPowerChange;
        _requiredServices = requiredServices;
        _gpus = gpus;
        _platform = platform;
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

    /// <summary>Completes when the start-up checks (leftover sessions, Remote Desktop Services) are done; desks wait for it.</summary>
    public Task Startup => _startup;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startup = Task.Run(StartupChecksAsync, CancellationToken.None);
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

        // Worker shutdown, then seat shutdown (twice as long: it logs the session off).
        var limit = TimeSpan.FromMilliseconds(_options.Value.ShutdownTimeoutMs * 3L + 2000);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("Not all desks stopped within {Limit}", limit);
        }
    }

    // ------------------------------------------------------------------ start-up checks

    /// <summary>
    /// Before the first desk starts: deals with desk sessions left over from before the service started (a crash, a
    /// killed process, an update that restarted the service) and gives Remote Desktop Services time to come up after a
    /// reboot. Bounded by <see cref="SupervisorOptions.StartupWaitMs"/>; never throws.
    /// </summary>
    private async Task StartupChecksAsync()
    {
        var limit = TimeSpan.FromMilliseconds(Math.Max(0, _options.Value.StartupWaitMs));
        var started = _time.GetTimestamp();
        try
        {
            using var cts = new CancellationTokenSource(limit, _time);
            await Task.Run(() => CleanUpLeftoverSessions(cts.Token)).WaitAsync(limit, _time).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("The check for desk sessions left over from before the service started did not finish within {Limit} s (Windows is not answering session queries); skipping it",
                (int)limit.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not check for desk sessions left over from before the service started: {Message}", ex.Message);
        }

        var remaining = limit - _time.GetElapsedTime(started);
        try
        {
            await _requiredServices.Completion.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Remote Desktop Services are still starting; starting the desks anyway");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The start-up check of Remote Desktop Services failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Disconnected sessions of desk users that are still there when the service starts. A desk that starts
    /// automatically reconnects to its session (games keep running); other sessions of accounts SplitPlay created are
    /// logged off. Sessions of accounts SplitPlay did not create are never touched.
    /// </summary>
    private void CleanUpLeftoverSessions(CancellationToken ct)
    {
        var leftovers = _platform.Sessions().Where(s => s.Disconnected && s.User is not null).ToList();
        if (leftovers.Count == 0)
        {
            return;
        }

        var desks = _config.Snapshot().Desks;
        foreach (var session in leftovers)
        {
            var user = session.User!;
            var desk = desks.FirstOrDefault(d => string.Equals(d.User, user, StringComparison.OrdinalIgnoreCase));
            var ours = _platform.FindAccount(user)?.CreatedBySplitPlay == true && desk?.UserManaged != false;
            if (desk is { Autostart: true } && _options.Value.Autostart)
            {
                _logger.LogInformation("Desk {Desk}: session {Session} of {User} is still there from before the service started; the desk reconnects to it",
                    desk.Id, session.Id, user);
            }
            else if (!ours)
            {
                if (desk is not null)
                {
                    _logger.LogInformation("Desk {Desk}: session {Session} of {User} is disconnected; left alone (SplitPlay did not create this account)",
                        desk.Id, session.Id, user);
                }
            }
            else
            {
                // Desks may start once the start-up wait is over: then nothing more is logged off. Each session is
                // checked again right before, so that a session that changed hands in between is left alone.
                if (ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Stopped logging off leftover sessions: the start-up wait is over");
                    return;
                }

                var again = _platform.Sessions().FirstOrDefault(s => s.Id == session.Id);
                if (again is not { Disconnected: true } || !string.Equals(again.User, user, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    _platform.LogoffSession(session.Id);
                    _logger.LogInformation("Logged off session {Session} of {User}, left over from before the service started ({Desk})",
                        session.Id, user, desk is null ? "no desk uses this account any more" : $"desk {desk.Id}");
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _logger.LogWarning("Could not log off leftover session {Session} of {User}: {Message}", session.Id, user, ex.Message);
                }
            }
        }
    }

    // ------------------------------------------------------------------ sleep and resume

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    /// <summary>Completes when the PC goes to sleep (already completed while it sleeps).</summary>
    private Task SleepTask
    {
        get
        {
            lock (_powerGate)
            {
                return _sleep.Task;
            }
        }
    }

    /// <summary>Completes once the PC is awake.</summary>
    private Task WakeTask
    {
        get
        {
            lock (_powerGate)
            {
                return _wake.Task;
            }
        }
    }

    /// <summary>
    /// Sleep: every running worker is shut down (its NVENC session and desktop duplication do not survive sleep) and
    /// its desk waits, paused, with its seat and session kept. Resume: the paused desks reconnect and get new workers.
    /// Neither counts as a failure.
    /// </summary>
    private void OnPowerChange(PowerChange change)
    {
        lock (_powerGate)
        {
            if (change == PowerChange.Suspend && !_sleep.Task.IsCompleted)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _sleep.TrySetResult();
                _logger.LogInformation("Stopping the desks' workers for sleep");
            }
            else if (change == PowerChange.Resume && _sleep.Task.IsCompleted)
            {
                _sleep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _wake.TrySetResult();
                _logger.LogInformation("Restarting the desks' workers after sleep");
            }
        }
    }

    // ------------------------------------------------------------------ queries

    public IReadOnlyList<DeskDto> List() =>
        _config.Snapshot().Desks.Select(d => _desks.TryGetValue(d.Id, out var rt) ? rt.ToDto() : null).OfType<DeskDto>().ToList();

    public DeskDto Get(string id) => Find(id).ToDto();

    /// <summary>
    /// True when <paramref name="sessionId"/> belongs to a desk user: a session a seat opened, or any session signed in
    /// with a configured desk user or an account SplitPlay created (also a desk that is stopped, or a leftover session).
    /// The console session and session 0 never are; a session whose user cannot be read counts as a desk (deny).
    /// A worker started in another session without a seat (<c>Supervisor:Seat=none</c>, tests) does not make that session
    /// a desk.
    /// </summary>
    public bool IsDeskSession(int sessionId)
    {
        if (sessionId <= 0)
        {
            // Session 0 is the services' session, never a desk.
            return false;
        }

        foreach (var rt in _desks.Values)
        {
            lock (rt.Gate)
            {
                if (rt.Seat?.Session == sessionId)
                {
                    return true;
                }
            }
        }

        try
        {
            if (_platform.ConsoleSessionId() == sessionId)
            {
                return false;
            }

            // Every API request asks. Who is signed in is read every time (a session id can be reused by someone else);
            // whether that user is a desk user (the config and an account lookup) is kept for a few seconds.
            var user = _platform.SessionUser(sessionId);
            if (user is null)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            lock (_userKindGate)
            {
                if (_userKinds.TryGetValue(user, out var known) && now - known.At < UserKindLifetime)
                {
                    return known.Desk;
                }
            }

            var desk = IsDeskUser(user);
            lock (_userKindGate)
            {
                if (_userKinds.Count > 256)
                {
                    _userKinds.Clear();
                }

                _userKinds[user] = (desk, now);
            }

            return desk;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Cannot tell who is signed in to session {Session} ({Message}); treating it as a desk session", sessionId, ex.Message);
            return true;
        }
    }

    /// <summary>A configured desk user, or an account SplitPlay created.</summary>
    public bool IsDeskUser(string user) =>
        _config.Snapshot().Desks.Any(d => string.Equals(d.User, user, StringComparison.OrdinalIgnoreCase))
        || _platform.FindAccount(user)?.CreatedBySplitPlay == true;

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

        var (memoryUsedMb, memoryTotalMb) = GpuInventory.PhysicalMemory();
        var composeMs = _config.Snapshot().Host.ComposeIntervalMs;
        var host = new HostMetricsDto(
            Math.Round(Math.Clamp(cpuPercent, 0, 100), 1),
            memoryUsedMb,
            memoryTotalMb,
            composeMs > 0 ? Math.Round(1000.0 / composeMs, 1) : null,
            _gpus.Get());
        var desks = List().Select(d => DeskMetricsEntryDto.From(d.Id, d.Metrics)).ToList();
        return new MetricsDto(host, desks);
    }

    // ------------------------------------------------------------------ CRUD

    /// <summary>
    /// Creates a desk. <paramref name="password"/> is the Windows password of <c>desk.user</c>: verified when the
    /// account exists, used for the new account otherwise; stored DPAPI-protected, never in config.json.
    /// </summary>
    public DeskDto Create(DeskConfig desk, string? password = null)
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

            desk.User ??= ConfigStore.DefaultUserName(desk.Id, _config.Snapshot().Desks.Select(d => d.User));
            desk.UserManaged = CheckUser(desk.User, password);
            desk.SteamIsolation ??= true;
            var saved = _config.Update(cfg =>
            {
                cfg.Desks.Add(desk);
                return desk;
            });

            if (password is not null)
            {
                _platform.StorePassword(saved.Id, password);
            }
            else
            {
                // A secret left behind by an earlier desk with the same id belongs to another account.
                _platform.ForgetPassword(saved.Id);
            }

            var rt = AddRuntime(saved);
            _logger.LogInformation("Created desk {Desk} (base port {Port}, user {User})", saved.Id, saved.BasePort, saved.User);
            _ = Task.Run(() =>
            {
                try
                {
                    _platform.EnsureFirewallRules(saved);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Desk {Desk}: could not add firewall rules: {Message}", saved.Id, ex.Message);
                }
            });
            Publish(rt);
            return rt.ToDto();
        }
    }

    /// <summary>Replaces a desk's settings; <paramref name="password"/> as in <see cref="Create"/>.</summary>
    public DeskDto Replace(string id, DeskConfig desk, string? password = null)
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
            desk.SteamIsolation ??= current.SteamIsolation ?? true;
            var userChanged = !string.Equals(desk.User, current.User, StringComparison.OrdinalIgnoreCase);
            if (!userChanged)
            {
                desk.HideFromLogon ??= current.HideFromLogon;
            }

            desk.UserManaged = userChanged || password is not null ? CheckUser(desk.User!, password) : current.UserManaged;
            if (userChanged && desk.UserManaged == false && desk.HideFromLogon == true)
            {
                // A client that sends back the old desk's effective value must not hide an account SplitPlay did not
                // create: that takes a request of its own once the user is set.
                _logger.LogInformation("Desk {Desk}: hideFromLogon ignored while switching to {User}, an account SplitPlay did not create", id, desk.User);
                desk.HideFromLogon = null;
            }

            if (!idle && (desk.Name != current.Name || !string.Equals(desk.User, current.User, StringComparison.OrdinalIgnoreCase)
                          || desk.BasePort != current.BasePort || desk.Autostart != current.Autostart || desk.Gpu != current.Gpu
                          || desk.DisableBluetoothUserService != current.DisableBluetoothUserService
                          || desk.SteamIsolationEnabled != current.SteamIsolationEnabled
                          || !SameSettings(desk.WorkerSettings, current.WorkerSettings)))
            {
                throw ApiException.Conflict(ApiErrorCodes.InvalidState, $"desk '{id}' is running: only 'profile' can change; stop it first");
            }

            var saved = _config.Update(cfg =>
            {
                var index = cfg.Desks.FindIndex(d => d.Id == id);
                cfg.Desks[index] = desk;
                return desk;
            });

            if (password is not null)
            {
                _platform.StorePassword(id, password);
            }
            else if (userChanged)
            {
                _platform.ForgetPassword(id);
            }

            if (userChanged || !saved.SteamIsolationEnabled)
            {
                // The old user's hive gets its previous values back (the desk is idle: its session is logged off).
                _platform.RevertSteamIsolation(id);
            }

            if (userChanged || saved.HideFromLogon != current.HideFromLogon)
            {
                // A registry value: applies at once, also while the desk runs.
                _platform.UpdateLogonListing(saved);
            }

            IpcConnection? connection;
            lock (rt.Gate)
            {
                rt.Config = saved;
                rt.HasPassword = _platform.HasPassword(id);
                connection = rt.Connection;
            }

            if (connection is not null)
            {
                _ = PushProfileAsync(id, connection, saved.Profile);
            }

            // A new resolution for a running desk: resize the RDP session if the seat can, else it applies at the next start.
            if (!idle && (saved.Profile.Width != current.Profile.Width || saved.Profile.Height != current.Profile.Height))
            {
                _ = ApplyResolutionAsync(rt, saved.Profile.Width ?? DefaultWidth, saved.Profile.Height ?? DefaultHeight);
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

            DeskConfig config;
            lock (rt.Gate)
            {
                config = rt.Config;
            }

            try
            {
                _platform.DeleteDesk(config, deleteUser);
            }
            catch (DeskUserNotManagedException ex)
            {
                throw ApiException.Conflict(ApiErrorCodes.UserNotManaged, ex.Message);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                throw new ApiException(StatusCodes.Status500InternalServerError, ApiErrorCodes.Internal,
                    $"could not delete {(deleteUser ? "the Windows account or " : "")}the stored password of desk '{id}': {ex.Message}");
            }

            try
            {
                _platform.RemoveFirewallRules(id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Desk {Desk}: could not remove firewall rules: {Message}", id, ex.Message);
            }

            _config.Update(cfg => cfg.Desks.RemoveAll(d => d.Id == id));
            _desks.TryRemove(id, out _);
            TryDeleteDirectory(_paths.DeskDirectory(id));
            _logger.LogInformation("Deleted desk {Desk}{User}", id, deleteUser ? $" and Windows account {config.User}" : "");
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
                await task.WaitAsync(TimeSpan.FromMilliseconds(_options.Value.ShutdownTimeoutMs * 3L + 2000)).ConfigureAwait(false);
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

    /// <summary>
    /// Checks a desk user before it is saved: never the console user (409), and a password given for an existing
    /// account must be right (400). Returns <see cref="DeskConfig.UserManaged"/>: true when SplitPlay creates the
    /// account (or created it earlier), false for an existing account the owner picked.
    /// </summary>
    private bool CheckUser(string user, string? password)
    {
        if (!ConfigValidator.IsValidUserName(user))
        {
            throw ApiException.Validation($"desk.user: must be a valid local Windows account name (1-{ConfigValidator.MaxUserNameLength} characters)");
        }

        if (_platform.ConsoleUserName() is { } console && string.Equals(console, user, StringComparison.OrdinalIgnoreCase))
        {
            throw ApiException.Conflict(ApiErrorCodes.ConsoleUser,
                $"'{user}' is signed in at this PC's console; a desk with that user would take over the console session. Pick another user");
        }

        var account = _platform.FindAccount(user);
        if (account is null)
        {
            return true;
        }

        if (password is not null && !_platform.VerifyPassword(user, password))
        {
            throw ApiException.Validation($"wrong password for {user}");
        }

        return account.CreatedBySplitPlay;
    }

    private DeskRuntime AddRuntime(DeskConfig desk)
    {
        var machine = new DeskStateMachine(_time, _options.Value.ToRecoveryPolicy());
        var rt = new DeskRuntime(desk, machine) { HasPassword = _platform.HasPassword(desk.Id) };
        machine.Transitioned += t => OnTransition(rt, t);
        _desks[desk.Id] = rt;
        return rt;
    }

    private void OnTransition(DeskRuntime rt, DeskTransition t)
    {
        if (t.Trigger is DeskTrigger.Pause or DeskTrigger.Resume)
        {
            _logger.LogInformation("Desk {Desk}: {From} -> {To} ({Reason})", rt.Id, t.From, t.To,
                t.Trigger == DeskTrigger.Pause ? t.Reason : "the PC woke up");
        }
        else if (t.To == DeskState.Faulted && t.Trigger == DeskTrigger.Fail && t.From != DeskState.Provisioning)
        {
            _logger.LogError("Desk {Desk}: giving up after {Failures} failures ({Reason}); it stays Faulted until it is stopped and started again",
                rt.Id, rt.Machine.ConsecutiveFailures, t.Reason);
        }
        else if (t.To is DeskState.Recovering or DeskState.Faulted)
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

    /// <summary>
    /// The desk's run loop: Provisioning, then repeated runs (seat, worker) with backoff until stopped or Faulted.
    /// The seat survives worker restarts; a seat failure also ends the worker.
    /// </summary>
    private async Task RunAsync(DeskRuntime rt, CancellationToken stop)
    {
        try
        {
            DeskConfig config;
            lock (rt.Gate)
            {
                config = rt.Config;
                rt.RelogonDone = false;
                rt.Warning = null;
                rt.ResolutionPendingRestart = false;
            }

            Directory.CreateDirectory(_paths.DeskDirectory(rt.Id));
            await _startup.WaitAsync(stop).ConfigureAwait(false);
            var credentials = await ProvisionAsync(rt, config, stop).ConfigureAwait(false);
            if (credentials is null)
            {
                return;
            }

            lock (rt.Gate)
            {
                rt.HasPassword = true;
                rt.Warning = credentials.Warning;
            }

            if (!Fire(rt, DeskTrigger.Provisioned))
            {
                return;
            }

            while (true)
            {
                await WakeTask.WaitAsync(stop).ConfigureAwait(false);
                var reason = await RunOnceAsync(rt, credentials, stop).ConfigureAwait(false);
                if (ReferenceEquals(reason, PausedForSleep))
                {
                    lock (rt.Gate)
                    {
                        if (!rt.Machine.TryFire(DeskTrigger.Pause, out _, reason))
                        {
                            stop.ThrowIfCancellationRequested();
                            return;
                        }
                    }

                    await WakeTask.WaitAsync(stop).ConfigureAwait(false);
                    lock (rt.Gate)
                    {
                        if (!rt.Machine.TryFire(DeskTrigger.Resume, out _))
                        {
                            return;
                        }
                    }

                    continue;
                }

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
            // Stop order: the worker is already gone here (RunOnceAsync shuts it down); the seat goes last and logs
            // the session off.
            await StopSeatAsync(rt).ConfigureAwait(false);
            lock (rt.Gate)
            {
                rt.Running = false;
                rt.Connection = null;
                rt.SessionId = null;
                rt.ChildPid = null;
                rt.ChildState = null;
                rt.SessionLost = false;
                rt.Metrics = new DeskMetricsDto();
                rt.ClearStreams();
                if (rt.Machine.State == DeskState.Stopping)
                {
                    rt.Machine.Fire(DeskTrigger.Stopped);
                }
            }
        }
    }

    /// <summary>
    /// Provisioning with bounds: errors that may be transient (Windows still starting after a reboot or an update) are
    /// retried <see cref="SupervisorOptions.ProvisionAttempts"/> times with a growing delay; a call that does not return
    /// within <see cref="SupervisorOptions.ProvisionTimeoutMs"/> faults the desk (it cannot be cancelled, and a second call
    /// next to it could race on the account's password). Returns null after firing Fail.
    /// </summary>
    private async Task<DeskCredentials?> ProvisionAsync(DeskRuntime rt, DeskConfig config, CancellationToken stop)
    {
        var o = _options.Value;
        var attempts = Math.Max(1, o.ProvisionAttempts);
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1, o.ProvisionTimeoutMs));
        for (var attempt = 1; ; attempt++)
        {
            Task<DeskCredentials>? call = null;
            lock (rt.Gate)
            {
                if (rt.ProvisionCall is not { IsCompleted: false })
                {
                    call = Task.Run(() =>
                    {
                        var c = _platform.Provision(config);
                        _platform.EnsureFirewallRules(config);
                        return c;
                    }, CancellationToken.None);
                    rt.ProvisionCall = call;
                }
            }

            if (call is null)
            {
                _logger.LogError("Desk {Desk}: the previous provisioning call has still not returned", rt.Id);
                Fire(rt, DeskTrigger.Fail, "the previous provisioning call has still not returned: Windows is not answering account calls (restart the PC)");
                return null;
            }

            try
            {
                return await call.WaitAsync(timeout, _time, stop).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("Desk {Desk}: provisioning did not finish within {Timeout} s; Windows is not answering account or Remote Desktop calls",
                    rt.Id, (int)timeout.TotalSeconds);
                Fire(rt, DeskTrigger.Fail,
                    $"provisioning did not finish within {(int)timeout.TotalSeconds} s: Windows is not answering account or Remote Desktop calls");
                return null;
            }
            catch (DeskProvisioningException ex)
            {
                Fire(rt, DeskTrigger.Fail, ex.Message);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= attempts)
                {
                    _logger.LogError(ex, "Provisioning desk {Desk} failed {Attempts} times; giving up", rt.Id, attempt);
                    Fire(rt, DeskTrigger.Fail, attempts > 1 ? $"provisioning failed {attempt} times: {ex.Message}" : $"provisioning failed: {ex.Message}");
                    return null;
                }

                var delay = TimeSpan.FromMilliseconds((long)Math.Max(0, o.ProvisionRetryDelayMs) * attempt);
                _logger.LogWarning("Desk {Desk}: provisioning attempt {Attempt} of {Attempts} failed ({Message}); retrying in {Delay} s",
                    rt.Id, attempt, attempts, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, _time, stop).ConfigureAwait(false);
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

    /// <summary>
    /// One run from Connecting: ensures the seat and its session, then runs one worker until something fails.
    /// Returns the failure reason; throws <see cref="OperationCanceledException"/> when stopped (after shutting the
    /// worker down; the seat is shut down by the caller).
    /// </summary>
    private async Task<string> RunOnceAsync(DeskRuntime rt, DeskCredentials? credentials, CancellationToken stop)
    {
        var o = _options.Value;
        var sleep = SleepTask;
        DeskConfig config;
        lock (rt.Gate)
        {
            config = rt.Config;
        }

        // ---- seat and session
        int? session;
        if (o.UseSeat)
        {
            var seatFailure = await EnsureSeatAsync(rt, config, credentials, stop).ConfigureAwait(false);
            if (seatFailure is not null)
            {
                return seatFailure;
            }

            lock (rt.Gate)
            {
                session = rt.Seat!.Session;
            }
        }
        else
        {
            session = await Task.Run(() => ResolveWorkerSession(o), stop).ConfigureAwait(false);
        }

        lock (rt.Gate)
        {
            rt.SessionId = session;
            if (!rt.Machine.TryFire(DeskTrigger.SessionReady, out _))
            {
                stop.ThrowIfCancellationRequested();
                return $"unexpected state {rt.Machine.State}";
            }

            // DisplayReady is immediate in the MVP (no IDD yet).
            if (!rt.Machine.TryFire(DeskTrigger.DisplayReady, out _))
            {
                stop.ThrowIfCancellationRequested();
                return $"unexpected state {rt.Machine.State}";
            }
        }

        // ---- worker
        if (sleep.IsCompleted)
        {
            // Went to sleep while the session came up: start the worker after resume.
            return PausedForSleep;
        }

        int attempt;
        lock (rt.Gate)
        {
            attempt = ++rt.Attempt;
            rt.WorkerAbort = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var request = WorkerLaunchRequest(rt.Id, config, session, attempt);
        var opened = await ChildChannel.OpenAsync(request, PipeNames.Local(IpcProtocol.Roles.Worker, rt.Id, o.PipeNamespace),
            [IpcProtocol.Roles.Worker, IpcProtocol.Roles.StubWorker], _launcher, o, _time, _logger, stop).ConfigureAwait(false);
        if (opened.Channel is not { } worker)
        {
            return opened.Failure!;
        }

        await using var workerGuard = worker.ConfigureAwait(false);
        try
        {
            lock (rt.Gate)
            {
                rt.ChildPid = worker.Hello.Pid;
                rt.LastHeartbeat = _time.GetUtcNow();
            }

            if (session is int expected && worker.Hello.Session is int reported && reported != expected)
            {
                _logger.LogWarning("Desk {Desk}: the worker reports session {Reported}, expected {Expected}", rt.Id, reported, expected);
            }

            await worker.Connection.SendAsync(new WelcomeMessage { Profile = DeskRuntime.ToIpcProfile(config.Profile) }, stop).ConfigureAwait(false);
            lock (rt.Gate)
            {
                rt.Connection = worker.Connection;
            }

            worker.Run((m, ct) => HandleWorkerMessageAsync(rt, worker, m, ct), TimeSpan.FromMilliseconds(o.ReadyTimeoutMs));

            Task<string> seatLost;
            TaskCompletionSource<string> abort;
            lock (rt.Gate)
            {
                seatLost = rt.Seat?.Completion ?? new TaskCompletionSource<string>().Task;
                abort = rt.WorkerAbort!;
            }

            var stopped = Task.Delay(Timeout.Infinite, stop);
            var winner = await Task.WhenAny(worker.Completion, seatLost, abort.Task, stopped, sleep).ConfigureAwait(false);
            if (winner == stopped)
            {
                await worker.ShutdownAsync(TimeSpan.FromMilliseconds(o.ShutdownTimeoutMs)).ConfigureAwait(false);
                stop.ThrowIfCancellationRequested();
            }

            if (winner == sleep)
            {
                // Windows gives the suspend notification about two seconds: a clean shutdown if the worker manages it,
                // and the job object takes the rest.
                await worker.ShutdownAsync(TimeSpan.FromMilliseconds(Math.Min(o.ShutdownTimeoutMs, 2000))).ConfigureAwait(false);
                return PausedForSleep;
            }

            if (winner == seatLost)
            {
                // The session is gone or unusable: the worker goes with it, the next run reconnects.
                var reason = await seatLost.ConfigureAwait(false);
                worker.Process.Kill();
                await DropSeatAsync(rt).ConfigureAwait(false);
                return reason;
            }

            if (winner == abort.Task)
            {
                worker.Process.Kill();
                return await abort.Task.ConfigureAwait(false);
            }

            return await worker.Completion.ConfigureAwait(false);
        }
        catch (IOException ex) when (!stop.IsCancellationRequested)
        {
            return $"worker pipe broken: {ex.Message}";
        }
        finally
        {
            lock (rt.Gate)
            {
                if (ReferenceEquals(rt.Connection, worker.Connection))
                {
                    rt.Connection = null;
                }

                rt.WorkerAbort = null;
                rt.ChildPid = null;
                rt.Metrics = new DeskMetricsDto();
                rt.ClearStreams();
            }
        }
    }

    private int? ResolveWorkerSession(SupervisorOptions o)
    {
        var value = o.WorkerSession.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (string.Equals(value, "console", StringComparison.OrdinalIgnoreCase))
        {
            return _platform.ConsoleSessionId() ?? throw new InvalidOperationException("there is no console session to start the worker in");
        }

        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Sunshine settings for the worker: defaults recommended in worker/README.md, overridden by the desk's
    /// <c>workerSettings</c>. Encoder, adapter, output and audio sink stay unset so that the worker picks the session's own.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, string>> WorkerSettings(string deskId, DeskConfig config)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sunshine_name"] = $"{Environment.MachineName}-{deskId}",
            ["min_log_level"] = "info",
            ["nvenc_preset"] = "1",
            ["nvenc_twopass"] = "disabled",
            ["hevc_mode"] = "0",
            ["av1_mode"] = "0",
            // XInput hands button state only to the console session's foreground window (rumble still works), so an
            // emulated Xbox 360 pad is deaf inside a desk. A DualShock 4 is a HID device and reaches the desk's games
            // directly or through Steam Input; Duo switched to DS4 for the same reason.
            ["gamepad"] = "ds4",
        };
        foreach (var (name, value) in config.WorkerSettings ?? [])
        {
            // Only the allowed settings ever reach a worker running as SYSTEM, whatever config.json says.
            if (ConfigValidator.IsAllowedWorkerSetting(name))
            {
                settings[name] = value;
            }
        }

        return settings;
    }

    /// <summary>The worker command line (ipc.md "Child command lines") plus the desk's extra Sunshine settings.</summary>
    private ChildLaunchRequest WorkerLaunchRequest(string deskId, DeskConfig config, int? session, int attempt)
    {
        var o = _options.Value;
        var pipe = PipeNames.Full(IpcProtocol.Roles.Worker, deskId, o.PipeNamespace);
        var deskDirectory = _paths.DeskDirectory(deskId);
        var args = new List<string>(SupervisorOptions.SplitArguments(o.WorkerArguments));
        if (o.StubWorker)
        {
            args.AddRange(["--pipe", pipe, "--desk", deskId]);
        }
        else
        {
            UpdateSteamApp(deskId, config, deskDirectory);
            args.AddRange(["--desk-dir", deskDirectory, "--pipe", pipe, "--desk", deskId]);
            args.Add(FormattableString.Invariant($"port={config.BasePort}"));
            foreach (var (name, value) in WorkerSettings(deskId, config))
            {
                args.Add($"{name}={value}");
            }
        }

        return new ChildLaunchRequest(deskId, IpcProtocol.Roles.Worker, pipe, deskDirectory, attempt)
        {
            Executable = o.ResolveWorkerExecutable(),
            Arguments = args,
            SessionId = session,
        };
    }

    /// <summary>
    /// The Moonlight app "Steam" in the desk's <c>apps.json</c> (read by the worker at start-up): starts the desk's
    /// isolated Steam in Big Picture; removed again when isolation is off or Steam is not installed.
    /// </summary>
    private void UpdateSteamApp(string deskId, DeskConfig config, string deskDirectory)
    {
        try
        {
            var steam = config.SteamIsolationEnabled ? _platform.SteamExecutable() : null;
            if (SteamApps.Ensure(Path.Combine(deskDirectory, SteamApps.FileName), deskId, steam, config.SteamIsolationEnabled))
            {
                _logger.LogInformation("Desk {Desk}: {Action} the Moonlight app \"Steam\" in apps.json", deskId, steam is null ? "removed" : "updated");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning("Desk {Desk}: could not update apps.json: {Message}", deskId, ex.Message);
        }
    }

    // ------------------------------------------------------------------ seat

    /// <summary>Makes sure a seat with a logged-on session runs. Returns a failure reason, or null when ready.</summary>
    private async Task<string?> EnsureSeatAsync(DeskRuntime rt, DeskConfig config, DeskCredentials? credentials, CancellationToken stop)
    {
        ChildChannel? existing;
        lock (rt.Gate)
        {
            existing = rt.Seat;
        }

        if (existing is { IsAlive: true, Session: not null })
        {
            return null;
        }

        if (existing is not null)
        {
            await DropSeatAsync(rt).ConfigureAwait(false);
        }

        if (credentials is null)
        {
            return "no desk credentials";
        }

        var failure = await StartSeatAsync(rt, config, credentials, stop).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        int session;
        lock (rt.Gate)
        {
            session = rt.Seat!.Session!.Value;
        }

        bool relogon;
        try
        {
            relogon = await Task.Run(() => _platform.OnSessionLogon(config, session, _paths.DeskDirectory(rt.Id)), stop)
                .WaitAsync(TimeSpan.FromSeconds(30), stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            _logger.LogWarning("Desk {Desk}: first-logon actions failed: {Message}", rt.Id, ex.Message);
            relogon = false;
        }

        bool again;
        lock (rt.Gate)
        {
            again = relogon && _options.Value.RelogonAfterLayoutCopy && !rt.RelogonDone;
            rt.RelogonDone |= again;
        }

        if (again)
        {
            // Windows reads the keyboard layout list and the user's environment at logon: log the session off once so the
            // copied layouts and the Steam isolation variable apply.
            _logger.LogInformation("Desk {Desk}: logging the new session off once so that the copied keyboard layouts and the Steam environment apply", rt.Id);
            await StopSeatAsync(rt).ConfigureAwait(false);
            failure = await StartSeatAsync(rt, config, credentials, stop).ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }

            lock (rt.Gate)
            {
                session = rt.Seat!.Session!.Value;
            }
        }

        StartSessionJanitor(rt, config, session);
        return null;
    }

    /// <summary>Launches the seat, sends welcome with the RDP target and waits for its ready with a session.</summary>
    private async Task<string?> StartSeatAsync(DeskRuntime rt, DeskConfig config, DeskCredentials credentials, CancellationToken stop)
    {
        var o = _options.Value;
        var pipe = PipeNames.Full(IpcProtocol.Roles.Seat, rt.Id, o.PipeNamespace);
        var attempt = Interlocked.Increment(ref rt.SeatAttempts);
        var request = new ChildLaunchRequest(rt.Id, IpcProtocol.Roles.Seat, pipe, _paths.DeskDirectory(rt.Id), attempt)
        {
            Executable = o.ResolveSeatExecutable(),
            Arguments = [.. SupervisorOptions.SplitArguments(o.SeatArguments), "--pipe", pipe, "--desk", rt.Id],
        };

        var opened = await ChildChannel.OpenAsync(request, PipeNames.Local(IpcProtocol.Roles.Seat, rt.Id, o.PipeNamespace),
            [IpcProtocol.Roles.Seat], _launcher, o, _time, _logger, stop).ConfigureAwait(false);
        if (opened.Channel is not { } seat)
        {
            return opened.Failure;
        }

        var ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (rt.Gate)
        {
            rt.Seat = seat;
            rt.SessionLost = false;
        }

        try
        {
            var port = await Task.Run(_platform.RdpPort, stop).ConfigureAwait(false);
            var profile = config.Profile;
            await seat.Connection.SendAsync(new WelcomeMessage
            {
                Profile = DeskRuntime.ToIpcProfile(profile),
                Rdp = new IpcRdpTarget
                {
                    Host = o.RdpHost,
                    Port = port,
                    User = credentials.User,
                    Domain = credentials.Domain,
                    Password = credentials.Password,
                    Width = profile.Width ?? DefaultWidth,
                    Height = profile.Height ?? DefaultHeight,
                },
            }, stop).ConfigureAwait(false);

            seat.Run((m, ct) => HandleSeatMessageAsync(rt, seat, m, ready, ct), TimeSpan.FromMilliseconds(o.SeatReadyTimeoutMs));
            var stopped = Task.Delay(Timeout.Infinite, stop);
            var winner = await Task.WhenAny(ready.Task, seat.Completion, stopped).ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            var failure = winner == ready.Task ? await ready.Task.ConfigureAwait(false) : await seat.Completion.ConfigureAwait(false);
            if (failure is null)
            {
                _logger.LogInformation("Desk {Desk}: session {Session} is logged on for {User}", rt.Id, seat.Session, credentials.User);
                return null;
            }

            await DropSeatAsync(rt).ConfigureAwait(false);
            return failure;
        }
        catch (IOException ex) when (!stop.IsCancellationRequested)
        {
            await DropSeatAsync(rt).ConfigureAwait(false);
            return $"seat pipe broken: {ex.Message}";
        }
    }

    private async Task HandleSeatMessageAsync(DeskRuntime rt, ChildChannel seat, IpcMessage message, TaskCompletionSource<string?> ready,
        CancellationToken ct)
    {
        switch (message)
        {
            case ReadyMessage r when r.Session is int session:
                seat.Session = session;
                seat.MarkReady();
                ready.TrySetResult(null);
                break;

            case ReadyMessage r:
                ready.TrySetResult($"seat is up but reported no session (connector '{r.Connector ?? "null"}'): the RDP connection did not log on");
                break;

            case EventMessage { Name: IpcProtocol.Events.SessionLost } e:
            {
                int loss;
                lock (rt.Gate)
                {
                    rt.SessionLost = true;
                    loss = ++rt.SessionLosses;
                }

                _logger.LogWarning("Desk {Desk}: RDP session lost ({Reason}); the seat is reconnecting", rt.Id, EventString(e, "reason") ?? "unknown");
                _ = WatchSessionLossAsync(rt, seat, loss);
                break;
            }

            case EventMessage { Name: IpcProtocol.Events.SessionRestored } e:
            {
                var restored = EventInt(e, "session");
                int? previous;
                TaskCompletionSource<string>? abort;
                lock (rt.Gate)
                {
                    rt.SessionLost = false;
                    previous = seat.Session;
                    if (restored is int s)
                    {
                        seat.Session = s;
                    }

                    abort = rt.WorkerAbort;
                }

                if (restored is int now && previous is int before && now != before)
                {
                    _logger.LogWarning("Desk {Desk}: the RDP session came back as session {Now} (was {Before}); restarting the worker there",
                        rt.Id, now, before);
                    abort?.TrySetResult($"desk session changed from {before} to {now} after a reconnect");
                }
                else
                {
                    _logger.LogInformation("Desk {Desk}: RDP session restored (session {Session})", rt.Id, restored ?? previous);
                }

                break;
            }

            case EventMessage other:
                _logger.LogInformation("Desk {Desk} seat event {Event}", rt.Id, other.Name);
                break;

            case LogMessage log:
                _childLogger.Log(MapLevel(log.Level), "[{Desk}/seat] {Message}", rt.Id, log.Message);
                break;

            case HeartbeatMessage:
                break;

            default:
                _logger.LogDebug("Desk {Desk}: ignoring '{Type}' from the seat", rt.Id, message.Type);
                break;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// The seat reconnects a lost session on its own and gives up after its own attempts; this bounds the case where a
    /// reconnect hangs (TermService stuck, a logon that never finishes): the seat is replaced.
    /// </summary>
    private async Task WatchSessionLossAsync(DeskRuntime rt, ChildChannel seat, int loss)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1, _options.Value.SessionLostTimeoutMs));
        await Task.WhenAny(Task.Delay(timeout, _time), seat.Completion).ConfigureAwait(false);
        lock (rt.Gate)
        {
            if (!ReferenceEquals(rt.Seat, seat) || !rt.SessionLost || rt.SessionLosses != loss || !seat.IsAlive)
            {
                return;
            }
        }

        _logger.LogError("Desk {Desk}: the RDP session did not come back within {Timeout} s; replacing the seat", rt.Id, (int)timeout.TotalSeconds);
        seat.Fail($"the RDP session did not come back within {(int)timeout.TotalSeconds} s");
    }

    /// <summary>Stops the per-session janitor and shuts the seat down gracefully (it logs the session off).</summary>
    private async Task StopSeatAsync(DeskRuntime rt)
    {
        ChildChannel? seat;
        lock (rt.Gate)
        {
            seat = rt.Seat;
        }

        if (seat is null)
        {
            return;
        }

        CancelSessionJanitor(rt);
        try
        {
            await seat.ShutdownAsync(TimeSpan.FromMilliseconds(_options.Value.ShutdownTimeoutMs * 2L)).ConfigureAwait(false);
        }
        finally
        {
            await DropSeatAsync(rt).ConfigureAwait(false);
        }
    }

    /// <summary>Forgets the seat and kills whatever is left of it.</summary>
    private async Task DropSeatAsync(DeskRuntime rt)
    {
        ChildChannel? seat;
        lock (rt.Gate)
        {
            seat = rt.Seat;
            rt.Seat = null;
            rt.SessionLost = false;
        }

        CancelSessionJanitor(rt);
        if (seat is not null)
        {
            await seat.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ per-session janitor (Bluetooth)

    private void StartSessionJanitor(DeskRuntime rt, DeskConfig config, int session)
    {
        CancelSessionJanitor(rt);
        if (!config.DisableBluetoothUserService)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        lock (rt.Gate)
        {
            rt.SeatScope = cts;
        }

        _ = Task.Run(() => BluetoothJanitorAsync(rt.Id, session, cts.Token), CancellationToken.None);
    }

    private void CancelSessionJanitor(DeskRuntime rt)
    {
        CancellationTokenSource? cts;
        lock (rt.Gate)
        {
            cts = rt.SeatScope;
            rt.SeatScope = null;
        }

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>
    /// GitHub issue #3: several BluetoothUserService instances fight over the radio and can reset the console user's
    /// devices. Stops the desk session's instance, and again whenever Windows restarts it, while the session lives.
    /// </summary>
    private async Task BluetoothJanitorAsync(string deskId, int session, CancellationToken ct)
    {
        var stops = 0;
        var period = TimeSpan.FromMilliseconds(Math.Max(100, _options.Value.BluetoothPollMs));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var stopped = await Task.Run(() => _platform.StopBluetoothUserService(session), ct)
                        .WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    foreach (var name in stopped)
                    {
                        stops++;
                        _logger.LogInformation("Desk {Desk}: stopped {Service} in session {Session}{Again}", deskId, name, session,
                            stops > 1 ? " again (Windows restarted it)" : "");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug("Desk {Desk}: Bluetooth user service check failed: {Message}", deskId, ex.Message);
                }

                await Task.Delay(period, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ------------------------------------------------------------------ worker messages

    private async Task HandleWorkerMessageAsync(DeskRuntime rt, ChildChannel worker, IpcMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case HeartbeatMessage heartbeat:
                if (rt.ApplyHeartbeat(worker.Connection, heartbeat, _time.GetUtcNow()) == WorkerUpdate.Publish)
                {
                    Publish(rt);
                }

                break;

            case ReadyMessage:
                worker.MarkReady();
                lock (rt.Gate)
                {
                    if (!rt.IsCurrent(worker.Connection))
                    {
                        break;
                    }

                    rt.LastHeartbeat = _time.GetUtcNow();
                    if (!rt.Machine.TryFire(DeskTrigger.WorkerReady, out _))
                    {
                        _logger.LogDebug("Desk {Desk}: ignoring ready in state {State}", rt.Id, rt.Machine.State);
                    }
                }

                break;

            case EventMessage { Name: IpcProtocol.Events.ClientConnected } e:
            {
                var stream = IpcStreamSession.FromJson(e.Data) ?? new IpcStreamSession();
                var update = rt.ApplyClientConnected(worker.Connection, stream);
                if (update == WorkerUpdate.Ignored)
                {
                    break;
                }

                _logger.LogInformation("Desk {Desk}: {Client} ({Address}) connected, {Width}x{Height}@{Fps} {Codec}", rt.Id,
                    stream.Client ?? "unnamed client", stream.Address ?? "?", stream.Width, stream.Height, stream.FpsRequested, stream.Codec ?? "?");
                if (update == WorkerUpdate.Publish)
                {
                    Publish(rt);
                }

                break;
            }

            case EventMessage { Name: IpcProtocol.Events.ClientDisconnected } e:
            {
                var stream = IpcStreamSession.FromJson(e.Data) ?? new IpcStreamSession();
                var update = rt.ApplyClientDisconnected(worker.Connection, stream);
                if (update == WorkerUpdate.Ignored)
                {
                    break;
                }

                _logger.LogInformation("Desk {Desk}: {Client} ({Address}) disconnected", rt.Id, stream.Client ?? "unnamed client", stream.Address ?? "?");
                if (update == WorkerUpdate.Publish)
                {
                    Publish(rt);
                }

                break;
            }

            case EventMessage { Name: IpcProtocol.Events.ModeRequested } e:
                _ = ForwardModeAsync(rt, EventInt(e, "width"), EventInt(e, "height"), EventInt(e, "fps"));
                break;

            case EventMessage other:
                _logger.LogInformation("Desk {Desk} event {Event}", rt.Id, other.Name);
                break;

            case LogMessage log:
                _childLogger.Log(MapLevel(log.Level), "[{Desk}] {Message}", rt.Id, log.Message);
                break;

            default:
                _logger.LogDebug("Desk {Desk}: ignoring '{Type}'", rt.Id, message.Type);
                break;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>A Moonlight client asked for a mode: resize the session through the seat (<c>set_mode</c>).</summary>
    private async Task ApplyResolutionAsync(DeskRuntime rt, int width, int height)
    {
        var applied = await ForwardModeAsync(rt, width, height, null).ConfigureAwait(false);
        lock (rt.Gate)
        {
            rt.ResolutionPendingRestart = !applied;
        }

        Publish(rt);
    }

    /// <summary>Asks the seat to resize the desk session; false if there is no seat or it could not.</summary>
    private async Task<bool> ForwardModeAsync(DeskRuntime rt, int? width, int? height, int? fps)
    {
        if (width is not (> 0) || height is not (> 0))
        {
            _logger.LogWarning("Desk {Desk}: mode_requested without a valid width and height", rt.Id);
            return false;
        }

        IpcConnection? seat;
        lock (rt.Gate)
        {
            seat = rt.Seat?.Connection;
        }

        if (seat is null)
        {
            _logger.LogInformation("Desk {Desk}: {Width}x{Height}@{Fps} requested but there is no seat to resize the session",
                rt.Id, width, height, fps);
            return false;
        }

        try
        {
            var result = await seat.RequestAsync(id => new SetModeMessage { Id = id, Width = width.Value, Height = height.Value },
                TimeSpan.FromMilliseconds(_options.Value.RequestTimeoutMs)).ConfigureAwait(false);
            if (result.Ok)
            {
                _logger.LogInformation("Desk {Desk}: session resized to {Width}x{Height} ({Fps} fps)", rt.Id, width, height, fps);
                return true;
            }

            _logger.LogInformation("Desk {Desk}: the seat could not resize to {Width}x{Height}: {Code} {Message}",
                rt.Id, width, height, result.Error?.Code, result.Error?.Message);
            return false;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or EndOfStreamException or InvalidOperationException)
        {
            _logger.LogWarning("Desk {Desk}: set_mode failed: {Message}", rt.Id, ex.Message);
            return false;
        }
    }

    private static int? EventInt(EventMessage e, string name) =>
        e.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } d && d.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static string? EventString(EventMessage e, string name) =>
        e.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } d && d.TryGetProperty(name, out var v)
            ? v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

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
            else if (result.Data is { } applied)
            {
                // The worker lists what applies now, what from the next stream and what it does not apply (ipc.md).
                _logger.LogInformation("Desk {Desk}: new profile applied {Result}", id, applied.GetRawText());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Desk {Desk}: could not push the new profile: {Message}", id, ex.Message);
        }
    }

    private static bool SameSettings(Dictionary<string, string>? a, Dictionary<string, string>? b) =>
        (a is null || a.Count == 0) && (b is null || b.Count == 0)
        || (a is not null && b is not null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value));

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
