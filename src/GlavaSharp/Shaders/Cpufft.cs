using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace GlavaSharp.Shaders;

/// <summary>
///     Iterative radix-2 Cooley-Tukey FFT, CPU-side. Default backend selected
///     via <see cref="FftSettings.Device" /> (<c>--fft-device cpu</c>, the
///     default) -- see <c>GpuFft</c> for the compute-shader backend that
///     implements the exact same math on the GPU. Implements <see cref="IFft" />
///     so <see cref="Windowing.AppWindow" /> can swap backends without caring
///     which one it got. Size/gravity/gain are configured via
///     <see cref="FftSettings" /> (see Program.cs for the CLI flags and
///     <see cref="RcConfig" /> for the rc.glsl-derived defaults).
///
///     PERF NOTES (this file):
///      - Requires &lt;AllowUnsafeBlocks&gt;true&lt;/AllowUnsafeBlocks&gt; in the csproj.
///      - The butterfly stage (<see cref="Transform" />) is vectorized with
///        AVX2 + FMA when available (8 lanes/iteration, gathered twiddle
///        factors so every stage -- not just the contiguous last one --
///        gets SIMD'd). Falls back to a scalar loop on non-AVX2 hardware
///        (ARM/older x86), so behavior/output is identical either way.
///      - Magnitude (sqrt(re^2+im^2)) is vectorized; MathF.Log stays scalar
///        since there's no hardware transcendental, but the per-call
///        invariant log(1+gain) is now hoisted instead of recomputed
///        N/2 times per Process() call (it was previously recomputed
///        every bin, every frame -- pure waste).
///      - All hot loops run over `fixed`-pinned pointers to drop bounds
///        checks and array-length reloads.
/// </summary>
public sealed class CpuFft : IFft
{
    // Gravity: rises fast (attack), falls slowly (decay) — same feel as
    // GLava's util/gravity_pass.frag. Sourced from FftSettings so the CLI
    // (and rc.glsl, where GLava has an equivalent) can tune these.
    private readonly float _attack;
    private readonly int[] _bitRev;
    private readonly float[] _cosTable, _sinTable; // twiddle factors, one full period at N resolution
    private readonly float _decay;
    private readonly float _gain;
    private readonly float _invLogGain; // 1 / log(1 + gain), hoisted out of the per-bin hot loop

    private readonly float[] _hann;

    // Raw, linearly-spaced per-bin magnitude (length N/2, always -- the raw
    // FFT output resolution, independent of whether/how it's bucketed).
    private readonly float[] _rawOutL, _rawOutR;

    // output needs this correction on top of the usual /N.
    private readonly float[] _reL, _imL, _reR, _imR; // working buffers, in-place FFT

    // Perceptual bucketing (see FrequencyBucketing), null for FrequencyScale.Linear
    // -- in which case _rawOutL/_rawOutR feed ApplyGravity directly instead.
    private readonly FrequencyBucketing? _bucketing;

    // Bucketed-but-not-yet-gravity-smoothed scratch (length Bins), only
    // allocated/used when _bucketing is non-null.
    private readonly float[]? _bucketedL, _bucketedR;

    private readonly float[] _smoothL, _smoothR; // gravity-smoothed, mirrors glava's gravity/avg transforms; length Bins
    private readonly float _windowGain; // mean of _hann; Hann halves average amplitude, so raw FFT

    // Whether the AVX2+FMA butterfly path is usable on this CPU. Checked
    // once at construction rather than re-probed every Transform() call.
    private readonly bool _useAvx2;

