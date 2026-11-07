// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Api;
using SplitPlay.Core.Games;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Core.Nvidia;
using SplitPlay.Service.Hosting;
using SplitPlay.Service.Platform;

namespace SplitPlay.Service.Tests;

/// <summary>Game FPS limits: games found in a desk by their GPU load get the NVIDIA frame rate limit.</summary>
public sealed class GameLimitsServiceTests
{
    private const string Luid = "0x000000000000D1B4";
    private const int DeskSession = 7; // the fake seat's session
    private const int GamePid = 300;
    private const int WorkerPid = 100;

    private static GpuEngineSample Engine(int pid, int eng, string type, double percent) => new(pid, Luid, 0, eng, type, percent);

    /// <summary>A game rendering at 60 % in the desk next to the worker (which is never a game).</summary>
    private static void GameRuns(TestService svc, string path = @"D:\Games\Foo\foo.exe")
    {
        var gpus = svc.App.Services.GetRequiredService<FakeGpuCounterSource>();
        gpus.Sessions[GamePid] = DeskSession;
        gpus.Sessions[WorkerPid] = DeskSession;
        gpus.Sample = new GpuRawSample(
            [Engine(GamePid, 0, "3D", 60), Engine(WorkerPid, 0, "3D", 30), Engine(WorkerPid, 5, "VideoEncode", 20)],
            [new(GamePid, Luid, 2048 * 1024 * 1024.0)],
            [new(Luid, 4096 * 1024 * 1024.0)]);
        var images = svc.App.Services.GetRequiredService<FakeProcessImages>();
        images.Paths[GamePid] = path;
        images.Paths[WorkerPid] = @"C:\Program Files\SplitPlay\worker\splitplay-worker.exe";
    }

    /// <summary>Checks until <paramref name="done"/> holds (the watcher's own interval is too slow for a test).</summary>
    private static async Task<List<GameDto>> WaitForGamesAsync(TestService svc, Func<List<GameDto>, bool> done, string what)
    {
        var watcher = svc.App.Services.GetRequiredService<GameWatcher>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            watcher.Tick();
            var games = (await svc.Client.GetFromJsonAsync("/api/v1/games", SplitPlayJsonContext.Default.ListGameDto))!;
            if (done(games))
            {
                return games;
            }

            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}; games: {string.Join(", ", games.Select(g => $"{g.Exe}:{g.Status}"))}");
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task A_game_in_a_desk_is_found_and_limited_and_the_owner_can_turn_it_off()
    {
        await using var svc = await TestService.StartAsync();
        var drs = svc.App.Services.GetRequiredService<FakeDrs>();
        GameRuns(svc);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        var games = await WaitForGamesAsync(svc, g => g.Any(x => x.Status == "limited"), "foo.exe limited");
        var game = Assert.Single(games);
        Assert.Equal("foo.exe", game.Exe);
        Assert.Equal(new[] { "desk-1" }, game.Desks);
        Assert.NotNull(game.Limit);
        Assert.Equal(NvidiaProfiles.GameProfile("foo.exe"), game.Profile);
        Assert.Equal((uint)game.Limit!.Value, drs.Get(NvidiaProfiles.GameProfile("foo.exe"), DrsSettings.FrameRateLimit));
        Assert.True(File.Exists(GameCatalog.FileIn(svc.DataRoot)));

        // A fixed limit for the desk: the game follows.
        var desk = await svc.GetDeskAsync("desk-1");
        var put = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json($$"""{"id":"desk-1","user":"{{desk.User}}","profile":{{Profile(desk)}},"gameFpsLimit":45}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        await WaitForGamesAsync(svc, g => g.Single().Limit == 45 && g.Single().Status == "limited", "limit 45");
        Assert.Equal(45u, drs.Get(NvidiaProfiles.GameProfile("foo.exe"), DrsSettings.FrameRateLimit));

        // An update that leaves the limit out keeps it.
        put = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json($$"""{"id":"desk-1","user":"{{desk.User}}","profile":{{Profile(desk)}}}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(45, (await svc.GetDeskAsync("desk-1")).GameFpsLimit);

        // Off for this game: the driver gets its profile back at once.
        var excluded = await svc.Client.PutAsync("/api/v1/games/foo.exe", Json("""{"excluded":true}"""));
        Assert.Equal(HttpStatusCode.OK, excluded.StatusCode);
        Assert.Equal("excluded", (await excluded.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.GameDto))!.Status);
        Assert.False(drs.HasProfile(NvidiaProfiles.GameProfile("foo.exe")));

        Assert.Equal(HttpStatusCode.NoContent, (await svc.Client.DeleteAsync("/api/v1/games/foo.exe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await svc.Client.DeleteAsync("/api/v1/games/foo.exe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await svc.Client.PutAsync("/api/v1/games/foo.exe", Json("""{"excluded":false}"""))).StatusCode);
    }

    [Fact]
    public async Task A_browser_in_a_desk_is_not_a_game()
    {
        await using var svc = await TestService.StartAsync();
        GameRuns(svc, @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var watcher = svc.App.Services.GetRequiredService<GameWatcher>();
        for (var i = 0; i < 15; i++)
        {
            watcher.Tick();
            await Task.Delay(200);
        }

        Assert.Empty((await svc.Client.GetFromJsonAsync("/api/v1/games", SplitPlayJsonContext.Default.ListGameDto))!);
    }

    [Fact]
    public async Task A_game_profile_of_nvidia_is_used_and_an_invalid_limit_is_refused()
    {
        await using var svc = await TestService.StartAsync(drs: d => d.AddProfile("Foo (NVIDIA)", "foo.exe"));
        var drs = svc.App.Services.GetRequiredService<FakeDrs>();
        GameRuns(svc);
        var desk = await svc.CreateDeskAsync("desk-1", ""","gameFpsLimit":50""");
        Assert.Equal(50, desk.GameFpsLimit);
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        var game = Assert.Single(await WaitForGamesAsync(svc, g => g.Any(x => x.Status == "limited"), "foo.exe limited"));
        Assert.Equal("Foo (NVIDIA)", game.Profile);
        Assert.Equal(50u, drs.Get("Foo (NVIDIA)", DrsSettings.FrameRateLimit));
        Assert.False(drs.HasProfile(NvidiaProfiles.GameProfile("foo.exe")));

        var bad = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json($$"""{"id":"desk-1","user":"{{desk.User}}","profile":{{Profile(desk)}},"gameFpsLimit":5}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private static string Profile(DeskDto desk) => System.Text.Json.JsonSerializer.Serialize(desk.Profile, SplitPlayJsonContext.Default.ProfileConfig);

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
