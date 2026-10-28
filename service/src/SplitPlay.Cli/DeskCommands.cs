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
                    Profile = new ProfileConfig
                    {
                        Fps = args.IntOption("fps"),
                        Width = args.IntOption("width"),
                        Height = args.IntOption("height"),
                        BitrateKbps = args.IntOption("bitrate-kbps"),
                        Codec = args.Option("codec"),
                    },
                };
                args.EnsureNoUnknownOptions();
                using var client = api();
                Print(await client.CreateDeskAsync(desk).ConfigureAwait(false), json);
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
