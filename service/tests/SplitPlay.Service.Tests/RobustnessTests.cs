// SPDX-License-Identifier: MIT
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Tests;

/// <summary>Bounded retries and clean-up when Windows is slow, hangs or the service restarts.</summary>
public sealed class RobustnessTests
{
    private const string Seat = IpcProtocol.Roles.Seat;

    [Fact]
    public async Task Provisioning_that_hangs_faults_the_desk_and_is_not_called_twice()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:ProvisionTimeoutMs=300"],
            platform: p => p.ProvisionDelay = TimeSpan.FromSeconds(3));
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted);
        Assert.Contains("did not finish within", faulted.LastError, StringComparison.Ordinal);

        // The hung call still runs: a new start does not race it on the account's password.
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        await svc.PostDeskActionAsync("desk-1", "start");
        faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted);
        Assert.Contains("still not returned", faulted.LastError, StringComparison.Ordinal);
        Assert.Equal(1, svc.Platform.ProvisionCalls);
    }

    [Fact]
    public async Task Transient_provisioning_errors_are_retried_a_bounded_number_of_times()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:ProvisionRetryDelayMs=20"],
            platform: p => p.TransientProvisionErrors = 2);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Equal(2, svc.Platform.Actions.Count(a => a == "provision-error desk-1"));

        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        svc.Platform.TransientProvisionErrors = 5;
        await svc.PostDeskActionAsync("desk-1", "start");
        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted);
        Assert.Contains("provisioning failed 3 times", faulted.LastError, StringComparison.Ordinal);
        Assert.Equal(2, svc.Platform.TransientProvisionErrors);
    }

    [Fact]
    public async Task A_lost_session_that_never_comes_back_replaces_the_seat()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:SessionLostTimeoutMs=500"],
            setup: l => l.Behavior = r => r.Role == Seat && r.Attempt == 1 ? FakeChildBehavior.LoseSessionAfterReady : FakeChildBehavior.Normal);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForAsync("desk-1", d => d.State == DeskState.Ready && svc.Launcher.LaunchesOf(Seat) == 2, TimeSpan.FromSeconds(20),
            "a new seat");
        var logs = await svc.Client.GetStringAsync("/api/v1/logs?tail=500");
        Assert.Contains("did not come back within", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Slow_failures_in_a_row_end_in_Faulted_instead_of_retrying_forever()
    {
        await using var svc = await TestService.StartAsync(
            ["--Supervisor:MaxFailuresInWindow=100", "--Supervisor:MaxConsecutiveFailures=3", "--Supervisor:SeatReadyTimeoutMs=200"],
            setup: l => l.Behavior = r => r.Role == Seat ? FakeChildBehavior.NoReady : FakeChildBehavior.Normal);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted, TimeSpan.FromSeconds(30));
        Assert.Contains("not ready", faulted.LastError, StringComparison.Ordinal);
        Assert.Equal(4, svc.Launcher.LaunchesOf(Seat));
        var logs = await svc.Client.GetStringAsync("/api/v1/logs?tail=500");
        Assert.Contains("giving up after 4 failures", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leftover_sessions_are_reconnected_or_logged_off_and_never_for_foreign_accounts()
    {
        var dataRoot = TestService.NewDataRoot();
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "config.json"), """
            {"schemaVersion":1,"desks":[
              {"id":"auto","user":"sp-auto","autostart":true},
              {"id":"manual","user":"sp-manual"},
              {"id":"own","user":"owner2","userManaged":false}]}
            """);
        try
        {
            await using var svc = await TestService.StartAsync(dataRoot: dataRoot, platform: p =>
            {
                p.AddManagedAccount("sp-auto");
                p.AddManagedAccount("sp-manual");
                p.AddManagedAccount("sp-orphan");
                p.AddExistingAccount("owner2", "secret");
                p.AddSession(3, "sp-auto");
                p.AddSession(4, "sp-manual");
                p.AddSession(5, "sp-orphan");
                p.AddSession(6, "owner2");
                p.AddSession(8, "sp-manual", disconnected: false);
                p.AddSession(9, "someone");
            });
            await svc.App.Services.GetRequiredService<DeskSupervisor>().Startup;

            var logoffs = svc.Platform.Actions.Where(a => a.StartsWith("logoff ", StringComparison.Ordinal)).Order().ToList();
            Assert.Equal(["logoff 4", "logoff 5"], logoffs);
            Assert.Equal([3, 6, 8, 9], svc.Platform.Sessions().Select(s => s.Id));
            await svc.WaitForStateAsync("auto", DeskState.Ready);

            var logs = await svc.Client.GetStringAsync("/api/v1/logs?tail=500");
            Assert.Contains("the desk reconnects to it", logs, StringComparison.Ordinal);
            Assert.Contains("SplitPlay did not create this account", logs, StringComparison.Ordinal);
        }
        finally
        {
            TestService.TryDelete(dataRoot);
        }
    }

    [Fact]
    public async Task A_hung_session_query_at_start_up_does_not_keep_desks_from_starting()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:StartupWaitMs=300"],
            platform: p => p.SessionsDelay = TimeSpan.FromSeconds(3));
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var logs = await svc.Client.GetStringAsync("/api/v1/logs?tail=500");
        Assert.Contains("did not finish within", logs, StringComparison.Ordinal);
    }
}
