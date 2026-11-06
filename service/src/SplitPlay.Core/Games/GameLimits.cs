// SPDX-License-Identifier: MIT
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Nvidia;

namespace SplitPlay.Core.Games;

/// <summary>
/// The game FPS limits (stage4-plan, PR 7): the service notices games in a desk by their GPU load and gives each one
/// the NVIDIA driver's frame rate limit (<see cref="DrsSettings.FrameRateLimit"/>), so a desk's game does not render
/// frames nobody sees and takes less of the GPU from the console and the other desks.
/// <list type="bullet">
/// <item>The driver reads its profiles when a game starts: a limit applies from the game's next start.</item>
/// <item>A driver profile is per executable, so the limit holds for that game on the whole PC (also on the console).
/// A game seen in several desks gets the largest of their limits.</item>
/// </list>
/// </summary>
public static class GameLimits
{
    /// <summary><see cref="DeskConfig.GameFpsLimit"/> value that turns the limit off for a desk.</summary>
    public const int Off = 0;

    public const int MinFps = 10;

    public const int MaxFps = 1000;

    /// <summary>Auto when neither a client nor the desk profile tells the frame rate.</summary>
    public const int DefaultFps = 60;

    /// <summary>
    /// A desk's limit: the fixed value, or for Auto the frame rate the last Moonlight client asked for (the desk's
    /// profile fps before any client connected); null when off.
    /// </summary>
    public static int? DeskLimit(DeskConfig desk, int? clientFps)
    {
        ArgumentNullException.ThrowIfNull(desk);
        return desk.GameFpsLimit switch
        {
            null => Math.Clamp(clientFps is > 0 ? clientFps.Value : desk.Profile?.Fps ?? DefaultFps, MinFps, MaxFps),
            Off => null,
            int fixedFps => Math.Clamp(fixedFps, MinFps, MaxFps),
        };
    }

    /// <summary>
    /// The limit of every game on record that is not excluded: the largest limit of the desks it was seen in (desks that
    /// no longer exist and desks with the limit off do not count). Games without a limit are left out.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Limits(SplitPlayConfig config, GameCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in state.Games)
        {
            if (Limit(config, state, game) is int fps)
            {
                limits[game.Exe] = fps;
            }
        }

        return limits;
    }

    /// <summary>The limit of one game (see <see cref="Limits"/>); null when it gets none.</summary>
    public static int? Limit(SplitPlayConfig config, GameCatalogState state, GameRecord game)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(game);
        if (game.Excluded)
        {
            return null;
        }

        int? best = null;
        foreach (var deskId in game.Desks)
        {
            var desk = config.Desks.FirstOrDefault(d => d.Id == deskId);
            if (desk is not null && DeskLimit(desk, state.ClientFps.TryGetValue(deskId, out var f) ? f : null) is int fps)
            {
                best = Math.Max(best ?? 0, fps);
            }
        }

        return best;
    }

    /// <summary>The driver settings for <paramref name="limits"/>.</summary>
    public static IReadOnlyList<DrsAppSpec> Specs(IReadOnlyDictionary<string, int> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return [.. limits.OrderBy(l => l.Key, StringComparer.OrdinalIgnoreCase)
            .Select(l => new DrsAppSpec(l.Key, new Dictionary<uint, uint> { [DrsSettings.FrameRateLimit] = (uint)Math.Clamp(l.Value, MinFps, MaxFps) }))];
    }

    /// <summary>True when both hold the same games with the same limits.</summary>
    public static bool Same(IReadOnlyDictionary<string, int>? a, IReadOnlyDictionary<string, int>? b) =>
        a is not null && b is not null && a.Count == b.Count
        && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    // ------------------------------------------------------------------ what counts as a game

    /// <summary>
    /// Programs that load the GPU in a desk but are not games: Windows itself, SplitPlay, launchers, browsers, chat and
    /// video apps, overlays. Lower case file names.
    /// </summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows
        "dwm.exe", "explorer.exe", "csrss.exe", "winlogon.exe", "fontdrvhost.exe", "searchhost.exe", "searchapp.exe",
        "startmenuexperiencehost.exe", "shellexperiencehost.exe", "textinputhost.exe", "applicationframehost.exe",
        "widgets.exe", "widgetservice.exe", "phoneexperiencehost.exe", "lockapp.exe", "systemsettings.exe", "rdpclip.exe",
        "mstsc.exe", "taskmgr.exe", "gamebar.exe", "gamebarftserver.exe", "gamebarpresencewriter.exe", "xboxpcapp.exe",
        "msedgewebview2.exe", "photos.exe", "microsoft.photos.exe", "video.ui.exe", "snippingtool.exe", "screenclippinghost.exe",
        // SplitPlay and streaming
        "sunshine.exe", "moonlight.exe", "obs64.exe", "obs32.exe",
        // launchers and stores
        "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "epicwebhelper.exe", "galaxyclient.exe",
        "galaxyclient helper.exe", "battle.net.exe", "agent.exe", "eadesktop.exe", "eabackgroundservice.exe", "origin.exe",
        "originwebhelperservice.exe", "ubisoftconnect.exe", "upc.exe", "uplaywebcore.exe", "riotclientservices.exe",
        "riotclientux.exe", "riotclientuxrender.exe", "rockstarlauncher.exe", "socialclubhelper.exe", "playnite.desktopapp.exe",
        "playnite.fullscreenapp.exe", "itch.exe", "heroicgameslauncher.exe",
        // browsers
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "opera_gx.exe", "brave.exe", "browser.exe", "vivaldi.exe",
        "yandex.exe", "arc.exe",
        // chat, media, overlays, tools
        "discord.exe", "discordptb.exe", "discordcanary.exe", "telegram.exe", "teams.exe", "ms-teams.exe", "slack.exe",
        "zoom.exe", "skype.exe", "spotify.exe", "vlc.exe", "mpv.exe", "mpc-hc64.exe", "mpc-be64.exe", "potplayermini64.exe",
        "wallpaper32.exe", "wallpaper64.exe", "nvidia overlay.exe", "nvidia app.exe", "nvidia share.exe", "nvcontainer.exe",
        "nvsphelper64.exe", "code.exe", "devenv.exe", "rtss.exe", "msiafterburner.exe",
    };

    /// <summary>
    /// True when the process at <paramref name="imagePath"/> may be a game: an <c>.exe</c> outside the Windows
    /// directory that is not one of SplitPlay's and not on the list of known non-games.
    /// </summary>
    /// <param name="windowsDirectory">%WINDIR% (null: not checked).</param>
    public static bool MayBeGame(string imagePath, string? windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return false;
        }

        var name = FileName(imagePath);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("splitplay", StringComparison.OrdinalIgnoreCase)
            || NotGames.Contains(name))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(windowsDirectory))
        {
            var windows = windowsDirectory.TrimEnd('\\', '/') + "\\";
            if (imagePath.Replace('/', '\\').StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The file name of a Windows path (also when this code runs elsewhere, as in tests).</summary>
    public static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
}
