// SPDX-License-Identifier: MIT
using Microsoft.Extensions.Time.Testing;
using SplitPlay.Core.Host;

namespace SplitPlay.Core.Tests;

public sealed class HostTuningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "splitplay-host-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeRegistry _registry = new();
    private readonly FakeServices _services = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

    public HostTuningTests()
    {
        Directory.CreateDirectory(TermWrapDir);
        File.WriteAllText(Path.Combine(TermWrapDir, HostTuner.TermWrapDll), "fake");
        File.WriteAllText(Path.Combine(TermWrapDir, HostTuner.UmWrapDll), "fake");

        // A Windows 11 Pro host with Remote Desktop off, as shipped.
        _registry.Set(HostTuner.CurrentVersionKey, "EditionID", new RegValue(RegKind.String, "Professional"));
        _registry.Set(HostTuner.CurrentVersionKey, "InstallationType", new RegValue(RegKind.String, "Client"));
        _registry.Set(HostTuner.TerminalServerKey, "fDenyTSConnections", RegValue.DWord(1));
        _registry.Set(HostTuner.RdpTcpKey, "UserAuthentication", RegValue.DWord(0));
        _registry.Set(HostTuner.TermServiceParametersKey, "ServiceDll", RegValue.Expand(HostTuner.DefaultTermServiceDll));
        _registry.Set(HostTuner.UmRdpServiceParametersKey, "ServiceDll", RegValue.Expand(HostTuner.DefaultUmRdpServiceDll));
        _services.StartTypes["TermService"] = 3;
        _services.Running.Add("TermService");
        _services.StartTimes["TermService"] = _time.GetUtcNow() - TimeSpan.FromHours(1);
    }

    private string TermWrapDir => Path.Combine(_dir, "app", "termwrap");

    private string StateFile => Path.Combine(_dir, "data", HostTuner.StateFileName);

    private HostTuner Tuner() => new(_registry, _services, StateFile, _time);

    private HostTuningOptions Options(bool skip = false, int compose = 4) =>
        new() { TermWrapDirectory = TermWrapDir, SkipTermWrap = skip, ComposeIntervalMs = compose };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Prepare_sets_every_value_and_swaps_ServiceDll_like_TermWrap()
    {
        var result = Tuner().Prepare(Options());

        Assert.Equal("0", _registry.Get(HostTuner.TerminalServerKey, "fDenyTSConnections")!.Data);
        Assert.Equal("0", _registry.Get(HostTuner.TerminalServerKey, "fSingleSessionPerUser")!.Data);
        Assert.Equal("1", _registry.Get(HostTuner.RdpTcpKey, "UserAuthentication")!.Data);
        Assert.Equal("1", _registry.Get(HostTuner.RdpTcpKey, "fInheritColorDepth")!.Data);
        Assert.Equal("5", _registry.Get(HostTuner.RdpTcpKey, "ColorDepth")!.Data);
        Assert.Equal("1", _registry.Get(HostTuner.PolicyKey, "bEnumerateHWBeforeSW")!.Data);
        Assert.Equal("4", _registry.Get(HostTuner.WinStationsKey, "DWMFRAMEINTERVAL")!.Data);
        Assert.Equal("0", _registry.Get(HostTuner.PolicyKey, "MaxIdleTime")!.Data);
        Assert.Equal("0", _registry.Get(HostTuner.PolicyKey, "MaxDisconnectionTime")!.Data);

        var dll = _registry.Get(HostTuner.TermServiceParametersKey, "ServiceDll")!;
        Assert.Equal(RegKind.ExpandString, dll.Kind);
        Assert.Equal(Path.Combine(TermWrapDir, "TermWrap.dll"), dll.Data);

        // Pro edition: UmRdpService stays as it is (UmWrap is for Home and Server only).
        Assert.Equal(HostTuner.DefaultUmRdpServiceDll, _registry.Get(HostTuner.UmRdpServiceParametersKey, "ServiceDll")!.Data);
        Assert.Equal(2, _services.StartTypes["TermService"]);

        // TermService was running with termsrv.dll loaded: a reboot is needed.
        Assert.True(result.RebootRequired);
        var status = Tuner().Status(Options());
        Assert.True(status.Prepared);
        Assert.True(status.RebootRequired);
        Assert.Contains("reboot required", status.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Reboot_requirement_clears_once_TermService_restarted()
    {
        Tuner().Prepare(Options());
        _time.Advance(TimeSpan.FromMinutes(5));
        _services.StartTimes["TermService"] = _time.GetUtcNow(); // as after a reboot
        var status = Tuner().Status(Options());
        Assert.False(status.RebootRequired);
        Assert.True(status.Ready);
        Assert.Null(status.Problem);
    }

    [Fact]
    public void Stopped_TermService_loads_TermWrap_on_start_without_a_reboot()
    {
        _services.Running.Clear();
        var result = Tuner().Prepare(Options());
        Assert.False(result.RebootRequired);
        Assert.Contains("TermService", _services.Started);
        Assert.Contains("TermService started", result.Changes);
    }

    [Fact]
    public void Revert_restores_the_exact_previous_values()
    {
        Tuner().Prepare(Options());
        Tuner().Prepare(Options(compose: 2)); // preparing again must not record SplitPlay's own values as "previous"

        var result = Tuner().Revert();
        Assert.NotEmpty(result.Changes);
        Assert.Equal("1", _registry.Get(HostTuner.TerminalServerKey, "fDenyTSConnections")!.Data);
        Assert.Equal("0", _registry.Get(HostTuner.RdpTcpKey, "UserAuthentication")!.Data);
        Assert.Equal(new RegValue(RegKind.ExpandString, HostTuner.DefaultTermServiceDll), _registry.Get(HostTuner.TermServiceParametersKey, "ServiceDll"));

        // Values that did not exist before are deleted again.
        Assert.Null(_registry.Get(HostTuner.TerminalServerKey, "fSingleSessionPerUser"));
        Assert.Null(_registry.Get(HostTuner.PolicyKey, "bEnumerateHWBeforeSW"));
        Assert.Null(_registry.Get(HostTuner.WinStationsKey, "DWMFRAMEINTERVAL"));
        Assert.Equal(3, _services.StartTypes["TermService"]);
        Assert.True(result.RebootRequired);

        var status = Tuner().Status(Options());
        Assert.False(status.Prepared);

        // Reverting twice is harmless.
        Assert.Empty(Tuner().Revert().Changes);
    }

    [Fact]
    public void Skip_termwrap_leaves_ServiceDll_alone_and_needs_no_reboot()
    {
        var result = Tuner().Prepare(Options(skip: true));
        Assert.False(result.RebootRequired);
        Assert.Equal(HostTuner.DefaultTermServiceDll, _registry.Get(HostTuner.TermServiceParametersKey, "ServiceDll")!.Data);

        // The service checks without knowing about the flag: the state file remembers it.
        var status = Tuner().Status(Options());
        Assert.True(status.Ready);
        Assert.True(status.TermWrapSkipped);
        Assert.Contains(status.Items, i => i.Id == "TermService.ServiceDll" && i.State == HostItemStatus.Skipped);

        Tuner().Revert();
        Assert.Equal("1", _registry.Get(HostTuner.TerminalServerKey, "fDenyTSConnections")!.Data);
    }

    [Fact]
    public void Missing_TermWrap_dll_is_an_error_unless_skipped()
    {
        File.Delete(Path.Combine(TermWrapDir, HostTuner.TermWrapDll));
        var ex = Assert.Throws<HostTuningException>(() => Tuner().Prepare(Options()));
        Assert.Contains("--skip-termwrap", ex.Message, StringComparison.Ordinal);
        Assert.Equal("1", _registry.Get(HostTuner.TerminalServerKey, "fDenyTSConnections")!.Data);
        Tuner().Prepare(Options(skip: true));
    }

    [Theory]
    [InlineData("Core", "Client")]
    [InlineData("CoreSingleLanguage", "Client")]
    [InlineData("ServerDatacenter", "Server")]
    public void Home_and_Server_editions_also_swap_UmRdpService(string edition, string installation)
    {
        _registry.Set(HostTuner.CurrentVersionKey, "EditionID", new RegValue(RegKind.String, edition));
        _registry.Set(HostTuner.CurrentVersionKey, "InstallationType", new RegValue(RegKind.String, installation));
        Tuner().Prepare(Options());
        Assert.Equal(Path.Combine(TermWrapDir, "UmWrap.dll"), _registry.Get(HostTuner.UmRdpServiceParametersKey, "ServiceDll")!.Data);
        Tuner().Revert();
        Assert.Equal(HostTuner.DefaultUmRdpServiceDll, _registry.Get(HostTuner.UmRdpServiceParametersKey, "ServiceDll")!.Data);
    }

    [Fact]
    public void Status_reports_drift_and_unprepared_hosts()
    {
        var before = Tuner().Status(Options());
        Assert.False(before.Prepared);
        Assert.Contains("run setup again", before.Problem, StringComparison.Ordinal);

        Tuner().Prepare(Options(skip: true));
        _registry.Set(HostTuner.TerminalServerKey, "fDenyTSConnections", RegValue.DWord(1)); // someone turned RDP off
        var drift = Tuner().Status(Options());
        Assert.False(drift.Prepared);
        Assert.Contains("fDenyTSConnections", drift.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_interval_follows_the_host_setting()
    {
        Assert.False(Tuner().ApplyComposeInterval(2)); // not prepared: nothing written
        Tuner().Prepare(Options(skip: true));
        Assert.True(Tuner().ApplyComposeInterval(2));
        Assert.Equal("2", _registry.Get(HostTuner.WinStationsKey, "DWMFRAMEINTERVAL")!.Data);
        Assert.True(Tuner().Status(Options(compose: 4)).Ready); // the recorded value wins over the default
    }

    private sealed class FakeRegistry : IHostRegistry
    {
        private readonly Dictionary<(string, string), RegValue> _values = new();

        public RegValue? Get(string key, string name) => _values.TryGetValue((key.ToUpperInvariant(), name.ToUpperInvariant()), out var v) ? v : null;

        public void Set(string key, string name, RegValue value) => _values[(key.ToUpperInvariant(), name.ToUpperInvariant())] = value;

        public void Delete(string key, string name) => _values.Remove((key.ToUpperInvariant(), name.ToUpperInvariant()));
    }

    private sealed class FakeServices : IHostServices
    {
        public Dictionary<string, int> StartTypes { get; } = new();

        public HashSet<string> Running { get; } = [];

        public Dictionary<string, DateTimeOffset> StartTimes { get; } = new();

        public List<string> Started { get; } = [];

        public int? GetStartType(string name) => StartTypes.TryGetValue(name, out var t) ? t : null;

        public void SetStartType(string name, int startType) => StartTypes[name] = startType;

        public bool IsRunning(string name) => Running.Contains(name);

        public void Start(string name)
        {
            Started.Add(name);
            Running.Add(name);
        }

        public DateTimeOffset? ProcessStartTime(string name) => Running.Contains(name) && StartTimes.TryGetValue(name, out var t) ? t : null;
    }
}
