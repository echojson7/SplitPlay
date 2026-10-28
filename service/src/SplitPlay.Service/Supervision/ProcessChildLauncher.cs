// SPDX-License-Identifier: MIT
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace SplitPlay.Service.Supervision;

/// <summary>
/// Starts a configurable executable with <c>[ChildArguments] --pipe &lt;name&gt; --desk &lt;id&gt;</c>.
/// Stage 2 stub: the child runs as the service account in the service's session. Stage 3 replaces the start with
/// <c>CreateProcessAsUser</c> (duplicated SYSTEM token with <c>TokenSessionId</c> = desk session) inside a per-desk
/// job object.
/// </summary>
public sealed class ProcessChildLauncher(IOptions<SupervisorOptions> options, ILogger<ProcessChildLauncher> logger) : IChildLauncher
{
    public Task<IChildProcess> LaunchAsync(ChildLaunchRequest request, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var executable = Path.IsPathRooted(o.ChildExecutable)
            ? o.ChildExecutable
            : Path.Combine(AppContext.BaseDirectory, o.ChildExecutable);

        if (!File.Exists(executable))
        {
            throw new FileNotFoundException($"child executable not found: {executable}", executable);
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(request.DeskDirectory) ? request.DeskDirectory : AppContext.BaseDirectory,
        };

        foreach (var arg in o.ChildArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(request.PipeName);
        startInfo.ArgumentList.Add("--desk");
        startInfo.ArgumentList.Add(request.DeskId);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"could not start {executable}");
        logger.LogInformation("Started {Executable} for desk {Desk} (pid {Pid}, attempt {Attempt})",
            Path.GetFileName(executable), request.DeskId, process.Id, request.Attempt);
        return Task.FromResult<IChildProcess>(new ProcessChild(process));
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
