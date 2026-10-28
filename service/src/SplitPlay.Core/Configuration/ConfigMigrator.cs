// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace SplitPlay.Core.Configuration;

/// <summary>
/// Upgrades older <c>config.json</c> documents to <see cref="SplitPlayConfig.CurrentSchemaVersion"/> in memory.
/// Each step takes a document of version N and returns one of version N+1.
/// </summary>
public static class ConfigMigrator
{
    private static readonly SortedDictionary<int, Action<JsonObject>> Steps = new()
    {
        [0] = MigrateV0ToV1,
    };

    /// <summary>Reads <c>schemaVersion</c>; a document without one is treated as version 0 (pre-release format).</summary>
    public static int ReadSchemaVersion(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.TryGetPropertyValue("schemaVersion", out var node) || node is null)
        {
            return 0;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var version))
        {
            return version;
        }

        throw new ConfigException(ConfigException.Codes.Invalid, "schemaVersion must be an integer");
    }

    /// <summary>
    /// Migrates <paramref name="document"/> in place. Returns the version it had before migration.
    /// </summary>
    /// <exception cref="ConfigException">The document is newer than this build understands.</exception>
    public static int Migrate(JsonObject document)
    {
        var version = ReadSchemaVersion(document);
        if (version > SplitPlayConfig.CurrentSchemaVersion)
        {
            throw new ConfigException(ConfigException.Codes.SchemaTooNew,
                $"config schemaVersion {version} is newer than the supported version {SplitPlayConfig.CurrentSchemaVersion}");
        }

        if (version < 0)
        {
            throw new ConfigException(ConfigException.Codes.Invalid, "schemaVersion must not be negative");
        }

        var original = version;
        while (version < SplitPlayConfig.CurrentSchemaVersion)
        {
            if (!Steps.TryGetValue(version, out var step))
            {
                throw new ConfigException(ConfigException.Codes.Invalid, $"no migration from schemaVersion {version}");
            }

            step(document);
            version++;
            document["schemaVersion"] = version;
        }

        return original;
    }

    /// <summary>
    /// Version 0 is the prototype layout used during the design stage: desks were called "desktops", the host
    /// section was optional and the base port was a single "basePort" on the host.
    /// </summary>
    private static void MigrateV0ToV1(JsonObject doc)
    {
        if (doc["desks"] is null && doc["desktops"] is JsonNode desktops)
        {
            doc.Remove("desktops");
            doc["desks"] = desktops;
        }

        doc["desks"] ??= new JsonArray();

        if (doc["host"] is not JsonObject host)
        {
            host = new JsonObject();
            doc["host"] = host;
        }

        if (host["basePortStart"] is null && host["basePort"] is JsonNode basePort)
        {
            host.Remove("basePort");
            host["basePortStart"] = basePort;
        }
    }
}
