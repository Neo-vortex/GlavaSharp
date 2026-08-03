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
}
