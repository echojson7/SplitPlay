// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Nvidia;

/// <summary>
/// An in-memory driver settings database for tests and the fake platform. <see cref="Open"/> hands out a copy;
/// <see cref="IDrsSession.Save"/> writes it back, like NvAPI's load/save. Null <see cref="Driver"/>: no NVIDIA driver.
/// </summary>
public sealed class FakeDrs : IDrsProvider
{
    private readonly object _gate = new();

    /// <summary>Profile name (null key: base profile, stored as "") → settings.</summary>
    private Dictionary<string, Dictionary<uint, uint>> _settings = new(StringComparer.OrdinalIgnoreCase) { [""] = [] };

    private Dictionary<string, HashSet<string>> _apps = new(StringComparer.OrdinalIgnoreCase);

    public bool Driver { get; set; } = true;

    /// <summary>Sessions opened so far.</summary>
    public int Opens { get; private set; }

    /// <summary>Saves so far.</summary>
    public int Saves { get; private set; }

    /// <summary>Throw from the next <see cref="IDrsSession.Save"/> (once).</summary>
    public bool FailNextSave { get; set; }

    public IDrsSession? Open()
    {
        lock (_gate)
        {
            if (!Driver)
            {
                return null;
            }

            Opens++;
            return new Session(this, Copy(_settings), _apps.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase));
        }
    }

    // ---- what the driver holds (saved state), for tests

    public bool HasProfile(string name)
    {
        lock (_gate)
        {
            return _apps.ContainsKey(name);
        }
    }

    public IReadOnlySet<string> Applications(string profile)
    {
        lock (_gate)
        {
            return _apps.TryGetValue(profile, out var apps) ? new HashSet<string>(apps) : new HashSet<string>();
        }
    }

    public uint? Get(string? profile, uint id)
    {
        lock (_gate)
        {
            return _settings.TryGetValue(profile ?? "", out var s) && s.TryGetValue(id, out var v) ? v : null;
        }
    }

    /// <summary>Changes the driver directly (the owner in NVIDIA Control Panel); null removes the value.</summary>
    public void Set(string? profile, uint id, uint? value)
    {
        lock (_gate)
        {
            if (profile is not null && !_apps.ContainsKey(profile))
            {
                _apps[profile] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var s = _settings.TryGetValue(profile ?? "", out var existing) ? existing : _settings[profile ?? ""] = [];
            if (value is { } v)
            {
                s[id] = v;
            }
            else
            {
                s.Remove(id);
            }
        }
    }

    /// <summary>Creates a profile directly (another program, or a worker before patch 0020).</summary>
    public void AddProfile(string name, params string[] apps)
    {
        lock (_gate)
        {
            _apps[name] = new HashSet<string>(apps, StringComparer.OrdinalIgnoreCase);
            _settings.TryAdd(name, []);
        }
    }

    private static Dictionary<string, Dictionary<uint, uint>> Copy(Dictionary<string, Dictionary<uint, uint>> source) =>
        source.ToDictionary(kv => kv.Key, kv => new Dictionary<uint, uint>(kv.Value), StringComparer.OrdinalIgnoreCase);

    private sealed class Session(FakeDrs owner, Dictionary<string, Dictionary<uint, uint>> settings, Dictionary<string, HashSet<string>> apps)
        : IDrsSession
    {
        public bool ProfileExists(string name) => apps.ContainsKey(name);

        public void CreateProfile(string name)
        {
            if (!apps.TryAdd(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            {
                throw new NvidiaDrsException($"NvAPI_DRS_CreateProfile({name}): NVAPI_PROFILE_NAME_IN_USE");
            }

            settings[name] = [];
        }

        public void DeleteProfile(string name)
        {
            apps.Remove(name);
            settings.Remove(name);
        }

        public bool HasApplication(string profile, string executable) =>
            apps.TryGetValue(profile, out var a) ? a.Contains(executable) : throw new NvidiaDrsException($"{profile}: NVAPI_PROFILE_NOT_FOUND");

        public void AddApplication(string profile, string executable)
        {
            if (apps.Any(kv => kv.Value.Contains(executable)))
            {
                throw new NvidiaDrsException($"NvAPI_DRS_CreateApplication({executable}): NVAPI_EXECUTABLE_ALREADY_IN_USE");
            }

            Profile(profile);
            apps[profile].Add(executable);
        }

        public uint? GetSetting(string? profile, uint settingId) => Profile(profile).TryGetValue(settingId, out var v) ? v : null;

        public void SetSetting(string? profile, uint settingId, uint value) => Profile(profile)[settingId] = value;

        public void DeleteSetting(string? profile, uint settingId) => Profile(profile).Remove(settingId);

        public void Save()
        {
            lock (owner._gate)
            {
                if (owner.FailNextSave)
                {
                    owner.FailNextSave = false;
                    throw new NvidiaDrsException("NvAPI_DRS_SaveSettings: NVAPI_ERROR");
                }

                owner._settings = Copy(settings);
                owner._apps = apps.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
                owner.Saves++;
            }
        }

        public void Dispose()
        {
        }

        private Dictionary<uint, uint> Profile(string? profile)
        {
            if (profile is not null && !apps.ContainsKey(profile))
            {
                throw new NvidiaDrsException($"{profile}: NVAPI_PROFILE_NOT_FOUND");
            }

            return settings[profile ?? ""];
        }
    }
}
