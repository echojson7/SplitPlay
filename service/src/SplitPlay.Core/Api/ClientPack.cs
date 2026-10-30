// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.RegularExpressions;
using SplitPlay.Core.Configuration;

namespace SplitPlay.Core.Api;

/// <summary>Moonlight settings recommended for a desk (<see cref="ClientPackDto.Settings"/>).</summary>
/// <param name="Width">Stream width (the desk's resolution).</param>
/// <param name="Height">Stream height.</param>
/// <param name="Fps">Stream frame rate (the desk's).</param>
/// <param name="BitrateKbps">Video bitrate, kbit/s.</param>
/// <param name="Codec"><c>h264</c>, <c>hevc</c> or <c>av1</c>.</param>
/// <param name="Vsync">Moonlight's V-Sync: off (a VRR display still avoids tearing).</param>
/// <param name="DisplayMode"><c>fullscreen</c>: exclusive full screen, the lowest-latency present path.</param>
/// <param name="VideoDecoder"><c>hardware</c>: D3D11VA on Windows, VAAPI/VDPAU on Linux.</param>
/// <param name="FramePacing">Off: frame pacing holds frames back to smooth an uneven network.</param>
public sealed record ClientPackSettings(
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    string Codec,
    bool Vsync,
    string DisplayMode,
    string VideoDecoder,
    bool FramePacing);

/// <summary>One downloadable launch script of a client pack.</summary>
/// <param name="Platform"><c>windows</c> (.cmd) or <c>linux</c> (.sh).</param>
/// <param name="FileName">Suggested file name.</param>
public sealed record ClientPackScriptDto(string Platform, string FileName);

/// <summary>
/// <c>GET /desks/{id}/client-pack</c>: how a Moonlight client should connect to a desk. <see cref="Arguments"/> is
/// moonlight-qt's command line after the executable (<c>moonlight stream &lt;host:port&gt; &lt;app&gt; --resolution ...</c>).
/// </summary>
public sealed record ClientPackDto(
    string Desk,
    string Host,
    int Port,
    string Address,
    string App,
    ClientPackSettings Settings,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<ClientPackScriptDto> Scripts);

/// <summary>
/// The client launch pack: Moonlight settings that take latency out of the client (V-Sync off, exclusive full
/// screen, hardware decoding, no frame pacing, the desk's resolution and rate, a bitrate that suits them) and scripts
/// that start moonlight-qt with them from the command line. Option names are moonlight-qt's
/// (app/cli/commandlineparser.cpp, v6.2.0): <c>--resolution WxH</c>, <c>--fps</c>, <c>--bitrate</c> (kbit/s),
/// <c>--no-vsync</c>, <c>--video-codec H.264|HEVC|AV1</c>, <c>--video-decoder hardware</c>,
/// <c>--display-mode fullscreen</c>, <c>--no-frame-pacing</c>. Moonlight finds the host by <c>address:port</c>
/// (it adds it as a manual host when it does not know it yet); the device must be paired with the desk first.
/// </summary>
public static partial class ClientPack
{
    /// <summary>The Moonlight app every desk offers (worker patch 0009: apps.json has only "Desktop").</summary>
    public const string App = "Desktop";

    public const string PlatformWindows = "windows";

    public const string PlatformLinux = "linux";

    public const int DefaultWidth = 1920;

    public const int DefaultHeight = 1080;

    /// <summary>Moonlight's default frame rate, used when the profile has none.</summary>
    public const int DefaultFps = 60;

    /// <summary>moonlight-qt warns outside 500-500000 kbit/s.</summary>
    private const int MinBitrateKbps = 500;

    private const int MaxBitrateKbps = 500_000;

