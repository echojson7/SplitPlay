// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplitPlay.Core.Host;

/// <summary>What <see cref="TermWrapGuard"/> needs to look at besides the registry and the services.</summary>
public interface ITermsrvInspector
{
    /// <summary>File version of <c>%SystemRoot%\System32\termsrv.dll</c> (e.g. <c>10.0.26100.4202</c>), or null.</summary>
    string? TermsrvVersion();

    /// <summary>Something that changes when the file changes (size and write time), or null when it does not exist.</summary>
    string? FileStamp(string path);

    /// <summary>
    /// Runs <c>termwrap-probe.exe</c>: TermWrap's own patch search on a private copy of <c>termsrv.dll</c>, the
    /// running TermService is not touched. Null when the probe cannot be started.
    /// </summary>
    TermWrapProbeRun? RunProbe(string probe, TimeSpan timeout);

    /// <summary>Whether the process hosting <paramref name="service"/> has <paramref name="module"/> loaded; null when unknown.</summary>
    bool? IsModuleLoaded(string service, string module);
}

/// <summary>Exit code and the lines <c>termwrap-probe.exe</c> wrote (TermWrap's debug messages and its own).</summary>
public sealed record TermWrapProbeRun(int ExitCode, IReadOnlyList<string> Lines);

/// <summary>What the probe says about the installed <c>termsrv.dll</c>.</summary>
public enum TermWrapVerdict
{
    /// <summary>No probe (older install) or it could not run: nothing is claimed.</summary>
    Unknown,

    /// <summary>TermWrap finds everything it needs for concurrent sessions.</summary>
    Supported,

    /// <summary>TermWrap cannot patch this build: a second session would end the first.</summary>
    Unsupported,
}

/// <summary>State of the TermWrap check.</summary>
public enum TermWrapState
{
    /// <summary>TermWrap is in place and loaded.</summary>
    Ok,

    /// <summary>The host is not prepared with TermWrap (skipped, Windows Server, CI): nothing is checked.</summary>
    NotManaged,

    /// <summary>The right <c>ServiceDll</c> is set, but the running TermService still has the old DLL: reboot.</summary>
    RebootRequired,

    /// <summary>This <c>termsrv.dll</c> build cannot be patched by the shipped TermWrap.</summary>
    Unsupported,

    /// <summary>The TermWrap files are gone (antivirus, manual delete): reinstall.</summary>
    Missing,

    /// <summary>The <c>ServiceDll</c> could not be written back.</summary>
    RepairFailed,
}

/// <summary>Result of <see cref="TermWrapGuard.Check"/>.</summary>
/// <param name="Repaired">This check wrote the TermWrap <c>ServiceDll</c> back (Windows had reset it).</param>
/// <param name="TermsrvVersion">The installed <c>termsrv.dll</c> version, when known.</param>
/// <param name="Detail">For the log: what the probe reported, the missing file, or the error.</param>
public sealed record TermWrapHealth(TermWrapState State, bool Repaired = false, string? TermsrvVersion = null, string? Detail = null)
{
    public static readonly TermWrapHealth NotManaged = new(TermWrapState.NotManaged);

    /// <summary>The line a desk shows in <c>lastError</c> when it cannot start because of TermWrap, or null.</summary>
    public string? Problem => State switch
    {
        TermWrapState.RebootRequired => Repaired
            ? "Windows Update reset TermWrap; SplitPlay put it back. Restart the PC to start desks"
            : "Restart the PC to start desks: TermWrap loads at boot",
        TermWrapState.Unsupported =>
            $"This Windows update (termsrv.dll {TermsrvVersion ?? "?"}) is not supported by TermWrap yet; desks cannot start until SplitPlay is updated",
        TermWrapState.Missing => "TermWrap files are missing; reinstall SplitPlay",
        TermWrapState.RepairFailed => $"Cannot restore TermWrap after a Windows update: {Detail}",
        _ => null,
    };
}

