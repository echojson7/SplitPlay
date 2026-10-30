// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Ipc;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Hosting;
using SplitPlay.Service.Platform;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Tests;

/// <summary>Several desks on one PC: desk names for Moonlight and mDNS, busy ports, firewall rules, host network status.</summary>
public sealed class MultiDeskNetworkTests
{
    // ------------------------------------------------------------------ sunshine_name

    [Theory]
    [InlineData("desk-1", null, "GAMEPC-desk-1")]
    [InlineData("desk-1", "", "GAMEPC-desk-1")]
    [InlineData("desk-1", "Skyrim box", "GAMEPC-Skyrim box")]
    [InlineData("desk-2", "  Стол \t девушки  ", "GAMEPC-Стол девушки")]
    [InlineData("desk-3", "A#1\u0001", "GAMEPC-A1")]
    [InlineData("desk-4", "###", "GAMEPC-desk-4")]
    public void Sunshine_name_is_the_pc_name_and_the_desk_name(string id, string? name, string expected)
    {
        Assert.Equal(expected, DeskSupervisor.SunshineName("GAMEPC", id, name));
    }

    [Fact]
    public void The_worker_gets_the_desk_name_unless_the_desk_overrides_it()
    {
        var named = DeskSupervisor.WorkerSettings("desk-1", new DeskConfig { Id = "desk-1", Name = "Living room" }).ToDictionary();
        Assert.Equal($"{Environment.MachineName}-Living room", named["sunshine_name"]);

        var overridden = DeskSupervisor.WorkerSettings("desk-1", new DeskConfig
        {
            Id = "desk-1",
            Name = "Living room",
            WorkerSettings = new() { ["sunshine_name"] = "Mine" },
        }).ToDictionary();
        Assert.Equal("Mine", overridden["sunshine_name"]);
    }

    // ------------------------------------------------------------------ owner tables

    private static byte[] ListenerTableV4(params (int Port, uint Pid)[] rows)
    {
        var table = new byte[4 + (rows.Length * TcpOwnerTable.RowSizeV4)];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = table.AsSpan(4 + (i * TcpOwnerTable.RowSizeV4));
            BinaryPrimitives.WriteUInt32LittleEndian(row, 2); // LISTEN
            BinaryPrimitives.WriteUInt16BigEndian(row[8..], (ushort)rows[i].Port);
            BinaryPrimitives.WriteUInt32LittleEndian(row[20..], rows[i].Pid);
        }

