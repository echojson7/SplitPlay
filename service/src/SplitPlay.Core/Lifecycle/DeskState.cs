// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Lifecycle;

/// <summary>Desk lifecycle states (ARCHITECTURE.md §4). Serialized by name, e.g. <c>"Stopped"</c>.</summary>
public enum DeskState
{
    Defined,
    Provisioning,
    Connecting,
    SessionReady,
    DisplayReady,
    Ready,
    Streaming,
    Recovering,
    Faulted,
    Stopping,
    Stopped,
}

/// <summary>Events that drive the desk state machine.</summary>
public enum DeskTrigger
{
    /// <summary>User or autostart asked the desk to start.</summary>
    Start,

    /// <summary>Account, RDU rights and LSA password are in place.</summary>
    Provisioned,

    /// <summary>The seat opened the loopback RDP session and a session id is known.</summary>
    SessionReady,

    /// <summary>The IDD is up and the display mode is set.</summary>
    DisplayReady,

    /// <summary>The worker reported <c>ready</c> (ports listening).</summary>
    WorkerReady,

    /// <summary>A Moonlight client connected.</summary>
    ClientConnected,

    /// <summary>The last Moonlight client disconnected.</summary>
    ClientDisconnected,

    /// <summary>A child crashed, hung, timed out or a step failed.</summary>
    Fail,

    /// <summary>The recovery backoff elapsed.</summary>
    RetryDue,

    /// <summary>User asked the desk to stop.</summary>
    Stop,

    /// <summary>Children are gone and the session is closed.</summary>
    Stopped,
}

public static class DeskStateExtensions
{
    /// <summary>True for states in which nothing runs for the desk (configuration may be edited, desk may be deleted).</summary>
    public static bool IsIdle(this DeskState state) => state is DeskState.Defined or DeskState.Stopped;

    /// <summary>True for states from which <see cref="DeskTrigger.Start"/> is accepted.</summary>
    public static bool CanStart(this DeskState state) => state is DeskState.Defined or DeskState.Stopped;
}
