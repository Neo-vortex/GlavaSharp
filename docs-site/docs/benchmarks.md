# Benchmarks

## `CpuFft`: AVX2+FMA vectorization

`Shaders/CpuFft.cs`'s butterfly stage (`Transform`) and magnitude
computation (`ComputeMagnitude`) are vectorized with AVX2+FMA (8 lanes at
a time), gated behind a runtime `Avx2.IsSupported && Fma.IsSupported`
check with a scalar fallback for CPUs without it (older x86, ARM). Twiddle
factors for the butterfly aren't contiguous in memory except for the very
last stage, so they're loaded with `Avx2.GatherVector256` rather than
assuming a simple stride; stages where `half < 8` (the early, small
stages, regardless of overall FFT size) always take the scalar path since
there aren't enough elements to fill a lane.

`--benchmark-fft` runs `CpuFft.Process()` (200 warmup + 2000 timed
iterations, fixed RNG seed, `Scale=Linear` to exclude
`FrequencyBucketing` from the measurement) across a spread of window
sizes and reports ms/call, calls/sec, and a checksum of the returned
spectrum. `CpuFft.UsingAvx2` exposes which path actually ran, logged at
`Debug` on construction too (`CpuFft: AVX2+FMA available, using the
vectorized butterfly path` / `... using the scalar fallback`).

**Measured on an Intel Core i7-12700 (AVX2+FMA-capable), 3 runs averaged
per configuration, JIT build (`dotnet build`, not AOT — see the note
below for why):**

| Size | AVX2+FMA (ms/call) | Scalar fallback (ms/call) | Speedup |
| ---: | ---: | ---: | ---: |
| 1024 | 0.0251 | 0.0308 | 1.23x |
| 2048 | 0.0364 | 0.0523 | 1.44x |
| 4096 | 0.0552 | 0.0813 | 1.47x |
| 8192 | 0.1176 | 0.1839 | 1.56x |

Scalar numbers came from the exact same binary with `Avx2.IsSupported`
forced constant-false at runtime (`DOTNET_EnableAVX2=0` in the
environment — a real .NET diagnostic env var, not benchmark-specific
plumbing), so this isolates vectorization as the only variable; it isn't
comparing across different compiler output. Speedup grows with size
because the early FFT stages (`half < 8`) are scalar-only regardless of N,
so larger N means a larger fraction of total stages are actually
vectorizable — at N=1024 (10 stages) 7 stages qualify; at N=8192 (13
stages) 10 do.

**Correctness**: the checksums (sum of every returned magnitude, both
channels) matched to 6-7 significant figures between the AVX2 and scalar
runs at every size (e.g. size 8192: `37.241384` vectorized vs `37.241385`
scalar) — the last-digit difference is expected FMA rounding (a fused
multiply-add rounds once instead of twice, so it's not bit-identical to
separate multiply+subtract), not a bug.

**Native AOT gets none of this speedup as currently configured, and
that's a real gap, not a rounding footnote.** Native AOT (ILC) compiles
`Xxx.IsSupported` for any ISA above its baseline (SSE2 on x64) as a
compile-time-constant `false` unless the target instruction set is
explicitly widened via `<IlcInstructionSet>` — confirmed by testing the
identical DLL both ways: JIT (`dotnet GlavaSharp.dll`) correctly detects
AVX2 at runtime, the AOT-published `build/dist/GlavaSharp` reports "AVX2+FMA
not available" on the *same* AVX2-capable CPU. This isn't a bug in
`CpuFft.cs` — it's Native AOT deliberately choosing portability over
performance by default, since an AOT binary (unlike JIT) is compiled once
and run on whatever hardware it's copied to, which might not be the build
machine. Setting `<IlcInstructionSet>avx2,fma</IlcInstructionSet>` doesn't
add a runtime check the way JIT has — it bakes AVX2 in as a hard
requirement, deletes the scalar fallback from the compiled output
entirely, and makes the runtime fail-fast at startup (or, per a known ILC
edge case, occasionally a raw illegal-instruction crash) on any CPU that
turns out not to have it. Native AOT has no JIT to fall back to, so there
is no single-binary way to get "AVX2 when present, scalar otherwise" the
way this file's own source code implies — that pattern only works for
JIT/framework-dependent builds.

**Resolved**: rather than picking one default for everyone, `build/dist/`
stays on the safe scalar baseline by default, and
`-DGLAVASHARP_AVX2_CPU_FFT=ON` (see [Building](getting-started/building.md))
opts a build into the AVX2+FMA requirement explicitly — `cmake --build
build --target appimage` names its output `GlavaSharp-x86_64-avx2.AppImage`
instead of the plain name whenever this is on, specifically so the
AVX2-requiring artifact can't be mistaken for, or silently overwrite, the
portable one. Verified live both ways: `CpuFft.UsingAvx2`/`--benchmark-fft`
reports `no` on a plain `cmake --build build` of this AVX2-capable CPU and
`yes` after reconfiguring with the option on and rebuilding — same
machine, only the ILC instruction-set flag differs.

## `--benchmark-fft`: standalone CPU/GPU benchmark mode

`--benchmark-fft` runs entirely outside the normal app flow — no window,
no `ShaderModule`, no audio capture, just `IFft.Process()` timed in a loop
and a results table on stdout. `--fft-device cpu|gpu` (default `cpu`)
picks which `IFft` implementation gets benchmarked, reusing the same flag
the real app uses; `--fft-attack`/`-decay`/`-gain`/`--sample-rate` apply as
normal, but `--fft-size` itself is ignored — the benchmark always sweeps
its own fixed list (1024/2048/4096/8192) so one run reports the full
picture rather than needing four separate invocations.

`--fft-device gpu` needs a real GL context (compute shaders don't exist
without one) but deliberately never shows a window: it creates a GLFW
window with `WindowHintBool.Visible` false, uses it purely to get a
current GL 4.3 context, and never calls `SwapBuffers` or renders anything
— exactly what `GpuFft.Process()` itself touches on the GPU side anyway
(SSBO upload → dispatch → readback, no framebuffer involved).

Before running any GPU size, it queries `GL_MAX_COMPUTE_WORK_GROUP_INVOCATIONS`
(`GpuFft` dispatches a single workgroup of `N/2` invocations) and skips
sizes that would exceed it, rather than attempting the dispatch and
risking a repeat of the exact failure mode already documented for `GpuFft`
bring-up: a compute shader that violates this limit is the kind of thing
that's hung `glCompileShader`/`glLinkProgram` with no error on some driver
paths instead of failing cleanly. Confirmed live on this machine (AMD RX
6700 XT, Mesa radeonsi): `GL_MAX_COMPUTE_WORK_GROUP_INVOCATIONS = 1024`,
so 1024/2048 ran and 4096/8192 were skipped with a clear reason instead of
risking a hang:

```
size   ms/call   calls/sec  checksum
1024   0.1672    5980       17.431565
2048   0.1866    5359       21.986066
4096   skipped (needs 2048 compute invocations, this GPU allows 1024)
8192   skipped (needs 4096 compute invocations, this GPU allows 1024)
```

The checksums matched `CpuFft`'s own (`17.431567`/`21.986067` at the same
sizes) to 5-6 significant figures, cross-checking correctness between the
two backends the same way the AVX2-vs-scalar comparison above does. GPU
numbers here are slower than CPU's at these sizes — expected, since every
`GpuFft.Process()` call pays real upload/dispatch/readback round-trip
overhead that a single-workgroup, N≤2048-sized FFT is too small to
amortize; `GpuFft`'s actual purpose is freeing up the CPU core FFT would
otherwise occupy, not raw throughput at this scale.
