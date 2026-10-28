// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SplitPlay.Core.Host;

namespace SplitPlay.Core.Steam;

/// <summary>
/// One user's registry hive (<c>HKU\&lt;SID&gt;</c>, loaded at logon or mounted from <c>NTUSER.DAT</c>). Keys are
/// relative to the hive root, e.g. <c>Software\Microsoft\Windows\CurrentVersion\Run</c>; the name <c>""</c> is the
/// key's default value.
/// </summary>
public interface IUserHive : IHostRegistry
{
    bool KeyExists(string key);

    /// <summary>Deletes the key if it has no values and no subkeys.</summary>
    void DeleteKeyIfEmpty(string key);

    /// <summary>
    /// False when <c>Software\Classes</c> cannot be reached (the profile has no <c>UsrClass.dat</c> yet): values under it
    /// are then not written.
    /// </summary>
    bool ClassesAvailable => true;
}

/// <summary>
/// A user's two hives seen as one: keys under <c>Software\Classes</c> go to the classes hive (<c>UsrClass.dat</c>,
/// <c>HKU\&lt;SID&gt;_Classes</c>) without that prefix, everything else to <c>NTUSER.DAT</c>. This is what Windows
/// does with a symbolic link at logon; a hive mounted from disk has no such link.
/// </summary>
public sealed class ClassesRoutedHive(IUserHive main, IUserHive? classes) : IUserHive
{
    private const string Prefix = @"Software\Classes";

    public bool ClassesAvailable => classes is not null;

    public RegValue? Get(string key, string name) => Route(key, out var k) is { } hive ? hive.Get(k, name) : null;

    public void Set(string key, string name, RegValue value) =>
        (Route(key, out var k) ?? throw new InvalidOperationException($"{key}: the classes hive is not available")).Set(k, name, value);

    public void Delete(string key, string name) => Route(key, out var k)?.Delete(k, name);

    public bool KeyExists(string key) => Route(key, out var k) is { } hive && hive.KeyExists(k);

    public void DeleteKeyIfEmpty(string key) => Route(key, out var k)?.DeleteKeyIfEmpty(k);

    private IUserHive? Route(string key, out string routed)
    {
        if (key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && (key.Length == Prefix.Length || key[Prefix.Length] == '\\'))
        {
            routed = key.Length == Prefix.Length ? "" : key[(Prefix.Length + 1)..];
            return classes;
        }

        routed = key;
        return main;
    }
}

/// <summary>What the machine-wide Steam install offers (read from HKLM; nulls when Steam is not installed).</summary>
/// <param name="HandlerCommand">Default value of <c>HKLM\SOFTWARE\Classes\steam\Shell\Open\Command</c>.</param>
/// <param name="HandlerName">Default value of <c>HKLM\SOFTWARE\Classes\steam</c> (<c>URL:steam protocol</c>).</param>
public sealed record SteamMachineInfo(string? HandlerCommand, string? HandlerName = null)
{
    public static readonly SteamMachineInfo None = new(HandlerCommand: null);
}

/// <summary>What <see cref="SteamIsolation.Apply"/> did.</summary>
/// <param name="EnvironmentWritten">
/// <c>steam_master_ipc_name_override</c> was (re)written: a session that is already logged on only sees it after the
/// next logon.
/// </param>
public sealed record SteamIsolationResult(bool EnvironmentWritten, IReadOnlyList<string> Changes);

/// <summary>
/// Per-desk Steam isolation (Steam isolation research, 2026-10-28): the desk user's Steam client and the games it starts use
/// their own IPC name <c>SplitPlay_&lt;desk&gt;</c> instead of the default <c>Steam3Master</c>, so the console user's
/// Steam keeps running. Only the desk user's hive is changed, never the console user's and never machine-wide:
/// <list type="bullet">
/// <item><c>Environment\steam_master_ipc_name_override</c> (REG_SZ) for Steamworks games started outside Steam;</item>
/// <item><c>Software\Microsoft\Windows\CurrentVersion\Run\Steam</c>, when Steam's autostart is on, gets
/// <c>-master_ipc_name_override &lt;name&gt;</c>;</item>
/// <item>a per-user <c>steam://</c> handler (<c>Software\Classes\steam</c>) mirroring the machine one plus the flag.</item>
/// </list>
/// Every value SplitPlay writes is recorded with what was there before (<c>steam-isolation.json</c> in the desk
/// directory) so that <see cref="Revert"/> restores it. A value someone changed since is left alone.
/// </summary>
public sealed partial class SteamIsolation
{
    public const string StateFileName = "steam-isolation.json";
    public const string Flag = "-master_ipc_name_override";
    public const string EnvironmentKey = "Environment";
    public const string EnvironmentName = "steam_master_ipc_name_override";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunName = "Steam";
    public const string ClassKey = @"Software\Classes\steam";
    public const string CommandKey = @"Software\Classes\steam\Shell\Open\Command";

