// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplitPlay.Core.Host;

/// <summary>Inputs of <see cref="HostTuner.Prepare"/> and <see cref="HostTuner.Status"/>.</summary>
public sealed record HostTuningOptions
{
    /// <summary>Folder with TermWrap.dll, UmWrap.dll and their dependencies (installed: <c>{app}\termwrap</c>).</summary>
    public required string TermWrapDirectory { get; init; }

    /// <summary>Leave <c>ServiceDll</c> alone (CI runners, Windows Server, or when TermWrap is managed by hand).</summary>
    public bool SkipTermWrap { get; init; }

    /// <summary><c>host.composeIntervalMs</c>, written to <c>DWMFRAMEINTERVAL</c>.</summary>
    public int ComposeIntervalMs { get; init; } = 4;
}

/// <summary>One registry value the host tuning manages.</summary>
/// <param name="Id">Stable short name used in the state file and in <c>status</c> output.</param>
public sealed record HostTuningItem(string Id, string Key, string Name, RegValue Desired, bool TermWrap = false);

/// <summary>State of one item as reported by <c>status</c>.</summary>
public sealed record HostItemStatus(string Id, string Key, string Name, RegValue? Current, RegValue? Desired, string State)
{
    public const string Ok = "ok";
    public const string Differs = "differs";
    public const string Skipped = "skipped";
    public const string NotApplicable = "n/a";

    public bool IsOk => State is Ok or Skipped or NotApplicable;
}

/// <summary>Result of <see cref="HostTuner.Status"/>.</summary>
public sealed record HostStatus(
    bool Prepared,
    bool RebootRequired,
    bool TermWrapSkipped,
    string Edition,
    IReadOnlyList<HostItemStatus> Items,
    IReadOnlyList<string> Notes)
{
    /// <summary>Ready for desks: prepared and no reboot pending.</summary>
    public bool Ready => Prepared && !RebootRequired;

    /// <summary>The message a desk shows in <c>lastError</c> when the host is not ready, or null when it is.</summary>
    public string? Problem => Ready ? null : RebootRequired
        ? "RDP host not prepared: reboot required (TermWrap was installed or removed while Remote Desktop Services were running)"
        : "RDP host not prepared: run setup again (or 'splitplay host prepare')"
          + (Items.FirstOrDefault(i => !i.IsOk) is { } bad ? $"; {bad.Id} is {bad.Current?.ToString() ?? "missing"}, expected {bad.Desired}" : "");
}

/// <summary>Result of prepare or revert.</summary>
public sealed record HostTuningResult(IReadOnlyList<string> Changes, bool RebootRequired, IReadOnlyList<string> Notes);

/// <summary>
/// RDP host preparation (brief "RDP host preparation"): sets the Remote Desktop values SplitPlay needs, swaps the
/// <c>ServiceDll</c> of TermService (and UmRdpService on Home and Server editions) to TermWrap the way TermWrap's
/// <c>Install_termwrap_*.reg</c> do, and records every previous value in <c>host-tuning.json</c> so that
/// <see cref="Revert"/> restores them exactly. Reversible and idempotent: preparing twice keeps the values recorded
/// by the first run.
/// </summary>
public sealed class HostTuner
{
    public const string StateFileName = "host-tuning.json";

    public const string TerminalServerKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    public const string WinStationsKey = TerminalServerKey + @"\WinStations";
    public const string RdpTcpKey = WinStationsKey + @"\RDP-Tcp";
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    public const string TermServiceParametersKey = @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";
    public const string UmRdpServiceParametersKey = @"SYSTEM\CurrentControlSet\Services\UmRdpService\Parameters";
    public const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public const string TermService = "TermService";
    public const string UmRdpService = "UmRdpService";
    public const string TermWrapDll = "TermWrap.dll";
    public const string UmWrapDll = "UmWrap.dll";

