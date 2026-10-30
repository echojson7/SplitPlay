// SPDX-License-Identifier: MIT
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;

namespace SplitPlay.Service.Platform;

/// <summary>The RDP credentials of a desk. <see cref="ToString"/> never shows the password.</summary>
public sealed record DeskCredentials(string User, string Domain, string Password)
{
    /// <summary>A non-fatal problem worth showing (e.g. the password of an account SplitPlay did not create can expire).</summary>
    public string? Warning { get; init; }

    public override string ToString() => $"{Domain}\\{User} (password *****)";
}

/// <summary>An existing local account (<see cref="IDeskPlatform.FindAccount"/>).</summary>
public sealed record DeskAccount(string Name, bool CreatedBySplitPlay);

/// <summary>A Windows session: its id, the local account signed in (null: nobody, or a domain account) and whether it is disconnected.</summary>
public sealed record HostSession(int Id, string? User, bool Disconnected);

/// <summary><c>deleteUser=true</c> for an account SplitPlay did not create (API: 409).</summary>
public sealed class DeskUserNotManagedException(string message) : Exception(message);

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
    /// Ensures the desk's account and stored password are usable and the RDP host is prepared. With Steam isolation on
    /// and a profile that exists, the user's hive gets the isolation values before the session logs on (best effort).
    /// Managed accounts (<see cref="DeskConfig.UserManaged"/>) are created when missing (stored or random password,
    /// never expires, in Users and Remote Desktop Users) and get a new password when the stored one is missing or stale.
    /// An account SplitPlay did not create is never changed except for being added to Remote Desktop Users: its stored
    /// password must work ("password required for X" otherwise).
    /// </summary>
    /// <exception cref="DeskProvisioningException">Account, secret or host problem; the message goes to <c>lastError</c>.</exception>
    DeskCredentials Provision(DeskConfig desk);

    /// <summary>The RDP-Tcp listener port (registry, default 3389).</summary>
    int RdpPort();

    /// <summary>Creates or refreshes the desk's inbound firewall rules for its Moonlight ports.</summary>
    void EnsureFirewallRules(DeskConfig desk);

    /// <summary>Removes the desk's firewall rules (desk deleted).</summary>
    void RemoveFirewallRules(string deskId);

    /// <summary>
    /// Removes the stored password, shows the account on the sign-in screen again if the desk hid it and, with
    /// <paramref name="deleteUser"/>, deletes the Windows account and its profile.
    /// </summary>
    void DeleteDesk(DeskConfig desk, bool deleteUser);

    /// <summary>Deletes a Windows account SplitPlay created, with its profile (no-op when it does not exist).</summary>
    /// <exception cref="DeskUserNotManagedException">The account was not created by SplitPlay; it is never deleted.</exception>
    void DeleteManagedAccount(string user);

    /// <summary>
    /// Runs once the desk session is logged on. Returns true if something was changed that only applies at the next
    /// logon (the console user's keyboard layouts were copied for the first time, or the Steam isolation environment
    /// variable was newly written).
    /// </summary>
    bool OnSessionLogon(DeskConfig desk, int sessionId, string deskDirectory);

    /// <summary>
    /// Undoes the desk's Steam isolation (<see cref="DeskConfig.SteamIsolation"/>) in the hive of the user it was applied
    /// to: recorded previous values are restored. No-op when nothing is recorded. Best effort: failures are logged.
    /// </summary>
    void RevertSteamIsolation(string deskId);

    /// <summary>
    /// Hides the desk's account from the sign-in screen, or shows it again, as <see cref="DeskConfig.HidesFromLogon"/>
    /// says (an account SplitPlay did not create only when the desk asks for it). A user hidden earlier for this desk
    /// that is no longer its user is shown again. Best effort: failures are logged.
    /// </summary>
    void UpdateLogonListing(DeskConfig desk);

    /// <summary>Shows the account the desk hid on the sign-in screen again (no-op when it hid none). Best effort.</summary>
    void ShowOnLogon(string deskId);

    /// <summary>The machine's <c>steam.exe</c> (from <c>HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath</c>), or null.</summary>
    string? SteamExecutable();

    /// <summary>Stops the <c>BluetoothUserService_*</c> instance running in <paramref name="sessionId"/>; returns what was stopped.</summary>
    IReadOnlyList<string> StopBluetoothUserService(int sessionId);

    /// <summary>Writes a changed <c>host.composeIntervalMs</c> to <c>DWMFRAMEINTERVAL</c> (prepared hosts only).</summary>
    void ApplyComposeInterval(int composeIntervalMs);

    /// <summary>The active console session id, or null if there is none.</summary>
    int? ConsoleSessionId();

    /// <summary>The local account signed in at the console, or null (nobody, or a domain account).</summary>
    string? ConsoleUserName();

    /// <summary>The local account named <paramref name="user"/>, or null if it does not exist.</summary>
    DeskAccount? FindAccount(string user);

    /// <summary>Checks a password with a network logon; false only for a wrong user name or password.</summary>
    bool VerifyPassword(string user, string password);

    /// <summary>Stores the desk's password (DPAPI, LocalMachine).</summary>
    void StorePassword(string deskId, string password);

    /// <summary>Removes the desk's stored password.</summary>
    void ForgetPassword(string deskId);

    /// <summary>True if a password is stored for the desk.</summary>
    bool HasPassword(string deskId);

    /// <summary>Enabled local accounts a desk can use (built-in accounts left out).</summary>
    IReadOnlyList<HostUserDto> ListUsers();

    /// <summary>The sessions on this PC (WTSEnumerateSessions).</summary>
    IReadOnlyList<HostSession> Sessions();

    /// <summary>The local account signed in to <paramref name="sessionId"/>, or null (nobody, or a domain account).</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The session cannot be queried.</exception>
    string? SessionUser(int sessionId);

    /// <summary>Logs session <paramref name="sessionId"/> off without waiting for it (WTSLogoffSession).</summary>
    void LogoffSession(int sessionId);
}
