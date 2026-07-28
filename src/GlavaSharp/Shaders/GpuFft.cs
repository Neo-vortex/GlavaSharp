using System;
using OpenTK.Graphics.OpenGL;

namespace GlavaSharp.Shaders;

/// <summary>
///     GLSL 4.3 compute-shader FFT. Same architectural spot GLava's own
///     fft_radix*.glsl compute kernels occupy (GPU does the transform, CPU
///     just feeds windowed PCM in and reads magnitude bins back out) — this
///     is a from-scratch single-workgroup radix-2 Cooley-Tukey implementation
///     rather than a port of GLava's templated radix-4/8/16/64 kernels, which
///     depend on GLava's own C preprocessor harness to generate. N must be a
///     power of two (default 2048, matching the probe buffer already in
///     Program.cs) and &lt;= 2048 so one workgroup (local_size_x = N/2) covers
///     the whole transform in shared memory without ping-ponging buffers.
/// </summary>
public sealed class GpuFft : IDisposable
{
    // Gravity: rises fast (attack), falls slowly (decay) — same feel as
    // GLava's util/gravity_pass.frag.
    private const float Attack = 0.6f;
    private const float Decay = 0.08f;

    // Plain (non-interpolated) raw string + token substitution instead of
    // a $$"""...""" interpolated raw string: GLSL is brace-heavy and
    // relying on C#'s "N dollars => N braces starts interpolation" rule
    // next to hand-written GLSL braces is easy to get subtly wrong. Token
    // substitution keeps the GLSL body untouched by C# string-literal
    // escaping rules entirely.
    private const string SourceTemplate = """
                                          #version 430
                                          layout(local_size_x = __HALF__) in;

                                          layout(std430, binding = 0) buffer InL { vec2 dataL[]; };
                                          layout(std430, binding = 1) buffer InR { vec2 dataR[]; };
                                          layout(std430, binding = 2) buffer OutL { float outL[]; };
                                          layout(std430, binding = 3) buffer OutR { float outR[]; };

                                          shared vec2 shL[__N__];
                                          shared vec2 shR[__N__];

                                          const float PI = 3.14159265359;
                                          const uint N = __N__u;
                                          const uint HALF = __HALF__u;

                                          // Deliberately a uniform, not a compile-time const: with LOGN baked
                                          // in as a literal, Mesa's compute-shader compiler (iris/NIR on
                                          // Intel) tries to fully unroll this loop and do scalar replacement
                                          // on the two __N__-element `shared` arrays below, since every index
                                          // into them (bitReverse(tid), j, j+half_) is a per-invocation
                                          // runtime value rather than a constant. That combination can make
                                          // glCompileShader/glLinkProgram hang or take minutes on real
                                          // hardware with no error reported. Keeping LOGN as a uniform forces
                                          // the driver to keep this as a real loop instead.
                                          uniform uint u_logN;

                                          uint bitReverse(uint x) {
                                              uint r = 0u;
                                              for (uint i = 0u; i < u_logN; i++) {
                                                  r = (r << 1) | (x & 1u);
                                                  x >>= 1;
                                              }
                                              return r;
                                          }

                                          vec2 cmul(vec2 a, vec2 b) { return vec2(a.x*b.x - a.y*b.y, a.x*b.y + a.y*b.x); }

                                          void fftStage(uint tid, inout vec2 sh[__N__]) {
                                              for (uint stage = 0u; stage < u_logN; stage++) {
                                                  uint m = 1u << (stage + 1u);
                                                  uint half_ = m >> 1;
                                                  uint k = tid % half_;
                                                  uint j = (tid / half_) * m + k;
                                                  float angle = -2.0 * PI * float(k) / float(m);
                                                  vec2 w = vec2(cos(angle), sin(angle));
                                                  vec2 a = sh[j];
                                                  vec2 b = cmul(w, sh[j + half_]);
                                                  barrier();
                                                  sh[j] = a + b;
                                                  sh[j + half_] = a - b;
                                                  barrier();
                                              }
                                          }

                                          void main() {
                                              uint tid = gl_LocalInvocationID.x; // 0 .. HALF-1

                                              shL[bitReverse(tid)] = dataL[tid];
                                              shL[bitReverse(tid + HALF)] = dataL[tid + HALF];
                                              shR[bitReverse(tid)] = dataR[tid];
                                              shR[bitReverse(tid + HALF)] = dataR[tid + HALF];
                                              barrier();

                                              fftStage(tid, shL);
                                              fftStage(tid, shR);

                                              // Log-compressed magnitude, roughly [0,1]. Mirrors the shape of
                                              // GLava's scale_audio() (util/smooth.glsl) without importing its
                                              // exact SAMPLE_RANGE/SAMPLE_SCALE constants.
                                              float magL = length(shL[tid]) / float(N);
                                              float magR = length(shR[tid]) / float(N);
                                              const float K = 40.0;
                                              outL[tid] = clamp(log(1.0 + magL * K) / log(1.0 + K), 0.0, 1.0);
                                              outR[tid] = clamp(log(1.0 + magR * K) / log(1.0 + K), 0.0, 1.0);
                                          }
                                          """;

    private readonly float[] _cpuInL, _cpuInR; // interleaved re,im=0
    private readonly float[] _hann;
    private readonly int _logNLoc;

    private readonly int _program;
    private readonly float[] _rawOutL, _rawOutR;
    private readonly float[] _smoothL, _smoothR; // gravity-smoothed, CPU-side (mirrors glava's gravity/avg transforms)
    private readonly int _ssboInL, _ssboInR, _ssboOutL, _ssboOutR;

    public GpuFft(int n = 2048)
    {
        if ((n & (n - 1)) != 0) throw new ArgumentException("N must be a power of two", nameof(n));
        if (n > 2048) throw new ArgumentException("N must be <= 2048 (single-workgroup limit)", nameof(n));
        N = n;

        _hann = new float[N];
        for (var i = 0; i < N; i++)
            _hann[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (N - 1));

        _cpuInL = new float[N * 2];
        _cpuInR = new float[N * 2];
        _rawOutL = new float[Bins];
        _rawOutR = new float[Bins];
        _smoothL = new float[Bins];
        _smoothR = new float[Bins];

        _program = CompileCompute(BuildSource(N));
        _logNLoc = GL.GetUniformLocation(_program, "u_logN");
        GL.UseProgram(_program);
        GL.Uniform1(_logNLoc, (int)Math.Log2(N));

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

    private int N { get; }
    private int Bins => N / 2;

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
            _cpuInL[2 * (N - take + i)] = interleavedStereo[2 * (offset + i)] * w;
            _cpuInR[2 * (N - take + i)] = interleavedStereo[2 * (offset + i) + 1] * w;
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

        ApplyGravity(_rawOutL, _smoothL);
        ApplyGravity(_rawOutR, _smoothR);
        return (_smoothL, _smoothR);
    }

    private static void ApplyGravity(float[] raw, float[] smoothed)
    {
        for (var i = 0; i < raw.Length; i++)
        {
            var rate = raw[i] > smoothed[i] ? Attack : Decay;
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