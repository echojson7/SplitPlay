// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Streaming;

/// <summary>
/// The network share of each streaming desk ("общий бюджет сети", stage 4 PR 11): desks whose clients are reached
/// through the same network adapter share <see cref="UsableShare"/> of its link speed. While together they ask for
/// no more than that, nobody is limited; otherwise each gets a share in proportion to what its clients ask for.
/// Thread-safe.
/// </summary>
public sealed class NetworkBudget
{
    /// <summary>The part of a link the video streams may use; the rest is left for audio, input and everything else.</summary>
    public const double UsableShare = 0.8;

    /// <summary>No desk is cut below this by the budget, kbit/s.</summary>
    public const int MinShareKbps = 2000;

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Link, long LinkBitsPerSecond, int DemandKbps)> _desks = new(StringComparer.Ordinal);

    /// <summary>
    /// A desk streams <paramref name="demandKbps"/> (what its clients ask for) over <paramref name="link"/>; a desk
    /// without a known link or demand is removed.
    /// </summary>
    public void Set(string deskId, string? link, long linkBitsPerSecond, int demandKbps)
    {
        ArgumentNullException.ThrowIfNull(deskId);
        lock (_gate)
        {
            if (string.IsNullOrEmpty(link) || linkBitsPerSecond <= 0 || demandKbps <= 0)
            {
                _desks.Remove(deskId);
                return;
            }

            _desks[deskId] = (link, linkBitsPerSecond, demandKbps);
        }
    }

    /// <summary>The desk stopped streaming.</summary>
    public void Remove(string deskId)
    {
        lock (_gate)
        {
            _desks.Remove(deskId);
        }
    }

    /// <summary>The desk's cap, kbit/s, or null when its adapter carries every desk on it in full.</summary>
    public int? ShareKbps(string deskId)
    {
        lock (_gate)
        {
            if (!_desks.TryGetValue(deskId, out var mine))
            {
                return null;
            }

            long total = 0;
            foreach (var (_, desk) in _desks)
            {
                if (desk.Link == mine.Link)
                {
                    total += desk.DemandKbps;
                }
            }

            var budgetKbps = mine.LinkBitsPerSecond * UsableShare / 1000;
            if (total <= budgetKbps)
            {
                return null;
            }

            var share = (int)Math.Min(int.MaxValue, budgetKbps * mine.DemandKbps / total);
            return Math.Min(mine.DemandKbps, Math.Max(MinShareKbps, share));
        }
    }
}
