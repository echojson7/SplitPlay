// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using SplitPlay.Core.Configuration;

namespace SplitPlay.Core.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "splitplay-tests-" + Guid.NewGuid().ToString("N"));

    public ConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>The example from docs/contracts/config.md.</summary>
    private const string ContractExample = """
        {
          "schemaVersion": 1,
          "host": {
            "webPort": 38299,
            "lanAccess": false,
            "basePortStart": 48089,
            "basePortStride": 100,
            "composeIntervalMs": 4,
            "logLevel": "info"
          },
          "desks": [
            {
              "id": "desk-1",
              "name": "Skyrim box",
              "user": "splitplay-desk1",
              "basePort": 48089,
              "autostart": true,
              "gpu": "auto",
              "profile": {
                "fps": 120,
                "width": 1920,
                "height": 1080,
                "bitrateKbps": 60000,
                "codec": "hevc",
                "fecPercent": 20,
                "minFps": 15,
                "dpbFrames": 16,
                "pacingMbps": 800,
                "gpuPriority": "high"
              }
            }
          ]
        }
        """;

    [Fact]
    public void Default_config_is_valid()
    {
        Assert.Empty(ConfigValidator.Validate(new SplitPlayConfig()));
    }

    [Fact]
    public void Contract_example_parses_and_validates()
    {
        var result = ConfigStore.Parse(ContractExample);
        Assert.Null(result.MigratedFrom);
        Assert.False(result.DefaultsApplied);
        var desk = Assert.Single(result.Config.Desks);
        Assert.Equal("desk-1", desk.Id);
        Assert.Equal(48089, desk.BasePort);
        Assert.Equal("hevc", desk.Profile.Codec);
        Assert.Equal(120, desk.Profile.Fps);
        Assert.Equal("high", desk.Profile.GpuPriority);
        Assert.Equal(38299, result.Config.Host.WebPort);
    }

    [Fact]
    public void Serialize_round_trips_and_uses_camel_case()
    {
        var config = ConfigStore.Parse(ContractExample).Config;
        var json = ConfigStore.Serialize(config);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"bitrateKbps\": 60000", json, StringComparison.Ordinal);
        Assert.DoesNotContain("gpuLuid", json, StringComparison.Ordinal);
        var again = ConfigStore.Parse(json).Config;
        Assert.Equal(json, ConfigStore.Serialize(again));
    }

    [Theory]
    [InlineData("Desk-1")]
    [InlineData("desk_1")]
    [InlineData("")]
    [InlineData("a-very-long-desk-id-that-exceeds-32")]
    public void Invalid_desk_ids_are_rejected(string id)
    {
        var config = OneDesk(d => d.Id = id);
        Assert.Contains(ConfigValidator.Validate(config), e => e.Path == "desks[0].id");
    }

    [Theory]
    [InlineData(9)]
    [InlineData(501)]
    public void Fps_outside_10_to_500_is_rejected(int fps)
    {
        var config = OneDesk(d => d.Profile.Fps = fps);
        Assert.Contains(ConfigValidator.Validate(config), e => e.Path == "desks[0].profile.fps");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(500)]
    public void Fps_bounds_are_accepted(int fps)
    {
        Assert.Empty(ConfigValidator.Validate(OneDesk(d => d.Profile.Fps = fps)));
    }

    [Fact]
    public void Codec_gpu_priority_and_gpu_are_checked()
    {
        var config = OneDesk(d =>
        {
            d.Profile.Codec = "vp9";
            d.Profile.GpuPriority = "max";
            d.Gpu = "nvidia";
        });
        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Path == "desks[0].profile.codec");
        Assert.Contains(errors, e => e.Path == "desks[0].profile.gpuPriority");
        Assert.Contains(errors, e => e.Path == "desks[0].gpu");
    }

    [Fact]
    public void Adapter_luid_is_accepted_for_gpu()
    {
        Assert.Empty(ConfigValidator.Validate(OneDesk(d => d.Gpu = "0x0000000000012345")));
    }

    [Fact]
    public void Duplicate_ids_users_and_overlapping_ports_are_rejected()
    {
        var config = new SplitPlayConfig
        {
            Desks =
            [
                new DeskConfig { Id = "a", User = "user-a", BasePort = 48089 },
                new DeskConfig { Id = "a", User = "USER-A", BasePort = 48100 },
            ],
        };
        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Path == "desks[1].id");
        Assert.Contains(errors, e => e.Path == "desks[1].user");
        Assert.Contains(errors, e => e.Path == "desks[1].basePort");
    }

    [Fact]
    public void Desk_range_must_not_contain_the_web_port()
    {
        var config = OneDesk(d => d.BasePort = 38300);
        Assert.Contains(ConfigValidator.Validate(config), e => e.Path == "desks[0].basePort");
    }

    [Theory]
    [InlineData("bad\\name")]
    [InlineData("name:with:colons")]
    [InlineData("this-name-is-far-too-long")]
    [InlineData("...")]
    public void Invalid_user_names_are_rejected(string user)
    {
        Assert.Contains(ConfigValidator.Validate(OneDesk(d => d.User = user)), e => e.Path == "desks[0].user");
    }

    [Fact]
    public void Host_values_are_checked()
    {
        var config = new SplitPlayConfig
        {
            Host = new HostConfig { WebPort = 0, BasePortStride = 10, ComposeIntervalMs = 0, LogLevel = "loud" },
        };
        var paths = ConfigValidator.Validate(config).Select(e => e.Path).ToList();
        Assert.Contains("host.webPort", paths);
        Assert.Contains("host.basePortStride", paths);
        Assert.Contains("host.composeIntervalMs", paths);
        Assert.Contains("host.logLevel", paths);
    }

    [Fact]
    public void Missing_values_get_defaults()
    {
        var json = """
            {"schemaVersion":1,"desks":[{"id":"desk-1"},{"id":"desk-2","basePort":48089},{"id":"gaming"}]}
            """;
        var result = ConfigStore.Parse(json);
        Assert.True(result.DefaultsApplied);
        var desks = result.Config.Desks;
        // desk-2 holds 48089, so desk-1 takes the next slot and gaming the one after.
        Assert.Equal(48189, desks[0].BasePort);
        Assert.Equal(48089, desks[1].BasePort);
        Assert.Equal(48289, desks[2].BasePort);
        Assert.Equal("splitplay-desk1", desks[0].User);
        Assert.Equal("splitplay-gaming", desks[2].User);
        Assert.Equal("desk-1", desks[0].Name);
    }

    [Fact]
    public void Default_user_names_are_truncated_and_made_unique()
    {
        Assert.Equal("splitplay-averylongd", ConfigStore.DefaultUserName("a-very-long-desk-id", []));
        Assert.Equal("splitplay-averylong2", ConfigStore.DefaultUserName("a-very-long-desk-id", ["splitplay-averylongd"]));
    }

    [Fact]
    public void Newer_schema_is_refused()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigStore.Parse("""{"schemaVersion":2,"desks":[]}"""));
        Assert.Equal(ConfigException.Codes.SchemaTooNew, ex.Code);
    }

    [Fact]
    public void Malformed_json_is_reported()
    {
        Assert.Equal(ConfigException.Codes.Malformed, Assert.Throws<ConfigException>(() => ConfigStore.Parse("{not json")).Code);
        Assert.Equal(ConfigException.Codes.Malformed, Assert.Throws<ConfigException>(() => ConfigStore.Parse("[]")).Code);
        Assert.Equal(ConfigException.Codes.Malformed,
            Assert.Throws<ConfigException>(() => ConfigStore.Parse("""{"schemaVersion":1,"host":{"webPort":"x"}}""")).Code);
    }

    [Fact]
    public void Invalid_values_are_reported_with_all_errors()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            ConfigStore.Parse("""{"schemaVersion":1,"desks":[{"id":"X","profile":{"fps":1000,"codec":"vp8"}}]}"""));
        Assert.Equal(ConfigException.Codes.Invalid, ex.Code);
        Assert.Equal(3, ex.Errors.Count);
    }

    [Fact]
    public void Version_0_is_migrated()
    {
        var v0 = """
            {
              "host": { "webPort": 38299, "basePort": 50000 },
              "desktops": [ { "id": "old-desk", "profile": { "fps": 60 } } ]
            }
            """;
        var result = ConfigStore.Parse(v0);
        Assert.Equal(0, result.MigratedFrom);
        Assert.Equal(SplitPlayConfig.CurrentSchemaVersion, result.Config.SchemaVersion);
        Assert.Equal(50000, result.Config.Host.BasePortStart);
        var desk = Assert.Single(result.Config.Desks);
        Assert.Equal("old-desk", desk.Id);
        Assert.Equal(50000, desk.BasePort);
    }

    [Fact]
    public void Migrator_steps_are_applied_in_place()
    {
        var doc = (JsonObject)JsonNode.Parse("""{"desktops":[]}""")!;
        Assert.Equal(0, ConfigMigrator.Migrate(doc));
        Assert.Equal(1, (int)doc["schemaVersion"]!);
        Assert.NotNull(doc["desks"]);
        Assert.Null(doc["desktops"]);
        Assert.NotNull(doc["host"]);
    }

    [Fact]
    public void LoadOrCreate_writes_defaults_when_missing()
    {
        var path = Path.Combine(_dir, "config.json");
        var result = ConfigStore.LoadOrCreate(path);
        Assert.True(File.Exists(path));
        Assert.Empty(result.Config.Desks);
        Assert.Equal(38299, ConfigStore.Parse(File.ReadAllText(path)).Config.Host.WebPort);
    }

    [Fact]
    public void LoadOrCreate_rewrites_migrated_file_and_keeps_a_backup()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{"desktops":[{"id":"a"}]}""");
        var result = ConfigStore.LoadOrCreate(path);
        Assert.Equal(0, result.MigratedFrom);
        Assert.True(File.Exists(path + ".v0.bak"));
        var rewritten = ConfigStore.Parse(File.ReadAllText(path));
        Assert.Null(rewritten.MigratedFrom);
        Assert.False(rewritten.DefaultsApplied);
        Assert.Equal(48089, rewritten.Config.Desks[0].BasePort);
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temp_files()
    {
        var path = Path.Combine(_dir, "nested", "config.json");
        ConfigStore.Save(path, OneDesk(_ => { }));
        ConfigStore.Save(path, OneDesk(d => d.Name = "second"));
        Assert.Equal("second", ConfigStore.Parse(File.ReadAllText(path)).Config.Desks[0].Name);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Save_refuses_invalid_config_and_keeps_the_old_file()
    {
        var path = Path.Combine(_dir, "config.json");
        ConfigStore.Save(path, OneDesk(_ => { }));
        var before = File.ReadAllText(path);
        Assert.Throws<ConfigException>(() => ConfigStore.Save(path, OneDesk(d => d.Profile.Fps = 1)));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void DeepClone_copies_nested_objects()
    {
        var config = OneDesk(_ => { });
        var clone = config.DeepClone();
        clone.Desks[0].Profile.Fps = 30;
        Assert.Equal(120, config.Desks[0].Profile.Fps);
    }

    private static SplitPlayConfig OneDesk(Action<DeskConfig> change)
    {
        var desk = new DeskConfig
        {
            Id = "desk-1",
            Name = "Desk",
            User = "splitplay-desk1",
            BasePort = 48089,
            Profile = new ProfileConfig { Fps = 120, Codec = "hevc" },
        };
        change(desk);
        return new SplitPlayConfig { Desks = [desk] };
    }
}
