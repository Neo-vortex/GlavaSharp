using System;
using System.Threading;

namespace GlavaSharp.Audio;

/// <summary>
///     Lock-free SPSC ring buffer. One writer (the PipeWire stream callback,
///     on PipeWire's own thread), one reader (the render loop). Capacity is
///     rounded up to a power of two so index wrapping is a cheap mask.
/// </summary>
public sealed class RingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private long _readIndex;
    private long _writeIndex;

    public RingBuffer(int capacity)
    {
        var pow2 = 1;
        while (pow2 < capacity) pow2 <<= 1;
        _buffer = new float[pow2];
        _mask = pow2 - 1;
    }

    public int Capacity => _buffer.Length;

    /// <summary>
    ///     Called from the audio thread. Drops oldest data on overflow
    ///     rather than blocking — a glitchy visualizer beats an audio-thread
    ///     stall.
    /// </summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        var writeIndex = Volatile.Read(ref _writeIndex);
        for (var i = 0; i < samples.Length; i++) _buffer[(writeIndex + i) & _mask] = samples[i];
        Volatile.Write(ref _writeIndex, writeIndex + samples.Length);
    }

    /// <summary>Called from the render thread. Returns samples written.</summary>
    public int Read(Span<float> destination)
    {
        var writeIndex = Volatile.Read(ref _writeIndex);
        var readIndex = Volatile.Read(ref _readIndex);
        var available = writeIndex - readIndex;

        // If the reader fell behind by more than a full buffer, the audio
        // thread has overwritten unread data — snap forward to the oldest
        // still-valid sample instead of reading garbage.
        if (available > _buffer.Length)
        {
            readIndex = writeIndex - _buffer.Length;
            available = _buffer.Length;
        }

        var toRead = (int)Math.Min(available, destination.Length);
        for (var i = 0; i < toRead; i++) destination[i] = _buffer[(readIndex + i) & _mask];
        Volatile.Write(ref _readIndex, readIndex + toRead);
        return toRead;
    }
}