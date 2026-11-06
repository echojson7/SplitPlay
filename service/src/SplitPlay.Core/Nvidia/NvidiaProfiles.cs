// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplitPlay.Core.Nvidia;

/// <summary>
/// What SplitPlay wants in one driver profile: the profile (null = the base, global profile), the executables it must
/// contain (a named profile is created by SplitPlay when missing) and its DWORD settings.
/// </summary>
public sealed record DrsProfileSpec(string? Profile, IReadOnlyList<string> Applications, IReadOnlyDictionary<uint, uint> Settings);

/// <summary>What <see cref="NvidiaProfiles.Apply"/> did: one line per change, notes for what it left alone or could not do.</summary>
/// <param name="Driver">False when there is no NVIDIA driver (nothing was done).</param>
public sealed record NvidiaProfilesResult(bool Driver, IReadOnlyList<string> Changes, IReadOnlyList<string> Notes);

/// <summary>
/// The service's owner of the NVIDIA driver settings (NvAPI DRS): the only writer, so desks no longer race on one
/// database (before, every worker changed it itself). <see cref="Apply"/> brings the driver to the wanted set of
/// profiles and settings and gives back everything SplitPlay set earlier that is no longer wanted:
/// <list type="bullet">
/// <item>Before a value is changed for the first time, its previous value (or "not set") is written to
/// <c>nvidia-profiles.json</c>, so a crash between the record and the driver save leaves nothing unrecorded.</item>
/// <item>A value is given back only while it still holds SplitPlay's value: something the owner changed later (NVIDIA
/// Control Panel, NVIDIA Profile Inspector) is left alone, as upstream Sunshine does.</item>
/// <item>A profile SplitPlay created is deleted as a whole. A profile whose name starts with "SplitPlay " counts as
/// SplitPlay's even without a record: workers before patch 0020 created "SplitPlay worker" themselves.</item>
/// <item>The undo file those workers (and upstream Sunshine) keep, <c>%ProgramData%\Sunshine\nvprefs_undo.json</c>, is
/// taken over when no running process holds it: its previous value moves into the record and the file is deleted.</item>
/// </list>
/// Thread-safe: calls are serialised.
/// </summary>
public sealed class NvidiaProfiles
{
    public const string StateFileName = "nvidia-profiles.json";

    /// <summary>Profiles with this prefix are SplitPlay's own.</summary>
    public const string OwnPrefix = "SplitPlay ";

    /// <summary>The worker's application profile (the name patch 0015 used, so it is adopted, not duplicated).</summary>
    public const string WorkerProfile = "SplitPlay worker";

    public const string WorkerExecutable = "splitplay-worker.exe";

    private readonly object _gate = new();
    private readonly IDrsProvider _drs;
    private readonly string _stateFile;
    private readonly string? _legacyUndoFile;

    /// <param name="legacyUndoFile">Upstream's undo file to take over (null: none).</param>
    public NvidiaProfiles(IDrsProvider drs, string stateFile, string? legacyUndoFile)
    {
        ArgumentNullException.ThrowIfNull(drs);
        ArgumentException.ThrowIfNullOrEmpty(stateFile);
        _drs = drs;
        _stateFile = stateFile;
        _legacyUndoFile = legacyUndoFile;
    }

    /// <summary><c>nvidia-profiles.json</c> under a data root.</summary>
    public static string StateFileIn(string dataRoot) => Path.Combine(dataRoot, StateFileName);

    /// <summary><c>%ProgramData%\Sunshine\nvprefs_undo.json</c>, the undo file of upstream's nvprefs (null off Windows).</summary>
    public static string? DefaultLegacyUndoFile() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sunshine", "nvprefs_undo.json")
            : null;

    /// <summary>
    /// The worker's profile: "prefer maximum performance" for <c>splitplay-worker.exe</c>, so the GPU does not clock
    /// down between frames while a desk streams. Kept while SplitPlay is installed.
    /// </summary>
    public static DrsProfileSpec Worker { get; } = new(WorkerProfile, [WorkerExecutable],
        new Dictionary<uint, uint> { [DrsSettings.PreferredPState] = DrsSettings.PreferredPStatePreferMax });

    /// <summary>
    /// Upstream's global change: Vulkan/OpenGL games present through a DXGI swapchain, so desktop duplication captures
    /// them like Direct3D games. Global, so it is held only while the service runs.
    /// </summary>
    public static DrsProfileSpec Global { get; } = new(null, [],
        new Dictionary<uint, uint> { [DrsSettings.OglPreferDxPresent] = DrsSettings.OglPreferDxPresentEnabled });

