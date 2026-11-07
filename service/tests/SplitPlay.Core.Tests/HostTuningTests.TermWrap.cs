// SPDX-License-Identifier: MIT
using SplitPlay.Core.Host;

namespace SplitPlay.Core.Tests;

// TermWrapGuard: TermWrap surviving Windows updates (ServiceDll reset by a feature update, a termsrv.dll build TermWrap
// cannot patch). Shares the fake host of HostTuningTests.
public sealed partial class HostTuningTests
{
    private readonly FakeInspector _inspector = new();

    private string CacheFile => Path.Combine(_dir, "data", TermWrapGuard.CacheFileName);

    private TermWrapGuard Guard() => new(Tuner(), _services, _inspector, CacheFile, _time);

    private TermWrapHealth Check() => Guard().Check(TermWrapDir);

    private string TermWrapPath => Path.Combine(TermWrapDir, HostTuner.TermWrapDll);

    /// <summary>Prepared with TermWrap, then rebooted: TermService runs with TermWrap loaded.</summary>
    private void PrepareAndReboot()
    {
        Tuner().Prepare(Options());
        _time.Advance(TimeSpan.FromMinutes(5));
        _services.StartTimes["TermService"] = _time.GetUtcNow();
        _inspector.Loaded = true;
    }

    /// <summary>What a feature update does: ServiceDll back to termsrv.dll, then a reboot.</summary>
    private void FeatureUpdate()
    {
        _registry.Set(HostTuner.TermServiceParametersKey, "ServiceDll", RegValue.Expand(HostTuner.DefaultTermServiceDll));
        _time.Advance(TimeSpan.FromHours(1));
        _services.StartTimes["TermService"] = _time.GetUtcNow();
        _inspector.Loaded = false;
        _time.Advance(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void TermWrap_in_place_and_loaded_is_ok()
    {
        PrepareAndReboot();
        var health = Check();
        Assert.Equal(TermWrapState.Ok, health.State);
        Assert.False(health.Repaired);
        Assert.Null(health.Problem);
        Assert.Equal("10.0.26100.4202", health.TermsrvVersion);
    }

    [Fact]
    public void ServiceDll_reset_by_a_feature_update_is_written_back_and_asks_for_a_reboot()
    {
        PrepareAndReboot();
        FeatureUpdate();

        var health = Check();

        Assert.Equal(TermWrapState.RebootRequired, health.State);
        Assert.True(health.Repaired);
        Assert.Contains("Windows Update reset TermWrap", health.Problem, StringComparison.Ordinal);
        var dll = _registry.Get(HostTuner.TermServiceParametersKey, "ServiceDll")!;
        Assert.Equal(RegKind.ExpandString, dll.Kind);
        Assert.Equal(TermWrapPath, dll.Data);

        // The host status agrees (desks get "reboot required", not "run setup again").
        var status = Tuner().Status(Options());
        Assert.True(status.RebootRequired);
        Assert.Contains("reboot required", status.Problem, StringComparison.Ordinal);

        // Revert still gives back the value from before SplitPlay, not the one the update wrote.
        Tuner().Revert();
        Assert.Equal(HostTuner.DefaultTermServiceDll, _registry.Get(HostTuner.TermServiceParametersKey, "ServiceDll")!.Data);
    }

    [Fact]
    public void After_the_reboot_the_repaired_host_is_ok_again()
    {
        PrepareAndReboot();
        FeatureUpdate();
        Check();

        _time.Advance(TimeSpan.FromMinutes(3));
        _services.StartTimes["TermService"] = _time.GetUtcNow();
        _inspector.Loaded = true;

        var health = Check();
        Assert.Equal(TermWrapState.Ok, health.State);
        Assert.False(health.Repaired);
    }

    [Fact]
    public void A_stopped_TermService_needs_no_reboot_after_the_repair()
    {
        PrepareAndReboot();
        FeatureUpdate();
        _services.Running.Clear();

        var health = Check();
        Assert.Equal(TermWrapState.Ok, health.State);
        Assert.True(health.Repaired);
        Assert.Equal(TermWrapPath, _registry.Get(HostTuner.TermServiceParametersKey, "ServiceDll")!.Data);
    }

    [Fact]
    public void UmWrap_is_written_back_too_on_Home()
    {
        _registry.Set(HostTuner.CurrentVersionKey, "EditionID", new RegValue(RegKind.String, "Core"));
        PrepareAndReboot();
        _registry.Set(HostTuner.UmRdpServiceParametersKey, "ServiceDll", RegValue.Expand(HostTuner.DefaultUmRdpServiceDll));
        _services.Running.Add("UmRdpService");
        _services.StartTimes["UmRdpService"] = _time.GetUtcNow() - TimeSpan.FromMinutes(1);

        var health = Check();

        Assert.Equal(TermWrapState.RebootRequired, health.State);
        Assert.True(health.Repaired);
        Assert.Equal(Path.Combine(TermWrapDir, HostTuner.UmWrapDll), _registry.Get(HostTuner.UmRdpServiceParametersKey, "ServiceDll")!.Data);
    }

    [Fact]
    public void A_host_not_prepared_with_TermWrap_is_left_alone()
    {
        Assert.Equal(TermWrapState.NotManaged, Check().State);

        Tuner().Prepare(Options(skip: true));
        var health = Check();
        Assert.Equal(TermWrapState.NotManaged, health.State);
        Assert.Null(health.Problem);
        Assert.Equal(HostTuner.DefaultTermServiceDll, _registry.Get(HostTuner.TermServiceParametersKey, "ServiceDll")!.Data);
        Assert.Equal(0, _inspector.ProbeRuns);

        // Reverted: not ours any more either.
        Tuner().Prepare(Options());
        Tuner().Revert();
        Assert.Equal(TermWrapState.NotManaged, Check().State);
        Assert.Equal(HostTuner.DefaultTermServiceDll, _registry.Get(HostTuner.TermServiceParametersKey, "ServiceDll")!.Data);
    }

    [Fact]
    public void Missing_TermWrap_files_are_reported_and_not_pointed_at()
    {
        PrepareAndReboot();
        FeatureUpdate();
        File.Delete(TermWrapPath);

        var health = Check();
        Assert.Equal(TermWrapState.Missing, health.State);
        Assert.Contains("reinstall", health.Problem, StringComparison.Ordinal);
        Assert.Equal(HostTuner.DefaultTermServiceDll, _registry.Get(HostTuner.TermServiceParametersKey, "ServiceDll")!.Data);
    }

    [Fact]
    public void A_termsrv_build_TermWrap_cannot_patch_is_unsupported()
    {
        PrepareAndReboot();
        _inspector.Probe = () => new TermWrapProbeRun(0, ["probe: termsrv C:\\Windows\\System32\\termsrv.dll", "DefPolicyPatch not found", "probe: done"]);

        var health = Check();

        Assert.Equal(TermWrapState.Unsupported, health.State);
        Assert.Equal("DefPolicyPatch not found", health.Detail);
        Assert.Contains("termsrv.dll 10.0.26100.4202", health.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_wins_over_a_pending_reboot()
    {
        PrepareAndReboot();
        FeatureUpdate();
        _inspector.Probe = () => new TermWrapProbeRun(0, ["CSLQuery_Initialize not found", "probe: done"]);

        var health = Check();
        Assert.Equal(TermWrapState.Unsupported, health.State);
        Assert.True(health.Repaired); // the ServiceDll was still written back: a TermWrap update then only needs a reboot
    }

    [Fact]
    public void The_probe_runs_once_per_termsrv_version_and_probe_build()
    {
        PrepareAndReboot();
        Check();
        Check();
        Assert.Equal(1, _inspector.ProbeRuns);

        // A guard of a restarted service reads the cached verdict.
        Guard().Check(TermWrapDir);
        Assert.Equal(1, _inspector.ProbeRuns);

        _inspector.Version = "10.0.26100.7000"; // Patch Tuesday
        Check();
        Assert.Equal(2, _inspector.ProbeRuns);

        _inspector.ProbeStamp = "2-2"; // a new SplitPlay with a new TermWrap
        Check();
        Assert.Equal(3, _inspector.ProbeRuns);
    }

    [Fact]
    public void An_unknown_verdict_is_not_cached()
    {
        PrepareAndReboot();
        _inspector.Probe = () => null; // could not start
        Assert.Equal(TermWrapState.Ok, Check().State);
        _inspector.Probe = () => new TermWrapProbeRun(0, ["probe: done"]);
        Check();
        Assert.Equal(2, _inspector.ProbeRuns);
        Check();
        Assert.Equal(2, _inspector.ProbeRuns);
    }

    [Fact]
    public void No_probe_installed_claims_nothing()
    {
        PrepareAndReboot();
        _inspector.ProbeStamp = null;
        var health = Check();
        Assert.Equal(TermWrapState.Ok, health.State);
        Assert.Equal(0, _inspector.ProbeRuns);
    }

    [Fact]
    public void A_corrupt_cache_only_costs_a_probe_run()
    {
        PrepareAndReboot();
        Check();
        File.WriteAllText(CacheFile, "{ not json");
        Assert.Equal(TermWrapState.Ok, Check().State);
        Assert.Equal(2, _inspector.ProbeRuns);
    }

    [Fact]
    public void TermService_without_TermWrap_loaded_needs_a_reboot()
    {
        // ServiceDll is right and no reboot is recorded, but the running TermService has termsrv.dll itself (someone
        // changed the value by hand while it ran, then put it back).
        PrepareAndReboot();
        _inspector.Loaded = false;
        var health = Check();
        Assert.Equal(TermWrapState.RebootRequired, health.State);
        Assert.False(health.Repaired);
        Assert.Contains("Restart the PC", health.Problem, StringComparison.Ordinal);

        _inspector.Loaded = null; // module list unreadable: no claim
        Assert.Equal(TermWrapState.Ok, Check().State);
    }

    [Theory]
    [InlineData(0, new[] { "probe: done" }, TermWrapVerdict.Supported)]
    [InlineData(0, new[] { "SingleUserPatch not found", "NonRDPPatch not found", "probe: done" }, TermWrapVerdict.Supported)]
    [InlineData(0, new[] { "DefPolicyPatch: Unknown reg2", "probe: done" }, TermWrapVerdict.Unsupported)]
    [InlineData(0, new[] { "CDefPolicy_Query not found", "probe: done" }, TermWrapVerdict.Unsupported)]
    [InlineData(0, new[] { "bInitialized not found", "probe: done" }, TermWrapVerdict.Unsupported)]
    [InlineData(3, new[] { "probe: crashed" }, TermWrapVerdict.Unsupported)]
    [InlineData(1, new[] { "probe: cannot load termsrv.dll (error 2)" }, TermWrapVerdict.Unknown)]
    [InlineData(-1, new[] { "probe: timed out" }, TermWrapVerdict.Unknown)]
    public void Probe_output_is_classified(int exit, string[] lines, TermWrapVerdict expected)
    {
        Assert.Equal(expected, TermWrapGuard.Classify(new TermWrapProbeRun(exit, lines)).Verdict);
    }

    [Fact]
    public void Non_critical_probe_messages_are_kept_for_the_log()
    {
        var (verdict, detail) = TermWrapGuard.Classify(new TermWrapProbeRun(0, ["probe: termsrv x", "PropertyPatch not found", "probe: done"]));
        Assert.Equal(TermWrapVerdict.Supported, verdict);
        Assert.Equal("PropertyPatch not found", detail);
    }

    private sealed class FakeInspector : ITermsrvInspector
    {
        public string? Version { get; set; } = "10.0.26100.4202";

        public string? ProbeStamp { get; set; } = "1-1";

        public Func<TermWrapProbeRun?> Probe { get; set; } = () => new TermWrapProbeRun(0, ["probe: termsrv x", "probe: done"]);

        public int ProbeRuns { get; private set; }

        public bool? Loaded { get; set; } = true;

        public string? TermsrvVersion() => Version;

        public string? FileStamp(string path) =>
            path.EndsWith(TermWrapGuard.ProbeExe, StringComparison.OrdinalIgnoreCase) ? ProbeStamp : null;

        public TermWrapProbeRun? RunProbe(string probe, TimeSpan timeout)
        {
            ProbeRuns++;
            return Probe();
        }

        public bool? IsModuleLoaded(string service, string module) => Loaded;
    }
}
