// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using SplitPlay.Core.Json;
using SplitPlay.Service.Hosting;

namespace SplitPlay.Service.Tests;

/// <summary><c>GET /host/network</c>: the LAN addresses the UI shows for Moonlight.</summary>
public sealed class HostNetworkTests
{
    [Fact]
    public void Select_keeps_lan_ipv4_and_puts_gateway_interfaces_first()
    {
        var picked = HostNetwork.Select(
        [
            new HostNetwork.Candidate(IPAddress.Parse("10.8.0.2"), HasGateway: false),
            new HostNetwork.Candidate(IPAddress.Loopback, HasGateway: false),
            new HostNetwork.Candidate(IPAddress.Parse("169.254.12.7"), HasGateway: false),
            new HostNetwork.Candidate(IPAddress.Parse("fe80::1"), HasGateway: true),
            new HostNetwork.Candidate(IPAddress.Parse("2001:db8::5"), HasGateway: true),
            new HostNetwork.Candidate(IPAddress.Parse("192.168.1.20"), HasGateway: true),
            new HostNetwork.Candidate(IPAddress.Parse("192.168.1.20"), HasGateway: true),
            new HostNetwork.Candidate(IPAddress.Parse("172.20.0.1"), HasGateway: false),
        ]);

        Assert.Equal(new[] { "192.168.1.20", "10.8.0.2", "172.20.0.1" }, picked);
    }

    [Fact]
    public void Select_of_nothing_is_empty()
    {
        Assert.Empty(HostNetwork.Select([]));
    }

    [Fact]
    public async Task Host_network_answers_with_ipv4_addresses_only()
    {
        await using var svc = await TestService.StartAsync();
        var raw = await svc.Client.GetStringAsync("/api/v1/host/network");
        Assert.StartsWith("{\"addresses\":[", raw, StringComparison.Ordinal);

        var network = await svc.Client.GetFromJsonAsync("/api/v1/host/network", SplitPlayJsonContext.Default.HostNetworkDto);
        Assert.NotNull(network);
        foreach (var text in network.Addresses)
        {
            var address = IPAddress.Parse(text);
            Assert.Equal(AddressFamily.InterNetwork, address.AddressFamily);
            Assert.False(IPAddress.IsLoopback(address), text);
            Assert.False(address.GetAddressBytes() is [169, 254, ..], text);
        }
    }
}
