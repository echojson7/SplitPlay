// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SplitPlay.Service.Hosting;

/// <summary>
/// What <c>GET /host/network</c> reports about the LAN besides the addresses: the Windows network profile of the
/// host's LAN (<c>Public</c>, <c>Private</c>, <c>Domain</c>) and whether inbound mDNS reaches Windows' mDNS responder on
/// it. Null means unknown. SplitPlay never changes either; the UI only points at them.
/// </summary>
public sealed record HostNetworkState(string? Profile, bool? MdnsAllowed)
{
    public static readonly HostNetworkState Unknown = new(null, null);
}

/// <summary>The current <see cref="HostNetworkState"/>.</summary>
public interface IHostNetworkStatus
{
    HostNetworkState Current();
}

/// <summary>Tests and non-Windows hosts: whatever <see cref="State"/> is set to (unknown by default).</summary>
public sealed class FakeHostNetworkStatus : IHostNetworkStatus
{
    public HostNetworkState State { get; set; } = HostNetworkState.Unknown;

    public HostNetworkState Current() => State;
}

/// <summary>Pure parts of the network profile decision.</summary>
public static class NetworkProfiles
{
    /// <summary>NLM_NETWORK_CATEGORY: 0 public, 1 private, 2 domain authenticated.</summary>
    public static string? Name(int category) => category switch
    {
        0 => "Public",
        1 => "Private",
        2 => "Domain",
        _ => null,
    };

    /// <summary>One network interface: whether it has an IPv4 default gateway, and its network's category (null: unknown).</summary>
    public sealed record Interface(bool HasGateway, int? Category);

    /// <summary>
    /// The profile that matters for Moonlight: that of the interfaces with a default gateway (the LAN; virtual switches
    /// of Hyper-V or WSL, usually on an unidentified public network, have none), or of every interface when none has
    /// one. The worst wins: Public, then Private, then Domain. Null when no interface has a known category.
    /// </summary>
    public static string? Pick(IEnumerable<Interface> interfaces)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        var known = interfaces.Where(i => i.Category is not null).ToList();
        var lan = known.Where(i => i.HasGateway).ToList();
        var chosen = lan.Count > 0 ? lan : known;
        foreach (var category in new[] { 0, 1, 2 })
        {
            if (chosen.Any(i => i.Category == category))
            {
                return Name(category);
            }
        }

        return null;
    }
}

/// <summary>
/// Windows: the category of each connected network from the Network List Manager, matched to the interfaces by adapter
/// id, and the firewall's rules from the registry (<see cref="WindowsFirewallStore"/>). Cached for a few seconds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostNetworkStatus(ILogger<WindowsHostNetworkStatus> logger) : IHostNetworkStatus
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private HostNetworkState _cached = HostNetworkState.Unknown;
    private DateTime _cachedAt = DateTime.MinValue;

    public HostNetworkState Current()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _cachedAt < CacheFor)
            {
                return _cached;
            }
        }

        var profile = Profile();
        bool? mdns = null;
        // An unknown profile is checked as Private, the profile a home LAN should have.
        var checkedProfile = profile ?? "Private";
        if (WindowsFirewallStore.LocalRules() is { } local)
        {
            var enabled = WindowsFirewallStore.Enabled(checkedProfile) ?? true;
            mdns = FirewallRules.MdnsAllowed([.. local, .. WindowsFirewallStore.PolicyRules()], checkedProfile, enabled);
        }

        var state = new HostNetworkState(profile, mdns);
        lock (_gate)
        {
            _cached = state;
            _cachedAt = DateTime.UtcNow;
        }

        return state;
    }

    private string? Profile()
    {
        var categories = NetworkListManager.CategoriesByAdapter(logger);
        if (categories is null)
        {
            return null;
        }

        var interfaces = new List<NetworkProfiles.Interface>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || !Guid.TryParse(nic.Id, out var id))
                {
                    continue;
                }

                var hasGateway = nic.GetIPProperties().GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                interfaces.Add(new NetworkProfiles.Interface(hasGateway, categories.TryGetValue(id, out var category) ? category : null));
            }
        }
        catch (NetworkInformationException ex)
        {
            logger.LogDebug("Network interfaces could not be read: {Message}", ex.Message);
            return null;
        }

        return NetworkProfiles.Pick(interfaces);
    }

    /// <summary>The Network List Manager (netlistmgr.h), through its dual COM interfaces; only the members used here and the ones before them.</summary>
    private static class NetworkListManager
    {
        private static readonly Guid Clsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

        /// <summary>NLM_NETWORK_CATEGORY of the network of each connection, by adapter id; null when NLM is not available.</summary>
        public static Dictionary<Guid, int>? CategoriesByAdapter(ILogger logger)
        {
            object? instance = null;
            IEnumNetworkConnections? connections = null;
            try
            {
                var type = Type.GetTypeFromCLSID(Clsid, throwOnError: false);
                if (type is null)
                {
                    return null;
                }

                instance = Activator.CreateInstance(type);
                if (instance is not INetworkListManager manager)
                {
                    return null;
                }

                connections = manager.GetNetworkConnections();
                var result = new Dictionary<Guid, int>();
                for (var i = 0; i < 64; i++)
                {
                    connections.Next(1, out var connection, out var fetched);
                    if (fetched == 0 || connection is null)
                    {
                        break;
                    }

                    INetwork? network = null;
                    try
                    {
                        network = connection.GetNetwork();
                        result[connection.GetAdapterId()] = network.GetCategory();
                    }
                    finally
                    {
                        if (network is not null)
                        {
                            Marshal.ReleaseComObject(network);
                        }

                        Marshal.ReleaseComObject(connection);
                    }
                }

                return result;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or InvalidComObjectException)
            {
                logger.LogDebug("Network List Manager unavailable: {Message}", ex.Message);
                return null;
            }
            finally
            {
                if (connections is not null)
                {
                    Marshal.ReleaseComObject(connections);
                }

                if (instance is not null && Marshal.IsComObject(instance))
                {
                    Marshal.ReleaseComObject(instance);
                }
            }
        }
    }

    [ComImport]
    [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworks(int flags);

        [return: MarshalAs(UnmanagedType.Interface)]
        INetwork GetNetwork(Guid networkId);

        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport]
    [Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object get__NewEnum();

        void Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection? connection, out uint fetched);
    }

    [ComImport]
    [Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        INetwork GetNetwork();

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool get_IsConnectedToInternet();

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool get_IsConnected();

        int GetConnectivity();

        Guid GetConnectionId();

        Guid GetAdapterId();
    }

    [ComImport]
    [Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetName();

        void SetName([MarshalAs(UnmanagedType.BStr)] string name);

        [return: MarshalAs(UnmanagedType.BStr)]
        string GetDescription();

        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);

        Guid GetNetworkId();

        int GetDomainType();

        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumNetworkConnections GetNetworkConnections();

        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool get_IsConnectedToInternet();

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool get_IsConnected();

        int GetConnectivity();

        int GetCategory();
    }
}
