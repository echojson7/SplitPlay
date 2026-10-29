// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Lifecycle;

/// <summary>One state change.</summary>
public sealed record DeskTransition(DeskState From, DeskState To, DeskTrigger Trigger, DateTimeOffset At, string? Reason);

/// <summary>A trigger is not allowed in the current state.</summary>
public sealed class InvalidDeskTransitionException(DeskState state, DeskTrigger trigger)
    : InvalidOperationException($"'{trigger}' is not allowed while the desk is {state}")
{
    public DeskState State { get; } = state;

    public DeskTrigger Trigger { get; } = trigger;
}

/// <summary>
/// Pure desk lifecycle logic (ARCHITECTURE.md §4) with an injectable clock. It does no I/O and is not thread-safe;
/// the owner serializes calls.
/// </summary>
/// <remarks>
/// The diagram in ARCHITECTURE.md §4 is implemented as drawn, with these additions the diagram leaves open:
/// <list type="bullet">
/// <item><c>Stop</c> is also accepted while starting up (Provisioning … DisplayReady) and while Recovering,
/// so a desk stuck in a retry loop can always be stopped.</item>
/// <item><c>Fail</c> during Provisioning goes straight to Faulted: provisioning errors (account, rights, LSA) are not
/// transient and retrying from Connecting would skip the failed step.</item>
/// <item><c>Pause</c> (the PC goes to sleep) moves a running desk to Recovering without counting a failure and without
/// a retry time; only <c>Resume</c> (back to Connecting) or <c>Stop</c> leave it.</item>
/// </list>
/// </remarks>
public sealed class DeskStateMachine
{
    private static readonly Dictionary<(DeskState, DeskTrigger), DeskState> Simple = new()
    {
        [(DeskState.Defined, DeskTrigger.Start)] = DeskState.Provisioning,
        [(DeskState.Stopped, DeskTrigger.Start)] = DeskState.Provisioning,
        [(DeskState.Provisioning, DeskTrigger.Provisioned)] = DeskState.Connecting,
        [(DeskState.Connecting, DeskTrigger.SessionReady)] = DeskState.SessionReady,
        [(DeskState.SessionReady, DeskTrigger.DisplayReady)] = DeskState.DisplayReady,
        [(DeskState.DisplayReady, DeskTrigger.WorkerReady)] = DeskState.Ready,
        [(DeskState.Ready, DeskTrigger.ClientConnected)] = DeskState.Streaming,
        [(DeskState.Streaming, DeskTrigger.ClientDisconnected)] = DeskState.Ready,
        [(DeskState.Ready, DeskTrigger.Stop)] = DeskState.Stopping,
        [(DeskState.Streaming, DeskTrigger.Stop)] = DeskState.Stopping,
        [(DeskState.Faulted, DeskTrigger.Stop)] = DeskState.Stopping,
        [(DeskState.Stopping, DeskTrigger.Stopped)] = DeskState.Stopped,

        // Additions (see remarks).
        [(DeskState.Provisioning, DeskTrigger.Stop)] = DeskState.Stopping,
        [(DeskState.Connecting, DeskTrigger.Stop)] = DeskState.Stopping,
        [(DeskState.SessionReady, DeskTrigger.Stop)] = DeskState.Stopping,
        [(DeskState.DisplayReady, DeskTrigger.Stop)] = DeskState.Stopping,
        [(DeskState.Recovering, DeskTrigger.Stop)] = DeskState.Stopping,
    };

    /// <summary>States from which a failure leads to Recovering (or Faulted when the budget is exhausted).</summary>
    private static readonly HashSet<DeskState> Recoverable =
    [
        DeskState.Connecting, DeskState.SessionReady, DeskState.DisplayReady, DeskState.Ready, DeskState.Streaming,
    ];

    private readonly TimeProvider _time;
    private readonly Queue<DateTimeOffset> _failures = new();

    public DeskStateMachine(TimeProvider? time = null, RecoveryPolicy? policy = null, DeskState initial = DeskState.Defined)
    {
        _time = time ?? TimeProvider.System;
        Policy = policy ?? RecoveryPolicy.Default;
        State = initial;
        EnteredAt = _time.GetUtcNow();
    }

    public RecoveryPolicy Policy { get; }

    public DeskState State { get; private set; }

    /// <summary>When the current state was entered.</summary>
    public DateTimeOffset EnteredAt { get; private set; }

    /// <summary>Reason of the most recent failure, cleared on Start and on reaching Ready.</summary>
    public string? LastError { get; private set; }