/// <summary>
/// Keeps TermWrap working across Windows updates. A feature update writes TermService's <c>ServiceDll</c> back to
/// <c>termsrv.dll</c>, and a cumulative update may ship a <c>termsrv.dll</c> TermWrap cannot patch; either way desks
/// would fail with an obscure logon error. The check:
/// <list type="number">
/// <item>writes the recorded TermWrap <c>ServiceDll</c> back when it was changed (<see cref="HostTuner.RepairTermWrap"/>);</item>
/// <item>runs <c>termwrap-probe.exe</c> once per <c>termsrv.dll</c> build (and probe build) and keeps its verdict in
/// <c>termwrap-check.json</c>;</item>
/// <item>checks that the running TermService has <c>TermWrap.dll</c> loaded.</item>
/// </list>
/// Cheap when nothing changed: two registry reads, a file version and a module list.
/// </summary>
public sealed class TermWrapGuard
{
    public const string CacheFileName = "termwrap-check.json";
    public const string ProbeExe = "termwrap-probe.exe";

    /// <summary>Probe exit codes (native/termwrap/probe.cpp).</summary>
    public const int ProbeRan = 0;
    public const int ProbeCannotLoad = 1;
    public const int ProbeCrashed = 3;

    /// <summary>
    /// TermWrap's messages for the patches concurrent sessions need: <c>CDefPolicy::Query</c> (the session limit) and
    /// <c>CSLQuery::Initialize</c> (the licensing flags, <c>bFUSEnabled</c>/<c>bAppServerAllowed</c>/...). The others
    /// (single session per user, licence type, PnP and non-RDP stack) do not stop desks and are only logged.
    /// </summary>
    private static readonly string[] CriticalPrefixes =
    [
        "DefPolicyPatch",
        "CDefPolicy_Query not found",
        "CSLQuery_Initialize not found",
        "bInitialized not found",
    ];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private readonly HostTuner _tuner;
    private readonly IHostServices _services;
    private readonly ITermsrvInspector _inspector;
    private readonly string _cacheFile;
    private readonly TimeProvider _time;

    public TermWrapGuard(HostTuner tuner, IHostServices services, ITermsrvInspector inspector, string cacheFile, TimeProvider? time = null)
    {
        _tuner = tuner;
        _services = services;
        _inspector = inspector;
        _cacheFile = cacheFile;
        _time = time ?? TimeProvider.System;
    }

    /// <summary><c>termwrap-check.json</c> under a data root.</summary>
    public static string CacheFileIn(string dataRoot) => Path.Combine(dataRoot, CacheFileName);

    /// <summary>Reads the probe's verdict from its exit code and lines.</summary>
    public static (TermWrapVerdict Verdict, string? Detail) Classify(TermWrapProbeRun? run)
    {
        if (run is null || run.ExitCode == ProbeCannotLoad)
        {
            return (TermWrapVerdict.Unknown, run is null ? "probe not started" : string.Join("; ", run.Lines));
        }

        if (run.ExitCode == ProbeCrashed)
        {
            // TermWrap would crash TermService the same way.
            return (TermWrapVerdict.Unsupported, "TermWrap's patch search crashed on this termsrv.dll");
        }

        if (run.ExitCode != ProbeRan)
        {
            return (TermWrapVerdict.Unknown, $"probe exit code {run.ExitCode}");
        }

        var messages = run.Lines.Where(l => l.Length > 0 && !l.StartsWith("probe:", StringComparison.Ordinal)).ToList();
        var critical = messages.Where(l => CriticalPrefixes.Any(p => l.StartsWith(p, StringComparison.Ordinal))).ToList();
        return critical.Count > 0
            ? (TermWrapVerdict.Unsupported, string.Join("; ", critical))
            : (TermWrapVerdict.Supported, messages.Count > 0 ? string.Join("; ", messages) : null);
    }

