// SPDX-License-Identifier: MIT
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;

namespace SplitPlay.Core.Tests;

public sealed class ClientPackTests
{
    [Theory]
    [InlineData(1920, 1080, 60, 35_000)]
    [InlineData(1920, 1080, 120, 50_000)]
    [InlineData(2560, 1440, 120, 65_000)]
    [InlineData(3840, 2160, 60, 70_000)]
    [InlineData(1280, 720, 30, 15_000)]
    [InlineData(640, 480, 10, 10_000)]
    [InlineData(7680, 4320, 500, 150_000)]
    public void Suggested_bitrate_follows_the_pixel_rate(int width, int height, int fps, int expected) =>
        Assert.Equal(expected, ClientPack.SuggestBitrateKbps(width, height, fps));

    [Fact]
    public void Pack_carries_the_desk_profile_and_the_latency_settings()
    {
        var pack = ClientPack.Build("desk-1", new ProfileConfig { Width = 2560, Height = 1440, Fps = 120, BitrateKbps = 80_000, Codec = "hevc" },
            "192.168.1.20", 48089);

        Assert.Equal("192.168.1.20:48089", pack.Address);
        Assert.Equal("Desktop", pack.App);
        Assert.Equal(new ClientPackSettings(2560, 1440, 120, 80_000, "hevc", false, "fullscreen", "hardware", false), pack.Settings);
        Assert.Equal(
        [
            "stream", "192.168.1.20:48089", "Desktop", "--resolution", "2560x1440", "--fps", "120", "--bitrate", "80000",
            "--video-codec", "HEVC", "--video-decoder", "hardware", "--display-mode", "fullscreen", "--no-vsync", "--no-frame-pacing",
        ], pack.Arguments);
        Assert.Equal(["splitplay-desk-1.cmd", "splitplay-desk-1.sh"], pack.Scripts.Select(x => x.FileName));
    }

    [Fact]
    public void Missing_profile_values_get_defaults_and_a_suggested_bitrate()
    {
        var pack = ClientPack.Build("d", new ProfileConfig(), "host-pc", 47989);
        Assert.Equal(new ClientPackSettings(1920, 1080, 60, 35_000, "auto", false, "fullscreen", "hardware", false), pack.Settings);
        Assert.Equal("auto", pack.Arguments[pack.Arguments.ToList().IndexOf("--video-codec") + 1]);
    }

    [Theory]
    [InlineData("h264", "H.264")]
    [InlineData("AV1", "AV1")]
    [InlineData("hevc", "HEVC")]
    [InlineData("vp9", "auto")]
    [InlineData(null, "auto")]
    public void Codec_maps_to_moonlight_choices(string? codec, string expected)
    {
        var pack = ClientPack.Build("d", new ProfileConfig { Codec = codec }, "10.0.0.2", 48089);
        Assert.Equal(expected, pack.Arguments[pack.Arguments.ToList().IndexOf("--video-codec") + 1]);
    }

    [Fact]
    public void Bitrate_is_kept_in_moonlights_range()
    {
        Assert.Equal(500, ClientPack.Build("d", new ProfileConfig { BitrateKbps = 100 }, "h", 1).Settings.BitrateKbps);
        Assert.Equal(500_000, ClientPack.Build("d", new ProfileConfig { BitrateKbps = 2_000_000 }, "h", 1).Settings.BitrateKbps);
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("gaming-pc", true)]
    [InlineData("gaming-pc.local", true)]
    [InlineData("", false)]
    [InlineData("pc & calc", false)]
    [InlineData("pc;rm -rf", false)]
    [InlineData("$(id)", false)]
    [InlineData("%PATH%", false)]
    [InlineData("pc\n", false)]
    [InlineData("pc\r\n", false)]
    [InlineData("evil.com\nstart calc", false)]
    [InlineData("fe80::1", false)]
    [InlineData("-pc", false)]
    public void Host_must_be_safe_to_put_in_a_script(string host, bool valid)
    {
        Assert.Equal(valid, ClientPack.IsValidHost(host));
        if (!valid)
        {
            Assert.Throws<ArgumentException>(() => ClientPack.Build("d", new ProfileConfig(), host, 48089));
        }
    }

    [Fact]
    public void Windows_script_finds_moonlight_and_starts_the_stream()
    {
        var pack = ClientPack.Build("desk-1", new ProfileConfig { Width = 1920, Height = 1080, Fps = 120, BitrateKbps = 50_000, Codec = "hevc" }, "192.168.1.20", 48089);
        var script = ClientPack.WindowsScript(pack);

        Assert.StartsWith("@echo off\r\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", script.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains(@"%ProgramFiles%\Moonlight Game Streaming\Moonlight.exe", script, StringComparison.Ordinal);
        Assert.Contains(
            "start \"\" \"%MOONLIGHT%\" stream 192.168.1.20:48089 Desktop --resolution 1920x1080 --fps 120 --bitrate 50000 "
            + "--video-codec HEVC --video-decoder hardware --display-mode fullscreen --no-vsync --no-frame-pacing\r\n",
            script, StringComparison.Ordinal);
        Assert.Equal(script, ClientPack.Script(pack, "Windows"));
    }

    [Fact]
    public void Linux_script_tries_path_and_flatpak()
    {
        var pack = ClientPack.Build("desk-1", new ProfileConfig { Fps = 60, Codec = "hevc" }, "192.168.1.20", 48089);
        var script = ClientPack.LinuxScript(pack);

        Assert.StartsWith("#!/bin/sh\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", script, StringComparison.Ordinal);
        Assert.Contains("command -v moonlight ", script, StringComparison.Ordinal);
        Assert.Contains("flatpak run com.moonlight_stream.Moonlight", script, StringComparison.Ordinal);
        Assert.EndsWith("exec \"$@\" stream 192.168.1.20:48089 Desktop --resolution 1920x1080 --fps 60 --bitrate 35000 "
                        + "--video-codec HEVC --video-decoder hardware --display-mode fullscreen --no-vsync --no-frame-pacing\n",
            script, StringComparison.Ordinal);
        Assert.Equal(script, ClientPack.Script(pack, "linux"));
        Assert.Null(ClientPack.Script(pack, "macos"));
    }
}
