// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SplitPlay.Core.Api;

namespace SplitPlay.Service.Hosting;

/// <summary>
/// Lists hardware GPUs through DXGI (<c>IDXGIFactory1::EnumAdapters1</c>). Utilization, encoder load and VRAM use are
/// not measured yet (Stage 4: D3DKMT / NVML), so those fields are null. Software adapters are skipped.
/// The list is cached for a minute. Returns an empty list off Windows or on any failure.
/// </summary>
public sealed class GpuInventory
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger<GpuInventory> _logger;
    private IReadOnlyList<GpuDto> _cached = [];
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public GpuInventory(TimeProvider time, ILogger<GpuInventory> logger)
    {
        _time = time;
        _logger = logger;
    }

    public IReadOnlyList<GpuDto> Get()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now - _cachedAt < CacheFor)
            {
                return _cached;
            }

            _cachedAt = now;
            try
            {
                _cached = OperatingSystem.IsWindows() ? Enumerate() : [];
            }
            catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                _logger.LogDebug("DXGI enumeration failed: {Message}", ex.Message);
                _cached = [];
            }

            return _cached;
        }
    }

    /// <summary>Physical memory (used, total) in MB, or nulls when unknown.</summary>
    public static (double? UsedMb, double? TotalMb) PhysicalMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                return (Math.Round((status.TotalPhys - status.AvailPhys) / 1024.0 / 1024.0), Math.Round(status.TotalPhys / 1024.0 / 1024.0));
            }
        }

        var info = GC.GetGCMemoryInfo();
        return (info.MemoryLoadBytes > 0 ? Math.Round(info.MemoryLoadBytes / 1024.0 / 1024.0) : null,
            info.TotalAvailableMemoryBytes > 0 ? Math.Round(info.TotalAvailableMemoryBytes / 1024.0 / 1024.0) : null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>Formats an adapter LUID like the config's <c>gpu</c> field: <c>0x</c> + 16 hex digits.</summary>
    public static string FormatLuid(uint low, int high) =>
        "0x" + (((ulong)(uint)high << 32) | low).ToString("X16", CultureInfo.InvariantCulture);

    [SupportedOSPlatform("windows")]
    private static unsafe List<GpuDto> Enumerate()
    {
        var list = new List<GpuDto>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IID_IDXGIFactory1
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(&iid, out var factory));
        try
        {
            var vtable = *(IntPtr**)factory;
            // IDXGIFactory1::EnumAdapters1 is slot 12 (IUnknown 0-2, IDXGIObject 3-6, IDXGIFactory 7-11).
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtable[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                var hr = enumAdapters1(factory, i, &adapter);
                if (hr == DxgiErrorNotFound)
                {
                    break;
                }

                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    // IDXGIAdapter1::GetDesc1 is slot 10 (IDXGIObject 3-6, IDXGIAdapter 7-9).
                    var adapterVtable = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, DxgiAdapterDesc1*, int>)adapterVtable[10];
                    DxgiAdapterDesc1 desc;
                    Marshal.ThrowExceptionForHR(getDesc1(adapter, &desc));
                    // Skip software adapters, including the Microsoft Basic Render Driver (VEN_1414 DEV_008C),
                    // which does not always carry the software flag.
                    if ((desc.Flags & DxgiAdapterFlagSoftware) != 0 || (desc.VendorId == 0x1414 && desc.DeviceId == 0x8C))
                    {
                        continue;
                    }

                    var name = new string(desc.Description);
                    list.Add(new GpuDto(FormatLuid(desc.LuidLow, desc.LuidHigh), name, null, null, null,
                        Math.Round(desc.DedicatedVideoMemory / 1024.0 / 1024.0)));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return list;
    }

    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DxgiAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern unsafe int CreateDXGIFactory1(Guid* riid, out IntPtr factory);
}
