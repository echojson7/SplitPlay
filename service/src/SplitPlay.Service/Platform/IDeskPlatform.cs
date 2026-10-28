// SPDX-License-Identifier: MIT
using SplitPlay.Core.Configuration;

namespace SplitPlay.Service.Platform;

/// <summary>The RDP credentials of a desk. <see cref="ToString"/> never shows the password.</summary>
public sealed record DeskCredentials(string User, string Domain, string Password)
{
    public override string ToString() => $"{Domain}\\{User} (password *****)";
}

/// <summary>Provisioning failed in a way retrying will not fix (the desk goes to Faulted with this message).</summary>
public sealed class DeskProvisioningException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The Windows side of a desk's lifecycle: account and password, host check, firewall rules, per-session fixes.
/// <see cref="WindowsDeskPlatform"/> in production, <see cref="FakeDeskPlatform"/> in tests and on non-Windows hosts.
/// All members may block (Win32, netsh); the supervisor calls them off the request path where it matters.
/// </summary>
public interface IDeskPlatform
{
    /// <summary>
    /// Ensures the desk's local account exists (random password, never expires, in Users and Remote Desktop Users),
    /// that its DPAPI-protected password is stored, and that the RDP host is prepared.
    /// </summary>
    /// <exception cref="DeskProvisioningException">Account, secret or host problem; the message goes to <c>lastError</c>.</exception>
    DeskCredentials Provision(DeskConfig desk);

    /// <summary>The RDP-Tcp listener port (registry, default 3389).</summary>
    int RdpPort();

    /// <summary>Creates or refreshes the desk's inbound firewall rules for its Moonlight ports.</summary>
    void EnsureFirewallRules(DeskConfig desk);

    /// <summary>Removes the desk's firewall rules (desk deleted).</summary>
    void RemoveFirewallRules(string deskId);

    /// <summary>Removes the stored password and, with <paramref name="deleteUser"/>, the Windows account and its profile.</summary>
    void DeleteDesk(DeskConfig desk, bool deleteUser);

    /// <summary>
    /// Runs once the desk session is logged on. Returns true if something was changed that only applies at the next
    /// logon (the console user's keyboard layouts were copied for the first time).
    /// </summary>
    bool OnSessionLogon(DeskConfig desk, int sessionId, string deskDirectory);

    /// <summary>Stops the <c>BluetoothUserService_*</c> instance running in <paramref name="sessionId"/>; returns what was stopped.</summary>
    IReadOnlyList<string> StopBluetoothUserService(int sessionId);

    /// <summary>Writes a changed <c>host.composeIntervalMs</c> to <c>DWMFRAMEINTERVAL</c> (prepared hosts only).</summary>
    void ApplyComposeInterval(int composeIntervalMs);

    /// <summary>The active console session id, or null if there is none.</summary>
    int? ConsoleSessionId();
}
