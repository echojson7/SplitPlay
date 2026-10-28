// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SplitPlay.Core.Host;

/// <summary><see cref="IHostRegistry"/> over the real HKLM (64-bit view).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostRegistry : IHostRegistry
{
    private static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public RegValue? Get(string key, string name)
    {
        using var hklm = Hklm();
        using var k = hklm.OpenSubKey(key);
        if (k is null || k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
        {
            return null;
        }

        return k.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => RegValue.DWord(unchecked((uint)(int)value)),
            RegistryValueKind.ExpandString => new RegValue(RegKind.ExpandString, (string)value),
            RegistryValueKind.String => new RegValue(RegKind.String, (string)value),
            RegistryValueKind.QWord => new RegValue(RegKind.DWord, ((long)value).ToString(CultureInfo.InvariantCulture)),
            var other => new RegValue(RegKind.String, Convert.ToString(value, CultureInfo.InvariantCulture) ?? other.ToString()),
        };
    }

    public void Set(string key, string name, RegValue value)
    {
        using var hklm = Hklm();
        using var k = hklm.CreateSubKey(key, writable: true);
        switch (value.Kind)
        {
            case RegKind.DWord:
                k.SetValue(name, unchecked((int)uint.Parse(value.Data, CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
                break;
            case RegKind.ExpandString:
                k.SetValue(name, value.Data, RegistryValueKind.ExpandString);
                break;
            default:
                k.SetValue(name, value.Data, RegistryValueKind.String);
                break;
        }
    }

    public void Delete(string key, string name)
    {
        using var hklm = Hklm();
        using var k = hklm.OpenSubKey(key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary><see cref="IHostServices"/> through the Service Control Manager.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostServices : IHostServices
{
    public int? GetStartType(string name)
    {
        // The registry reflects what the SCM uses at the next boot and what ChangeServiceConfig writes.
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return k?.GetValue("Start") is int start ? start : null;
    }

    public void SetStartType(string name, int startType)
    {
        using var scm = Scm.Open(Scm.SC_MANAGER_CONNECT);
        using var svc = scm.OpenService(name, Scm.SERVICE_CHANGE_CONFIG);
        if (!Scm.ChangeServiceConfigW(svc.Handle, Scm.SERVICE_NO_CHANGE, (uint)startType, Scm.SERVICE_NO_CHANGE,
                null, null, IntPtr.Zero, null, null, null, null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ChangeServiceConfig({name})");
        }
    }

    public bool IsRunning(string name) => Query(name) is { } status && status.dwCurrentState == Scm.SERVICE_RUNNING;

    public void Start(string name)
    {
        using var scm = Scm.Open(Scm.SC_MANAGER_CONNECT);
        using var svc = scm.OpenService(name, Scm.SERVICE_START | Scm.SERVICE_QUERY_STATUS);
        if (!Scm.StartServiceW(svc.Handle, 0, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1056 /* ERROR_SERVICE_ALREADY_RUNNING */)
            {
                throw new Win32Exception(error, $"StartService({name})");
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (IsRunning(name))
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"{name} did not reach the running state within 30 s");
    }

    public DateTimeOffset? ProcessStartTime(string name)
    {
        if (Query(name) is not { dwCurrentState: Scm.SERVICE_RUNNING, dwProcessId: > 0 } status)
        {
            return null;
        }

        var process = Scm.OpenProcess(Scm.PROCESS_QUERY_LIMITED_INFORMATION, false, status.dwProcessId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Scm.GetProcessTimes(process, out var creation, out _, out _, out _)
                ? DateTimeOffset.FromFileTime(creation)
                : null;
        }
        finally
        {
            Scm.CloseHandle(process);
        }
    }

    /// <summary>Process id of a running service (also per-user service instances), or null.</summary>
    public static int? ProcessId(string name) =>
        Query(name) is { dwCurrentState: Scm.SERVICE_RUNNING, dwProcessId: > 0 } status ? (int)status.dwProcessId : null;

    /// <summary>Sends SERVICE_CONTROL_STOP without waiting. Returns false if the service is not running.</summary>
    public static bool RequestStop(string name)
    {
        using var scm = Scm.Open(Scm.SC_MANAGER_CONNECT);
        using var svc = scm.OpenService(name, Scm.SERVICE_STOP | Scm.SERVICE_QUERY_STATUS);
        if (Scm.ControlService(svc.Handle, Scm.SERVICE_CONTROL_STOP, out _))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        return error == 1062 /* ERROR_SERVICE_NOT_ACTIVE */ ? false : throw new Win32Exception(error, $"ControlService({name}, STOP)");
    }

    private static Scm.SERVICE_STATUS_PROCESS? Query(string name)
    {
        using var scm = Scm.Open(Scm.SC_MANAGER_CONNECT);
        var handle = Scm.OpenServiceW(scm.Handle, name, Scm.SERVICE_QUERY_STATUS);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Scm.QueryServiceStatusEx(handle, 0 /* SC_STATUS_PROCESS_INFO */, out var status,
                Marshal.SizeOf<Scm.SERVICE_STATUS_PROCESS>(), out _)
                ? status
                : null;
        }
        finally
        {
            Scm.CloseServiceHandle(handle);
        }
    }

    /// <summary>Minimal advapi32/kernel32 interop for the SCM.</summary>
    internal static class Scm
    {
        public const uint SC_MANAGER_CONNECT = 0x0001;
        public const uint SERVICE_QUERY_STATUS = 0x0004;
        public const uint SERVICE_CHANGE_CONFIG = 0x0002;
        public const uint SERVICE_START = 0x0010;
        public const uint SERVICE_STOP = 0x0020;
        public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
        public const uint SERVICE_RUNNING = 4;
        public const uint SERVICE_STOPPED = 1;
        public const uint SERVICE_CONTROL_STOP = 1;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
            public uint dwProcessId;
            public uint dwServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        public sealed class ScHandle(IntPtr handle) : IDisposable
        {
            public IntPtr Handle { get; } = handle;

            public ScHandle OpenService(string name, uint access)
            {
                var h = OpenServiceW(Handle, name, access);
                return h == IntPtr.Zero ? throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenService({name})") : new ScHandle(h);
            }

            public void Dispose() => CloseServiceHandle(Handle);
        }

        public static ScHandle Open(uint access)
        {
            var h = OpenSCManagerW(null, null, access);
            return h == IntPtr.Zero ? throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager") : new ScHandle(h);
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out SERVICE_STATUS_PROCESS status, int size, out int needed);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl,
            string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? startName, string? password, string? displayName);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool StartServiceW(IntPtr service, uint argc, IntPtr argv);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
