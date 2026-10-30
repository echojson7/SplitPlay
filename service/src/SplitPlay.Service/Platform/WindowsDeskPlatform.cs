// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Host;
using SplitPlay.Core.Platform;
using SplitPlay.Core.Ports;
using SplitPlay.Core.Steam;
using SplitPlay.Service.Configuration;
using SplitPlay.Service.Hosting;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Platform;

/// <summary>The real Windows platform (the service runs as LocalSystem).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeskPlatform : IDeskPlatform
{
    /// <summary>Written to the desk directory once the console user's keyboard layouts were copied (holds the desk user SID).</summary>
    public const string KeyboardMarkerFile = "keyboard-layouts.copied";

    private const string RdpTcpKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";

    private readonly DataPaths _paths;
    private readonly ConfigService _config;
    private readonly IOptions<SupervisorOptions> _supervisor;
    private readonly IOptions<FirewallOptions> _firewall;
    private readonly ILogger<WindowsDeskPlatform> _logger;
    private readonly DeskSecretStore _secrets;

    public WindowsDeskPlatform(DataPaths paths, ConfigService config, IOptions<SupervisorOptions> supervisor, IOptions<FirewallOptions> firewall,
        ILogger<WindowsDeskPlatform> logger)
    {
        _paths = paths;
        _config = config;
        _supervisor = supervisor;
        _firewall = firewall;
        _logger = logger;
        _secrets = new DeskSecretStore(paths.Root, new DpapiMachineProtector());
    }

    // ------------------------------------------------------------------ provisioning

    public DeskCredentials Provision(DeskConfig desk)
    {
        ArgumentNullException.ThrowIfNull(desk);
        var user = desk.User ?? throw new DeskProvisioningException("the desk has no Windows user configured");
        if (ConsoleUserName() is { } console && string.Equals(console, user, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeskProvisioningException(
                $"'{user}' is signed in at the console; a desk with that user would take over the console session. Pick another user");
        }

        DeskCredentials credentials;
        try
        {
            credentials = EnsureAccount(desk, user);
        }
        catch (Win32Exception ex)
        {
            throw new DeskProvisioningException($"could not provision Windows account '{user}': {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException and not OutOfMemoryException)
        {
            throw new DeskProvisioningException($"could not provision Windows account '{user}': {ex.Message}", ex);
        }

        CheckHost();
        UpdateLogonListing(desk);
        if (desk.SteamIsolationEnabled)
        {
            // Before the session logs on, so that explorer, Steam's autostart and steam:// already see the desk's values.
            TrySteamIsolation(desk, user);
        }
        else
        {
            RevertSteamIsolation(desk.Id);
        }

        return credentials;
    }

    private DeskCredentials EnsureAccount(DeskConfig desk, string user)
    {
        var deskId = desk.Id;
        var account = LocalAccounts.Get(user);
        var password = _secrets.Read(deskId);
        var managed = desk.UserManaged ?? account?.CreatedBySplitPlay ?? true;
        if (account is null)
        {
            if (!managed)
            {
                throw new DeskProvisioningException($"Windows account '{user}' does not exist any more; pick another user for this desk");
            }

            // The owner may have typed the password for the new account; otherwise SplitPlay picks one.
            var generated = password is null;
            password ??= PasswordGenerator.Generate();
            LocalAccounts.Create(user, password, $"{LocalAccounts.CommentPrefix} (desk {deskId})");
            if (generated)
            {
                _secrets.Write(deskId, password);
            }

            _logger.LogInformation("Created Windows account {User} for desk {Desk}", user, deskId);
            account = LocalAccounts.Get(user) ?? throw new DeskProvisioningException($"account '{user}' vanished right after it was created");
        }
        else if (!managed || !account.CreatedBySplitPlay)
        {
            // Someone else's account: use the password the owner typed, never change anything but RDP access.
            if (password is null)
            {
                throw new DeskProvisioningException($"password required for {user}");
            }

            if (!LocalAccounts.PasswordWorks(user, password))
            {
                throw new DeskProvisioningException($"wrong password for {user}: the stored password no longer works; enter the current password in the desk settings");
            }

            if (account.Disabled)
            {
                throw new DeskProvisioningException($"Windows account '{user}' is disabled");
            }

            LocalAccounts.AddToGroup(LocalAccounts.SidOf(user), LocalAccounts.RemoteDesktopUsersSid);
            if (!account.PasswordNeverExpires)
            {
                var warning = $"the password of {user} can expire; when it does, the desk stops logging on until you enter the new password";
                _logger.LogWarning("Desk {Desk}: {Warning}", deskId, warning);
                return new DeskCredentials(user, ".", password) { Warning = warning };
            }

            return new DeskCredentials(user, ".", password);
        }
        else if (password is null || !LocalAccounts.PasswordWorks(user, password))
        {
            password = PasswordGenerator.Generate();
            LocalAccounts.SetPassword(user, password);
            _secrets.Write(deskId, password);
            _logger.LogInformation("Rotated the password of {User} for desk {Desk} (stored secret was missing or stale)", user, deskId);
        }

        LocalAccounts.EnsureFlags(account);
        var sid = LocalAccounts.SidOf(user);
        LocalAccounts.AddToGroup(sid, LocalAccounts.UsersSid);
        LocalAccounts.AddToGroup(sid, LocalAccounts.RemoteDesktopUsersSid);
        return new DeskCredentials(user, ".", password);
    }

    private void CheckHost()
    {
        var tuner = new HostTuner(new WindowsHostRegistry(), new WindowsHostServices(), HostTuner.StateFileIn(_paths.Root));
        HostStatus status;
        try
        {
            status = tuner.Status(new HostTuningOptions
            {
                TermWrapDirectory = TermWrapDirectory(),
                ComposeIntervalMs = _config.Snapshot().Host.ComposeIntervalMs,
            });
        }
        catch (Exception ex) when (ex is HostTuningException or IOException or UnauthorizedAccessException)
        {
            throw new DeskProvisioningException($"RDP host not prepared: {ex.Message}", ex);
        }

        if (status.Problem is { } problem)
        {
            throw new DeskProvisioningException(problem);
        }
    }

    /// <summary><c>{app}\termwrap</c>: next to the service folder.</summary>
    public static string TermWrapDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "termwrap"));

    public int RdpPort()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(RdpTcpKey);
        return key?.GetValue("PortNumber") is int port and > 0 and < 65536 ? port : 3389;
    }

    public void DeleteDesk(DeskConfig desk, bool deleteUser)
    {
        ArgumentNullException.ThrowIfNull(desk);
        var account = deleteUser && desk.User is not null ? LocalAccounts.Get(desk.User) : null;
        if (account is not null && (desk.UserManaged == false || !account.CreatedBySplitPlay))
        {
            throw new DeskUserNotManagedException($"Windows account '{desk.User}' was not created by SplitPlay; it is never deleted by SplitPlay");
        }

        RevertSteamIsolation(desk.Id);
        ShowOnLogon(desk.Id);
        _secrets.Delete(desk.Id);
        if (account is null || desk.User is null)
        {
            return;
        }

        SecurityIdentifier? sid = null;
        try
        {
            sid = LocalAccounts.SidOf(desk.User);
        }
        catch (IdentityNotMappedException)
        {
        }

        LocalAccounts.Delete(desk.User, sid);
        _logger.LogInformation("Deleted Windows account {User} of desk {Desk}", desk.User, desk.Id);
    }

    public void DeleteManagedAccount(string user)
    {
        var account = LocalAccounts.Get(user);
        if (account is null)
        {
            return;
        }

        if (!account.CreatedBySplitPlay)
        {
            throw new DeskUserNotManagedException($"Windows account '{user}' was not created by SplitPlay; it is never deleted by SplitPlay");
        }

        SecurityIdentifier? sid = null;
        try
        {
            sid = LocalAccounts.SidOf(user);
        }
        catch (IdentityNotMappedException)
        {
        }

        LocalAccounts.Delete(user, sid);
        _logger.LogInformation("Deleted Windows account {User}", user);
    }

    // ------------------------------------------------------------------ firewall

    public static string FirewallRuleName(string deskId, string protocol) => $"SplitPlay desk {deskId} ({protocol})";

    public void EnsureFirewallRules(DeskConfig desk)
    {
        ArgumentNullException.ThrowIfNull(desk);
        if (!_firewall.Value.Enabled || desk.BasePort is not int b)
        {
            return;
        }

        RemoveFirewallRules(desk.Id);
        var program = _supervisor.Value.ResolveWorkerExecutable();
        var tcp = string.Join(',', BasePortAllocator.TcpPorts(b));
        var udp = string.Join(',', BasePortAllocator.UdpPorts(b));
        Netsh($"advfirewall firewall add rule name=\"{FirewallRuleName(desk.Id, "TCP")}\" dir=in action=allow protocol=TCP localport={tcp} " +
              $"profile=private,domain program=\"{program}\" enable=yes description=\"SplitPlay desk {desk.Id}: Moonlight (HTTPS, HTTP, RTSP)\"");
        Netsh($"advfirewall firewall add rule name=\"{FirewallRuleName(desk.Id, "UDP")}\" dir=in action=allow protocol=UDP localport={udp} " +
              $"profile=private,domain program=\"{program}\" enable=yes description=\"SplitPlay desk {desk.Id}: Moonlight (video, control, audio)\"");
    }

    public void RemoveFirewallRules(string deskId)
    {
        if (!_firewall.Value.Enabled)
        {
            return;
        }

        foreach (var protocol in new[] { "TCP", "UDP" })
        {
            Netsh($"advfirewall firewall delete rule name=\"{FirewallRuleName(deskId, protocol)}\"", quietFailure: true);
        }
    }

    private void Netsh(string arguments, bool quietFailure = false)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("netsh did not start");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            if (process.ExitCode != 0 && !quietFailure)
            {
                _logger.LogWarning("netsh {Arguments} failed (exit {Code}): {Output}", arguments, process.ExitCode, output.Trim());
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            _logger.LogWarning("Could not run netsh: {Message}", ex.Message);
        }
    }

    // ------------------------------------------------------------------ session

    public bool OnSessionLogon(DeskConfig desk, int sessionId, string deskDirectory)
    {
        ArgumentNullException.ThrowIfNull(desk);
        if (desk.User is null)
        {
            return false;
        }

        // The hive is loaded now; for a new account this is the first time it exists.
        var steam = desk.SteamIsolationEnabled && TrySteamIsolation(desk, desk.User);
        if (desk.UserManaged == false)
        {
            // An account the owner picked keeps its own keyboard layouts.
            return steam;
        }

        var deskSid = LocalAccounts.SidOf(desk.User).Value;
        var marker = Path.Combine(deskDirectory, KeyboardMarkerFile);
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == deskSid)
        {
            return steam;
        }

        var sourceSid = ConsoleUserSid() ?? ".DEFAULT";
        var copied = KeyboardLayouts.Copy(sourceSid, deskSid, _logger);
        Directory.CreateDirectory(deskDirectory);
        File.WriteAllText(marker, deskSid);
        if (copied)
        {
            _logger.LogInformation("Desk {Desk}: copied keyboard layouts of {Source} to {User}", desk.Id, sourceSid, desk.User);
        }

        return copied || steam;
    }

    // ------------------------------------------------------------------ sign-in screen

    private LogonListing LogonState(string deskId) => new(Path.Combine(_paths.DeskDirectory(deskId), LogonListing.StateFileName));

    public void UpdateLogonListing(DeskConfig desk)
    {
        ArgumentNullException.ThrowIfNull(desk);
        try
        {
            var listing = LogonState(desk.Id);
            var account = desk.User is null ? null : LocalAccounts.Get(desk.User);
            if (account is not null && desk.HidesFromLogon(account.CreatedBySplitPlay))
            {
                if (listing.Hide(new WindowsHostRegistry(), account.Name))
                {
                    _logger.LogInformation("Desk {Desk}: {User} is hidden from the sign-in screen", desk.Id, account.Name);
                }
            }
            else
            {
                ShowOnLogon(desk.Id);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or Win32Exception)
        {
            _logger.LogWarning("Desk {Desk}: could not update the sign-in screen entry: {Message}", desk.Id, ex.Message);
        }
    }

    public void ShowOnLogon(string deskId)
    {
        try
        {
            var listing = LogonState(deskId);
            var user = listing.RecordedUser();
            if (listing.Show(new WindowsHostRegistry()))
            {
                _logger.LogInformation("Desk {Desk}: {User} is shown on the sign-in screen again", deskId, user);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _logger.LogWarning("Desk {Desk}: could not restore the sign-in screen entry: {Message}", deskId, ex.Message);
        }
    }

    // ------------------------------------------------------------------ Steam isolation

    private SteamIsolation SteamState(string deskId) => new(Path.Combine(_paths.DeskDirectory(deskId), SteamIsolation.StateFileName));

    /// <summary>
    /// Applies the desk's Steam isolation to <paramref name="user"/>'s hive (loaded, or mounted when the profile exists).
    /// Returns true if the environment variable was newly written into a loaded hive: the running session only sees it
    /// after the next logon. Failures are logged, never thrown.
    /// </summary>
    private bool TrySteamIsolation(DeskConfig desk, string user)
    {
        try
        {
            var sid = LocalAccounts.SidOf(user).Value;
            var isolation = SteamState(desk.Id);
            if (isolation.RecordedSid() is { } recorded && !string.Equals(recorded, sid, StringComparison.OrdinalIgnoreCase))
            {
                RevertSteamIsolation(desk.Id);
            }

            using var hive = UserHive.Open(sid, _logger);
            if (hive is null)
            {
                _logger.LogDebug("Desk {Desk}: {User} has no profile yet; Steam isolation applies at the first logon", desk.Id, user);
                return false;
            }

            var result = isolation.Apply(hive.Hive, sid, desk.Id, SteamMachine());
            foreach (var change in result.Changes)
            {
                _logger.LogInformation("Desk {Desk}: Steam isolation for {User}: {Change}", desk.Id, user, change);
            }

            return result.EnvironmentWritten && !hive.Mounted;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Desk {Desk}: could not apply Steam isolation to {User}: {Message}", desk.Id, user, ex.Message);
            return false;
        }
    }

    public void RevertSteamIsolation(string deskId)
    {
        try
        {
            var isolation = SteamState(deskId);
            if (isolation.RecordedSid() is not { } sid)
            {
                return;
            }

            using var hive = UserHive.Open(sid, _logger);
            if (hive is null)
            {
                if (UserHive.ProfileHiveFile(sid) is not { } file || !File.Exists(file))
                {
                    // The account or its profile is gone, and with it everything SplitPlay wrote.
                    isolation.Forget();
                }

                return;
            }

            foreach (var change in isolation.Revert(hive.Hive))
            {
                _logger.LogInformation("Desk {Desk}: Steam isolation reverted for {Sid}: {Change}", deskId, sid, change);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Desk {Desk}: could not revert Steam isolation: {Message}", deskId, ex.Message);
        }
    }

    /// <summary>The machine-wide <c>steam://</c> handler (HKLM; Steam's installer writes it there).</summary>
    private static SteamMachineInfo SteamMachine()
    {
        var hklm = new WindowsHostRegistry();
        return new SteamMachineInfo(
            hklm.Get(@"SOFTWARE\Classes\steam\Shell\Open\Command", "")?.Data,
            hklm.Get(@"SOFTWARE\Classes\steam", "")?.Data);
    }

    public string? SteamExecutable()
    {
        var hklm = new WindowsHostRegistry();
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
        {
            if (hklm.Get(key, "InstallPath")?.Data is { Length: > 0 } path)
            {
                var exe = Path.Combine(Environment.ExpandEnvironmentVariables(path), "steam.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }

        return null;
    }

    private string? ConsoleUserSid()
    {
        if (ConsoleSessionId() is not int session)
        {
            return null;
        }

        var user = Wts.QueryString(session, Wts.WTSUserName);
        var domain = Wts.QueryString(session, Wts.WTSDomainName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        try
        {
            var account = string.IsNullOrEmpty(domain) ? new NTAccount(user) : new NTAccount(domain, user);
            return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> StopBluetoothUserService(int sessionId)
    {
        var stopped = new List<string>();
        string[] names;
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var services = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
        {
            // Per-user instances are "BluetoothUserService_<LUID>"; the template "BluetoothUserService" is never touched.
            names = services?.GetSubKeyNames().Where(n => n.StartsWith("BluetoothUserService_", StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        }

        foreach (var name in names)
        {
            try
            {
                if (WindowsHostServices.ProcessId(name) is not int pid || !ProcessIdToSessionId((uint)pid, out var session) || session != sessionId)
                {
                    continue;
                }

                if (WindowsHostServices.RequestStop(name))
                {
                    stopped.Add(name);
                }
            }
            catch (Win32Exception ex)
            {
                _logger.LogDebug("Could not stop {Service}: {Message}", name, ex.Message);
            }
        }

        return stopped;
    }

    public void ApplyComposeInterval(int composeIntervalMs)
    {
        var tuner = new HostTuner(new WindowsHostRegistry(), new WindowsHostServices(), HostTuner.StateFileIn(_paths.Root));
        if (tuner.ApplyComposeInterval(composeIntervalMs))
        {
            _logger.LogInformation("DWMFRAMEINTERVAL set to {Interval} ms; it applies to desk sessions that log on from now on", composeIntervalMs);
        }
    }

    public int? ConsoleSessionId()
    {
        var id = WTSGetActiveConsoleSessionId();
        return id == 0xFFFFFFFF ? null : (int)id;
    }

    public string? ConsoleUserName()
    {
        if (ConsoleSessionId() is not int session)
        {
            return null;
        }

        var user = Wts.QueryString(session, Wts.WTSUserName);
        var domain = Wts.QueryString(session, Wts.WTSDomainName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        // Only a local account can be a desk user.
        return string.IsNullOrEmpty(domain) || string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? user : null;
    }

    public DeskAccount? FindAccount(string user)
    {
        var account = LocalAccounts.Get(user);
        return account is null ? null : new DeskAccount(account.Name, account.CreatedBySplitPlay);
    }

    public bool VerifyPassword(string user, string password) => LocalAccounts.PasswordIsCorrect(user, password);

    public void StorePassword(string deskId, string password) => _secrets.Write(deskId, password);

    public void ForgetPassword(string deskId) => _secrets.Delete(deskId);

    public bool HasPassword(string deskId) => _secrets.Exists(deskId);

    public IReadOnlyList<HostUserDto> ListUsers()
    {
        var console = ConsoleUserName();
        return LocalAccounts.List()
            .Select(a => new HostUserDto(a.Name, string.IsNullOrWhiteSpace(a.FullName) ? null : a.FullName, a.IsAdmin,
                string.Equals(a.Name, console, StringComparison.OrdinalIgnoreCase), a.CreatedBySplitPlay))
            .ToList();
    }

    public IReadOnlyList<HostSession> Sessions() =>
        Wts.EnumerateSessions()
            .Where(s => s.Id != 0)
            .Select(s =>
            {
                var user = Wts.QueryString(s.Id, Wts.WTSUserName);
                var domain = Wts.QueryString(s.Id, Wts.WTSDomainName);
                var local = !string.IsNullOrEmpty(user)
                            && (string.IsNullOrEmpty(domain) || string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
                return new HostSession(s.Id, local ? user : null, s.State == Wts.WTSDisconnected);
            })
            .ToList();

    public string? SessionUser(int sessionId)
    {
        var user = Wts.QueryString(sessionId, Wts.WTSUserName) ?? throw new Win32Exception($"WTSQuerySessionInformation({sessionId}) failed");
        var domain = Wts.QueryString(sessionId, Wts.WTSDomainName);
        return user.Length > 0 && (string.IsNullOrEmpty(domain) || string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            ? user
            : null;
    }

    public void LogoffSession(int sessionId)
    {
        if (!Wts.Logoff(sessionId))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"WTSLogoffSession({sessionId}) failed ({error})");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint sessionId);
}

/// <summary>WTS queries.</summary>
[SupportedOSPlatform("windows")]
internal static class Wts
{
    public const int WTSUserName = 5;
    public const int WTSDomainName = 7;
    public const int WTSDisconnected = 4;

    /// <summary>All sessions (id and WTS_CONNECTSTATE_CLASS).</summary>
    public static IReadOnlyList<(int Id, int State)> EnumerateSessions()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"WTSEnumerateSessions failed ({error})");
        }

        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            var result = new List<(int, int)>(count);
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(buffer + (i * size));
                result.Add((info.SessionId, info.State));
            }

            return result;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>WTSLogoffSession without waiting.</summary>
    public static bool Logoff(int session) => WTSLogoffSession(IntPtr.Zero, session, false);

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr server, int sessionId, bool wait);

    public static string? QueryString(int session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}

/// <summary>Copies keyboard layouts between loaded user hives (<c>HKU\&lt;SID&gt;</c>).</summary>
[SupportedOSPlatform("windows")]
internal static class KeyboardLayouts
{
    /// <summary>
    /// <c>Keyboard Layout\Preload</c> and <c>Substitutes</c> are the classic list; Windows 8 and later keep the
    /// language list in <c>Control Panel\International\User Profile</c> too, so that tree is copied as well.
    /// </summary>
    private static readonly string[] Keys =
    [
        @"Keyboard Layout\Preload",
        @"Keyboard Layout\Substitutes",
        @"Control Panel\International\User Profile",
    ];

    public static bool Copy(string sourceSid, string targetSid, ILogger logger)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using var target = users.OpenSubKey(targetSid, writable: true);
        if (target is null)
        {
            logger.LogWarning("The hive of {Sid} is not loaded; keyboard layouts not copied", targetSid);
            return false;
        }

        var copied = false;
        foreach (var path in Keys)
        {
            using var source = users.OpenSubKey($@"{sourceSid}\{path}");
            if (source is null)
            {
                continue;
            }

            target.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            using var destination = target.CreateSubKey(path, writable: true);
            CopyTree(source, destination);
            copied = true;
        }

        return copied;
    }

    private static void CopyTree(RegistryKey source, RegistryKey destination)
    {
        foreach (var name in source.GetValueNames())
        {
            var kind = source.GetValueKind(name);
            var value = source.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is not null)
            {
                destination.SetValue(name, value, kind);
            }
        }

        foreach (var sub in source.GetSubKeyNames())
        {
            using var s = source.OpenSubKey(sub);
            using var d = destination.CreateSubKey(sub, writable: true);
            if (s is not null)
            {
                CopyTree(s, d);
            }
        }
    }
}
