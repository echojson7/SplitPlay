// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SplitPlay.Service.Platform;

/// <summary>
/// CPU load since the previous read: the whole CPU and each logical processor, percent. Processors are keyed by their
/// index as config and <see cref="SplitPlay.Core.Host.LogicalCpu.Index"/> number them (<c>group * 64 + number</c>).
/// </summary>
public sealed record CpuLoad(double TotalPercent, IReadOnlyDictionary<int, double> PerCpu)
{
    /// <summary>The busiest of <paramref name="cpus"/> (all processors when null); null when none of them is known.</summary>
    public double? MaxOf(IReadOnlyList<int>? cpus)
    {
        if (cpus is null)
        {
            return PerCpu.Count > 0 ? PerCpu.Values.Max() : null;
        }

        double? max = null;
        foreach (var i in cpus)
        {
            if (PerCpu.TryGetValue(i, out var v))
            {
                max = Math.Max(max ?? 0, v);
            }
        }

        return max;
    }
}

/// <summary>Cumulative idle and total (kernel + user, idle included) time of one logical processor, 100 ns ticks.</summary>
public readonly record struct CpuTimes(long Idle, long Total);

/// <summary>
/// Turns cumulative processor times into load. Each <see cref="Sample"/> reads them once and measures since the
/// previous read; a read more than <see cref="MaxGap"/> after the previous one starts over (a load averaged over the
/// hours between two recordings says nothing). Thread-safe; never throws.
/// </summary>
public sealed class CpuLoadSampler
{
    /// <summary>Reads further apart than this start a new baseline instead of reporting a load.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(3);

    private readonly Func<IReadOnlyDictionary<int, CpuTimes>?> _read;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private IReadOnlyDictionary<int, CpuTimes>? _previous;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public CpuLoadSampler(Func<IReadOnlyDictionary<int, CpuTimes>?> read, TimeProvider time)
    {
        _read = read;
        _time = time;
    }

    /// <summary>The processor times of this machine: <c>NtQuerySystemInformationEx</c> on Windows, nothing elsewhere.</summary>
    public static CpuLoadSampler ForThisHost(TimeProvider time) =>
        new(OperatingSystem.IsWindows() ? WindowsCpuTimes.Read : () => null, time);

    /// <summary>Reads the times now: the load since the previous read, or null (first read, a long gap, no times).</summary>
    public CpuLoad? Sample()
    {
        lock (_gate)
        {
            IReadOnlyDictionary<int, CpuTimes>? times;
            try
            {
                times = _read();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                times = null;
            }

            var now = _time.GetUtcNow();
            var load = times is not null && _previous is not null && now - _readAt <= MaxGap ? Load(_previous, times) : null;
            _previous = times;
            _readAt = now;
            return load;
        }
    }

    /// <summary>Load between two reads, over the processors both have; null when they share none.</summary>
    public static CpuLoad? Load(IReadOnlyDictionary<int, CpuTimes> before, IReadOnlyDictionary<int, CpuTimes> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var per = new Dictionary<int, double>();
        long idle = 0, total = 0;
        foreach (var (cpu, a) in after)
        {
            if (!before.TryGetValue(cpu, out var b))
            {
                continue;
            }

            var dTotal = Math.Max(0, a.Total - b.Total);
            var dIdle = Math.Clamp(a.Idle - b.Idle, 0, dTotal);
            per[cpu] = dTotal > 0 ? Math.Round(100.0 * (dTotal - dIdle) / dTotal, 1) : 0;
            idle += dIdle;
            total += dTotal;
        }

        if (per.Count == 0)
        {
            return null;
        }

        return new CpuLoad(total > 0 ? Math.Round(100.0 * (total - idle) / total, 1) : 0, per);
    }
}

