// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;

namespace SplitPlay.Core.Ipc;

/// <summary>
/// One running Moonlight stream as the worker describes it in <c>heartbeat.sessions[]</c> and in the
/// <c>client_connected</c>/<c>client_disconnected</c> event data (docs/contracts/ipc.md). Parsed leniently: a field of
/// the wrong type is treated as absent, so a newer or older worker never breaks the heartbeat.
/// </summary>
public sealed record IpcStreamSession
{
    /// <summary>The worker's launch session id; unique while the worker runs.</summary>
    public long? Id { get; init; }

    /// <summary>The paired client's name, when the worker knows it.</summary>
    public string? Client { get; init; }

    /// <summary>The paired client's uuid (the <c>uuid</c> of <c>list_clients</c>).</summary>
    public string? ClientUuid { get; init; }

    /// <summary>Client IP address.</summary>
    public string? Address { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int FpsRequested { get; init; }

    /// <summary><c>h264</c>, <c>hevc</c> or <c>av1</c>.</summary>
    public string? Codec { get; init; }

    public int BitrateKbpsRequested { get; init; }

    public bool Hdr { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// True when <paramref name="other"/> describes the same stream: the same id when both have one, otherwise the same
    /// address and start time.
    /// </summary>
    public bool SameStream(IpcStreamSession other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Id is long a && other.Id is long b)
        {
            return a == b;
        }

        return string.Equals(Address, other.Address, StringComparison.Ordinal) && StartedAt == other.StartedAt;
    }

    /// <summary>Parses one session object; null when <paramref name="element"/> is not an object.</summary>
    public static IpcStreamSession? FromJson(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e)
        {
            return null;
        }

        return new IpcStreamSession
        {
            Id = Long(e, "id"),
            Client = String(e, "client"),
            ClientUuid = String(e, "client_uuid"),
            Address = String(e, "address"),
            Width = Int(e, "width"),
            Height = Int(e, "height"),
            FpsRequested = Int(e, "fps_requested"),
            Codec = String(e, "codec"),
            BitrateKbpsRequested = Int(e, "bitrate_kbps_requested"),
            Hdr = e.TryGetProperty("hdr", out var hdr) && hdr.ValueKind == JsonValueKind.True,
            StartedAt = String(e, "started_at") is { } s
                && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
                    ? t
                    : null,
        };
    }

    /// <summary>Parses <c>heartbeat.sessions</c>; entries that are not objects are skipped.</summary>
    public static IReadOnlyList<IpcStreamSession> ListFromJson(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        var list = new List<IpcStreamSession>();
        foreach (var item in array.EnumerateArray())
        {
            if (FromJson(item) is { } session)
            {
                list.Add(session);
            }
        }

        return list;
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d is >= 0 and <= int.MaxValue
            ? (int)Math.Round(d)
            : 0;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
}
