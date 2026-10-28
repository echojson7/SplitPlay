// SPDX-License-Identifier: MIT
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;

namespace SplitPlay.Service.Supervision;

/// <summary>Supervisor settings, bound from the <c>Supervisor</c> configuration section.</summary>
public sealed class SupervisorOptions
{
    public const string Section = "Supervisor";

    /// <summary><c>process</c> (start <see cref="ChildExecutable"/>) or <c>fake</c> (in-memory fake child). Empty = by OS.</summary>
    public string Launcher { get; set; } = "";

    /// <summary>
    /// Child executable; relative paths resolve against the service directory. The default matches the installed
    /// layout (<c>%ProgramFiles%\SplitPlay\service</c> next to <c>native</c>).
    /// </summary>
    public string ChildExecutable { get; set; } = @"..\native\splitplay-stubworker.exe";

    /// <summary>Arguments placed before <c>--pipe … --desk …</c>, separated by spaces.</summary>
    public string ChildArguments { get; set; } = "";

    /// <summary>The <c>role</c> the child is expected to announce; the pipe is <c>worker-&lt;desk&gt;</c> for all worker roles.</summary>
    public string ChildRole { get; set; } = IpcProtocol.Roles.Worker;

    /// <summary>Namespace inside <c>\\.\pipe\</c>; tests use a unique one to run in parallel.</summary>
    public string PipeNamespace { get; set; } = PipeNames.DefaultNamespace;

    public int HeartbeatIntervalMs { get; set; } = 1000;

    /// <summary>Missed heartbeats after which the child is treated as hung (ipc.md: 3).</summary>
    public int MissedHeartbeats { get; set; } = 3;

    /// <summary>Time for the child to connect and say hello.</summary>
    public int HelloTimeoutMs { get; set; } = 30_000;

    /// <summary>Time from welcome to ready.</summary>
    public int ReadyTimeoutMs { get; set; } = 60_000;

    /// <summary>Time a child has to answer shutdown and exit (ipc.md: 5 s).</summary>
    public int ShutdownTimeoutMs { get; set; } = 5_000;

    /// <summary>Timeout for requests such as pair or list_clients.</summary>
    public int RequestTimeoutMs { get; set; } = 10_000;

    public int InitialBackoffMs { get; set; } = 1_000;

    public int MaxBackoffMs { get; set; } = 60_000;

    public int MaxFailuresInWindow { get; set; } = 5;

    public int FailureWindowSeconds { get; set; } = 600;

    /// <summary>Start desks flagged <c>autostart</c> when the service starts.</summary>
    public bool Autostart { get; set; } = true;

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
}