    /// <param name="termWrapDirectory">The installed <c>{app}\termwrap</c> (where the probe is).</param>
    public TermWrapHealth Check(string termWrapDirectory)
    {
        TermWrapRepair repair;
        try
        {
            repair = _tuner.RepairTermWrap();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or HostTuningException)
        {
            return new TermWrapHealth(TermWrapState.RepairFailed, Detail: ex.Message);
        }

        if (!repair.Managed)
        {
            return TermWrapHealth.NotManaged;
        }

        var repaired = repair.Changes.Count > 0;
        var version = _inspector.TermsrvVersion();
        if (repair.MissingFiles.Count > 0)
        {
            return new TermWrapHealth(TermWrapState.Missing, repaired, version, string.Join(", ", repair.MissingFiles));
        }

        var (verdict, detail) = Verdict(termWrapDirectory, version);
        if (verdict == TermWrapVerdict.Unsupported)
        {
            return new TermWrapHealth(TermWrapState.Unsupported, repaired, version, detail);
        }

        if (repair.RebootRequired)
        {
            return new TermWrapHealth(TermWrapState.RebootRequired, repaired, version, detail);
        }

        // ServiceDll is right and no change is pending, yet the running TermService may still have termsrv.dll itself
        // (the value was changed by someone else while it ran, or the state file was reset).
        if (_services.IsRunning(HostTuner.TermService) && _inspector.IsModuleLoaded(HostTuner.TermService, HostTuner.TermWrapDll) == false)
        {
            return new TermWrapHealth(TermWrapState.RebootRequired, repaired, version, "TermWrap.dll is not loaded in TermService");
        }

        return new TermWrapHealth(TermWrapState.Ok, repaired, version, detail);
    }

    /// <summary>The probe's verdict for this termsrv.dll and probe build, from the cache or a new run.</summary>
    private (TermWrapVerdict Verdict, string? Detail) Verdict(string termWrapDirectory, string? version)
    {
        var probe = Path.Combine(termWrapDirectory, ProbeExe);
        var probeStamp = _inspector.FileStamp(probe);
        if (version is null || probeStamp is null)
        {
            return (TermWrapVerdict.Unknown, version is null ? "termsrv.dll version unknown" : $"{ProbeExe} not installed");
        }

        var key = $"termsrv {version}; probe {probeStamp}";
        var cache = LoadCache();
        if (cache is not null && cache.Key == key && cache.Verdict != TermWrapVerdict.Unknown)
        {
            return (cache.Verdict, cache.Detail);
        }

        var (verdict, detail) = Classify(_inspector.RunProbe(probe, ProbeTimeout));
        if (verdict != TermWrapVerdict.Unknown)
        {
            // Unknown is not kept: the next check tries again.
            SaveCache(new TermWrapCheckCache { Key = key, TermsrvVersion = version, Verdict = verdict, Detail = detail, CheckedAt = _time.GetUtcNow() });
        }

        return (verdict, detail);
    }

    private TermWrapCheckCache? LoadCache()
    {
        try
        {
            return File.Exists(_cacheFile)
                ? JsonSerializer.Deserialize(File.ReadAllText(_cacheFile, Encoding.UTF8), TermWrapJsonContext.Default.TermWrapCheckCache)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // a cache: run the probe again
        }
    }

    private void SaveCache(TermWrapCheckCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cacheFile))!);
            var temp = _cacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache, TermWrapJsonContext.Default.TermWrapCheckCache), new UTF8Encoding(false));
            File.Move(temp, _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only costs a probe run next time.
        }
    }
}

/// <summary><c>termwrap-check.json</c>: the probe's last verdict.</summary>
public sealed class TermWrapCheckCache
{
    /// <summary>termsrv.dll version and probe file stamp the verdict is for.</summary>
    public string Key { get; set; } = "";

    public string? TermsrvVersion { get; set; }

    public TermWrapVerdict Verdict { get; set; }

    public string? Detail { get; set; }

    public DateTimeOffset CheckedAt { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TermWrapCheckCache))]
public sealed partial class TermWrapJsonContext : JsonSerializerContext;
