// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SplitPlay.Service.Hosting;

/// <summary>
/// One rule as the Windows Firewall stores it in the registry (one REG_SZ value per rule, e.g.
/// <c>v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=17|Profile=Private|LPort=5353|App=%SystemRoot%\system32\svchost.exe|Svc=dnscache|Name=…|</c>).
/// A key may repeat (<c>Profile</c>, <c>LPort</c>); an absent key means "any".
/// </summary>
public sealed class FirewallRuleData
{
    private readonly Dictionary<string, List<string>> _fields;

    private FirewallRuleData(Dictionary<string, List<string>> fields) => _fields = fields;

    public static FirewallRuleData Parse(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in data.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue; // the version ("v2.31")
            }

            var key = part[..eq];
            if (!fields.TryGetValue(key, out var values))
            {
                fields[key] = values = [];
            }

            values.Add(part[(eq + 1)..]);
        }

        return new FirewallRuleData(fields);
    }

    public string? Name => First("Name");

    public string? App => First("App");

    public string? Service => First("Svc");

    public bool Active => string.Equals(First("Active"), "TRUE", StringComparison.OrdinalIgnoreCase);

    public bool Inbound => string.Equals(First("Dir"), "In", StringComparison.OrdinalIgnoreCase);

    public bool Allow => string.Equals(First("Action"), "Allow", StringComparison.OrdinalIgnoreCase);

    public bool Block => string.Equals(First("Action"), "Block", StringComparison.OrdinalIgnoreCase);

    /// <summary>The IP protocol number, null for any.</summary>
    public int? Protocol => int.TryParse(First("Protocol"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;

    /// <summary><c>Domain</c>, <c>Private</c>, <c>Public</c>; empty for all profiles.</summary>
    public IReadOnlyList<string> Profiles => Values("Profile");

    /// <summary>Local port specifications (<c>5353</c>, <c>1000-2000</c>, keywords); empty for any port.</summary>
    public IReadOnlyList<string> LocalPorts =>
        [.. _fields.Where(f => f.Key.StartsWith("LPort", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => f.Value).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];

    public bool AppliesTo(string profile) => Profiles.Count == 0 || Profiles.Contains(profile, StringComparer.OrdinalIgnoreCase);

    public bool CoversPort(int port) => LocalPorts.Count == 0 || LocalPorts.Any(spec => PortSpecCovers(spec, port));

    /// <summary>The single numeric ports of <see cref="LocalPorts"/>; null when there is a range or a keyword.</summary>
    public IReadOnlySet<int>? NumericPorts()
    {
        var set = new HashSet<int>();
        foreach (var spec in LocalPorts)
        {
            if (!int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                return null;
            }

            set.Add(port);
        }

        return set;
    }

    private static bool PortSpecCovers(string spec, int port)
    {
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0
            && int.TryParse(spec.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var low)
            && int.TryParse(spec.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var high))
        {
            return port >= low && port <= high;
        }

        return int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out var single) && single == port;
    }

    private string? First(string key) => _fields.TryGetValue(key, out var values) ? values[0] : null;

    private List<string> Values(string key) => _fields.TryGetValue(key, out var values) ? values : [];
}

/// <summary>Decisions over the firewall's rules (<see cref="FirewallRuleData"/>), kept free of the registry for tests.</summary>
public static class FirewallRules
{
    public const int Tcp = 6;
    public const int Udp = 17;

    /// <summary>The UDP port of mDNS.</summary>
    public const int MdnsPort = 5353;

    /// <summary>The profiles a desk rule is created for (<c>profile=private,domain</c>).</summary>
    public static readonly IReadOnlySet<string> DeskRuleProfiles = new HashSet<string>(["Private", "Domain"], StringComparer.OrdinalIgnoreCase);

    public enum DeskRuleState
    {
        /// <summary>Exactly one rule of that name, enabled, as SplitPlay creates it.</summary>
        Match,

        /// <summary>No rule of that name (deleted by someone, or never created).</summary>
        Missing,

        /// <summary>Disabled, changed or duplicated.</summary>
        Different,
    }

