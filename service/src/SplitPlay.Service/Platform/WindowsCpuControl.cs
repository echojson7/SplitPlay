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
/// The desk session's processes join a job object per desk session (no kill-on-close: closing it never ends a game).
/// A restriction is the job's processor affinity (<c>JOB_OBJECT_LIMIT_AFFINITY</c>), which children inherit from the
/// moment they start, so a game started from Explorer or Steam is on the desk's cores from its first instruction. A
/// process that cannot join (protected, or in a job that forbids nesting) gets the same processors as its default CPU
/// Sets instead. Only processes of the desk's user are touched; SYSTEM processes of the session (DWM, the worker, the
/// seat) are left alone.
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
            return (topology, sets.ToDictionary(s => s.Cpu.Index, s => s.Id));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
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
        private readonly HashSet<int> _seen = [];
        private readonly Dictionary<int, SafeProcessHandle> _outside = [];
        private IntPtr _job;
        private string? _applied;
        private IReadOnlyList<int>? _cpus;

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
            if (_applied is null && cpus is null)
            {
                // Never restricted: leave the session's processes exactly as Windows made them.
                return 0;
            }

            if (key != _applied)
            {
                Apply(cpus);
                _applied = key;
                _cpus = cpus;
                if (cpus is null)
                {
                    // Lifted: processes keep the job (now on every processor); new ones need nothing.
                    _applied = null;
                    return 0;
                }
            }

            return JoinNewProcesses();
        }

        private void Apply(IReadOnlyList<int>? cpus)
        {
            _job = _job != IntPtr.Zero ? _job : CreateJob();
            var limits = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_AFFINITY, Affinity = (UIntPtr)AffinityMask(cpus) };
            if (!SetInformationJobObject(_job, JobObjectBasicLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_BASIC_LIMIT_INFORMATION>()))
            {
                Log.LogWarning("Desk {Desk}: could not set the processors of the session's job: {Message}", _deskId,
                    new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            foreach (var (pid, process) in _outside.ToList())
            {
                if (!SetCpuSets(process, cpus))
                {
                    process.Dispose();
                    _outside.Remove(pid);
                }
            }

            Log.LogInformation("Desk {Desk}: processes of session {Session} run on processors {Cpus}", _deskId, _session, DeskCorePlanner.Format(cpus));
        }

        /// <summary>The affinity mask for <paramref name="cpus"/>; every processor when null or not in group 0.</summary>
        private ulong AffinityMask(IReadOnlyList<int>? cpus)
        {
            GetProcessAffinityMask(GetCurrentProcess(), out _, out var system);
            var all = (ulong)system;
            if (cpus is null)
            {
                return all;
            }

            var mask = 0UL;
            foreach (var c in cpus)
            {
                if (c is >= 0 and < 64)
                {
                    mask |= 1UL << c;
                }
            }

            mask &= all;
            if (mask == 0)
            {
                Log.LogWarning("Desk {Desk}: processors {Cpus} are not in processor group 0; the session is not restricted", _deskId,
                    DeskCorePlanner.Format(cpus));
                return all;
            }

            return mask;
        }

        private int JoinNewProcesses()
        {
            var joined = 0;
            var present = new HashSet<int>();
            foreach (var pid in UserProcesses())
            {
                present.Add(pid);
                if (!_seen.Add(pid))
                {
                    continue;
                }

                if (Join(pid))
                {
                    joined++;
                }
            }

            _seen.IntersectWith(present);
            foreach (var pid in _outside.Keys.Where(p => !present.Contains(p)).ToList())
            {
                _outside[pid].Dispose();
                _outside.Remove(pid);
            }

            if (joined > 0)
            {
                Log.LogDebug("Desk {Desk}: {Count} new process(es) of session {Session} on processors {Cpus}", _deskId, joined, _session,
                    DeskCorePlanner.Format(_cpus));
            }

            return joined;
        }

        private bool Join(int pid)
        {
            var process = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_LIMITED_INFORMATION,
                false, pid);
            if (process.IsInvalid)
            {
                process.Dispose();
                return false;
            }

            if (IsProcessInJob(process, _job, out var inJob) && inJob)
            {
                // A child of a process that already joined: it inherited the job (and its processors).
                process.Dispose();
                return false;
            }

            if (AssignProcessToJobObject(_job, process))
            {
                process.Dispose();
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            if (SetCpuSets(process, _cpus))
            {
                Log.LogDebug("Desk {Desk}: process {Pid} could not join the session's job ({Error}); using its CPU Sets", _deskId, pid, error);
                _outside[pid] = process;
                return true;
            }

            process.Dispose();
            return false;
        }

        private bool SetCpuSets(SafeProcessHandle process, IReadOnlyList<int>? cpus)
        {
            if (cpus is null)
            {
                return SetProcessDefaultCpuSets(process, null, 0);
            }

            var ids = _owner._cpu.Value.CpuSetIds;
            var sets = cpus.Where(ids.ContainsKey).Select(c => ids[c]).ToArray();
            return sets.Length > 0 && SetProcessDefaultCpuSets(process, sets, (uint)sets.Length);
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

        private IntPtr CreateJob()
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
            }

            return job;
        }

        public void Dispose()
        {
            foreach (var process in _outside.Values)
            {
                process.Dispose();
            }

            _outside.Clear();
            if (_job != IntPtr.Zero)
            {
                // No kill-on-close: the processes keep running (and keep the job and its processors until they exit).
                CloseHandle(_job);
                _job = IntPtr.Zero;
            }
        }
    }

    // ------------------------------------------------------------------ interop

    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int JobObjectBasicLimitInformation = 2;
    private const uint JOB_OBJECT_LIMIT_AFFINITY = 0x00000010;
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_BASIC_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateProcessesW(IntPtr server, int reserved, int version, out IntPtr processes, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
