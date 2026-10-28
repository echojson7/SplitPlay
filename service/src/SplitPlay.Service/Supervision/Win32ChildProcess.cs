// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SplitPlay.Core.Platform;

namespace SplitPlay.Service.Supervision;

/// <summary>
/// A child started with <c>CreateProcess</c> / <c>CreateProcessAsUser</c> inside its own job object
/// (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>), assigned at creation through <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c> so
/// that nothing can escape it. For a session-N child the service duplicates its own (SYSTEM) primary token, sets
/// <c>TokenSessionId</c>, builds the SYSTEM environment block and starts it on <c>winsta0\default</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32ChildProcess : IChildProcess
{
    private readonly SafeProcessHandle _process;
    private readonly IntPtr _job;
    private readonly RegisteredWaitHandle _registration;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private Win32ChildProcess(SafeProcessHandle process, int pid, IntPtr job)
    {
        _process = process;
        _job = job;
        Pid = pid;
        var wait = new ProcessWaitHandle(process);
        _registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) =>
        {
            _exited.TrySetResult(GetExitCodeProcess(_process, out var code) ? (int)code : -1);
            wait.Dispose();
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int Pid { get; }

    public Task<int> Exited => _exited.Task;

    public static Win32ChildProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory, int? sessionId)
    {
        var commandLine = new StringBuilder(WindowsCommandLine.Build(executable, arguments));
        var job = CreateJob();
        var attributes = IntPtr.Zero;
        var token = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var jobSlot = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            // PROC_THREAD_ATTRIBUTE_JOB_LIST: the process starts inside the job (no window in which it runs outside).
            Marshal.WriteIntPtr(jobSlot, job);
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            Win32Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size), "InitializeProcThreadAttributeList");
            Win32Check(UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, jobSlot, IntPtr.Size, IntPtr.Zero, IntPtr.Zero),
                "UpdateProcThreadAttribute(JOB_LIST)");

            var startup = new STARTUPINFOEXW();
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEXW>();
            startup.lpAttributeList = attributes;
            var flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT;
            PROCESS_INFORMATION info;
            bool ok;
            if (sessionId is int session)
            {
                token = SessionToken(session);
                Win32Check(CreateEnvironmentBlock(out environment, token, false), "CreateEnvironmentBlock");
                startup.StartupInfo.lpDesktop = @"winsta0\default";
                ok = CreateProcessAsUserW(token, executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, environment,
                    workingDirectory, ref startup, out info);
            }
            else
            {
                flags &= ~CREATE_UNICODE_ENVIRONMENT;
                ok = CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, workingDirectory, ref startup, out info);
            }

            Win32Check(ok, sessionId is null ? "CreateProcess" : "CreateProcessAsUser");
            CloseHandle(info.hThread);
            var child = new Win32ChildProcess(new SafeProcessHandle(info.hProcess, ownsHandle: true), info.dwProcessId, job);
            job = IntPtr.Zero;
            return child;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            Marshal.FreeHGlobal(jobSlot);
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }

            if (job != IntPtr.Zero)
            {
                CloseHandle(job);
            }
        }
    }

    /// <summary>A primary duplicate of the service's own token with <c>TokenSessionId</c> = <paramref name="session"/>.</summary>
    private static IntPtr SessionToken(int session)
    {
        Win32Check(OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES | TOKEN_ASSIGN_PRIMARY, out var self),
            "OpenProcessToken");
        try
        {
            // SYSTEM holds these; SeTcbPrivilege is required for TokenSessionId. Enabling is best effort.
            EnablePrivilege(self, "SeTcbPrivilege");
            EnablePrivilege(self, "SeAssignPrimaryTokenPrivilege");
            EnablePrivilege(self, "SeIncreaseQuotaPrivilege");
            Win32Check(DuplicateTokenEx(self, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var duplicate), "DuplicateTokenEx");
            if (!SetTokenInformation(duplicate, TokenSessionId, ref session, sizeof(int)))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(duplicate);
                throw new Win32Exception(error, $"SetTokenInformation(TokenSessionId={session}) failed (the service must run as LocalSystem)");
            }

            return duplicate;
        }
        finally
        {
            CloseHandle(self);
        }
    }

    private static void EnablePrivilege(IntPtr token, string name)
    {
        if (!LookupPrivilegeValueW(null, name, out var luid))
        {
            return;
        }

        var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
        AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr CreateJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(job);
            throw new Win32Exception(error, "SetInformationJobObject");
        }

        return job;
    }

    public void Kill()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            TerminateJobObject(_job, 1);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try
        {
            await Exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _registration.Unregister(null);
        CloseHandle(_job);
        _process.Dispose();
    }

    private static void Win32Check(bool ok, string what)
    {
        if (!ok)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"{what} failed: {new Win32Exception(error).Message}");
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            var current = GetCurrentProcess();
            if (!DuplicateHandle(current, process.DangerousGetHandle(), current, out var duplicate, 0, false, DUPLICATE_SAME_ACCESS))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle");
            }

            SafeWaitHandle = new SafeWaitHandle(duplicate, ownsHandle: true);
        }
    }

    // ------------------------------------------------------------------ interop

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000D;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;
    private const uint DUPLICATE_SAME_ACCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref STARTUPINFOEXW startup, out PROCESS_INFORMATION info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref STARTUPINFOEXW startup,
        out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access,
        bool inherit, uint options);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref int value, int length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int length, IntPtr previous, IntPtr returnLength);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