/// <summary>
/// Processor times from <c>NtQuerySystemInformationEx(SystemProcessorPerformanceInformation)</c>, one processor group
/// at a time (the plain call only returns the calling thread's group).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsCpuTimes
{
    private const int SystemProcessorPerformanceInformation = 8;

    /// <summary>SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION: Idle, Kernel (idle included), User, Dpc, Interrupt (8 each), InterruptCount (4) + padding.</summary>
    private const int EntrySize = 48;

    public static IReadOnlyDictionary<int, CpuTimes>? Read()
    {
        var result = new Dictionary<int, CpuTimes>();
        var groups = GetActiveProcessorGroupCount();
        for (ushort group = 0; group < groups; group++)
        {
            var count = (int)GetActiveProcessorCount(group);
            if (count <= 0)
            {
                continue;
            }

            var buffer = new byte[EntrySize * count];
            var g = group;
            if (NtQuerySystemInformationEx(SystemProcessorPerformanceInformation, ref g, sizeof(ushort), buffer, buffer.Length, out var returned) != 0)
            {
                return null;
            }

            for (var i = 0; i < returned / EntrySize; i++)
            {
                var idle = BitConverter.ToInt64(buffer, i * EntrySize);
                var kernel = BitConverter.ToInt64(buffer, (i * EntrySize) + 8);
                var user = BitConverter.ToInt64(buffer, (i * EntrySize) + 16);
                result[(group * 64) + i] = new CpuTimes(idle, kernel + user);
            }
        }

        return result.Count > 0 ? result : null;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformationEx(int infoClass, ref ushort input, int inputLength, [Out] byte[] info, int length, out int returned);

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetActiveProcessorGroupCount();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetActiveProcessorCount(ushort group);
}

/// <summary>
/// The host's network adapter towards a client as the stream recording shows it. Counters are cumulative since the
/// adapter came up; Wi-Fi values are null on a wired adapter and when the WLAN service cannot tell.
/// </summary>
/// <param name="Name">The adapter's description ("Realtek PCIe 2.5GbE Family Controller").</param>
/// <param name="Kind"><c>ethernet</c>, <c>wifi</c> or <c>other</c>.</param>
/// <param name="Mbps">Link speed as Windows reports it (for Wi-Fi the current transmit rate), Mbit/s.</param>
/// <param name="OutDiscards">Outgoing packets the adapter or its driver threw away (a full send queue).</param>
/// <param name="OutErrors">Outgoing packets that failed.</param>
/// <param name="WifiSignalPercent">Signal quality, 0-100 (WLAN_ASSOCIATION_ATTRIBUTES.wlanSignalQuality).</param>
/// <param name="WifiRssiDbm">Received signal strength, dBm.</param>
/// <param name="WifiTxMbps">Current transmit rate, Mbit/s.</param>
/// <param name="WifiRxMbps">Current receive rate, Mbit/s.</param>
/// <param name="WifiChannel">Channel number.</param>
/// <param name="WifiPhy">PHY ("ac", "ax", "be", …).</param>
public sealed record LinkStatus(
    string Name,
    string Kind,
    double Mbps,
    long? OutDiscards,
    long? OutErrors,
    int? WifiSignalPercent = null,
    int? WifiRssiDbm = null,
    double? WifiTxMbps = null,
    double? WifiRxMbps = null,
    int? WifiChannel = null,
    string? WifiPhy = null)
{
    /// <summary>"Ethernet Realtek PCIe 2.5GbE 2500 Mbit/s" or "Wi-Fi Intel AX210 ch 36 ax".</summary>
    public string Describe()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var kind = Kind switch
        {
            "wifi" => "Wi-Fi",
            "ethernet" => "Ethernet",
            _ => "link",
        };
        var wifi = Kind == "wifi"
            ? string.Create(inv, $"{(WifiChannel is int ch ? $" ch {ch}" : "")}{(WifiPhy is { } phy ? $" {phy}" : "")}")
            : "";
        // A Wi-Fi adapter's speed is its current transmit rate, which changes every few seconds: it is not part of the
        // line (the recording shows the rate in a column of its own).
        return Kind == "wifi" ? string.Create(inv, $"{kind} {Name}{wifi}") : string.Create(inv, $"{kind} {Name} {Mbps:0} Mbit/s");
    }
}

/// <summary>
/// Reads <see cref="LinkStatus"/> of an adapter by its id (<see cref="NetworkInterface.Id"/>, as
/// <see cref="NetworkLinks"/> finds it). Thread-safe; never throws (null when the adapter is gone).
/// </summary>
public static class LinkStatusReader
{
    /// <summary>Reads closer together than this (several desks ask every second) return the previous result.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMilliseconds(900);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long At, LinkStatus? Status)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static LinkStatus? Read(string adapterId)
    {
        ArgumentNullException.ThrowIfNull(adapterId);
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (Cache.TryGetValue(adapterId, out var hit) && now - hit.At < CacheFor.TotalMilliseconds)
            {
                return hit.Status;
            }
        }

        var status = ReadNow(adapterId);
        lock (Gate)
        {
            Cache[adapterId] = (now, status);
        }

