// SPDX-License-Identifier: MIT
namespace SplitPlay.Core.Host;

/// <summary>
/// <c>host.composeIntervalMs</c>: the DWM composition interval of remote sessions (<c>DWMFRAMEINTERVAL</c>), or
/// <see cref="Auto"/>.
/// </summary>
public static class ComposeInterval
{
    /// <summary><c>host.composeIntervalMs = 0</c>: the service picks the interval from the fastest desk.</summary>
    public const int Auto = 0;

    /// <summary>What Auto uses while no desk has a frame rate (250 Hz, the fixed default before Auto).</summary>
    public const int AutoSlowestMs = 4;

    /// <summary>The fastest interval Auto picks (500 Hz).</summary>
    public const int AutoFastestMs = 2;

    /// <summary>
    /// The interval to write. A configured value (1–100 ms) is used as is. Auto composes at least twice as often as the
    /// fastest stream (so a game frame waits at most half a stream interval for DWM) and never slower than 250 Hz:
    /// up to 125 fps 4 ms, 126–166 fps 3 ms, 167 fps and more 2 ms.
    /// </summary>
    /// <param name="configured"><c>host.composeIntervalMs</c>.</param>
    /// <param name="fastestFps">Highest frame rate a desk streams or is set to; 0 or less when none is known.</param>
    public static int Resolve(int configured, int fastestFps)
    {
        if (configured != Auto)
        {
            return Math.Clamp(configured, 1, 100);
        }

        if (fastestFps <= 0)
        {
            return AutoSlowestMs;
        }

        return Math.Clamp(1000 / (2 * fastestFps), AutoFastestMs, AutoSlowestMs);
    }
}
