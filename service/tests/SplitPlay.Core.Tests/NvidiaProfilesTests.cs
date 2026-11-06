// SPDX-License-Identifier: MIT
using SplitPlay.Core.Nvidia;

namespace SplitPlay.Core.Tests;

public sealed class NvidiaProfilesTests : IDisposable
{
    private const uint Present = DrsSettings.OglPreferDxPresent;
    private const uint PState = DrsSettings.PreferredPState;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "splitplay-nv-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeDrs _drs = new();

    public NvidiaProfilesTests() => Directory.CreateDirectory(_dir);

    private string StateFile => Path.Combine(_dir, NvidiaProfiles.StateFileName);

    private string UndoFile => Path.Combine(_dir, "Sunshine", "nvprefs_undo.json");

    private NvidiaProfiles Profiles() => new(_drs, StateFile, UndoFile);

    private static readonly DrsProfileSpec[] Running = [NvidiaProfiles.Worker, NvidiaProfiles.Global];
    private static readonly DrsProfileSpec[] Stopped = [NvidiaProfiles.Worker];

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
    public void Start_creates_the_worker_profile_and_sets_the_global_value_recording_what_was_there()
    {
        _drs.Set(null, Present, 0);
        var result = Profiles().Apply(Running);

        Assert.True(result.Driver);
        Assert.True(_drs.HasProfile(NvidiaProfiles.WorkerProfile));
        Assert.Contains(NvidiaProfiles.WorkerExecutable, _drs.Applications(NvidiaProfiles.WorkerProfile));
        Assert.Equal(DrsSettings.PreferredPStatePreferMax, _drs.Get(NvidiaProfiles.WorkerProfile, PState));
        Assert.Equal(DrsSettings.OglPreferDxPresentEnabled, _drs.Get(null, Present));
        Assert.Equal(1, _drs.Saves);

        var state = Profiles().LoadState();
        Assert.Contains(NvidiaProfiles.WorkerProfile, state.CreatedProfiles);
        var global = Assert.Single(state.Settings, s => s.Profile is null);
        Assert.Equal(0u, global.Previous);
        Assert.Equal(DrsSettings.OglPreferDxPresentEnabled, global.Ours);
    }

    [Fact]
    public void Applying_again_changes_and_saves_nothing()
    {
        Profiles().Apply(Running);
        var again = Profiles().Apply(Running);
        Assert.Empty(again.Changes);
        Assert.Equal(1, _drs.Saves);
    }

    [Fact]
    public void Stopping_gives_the_global_value_back_and_keeps_the_worker_profile()
    {
        _drs.Set(null, Present, 0);
        Profiles().Apply(Running);
        Profiles().Apply(Stopped);

        Assert.Equal(0u, _drs.Get(null, Present));
        Assert.True(_drs.HasProfile(NvidiaProfiles.WorkerProfile));
        Assert.DoesNotContain(Profiles().LoadState().Settings, s => s.Profile is null);
    }

    [Fact]
    public void A_global_value_that_was_not_set_is_removed_again()
    {
        Profiles().Apply(Running);
        Assert.Equal(DrsSettings.OglPreferDxPresentEnabled, _drs.Get(null, Present));
        Profiles().Apply(Stopped);
        Assert.Null(_drs.Get(null, Present));
    }

