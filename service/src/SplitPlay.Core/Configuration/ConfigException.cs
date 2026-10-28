// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Configuration;

/// <summary>A configuration file could not be loaded, migrated or saved.</summary>
public sealed class ConfigException : Exception
{
    public static class Codes
    {
        public const string Invalid = "config_invalid";
        public const string SchemaTooNew = "schema_too_new";
        public const string Malformed = "config_malformed";
    }

    public ConfigException(string code, string message, IReadOnlyList<ConfigError>? errors = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Errors = errors ?? [];
    }

    public string Code { get; }

    public IReadOnlyList<ConfigError> Errors { get; }
}
