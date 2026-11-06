// SPDX-License-Identifier: MIT
using System.Text.Json;
using SplitPlay.Core.Api;

namespace SplitPlay.Core.Ipc;

/// <summary>
/// The worker's <c>display_hdr</c> event data (docs/contracts/ipc.md): the HDR state of the desk session's displays.
/// Parsed leniently: an unknown state becomes <c>unknown</c>, a field of the wrong type is treated as absent.
/// </summary>
public static class IpcDisplayHdr
{
    /// <summary>The desk's HDR state from <paramref name="element"/>; null when it is not an object.</summary>
    public static DeskHdrDto? FromJson(JsonElement? element, DateTimeOffset now)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e)
        {
            return null;
        }

        var state = e.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        state = state switch
        {
            DeskHdrStates.Enabled or DeskHdrStates.Off or DeskHdrStates.Unsupported => state,
            _ => DeskHdrStates.Unknown,
        };

        // The display that decides the state comes first: one in HDR, else one that supports it, else the first.
        JsonElement? display = null;
        if (e.TryGetProperty("displays", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var displays = list.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object).ToList();
            var index = displays.FindIndex(d => True(d, "dxgi_hdr"));
            if (index < 0)
            {
                index = displays.FindIndex(d => True(d, "hdr_supported"));
            }

            if (index < 0 && displays.Count > 0)
            {
                index = 0;
            }

            display = index >= 0 ? displays[index] : null;
        }

        return new DeskHdrDto
        {
            State = state,
            Monitor = display is { } m ? String(m, "monitor") : null,
            Adapter = display is { } a ? String(a, "adapter") : null,
            BitsPerColor = display is { } b && Number(b, "bits_per_color") is double bits && bits is >= 1 and <= 64 ? (int)bits : null,
            MaxNits = display is { } n && Number(n, "max_nits") is double nits && nits >= 0 ? Math.Round(nits, 1) : null,
            UpdatedAt = now,
        };
    }

    private static bool True(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
}
