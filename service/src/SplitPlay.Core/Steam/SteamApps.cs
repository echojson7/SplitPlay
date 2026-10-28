// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SplitPlay.Core.Steam;

/// <summary>
/// The "Steam" entry of a desk's Moonlight app list (<c>apps.json</c> in the desk directory, read by the worker at
/// start-up). With Steam isolation on it starts the desk's own client in Big Picture:
/// <c>steam.exe -master_ipc_name_override SplitPlay_&lt;desk&gt; -gamepadui</c>. An entry named "Steam" without the
/// flag is the owner's own and is never changed.
/// </summary>
public static class SteamApps
{
    public const string FileName = "apps.json";
    public const string AppName = "Steam";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,

        // Quotes as \" and non-ASCII as is: Sunshine's JSON reader is not a browser.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Adds, updates or removes SplitPlay's "Steam" entry. <paramref name="steamExe"/> null (Steam not installed) or
    /// <paramref name="enabled"/> false removes it. A missing file is created with "Desktop" (the worker's default)
    /// first. Returns true if the file changed.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a JSON object with an <c>apps</c> array.</exception>
    public static bool Ensure(string appsJsonPath, string deskId, string? steamExe, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(appsJsonPath);
        var want = enabled && !string.IsNullOrWhiteSpace(steamExe);
        JsonObject root;
        if (File.Exists(appsJsonPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(appsJsonPath, Encoding.UTF8)) as JsonObject
                       ?? throw new InvalidDataException($"{appsJsonPath} is not a JSON object");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{appsJsonPath} is not valid JSON: {ex.Message}", ex);
            }
        }
        else if (!want)
        {
            return false;
        }
        else
        {
            root = new JsonObject
            {
                ["env"] = new JsonObject(),
                ["apps"] = new JsonArray(new JsonObject { ["name"] = "Desktop", ["image-path"] = "desktop.png" }),
            };
        }

        if (root["apps"] is not JsonArray apps)
        {
            throw new InvalidDataException($"{appsJsonPath} has no \"apps\" array");
        }

        var existing = apps.OfType<JsonObject>().FirstOrDefault(a => a["name"]?.GetValueKind() == JsonValueKind.String
                                                                       && (string?)a["name"] == AppName);
        var ours = existing?["cmd"] is JsonValue cmd && cmd.GetValueKind() == JsonValueKind.String
                   && ((string?)cmd)!.Contains(SteamIsolation.Flag, StringComparison.OrdinalIgnoreCase);
        if (existing is not null && !ours)
        {
            return false;
        }

        if (!want)
        {
            if (existing is null)
            {
                return false;
            }

            apps.Remove(existing);
        }
        else
        {
            var command = $"\"{steamExe}\" {SteamIsolation.Flag} {SteamIsolation.IpcName(deskId)} -gamepadui";
            if (existing is not null && (string?)existing["cmd"] == command)
            {
                return false;
            }

            var entry = new JsonObject
            {
                ["name"] = AppName,
                ["cmd"] = command,
                ["auto-detach"] = true,
                ["wait-all"] = true,
                ["image-path"] = "steam.png",
            };
            if (existing is null)
            {
                apps.Add((JsonNode)entry);
            }
            else
            {
                apps[apps.IndexOf(existing)] = entry;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(appsJsonPath))!);
        var temp = appsJsonPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented), new UTF8Encoding(false));
        File.Move(temp, appsJsonPath, overwrite: true);
        return true;
    }
}
