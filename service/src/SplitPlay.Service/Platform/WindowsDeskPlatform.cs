// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Host;
using SplitPlay.Core.Platform;
using SplitPlay.Core.Ports;
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
        string password;
        try
        {
            password = EnsureAccount(desk.Id, user);
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
        return new DeskCredentials(user, ".", password);
    }

    private string EnsureAccount(string deskId, string user)
    {
        var account = LocalAccounts.Get(user);
        var password = _secrets.Read(deskId);
        if (account is null)
        {
            password = PasswordGenerator.Generate();
            LocalAccounts.Create(user, password, $"{LocalAccounts.CommentPrefix} (desk {deskId})");
            _secrets.Write(deskId, password);
            _logger.LogInformation("Created Windows account {User} for desk {Desk}", user, deskId);
            account = LocalAccounts.Get(user) ?? throw new DeskProvisioningException($"account '{user}' vanished right after it was created");
        }
        else if (password is null || !LocalAccounts.PasswordWorks(user, password))
        {
            if (!account.CreatedBySplitPlay)
            {
                throw new DeskProvisioningException(
                    $"Windows account '{user}' already exists and was not created by SplitPlay; SplitPlay will not reset its password. " +
                    "Choose another user for this desk or delete that account");
            }

            password = PasswordGenerator.Generate();
            LocalAccounts.SetPassword(user, password);
            _secrets.Write(deskId, password);
            _logger.LogInformation("Rotated the password of {User} for desk {Desk} (stored secret was missing or stale)", user, deskId);
        }

        LocalAccounts.EnsureFlags(account);
        var sid = LocalAccounts.SidOf(user);
        LocalAccounts.AddToGroup(sid, LocalAccounts.UsersSid);
        LocalAccounts.AddToGroup(sid, LocalAccounts.RemoteDesktopUsersSid);
        return password!;
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
        _secrets.Delete(desk.Id);
        if (!deleteUser || desk.User is null)
        {
            return;
        }

        var account = LocalAccounts.Get(desk.User);
        if (account is null)
        {
            return;
        }

        if (!account.CreatedBySplitPlay)
        {
            throw new DeskProvisioningException($"Windows account '{desk.User}' was not created by SplitPlay; delete it yourself if you want it gone");
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

        var deskSid = LocalAccounts.SidOf(desk.User).Value;
        var marker = Path.Combine(deskDirectory, KeyboardMarkerFile);
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == deskSid)
        {
            return false;
        }

        var sourceSid = ConsoleUserSid() ?? ".DEFAULT";
        var copied = KeyboardLayouts.Copy(sourceSid, deskSid, _logger);
        Directory.CreateDirectory(deskDirectory);
        File.WriteAllText(marker, deskSid);
        if (copied)
        {
            _logger.LogInformation("Desk {Desk}: copied keyboard layouts of {Source} to {User}", desk.Id, sourceSid, desk.User);
        }

        return copied;
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
