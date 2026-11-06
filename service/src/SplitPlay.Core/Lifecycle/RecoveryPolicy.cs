// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Lifecycle;

/// <summary>
/// Recovery rules from ARCHITECTURE.md §4: retry with backoff 1, 2, 4 … 60 s; more than 5 failures within 10 minutes
/// puts the desk into <see cref="DeskState.Faulted"/>, and so do more than 10 failures in a row without reaching Ready
/// (slow failures such as a logon that times out after minutes never fill the window, but must not retry forever).
/// </summary>
public sealed record RecoveryPolicy
{
    public static RecoveryPolicy Default { get; } = new();

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A desk faults when the number of failures inside <see cref="FailureWindow"/> exceeds this value.</summary>
    public int MaxFailuresInWindow { get; init; } = 5;

    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>A desk faults when its failures since it was last Ready exceed this value, however far apart they are.</summary>
    public int MaxConsecutiveFailures { get; init; } = 10;

    /// <summary>
    /// After a graphics driver reset the worker restarts this much later: the driver and DWM come back within a couple
    /// of seconds, and a worker started into the reset would only fail again.
    /// </summary>
    public TimeSpan GpuResetDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Driver resets inside <see cref="FailureWindow"/> that restart a desk without counting as failures; more than this
    /// (a driver that keeps crashing) count as ordinary failures, so the desk still faults instead of looping.
    /// </summary>
    public int MaxGpuResetsInWindow { get; init; } = 3;

    /// <summary>
    /// A failure at most this long before a reset is reported is put down to the reset and forgiven: the worker often
    /// dies of the lost device before Windows logs the reset.
    /// </summary>
    public TimeSpan GpuResetForgiveWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay before retry number <paramref name="consecutiveFailures"/> (1-based): initial · 2^(n-1), capped at the maximum.
    /// </summary>
    public TimeSpan BackoffFor(int consecutiveFailures)
    {
        if (consecutiveFailures < 1)
        {
            return TimeSpan.Zero;
        }

        // 2^30 already exceeds any sane cap; avoid overflow for long failure streaks.
        var exponent = Math.Min(consecutiveFailures - 1, 30);
        var ticks = InitialBackoff.Ticks * (double)(1L << exponent);
        return ticks >= MaxBackoff.Ticks ? MaxBackoff : TimeSpan.FromTicks((long)ticks);
    }
}
