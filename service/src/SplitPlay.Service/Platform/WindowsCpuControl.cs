// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SplitPlay.Core.Host;

namespace SplitPlay.Service.Platform;

/// <summary>
/// Reads the CPU from <c>GetSystemCpuSetInformation</c> (cores, L3 cache per processor, efficiency class) and
/// <c>GetLogicalProcessorInformationEx(RelationCache)</c> (L3 sizes, to tell the 3D V-Cache CCD apart).
/// The parsers take raw buffers so that tests can feed them without Windows.
/// </summary>
public static class CpuTopologyReader
{
    /// <summary>A parsed <c>SYSTEM_CPU_SET_INFORMATION</c> entry: the processor and its CPU Set id.</summary>
    public sealed record CpuSetEntry(uint Id, LogicalCpu Cpu);

    /// <summary>Parses the buffer <c>GetSystemCpuSetInformation</c> fills.</summary>
    public static IReadOnlyList<CpuSetEntry> ParseCpuSets(ReadOnlySpan<byte> buffer)
    {
        var entries = new List<CpuSetEntry>();
        var offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            var type = BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 4)..]);
            if (size < 8 || offset + size > buffer.Length)
            {
                break;
            }

            // CpuSetInformation = 0: Id (4) Group (2) LogicalProcessorIndex (1) CoreIndex (1) LastLevelCacheIndex (1)
            // NumaNodeIndex (1) EfficiencyClass (1) AllFlags (1) ...
            if (type == 0 && size >= 20)
            {
                var e = buffer.Slice(offset, size);
                var id = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
                var group = BinaryPrimitives.ReadUInt16LittleEndian(e[12..]);
                entries.Add(new CpuSetEntry(id, new LogicalCpu(group, e[14], e[15], e[16], e[18])));
            }

            offset += size;
        }

        return entries;
    }

    /// <summary>
    /// Parses the buffer <c>GetLogicalProcessorInformationEx(RelationCache)</c> fills and returns the size of each L3
    /// cache by (group, last level cache index of <paramref name="cpus"/>).
    /// </summary>
    public static IReadOnlyDictionary<(int Group, int Cache), long> ParseL3Sizes(ReadOnlySpan<byte> buffer, IReadOnlyList<LogicalCpu> cpus)
    {
        ArgumentNullException.ThrowIfNull(cpus);
        var sizes = new Dictionary<(int, int), long>();
        var offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            var relationship = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 4)..]);
            if (size < 8 || offset + size > buffer.Length)
            {
                break;
            }

            // RelationCache = 2: Level (1) Associativity (1) LineSize (2) CacheSize (4) Type (4) Reserved (18)
            // GroupCount (2, 0 before Windows 11 = one mask) GroupMask[] (GROUP_AFFINITY: Mask (8) Group (2) Reserved (6)).
            var e = buffer.Slice(offset, size);
            if (relationship == 2 && size >= 56 && e[8] == 3)
            {
                var cacheBytes = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
                var groupCount = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(e[38..]));
                for (var g = 0; g < groupCount && 40 + ((g + 1) * 16) <= size; g++)
                {
                    var mask = BinaryPrimitives.ReadUInt64LittleEndian(e[(40 + (g * 16))..]);
                    var group = BinaryPrimitives.ReadUInt16LittleEndian(e[(48 + (g * 16))..]);
                    foreach (var cpu in cpus)
                    {
                        if (cpu.Group == group && cpu.Number < 64 && (mask & (1UL << cpu.Number)) != 0)
                        {
                            sizes[(cpu.Group, cpu.Cache)] = cacheBytes;
                        }
                    }
                }
            }

            offset += size;
        }

        return sizes;
    }
}

