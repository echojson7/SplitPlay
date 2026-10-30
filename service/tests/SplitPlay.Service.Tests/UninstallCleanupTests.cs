// SPDX-License-Identifier: MIT
using Microsoft.Extensions.Logging.Abstractions;
using SplitPlay.Core.Configuration;
using SplitPlay.Service.Hosting;
using SplitPlay.Service.Platform;

namespace SplitPlay.Service.Tests;

/// <summary>What the uninstaller undoes (<c>SplitPlay.Service.exe cleanup</c>).</summary>
public sealed class UninstallCleanupTests : IDisposable
{
    private readonly string _root = TestService.NewDataRoot();

    public void Dispose() => TestService.TryDelete(_root);

    private (FakeDeskPlatform Platform, DataPaths Paths, SplitPlayConfig Config) Setup()
    {
        var paths = new DataPaths(_root);
        paths.EnsureCreated();
        var platform = new FakeDeskPlatform(paths);
        var managed = new DeskConfig { Id = "desk-1", User = "sp-desk-1", UserManaged = true };
        var own = new DeskConfig { Id = "desk-2", User = "gamer", UserManaged = false, HideFromLogon = true };
        platform.AddExistingAccount("gamer", "Gamer-Pass-1");
        platform.AddExistingAccount("owner2", "Owner-Pass-1");
        platform.StorePassword("desk-2", "Gamer-Pass-1");
        platform.Provision(managed);
        platform.Provision(own);
        platform.AddManagedAccount("sp-old");
        platform.AddSession(5, "sp-desk-1");
        platform.AddSession(6, "gamer");
        Directory.CreateDirectory(paths.DeskDirectory("gone"));
        var config = new SplitPlayConfig();
        config.Desks.Add(managed);
        config.Desks.Add(own);
        Assert.Equal("0", platform.UserListValue("sp-desk-1"));
        Assert.Equal("0", platform.UserListValue("gamer"));
        return (platform, paths, config);
    }

    [Fact]
    public void Without_deleting_accounts_every_desk_setting_is_undone_and_every_account_stays()
    {
        var (platform, paths, config) = Setup();
        var report = UninstallCleanup.Run(platform, paths, config, deleteAccounts: false, NullLogger.Instance);

        Assert.Empty(report.Failed);
        Assert.Null(platform.UserListValue("sp-desk-1"));
        Assert.Null(platform.UserListValue("gamer"));
        // Passwords stay with the data folder for a reinstall over kept data.
        Assert.True(platform.HasPassword("desk-1"));
        Assert.True(platform.HasPassword("desk-2"));
        Assert.Contains("firewall- desk-1", platform.Actions);
        Assert.Contains("firewall- desk-2", platform.Actions);
        Assert.Contains("firewall- gone", platform.Actions);
        Assert.Equal(["gamer", "owner2", "sp-desk-1", "sp-old"], platform.Accounts.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(platform.Actions, a => a.StartsWith("logoff", StringComparison.Ordinal));
    }

    [Fact]
    public void Deleting_accounts_removes_only_the_ones_SplitPlay_created()
    {
        var (platform, paths, config) = Setup();
        var report = UninstallCleanup.Run(platform, paths, config, deleteAccounts: true, NullLogger.Instance);

        Assert.Empty(report.Failed);
        Assert.Equal(["gamer", "owner2"], platform.Accounts.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(report.Done, l => l.Contains("account sp-desk-1 deleted", StringComparison.Ordinal));
        Assert.Contains(report.Done, l => l == "account sp-old deleted");
        // Only the sessions of accounts about to be deleted are logged off.
        Assert.Contains("logoff 5", platform.Actions);
        Assert.DoesNotContain("logoff 6", platform.Actions);
        Assert.Null(platform.UserListValue("gamer"));
        Assert.False(platform.HasPassword("desk-1"));
        Assert.True(platform.HasPassword("desk-2"));
    }

    [Fact]
    public void An_account_that_is_not_SplitPlay_s_is_never_deleted_even_when_asked_directly()
    {
        var (platform, _, _) = Setup();
        Assert.Throws<DeskUserNotManagedException>(() => platform.DeleteManagedAccount("owner2"));
        Assert.True(platform.Accounts.ContainsKey("owner2"));
    }
}