        return status;
    }

    private static LinkStatus? ReadNow(string adapterId)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Id, adapterId, StringComparison.OrdinalIgnoreCase));
            if (nic is null)
            {
                return null;
            }

            var kind = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "wifi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "ethernet",
                _ => "other",
            };
            long? discards = null, errors = null;
            try
            {
                var stats = nic.GetIPStatistics();
                discards = stats.OutgoingPacketsDiscarded;
                errors = stats.OutgoingPacketsWithErrors;
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
            }

            var status = new LinkStatus(nic.Description, kind, nic.Speed / 1_000_000.0, discards, errors);
            if (kind == "wifi" && OperatingSystem.IsWindows() && Guid.TryParse(nic.Id, out var guid))
            {
                status = WindowsWlan.Fill(status, guid);
            }

            return status;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>Signal, rates and channel of a Wi-Fi adapter from the WLAN API (<c>wlanapi.dll</c>).</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsWlan
{
    private const int OpcodeCurrentConnection = 7; // wlan_intf_opcode_current_connection
    private const int OpcodeChannelNumber = 8; // wlan_intf_opcode_channel_number
    private const int OpcodeRssi = 0x10000102; // wlan_intf_opcode_rssi

    // WLAN_CONNECTION_ATTRIBUTES: isState (4), wlanConnectionMode (4), strProfileName (WCHAR[256] = 512), then
    // WLAN_ASSOCIATION_ATTRIBUTES: dot11Ssid (4 + 32), dot11BssType (4), dot11Bssid (6) + 2 padding, dot11PhyType (4),
    // uDot11PhyIndex (4), wlanSignalQuality (4), ulRxRate (4), ulTxRate (4) (kbit/s).
    private const int AssociationOffset = 4 + 4 + 512;
    private const int PhyTypeOffset = AssociationOffset + 36 + 4 + 8;
    private const int SignalOffset = PhyTypeOffset + 8;
    private const int RxRateOffset = SignalOffset + 4;
    private const int TxRateOffset = RxRateOffset + 4;

    /// <summary><paramref name="status"/> with the Wi-Fi values the WLAN service reports for the adapter; unchanged when it reports none.</summary>
    private static readonly object Gate = new();
    private static IntPtr _handle;

    public static LinkStatus Fill(LinkStatus status, Guid adapter)
    {
        // One client handle for the service's lifetime, opened on first use and dropped when a query fails (the WLAN
        // service restarted). A host without the WLAN service (wlanapi.dll missing) has no Wi-Fi values.
        lock (Gate)
        {
            try
            {
                if (_handle == IntPtr.Zero && WlanOpenHandle(2, IntPtr.Zero, out _, out _handle) != 0)
                {
                    _handle = IntPtr.Zero;
                    return status;
                }

                var filled = FillWith(_handle, status, adapter, out var failed);
                if (failed)
                {
                    _ = WlanCloseHandle(_handle, IntPtr.Zero);
                    _handle = IntPtr.Zero;
                }

                return filled;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return status;
            }
        }
    }

    private static LinkStatus FillWith(IntPtr handle, LinkStatus status, Guid adapter, out bool failed)
    {
        failed = false;
        {
            var conn = Query(handle, adapter, OpcodeCurrentConnection);
            if (conn is null)
            {
                // Not connected, or the handle went stale: open a new one next time.
                failed = true;
                return status;
            }

            if (conn.Length >= TxRateOffset + 4)
            {
                var phy = BitConverter.ToInt32(conn, PhyTypeOffset) switch
                {
                    4 => "a",
                    5 => "b",
                    6 => "g",
                    7 => "n",
                    8 => "ac",
                    9 => "ad",
                    10 => "ax",
                    11 => "be",
                    _ => null,
                };
                status = status with
                {
                    WifiPhy = phy,
                    WifiSignalPercent = BitConverter.ToInt32(conn, SignalOffset),
                    WifiRxMbps = BitConverter.ToUInt32(conn, RxRateOffset) / 1000.0,
                    WifiTxMbps = BitConverter.ToUInt32(conn, TxRateOffset) / 1000.0,
                };
            }

            if (Query(handle, adapter, OpcodeChannelNumber) is { Length: >= 4 } channel)
            {
                status = status with { WifiChannel = BitConverter.ToInt32(channel, 0) };
            }

            if (Query(handle, adapter, OpcodeRssi) is { Length: >= 4 } rssi)
            {
                status = status with { WifiRssiDbm = BitConverter.ToInt32(rssi, 0) };
            }

            return status;
        }
    }

    private static byte[]? Query(IntPtr handle, Guid adapter, int opcode)
    {
        if (WlanQueryInterface(handle, in adapter, opcode, IntPtr.Zero, out var size, out var data, out _) != 0 || data == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    [LibraryImport("wlanapi.dll")]
    private static partial int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [LibraryImport("wlanapi.dll")]
    private static partial int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial int WlanQueryInterface(IntPtr handle, in Guid interfaceGuid, int opcode, IntPtr reserved, out uint dataSize,
        out IntPtr data, out int valueType);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(IntPtr memory);
}