    /// <summary>Failures since the desk was last Ready; drives the backoff exponent.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>While Recovering: the earliest time <see cref="DeskTrigger.RetryDue"/> is accepted.</summary>
    public DateTimeOffset? RetryAt { get; private set; }

    /// <summary>Recovering because of <see cref="DeskTrigger.Pause"/> (the PC sleeps), waiting for <see cref="DeskTrigger.Resume"/>.</summary>
    public bool Paused { get; private set; }

    /// <summary>Number of failures inside the sliding window, as of now.</summary>
    public int FailuresInWindow
    {
        get
        {
            Prune(_time.GetUtcNow());
            return _failures.Count;
        }
    }

    /// <summary>Raised after every transition.</summary>
    public event Action<DeskTransition>? Transitioned;

    /// <summary>True if <paramref name="trigger"/> would be accepted now.</summary>
    public bool CanFire(DeskTrigger trigger) => Resolve(trigger, _time.GetUtcNow(), commit: false) is not null;

    /// <summary>Applies <paramref name="trigger"/>; throws <see cref="InvalidDeskTransitionException"/> if not allowed.</summary>
    public DeskTransition Fire(DeskTrigger trigger, string? reason = null) =>
        TryFire(trigger, out var transition, reason) ? transition : throw new InvalidDeskTransitionException(State, trigger);

    /// <summary>Applies <paramref name="trigger"/> if allowed.</summary>
    public bool TryFire(DeskTrigger trigger, out DeskTransition transition, string? reason = null)
    {
        var now = _time.GetUtcNow();
        var target = Resolve(trigger, now, commit: false);
        if (target is null)
        {
            transition = null!;
            return false;
        }

        Resolve(trigger, now, commit: true, reason);
        transition = new DeskTransition(State, target.Value, trigger, now, reason);
        State = target.Value;
        EnteredAt = now;
        Transitioned?.Invoke(transition);
        return true;
    }

    private DeskState? Resolve(DeskTrigger trigger, DateTimeOffset now, bool commit, string? reason = null)
    {
        if (Simple.TryGetValue((State, trigger), out var simple))
        {
            if (commit)
            {
                OnSimple(trigger);
            }

            return simple;
        }

        switch (trigger)
        {
            case DeskTrigger.Fail when State == DeskState.Provisioning:
                if (commit)
                {
                    RecordFailure(now, reason);
                    RetryAt = null;
                }

                return DeskState.Faulted;

            case DeskTrigger.Fail when Recoverable.Contains(State):
            {
                Prune(now);
                var faulted = _failures.Count + 1 > Policy.MaxFailuresInWindow
                              || ConsecutiveFailures + 1 > Policy.MaxConsecutiveFailures;
                if (commit)
                {
                    RecordFailure(now, reason);
                    RetryAt = faulted ? null : now + Policy.BackoffFor(ConsecutiveFailures);
                }

                return faulted ? DeskState.Faulted : DeskState.Recovering;
            }

            case DeskTrigger.Pause when Recoverable.Contains(State):
                if (commit)
                {
                    Paused = true;
                    RetryAt = null;
                }

                return DeskState.Recovering;

            case DeskTrigger.Resume when State == DeskState.Recovering && Paused:
                if (commit)
                {
                    Paused = false;
                }

                return DeskState.Connecting;

            case DeskTrigger.RetryDue when State == DeskState.Recovering && RetryAt is { } at && now >= at:
                if (commit)
                {
                    RetryAt = null;
                }

                return DeskState.Connecting;

            default:
                return null;
        }
    }

    private void OnSimple(DeskTrigger trigger)
    {
        switch (trigger)
        {
            case DeskTrigger.Start:
                // A user-initiated start gets a fresh failure budget.
                _failures.Clear();
                ConsecutiveFailures = 0;
                LastError = null;
                RetryAt = null;
                Paused = false;
                break;
            case DeskTrigger.WorkerReady:
                ConsecutiveFailures = 0;
                LastError = null;
                break;
            case DeskTrigger.Stop:
                RetryAt = null;
                Paused = false;
                break;
        }
    }

    private void RecordFailure(DateTimeOffset now, string? reason)
    {
        _failures.Enqueue(now);
        ConsecutiveFailures++;
        LastError = reason ?? "failure";
    }

    private void Prune(DateTimeOffset now)
    {
        while (_failures.Count > 0 && now - _failures.Peek() > Policy.FailureWindow)
        {
            _failures.Dequeue();
        }
    }
}
