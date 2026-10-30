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
/// <see cref="AdminCallerRules"/> on Windows: the caller's session from the pipe, its token from impersonation (or, when
/// that fails, its process), and the desk sessions from <see cref="DeskSupervisor.IsDeskSession"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAdminCallerCheck(DeskSupervisor desks, IDeskPlatform platform) : IAdminCallerCheck
{
    public int? SessionOf(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session) ? (int)session : null;
    }

    public AdminCallerDecision Screen(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        var (pid, session) = Client(pipe);
        if (desks.IsDeskSession(session))
        {
            return new AdminCallerDecision(false, $"pid {pid} (session {session})", "callers in a desk session cannot manage SplitPlay", session);
        }

        // The process token is good enough to drop obvious non-admins early; the final decision uses impersonation.
        return TokenFacts.OfProcess(pid) is { } facts
            ? AdminCallerRules.Decide(Facts(facts, session, deskSession: false))
            : new AdminCallerDecision(true, $"pid {pid} (session {session})", Session: session);
    }

    public AdminCallerDecision Check(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        var (pid, session) = Client(pipe);

        // The token the client connected with (impersonation, identification level); the client's process token only when
        // impersonation is not possible.
        TokenFacts? facts = null;
        try
        {
            pipe.RunAsClient(() => facts = TokenFacts.OfCurrentThread());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            facts = TokenFacts.OfProcess(pid) ?? throw new Win32Exception($"cannot read the caller's token ({ex.Message})");
        }

        var decision = AdminCallerRules.Decide(Facts(facts!, session, desks.IsDeskSession(session)));
        return decision with { Caller = $"{decision.Caller}, pid {pid}" };
    }

    private AdminCallerFacts Facts(TokenFacts facts, int session, bool deskSession) =>
        new(facts.Name, facts.IsSystem, facts.AdminEnabled, facts.AdminFiltered, facts.IntegrityRid, session, platform.ConsoleSessionId(),
            deskSession);

    private static (uint Pid, int Session) Client(NamedPipeServerStream pipe)
    {
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

        return (pid, (int)session);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
}

/// <summary>What the admin check needs from a token.</summary>
[SupportedOSPlatform("windows")]
internal sealed record TokenFacts(string Name, bool IsSystem, bool AdminEnabled, bool AdminFiltered, int IntegrityRid)
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;
    private const int TokenGroups = 2;
    private const int TokenIntegrityLevel = 25;
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

        return new TokenFacts(name, user.IsWellKnown(WellKnownSidType.LocalSystemSid), enabled, filtered, Integrity(token));
    }

    /// <summary>The RID of the token's mandatory label (S-1-16-RID), or -1.</summary>
    private static int Integrity(SafeAccessTokenHandle token)
    {
        IntPtr buffer;
        try
        {
            buffer = Query(token, TokenIntegrityLevel);
        }
        catch (Win32Exception)
        {
            return -1;
        }

        try
        {
            // TOKEN_MANDATORY_LABEL: SID_AND_ATTRIBUTES Label.
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            return int.TryParse(sid[(sid.LastIndexOf('-') + 1)..], out var rid) ? rid : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
/// ProcessIdToSessionId), once per connection. A loopback peer that cannot be found is <see cref="PeerLookupKind.Failed"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPeerInspector : IPeerInspector
{
    private const string ItemKey = "splitplay.peer-session";
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;

    public PeerLookup PeerSession(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = context.Features.Get<IConnectionItemsFeature>()?.Items;
        if (items is not null && items.TryGetValue(ItemKey, out var cached) && cached is PeerLookup known)
        {
            return known;
        }

        var result = Lookup(context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.LocalPort);
        if (items is not null)
        {
            items[ItemKey] = result;
        }

        return result;
    }

    private static PeerLookup Lookup(IPAddress? remote, int remotePort, int localPort)
    {
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return PeerLookup.NotApplicable;
        }

        var v6 = remote.AddressFamily == AddressFamily.InterNetworkV6 && !remote.IsIPv4MappedToIPv6;
        var table = Table(v6 ? AF_INET6 : AF_INET);
        if (table is null || TcpOwnerTable.FindOwner(table, remote, remotePort, localPort) is not uint pid)
        {
            return PeerLookup.Failed;
        }

        return ProcessIdToSessionId(pid, out var session) ? PeerLookup.Found((int)session) : PeerLookup.Failed;
    }

    /// <summary>The raw table, or null.</summary>
    private static byte[]? Table(int family)
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

                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint sessionId);
}
