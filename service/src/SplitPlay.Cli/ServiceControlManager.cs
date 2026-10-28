// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SplitPlay.Cli;

/// <summary>Settings applied by <see cref="ServiceControlManager.InstallOrUpdate"/>.</summary>
internal sealed record ServiceInstallSpec(
    string Name,
    string DisplayName,
    string Description,
    string CommandLine,
    bool DelayedAutoStart,
    IReadOnlyList<TimeSpan> RestartDelays,
    TimeSpan FailureResetPeriod);

/// <summary>Service Control Manager P/Invoke (advapi32) for install, reconfigure and delete.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class ServiceControlManager
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint Delete = 0x00010000;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceConfigDescription = 1;
    private const uint ServiceConfigFailureActions = 2;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const uint ServiceConfigFailureActionsFlag = 4;
    private const int ScActionRestart = 1;
    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;

    /// <summary>True if a service with this name exists.</summary>
    public static bool Exists(string name)
    {
        using var scm = OpenManager(ScManagerConnect);
        var service = OpenServiceW(scm.Handle, name, ServiceQueryStatus);
        if (service == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorServiceDoesNotExist ? false : throw new Win32Exception(error, $"OpenService({name}) failed");
        }

        CloseServiceHandle(service);
        return true;
    }

    /// <summary>
    /// Creates the service as LocalSystem, auto start (delayed), with restart-on-failure recovery actions.
    /// If it already exists its configuration is updated instead. Returns true when it was created.
    /// </summary>
    public static bool InstallOrUpdate(ServiceInstallSpec spec)
    {
        using var scm = OpenManager(ScManagerConnect | ScManagerCreateService);
        var created = true;
        var service = CreateServiceW(scm.Handle, spec.Name, spec.DisplayName, ServiceAllAccess, ServiceWin32OwnProcess,
            ServiceAutoStart, ServiceErrorNormal, spec.CommandLine, null, IntPtr.Zero, null, null, null);
        if (service == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorServiceExists)
            {
                throw new Win32Exception(error, $"CreateService({spec.Name}) failed");
            }

            created = false;
            service = OpenServiceW(scm.Handle, spec.Name, ServiceAllAccess);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"OpenService({spec.Name}) failed");
            }
        }

        try
        {
            if (!created && !ChangeServiceConfigW(service, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal,
                    spec.CommandLine, null, IntPtr.Zero, null, null, null, spec.DisplayName))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "ChangeServiceConfig failed");
            }

            fixed (char* description = spec.Description)
            {
                var info = new ServiceDescription { Description = description };
                Check(ChangeServiceConfig2W(service, ServiceConfigDescription, &info), "description");
            }

            var delayed = new ServiceDelayedAutoStartInfo { DelayedAutostart = spec.DelayedAutoStart ? 1 : 0 };
            Check(ChangeServiceConfig2W(service, ServiceConfigDelayedAutoStartInfo, &delayed), "delayed auto start");

            var actions = spec.RestartDelays.Select(d => new ScAction { Type = ScActionRestart, Delay = (uint)d.TotalMilliseconds }).ToArray();
            fixed (ScAction* actionPtr = actions)
            {
                var failure = new ServiceFailureActions
                {
                    ResetPeriod = (uint)spec.FailureResetPeriod.TotalSeconds,
                    RebootMessage = null,
                    Command = null,
                    ActionCount = (uint)actions.Length,
                    Actions = actionPtr,
                };
                Check(ChangeServiceConfig2W(service, ServiceConfigFailureActions, &failure), "failure actions");
            }

            // Also restart when the service stops with a non-zero exit code, not only on crashes.
            var flag = new ServiceFailureActionsFlag { FailureActionsOnNonCrashFailures = 1 };
            Check(ChangeServiceConfig2W(service, ServiceConfigFailureActionsFlag, &flag), "failure actions flag");
        }
        finally
        {
            CloseServiceHandle(service);
        }

        return created;
    }

    /// <summary>Marks the service for deletion. Returns false if it did not exist.</summary>
    public static bool Remove(string name)
    {
        using var scm = OpenManager(ScManagerConnect);
        var service = OpenServiceW(scm.Handle, name, Delete);
        if (service == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorServiceDoesNotExist ? false : throw new Win32Exception(error, $"OpenService({name}) failed");
        }

        try
        {
            if (!DeleteService(service))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorServiceMarkedForDelete)
                {
                    throw new Win32Exception(error, $"DeleteService({name}) failed");
                }
            }
        }
        finally
        {
            CloseServiceHandle(service);
        }

        return true;
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"ChangeServiceConfig2 ({what}) failed");
        }
    }

    private static ScHandle OpenManager(uint access)
    {
        var handle = OpenSCManagerW(null, null, access);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error, error == 5 ? "access denied: run from an elevated (administrator) prompt" : "OpenSCManager failed");
        }

        return new ScHandle(handle);
    }

    private readonly struct ScHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public void Dispose() => CloseServiceHandle(Handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public char* Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDelayedAutoStartInfo
    {
        public int DelayedAutostart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActionsFlag
    {
        public int FailureActionsOnNonCrashFailures;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public char* RebootMessage;
        public char* Command;
        public uint ActionCount;
        public ScAction* Actions;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateServiceW(IntPtr scManager, string serviceName, string displayName, uint desiredAccess,
        uint serviceType, uint startType, uint errorControl, string binaryPathName, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? serviceStartName, string? password);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? serviceStartName,
        string? password, string? displayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, void* info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(IntPtr service);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