    /// <summary>The values TermWrap's <c>Revert_to_default.reg</c> writes (used when nothing was recorded).</summary>
    public const string DefaultTermServiceDll = @"%SystemRoot%\System32\termsrv.dll";
    public const string DefaultUmRdpServiceDll = @"%SystemRoot%\System32\umrdp.dll";

    private const int StartAutomatic = 2;

    private readonly IHostRegistry _registry;
    private readonly IHostServices _services;
    private readonly string _stateFile;
    private readonly TimeProvider _time;

    public HostTuner(IHostRegistry registry, IHostServices services, string stateFile, TimeProvider? time = null)
    {
        _registry = registry;
        _services = services;
        _stateFile = stateFile;
        _time = time ?? TimeProvider.System;
    }

    /// <summary><c>host-tuning.json</c> under a data root.</summary>
    public static string StateFileIn(string dataRoot) => Path.Combine(dataRoot, StateFileName);

    // ------------------------------------------------------------------ edition

    /// <summary>EditionID, e.g. <c>Professional</c>, <c>Core</c> (Home), <c>ServerDatacenter</c>.</summary>
    public string Edition => _registry.Get(CurrentVersionKey, "EditionID")?.Data ?? "unknown";

    /// <summary>Home and Server editions also need UmWrap (TermWrap README).</summary>
    public bool NeedsUmWrap
    {
        get
        {
            var edition = Edition;
            var installation = _registry.Get(CurrentVersionKey, "InstallationType")?.Data ?? "";
            return edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase)
                   || edition.StartsWith("Server", StringComparison.OrdinalIgnoreCase)
                   || installation.StartsWith("Server", StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------ plan

    /// <summary>Everything prepare sets, in order.</summary>
    public IReadOnlyList<HostTuningItem> Plan(HostTuningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var items = new List<HostTuningItem>
        {
            new("fDenyTSConnections", TerminalServerKey, "fDenyTSConnections", RegValue.DWord(0)),
            new("fSingleSessionPerUser", TerminalServerKey, "fSingleSessionPerUser", RegValue.DWord(0)),
            new("UserAuthentication", RdpTcpKey, "UserAuthentication", RegValue.DWord(1)),
            // 32 bpp desk sessions: Windows' default RDP-Tcp caps them at 16 bpp, and fInheritColorDepth alone is not
            // enough (measured in native CI). Applies to sessions started after TermService restarts.
            new("fInheritColorDepth", RdpTcpKey, "fInheritColorDepth", RegValue.DWord(1)),
            new("ColorDepth", RdpTcpKey, "ColorDepth", RegValue.DWord(5)),
            new("bEnumerateHWBeforeSW", PolicyKey, "bEnumerateHWBeforeSW", RegValue.DWord(1)),
            new("DWMFRAMEINTERVAL", WinStationsKey, "DWMFRAMEINTERVAL", RegValue.DWord((uint)Math.Clamp(options.ComposeIntervalMs, 1, 100))),
            new("MaxIdleTime", PolicyKey, "MaxIdleTime", RegValue.DWord(0)),
            new("MaxDisconnectionTime", PolicyKey, "MaxDisconnectionTime", RegValue.DWord(0)),
        };

        if (!options.SkipTermWrap)
        {
            var dir = Path.GetFullPath(options.TermWrapDirectory);
            items.Add(new("TermService.ServiceDll", TermServiceParametersKey, "ServiceDll", RegValue.Expand(Path.Combine(dir, TermWrapDll)), TermWrap: true));
            if (NeedsUmWrap && File.Exists(Path.Combine(dir, UmWrapDll)))
            {
                items.Add(new("UmRdpService.ServiceDll", UmRdpServiceParametersKey, "ServiceDll", RegValue.Expand(Path.Combine(dir, UmWrapDll)), TermWrap: true));
            }
        }

        return items;
    }

    // ------------------------------------------------------------------ prepare

    public HostTuningResult Prepare(HostTuningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var changes = new List<string>();
        var notes = new List<string>();
        if (!options.SkipTermWrap)
        {
            var termWrap = Path.Combine(Path.GetFullPath(options.TermWrapDirectory), TermWrapDll);
            if (!File.Exists(termWrap))
            {
                throw new HostTuningException($"{termWrap} not found: reinstall SplitPlay with the TermWrap component, or pass --skip-termwrap");
            }

            if (NeedsUmWrap && !File.Exists(Path.Combine(Path.GetDirectoryName(termWrap)!, UmWrapDll)))
            {
                notes.Add($"{Edition} edition: {UmWrapDll} is missing, UmRdpService is left unchanged");
            }
        }

        var state = LoadState();
        if (state is not { State: HostTuningState.PreparedState })
        {
            // Fresh start (never prepared, or reverted): keep only a pending reboot marker.
            state = new HostTuningState { RebootRequiredSince = state?.RebootRequiredSince, PendingServices = state?.PendingServices ?? [] };
        }

        var serviceDllChanged = new List<string>();
        foreach (var item in Plan(options))
        {
            var record = state.Items.FirstOrDefault(r => r.Id == item.Id);
            var current = _registry.Get(item.Key, item.Name);
            if (record is null)
            {
                record = new HostTuningRecord
                {
                    Id = item.Id,
                    Key = item.Key,
                    Name = item.Name,
                    TermWrap = item.TermWrap,
                    Existed = current is not null,
                    Previous = current,
                };
                state.Items.Add(record);
            }

            record.Desired = item.Desired;
            if (!item.Desired.SameAs(current))
            {
                _registry.Set(item.Key, item.Name, item.Desired);
                changes.Add($"{item.Id}: {current?.ToString() ?? "(missing)"} -> {item.Desired}");
                if (item.TermWrap)
                {
                    serviceDllChanged.Add(item.Key == TermServiceParametersKey ? TermService : UmRdpService);
                }
            }
        }

        state.TermWrapSkipped = options.SkipTermWrap && !state.Items.Any(r => r.TermWrap);

        // TermService must run at boot so that autostart desks can connect; record the previous start type.
        var startType = _services.GetStartType(TermService);
        if (startType is null)
        {
            notes.Add("TermService does not exist on this system");
        }
        else
        {
            var svc = state.Services.FirstOrDefault(s => s.Name == TermService);
            if (svc is null)
            {
                state.Services.Add(new ServiceStartRecord { Name = TermService, PreviousStartType = startType.Value, DesiredStartType = StartAutomatic });
            }

            if (startType != StartAutomatic)
            {
                _services.SetStartType(TermService, StartAutomatic);
                changes.Add($"TermService start type: {startType} -> {StartAutomatic} (automatic)");
            }
        }

        MarkServiceDllChange(state, serviceDllChanged);
        state.State = HostTuningState.PreparedState;
        state.PreparedAt = _time.GetUtcNow();
        SaveState(state);

        if (startType is not null && !_services.IsRunning(TermService))
        {
            try
            {
                _services.Start(TermService);
                changes.Add("TermService started");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                notes.Add($"could not start TermService: {ex.Message}");
            }
        }

        return new HostTuningResult(changes, IsRebootRequired(state), notes);
    }

    // ------------------------------------------------------------------ revert

    public HostTuningResult Revert()
    {
        var changes = new List<string>();
        var notes = new List<string>();
        var state = LoadState();
        if (state is not { State: HostTuningState.PreparedState })
        {
            notes.Add("the host was not prepared by SplitPlay; nothing to revert");
            return new HostTuningResult(changes, state is not null && IsRebootRequired(state), notes);
        }

        var serviceDllChanged = new List<string>();
        foreach (var record in Enumerable.Reverse(state.Items))
        {
            var current = _registry.Get(record.Key, record.Name);
            if (record.Existed && record.Previous is not null)
            {
                if (!record.Previous.SameAs(current) || record.Previous.Kind != current?.Kind)
                {
                    _registry.Set(record.Key, record.Name, record.Previous);
                    changes.Add($"{record.Id}: {current?.ToString() ?? "(missing)"} -> {record.Previous}");
                }
            }
            else if (current is not null)
            {
                _registry.Delete(record.Key, record.Name);
                changes.Add($"{record.Id}: {current} -> (deleted)");
            }
            else
            {
                continue;
            }

            if (record.TermWrap)
            {
                serviceDllChanged.Add(record.Key == TermServiceParametersKey ? TermService : UmRdpService);
            }
        }

        foreach (var svc in state.Services)
        {
            var startType = _services.GetStartType(svc.Name);
            if (startType is not null && startType != svc.PreviousStartType)
            {
                _services.SetStartType(svc.Name, svc.PreviousStartType);
                changes.Add($"{svc.Name} start type: {startType} -> {svc.PreviousStartType}");
            }
        }

        MarkServiceDllChange(state, serviceDllChanged);
        var reverted = new HostTuningState
        {
            State = HostTuningState.RevertedState,
            RevertedAt = _time.GetUtcNow(),
            RebootRequiredSince = state.RebootRequiredSince,
            PendingServices = state.PendingServices,
        };
        SaveState(reverted);
        return new HostTuningResult(changes, IsRebootRequired(reverted), notes);
    }

    // ------------------------------------------------------------------ status

    public HostStatus Status(HostTuningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var state = LoadState();
        var prepared = state is { State: HostTuningState.PreparedState };
        var notes = new List<string>();
        var skipped = prepared && state!.TermWrapSkipped;
        var plan = Plan(options with { SkipTermWrap = options.SkipTermWrap || skipped }).ToList();

        // Items recorded at prepare time that the current plan does not list (e.g. UmWrap) are still checked.
        if (prepared)
        {
            foreach (var record in state!.Items.Where(r => plan.All(p => p.Id != r.Id) && r.Desired is not null))
            {
                plan.Add(new HostTuningItem(record.Id, record.Key, record.Name, record.Desired!, record.TermWrap));
            }
        }

        var items = new List<HostItemStatus>();
        foreach (var item in plan)
        {
            var current = _registry.Get(item.Key, item.Name);
            var desired = (prepared ? state!.Items.FirstOrDefault(r => r.Id == item.Id)?.Desired : null) ?? item.Desired;
            items.Add(new HostItemStatus(item.Id, item.Key, item.Name, current, desired,
                desired.SameAs(current) ? HostItemStatus.Ok : HostItemStatus.Differs));
        }

        if (skipped || options.SkipTermWrap)
        {
            var current = _registry.Get(TermServiceParametersKey, "ServiceDll");
            items.Add(new HostItemStatus("TermService.ServiceDll", TermServiceParametersKey, "ServiceDll", current, null, HostItemStatus.Skipped));
        }

        var startType = _services.GetStartType(TermService);
        var running = startType is not null && _services.IsRunning(TermService);
        items.Add(new HostItemStatus("TermService", @"SYSTEM\CurrentControlSet\Services\TermService", "Start",
            startType is null ? null : RegValue.DWord((uint)startType.Value), RegValue.DWord(StartAutomatic),
            startType == StartAutomatic ? HostItemStatus.Ok : HostItemStatus.Differs));
        if (!running)
        {
            notes.Add("TermService is not running");
        }

        if (!prepared)
        {
            notes.Add(state is null ? $"not prepared ({_stateFile} does not exist)" : "not prepared (reverted)");
        }

        var reboot = state is not null && IsRebootRequired(state);
        return new HostStatus(prepared && items.All(i => i.IsOk), reboot, skipped, Edition, items, notes);
    }

    // ------------------------------------------------------------------ compose interval

    /// <summary>Updates <c>DWMFRAMEINTERVAL</c> after a <c>host.composeIntervalMs</c> change (only on a prepared host).</summary>
    public bool ApplyComposeInterval(int composeIntervalMs)
    {
        var state = LoadState();
        var record = state?.Items.FirstOrDefault(r => r.Id == "DWMFRAMEINTERVAL");
        if (state is not { State: HostTuningState.PreparedState } || record is null)
        {
            return false;
        }

        var desired = RegValue.DWord((uint)Math.Clamp(composeIntervalMs, 1, 100));
        _registry.Set(record.Key, record.Name, desired);
        record.Desired = desired;
        SaveState(state);
        return true;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A changed <c>ServiceDll</c> takes effect when the hosting process starts again. If the service is not running it
    /// loads the new DLL on its next start; otherwise a reboot is needed.
    /// </summary>
    private void MarkServiceDllChange(HostTuningState state, List<string> services)
    {
        var running = services.Distinct().Where(_services.IsRunning).ToList();
        if (running.Count == 0)
        {
            return;
        }

        state.RebootRequiredSince = _time.GetUtcNow();
        state.PendingServices = [.. state.PendingServices.Union(running)];
    }

    private bool IsRebootRequired(HostTuningState state)
    {
        if (state.RebootRequiredSince is not { } since)
        {
            return false;
        }

        foreach (var name in state.PendingServices.DefaultIfEmpty(TermService))
        {
            // A service process started after the change has loaded the new DLL; one that is not running will on start.
            if (_services.IsRunning(name) && (_services.ProcessStartTime(name) is not { } started || started < since))
            {
                return true;
            }
        }

        return false;
    }

    private HostTuningState? LoadState()
    {
        if (!File.Exists(_stateFile))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(_stateFile, Encoding.UTF8), HostTuningJsonContext.Default.HostTuningState);
        }
        catch (JsonException ex)
        {
            throw new HostTuningException($"{_stateFile} is corrupt: {ex.Message}", ex);
        }
    }

    private void SaveState(HostTuningState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_stateFile))!);
        var temp = _stateFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, HostTuningJsonContext.Default.HostTuningState), new UTF8Encoding(false));
        File.Move(temp, _stateFile, overwrite: true);
    }
}

