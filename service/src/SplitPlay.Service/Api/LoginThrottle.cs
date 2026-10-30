// SPDX-License-Identifier: MIT
namespace SplitPlay.Service.Api;

/// <summary>What became of a password attempt.</summary>
public enum LoginOutcome
{
    /// <summary>The password was right.</summary>
    Ok,

    /// <summary>The password was wrong (and counted).</summary>
    Wrong,

    /// <summary>Not checked: the caller is locked out or already has a check running; retry after <see cref="LoginAttempt.RetryAfter"/>.</summary>
    Throttled,
}

/// <summary>The result of <see cref="LoginThrottle.AttemptAsync"/>.</summary>
public readonly record struct LoginAttempt(LoginOutcome Outcome, TimeSpan RetryAfter = default);

/// <summary>
/// Slows down password guessing on <c>/auth/login</c> and <c>/auth/password</c>:
/// <list type="bullet">
/// <item>after <see cref="FreeFailures"/> wrong passwords from one caller, each further one locks that caller out for
/// 1, 2, 4 … s up to <see cref="MaxLockout"/>; the count is forgotten after <see cref="MaxLockout"/> without failures
/// or on a success;</item>
/// <item>a caller has at most one check running: further attempts meanwhile are refused, not queued;</item>
/// <item>more than <see cref="GlobalFreeFailures"/> failures from everybody within <see cref="GlobalWindow"/> slow every
/// check down (0.1, 0.2, 0.4 … s, at most <see cref="MaxGlobalDelay"/>) instead of locking everybody out, so that one
/// local process cannot keep the owner from signing in;</item>
/// <item>checks run one at a time (PBKDF2 is deliberately slow) and the lockout is checked again once it is this
/// caller's turn.</item>
/// </list>
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    public const int FreeFailures = 5;

    public const int GlobalFreeFailures = 20;

    public static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan MaxGlobalDelay = TimeSpan.FromSeconds(2);

    /// <summary>Retry-After for an attempt refused because the same caller already has a check running.</summary>
    public static readonly TimeSpan BusyRetryAfter = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _peers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly SemaphoreSlim _verify = new(1, 1);

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset LockedUntil;
        public DateTimeOffset LastFailure;
    }

    /// <summary>
    /// Checks a password for <paramref name="peer"/>: refused while it is locked out or has another check running,
    /// otherwise <paramref name="verify"/> runs (alone, after the global slow-down) and its outcome is counted.
    /// </summary>
    public async Task<LoginAttempt> AttemptAsync(string peer, Func<bool> verify, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(verify);
        lock (_gate)
        {
            if (!_pending.Add(peer))
            {
                return new LoginAttempt(LoginOutcome.Throttled, BusyRetryAfter);
            }
        }

        try
        {
            if (RetryAfter(peer) is { } wait)
            {
                return new LoginAttempt(LoginOutcome.Throttled, wait);
            }

            await _verify.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Checked again now that it is this caller's turn: the lockout may have started while it waited.
                if (RetryAfter(peer) is { } late)
                {
                    return new LoginAttempt(LoginOutcome.Throttled, late);
                }

                var delay = GlobalDelay();
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
                }

                if (verify())
                {
                    Succeeded(peer);
                    return new LoginAttempt(LoginOutcome.Ok);
                }

                Failed(peer);
                return new LoginAttempt(LoginOutcome.Wrong);
            }
            finally
            {
                _verify.Release();
            }
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(peer);
            }
        }
    }

    /// <summary>How long <paramref name="peer"/> is locked out, or null when it may try now.</summary>
    public TimeSpan? RetryAfter(string peer)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_peers.TryGetValue(peer, out var e))
            {
                return null;
            }

            if (now - e.LastFailure > MaxLockout && now >= e.LockedUntil)
            {
                _peers.Remove(peer);
                return null;
            }

            return e.LockedUntil > now ? e.LockedUntil - now : null;
        }
    }

    /// <summary>The delay added to every check while many wrong passwords arrive from everybody (zero otherwise).</summary>
    public TimeSpan GlobalDelay()
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            Expire(now);
            var excess = _recent.Count - GlobalFreeFailures;
            if (excess <= 0)
            {
                return TimeSpan.Zero;
            }

            var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, Math.Min(excess - 1, 10)));
            return delay > MaxGlobalDelay ? MaxGlobalDelay : delay;
        }
    }

    /// <summary>Records a wrong password.</summary>
    public void Failed(string peer)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_peers.TryGetValue(peer, out var e) || now - e.LastFailure > MaxLockout)
            {
                e = new Entry();
                _peers[peer] = e;
            }

            e.Failures++;
            e.LastFailure = now;
            if (e.Failures >= FreeFailures)
            {
                e.LockedUntil = now + Backoff(e.Failures - FreeFailures);
            }

            _recent.Enqueue(now);
            Expire(now);

            // Bounded memory: drop the oldest callers when someone cycles through many peer keys.
            if (_peers.Count > 1024)
            {
                foreach (var key in _peers.OrderBy(p => p.Value.LastFailure).Take(256).Select(p => p.Key).ToList())
                {
                    _peers.Remove(key);
                }
            }
        }
    }

    /// <summary>Records a correct password: the caller's count starts over.</summary>
    public void Succeeded(string peer)
    {
        lock (_gate)
        {
            _peers.Remove(peer);
        }
    }

    private void Expire(DateTimeOffset now)
    {
        while (_recent.Count > 0 && now - _recent.Peek() > GlobalWindow)
        {
            _recent.Dequeue();
        }
    }

    private static TimeSpan Backoff(int step)
    {
        var seconds = Math.Pow(2, Math.Min(step, 20));
        return seconds >= MaxLockout.TotalSeconds ? MaxLockout : TimeSpan.FromSeconds(seconds);
    }
}
