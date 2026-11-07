// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SplitPlay.Service.Platform;

/// <summary>A network adapter a client is reached through: its id and link speed (bit/s).</summary>
public sealed record NetworkLink(string Id, long BitsPerSecond);

/// <summary>
/// Finds the network adapter a Moonlight client is reached through, for the shared network budget: the adapter that
/// is up and has an address in the client's subnet. Clients behind a router (another subnet) and loopback clients are
/// not matched. The adapter list is read at most every <see cref="CacheFor"/>.
/// </summary>
public sealed class NetworkLinks
{
    /// <summary>How long one read of the adapter list is used.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private (IPAddress Address, int PrefixLength, NetworkLink Link)[] _subnets = [];
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public NetworkLinks(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>The adapter that reaches <paramref name="client"/> directly, or null.</summary>
    public NetworkLink? Find(string? client)
    {
        if (!IPAddress.TryParse(client, out var address) || IPAddress.IsLoopback(address))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var subnets = Subnets();
        NetworkLink? best = null;
        var bestPrefix = -1;
        foreach (var (local, prefix, link) in subnets)
        {
            if (prefix > bestPrefix && SameSubnet(local, address, prefix))
            {
                best = link;
                bestPrefix = prefix;
            }
        }

        return best;
    }

    /// <summary>True when the first <paramref name="prefixLength"/> bits of both addresses are equal.</summary>
    public static bool SameSubnet(IPAddress a, IPAddress b, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.AddressFamily != b.AddressFamily)
        {
            return false;
        }

        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        var bits = Math.Clamp(prefixLength, 0, x.Length * 8);
        for (var i = 0; i < x.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((x[i] & mask) != (y[i] & mask))
            {
                return false;
            }
        }

        return true;
    }

    private (IPAddress Address, int PrefixLength, NetworkLink Link)[] Subnets()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now - _readAt < CacheFor)
            {
                return _subnets;
            }

            _readAt = now;
            try
            {
                _subnets = Read();
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                _subnets = [];
            }

            return _subnets;
        }
    }

    private static (IPAddress, int, NetworkLink)[] Read()
    {
        var list = new List<(IPAddress, int, NetworkLink)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            long speed;
            try
            {
                speed = nic.Speed;
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                continue;
            }

            if (speed <= 0)
            {
                continue;
            }

            var link = new NetworkLink(nic.Id, speed);
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && unicast.PrefixLength > 0)
                {
                    list.Add((unicast.Address, unicast.PrefixLength, link));
                }
            }
        }

        return [.. list];
    }
}
