// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Json;
using SplitPlay.Core.Api;
using SplitPlay.Core.Json;

namespace SplitPlay.Service.Tests;

public sealed class ClientPackApiTests
{
    private const string Profile = ""","basePort":48189,"profile":{"width":2560,"height":1440,"fps":120,"bitrateKbps":60000,"codec":"hevc"}""";

    [Fact]
    public async Task Pack_recommends_the_desk_settings_at_the_given_address()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1", Profile);

        var pack = await svc.Client.GetFromJsonAsync("/api/v1/desks/desk-1/client-pack?address=192.168.1.20", SplitPlayJsonContext.Default.ClientPackDto);

        Assert.Equal("desk-1", pack!.Desk);
        Assert.Equal("192.168.1.20", pack.Host);
        Assert.Equal(48189, pack.Port);
        Assert.Equal("192.168.1.20:48189", pack.Address);
        Assert.Equal("Desktop", pack.App);
        Assert.Equal(new ClientPackSettings(2560, 1440, 120, 60_000, "hevc", false, "fullscreen", "hardware", false), pack.Settings);
        Assert.Equal("--no-vsync", pack.Arguments[^2]);
    }

    [Fact]
    public async Task Pack_defaults_to_a_lan_address_or_the_computer_name()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1", Profile);

        var pack = await svc.Client.GetFromJsonAsync("/api/v1/desks/desk-1/client-pack", SplitPlayJsonContext.Default.ClientPackDto);
        Assert.True(ClientPack.IsValidHost(pack!.Host));
        Assert.Equal($"{pack.Host}:48189", pack.Address);
    }

    [Fact]
    public async Task Scripts_download_as_attachments()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1", Profile);

        var cmd = await svc.Client.GetAsync("/api/v1/desks/desk-1/client-pack/windows?address=192.168.1.20");
        Assert.Equal(HttpStatusCode.OK, cmd.StatusCode);
        Assert.Equal("attachment", cmd.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("splitplay-desk-1.cmd", cmd.Content.Headers.ContentDisposition.FileName);
        var cmdText = await cmd.Content.ReadAsStringAsync();
        Assert.Contains("stream 192.168.1.20:48189 Desktop --resolution 2560x1440 --fps 120 --bitrate 60000", cmdText, StringComparison.Ordinal);
        Assert.Contains("\r\n", cmdText, StringComparison.Ordinal);

        var sh = await svc.Client.GetAsync("/api/v1/desks/desk-1/client-pack/linux?address=gaming-pc");
        Assert.Equal(HttpStatusCode.OK, sh.StatusCode);
        Assert.Equal("splitplay-desk-1.sh", sh.Content.Headers.ContentDisposition!.FileName);
        var shText = await sh.Content.ReadAsStringAsync();
        Assert.StartsWith("#!/bin/sh\n", shText, StringComparison.Ordinal);
        Assert.Contains("stream gaming-pc:48189 Desktop", shText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/v1/desks/nope/client-pack", HttpStatusCode.NotFound, ApiErrorCodes.NotFound)]
    [InlineData("/api/v1/desks/desk-1/client-pack/macos", HttpStatusCode.NotFound, ApiErrorCodes.NotFound)]
    [InlineData("/api/v1/desks/desk-1/client-pack?address=pc%26calc", HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed)]
    [InlineData("/api/v1/desks/desk-1/client-pack/windows?address=%25COMSPEC%25", HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed)]
    public async Task Bad_requests_are_refused(string url, HttpStatusCode status, string code)
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1", Profile);

        var response = await svc.Client.GetAsync(url);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.ApiErrorResponse))!.Error.Code);
    }
}
