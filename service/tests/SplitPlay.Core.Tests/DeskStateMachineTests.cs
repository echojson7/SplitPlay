// SPDX-License-Identifier: MIT
using Microsoft.Extensions.Time.Testing;
using SplitPlay.Core.Lifecycle;

namespace SplitPlay.Core.Tests;

public sealed class DeskStateMachineTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

    private DeskStateMachine NewMachine(DeskState initial = DeskState.Defined) => new(_time, RecoveryPolicy.Default, initial);

    private static void BringUp(DeskStateMachine m)
    {
        m.Fire(DeskTrigger.Start);
        m.Fire(DeskTrigger.Provisioned);
        m.Fire(DeskTrigger.SessionReady);
        m.Fire(DeskTrigger.DisplayReady);
        m.Fire(DeskTrigger.WorkerReady);
    }

    [Fact]
    public void Happy_path_follows_the_architecture_diagram()
    {
        var m = NewMachine();
        var seen = new List<DeskState>();
        m.Transitioned += t => seen.Add(t.To);

        BringUp(m);
        m.Fire(DeskTrigger.ClientConnected);
        m.Fire(DeskTrigger.ClientDisconnected);
        m.Fire(DeskTrigger.Stop);
        m.Fire(DeskTrigger.Stopped);
        m.Fire(DeskTrigger.Start);

        Assert.Equal(
        [
            DeskState.Provisioning, DeskState.Connecting, DeskState.SessionReady, DeskState.DisplayReady, DeskState.Ready,
            DeskState.Streaming, DeskState.Ready, DeskState.Stopping, DeskState.Stopped, DeskState.Provisioning,
        ], seen);
    }

    [Theory]
    [InlineData(DeskState.Defined, DeskTrigger.Stop)]
    [InlineData(DeskState.Defined, DeskTrigger.WorkerReady)]
    [InlineData(DeskState.Ready, DeskTrigger.Start)]
    [InlineData(DeskState.Stopped, DeskTrigger.Stop)]
    [InlineData(DeskState.Stopping, DeskTrigger.Start)]
    [InlineData(DeskState.Faulted, DeskTrigger.Start)]
    [InlineData(DeskState.Connecting, DeskTrigger.WorkerReady)]
    [InlineData(DeskState.Stopping, DeskTrigger.Fail)]
    [InlineData(DeskState.Recovering, DeskTrigger.Fail)]
    public void Disallowed_triggers_throw_and_keep_state(DeskState state, DeskTrigger trigger)
    {
        var m = NewMachine(state);
        Assert.False(m.CanFire(trigger));
        var ex = Assert.Throws<InvalidDeskTransitionException>(() => m.Fire(trigger));
        Assert.Equal(state, ex.State);
        Assert.Equal(state, m.State);
    }

    [Theory]
    [InlineData(DeskState.Provisioning)]
    [InlineData(DeskState.Connecting)]
    [InlineData(DeskState.SessionReady)]
    [InlineData(DeskState.DisplayReady)]
    [InlineData(DeskState.Ready)]
    [InlineData(DeskState.Streaming)]
    [InlineData(DeskState.Recovering)]
    [InlineData(DeskState.Faulted)]
    public void Stop_is_accepted_from_every_active_state(DeskState state)
    {
        var m = NewMachine(state);
        Assert.Equal(DeskState.Stopping, m.Fire(DeskTrigger.Stop).To);
    }

    [Theory]
    [InlineData(DeskState.Connecting)]
    [InlineData(DeskState.SessionReady)]
    [InlineData(DeskState.DisplayReady)]
    [InlineData(DeskState.Ready)]
    [InlineData(DeskState.Streaming)]
    public void Failure_leads_to_recovering_with_backoff(DeskState state)
    {
        var m = NewMachine(state);
        var t = m.Fire(DeskTrigger.Fail, "worker crashed");
        Assert.Equal(DeskState.Recovering, t.To);
        Assert.Equal("worker crashed", m.LastError);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(1), m.RetryAt);
    }

    [Fact]
    public void Retry_is_only_accepted_after_the_backoff()
    {
        var m = NewMachine(DeskState.Ready);
        m.Fire(DeskTrigger.Fail);
        Assert.False(m.CanFire(DeskTrigger.RetryDue));
        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(m.CanFire(DeskTrigger.RetryDue));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(DeskState.Connecting, m.Fire(DeskTrigger.RetryDue).To);
        Assert.Null(m.RetryAt);
    }

    [Fact]
    public void Backoff_doubles_up_to_60_seconds()
    {
        var policy = RecoveryPolicy.Default;
        var delays = Enumerable.Range(1, 9).Select(n => policy.BackoffFor(n).TotalSeconds).ToArray();
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60, 60], delays);
        Assert.Equal(TimeSpan.Zero, policy.BackoffFor(0));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.BackoffFor(10_000));
    }

    [Fact]
    public void Consecutive_failures_grow_the_backoff()
    {
        var m = NewMachine(DeskState.Connecting);
        var expected = new[] { 1, 2, 4, 8, 16 };
        foreach (var seconds in expected)
        {
            m.Fire(DeskTrigger.Fail);
            Assert.Equal(TimeSpan.FromSeconds(seconds), m.RetryAt - _time.GetUtcNow());
            _time.Advance(TimeSpan.FromSeconds(seconds));
            m.Fire(DeskTrigger.RetryDue);
        }

        Assert.Equal(5, m.ConsecutiveFailures);
    }

    [Fact]
    public void More_than_five_failures_in_ten_minutes_fault_the_desk()
    {
        var m = NewMachine(DeskState.Connecting);
        for (var i = 1; i <= 5; i++)
        {
            Assert.Equal(DeskState.Recovering, m.Fire(DeskTrigger.Fail, $"failure {i}").To);
            _time.Advance(m.RetryAt!.Value - _time.GetUtcNow());
            m.Fire(DeskTrigger.RetryDue);
        }

        var sixth = m.Fire(DeskTrigger.Fail, "failure 6");
        Assert.Equal(DeskState.Faulted, sixth.To);
        Assert.Null(m.RetryAt);
        Assert.Equal("failure 6", m.LastError);

        // Faulted only leaves through Stop.
        Assert.False(m.CanFire(DeskTrigger.Start));
        m.Fire(DeskTrigger.Stop);
        m.Fire(DeskTrigger.Stopped);
        m.Fire(DeskTrigger.Start);
        Assert.Equal(0, m.FailuresInWindow);
        Assert.Null(m.LastError);
    }

    [Fact]
    public void Slow_failures_in_a_row_fault_the_desk_even_outside_the_window()
    {
        // A logon that times out after three minutes never puts six failures into ten minutes.
        var m = NewMachine(DeskState.Connecting);
        for (var i = 1; i <= RecoveryPolicy.Default.MaxConsecutiveFailures; i++)
        {
            Assert.Equal(DeskState.Recovering, m.Fire(DeskTrigger.Fail, $"logon timed out {i}").To);
            _time.Advance(TimeSpan.FromMinutes(3));
            m.Fire(DeskTrigger.RetryDue);
            Assert.True(m.FailuresInWindow <= 4);
        }

        var last = m.Fire(DeskTrigger.Fail, "logon timed out again");
        Assert.Equal(DeskState.Faulted, last.To);
        Assert.Null(m.RetryAt);
    }

    [Fact]
    public void Reaching_ready_restarts_the_count_of_failures_in_a_row()
    {
        var m = NewMachine(DeskState.Connecting);
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < RecoveryPolicy.Default.MaxConsecutiveFailures; i++)
            {
                m.Fire(DeskTrigger.Fail);
                _time.Advance(TimeSpan.FromMinutes(3));
                m.Fire(DeskTrigger.RetryDue);
            }

            m.Fire(DeskTrigger.SessionReady);
            m.Fire(DeskTrigger.DisplayReady);
            m.Fire(DeskTrigger.WorkerReady);
            Assert.Equal(0, m.ConsecutiveFailures);
        }

        Assert.Equal(DeskState.Ready, m.State);
    }

    [Fact]
    public void Failures_older_than_the_window_do_not_count()
    {
        var m = NewMachine(DeskState.Ready);
        for (var i = 0; i < 5; i++)
        {
            m.Fire(DeskTrigger.Fail);
            _time.Advance(TimeSpan.FromMinutes(3));
            m.Fire(DeskTrigger.RetryDue);
            m.Fire(DeskTrigger.SessionReady);
            m.Fire(DeskTrigger.DisplayReady);
            m.Fire(DeskTrigger.WorkerReady);
        }

        // 15 minutes have passed; only the failures of the last 10 minutes remain.
        Assert.True(m.FailuresInWindow <= 4);
        Assert.Equal(DeskState.Recovering, m.Fire(DeskTrigger.Fail).To);
    }

    [Fact]
    public void Reaching_ready_resets_the_backoff_but_not_the_fault_window()
    {
        var m = NewMachine(DeskState.Ready);
        m.Fire(DeskTrigger.Fail);
        _time.Advance(TimeSpan.FromSeconds(1));
        m.Fire(DeskTrigger.RetryDue);
        m.Fire(DeskTrigger.SessionReady);
        m.Fire(DeskTrigger.DisplayReady);
        m.Fire(DeskTrigger.WorkerReady);

        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Null(m.LastError);
        Assert.Equal(1, m.FailuresInWindow);
        m.Fire(DeskTrigger.Fail);
        Assert.Equal(TimeSpan.FromSeconds(1), m.RetryAt - _time.GetUtcNow());
    }

    [Fact]
    public void Provisioning_failure_faults_immediately()
    {
        var m = NewMachine();
        m.Fire(DeskTrigger.Start);
        Assert.Equal(DeskState.Faulted, m.Fire(DeskTrigger.Fail, "cannot create account").To);
        Assert.Equal("cannot create account", m.LastError);
    }

    [Fact]
    public void Stop_while_recovering_cancels_the_retry()
    {
        var m = NewMachine(DeskState.Ready);
        m.Fire(DeskTrigger.Fail);
        m.Fire(DeskTrigger.Stop);
        Assert.Null(m.RetryAt);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(m.CanFire(DeskTrigger.RetryDue));
    }

    [Fact]
    public void Transition_records_time_and_reason()
    {
        var m = NewMachine(DeskState.Ready);
        _time.Advance(TimeSpan.FromSeconds(5));
        var t = m.Fire(DeskTrigger.Fail, "hang");
        Assert.Equal(DeskState.Ready, t.From);
        Assert.Equal(DeskTrigger.Fail, t.Trigger);
        Assert.Equal(_time.GetUtcNow(), t.At);
        Assert.Equal("hang", t.Reason);
        Assert.Equal(_time.GetUtcNow(), m.EnteredAt);
    }

    [Fact]
    public void Custom_policy_is_honoured()
    {
        var policy = new RecoveryPolicy { InitialBackoff = TimeSpan.FromMilliseconds(10), MaxBackoff = TimeSpan.FromMilliseconds(25), MaxFailuresInWindow = 1 };
        var m = new DeskStateMachine(_time, policy, DeskState.Ready);
        m.Fire(DeskTrigger.Fail);
        Assert.Equal(TimeSpan.FromMilliseconds(10), m.RetryAt - _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMilliseconds(10));
        m.Fire(DeskTrigger.RetryDue);
        Assert.Equal(DeskState.Faulted, m.Fire(DeskTrigger.Fail).To);
        Assert.Equal(TimeSpan.FromMilliseconds(25), policy.BackoffFor(3));
    }

    [Theory]
    [InlineData(DeskState.SessionReady)]
    [InlineData(DeskState.DisplayReady)]
    [InlineData(DeskState.Ready)]
    [InlineData(DeskState.Streaming)]
    public void Pause_for_sleep_is_not_a_failure_and_waits_for_resume(DeskState state)
    {
        var m = NewMachine(state);
        Assert.Equal(DeskState.Recovering, m.Fire(DeskTrigger.Pause, "the PC is going to sleep").To);
        Assert.True(m.Paused);
        Assert.Null(m.RetryAt);
        Assert.Null(m.LastError);
        Assert.Equal(0, m.FailuresInWindow);
        Assert.Equal(0, m.ConsecutiveFailures);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.False(m.CanFire(DeskTrigger.RetryDue));
        Assert.Equal(DeskState.Connecting, m.Fire(DeskTrigger.Resume).To);
        Assert.False(m.Paused);
        Assert.False(m.CanFire(DeskTrigger.Resume));
    }

    [Fact]
    public void Resume_only_leaves_a_paused_desk_and_stop_ends_the_pause()
    {
        var m = NewMachine(DeskState.Ready);
        m.Fire(DeskTrigger.Fail);
        Assert.False(m.CanFire(DeskTrigger.Resume));
        Assert.False(NewMachine(DeskState.Stopped).CanFire(DeskTrigger.Pause));
        Assert.False(NewMachine(DeskState.Provisioning).CanFire(DeskTrigger.Pause));

        var paused = NewMachine(DeskState.Ready);
        paused.Fire(DeskTrigger.Pause);
        Assert.Equal(DeskState.Stopping, paused.Fire(DeskTrigger.Stop).To);
        Assert.False(paused.Paused);
    }
}
