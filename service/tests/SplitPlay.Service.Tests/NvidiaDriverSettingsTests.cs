// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Core.Nvidia;
using SplitPlay.Service.Platform;

namespace SplitPlay.Service.Tests;

/// <summary>The service owns the NVIDIA driver settings (patch 0020: workers leave them alone).</summary>
public sealed class NvidiaDriverSettingsTests
{
    [Fact]
    public async Task The_service_sets_the_driver_settings_and_gives_the_global_one_back_when_it_stops()
    {
        var root = TestService.NewDataRoot();
        FakeDrs drs;
        try
        {
            // The owner's global value before SplitPlay: 0.
            await using (var svc = await TestService.StartAsync(dataRoot: root, drs: d => d.Set(null, DrsSettings.OglPreferDxPresent, 0)))
            {
                drs = svc.App.Services.GetRequiredService<FakeDrs>();
                await svc.CreateDeskAsync("desk-1");
                await svc.PostDeskActionAsync("desk-1", "start");
                await svc.WaitForStateAsync("desk-1", DeskState.Ready);

                // Checked again before the worker started.
                Assert.True(drs.HasProfile(NvidiaProfiles.WorkerProfile));
                Assert.Equal(DrsSettings.PreferredPStatePreferMax, drs.Get(NvidiaProfiles.WorkerProfile, DrsSettings.PreferredPState));
                Assert.Equal(DrsSettings.OglPreferDxPresentEnabled, drs.Get(null, DrsSettings.OglPreferDxPresent));
            }

            // Service stop: the global value is back, the worker's profile stays for the next start.
            Assert.Equal(0u, drs.Get(null, DrsSettings.OglPreferDxPresent));
            Assert.True(drs.HasProfile(NvidiaProfiles.WorkerProfile));
            Assert.True(File.Exists(NvidiaProfiles.StateFileIn(root)));
        }
        finally
        {
            TestService.TryDelete(root);
        }
    }

    [Fact]
    public async Task Turning_nvidiaProfiles_off_gives_everything_back()
    {
        await using var svc = await TestService.StartAsync();
        var drs = svc.App.Services.GetRequiredService<FakeDrs>();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.True(drs.HasProfile(NvidiaProfiles.WorkerProfile));

        var host = (await svc.Client.GetFromJsonAsync("/api/v1/host", SplitPlayJsonContext.Default.HostConfig))!;
        Assert.True(host.NvidiaProfiles);
        host.NvidiaProfiles = false;
        var put = await svc.Client.PutAsJsonAsync("/api/v1/host", host, SplitPlayJsonContext.Default.HostConfig);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False(drs.HasProfile(NvidiaProfiles.WorkerProfile));
        Assert.Null(drs.Get(null, DrsSettings.OglPreferDxPresent));

        // Off: a worker start leaves the driver alone.
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.False(drs.HasProfile(NvidiaProfiles.WorkerProfile));
    }

    [Fact]
    public async Task A_failing_driver_does_not_keep_a_desk_from_starting()
    {
        await using var svc = await TestService.StartAsync();
        var drs = svc.App.Services.GetRequiredService<FakeDrs>();
        drs.AddProfile("Someone else", NvidiaProfiles.WorkerExecutable);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
    }

    [Fact]
    public void The_NvAPI_structures_have_the_sizes_of_nvapi_h()
    {
        // sizeof()/offsetof() of NVDRS_SETTING_V1, NVDRS_PROFILE_V1 and NVDRS_APPLICATION_V1 from nvapi.h on x64.
        Assert.Equal(12328, NvApiDrs.SettingSize);
        Assert.Equal(0x13028u, NvApiDrs.SettingVersion1);
        Assert.Equal(4100, NvApiDrs.SettingIdOffset);
        Assert.Equal(4108, NvApiDrs.SettingLocationOffset);
        Assert.Equal(8224, NvApiDrs.SettingCurrentValueOffset);
        Assert.Equal(4116, NvApiDrs.ProfileSize);
        Assert.Equal(12296, NvApiDrs.ApplicationSize);

        var setting = NvApiDrs.DwordSetting(DrsSettings.OglPreferDxPresent, 1);
        Assert.Equal(NvApiDrs.SettingSize, setting.Length);
        Assert.Equal(DrsSettings.OglPreferDxPresent, BitConverter.ToUInt32(setting, NvApiDrs.SettingIdOffset));
        Assert.Equal(1u, BitConverter.ToUInt32(setting, NvApiDrs.SettingCurrentValueOffset));
        Assert.Equal("splitplay-worker.exe", System.Text.Encoding.Unicode.GetString(NvApiDrs.UnicodeString("splitplay-worker.exe")).TrimEnd('\0'));
    }
}
