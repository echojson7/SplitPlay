// SPDX-License-Identifier: MIT
using System.Net;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Tests;

/// <summary>Supervisor behaviour: handshake, heartbeat timeout, backoff, Faulted, shutdown, real child processes.</summary>
public sealed class LifecycleTests
{
    [Fact]
    public async Task Hung_child_is_detected_and_restarted()
    {
        await using var svc = await TestService.StartAsync(setup: l =>
            l.Behavior = r => r.Attempt == 1 ? FakeChildBehavior.StopHeartbeats : FakeChildBehavior.Normal);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");

        await svc.WaitForAsync("desk-1", _ => svc.Launcher.Launches >= 2, what: "a relaunch");
        var ready = await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Null(ready.LastError);

        var logs = await svc.Client.GetStringAsync("/api/v1/logs?desk=desk-1&tail=200");
        Assert.Contains("hung", logs, StringComparison.Ordinal);
        Assert.Contains("Recovering", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Crash_loop_ends_in_faulted_and_stop_recovers()
    {
        await using var svc = await TestService.StartAsync(setup: l => l.Behavior = _ => FakeChildBehavior.ExitAfterReady);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");

        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted, TimeSpan.FromSeconds(30));
        Assert.Contains("exited with code 3", faulted.LastError, StringComparison.Ordinal);
        Assert.Equal(4, svc.Launcher.Launches); // MaxFailuresInWindow = 3 in tests, so the 4th failure faults.

        Assert.Equal(HttpStatusCode.Conflict, (await svc.Client.PostAsync("/api/v1/desks/desk-1/start", null)).StatusCode);

        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);

        svc.Launcher.Behavior = _ => FakeChildBehavior.Normal;
        await svc.PostDeskActionAsync("desk-1", "start");
        var ready = await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Null(ready.LastError);
    }

    [Fact]
    public async Task Launch_failures_are_reported()
    {
        await using var svc = await TestService.StartAsync(setup: l => l.FailLaunch = _ => true);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted, TimeSpan.FromSeconds(30));
        Assert.Contains("launch failed", faulted.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_hello_times_out()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:HelloTimeoutMs=300", "--Supervisor:MaxFailuresInWindow=0"],
            setup: l => l.Behavior = _ => FakeChildBehavior.NoHello);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted);
        Assert.Contains("hello", faulted.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_ready_times_out()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:ReadyTimeoutMs=300", "--Supervisor:MaxFailuresInWindow=0"],
            setup: l => l.Behavior = _ => FakeChildBehavior.NoReady);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted);
        Assert.Contains("not ready", faulted.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Child_that_ignores_shutdown_is_killed()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:ShutdownTimeoutMs=300"],
            setup: l => l.Behavior = _ => FakeChildBehavior.IgnoreShutdown);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        var started = DateTime.UtcNow;
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task Client_events_move_the_desk_to_streaming()
    {
        await using var svc = await TestService.StartAsync(setup: l => l.SimulateClient = true);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Streaming);
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
    }

    [Fact]
    public async Task Autostart_desks_start_with_the_service()
    {
        var dataRoot = TestService.NewDataRoot();
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "config.json"),
            """{"schemaVersion":1,"desks":[{"id":"auto","autostart":true},{"id":"manual"}]}""");
        try
        {
            await using var svc = await TestService.StartAsync(dataRoot: dataRoot);
            await svc.WaitForStateAsync("auto", DeskState.Ready);
            Assert.Equal(DeskState.Defined, (await svc.GetDeskAsync("manual")).State);
        }
        finally
        {
            TestService.TryDelete(dataRoot);
        }
    }

    [Fact]
    public async Task Older_config_is_migrated_on_startup()
    {
        var dataRoot = TestService.NewDataRoot();
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, "config.json");
        await File.WriteAllTextAsync(path, """{"desktops":[{"id":"legacy"}]}""");
        try
        {
            await using var svc = await TestService.StartAsync(dataRoot: dataRoot);
            Assert.Equal(48089, (await svc.GetDeskAsync("legacy")).BasePort);
            Assert.True(File.Exists(path + ".v0.bak"));
            Assert.Contains("\"schemaVersion\": 1", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            TestService.TryDelete(dataRoot);
        }
    }

    [Fact]
    public void Newer_config_prevents_startup()
    {
        var dataRoot = TestService.NewDataRoot();
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "config.json"), """{"schemaVersion":99}""");
        try
        {
            var options = ServiceHostOptions.Parse(["--console", "--data-root", dataRoot]);
            var ex = Assert.Throws<ConfigException>(() => ServiceHost.Build(options));
            Assert.Equal(ConfigException.Codes.SchemaTooNew, ex.Code);
        }
        finally
        {
            TestService.TryDelete(dataRoot);
        }
    }

    [Fact]
    public async Task Real_child_process_speaks_the_protocol()
    {
        // The service executable doubles as a protocol-conformant child: SplitPlay.Service fake-child --pipe … --desk …
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SplitPlay.Service.exe" : "SplitPlay.Service");
        Assert.True(File.Exists(exe), $"apphost not found: {exe}");

        await using var svc = await TestService.StartAsync(
        [
            "--Supervisor:Launcher=process",
            $"--Supervisor:ChildExecutable={exe}",
            "--Supervisor:ChildArguments=fake-child --heartbeat-ms 100",
            "--Supervisor:HelloTimeoutMs=20000",
        ]);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var ready = await svc.WaitForStateAsync("desk-1", DeskState.Ready, TimeSpan.FromSeconds(30));
        Assert.Null(ready.LastError);

        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped, TimeSpan.FromSeconds(15));
        Assert.Equal(0, svc.Launcher.Launches);
    }

    [Fact]
    public async Task Missing_child_executable_faults_the_desk()
    {
        await using var svc = await TestService.StartAsync(
            ["--Supervisor:Launcher=process", "--Supervisor:ChildExecutable=does-not-exist.exe", "--Supervisor:MaxFailuresInWindow=0"]);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted);
        Assert.Contains("not found", faulted.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_child_rejects_unsupported_requests()
    {
        // Seat-only request sent to a worker-role child is answered with "unsupported" (ipc.md).
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var supervisor = (Supervision.DeskSupervisor)svc.App.Services.GetService(typeof(Supervision.DeskSupervisor))!;
        var result = await supervisor.RequestAsync("desk-1", id => new SendSasMessage { Id = id });
        Assert.False(result.Ok);
        Assert.Equal(IpcProtocol.ErrorCodes.Unsupported, result.Error!.Code);
    }
}
