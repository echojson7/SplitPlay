// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;
using SplitPlay.Core;
using SplitPlay.Core.Api;

namespace SplitPlay.Service.Hosting;

/// <summary><see cref="IServiceControl"/> through <see cref="ServiceController"/> (the service runs as LocalSystem).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceControl(IHostApplicationLifetime lifetime) : IServiceControl
{
    private const int ErrorServiceDoesNotExist = 1060;

    public ServiceInfo Query(string name)
    {
        using var sc = new ServiceController(name);
        try
        {
            var status = sc.Status switch
            {
                ServiceControllerStatus.Running => HostServiceStatus.Running,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => HostServiceStatus.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => HostServiceStatus.Stopping,
                // Paused counts as stopped; none of the listed services can be paused.
                _ => HostServiceStatus.Stopped,
            };
            var isDriver = (sc.ServiceType & (ServiceType.KernelDriver | ServiceType.FileSystemDriver)) != 0;
            return new ServiceInfo(sc.ServiceName, sc.DisplayName, status, StartTypeOf(sc), sc.Status == ServiceControllerStatus.Running && sc.CanStop, isDriver);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: ErrorServiceDoesNotExist })
        {
            return ServiceInfo.Missing(name);
        }
    }

    public void Start(string name)
    {
        using var sc = new ServiceController(name);
        try
        {
            sc.Start();
        }
        catch (InvalidOperationException ex)
        {
            throw new ServiceControlException($"could not start {name}: {(ex.InnerException ?? ex).Message}", ex);
        }
    }

    public void Stop(string name)
    {
        if (string.Equals(name, BuildInfo.ServiceName, StringComparison.OrdinalIgnoreCase) && !WindowsServiceHelpers.IsWindowsService())
        {
            // Started with --console: there is no service to stop, only this process.
            lifetime.StopApplication();
            return;
        }

        using var sc = new ServiceController(name);
        try
        {
            // Stops running dependents first (UmRdpService before TermService), waiting for each.
            sc.Stop(stopDependentServices: true);
        }
        catch (InvalidOperationException ex)
        {
            throw new ServiceControlException($"could not stop {name}: {(ex.InnerException ?? ex).Message}", ex);
        }
    }

    private static string StartTypeOf(ServiceController sc)
    {
        switch (sc.StartType)
        {
            case ServiceStartMode.Automatic:
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{sc.ServiceName}"))
                {
                    return key?.GetValue("DelayedAutostart") is 1 ? "automaticDelayed" : "automatic";
                }

            case ServiceStartMode.Manual:
                return "manual";
            case ServiceStartMode.Disabled:
                return "disabled";
            case ServiceStartMode.Boot:
                return "boot";
            case ServiceStartMode.System:
                return "system";
            default:
                return sc.StartType.ToString().ToLowerInvariant();
        }
    }
}
