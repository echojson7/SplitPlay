// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Ports;

/// <summary>An inclusive TCP/UDP port range.</summary>
public readonly record struct PortRange(int First, int Last)
{
    public bool Contains(int port) => port >= First && port <= Last;

    public bool Overlaps(PortRange other) => First <= other.Last && other.First <= Last;

    public override string ToString() => $"{First}-{Last}";
}

/// <summary>
/// Allocates desk base ports. A desk with base port P occupies <c>P-5 … P+21</c> (docs/contracts/config.md):
/// P-5 HTTPS, P HTTP, P+9 video, P+10 control, P+11 audio, P+21 RTSP.
/// </summary>
public static class BasePortAllocator
{
    /// <summary>Ports below the base port that belong to the desk.</summary>
    public const int PortsBelow = 5;

    /// <summary>Ports above the base port that belong to the desk.</summary>
    public const int PortsAbove = 21;

    /// <summary>Number of ports in one desk's range.</summary>
    public const int PortSpan = PortsBelow + PortsAbove + 1;

    public const int MinBasePort = 1 + PortsBelow;

    public const int MaxBasePort = 65535 - PortsAbove;

    public static bool IsValidBasePort(int basePort) => basePort is >= MinBasePort and <= MaxBasePort;

    public static PortRange RangeOf(int basePort) => new(basePort - PortsBelow, basePort + PortsAbove);

    /// <summary>
    /// Returns <c>start + stride * k</c> for the lowest <c>k ≥ 0</c> whose range does not overlap any range of
    /// <paramref name="usedBasePorts"/> and does not contain any of <paramref name="reservedPorts"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No free slot is left below port 65535.</exception>
    public static int Allocate(int start, int stride, IEnumerable<int> usedBasePorts, IEnumerable<int>? reservedPorts = null)
    {
        if (!IsValidBasePort(start))
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, $"must be between {MinBasePort} and {MaxBasePort}");
        }

        if (stride < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "must be positive");
        }

        var used = usedBasePorts.Select(RangeOf).ToList();
        var reserved = reservedPorts?.ToList() ?? [];

        for (long candidate = start; candidate <= MaxBasePort; candidate += stride)
        {
            var range = RangeOf((int)candidate);
            if (used.Any(range.Overlaps) || reserved.Any(range.Contains))
            {
                continue;
            }

            return (int)candidate;
        }

        throw new InvalidOperationException("No free base port is left in the configured range.");
    }
}
