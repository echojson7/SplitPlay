// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SplitPlay.Core.Steam;

namespace SplitPlay.Core.Host;

/// <summary>
/// <see cref="IHostRegistry"/> over the real registry (64-bit view): HKLM by default, or a user hive
/// (<c>HKEY_USERS\&lt;root&gt;</c>) as <see cref="IUserHive"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostRegistry : IUserHive
{
    private readonly RegistryHive _hive;
    private readonly string _root;

    /// <summary>HKLM.</summary>
    public WindowsHostRegistry()
        : this(RegistryHive.LocalMachine, "")
    {
    }

    /// <summary>Keys relative to <c>&lt;hive&gt;\&lt;root&gt;</c> (e.g. <c>HKEY_USERS\S-1-5-21-…</c>).</summary>
    public WindowsHostRegistry(RegistryHive hive, string root)
    {
        _hive = hive;
        _root = root.TrimEnd('\\');
    }

    private RegistryKey Base() => RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);

    private string Full(string key) => _root.Length == 0 ? key : key.Length == 0 ? _root : _root + "\\" + key;

    public bool KeyExists(string key)
    {
        using var hklm = Base();
        using var k = hklm.OpenSubKey(Full(key));
        return k is not null;
    }

    public void DeleteKeyIfEmpty(string key)
    {
        using var hklm = Base();
        using (var k = hklm.OpenSubKey(Full(key)))
        {
            if (k is null || k.ValueCount > 0 || k.SubKeyCount > 0)
            {
                return;
            }
        }

        hklm.DeleteSubKey(Full(key), throwOnMissingSubKey: false);
    }

    public RegValue? Get(string key, string name)
    {
        using var hklm = Base();
        using var k = hklm.OpenSubKey(Full(key));
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
        using var hklm = Base();
        using var k = hklm.CreateSubKey(Full(key), writable: true);
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
        using var hklm = Base();
        using var k = hklm.OpenSubKey(Full(key), writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary><see cref="IHostPower"/> through powrprof.dll and <c>powercfg.exe</c>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostPower : IHostPower
{
    private const uint AccessScheme = 16; // ACCESS_SCHEME
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorMoreData = 234;

    public Guid? ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public IReadOnlyList<Guid> Schemes()
    {
        var schemes = new List<Guid>();
        for (uint index = 0; ; index++)
        {
            var buffer = new byte[16];
            var size = (uint)buffer.Length;
            var error = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buffer, ref size);
            if (error == ErrorNoMoreItems)
            {
                return schemes;
            }

            if (error != 0)
            {
                throw new Win32Exception((int)error, "PowerEnumerate");
            }

            schemes.Add(new Guid(buffer));
        }
    }

    public void SetActiveScheme(Guid scheme)
    {
        var error = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (error != 0)
        {
            throw new Win32Exception((int)error, $"PowerSetActiveScheme({scheme})");
        }
    }

    public string? SchemeName(Guid scheme)
    {
        uint size = 0;
        var error = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if ((error != 0 && error != ErrorMoreData) || size < 2)
        {
            return null;
        }

        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
        {
            return null;
        }

        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public void SetPowerThrottling(string executable, bool exempt)
    {
        var verb = exempt ? "disable" : "reset";
        var (exitCode, text) = Powercfg("/powerthrottling", verb, "/path", executable);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"powercfg /powerthrottling {verb} exited with {exitCode}: {text}");
        }
    }

    public bool? IsPowerThrottlingDisabled(string executable)
    {
        try
        {
            // The listed paths are not localised; the words around them are.
            var (exitCode, text) = Powercfg("/powerthrottling", "list");
            return exitCode == 0 ? text.Contains(executable, StringComparison.OrdinalIgnoreCase) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    private static (int ExitCode, string Output) Powercfg(params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("powercfg did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            process.Kill();
            throw new TimeoutException($"powercfg {string.Join(' ', args)} did not finish within 15 s");
        }

        return (process.ExitCode, (output.Result + " " + errors.Result).Trim());
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        uint accessFlags, uint index, byte[] buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
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