    public CpuFft(FftSettings? settings = null)
    {
        settings ??= new FftSettings();
        var n = settings.Size;
        if ((n & (n - 1)) != 0) throw new ArgumentException("Size must be a power of two", nameof(settings));
        N = n;
        _attack = settings.Attack;
        _decay = settings.Decay;
        _gain = settings.Gain;
        _invLogGain = 1f / MathF.Log(1f + _gain);
        _useAvx2 = Avx2.IsSupported && Fma.IsSupported;
        Log.Debug(_useAvx2
            ? "CpuFft: AVX2+FMA available, using the vectorized butterfly path"
            : "CpuFft: AVX2+FMA not available on this CPU, using the scalar fallback");

        _hann = new float[N];
        for (var i = 0; i < N; i++)
            _hann[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (N - 1));
        var sum = 0f;
        for (var i = 0; i < N; i++) sum += _hann[i];
        _windowGain = sum / N; // ~0.5 for Hann

        _reL = new float[N];
        _imL = new float[N];
        _reR = new float[N];
        _imR = new float[N];
        _rawOutL = new float[N / 2];
        _rawOutR = new float[N / 2];

        if (settings.Scale != FrequencyScale.Linear)
        {
            _bucketing = new FrequencyBucketing(settings.Scale, N / 2, N / 2, settings.SampleRate);
            Bins = _bucketing.BucketCount;
            _bucketedL = new float[Bins];
            _bucketedR = new float[Bins];
        }
        else
        {
            Bins = N / 2;
        }

        _smoothL = new float[Bins];
        _smoothR = new float[Bins];

        var logN = (int)Math.Log2(N);
        _bitRev = new int[N];
        for (var i = 0; i < N; i++)
        {
            int r = 0, x = i;
            var b = 0;
            for (; b < logN; b++)
            {
                r = (r << 1) | (x & 1);
                x >>= 1;
            }

            _bitRev[i] = r;
        }

        // cosTable[k]/sinTable[k] = twiddle for angle -2*pi*k/N, k in [0, N/2).
        _cosTable = new float[N / 2];
        _sinTable = new float[N / 2];
        var k = 0;
        for (; k < N / 2; k++)
        {
            var angle = -2f * MathF.PI * k / N;
            _cosTable[k] = MathF.Cos(angle);
            _sinTable[k] = MathF.Sin(angle);
        }
    }

    public int N { get; }

    /// <summary>Length of the arrays <see cref="Process" /> returns -- N/2 raw bins, or <see cref="FrequencyBucketing.BucketCount" /> when a perceptual scale is active.</summary>
    public int Bins { get; }

    /// <summary>Whether <see cref="Transform" /> is actually taking the AVX2+FMA path on this CPU (see <see cref="_useAvx2" />) -- exposed for <c>--benchmark-fft</c> and startup logging, not used internally.</summary>
    public bool UsingAvx2 => _useAvx2;

    public void Dispose()
    {
        //nothing to release for cpu fft.
    }

    /// <summary>
    ///     Runs one FFT for interleaved stereo PCM (length &gt;= N*2), windows
    ///     it, perceptually rebuckets the raw N/2-bin spectrum if
    ///     <see cref="FftSettings.Scale" /> isn't <see cref="FrequencyScale.Linear" />,
    ///     and returns smoothed magnitude spectra (length <see cref="Bins" />
    ///     each, values roughly in [0,1]) for left/right.
    /// </summary>
    public unsafe (float[] left, float[] right) Process(ReadOnlySpan<float> interleavedStereo)
    {
        var avail = interleavedStereo.Length / 2;
        var take = Math.Min(N, avail);
        var offset = avail - take; // most recent `take` frames

        Array.Clear(_reL);
        Array.Clear(_imL);
        Array.Clear(_reR);
        Array.Clear(_imR);

        fixed (float* hannP = _hann, reLP = _reL, imLP = _imL, reRP = _reR, imRP = _imR)
        fixed (int* bitRevP = _bitRev)
        fixed (float* srcP = interleavedStereo)
        {
            var hannBase = N - take;
            for (var i = 0; i < take; i++)
            {
                var w = hannP[hannBase + i];
                var dst = bitRevP[hannBase + i];
                var srcIdx = 2 * (offset + i);
                reLP[dst] = srcP[srcIdx] * w;
                reRP[dst] = srcP[srcIdx + 1] * w;
            }
        }

        Transform(_reL, _imL);
        Transform(_reR, _imR);

        ComputeMagnitude(_reL, _imL, _rawOutL);
        ComputeMagnitude(_reR, _imR, _rawOutR);

        if (_bucketing != null)
        {
            _bucketing.Apply(_rawOutL, _bucketedL!);
            _bucketing.Apply(_rawOutR, _bucketedR!);
            ApplyGravity(_bucketedL!, _smoothL);
            ApplyGravity(_bucketedR!, _smoothR);
        }
        else
        {
            ApplyGravity(_rawOutL, _smoothL);
            ApplyGravity(_rawOutR, _smoothR);
        }

        return (_smoothL, _smoothR);
    }

