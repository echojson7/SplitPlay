// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using SplitPlay.Core;

namespace SplitPlay.Tray;

/// <summary>What <see cref="ServiceStarter.TryStart"/> did.</summary>
internal enum StartOutcome
{
    /// <summary>The SCM accepted the start (or the service was already starting or running).</summary>
    Requested,

    /// <summary>This user may not start the service (an install without the setup's service permissions).</summary>
    AccessDenied,

    Disabled,
    Missing,
    Failed,
}

/// <summary>
/// The SplitPlay service as the tray sees it. Setup lets signed-in users start it (sc sdset, see installer/splitplay.iss);
/// without that permission <see cref="StartElevatedAsync"/> runs <c>splitplay.exe service start</c> elevated.
/// All members block briefly on the SCM: call them off the UI thread.
/// </summary>
internal static class ServiceStarter
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceDisabled = 1058;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorCancelled = 1223;

    /// <summary>A <see cref="FallbackPage"/> status: running, starting, stopping, stopped, missing or unknown.</summary>
    public static string Query()
    {
        try
        {
            using var sc = new ServiceController(BuildInfo.ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => "running",
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => "starting",
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => "stopping",
                _ => "stopped",
            };
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: ErrorServiceDoesNotExist })
        {
            return "missing";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return "unknown";
        }
    }

    /// <summary>Starts the service when it is stopped, without elevation.</summary>
    public static (StartOutcome Outcome, string? Error) TryStart()
    {
        try
        {
            using var sc = new ServiceController(BuildInfo.ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                return (StartOutcome.Requested, null);
            }

            sc.Start();
            return (StartOutcome.Requested, null);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception win32)
        {
            return win32.NativeErrorCode switch
            {
                ErrorServiceAlreadyRunning => (StartOutcome.Requested, null),
                ErrorAccessDenied => (StartOutcome.AccessDenied, win32.Message),
                ErrorServiceDisabled => (StartOutcome.Disabled, win32.Message),
                ErrorServiceDoesNotExist => (StartOutcome.Missing, win32.Message),
                _ => (StartOutcome.Failed, win32.Message),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return (StartOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Runs <c>splitplay.exe service start</c> (next to the service, <c>{app}\service</c>) through ShellExecute
    /// <c>runas</c>: one UAC prompt. Falls back to <c>sc.exe start</c> when the CLI is not there. Completes when the
    /// command exits; returns false and the reason when the prompt was declined or the command failed. The UAC prompt
    /// is owned by <paramref name="owner"/> (the dashboard window); the launch runs off the UI thread.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> StartElevatedAsync(IntPtr owner)
    {
        var cli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "service", "splitplay.exe"));
        var info = File.Exists(cli)
            ? new ProcessStartInfo(cli, "service start --timeout 60")
            : new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), "start " + BuildInfo.ServiceName);
        info.UseShellExecute = true;
        info.Verb = "runas";
        info.WindowStyle = ProcessWindowStyle.Hidden;
        info.ErrorDialog = true;
        info.ErrorDialogParentHandle = owner;
        try
        {
            // ShellExecuteEx with runas blocks until the prompt is answered: not on the UI thread.
            using var process = await Task.Run(() => Process.Start(info)).ConfigureAwait(false);
            if (process is null)
            {
                return (false, null);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0 ? (true, null) : (false, $"exit code {process.ExitCode}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return (false, null);
        }
        catch (Win32Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