    /// <summary>Whether the desk rule <paramref name="name"/> is there as <c>EnsureFirewallRules</c> creates it.</summary>
    public static DeskRuleState DeskRule(IEnumerable<string> rules, string name, int protocol, IEnumerable<int> ports, string program)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var named = rules.Select(FirewallRuleData.Parse).Where(r => string.Equals(r.Name, name, StringComparison.Ordinal)).ToList();
        if (named.Count == 0)
        {
            return DeskRuleState.Missing;
        }

        if (named.Count > 1)
        {
            return DeskRuleState.Different;
        }

        var rule = named[0];
        var match = rule.Active && rule.Inbound && rule.Allow && rule.Protocol == protocol
                    && rule.NumericPorts() is { } actual && actual.SetEquals(ports)
                    && SamePath(rule.App, program)
                    && rule.Profiles.Count == DeskRuleProfiles.Count && rule.Profiles.All(DeskRuleProfiles.Contains);
        return match ? DeskRuleState.Match : DeskRuleState.Different;
    }

    /// <summary>
    /// Whether inbound mDNS reaches the Windows mDNS responder (the DNS Client service, <c>dnscache</c> in svchost) on
    /// <paramref name="profile"/>: the firewall is off for it, or an enabled rule allows inbound UDP 5353 for it and no
    /// enabled rule blocks it. Windows' own rule is "mDNS (UDP-In)".
    /// </summary>
    public static bool MdnsAllowed(IEnumerable<string> rules, string profile, bool firewallEnabled)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!firewallEnabled)
        {
            return true;
        }

        var matching = rules.Select(FirewallRuleData.Parse)
            .Where(r => r.Active && r.Inbound && (r.Protocol is null or Udp) && r.AppliesTo(profile) && r.CoversPort(MdnsPort) && ReachesDnsClient(r))
            .ToList();
        return matching.Any(r => r.Allow) && !matching.Any(r => r.Block);
    }

    /// <summary>A rule for any program, or for svchost with any service or the DNS Client service.</summary>
    private static bool ReachesDnsClient(FirewallRuleData rule)
    {
        if (string.IsNullOrEmpty(rule.App) || rule.App == "*")
        {
            return true;
        }

        if (!rule.App.EndsWith("\\svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrEmpty(rule.Service) || rule.Service == "*" || string.Equals(rule.Service, "dnscache", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string? stored, string expected)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        return string.Equals(Environment.ExpandEnvironmentVariables(stored).Trim('"'), expected, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The firewall's registry stores: local rules (what <c>netsh advfirewall</c> writes) and Group Policy rules.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFirewallStore
{
    private const string LocalPolicy = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
    private const string GroupPolicy = @"SOFTWARE\Policies\Microsoft\WindowsFirewall";

    /// <summary>The local rules, or null when the store cannot be read.</summary>
    public static IReadOnlyList<string>? LocalRules() => Rules(LocalPolicy + @"\FirewallRules");

    /// <summary>The Group Policy rules; empty when there are none or they cannot be read.</summary>
    public static IReadOnlyList<string> PolicyRules() => Rules(GroupPolicy + @"\FirewallRules") ?? [];

    /// <summary>Whether the firewall is on for <paramref name="profile"/> (Group Policy first); null when unknown.</summary>
    public static bool? Enabled(string profile)
    {
        var (local, policy) = profile switch
        {
            "Domain" => ("DomainProfile", "DomainProfile"),
            "Private" => ("StandardProfile", "PrivateProfile"),
            "Public" => ("PublicProfile", "PublicProfile"),
            _ => (null, null),
        };
        if (local is null)
        {
            return null;
        }

        try
        {
            if (Registry.LocalMachine.OpenSubKey($@"{GroupPolicy}\{policy}")?.GetValue("EnableFirewall") is int fromPolicy)
            {
                return fromPolicy != 0;
            }

            return Registry.LocalMachine.OpenSubKey($@"{LocalPolicy}\{local}")?.GetValue("EnableFirewall") is int value ? value != 0 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static List<string>? Rules(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null)
            {
                return null;
            }

            var rules = new List<string>();
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string data)
                {
                    rules.Add(data);
                }
            }

            return rules;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
