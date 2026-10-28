// SPDX-License-Identifier: MIT
using System.Diagnostics;

namespace SplitPlay.Service.Supervision;

/// <summary>
/// Starts real executables. On Windows every child gets its own job object (kill on close); a child with
/// <see cref="ChildLaunchRequest.SessionId"/> is started in that session with a duplicate of the service's SYSTEM token
/// (<see cref="Win32ChildProcess"/>). Elsewhere (developer machines, Linux CI) it is a plain <see cref="Process"/>.
/// </summary>
public sealed class ProcessChildLauncher(ILogger<ProcessChildLauncher> logger) : IChildLauncher
{
    public Task<IChildProcess> LaunchAsync(ChildLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var executable = request.Executable;
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException($"{request.Role} executable not found: {executable}", executable);
        }

        var workingDirectory = Directory.Exists(request.DeskDirectory) ? request.DeskDirectory : AppContext.BaseDirectory;
        IChildProcess child;
        if (OperatingSystem.IsWindows())
        {
            child = Win32ChildProcess.Start(executable, request.Arguments, workingDirectory, request.SessionId);
        }
        else
        {
            if (request.SessionId is not null)
            {
                throw new PlatformNotSupportedException("starting a child in another session needs Windows");
            }

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };
            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            child = new ProcessChild(Process.Start(startInfo) ?? throw new InvalidOperationException($"could not start {executable}"));
        }

        logger.LogInformation("Started {Executable} for desk {Desk} as {Role} (pid {Pid}, session {Session}, attempt {Attempt})",
            Path.GetFileName(executable), request.DeskId, request.Role, child.Pid, request.SessionId?.ToString() ?? "own", request.Attempt);
        return Task.FromResult(child);
    }

    private sealed class ProcessChild : IChildProcess
    {
        private readonly Process _process;

        public ProcessChild(Process process)
        {
            _process = process;
            Pid = process.Id;
            Exited = WaitAsync();
        }

        public int Pid { get; }

        public Task<int> Exited { get; }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
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

            _process.Dispose();
        }

        private async Task<int> WaitAsync()
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            return _process.ExitCode;
        }
    }
}
