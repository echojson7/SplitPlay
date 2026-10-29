// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Win32.SafeHandles;
using SplitPlay.Service.Api;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Platform;

/// <summary>
/// Who may get a credential from the admin pipe, decided from the client's token:
/// <list type="bullet">
/// <item>never a process in a desk session (desk users are other people, even when their account is an administrator);</item>
/// <item>SYSTEM, and an administrator whose token is elevated (an elevated prompt, the installer, SSH);</item>
/// <item>an administrator with a UAC-filtered token in the active console session (the owner's tray and CLI).</item>
/// </list>
/// Everybody else (a standard user at the console, any RDP session) is refused and can still sign in with the admin
/// password.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAdminCallerCheck(DeskSupervisor desks, IDeskPlatform platform) : IAdminCallerCheck
{
    public AdminCallerDecision Check(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        var handle = pipe.SafePipeHandle;
        if (!GetNamedPipeClientProcessId(handle, out var pid))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"GetNamedPipeClientProcessId failed ({error})");
        }

        if (!GetNamedPipeClientSessionId(handle, out var session))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"GetNamedPipeClientSessionId failed ({error})");
        }

        // The client process's own token (it is connected, so the pid is still its own). The service runs as SYSTEM and
        // can open every process that is not protected; impersonation is the fallback.
        var facts = TokenFacts.OfProcess(pid);
        if (facts is null)
        {
            pipe.RunAsClient(() => facts = TokenFacts.OfCurrentThread());
        }

        var caller = $"{facts!.Name} (pid {pid}, session {session})";

        if (desks.IsDeskSession((int)session))
        {
            return new AdminCallerDecision(false, caller, "callers in a desk session cannot manage SplitPlay");
        }

        if (facts.IsSystem || facts.AdminEnabled)
        {
            return new AdminCallerDecision(true, caller);
        }

        if (facts.AdminFiltered && platform.ConsoleSessionId() == (int)session)
        {
            return new AdminCallerDecision(true, caller);
        }

        return new AdminCallerDecision(false, caller,
            "only an administrator signed in at this PC (or an elevated prompt) can manage SplitPlay; sign in with the admin password instead");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
}