    /// <summary>
    ///     magOut[i] = clamp(log(1 + gain * sqrt(re[i]^2 + im[i]^2)) / log(1+gain), 0, 1),
    ///     for i in [0, N/2). The sqrt/square/scale/clamp portion is done
    ///     8-wide with AVX; MathF.Log has no vector form so it stays scalar
    ///     (the sqrt is the part that actually shows up in profiles for
    ///     N in the thousands, since it used to run through the scalar
    ///     MathF.Sqrt path once per bin per channel).
    /// </summary>
    private unsafe void ComputeMagnitude(float[] re, float[] im, float[] magOut)
    {
        var len = magOut.Length;
        var norm = 2f / (N * _windowGain);
        var invLogGain = _invLogGain;
        var gain = _gain;

        fixed (float* reP = re, imP = im, outP = magOut)
        {
            var i = 0;

            if (Avx.IsSupported)
            {
                var normV = Vector256.Create(norm);
                var one = Vector256.Create(1f);
                var gainV = Vector256.Create(gain);
                // Hoisted out of the loop -- a stackalloc *inside* a loop body
                // reserves fresh stack space every iteration (CA2014); the
                // JIT doesn't reliably collapse that back down to one
                // allocation on its own, so for large N this was one unbounded
                // stack growth away from a real stack-overflow risk instead of
                // a one-time 32-byte scratch buffer.
                var buf = stackalloc float[8];

                for (; i + 8 <= len; i += 8)
                {
                    var reV = Avx.LoadVector256(reP + i);
                    var imV = Avx.LoadVector256(imP + i);
                    var sq = Avx.Add(Avx.Multiply(reV, reV), Avx.Multiply(imV, imV));
                    var mag = Avx.Multiply(Avx.Sqrt(sq), normV);
                    // argument to log: 1 + mag*gain
                    var arg = Avx.Add(one, Avx.Multiply(mag, gainV));

                    // MathF.Log has no AVX intrinsic in BCL -- extract, log, clamp+store scalar.
                    Avx.Store(buf, arg);
                    for (var lane = 0; lane < 8; lane++)
                    {
                        var v = MathF.Log(buf[lane]) * invLogGain;
                        outP[i + lane] = v < 0f ? 0f : v > 1f ? 1f : v;
                    }
                }
            }

            for (; i < len; i++)
            {
                var mag = MathF.Sqrt(reP[i] * reP[i] + imP[i] * imP[i]) * norm;
                var v = MathF.Log(1f + mag * gain) * invLogGain;
                outP[i] = v < 0f ? 0f : v > 1f ? 1f : v;
            }
        }
    }

