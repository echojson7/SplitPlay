// SPDX-License-Identifier: MIT
using SplitPlay.Core.Api;

namespace SplitPlay.Service.Hosting;

/// <summary>
/// Turns live GPU numbers into "encoder busy" and "VRAM full" warnings. A warning comes on when the value has stayed at
/// or above its threshold for <see cref="Sustain"/> and goes off once it drops below a lower threshold, so a short peak
/// or a value hovering at the line does not make it flicker. Thread-safe; fed by every metrics read.
/// </summary>
public sealed class GpuLoadWarnings
{
    public static readonly TimeSpan Sustain = TimeSpan.FromSeconds(5);

    /// <summary>NVENC at or above this: new streams and higher fps start to queue for the encoder.</summary>
    public const double EncoderOnPercent = 90;

    public const double EncoderOffPercent = 80;

    /// <summary>Dedicated memory at or above this: Windows starts paging textures to system memory and frames stutter.</summary>
    public const double VramOnPercent = 95;

    public const double VramOffPercent = 90;

    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public GpuLoadWarnings(TimeProvider time, ILogger logger)
    {
        _time = time;
        _logger = logger;
    }

    /// <summary>The warnings for these adapters now. Adapters without numbers (counters off) have none.</summary>
    public IReadOnlyList<HostWarningDto> Update(IReadOnlyList<GpuDto> gpus)
    {
        var now = _time.GetTimestamp();
        var warnings = new List<HostWarningDto>();
        lock (_gate)
        {
            foreach (var gpu in gpus)
            {
                if (!_states.TryGetValue(gpu.Luid, out var state))
                {
                    state = new State();
                    _states[gpu.Luid] = state;
                }

                if (state.Encoder.Update(gpu.EncoderPercent, EncoderOnPercent, EncoderOffPercent, now, _time))
                {
                    _logger.LogWarning("{Gpu}: the video encoder has been {Percent:0} % busy for {Seconds} s; streams may drop frames",
                        gpu.Name, gpu.EncoderPercent, (int)Sustain.TotalSeconds);
                }

                var vram = gpu.VramUsedMb is { } used && gpu.VramTotalMb is { } total && total > 0 ? used / total * 100 : (double?)null;
                if (state.Vram.Update(vram, VramOnPercent, VramOffPercent, now, _time))
                {
                    _logger.LogWarning("{Gpu}: video memory has been {Percent:0} % full for {Seconds} s; games and streams may stutter",
                        gpu.Name, vram, (int)Sustain.TotalSeconds);
                }

                if (state.Encoder.On)
                {
                    warnings.Add(new HostWarningDto(HostWarningCodes.EncoderBusy, gpu.Name, Math.Round(gpu.EncoderPercent ?? 0)));
                }

                if (state.Vram.On)
                {
                    warnings.Add(new HostWarningDto(HostWarningCodes.VramFull, gpu.Name, Math.Round(vram ?? 0)));
                }
            }
        }

        return warnings;
    }

    private sealed class State
    {
        public Hysteresis Encoder { get; } = new();

        public Hysteresis Vram { get; } = new();
    }

    private sealed class Hysteresis
    {
        private long? _highSince;

        public bool On { get; private set; }

        /// <summary>Feeds one value; true when the warning has just come on.</summary>
        public bool Update(double? value, double on, double off, long now, TimeProvider time)
        {
            if (value is not { } v)
            {
                // No numbers (the counters stopped): nothing to warn about until they are back.
                _highSince = null;
                On = false;
                return false;
            }

            if (On)
            {
                if (v < off)
                {
                    On = false;
                    _highSince = null;
                }

                return false;
            }

            if (v < on)
            {
                _highSince = null;
                return false;
            }

            _highSince ??= now;
            if (time.GetElapsedTime(_highSince.Value, now) >= Sustain)
            {
                On = true;
                return true;
            }

            return false;
        }
    }
}
