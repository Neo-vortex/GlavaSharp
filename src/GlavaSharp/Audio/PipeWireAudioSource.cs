using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GlavaSharp.Audio;

public sealed unsafe class PipeWireAudioSource : IAudioSource, IDisposable
{
    // Ring buffer sized for ~1s of stereo float audio at 48kHz; generous
    // headroom since the render loop only ever needs the last FFT window.
    private const int RingCapacitySamples = 48_000 * 2;

    private readonly RingBuffer _ring = new(RingCapacitySamples);
    private readonly int _targetId;
    private IntPtr _ctx;
    private GCHandle _selfHandle;

    /// <param name="targetId">
    ///     PipeWire node id to capture from (see <see cref="AudioTargetEnumerator" />),
    ///     or -1 to capture the default sink's monitor.
    /// </param>
    public PipeWireAudioSource(int sampleRate = 48_000, int channels = 2, int targetId = -1)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _targetId = targetId;
    }

    public int SampleRate { get; }
    public int Channels { get; }

    public void Start()
    {
        if (_ctx != IntPtr.Zero) return;

        _selfHandle = GCHandle.Alloc(this);
        _ctx = PipeWireNative.pwshim_start(
            (uint)SampleRate,
            (uint)Channels,
            _targetId,
            &OnData,
            GCHandle.ToIntPtr(_selfHandle));

        if (_ctx != IntPtr.Zero) return;
        _selfHandle.Free();
        throw new InvalidOperationException(
            "pwshim_start failed — check stderr for PipeWire connection errors " +
            "(e.g. no default sink, invalid --sink id, or libpwshim.so not built/found).");
    }

    public void Stop()
    {
        if (_ctx != IntPtr.Zero)
        {
            PipeWireNative.pwshim_stop(_ctx);
            _ctx = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    public int Read(Span<float> destination)
    {
        return _ring.Read(destination);
    }

    public void Dispose()
    {
        // TODO release managed resources here
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnData(float* samples, uint sampleCount, IntPtr userData)
    {
        var handle = GCHandle.FromIntPtr(userData);
        if (handle.Target is PipeWireAudioSource self)
            self._ring.Write(new ReadOnlySpan<float>(samples, (int)sampleCount));
    }
}