// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SplitPlay.Core.Api;
using SplitPlay.Core.Json;
using SplitPlay.Service.Api;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Tests;

/// <summary><c>/host/services</c>: the related Windows services, the allow-list, and the start-up check.</summary>
public sealed class HostServicesTests
{
    private static async Task<List<HostServiceDto>> ListAsync(TestService svc) =>
        (await svc.Client.GetFromJsonAsync("/api/v1/host/services", SplitPlayJsonContext.Default.ListHostServiceDto))!;

    private static async Task<ApiError> ReadErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.ApiErrorResponse))!.Error;

    private static FakeServiceControl Fake(TestService svc) => svc.App.Services.GetRequiredService<FakeServiceControl>();

    private static async Task WaitForActionAsync(FakeServiceControl fake, string action)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!fake.Actions.Contains(action))
        {
            Assert.True(DateTime.UtcNow < deadline, $"'{action}' was not done; actions: {string.Join(", ", fake.Actions)}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task List_has_the_related_services_in_order_and_hides_steam_when_missing()
    {
        await using var svc = await TestService.StartAsync();
        var raw = await svc.Client.GetStringAsync("/api/v1/host/services");
        Assert.StartsWith("""[{"name":"SplitPlay","displayName":"SplitPlay Host Service","status":"running","startType":"automatic","canStart":false,"canStop":true}""",
            raw, StringComparison.Ordinal);

        var list = await ListAsync(svc);
        Assert.Equal(new[] { "SplitPlay", "TermService", "UmRdpService", "ViGEmBus", "Audiosrv" }, list.Select(s => s.Name));
        var vigem = list.Single(s => s.Name == "ViGEmBus");
        Assert.Equal(HostServiceStatus.Running, vigem.Status);
        Assert.False(vigem.CanStop);
        Assert.False(vigem.CanStart);

        Fake(svc).Set(new ServiceInfo("Steam Client Service", "Steam Client Service", HostServiceStatus.Stopped, "manual", false, false));
        list = await ListAsync(svc);
        var steam = Assert.Single(list, s => s.Name == "Steam Client Service");
        Assert.True(steam.CanStart);
        Assert.False(steam.CanStop);
    }

    [Fact]
    public async Task Missing_and_disabled_services_cannot_be_started()
    {
        await using var svc = await TestService.StartAsync();
        var fake = Fake(svc);
        fake.Remove("ViGEmBus");
        fake.Set(new ServiceInfo("Audiosrv", "Windows Audio", HostServiceStatus.Stopped, "disabled", false, false));

        var list = await ListAsync(svc);
        var vigem = list.Single(s => s.Name == "ViGEmBus");
        Assert.Equal(HostServiceStatus.Missing, vigem.Status);
        Assert.Null(vigem.StartType);
        Assert.Equal("Virtual Gamepad Emulation Bus", vigem.DisplayName);
        Assert.False(vigem.CanStart || vigem.CanStop);
        Assert.False(list.Single(s => s.Name == "Audiosrv").CanStart);

        foreach (var name in new[] { "ViGEmBus", "Audiosrv" })
        {
            var response = await svc.Client.PostAsync($"/api/v1/host/services/{name}/start", null);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(ApiErrorCodes.InvalidState, (await ReadErrorAsync(response)).Code);
        }

        Assert.Empty(fake.Actions);
    }

    [Theory]
    [InlineData("start", "Spooler")]
    [InlineData("stop", "Spooler")]
    [InlineData("stop", "wuauserv")]
    [InlineData("start", "..%2F..%2Fdesks")]
    public async Task Names_outside_the_allow_list_are_refused(string action, string name)
    {
        await using var svc = await TestService.StartAsync();
        var fake = Fake(svc);
        fake.Set(new ServiceInfo("Spooler", "Print Spooler", HostServiceStatus.Running, "automatic", true, false));
        fake.Set(new ServiceInfo("wuauserv", "Windows Update", HostServiceStatus.Running, "manual", true, false));

        var response = await svc.Client.PostAsync($"/api/v1/host/services/{name}/{action}", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.NotFound, (await ReadErrorAsync(response)).Code);
        Assert.Empty(fake.Actions);
        Assert.DoesNotContain(await ListAsync(svc), s => s.Name is "Spooler" or "wuauserv");
    }

    [Fact]
    public async Task Start_and_stop_go_through_the_scm_and_answer_202()
    {
        await using var svc = await TestService.StartAsync();
        var fake = Fake(svc);
        fake.Set(new ServiceInfo("UmRdpService", "Remote Desktop Services UserMode Port Redirector", HostServiceStatus.Stopped, "manual", false, false));

        // Names are case-insensitive.
        var started = await svc.Client.PostAsync("/api/v1/host/services/umrdpservice/start", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var dto = (await started.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.HostServiceDto))!;
        Assert.Equal("UmRdpService", dto.Name);
        Assert.Contains("start UmRdpService", fake.Actions);

        var again = await svc.Client.PostAsync("/api/v1/host/services/UmRdpService/start", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var stopped = await svc.Client.PostAsync("/api/v1/host/services/TermService/stop", null);
        Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode);
        dto = (await stopped.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.HostServiceDto))!;
        Assert.Equal(HostServiceStatus.Stopping, dto.Status);
        Assert.False(dto.CanStop);
        await WaitForActionAsync(fake, "stop TermService");
        Assert.Equal(HostServiceStatus.Stopped, (await ListAsync(svc)).Single(s => s.Name == "TermService").Status);

        var notRunning = await svc.Client.PostAsync("/api/v1/host/services/TermService/stop", null);
        Assert.Equal(HttpStatusCode.Conflict, notRunning.StatusCode);
    }

    [Fact]
    public async Task Names_with_spaces_are_url_decoded()
    {
        await using var svc = await TestService.StartAsync();
        var fake = Fake(svc);
        fake.Set(new ServiceInfo("Steam Client Service", "Steam Client Service", HostServiceStatus.Stopped, "manual", false, false));
        var response = await svc.Client.PostAsync("/api/v1/host/services/Steam%20Client%20Service/start", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("start Steam Client Service", fake.Actions);
    }

    [Fact]
    public async Task A_refused_start_is_a_500_with_the_scm_message()
    {
        await using var svc = await TestService.StartAsync();
        var fake = Fake(svc);
        fake.Set(new ServiceInfo("Audiosrv", "Windows Audio", HostServiceStatus.Stopped, "automatic", false, false));
        fake.FailWith = "could not start Audiosrv: Access is denied";
        var response = await svc.Client.PostAsync("/api/v1/host/services/Audiosrv/start", null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var error = await ReadErrorAsync(response);
        Assert.Equal(ApiErrorCodes.Internal, error.Code);
        Assert.Contains("Access is denied", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_splitplay_answers_first_and_stops_through_the_scm_afterwards()
    {
        await using var svc = await TestService.StartAsync();
        var response = await svc.Client.PostAsync("/api/v1/host/services/SplitPlay/stop", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(HostServiceStatus.Stopping, (await response.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.HostServiceDto))!.Status);
        await WaitForActionAsync(Fake(svc), "stop SplitPlay");
    }

    [Fact]
    public async Task Self_stop_waits_for_the_delay()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
        var fake = new FakeServiceControl();
        var services = new RelatedServices(fake, NullLogger<RelatedServices>.Instance, time);

        Assert.Equal(HostServiceStatus.Stopping, services.Stop("splitplay").Status);
        await Task.Delay(100);
        Assert.Empty(fake.Actions);
        time.Advance(RelatedServices.SelfStopDelay - TimeSpan.FromMilliseconds(1));
        await Task.Delay(100);
        Assert.Empty(fake.Actions);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitForActionAsync(fake, "stop SplitPlay");
    }

    [Fact]
    public void Allow_list_is_fixed()
    {
        Assert.Equal(new[] { "SplitPlay", "TermService", "UmRdpService", "ViGEmBus", "Audiosrv", "Steam Client Service" },
            RelatedServices.Catalog.Select(e => e.Name));
        Assert.NotNull(RelatedServices.Find("TERMSERVICE"));
        Assert.Null(RelatedServices.Find("Spooler"));
        Assert.Null(RelatedServices.Find(""));
    }

    [Fact]
    public async Task Startup_starts_stopped_desk_dependencies_and_leaves_disabled_ones()
    {
        var fake = new FakeServiceControl();
        fake.Set(new ServiceInfo("TermService", "Remote Desktop Services", HostServiceStatus.Stopped, "manual", false, false));
        fake.Set(new ServiceInfo("UmRdpService", "Remote Desktop Services UserMode Port Redirector", HostServiceStatus.Stopped, "disabled", false, false));
        var starter = new RequiredServicesStarter(fake, NullLogger<RequiredServicesStarter>.Instance, TimeProvider.System);
        await starter.EnsureRunningAsync(CancellationToken.None);
        Assert.Equal(new[] { "start TermService" }, fake.Actions);
        Assert.Equal(HostServiceStatus.Running, fake.Query("TermService").Status);
    }

    [Fact]
    public async Task Startup_check_never_throws()
    {
        var fake = new FakeServiceControl { FailWith = "access denied" };
        fake.Set(new ServiceInfo("TermService", "Remote Desktop Services", HostServiceStatus.Stopped, "automatic", false, false));
        fake.Remove("UmRdpService");
        var starter = new RequiredServicesStarter(fake, NullLogger<RequiredServicesStarter>.Instance, TimeProvider.System);
        await starter.EnsureRunningAsync(CancellationToken.None);
        Assert.Equal(new[] { "start TermService" }, fake.Actions);
    }

    [Fact]
    public async Task Service_start_runs_the_dependency_check()
    {
        await using var svc = await TestService.StartAsync();
        await svc.App.Services.GetRequiredService<RequiredServicesStarter>().Completion;
        // The fake host has everything running: nothing to do.
        Assert.Empty(Fake(svc).Actions);
    }

    [Fact]
    public void Api_exception_type_is_used_for_refusals()
    {
        var services = new RelatedServices(new FakeServiceControl(), NullLogger<RelatedServices>.Instance, TimeProvider.System);
        var ex = Assert.Throws<ApiException>(() => services.Start("Spooler"));
        Assert.Equal(404, ex.Status);
    }
}
