// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SplitPlay.Service.Platform;

/// <summary>
/// The raw owner tables of <c>GetExtendedTcpTable</c> and <c>GetExtendedUdpTable</c>, read by
/// <see cref="TcpOwnerTable"/> and <see cref="UdpOwnerTable"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IpHelperTables
{
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;
    public const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    public const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    private delegate uint Getter(IntPtr buffer, ref int size);

    /// <summary>A TCP table of <paramref name="tableClass"/> for <paramref name="family"/>, or null.</summary>
    public static byte[]? Tcp(int family, int tableClass) =>
        Read((IntPtr buffer, ref int size) => GetExtendedTcpTable(buffer, ref size, false, family, tableClass, 0));

    /// <summary>The <c>UDP_TABLE_OWNER_PID</c> table for <paramref name="family"/>, or null.</summary>
    public static byte[]? Udp(int family) =>
        Read((IntPtr buffer, ref int size) => GetExtendedUdpTable(buffer, ref size, false, family, UDP_TABLE_OWNER_PID, 0));

    private static byte[]? Read(Getter get)
    {
        var size = 0;
        get(IntPtr.Zero, ref size);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
            try
            {
                var rc = get(buffer, ref size);
                if (rc == ERROR_INSUFFICIENT_BUFFER)
                {
                    // The table grew in between.
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

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