    /// <summary>The record (for status and tests); empty when nothing is held.</summary>
    public NvidiaProfilesState LoadState()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    /// <summary>
    /// Makes the driver hold exactly <paramref name="wanted"/> of what SplitPlay manages: missing profiles, executables
    /// and values are set, and what SplitPlay set before but is not in <paramref name="wanted"/> any more is given back
    /// (an empty list gives everything back). Throws <see cref="NvidiaDrsException"/> when a driver call fails; the
    /// record keeps what was already done.
    /// </summary>
    public NvidiaProfilesResult Apply(IReadOnlyList<DrsProfileSpec> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        lock (_gate)
        {
            var changes = new List<string>();
            var notes = new List<string>();
            using var drs = _drs.Open();
            if (drs is null)
            {
                return new NvidiaProfilesResult(false, changes, ["no NVIDIA driver"]);
            }

            var state = Load();
            TakeOverLegacyUndoFile(state, changes, notes);
            var dirty = false;

            // ---- give back what is no longer wanted (first, so that a value moving between profiles ends up clean)
            var wantedProfiles = wanted.Where(w => w.Profile is not null).Select(w => w.Profile!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var doomed = state.CreatedProfiles.Append(WorkerProfile).Where(p => !wantedProfiles.Contains(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Wanted(OwnedSetting s) => wanted.Any(w => SameProfile(w.Profile, s.Profile) && w.Settings.ContainsKey(s.Id));

            foreach (var owned in state.Settings.Where(s => !Wanted(s)).ToList())
            {
                // A value in a profile that is deleted below goes with it.
                if (owned.Profile is not { } p || !doomed.Contains(p))
                {
                    dirty |= GiveBack(drs, owned, changes, notes);
                }

                state.Settings.Remove(owned);
                Save(state);
            }

            // Profiles SplitPlay created, and "SplitPlay worker" without a record (made by a worker before patch 0020).
            foreach (var profile in doomed.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (drs.ProfileExists(profile))
                {
                    drs.DeleteProfile(profile);
                    changes.Add($"profile \"{profile}\" deleted");
                    dirty = true;
                }

                if (state.CreatedProfiles.RemoveAll(p => SameProfile(p, profile)) > 0)
                {
                    Save(state);
                }
            }

            // ---- set what is wanted
            foreach (var spec in wanted)
            {
                if (spec.Profile is { } name)
                {
                    if (!drs.ProfileExists(name))
                    {
                        // Recorded before the driver is saved: a crash in between leaves at worst the record of a
                        // profile that does not exist, which is harmless.
                        if (!state.CreatedProfiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                        {
                            state.CreatedProfiles.Add(name);
                            Save(state);
                        }

                        drs.CreateProfile(name);
                        changes.Add($"profile \"{name}\" created");
                        dirty = true;
                    }
                    else if (IsOwnName(name) && !state.CreatedProfiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        state.CreatedProfiles.Add(name);
                        Save(state);
                        notes.Add($"profile \"{name}\" adopted (made by an earlier worker)");
                    }

                    foreach (var exe in spec.Applications)
                    {
                        if (!drs.HasApplication(name, exe))
                        {
                            drs.AddApplication(name, exe);
                            changes.Add($"profile \"{name}\": {exe} added");
                            dirty = true;
                        }
                    }
                }

                foreach (var (id, value) in spec.Settings)
                {
                    var current = drs.GetSetting(spec.Profile, id);
                    var owned = state.Settings.FirstOrDefault(s => SameProfile(s.Profile, spec.Profile) && s.Id == id);
                    var setBefore = owned?.Ours;
                    if (owned is null)
                    {
                        // Remember what was there before the first change (also in SplitPlay's own profiles, so that a
                        // value no longer wanted there is removed).
                        state.Settings.Add(new OwnedSetting { Profile = spec.Profile, Id = id, Ours = value, Previous = current });
                        Save(state);
                    }
                    else if (owned.Ours != value)
                    {
                        owned.Ours = value;
                        Save(state);
                    }

                    if (current != value)
                    {
                        if (setBefore is { } before && current != before)
                        {
                            notes.Add($"{Where(spec.Profile)}: {DrsSettings.Name(id)} was changed to {Show(current)} outside SplitPlay; set again");
                        }

                        drs.SetSetting(spec.Profile, id, value);
                        changes.Add($"{Where(spec.Profile)}: {DrsSettings.Name(id)} {Show(current)} -> {value}");
                        dirty = true;
                    }
                }
            }

            if (dirty)
            {
                drs.Save();
            }

            return new NvidiaProfilesResult(true, changes, notes);
        }
    }

    // ------------------------------------------------------------------ internals

    private static bool GiveBack(IDrsSession drs, OwnedSetting owned, List<string> changes, List<string> notes)
    {
        var where = Where(owned.Profile);
        if (owned.Profile is { } p && !drs.ProfileExists(p))
        {
            notes.Add($"{where} is gone; {DrsSettings.Name(owned.Id)} not restored");
            return false;
        }

        var current = drs.GetSetting(owned.Profile, owned.Id);
        if (current != owned.Ours)
        {
            notes.Add($"{where}: {DrsSettings.Name(owned.Id)} was changed to {Show(current)} after SplitPlay set it; left alone");
            return false;
        }

        if (owned.Previous is { } previous)
        {
            drs.SetSetting(owned.Profile, owned.Id, previous);
        }
        else
        {
            drs.DeleteSetting(owned.Profile, owned.Id);
        }

        changes.Add($"{where}: {DrsSettings.Name(owned.Id)} restored to {Show(owned.Previous)}");
        return true;
    }

    /// <summary>
    /// Upstream's undo file: <c>{"opengl_swapchain":{"our_value":1,"undo_value":null|n}}</c>. Opened without sharing, so
    /// a running Sunshine or an old worker that holds it keeps it.
    /// </summary>
    private void TakeOverLegacyUndoFile(NvidiaProfilesState state, List<string> changes, List<string> notes)
    {
        if (_legacyUndoFile is null || !File.Exists(_legacyUndoFile))
        {
            return;
        }

        uint? ours = null;
        uint? previous = null;
        try
        {
            using (var file = new FileStream(_legacyUndoFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    using var doc = JsonDocument.Parse(file);
                    if (doc.RootElement.TryGetProperty("opengl_swapchain", out var swap) && swap.ValueKind == JsonValueKind.Object)
                    {
                        ours = Number(swap, "our_value");
                        previous = Number(swap, "undo_value");
                    }
                }
                catch (JsonException)
                {
                    notes.Add($"{_legacyUndoFile} is not valid JSON; deleted");
                }
            }

            File.Delete(_legacyUndoFile);
        }
        catch (IOException ex)
        {
            notes.Add($"{_legacyUndoFile} is in use or unreadable ({ex.Message}); left to its owner");
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            notes.Add($"{_legacyUndoFile}: {ex.Message}");
            return;
        }

        if (ours is not { } value)
        {
            return;
        }

        if (state.Settings.Any(s => s.Profile is null && s.Id == DrsSettings.OglPreferDxPresent))
        {
            // SplitPlay already holds the original value; the file's is a later, second-hand one.
            notes.Add($"{_legacyUndoFile} taken over (the original value was already recorded)");
            return;
        }

        var owned = new OwnedSetting { Profile = null, Id = DrsSettings.OglPreferDxPresent, Ours = value, Previous = previous };
        state.Settings.Add(owned);
        Save(state);
        changes.Add($"{_legacyUndoFile} taken over: global {DrsSettings.Name(owned.Id)} was {Show(previous)} before the workers changed it");
    }

    private static uint? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetUInt32(out var v) ? v : null;

    private static bool IsOwnName(string profile) => profile.StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool SameProfile(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Where(string? profile) => profile is null ? "global profile" : $"profile \"{profile}\"";

    private static string Show(uint? value) => value is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "not set";

    private NvidiaProfilesState Load()
    {
        if (!File.Exists(_stateFile))
        {
            return new NvidiaProfilesState();
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(_stateFile), NvidiaProfilesJsonContext.Default.NvidiaProfilesState)
                   ?? new NvidiaProfilesState();
        }
        catch (JsonException ex)
        {
            throw new NvidiaDrsException($"{_stateFile} is damaged ({ex.Message}); fix or delete it");
        }
    }

    private void Save(NvidiaProfilesState state)
    {
        if (state.Settings.Count == 0 && state.CreatedProfiles.Count == 0)
        {
            File.Delete(_stateFile);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_stateFile))!);
        var temp = _stateFile + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state, NvidiaProfilesJsonContext.Default.NvidiaProfilesState));
        File.Move(temp, _stateFile, overwrite: true);
    }
}

/// <summary><c>nvidia-profiles.json</c>: what SplitPlay changed in the driver settings, and what was there before.</summary>
public sealed class NvidiaProfilesState
{
    public int Version { get; set; } = 1;

    /// <summary>Profiles SplitPlay created (or adopted by name); deleted as a whole when no longer wanted.</summary>
    public List<string> CreatedProfiles { get; set; } = [];

    /// <summary>Values SplitPlay set in profiles that are not its own (the global profile).</summary>
    public List<OwnedSetting> Settings { get; set; } = [];
}

/// <summary>One value SplitPlay set, with the value before (null: the profile did not set it).</summary>
public sealed class OwnedSetting
{
    /// <summary>Null: the base (global) profile.</summary>
    public string? Profile { get; set; }

    public uint Id { get; set; }

    public uint Ours { get; set; }

    public uint? Previous { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NvidiaProfilesState))]
public sealed partial class NvidiaProfilesJsonContext : JsonSerializerContext;
