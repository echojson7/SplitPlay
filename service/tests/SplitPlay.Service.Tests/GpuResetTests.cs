// SPDX-License-Identifier: MIT
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SplitPlay.Core.Api;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Tests;

/// <summary>Graphics driver resets restart the workers (sessions stay); GPU overload shows as a warning.</summary>
public sealed class GpuResetTests
{
    private const string Worker = IpcProtocol.Roles.Worker;
    private const string Seat = IpcProtocol.Roles.Seat;

    private static async Task<MetricsDto> MetricsAsync(TestService svc) =>
        (await svc.Client.GetFromJsonAsync("/api/v1/metrics", SplitPlayJsonContext.Default.MetricsDto))!;

    [Fact]
    public async Task A_reset_logged_by_Windows_restarts_every_worker_and_keeps_the_sessions()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:GpuResetDelayMs=100"]);
        await svc.CreateDeskAsync("desk-1");
        await svc.CreateDeskAsync("desk-2");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.PostDeskActionAsync("desk-2", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        await svc.WaitForStateAsync("desk-2", DeskState.Ready);
        Assert.Equal(2, svc.Launcher.LaunchesOf(Worker));

        svc.App.Services.GetRequiredService<FakeGpuResetSource>().Raise();
        // The same reset reported again (by a worker) is not a second restart.
        svc.App.Services.GetRequiredService<FakeGpuResetSource>().Raise(new GpuResetSignal("desk-1", "reason 0x887A0007"));

        await svc.WaitForAsync("desk-1", d => d.State == DeskState.Ready && svc.Launcher.LaunchesOf(Worker) == 4, TimeSpan.FromSeconds(20),
            "both workers restarted");
        await svc.WaitForStateAsync("desk-2", DeskState.Ready);
        await Task.Delay(300);
        Assert.Equal(4, svc.Launcher.LaunchesOf(Worker));
        Assert.Equal(2, svc.Launcher.LaunchesOf(Seat));

        var warning = Assert.Single((await MetricsAsync(svc)).Host.Warnings);
        Assert.Equal(HostWarningCodes.GpuReset, warning.Code);
        Assert.Equal(2, warning.Desks);
        Assert.NotNull(warning.At);

        var logs = await svc.Client.GetStringAsync("/api/v1/logs?tail=500");
        Assert.Contains("The graphics driver was reset", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worker_that_loses_its_GPU_restarts_without_using_up_the_failure_budget()
    {
        // MaxFailuresInWindow is 3 in tests: three crashes would fault the desk, three resets must not.
        await using var svc = await TestService.StartAsync(["--Supervisor:GpuResetDelayMs=50"],
            setup: l => l.Behavior = r => r.Role == Worker && r.Attempt <= 3 ? FakeChildBehavior.GpuResetAfterReady : FakeChildBehavior.Normal);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForAsync("desk-1", d => d.State == DeskState.Ready && svc.Launcher.LaunchesOf(Worker) == 4,
            TimeSpan.FromSeconds(30), "the worker restarted after three resets");
        Assert.Equal(1, svc.Launcher.LaunchesOf(Seat));

        var logs = "";
        for (var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10); DateTime.UtcNow < deadline; await Task.Delay(100))
        {
            logs = await svc.Client.GetStringAsync("/api/v1/logs?tail=500");
            if (logs.Contains("reason 0x887A0007", StringComparison.Ordinal))
            {
                break;
            }
        }

        Assert.Contains("the worker of desk desk-1", logs, StringComparison.Ordinal);
        Assert.Contains("reason 0x887A0007", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_crash_just_before_Windows_reports_the_reset_is_taken_back()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:GpuResetDelayMs=100", "--Supervisor:InitialBackoffMs=30000", "--Supervisor:MaxBackoffMs=30000"],
            setup: l => l.Behavior = r => r.Role == Worker && r.Attempt == 1 ? FakeChildBehavior.ExitAfterReady : FakeChildBehavior.Normal);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var recovering = await svc.WaitForStateAsync("desk-1", DeskState.Recovering);
        Assert.Contains("exited with code 3", recovering.LastError, StringComparison.Ordinal);

        // Windows logs the reset after the worker died: the desk retries after the short reset delay, not the 3 s backoff.
        svc.App.Services.GetRequiredService<FakeGpuResetSource>().Raise();
        var started = DateTime.UtcNow;
        await svc.WaitForAsync("desk-1", d => d.State == DeskState.Ready && svc.Launcher.LaunchesOf(Worker) == 2, TimeSpan.FromSeconds(20),
            "the worker restarted");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the retry waited out the failure backoff");
    }

    [Fact]
    public void Encoder_and_VRAM_warnings_need_a_sustained_load_and_clear_below_a_lower_line()
    {
        var time = new FakeTimeProvider();
        var warnings = new GpuLoadWarnings(time, NullLogger.Instance);
        const string Luid = "0x0000000000012345";
        GpuDto Gpu(double encoder, double vramUsed) => new(Luid, "RTX", 50, encoder, vramUsed, 1000);

        Assert.Empty(warnings.Update([Gpu(95, 960)]));
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(warnings.Update([Gpu(95, 960)]));
        time.Advance(TimeSpan.FromSeconds(2));
        var on = warnings.Update([Gpu(96, 970)]);
        Assert.Equal([HostWarningCodes.EncoderBusy, HostWarningCodes.VramFull], on.Select(w => w.Code));
        Assert.Equal(96, on[0].Percent);
        Assert.Equal(97, on[1].Percent);
        Assert.Equal("RTX", on[0].Gpu);

        // Between the lines the warnings stay; below the lower line they go.
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, warnings.Update([Gpu(85, 920)]).Count);
        Assert.Empty(warnings.Update([Gpu(70, 800)]));

        // A short peak is not a warning, and neither is an adapter without numbers.
        Assert.Empty(warnings.Update([Gpu(99, 990)]));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(warnings.Update([Gpu(50, 500)]));
        Assert.Empty(warnings.Update([new GpuDto(Luid, "RTX", null, null, null, 1000)]));
    }
}
