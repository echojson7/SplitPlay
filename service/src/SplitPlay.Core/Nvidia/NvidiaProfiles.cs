// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplitPlay.Core.Nvidia;

/// <summary>
/// What SplitPlay wants in one driver profile: the profile (null = the base, global profile), the executables it must
/// contain (a named profile is created by SplitPlay when missing) and its DWORD settings.
/// </summary>
/// <param name="YieldToOwner">
/// A value the owner set (not SplitPlay's, not NVIDIA's predefined one) is left alone, and SplitPlay forgets its own
/// record of it: used for games, where a limit set in NVIDIA Control Panel wins.
/// </param>
public sealed record DrsProfileSpec(string? Profile, IReadOnlyList<string> Applications, IReadOnlyDictionary<uint, uint> Settings,
    bool YieldToOwner = false);

/// <summary>
/// DWORD settings for one executable, wherever the driver keeps it: in the profile that already lists it (often one of
/// NVIDIA's predefined game profiles; its values are recorded and given back like the global ones), otherwise in a
/// profile SplitPlay creates for it ("SplitPlay game &lt;exe&gt;"). Values the owner set there are left alone.
/// </summary>
/// <param name="Path">
/// The executable's full path when known: NvAPI then finds exactly the profile the driver applies to it (entries tied
/// to a folder, generic names such as <c>game.exe</c> in several profiles); otherwise the file name is looked up.
/// </param>
public sealed record DrsAppSpec(string Executable, IReadOnlyDictionary<uint, uint> Settings, string? Path = null);

/// <summary>What became of one <see cref="DrsAppSpec"/>.</summary>
public enum DrsAppStatus
{
    /// <summary>The driver holds SplitPlay's values.</summary>
    Set,

    /// <summary>The owner set a value of their own there; left alone.</summary>
    Owner,

    /// <summary>A driver call failed (see the notes); tried again at the next apply.</summary>
    Failed,
}

/// <param name="Profile">The driver profile that holds the executable's settings (null when it could not be found).</param>
public sealed record DrsAppResult(string Executable, string? Profile, DrsAppStatus Status);

