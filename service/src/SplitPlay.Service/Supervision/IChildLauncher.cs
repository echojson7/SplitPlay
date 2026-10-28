// SPDX-License-Identifier: MIT
namespace SplitPlay.Service.Supervision;

/// <summary>What to start for a desk.</summary>
/// <param name="DeskId">Desk id, passed as <c>--desk</c>.</param>
/// <param name="Role">Pipe role (<c>worker</c> or <c>seat</c>).</param>
/// <param name="PipeName">Full pipe name, passed as <c>--pipe</c> (e.g. <c>\\.\pipe\splitplay\worker-desk-1</c>).</param>
/// <param name="DeskDirectory">The desk's data directory (<c>desks\&lt;id&gt;\</c>), used as working directory.</param>
/// <param name="Attempt">1 for the first launch after start, incremented on every recovery.</param>
public sealed record ChildLaunchRequest(string DeskId, string Role, string PipeName, string DeskDirectory, int Attempt);

/// <summary>A running child process.</summary>
public interface IChildProcess : IAsyncDisposable
{
    int Pid { get; }

    /// <summary>Completes with the exit code when the child exits.</summary>
    Task<int> Exited { get; }

    /// <summary>Terminates the child (and, in production, its job object) immediately.</summary>
    void Kill();
}

/// <summary>
/// Starts child processes for a desk. Production: <see cref="ProcessChildLauncher"/> (Stage 3 adds the job object,
/// <c>CreateProcessAsUser</c> with <c>TokenSessionId</c>). Tests and non-Windows hosts: <see cref="FakeChildLauncher"/>.
/// </summary>
public interface IChildLauncher
{
    /// <summary>Starts the child. The pipe server already exists when this is called.</summary>
    Task<IChildProcess> LaunchAsync(ChildLaunchRequest request, CancellationToken cancellationToken);
}
