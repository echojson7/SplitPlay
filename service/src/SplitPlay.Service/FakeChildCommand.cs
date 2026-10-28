// SPDX-License-Identifier: MIT
using System.Globalization;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Service;

/// <summary>
/// <c>SplitPlay.Service.exe fake-child --pipe NAME --desk ID</c>: a protocol-conformant child process for smoke tests
/// on machines without the real worker (set <c>--Supervisor:ChildExecutable</c> to this executable and
/// <c>--Supervisor:ChildArguments=fake-child</c>).
/// </summary>
internal static class FakeChildCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? pipe = null;
        string? desk = null;
        var role = IpcProtocol.Roles.StubWorker;
        var heartbeatMs = 1000;
        var behavior = FakeChildBehavior.Normal;

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--pipe":
                    pipe = Next();
                    break;
                case "--desk":
                    desk = Next();
                    break;
                case "--role":
                    role = Next();
                    break;
                case "--heartbeat-ms":
                    heartbeatMs = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--behavior":
                    behavior = Enum.Parse<FakeChildBehavior>(Next(), ignoreCase: true);
                    break;
                default:
                    await Console.Error.WriteLineAsync($"fake-child: unknown argument {args[i]}").ConfigureAwait(false);
                    return 64;
            }
        }

        if (pipe is null || desk is null)
        {
            await Console.Error.WriteLineAsync("usage: SplitPlay.Service fake-child --pipe NAME --desk ID [--role R] [--heartbeat-ms N] [--behavior B]")
                .ConfigureAwait(false);
            return 64;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return await FakeChild.RunAsync(new FakeChildOptions
            {
                PipeName = pipe,
                DeskId = desk,
                Role = role,
                HeartbeatInterval = TimeSpan.FromMilliseconds(heartbeatMs),
                Behavior = behavior,
                Log = message => Console.Error.WriteLine($"fake-child[{desk}]: {message}"),
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }
}
