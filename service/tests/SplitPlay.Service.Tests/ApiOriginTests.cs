// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Api;
using SplitPlay.Core.Json;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Tests;

/// <summary>Cross-origin and DNS-rebinding refusals in front of every <c>/api</c> route.</summary>
public sealed class ApiOriginTests
{
    private static HttpRequestMessage Post(string path, string? origin = null, string? fetchSite = null, string? host = null, string? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (fetchSite is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", fetchSite);
        }

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        return request;
    }

    private static async Task<ApiError> ReadErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.ApiErrorResponse))!.Error;

    [Theory]
    [InlineData("http://evil.example", null)]
    [InlineData("null", null)]
    [InlineData("http://localhost:5173", null)]
    [InlineData("https://localhost", null)]
    [InlineData("http://127.0.0.1", null)]
    [InlineData(null, "cross-site")]
    [InlineData("http://localhost", "cross-site")]
    public async Task Cross_origin_service_stop_is_refused(string? origin, string? fetchSite)
    {
        await using var svc = await TestService.StartAsync();
        var response = await svc.Client.SendAsync(Post("/api/v1/host/services/TermService/stop", origin, fetchSite));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Forbidden, (await ReadErrorAsync(response)).Code);
        await Task.Delay(50);
        Assert.Empty(svc.App.Services.GetRequiredService<FakeServiceControl>().Actions);
    }

    [Fact]
    public async Task Cross_origin_desk_create_is_refused()
    {
        await using var svc = await TestService.StartAsync();
        var response = await svc.Client.SendAsync(Post("/api/v1/desks", "http://evil.example", "cross-site", body: """{"id":"desk-1"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await svc.Client.GetFromJsonAsync("/api/v1/desks", SplitPlayJsonContext.Default.ListDeskDto))!);
    }

    [Theory]
    [InlineData(null, null, null)] // CLI, scripts: no browser headers
    [InlineData("http://localhost", "same-origin", null)] // the UI (tray WebView or a browser)
    [InlineData(null, "none", null)] // typed into the address bar
    [InlineData("http://127.0.0.1:38299", "same-origin", "127.0.0.1:38299")]
    [InlineData("http://192.168.1.20:38299", "same-origin", "192.168.1.20:38299")] // LAN access: the origin is the LAN host
    [InlineData("http://[::1]:38299", null, "[::1]:38299")]
    public async Task Same_origin_and_headerless_requests_pass(string? origin, string? fetchSite, string? host)
    {
        await using var svc = await TestService.StartAsync();
        var response = await svc.Client.SendAsync(Post("/api/v1/desks", origin, fetchSite, host, """{"id":"desk-1"}"""));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Cross_origin_reads_are_not_blocked_by_the_origin_check()
    {
        // Without CORS headers a browser does not hand the answer to the other site; GET changes nothing.
        await using var svc = await TestService.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/host/services");
        request.Headers.Add("Origin", "http://evil.example");
        var response = await svc.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("evil.example:38299")]
    [InlineData("rebind.attacker.test")]
    public async Task Dns_rebinding_host_names_are_refused_on_loopback(string host)
    {
        await using var svc = await TestService.StartAsync();
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/desks");
        get.Headers.Host = host;
        var response = await svc.Client.SendAsync(get);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Forbidden, (await ReadErrorAsync(response)).Code);

        // Same-origin from the attacker's point of view, so only the Host check stops it.
        var post = await svc.Client.SendAsync(Post("/api/v1/host/services/TermService/stop", $"http://{host}", "same-origin", host));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task Ui_files_are_not_affected()
    {
        await using var svc = await TestService.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "mypc";
        var response = await svc.Client.SendAsync(request);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
