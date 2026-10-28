// SPDX-License-Identifier: MIT
using System.Net;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Tests;

/// <summary>The Stage 3 desk lifecycle: provisioning, seat, worker in the seat's session, recovery and stop order.</summary>
public sealed class SeatLifecycleTests
{
    private const string Seat = IpcProtocol.Roles.Seat;
    private const string Worker = IpcProtocol.Roles.Worker;

    private static Func<ChildLaunchRequest, FakeChildBehavior> SeatOnly(FakeChildBehavior behavior, int attempt = 1) =>
        r => r.Role == Seat && r.Attempt == attempt ? behavior : FakeChildBehavior.Normal;

    [Fact]
    public async Task Desk_runs_seat_then_worker_in_the_seat_session_and_stops_worker_first()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1", ""","user":"splitplay desk 1","profile":{"width":2560,"height":1440},"workerSettings":{"min_log_level":"warning","encoder":"nvenc"}""");
        await svc.PostDeskActionAsync("desk-1", "start");
        var ready = await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Equal(7, ready.SessionId);
        Assert.Null(ready.LastError);

        // The seat got the RDP target with the provisioned credentials.
        var welcome = Assert.IsType<WelcomeMessage>(svc.Launcher.Received.First(m => m.Role == Seat).Message);
        var rdp = welcome.Rdp!;
        Assert.Equal("127.0.0.2", rdp.Host);
        Assert.Equal(3389, rdp.Port);
        Assert.Equal("splitplay desk 1", rdp.User);
        Assert.Equal(".", rdp.Domain);
        Assert.Equal(svc.Platform.Accounts["splitplay desk 1"], rdp.Password);
        Assert.Equal(32, rdp.Password.Length);
        Assert.Equal((2560, 1440), (rdp.Width, rdp.Height));
        Assert.DoesNotContain(rdp.Password, rdp.ToString(), StringComparison.Ordinal);

        // The worker runs in the seat's session with the documented command line.
        var worker = svc.Launcher.Requests.Single(r => r.Role == Worker);
        Assert.Equal(7, worker.SessionId);
        var deskDir = Path.Combine(svc.DataRoot, "desks", "desk-1");
        Assert.Equal(
            ["--desk-dir", deskDir, "--pipe", worker.PipeName, "--desk", "desk-1", "port=48089", "min_log_level=warning", "encoder=nvenc"],
            worker.Arguments);
        Assert.EndsWith("splitplay-worker.exe", worker.Executable, StringComparison.Ordinal);
        Assert.Null(svc.Launcher.Requests.Single(r => r.Role == Seat).SessionId);
        Assert.Null((await svc.GetDeskAsync("desk-1")).LastError);

        // Provisioning, firewall, first logon, Bluetooth janitor.
        await svc.WaitForAsync("desk-1", _ => svc.Platform.Actions.Contains("bluetooth 7"), what: "a Bluetooth check");
        var actions = svc.Platform.Actions;
        Assert.Contains("provision desk-1", actions);
        Assert.Contains("firewall+ desk-1 48089", actions);
        Assert.Contains("logon desk-1 7", actions);
        Assert.Contains("layouts desk-1", actions);
        Assert.True(File.Exists(Path.Combine(svc.DataRoot, "secrets", "desks", "desk-1.bin")));

        await svc.PostDeskActionAsync("desk-1", "stop");
        var stopped = await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.Null(stopped.SessionId);

        var received = svc.Launcher.Received.ToList();
        var workerShutdown = received.FindIndex(m => m.Role == Worker && m.Message is ShutdownMessage);
        var seatShutdown = received.FindIndex(m => m.Role == Seat && m.Message is ShutdownMessage);
        Assert.True(workerShutdown >= 0 && seatShutdown > workerShutdown, "the worker must be shut down before the seat");
    }

    [Fact]
    public async Task Worker_crash_relaunches_only_the_worker()
    {
        await using var svc = await TestService.StartAsync(setup: l =>
            l.Behavior = r => r.Role == Worker && r.Attempt == 1 ? FakeChildBehavior.ExitAfterReady : FakeChildBehavior.Normal);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForAsync("desk-1", d => d.State == DeskState.Ready && svc.Launcher.LaunchesOf(Worker) == 2, what: "a relaunched worker");
        Assert.Equal(1, svc.Launcher.LaunchesOf(Seat));
        Assert.All(svc.Launcher.Requests.Where(r => r.Role == Worker), r => Assert.Equal(7, r.SessionId));
    }

    [Fact]
    public async Task Seat_giving_up_kills_the_worker_and_reconnects()
    {
        await using var svc = await TestService.StartAsync(setup: l => l.Behavior = SeatOnly(FakeChildBehavior.GiveUpAfterReady));
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForAsync("desk-1", d => d.State == DeskState.Ready && svc.Launcher.LaunchesOf(Seat) == 2, TimeSpan.FromSeconds(20),
            "a reconnected seat");
        Assert.Equal(2, svc.Launcher.LaunchesOf(Worker));

        var logs = await svc.Client.GetStringAsync("/api/v1/logs?tail=500");
        Assert.Contains("gave up reconnecting", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moonlight_mode_request_is_forwarded_to_the_seat()
    {
        await using var svc = await TestService.StartAsync(setup: l =>
            l.Customize = (r, o) => r.Role == Worker ? o with { RequestMode = (1280, 720, 120) } : o);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        await svc.WaitForAsync("desk-1", _ => svc.Launcher.Received.Any(m => m.Role == Seat && m.Message is SetModeMessage), what: "set_mode");
        var setMode = (SetModeMessage)svc.Launcher.Received.First(m => m.Role == Seat && m.Message is SetModeMessage).Message;
        Assert.Equal((1280, 720), (setMode.Width, setMode.Height));
    }

    [Fact]
    public async Task Reconnect_into_another_session_restarts_the_worker_there()
    {
        await using var svc = await TestService.StartAsync(setup: l =>
            l.Customize = (r, o) => r.Role == Seat ? o with { RestoreIntoSession = 9 } : o);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var desk = await svc.WaitForAsync("desk-1", d => d.State == DeskState.Ready && d.SessionId == 9, what: "the worker in session 9");
        Assert.Equal(1, svc.Launcher.LaunchesOf(Seat));
        Assert.Equal(9, svc.Launcher.Requests.Last(r => r.Role == Worker).SessionId);
        Assert.Equal(9, desk.SessionId);
    }

    [Fact]
    public async Task Reconnect_into_the_same_session_keeps_the_worker()
    {
        await using var svc = await TestService.StartAsync(setup: l =>
            l.Customize = (r, o) => r.Role == Seat ? o with { RestoreIntoSession = 7 } : o);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var logs = await WaitForLogAsync(svc, "RDP session restored");
        Assert.Contains("RDP session lost", logs, StringComparison.Ordinal);
        Assert.Equal(1, svc.Launcher.LaunchesOf(Worker));
        Assert.Equal(DeskState.Ready, (await svc.GetDeskAsync("desk-1")).State);
    }

    [Fact]
    public async Task Host_not_prepared_faults_the_desk_with_a_clear_error()
    {
        await using var svc = await TestService.StartAsync();
        svc.Platform.ProvisionError = "RDP host not prepared: reboot required";
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted);
        Assert.Equal("RDP host not prepared: reboot required", faulted.LastError);
        Assert.Equal(0, svc.Launcher.Launches);

        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
    }

    [Fact]
    public async Task Seat_without_a_session_is_a_failure()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:MaxFailuresInWindow=0"], setup: l => l.SeatSession = _ => null);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted);
        Assert.Contains("no session", faulted.LastError, StringComparison.Ordinal);
        Assert.Equal(0, svc.Launcher.LaunchesOf(Worker));
    }

    [Fact]
    public async Task Missing_secret_rotates_the_password()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        var first = svc.Platform.Accounts["splitplay-desk1"];

        // Same secret: same password on the next start.
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.Equal(first, svc.Platform.Accounts["splitplay-desk1"]);

        File.Delete(Path.Combine(svc.DataRoot, "secrets", "desks", "desk-1.bin"));
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var second = svc.Platform.Accounts["splitplay-desk1"];
        Assert.NotEqual(first, second);
        var lastWelcome = svc.Launcher.Received.Last(m => m.Role == Seat && m.Message is WelcomeMessage).Message;
        Assert.Equal(second, ((WelcomeMessage)lastWelcome).Rdp!.Password);
    }

    [Fact]
    public async Task Delete_with_deleteUser_removes_the_account_secret_and_firewall_rules()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.True(svc.Platform.Accounts.ContainsKey("splitplay-desk1"));

        var response = await svc.Client.DeleteAsync("/api/v1/desks/desk-1?deleteUser=true");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(svc.Platform.Accounts.ContainsKey("splitplay-desk1"));
        Assert.False(File.Exists(Path.Combine(svc.DataRoot, "secrets", "desks", "desk-1.bin")));
        Assert.Contains("userdel splitplay-desk1", svc.Platform.Actions);
        Assert.Contains("firewall- desk-1", svc.Platform.Actions);
    }

    [Fact]
    public async Task Delete_without_deleteUser_keeps_the_account()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);

        Assert.Equal(HttpStatusCode.NoContent, (await svc.Client.DeleteAsync("/api/v1/desks/desk-1")).StatusCode);
        Assert.True(svc.Platform.Accounts.ContainsKey("splitplay-desk1"));
        Assert.False(File.Exists(Path.Combine(svc.DataRoot, "secrets", "desks", "desk-1.bin")));
    }

    [Fact]
    public async Task First_logon_with_copied_layouts_logs_the_session_off_once()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:RelogonAfterLayoutCopy=true"]);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Equal(2, svc.Launcher.LaunchesOf(Seat));

        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Equal(3, svc.Launcher.LaunchesOf(Seat)); // layouts already copied: no extra logoff
    }

    [Fact]
    public async Task Bluetooth_janitor_can_be_turned_off_per_desk()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1", ""","disableBluetoothUserService":false""");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        await Task.Delay(400);
        Assert.DoesNotContain(svc.Platform.Actions, a => a.StartsWith("bluetooth", StringComparison.Ordinal));
        Assert.False((await svc.GetDeskAsync("desk-1")).DisableBluetoothUserService);
    }

    [Fact]
    public async Task Worker_settings_cannot_override_the_port()
    {
        await using var svc = await TestService.StartAsync();
        var response = await svc.Client.PostAsync("/api/v1/desks",
            new StringContent("""{"id":"desk-1","workerSettings":{"port":"1234"}}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Stub_worker_gets_the_short_command_line()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:Worker=stub"]);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var worker = svc.Launcher.Requests.Single(r => r.Role == Worker);
        Assert.Equal(["--pipe", worker.PipeName, "--desk", "desk-1"], worker.Arguments);
        Assert.EndsWith("splitplay-stubworker.exe", worker.Executable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_seat_the_worker_can_be_placed_in_the_console_session()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:Seat=none", "--Supervisor:WorkerSession=console"]);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var ready = await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Equal(1, ready.SessionId);
        Assert.Equal(0, svc.Launcher.LaunchesOf(Seat));
        Assert.Equal(1, svc.Launcher.Requests.Single().SessionId);
    }

    private static async Task<string> WaitForLogAsync(TestService svc, string text)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var logs = await svc.Client.GetStringAsync("/api/v1/logs?tail=500");
            if (logs.Contains(text, StringComparison.Ordinal))
            {
                return logs;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"log line '{text}' did not appear");
            }

            await Task.Delay(50);
        }
    }
}
