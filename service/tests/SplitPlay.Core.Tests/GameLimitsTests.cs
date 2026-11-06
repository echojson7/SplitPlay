// SPDX-License-Identifier: MIT
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Games;
using SplitPlay.Core.Nvidia;

namespace SplitPlay.Core.Tests;

public sealed class GameLimitsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "splitplay-games-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Also a directory that was never created.
        }
    }

    private static DeskConfig Desk(string id, int? limit, int fps = 120) => new() { Id = id, GameFpsLimit = limit, Profile = new ProfileConfig { Fps = fps } };

    [Fact]
    public void Auto_is_the_clients_fps_then_the_desks_fps()
    {
        Assert.Equal(90, GameLimits.DeskLimit(Desk("a", null), 90));
        Assert.Equal(120, GameLimits.DeskLimit(Desk("a", null), null));
        Assert.Equal(GameLimits.DefaultFps, GameLimits.DeskLimit(new DeskConfig { Id = "a" }, null));
        Assert.Null(GameLimits.DeskLimit(Desk("a", GameLimits.Off), 90));
        Assert.Equal(144, GameLimits.DeskLimit(Desk("a", 144), 90));
    }

    [Fact]
    public void A_game_gets_the_largest_limit_of_its_desks_and_none_when_excluded_or_off()
    {
        var config = new SplitPlayConfig { Desks = [Desk("a", 60), Desk("b", null), Desk("c", GameLimits.Off)] };
        var state = new GameCatalogState
        {
            Games =
            [
                new GameRecord { Exe = "both.exe", Desks = ["a", "b"] },
                new GameRecord { Exe = "off.exe", Desks = ["c"] },
                new GameRecord { Exe = "excluded.exe", Desks = ["a"], Excluded = true },
                new GameRecord { Exe = "gone.exe", Desks = ["deleted-desk"] },
            ],
            ClientFps = { ["b"] = 75 },
        };

        var limits = GameLimits.Limits(config, state);
        var only = Assert.Single(limits);
        Assert.Equal(("both.exe", 75), (only.Key, only.Value));

        var spec = Assert.Single(GameLimits.Specs(limits));
        Assert.Equal("both.exe", spec.Executable);
        Assert.Equal(75u, spec.Settings[DrsSettings.FrameRateLimit]);
        Assert.True(GameLimits.Same(limits, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["BOTH.exe"] = 75 }));
        Assert.False(GameLimits.Same(limits, null));
    }

    [Theory]
    [InlineData(@"D:\Games\Elden Ring\eldenring.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.Forza\forza.exe", true)]
    [InlineData(@"C:\Windows\System32\dwm.exe", false)]
    [InlineData(@"C:\WINDOWS\explorer.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\x\steamwebhelper.exe", false)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    [InlineData(@"C:\Program Files\SplitPlay\worker\splitplay-worker.exe", false)]
    [InlineData(@"D:\Games\thing.bin", false)]
    [InlineData("", false)]
    public void Only_programs_that_may_be_games_count(string path, bool game) =>
        Assert.Equal(game, GameLimits.MayBeGame(path, @"C:\Windows"));

    [Fact]
    public void The_catalog_records_games_per_desk_and_survives_a_restart()
    {
        var file = Path.Combine(_dir, GameCatalog.FileName);
        var now = DateTimeOffset.Parse("2026-11-06T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var catalog = new GameCatalog(file);
        Assert.True(catalog.Record("game.exe", @"D:\g\game.exe", "a", now));
        Assert.False(catalog.Record("GAME.exe", @"D:\g\game.exe", "a", now.AddHours(1)));
        Assert.True(catalog.Record("game.exe", @"D:\g\game.exe", "b", now));
        Assert.True(catalog.SetClientFps("a", 90));
        Assert.False(catalog.SetClientFps("a", 90));
        Assert.True(catalog.SetExcluded("game.exe", true));
        Assert.False(catalog.SetExcluded("other.exe", true));

        var again = new GameCatalog(file).Snapshot();
        var game = Assert.Single(again.Games);
        Assert.Equal(new[] { "a", "b" }, game.Desks);
        Assert.True(game.Excluded);
        Assert.Equal(90, again.ClientFps["a"]);

        // A deleted desk goes, and with it games seen only there.
        var reloaded = new GameCatalog(file);
        Assert.True(reloaded.Prune(["b"]));
        Assert.Equal(new[] { "b" }, Assert.Single(reloaded.Snapshot().Games).Desks);
        Assert.Empty(reloaded.Snapshot().ClientFps);
        Assert.True(reloaded.Prune([]));
        Assert.Empty(reloaded.Snapshot().Games);
        Assert.False(reloaded.Forget("game.exe"));
    }

    [Fact]
    public void A_damaged_catalog_starts_empty()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, GameCatalog.FileName);
        File.WriteAllText(file, "{ not json");
        var catalog = new GameCatalog(file);
        Assert.NotNull(catalog.LoadError);
        Assert.Empty(catalog.Snapshot().Games);
    }

    [Fact]
    public void The_limit_is_validated()
    {
        static IReadOnlyList<ConfigError> Check(int? limit) =>
            ConfigValidator.Validate(new SplitPlayConfig { Desks = [new DeskConfig { Id = "a", User = "a", GameFpsLimit = limit }] })
                .Where(e => e.Path.EndsWith("gameFpsLimit", StringComparison.Ordinal)).ToList();

        Assert.Empty(Check(null));
        Assert.Empty(Check(0));
        Assert.Empty(Check(60));
        Assert.NotEmpty(Check(5));
        Assert.NotEmpty(Check(2000));
        Assert.NotEmpty(Check(-1));
    }
}
