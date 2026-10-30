// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Api;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Api;

namespace SplitPlay.Service.Tests;

/// <summary>Who may use the Web API (web-api.md "Authentication").</summary>
public sealed class AuthTests
{
    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<ApiError> ReadErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.ApiErrorResponse))!.Error;

    private static string PipeNamespace(TestService svc) =>
        svc.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Supervision.SupervisorOptions>>().Value.PipeNamespace;

    [Fact]
    public async Task Anonymous_local_callers_get_health_and_sign_in_only()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        using var anonymous = svc.AnonymousClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1/health")).StatusCode);
        var session = await anonymous.GetFromJsonAsync("/api/v1/auth/session", SplitPlayJsonContext.Default.AuthSessionDto);
        Assert.True(session!.SetupRequired);
        Assert.False(session.Authenticated);

        // Reads and state changes alike: another local process (a desk user's) controls nothing.
        foreach (var (method, path) in new[]
                 {
                     ("GET", "/api/v1/desks"), ("GET", "/api/v1/logs"), ("GET", "/api/v1/config/export"), ("GET", "/api/v1/host/users"),
                     ("GET", "/api/v1/desks/desk-1"), ("POST", "/api/v1/desks/desk-1/start"), ("DELETE", "/api/v1/desks/desk-1"),
                     ("PUT", "/api/v1/host"), ("POST", "/api/v1/host/services/TermService/stop"), ("GET", "/api/v1/nothing-here"),
                 })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "GET" ? null : JsonBody("{}") };
            var response = await anonymous.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(ApiErrorCodes.Unauthorized, (await ReadErrorAsync(response)).Code);
        }

        // A bearer token nobody issued is no better.
        using (var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/desks"))
        {
            forged.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new string('0', 64));
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(forged)).StatusCode);
        }

        // Nor can it set the first admin password and take the host over.
        var takeover = await anonymous.PostAsync("/api/v1/auth/password", JsonBody("""{"newPassword":"correct horse"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, takeover.StatusCode);
        Assert.False(File.Exists(Path.Combine(svc.DataRoot, "secrets", "admin-password.json")));
        Assert.Equal(DeskState.Defined, (await svc.GetDeskAsync("desk-1")).State);
    }

    [Fact]
    public async Task Admin_sets_the_password_then_browsers_log_in_with_csrf()
    {
        await using var svc = await TestService.StartAsync();
        using var browser = svc.AnonymousClient();

        var admin = await svc.Client.GetFromJsonAsync("/api/v1/auth/session", SplitPlayJsonContext.Default.AuthSessionDto);
        Assert.True(admin!.SetupRequired);
        Assert.True(admin.Authenticated);
        Assert.Null(admin.CsrfToken);
        Assert.Equal(HttpStatusCode.Conflict, (await browser.PostAsync("/api/v1/auth/login", JsonBody("""{"password":"whatever1"}"""))).StatusCode);

        // Too short; then the first password from the admin (no currentPassword).
        Assert.Equal(HttpStatusCode.BadRequest, (await svc.Client.PostAsync("/api/v1/auth/password", JsonBody("""{"newPassword":"short"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await svc.Client.PostAsync("/api/v1/auth/password", JsonBody("""{"newPassword":"correct horse"}"""))).StatusCode);
        Assert.True(File.Exists(Path.Combine(svc.DataRoot, "secrets", "admin-password.json")));

        var anonymous = await browser.GetFromJsonAsync("/api/v1/auth/session", SplitPlayJsonContext.Default.AuthSessionDto);
        Assert.False(anonymous!.SetupRequired);
        Assert.False(anonymous.Authenticated);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/v1/auth/login", JsonBody("""{"password":"wrong pass"}"""))).StatusCode);
        var login = await browser.PostAsync("/api/v1/auth/login", JsonBody("""{"password":"correct horse"}"""));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var csrf = (await login.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.LoginResponse))!.CsrfToken;
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var cookieHeader = cookie.Split(';')[0];

        using (var withCookie = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session"))
        {
            withCookie.Headers.Add("Cookie", cookieHeader);
            var state = await (await browser.SendAsync(withCookie)).Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.AuthSessionDto);
            Assert.True(state!.Authenticated);
            Assert.Equal(csrf, state.CsrfToken);
        }

        using (var read = new HttpRequestMessage(HttpMethod.Get, "/api/v1/desks"))
        {
            read.Headers.Add("Cookie", cookieHeader);
            Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(read)).StatusCode);
        }

        // A cookie-carrying state change without the CSRF header is refused; with it, it passes.
        using (var noCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/v1/desks") { Content = JsonBody("""{"id":"desk-1"}""") })
        {
            noCsrf.Headers.Add("Cookie", cookieHeader);
            var refused = await browser.SendAsync(noCsrf);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(ApiErrorCodes.CsrfFailed, (await ReadErrorAsync(refused)).Code);
        }

        using (var okCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/v1/desks") { Content = JsonBody("""{"id":"desk-1"}""") })
        {
            okCsrf.Headers.Add("Cookie", cookieHeader);
            okCsrf.Headers.Add("X-CSRF-Token", csrf);
            Assert.Equal(HttpStatusCode.Created, (await browser.SendAsync(okCsrf)).StatusCode);
        }

        // Changing the password needs the current one, and ends the password sessions.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await browser.PostAsync("/api/v1/auth/password", JsonBody("""{"newPassword":"new password"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await browser.PostAsync("/api/v1/auth/password", JsonBody("""{"newPassword":"new password","currentPassword":"correct horse"}"""))).StatusCode);
        using (var stale = new HttpRequestMessage(HttpMethod.Get, "/api/v1/desks"))
        {
            stale.Headers.Add("Cookie", cookieHeader);
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(stale)).StatusCode);
        }

        // Bearer tokens (CLI) are not tied to the password.
        Assert.Equal(HttpStatusCode.OK, (await svc.Client.GetAsync("/api/v1/desks")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/api/v1/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task Admin_pipe_hands_out_bearer_tokens_and_tray_sessions()
    {
        await using var svc = await TestService.StartAsync();
        await svc.App.Services.GetRequiredService<AdminPipeServer>().Listening;
        var ns = PipeNamespace(svc);
        using var anonymous = svc.AnonymousClient();

        // CLI: a bearer token.
        var bearer = await AdminPipe.RequestAsync(AdminPipe.Kinds.Bearer, "test", TimeSpan.FromSeconds(10), ns);
        Assert.NotNull(bearer);
        Assert.Null(bearer.CsrfToken);
        Assert.InRange(bearer.ExpiresAt, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + AuthService.BearerLifetime + TimeSpan.FromMinutes(1));
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/desks") { Content = JsonBody("""{"id":"desk-1"}""") })
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer.Token);
            Assert.Equal(HttpStatusCode.Created, (await anonymous.SendAsync(request)).StatusCode);
        }

        // Tray: a session for its WebView's cookie, with the CSRF token the UI reads from /auth/session.
        var tray = await AdminPipe.RequestAsync(AdminPipe.Kinds.Session, "test", TimeSpan.FromSeconds(10), ns);
        Assert.NotNull(tray);
        Assert.NotNull(tray.CsrfToken);
        using (var state = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session"))
        {
            state.Headers.Add("Cookie", $"{AuthService.CookieName}={tray.Token}");
            var dto = await (await anonymous.SendAsync(state)).Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.AuthSessionDto);
            Assert.True(dto!.Authenticated);
            Assert.Equal(tray.CsrfToken, dto.CsrfToken);
        }

        using (var start = new HttpRequestMessage(HttpMethod.Post, "/api/v1/desks/desk-1/start"))
        {
            start.Headers.Add("Cookie", $"{AuthService.CookieName}={tray.Token}");
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.SendAsync(start)).StatusCode);
        }

        // Setting a password from the tray does not sign the tray out.
        using (var password = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/password") { Content = JsonBody("""{"newPassword":"correct horse"}""") })
        {
            password.Headers.Add("Cookie", $"{AuthService.CookieName}={tray.Token}");
            password.Headers.Add("X-CSRF-Token", tray.CsrfToken);
            Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(password)).StatusCode);
        }

        using (var stop = new HttpRequestMessage(HttpMethod.Post, "/api/v1/desks/desk-1/stop"))
        {
            stop.Headers.Add("Cookie", $"{AuthService.CookieName}={tray.Token}");
            stop.Headers.Add("X-CSRF-Token", tray.CsrfToken);
            Assert.Equal(HttpStatusCode.Accepted, (await anonymous.SendAsync(stop)).StatusCode);
        }

        // A caller the check refuses gets no token, and the reason.
        svc.App.Services.GetRequiredService<FakeAdminCallerCheck>().Refuse = "not the admin";
        var refused = await Assert.ThrowsAsync<AdminPipeException>(() =>
            AdminPipe.RequestAsync(AdminPipe.Kinds.Bearer, "test", TimeSpan.FromSeconds(10), ns));
        Assert.Equal("forbidden", refused.Code);
        Assert.Equal("not the admin", refused.Message);
        svc.App.Services.GetRequiredService<FakeAdminCallerCheck>().Refuse = null;

        // Unknown kinds are refused.
        var unknown = await Assert.ThrowsAsync<AdminPipeException>(() => AdminPipe.RequestAsync("root", "test", TimeSpan.FromSeconds(10), ns));
        Assert.Equal(ApiErrorCodes.BadRequest, unknown.Code);

        // No service, no pipe: null rather than a hang.
        Assert.Null(await AdminPipe.RequestAsync(AdminPipe.Kinds.Bearer, "test", TimeSpan.FromMilliseconds(300), "nobody-" + Guid.NewGuid().ToString("N")[..8] + "\\"));
    }

    [Fact]
    public async Task Callers_in_a_desk_session_are_refused_even_with_a_token()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        var desk = await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Equal(7, desk.SessionId);

        var peer = svc.App.Services.GetRequiredService<FakePeerInspector>();
        peer.Session = 7;
        Assert.Equal(HttpStatusCode.OK, (await svc.Client.GetAsync("/api/v1/health")).StatusCode);
        foreach (var response in new[]
                 {
                     await svc.Client.GetAsync("/api/v1/desks"),
                     await svc.Client.PostAsync("/api/v1/desks/desk-1/stop", null),
                     await svc.Client.PostAsync("/api/v1/auth/login", JsonBody("""{"password":"guess"}""")),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(ApiErrorCodes.Forbidden, (await ReadErrorAsync(response)).Code);
        }

        // The console (or any session that is not a desk's) is served.
        peer.Session = 1;
        Assert.Equal(HttpStatusCode.OK, (await svc.Client.GetAsync("/api/v1/desks")).StatusCode);
        peer.Session = null;
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);

        // Once the desk is stopped its old session id is nobody's.
        peer.Session = 7;
        Assert.Equal(HttpStatusCode.OK, (await svc.Client.GetAsync("/api/v1/desks")).StatusCode);
    }

    [Fact]
    public async Task Cross_site_websocket_is_refused()
    {
        await using var svc = await TestService.StartAsync();
        var ws = svc.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r =>
        {
            r.Headers.Authorization = "Bearer " + svc.Token;
            r.Headers.Origin = "https://evil.example";
        };
        await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri(svc.Server.BaseAddress, "/api/v1/ws"), CancellationToken.None));
    }
}
