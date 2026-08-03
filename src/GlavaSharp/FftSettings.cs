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
///     Which perceptual frequency scale redistributes raw (linearly-spaced)
///     FFT bins across the displayed spectrum before it ever reaches a
///     shader. Set via <c>--freq-scale</c> on the CLI (see Program.cs) --
///     see <see cref="FrequencyBucketing" /> for the actual bucket-edge
///     formulas and why this exists at all: with raw linear bins, most of
///     a typical track's audible energy (bass/low-mid) is crammed into a
///     handful of the lowest bins, so whichever shader-side mapping decides
///     "which screen position reads which bin" either redundantly samples
///     nearly the same few bass bins across a wide swath (looks static) or
///     spreads the sparse, noisier high-frequency bins across the rest
///     (looks disproportionately "active"). Perceptual bucketing fixes this
///     at the source instead of shader-side.
/// </summary>
public enum FrequencyScale
{
    /// <summary>
    ///     No bucketing -- raw, linearly-spaced FFT bins, exactly like
    ///     GlavaSharp before this option existed. Kept as an explicit
    ///     opt-out since every bundled module's own `util/smooth.glsl`
    ///     still applies its own (now redundant, if a real scale is picked)
    ///     log-ish warp on top of whatever's in the texture -- see
    ///     `_FREQ_PREBUCKETED` in that file for how the two stay consistent.
    /// </summary>
    Linear,

    /// <summary>Octave/log2 spacing -- simplest perceptual scale, one bucket covers a fixed frequency ratio. Default.</summary>
    Log2,

    /// <summary>Mel scale (pitch-perception based; standard in speech/ML feature extraction).</summary>
    Mel,

    /// <summary>Bark scale (Zwicker's 24 critical bands; coarser than ERB, especially below ~500Hz).</summary>
    Bark,

    /// <summary>ERB scale (Glasberg &amp; Moore; the closest match to actual measured hearing resolution, especially at low frequencies).</summary>
    Erb
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
    ///     Also determines raw bin count: N/2. Bigger = more frequency
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

    /// <summary>
    ///     Audio sample rate in Hz -- needed to convert raw bin index &lt;-&gt;
    ///     Hz for <see cref="Scale" />'s bucket-edge formulas (the FFT math
    ///     itself doesn't need this; only frequency-aware bucketing does).
    ///     Corresponds to GLava's `setsamplerate`; always explicitly set by
    ///     Program.cs from the same resolved value <see cref="Audio.PipeWireAudioSource" /> captures at.
    /// </summary>
    public int SampleRate { get; init; } = 44100;

    /// <summary>Which perceptual frequency scale to bucket raw FFT bins into. See <see cref="FrequencyScale" />.</summary>
    public FrequencyScale Scale { get; init; } = FrequencyScale.Log2;
}