/// <summary><c>host-tuning.json</c>.</summary>
public sealed class HostTuningState
{
    public const string PreparedState = "prepared";
    public const string RevertedState = "reverted";

    public int Version { get; set; } = 1;

    public string State { get; set; } = PreparedState;

    public DateTimeOffset? PreparedAt { get; set; }

    public DateTimeOffset? RevertedAt { get; set; }

    /// <summary>Prepared with <c>--skip-termwrap</c> and TermWrap was never installed.</summary>
    public bool TermWrapSkipped { get; set; }

    /// <summary>When a <c>ServiceDll</c> changed while its service was running (cleared implicitly by a restart).</summary>
    public DateTimeOffset? RebootRequiredSince { get; set; }

    public List<string> PendingServices { get; set; } = [];

    public List<HostTuningRecord> Items { get; set; } = [];

    public List<ServiceStartRecord> Services { get; set; } = [];
}

/// <summary>One managed value with what was there before SplitPlay touched it.</summary>
public sealed class HostTuningRecord
{
    public string Id { get; set; } = "";

    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public bool TermWrap { get; set; }

    /// <summary>False if the value did not exist; revert then deletes it.</summary>
    public bool Existed { get; set; }

    public RegValue? Previous { get; set; }

    public RegValue? Desired { get; set; }
}

public sealed class ServiceStartRecord
{
    public string Name { get; set; } = "";

    public int PreviousStartType { get; set; }

    public int DesiredStartType { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HostTuningState))]
[JsonSerializable(typeof(HostStatus))]
[JsonSerializable(typeof(HostTuningResult))]
public sealed partial class HostTuningJsonContext : JsonSerializerContext;
