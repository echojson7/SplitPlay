// SPDX-License-Identifier: MIT
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SplitPlay.Core.Api;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;

namespace SplitPlay.Service.Tests;

public sealed class ApiTests
{
    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<ApiError> ReadErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.ApiErrorResponse))!.Error;

    [Fact]
    public async Task Health_reports_ok_and_version()
    {
        await using var svc = await TestService.StartAsync();
        var health = await svc.Client.GetFromJsonAsync("/api/v1/health", SplitPlayJsonContext.Default.HealthDto);
        Assert.Equal("ok", health!.Status);
        Assert.Equal("0.1.0", health.Version);
        Assert.True(health.UptimeSeconds >= 0);
    }

    [Fact]
    public async Task Auth_setup_login_logout_and_csrf()
    {
        await using var svc = await TestService.StartAsync();

        var session = await svc.Client.GetFromJsonAsync("/api/v1/auth/session", SplitPlayJsonContext.Default.AuthSessionDto);
        Assert.True(session!.SetupRequired);
        Assert.Null(session.CsrfToken);
        Assert.Equal(HttpStatusCode.Conflict, (await svc.Client.PostAsync("/api/v1/auth/login", JsonBody("""{"password":"whatever1"}"""))).StatusCode);

        // Too short.
        Assert.Equal(HttpStatusCode.BadRequest, (await svc.Client.PostAsync("/api/v1/auth/password", JsonBody("""{"newPassword":"short"}"""))).StatusCode);

        // First password: no currentPassword, and the browser is logged in.
        var setup = await svc.Client.PostAsync("/api/v1/auth/password", JsonBody("""{"newPassword":"correct horse"}"""));
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var csrf = (await setup.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.LoginResponse))!.CsrfToken;
        var cookie = Assert.Single(setup.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        var cookieHeader = cookie.Split(';')[0];
        Assert.True(File.Exists(Path.Combine(svc.DataRoot, "secrets", "admin-password.json")));

        using (var withCookie = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session"))
        {
            withCookie.Headers.Add("Cookie", cookieHeader);
            var state = await (await svc.Client.SendAsync(withCookie)).Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.AuthSessionDto);
            Assert.False(state!.SetupRequired);
            Assert.True(state.Authenticated);
            Assert.Equal(csrf, state.CsrfToken);
        }

        // A cookie-carrying state change without the CSRF header is refused; with it, it passes.
        using (var noCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/v1/desks") { Content = JsonBody("""{"id":"desk-1"}""") })
        {
            noCsrf.Headers.Add("Cookie", cookieHeader);
            var refused = await svc.Client.SendAsync(noCsrf);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(ApiErrorCodes.CsrfFailed, (await ReadErrorAsync(refused)).Code);
        }

        using (var okCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/v1/desks") { Content = JsonBody("""{"id":"desk-1"}""") })
        {
            okCsrf.Headers.Add("Cookie", cookieHeader);
            okCsrf.Headers.Add("X-CSRF-Token", csrf);
            Assert.Equal(HttpStatusCode.Created, (await svc.Client.SendAsync(okCsrf)).StatusCode);
        }

        // Without a session the browser is not authenticated any more (loopback callers are still served in Stage 2).
        var anonymous = await svc.Client.GetFromJsonAsync("/api/v1/auth/session", SplitPlayJsonContext.Default.AuthSessionDto);
        Assert.False(anonymous!.SetupRequired);
        Assert.False(anonymous.Authenticated);

        Assert.Equal(HttpStatusCode.Unauthorized, (await svc.Client.PostAsync("/api/v1/auth/login", JsonBody("""{"password":"wrong pass"}"""))).StatusCode);
        var login = await svc.Client.PostAsync("/api/v1/auth/login", JsonBody("""{"password":"correct horse"}"""));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.NotEmpty((await login.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.LoginResponse))!.CsrfToken);

        // Changing the password needs the current one.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await svc.Client.PostAsync("/api/v1/auth/password", JsonBody("""{"newPassword":"new password"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await svc.Client.PostAsync("/api/v1/auth/password", JsonBody("""{"newPassword":"new password","currentPassword":"correct horse"}"""))).StatusCode);

        // The old session was ended by the password change.
        using (var stale = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session"))
        {
            stale.Headers.Add("Cookie", cookieHeader);
            var state = await (await svc.Client.SendAsync(stale)).Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.AuthSessionDto);
            Assert.False(state!.Authenticated);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await svc.Client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task Host_settings_can_be_read_and_replaced()
    {
        await using var svc = await TestService.StartAsync();
        var host = await svc.Client.GetFromJsonAsync("/api/v1/host", SplitPlayJsonContext.Default.HostConfig);
        Assert.Equal(38299, host!.WebPort);
        Assert.False(host.LanAccess);

        var put = await svc.Client.PutAsync("/api/v1/host",
            JsonBody("""{"webPort":38299,"lanAccess":true,"basePortStart":50089,"basePortStride":100,"composeIntervalMs":2,"logLevel":"debug"}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = (await put.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.HostConfig))!;
        Assert.True(saved.LanAccess);
        Assert.Equal(2, saved.ComposeIntervalMs);
        Assert.Equal(50089, svc.ReadConfigFile().Host.BasePortStart);
        Assert.Equal(50089, (await svc.CreateDeskAsync("desk-1")).BasePort);

        var logs = await svc.Client.GetStringAsync("/api/v1/logs?tail=50");
        Assert.Contains("SplitPlay Web UI", logs, StringComparison.Ordinal);

        var bad = await svc.Client.PutAsync("/api/v1/host", JsonBody("""{"webPort":0,"logLevel":"info"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, (await ReadErrorAsync(bad)).Code);
    }

    [Fact]
    public async Task Desk_crud_round_trip()
    {
        await using var svc = await TestService.StartAsync();

        var created = await svc.CreateDeskAsync("desk-1", ""","name":"Skyrim box","profile":{"fps":120,"codec":"hevc"}""");
        Assert.Equal(DeskState.Defined, created.State);
        Assert.Equal(48089, created.BasePort);
        Assert.Equal("splitplay-desk1", created.User);
        Assert.Equal("Skyrim box", created.Name);
        Assert.Null(created.SessionId);
        Assert.Null(created.LastError);

        var second = await svc.CreateDeskAsync("gaming");
        Assert.Equal(48189, second.BasePort);

        var list = await svc.Client.GetFromJsonAsync("/api/v1/desks", SplitPlayJsonContext.Default.ListDeskDto);
        Assert.Equal(["desk-1", "gaming"], list!.Select(d => d.Id));

        // The desk object uses the field names from web-api.md.
        var raw = await svc.Client.GetStringAsync("/api/v1/desks/desk-1");
        using (var doc = JsonDocument.Parse(raw))
        {
            foreach (var field in new[] { "id", "name", "user", "state", "sessionId", "basePort", "autostart", "gpu", "profile", "metrics", "lastError" })
            {
                Assert.True(doc.RootElement.TryGetProperty(field, out _), $"missing {field}");
            }

            Assert.Equal("Defined", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal(120, doc.RootElement.GetProperty("profile").GetProperty("fps").GetInt32());
        }

        var put = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"name":"Renamed","autostart":true,"profile":{"fps":240}}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = (await put.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.DeskDto))!;
        Assert.Equal("Renamed", updated.Name);
        Assert.True(updated.Autostart);
        Assert.Equal(240, updated.Profile.Fps);
        Assert.Equal(48089, updated.BasePort);

        var onDisk = svc.ReadConfigFile();
        Assert.Equal(2, onDisk.Desks.Count);
        Assert.Equal("Renamed", onDisk.Desks[0].Name);

        Assert.Equal(HttpStatusCode.NoContent, (await svc.Client.DeleteAsync("/api/v1/desks/gaming")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await svc.Client.GetAsync("/api/v1/desks/gaming")).StatusCode);
        Assert.Single(svc.ReadConfigFile().Desks);
    }

    [Fact]
    public async Task Create_rejects_duplicates_and_invalid_desks()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");

        var duplicate = await svc.Client.PostAsync("/api/v1/desks", JsonBody("""{"id":"desk-1"}"""));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(ApiErrorCodes.AlreadyExists, (await ReadErrorAsync(duplicate)).Code);

        var badId = await svc.Client.PostAsync("/api/v1/desks", JsonBody("""{"id":"Bad Id"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, (await ReadErrorAsync(badId)).Code);

        var badFps = await svc.Client.PostAsync("/api/v1/desks", JsonBody("""{"id":"desk-2","profile":{"fps":900}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, badFps.StatusCode);
        Assert.Contains("fps", (await ReadErrorAsync(badFps)).Message, StringComparison.Ordinal);

        var overlap = await svc.Client.PostAsync("/api/v1/desks", JsonBody("""{"id":"desk-3","basePort":48100}"""));
        Assert.Equal(HttpStatusCode.BadRequest, overlap.StatusCode);

        var badJson = await svc.Client.PostAsync("/api/v1/desks", JsonBody("{nope"));
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
        Assert.Equal(ApiErrorCodes.BadRequest, (await ReadErrorAsync(badJson)).Code);

        Assert.Single(svc.ReadConfigFile().Desks);
    }

    [Fact]
    public async Task Unknown_desk_and_endpoint_return_json_404()
    {
        await using var svc = await TestService.StartAsync();
        foreach (var path in new[] { "/api/v1/desks/nope", "/api/v1/nothing-here", "/api/v2/desks" })
        {
            var response = await svc.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ApiErrorCodes.NotFound, (await ReadErrorAsync(response)).Code);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await svc.Client.PostAsync("/api/v1/desks/nope/start", null)).StatusCode);
    }

    [Fact]
    public async Task Start_and_stop_drive_the_lifecycle()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1", ""","profile":{"fps":120}""");

        var accepted = await svc.PostDeskActionAsync("desk-1", "start");
        Assert.NotEqual(DeskState.Defined, accepted.State);

        var ready = await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Null(ready.LastError);

        // An idle worker: no stream, zero metrics (key mapping and pass-through: LiveStatsTests).
        Assert.Null(ready.Stream);
        Assert.Equal(0, ready.Metrics.Fps);
        Assert.Equal(0, ready.Metrics.Clients);

        // Starting twice is a conflict.
        var again = await svc.Client.PostAsync("/api/v1/desks/desk-1/start", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidState, (await ReadErrorAsync(again)).Code);

        // A running desk can't be deleted, and only its profile can change.
        Assert.Equal(HttpStatusCode.Conflict, (await svc.Client.DeleteAsync("/api/v1/desks/desk-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"name":"Other","profile":{"fps":60}}"""))).StatusCode);
        var profileOnly = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"profile":{"fps":60}}"""));
        Assert.Equal(HttpStatusCode.OK, profileOnly.StatusCode);
        Assert.Equal(DeskState.Ready, (await svc.GetDeskAsync("desk-1")).State);

        await svc.PostDeskActionAsync("desk-1", "stop");
        var stopped = await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.Null(stopped.SessionId);

        // Stopping a stopped desk is a no-op.
        Assert.Equal(DeskState.Stopped, (await svc.PostDeskActionAsync("desk-1", "stop")).State);
        Assert.Equal(1, svc.Launcher.LaunchesOf(IpcProtocol.Roles.Worker));
        Assert.Equal(1, svc.Launcher.LaunchesOf(IpcProtocol.Roles.Seat));
    }

    [Fact]
    public async Task Restart_relaunches_the_child()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        await svc.PostDeskActionAsync("desk-1", "restart");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Equal(2, svc.Launcher.LaunchesOf(IpcProtocol.Roles.Worker));
        Assert.Equal(2, svc.Launcher.LaunchesOf(IpcProtocol.Roles.Seat));
    }

    [Fact]
    public async Task Pairing_requests_are_forwarded_to_the_worker()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");

        var notRunning = await svc.Client.GetAsync("/api/v1/desks/desk-1/clients");
        Assert.Equal(HttpStatusCode.Conflict, notRunning.StatusCode);
        Assert.Equal(ApiErrorCodes.ChildUnavailable, (await ReadErrorAsync(notRunning)).Code);

        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        var clients = await svc.Client.GetFromJsonAsync("/api/v1/desks/desk-1/clients", SplitPlayJsonContext.Default.ListClientDto);
        Assert.Empty(clients!);

        var badPin = await svc.Client.PostAsync("/api/v1/desks/desk-1/pair", JsonBody("""{"pin":"12","name":"Laptop"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, badPin.StatusCode);

        var pair = await svc.Client.PostAsync("/api/v1/desks/desk-1/pair", JsonBody("""{"pin":"1234","name":"Laptop"}"""));
        Assert.Equal(HttpStatusCode.NoContent, pair.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await svc.Client.DeleteAsync("/api/v1/desks/desk-1/clients/abc")).StatusCode);
    }

    [Fact]
    public async Task Metrics_and_logs_are_served()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        var metrics = await svc.Client.GetFromJsonAsync("/api/v1/metrics", SplitPlayJsonContext.Default.MetricsDto);
        Assert.Equal(250, metrics!.Host.ComposeHz);
        Assert.NotNull(metrics.Host.Gpus);
        var deskMetrics = Assert.Single(metrics.Desks);
        Assert.Equal("desk-1", deskMetrics.Id);
        Assert.Equal(0, deskMetrics.Fps);

        var raw = await svc.Client.GetStringAsync("/api/v1/metrics");
        Assert.Contains("\"gpus\":[", raw, StringComparison.Ordinal);
        Assert.Contains("\"encodeMsP99\":", raw, StringComparison.Ordinal);

        var logs = await svc.Client.GetFromJsonAsync("/api/v1/logs?component=service&desk=desk-1&tail=50", SplitPlayJsonContext.Default.ListLogLineDto);
        Assert.NotEmpty(logs!);
        Assert.All(logs!, l => Assert.Equal("desk-1", l.Desk));
        Assert.Contains(logs!, l => l.Message.Contains("-> Ready", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(svc.DataRoot, "logs", "service.log")));
    }

    [Fact]
    public async Task Config_export_and_import_round_trip()
    {
        byte[] zip;
        await using (var source = await TestService.StartAsync())
        {
            await source.CreateDeskAsync("desk-1", ""","name":"One","profile":{"fps":144}""");
            await source.CreateDeskAsync("desk-2");
            var export = await source.Client.GetAsync("/api/v1/config/export");
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            Assert.Equal("application/zip", export.Content.Headers.ContentType!.MediaType);
            zip = await export.Content.ReadAsByteArrayAsync();

            Assert.Equal(HttpStatusCode.NotImplemented, (await source.Client.GetAsync("/api/v1/config/export?includeSecrets=true&password=x")).StatusCode);
        }

        using (var archive = new ZipArchive(new MemoryStream(zip)))
        {
            Assert.Equal(["config.json"], archive.Entries.Select(e => e.FullName));
        }

        await using var target = await TestService.StartAsync();
        await target.CreateDeskAsync("old");
        var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        var import = await target.Client.PostAsync("/api/v1/config/import", content);
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var desks = (await import.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.ListDeskDto))!;
        Assert.Equal(["desk-1", "desk-2"], desks.Select(d => d.Id));
        Assert.Equal(144, desks[0].Profile.Fps);
        Assert.Equal(HttpStatusCode.NotFound, (await target.Client.GetAsync("/api/v1/desks/old")).StatusCode);
        Assert.Equal(2, target.ReadConfigFile().Desks.Count);

        var garbage = await target.Client.PostAsync("/api/v1/config/import", new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    [Fact]
    public async Task Import_is_refused_while_a_desk_runs()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        var zip = await svc.Client.GetByteArrayAsync("/api/v1/config/export");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var import = await svc.Client.PostAsync("/api/v1/config/import", new ByteArrayContent(zip));
        Assert.Equal(HttpStatusCode.Conflict, import.StatusCode);
    }

    [Fact]
    public async Task Static_files_are_served_from_the_web_root_without_fallback()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "splitplay-www-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>SplitPlay test</title>");
        try
        {
            await using var svc = await TestService.StartAsync(webRoot: webRoot);
            Assert.Contains("SplitPlay test", await svc.Client.GetStringAsync("/"), StringComparison.Ordinal);
            // Hash routing: no server-side fallback, unknown paths are plain 404s.
            Assert.Equal(HttpStatusCode.NotFound, (await svc.Client.GetAsync("/desks/desk-1")).StatusCode);
        }
        finally
        {
            TestService.TryDelete(webRoot);
        }
    }

    [Fact]
    public async Task Placeholder_index_ships_with_the_service()
    {
        var placeholder = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        Assert.True(File.Exists(placeholder), placeholder);
        await using var svc = await TestService.StartAsync(webRoot: Path.GetDirectoryName(placeholder));
        Assert.Contains("<title>SplitPlay</title>", await svc.Client.GetStringAsync("/"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSocket_pushes_desk_events()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");

        var ws = svc.Server.CreateWebSocketClient();
        using var socket = await ws.ConnectAsync(new Uri(svc.Server.BaseAddress, "/api/v1/ws"), CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // The snapshot arrives first.
        Assert.Contains("\"type\":\"desk\"", await ReceiveTextAsync(socket, cts.Token), StringComparison.Ordinal);

        await svc.PostDeskActionAsync("desk-1", "start");
        while (true)
        {
            var text = await ReceiveTextAsync(socket, cts.Token);
            if (text.Contains("\"state\":\"Ready\"", StringComparison.Ordinal))
            {
                Assert.StartsWith("{\"type\":\"desk\"", text, StringComparison.Ordinal);
                break;
            }
        }

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var builder = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        return builder.ToString();
    }
}
