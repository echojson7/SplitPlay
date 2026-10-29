// SPDX-License-Identifier: MIT
using System.Net;
using System.Text;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Tests;

/// <summary>The stream settings the worker applies (FEC, minimum fps, DPB, pacing): defaults and live changes.</summary>
public sealed class StreamProfileTests
{
    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public void Unset_stream_settings_are_sent_as_the_service_defaults()
    {
        var p = DeskRuntime.ToIpcProfile(new ProfileConfig { Fps = 120 });
        Assert.Equal(ProfileConfig.DefaultFecPercent, p.FecPercent);
        Assert.Equal(20, p.MinFps);
        Assert.Equal(16, p.DpbFrames);
        Assert.Equal(800, p.PacingMbps);

        // The idle rate never exceeds the desk's frame rate; set values pass through.
        Assert.Equal(10, DeskRuntime.ToIpcProfile(new ProfileConfig { Fps = 10 }).MinFps);
        var set = DeskRuntime.ToIpcProfile(new ProfileConfig { Fps = 60, FecPercent = 5, MinFps = 30, DpbFrames = 4, PacingMbps = 2500 });
        Assert.Equal((5, 30, 4, 2500), (set.FecPercent, set.MinFps, set.DpbFrames, set.PacingMbps));
    }

    [Fact]
    public async Task Welcome_carries_the_defaults_and_a_cleared_value_goes_back_to_its_default_live()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        var welcome = svc.Launcher.Received.Select(m => m.Message).OfType<WelcomeMessage>().First(w => w.Rdp is null);
        Assert.Equal((20, 20, 16, 800), (welcome.Profile!.FecPercent, welcome.Profile.MinFps, welcome.Profile.DpbFrames, welcome.Profile.PacingMbps));

        var put = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"profile":{"fps":120,"pacingMbps":400,"minFps":10}}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var first = await NextSetProfileAsync(svc, 0);
        Assert.Equal((400, 10), (first.Profile!.PacingMbps, first.Profile.MinFps));

        put = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"profile":{"fps":120}}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var second = await NextSetProfileAsync(svc, 1);
        Assert.Equal((800, 20), (second.Profile!.PacingMbps, second.Profile.MinFps));
    }

    private static async Task<SetProfileMessage> NextSetProfileAsync(TestService svc, int index)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var pushed = svc.Launcher.Received.Select(m => m.Message).OfType<SetProfileMessage>().ToList();
            if (pushed.Count > index)
            {
                return pushed[index];
            }

            Assert.True(DateTime.UtcNow < deadline, "the profile was not pushed to the worker");
            await Task.Delay(25);
        }
    }
}
