// SPDX-License-Identifier: MIT
using System.Net;
using System.Text;
using SplitPlay.Core.Host;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Core.Platform;

namespace SplitPlay.Service.Tests;

/// <summary>Desk accounts and the Windows sign-in screen.</summary>
public sealed class LogonListingServiceTests
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task An_account_SplitPlay_created_is_hidden_and_shown_again_when_the_desk_goes()
    {
        await using var svc = await TestService.StartAsync();
        var created = await svc.CreateDeskAsync("desk-1");
        Assert.True(created.HideFromLogon);
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Equal("0", svc.Platform.UserListValue(created.User!));

        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.Equal(HttpStatusCode.NoContent, (await svc.Client.DeleteAsync("/api/v1/desks/desk-1?deleteUser=true")).StatusCode);
        Assert.Null(svc.Platform.UserListValue(created.User!));
    }

    [Fact]
    public async Task An_existing_account_is_never_hidden_unless_the_desk_asks_for_it()
    {
        await using var svc = await TestService.StartAsync();
        svc.Platform.AddExistingAccount("gamer", "Gamer-Pass-1", passwordNeverExpires: true);
        svc.Platform.Machine.Set(LogonListing.UserListKey, "gamer", RegValue.DWord(1));
        var created = await svc.CreateDeskAsync("desk-1", ""","user":"gamer","password":"Gamer-Pass-1" """);
        Assert.False(created.HideFromLogon);
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Equal("1", svc.Platform.UserListValue("gamer"));
        Assert.DoesNotContain("hide gamer", svc.Platform.Actions);

        // Turned on for this desk: applies at once, also while it runs; a PUT that leaves it out keeps it.
        var put = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"user":"gamer","hideFromLogon":true}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("0", svc.Platform.UserListValue("gamer"));
        put = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"user":"gamer"}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await svc.GetDeskAsync("desk-1")).HideFromLogon);

        // Off again: the owner's own value comes back.
        put = await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"user":"gamer","hideFromLogon":false}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("1", svc.Platform.UserListValue("gamer"));
        Assert.False((await svc.GetDeskAsync("desk-1")).HideFromLogon);

        // Deleting the desk shows the account again if it was hidden, and never deletes it.
        await svc.Client.PutAsync("/api/v1/desks/desk-1", Json("""{"user":"gamer","hideFromLogon":true}"""));
        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        Assert.Equal(HttpStatusCode.NoContent, (await svc.Client.DeleteAsync("/api/v1/desks/desk-1")).StatusCode);
        Assert.Equal("1", svc.Platform.UserListValue("gamer"));
        Assert.True(svc.Platform.Accounts.ContainsKey("gamer"));
    }

    [Fact]
    public async Task A_managed_desk_can_be_left_on_the_sign_in_screen()
    {
        await using var svc = await TestService.StartAsync();
        var created = await svc.CreateDeskAsync("desk-1", ""","hideFromLogon":false""");
        Assert.False(created.HideFromLogon);
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        Assert.Null(svc.Platform.UserListValue(created.User!));
    }
}
