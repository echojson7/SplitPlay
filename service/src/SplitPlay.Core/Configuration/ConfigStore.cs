// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SplitPlay.Core.Json;
using SplitPlay.Core.Ports;

namespace SplitPlay.Core.Configuration;

/// <summary>Result of parsing a configuration document.</summary>
/// <param name="Config">The validated configuration (already migrated and with defaults applied).</param>
/// <param name="MigratedFrom">The schema version on disk when it was older than the current one, otherwise null.</param>
/// <param name="DefaultsApplied">True when missing values (base ports, user names) were filled in.</param>
/// <param name="DroppedWorkerSettings">Desk <c>workerSettings</c> left out because a desk may not set them
/// (<c>desk-id.name</c>); the file keeps them until it is next saved.</param>
public sealed record ConfigLoadResult(SplitPlayConfig Config, int? MigratedFrom, bool DefaultsApplied,
    IReadOnlyList<string>? DroppedWorkerSettings = null)
{
    /// <summary>True when the file on disk should be rewritten.</summary>
    public bool NeedsRewrite => MigratedFrom is not null || DefaultsApplied;
}

/// <summary>
/// Reads, validates, migrates and atomically writes <c>config.json</c> (docs/contracts/config.md).
/// </summary>
public static class ConfigStore
{
    public const string FileName = "config.json";

    /// <summary>Parses, migrates, fills defaults and validates a document.</summary>
    /// <exception cref="ConfigException">The document is malformed, too new or invalid.</exception>
    public static ConfigLoadResult Parse(string json)
    {
        JsonObject document;
        try
        {
            document = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new ConfigException(ConfigException.Codes.Malformed, "config must be a JSON object");
        }
        catch (JsonException ex)
        {
            throw new ConfigException(ConfigException.Codes.Malformed, $"config is not valid JSON: {ex.Message}", inner: ex);
        }

        var original = ConfigMigrator.Migrate(document);

        SplitPlayConfig config;
        try
        {
            config = document.Deserialize(SplitPlayJsonContext.Default.SplitPlayConfig)
                     ?? throw new ConfigException(ConfigException.Codes.Malformed, "config must be a JSON object");
        }
        catch (JsonException ex)
        {
            throw new ConfigException(ConfigException.Codes.Malformed, $"config has a wrong value type: {ex.Message}", inner: ex);
        }

        // A setting a desk may not change (older files, hand edits) is left out rather than failing the whole file.
        var dropped = DropDisallowedWorkerSettings(config);

        // Validate before filling defaults so that an explicit bad value is reported as such.
        ThrowIfInvalid(config);
        var defaultsApplied = ApplyDefaults(config);
        ThrowIfInvalid(config);

        return new ConfigLoadResult(config, original < SplitPlayConfig.CurrentSchemaVersion ? original : null, defaultsApplied,
            dropped.Count > 0 ? dropped : null);
    }

    /// <summary>Removes desk worker settings that are well-formed but not allowed; returns them as <c>desk-id.name</c>.</summary>
    public static List<string> DropDisallowedWorkerSettings(SplitPlayConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var dropped = new List<string>();
        foreach (var desk in config.Desks)
        {
            if (desk.WorkerSettings is not { } settings)
            {
                continue;
            }

            foreach (var name in settings.Keys.ToList())
            {
                if (name is not null && !ConfigValidator.IsAllowedWorkerSetting(name) && !ConfigValidator.ReservedWorkerSettings.Contains(name))
                {
                    settings.Remove(name);
                    dropped.Add($"{desk.Id}.{name}");
                }
            }
        }

        return dropped;
    }

    /// <summary>
    /// Loads <paramref name="path"/>. A missing file yields the default configuration, which is written out.
    /// An older schema is migrated and the file rewritten (the previous file is kept as <c>config.json.v{N}.bak</c>).
    /// </summary>
    public static ConfigLoadResult LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var config = new SplitPlayConfig();
            Save(path, config);
            return new ConfigLoadResult(config, null, false);
        }

        var result = Parse(File.ReadAllText(path, Encoding.UTF8));
        if (result.MigratedFrom is int from)
        {
            File.Copy(path, $"{path}.v{from}.bak", overwrite: true);
        }

        if (result.NeedsRewrite)
        {
            Save(path, result.Config);
        }

        return result;
    }

    /// <summary>Serializes a configuration as indented UTF-8 JSON (no BOM).</summary>
    public static string Serialize(SplitPlayConfig config)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            JsonSerializer.Serialize(writer, config, SplitPlayJsonContext.Default.SplitPlayConfig);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Validates and writes the file atomically: the content goes to a temporary file in the same directory,
    /// is flushed to disk and then renamed over the target (on Windows <c>MoveFileEx</c> with replace).
    /// </summary>
    public static void Save(string path, SplitPlayConfig config)
    {
        ThrowIfInvalid(config);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Serialize(config) + "\n");
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// Fills values the contract lets the service choose: base ports (lowest free slot), user names and display names.
    /// Returns true if anything changed.
    /// </summary>
    public static bool ApplyDefaults(SplitPlayConfig config)
    {
        var changed = false;
        foreach (var desk in config.Desks)
        {
            changed |= ApplyDeskDefaults(desk, config);
        }

        return changed;
    }

    /// <summary>Fills defaults for one desk given the rest of the configuration.</summary>
    public static bool ApplyDeskDefaults(DeskConfig desk, SplitPlayConfig config)
    {
        var changed = false;
        if (desk.BasePort is null)
        {
            var used = config.Desks.Where(d => !ReferenceEquals(d, desk) && d.BasePort is not null).Select(d => d.BasePort!.Value);
            desk.BasePort = BasePortAllocator.Allocate(config.Host.BasePortStart, config.Host.BasePortStride, used, [config.Host.WebPort]);
            changed = true;
        }

        if (desk.User is null && ConfigValidator.IsValidDeskId(desk.Id))
        {
            desk.User = DefaultUserName(desk.Id, config.Desks.Where(d => !ReferenceEquals(d, desk)).Select(d => d.User));
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(desk.Name))
        {
            desk.Name = desk.Id;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Derives a Windows account name from a desk id: <c>desk-1</c> becomes <c>splitplay-desk1</c>, truncated to the
    /// 20-character SAM limit and suffixed with a number if it collides with another desk's user.
    /// </summary>
    public static string DefaultUserName(string deskId, IEnumerable<string?> taken)
    {
        var takenSet = new HashSet<string>(taken.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var stem = "splitplay-" + deskId.Replace("-", "", StringComparison.Ordinal);
        if (stem.Length > ConfigValidator.MaxUserNameLength)
        {
            stem = stem[..ConfigValidator.MaxUserNameLength];
        }

        var candidate = stem;
        for (var n = 2; takenSet.Contains(candidate); n++)
        {
            var suffix = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            candidate = stem[..Math.Min(stem.Length, ConfigValidator.MaxUserNameLength - suffix.Length)] + suffix;
        }

        return candidate;
    }

    /// <summary>Throws a <see cref="ConfigException"/> listing every problem if the configuration is invalid.</summary>
    public static void ThrowIfInvalid(SplitPlayConfig config)
    {
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            throw new ConfigException(ConfigException.Codes.Invalid,
                "invalid configuration: " + string.Join("; ", errors), errors);
        }
    }
}