    [Fact]
    public void A_value_the_owner_changed_afterwards_is_left_alone()
    {
        _drs.Set(null, Present, 0);
        Profiles().Apply(Running);
        _drs.Set(null, Present, 2);

        var result = Profiles().Apply(Stopped);
        Assert.Equal(2u, _drs.Get(null, Present));
        Assert.Contains(result.Notes, n => n.Contains("left alone", StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_changed_while_running_is_set_again_and_the_original_is_still_restored()
    {
        _drs.Set(null, Present, 0);
        Profiles().Apply(Running);
        _drs.Set(null, Present, 2);
        _drs.Set(NvidiaProfiles.WorkerProfile, PState, null);

        var result = Profiles().Apply(Running);
        Assert.Equal(DrsSettings.OglPreferDxPresentEnabled, _drs.Get(null, Present));
        Assert.Equal(DrsSettings.PreferredPStatePreferMax, _drs.Get(NvidiaProfiles.WorkerProfile, PState));
        Assert.Contains(result.Notes, n => n.Contains("set again", StringComparison.Ordinal));

        Profiles().Apply(Stopped);
        Assert.Equal(0u, _drs.Get(null, Present));
    }

    [Fact]
    public void Giving_everything_back_deletes_the_profile_and_the_record()
    {
        _drs.Set(null, Present, 0);
        Profiles().Apply(Running);
        Profiles().Apply([]);

        Assert.False(_drs.HasProfile(NvidiaProfiles.WorkerProfile));
        Assert.Equal(0u, _drs.Get(null, Present));
        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public void A_worker_profile_made_by_an_older_worker_is_adopted_and_deleted_on_uninstall()
    {
        _drs.AddProfile(NvidiaProfiles.WorkerProfile, NvidiaProfiles.WorkerExecutable);
        _drs.Set(NvidiaProfiles.WorkerProfile, PState, DrsSettings.PreferredPStatePreferMax);

        var result = Profiles().Apply(Running);
        Assert.Contains(result.Notes, n => n.Contains("adopted", StringComparison.Ordinal));
        Assert.Contains(NvidiaProfiles.WorkerProfile, Profiles().LoadState().CreatedProfiles);

        Profiles().Apply([]);
        Assert.False(_drs.HasProfile(NvidiaProfiles.WorkerProfile));
    }

    [Fact]
    public void Uninstall_without_a_record_still_deletes_an_older_workers_profile()
    {
        _drs.AddProfile(NvidiaProfiles.WorkerProfile, NvidiaProfiles.WorkerExecutable);
        Profiles().Apply([]);
        Assert.False(_drs.HasProfile(NvidiaProfiles.WorkerProfile));
    }

    [Fact]
    public void Another_programs_profile_is_never_deleted()
    {
        _drs.AddProfile("SunshineStream", "sunshine.exe");
        Profiles().Apply(Running);
        Profiles().Apply([]);
        Assert.True(_drs.HasProfile("SunshineStream"));
    }

    [Fact]
    public void An_older_workers_undo_file_is_taken_over_and_its_original_value_restored()
    {
        // A worker before patch 0020 changed the global value from 3 and was killed before it could restore it.
        _drs.Set(null, Present, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(UndoFile)!);
        File.WriteAllText(UndoFile, """{"opengl_swapchain":{"our_value":1,"undo_value":3}}""");

        var result = Profiles().Apply(Running);
        Assert.False(File.Exists(UndoFile));
        Assert.Contains(result.Changes, c => c.Contains("taken over", StringComparison.Ordinal));
        Assert.Equal(3u, Assert.Single(Profiles().LoadState().Settings, s => s.Profile is null).Previous);

        Profiles().Apply(Stopped);
        Assert.Equal(3u, _drs.Get(null, Present));
    }

    [Fact]
    public void An_undo_file_with_a_null_previous_value_removes_the_value()
    {
        _drs.Set(null, Present, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(UndoFile)!);
        File.WriteAllText(UndoFile, """{"opengl_swapchain":{"our_value":1,"undo_value":null}}""");

        Profiles().Apply(Stopped);
        Assert.Null(_drs.Get(null, Present));
        Assert.False(File.Exists(UndoFile));
    }

    [Fact]
    public void An_undo_file_a_running_process_holds_is_left_to_it()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UndoFile)!);
        File.WriteAllText(UndoFile, """{"opengl_swapchain":{"our_value":1,"undo_value":3}}""");
        using (new FileStream(UndoFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = Profiles().Apply(Running);
            Assert.Contains(result.Notes, n => n.Contains("left to its owner", StringComparison.Ordinal));
        }

        Assert.True(File.Exists(UndoFile));
    }

    [Fact]
    public void A_failed_save_keeps_the_original_value_on_record()
    {
        _drs.Set(null, Present, 0);
        _drs.FailNextSave = true;
        Assert.Throws<NvidiaDrsException>(() => Profiles().Apply(Running));
        Assert.Equal(0u, _drs.Get(null, Present));
        Assert.Equal(0u, Assert.Single(Profiles().LoadState().Settings, s => s.Profile is null).Previous);

        // The next attempt works and still knows the value from before the first one.
        Profiles().Apply(Running);
        Assert.Equal(DrsSettings.OglPreferDxPresentEnabled, _drs.Get(null, Present));
        Profiles().Apply([]);
        Assert.Equal(0u, _drs.Get(null, Present));
        Assert.False(_drs.HasProfile(NvidiaProfiles.WorkerProfile));
    }

    [Fact]
    public void Without_an_NVIDIA_driver_nothing_happens()
    {
        _drs.Driver = false;
        var result = Profiles().Apply(Running);
        Assert.False(result.Driver);
        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public void A_failing_profile_is_reported_and_the_rest_still_applies()
    {
        _drs.AddProfile("Someone else", NvidiaProfiles.WorkerExecutable);
        var result = Profiles().Apply(Running);
        Assert.Contains(result.Notes, n => n.Contains("failed", StringComparison.Ordinal) && n.Contains("ALREADY_IN_USE", StringComparison.Ordinal));
        Assert.Equal(DrsSettings.OglPreferDxPresentEnabled, _drs.Get(null, Present));
    }

    [Fact]
    public void A_failed_save_while_giving_back_keeps_the_record_so_the_next_attempt_restores()
    {
        _drs.Set(null, Present, 0);
        Profiles().Apply(Running);
        _drs.FailNextSave = true;
        Assert.Throws<NvidiaDrsException>(() => Profiles().Apply(Stopped));
        Assert.Equal(DrsSettings.OglPreferDxPresentEnabled, _drs.Get(null, Present));
        Assert.Equal(0u, Assert.Single(Profiles().LoadState().Settings, s => s.Profile is null).Previous);

        // After a restart the original value is still known.
        Profiles().Apply(Running);
        Profiles().Apply([]);
        Assert.Equal(0u, _drs.Get(null, Present));
        Assert.False(_drs.HasProfile(NvidiaProfiles.WorkerProfile));
    }

    [Fact]
    public void A_new_value_of_SplitPlays_that_was_not_saved_still_counts_as_SplitPlays()
    {
        // Step 7 changes values in place (a new FPS limit): a failed save must not make the old one look like the owner's.
        _drs.Set(null, Present, 0);
        Profiles().Apply(Running);
        var changed = new DrsProfileSpec(null, [], new Dictionary<uint, uint> { [Present] = 5 });
        _drs.FailNextSave = true;
        Assert.Throws<NvidiaDrsException>(() => Profiles().Apply([NvidiaProfiles.Worker, changed]));
        Assert.Equal(DrsSettings.OglPreferDxPresentEnabled, _drs.Get(null, Present));

        Profiles().Apply(Stopped);
        Assert.Equal(0u, _drs.Get(null, Present));
    }

    [Fact]
    public void An_unsaved_value_still_counts_as_SplitPlays_when_a_later_step_fails()
    {
        // The reviewer's case: 60 saved, 120 not saved, then the spec fails before writing; the original 0 must come back.
        _drs.AddProfile("Game", "a.exe");
        _drs.Set("Game", DrsSettings.FrameRateLimit, 0);
        DrsProfileSpec Limit(uint fps, params string[] apps) => new("Game", apps, new Dictionary<uint, uint> { [DrsSettings.FrameRateLimit] = fps });

        Profiles().Apply([Limit(60, "a.exe")]);
        _drs.FailNextSave = true;
        Assert.Throws<NvidiaDrsException>(() => Profiles().Apply([Limit(120, "a.exe")]));
        _drs.FailNextSave = true;
        Assert.Throws<NvidiaDrsException>(() => Profiles().Apply([Limit(150, "a.exe")]));
        _drs.AddProfile("Other", "b.exe");
        var failing = Profiles().Apply([Limit(150, "a.exe", "b.exe")]);
        Assert.Equal(1, failing.Failed);
        Assert.Equal(60u, _drs.Get("Game", DrsSettings.FrameRateLimit));

        Profiles().Apply([]);
        Assert.Equal(0u, _drs.Get("Game", DrsSettings.FrameRateLimit));
        Assert.True(_drs.HasProfile("Game"));
    }

    [Fact]
    public void Without_a_driver_a_held_record_is_reported()
    {
        Profiles().Apply(Running);
        _drs.Driver = false;
        var result = Profiles().Apply([]);
        Assert.False(result.Driver);
        Assert.True(result.Held);
    }
}
