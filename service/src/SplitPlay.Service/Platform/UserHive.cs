// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SplitPlay.Core.Host;
using SplitPlay.Core.Steam;

namespace SplitPlay.Service.Platform;

/// <summary>
/// Access to a user's registry: <c>HKU\&lt;SID&gt;</c> and <c>HKU\&lt;SID&gt;_Classes</c> when they are loaded (the user
/// is logged on), otherwise the profile's <c>NTUSER.DAT</c> and <c>UsrClass.dat</c> mounted for the duration of the
/// lease and unloaded on dispose. <c>Software\Classes</c> is routed to the classes hive: at logon Windows links that key
/// to <c>UsrClass.dat</c>, so a value written into <c>NTUSER.DAT</c>'s own copy would never be seen. Null when the
/// profile does not exist yet (the account never logged on).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UserHive : IDisposable
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private static readonly IntPtr HkeyUsers = unchecked((IntPtr)(int)0x80000003);

    private readonly List<string> _mounts;
    private readonly ILogger _logger;

    private UserHive(string root, string? classesRoot, List<string> mounts, ILogger logger)
    {
        Hive = new ClassesRoutedHive(
            new WindowsHostRegistry(RegistryHive.Users, root),
            classesRoot is null ? null : new WindowsHostRegistry(RegistryHive.Users, classesRoot));
        _mounts = mounts;
        _logger = logger;
    }

    public IUserHive Hive { get; }

    /// <summary>True if the hive was mounted from <c>NTUSER.DAT</c> (the user is not logged on).</summary>
    public bool Mounted => _mounts.Count > 0;

    /// <summary>Opens the hives of <paramref name="sid"/>; null if there is no profile yet or it cannot be mounted.</summary>
    public static UserHive? Open(string sid, ILogger logger)
    {
        if (IsLoaded(sid))
        {
            return new UserHive(sid, IsLoaded(sid + "_Classes") ? sid + "_Classes" : null, [], logger);
        }

        var file = ProfileHiveFile(sid);
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        var mounts = new List<string>();
        var mount = "SplitPlay_" + sid;
        if (!Mount(mount, file, logger))
        {
            return null;
        }

        mounts.Add(mount);
        var classesFile = Path.Combine(Path.GetDirectoryName(file)!, @"AppData\Local\Microsoft\Windows\UsrClass.dat");
        string? classes = mount + "_Classes";
        if (File.Exists(classesFile) && Mount(classes, classesFile, logger))
        {
            mounts.Add(classes);
        }
        else
        {
            classes = null;
        }

        return new UserHive(mount, classes, mounts, logger);
    }

    private static bool IsLoaded(string name)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using var key = users.OpenSubKey(name);
        return key is not null;
    }

    private static bool Mount(string name, string file, ILogger logger)
    {
        if (IsLoaded(name))
        {
            // Left mounted by an earlier run that did not get to unload it: use it, unload it afterwards.
            return true;
        }

        EnablePrivilege("SeRestorePrivilege");
        EnablePrivilege("SeBackupPrivilege");
        var error = RegLoadKeyW(HkeyUsers, name, file);
        if (error != 0)
        {
            logger.LogWarning("Could not mount {File}: {Message}", file, new Win32Exception(error).Message);
            return false;
        }

        return true;
    }

    /// <summary><c>&lt;ProfileImagePath&gt;\NTUSER.DAT</c> of <paramref name="sid"/>, or null if it has no profile.</summary>
    public static string? ProfileHiveFile(string sid)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey($@"{ProfileListKey}\{sid}");
        return key?.GetValue("ProfileImagePath") is string path && path.Length > 0
            ? Path.Combine(Environment.ExpandEnvironmentVariables(path), "NTUSER.DAT")
            : null;
    }

    public void Dispose()
    {
        // All RegistryKey handles are closed by now (WindowsHostRegistry opens one per call); a handle someone else
        // holds can delay the unload briefly. A hive left mounted would make the user's next logon fail.
        foreach (var mount in Enumerable.Reverse(_mounts))
        {
            var error = 0;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                error = RegUnLoadKeyW(HkeyUsers, mount);
                if (error == 0)
                {
                    break;
                }

                Thread.Sleep(200);
            }

            if (error != 0)
            {
                _logger.LogError("Could not unload the registry hive HKU\\{Mount}: {Message}", mount, new Win32Exception(error).Message);
            }
        }
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");
        }

        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"LookupPrivilegeValue({name})");
            }

            var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"AdjustTokenPrivileges({name})");
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    /// <summary>TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES (the LUID is two DWORDs: 4-byte aligned).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadKeyW(IntPtr key, string subKey, string file);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegUnLoadKeyW(IntPtr key, string subKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, uint length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
