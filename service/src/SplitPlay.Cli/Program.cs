// SPDX-License-Identifier: MIT
using System.ComponentModel;
using SplitPlay.Core;

namespace SplitPlay.Cli;

/// <summary><c>splitplay</c>: command-line front end for the SplitPlay service.</summary>
public static class Program
{
    private const string Usage = """
        usage: splitplay [--url URL] [--data-root DIR] <command>

        service install [--bin PATH] [--data-root DIR] [--start]
                                    register the 'SplitPlay' Windows service (delayed auto start,
                                    restart on failure); updates it if it already exists
        service uninstall           stop and remove the service
        service start|stop [--timeout S]
        service status

        desk list [--json]
        desk show <id> [--json]
        desk create <id> [--name N] [--user U] [--base-port P] [--autostart] [--gpu auto|0x…]
                         [--fps N] [--width N] [--height N] [--bitrate-kbps N] [--codec h264|hevc|av1]
        desk start <id> [--wait] [--timeout S]
        desk stop <id> [--wait] [--timeout S]
        desk delete <id>

        config export <file.zip>
        config import <file.zip>

        version

        The Web API URL defaults to SPLITPLAY_URL or http://127.0.0.1:<host.webPort>/ from config.json.
        Exit codes: 0 ok, 1 error, 2 usage, 3 service unreachable, 4 timeout.
        """;

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "json", "wait", "autostart", "start", "help" };

    public static async Task<int> Main(string[] argv)
    {
        try
        {
            var args = new Args(argv, Flags);
            if (args.Positionals.Count == 0 || args.Flag("help") || args.Positionals[0] is "help" or "-h" or "/?")
            {
                Console.WriteLine(Usage);
                return args.Positionals.Count == 0 && !args.Flag("help") ? 2 : 0;
            }

            var url = args.Option("url");
            var dataRoot = args.Option("data-root");
            ApiClient Api() => new(ApiClient.ResolveBaseUri(url, dataRoot));

            var group = args.Positionals[0];
            if (group == "version")
            {
                Console.WriteLine($"splitplay {BuildInfo.Version}");
                return 0;
            }

            var verb = args.Positional(1, "command");
            var rest = new Args(argv.SkipWhile(a => a != verb).Skip(1), Flags);
            MarkGlobals(rest);

            return group switch
            {
                "service" when OperatingSystem.IsWindows() => await ServiceCommands.RunAsync(verb, rest, Api).ConfigureAwait(false),
                "service" => Fail("'service' commands need Windows"),
                "desk" or "desks" => await DeskCommands.RunDeskAsync(verb, rest, Api).ConfigureAwait(false),
                "config" => await DeskCommands.RunConfigAsync(verb, rest, Api).ConfigureAwait(false),
                _ => throw new UsageException($"unknown command '{group}'"),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (ApiCallException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message} ({ex.Code}, HTTP {(int)ex.Status})");
            return 1;
        }
        catch (ServiceUnreachableException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 3;
        }
        catch (System.ServiceProcess.TimeoutException ex) when (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 4;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    /// <summary>Global options may appear before the command; mark them as consumed in the command's reader.</summary>
    private static void MarkGlobals(Args command)
    {
        command.Option("url");
        command.Option("data-root");
        command.Flag("help");
    }
}
