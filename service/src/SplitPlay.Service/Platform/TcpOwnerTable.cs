// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace SplitPlay.Service.Platform;

/// <summary>
/// Reads the tables of <c>GetExtendedTcpTable(TCP_TABLE_OWNER_PID_CONNECTIONS)</c>: a DWORD row count, then rows of
/// <c>MIB_TCPROW_OWNER_PID</c> (IPv4, 24 bytes: state, local address, local port, remote address, remote port, pid) or
/// <c>MIB_TCP6ROW_OWNER_PID</c> (IPv6, 56 bytes: local address[16], scope, port, remote address[16], scope, port,
/// state, pid). Addresses are in network order; a port DWORD holds the port in network order in its low 16 bits.
/// </summary>
public static class TcpOwnerTable
{
    public const int RowSizeV4 = 24;

    public const int RowSizeV6 = 56;

    /// <summary>The port of a port DWORD as the table stores it (its first two bytes, big-endian).</summary>
    public static int Port(ReadOnlySpan<byte> portField) => (portField[0] << 8) | portField[1];

    /// <summary>
    /// The pid owning the client end of a loopback connection to us: its local address and port are the client's, its
    /// remote port is the server's. Null when no row matches or the table is malformed.
    /// </summary>
    public static uint? FindOwner(ReadOnlySpan<byte> table, IPAddress client, int clientPort, int serverPort)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.IsIPv4MappedToIPv6)
        {
            client = client.MapToIPv4();
        }

        var v6 = client.AddressFamily == AddressFamily.InterNetworkV6;
        var address = client.GetAddressBytes();
        var rowSize = v6 ? RowSizeV6 : RowSizeV4;
        if (table.Length < 4)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (count > (uint)((table.Length - 4) / rowSize))
        {
            return null;
        }

        for (var i = 0; i < (int)count; i++)
        {
            var row = table.Slice(4 + (i * rowSize), rowSize);
            ReadOnlySpan<byte> localAddr, localPort, remotePort, pid;
            if (v6)
            {
                localAddr = row[..16];
                localPort = row.Slice(20, 4);
                remotePort = row.Slice(44, 4);
                pid = row.Slice(52, 4);
            }
            else
            {
                localAddr = row.Slice(4, 4);
                localPort = row.Slice(8, 4);
                remotePort = row.Slice(16, 4);
                pid = row.Slice(20, 4);
            }

            if (localAddr.SequenceEqual(address) && Port(localPort) == clientPort && Port(remotePort) == serverPort)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(pid);
            }
        }

        return null;
    }

    /// <summary>
    /// The pid listening on local <paramref name="port"/> (any local address) in a
    /// <c>GetExtendedTcpTable(TCP_TABLE_OWNER_PID_LISTENER)</c> table, whose rows have the layout above. Null when no
    /// row matches or the table is malformed.
    /// </summary>
    public static uint? FindListener(ReadOnlySpan<byte> table, bool v6, int port)
    {
        var rowSize = v6 ? RowSizeV6 : RowSizeV4;
        if (table.Length < 4)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (count > (uint)((table.Length - 4) / rowSize))
        {
            return null;
        }

        for (var i = 0; i < (int)count; i++)
        {
            var row = table.Slice(4 + (i * rowSize), rowSize);
            var localPort = v6 ? row.Slice(20, 4) : row.Slice(8, 4);
            if (Port(localPort) == port)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(v6 ? row.Slice(52, 4) : row.Slice(20, 4));
            }
        }

        return null;
    }
}

/// <summary>
/// Reads the tables of <c>GetExtendedUdpTable(UDP_TABLE_OWNER_PID)</c>: a DWORD row count, then rows of
/// <c>MIB_UDPROW_OWNER_PID</c> (IPv4, 12 bytes: local address, local port, pid) or <c>MIB_UDP6ROW_OWNER_PID</c> (IPv6,
/// 28 bytes: local address[16], scope, local port, pid). Ports are stored as in <see cref="TcpOwnerTable"/>.
/// </summary>
public static class UdpOwnerTable
{
    public const int RowSizeV4 = 12;

    public const int RowSizeV6 = 28;

    /// <summary>The pid of a socket bound to local <paramref name="port"/> (any local address), or null.</summary>
    public static uint? FindOwner(ReadOnlySpan<byte> table, bool v6, int port)
    {
        var rowSize = v6 ? RowSizeV6 : RowSizeV4;
        if (table.Length < 4)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (count > (uint)((table.Length - 4) / rowSize))
        {
            return null;
        }

        for (var i = 0; i < (int)count; i++)
        {
            var row = table.Slice(4 + (i * rowSize), rowSize);
            var localPort = v6 ? row.Slice(20, 4) : row.Slice(4, 4);
            if (TcpOwnerTable.Port(localPort) == port)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(v6 ? row.Slice(24, 4) : row.Slice(8, 4));
            }
        }

        return null;
    }
}
