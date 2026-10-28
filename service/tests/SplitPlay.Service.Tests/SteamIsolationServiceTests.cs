// SPDX-License-Identifier: MIT
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SplitPlay.Core.Host;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Core.Steam;

namespace SplitPlay.Service.Tests;

/// <summary>The desk option <c>steamIsolation</c> through the API, the fake platform and the desk's apps.json.</summary>
public sealed class SteamIsolationServiceTests
{
    private const string Env = "steam_master_ipc_name_override";
    private const string RunValue = "\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent";

    private const string Seat = IpcProtocol.Roles.Seat;

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task StartAndStopAsync(TestService svc, string id)
    {
        await svc.PostDeskActionAsync(id, "start");
        await svc.WaitForStateAsync(id, DeskState.Ready);
        await svc.PostDeskActionAsync(id, "stop");
        await svc.WaitForStateAsync(id, DeskState.Stopped);
    }

    [Fact]
    public async Task New_desk_user_gets_the_desk_IPC_name_at_first_logon_and_logs_on_again()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:RelogonAfterLayoutCopy=true"]);
        var created = await svc.CreateDeskAsync("desk-1");
        Assert.True(created.SteamIsolation);
        Assert.True(svc.ReadConfigFile().Desks[0].SteamIsolation);

        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var hive = svc.Platform.Hive(created.User!);
        Assert.Equal(new RegValue(RegKind.String, "SplitPlay_desk_1"), hive.Get("Environment", Env));
        Assert.Contains("-master_ipc_name_override SplitPlay_desk_1 -- \"%1\"", hive.Get(SteamIsolation.CommandKey, "")!.Data, StringComparison.Ordinal);
        Assert.Equal(2, svc.Launcher.LaunchesOf(Seat)); // one relogon for layouts and the environment together

        // The worker's app list offers the desk's own Steam.
        var apps = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(svc.DataRoot, "desks", "desk-1", SteamApps.FileName)))!["apps"]!.AsArray();
        Assert.Contains(apps, a => (string?)a!["name"] == "Steam"
                                   && ((string?)a["cmd"])!.EndsWith("-master_ipc_name_override SplitPlay_desk_1 -gamepadui", StringComparison.Ordinal));

        // Already applied: the next start logs on once.
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Equal(3, svc.Launcher.LaunchesOf(Seat));
    }

    [Fact]
    public async Task Existing_account_is_changed_before_logon_and_restored_when_the_desk_is_deleted()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:RelogonAfterLayoutCopy=true"]);
        svc.Platform.AddExistingAccount("gamer", "Gamer-Pass-1", passwordNeverExpires: true);
        var hive = svc.Platform.Hive("gamer");
        hive.Set(SteamIsolation.RunKey, "Steam", new RegValue(RegKind.String, RunValue));

        await svc.CreateDeskAsync("desk-1", ""","user":"gamer","password":"Gamer-Pass-1" """);
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        // Written while provisioning (profile mounted), so the first logon already has it: no relogon.
        Assert.Equal(1, svc.Launcher.LaunchesOf(Seat));
        Assert.Equal("SplitPlay_desk_1", hive.Get("Environment", Env)!.Data);
        Assert.Equal("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -master_ipc_name_override SplitPlay_desk_1 -silent",
            hive.Get(SteamIsolation.RunKey, "Steam")!.Data);
        var actions = svc.Platform.Actions.ToList();
        Assert.True(actions.IndexOf("steam+ desk-1 gamer") < actions.FindIndex(a => a.StartsWith("logon desk-1 ", StringComparison.Ordinal)));

        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.Equal(HttpStatusCode.NoContent, (await svc.Client.DeleteAsync("/api/v1/desks/desk-1")).StatusCode);
        Assert.Null(hive.Get("Environment", Env));
        Assert.Equal(RunValue, hive.Get(SteamIsolation.RunKey, "Steam")!.Data);
        Assert.Null(hive.Get(SteamIsolation.CommandKey, ""));
    }

    [Fact]
    public async Task Existing_account_without_a_profile_gets_it_at_first_logon()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:RelogonAfterLayoutCopy=true"]);
        svc.Platform.AddExistingAccount("gamer", "Gamer-Pass-1", passwordNeverExpires: true, hasProfile: false);
        await svc.CreateDeskAsync("desk-1", ""","user":"gamer","password":"Gamer-Pass-1" """);
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Equal("SplitPlay_desk_1", svc.Platform.Hive("gamer").Get("Environment", Env)!.Data);
        Assert.Equal(2, svc.Launcher.LaunchesOf(Seat)); // no layouts for someone else's account, but the environment needs a relogon
    }

    [Fact]
    public async Task Turning_the_option_off_restores_the_hive_and_the_app_list()
    {
        await using var svc = await TestService.StartAsync();
        var created = await svc.CreateDeskAsync("desk-1");
        await StartAndStopAsync(svc, "desk-1");
        var hive = svc.Platform.Hive(created.User!);
        Assert.NotNull(hive.Get("Environment", Env));

        // A PUT without the field keeps the current value (clients that do not know it).
        var kept = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"autostart":false}"""));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.True((await svc.GetDeskAsync("desk-1")).SteamIsolation);
        Assert.NotNull(hive.Get("Environment", Env));

        var off = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"steamIsolation":false}"""));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await svc.GetDeskAsync("desk-1")).SteamIsolation);
        Assert.False(svc.ReadConfigFile().Desks[0].SteamIsolation);
        Assert.Empty(hive.Values);
        Assert.Contains("steam- desk-1 " + created.User, svc.Platform.Actions);

        await StartAndStopAsync(svc, "desk-1");
        Assert.Empty(hive.Values);
        var apps = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(svc.DataRoot, "desks", "desk-1", SteamApps.FileName)))!["apps"]!.AsArray();
        Assert.DoesNotContain(apps, a => (string?)a!["name"] == "Steam");
    }

    [Fact]
    public async Task Option_cannot_change_while_the_desk_runs()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var response = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"steamIsolation":false}"""));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Desk_created_without_isolation_never_touches_the_hive()
    {
        await using var svc = await TestService.StartAsync();
        var created = await svc.CreateDeskAsync("desk-1", ""","steamIsolation":false""");
        Assert.False(created.SteamIsolation);
        await StartAndStopAsync(svc, "desk-1");
        Assert.Empty(svc.Platform.Hive(created.User!).Values);
        Assert.False(File.Exists(Path.Combine(svc.DataRoot, "desks", "desk-1", SteamApps.FileName)));
    }

    [Fact]
    public async Task Changing_the_user_moves_the_isolation_to_the_new_account()
    {
        await using var svc = await TestService.StartAsync();
        var created = await svc.CreateDeskAsync("desk-1");
        await StartAndStopAsync(svc, "desk-1");
        var oldHive = svc.Platform.Hive(created.User!);
        Assert.NotNull(oldHive.Get("Environment", Env));

        var moved = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"user":"other-desk-user"}"""));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Empty(oldHive.Values);

        await StartAndStopAsync(svc, "desk-1");
        Assert.NotNull(svc.Platform.Hive("other-desk-user").Get("Environment", Env));
    }

    [Fact]
    public void Ipc_name_matches_the_research_rules() =>
        Assert.Matches("^SplitPlay_[A-Za-z0-9_]+$", SteamIsolation.IpcName("desk-1"));
}
