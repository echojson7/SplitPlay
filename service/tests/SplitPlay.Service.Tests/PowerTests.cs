// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Tests;

/// <summary>Sleep and resume, and keeping the PC awake while streaming.</summary>
public sealed class PowerTests
{
    private const string Worker = IpcProtocol.Roles.Worker;
    private const string Seat = IpcProtocol.Roles.Seat;

    [Fact]
    public async Task Sleep_stops_the_worker_cleanly_and_resume_starts_a_new_one_without_a_failure()
    {
        await using var svc = await TestService.StartAsync();
        var power = svc.App.Services.GetRequiredService<PowerEvents>();
        await svc.CreateDeskAsync("desk-1");
        await svc.CreateDeskAsync("desk-2");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        power.Raise(PowerChange.Suspend);
        var asleep = await svc.WaitForStateAsync("desk-1", DeskState.Recovering);
        Assert.Null(asleep.LastError);
        Assert.Contains(svc.Launcher.Received, m => m.Role == Worker && m.Message is ShutdownMessage);
        Assert.DoesNotContain(svc.Launcher.Received, m => m.Role == Seat && m.Message is ShutdownMessage);

        // Nothing restarts while the PC sleeps, and a desk started now waits for resume.
        await svc.PostDeskActionAsync("desk-2", "start");
        await Task.Delay(300);
        Assert.Equal(DeskState.Recovering, (await svc.GetDeskAsync("desk-1")).State);
        Assert.Equal(1, svc.Launcher.Requests.Count(r => r.Role == Worker && r.DeskId == "desk-1"));
        Assert.Equal(0, svc.Launcher.Requests.Count(r => r.Role == Worker && r.DeskId == "desk-2"));

        power.Raise(PowerChange.Resume);
        var awake = await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Null(awake.LastError);
        await svc.WaitForStateAsync("desk-2", DeskState.Ready);
        Assert.Equal(2, svc.Launcher.Requests.Count(r => r.Role == Worker && r.DeskId == "desk-1"));
        // The seat and its session survived sleep.
        Assert.Equal(1, svc.Launcher.Requests.Count(r => r.Role == Seat && r.DeskId == "desk-1"));
        Assert.Equal(7, awake.SessionId);

        // Repeated notifications are harmless, and stop works while paused.
        power.Raise(PowerChange.Resume);
        power.Raise(PowerChange.Suspend);
        power.Raise(PowerChange.Suspend);
        await svc.WaitForStateAsync("desk-1", DeskState.Recovering);
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        power.Raise(PowerChange.Resume);
        await svc.WaitForStateAsync("desk-2", DeskState.Ready);
        Assert.Equal(DeskState.Stopped, (await svc.GetDeskAsync("desk-1")).State);
    }

    [Fact]
    public async Task Sleep_cycles_never_fault_a_desk()
    {
        await using var svc = await TestService.StartAsync();
        var power = svc.App.Services.GetRequiredService<PowerEvents>();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        // MaxFailuresInWindow is 3 in tests: more sleep cycles than that.
        for (var i = 0; i < 5; i++)
        {
            power.Raise(PowerChange.Suspend);
            await svc.WaitForStateAsync("desk-1", DeskState.Recovering);
            power.Raise(PowerChange.Resume);
            await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        }

        Assert.Null((await svc.GetDeskAsync("desk-1")).LastError);
    }

    [Fact]
    public async Task The_PC_stays_awake_while_a_client_streams_unless_turned_off()
    {
        await using var svc = await TestService.StartAsync(setup: l => l.SimulateClient = true);
        var request = svc.App.Services.GetRequiredService<FakePowerRequest>();
        Assert.False(request.Held);
        Assert.True(svc.ReadConfigFile().Host.KeepAwake);

        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Streaming);
        await svc.WaitForAsync("desk-1", _ => request.Held, what: "a held power request");
        Assert.Contains("desk-1", request.Reason, StringComparison.Ordinal);

        // host.keepAwake=false lets Windows sleep at once; back on, the request returns.
        var host = (await svc.Client.GetFromJsonAsync("/api/v1/host", SplitPlayJsonContext.Default.HostConfig))!;
        host.KeepAwake = false;
        var put = await svc.Client.PutAsJsonAsync("/api/v1/host", host, SplitPlayJsonContext.Default.HostConfig);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False(request.Held);
        Assert.False(svc.ReadConfigFile().Host.KeepAwake);
        host.KeepAwake = true;
        await svc.Client.PutAsJsonAsync("/api/v1/host", host, SplitPlayJsonContext.Default.HostConfig);
        Assert.True(request.Held);

        // The stream ends with the desk.
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        await svc.WaitForAsync("desk-1", _ => !request.Held, what: "the power request released");
    }

    [Fact]
    public async Task Host_settings_without_keepAwake_keep_it_on()
    {
        await using var svc = await TestService.StartAsync();
        var put = await svc.Client.PutAsync("/api/v1/host", new StringContent(
            """{"webPort":38299,"lanAccess":false,"basePortStart":48089,"basePortStride":100,"composeIntervalMs":4,"logLevel":"info"}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True(svc.ReadConfigFile().Host.KeepAwake);
    }
}
