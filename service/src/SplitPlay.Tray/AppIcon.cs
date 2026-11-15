// SPDX-License-Identifier: MIT
namespace SplitPlay.Tray;

/// <summary>
/// The SplitPlay icon (service/assets/splitplay.ico, 16-256 px, an embedded resource) for the dashboard window and the
/// notification area. The exe's own icon (Explorer, taskbar, shortcuts) is the same file via ApplicationIcon.
/// </summary>
internal static class AppIcon
{
    private static Icon? _all;

    /// <summary>Every size: the window picks its title bar and Alt+Tab sizes from it.</summary>
    public static Icon All => _all ??= Load() ?? SystemIcons.Application;

    /// <summary>The notification-area size at the current DPI (16 px at 100 %, 24 at 150 %, ...).</summary>
    public static Icon Small() => new(All, SystemInformation.SmallIconSize);

    private static Icon? Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("SplitPlay.Tray.splitplay.ico");
        return stream is null ? null : new Icon(stream);
    }
}
