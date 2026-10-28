// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, string> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _actions = new();

    public FakeDeskPlatform(DataPaths paths)
    {
        Secrets = new DeskSecretStore(paths.Root, new PlainTestProtector());
    }

    public DeskSecretStore Secrets { get; }

    /// <summary>When set, <see cref="Provision"/> fails with this message (e.g. a host that is not prepared).</summary>
    public string? ProvisionError { get; set; }

    public int Port { get; set; } = 3389;

    public int ConsoleSession { get; set; } = 1;

    /// <summary>Everything that happened, in order (<c>provision desk-1</c>, <c>firewall+ desk-1</c>, …).</summary>
    public IReadOnlyList<string> Actions => [.. _actions];

    /// <summary>Accounts and their current passwords.</summary>
    public IReadOnlyDictionary<string, string> Accounts => _accounts;

    public DeskCredentials Provision(DeskConfig desk)
    {
        ArgumentNullException.ThrowIfNull(desk);
        _actions.Enqueue($"provision {desk.Id}");
        if (ProvisionError is { } error)
        {
            throw new DeskProvisioningException(error);
        }

        var user = desk.User ?? throw new DeskProvisioningException("desk has no user");
        var password = Secrets.Read(desk.Id);
        if (password is null || !_accounts.TryGetValue(user, out var current) || current != password)
        {
            password = PasswordGenerator.Generate();
            _accounts[user] = password;
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
        Secrets.Delete(desk.Id);
        if (deleteUser && desk.User is not null)
        {
            _accounts.TryRemove(desk.User, out _);
            _actions.Enqueue($"userdel {desk.User}");
        }
    }

    public bool OnSessionLogon(DeskConfig desk, int sessionId, string deskDirectory)
    {
        _actions.Enqueue($"logon {desk.Id} {sessionId}");
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
}
