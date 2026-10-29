// SPDX-License-Identifier: MIT
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;

namespace SplitPlay.Service.Supervision;

/// <summary>Supervisor settings, bound from the <c>Supervisor</c> configuration section.</summary>
public sealed class SupervisorOptions
{
    public const string Section = "Supervisor";

    /// <summary><c>process</c> (start real executables) or <c>fake</c> (in-memory fake children). Empty = by OS.</summary>
    public string Launcher { get; set; } = "";

    /// <summary><c>windows</c> (real accounts, firewall, host check) or <c>fake</c> (in memory). Empty = by OS.</summary>
    public string Platform { get; set; } = "";

    /// <summary><c>real</c> (splitplay-worker.exe, Sunshine) or <c>stub</c> (splitplay-stubworker.exe, no network).</summary>
    public string Worker { get; set; } = "real";

    /// <summary>Overrides the worker executable; relative paths resolve against the service directory.</summary>
    public string WorkerExecutable { get; set; } = "";

    /// <summary>Arguments placed before the worker's own arguments, separated by spaces (e.g. <c>fake-child</c>).</summary>
    public string WorkerArguments { get; set; } = "";

    /// <summary>
    /// <c>real</c>: launch splitplay-seat.exe, which opens the loopback RDP session the worker runs in.
    /// <c>none</c>: no seat; the worker runs in <see cref="WorkerSession"/> (tests and CI without an RDP connector).
    /// </summary>
    public string Seat { get; set; } = "real";

    public string SeatExecutable { get; set; } = @"..\native\splitplay-seat.exe";

    /// <summary>Arguments placed before <c>--pipe … --desk …</c> for the seat.</summary>
    public string SeatArguments { get; set; } = "";

    /// <summary>
    /// With <c>Seat=none</c>: empty = the worker is a plain child in the service's own session; <c>console</c> or a
    /// session id = launched into that session with the duplicated SYSTEM token, as with a real seat.
    /// </summary>
    public string WorkerSession { get; set; } = "";

    /// <summary>Loopback address the seat connects to (127.0.0.2 so that it does not collide with the console's own loopback use).</summary>
    public string RdpHost { get; set; } = "127.0.0.2";

    /// <summary>
    /// <see cref="Launcher"/> <c>fake</c> only: every fake worker reports a Moonlight client streaming to it with live
    /// metrics (UI development without hardware).
    /// </summary>
    public bool FakeStreams { get; set; }

    /// <summary>Namespace inside <c>\\.\pipe\</c>; tests use a unique one to run in parallel.</summary>
    public string PipeNamespace { get; set; } = PipeNames.DefaultNamespace;

    public int HeartbeatIntervalMs { get; set; } = 1000;

    /// <summary>Missed heartbeats after which a child is treated as hung (ipc.md: 3).</summary>
    public int MissedHeartbeats { get; set; } = 3;

    /// <summary>Time for a child to connect and say hello.</summary>
    public int HelloTimeoutMs { get; set; } = 30_000;

    /// <summary>Time from welcome to the worker's ready.</summary>
    public int ReadyTimeoutMs { get; set; } = 60_000;

    /// <summary>Time from welcome to the seat's ready (RDP logon; a first logon creates the profile and is slow).</summary>
    public int SeatReadyTimeoutMs { get; set; } = 180_000;

    /// <summary>Time a child has to answer shutdown and exit (ipc.md: 5 s). The seat gets twice as long (logoff).</summary>
    public int ShutdownTimeoutMs { get; set; } = 5_000;

    /// <summary>Timeout for requests such as pair, list_clients or set_mode.</summary>
    public int RequestTimeoutMs { get; set; } = 10_000;

    public int InitialBackoffMs { get; set; } = 1_000;

    public int MaxBackoffMs { get; set; } = 60_000;

    public int MaxFailuresInWindow { get; set; } = 5;

    public int FailureWindowSeconds { get; set; } = 600;

    /// <summary>Start desks flagged <c>autostart</c> when the service starts.</summary>
    public bool Autostart { get; set; } = true;

    /// <summary>
    /// After the console user's keyboard layouts were copied into a new desk profile, or the Steam isolation variable
    /// was newly written into a logged-on hive, log the session off once so that they apply (Windows loads the layout
    /// list and the user's environment at logon).
    /// </summary>
    public bool RelogonAfterLayoutCopy { get; set; } = true;

    /// <summary>How often the desk session's BluetoothUserService instance is checked (and stopped again).</summary>
    public int BluetoothPollMs { get; set; } = 10_000;

    public TimeSpan HeartbeatInterval => TimeSpan.FromMilliseconds(HeartbeatIntervalMs);

    public TimeSpan HeartbeatTimeout => TimeSpan.FromMilliseconds((long)HeartbeatIntervalMs * MissedHeartbeats);

    public RecoveryPolicy ToRecoveryPolicy() => new()
    {
        InitialBackoff = TimeSpan.FromMilliseconds(InitialBackoffMs),
        MaxBackoff = TimeSpan.FromMilliseconds(MaxBackoffMs),
        MaxFailuresInWindow = MaxFailuresInWindow,
        FailureWindow = TimeSpan.FromSeconds(FailureWindowSeconds),
    };

    /// <summary>The effective launcher kind.</summary>
    public string EffectiveLauncher =>
        !string.IsNullOrWhiteSpace(Launcher) ? Launcher.Trim().ToLowerInvariant() : OperatingSystem.IsWindows() ? "process" : "fake";

    /// <summary>The effective platform kind.</summary>
    public string EffectivePlatform =>
        !string.IsNullOrWhiteSpace(Platform) ? Platform.Trim().ToLowerInvariant() : OperatingSystem.IsWindows() ? "windows" : "fake";

    public bool StubWorker => string.Equals(Worker.Trim(), "stub", StringComparison.OrdinalIgnoreCase);

    public bool UseSeat => !string.Equals(Seat.Trim(), "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>The worker executable as a full path.</summary>
    public string ResolveWorkerExecutable() => Resolve(!string.IsNullOrWhiteSpace(WorkerExecutable)
        ? WorkerExecutable
        : StubWorker ? @"..\native\splitplay-stubworker.exe" : @"..\worker\splitplay-worker.exe");

    public string ResolveSeatExecutable() => Resolve(SeatExecutable);

    /// <summary>Resolves a path relative to the service directory; <c>\</c> and <c>/</c> both work on every OS.</summary>
    public static string Resolve(string path)
    {
        var normalized = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(normalized) ? normalized : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, normalized));
    }

    public static IEnumerable<string> SplitArguments(string arguments) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