    /// <summary>
    ///     In-place iterative DIT FFT. `re`/`im` must already be in
    ///     bit-reversed order (done during the windowing copy above).
    ///     Vectorized 8-lanes-at-a-time with AVX2+FMA when supported --
    ///     twiddle factors for a stage sit at stride `tableStride` in
    ///     _cosTable/_sinTable, so they're loaded with a gather instead of
    ///     assuming contiguity (only the very last stage is contiguous).
    /// </summary>
    private unsafe void Transform(float[] re, float[] im)
    {
        fixed (float* reP = re, imP = im, cosP = _cosTable, sinP = _sinTable)
        {
            for (var size = 2; size <= N; size <<= 1)
            {
                var half = size >> 1;
                var tableStride = N / size; // twiddle table index step for this stage

                if (_useAvx2 && half >= 8)
                {
                    // Per-lane twiddle-table offsets for a gather starting at
                    // k*tableStride: lane j reads index (k+j)*tableStride, i.e.
                    // base + j*tableStride for j in [0,8).
                    var idxOffsets = Vector256.Create(
                        0, tableStride, 2 * tableStride, 3 * tableStride,
                        4 * tableStride, 5 * tableStride, 6 * tableStride, 7 * tableStride);

                    for (var start = 0; start < N; start += size)
                    {
                        var k = 0;
                        for (; k + 8 <= half; k += 8)
                        {
                            var evenIdx = start + k;
                            var oddIdx = evenIdx + half;

                            var gatherIdx = Vector256.Create(k * tableStride) + idxOffsets;

                            var twR = Avx2.GatherVector256(cosP, gatherIdx, 4);
                            var twI = Avx2.GatherVector256(sinP, gatherIdx, 4);

                            var oddRe = Avx.LoadVector256(reP + oddIdx);
                            var oddIm = Avx.LoadVector256(imP + oddIdx);
                            var evenRe = Avx.LoadVector256(reP + evenIdx);
                            var evenIm = Avx.LoadVector256(imP + evenIdx);

                            // (oddRe + i*oddIm) * (twR + i*twI)
                            var rOddRe = Fma.MultiplySubtract(oddRe, twR, Avx.Multiply(oddIm, twI));
                            var rOddIm = Fma.MultiplyAdd(oddRe, twI, Avx.Multiply(oddIm, twR));

                            Avx.Store(reP + evenIdx, Avx.Add(evenRe, rOddRe));
                            Avx.Store(imP + evenIdx, Avx.Add(evenIm, rOddIm));
                            Avx.Store(reP + oddIdx, Avx.Subtract(evenRe, rOddRe));
                            Avx.Store(imP + oddIdx, Avx.Subtract(evenIm, rOddIm));
                        }

                        // scalar tail for half % 8 != 0
                        for (; k < half; k++)
                        {
                            var evenIdx = start + k;
                            var oddIdx = evenIdx + half;

                            var twR = cosP[k * tableStride];
                            var twI = sinP[k * tableStride];

                            var oddRe = reP[oddIdx] * twR - imP[oddIdx] * twI;
                            var oddIm = reP[oddIdx] * twI + imP[oddIdx] * twR;

                            var evenRe = reP[evenIdx];
                            var evenIm = imP[evenIdx];

                            reP[evenIdx] = evenRe + oddRe;
                            imP[evenIdx] = evenIm + oddIm;
                            reP[oddIdx] = evenRe - oddRe;
                            imP[oddIdx] = evenIm - oddIm;
                        }
                    }
                }
                else
                {
                    for (var start = 0; start < N; start += size)
                    for (var k = 0; k < half; k++)
                    {
                        var evenIdx = start + k;
                        var oddIdx = evenIdx + half;

                        var twR = cosP[k * tableStride];
                        var twI = sinP[k * tableStride];

                        var oddRe = reP[oddIdx] * twR - imP[oddIdx] * twI;
                        var oddIm = reP[oddIdx] * twI + imP[oddIdx] * twR;

                        var evenRe = reP[evenIdx];
                        var evenIm = imP[evenIdx];

                        reP[evenIdx] = evenRe + oddRe;
                        imP[evenIdx] = evenIm + oddIm;
                        reP[oddIdx] = evenRe - oddRe;
                        imP[oddIdx] = evenIm - oddIm;
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ApplyGravity(float[] raw, float[] smoothed)
    {
        var attack = _attack;
        var decay = _decay;
        for (var i = 0; i < raw.Length; i++)
        {
            var rate = raw[i] > smoothed[i] ? attack : decay;
            smoothed[i] += (raw[i] - smoothed[i]) * rate;
        }
    }
}
