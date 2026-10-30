// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using SplitPlay.Core.Ports;

namespace SplitPlay.Service.Platform;

/// <summary>
/// One of a desk's Moonlight ports that the worker could not bind: another process holds it (<see cref="Owner"/> is its
/// executable name) or Windows refuses it (<see cref="Owner"/> is null; usually an excluded port range that Hyper-V,
/// WSL or Docker reserved, <c>netsh int ipv4 show excludedportrange</c>).
/// </summary>
public sealed record PortConflict(int Port, string Protocol, string? Owner)
{
    /// <summary>The desk's <c>lastError</c>: <c>Port 48089 busy: sunshine.exe</c> or <c>Port 48089 reserved by Windows</c>.</summary>
    public string Message => Owner is null ? $"Port {Port} reserved by Windows" : $"Port {Port} busy: {Owner}";

    public override string ToString() => $"{Message} ({Protocol})";
}

/// <summary>Finds the first busy port of a desk (<see cref="BasePortAllocator.TcpPorts"/>, then <see cref="BasePortAllocator.UdpPorts"/>).</summary>
public static class PortCheck
{
    /// <summary>
    /// The first conflict: a TCP port with a listener, a UDP port with a bound socket (by owner pid), then a port
    /// Windows refuses to bind. Null when every port is free.
    /// </summary>
    public static PortConflict? Find(int basePort, Func<int, uint?> tcpOwner, Func<int, uint?> udpOwner,
        Func<int, bool> tcpRefused, Func<int, bool> udpRefused, Func<uint, string> processName)
    {
        ArgumentNullException.ThrowIfNull(tcpOwner);
        ArgumentNullException.ThrowIfNull(udpOwner);
        ArgumentNullException.ThrowIfNull(tcpRefused);
        ArgumentNullException.ThrowIfNull(udpRefused);
        ArgumentNullException.ThrowIfNull(processName);
        var tcp = BasePortAllocator.TcpPorts(basePort);
        var udp = BasePortAllocator.UdpPorts(basePort);
        foreach (var port in tcp)
        {
            if (tcpOwner(port) is uint pid)
            {
                return new PortConflict(port, "TCP", processName(pid));
            }
        }

        foreach (var port in udp)
        {
            if (udpOwner(port) is uint pid)
            {
                return new PortConflict(port, "UDP", processName(pid));
            }
        }

        foreach (var port in tcp)
        {
            if (tcpRefused(port))
            {
                return new PortConflict(port, "TCP", null);
            }
        }

        foreach (var port in udp)
        {
            if (udpRefused(port))
            {
                return new PortConflict(port, "UDP", null);
            }
        }

        return null;
    }
}

/// <summary><see cref="PortCheck"/> against this PC's TCP and UDP owner tables (IPv4 and IPv6) and a test bind.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPortCheck
{
    public static PortConflict? Find(int basePort)
    {
        var tcp4 = IpHelperTables.Tcp(IpHelperTables.AF_INET, IpHelperTables.TCP_TABLE_OWNER_PID_LISTENER);
        var tcp6 = IpHelperTables.Tcp(IpHelperTables.AF_INET6, IpHelperTables.TCP_TABLE_OWNER_PID_LISTENER);
        var udp4 = IpHelperTables.Udp(IpHelperTables.AF_INET);
        var udp6 = IpHelperTables.Udp(IpHelperTables.AF_INET6);
        return PortCheck.Find(basePort,
            port => (tcp4 is null ? null : TcpOwnerTable.FindListener(tcp4, false, port)) ?? (tcp6 is null ? null : TcpOwnerTable.FindListener(tcp6, true, port)),
            port => (udp4 is null ? null : UdpOwnerTable.FindOwner(udp4, false, port)) ?? (udp6 is null ? null : UdpOwnerTable.FindOwner(udp6, true, port)),
            port => BindRefused(SocketType.Stream, ProtocolType.Tcp, port),
            port => BindRefused(SocketType.Dgram, ProtocolType.Udp, port),
            ProcessName);
    }

    /// <summary>
    /// True when Windows refuses a bind to the port with WSAEACCES (an excluded port range). "In use" is not reported
    /// here: the owner tables name the process, and a port that only has connections in TIME_WAIT is fine for the worker.
    /// </summary>
    private static bool BindRefused(SocketType type, ProtocolType protocol, int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, type, protocol);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return false;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.AccessDenied;
        }
    }

    /// <summary>The executable name of <paramref name="pid"/> (<c>sunshine.exe</c>), <c>System</c> for pid 4, or <c>PID n</c>.</summary>
    internal static string ProcessName(uint pid)
    {
        if (pid == 4)
        {
            return "System";
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return $"PID {pid}";
        }
    }
}