/// <summary>What the admin check needs from a token.</summary>
[SupportedOSPlatform("windows")]
internal sealed record TokenFacts(string Name, bool IsSystem, bool AdminEnabled, bool AdminFiltered)
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;
    private const int TokenGroups = 2;
    private const uint SE_GROUP_ENABLED = 0x4;
    private const uint SE_GROUP_USE_FOR_DENY_ONLY = 0x10;

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>The primary token of process <paramref name="pid"/>, or null when it cannot be opened.</summary>
    public static TokenFacts? OfProcess(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            {
                return null;
            }

            using (token)
            {
                return Read(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The impersonation token of the calling thread (inside <c>RunAsClient</c>).</summary>
    public static TokenFacts OfCurrentThread()
    {
        // openAsSelf: the service opens the token; an identification-level token cannot open itself.
        if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, true, out var token))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"OpenThreadToken failed ({error})");
        }

        using (token)
        {
            return Read(token);
        }
    }

    private static TokenFacts Read(SafeAccessTokenHandle token)
    {
        var user = User(token);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var enabled = false;
        var filtered = false;
        foreach (var (sid, attributes) in Groups(token))
        {
            if (!sid.Equals(admins))
            {
                continue;
            }

            enabled |= (attributes & SE_GROUP_ENABLED) != 0 && (attributes & SE_GROUP_USE_FOR_DENY_ONLY) == 0;
            filtered |= (attributes & SE_GROUP_USE_FOR_DENY_ONLY) != 0;
        }

        string name;
        try
        {
            name = ((NTAccount)user.Translate(typeof(NTAccount))).Value;
        }
        catch (IdentityNotMappedException)
        {
            name = user.Value;
        }

        return new TokenFacts(name, user.IsWellKnown(WellKnownSidType.LocalSystemSid), enabled, filtered);
    }

    private static SecurityIdentifier User(SafeAccessTokenHandle token)
    {
        var buffer = Query(token, TokenUser);
        try
        {
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<(SecurityIdentifier Sid, uint Attributes)> Groups(SafeAccessTokenHandle token)
    {
        var buffer = Query(token, TokenGroups);
        try
        {
            // TOKEN_GROUPS: DWORD GroupCount, then SID_AND_ATTRIBUTES[] aligned to a pointer.
            var count = Marshal.ReadInt32(buffer);
            var size = Marshal.SizeOf<SID_AND_ATTRIBUTES>();
            var list = new List<(SecurityIdentifier, uint)>(count);
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<SID_AND_ATTRIBUTES>(buffer + IntPtr.Size + (i * size));
                list.Add((new SecurityIdentifier(entry.Sid), entry.Attributes));
            }

            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr Query(SafeAccessTokenHandle token, int infoClass)
    {
        GetTokenInformation(token, infoClass, IntPtr.Zero, 0, out var length);
        var buffer = Marshal.AllocHGlobal(length);
        if (!GetTokenInformation(token, infoClass, buffer, length, out _))
        {
            var error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(buffer);
            throw new Win32Exception(error, $"GetTokenInformation failed ({error})");
        }

        return buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, int length, out int returned);
}

/// <summary>
/// Maps a loopback Web API connection to the session of the process that opened it (GetExtendedTcpTable, then
/// ProcessIdToSessionId), once per connection.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPeerInspector : IPeerInspector
{
    private const string ItemKey = "splitplay.peer-session";
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;

    public int? PeerSession(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = context.Features.Get<IConnectionItemsFeature>()?.Items;
        if (items is not null && items.TryGetValue(ItemKey, out var cached))
        {
            return cached as int?;
        }

        var session = Lookup(context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.LocalPort);
        if (items is not null)
        {
            items[ItemKey] = session;
        }

        return session;
    }

    private static int? Lookup(IPAddress? remote, int remotePort, int localPort)
    {
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return null;
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        var pid = remote.AddressFamily == AddressFamily.InterNetwork
            ? OwnerV4(remote, remotePort, localPort)
            : OwnerV6(remote, remotePort, localPort);
        return pid is uint p && ProcessIdToSessionId(p, out var session) ? (int)session : null;
    }

    /// <summary>The client's end of the connection: local = its address and port, remote port = ours.</summary>
    private static uint? OwnerV4(IPAddress client, int clientPort, int serverPort)
    {
        var address = BitConverter.ToUInt32(client.GetAddressBytes(), 0);
        return Scan(AF_INET, Marshal.SizeOf<MIB_TCPROW_OWNER_PID>(), row =>
        {
            var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(row);
            return r.LocalAddr == address && Port(r.LocalPort) == clientPort && Port(r.RemotePort) == serverPort ? r.OwningPid : null;
        });
    }

    private static uint? OwnerV6(IPAddress client, int clientPort, int serverPort)
    {
        var bytes = client.GetAddressBytes();
        return Scan(AF_INET6, Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>(), row =>
        {
            var r = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(row);
            return r.LocalAddr.AsSpan().SequenceEqual(bytes) && Port(r.LocalPort) == clientPort && Port(r.RemotePort) == serverPort
                ? r.OwningPid
                : null;
        });
    }

    private static int Port(uint networkOrder) => ((int)(networkOrder & 0xFF) << 8) | (int)((networkOrder >> 8) & 0xFF);

    private static uint? Scan(int family, int rowSize, Func<IntPtr, uint?> match)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var rc = GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
                if (rc == 122)
                {
                    // ERROR_INSUFFICIENT_BUFFER: the table grew in between.
                    continue;
                }

                if (rc != 0)
                {
                    return null;
                }

                var count = Marshal.ReadInt32(buffer);
                // The rows follow dwNumEntries, aligned to 4 bytes.
                for (var i = 0; i < count; i++)
                {
                    if (match(buffer + 4 + (i * rowSize)) is uint pid)
                    {
                        return pid;
                    }
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint sessionId);
}
