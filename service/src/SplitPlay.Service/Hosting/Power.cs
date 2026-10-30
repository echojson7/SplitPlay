// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using SplitPlay.Core.Api;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Configuration;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Hosting;

/// <summary>A change of the PC's power state.</summary>
public enum PowerChange
{
    /// <summary>The PC is about to sleep or hibernate (PBT_APMSUSPEND).</summary>
    Suspend,

    /// <summary>The PC woke up (PBT_APMRESUMEAUTOMATIC / PBT_APMRESUMESUSPEND).</summary>
    Resume,
}

/// <summary>Power notifications for the supervisor; raised by <see cref="SplitPlayServiceLifetime"/> (and tests).</summary>
public sealed class PowerEvents(ILogger<PowerEvents> logger)
{
    public event Action<PowerChange>? Changed;

    public void Raise(PowerChange change)
    {
        logger.LogInformation(change == PowerChange.Suspend ? "The PC is going to sleep" : "The PC woke up");
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Power event handler failed");
        }
    }
}

/// <summary>
/// The Windows service lifetime with SERVICE_CONTROL_POWEREVENT: Windows tells the service about sleep and resume,
/// which <see cref="PowerEvents"/> passes on. Windows waits only about two seconds for the suspend notification, so
/// the handler only starts the work.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SplitPlayServiceLifetime : WindowsServiceLifetime
{
    private readonly PowerEvents _power;

    public SplitPlayServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor, IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor, PowerEvents power)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _power = power;
        CanHandlePowerEvent = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                _power.Raise(PowerChange.Suspend);
                break;
            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeSuspend:
            case PowerBroadcastStatus.ResumeCritical:
                _power.Raise(PowerChange.Resume);
                break;
        }

        return base.OnPowerEvent(powerStatus);
    }
}

/// <summary>A system-required power request: keeps Windows from sleeping on its own while held.</summary>
public interface IPowerRequest
{
    void Set(string reason);

    void Clear();
}

/// <summary>Tests and non-Windows hosts: remembers whether the request is held.</summary>
public sealed class FakePowerRequest : IPowerRequest
{
    public string? Reason { get; private set; }

    public bool Held => Reason is not null;

    public void Set(string reason) => Reason = reason;

    public void Clear() => Reason = null;
}

/// <summary>PowerCreateRequest / PowerSetRequest(PowerRequestSystemRequired); shown by <c>powercfg /requests</c>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerRequest : IPowerRequest, IDisposable
{
    private const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;
    private const int PowerRequestSystemRequired = 1;

    private readonly object _gate = new();
    private SafePowerRequestHandle? _request;

    public void Set(string reason)
    {
        lock (_gate)
        {
            ClearLocked();
            var context = new REASON_CONTEXT
            {
                Version = POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = reason,
            };
            var handle = PowerCreateRequest(ref context);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "PowerCreateRequest");
            }

            if (!PowerSetRequest(handle, PowerRequestSystemRequired))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "PowerSetRequest");
            }

            _request = handle;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearLocked();
        }
    }

    public void Dispose() => Clear();

    private void ClearLocked()
    {
        if (_request is null)
        {
            return;
        }

        PowerClearRequest(_request, PowerRequestSystemRequired);
        _request.Dispose();
        _request = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
    }

    private sealed class SafePowerRequestHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafePowerRequestHandle PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(SafePowerRequestHandle request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(SafePowerRequestHandle request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Keeps the PC awake while a Moonlight client streams from any desk (<c>host.keepAwake</c>, default on): holds a
/// system-required power request from the first stream until the last one ends. Sleeping by hand (Start → Sleep, the
/// lid) still works; <see cref="DeskSupervisor"/> then stops the workers and restarts them after resume.
/// </summary>
public sealed class SleepGuard(DeskSupervisor desks, ConfigService config, IPowerRequest power, ILogger<SleepGuard> logger) : IHostedService
{
    private readonly object _gate = new();
    private readonly HashSet<string> _streaming = new(StringComparer.Ordinal);
    private bool _keepAwake = true;
    private bool _held;

    /// <summary>True while the power request is held (tests).</summary>
    public bool Holding
    {
        get
        {
            lock (_gate)
            {
                return _held;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        desks.DeskChanged += OnDeskChanged;
        foreach (var desk in desks.List())
        {
            OnDeskChanged(desk);
        }

        Refresh();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        desks.DeskChanged -= OnDeskChanged;
        lock (_gate)
        {
            if (_held)
            {
                power.Clear();
                _held = false;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Re-reads <c>host.keepAwake</c> (after the host settings change).</summary>
    public void Refresh()
    {
        var keepAwake = config.Snapshot().Host.KeepAwake;
        lock (_gate)
        {
            _keepAwake = keepAwake;
            ApplyLocked();
        }
    }

    // Raised under a desk lock: touches only this object's own state.
    private void OnDeskChanged(DeskDto desk)
    {
        lock (_gate)
        {
            var streaming = desk.Stream is not null || desk.State == DeskState.Streaming;
            var changed = streaming ? _streaming.Add(desk.Id) : _streaming.Remove(desk.Id);
            if (changed)
            {
                ApplyLocked();
            }
        }
    }

    private void ApplyLocked()
    {
        var want = _keepAwake && _streaming.Count > 0;
        try
        {
            if (want && !_held)
            {
                power.Set($"SplitPlay: streaming from {string.Join(", ", _streaming.Order(StringComparer.Ordinal))}");
                _held = true;
                logger.LogInformation("Keeping the PC awake while desks stream ({Desks})", string.Join(", ", _streaming));
            }
            else if (!want && _held)
            {
                power.Clear();
                _held = false;
                logger.LogInformation("No stream needs the PC awake any more; Windows may sleep again");
            }
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning("Could not change the power request: {Message}", ex.Message);
        }
    }
}
