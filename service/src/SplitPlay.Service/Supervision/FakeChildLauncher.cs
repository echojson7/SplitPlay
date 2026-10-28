// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Service.Supervision;

/// <summary>
/// In-memory launcher: runs a <see cref="FakeChild"/> (worker or seat) on the thread pool that talks to the real pipe
/// server. Used on non-Windows hosts and by tests; behaviour can be chosen per launch, and every launch request and
/// every message a child receives is recorded.
/// </summary>
public sealed class FakeChildLauncher(IOptions<SupervisorOptions> options, ILogger<FakeChildLauncher> logger) : IChildLauncher
{
    private readonly ConcurrentQueue<ChildLaunchRequest> _requests = new();
    private readonly ConcurrentQueue<(string Role, IpcMessage Message)> _received = new();
    private int _nextPid = 100_000;

    /// <summary>Chooses the behaviour of each launched child. Default: always <see cref="FakeChildBehavior.Normal"/>.</summary>
    public Func<ChildLaunchRequest, FakeChildBehavior> Behavior { get; set; } = _ => FakeChildBehavior.Normal;

    /// <summary>When it returns true the launch throws (simulates a missing or broken executable).</summary>
    public Func<ChildLaunchRequest, bool> FailLaunch { get; set; } = _ => false;

    /// <summary>The session a fake seat reports in <c>ready</c>. Default 7.</summary>
    public Func<ChildLaunchRequest, int?> SeatSession { get; set; } = _ => 7;

    /// <summary>Final say over each child's options (e.g. <see cref="FakeChildOptions.RequestMode"/>).</summary>
    public Func<ChildLaunchRequest, FakeChildOptions, FakeChildOptions> Customize { get; set; } = (_, o) => o;

    /// <summary>Workers send <c>client_connected</c> after ready.</summary>
    public bool SimulateClient { get; set; }

    /// <summary>Number of launches so far (all roles).</summary>
    public int Launches => _requests.Count;

    public IReadOnlyList<ChildLaunchRequest> Requests => [.. _requests];

    /// <summary>Messages the children received from the service, in order.</summary>
    public IReadOnlyList<(string Role, IpcMessage Message)> Received => [.. _received];

    public int LaunchesOf(string role) => _requests.Count(r => r.Role == role);

    public Task<IChildProcess> LaunchAsync(ChildLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests.Enqueue(request);
        if (FailLaunch(request))
        {
            throw new InvalidOperationException("simulated launch failure");
        }

        var pid = Interlocked.Increment(ref _nextPid);
        var seat = request.Role == IpcProtocol.Roles.Seat;
        var childOptions = new FakeChildOptions
        {
            PipeName = request.PipeName,
            DeskId = request.DeskId,
            Role = seat ? IpcProtocol.Roles.Seat : IpcProtocol.Roles.StubWorker,
            Pid = pid,
            Session = seat ? SeatSession(request) : request.SessionId,
            HeartbeatInterval = options.Value.HeartbeatInterval,
            Behavior = Behavior(request),
            SimulateClient = !seat && SimulateClient,
            ConnectTimeout = TimeSpan.FromMilliseconds(options.Value.HelloTimeoutMs),
            OnMessage = m => _received.Enqueue((request.Role, m)),
            Log = message => logger.LogDebug("fake {Role} {Desk}/{Pid}: {Message}", request.Role, request.DeskId, pid, message),
        };

        return Task.FromResult<IChildProcess>(new FakeProcess(pid, Customize(request, childOptions)));
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
