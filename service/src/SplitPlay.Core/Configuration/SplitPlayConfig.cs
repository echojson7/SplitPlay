// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;
using SplitPlay.Core.Json;

namespace SplitPlay.Core.Configuration;

/// <summary>Root of <c>config.json</c> (docs/contracts/config.md).</summary>
public sealed class SplitPlayConfig
{
    /// <summary>The schema version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public HostConfig Host { get; set; } = new();

    public List<DeskConfig> Desks { get; set; } = [];

    /// <summary>Returns a deep copy (via JSON round-trip so that nested lists are copied too).</summary>
    public SplitPlayConfig DeepClone()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this, SplitPlayJsonContext.Default.SplitPlayConfig);
        return JsonSerializer.Deserialize(json, SplitPlayJsonContext.Default.SplitPlayConfig)!;
    }
}

/// <summary>Host-wide settings.</summary>
public sealed class HostConfig
{
    public int WebPort { get; set; } = BuildInfo.DefaultWebPort;

    public bool LanAccess { get; set; }

    public int BasePortStart { get; set; } = 48089;

    public int BasePortStride { get; set; } = 100;

    /// <summary>Written to <c>DWMFRAMEINTERVAL</c> by the service (2 = 500 Hz, 4 = 250 Hz).</summary>
    public int ComposeIntervalMs { get; set; } = 4;

    public string LogLevel { get; set; } = "info";

    /// <summary>Keep the PC from sleeping on its own while a Moonlight client streams from any desk (default true).</summary>
    public bool KeepAwake { get; set; } = true;
}

/// <summary>One desk (an isolated Windows session streamed to Moonlight).</summary>
public sealed class DeskConfig
{
    public string Id { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }

    /// <summary>
    /// True when SplitPlay created the Windows account (it then owns its password: never expires, rotated when the
    /// stored secret is lost, deletable with <c>deleteUser=true</c>). False for an existing account the owner picked:
    /// SplitPlay only stores the password the owner typed and never changes that account. Set by the service when the
    /// desk is created or its user changes; null (older configs) means "decide by the account's comment".
    /// The password itself never appears in config.json (DPAPI-protected in <c>secrets\desks\&lt;id&gt;.bin</c>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UserManaged { get; set; }

    /// <summary>Base port; when omitted the service allocates one (see <see cref="Ports.BasePortAllocator"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BasePort { get; set; }

    public bool Autostart { get; set; }

    /// <summary><c>"auto"</c> or an adapter LUID string <c>"0x…"</c>.</summary>
    public string Gpu { get; set; } = "auto";

    public ProfileConfig Profile { get; set; } = new();

    /// <summary>
    /// Stop the per-user <c>BluetoothUserService_*</c> instance in the desk session (GitHub issue #3): several running
    /// instances compete for the radio and can reset the console user's Bluetooth devices. Default true.
    /// </summary>
    public bool DisableBluetoothUserService { get; set; } = true;

    /// <summary>
    /// Per-desk Steam isolation (default true; null in a request keeps the current value): the desk user's Steam uses
    /// the IPC name <c>SplitPlay_&lt;id&gt;</c> so it runs next to the console user's Steam. See
    /// <see cref="Steam.SteamIsolation"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SteamIsolation { get; set; }

    /// <summary>The effective <see cref="SteamIsolation"/>.</summary>
    [JsonIgnore]
    public bool SteamIsolationEnabled => SteamIsolation ?? true;

    /// <summary>
    /// Keep the desk's account off the Windows sign-in screen (<see cref="Platform.LogonListing"/>). Null: on for an
    /// account SplitPlay created, off for any other; an account SplitPlay did not create is hidden only when this is
    /// set to true for the desk. A request that leaves it out keeps the current value unless the user changes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HideFromLogon { get; set; }

    /// <summary>The effective <see cref="HideFromLogon"/> for an account that SplitPlay did (or did not) create.</summary>
    public bool HidesFromLogon(bool createdBySplitPlay) => HideFromLogon ?? (createdBySplitPlay && UserManaged != false);

    /// <summary>
    /// Extra Sunshine settings passed verbatim to the worker as trailing <c>name=value</c> arguments (after
    /// <c>port=&lt;basePort&gt;</c>); they override the worker's <c>sunshine.conf</c>. See worker/README.md.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? WorkerSettings { get; set; }
}

/// <summary>Streaming profile. All fields optional; absent means "keep current / use default".</summary>
public sealed class ProfileConfig
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Fps { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BitrateKbps { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Codec { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FecPercent { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinFps { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DpbFrames { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PacingMbps { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GpuPriority { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GpuLuid { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? CpuSet { get; set; }
}