/// <summary>
/// Keeps the desk user's processes in the session on the desk's processors with default CPU Sets
/// (<c>SetProcessDefaultCpuSets</c>). CPU Sets are soft: a game that pins its own threads (affinity) still can, nothing
/// changes for <c>CREATE_BREAKAWAY_FROM_JOB</c>, and lifting them restores the process exactly. A job object with a
/// processor affinity was the first design and was dropped: an empty job assigned to a process already in a job nests
/// under it (a worker's kill-on-close job could then take Explorer and the game down), affinity is hard, and a job
/// orphaned by a service restart keeps its processes on the old processors. CPU Sets are not inherited, so a new
/// process is put on the desk's processors at the next sweep (every 2 s by default; a game spends that long loading).
/// Only processes of the desk's user are touched; SYSTEM processes of the session (DWM, the worker, the seat) are
/// left alone. Protected processes cannot be opened and keep running everywhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCpuControl : ICpuControl
{
    private readonly ILogger _logger;
    private readonly Lazy<(CpuTopology Topology, IReadOnlyDictionary<int, uint> CpuSetIds)> _cpu;

    public WindowsCpuControl(ILogger<WindowsCpuControl> logger)
    {
        _logger = logger;
        _cpu = new(() => Read(logger));
    }

    public CpuTopology Topology() => _cpu.Value.Topology;

    public IDeskCpuScope Open(string deskId, int sessionId, string user)
    {
        var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, user).Translate(typeof(SecurityIdentifier));
        return new Scope(this, deskId, sessionId, sid);
    }

    private static (CpuTopology, IReadOnlyDictionary<int, uint>) Read(ILogger logger)
    {
        try
        {
            var sets = CpuTopologyReader.ParseCpuSets(Query((p, n, out r) => GetSystemCpuSetInformation(p, n, out r, IntPtr.Zero, 0)));
            var cpus = sets.Select(s => s.Cpu).ToList();
            var l3 = CpuTopologyReader.ParseL3Sizes(Query((p, n, out r) =>
            {
                r = n;
                return GetLogicalProcessorInformationEx(2, p, ref r);
            }), cpus);
            string? name = null;
            using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                name = (key?.GetValue("ProcessorNameString") as string)?.Trim();
            }

            var topology = new CpuTopology(name, cpus, l3);
            logger.LogInformation("CPU: {Name}, {Cores} cores / {Threads} threads, {Kind}, L3 groups: {Groups}", name ?? "unknown", topology.Cores,
                cpus.Count, topology.Kind,
                string.Join("; ", topology.CacheGroups.Select(g => $"{DeskCorePlanner.Format(g.Cpus)} ({g.L3Bytes >> 20} MB)")));
            return (topology, sets.GroupBy(s => s.Cpu.Index).ToDictionary(g => g.Key, g => g.First().Id));
        }
        catch (Exception ex)
        {
            // Never cached as a failure of the Lazy: an unexpected buffer just means "nothing to split".
            logger.LogWarning("Could not read the CPU topology: {Message}", ex.Message);
            return (CpuTopology.Empty, new Dictionary<int, uint>());
        }
    }

    private delegate bool QueryFn(IntPtr buffer, uint length, out uint returned);

    private static byte[] Query(QueryFn fn)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            fn(IntPtr.Zero, 0, out var needed);
            if (needed == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var buffer = new byte[needed];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                if (fn(handle.AddrOfPinnedObject(), needed, out var returned))
                {
                    return buffer.AsSpan(0, (int)Math.Min(returned, needed)).ToArray();
                }

                if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                handle.Free();
            }
        }

        throw new Win32Exception(ERROR_INSUFFICIENT_BUFFER);
    }

    private sealed class Scope : IDeskCpuScope
    {
        private readonly WindowsCpuControl _owner;
        private readonly string _deskId;
        private readonly int _session;
        private readonly SecurityIdentifier _user;

        /// <summary>Processes handled in this scope and the set they got ("all" when cleared or untouched).</summary>
        private readonly Dictionary<int, string> _seen = [];
        private bool _first = true;
        private string _logged = "all";

        public Scope(WindowsCpuControl owner, string deskId, int session, SecurityIdentifier user)
        {
            _owner = owner;
            _deskId = deskId;
            _session = session;
            _user = user;
        }

        private ILogger Log => _owner._logger;

        public int Sweep(IReadOnlyList<int>? cpus)
        {
            var key = DeskCorePlanner.Format(cpus);
            var ids = cpus is null ? null : CpuSetIds(cpus);
            if (cpus is not null && ids is null)
            {
                return 0;
            }

            var changed = 0;
            var present = new HashSet<int>();
            foreach (var pid in UserProcesses())
            {
                present.Add(pid);
                if (_seen.TryGetValue(pid, out var had) ? had == key : cpus is null && !_first)
                {
                    // Already on this set, or a new process while not restricted: nothing to do.
                    _seen[pid] = had ?? key;
                    continue;
                }

                // The first sweep of a scope also clears sets a previous service run left (off after a restart).
                _seen[pid] = key;
                if (Apply(pid, ids))
                {
                    changed++;
                }
            }

            foreach (var gone in _seen.Keys.Where(p => !present.Contains(p)).ToList())
            {
                _seen.Remove(gone);
            }

            _first = false;
            if (key != _logged)
            {
                Log.LogInformation("Desk {Desk}: processes of session {Session} run on processors {Cpus}", _deskId, _session, key);
                _logged = key;
            }
            else if (changed > 0)
            {
                Log.LogDebug("Desk {Desk}: {Count} process(es) of session {Session} put on processors {Cpus}", _deskId, changed, _session, key);
            }

            return changed;
        }

        /// <summary>The CPU Set ids of <paramref name="cpus"/>, or null (logged) when none of them exists.</summary>
        private uint[]? CpuSetIds(IReadOnlyList<int> cpus)
        {
            var map = _owner._cpu.Value.CpuSetIds;
            var ids = cpus.Where(map.ContainsKey).Select(c => map[c]).ToArray();
            if (ids.Length == 0)
            {
                Log.LogWarning("Desk {Desk}: processors {Cpus} do not exist; the session is not restricted", _deskId, DeskCorePlanner.Format(cpus));
                return null;
            }

            return ids;
        }

        private static bool Apply(int pid, uint[]? ids)
        {
            using var process = OpenProcess(PROCESS_SET_LIMITED_INFORMATION, false, pid);
            if (process.IsInvalid)
            {
                return false;
            }

            return ids is null ? SetProcessDefaultCpuSets(process, null, 0) : SetProcessDefaultCpuSets(process, ids, (uint)ids.Length);
        }

        /// <summary>Processes in the session that run as the desk user (WTSEnumerateProcesses).</summary>
        private List<int> UserProcesses()
        {
            var pids = new List<int>();
            if (!WTSEnumerateProcessesW(IntPtr.Zero, 0, 1, out var buffer, out var count))
            {
                Log.LogDebug("Desk {Desk}: WTSEnumerateProcesses failed ({Error})", _deskId, Marshal.GetLastWin32Error());
                return pids;
            }

            try
            {
                var size = Marshal.SizeOf<WTS_PROCESS_INFOW>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WTS_PROCESS_INFOW>(buffer + (i * size));
                    if (info.SessionId == _session && info.pUserSid != IntPtr.Zero && info.ProcessId > 4 &&
                        new SecurityIdentifier(info.pUserSid).Equals(_user))
                    {
                        pids.Add(info.ProcessId);
                    }
                }
            }
            finally
            {
                WTSFreeMemory(buffer);
            }

            return pids;
        }

        /// <summary>Processes keep their CPU Sets: a desk session ending ends them anyway, a seat restart keeps them.</summary>
        public void Dispose()
        {
        }
    }

    // ------------------------------------------------------------------ interop

    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_PROCESS_INFOW
    {
        public int SessionId;
        public int ProcessId;
        public IntPtr pProcessName;
        public IntPtr pUserSid;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(SafeProcessHandle process, uint[]? cpuSetIds, uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateProcessesW(IntPtr server, int reserved, int version, out IntPtr processes, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
