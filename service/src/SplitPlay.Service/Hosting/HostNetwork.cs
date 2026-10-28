// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SplitPlay.Service.Hosting;

/// <summary>
/// LAN IPv4 addresses of this PC for <c>GET /host/network</c>: the UI shows them as the address to add in Moonlight.
/// Only interfaces that are up count; loopback and link-local (169.254/16) addresses are left out, and addresses of
/// interfaces with a default gateway come first.
/// </summary>
public static class HostNetwork
{
    /// <summary>A unicast address of an interface that is up, and whether that interface has an IPv4 gateway.</summary>
    public sealed record Candidate(IPAddress Address, bool HasGateway);

    /// <summary>The current addresses; empty when the interfaces cannot be read.</summary>
    public static IReadOnlyList<string> Addresses() => Select(Candidates());

    /// <summary>Filters and orders <paramref name="candidates"/> (the order within each group is kept).</summary>
    public static IReadOnlyList<string> Select(IEnumerable<Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(c => IsLanIPv4(c.Address))
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
                list.Add(new Candidate(unicast.Address, hasGateway));
            }
        }

        return list;
    }
}
