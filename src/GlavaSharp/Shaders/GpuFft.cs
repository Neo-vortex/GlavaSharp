using System;
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
/// </summary>
public sealed class GpuFft : IFft
{
    // Token substitution instead of a $$"""...""" interpolated raw string:
    // GLSL is brace-heavy and relying on C#'s "N dollars => N braces starts
    // interpolation" rule next to hand-written GLSL braces is easy to get
    // subtly wrong. Token substitution keeps the GLSL body untouched by C#
    // string-literal escaping rules entirely.
    private const string SourceTemplate = """
                                          #version 430
                                          layout(local_size_x = __HALF__) in;

                                          // Windowed, bit-reversed time-domain samples, precomputed on the CPU
                                          // (same _hann/_bitRev tables as CpuFft) so the shader only has to do
                                          // the butterfly stages, not windowing or bit-reversal.
                                          layout(std430, binding = 0) buffer InL { float inL[]; };
                                          layout(std430, binding = 1) buffer InR { float inR[]; };
                                          layout(std430, binding = 2) buffer OutL { float outL[]; };
                                          layout(std430, binding = 3) buffer OutR { float outR[]; };

                                          shared vec2 shL[__N__];
                                          shared vec2 shR[__N__];

                                          const float PI = 3.14159265359;
                                          const uint N = __N__u;
                                          const uint HALF = __HALF__u;

                                          // Deliberately uniforms, not compile-time consts: with LOGN baked in
                                          // as a literal, Mesa's compute-shader compiler (iris/NIR on Intel)
                                          // tries to fully unroll the stage loop and do scalar replacement on
                                          // the two __N__-element `shared` arrays below, since every index into
                                          // them is a per-invocation runtime value rather than a constant. That
                                          // combination can make glCompileShader/glLinkProgram hang or take
                                          // minutes on real hardware with no error reported. Keeping LOGN (and
                                          // the tunables below) as uniforms forces the driver to keep this as a
                                          // real loop instead.
                                          uniform uint u_logN;
                                          uniform float u_normFactor; // 2 / (N * hannWindowGain), matches CpuFft
                                          uniform float u_gain;       // FftSettings.Gain (log-compression contrast)

                                          vec2 cmul(vec2 a, vec2 b) { return vec2(a.x*b.x - a.y*b.y, a.x*b.y + a.y*b.x); }

                                          void main() {
                                              uint tid = gl_LocalInvocationID.x; // 0 .. HALF-1

                                              // inL/inR already windowed + bit-reversed on the CPU; imaginary
                                              // part starts at zero, same as CpuFft's _imL/_imR.
                                              shL[tid] = vec2(inL[tid], 0.0);
                                              shL[tid + HALF] = vec2(inL[tid + HALF], 0.0);
                                              shR[tid] = vec2(inR[tid], 0.0);
                                              shR[tid + HALF] = vec2(inR[tid + HALF], 0.0);
                                              barrier();

                                              // In-place iterative DIT FFT, identical stage structure to
                                              // CpuFft.Transform: for each of log2(N) stages, pair up elements
                                              // `half` apart within blocks of `size`, apply the twiddle factor
                                              // for this stage/position, and butterfly. Both channels share the
                                              // same j/twiddle math, so they're done together in one loop --
                                              // shL/shR are referenced directly (not passed through a function
                                              // call) since `shared` arrays can't safely cross a GLSL function
                                              // boundary by value/inout.
                                              for (uint stage = 0u; stage < u_logN; stage++) {
                                                  uint m = 1u << (stage + 1u);
                                                  uint half_ = m >> 1;
                                                  uint k = tid % half_;
                                                  uint j = (tid / half_) * m + k;
                                                  float angle = -2.0 * PI * float(k) / float(m);
                                                  vec2 w = vec2(cos(angle), sin(angle));

                                                  vec2 aL = shL[j];
                                                  vec2 bL = cmul(w, shL[j + half_]);
                                                  vec2 aR = shR[j];
                                                  vec2 bR = cmul(w, shR[j + half_]);
                                                  barrier();
                                                  shL[j] = aL + bL;
                                                  shL[j + half_] = aL - bL;
                                                  shR[j] = aR + bR;
                                                  shR[j + half_] = aR - bR;
                                                  barrier();
                                              }

                                              // Only the first N/2 bins (single-sided spectrum) are meaningful;
                                              // this workgroup has exactly HALF=N/2 invocations, so every
                                              // invocation writes exactly one output bin -- same magnitude/log
                                              // -compression formula as CpuFft.Process:
                                              //   norm = 2 / (N * windowGain)
                                              //   out  = clamp(log(1 + mag*gain) / log(1 + gain), 0, 1)
                                              float magL = length(shL[tid]) * u_normFactor;
                                              float magR = length(shR[tid]) * u_normFactor;
                                              outL[tid] = clamp(log(1.0 + magL * u_gain) / log(1.0 + u_gain), 0.0, 1.0);
                                              outR[tid] = clamp(log(1.0 + magR * u_gain) / log(1.0 + u_gain), 0.0, 1.0);
                                          }
                                          """;

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

        _program = CompileCompute(BuildSource(N));
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

    private static string BuildSource(int n)
    {
        var half = n / 2;
        return SourceTemplate
            .Replace("__N__", n.ToString())
            .Replace("__HALF__", half.ToString());
    }
}