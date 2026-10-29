// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Api;
using SplitPlay.Service.Hosting;
using SplitPlay.Service.Platform;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Tests;

/// <summary>The security review's cases: caller rules, peer lookup, desk sessions, login throttling, the admin pipe.</summary>
public sealed class SecurityTests
{
    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static string PipeNamespace(TestService svc) =>
        svc.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SupervisorOptions>>().Value.PipeNamespace;

    // ------------------------------------------------------------------ admin caller rules

    private static AdminCallerFacts Facts(bool system = false, bool enabled = false, bool filtered = false, int integrity = 0x2000,
        int session = 1, int? console = 1, bool desk = false) =>
        new("user", system, enabled, filtered, integrity, session, console, desk);

    [Theory]
    [InlineData(true, false, false, 0x4000, 0, 1, false, true)]   // SYSTEM
    [InlineData(false, true, false, 0x3000, 3, 1, false, true)]   // elevated admin in another session (SSH, elevated prompt)
    [InlineData(false, false, true, 0x2000, 1, 1, false, true)]   // UAC-filtered admin at the console (tray, CLI)
    [InlineData(false, false, true, 0x2000, 3, 1, false, false)]  // UAC-filtered admin in an RDP session
    [InlineData(false, false, true, 0x2000, 1, null, false, false)] // no console session
    [InlineData(false, false, false, 0x2000, 1, 1, false, false)] // standard user at the console
    [InlineData(false, false, true, 0x1000, 1, 1, false, false)]  // low integrity process of the owner
    [InlineData(false, true, false, -1, 3, 1, false, false)]      // unknown integrity
    [InlineData(false, true, false, 0x3000, 5, 1, true, false)]   // elevated admin, but in a desk session
    [InlineData(true, false, false, 0x4000, 5, 1, true, false)]   // even SYSTEM in a desk session
    public void Admin_caller_rules(bool system, bool enabled, bool filtered, int integrity, int session, int? console, bool desk, bool allowed)
    {
        var decision = AdminCallerRules.Decide(Facts(system, enabled, filtered, integrity, session, console, desk));
        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(session, decision.Session);
        if (!allowed)
        {
            Assert.False(string.IsNullOrEmpty(decision.Reason));
        }
    }

    // ------------------------------------------------------------------ TCP owner table

    private static byte[] TableV4(params (byte[] Local, int LocalPort, int RemotePort, uint Pid)[] rows)
    {
        var table = new byte[4 + (rows.Length * TcpOwnerTable.RowSizeV4)];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = table.AsSpan(4 + (i * TcpOwnerTable.RowSizeV4));
            BinaryPrimitives.WriteUInt32LittleEndian(row, 5); // ESTABLISHED
            rows[i].Local.CopyTo(row[4..]);
            BinaryPrimitives.WriteUInt16BigEndian(row[8..], (ushort)rows[i].LocalPort);
            new byte[] { 127, 0, 0, 1 }.CopyTo(row[12..]);
            BinaryPrimitives.WriteUInt16BigEndian(row[16..], (ushort)rows[i].RemotePort);
            BinaryPrimitives.WriteUInt32LittleEndian(row[20..], rows[i].Pid);
        }

