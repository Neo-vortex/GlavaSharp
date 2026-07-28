using System;

namespace GlavaSharp.Audio;

/// <summary>
///     Fixed-size, continuously-updated tail buffer of interleaved stereo
///     samples for the render thread. <see cref="RingBuffer.Read" /> is
///     destructive (it advances a read cursor), so this sits on top of it to
///     keep the most recent N frames around across render calls even when a
///     given frame produces fewer new samples than the FFT window needs.
/// </summary>
public sealed class AudioWindow(int capacityFrames, int channels, int scratchFrames = 4096)
{
    private readonly float[] _interleaved = new float[capacityFrames * channels]; // capacityFrames * channels
    private readonly float[] _scratch = new float[scratchFrames * channels];

    //todo lets use this later
    public int CapacityFrames => _interleaved.Length / channels;

    public ReadOnlySpan<float> Snapshot => _interleaved;

    /// <summary>Pulls everything currently available from <paramref name="source" /> and folds it in.</summary>
    public void Pump(IAudioSource source)
    {
        int read;
        while ((read = source.Read(_scratch)) > 0) Append(_scratch.AsSpan(0, read));
    }

    private void Append(ReadOnlySpan<float> newInterleaved)
    {
        var n = newInterleaved.Length;
        if (n >= _interleaved.Length)
        {
            // New chunk alone covers (or exceeds) the whole window.
            newInterleaved[^_interleaved.Length..].CopyTo(_interleaved);
            return;
        }

        var keep = _interleaved.Length - n;
        Array.Copy(_interleaved, n, _interleaved, 0, keep);
        newInterleaved.CopyTo(_interleaved.AsSpan(keep));
    }
}