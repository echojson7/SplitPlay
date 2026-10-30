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
    /// The Sunshine settings a desk may set (<c>workerSettings</c>), from <c>src/config.cpp</c> at the pinned worker
    /// commit (worker/upstream.lock): encoder, codec, quality and rate control, frame rate, audio on/off, input and
    /// gamepads, the name and the log level. Everything else is refused (API 400) and dropped (config.json, worker launch):
    /// the worker runs as SYSTEM, so settings that run commands, name files, open the network (UPnP, bind address,
    /// external IP, encryption modes), install drivers, or move capture, display or audio away from the desk's session
    /// are not the owner's to set per desk. The service's own settings (<see cref="ReservedWorkerSettings"/> and the
    /// defaults it passes) do not go through this list.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedWorkerSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Encoder and codec
        "encoder", "hevc_mode", "av1_mode", "qp", "min_threads", "sw_preset", "sw_tune",
        "nvenc_preset", "nvenc_vbv_increase", "nvenc_spatial_aq", "nvenc_twopass", "nvenc_h264_cavlc", "nvenc_split_encode",
        "nvenc_realtime_hags", "nvenc_latency_over_power",
        "qsv_preset", "qsv_coder", "qsv_slow_hevc",
        "amd_quality", "amd_rc", "amd_coder", "amd_usage", "amd_preanalysis", "amd_vbaq", "amd_enforce_hrd",

        // Rate control and frame rate
        "max_bitrate", "minimum_fps_target", "fec_percentage",

        // Audio
        "stream_audio",

        // Input and gamepads
        "gamepad", "ds4_back_as_touchpad_click", "motion_as_ds4", "touchpad_as_ds4",
        "mouse", "keyboard", "controller", "always_send_scancodes", "high_resolution_scrolling", "native_pen_touch",
        "keybindings", "key_rightalt_to_key_win", "back_button_timeout", "key_repeat_frequency", "key_repeat_delay",

        // Identity and logging
        "sunshine_name", "min_log_level",
    };

    /// <summary>True for a setting a desk's <c>workerSettings</c> may carry (see <see cref="AllowedWorkerSettings"/>).</summary>
    public static bool IsAllowedWorkerSetting(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return AllowedWorkerSettings.Contains(name) && !ReservedWorkerSettings.Contains(name);
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
                else if (!IsAllowedWorkerSetting(name!))
                {
                    errors.Add(new($"{path}.workerSettings.{name}", $"'{name}' is not a setting a desk may change (see config.md, workerSettings)"));
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
