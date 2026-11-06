// SPDX-License-Identifier: MIT
using System.Text;
using SplitPlay.Core.Api;
using SplitPlay.Core.Json;
using SplitPlay.Service.Hosting;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Api;

/// <summary>
/// <c>GET /desks/{id}/client-pack</c> and its launch scripts (web-api.md, <see cref="ClientPack"/>): the Moonlight
/// settings recommended for a desk and .cmd/.sh files that start moonlight-qt with them.
/// </summary>
public static class ClientPackEndpoints
{
    public static void MapClientPack(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/desks/{id}/client-pack", (string id, string? address, DeskSupervisor s) =>
            Results.Json(Build(id, address, s), SplitPlayJsonContext.Default.ClientPackDto));

        api.MapGet("/desks/{id}/client-pack/{platform}", (string id, string platform, string? address, DeskSupervisor s) =>
        {
            var pack = Build(id, address, s);
            var script = ClientPack.Script(pack, platform)
                         ?? throw ApiException.NotFound($"no launch script for '{platform}' (windows, linux)");
            var linux = string.Equals(platform, ClientPack.PlatformLinux, StringComparison.OrdinalIgnoreCase);
            var file = pack.Scripts.First(x => x.Platform == (linux ? ClientPack.PlatformLinux : ClientPack.PlatformWindows)).FileName;
            return Results.File(new UTF8Encoding(false).GetBytes(script), linux ? "application/x-sh" : "application/x-bat", file);
        });
    }

    /// <summary>
    /// The pack for desk <paramref name="id"/>. <paramref name="address"/> picks the host address Moonlight dials (an
    /// IPv4 address or host name); by default the first LAN address of <c>GET /host/network</c>, else the computer name.
    /// </summary>
    private static ClientPackDto Build(string id, string? address, DeskSupervisor s)
    {
        var desk = s.Get(id);
        var port = desk.BasePort ?? throw ApiException.Conflict(ApiErrorCodes.InvalidState, $"desk {id} has no base port");

        string host;
        if (!string.IsNullOrEmpty(address))
        {
            if (!ClientPack.IsValidHost(address))
            {
                throw ApiException.Validation("address: must be an IPv4 address or a host name");
            }

            host = address;
        }
        else
        {
            host = HostNetwork.Addresses().FirstOrDefault() ?? Environment.MachineName;
            if (!ClientPack.IsValidHost(host))
            {
                throw ApiException.Conflict(ApiErrorCodes.InvalidState, "the PC has no LAN address; pass ?address=");
            }
        }

        return ClientPack.Build(desk.Id, desk.Profile, host, port);
    }
}
