// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;

namespace SplitPlay.Cli;

/// <summary><c>splitplay desk list|show|create|start|stop|delete</c> and <c>splitplay config export|import</c>.</summary>
internal static class DeskCommands
{
    public static async Task<int> RunDeskAsync(string verb, Args args, Func<ApiClient> api)
    {
        var json = args.Flag("json");
        switch (verb)
        {
            case "list":
            {
                args.EnsureNoUnknownOptions();
                using var client = api();
                var desks = await client.ListDesksAsync().ConfigureAwait(false);
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(desks, SplitPlayJsonContext.Default.ListDeskDto));
                    return 0;
                }

                PrintTable(desks);
                return 0;
            }

            case "show":
            {
                var id = args.Positional(0, "id");
                args.EnsureNoUnknownOptions();
                using var client = api();
                Print(await client.GetDeskAsync(id).ConfigureAwait(false), json);
                return 0;
            }

            case "create":
            {
                var desk = new DeskConfig
                {
                    Id = args.Positional(0, "id"),
                    Name = args.Option("name"),
                    User = args.Option("user"),
                    BasePort = args.IntOption("base-port"),
                    Autostart = args.Flag("autostart"),
                    Gpu = args.Option("gpu") ?? "auto",
                    SteamIsolation = args.Flag("no-steam-isolation") ? false : null,
                    Profile = new ProfileConfig
                    {
                        Fps = args.IntOption("fps"),
                        Width = args.IntOption("width"),
                        Height = args.IntOption("height"),
                        BitrateKbps = args.IntOption("bitrate-kbps"),
                        Codec = args.Option("codec"),
                    },
                };
                var password = ReadPassword(args, desk.User, required: false);
                args.EnsureNoUnknownOptions();
                using var client = api();
                Print(await client.CreateDeskAsync(desk, password).ConfigureAwait(false), json);
                return 0;
            }

            case "set-password":
            {
                var id = args.Positional(0, "id");
                using var client = api();
                var current = await client.GetDeskAsync(id).ConfigureAwait(false);
                var password = ReadPassword(args, current.User, required: true);
                args.EnsureNoUnknownOptions();
                var desk = new DeskConfig
                {
                    Id = current.Id,
                    Name = current.Name,
                    User = current.User,
                    BasePort = current.BasePort,
                    Autostart = current.Autostart,
                    Gpu = current.Gpu,
                    Profile = current.Profile,
                    DisableBluetoothUserService = current.DisableBluetoothUserService,
                    SteamIsolation = current.SteamIsolation,
                    WorkerSettings = current.WorkerSettings,
                };
                Print(await client.ReplaceDeskAsync(desk, password).ConfigureAwait(false), json);
                return 0;
            }

            case "start":
            case "stop":
            {
                var id = args.Positional(0, "id");
                var wait = args.Flag("wait");
                var timeout = TimeSpan.FromSeconds(args.IntOption("timeout") ?? 60);
                args.EnsureNoUnknownOptions();
                using var client = api();
                var desk = verb == "start"
                    ? await client.StartDeskAsync(id).ConfigureAwait(false)
                    : await client.StopDeskAsync(id).ConfigureAwait(false);
                if (wait)
                {
                    desk = await WaitForAsync(client, id, verb == "start" ? IsStarted : IsStopped, timeout).ConfigureAwait(false);
                    if (desk is null)
                    {
                        Console.Error.WriteLine($"error: desk '{id}' did not reach the expected state within {timeout.TotalSeconds:0} s");
                        return 4;
                    }

                    if (verb == "start" && desk.State == DeskState.Faulted)
                    {
                        Console.Error.WriteLine($"error: desk '{id}' faulted: {desk.LastError}");
                        return 1;
                    }
                }

                Print(desk, json);
                return 0;
            }

            case "delete":
            {
                var id = args.Positional(0, "id");
                var deleteUser = args.Flag("delete-user");
                args.EnsureNoUnknownOptions();
                using var client = api();
                await client.DeleteDeskAsync(id, deleteUser).ConfigureAwait(false);
                Console.WriteLine(deleteUser ? $"Deleted desk '{id}' and its Windows account." : $"Deleted desk '{id}'.");
                return 0;
            }

            default:
                throw new UsageException($"unknown desk command '{verb}'");
        }
    }

    public static async Task<int> RunConfigAsync(string verb, Args args, Func<ApiClient> api)
    {
        switch (verb)
        {
            case "export":
            {
                var file = args.Positional(0, "file.zip");
                args.EnsureNoUnknownOptions();
                using var client = api();
                var zip = await client.ExportConfigAsync().ConfigureAwait(false);
                await File.WriteAllBytesAsync(file, zip).ConfigureAwait(false);
                Console.WriteLine($"Exported configuration to {Path.GetFullPath(file)} ({zip.Length} bytes).");
                return 0;
            }

            case "import":
            {
                var file = args.Positional(0, "file.zip");
                args.EnsureNoUnknownOptions();
                var zip = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                using var client = api();
                var desks = await client.ImportConfigAsync(zip).ConfigureAwait(false);
                Console.WriteLine($"Imported configuration with {desks.Count} desk(s).");
                return 0;
            }

            default:
                throw new UsageException($"unknown config command '{verb}'");
        }
    }

    /// <summary>
    /// The desk user's Windows password: one line from stdin with <c>--password-stdin</c>, a hidden prompt with
    /// <c>--ask-password</c> (or when <paramref name="required"/> and stdin is a console), else null. Never an
    /// argument, so it does not show up in process listings or shell history.
    /// </summary>
    private static string? ReadPassword(Args args, string? user, bool required)
    {
        if (args.Flag("password-stdin"))
        {
            return Console.In.ReadLine()?.TrimEnd('\r', '\n') is { Length: > 0 } line ? line : null;
        }

        if (!args.Flag("ask-password") && !(required && !Console.IsInputRedirected))
        {
            return required ? throw new UsageException("give the password with --password-stdin or --ask-password") : null;
        }

        Console.Error.Write($"Windows password for {user ?? "the desk user"}: ");
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return buffer.Length > 0 ? buffer.ToString() : null;
    }

    private static bool IsStarted(DeskState s) => s is DeskState.Ready or DeskState.Streaming or DeskState.Faulted;

    private static bool IsStopped(DeskState s) => s is DeskState.Stopped or DeskState.Defined;

    private static async Task<DeskDto?> WaitForAsync(ApiClient client, string id, Func<DeskState, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var desk = await client.GetDeskAsync(id).ConfigureAwait(false);
            if (done(desk.State))
            {
                return desk;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    private static void Print(DeskDto desk, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(desk, SplitPlayJsonContext.Default.DeskDto));
            return;
        }

        PrintTable([desk]);
        if (desk.LastError is not null)
        {
            Console.WriteLine($"last error: {desk.LastError}");
        }
    }

    private static void PrintTable(IReadOnlyList<DeskDto> desks)
    {
        if (desks.Count == 0)
        {
            Console.WriteLine("No desks.");
            return;
        }

        var rows = desks.Select(d => new[]
        {
            d.Id, d.Name ?? "", d.User ?? "", d.State.ToString(),
            d.BasePort?.ToString(CultureInfo.InvariantCulture) ?? "", d.Autostart ? "yes" : "no",
        }).ToList();
        string[] header = ["ID", "NAME", "USER", "STATE", "PORT", "AUTOSTART"];
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();
        Console.WriteLine(string.Join("  ", header.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        foreach (var row in rows)
        {
            Console.WriteLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
        }
    }
}