        return table;
    }

    private static byte[] TableV6(byte[] local, int localPort, int remotePort, uint pid)
    {
        var table = new byte[4 + TcpOwnerTable.RowSizeV6];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
        var row = table.AsSpan(4);
        local.CopyTo(row);
        BinaryPrimitives.WriteUInt16BigEndian(row[20..], (ushort)localPort);
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(row[24..]);
        BinaryPrimitives.WriteUInt16BigEndian(row[44..], (ushort)remotePort);
        BinaryPrimitives.WriteUInt32LittleEndian(row[48..], 5);
        BinaryPrimitives.WriteUInt32LittleEndian(row[52..], pid);
        return table;
    }

    [Fact]
    public void Tcp_table_rows_are_matched_by_client_address_and_both_ports_in_network_order()
    {
        var loopback = IPAddress.Loopback.GetAddressBytes();
        var table = TableV4(
            (loopback, 38299, 50000, 11),           // the server's own end: local port 38299
            (loopback, 50001, 38299, 22),           // another client
            (loopback, 50000, 38299, 33));          // ours
        Assert.Equal(33u, TcpOwnerTable.FindOwner(table, IPAddress.Loopback, 50000, 38299));
        Assert.Equal(33u, TcpOwnerTable.FindOwner(table, IPAddress.Loopback.MapToIPv6(), 50000, 38299));
        Assert.Null(TcpOwnerTable.FindOwner(table, IPAddress.Loopback, 50002, 38299));
        Assert.Null(TcpOwnerTable.FindOwner(table, IPAddress.Parse("127.0.0.2"), 50000, 38299));

        // Port byte order: 0x01 0x02 is 258, not 513.
        Assert.Equal(258, TcpOwnerTable.Port([1, 2, 0, 0]));

        var v6 = TableV6(IPAddress.IPv6Loopback.GetAddressBytes(), 51000, 38299, 44);
        Assert.Equal(44u, TcpOwnerTable.FindOwner(v6, IPAddress.IPv6Loopback, 51000, 38299));
        Assert.Null(TcpOwnerTable.FindOwner(v6, IPAddress.IPv6Loopback, 51000, 1));
    }

    [Fact]
    public void A_malformed_tcp_table_finds_nobody()
    {
        Assert.Null(TcpOwnerTable.FindOwner([], IPAddress.Loopback, 1, 2));
        var table = TableV4((IPAddress.Loopback.GetAddressBytes(), 1, 2, 3));
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1000); // more rows than bytes
        Assert.Null(TcpOwnerTable.FindOwner(table, IPAddress.Loopback, 1, 2));
    }

    [Fact]
    public async Task A_loopback_peer_that_cannot_be_identified_is_refused_unless_it_holds_a_pipe_credential()
    {
        await using var svc = await TestService.StartAsync();
        svc.App.Services.GetRequiredService<FakePeerInspector>().Result = PeerLookup.Failed;
        using var anonymous = svc.AnonymousClient();
        var refused = await anonymous.GetAsync("/api/v1/auth/session");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1/health")).StatusCode);
        // The CLI's bearer token comes from the admin pipe: still served.
        Assert.Equal(HttpStatusCode.OK, (await svc.Client.GetAsync("/api/v1/desks")).StatusCode);
    }

    // ------------------------------------------------------------------ desk sessions

    [Fact]
    public async Task Desk_sessions_are_told_by_their_user_without_a_seat()
    {
        await using var svc = await TestService.StartAsync(platform: p =>
        {
            p.AddManagedAccount("sp-orphan");
            p.AddExistingAccount("gamer", "Gamer-Pass-1");
            p.AddSession(1, "owner", disconnected: false);
            p.AddSession(4, "splitplay-desk-1");
            p.AddSession(5, "sp-orphan", disconnected: false); // active: the start-up clean-up leaves it alone
            p.AddSession(6, "gamer");
            p.AddSession(7, "visitor");
            p.AddSession(8, null);
        });
        await svc.CreateDeskAsync("desk-1", ""","user":"splitplay-desk-1" """);
        var desks = svc.App.Services.GetRequiredService<DeskSupervisor>();

        Assert.False(desks.IsDeskSession(0));
        Assert.False(desks.IsDeskSession(1));   // the console
        Assert.True(desks.IsDeskSession(4));    // a configured desk user, desk stopped (no seat)
        Assert.True(desks.IsDeskSession(5));    // an account SplitPlay created, no desk
        Assert.False(desks.IsDeskSession(6));   // someone else's account
        Assert.False(desks.IsDeskSession(7));
        Assert.False(desks.IsDeskSession(8));   // nobody signed in

        // Who is signed in cannot be read: deny, but the console stays the owner's.
        svc.Platform.SessionQueryFails = true;
        Assert.True(desks.IsDeskSession(7));
        Assert.False(desks.IsDeskSession(1));

        // And the Web API refuses such a peer, token or not.
        svc.Platform.SessionQueryFails = false;
        svc.App.Services.GetRequiredService<FakePeerInspector>().Session = 5;
        Assert.Equal(HttpStatusCode.Forbidden, (await svc.Client.GetAsync("/api/v1/desks")).StatusCode);
    }

    // ------------------------------------------------------------------ login throttling

    [Fact]
    public void Throttle_locks_a_caller_out_with_a_growing_delay_and_forgets_on_success()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var throttle = new LoginThrottle(time);
        for (var i = 0; i < LoginThrottle.FreeFailures - 1; i++)
        {
            throttle.Failed("a");
            Assert.Null(throttle.RetryAfter("a"));
        }

        throttle.Failed("a");
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.RetryAfter("a"));
        Assert.Null(throttle.RetryAfter("b"));
        time.Advance(TimeSpan.FromSeconds(1));
        throttle.Failed("a");
        Assert.Equal(TimeSpan.FromSeconds(2), throttle.RetryAfter("a"));
        time.Advance(TimeSpan.FromSeconds(2));
        throttle.Succeeded("a");
        Assert.Null(throttle.RetryAfter("a"));

        // Many callers together lock everybody out.
        for (var i = 0; i <= LoginThrottle.GlobalFreeFailures; i++)
        {
            throttle.Failed($"peer-{i}");
        }

        Assert.NotNull(throttle.RetryAfter("someone-new"));
        time.Advance(LoginThrottle.MaxLockout);
        Assert.Null(throttle.RetryAfter("someone-new"));
    }

    [Fact]
    public async Task Wrong_passwords_get_429_with_retry_after_even_for_the_right_one()
    {
        await using var svc = await TestService.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await svc.Client.PostAsync("/api/v1/auth/password", JsonBody("""{"newPassword":"correct horse"}"""))).StatusCode);
        using var browser = svc.AnonymousClient();
        for (var i = 0; i < LoginThrottle.FreeFailures; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/v1/auth/login", JsonBody("""{"password":"wrong pass"}"""))).StatusCode);
        }

        var locked = await browser.PostAsync("/api/v1/auth/login", JsonBody("""{"password":"correct horse"}"""));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter is not null);
        var error = (await locked.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.ApiErrorResponse))!.Error;
        Assert.Equal(ApiErrorCodes.TooManyAttempts, error.Code);

        // Changing the password is throttled the same way.
        var change = await svc.Client.PostAsync("/api/v1/auth/password", JsonBody("""{"newPassword":"battery staple","currentPassword":"correct horse"}"""));
        Assert.Equal(HttpStatusCode.TooManyRequests, change.StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(1.2));
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/api/v1/auth/login", JsonBody("""{"password":"correct horse"}"""))).StatusCode);
    }

    // ------------------------------------------------------------------ admin pipe

    [Fact]
    public async Task Pipe_callers_refused_at_connect_are_dropped_before_they_write()
    {
        await using var svc = await TestService.StartAsync();
        var server = svc.App.Services.GetRequiredService<AdminPipeServer>();
        await server.Listening;
        svc.App.Services.GetRequiredService<FakeAdminCallerCheck>().RefuseAtConnect = "callers in a desk session cannot manage SplitPlay";
        var ns = PipeNamespace(svc);

        // A client that never writes still gets its refusal and the pipe closes at once.
        await using var idle = new NamedPipeClientStream(".", AdminPipe.LocalName(ns), PipeDirection.InOut, PipeOptions.Asynchronous);
        await idle.ConnectAsync(5000);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var line = await AdminPipe.ReadLineAsync(idle, cts.Token);
        Assert.Contains("desk session", line, StringComparison.Ordinal);
        Assert.True(server.Dropped >= 1);

        // A real client writes its request while the refusal is being written: it must still get the refusal, and
        // well before the connection deadline (on Windows both writes used to wait on each other until it fired).
        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<AdminPipeException>(() => AdminPipe.RequestAsync(AdminPipe.Kinds.Bearer, "test", TimeSpan.FromSeconds(5), ns));
        Assert.Contains("desk session", ex.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < AdminPipeServer.RequestTimeout, "the refusal waited for the connection deadline");
    }

    [Fact]
    public async Task A_silent_pipe_client_is_dropped_after_the_request_timeout_and_does_not_block_others()
    {
        await using var svc = await TestService.StartAsync();
        var server = svc.App.Services.GetRequiredService<AdminPipeServer>();
        await server.Listening;
        var ns = PipeNamespace(svc);

        // More silent clients than one session may keep open; the admin from another session would still get through,
        // and once they time out the same session is served again.
        var silent = new List<NamedPipeClientStream>();
        for (var i = 0; i < AdminPipeServer.MaxConnectionsPerSession + 2; i++)
        {
            var c = new NamedPipeClientStream(".", AdminPipe.LocalName(ns), PipeDirection.InOut, PipeOptions.Asynchronous);
            await c.ConnectAsync(5000);
            silent.Add(c);
        }

        var started = DateTime.UtcNow;
        using (var cts = new CancellationTokenSource(AdminPipeServer.RequestTimeout + TimeSpan.FromSeconds(5)))
        {
            // The first ones are closed by the timeout (end of stream), the extra ones are refused at once.
            foreach (var c in silent)
            {
                await AdminPipe.ReadLineAsync(c, cts.Token);
            }
        }

        Assert.True(DateTime.UtcNow - started < AdminPipeServer.RequestTimeout + TimeSpan.FromSeconds(4));
        foreach (var c in silent)
        {
            await c.DisposeAsync();
        }

        var token = await AdminPipe.RequestAsync(AdminPipe.Kinds.Bearer, "test", TimeSpan.FromSeconds(5), ns);
        Assert.NotNull(token);
    }

    [Fact]
    public async Task Too_many_pipe_requests_from_one_session_are_refused()
    {
        await using var svc = await TestService.StartAsync();
        await svc.App.Services.GetRequiredService<AdminPipeServer>().Listening;
        var ns = PipeNamespace(svc);
        for (var i = 0; i < AdminPipeServer.MaxRequestsPerSession; i++)
        {
            Assert.NotNull(await AdminPipe.RequestAsync(AdminPipe.Kinds.Bearer, "test", TimeSpan.FromSeconds(5), ns));
        }

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<AdminPipeException>(() => AdminPipe.RequestAsync(AdminPipe.Kinds.Bearer, "test", TimeSpan.FromSeconds(5), ns));
        Assert.Contains("too many", ex.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < AdminPipeServer.RequestTimeout, "the refusal waited for the connection deadline");
    }

    [Fact]
    public async Task Stopping_the_service_does_not_wait_for_pipe_clients()
    {
        var svc = await TestService.StartAsync();
        await svc.App.Services.GetRequiredService<AdminPipeServer>().Listening;
        await using var idle = new NamedPipeClientStream(".", AdminPipe.LocalName(PipeNamespace(svc)), PipeDirection.InOut, PipeOptions.Asynchronous);
        await idle.ConnectAsync(5000);
        var started = DateTime.UtcNow;
        await svc.DisposeAsync();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    // ------------------------------------------------------------------ hard rules

    [Fact]
    public async Task Switching_to_an_account_SplitPlay_did_not_create_ignores_a_sent_back_hide_flag()
    {
        await using var svc = await TestService.StartAsync();
        svc.Platform.AddExistingAccount("gamer", "Gamer-Pass-1", passwordNeverExpires: true);
        var created = await svc.CreateDeskAsync("desk-1");
        Assert.True(created.HideFromLogon);
        var put = await svc.Client.PutAsync("/api/v1/desks/desk-1", JsonBody("""{"user":"gamer","password":"Gamer-Pass-1","hideFromLogon":true}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False((await svc.GetDeskAsync("desk-1")).HideFromLogon);
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Null(svc.Platform.UserListValue("gamer"));
    }

    [Fact]
    public void Uninstall_never_deletes_an_account_a_desk_uses_as_the_owner_s_own()
    {
        var root = TestService.NewDataRoot();
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureCreated();
            var platform = new FakeDeskPlatform(paths);
            // Created by SplitPlay once, now picked as the owner's own account for a desk.
            platform.AddManagedAccount("sp-adopted");
            platform.AddSession(9, "sp-adopted");
            var config = new SplitPlayConfig();
            config.Desks.Add(new DeskConfig { Id = "desk-1", User = "sp-adopted", UserManaged = false });
            var report = UninstallCleanup.Run(platform, paths, config, deleteAccounts: true, NullLogger.Instance);
            Assert.Empty(report.Failed);
            Assert.True(platform.Accounts.ContainsKey("sp-adopted"));
            Assert.DoesNotContain("logoff 9", platform.Actions);
        }
        finally
        {
            TestService.TryDelete(root);
        }
    }

    [Theory]
    [InlineData("global_prep_cmd")]
    [InlineData("file_apps")]
    [InlineData("credentials_file")]
    [InlineData("pkey")]
    [InlineData("cert")]
    [InlineData("log_path")]
    [InlineData("file_state")]
    [InlineData("origin_web_ui_allowed")]
    [InlineData("something_cmd")]
    public async Task Worker_settings_that_run_commands_or_name_files_are_refused(string name)
    {
        await using var svc = await TestService.StartAsync();
        var response = await svc.Client.PostAsync("/api/v1/desks", JsonBody($$$"""{"id":"desk-1","workerSettings":{"{{{name}}}":"x"}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.ApiErrorResponse))!.Error;
        Assert.Contains(name, error.Message, StringComparison.Ordinal);

        // A hand-edited config.json never reaches the worker's command line with them.
        var desk = new DeskConfig { Id = "d", WorkerSettings = new() { [name] = "x", ["min_log_level"] = "warning" } };
        var settings = DeskSupervisor.WorkerSettings("d", desk).ToDictionary();
        Assert.False(settings.ContainsKey(name));
        Assert.Equal("warning", settings["min_log_level"]);
    }
}
