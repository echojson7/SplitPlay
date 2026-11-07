// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace SplitPlay.Core.Api;

/// <summary>One host warning as the tray sees it (<c>GET /api/v1/host/warnings</c>).</summary>
public sealed record TrayWarning(string Code, bool Repaired = false, string? Version = null);

/// <summary>Lenient parsing of <c>GET /api/v1/host/warnings</c> and the line the tray shows for a warning.</summary>
public static class TrayWarnings
{
    /// <summary>Entries without a code are skipped. Throws <see cref="JsonException"/> when it is not a JSON array.</summary>
    public static IReadOnlyList<TrayWarning> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("expected an array of warnings");
        }

        var list = new List<TrayWarning>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object
                || !e.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String || code.GetString() is not { Length: > 0 } c)
            {
                continue;
            }

            var repaired = e.TryGetProperty("repaired", out var r) && r.ValueKind == JsonValueKind.True;
            var version = e.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            list.Add(new TrayWarning(c, repaired, version));
        }

        return list;
    }

    /// <summary>The tray's one line for a warning, or null for a code it does not show (GPU load, unknown codes).</summary>
    public static string? Text(TrayWarning warning, bool russian)
    {
        ArgumentNullException.ThrowIfNull(warning);
        return warning.Code switch
        {
            HostWarningCodes.TermWrapReboot when warning.Repaired => russian
                ? "Обновление Windows сбросило TermWrap. Перезагрузите ПК"
                : "Windows Update reset TermWrap. Restart the PC",
            HostWarningCodes.TermWrapReboot => russian
                ? "Перезагрузите ПК, чтобы запустить столы"
                : "Restart the PC to start desks",
            HostWarningCodes.TermWrapUnsupported => russian
                ? "Обновление Windows несовместимо с TermWrap: столы не запустятся"
                : "This Windows update breaks TermWrap: desks cannot start",
            HostWarningCodes.TermWrapMissing => russian
                ? "TermWrap удалён: переустановите SplitPlay"
                : "TermWrap was removed: reinstall SplitPlay",
            HostWarningCodes.TermWrapRepairFailed => russian
                ? "Не удалось вернуть TermWrap: запустите установку SplitPlay"
                : "Cannot restore TermWrap: run SplitPlay setup again",
            _ => null,
        };
    }
}