        return table;
    }

    private static byte[] ListenerTableV6(int port, uint pid)
    {
        var table = new byte[4 + TcpOwnerTable.RowSizeV6];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
        var row = table.AsSpan(4);
        BinaryPrimitives.WriteUInt16BigEndian(row[20..], (ushort)port);
        BinaryPrimitives.WriteUInt32LittleEndian(row[48..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(row[52..], pid);
        return table;
    }

    private static byte[] UdpTable(bool v6, params (int Port, uint Pid)[] rows)
    {
        var size = v6 ? UdpOwnerTable.RowSizeV6 : UdpOwnerTable.RowSizeV4;
        var table = new byte[4 + (rows.Length * size)];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = table.AsSpan(4 + (i * size));
            BinaryPrimitives.WriteUInt16BigEndian(row[(v6 ? 20 : 4)..], (ushort)rows[i].Port);
            BinaryPrimitives.WriteUInt32LittleEndian(row[(v6 ? 24 : 8)..], rows[i].Pid);
        }

        return table;
    }

    [Fact]
    public void Listener_and_udp_tables_name_the_owner_of_a_local_port()
    {
        var tcp = ListenerTableV4((80, 4), (48089, 777));
        Assert.Equal(777u, TcpOwnerTable.FindListener(tcp, false, 48089));
        Assert.Equal(4u, TcpOwnerTable.FindListener(tcp, false, 80));
        Assert.Null(TcpOwnerTable.FindListener(tcp, false, 48084));
        Assert.Equal(55u, TcpOwnerTable.FindListener(ListenerTableV6(48110, 55), true, 48110));

        Assert.Equal(9u, UdpOwnerTable.FindOwner(UdpTable(false, (5353, 1), (48098, 9)), false, 48098));
        Assert.Equal(10u, UdpOwnerTable.FindOwner(UdpTable(true, (48099, 10)), true, 48099));
        Assert.Null(UdpOwnerTable.FindOwner(UdpTable(false, (48098, 9)), false, 48099));

        var malformed = UdpTable(false, (48098, 9));
        BinaryPrimitives.WriteUInt32LittleEndian(malformed, 99);
        Assert.Null(UdpOwnerTable.FindOwner(malformed, false, 48098));
        Assert.Null(TcpOwnerTable.FindListener([], false, 1));
    }

    [Fact]
    public void Port_check_names_the_first_busy_port_and_its_program()
    {
        static string Name(uint pid) => pid == 7 ? "sunshine.exe" : "other.exe";

        Assert.Null(PortCheck.Find(48089, _ => null, _ => null, _ => false, _ => false, Name));

        var busy = PortCheck.Find(48089, p => p == 48089 ? 7u : null, _ => null, _ => false, _ => false, Name);
        Assert.Equal("Port 48089 busy: sunshine.exe", busy!.Message);

        var udp = PortCheck.Find(48089, _ => null, p => p == 48099 ? 8u : null, _ => true, _ => false, Name);
        Assert.Equal("Port 48099 busy: other.exe", udp!.Message);

        // Owners are named before a port Windows merely refuses (an excluded range).
        var reserved = PortCheck.Find(48089, _ => null, _ => null, _ => false, p => p == 48100, Name);
        Assert.Equal("Port 48100 reserved by Windows", reserved!.Message);
        Assert.Equal("UDP", reserved.Protocol);
    }

    // ------------------------------------------------------------------ firewall rules

    private const string Program = @"C:\Program Files\SplitPlay\worker\splitplay-worker.exe";

    private static string DeskRule(string name = "SplitPlay desk desk-1 (TCP)", string active = "TRUE", string ports = "LPort=48084|LPort=48089|LPort=48110",
        string profiles = "Profile=Domain|Profile=Private") =>
        $"v2.31|Action=Allow|Active={active}|Dir=In|Protocol=6|{profiles}|{ports}|App={Program}|Name={name}|Desc=SplitPlay desk desk-1: Moonlight|";

    [Fact]
    public void Desk_rules_are_matched_by_name_ports_program_and_profiles()
    {
        int[] ports = [48084, 48089, 48110];
        const string name = "SplitPlay desk desk-1 (TCP)";
        Assert.Equal(FirewallRules.DeskRuleState.Match, FirewallRules.DeskRule([DeskRule()], name, FirewallRules.Tcp, ports, Program));
        Assert.Equal(FirewallRules.DeskRuleState.Match,
            FirewallRules.DeskRule([DeskRule(ports: "LPort=48084,48089,48110")], name, FirewallRules.Tcp, ports, Program));
        Assert.Equal(FirewallRules.DeskRuleState.Match,
            FirewallRules.DeskRule([DeskRule().Replace(Program, Program.ToUpperInvariant(), StringComparison.Ordinal)], name, FirewallRules.Tcp, ports, Program));

        Assert.Equal(FirewallRules.DeskRuleState.Missing, FirewallRules.DeskRule([], name, FirewallRules.Tcp, ports, Program));
        Assert.Equal(FirewallRules.DeskRuleState.Missing,
            FirewallRules.DeskRule([DeskRule("SplitPlay desk desk-2 (TCP)")], name, FirewallRules.Tcp, ports, Program));
        Assert.Equal(FirewallRules.DeskRuleState.Different,
            FirewallRules.DeskRule([DeskRule(active: "FALSE")], name, FirewallRules.Tcp, ports, Program));
        Assert.Equal(FirewallRules.DeskRuleState.Different,
            FirewallRules.DeskRule([DeskRule()], name, FirewallRules.Tcp, [48184, 48189, 48210], Program));
        Assert.Equal(FirewallRules.DeskRuleState.Different,
            FirewallRules.DeskRule([DeskRule(profiles: "Profile=Private")], name, FirewallRules.Tcp, ports, Program));
        Assert.Equal(FirewallRules.DeskRuleState.Different,
            FirewallRules.DeskRule([DeskRule(), DeskRule()], name, FirewallRules.Tcp, ports, Program));
        Assert.Equal(FirewallRules.DeskRuleState.Different,
            FirewallRules.DeskRule([DeskRule()], name, FirewallRules.Udp, ports, Program));
    }

    private const string MdnsPrivate =
        @"v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=17|Profile=Private|LPort=5353|App=%SystemRoot%\system32\svchost.exe|Svc=dnscache|Name=@%SystemRoot%\system32\firewallapi.dll,-37303|EmbedCtxt=@%SystemRoot%\system32\firewallapi.dll,-37302|";

    [Fact]
    public void Mdns_is_allowed_by_an_enabled_rule_for_the_profile_and_blocked_otherwise()
    {
        Assert.True(FirewallRules.MdnsAllowed([MdnsPrivate], "Private", firewallEnabled: true));
        Assert.False(FirewallRules.MdnsAllowed([MdnsPrivate], "Public", firewallEnabled: true));
        Assert.False(FirewallRules.MdnsAllowed([MdnsPrivate.Replace("Active=TRUE", "Active=FALSE", StringComparison.Ordinal)], "Private", true));
        Assert.False(FirewallRules.MdnsAllowed([], "Private", firewallEnabled: true));
        Assert.True(FirewallRules.MdnsAllowed([], "Public", firewallEnabled: false));

        // Another program's rule for 5353 does not reach Windows' responder; a rule for any program does.
        Assert.False(FirewallRules.MdnsAllowed(
            [@"v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=17|LPort=5353|App=C:\Program Files\Bonjour\mDNSResponder.exe|Name=Bonjour|"], "Private", true));
        Assert.True(FirewallRules.MdnsAllowed(["v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=17|LPort=5000-6000|Name=Any|"], "Public", true));

        // A block rule wins.
        Assert.False(FirewallRules.MdnsAllowed(
            [MdnsPrivate, "v2.31|Action=Block|Active=TRUE|Dir=In|Protocol=17|LPort=5353|Name=No mDNS|"], "Private", true));
    }

    [Fact]
    public void The_lan_profile_is_that_of_interfaces_with_a_gateway_and_the_worst_wins()
    {
        // Hyper-V / WSL switches sit on an unidentified public network without a gateway: ignored.
        Assert.Equal("Private", NetworkProfiles.Pick([new(true, 1), new(false, 0), new(false, null)]));
        Assert.Equal("Public", NetworkProfiles.Pick([new(true, 1), new(true, 0)]));
        Assert.Equal("Domain", NetworkProfiles.Pick([new(true, 2)]));
        Assert.Equal("Public", NetworkProfiles.Pick([new(false, 0), new(false, 1)]));
        Assert.Null(NetworkProfiles.Pick([new(true, null)]));
        Assert.Null(NetworkProfiles.Pick([]));
    }

    // ------------------------------------------------------------------ supervisor

    [Fact]
    public async Task A_busy_port_at_start_faults_the_desk_with_the_port_and_program()
    {
        await using var svc = await TestService.StartAsync();
        await svc.CreateDeskAsync("desk-1");
        svc.Platform.PortConflicts["desk-1"] = new PortConflict(48089, "TCP", "sunshine.exe");
        await svc.PostDeskActionAsync("desk-1", "start");

        var faulted = await svc.WaitForStateAsync("desk-1", DeskState.Faulted);
        Assert.Equal("Port 48089 busy: sunshine.exe", faulted.LastError);
        Assert.Equal(0, svc.Launcher.LaunchesOf(IpcProtocol.Roles.Worker));
        Assert.Equal(0, svc.Launcher.LaunchesOf(IpcProtocol.Roles.Seat));

        await svc.PostDeskActionAsync("desk-1", "stop");
        await svc.WaitForStateAsync("desk-1", DeskState.Stopped);
        svc.Platform.PortConflicts.Clear();
        await svc.PostDeskActionAsync("desk-1", "start");
        Assert.Null((await svc.WaitForStateAsync("desk-1", DeskState.Ready)).LastError);
    }

    [Fact]
    public async Task A_port_taken_while_the_desk_runs_stops_the_worker_restart_with_a_clear_error()
    {
        await using var svc = await TestService.StartAsync(["--Supervisor:PortBusyGraceMs=200"],
            setup: l => l.Behavior = r => r.Role == IpcProtocol.Roles.Worker && r.Attempt == 1 ? FakeChildBehavior.ExitAfterReady : FakeChildBehavior.Normal);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);
        svc.Platform.PortConflicts["desk-1"] = new PortConflict(48099, "UDP", "obs64.exe");

        // The first worker crashes; the restart finds the port taken and does not launch a worker.
        var blocked = await svc.WaitForAsync("desk-1", d => d.LastError == "Port 48099 busy: obs64.exe", what: "the port error");
        Assert.NotEqual(DeskState.Ready, blocked.State);
        var launches = svc.Launcher.LaunchesOf(IpcProtocol.Roles.Worker);

        svc.Platform.PortConflicts.Clear();
        var ready = await svc.WaitForStateAsync("desk-1", DeskState.Ready, TimeSpan.FromSeconds(30));
        Assert.Null(ready.LastError);
        Assert.Equal(launches + 1, svc.Launcher.LaunchesOf(IpcProtocol.Roles.Worker));
    }

    [Fact]
    public async Task Firewall_rules_are_ensured_at_start_and_before_every_worker_start()
    {
        await using var svc = await TestService.StartAsync(
            setup: l => l.Behavior = r => r.Role == IpcProtocol.Roles.Worker && r.Attempt == 1 ? FakeChildBehavior.ExitAfterReady : FakeChildBehavior.Normal);
        await svc.CreateDeskAsync("desk-1");
        await svc.PostDeskActionAsync("desk-1", "start");
        await svc.WaitForAsync("desk-1", _ => svc.Launcher.LaunchesOf(IpcProtocol.Roles.Worker) >= 2, what: "a worker restart");
        await svc.WaitForStateAsync("desk-1", DeskState.Ready);

        // Provisioning, then once before each of the two workers.
        Assert.True(svc.Platform.Actions.Count(a => a == "firewall+ desk-1 48089") >= 3, string.Join(", ", svc.Platform.Actions));
    }

    // ------------------------------------------------------------------ GET /host/network

    [Fact]
    public async Task Host_network_reports_the_profile_and_mdns_state()
    {
        await using var svc = await TestService.StartAsync();
        var unknown = await svc.Client.GetFromJsonAsync("/api/v1/host/network", SplitPlayJsonContext.Default.HostNetworkDto);
        Assert.Null(unknown!.Profile);
        Assert.Null(unknown.MdnsAllowed);

        svc.App.Services.GetRequiredService<FakeHostNetworkStatus>().State = new HostNetworkState("Public", false);
        var raw = await svc.Client.GetStringAsync("/api/v1/host/network");
        Assert.Contains("\"profile\":\"Public\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"mdnsAllowed\":false", raw, StringComparison.Ordinal);
    }
}
