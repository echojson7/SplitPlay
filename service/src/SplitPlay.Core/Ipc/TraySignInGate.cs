// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Ipc;

/// <summary>
/// When the tray signs its page in through the admin pipe and reloads it. The page asks (an "auth" message) whenever it
/// is not signed in; an ask soon after a reload means the API still rejects the session the pipe granted. After
/// <see cref="MaxFailedReloads"/> of those the tray stops reloading and leaves the page on its password login, until
/// <see cref="Reset"/> (the dashboard is opened again).
/// </summary>
public sealed class TraySignInGate(TimeProvider time)
{
    public const int MaxFailedReloads = 3;

    /// <summary>Least time between two sign-ins.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

    /// <summary>An ask within this time of a reload counts as a failed reload.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private DateTimeOffset? _lastReload;
    private DateTimeOffset? _lastAttempt;
    private int _failed;
    private bool _stopped;

    /// <summary>True once the tray has given up reloading.</summary>
    public bool Stopped
    {
        get
        {
            lock (_gate)
            {
                return _stopped;
            }
        }
    }

    /// <summary>The page asked to be signed in: true when the tray should sign in and reload now.</summary>
    public bool ShouldSignIn()
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_stopped || (_lastAttempt is { } last && now - last < MinInterval))
            {
                return false;
            }

            if (_lastReload is { } previous && now - previous < FailureWindow)
            {
                if (++_failed >= MaxFailedReloads)
                {
                    _stopped = true;
                    return false;
                }
            }
            else
            {
                _failed = 0;
            }

            _lastAttempt = now;
            return true;
        }
    }

    /// <summary>The tray signed in and reloaded the page.</summary>
    public void Reloaded()
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            _lastReload = now;
        }
    }

    /// <summary>Starts over (the dashboard was opened again).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _lastReload = null;
            _lastAttempt = null;
            _failed = 0;
            _stopped = false;
        }
    }
}
