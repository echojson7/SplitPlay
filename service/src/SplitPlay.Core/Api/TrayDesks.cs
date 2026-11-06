// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;
using SplitPlay.Core.Lifecycle;

namespace SplitPlay.Core.Api;

/// <summary>What the tray needs to know about one desk: its name, state and a waiting PIN request.</summary>
/// <param name="Id">Desk id.</param>
/// <param name="Name">Display name; the id when the desk has none.</param>
/// <param name="State">Lifecycle state; <see cref="DeskState.Defined"/> when the service sent an unknown one.</param>
/// <param name="PinSince">When a Moonlight client started waiting for its PIN; null when none waits.</param>
public sealed record TrayDesk(string Id, string Name, DeskState State, DateTimeOffset? PinSince)
{
    /// <summary>Start is offered (as the web UI: a stopped or never started desk).</summary>
    public bool CanStart => State is DeskState.Stopped or DeskState.Defined;

    /// <summary>Stop is offered: the desk runs or failed.</summary>
    public bool CanStop => !CanStart && State != DeskState.Stopping;
}

/// <summary>The tray's view of <c>GET /api/v1/desks</c>: lenient parsing and which PIN requests are new.</summary>
public static class TrayDesks
{
    /// <summary>
    /// Parses the desk list. Entries without an id are skipped and unknown fields ignored, so a newer or older service
    /// never breaks the tray. Throws <see cref="JsonException"/> when <paramref name="json"/> is not a JSON array.
    /// </summary>
    public static IReadOnlyList<TrayDesk> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("expected an array of desks");
        }

        var desks = new List<TrayDesk>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || String(e, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            var state = String(e, "state") is { } s && Enum.TryParse<DeskState>(s, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : DeskState.Defined;
            DateTimeOffset? since = e.TryGetProperty("pinRequest", out var pin) && pin.ValueKind == JsonValueKind.Object
                ? String(pin, "since") is { } t
                  && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? at
                    : DateTimeOffset.MinValue
                : null;
            desks.Add(new TrayDesk(id, String(e, "name") is { Length: > 0 } name ? name : id, state, since));
        }

        return desks;
    }

    /// <summary>
    /// Desks whose PIN request is new since <paramref name="before"/>: none before, or one that started at another time
    /// (Moonlight asked again). The first list after the tray starts counts every waiting request as new.
    /// </summary>
    public static IReadOnlyList<TrayDesk> NewPinRequests(IReadOnlyList<TrayDesk>? before, IReadOnlyList<TrayDesk> now)
    {
        ArgumentNullException.ThrowIfNull(now);
        return now.Where(d => d.PinSince is { } since
                              && before?.FirstOrDefault(b => b.Id == d.Id)?.PinSince != since)
            .ToList();
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
