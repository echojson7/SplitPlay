// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Nvidia;

/// <summary>
/// One loaded copy of the NVIDIA driver settings database (NvAPI DRS session with <c>NvAPI_DRS_LoadSettings</c> done).
/// Changes stay in memory until <see cref="Save"/>. A profile name of null means the base (global) profile.
/// Every failure throws <see cref="NvidiaDrsException"/>.
/// </summary>
public interface IDrsSession : IDisposable
{
    bool ProfileExists(string name);

    /// <summary>Creates an empty user profile; throws when the name is in use.</summary>
    void CreateProfile(string name);

    /// <summary>Deletes the profile and its applications; does nothing when it does not exist.</summary>
    void DeleteProfile(string name);

    /// <summary>True when <paramref name="executable"/> (a file name such as <c>splitplay-worker.exe</c>) is in the profile.</summary>
    bool HasApplication(string profile, string executable);

    /// <summary>Adds the executable to the profile; throws when another profile already has it.</summary>
    void AddApplication(string profile, string executable);

    /// <summary>
    /// The profile the driver applies to <paramref name="executable"/> (<c>NvAPI_DRS_FindApplicationByName</c>), or null
    /// when no profile lists it (the base profile applies). Games are often in one of NVIDIA's predefined profiles.
    /// </summary>
    string? FindApplicationProfile(string executable);

    /// <summary>
    /// True when the profile's own value of the setting is NVIDIA's predefined one (not changed by anybody):
    /// <see cref="DeleteSetting"/> brings it back.
    /// </summary>
    bool IsPredefined(string? profile, uint settingId);

    /// <summary>
    /// The DWORD value set in this profile itself, or null when the profile does not set it (the value then comes from
    /// the global profile or the driver default).
    /// </summary>
    uint? GetSetting(string? profile, uint settingId);

    void SetSetting(string? profile, uint settingId, uint value);

    /// <summary>
    /// Removes the profile's own value (back to the inherited or default one, or to NVIDIA's predefined value in a
    /// predefined profile); nothing when it is not set.
    /// </summary>
    void DeleteSetting(string? profile, uint settingId);

    void Save();
}

/// <summary>Opens <see cref="IDrsSession"/>s.</summary>
public interface IDrsProvider
{
    /// <summary>A session with the settings loaded, or null when there is no NVIDIA driver (or no NVIDIA GPU).</summary>
    IDrsSession? Open();
}

/// <summary>An NvAPI DRS call failed.</summary>
public sealed class NvidiaDrsException(string message) : Exception(message);

/// <summary>Setting ids and values from NVIDIA's <c>NvApiDriverSettings.h</c> (R5xx).</summary>
public static class DrsSettings
{
    /// <summary>Power management mode (<c>PREFERRED_PSTATE_ID</c>).</summary>
    public const uint PreferredPState = 0x1057EB71;

    /// <summary><c>PREFERRED_PSTATE_PREFER_MAX</c>: "Prefer maximum performance".</summary>
    public const uint PreferredPStatePreferMax = 0x00000001;

    /// <summary>Vulkan/OpenGL present method (<c>OGL_CPL_PREFER_DXPRESENT_ID</c>).</summary>
    public const uint OglPreferDxPresent = 0x20D690F8;

    /// <summary><c>OGL_CPL_PREFER_DXPRESENT_PREFER_ENABLED</c>: "Prefer layered on DXGI swapchain".</summary>
    public const uint OglPreferDxPresentEnabled = 0x00000001;

    /// <summary>Frame rate limiter, "Max Frame Rate" in NVIDIA Control Panel (<c>FRL_FPS_ID</c>, 0 = off); for the game FPS limits.</summary>
    public const uint FrameRateLimit = 0x10835002;

    /// <summary>The largest <see cref="FrameRateLimit"/> value (<c>FRL_FPS_MAX</c>).</summary>
    public const uint FrameRateLimitMax = 0x3FF;

    /// <summary>A readable name for logs.</summary>
    public static string Name(uint id) => id switch
    {
        PreferredPState => "PREFERRED_PSTATE",
        OglPreferDxPresent => "OGL_CPL_PREFER_DXPRESENT",
        FrameRateLimit => "FRL_FPS",
        _ => $"0x{id:X8}",
    };
}
