// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Json;
using System.Text;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;

namespace SplitPlay.Service.Tests;

/// <summary>Desks on an existing Windows account with the owner's password, and the rules around managed accounts.</summary>
public sealed class ExistingUserTests
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<ApiError> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.ApiErrorResponse))!.Error;

    [Fact]
    public async Task Existing_user_with_the_right_password_is_stored_and_used_unchanged()
    {
        await using var svc = await TestService.StartAsync();
        svc.Platform.AddExistingAccount("gamer", "Gamer-Pass-1");

        var created = await svc.CreateDeskAsync("desk-1", ""","user":"gamer","password":"Gamer-Pass-1" """);
        Assert.Equal("gamer", created.User);
        Assert.False(created.UserManaged);
        Assert.True(created.HasPassword);

        // The password never comes back and never reaches config.json.
        var raw = await svc.Client.GetStringAsync("/api/v1/desks/desk-1");
        Assert.DoesNotContain("Gamer-Pass-1", raw, StringComparison.Ordinal);
        Assert.Contains("\"hasPassword\":true", raw, StringComparison.Ordinal);
        var config = await File.ReadAllTextAsync(Path.Combine(svc.DataRoot, ConfigStore.FileName));
        Assert.DoesNotContain("Gamer-Pass-1", config, StringComparison.Ordinal);
        Assert.Contains("\"userManaged\": false", config, StringComparison.Ordinal);

        await svc.PostDeskActionAsync("desk-1", "start");
        var ready = await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var welcome = (WelcomeMessage)svc.Launcher.Received.First(m => m.Role == IpcProtocol.Roles.Seat && m.Message is WelcomeMessage).Message;
        Assert.Equal("Gamer-Pass-1", welcome.Rdp!.Password);

        // Nothing about the account changed except RDP access; the expiring password is only a warning.
        var account = svc.Platform.Accounts["gamer"];
        Assert.Equal("Gamer-Pass-1", account.Password);
        Assert.False(account.CreatedBySplitPlay);
        Assert.True(account.RemoteDesktopUser);
        Assert.DoesNotContain(svc.Platform.Actions, a => a.StartsWith("password ", StringComparison.Ordinal));
        Assert.Contains("can expire", ready.LastError, StringComparison.Ordinal);

        // Keyboard layouts of someone else's account are left alone.
        Assert.DoesNotContain("layouts desk-1", svc.Platform.Actions);

        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);

        // deleteUser on an account SplitPlay did not create: 409, nothing deleted.
        var delete = await svc.Client.DeleteAsync("/api/v1/desks/desk-1?deleteUser=true");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(ApiErrorCodes.UserNotManaged, (await ErrorOf(delete)).Code);
        Assert.True(svc.Platform.Accounts.ContainsKey("gamer"));
        Assert.True((await svc.GetDeskAsync("desk-1")).HasPassword);
        Assert.Equal(HttpStatusCode.NoContent, (await svc.Client.DeleteAsync("/api/v1/desks/desk-1")).StatusCode);
        Assert.True(svc.Platform.Accounts.ContainsKey("gamer"));
    }

    [Fact]
    public async Task Wrong_password_is_refused_with_validation_failed()
    {
        await using var svc = await TestService.StartAsync();
        svc.Platform.AddExistingAccount("gamer", "Gamer-Pass-1");
        var response = await svc.Client.PostAsync("/api/v1/desks", Json("""{"id":"desk-1","user":"gamer","password":"nope"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal(ApiErrorCodes.ValidationFailed, error.Code);
        Assert.Equal("wrong password for gamer", error.Message);
        Assert.Empty(await svc.Client.GetFromJsonAsync("/api/v1/desks", SplitPlayJsonContext.Default.ListDeskDto) ?? []);
    }

    [Fact]
    public async Task Existing_user_without_a_password_faults_instead_of_resetting_it()
    {
        await using var svc = await TestService.StartAsync();
        svc.Platform.AddExistingAccount("gamer", "Gamer-Pass-1", passwordNeverExpires: true);
        var created = await svc.CreateDeskAsync("desk-1", ""","user":"gamer" """);
        Assert.False(created.HasPassword);

        await svc.PostDeskActionAsync("desk-1", "start");
        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted);
        Assert.Equal("password required for gamer", faulted.LastError);
        Assert.Equal("Gamer-Pass-1", svc.Platform.Accounts["gamer"].Password);

        // The owner types the password: the desk starts, and no warning since the password never expires.
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        var updated = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"user":"gamer","password":"Gamer-Pass-1"}"""));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.True((await svc.GetDeskAsync("desk-1")).HasPassword);
        await svc.PostDeskActionAsync("desk-1", "start");
        var ready = await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Null(ready.LastError);

        // A wrong password on update is refused and keeps the stored one.
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        var wrong = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"user":"gamer","password":"bad"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.True((await svc.GetDeskAsync("desk-1")).HasPassword);
    }

    [Fact]
    public async Task Console_user_is_refused()
    {
        await using var svc = await TestService.StartAsync();
        svc.Platform.ConsoleUser = "Owner";
        svc.Platform.AddExistingAccount("owner", "pw");
        var response = await svc.Client.PostAsync("/api/v1/desks", Json("""{"id":"desk-1","user":"owner","password":"pw"}"""));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal(ApiErrorCodes.ConsoleUser, error.Code);
        Assert.Contains("console", error.Message, StringComparison.Ordinal);

        await svc.CreateDeskAsync("desk-1");
        var change = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"user":"owner"}"""));
        Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
    }

    [Fact]
    public async Task New_user_with_a_typed_password_is_created_with_it_and_managed()
    {
        await using var svc = await TestService.StartAsync();
        var created = await svc.CreateDeskAsync("desk-1", ""","user":"player2","password":"Typed-Pass-9" """);
        Assert.True(created.UserManaged);
        Assert.True(created.HasPassword);

        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        var account = svc.Platform.Accounts["player2"];
        Assert.Equal("Typed-Pass-9", account.Password);
        Assert.True(account.CreatedBySplitPlay);
        Assert.True(account.PasswordNeverExpires);
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.Equal(HttpStatusCode.NoContent, (await svc.Client.DeleteAsync("/api/v1/desks/desk-1?deleteUser=true")).StatusCode);
        Assert.False(svc.Platform.Accounts.ContainsKey("player2"));
    }

    [Fact]
    public async Task Changing_the_user_drops_the_old_password()
    {
        await using var svc = await TestService.StartAsync();
        svc.Platform.AddExistingAccount("gamer", "Gamer-Pass-1");
        svc.Platform.AddExistingAccount("other", "Other-Pass-2");
        await svc.CreateDeskAsync("desk-1", ""","user":"gamer","password":"Gamer-Pass-1" """);
        var response = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"user":"other"}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var desk = (await response.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.DeskDto))!;
        Assert.Equal("other", desk.User);
        Assert.False(desk.HasPassword);
        Assert.False(desk.UserManaged);
    }

    [Fact]
    public async Task Host_users_lists_local_accounts_and_flags_the_console_user()
    {
        await using var svc = await TestService.StartAsync();
        svc.Platform.AddExistingAccount("gamer", "x");
        var users = await svc.Client.GetFromJsonAsync("/api/v1/host/users", SplitPlayJsonContext.Default.ListHostUserDto);
        Assert.NotNull(users);
        Assert.Contains(users, u => u.Name == "gamer" && !u.IsConsoleUser && !u.CreatedBySplitPlay);
        Assert.Contains(users, u => u.Name == "owner" && u.IsConsoleUser);
        var raw = await svc.Client.GetStringAsync("/api/v1/host/users");
        Assert.Contains("\"isConsoleUser\":true", raw, StringComparison.Ordinal);
        Assert.Contains("\"fullName\"", raw, StringComparison.Ordinal);
    }
}
