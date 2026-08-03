using System;
using System.IO;
using OpenTK.Graphics.OpenGL;

namespace GlavaSharp.Shaders;

/// <summary>
///     GLSL 4.3 compute-shader FFT, selected via <c>--fft-device gpu</c> (see
///     <see cref="FftSettings.Device" />). Rewritten from scratch to be a
///     bit-for-bit-equivalent GPU port of <see cref="CpuFft" />: same Hann
///     window, same bit-reversal permutation, same iterative radix-2
///     Cooley-Tukey stage loop, same Hann-gain-corrected normalization, same
///     log-compression formula, and the same attack/decay/gain knobs pulled
///     from <see cref="FftSettings" /> instead of being hardcoded. Only the
///     windowing/bit-reversal (trivial, memory-bound) and the gravity
///     smoothing (inherently serial across frames, one value per bin) stay on
///     the CPU; the O(N log N) butterfly work happens on the GPU.
///     Single-workgroup (local_size_x = N/2), so the whole transform lives in
///     `shared` memory without ping-ponging buffers -- this caps N at 8192
///     (same limit <c>CpuFft</c> doesn't have, since it's not workgroup-bound,
///     but which matches the shared default of <see cref="FftSettings.Size" />).
///     The actual GLSL lives in <c>shaders/fft/radix2.comp</c> (see
///     <see cref="LoadKernelSource" />), not embedded here -- kept as its own
///     file/directory, sibling to where a future alternative kernel (a
///     different radix, a multi-workgroup approach not capped by
///     GL_MAX_COMPUTE_WORK_GROUP_INVOCATIONS the way this one is, etc.) could
///     go without touching this one.
/// </summary>
public sealed class GpuFft : IFft
{
    // shaders/fft/radix2.comp, resolved the same way ShaderModule resolves
    // shaders/glava/ -- relative to the published app's own directory, not
    // the working directory, so it's found regardless of where GlavaSharp
    // is actually launched from. Content-copied there by the .csproj's
    // existing `<Content Include="shaders/**/*">` item, same mechanism the
    // visualization modules already use -- no new build step needed.
    private static readonly string KernelPath =
        Path.Combine(AppContext.BaseDirectory, "shaders", "fft", "radix2.comp");

    // Same gravity smoothing as CpuFft.ApplyGravity, and sourced from the
    // same FftSettings (rather than hardcoded) so `--fft-attack`/`--fft-decay`
    // behave identically regardless of which backend is active.
    private readonly float _attack;
    private readonly float _decay;
    private readonly float _gain;

    private readonly int[] _bitRev;
    private readonly float[] _hann;
    private readonly float _windowGain;

    private readonly float[] _cpuInL, _cpuInR; // windowed + bit-reversed, real-valued (imag part is implicit 0)
    private readonly float[] _rawOutL, _rawOutR; // raw, linearly-spaced (length N/2), read back from the GPU

    // Perceptual bucketing (see FrequencyBucketing), null for FrequencyScale.Linear
    // -- in which case _rawOutL/_rawOutR feed ApplyGravity directly instead.
    // Same CPU-side bucketing CpuFft uses: cheap relative to the actual
    // O(N log N) transform, which stays on the GPU either way.
    private readonly FrequencyBucketing? _bucketing;

    // Bucketed-but-not-yet-gravity-smoothed scratch (length Bins), only
    // allocated/used when _bucketing is non-null.
    private readonly float[]? _bucketedL, _bucketedR;

    private readonly float[] _smoothL, _smoothR; // gravity-smoothed, CPU-side (inherently serial across frames); length Bins

    private readonly int _program;
    private readonly int _logNLoc, _normFactorLoc, _gainLoc;
    private readonly int _ssboInL, _ssboInR, _ssboOutL, _ssboOutR;

