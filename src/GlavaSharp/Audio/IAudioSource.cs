using System;

namespace GlavaSharp.Audio;

/// <summary>
///     A live PCM source. Implementations push samples in on whatever thread
///     their backend delivers them on; consumers pull from <see cref="Read" />
///     on the render thread. Kept deliberately backend-agnostic so PipeWire
///     today doesn't lock out PulseAudio-compat or WASAPI later.
/// </summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Sample rate actually negotiated with the backend.</summary>
    int SampleRate { get; }

    /// <summary>Channel count actually negotiated with the backend.</summary>
    int Channels { get; }

    void Start();
    void Stop();

    /// <summary>
    ///     Copies up to <paramref name="destination" />.Length interleaved
    ///     float samples out of the internal ring buffer. Returns the number
    ///     of samples actually written. Non-blocking — returns 0 if nothing
    ///     new is available yet.
    /// </summary>
    int Read(Span<float> destination);
}