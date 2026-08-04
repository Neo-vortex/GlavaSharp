using System;

namespace GlavaSharp.Shaders;

/// <summary>
///     Common surface shared by <see cref="CpuFft" /> and <see cref="GpuFft" />
///     so <see cref="Windowing.AppWindow" /> can pick either backend at runtime
///     via <see cref="FftSettings.Device" /> without caring which one it got.
/// </summary>
public interface IFft : IDisposable
{
    /// <summary>FFT window/buffer size in samples (power of two).</summary>
    int N { get; }

    /// <summary>
    ///     Length of the arrays <see cref="Process" /> returns. Equal to N/2
    ///     (raw FFT bins) when <see cref="FftSettings.Scale" /> is
    ///     <see cref="FrequencyScale.Linear" />; otherwise the perceptual
    ///     bucket count (see <see cref="FrequencyBucketing" />) the raw N/2
    ///     bins get redistributed into before ever leaving this backend.
    /// </summary>
    int Bins { get; }

    /// <summary>
    ///     Runs one FFT for interleaved stereo PCM (length &gt;= N*2), windows
    ///     it, and returns smoothed magnitude spectra (length Bins each, values
    ///     roughly in [0,1]) for left/right.
    /// </summary>
    (float[] left, float[] right) Process(ReadOnlySpan<float> interleavedStereo);

    /// <summary>
    ///     Same computation as <see cref="Process" />, but writes the result
    ///     straight into <paramref name="textureL" />/<paramref name="textureR" />
    ///     instead of returning CPU arrays. <see cref="Windowing.AppWindow" />'s
    ///     render loop uses this exclusively -- for <see cref="CpuFft" /> it's
    ///     just <see cref="Process" /> followed by
    ///     <see cref="AudioSpectrumTexture.Upload" />, but for <see cref="GpuFft" />
    ///     it never round-trips the spectrum through the CPU at all: the
    ///     compute shader that produces it also writes it directly into these
    ///     textures via image store, since the textures are already GPU-side
    ///     and about to be sampled by a GPU-side render pass anyway.
    /// </summary>
    void ProcessToTexture(ReadOnlySpan<float> interleavedStereo, AudioSpectrumTexture textureL,
        AudioSpectrumTexture textureR);

    /// <summary>
    ///     Live-tweakable gravity/gain knobs, mirroring <see cref="FftSettings.Attack" />/
    ///     <see cref="FftSettings.Decay" />/<see cref="FftSettings.Gain" /> --
    ///     registered into <see cref="Control.PropertyStore" /> as
    ///     <c>fft.attack</c>/<c>fft.decay</c>/<c>fft.gain</c> by
    ///     <see cref="Windowing.AppWindow" /> so the live control channel can
    ///     tune them without a restart. <see cref="GpuFft" /> re-uploads the
    ///     corresponding GL uniform on each call, so these must only be
    ///     invoked from the render thread (the only thread with the GL
    ///     context current) -- same constraint <see cref="Process" />/
    ///     <see cref="ProcessToTexture" /> already have.
    /// </summary>
    void SetAttack(float attack);

    void SetDecay(float decay);

    void SetGain(float gain);
}
