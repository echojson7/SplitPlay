// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Platform;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Platform;

/// <summary>
/// In-memory platform for tests and non-Windows hosts: accounts live in a dictionary, secrets in real files
/// (unprotected), firewall rules and per-session actions are only recorded.
/// </summary>
public sealed class FakeDeskPlatform : IDeskPlatform
{
    private readonly ConcurrentDictionary<string, FakeAccount> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _actions = new();

    public FakeDeskPlatform(DataPaths paths)
    {
        Secrets = new DeskSecretStore(paths.Root, new PlainTestProtector());
    }

    /// <summary>A fake local account.</summary>
    public sealed record FakeAccount(string Password, bool CreatedBySplitPlay, bool PasswordNeverExpires, bool RemoteDesktopUser = false);

    public DeskSecretStore Secrets { get; }

    /// <summary>When set, <see cref="Provision"/> fails with this message (e.g. a host that is not prepared).</summary>
    public string? ProvisionError { get; set; }

    public int Port { get; set; } = 3389;

    public int ConsoleSession { get; set; } = 1;

    /// <summary>The local account signed in at the console (null: nobody).</summary>
    public string? ConsoleUser { get; set; } = "owner";

    /// <summary>Everything that happened, in order (<c>provision desk-1</c>, <c>firewall+ desk-1</c>, …).</summary>
    public IReadOnlyList<string> Actions => [.. _actions];

    /// <summary>Accounts and their current state.</summary>
    public IReadOnlyDictionary<string, FakeAccount> Accounts => _accounts;

    /// <summary>Adds an account that SplitPlay did not create (the owner's own user).</summary>
    public void AddExistingAccount(string user, string password, bool passwordNeverExpires = false) =>
        _accounts[user] = new FakeAccount(password, CreatedBySplitPlay: false, passwordNeverExpires);

    public DeskCredentials Provision(DeskConfig desk)
    {
        ArgumentNullException.ThrowIfNull(desk);
        _actions.Enqueue($"provision {desk.Id}");
        if (ProvisionError is { } error)
        {
            throw new DeskProvisioningException(error);
        }

        var user = desk.User ?? throw new DeskProvisioningException("desk has no user");
        if (string.Equals(user, ConsoleUser, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeskProvisioningException($"'{user}' is signed in at the console");
        }

        var password = Secrets.Read(desk.Id);
        _accounts.TryGetValue(user, out var account);
        var managed = desk.UserManaged ?? account?.CreatedBySplitPlay ?? true;
        if (account is null)
        {
            if (!managed)
            {
                throw new DeskProvisioningException($"Windows account '{user}' does not exist any more");
            }

            if (password is null)
            {
                password = PasswordGenerator.Generate();
                Secrets.Write(desk.Id, password);
            }

            _accounts[user] = new FakeAccount(password, CreatedBySplitPlay: true, PasswordNeverExpires: true, RemoteDesktopUser: true);
            _actions.Enqueue($"useradd {user}");
            return new DeskCredentials(user, ".", password);
        }

        if (!managed || !account.CreatedBySplitPlay)
        {
            if (password is null)
            {
                throw new DeskProvisioningException($"password required for {user}");
            }

            if (password != account.Password)
            {
                throw new DeskProvisioningException($"wrong password for {user}: the stored password no longer works");
            }

            _accounts[user] = account with { RemoteDesktopUser = true };
            return new DeskCredentials(user, ".", password)
            {
                Warning = account.PasswordNeverExpires ? null : $"the password of {user} can expire",
            };
        }

        if (password is null || password != account.Password)
        {
            password = PasswordGenerator.Generate();
            _accounts[user] = account with { Password = password };
            Secrets.Write(desk.Id, password);
            _actions.Enqueue($"password {desk.Id}");
        }

        return new DeskCredentials(user, ".", password);
    }

    public int RdpPort() => Port;

    public void EnsureFirewallRules(DeskConfig desk) => _actions.Enqueue($"firewall+ {desk.Id} {desk.BasePort}");

    public void RemoveFirewallRules(string deskId) => _actions.Enqueue($"firewall- {deskId}");

    public void DeleteDesk(DeskConfig desk, bool deleteUser)
    {
        var account = deleteUser && desk.User is not null && _accounts.TryGetValue(desk.User, out var a) ? a : null;
        if (account is not null && (desk.UserManaged == false || !account.CreatedBySplitPlay))
        {
            throw new DeskUserNotManagedException($"Windows account '{desk.User}' was not created by SplitPlay");
        }

        Secrets.Delete(desk.Id);
        if (account is not null)
        {
            _accounts.TryRemove(desk.User!, out _);
            _actions.Enqueue($"userdel {desk.User}");
        }
    }

    public bool OnSessionLogon(DeskConfig desk, int sessionId, string deskDirectory)
    {
        _actions.Enqueue($"logon {desk.Id} {sessionId}");
        if (desk.UserManaged == false)
        {
            return false;
        }

        var marker = Path.Combine(deskDirectory, WindowsDeskPlatform.KeyboardMarkerFile);
        if (File.Exists(marker))
        {
            return false;
        }

        Directory.CreateDirectory(deskDirectory);
        File.WriteAllText(marker, "fake");
        _actions.Enqueue($"layouts {desk.Id}");
        return true;
    }

    public IReadOnlyList<string> StopBluetoothUserService(int sessionId)
    {
        _actions.Enqueue($"bluetooth {sessionId}");
        return [];
    }

    public void ApplyComposeInterval(int composeIntervalMs) => _actions.Enqueue($"compose {composeIntervalMs}");

    public int? ConsoleSessionId() => ConsoleSession;

    public string? ConsoleUserName() => ConsoleUser;

    public DeskAccount? FindAccount(string user) =>
        _accounts.TryGetValue(user, out var account) ? new DeskAccount(user, account.CreatedBySplitPlay) : null;

    public bool VerifyPassword(string user, string password) =>
        _accounts.TryGetValue(user, out var account) && account.Password == password;

    public void StorePassword(string deskId, string password)
    {
        Secrets.Write(deskId, password);
        _actions.Enqueue($"secret {deskId}");
    }

    public void ForgetPassword(string deskId) => Secrets.Delete(deskId);

    public bool HasPassword(string deskId) => Secrets.Exists(deskId);

    public IReadOnlyList<HostUserDto> ListUsers() =>
        _accounts.Keys.Concat(ConsoleUser is null ? [] : [ConsoleUser]).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => new HostUserDto(name, null, string.Equals(name, ConsoleUser, StringComparison.OrdinalIgnoreCase),
                string.Equals(name, ConsoleUser, StringComparison.OrdinalIgnoreCase),
                _accounts.TryGetValue(name, out var a) && a.CreatedBySplitPlay))
            .ToList();
}
