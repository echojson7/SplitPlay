// SPDX-License-Identifier: MIT
using System.Globalization;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Service;

/// <summary>
/// <c>SplitPlay.Service.exe fake-child --pipe NAME --desk ID</c>: a protocol-conformant child process for smoke tests
/// on machines without the real worker (set <c>--Supervisor:WorkerExecutable</c> to this executable and
/// <c>--Supervisor:WorkerArguments=fake-child</c>). It also accepts the real worker's <c>--desk-dir</c> and
/// <c>name=value</c> arguments, and reports the Windows session it runs in.
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
        var settings = new List<string>();

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
                case "--desk-dir":
                    // Real worker command line: accepted and ignored, like the trailing name=value settings.
                    Next();
                    break;
                case var setting when setting.Contains('=', StringComparison.Ordinal) && !setting.StartsWith("--", StringComparison.Ordinal):
                    settings.Add(setting);
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

        if (settings.Count > 0)
        {
            await Console.Error.WriteLineAsync($"fake-child[{desk}]: settings {string.Join(' ', settings)}").ConfigureAwait(false);
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
                // The session this process really runs in, so that tests can check the launch into a desk session.
                Session = OperatingSystem.IsWindows() ? System.Diagnostics.Process.GetCurrentProcess().SessionId : null,
                Log = message => Console.Error.WriteLine($"fake-child[{desk}]: {message}"),
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }
}
