// SPDX-License-Identifier: MIT
using SplitPlay.Core.Configuration;
using SplitPlay.Service.Api;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Configuration;

/// <summary>
/// Owns the in-memory configuration and persists every change atomically. All mutations go through
/// <see cref="Update{T}"/>, which works on a copy and only commits it when it validates and saves.
/// </summary>
public sealed class ConfigService
{
    private readonly object _gate = new();
    private readonly DataPaths _paths;
    private SplitPlayConfig _current;

    public ConfigService(DataPaths paths, ConfigLoadResult loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        _paths = paths;
        _current = loaded.Config.DeepClone();
        Loaded = loaded;
    }

    /// <summary>How the configuration was loaded at startup (for diagnostics).</summary>
    public ConfigLoadResult Loaded { get; }

    /// <summary>A copy of the current configuration.</summary>
    public SplitPlayConfig Snapshot()
    {
        lock (_gate)
        {
            return _current.DeepClone();
        }
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to a copy, fills defaults, validates and saves it, then makes it current.
    /// Validation problems surface as <see cref="ApiException"/> (400).
    /// </summary>
    public T Update<T>(Func<SplitPlayConfig, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var copy = _current.DeepClone();
            var result = mutate(copy);
            try
            {
                ConfigStore.ThrowIfInvalid(copy);
                ConfigStore.ApplyDefaults(copy);
                ConfigStore.Save(_paths.ConfigFile, copy);
            }
            catch (ConfigException ex)
            {
                throw ApiException.Validation(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                throw ApiException.Validation(ex.Message);
            }

            _current = copy;
            return result;
        }
    }

    /// <summary>Replaces the whole configuration (import). The configuration must already be valid.</summary>
    public void Replace(SplitPlayConfig config)
    {
        lock (_gate)
        {
            ConfigStore.Save(_paths.ConfigFile, config);
            _current = config.DeepClone();
        }
    }
}
