// SPDX-License-Identifier: MIT
using SplitPlay.Core;

namespace SplitPlay.Service.Hosting;

/// <summary>Options that shape how the service host is built (parsed from the command line or set by tests).</summary>
public sealed class ServiceHostOptions
{
    /// <summary>Run in the console instead of as a Windows service.</summary>
    public bool Console { get; set; }

    /// <summary>Data root (<c>config.json</c>, <c>desks\</c>, <c>logs\</c>); defaults to <c>%ProgramData%\SplitPlay</c>.</summary>
    public string DataRoot { get; set; } = DefaultDataRoot();

    /// <summary>Overrides <c>host.webPort</c> from config.json (0 picks a free port).</summary>
    public int? WebPort { get; set; }

    /// <summary>Overrides the static file root (defaults to <c>wwwroot</c> next to the executable).</summary>
    public string? WebRoot { get; set; }

    /// <summary>Remaining arguments, handed to the generic host's configuration (e.g. <c>--Supervisor:Launcher=fake</c>).</summary>
    public List<string> HostArgs { get; } = [];

    /// <summary>The <c>%ProgramData%\SplitPlay</c> directory.</summary>
    public static string DefaultDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), BuildInfo.ServiceName);

    /// <summary>
    /// Parses <c>--console</c>, <c>--data-root &lt;dir&gt;</c>, <c>--web-port &lt;n&gt;</c> and <c>--web-root &lt;dir&gt;</c>;
    /// everything else is passed through to the host.
    /// </summary>
    public static ServiceHostOptions Parse(IReadOnlyList<string> args)
    {
        var options = new ServiceHostOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--console":
                    options.Console = true;
                    break;
                case "--data-root":
                    options.DataRoot = Path.GetFullPath(RequireValue(args, ref i, arg));
                    break;
                case "--web-port":
                    options.WebPort = int.Parse(RequireValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--web-root":
                    options.WebRoot = Path.GetFullPath(RequireValue(args, ref i, arg));
                    break;
                default:
                    if (arg.StartsWith("--data-root=", StringComparison.Ordinal))
                    {
                        options.DataRoot = Path.GetFullPath(arg["--data-root=".Length..]);
                    }
                    else
                    {
                        options.HostArgs.Add(arg);
                    }

                    break;
            }
        }

        return options;
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int i, string name)
    {
        if (i + 1 >= args.Count)
        {
            throw new ArgumentException($"{name} needs a value");
        }

        return args[++i];
    }
}
