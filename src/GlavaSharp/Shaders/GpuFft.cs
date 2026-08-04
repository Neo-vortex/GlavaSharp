using System;
using System.IO;
using OpenTK.Graphics.OpenGL;

namespace GlavaSharp.Shaders;

/// <summary>
///     GLSL 4.3 compute-shader FFT, selected via <c>--fft-device gpu</c> (see
///     <see cref="FftSettings.Device" />). A bit-for-bit-equivalent GPU port
///     of <see cref="CpuFft" />: same Hann window, same bit-reversal
///     permutation, same iterative radix-2 Cooley-Tukey stage loop, same
///     Hann-gain-corrected normalization, same log-compression formula, same
///     bucketing (<see cref="FrequencyBucketing" />) and gravity attack/decay
///     formula, all pulled from <see cref="FftSettings" /> instead of being
///     hardcoded. Only the windowing/bit-reversal (trivial, memory-bound)
///     stays on the CPU; everything else -- the O(N log N) butterfly work,
///     bucketing, and gravity smoothing -- happens on the GPU.
///     Single-workgroup (local_size_x = N/2), so the whole transform lives in
///     `shared` memory without ping-ponging buffers -- this caps N at 8192
///     (same limit <c>CpuFft</c> doesn't have, since it's not workgroup-bound,
///     but which matches the shared default of <see cref="FftSettings.Size" />).
///
///     Two dispatches, two kernel files (both loaded/token-substituted, not
///     embedded here -- see <see cref="LoadRadix2KernelSource" />/
///     <see cref="LoadPostKernelSource" />):
///      - <c>shaders/fft/radix2.comp</c>: stage 1, FFT + magnitude. Used by
///        both <see cref="Process" /> (CPU-readback path, kept for
///        <c>--benchmark-fft</c>'s cross-backend checksum diffing) and
///        <see cref="ProcessToTexture" />.
///      - <c>shaders/fft/post.comp</c>: stage 2, bucketing + gravity +
///        imageStore straight into the render textures. Only
///        <see cref="ProcessToTexture" /> uses this -- it's what lets that
///        path skip the CPU entirely after the initial PCM upload, unlike
///        <see cref="Process" />, which still reads stage 1's output back
///        with a synchronous <c>glGetBufferSubData</c> and does bucketing/
///        gravity on the CPU the way the original single-kernel design did.
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

    // shaders/fft/post.comp -- stage 2 of the pipeline (bucketing + gravity
    // + direct imageStore into the render textures), see ProcessToTexture.
    private static readonly string PostKernelPath =
        Path.Combine(AppContext.BaseDirectory, "shaders", "fft", "post.comp");

    // Same gravity smoothing as CpuFft.ApplyGravity, and sourced from the
    // same FftSettings (rather than hardcoded) so `--fft-attack`/`--fft-decay`
    // behave identically regardless of which backend is active. Not
    // readonly: SetAttack/SetDecay/SetGain (see IFft) re-upload the
    // corresponding GL uniform whenever the live control channel changes
    // one -- must only be called from the render thread (same constraint as
    // Process/ProcessToTexture), since that's the only place with the GL
    // context current.
    private float _attack;
    private float _decay;
    private float _gain;

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

    // Stage 2: bucketing + gravity + direct write into the render textures
    // (see ProcessToTexture and shaders/fft/post.comp). _ssboBucketLoHi/
    // _ssboBucketCenter are 0/unused when _bucketing is null (Scale ==
    // Linear) -- post.comp's __BUCKETED__ token substitution compiles that
    // branch out entirely rather than binding empty buffers.
    private readonly int _postProgram;
    private readonly int _postAttackLoc, _postDecayLoc;
    private readonly int _ssboGravL, _ssboGravR;
    private readonly int _ssboBucketLoHi, _ssboBucketCenter;

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

        _program = CompileCompute(LoadRadix2KernelSource(N));
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

        // --- Stage 2 setup (see ProcessToTexture/post.comp) ---
        _postProgram = CompileCompute(LoadPostKernelSource(Bins, _bucketing != null, N / 2));
        _postAttackLoc = GL.GetUniformLocation(_postProgram, "u_attack");
        _postDecayLoc = GL.GetUniformLocation(_postProgram, "u_decay");
        GL.UseProgram(_postProgram);
        GL.Uniform1(_postAttackLoc, _attack);
        GL.Uniform1(_postDecayLoc, _decay);

        // Persistent gravity state -- zero-initialized (BufferData with a
        // zeroed managed array, not IntPtr.Zero: the latter leaves the
        // buffer's initial contents undefined on some drivers, and this one
        // has to start at 0 to match CpuFft's `new float[Bins]` -- ApplyGravity
        // /post.comp's smoothing formula both assume that as the frame-0 state).
        var zeros = new float[Bins];
        _ssboGravL = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboGravL);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, Bins * sizeof(float), zeros, BufferUsageHint.DynamicDraw);
        _ssboGravR = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboGravR);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, Bins * sizeof(float), zeros, BufferUsageHint.DynamicDraw);

        if (_bucketing != null)
        {
            // Flattened (lo0,hi0,lo1,hi1,...) to match post.comp's tightly
            // packed `ivec2 bucketLoHi[]` (std430 array-of-ivec2 stride is
            // 8 bytes, i.e. two ints, no padding).
            var loHiPairs = new (int lo, int hi)[Bins];
            var centers = new float[Bins];
            _bucketing.CopyBucketMap(loHiPairs, centers);
            var loHiFlat = new int[Bins * 2];
            for (var b = 0; b < Bins; b++)
            {
                loHiFlat[2 * b] = loHiPairs[b].lo;
                loHiFlat[2 * b + 1] = loHiPairs[b].hi;
            }

            _ssboBucketLoHi = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboBucketLoHi);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, loHiFlat.Length * sizeof(int), loHiFlat,
                BufferUsageHint.StaticDraw);
            _ssboBucketCenter = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboBucketCenter);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, centers.Length * sizeof(float), centers,
                BufferUsageHint.StaticDraw);
        }

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
        GL.DeleteBuffer(_ssboGravL);
        GL.DeleteBuffer(_ssboGravR);
        if (_bucketing != null)
        {
            GL.DeleteBuffer(_ssboBucketLoHi);
            GL.DeleteBuffer(_ssboBucketCenter);
        }

        GL.DeleteProgram(_program);
        GL.DeleteProgram(_postProgram);
    }

    /// <summary>Re-uploads u_attack on shaders/fft/post.comp -- render-thread only, see IFft.SetAttack.</summary>
    public void SetAttack(float attack)
    {
        _attack = attack;
        GL.UseProgram(_postProgram);
        GL.Uniform1(_postAttackLoc, _attack);
    }

    /// <summary>Re-uploads u_decay on shaders/fft/post.comp -- render-thread only, see IFft.SetDecay.</summary>
    public void SetDecay(float decay)
    {
        _decay = decay;
        GL.UseProgram(_postProgram);
        GL.Uniform1(_postDecayLoc, _decay);
    }

    /// <summary>Re-uploads u_gain on shaders/fft/radix2.comp -- render-thread only, see IFft.SetGain.</summary>
    public void SetGain(float gain)
    {
        _gain = gain;
        GL.UseProgram(_program);
        GL.Uniform1(_gainLoc, _gain);
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

    /// <summary>
    ///     GPU-resident version of <see cref="Process" />: same windowing +
    ///     stage-1 FFT dispatch, but instead of reading OutL/OutR back to the
    ///     CPU (the <c>GL.GetBufferSubData</c> calls in <see cref="Process" />
    ///     -- each one a synchronous CPU/GPU pipeline stall) it dispatches
    ///     stage 2 (shaders/fft/post.comp) directly against those same SSBOs,
    ///     which does bucketing + gravity smoothing on the GPU and
    ///     imageStores straight into <paramref name="textureL" />/
    ///     <paramref name="textureR" />. The only CPU&lt;-&gt;GPU traffic per call
    ///     is the small windowed-PCM upload before stage 1 -- nothing reads
    ///     anything back, so there's nothing to stall on.
    /// </summary>
    public void ProcessToTexture(ReadOnlySpan<float> interleavedStereo, AudioSpectrumTexture textureL,
        AudioSpectrumTexture textureR)
    {
        var avail = interleavedStereo.Length / 2;
        var take = Math.Min(N, avail);
        var offset = avail - take;

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

        // Stage 1: FFT + magnitude, same dispatch as Process().
        GL.UseProgram(_program);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _ssboInL);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _ssboInR);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _ssboOutL);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _ssboOutR);
        GL.DispatchCompute(1, 1, 1);
        // Stage 2 reads OutL/OutR as SSBOs (not images), so a shader-storage
        // barrier is what actually orders it after stage 1's writes here --
        // TextureFetchBarrierBit further down is a separate barrier, for a
        // separate kind of GPU-side reader (the render pass sampling the
        // textures stage 2 writes).
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        // Stage 2: bucketing + gravity + direct write into the spectrum
        // textures. OutL/OutR stay bound at 2/3 (stage 1 already left them
        // there); only the stage-2-only bindings need setting up fresh.
        GL.UseProgram(_postProgram);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _ssboOutL);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _ssboOutR);
        if (_bucketing != null)
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, _ssboBucketLoHi);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _ssboBucketCenter);
        }

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, _ssboGravL);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 7, _ssboGravR);
        GL.BindImageTexture(0, textureL.Handle, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.R32f);
        GL.BindImageTexture(1, textureR.Handle, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.R32f);
        GL.DispatchCompute(1, 1, 1);
        // Orders stage 2's imageStore writes before AppWindow's render pass
        // samples these same textures -- without this, the render pass can
        // (and on real drivers, does) see stale/partial texture contents.
        GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit);
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
    private static string LoadRadix2KernelSource(int n)
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

    /// <summary>Same token-substitution approach as <see cref="LoadRadix2KernelSource" />, for shaders/fft/post.comp.</summary>
    private static string LoadPostKernelSource(int bins, bool bucketed, int rawBins)
    {
        if (!File.Exists(PostKernelPath))
            throw new FileNotFoundException(
                $"GPU FFT post-processing kernel not found: {PostKernelPath} (expected next to the published " +
                "executable, same as the shaders/glava module tree).", PostKernelPath);

        return File.ReadAllText(PostKernelPath)
            .Replace("__BINS__", bins.ToString())
            .Replace("__BUCKETED__", bucketed ? "1" : "0")
            .Replace("__RAWBINS__", rawBins.ToString());
    }
}