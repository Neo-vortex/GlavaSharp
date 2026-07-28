using GlavaSharp.Shaders;

namespace GlavaSharp;

/// <summary>
///     Tunables for <see cref="CpuFft" />. Exposed on the CLI (see Program.cs)
///     and defaulted from rc.glsl's `setbufsize`/`setsamplerate` where GLava's
///     own config DSL already has an equivalent knob (see <see cref="RcConfig" />).
/// </summary>
public sealed class FftSettings
{
    /// <summary>
    ///     FFT window/buffer size in samples. Must be a power of two.
    ///     Also determines bin count: Bins = Size / 2. Bigger = more frequency
    ///     resolution and more "gravity" (slower to react), smaller = more
    ///     responsive but blockier. Corresponds to GLava's `setbufsize`.
    /// </summary>
    public int Size { get; init; } = 2048;

    /// <summary>
    ///     Gravity smoothing: how fast a bin's displayed value rises
    ///     toward a louder new reading, in [0,1] per frame.
    /// </summary>
    public float Attack { get; init; } = 0.6f;

    /// <summary>
    ///     Gravity smoothing: how fast a bin's displayed value falls
    ///     back down when the new reading is quieter, in [0,1] per frame.
    /// </summary>
    public float Decay { get; init; } = 0.08f;

    /// <summary>
    ///     Log-compression contrast applied to normalized magnitudes
    ///     before display (higher = more contrast between quiet and loud bins).
    /// </summary>
    public float Gain { get; init; } = 40.0f;
}