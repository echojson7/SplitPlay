// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;

namespace SplitPlay.Core.Ipc;

/// <summary>
/// A Moonlight client waiting for its PIN: the worker heartbeat's <c>pairing</c> object (docs/contracts/ipc.md,
/// patch 0021). Parsed leniently: anything but an object means no request.
/// </summary>
/// <param name="Address">Client IP address; null when the worker did not send one.</param>
/// <param name="Since">When the request arrived; null when the worker did not send a valid time.</param>
public sealed record IpcPinRequest(string? Address, DateTimeOffset? Since)
{
    /// <summary>The request in <paramref name="element"/>; null for <c>null</c>, a missing field or any non-object.</summary>
    public static IpcPinRequest? FromJson(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e)
        {
            return null;
        }

        var address = e.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } s
            ? s
            : null;
        DateTimeOffset? since = e.TryGetProperty("since", out var t) && t.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
        return new IpcPinRequest(address, since);
    }
}
