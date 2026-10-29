// SPDX-License-Identifier: MIT
namespace SplitPlay.Service.Api;

/// <summary>
/// Slows down password guessing on <c>/auth/login</c> and <c>/auth/password</c>: after <see cref="FreeFailures"/> wrong
/// passwords from one caller, each further one locks that caller out for 1, 2, 4 … s up to <see cref="MaxLockout"/>;
/// more than <see cref="GlobalFreeFailures"/> failures from everybody within <see cref="GlobalWindow"/> lock everybody
/// out the same way. A caller's count is forgotten after <see cref="MaxLockout"/> without failures or on a success.
/// Password checks run one at a time (PBKDF2 is deliberately slow).
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    public const int FreeFailures = 5;

    public const int GlobalFreeFailures = 20;

    public static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _peers = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly SemaphoreSlim _verify = new(1, 1);
    private int _globalExcess;
    private DateTimeOffset _globalLockedUntil;

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset LockedUntil;
        public DateTimeOffset LastFailure;
    }

    /// <summary>How long <paramref name="peer"/> has to wait before the next attempt, or null when it may try now.</summary>
    public TimeSpan? RetryAfter(string peer)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var until = _globalLockedUntil;
            if (_peers.TryGetValue(peer, out var e))
            {
                if (now - e.LastFailure > MaxLockout && now >= e.LockedUntil)
                {
                    _peers.Remove(peer);
                }
                else if (e.LockedUntil > until)
                {
                    until = e.LockedUntil;
                }
            }

            return until > now ? until - now : null;
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
            while (_recent.Count > 0 && now - _recent.Peek() > GlobalWindow)
            {
                _recent.Dequeue();
            }

            if (_recent.Count > GlobalFreeFailures)
            {
                _globalLockedUntil = now + Backoff(_globalExcess++);
            }
            else if (now >= _globalLockedUntil)
            {
                _globalExcess = 0;
            }

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

    /// <summary>Runs <paramref name="verify"/> (a PBKDF2 check) with no other check running at the same time.</summary>
    public async Task<bool> VerifyAsync(Func<bool> verify, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verify);
        await _verify.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return verify();
        }
        finally
        {
            _verify.Release();
        }
    }

    private static TimeSpan Backoff(int step)
    {
        var seconds = Math.Pow(2, Math.Min(step, 20));
        return seconds >= MaxLockout.TotalSeconds ? MaxLockout : TimeSpan.FromSeconds(seconds);
    }
}