    public GpuFft(FftSettings? settings = null)
    {
        settings ??= new FftSettings();
        var n = settings.Size;
        if ((n & (n - 1)) != 0) throw new ArgumentException("Size must be a power of two", nameof(settings));
        if (n > 8192) throw new ArgumentException("Size must be <= 8192 (single-workgroup limit)", nameof(settings));
        N = n;
        _attack = settings.Attack;
        _decay = settings.Decay;
        _gain = settings.Gain;

        // --- Same precomputation as CpuFft: Hann window (+ its mean, for
        // --- normalization), and the bit-reversal permutation table. No
        // --- twiddle table needed here -- the shader computes cos/sin
        // --- per-invocation, same as the CPU version 
        // --- (CPU precomputes for speed; on the GPU, HALF invocations run
        // --- the trig in parallel so it's cheap either way).
        _hann = new float[N];
        for (var i = 0; i < N; i++)
            _hann[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (N - 1));
        var sum = 0f;
        for (var i = 0; i < N; i++) sum += _hann[i];
        _windowGain = sum / N; // ~0.5 for Hann, matches CpuFft._windowGain

        var logN = (int)Math.Log2(N);
        _bitRev = new int[N];
        for (var i = 0; i < N; i++)
        {
            int r = 0, x = i;
            for (var b = 0; b < logN; b++)
            {
                r = (r << 1) | (x & 1);
                x >>= 1;
            }

            _bitRev[i] = r;
        }

        _cpuInL = new float[N];
        _cpuInR = new float[N];
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

        _program = CompileCompute(LoadKernelSource(N));
        _logNLoc = GL.GetUniformLocation(_program, "u_logN");
        _normFactorLoc = GL.GetUniformLocation(_program, "u_normFactor");
        _gainLoc = GL.GetUniformLocation(_program, "u_gain");
        GL.UseProgram(_program);
        GL.Uniform1(_logNLoc, (uint)logN); // u_logN is `uniform uint` in GLSL -- must go through the
                                            // glUniform1ui overload, not glUniform1i. Passing the plain
                                            // `int` here binds the wrong entry point; on strict drivers
                                            // the uniform is left at its default 0, so the stage loop
                                            // (`for (uint stage = 0u; stage < u_logN; ...)`) never runs
                                            // and the shader just reports the magnitude of the raw
                                            // windowed time-domain samples instead of a real spectrum.
        GL.Uniform1(_normFactorLoc, 2f / (N * _windowGain)); // matches CpuFft.Process's `norm`
        GL.Uniform1(_gainLoc, _gain);

        _ssboInL = GL.GenBuffer();
        _ssboInR = GL.GenBuffer();
        _ssboOutL = GL.GenBuffer();
        _ssboOutR = GL.GenBuffer();

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboInL);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, _cpuInL.Length * sizeof(float), IntPtr.Zero,
            BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboInR);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, _cpuInR.Length * sizeof(float), IntPtr.Zero,
            BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboOutL);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, _rawOutL.Length * sizeof(float), IntPtr.Zero,
            BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboOutR);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, _rawOutR.Length * sizeof(float), IntPtr.Zero,
            BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    }

    public int N { get; }
    /// <summary>Length of the arrays <see cref="Process" /> returns -- N/2 raw bins, or <see cref="FrequencyBucketing.BucketCount" /> when a perceptual scale is active.</summary>
    public int Bins { get; }

    public void Dispose()
    {
        GL.DeleteBuffer(_ssboInL);
        GL.DeleteBuffer(_ssboInR);
        GL.DeleteBuffer(_ssboOutL);
        GL.DeleteBuffer(_ssboOutR);
        GL.DeleteProgram(_program);
    }

    /// <summary>
    ///     Runs one FFT for interleaved stereo PCM (length &gt;= N*2), windows
    ///     it, dispatches the compute shader, and returns smoothed magnitude
    ///     spectra (length Bins each, values roughly in [0,1]) for left/right.
    ///     Same contract, same output values (up to floating point evaluation
    ///     order) as <see cref="CpuFft.Process" />.
    /// </summary>
    public (float[] left, float[] right) Process(ReadOnlySpan<float> interleavedStereo)
    {
        var avail = interleavedStereo.Length / 2;
        var take = Math.Min(N, avail);
        var offset = avail - take; // most recent `take` frames

        Array.Clear(_cpuInL);
        Array.Clear(_cpuInR);
        for (var i = 0; i < take; i++)
        {
            var w = _hann[N - take + i];
            var dst = _bitRev[N - take + i];
            _cpuInL[dst] = interleavedStereo[2 * (offset + i)] * w;
            _cpuInR[dst] = interleavedStereo[2 * (offset + i) + 1] * w;
        }

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboInL);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, _cpuInL.Length * sizeof(float), _cpuInL);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboInR);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, _cpuInR.Length * sizeof(float), _cpuInR);

        GL.UseProgram(_program);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _ssboInL);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _ssboInR);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _ssboOutL);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _ssboOutR);
        GL.DispatchCompute(1, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboOutL);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, _rawOutL.Length * sizeof(float), _rawOutL);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboOutR);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, _rawOutR.Length * sizeof(float), _rawOutR);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

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

    private void ApplyGravity(float[] raw, float[] smoothed)
    {
        for (var i = 0; i < raw.Length; i++)
        {
            var rate = raw[i] > smoothed[i] ? _attack : _decay;
            smoothed[i] += (raw[i] - smoothed[i]) * rate;
        }
    }

    private static int CompileCompute(string source)
    {
        var shader = GL.CreateShader(ShaderType.ComputeShader);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
        if (ok == 0)
        {
            GL.GetShaderInfoLog(shader, out var log);
            throw new InvalidOperationException($"FFT compute shader compile failed:\n{log}\n\nSource:\n{source}");
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, shader);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
        if (linked == 0)
        {
            GL.GetProgramInfoLog(program, out var log);
            throw new InvalidOperationException($"FFT compute program link failed:\n{log}");
        }

        GL.DeleteShader(shader);
        return program;
    }

    /// <summary>
    ///     Reads <see cref="KernelPath" /> and substitutes __N__/__HALF__ --
    ///     GLSL has no way to size a `shared` array or a workgroup from a
    ///     uniform, both have to be compile-time constants, so this can't be
    ///     a plain uniform the way u_logN/u_normFactor/u_gain are. Token
    ///     substitution instead of e.g. a templating library: the kernel
    ///     file is plain GLSL, no C#-string-literal escaping concerns to
    ///     work around now that it's not embedded as a C# string at all.
    /// </summary>
    private static string LoadKernelSource(int n)
    {
        if (!File.Exists(KernelPath))
            throw new FileNotFoundException(
                $"GPU FFT kernel not found: {KernelPath} (expected next to the published executable, " +
                "same as the shaders/glava module tree).", KernelPath);

        var half = n / 2;
        return File.ReadAllText(KernelPath)
            .Replace("__N__", n.ToString())
            .Replace("__HALF__", half.ToString());
    }
}