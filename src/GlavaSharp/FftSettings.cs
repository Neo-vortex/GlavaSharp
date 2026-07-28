using GlavaSharp.Shaders;

namespace GlavaSharp;

/// <summary>
///     Which <see cref="IFft" /> implementation to instantiate. Set via
///     <c>--fft-device</c> on the CLI (see Program.cs) or <see cref="FftSettings.Device" />.
/// </summary>
public enum FftDevice
{
    /// <summary>
    ///     <see cref="CpuFft" />. Works everywhere, no GL 4.3/compute-shader
    ///     requirement. Default.
    /// </summary>
    Cpu,

    /// <summary>
    ///     <see cref="GpuFft" />. Requires a GL 4.3 context with compute
    ///     shader + SSBO support; N is capped at 2048 (single-workgroup).
    /// </summary>
    Gpu
}

/// <summary>
///     Tunables shared by both <see cref="CpuFft" /> and <see cref="GpuFft" />
///     (see <see cref="IFft" />). Exposed on the CLI (see Program.cs) and
///     defaulted from rc.glsl's `setbufsize`/`setsamplerate` where GLava's own
///     config DSL already has an equivalent knob (see <see cref="RcConfig" />).
/// </summary>
public sealed class FftSettings
{
    /// <summary>Which FFT backend to use. Corresponds to <c>--fft-device cpu|gpu</c>.</summary>
    public FftDevice Device { get; init; } = FftDevice.Cpu;

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