    [GeneratedRegex("^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}[A-Za-z0-9])?)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HostNameRegex();

    /// <summary>
    /// A bitrate that suits the picture (the web UI's rule for new desks): 30 Mbit/s for 1080p60 scaled by the square
    /// root of the pixel rate, plus 15 %, rounded to 5 Mbit/s, 10-150 Mbit/s. 1080p120: 50, 1440p120: 65, 4K60: 70.
    /// </summary>
    public static int SuggestBitrateKbps(int width, int height, int fps)
    {
        var scale = (double)width * height * fps / (1920.0 * 1080 * 60);
        var kbps = (int)Math.Round(30000 * Math.Sqrt(Math.Max(scale, 0)) * 1.15 / 5000, MidpointRounding.AwayFromZero) * 5000;
        return Math.Clamp(kbps, 10_000, 150_000);
    }

    /// <summary>
    /// True for an address Moonlight can be given and that is safe to put in a script unquoted: a dotted IPv4 address
    /// or a DNS host name (letters, digits, dashes and dots only).
    /// </summary>
    public static bool IsValidHost(string? host) => !string.IsNullOrEmpty(host) && HostNameRegex().IsMatch(host);

    /// <summary>
    /// Builds the pack for desk <paramref name="deskId"/> served at <paramref name="host"/>:<paramref name="port"/>.
    /// The profile's resolution, rate and codec are kept (HEVC when it has none); its bitrate is used when set,
    /// otherwise <see cref="SuggestBitrateKbps"/>.
    /// </summary>
    public static ClientPackDto Build(string deskId, ProfileConfig profile, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!IsValidHost(host))
        {
            throw new ArgumentException($"'{host}' is not an IPv4 address or host name", nameof(host));
        }

        var width = profile.Width ?? DefaultWidth;
        var height = profile.Height ?? DefaultHeight;
        var fps = profile.Fps ?? DefaultFps;
        var bitrate = Math.Clamp(profile.BitrateKbps ?? SuggestBitrateKbps(width, height, fps), MinBitrateKbps, MaxBitrateKbps);
        var codec = profile.Codec?.ToLowerInvariant() switch
        {
            "h264" => "h264",
            "av1" => "av1",
            _ => "hevc",
        };

        var settings = new ClientPackSettings(width, height, fps, bitrate, codec, Vsync: false, DisplayMode: "fullscreen",
            VideoDecoder: "hardware", FramePacing: false);
        var address = $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
        string[] arguments =
        [
            "stream", address, App,
            "--resolution", $"{width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}",
            "--fps", fps.ToString(CultureInfo.InvariantCulture),
            "--bitrate", bitrate.ToString(CultureInfo.InvariantCulture),
            "--video-codec", MoonlightCodec(codec),
            "--video-decoder", settings.VideoDecoder,
            "--display-mode", settings.DisplayMode,
            "--no-vsync",
            "--no-frame-pacing",
        ];

        var baseName = $"splitplay-{deskId}";
        return new ClientPackDto(deskId, host, port, address, App, settings, arguments,
        [
            new ClientPackScriptDto(PlatformWindows, baseName + ".cmd"),
            new ClientPackScriptDto(PlatformLinux, baseName + ".sh"),
        ]);
    }

    /// <summary>moonlight-qt's <c>--video-codec</c> choice for a profile codec.</summary>
    public static string MoonlightCodec(string codec) => codec switch
    {
        "h264" => "H.264",
        "av1" => "AV1",
        _ => "HEVC",
    };

    /// <summary>The script for <paramref name="platform"/> (<c>windows</c> or <c>linux</c>), null for another name.</summary>
    public static string? Script(ClientPackDto pack, string platform) => platform.ToLowerInvariant() switch
    {
        PlatformWindows => WindowsScript(pack),
        PlatformLinux => LinuxScript(pack),
        _ => null,
    };

    /// <summary>
    /// A .cmd for a Windows client: finds Moonlight.exe (<c>%MOONLIGHT%</c>, next to the script, the installer's
    /// <c>%ProgramFiles%\Moonlight Game Streaming</c>, then PATH) and starts the stream. CRLF line ends. Every value
    /// in it is a validated host, a number, a desk id or a fixed word, so nothing needs cmd escaping.
    /// </summary>
    public static string WindowsScript(ClientPackDto pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var args = string.Join(' ', pack.Arguments);
        string[] lines =
        [
            "@echo off",
            $"rem SplitPlay: desk {pack.Desk} at {pack.Address} in Moonlight. Pair this PC with the desk first.",
            $"rem {Summary(pack.Settings)}",
            "setlocal",
            "if not defined MOONLIGHT if exist \"%~dp0Moonlight.exe\" set \"MOONLIGHT=%~dp0Moonlight.exe\"",
            "if not defined MOONLIGHT if exist \"%ProgramFiles%\\Moonlight Game Streaming\\Moonlight.exe\" set \"MOONLIGHT=%ProgramFiles%\\Moonlight Game Streaming\\Moonlight.exe\"",
            "if not defined MOONLIGHT for %%I in (Moonlight.exe) do if not \"%%~$PATH:I\"==\"\" set \"MOONLIGHT=%%~$PATH:I\"",
            "if not defined MOONLIGHT goto missing",
            $"start \"\" \"%MOONLIGHT%\" {args}",
            "exit /b 0",
            ":missing",
            "echo Moonlight is not installed: https://moonlight-stream.org",
            "pause",
            "exit /b 1",
        ];
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// A POSIX sh script for a Linux client: runs <c>$MOONLIGHT</c>, <c>moonlight</c>, <c>moonlight-qt</c> or the
    /// Flathub package <c>com.moonlight_stream.Moonlight</c>, whichever is found first. LF line ends.
    /// </summary>
    public static string LinuxScript(ClientPackDto pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var args = string.Join(' ', pack.Arguments);
        string[] lines =
        [
            "#!/bin/sh",
            $"# SplitPlay: desk {pack.Desk} at {pack.Address} in Moonlight. Pair this PC with the desk first.",
            $"# {Summary(pack.Settings)}",
            "# Another Moonlight (an AppImage, say): MOONLIGHT=/path/to/Moonlight.AppImage sh \"$0\"",
            "if [ -n \"${MOONLIGHT:-}\" ]; then",
            "  set -- \"$MOONLIGHT\"",
            "elif command -v moonlight >/dev/null 2>&1; then",
            "  set -- moonlight",
            "elif command -v moonlight-qt >/dev/null 2>&1; then",
            "  set -- moonlight-qt",
            "elif command -v flatpak >/dev/null 2>&1 && flatpak info com.moonlight_stream.Moonlight >/dev/null 2>&1; then",
            "  set -- flatpak run com.moonlight_stream.Moonlight",
            "else",
            "  echo 'Moonlight is not installed: https://moonlight-stream.org' >&2",
            "  exit 1",
            "fi",
            $"exec \"$@\" {args}",
        ];
        return string.Join("\n", lines) + "\n";
    }

    private static string Summary(ClientPackSettings s) => string.Create(CultureInfo.InvariantCulture,
        $"{s.Width}x{s.Height} {s.Fps} FPS, {s.BitrateKbps} kbit/s, {MoonlightCodec(s.Codec)}, V-Sync off, full screen, hardware decoding, frame pacing off");
}
