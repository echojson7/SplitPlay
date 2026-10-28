// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace SplitPlay.Service.Platform;

/// <summary>Local accounts through netapi32 (NetUserAdd, NetUserSetInfo, NetLocalGroupAddMembers, NetUserDel).</summary>
[SupportedOSPlatform("windows")]
public static class LocalAccounts
{
    /// <summary>Comment set on accounts SplitPlay creates; only such accounts get their password reset or are deleted.</summary>
    public const string CommentPrefix = "SplitPlay desk account";

    /// <summary>BUILTIN\Users and BUILTIN\Remote Desktop Users. Resolved by SID: group names are localized.</summary>
    public static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);

    public static readonly SecurityIdentifier RemoteDesktopUsersSid = new(WellKnownSidType.BuiltinRemoteDesktopUsersSid, null);

    private const int NERR_Success = 0;
    private const int NERR_UserNotFound = 2221;
    private const int ERROR_MEMBER_IN_ALIAS = 1378;
    private const int ERROR_LOGON_FAILURE = 1326;

    private const uint UF_SCRIPT = 0x0001;
    private const uint UF_ACCOUNTDISABLE = 0x0002;
    private const uint UF_LOCKOUT = 0x0010;
    private const uint UF_NORMAL_ACCOUNT = 0x0200;
    private const uint UF_DONT_EXPIRE_PASSWD = 0x10000;
    private const uint UF_PASSWORD_EXPIRED = 0x800000;
    private const uint USER_PRIV_USER = 1;

    /// <summary>What <see cref="Get"/> found.</summary>
    public sealed record AccountInfo(string Name, string Comment, uint Flags)
    {
        public bool CreatedBySplitPlay => Comment.StartsWith(CommentPrefix, StringComparison.Ordinal);

        public bool Disabled => (Flags & UF_ACCOUNTDISABLE) != 0;

        public bool PasswordNeverExpires => (Flags & UF_DONT_EXPIRE_PASSWD) != 0;
    }

    /// <summary>An entry of <see cref="List"/>.</summary>
    public sealed record ListedAccount(string Name, string FullName, bool IsAdmin, bool CreatedBySplitPlay);

    private const uint USER_PRIV_ADMIN = 2;
    private const int FILTER_NORMAL_ACCOUNT = 2;
    private const int ERROR_MORE_DATA = 234;

    /// <summary>
    /// Enabled normal local accounts, without the built-in ones (RIDs 500 Administrator, 501 Guest, 503 DefaultAccount,
    /// 504 WDAGUtilityAccount), sorted by name.
    /// </summary>
    public static IReadOnlyList<ListedAccount> List()
    {
        var result = new List<ListedAccount>();
        var resume = 0;
        int rc;
        do
        {
            rc = NetUserEnum(null, 20, FILTER_NORMAL_ACCOUNT, out var buffer, -1, out var read, out _, ref resume);
            if (rc != NERR_Success && rc != ERROR_MORE_DATA)
            {
                Check(rc, "NetUserEnum");
            }

            try
            {
                var size = Marshal.SizeOf<USER_INFO_20>();
                for (var i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<USER_INFO_20>(buffer + (i * size));
                    if (info.usri20_name is not { } name || (info.usri20_flags & UF_ACCOUNTDISABLE) != 0
                        || info.usri20_user_id is 500 or 501 or 503 or 504)
                    {
                        continue;
                    }

                    var isAdmin = false;
                    var created = false;
                    if (NetUserGetInfo(null, name, 1, out var detail) == NERR_Success)
                    {
                        try
                        {
                            var one = Marshal.PtrToStructure<USER_INFO_1>(detail);
                            isAdmin = one.usri1_priv == USER_PRIV_ADMIN;
                            created = (one.usri1_comment ?? "").StartsWith(CommentPrefix, StringComparison.Ordinal);
                        }
                        finally
                        {
                            NetApiBufferFree(detail);
                        }
                    }

                    result.Add(new ListedAccount(name, info.usri20_full_name ?? "", isAdmin, created));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    NetApiBufferFree(buffer);
                }
            }
        }
        while (rc == ERROR_MORE_DATA);

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static AccountInfo? Get(string user)
    {
        var rc = NetUserGetInfo(null, user, 1, out var buffer);
        if (rc == NERR_UserNotFound)
        {
            return null;
        }

        Check(rc, $"NetUserGetInfo({user})");
        try
        {
            var info = Marshal.PtrToStructure<USER_INFO_1>(buffer);
            return new AccountInfo(info.usri1_name ?? user, info.usri1_comment ?? "", info.usri1_flags);
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
    }

    public static void Create(string user, string password, string comment)
    {
        var info = new USER_INFO_1
        {
            usri1_name = user,
            usri1_password = password,
            usri1_priv = USER_PRIV_USER,
            usri1_comment = comment,
            usri1_flags = UF_SCRIPT | UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWD,
        };
        Check(NetUserAdd(null, 1, ref info, out var parm), $"NetUserAdd({user}) (parameter {parm})");
    }

    public static void SetPassword(string user, string password)
    {
        var info = new USER_INFO_1003 { usri1003_password = password };
        Check(NetUserSetInfo(null, user, 1003, ref info, out var parm), $"NetUserSetInfo({user}, 1003) (parameter {parm})");
    }

    /// <summary>Enabled, unlocked, password never expires, not "must change at next logon".</summary>
    public static void EnsureFlags(AccountInfo account)
    {
        var flags = (account.Flags | UF_DONT_EXPIRE_PASSWD | UF_SCRIPT) & ~(UF_ACCOUNTDISABLE | UF_LOCKOUT | UF_PASSWORD_EXPIRED);
        if (flags == account.Flags)
        {
            return;
        }

        var info = new USER_INFO_1008 { usri1008_flags = flags };
        Check(NetUserSetInfo(null, account.Name, 1008, ref info, out var parm), $"NetUserSetInfo({account.Name}, 1008) (parameter {parm})");
    }

    public static SecurityIdentifier SidOf(string user) =>
        (SecurityIdentifier)new NTAccount(Environment.MachineName, user).Translate(typeof(SecurityIdentifier));

    /// <summary>The localized name of a builtin group (without the <c>BUILTIN\</c> prefix).</summary>
    public static string GroupName(SecurityIdentifier group)
    {
        var name = ((NTAccount)group.Translate(typeof(NTAccount))).Value;
        var slash = name.IndexOf('\\', StringComparison.Ordinal);
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    /// <summary>Adds the user to a local group by SID; already being a member is fine.</summary>
    public static void AddToGroup(SecurityIdentifier user, SecurityIdentifier group)
    {
        var groupName = GroupName(group);
        var sid = new byte[user.BinaryLength];
        user.GetBinaryForm(sid, 0);
        var sidPtr = Marshal.AllocHGlobal(sid.Length);
        try
        {
            Marshal.Copy(sid, 0, sidPtr, sid.Length);
            var info = new LOCALGROUP_MEMBERS_INFO_0 { lgrmi0_sid = sidPtr };
            var rc = NetLocalGroupAddMembers(null, groupName, 0, ref info, 1);
            if (rc != ERROR_MEMBER_IN_ALIAS)
            {
                Check(rc, $"NetLocalGroupAddMembers({groupName})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sidPtr);
        }
    }

    /// <summary>
    /// Checks a password with a network logon. Returns false only for a wrong password; other failures (logon type
    /// not granted, restrictions) return true so that they never trigger a password rotation.
    /// </summary>
    public static bool PasswordWorks(string user, string password)
    {
        if (LogonUserW(user, ".", password, 3 /* LOGON32_LOGON_NETWORK */, 0, out var token))
        {
            CloseHandle(token);
            return true;
        }

        return Marshal.GetLastWin32Error() != ERROR_LOGON_FAILURE;
    }

    /// <summary>
    /// Checks a password the owner typed with a network logon. False for a wrong user name or password; other
    /// failures (logon type not granted, account restrictions) count as correct since the password itself matched.
    /// </summary>
    public static bool PasswordIsCorrect(string user, string password) => PasswordWorks(user, password);

    public static void Delete(string user, SecurityIdentifier? sid)
    {
        if (sid is not null)
        {
            // Best effort: fails while the profile is still loaded; the account goes regardless.
            DeleteProfileW(sid.Value, null, null);
        }

        var rc = NetUserDel(null, user);
        if (rc != NERR_UserNotFound)
        {
            Check(rc, $"NetUserDel({user})");
        }
    }

    private static void Check(int rc, string what)
    {
        if (rc != NERR_Success)
        {
            throw new Win32Exception(rc, $"{what} failed: {new Win32Exception(rc).Message} ({rc})");
        }
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1
    {
        public string? usri1_name;
        public string? usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        public string? usri1_home_dir;
        public string? usri1_comment;
        public uint usri1_flags;
        public string? usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_20
    {
        public string? usri20_name;
        public string? usri20_full_name;
        public string? usri20_comment;
        public uint usri20_flags;
        public uint usri20_user_id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1003
    {
        public string usri1003_password;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USER_INFO_1008
    {
        public uint usri1008_flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LOCALGROUP_MEMBERS_INFO_0
    {
        public IntPtr lgrmi0_sid;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserAdd(string? server, int level, ref USER_INFO_1 buf, out int parmErr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buf);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(string? server, string user, int level, ref USER_INFO_1003 buf, out int parmErr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(string? server, string user, int level, ref USER_INFO_1008 buf, out int parmErr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserDel(string? server, string user);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserEnum(string? server, int level, int filter, out IntPtr buf, int prefMaxLen, out int entriesRead,
        out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(string? server, string group, int level, ref LOCALGROUP_MEMBERS_INFO_0 buf, int total);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUserW(string user, string domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteProfileW(string sid, string? profilePath, string? computerName);
}
