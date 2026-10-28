// SPDX-License-Identifier: MIT
using Microsoft.Extensions.Options;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Service.Supervision;

/// <summary>
/// In-memory launcher: runs a <see cref="FakeChild"/> on the thread pool that talks to the real pipe server.
/// Used on non-Windows hosts and by tests; behaviour can be chosen per launch.
/// </summary>
public sealed class FakeChildLauncher(IOptions<SupervisorOptions> options, ILogger<FakeChildLauncher> logger) : IChildLauncher
{
    private int _nextPid = 100_000;
    private int _launches;

    /// <summary>Chooses the behaviour of each launched child. Default: always <see cref="FakeChildBehavior.Normal"/>.</summary>
    public Func<ChildLaunchRequest, FakeChildBehavior> Behavior { get; set; } = _ => FakeChildBehavior.Normal;

    /// <summary>When it returns true the launch throws (simulates a missing or broken executable).</summary>
    public Func<ChildLaunchRequest, bool> FailLaunch { get; set; } = _ => false;

    /// <summary>Send <c>client_connected</c> after ready.</summary>
    public bool SimulateClient { get; set; }

    /// <summary>Number of launches so far.</summary>
    public int Launches => Volatile.Read(ref _launches);

    public Task<IChildProcess> LaunchAsync(ChildLaunchRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _launches);
        if (FailLaunch(request))
        {
            throw new InvalidOperationException("simulated launch failure");
        }

        var pid = Interlocked.Increment(ref _nextPid);
        var childOptions = new FakeChildOptions
        {
            PipeName = request.PipeName,
            DeskId = request.DeskId,
            Pid = pid,
            HeartbeatInterval = options.Value.HeartbeatInterval,
            Behavior = Behavior(request),
            SimulateClient = SimulateClient,
            ConnectTimeout = TimeSpan.FromMilliseconds(options.Value.HelloTimeoutMs),
            Log = message => logger.LogDebug("fake child {Desk}/{Pid}: {Message}", request.DeskId, pid, message),
        };

        return Task.FromResult<IChildProcess>(new FakeProcess(pid, childOptions));
    }

    private sealed class FakeProcess : IChildProcess
    {
        private readonly CancellationTokenSource _kill = new();

        public FakeProcess(int pid, FakeChildOptions options)
        {
            Pid = pid;
            Exited = Task.Run(async () =>
            {
                try
                {
                    return await FakeChild.RunAsync(options, _kill.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return -1;
                }
                catch (IOException)
                {
                    return FakeChild.ExitCodes.PipeBroken;
                }
            });
        }

        public int Pid { get; }

        public Task<int> Exited { get; }

        public void Kill()
        {
            try
            {
                _kill.Cancel();
            }
            catch (ObjectDisposedException)
            {
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

            _kill.Dispose();
        }
    }
}
