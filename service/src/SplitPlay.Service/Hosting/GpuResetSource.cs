// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SplitPlay.Service.Hosting;

/// <summary>A sign that the graphics driver was reset: where it came from and, if known, the error behind it.</summary>
/// <param name="Source">"Windows" (the System event log) or the id of the desk whose worker lost its GPU device.</param>
/// <param name="Detail">The worker's HRESULT and device-removed reason, or null.</param>
public sealed record GpuResetSignal(string Source, string? Detail = null);

/// <summary>Reports graphics driver resets (TDR) that Windows records. Raised on a thread-pool thread.</summary>
public interface IGpuResetSource : IDisposable
{
    event Action<GpuResetSignal>? Reset;

    /// <summary>Starts watching; a source that cannot watch logs why and stays quiet.</summary>
    void Start();
}

/// <summary>The fake platform and tests: <see cref="Raise"/> simulates a reset.</summary>
public sealed class FakeGpuResetSource : IGpuResetSource
{
    public event Action<GpuResetSignal>? Reset;

    public bool Started { get; private set; }

    public void Raise(GpuResetSignal? signal = null) => Reset?.Invoke(signal ?? new GpuResetSignal("Windows"));

    public void Start() => Started = true;

    public void Dispose()
    {
    }
}

/// <summary>
/// Subscribes to the System event log for event 4101 of the "Display" provider: "Display driver … stopped responding
/// and has successfully recovered", which Windows writes after every TDR. The subscription is push (no polling); the
/// event itself is not read, its arrival is the signal.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsGpuResetSource : IGpuResetSource
{
    internal const string Query = "*[System[Provider[@Name='Display'] and (EventID=4101)]]";

    private const int EvtSubscribeToFutureEvents = 1;
    private const int EvtSubscribeActionError = 0;
    private const int EvtSubscribeActionDeliver = 1;

    private readonly ILogger<WindowsGpuResetSource> _logger;
    private readonly object _gate = new();
    private GCHandle _self;
    private nint _subscription;
    private volatile bool _disposed;

    public WindowsGpuResetSource(ILogger<WindowsGpuResetSource> logger)
    {
        _logger = logger;
    }

    public event Action<GpuResetSignal>? Reset;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _subscription != 0)
            {
                return;
            }

            _self = GCHandle.Alloc(this);
            _subscription = EvtSubscribe(0, 0, "System", Query, 0, GCHandle.ToIntPtr(_self), &OnEvent, EvtSubscribeToFutureEvents);
            if (_subscription == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                _self.Free();
                _logger.LogWarning("Watching the event log for graphics driver resets failed (error {Error}); desks still recover through their workers",
                    error);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_subscription != 0)
            {
                // The GCHandle is deliberately never freed: whether EvtClose waits for a callback that is already running
                // is not documented, and a freed handle there would crash the service. It holds one small object.
                EvtClose(_subscription);
                _subscription = 0;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint OnEvent(int action, nint context, nint evt)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not WindowsGpuResetSource { _disposed: false } self)
            {
                return 0;
            }

            if (action == EvtSubscribeActionDeliver)
            {
                self.Reset?.Invoke(new GpuResetSignal("Windows"));
            }
            else if (action == EvtSubscribeActionError)
            {
                self._logger.LogWarning("The graphics driver reset subscription reported error {Error}", (long)evt);
            }
        }
        catch (Exception)
        {
            // Never let an exception cross into the event log service's thread.
        }

        return 0;
    }

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint EvtSubscribe(nint session, nint signalEvent, string channelPath, string query, nint bookmark, nint context,
        delegate* unmanaged[Stdcall]<int, nint, nint, uint> callback, int flags);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(nint handle);
}
