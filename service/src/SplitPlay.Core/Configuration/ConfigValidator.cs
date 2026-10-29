// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;
using SplitPlay.Core.Ports;

namespace SplitPlay.Core.Configuration;

/// <summary>One validation problem, with a JSON-path-like location such as <c>desks[0].profile.fps</c>.</summary>
public sealed record ConfigError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>Validates a configuration against the rules in docs/contracts/config.md.</summary>
public static partial class ConfigValidator
{
    public static readonly IReadOnlySet<string> Codecs = new HashSet<string>(StringComparer.Ordinal) { "h264", "hevc", "av1" };

    public static readonly IReadOnlySet<string> GpuPriorities = new HashSet<string>(StringComparer.Ordinal) { "normal", "high", "realtime" };

    public static readonly IReadOnlySet<string> LogLevels =
        new HashSet<string>(StringComparer.Ordinal) { "trace", "debug", "info", "warning", "error", "critical" };

    /// <summary>Characters Windows does not allow in a local account name.</summary>
    private const string ForbiddenUserChars = "\"/\\[]:;|=,+*?<>@";

    /// <summary>Maximum length of a local Windows account name (SAM limit).</summary>
    public const int MaxUserNameLength = 20;

    [GeneratedRegex("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeskIdRegex();

    [GeneratedRegex("^0x[0-9a-fA-F]{1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex LuidRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkerSettingNameRegex();

    /// <summary>Worker settings the service sets itself (from other desk fields); they cannot be overridden.</summary>
    public static readonly IReadOnlySet<string> ReservedWorkerSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "port" };

    /// <summary>
    /// Sunshine settings that run commands or name files: the worker runs as SYSTEM, so a caller able to set them could
    /// run anything as SYSTEM or make the worker read or write any file. They are refused in the API and dropped at launch.
    /// </summary>
    public static readonly IReadOnlySet<string> ForbiddenWorkerSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "global_prep_cmd", "prep_cmd", "server_cmd", "do", "undo", "detached", "cmd",
        "file_apps", "file_state", "credentials_file", "pkey", "cert", "log_path", "config_file", "apps",
        "origin_web_ui_allowed", "origin_pin_allowed",
    };

    /// <summary>True for a worker setting that runs a command or names a file (see <see cref="ForbiddenWorkerSettings"/>).</summary>
    public static bool IsForbiddenWorkerSetting(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var n = name.ToLowerInvariant();
        return ForbiddenWorkerSettings.Contains(n)
               || n.EndsWith("cmd", StringComparison.Ordinal) || n.EndsWith("_command", StringComparison.Ordinal)
               || n.EndsWith("_file", StringComparison.Ordinal) || n.EndsWith("_path", StringComparison.Ordinal)
               || n.EndsWith("_dir", StringComparison.Ordinal) || n.StartsWith("file_", StringComparison.Ordinal);
    }

    /// <summary>True if <paramref name="id"/> is a valid desk id (<c>[a-z0-9-]{1,32}</c>).</summary>
    public static bool IsValidDeskId(string? id) => id is not null && DeskIdRegex().IsMatch(id);

    /// <summary>True if <paramref name="user"/> is a plausible local Windows account name.</summary>
    public static bool IsValidUserName(string? user)
    {
        if (string.IsNullOrWhiteSpace(user) || user.Length > MaxUserNameLength)
        {
            return false;
        }

        if (user.Trim('.', ' ').Length == 0)
        {
            return false;
        }

        foreach (var c in user)
        {
            if (char.IsControl(c) || ForbiddenUserChars.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Validates the whole file. Returns an empty list when the configuration is valid.</summary>
    public static IReadOnlyList<ConfigError> Validate(SplitPlayConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<ConfigError>();

        if (config.SchemaVersion != SplitPlayConfig.CurrentSchemaVersion)
        {
            errors.Add(new("schemaVersion", $"must be {SplitPlayConfig.CurrentSchemaVersion}"));
        }

        ValidateHost(config.Host, errors);

        if (config.Desks is null)
        {
            errors.Add(new("desks", "must be an array"));
            return errors;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranges = new List<(int Index, PortRange Range)>();

        for (var i = 0; i < config.Desks.Count; i++)
        {
            var desk = config.Desks[i];
            var path = $"desks[{i}]";
            if (desk is null)
            {
                errors.Add(new(path, "must be an object"));
                continue;
            }

            ValidateDesk(desk, path, config.Host, errors);

            if (IsValidDeskId(desk.Id) && !ids.Add(desk.Id))
            {
                errors.Add(new($"{path}.id", $"duplicate desk id '{desk.Id}'"));
            }

            if (desk.User is not null && IsValidUserName(desk.User) && !users.Add(desk.User))
            {
                errors.Add(new($"{path}.user", $"user '{desk.User}' is already used by another desk"));
            }

            if (desk.BasePort is int bp && BasePortAllocator.IsValidBasePort(bp))
            {
                var range = BasePortAllocator.RangeOf(bp);
                foreach (var (other, otherRange) in ranges)
                {
                    if (range.Overlaps(otherRange))
                    {
                        errors.Add(new($"{path}.basePort", $"port range {range} overlaps desks[{other}] ({otherRange})"));
                    }
                }

                ranges.Add((i, range));
            }
        }

        return errors;
    }

    /// <summary>Validates a single desk in isolation (no uniqueness checks).</summary>
    public static IReadOnlyList<ConfigError> ValidateDesk(DeskConfig desk, HostConfig host)
    {
        var errors = new List<ConfigError>();
        ValidateDesk(desk, "desk", host, errors);
        return errors;
    }

    private static void ValidateHost(HostConfig? host, List<ConfigError> errors)
    {
        if (host is null)
        {
            errors.Add(new("host", "is required"));
            return;
        }

        if (host.WebPort is < 1 or > 65535)
        {
            errors.Add(new("host.webPort", "must be between 1 and 65535"));
        }

        if (!BasePortAllocator.IsValidBasePort(host.BasePortStart))
        {
            errors.Add(new("host.basePortStart",
                $"must be between {BasePortAllocator.MinBasePort} and {BasePortAllocator.MaxBasePort}"));
        }

        if (host.BasePortStride < BasePortAllocator.PortSpan)
        {
            errors.Add(new("host.basePortStride", $"must be at least {BasePortAllocator.PortSpan} so that desks do not overlap"));
        }

        if (host.ComposeIntervalMs is < 1 or > 100)
        {
            errors.Add(new("host.composeIntervalMs", "must be between 1 and 100"));
        }

        if (host.LogLevel is null || !LogLevels.Contains(host.LogLevel))
        {
            errors.Add(new("host.logLevel", $"must be one of {string.Join(", ", LogLevels)}"));
        }
    }

    private static void ValidateDesk(DeskConfig desk, string path, HostConfig? host, List<ConfigError> errors)
    {
        if (!IsValidDeskId(desk.Id))
        {
            errors.Add(new($"{path}.id", "must match [a-z0-9-]{1,32}"));
        }

        if (desk.Name is { Length: > 64 })
        {
            errors.Add(new($"{path}.name", "must be at most 64 characters"));
        }

        if (desk.User is not null && !IsValidUserName(desk.User))
        {
            errors.Add(new($"{path}.user", $"must be a valid local Windows account name (1-{MaxUserNameLength} characters)"));
        }

        if (desk.BasePort is int basePort)
        {
            if (!BasePortAllocator.IsValidBasePort(basePort))
            {
                errors.Add(new($"{path}.basePort",
                    $"must be between {BasePortAllocator.MinBasePort} and {BasePortAllocator.MaxBasePort}"));
            }
            else if (host is not null && BasePortAllocator.RangeOf(basePort).Contains(host.WebPort))
            {
                errors.Add(new($"{path}.basePort", $"port range {BasePortAllocator.RangeOf(basePort)} contains the web port {host.WebPort}"));
            }
        }

        if (desk.Gpu is null || (desk.Gpu != "auto" && !LuidRegex().IsMatch(desk.Gpu)))
        {
            errors.Add(new($"{path}.gpu", "must be \"auto\" or an adapter LUID \"0x…\""));
        }

        if (desk.WorkerSettings is not null)
        {
            foreach (var (name, value) in desk.WorkerSettings)
            {
                if (!WorkerSettingNameRegex().IsMatch(name ?? ""))
                {
                    errors.Add(new($"{path}.workerSettings", $"'{name}': names must match [A-Za-z0-9_.-]{{1,64}}"));
                }
                else if (ReservedWorkerSettings.Contains(name!))
                {
                    errors.Add(new($"{path}.workerSettings.{name}", "is set by the service (from basePort) and cannot be overridden"));
                }
                else if (IsForbiddenWorkerSetting(name!))
                {
                    errors.Add(new($"{path}.workerSettings.{name}", "runs a command or names a file, which the worker (SYSTEM) must not do"));
                }

                if (value is null || value.Length > 1024 || value.Any(char.IsControl))
                {
                    errors.Add(new($"{path}.workerSettings.{name}", "must be a string of at most 1024 characters without control characters"));
                }
            }
        }

        if (desk.Profile is null)
        {
            errors.Add(new($"{path}.profile", "is required"));
            return;
        }

        ValidateProfile(desk.Profile, $"{path}.profile", errors);
    }

    /// <summary>Validates a profile (used for live profile changes too).</summary>
    public static void ValidateProfile(ProfileConfig p, string path, List<ConfigError> errors)
    {
        if (p.Fps is < 10 or > 500)
        {
            errors.Add(new($"{path}.fps", "must be between 10 and 500"));
        }

        if (p.Width is < 64 or > 16384)
        {
            errors.Add(new($"{path}.width", "must be between 64 and 16384"));
        }

        if (p.Height is < 64 or > 16384)
        {
            errors.Add(new($"{path}.height", "must be between 64 and 16384"));
        }

        if (p.BitrateKbps is < 100 or > 2_000_000)
        {
            errors.Add(new($"{path}.bitrateKbps", "must be between 100 and 2000000"));
        }

        if (p.Codec is not null && !Codecs.Contains(p.Codec))
        {
            errors.Add(new($"{path}.codec", "must be one of h264, hevc, av1"));
        }

        if (p.FecPercent is < 0 or > 100)
        {
            errors.Add(new($"{path}.fecPercent", "must be between 0 and 100"));
        }

        if (p.MinFps is < 1 or > 500)
        {
            errors.Add(new($"{path}.minFps", "must be between 1 and 500"));
        }
        else if (p.MinFps is int min && p.Fps is int fps && min > fps)
        {
            errors.Add(new($"{path}.minFps", "must not exceed fps"));
        }

        if (p.DpbFrames is < 1 or > 16)
        {
            errors.Add(new($"{path}.dpbFrames", "must be between 1 and 16"));
        }

        if (p.PacingMbps is < 1 or > 100_000)
        {
            errors.Add(new($"{path}.pacingMbps", "must be between 1 and 100000"));
        }

        if (p.GpuPriority is not null && !GpuPriorities.Contains(p.GpuPriority))
        {
            errors.Add(new($"{path}.gpuPriority", "must be one of normal, high, realtime"));
        }

        if (p.GpuLuid is not null && !LuidRegex().IsMatch(p.GpuLuid))
        {
            errors.Add(new($"{path}.gpuLuid", "must be an adapter LUID \"0x…\""));
        }

        if (p.CpuSet is not null && (p.CpuSet.Any(c => c < 0) || p.CpuSet.Distinct().Count() != p.CpuSet.Count))
        {
            errors.Add(new($"{path}.cpuSet", "must be a list of distinct non-negative CPU indices"));
        }
    }
}
