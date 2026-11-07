// SPDX-License-Identifier: MIT
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Api;
using SplitPlay.Core.Host;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Tests;

/// <summary>TermWrap after a Windows update: one line in the UI and the tray, and a clear reason on the desk.</summary>
public sealed class TermWrapTests
{
    private static async Task<IReadOnlyList<HostWarningDto>> MetricsWarningsAsync(TestService svc) =>
        (await svc.Client.GetFromJsonAsync("/api/v1/metrics", SplitPlayJsonContext.Default.MetricsDto))!.Host.Warnings;

    private static async Task<List<HostWarningDto>> HostWarningsAsync(TestService svc) =>
        (await svc.Client.GetFromJsonAsync("/api/v1/host/warnings", SplitPlayJsonContext.Default.ListHostWarningDto))!;

    /// <summary>The service with its first TermWrap check done (so that it cannot overwrite what a test sets).</summary>
    private static async Task<(TestService Svc, TermWrapMonitor Monitor)> StartAsync()
    {
        var svc = await TestService.StartAsync();
        var monitor = svc.App.Services.GetRequiredService<TermWrapMonitor>();
        for (var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10); monitor.Current is null && DateTime.UtcNow < deadline;)
        {
            await Task.Delay(20);
        }

        Assert.NotNull(monitor.Current);
        return (svc, monitor);
    }

    [Fact]
    public async Task The_service_checks_TermWrap_when_it_starts()
    {
        var (svc, _) = await StartAsync();
        await using var owned = svc;
        Assert.True(svc.Platform.TermWrapChecks > 0);
        Assert.Empty(await HostWarningsAsync(svc));
        Assert.Empty(await MetricsWarningsAsync(svc));
    }

    [Fact]
    public async Task A_repaired_ServiceDll_shows_one_reboot_line_and_desks_say_why_they_cannot_start()
    {
        var (svc, monitor) = await StartAsync();
        await using var owned = svc;
        svc.Platform.TermWrap = new TermWrapHealth(TermWrapState.RebootRequired, Repaired: true, TermsrvVersion: "10.0.26100.4202");
        await monitor.CheckAsync();

        var warning = Assert.Single(await HostWarningsAsync(svc));
        Assert.Equal(HostWarningCodes.TermWrapReboot, warning.Code);
        Assert.True(warning.Repaired);
        Assert.Equal(HostWarningCodes.TermWrapReboot, (await MetricsWarningsAsync(svc))[0].Code);

        // The wire format the tray reads.
        var json = await svc.Client.GetStringAsync("/api/v1/host/warnings");
        Assert.Equal(new TrayWarning(HostWarningCodes.TermWrapReboot, Repaired: true), Assert.Single(TrayWarnings.Parse(json)));

        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted);
        Assert.Contains("Windows Update reset TermWrap", faulted.LastError, StringComparison.Ordinal);
        Assert.Equal(0, svc.Launcher.LaunchesOf(IpcProtocol.Roles.Seat));
    }

    [Fact]
    public async Task An_unsupported_termsrv_names_its_version_and_the_line_goes_once_fixed()
    {
        var (svc, monitor) = await StartAsync();
        await using var owned = svc;
        svc.Platform.TermWrap = new TermWrapHealth(TermWrapState.Unsupported, TermsrvVersion: "10.0.26100.7000", Detail: "DefPolicyPatch not found");
        await monitor.CheckAsync();

        var warning = Assert.Single(await HostWarningsAsync(svc));
        Assert.Equal(HostWarningCodes.TermWrapUnsupported, warning.Code);
        Assert.Equal("10.0.26100.7000", warning.Version);

        svc.Platform.TermWrap = new TermWrapHealth(TermWrapState.Ok, TermsrvVersion: "10.0.26100.7000");
        await monitor.CheckAsync();
        Assert.Empty(await HostWarningsAsync(svc));

        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
    }

    [Theory]
    [InlineData(TermWrapState.Ok, null)]
    [InlineData(TermWrapState.NotManaged, null)]
    [InlineData(TermWrapState.RebootRequired, HostWarningCodes.TermWrapReboot)]
    [InlineData(TermWrapState.Unsupported, HostWarningCodes.TermWrapUnsupported)]
    [InlineData(TermWrapState.Missing, HostWarningCodes.TermWrapMissing)]
    [InlineData(TermWrapState.RepairFailed, HostWarningCodes.TermWrapRepairFailed)]
    public void Every_problem_has_a_warning_and_a_desk_reason(TermWrapState state, string? code)
    {
        var health = new TermWrapHealth(state, Detail: "access denied");
        Assert.Equal(code, TermWrapMonitor.ToWarning(health)?.Code);
        Assert.Equal(code is null, health.Problem is null);
    }
}
