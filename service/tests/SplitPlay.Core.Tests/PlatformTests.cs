// SPDX-License-Identifier: MIT
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Platform;
using SplitPlay.Core.Ports;

namespace SplitPlay.Core.Tests;

public sealed class PlatformTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData(@"C:\Program Files\SplitPlay\worker")]
    [InlineData(@"C:\ProgramData\SplitPlay\desks\desk-1\")]
    [InlineData("quote\"inside")]
    [InlineData("trailing backslash\\")]
    [InlineData("back\\\\\"slash quote")]
    [InlineData("")]
    [InlineData("name=value with spaces")]
    [InlineData(@"\\.\pipe\splitplay\worker-desk-1")]
    public void Command_line_round_trips_like_CommandLineToArgvW(string argument)
    {
        var line = WindowsCommandLine.Build(@"C:\Program Files\SplitPlay\worker\splitplay-worker.exe", ["--desk", "d1", argument, "port=48089"]);
        var parsed = WindowsCommandLine.Parse(line);
        Assert.Equal([@"C:\Program Files\SplitPlay\worker\splitplay-worker.exe", "--desk", "d1", argument, "port=48089"], parsed);
    }

    [Fact]
    public void Simple_arguments_are_not_quoted()
    {
        Assert.Equal(@"""C:\x.exe"" --pipe \\.\pipe\splitplay\seat-d port=1", WindowsCommandLine.Build(@"C:\x.exe", ["--pipe", @"\\.\pipe\splitplay\seat-d", "port=1"]));
    }

    [Fact]
    public void Passwords_are_long_random_and_complex()
    {
        var passwords = Enumerable.Range(0, 200).Select(_ => PasswordGenerator.Generate()).ToList();
        Assert.All(passwords, p =>
        {
            Assert.Equal(32, p.Length);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => !char.IsLetterOrDigit(c));
            Assert.DoesNotContain(p, char.IsWhiteSpace);
        });
        Assert.Equal(passwords.Count, passwords.Distinct().Count());
    }

    [Fact]
    public void Desk_ports_match_the_contract()
    {
        Assert.Equal([48084, 48089, 48110], BasePortAllocator.TcpPorts(48089));
        Assert.Equal([48098, 48099, 48100], BasePortAllocator.UdpPorts(48089));
    }

    [Fact]
    public void Worker_settings_are_validated()
    {
        var host = new HostConfig();
        var ok = new DeskConfig { Id = "d", WorkerSettings = new() { ["min_log_level"] = "warning", ["encoder"] = "nvenc" } };
        Assert.Empty(ConfigValidator.ValidateDesk(ok, host));

        Assert.Contains(ConfigValidator.ValidateDesk(new DeskConfig { Id = "d", WorkerSettings = new() { ["port"] = "1" } }, host),
            e => e.Path.EndsWith("port", StringComparison.Ordinal));
        Assert.NotEmpty(ConfigValidator.ValidateDesk(new DeskConfig { Id = "d", WorkerSettings = new() { ["bad name"] = "x" } }, host));
        Assert.NotEmpty(ConfigValidator.ValidateDesk(new DeskConfig { Id = "d", WorkerSettings = new() { ["x"] = "line\nbreak" } }, host));
    }

    [Fact]
    public void Desk_defaults_and_round_trip()
    {
        var parsed = ConfigStore.Parse("""{"schemaVersion":1,"desks":[{"id":"a"},{"id":"b","disableBluetoothUserService":false,"workerSettings":{"k":"v w"}}]}""").Config;
        Assert.True(parsed.Desks[0].DisableBluetoothUserService);
        Assert.Null(parsed.Desks[0].WorkerSettings);
        Assert.False(parsed.Desks[1].DisableBluetoothUserService);
        Assert.Equal("v w", parsed.Desks[1].WorkerSettings!["k"]);
        var again = ConfigStore.Parse(ConfigStore.Serialize(parsed)).Config;
        Assert.Equal("v w", again.Desks[1].WorkerSettings!["k"]);
    }

    [Fact]
    public void Seat_messages_round_trip_and_never_print_the_password()
    {
        var welcome = new WelcomeMessage
        {
            Profile = new IpcProfile { Fps = 120 },
            Rdp = new IpcRdpTarget { Host = "127.0.0.2", Port = 3390, User = "splitplay desk1", Domain = ".", Password = "p@ss\"w0rd\\", Width = 1920, Height = 1080 },
        };
        var json = IpcCodec.EncodeToString(welcome);
        Assert.StartsWith("""{"type":"welcome","protocol":1,"profile":{"fps":120},"rdp":{"host":"127.0.0.2","port":3390,"user":"splitplay desk1","domain":".",""", json,
            StringComparison.Ordinal);
        var back = Assert.IsType<WelcomeMessage>(IpcCodec.Decode(json));
        Assert.Equal("p@ss\"w0rd\\", back.Rdp!.Password);
        Assert.DoesNotContain("p@ss", welcome.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("p@ss", back.Rdp.ToString(), StringComparison.Ordinal);

        // The worker's welcome has no rdp object.
        Assert.DoesNotContain("rdp", IpcCodec.EncodeToString(new WelcomeMessage()), StringComparison.Ordinal);

        Assert.Equal("""{"type":"set_mode","id":4,"width":2560,"height":1440}""",
            IpcCodec.EncodeToString(new SetModeMessage { Id = 4, Width = 2560, Height = 1440 }));
        var ready = Assert.IsType<ReadyMessage>(IpcCodec.Decode("""{"type":"ready","input_ring":"Global\\SplitPlay.InputRing.d","connector":"freerdp","session":3}"""));
        Assert.Equal((3, "freerdp"), (ready.Session, ready.Connector));
    }
}