/// <summary>What <see cref="NvidiaProfiles.Apply"/> did: one line per change, notes for what it left alone or could not do.</summary>
/// <param name="Driver">False when there is no NVIDIA driver (nothing was done).</param>
/// <param name="Failed">Steps that failed (also in <paramref name="Notes"/>); their records are kept for a later attempt.</param>
/// <param name="Apps">One entry per <see cref="DrsAppSpec"/> (empty without a driver).</param>
public sealed record NvidiaProfilesResult(bool Driver, IReadOnlyList<string> Changes, IReadOnlyList<string> Notes, int Failed = 0,
    bool Held = false, IReadOnlyList<DrsAppResult>? Apps = null);

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

    /// <summary>The profile SplitPlay creates for a game no driver profile lists yet: this prefix and the file name.</summary>
    public const string GamePrefix = OwnPrefix + "game ";

    /// <summary>The name of SplitPlay's own profile for <paramref name="executable"/>.</summary>
    public static string GameProfile(string executable) => GamePrefix + executable;

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
    /// (an empty list gives everything back).
    /// <para>
    /// Crash safety: what SplitPlay is about to change (a new record, a new value of its own) reaches
    /// <c>nvidia-profiles.json</c> before the driver changes; what it gave back leaves the record only after the
    /// driver saved. A step that fails (one spec, one give-back) is reported in the notes and the rest still happens;
    /// a failed driver save throws <see cref="NvidiaDrsException"/> and leaves the record as it was.
    /// </para>
    /// </summary>
    /// <param name="apps">
    /// Settings per executable (the game FPS limits): each is resolved to the profile that lists the executable and then
    /// handled like <paramref name="wanted"/>; executables no longer listed are given back the same way.
    /// </param>
    public NvidiaProfilesResult Apply(IReadOnlyList<DrsProfileSpec> wanted, IReadOnlyList<DrsAppSpec>? apps = null)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        apps ??= [];
        lock (_gate)
        {
            var changes = new List<string>();
            var notes = new List<string>();
            using var drs = _drs.Open();
            if (drs is null)
            {
                var held = Load() is { } record && (record.Settings.Count > 0 || record.CreatedProfiles.Count > 0);
                return new NvidiaProfilesResult(false, changes, ["no NVIDIA driver"], Held: held, Apps: []);
            }

            // disk: what nvidia-profiles.json says until the driver saved (only grows); state: the record after it.
            var disk = Load();
            TakeOverLegacyUndoFile(disk, changes, notes);
            var state = Clone(disk);
            var dirty = false;
            var failed = 0;
            var confirmed = new HashSet<OwnedSetting>();

            bool Step(string what, Action action)
            {
                try
                {
                    action();
                    return true;
                }
                catch (NvidiaDrsException ex)
                {
                    failed++;
                    notes.Add($"{what} failed: {ex.Message}");
                    return false;
                }
            }

            // ---- executables to the profiles that hold them
            var appProfiles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var appSpecs = new List<DrsProfileSpec>();
            var lookupFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in apps)
            {
                string? found = null;
                // NvAPI documents a full path as "c:/Folder1/Folder2/App.exe".
                var name = app.Path is { Length: > 0 } path ? path.Replace('\\', '/') : app.Executable;
                if (!Step($"finding the profile of {app.Executable}", () => found = drs.FindApplicationProfile(name)))
                {
                    // Keep it where it was last time, so a passing NvAPI error does not give its limit back.
                    lookupFailed.Add(app.Executable);
                    if (!state.AppProfiles.TryGetValue(app.Executable, out var known))
                    {
                        appProfiles[app.Executable] = null;
                        continue;
                    }

                    found = known;
                }

                // Not listed anywhere (or in a profile of SplitPlay's): SplitPlay's own profile for it.
                var own = found is null || IsOwnName(found);
                var profile = own ? found ?? GameProfile(app.Executable) : found!;
                appProfiles[app.Executable] = profile;
                state.AppProfiles[app.Executable] = profile;
                var index = appSpecs.FindIndex(a => SameProfile(a.Profile, profile));
                if (index < 0)
                {
                    appSpecs.Add(new DrsProfileSpec(profile, own ? [app.Executable] : [], app.Settings, YieldToOwner: true));
                    continue;
                }

                // Two executables of one profile (a game and its launcher in NVIDIA's profile): the larger value wins.
                var merged = new Dictionary<uint, uint>(appSpecs[index].Settings);
                foreach (var (id, value) in app.Settings)
                {
                    merged[id] = merged.TryGetValue(id, out var other) ? Math.Max(other, value) : value;
                }

                appSpecs[index] = appSpecs[index] with
                {
                    Applications = own ? [.. appSpecs[index].Applications, app.Executable] : appSpecs[index].Applications,
                    Settings = merged,
                };
            }

            // A game profile can never replace one of the fixed specs.
            appSpecs.RemoveAll(a => wanted.Any(w => SameProfile(w.Profile, a.Profile)));
            wanted = [.. wanted, .. appSpecs];
            foreach (var exe in state.AppProfiles.Keys.Where(e => !apps.Any(a => string.Equals(a.Executable, e, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                state.AppProfiles.Remove(exe);
            }

            // The owner's own global frame rate cap (NVIDIA Control Panel, global settings) is not raised per game.
            var ownerCaps = new Dictionary<uint, uint>();
            foreach (var id in appSpecs.SelectMany(a => a.Settings.Keys).Distinct())
            {
                Step($"reading global {DrsSettings.Name(id)}", () =>
                {
                    if (drs.GetSetting(null, id) is { } global && global != 0 && Find(state, null, id) is null)
                    {
                        ownerCaps[id] = global;
                    }
                });
            }

            // ---- give back what is no longer wanted (first, so that a value moving between profiles ends up clean)
            var wantedProfiles = wanted.Where(w => w.Profile is not null).Select(w => w.Profile!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var doomed = state.CreatedProfiles.Append(WorkerProfile).Where(p => !wantedProfiles.Contains(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Wanted(OwnedSetting s) => wanted.Any(w => SameProfile(w.Profile, s.Profile) && w.Settings.ContainsKey(s.Id));

            var ownedBefore = state.Settings.ToList();
            foreach (var owned in state.Settings.Where(s => !Wanted(s)).ToList())
            {
                // A value in a profile that is deleted below goes with it.
                if (owned.Profile is { } p && doomed.Contains(p))
                {
                    state.Settings.Remove(owned);
                    continue;
                }

                Step($"giving back {Where(owned.Profile)} {DrsSettings.Name(owned.Id)}", () =>
                {
                    dirty |= GiveBack(drs, owned, changes, notes);
                    state.Settings.Remove(owned);
                });
            }

            // Profiles SplitPlay created, and "SplitPlay worker" without a record (made by a worker before patch 0020).
            foreach (var profile in doomed.Order(StringComparer.OrdinalIgnoreCase))
            {
                Step($"deleting profile \"{profile}\"", () =>
                {
                    if (profile.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase) && drs.ProfileExists(profile)
                        && drs.GetSetting(profile, DrsSettings.FrameRateLimit) is { } limit && limit != 0
                        && !ownedBefore.Any(o => SameProfile(o.Profile, profile) && o.Id == DrsSettings.FrameRateLimit && o.IsOurs(limit)))
                    {
                        // The owner set a limit of their own there: the profile is theirs now.
                        notes.Add($"profile \"{profile}\" kept: it holds a limit set outside SplitPlay");
                    }
                    else if (drs.ProfileExists(profile))
                    {
                        drs.DeleteProfile(profile);
                        changes.Add($"profile \"{profile}\" deleted");
                        dirty = true;
                    }

                    state.CreatedProfiles.RemoveAll(p => SameProfile(p, profile));
                    state.Settings.RemoveAll(s => SameProfile(s.Profile, profile));
                });
            }

            // ---- set what is wanted, one spec at a time
            var failedProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in wanted)
            {
                if (!Step(Where(spec.Profile), () => dirty |= SetWanted(drs, spec, disk, state, confirmed, changes, notes, yielded, ownerCaps)))
                {
                    failedProfiles.Add(spec.Profile ?? "");
                }
            }

            if (dirty)
            {
                drs.Save();
            }

            // Saved now: the driver holds Ours for these, so SplitPlay's earlier values no longer count. Any other entry keeps
            // them (its value was not written, or its step failed).
            foreach (var owned in confirmed)
            {
                owned.Earlier.Clear();
            }

            Save(state);
            var stillHeld = state.Settings.Count > 0 || state.CreatedProfiles.Count > 0;
            var appResults = apps.Select(a => appProfiles.GetValueOrDefault(a.Executable) is not { } profile
                    ? new DrsAppResult(a.Executable, null, DrsAppStatus.Failed)
                    : new DrsAppResult(a.Executable, profile,
                        failedProfiles.Contains(profile) || lookupFailed.Contains(a.Executable) ? DrsAppStatus.Failed
                        : yielded.Contains(profile) ? DrsAppStatus.Owner
                        : DrsAppStatus.Set))
                .ToList();
            return new NvidiaProfilesResult(true, changes, notes, failed, stillHeld, appResults);
        }
    }

    /// <summary>One wanted profile; returns true when the driver changed. Additions are recorded on disk first.</summary>
    private bool SetWanted(IDrsSession drs, DrsProfileSpec spec, NvidiaProfilesState disk, NvidiaProfilesState state,
        HashSet<OwnedSetting> confirmed, List<string> changes, List<string> notes, HashSet<string> yielded,
        IReadOnlyDictionary<uint, uint>? ownerCaps = null)
    {
        var dirty = false;
        if (spec.Profile is { } name)
        {
            var exists = drs.ProfileExists(name);
            if (!exists || IsOwnName(name))
            {
                if (!state.CreatedProfiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    state.CreatedProfiles.Add(name);
                    if (exists)
                    {
                        notes.Add($"profile \"{name}\" adopted (made by an earlier worker)");
                    }
                }

                if (!disk.CreatedProfiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    disk.CreatedProfiles.Add(name);
                    Save(disk);
                }
            }

            if (!exists)
            {
                drs.CreateProfile(name);
                changes.Add($"profile \"{name}\" created");
                dirty = true;
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
            var owned = Find(state, spec.Profile, id);
            var existed = owned is not null;
            var ours = owned is not null && owned.IsOurs(current);
            var predefined = current is not null && !ours && drs.IsPredefined(spec.Profile, id);
            if (spec.YieldToOwner && ownerCaps is not null && ownerCaps.TryGetValue(id, out var cap) && (current is null || ours))
            {
                // A cap the owner set for every program: SplitPlay's own value goes (the game then gets the owner's).
                if (owned is not null && current is not null)
                {
                    dirty |= GiveBack(drs, owned, changes, notes);
                }

                if (owned is not null)
                {
                    state.Settings.Remove(owned);
                }

                yielded.Add(spec.Profile ?? "");
                notes.Add($"{Where(spec.Profile)}: the global {DrsSettings.Name(id)} {cap} was set outside SplitPlay; left to it");
                continue;
            }

            if (spec.YieldToOwner && current is { } theirs && theirs != 0 && !ours && !predefined)
            {
                // The owner's own value (NVIDIA Control Panel, Profile Inspector): theirs to keep. 0 is "off", which
                // NVIDIA Control Panel also writes when a limit is switched off again: not a choice to keep. SplitPlay's record
                // goes (there is nothing of SplitPlay's left to give back); the driver is not touched.
                if (owned is not null)
                {
                    state.Settings.Remove(owned);
                }

                yielded.Add(spec.Profile ?? "");
                notes.Add($"{Where(spec.Profile)}: {DrsSettings.Name(id)} is {Show(theirs)}, set outside SplitPlay; left alone");
                continue;
            }

            if (owned is null)
            {
                // Remember what was there before the first change (also in SplitPlay's own profiles, so that a value no
                // longer wanted there is removed).
                owned = new OwnedSetting { Profile = spec.Profile, Id = id, Ours = value, Previous = current, PreviousPredefined = predefined };
                state.Settings.Add(owned);
                disk.Settings.RemoveAll(s => SameProfile(s.Profile, spec.Profile) && s.Id == id);
                disk.Settings.Add(new OwnedSetting { Profile = spec.Profile, Id = id, Ours = value, Previous = current, PreviousPredefined = predefined });
                Save(disk);
            }
            else if (owned.Ours != value)
            {
                // Until the driver saved, the earlier values count as SplitPlay's too.
                var onDisk = Find(disk, spec.Profile, id);
                if (onDisk is not null)
                {
                    onDisk.Remember(onDisk.Ours);
                    onDisk.Ours = value;
                    Save(disk);
                }

                owned.Remember(owned.Ours);
                owned.Ours = value;
            }

            if (current == value)
            {
                confirmed.Add(owned);
            }
            else
            {
                if (existed && !ours)
                {
                    notes.Add($"{Where(spec.Profile)}: {DrsSettings.Name(id)} was changed to {Show(current)} outside SplitPlay; set again");
                }

                drs.SetSetting(spec.Profile, id, value);
                changes.Add($"{Where(spec.Profile)}: {DrsSettings.Name(id)} {Show(current)} -> {value}");
                dirty = true;
                confirmed.Add(owned);
            }
        }

        return dirty;
    }

    private static OwnedSetting? Find(NvidiaProfilesState state, string? profile, uint id) =>
        state.Settings.FirstOrDefault(s => SameProfile(s.Profile, profile) && s.Id == id);

    private static NvidiaProfilesState Clone(NvidiaProfilesState state) =>
        Normalize(JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(state, NvidiaProfilesJsonContext.Default.NvidiaProfilesState),
            NvidiaProfilesJsonContext.Default.NvidiaProfilesState)!);

    /// <summary>Case-insensitive executables and no null lists after reading (hand edits, older files).</summary>
    private static NvidiaProfilesState Normalize(NvidiaProfilesState state)
    {
        state.CreatedProfiles ??= [];
        state.Settings ??= [];
        state.AppProfiles = new Dictionary<string, string>(state.AppProfiles ?? [], StringComparer.OrdinalIgnoreCase);
        return state;
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
        if (!owned.IsOurs(current))
        {
            notes.Add($"{where}: {DrsSettings.Name(owned.Id)} was changed to {Show(current)} after SplitPlay set it; left alone");
            return false;
        }

        if (owned.Previous is { } previous && !owned.PreviousPredefined)
        {
            drs.SetSetting(owned.Profile, owned.Id, previous);
        }
        else
        {
            // Not set before, or NVIDIA's predefined value, which deleting the setting brings back.
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

        if (ours is { } value)
        {
            if (state.Settings.Any(s => s.Profile is null && s.Id == DrsSettings.OglPreferDxPresent))
            {
                // SplitPlay already holds the original value; the file's is a later, second-hand one.
                notes.Add($"{_legacyUndoFile} taken over (the original value was already recorded)");
            }
            else
            {
                // On record before the file goes: a crash in between leaves both, and the file is taken over again.
                state.Settings.Add(new OwnedSetting { Profile = null, Id = DrsSettings.OglPreferDxPresent, Ours = value, Previous = previous });
                Save(state);
                changes.Add($"{_legacyUndoFile} taken over: global {DrsSettings.Name(DrsSettings.OglPreferDxPresent)} was {Show(previous)} before the workers changed it");
            }
        }

        try
        {
            File.Delete(_legacyUndoFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"{_legacyUndoFile} could not be deleted: {ex.Message}");
        }
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
            return Normalize(JsonSerializer.Deserialize(File.ReadAllBytes(_stateFile), NvidiaProfilesJsonContext.Default.NvidiaProfilesState)
                   ?? new NvidiaProfilesState());
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
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(JsonSerializer.SerializeToUtf8Bytes(state, NvidiaProfilesJsonContext.Default.NvidiaProfilesState));
            // Through to the disk before the rename: after a power cut the record is the old one or the new one.
            file.Flush(flushToDisk: true);
        }

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

    /// <summary>
    /// Executable → the profile its per-game settings went to last time: kept when a later lookup fails, so a passing
    /// NvAPI error does not give a game's limit back.
    /// </summary>
    public Dictionary<string, string> AppProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One value SplitPlay set, with the value before (null: the profile did not set it).</summary>
public sealed class OwnedSetting
{
    /// <summary>Null: the base (global) profile.</summary>
    public string? Profile { get; set; }

    public uint Id { get; set; }

    public uint Ours { get; set; }

    /// <summary>
    /// SplitPlay's earlier values while a change of <see cref="Ours"/> is not known to be saved in the driver: they
    /// still count as SplitPlay's (not the owner's) when the value is given back.
    /// </summary>
    public List<uint> Earlier { get; set; } = [];

    /// <summary>True when <paramref name="value"/> is SplitPlay's (current or not yet replaced in the driver).</summary>
    public bool IsOurs(uint? value) => value is { } v && (v == Ours || Earlier.Contains(v));

    /// <summary>Adds an earlier value of SplitPlay's (kept until a save confirms the new one).</summary>
    public void Remember(uint value)
    {
        if (!Earlier.Contains(value))
        {
            Earlier.Add(value);
        }
    }

    public uint? Previous { get; set; }

    /// <summary>
    /// <see cref="Previous"/> was NVIDIA's predefined value (in one of NVIDIA's game profiles): given back by deleting
    /// the setting, so that the driver's later updates of that value still apply.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreviousPredefined { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NvidiaProfilesState))]
public sealed partial class NvidiaProfilesJsonContext : JsonSerializerContext;
