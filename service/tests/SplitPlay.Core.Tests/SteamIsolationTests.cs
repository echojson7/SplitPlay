// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using SplitPlay.Core.Host;
using SplitPlay.Core.Steam;

namespace SplitPlay.Core.Tests;

public sealed class SteamIsolationTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string SteamExe = @"C:\Program Files (x86)\Steam\steam.exe";
    private const string MachineHandler = "\"" + SteamExe + "\" -- \"%1\"";
    private const string RunValue = "\"" + SteamExe + "\" -silent";

    private static readonly SteamMachineInfo Machine = new(MachineHandler, "URL:steam protocol");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "splitplay-steam-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly MemoryUserHive _hive = new();

    private string StateFile => Path.Combine(_dir, "desks", "desk-1", SteamIsolation.StateFileName);

    private SteamIsolation Isolation() => new(StateFile);

    private static RegValue Sz(string data) => new(RegKind.String, data);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Theory]
    [InlineData("desk-1", "SplitPlay_desk_1")]
    [InlineData("living-room-2", "SplitPlay_living_room_2")]
    [InlineData("abc", "SplitPlay_abc")]
    public void Ipc_name_keeps_only_letters_digits_and_underscores(string deskId, string expected) =>
        Assert.Equal(expected, SteamIsolation.IpcName(deskId));

    [Theory]
    [InlineData("\"C:\\Steam\\steam.exe\" -silent", "\"C:\\Steam\\steam.exe\" -master_ipc_name_override X -silent")]
    [InlineData("\"C:\\Steam\\steam.exe\"", "\"C:\\Steam\\steam.exe\" -master_ipc_name_override X")]
    [InlineData("C:\\Steam\\steam.exe -silent", "C:\\Steam\\steam.exe -master_ipc_name_override X -silent")]
    [InlineData("\"C:\\Steam\\steam.exe\" -- \"%1\"", "\"C:\\Steam\\steam.exe\" -master_ipc_name_override X -- \"%1\"")]
    [InlineData("\"C:\\Steam\\steam.exe\" -master_ipc_name_override Old -silent", "\"C:\\Steam\\steam.exe\" -master_ipc_name_override X -silent")]
    [InlineData("\"C:\\Steam\\steam.exe\" -silent -MASTER_IPC_NAME_OVERRIDE \"Old name\"", "\"C:\\Steam\\steam.exe\" -master_ipc_name_override X -silent")]
    public void Flag_goes_right_after_the_executable_and_replaces_an_old_one(string command, string expected) =>
        Assert.Equal(expected, SteamIsolation.WithFlag(command, "X"));

    [Fact]
    public void Flag_is_removed_without_touching_the_rest()
    {
        Assert.Equal(RunValue, SteamIsolation.WithoutFlag(SteamIsolation.WithFlag(RunValue, "SplitPlay_a")!));
        Assert.True(SteamIsolation.IsSteamCommand(RunValue));
        Assert.True(SteamIsolation.IsSteamCommand(@"D:\Games\Steam\STEAM.EXE"));
        Assert.False(SteamIsolation.IsSteamCommand("\"C:\\Tools\\launcher.exe\" steam.exe"));
        Assert.False(SteamIsolation.IsSteamCommand(""));
    }

    [Fact]
    public void Apply_writes_the_environment_variable_and_a_per_user_handler()
    {
        var result = Isolation().Apply(_hive, Sid, "desk-1", Machine);

        Assert.True(result.EnvironmentWritten);
        Assert.Equal(Sz("SplitPlay_desk_1"), _hive.Get("Environment", "steam_master_ipc_name_override"));
        Assert.Equal(Sz("URL:steam protocol"), _hive.Get(@"Software\Classes\steam", ""));
        Assert.Equal(Sz(""), _hive.Get(@"Software\Classes\steam", "URL Protocol"));
        Assert.Equal(Sz($"\"{SteamExe}\" -master_ipc_name_override SplitPlay_desk_1 -- \"%1\""),
            _hive.Get(@"Software\Classes\steam\Shell\Open\Command", ""));

        // Steam's autostart is off: nothing is created for it.
        Assert.Null(_hive.Get(SteamIsolation.RunKey, "Steam"));
        Assert.Equal(Sid, Isolation().RecordedSid());

        // Idempotent.
        var again = Isolation().Apply(_hive, Sid, "desk-1", Machine);
        Assert.False(again.EnvironmentWritten);
        Assert.Empty(again.Changes);
    }

    [Fact]
    public void Revert_removes_everything_it_created()
    {
        Isolation().Apply(_hive, Sid, "desk-1", Machine);
        var changes = Isolation().Revert(_hive);

        Assert.NotEmpty(changes);
        Assert.Empty(_hive.Values);
        Assert.False(_hive.KeyExists(@"Software\Classes\steam"));
        Assert.False(File.Exists(StateFile));
        Assert.Null(Isolation().RecordedSid());
        Assert.Empty(Isolation().Revert(_hive)); // nothing recorded: no-op
    }

    [Fact]
    public void Steam_autostart_gets_the_flag_and_revert_restores_it_exactly()
    {
        _hive.Set(SteamIsolation.RunKey, "Steam", Sz(RunValue));
        _hive.Set(SteamIsolation.RunKey, "OneDrive", Sz("onedrive.exe /background"));

        Isolation().Apply(_hive, Sid, "desk-1", Machine);
        Assert.Equal(Sz($"\"{SteamExe}\" -master_ipc_name_override SplitPlay_desk_1 -silent"), _hive.Get(SteamIsolation.RunKey, "Steam"));

        Isolation().Revert(_hive);
        Assert.Equal(Sz(RunValue), _hive.Get(SteamIsolation.RunKey, "Steam"));
        Assert.Equal(Sz("onedrive.exe /background"), _hive.Get(SteamIsolation.RunKey, "OneDrive"));
        Assert.True(_hive.KeyExists(SteamIsolation.RunKey));
    }

    [Fact]
    public void Autostart_written_again_by_Steam_is_fixed_at_the_next_apply()
    {
        Isolation().Apply(_hive, Sid, "desk-1", Machine);

        // The user ticks "Run Steam when my computer starts" in the desk: Steam writes its plain value.
        const string rewritten = "\"" + SteamExe + "\" -silent -nofriendsui";
        _hive.Set(SteamIsolation.RunKey, "Steam", Sz(rewritten));
        var result = Isolation().Apply(_hive, Sid, "desk-1", Machine);
        Assert.False(result.EnvironmentWritten);
        Assert.Single(result.Changes);
        Assert.Equal(Sz($"\"{SteamExe}\" -master_ipc_name_override SplitPlay_desk_1 -silent -nofriendsui"), _hive.Get(SteamIsolation.RunKey, "Steam"));

        Isolation().Revert(_hive);
        Assert.Equal(Sz(rewritten), _hive.Get(SteamIsolation.RunKey, "Steam"));
    }

    [Fact]
    public void Autostart_that_is_not_Steam_is_left_alone()
    {
        _hive.Set(SteamIsolation.RunKey, "Steam", Sz(@"C:\Tools\steam-helper.exe"));
        Isolation().Apply(_hive, Sid, "desk-1", Machine);
        Assert.Equal(Sz(@"C:\Tools\steam-helper.exe"), _hive.Get(SteamIsolation.RunKey, "Steam"));
    }

    [Fact]
    public void Previous_values_come_back_and_existing_keys_stay()
    {
        _hive.Set("Environment", "steam_master_ipc_name_override", Sz("Mine"));
        _hive.Set("Environment", "TEMP", new RegValue(RegKind.ExpandString, @"%USERPROFILE%\AppData\Local\Temp"));
        _hive.Set(@"Software\Classes\steam\Shell\Open\Command", "", Sz("\"D:\\Old\\steam.exe\" \"%1\""));

        var result = Isolation().Apply(_hive, Sid, "desk-1", Machine);
        Assert.True(result.EnvironmentWritten);

        Isolation().Revert(_hive);
        Assert.Equal(Sz("Mine"), _hive.Get("Environment", "steam_master_ipc_name_override"));
        Assert.Equal(Sz("\"D:\\Old\\steam.exe\" \"%1\""), _hive.Get(@"Software\Classes\steam\Shell\Open\Command", ""));
        Assert.Null(_hive.Get(@"Software\Classes\steam", "URL Protocol"));
        Assert.True(_hive.KeyExists(@"Software\Classes\steam\Shell\Open\Command"));
        Assert.NotNull(_hive.Get("Environment", "TEMP"));
    }

    [Fact]
    public void A_value_changed_since_is_left_as_is()
    {
        Isolation().Apply(_hive, Sid, "desk-1", Machine);
        _hive.Set("Environment", "steam_master_ipc_name_override", Sz("SomethingElse"));

        var changes = Isolation().Revert(_hive);
        Assert.Contains(changes, c => c.Contains("changed since", StringComparison.Ordinal));
        Assert.Equal(Sz("SomethingElse"), _hive.Get("Environment", "steam_master_ipc_name_override"));
    }

    [Fact]
    public void Without_Steam_installed_only_the_environment_is_written()
    {
        Isolation().Apply(_hive, Sid, "desk-1", SteamMachineInfo.None);
        Assert.NotNull(_hive.Get("Environment", "steam_master_ipc_name_override"));
        Assert.False(_hive.KeyExists(@"Software\Classes\steam"));

        // Steam installed later: the next apply adds the handler.
        Isolation().Apply(_hive, Sid, "desk-1", Machine);
        Assert.NotNull(_hive.Get(@"Software\Classes\steam\Shell\Open\Command", ""));
        Isolation().Revert(_hive);
        Assert.Empty(_hive.Values);
    }

    [Fact]
    public void Lost_state_still_reverts_the_values_it_finds()
    {
        Isolation().Apply(_hive, Sid, "desk-1", Machine);
        File.Delete(StateFile);

        var result = Isolation().Apply(_hive, Sid, "desk-1", Machine);
        Assert.False(result.EnvironmentWritten);
        Isolation().Revert(_hive);
        Assert.Empty(_hive.Values);
    }

    [Fact]
    public void Another_user_needs_a_revert_first()
    {
        Isolation().Apply(_hive, Sid, "desk-1", Machine);
        Assert.Throws<InvalidOperationException>(() => Isolation().Apply(new MemoryUserHive(), "S-1-5-21-9", "desk-1", Machine));
    }

    [Fact]
    public void Handler_goes_to_the_classes_hive_and_is_skipped_without_one()
    {
        var main = new MemoryUserHive();
        var classes = new MemoryUserHive();
        Isolation().Apply(new ClassesRoutedHive(main, classes), Sid, "desk-1", Machine);
        Assert.NotNull(main.Get("Environment", "steam_master_ipc_name_override"));
        Assert.Null(main.Get(@"Software\Classes\steam\Shell\Open\Command", ""));
        Assert.Contains("SplitPlay_desk_1", classes.Get(@"steam\Shell\Open\Command", "")!.Data, StringComparison.Ordinal);
        Assert.Equal(Sz(""), classes.Get("steam", "URL Protocol"));

        Isolation().Revert(new ClassesRoutedHive(main, classes));
        Assert.Empty(main.Values);
        Assert.Empty(classes.Values);
        Assert.False(classes.KeyExists("steam"));

        // A profile without UsrClass.dat (never logged on): only NTUSER.DAT values.
        var result = Isolation().Apply(new ClassesRoutedHive(main, null), Sid, "desk-1", Machine);
        Assert.True(result.EnvironmentWritten);
        Assert.Single(main.Values);
        Isolation().Revert(new ClassesRoutedHive(main, null));
        Assert.Empty(main.Values);
    }

    // ------------------------------------------------------------------ apps.json

    private string AppsFile => Path.Combine(_dir, "desks", "desk-1", SteamApps.FileName);

    private JsonArray Apps() => (JsonArray)JsonNode.Parse(File.ReadAllText(AppsFile))!["apps"]!;

    [Fact]
    public void Apps_json_is_created_with_Desktop_and_the_isolated_Steam()
    {
        Assert.True(SteamApps.Ensure(AppsFile, "desk-1", SteamExe, enabled: true));
        var apps = Apps();
        Assert.Equal(["Desktop", "Steam"], apps.Select(a => (string)a!["name"]!));
        Assert.Equal($"\"{SteamExe}\" -master_ipc_name_override SplitPlay_desk_1 -gamepadui", (string)apps[1]!["cmd"]!);
        Assert.True((bool)apps[1]!["auto-detach"]!);
        Assert.Contains("\\\"C:\\\\Program Files (x86)", File.ReadAllText(AppsFile), StringComparison.Ordinal);

        Assert.False(SteamApps.Ensure(AppsFile, "desk-1", SteamExe, enabled: true));
    }

    [Fact]
    public void Apps_json_keeps_other_apps_and_the_owners_own_Steam_entry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppsFile)!);
        File.WriteAllText(AppsFile, """{"env":{"X":"1"},"apps":[{"name":"Desktop"},{"name":"Steam","cmd":"steam://open/bigpicture"},{"name":"Game","cmd":"game.exe"}]}""");

        Assert.False(SteamApps.Ensure(AppsFile, "desk-1", SteamExe, enabled: true));
        Assert.False(SteamApps.Ensure(AppsFile, "desk-1", SteamExe, enabled: false));
        Assert.Equal("steam://open/bigpicture", (string)Apps()[1]!["cmd"]!);
    }

    [Fact]
    public void Apps_json_entry_is_updated_and_removed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppsFile)!);
        File.WriteAllText(AppsFile, """{"env":{"X":"1"},"apps":[{"name":"Desktop"},{"name":"Game","cmd":"game.exe"}]}""");
        Assert.True(SteamApps.Ensure(AppsFile, "desk-1", SteamExe, enabled: true));
        Assert.True(SteamApps.Ensure(AppsFile, "desk-1", @"D:\Steam\steam.exe", enabled: true));
        Assert.Equal(["Desktop", "Game", "Steam"], Apps().Select(a => (string)a!["name"]!));
        Assert.StartsWith("\"D:\\Steam\\steam.exe\"", (string)Apps()[2]!["cmd"]!, StringComparison.Ordinal);

        Assert.True(SteamApps.Ensure(AppsFile, "desk-1", null, enabled: true)); // Steam uninstalled
        Assert.Equal(["Desktop", "Game"], Apps().Select(a => (string)a!["name"]!));
        Assert.Equal("1", (string)JsonNode.Parse(File.ReadAllText(AppsFile))!["env"]!["X"]!);
    }

    [Fact]
    public void Apps_json_is_not_created_when_isolation_is_off()
    {
        Assert.False(SteamApps.Ensure(AppsFile, "desk-1", SteamExe, enabled: false));
        Assert.False(File.Exists(AppsFile));
    }

    [Fact]
    public void Broken_apps_json_is_reported_and_left_alone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppsFile)!);
        File.WriteAllText(AppsFile, "{ not json");
        Assert.Throws<InvalidDataException>(() => SteamApps.Ensure(AppsFile, "desk-1", SteamExe, enabled: true));
        Assert.Equal("{ not json", File.ReadAllText(AppsFile));
    }
}
