using System;

namespace GlavaSharp.Shaders;

/// <summary>
///     Iterative radix-2 Cooley-Tukey FFT, CPU-side. Temporary stand-in for the
///     GPU compute-shader version (<c>GpuFft</c>, kept in the repo but currently
///     unused): that shader hangs glCompileShader/glLinkProgram on Mesa/Intel
///     (iris/NIR trying to fully unroll+SROA a large dynamically-indexed
///     `shared` array — see GpuFft.cs comments). Same public surface (N, Bins,
///     Process) so <see cref="Windowing.AppWindow" /> doesn't need to change
///     beyond the type name. Swap back to GpuFft once the driver-side issue is
///     sorted, or once we verify the uniform-LOGN fix actually resolves it on
///     real hardware. Size/gravity/gain are configured via <see cref="FftSettings" />
///     (see Program.cs for the CLI flags and <see cref="RcConfig" /> for the
///     rc.glsl-derived defaults).
/// </summary>
public sealed class CpuFft : IDisposable
{
    // Gravity: rises fast (attack), falls slowly (decay) — same feel as
    // GLava's util/gravity_pass.frag. Sourced from FftSettings so the CLI
    // (and rc.glsl, where GLava has an equivalent) can tune these.
    private readonly float _attack;
    private readonly int[] _bitRev;
    private readonly float[] _cosTable, _sinTable; // twiddle factors, one full period at N resolution
    private readonly float _decay;
    private readonly float _gain;

    private readonly float[] _hann;

    private readonly float[] _rawOutL, _rawOutR;

    // output needs this correction on top of the usual /N.
    private readonly float[] _reL, _imL, _reR, _imR; // working buffers, in-place FFT
    private readonly float[] _smoothL, _smoothR; // gravity-smoothed, mirrors glava's gravity/avg transforms
    private readonly float _windowGain; // mean of _hann; Hann halves average amplitude, so raw FFT

    public CpuFft(FftSettings? settings = null)
    {
        settings ??= new FftSettings();
        var n = settings.Size;
        if ((n & (n - 1)) != 0) throw new ArgumentException("Size must be a power of two", nameof(settings));
        N = n;
        _attack = settings.Attack;
        _decay = settings.Decay;
        _gain = settings.Gain;

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
        _rawOutL = new float[Bins];
        _rawOutR = new float[Bins];
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
    public int Bins => N / 2;

    public void Dispose()
    {
        //nothing to release for cpu fft.
    }

    /// <summary>
    ///     Runs one FFT for interleaved stereo PCM (length &gt;= N*2), windows
    ///     it, and returns smoothed magnitude spectra (length Bins each, values
    ///     roughly in [0,1]) for left/right.
    /// </summary>
    public (float[] left, float[] right) Process(ReadOnlySpan<float> interleavedStereo)
    {
        var avail = interleavedStereo.Length / 2;
        var take = Math.Min(N, avail);
        var offset = avail - take; // most recent `take` frames

        Array.Clear(_reL);
        Array.Clear(_imL);
        Array.Clear(_reR);
        Array.Clear(_imR);
        for (var i = 0; i < take; i++)
        {
            var w = _hann[N - take + i];
            var dst = _bitRev[N - take + i];
            _reL[dst] = interleavedStereo[2 * (offset + i)] * w;
            _reR[dst] = interleavedStereo[2 * (offset + i) + 1] * w;
        }

        Transform(_reL, _imL);
        Transform(_reR, _imR);

        var norm = 2f / (N * _windowGain); // single-sided spectrum x2, corrected for Hann's ~0.5 mean gain
        for (var i = 0; i < Bins; i++)
        {
            var magL = MathF.Sqrt(_reL[i] * _reL[i] + _imL[i] * _imL[i]) * norm;
            var magR = MathF.Sqrt(_reR[i] * _reR[i] + _imR[i] * _imR[i]) * norm;
            _rawOutL[i] = Math.Clamp(MathF.Log(1f + magL * _gain) / MathF.Log(1f + _gain), 0f, 1f);
            _rawOutR[i] = Math.Clamp(MathF.Log(1f + magR * _gain) / MathF.Log(1f + _gain), 0f, 1f);
        }

        ApplyGravity(_rawOutL, _smoothL);
        ApplyGravity(_rawOutR, _smoothR);
        return (_smoothL, _smoothR);
    }

    /// <summary>
    ///     In-place iterative DIT FFT. `re`/`im` must already be in
    ///     bit-reversed order (done during the windowing copy above).
    /// </summary>
    private void Transform(float[] re, float[] im)
    {
        for (var size = 2; size <= N; size <<= 1)
        {
            var half = size >> 1;
            var tableStride = N / size; // twiddle table index step for this stage
            for (var start = 0; start < N; start += size)
            for (var k = 0; k < half; k++)
            {
                var evenIdx = start + k;
                var oddIdx = evenIdx + half;

                var twR = _cosTable[k * tableStride];
                var twI = _sinTable[k * tableStride];

                var oddRe = re[oddIdx] * twR - im[oddIdx] * twI;
                var oddIm = re[oddIdx] * twI + im[oddIdx] * twR;

                var evenRe = re[evenIdx];
                var evenIm = im[evenIdx];

                re[evenIdx] = evenRe + oddRe;
                im[evenIdx] = evenIm + oddIm;
                re[oddIdx] = evenRe - oddRe;
                im[oddIdx] = evenIm - oddIm;
            }
        }
    }

    private void ApplyGravity(float[] raw, float[] smoothed)
    {
        for (var i = 0; i < raw.Length; i++)
        {
            var rate = raw[i] > smoothed[i] ? _attack : _decay;
            smoothed[i] += (raw[i] - smoothed[i]) * rate;
        }
    }
}