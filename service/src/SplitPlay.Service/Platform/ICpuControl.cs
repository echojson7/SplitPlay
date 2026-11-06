// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using SplitPlay.Core.Host;

namespace SplitPlay.Service.Platform;

/// <summary>
/// Puts the processes of a desk session on the processors <see cref="DeskCorePlanner"/> picks (<c>host.deskCores</c>).
/// <see cref="WindowsCpuControl"/> in production, <see cref="FakeCpuControl"/> in tests and on non-Windows hosts.
/// </summary>
public interface ICpuControl
{
    /// <summary>The CPU (read once, cached).</summary>
    CpuTopology Topology();

    /// <summary>Opens the desk's scope for <paramref name="sessionId"/>; disposed when the session ends.</summary>
    IDeskCpuScope Open(string deskId, int sessionId, string user);
}

/// <summary>The desk session's processes and the processors they are kept on.</summary>
public interface IDeskCpuScope : IDisposable
{
    /// <summary>
    /// Brings the desk user's processes in the session under the scope (new ones since the last call) and keeps them
    /// on <paramref name="cpus"/> (null: all processors). Returns how many processes joined in this call.
    /// Before the first restriction nothing is touched: with <c>off</c> the desk's processes stay as Windows made them.
    /// </summary>
    int Sweep(IReadOnlyList<int>? cpus);
}

/// <summary>A CPU and desk scopes that only record what they were asked to do.</summary>
public sealed class FakeCpuControl : ICpuControl
{
    /// <summary>The topology <see cref="Topology"/> returns (tests replace it); a uniform 8-thread CPU by default.</summary>
    public CpuTopology Current { get; set; } = new("Fake CPU",
        [.. Enumerable.Range(0, 8).Select(i => new LogicalCpu(0, i, i / 2, 0, 0))],
        new Dictionary<(int, int), long> { [(0, 0)] = 32L << 20 });

    /// <summary>The last restriction each desk's open scope was swept with (desk id → "all" or "0-7").</summary>
    public ConcurrentDictionary<string, string> Applied { get; } = new(StringComparer.Ordinal);

    /// <summary>Desks with an open scope.</summary>
    public ConcurrentDictionary<string, int> Open { get; } = new(StringComparer.Ordinal);

    public CpuTopology Topology() => Current;

    IDeskCpuScope ICpuControl.Open(string deskId, int sessionId, string user)
    {
        Open[deskId] = sessionId;
        return new Scope(this, deskId);
    }

    private sealed class Scope(FakeCpuControl owner, string deskId) : IDeskCpuScope
    {
        public int Sweep(IReadOnlyList<int>? cpus)
        {
            owner.Applied[deskId] = DeskCorePlanner.Format(cpus);
            return 0;
        }

        public void Dispose()
        {
            owner.Open.TryRemove(deskId, out _);
            owner.Applied.TryRemove(deskId, out _);
        }
    }
}
