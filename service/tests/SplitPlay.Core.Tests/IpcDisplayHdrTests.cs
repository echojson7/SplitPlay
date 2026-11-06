// SPDX-License-Identifier: MIT
using System.Text.Json;
using SplitPlay.Core.Api;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Core.Tests;

/// <summary>The worker's <c>display_hdr</c> event data: the desk display's HDR state.</summary>
public sealed class IpcDisplayHdrTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void An_rdp_display_without_hdr_is_unsupported()
    {
        var hdr = IpcDisplayHdr.FromJson(Json("""
            {"state":"unsupported","displays":[{"name":"\\\\.\\DISPLAY1","monitor":"","adapter":"NVIDIA GeForce RTX 5070",
              "hdr_supported":false,"hdr_enabled":false,"limited_by_policy":false,"color_mode":"sdr","bits_per_color":8,
              "dxgi_hdr":false,"max_nits":270.04}]}
            """), Now)!;

        Assert.Equal(DeskHdrStates.Unsupported, hdr.State);
        Assert.Null(hdr.Monitor);
        Assert.Equal("NVIDIA GeForce RTX 5070", hdr.Adapter);
        Assert.Equal(8, hdr.BitsPerColor);
        Assert.Equal(270.0, hdr.MaxNits);
        Assert.Equal(Now, hdr.UpdatedAt);
    }

    [Fact]
    public void The_display_in_hdr_describes_the_desk()
    {
        var hdr = IpcDisplayHdr.FromJson(Json("""
            {"state":"enabled","displays":[
              {"name":"\\\\.\\DISPLAY1","adapter":"A","hdr_supported":false,"dxgi_hdr":false,"bits_per_color":8},
              {"name":"\\\\.\\DISPLAY2","adapter":"B","hdr_supported":true,"dxgi_hdr":true,"bits_per_color":10}]}
            """), Now)!;

        Assert.Equal(DeskHdrStates.Enabled, hdr.State);
        Assert.Equal("B", hdr.Adapter);
        Assert.Equal(10, hdr.BitsPerColor);
    }

    [Fact]
    public void Odd_data_is_read_leniently()
    {
        Assert.Null(IpcDisplayHdr.FromJson(null, Now));
        Assert.Null(IpcDisplayHdr.FromJson(Json("[]"), Now));

        var odd = IpcDisplayHdr.FromJson(Json("""{"state":"maybe","displays":[1,{"bits_per_color":"8","max_nits":-1,"adapter":7}]}"""), Now)!;
        Assert.Equal(DeskHdrStates.Unknown, odd.State);
        Assert.Null(odd.Adapter);
        Assert.Null(odd.BitsPerColor);
        Assert.Null(odd.MaxNits);

        var empty = IpcDisplayHdr.FromJson(Json("""{"state":"off"}"""), Now)!;
        Assert.Equal(DeskHdrStates.Off, empty.State);
        Assert.Null(empty.Adapter);
    }
}
