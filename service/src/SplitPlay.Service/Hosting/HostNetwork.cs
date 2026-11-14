// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SplitPlay.Service.Hosting;

/// <summary>
/// LAN IPv4 addresses of this PC for <c>GET /host/network</c>: the UI shows them as the address to add in Moonlight.
/// Only interfaces that are up count; loopback and link-local (169.254/16) addresses are left out, and addresses of
/// interfaces with a default gateway come first. Host-only virtual switches (Hyper-V's "Default Switch" and WSL,
/// VMware and VirtualBox host-only networks) are left out too when they have no gateway: no other device can reach
/// the PC there. A Hyper-V external switch carries the real LAN and has a gateway, so it stays; VPNs (Tailscale,
/// ZeroTier, WireGuard) are not virtual switches and stay as well.
/// </summary>
public static class HostNetwork
{
    /// <summary>
    /// A unicast address of an interface that is up, whether that interface has an IPv4 gateway, and its adapter
    /// description (<see cref="NetworkInterface.Description"/>).
    /// </summary>
    public sealed record Candidate(IPAddress Address, bool HasGateway, string? Description = null);

    /// <summary>Adapter descriptions of virtual switches that only reach virtual machines on this PC.</summary>
    private static readonly string[] HostOnlyAdapters =
    [
        "Hyper-V Virtual Ethernet Adapter",
        "VMware Virtual Ethernet Adapter",
        "VirtualBox Host-Only Ethernet Adapter",
    ];

    /// <summary>A host-only virtual switch (WSL, Hyper-V Default Switch, VMware, VirtualBox) without a gateway.</summary>
    public static bool IsHostOnly(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return !candidate.HasGateway && candidate.Description is { } d
            && HostOnlyAdapters.Any(a => d.StartsWith(a, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The current addresses; empty when the interfaces cannot be read.</summary>
    public static IReadOnlyList<string> Addresses() => Select(Candidates());

    /// <summary>Filters and orders <paramref name="candidates"/> (the order within each group is kept).</summary>
    public static IReadOnlyList<string> Select(IEnumerable<Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(c => IsLanIPv4(c.Address) && !IsHostOnly(c))
            .OrderByDescending(c => c.HasGateway)
            .Select(c => c.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsLanIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static List<Candidate> Candidates()
    {
        var list = new List<Candidate>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return list;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var unicast in properties.UnicastAddresses)
            {
                list.Add(new Candidate(unicast.Address, hasGateway, nic.Description));
            }
        }

        return list;
    }
}