    private const string DefaultHandlerName = "URL:steam protocol";

    /// <summary>The keys of the per-user handler, outermost first.</summary>
    private static readonly string[] HandlerKeys = [ClassKey, ClassKey + @"\Shell", ClassKey + @"\Shell\Open", CommandKey];

    private readonly string _stateFile;

    public SteamIsolation(string stateFile)
    {
        _stateFile = stateFile;
    }

    /// <summary>The desk's IPC name: <c>SplitPlay_</c> + the desk id with everything but [A-Za-z0-9] as <c>_</c>.</summary>
    public static string IpcName(string deskId)
    {
        ArgumentNullException.ThrowIfNull(deskId);
        var name = new StringBuilder("SplitPlay_", 10 + deskId.Length);
        foreach (var c in deskId)
        {
            name.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        return name.ToString();
    }

    /// <summary>True if the command line starts <c>steam.exe</c> (quoted or not, any folder).</summary>
    public static bool IsSteamCommand(string? commandLine) =>
        commandLine is not null && Split(commandLine) is { } parts && IsSteamExe(parts.Exe);

    /// <summary>
    /// <paramref name="commandLine"/> with <c>-master_ipc_name_override &lt;ipcName&gt;</c> right after the executable
    /// (an existing override is replaced). Null if it does not start with an executable.
    /// </summary>
    public static string? WithFlag(string commandLine, string ipcName)
    {
        if (Split(commandLine) is not { } parts)
        {
            return null;
        }

        var rest = StripFlag(parts.Arguments);
        return $"{parts.ExeToken} {Flag} {ipcName}{(rest.Length == 0 || char.IsWhiteSpace(rest[0]) ? "" : " ")}{rest}";
    }

    /// <summary><paramref name="commandLine"/> without any <c>-master_ipc_name_override</c>.</summary>
    public static string WithoutFlag(string commandLine) =>
        Split(commandLine) is { } parts ? parts.ExeToken + StripFlag(parts.Arguments) : commandLine;

    /// <summary>The desk user's SID the recorded values belong to, or null when nothing is recorded.</summary>
    public string? RecordedSid() => LoadState()?.Sid;

    /// <summary>
    /// Writes the desk's values into <paramref name="hive"/> (the hive of <paramref name="sid"/>) and records what was
    /// there before. Idempotent; a value Steam or the user changed since is rewritten and its new previous value recorded.
    /// </summary>
    public SteamIsolationResult Apply(IUserHive hive, string sid, string deskId, SteamMachineInfo machine)
    {
        ArgumentNullException.ThrowIfNull(hive);
        ArgumentNullException.ThrowIfNull(machine);
        var state = LoadState();
        if (state is not null && !string.Equals(state.Sid, sid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{_stateFile} belongs to {state.Sid}; revert it before applying to {sid}");
        }

        state ??= new SteamIsolationState { Sid = sid };
        var ipc = IpcName(deskId);
        state.IpcName = ipc;
        var changes = new List<string>();

        var environmentWritten = Ensure(hive, state, "environment", EnvironmentKey, EnvironmentName, new RegValue(RegKind.String, ipc), changes);

        // Steam's own "run at sign-in": only rewritten when it is on (Steam writes it again when the box is ticked).
        var run = hive.Get(RunKey, RunName);
        if (run is { Kind: RegKind.String or RegKind.ExpandString } && IsSteamCommand(run.Data))
        {
            var previous = new RegValue(run.Kind, WithoutFlag(run.Data));
            var desired = new RegValue(run.Kind, WithFlag(previous.Data, ipc)!);
            var record = Record(state, "run", RunKey, RunName);
            record.Existed = true;
            record.Previous = previous;
            record.Desired = desired;
            if (!Same(run, desired))
            {
                hive.Set(RunKey, RunName, desired);
                changes.Add($"Run\\Steam: {run} -> {desired}");
            }
        }
        else
        {
            state.Items.RemoveAll(r => r.Id == "run");
        }

        if (hive.ClassesAvailable && machine.HandlerCommand is { } handler && IsSteamCommand(handler) && WithFlag(handler, ipc) is { } command)
        {
            foreach (var key in HandlerKeys)
            {
                if (!hive.KeyExists(key) && !state.CreatedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    state.CreatedKeys.Add(key);
                }
            }

            var name = string.IsNullOrWhiteSpace(machine.HandlerName) ? DefaultHandlerName : machine.HandlerName;
            Ensure(hive, state, "handler-name", ClassKey, "", new RegValue(RegKind.String, name), changes);
            Ensure(hive, state, "handler-protocol", ClassKey, "URL Protocol", new RegValue(RegKind.String, ""), changes);
            Ensure(hive, state, "handler-command", CommandKey, "", new RegValue(RegKind.String, command), changes);
        }

        SaveState(state);
        return new SteamIsolationResult(environmentWritten, changes);
    }

    /// <summary>
    /// Restores every recorded value that still holds what SplitPlay wrote, deletes the handler keys SplitPlay created
    /// (when empty) and the state file. Returns what changed.
    /// </summary>
    public IReadOnlyList<string> Revert(IUserHive hive)
    {
        ArgumentNullException.ThrowIfNull(hive);
        var state = LoadState();
        var changes = new List<string>();
        if (state is null)
        {
            return changes;
        }

        foreach (var record in Enumerable.Reverse(state.Items))
        {
            var current = hive.Get(record.Key, record.Name);
            if (record.Desired is null || !Same(current, record.Desired))
            {
                changes.Add($"{Describe(record)}: left as is ({current?.ToString() ?? "missing"}, changed since)");
                continue;
            }

            if (record.Existed && record.Previous is not null)
            {
                hive.Set(record.Key, record.Name, record.Previous);
                changes.Add($"{Describe(record)}: {current} -> {record.Previous}");
            }
            else
            {
                hive.Delete(record.Key, record.Name);
                changes.Add($"{Describe(record)}: {current} -> (deleted)");
            }
        }

        foreach (var key in Enumerable.Reverse(HandlerKeys))
        {
            if (state.CreatedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                hive.DeleteKeyIfEmpty(key);
            }
        }

        DeleteState();
        return changes;
    }

    /// <summary>Removes the state file without touching any hive (the account and its profile are gone).</summary>
    public void Forget() => DeleteState();

    private static bool Ensure(IUserHive hive, SteamIsolationState state, string id, string key, string name, RegValue desired, List<string> changes)
    {
        var current = hive.Get(key, name);
        var record = state.Items.FirstOrDefault(r => r.Id == id);
        if (Same(current, desired))
        {
            if (record is null)
            {
                // Already there without a record (state file lost): it is SplitPlay's, revert deletes it.
                record = Record(state, id, key, name);
                record.Existed = false;
            }

            record.Desired = desired;
            return false;
        }

        record ??= Record(state, id, key, name);
        record.Existed = current is not null;
        record.Previous = current;
        record.Desired = desired;
        hive.Set(key, name, desired);
        changes.Add($"{Describe(record)}: {current?.ToString() ?? "(missing)"} -> {desired}");
        return true;
    }

    private static SteamIsolationRecord Record(SteamIsolationState state, string id, string key, string name)
    {
        var record = state.Items.FirstOrDefault(r => r.Id == id);
        if (record is null)
        {
            record = new SteamIsolationRecord { Id = id, Key = key, Name = name };
            state.Items.Add(record);
        }

        return record;
    }

    /// <summary>Same kind and same data; case matters (unlike <see cref="RegValue.SameAs"/>, which is meant for paths).</summary>
    private static bool Same(RegValue? a, RegValue? b) =>
        a is not null && b is not null && a.Kind == b.Kind && string.Equals(a.Data, b.Data, StringComparison.Ordinal);

    private static string Describe(SteamIsolationRecord record) =>
        $"{record.Key}\\{(record.Name.Length == 0 ? "(default)" : record.Name)}";

    private static bool IsSteamExe(string exe)
    {
        // Backslash and slash both separate folders here, also when this runs on Linux (tests).
        var file = exe[(exe.LastIndexOfAny(['\\', '/']) + 1)..];
        return string.Equals(file, "steam.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static (string ExeToken, string Exe, string Arguments)? Split(string commandLine)
    {
        var s = commandLine.TrimStart();
        if (s.Length == 0)
        {
            return null;
        }

        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end <= 1 ? null : (s[..(end + 1)], s[1..end], s[(end + 1)..]);
        }

        var space = s.IndexOfAny([' ', '\t']);
        return space < 0 ? (s, s, "") : (s[..space], s[..space], s[space..]);
    }

    private static string StripFlag(string arguments) => FlagPattern().Replace(arguments, "");

    [GeneratedRegex("""\s*-master_ipc_name_override\s+("[^"]*"|\S+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FlagPattern();

    private SteamIsolationState? LoadState()
    {
        if (!File.Exists(_stateFile))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(_stateFile, Encoding.UTF8), SteamIsolationJsonContext.Default.SteamIsolationState);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{_stateFile} is corrupt: {ex.Message}", ex);
        }
    }

    private void SaveState(SteamIsolationState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_stateFile))!);
        var temp = _stateFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, SteamIsolationJsonContext.Default.SteamIsolationState), new UTF8Encoding(false));
        File.Move(temp, _stateFile, overwrite: true);
    }

    private void DeleteState()
    {
        if (File.Exists(_stateFile))
        {
            File.Delete(_stateFile);
        }
    }
}

/// <summary><c>steam-isolation.json</c> in the desk directory.</summary>
public sealed class SteamIsolationState
{
    public int Version { get; set; } = 1;

    /// <summary>The desk user whose hive holds the values.</summary>
    public string Sid { get; set; } = "";

    public string IpcName { get; set; } = "";

    public List<SteamIsolationRecord> Items { get; set; } = [];

    /// <summary>Handler keys that did not exist before (deleted on revert when empty).</summary>
    public List<string> CreatedKeys { get; set; } = [];
}

/// <summary>One value SplitPlay wrote, with what was there before.</summary>
public sealed class SteamIsolationRecord
{
    public string Id { get; set; } = "";

    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>False if the value did not exist; revert then deletes it.</summary>
    public bool Existed { get; set; }

    public RegValue? Previous { get; set; }

    public RegValue? Desired { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SteamIsolationState))]
public sealed partial class SteamIsolationJsonContext : JsonSerializerContext;

/// <summary>An in-memory <see cref="IUserHive"/> (tests and the fake platform). Keys and names are case-insensitive.</summary>
public sealed class MemoryUserHive : IUserHive
{
    private readonly Dictionary<(string Key, string Name), RegValue> _values = new(KeyComparer.Instance);
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Every value, for assertions.</summary>
    public IReadOnlyDictionary<(string Key, string Name), RegValue> Values
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<(string Key, string Name), RegValue>(_values, KeyComparer.Instance);
            }
        }
    }

    public RegValue? Get(string key, string name)
    {
        lock (_gate)
        {
            return _values.TryGetValue((key, name), out var value) ? value : null;
        }
    }

    public void Set(string key, string name, RegValue value)
    {
        lock (_gate)
        {
            CreateKey(key);
            _values[(key, name)] = value;
        }
    }

    public void Delete(string key, string name)
    {
        lock (_gate)
        {
            _values.Remove((key, name));
        }
    }

    public bool KeyExists(string key)
    {
        lock (_gate)
        {
            return _keys.Contains(key);
        }
    }

    public void CreateKey(string key)
    {
        lock (_gate)
        {
            for (var k = key; k.Length > 0; k = k.LastIndexOf('\\') is var i and > 0 ? k[..i] : "")
            {
                _keys.Add(k);
            }
        }
    }

    public void DeleteKeyIfEmpty(string key)
    {
        lock (_gate)
        {
            var prefix = key + "\\";
            if (_values.Keys.Any(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))
                || _keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _keys.Remove(key);
        }
    }

    private sealed class KeyComparer : IEqualityComparer<(string Key, string Name)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Key, string Name) x, (string Key, string Name) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Key, y.Key) && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((string Key, string Name) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
