// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Host;

/// <summary>Registry value types used by the host tuning.</summary>
public enum RegKind
{
    DWord,
    String,
    ExpandString,
}

/// <summary>A registry value. DWORDs are kept as their unsigned decimal text so that the state file is readable.</summary>
public sealed record RegValue(RegKind Kind, string Data)
{
    public static RegValue DWord(uint value) => new(RegKind.DWord, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static RegValue Expand(string value) => new(RegKind.ExpandString, value);

    /// <summary>Same kind class (DWORD vs. string) and same data; strings compare case-insensitively (paths).</summary>
    public bool SameAs(RegValue? other) =>
        other is not null
        && (Kind == RegKind.DWord) == (other.Kind == RegKind.DWord)
        && string.Equals(Data, other.Data, Kind == RegKind.DWord ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Kind == RegKind.DWord ? Data : $"\"{Data}\"";
}

/// <summary>
/// The part of <c>HKEY_LOCAL_MACHINE</c> the host tuning touches (64-bit view). Keys are relative to HKLM, for example
/// <c>SYSTEM\CurrentControlSet\Control\Terminal Server</c>.
/// </summary>
public interface IHostRegistry
{
    /// <summary>Reads a value without expanding environment variables; null if the key or value is missing.</summary>
    RegValue? Get(string key, string name);

    /// <summary>Writes a value, creating the key if needed.</summary>
    void Set(string key, string name, RegValue value);

    /// <summary>Deletes a value if it exists.</summary>
    void Delete(string key, string name);
}

/// <summary>Windows services as far as the host tuning needs them.</summary>
public interface IHostServices
{
    /// <summary>Start type (2 automatic, 3 manual, 4 disabled), or null if the service does not exist.</summary>
    int? GetStartType(string name);

    void SetStartType(string name, int startType);

    bool IsRunning(string name);

    /// <summary>Starts the service and waits briefly; failures are reported as exceptions.</summary>
    void Start(string name);

    /// <summary>When the process hosting the running service started, or null if it is not running or unknown.</summary>
    DateTimeOffset? ProcessStartTime(string name);
}

/// <summary>Power plans and per-application power throttling as far as the host tuning needs them.</summary>
public interface IHostPower
{
    /// <summary>The active power plan (scheme GUID), or null if it cannot be read.</summary>
    Guid? ActiveScheme();

    /// <summary>The power plans that exist on this system.</summary>
    IReadOnlyList<Guid> Schemes();

    /// <summary>Makes a plan the active one; failures are reported as exceptions.</summary>
    void SetActiveScheme(Guid scheme);

    /// <summary>The plan's name as Windows shows it, or null.</summary>
    string? SchemeName(Guid scheme);

    /// <summary>
    /// <c>powercfg /powerthrottling disable /path &lt;exe&gt;</c> (<paramref name="exempt"/>) or <c>reset</c>: the
    /// executable is never power throttled, or Windows decides again. Failures are reported as exceptions.
    /// </summary>
    void SetPowerThrottling(string executable, bool exempt);

    /// <summary>
    /// Whether <c>powercfg /powerthrottling list</c> lists the executable (as exempt); null when that cannot be told.
    /// </summary>
    bool? IsPowerThrottlingDisabled(string executable);
}

/// <summary>A preparation step could not be done (missing TermWrap files, access denied, …).</summary>
public sealed class HostTuningException(string message, Exception? inner = null) : Exception(message, inner);
