// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplitPlay.Core.Games;

/// <summary>
/// <c>games.json</c>: the games the service has seen in desks (<see cref="GameLimits"/>) and the frame rate each desk's
/// last Moonlight client asked for. Kept across service restarts, because a limit applies at a game's next start, which
/// may come after the next service start. Thread-safe; every change is written at once (atomically).
/// </summary>
public sealed class GameCatalog
{
    public const string FileName = "games.json";

    /// <summary>More games than this: the ones seen longest ago are forgotten.</summary>
    public const int MaxGames = 200;

    private readonly object _gate = new();
    private readonly string _file;
    private GameCatalogState _state;

    /// <param name="file">The state file; a missing or damaged file starts empty.</param>
    public GameCatalog(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        _file = file;
        _state = Load(file, out var damaged);
        LoadError = damaged;
    }

    /// <summary><c>games.json</c> under a data root.</summary>
    public static string FileIn(string dataRoot) => Path.Combine(dataRoot, FileName);

    /// <summary>Why the file could not be read at start (it then started empty), or null.</summary>
    public string? LoadError { get; }

    /// <summary>A copy of the state.</summary>
    public GameCatalogState Snapshot()
    {
        lock (_gate)
        {
            return Clone(_state);
        }
    }

    /// <summary>
    /// Notes <paramref name="exe"/> (a file name) seen in <paramref name="deskId"/>. Returns true when that is new: a new
    /// game, or a desk the game was not seen in before.
    /// </summary>
    public bool Record(string exe, string? path, string deskId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(exe);
        ArgumentException.ThrowIfNullOrEmpty(deskId);
        lock (_gate)
        {
            var game = Find(exe);
            if (game is null)
            {
                game = new GameRecord { Exe = exe, Path = path, FirstSeen = now, LastSeen = now };
                _state.Games.Add(game);
                if (_state.Games.Count > MaxGames)
                {
                    // Bounded: a desk that runs many programs must not grow the file (and the driver profiles) forever.
                    // The owner's exclusions go last.
                    _state.Games.Remove(_state.Games.Where(g => g != game).OrderBy(g => g.Excluded).ThenBy(g => g.LastSeen).First());
                }
            }
            else if (game.Desks.Contains(deskId, StringComparer.Ordinal))
            {
                // Seen again: only the date moves, and it is written at most once a day.
                if (now - game.LastSeen < TimeSpan.FromDays(1))
                {
                    return false;
                }

                game.LastSeen = now;
                game.Path = path ?? game.Path;
                Save();
                return false;
            }

            game.Desks.Add(deskId);
            game.LastSeen = now;
            game.Path = path ?? game.Path;
            Save();
            return true;
        }
    }

    /// <summary>The frame rate <paramref name="deskId"/>'s Moonlight client asked for; true when it changed.</summary>
    public bool SetClientFps(string deskId, int fps)
    {
        ArgumentException.ThrowIfNullOrEmpty(deskId);
        lock (_gate)
        {
            if (fps <= 0 || (_state.ClientFps.TryGetValue(deskId, out var known) && known == fps))
            {
                return false;
            }

            _state.ClientFps[deskId] = fps;
            Save();
            return true;
        }
    }

    /// <summary>Turns the limit off (or on again) for one game; false when the game is not on record.</summary>
    public bool SetExcluded(string exe, bool excluded)
    {
        lock (_gate)
        {
            var game = Find(exe);
            if (game is null)
            {
                return false;
            }

            if (game.Excluded != excluded)
            {
                game.Excluded = excluded;
                Save();
            }

            return true;
        }
    }

    /// <summary>Forgets one game (its limit is given back); false when it is not on record.</summary>
    public bool Forget(string exe)
    {
        lock (_gate)
        {
            var game = Find(exe);
            if (game is null)
            {
                return false;
            }

            _state.Games.Remove(game);
            Save();
            return true;
        }
    }

    /// <summary>Drops desks that no longer exist (and games seen only in them); true when anything changed.</summary>
    public bool Prune(IReadOnlyCollection<string> deskIds)
    {
        ArgumentNullException.ThrowIfNull(deskIds);
        lock (_gate)
        {
            var changed = false;
            foreach (var game in _state.Games)
            {
                changed |= game.Desks.RemoveAll(d => !deskIds.Contains(d)) > 0;
            }

            changed |= _state.Games.RemoveAll(g => g.Desks.Count == 0) > 0;
            foreach (var desk in _state.ClientFps.Keys.Where(d => !deskIds.Contains(d)).ToList())
            {
                changed |= _state.ClientFps.Remove(desk);
            }

            if (changed)
            {
                Save();
            }

            return changed;
        }
    }

    private GameRecord? Find(string exe) => _state.Games.FirstOrDefault(g => string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase));

    private static GameCatalogState Clone(GameCatalogState state) =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(state, GameCatalogJsonContext.Default.GameCatalogState),
            GameCatalogJsonContext.Default.GameCatalogState)!;

    private static GameCatalogState Load(string file, out string? error)
    {
        error = null;
        if (!File.Exists(file))
        {
            return new GameCatalogState();
        }

        try
        {
            var state = JsonSerializer.Deserialize(File.ReadAllBytes(file), GameCatalogJsonContext.Default.GameCatalogState) ?? new GameCatalogState();
            // Tolerate hand edits: no nulls, empty names, desks or duplicates.
            state.Games = [.. (state.Games ?? []).Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Exe))
                .GroupBy(g => g.Exe, StringComparer.OrdinalIgnoreCase).Select(g => g.First())];
            foreach (var game in state.Games)
            {
                game.Desks = [.. (game.Desks ?? []).Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.Ordinal)];
            }

            state.ClientFps = new Dictionary<string, int>(state.ClientFps ?? [], StringComparer.Ordinal);
            return state;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"{file} could not be read ({ex.Message}); starting with no games";
            return new GameCatalogState();
        }
    }

    /// <summary>Writes the state through to the disk (temp file + rename). Call under <c>_gate</c>.</summary>
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
        var temp = _file + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(_state, GameCatalogJsonContext.Default.GameCatalogState));
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _file, overwrite: true);
    }
}

/// <summary>The content of <c>games.json</c>.</summary>
public sealed class GameCatalogState
{
    public int Version { get; set; } = 1;

    public List<GameRecord> Games { get; set; } = [];

    /// <summary>Desk id → the frame rate its last Moonlight client asked for (the Auto limit).</summary>
    public Dictionary<string, int> ClientFps { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One game the service has seen.</summary>
public sealed class GameRecord
{
    /// <summary>The executable's file name, as the driver profiles list it (<c>game.exe</c>).</summary>
    public string Exe { get; set; } = "";

    /// <summary>Full path when it was last seen (for the owner; the driver only uses the file name).</summary>
    public string? Path { get; set; }

    /// <summary>The desks it was seen in.</summary>
    public List<string> Desks { get; set; } = [];

    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    /// <summary>The owner turned the limit off for this game.</summary>
    public bool Excluded { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GameCatalogState))]
public sealed partial class GameCatalogJsonContext : JsonSerializerContext;
