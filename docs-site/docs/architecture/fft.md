# FFT & Frequency Bucketing

`Shaders/CpuFft.cs`, `Shaders/GpuFft.cs`

Both FFT backends implement the same `IFft` interface and are
interchangeable at runtime via `--fft-device {cpu,gpu}` (`FftSettings.Device`);
`AppWindow` doesn't care which one it got.

`CpuFft` is an iterative radix-2 Cooley-Tukey FFT with precomputed
bit-reversal and twiddle-factor tables, a Hann window, and gravity
smoothing (fast attack, slow decay — the same feel as GLava's
`util/gravity_pass.frag`). It runs entirely on the CPU and uploads the
resulting spectra as textures every frame. It's the default backend. Its
output magnitudes are log-compressed and clamped to `[0, 1]`
(`Log(1 + mag * gain) / Log(1 + gain)`) before being written to the
spectrum textures — every module (including `waterfall`'s heatmap) assumes
this normalized range.

`GpuFft` is the "real" architectural target: a single-workgroup GLSL 4.3
compute shader doing the same radix-2 transform on the GPU, matching the
spot GLava's own `fft_radix*.glsl` kernels occupy, so the CPU only feeds
windowed PCM in via SSBOs and reads magnitude bins back out. It's a
bit-for-bit-equivalent port of `CpuFft`: same Hann window, same
bit-reversal permutation, same iterative stage loop (both channels'
butterflies share the same per-stage twiddle math, so they run together
in one loop rather than as two passes), and same normalization/log
-compression formula. Only the windowing/bit-reversal (trivial,
memory-bound) and the gravity smoothing (inherently serial across frames)
stay on the CPU; the O(N log N) butterfly work happens on the GPU. It's
opt-in today (pass `--fft-device gpu`) — see
[Status & Roadmap](../status-roadmap.md) for the two driver-level bugs
already found and fixed during bring-up.

The actual GLSL compute kernel lives in `shaders/fft/radix2.comp`, not
embedded in `GpuFft.cs` — `GpuFft.LoadKernelSource` reads it from disk
(resolved via `AppContext.BaseDirectory`, same convention
`ShaderModule`/`shaders/glava/` already uses) and does the same
__N__/__HALF__ token substitution the old embedded C# string did (GLSL
can't size a `shared` array or a workgroup from a uniform — both have to
be compile-time constants, so this can't just be another `uniform` like
`u_logN`). It's picked up automatically by the existing `<Content
Include="shaders/**/*">` item in the `.csproj` — no build changes needed,
same mechanism the visualization modules already rely on. The point of
pulling it out of `GpuFft.cs` isn't purely cosmetic: `radix2.comp` is the
*only* GPU FFT kernel today, but it's structured as one file in a
directory specifically so a future alternative (a different radix, a
multi-workgroup approach not capped by
`GL_MAX_COMPUTE_WORK_GROUP_INVOCATIONS` the way this single-workgroup one
is, etc.) can sit next to it as a sibling file without touching this one
— there's no kernel-selection mechanism in `GpuFft.cs` itself yet, since
that's follow-up work for whenever a second kernel actually exists rather
than speculative plumbing added ahead of it.

## Perceptual frequency bucketing

`Shaders/FrequencyBucketing.cs`

Both backends produce a raw, linearly-spaced spectrum (bin `i`'s center
frequency is `i * sampleRate / N`) straight out of the FFT math above.
`FrequencyBucketing` sits between that and gravity smoothing (in both
`CpuFft.Process` and `GpuFft.Process` — one shared implementation instead
of duplicating it per backend) and redistributes it onto a perceptual
scale, selected via `--freq-scale` (`FftSettings.Scale`):

- **`log2`** (default) — octave spacing, `forward(f) = log2(f)`. Simplest
  perceptual scale; each bucket covers a fixed frequency *ratio*, not a
  fixed span.
- **`mel`** — O'Shaughnessy's closed-form fit to the classic
  Stevens/Volkmann/Newman pitch-matching data; standard in speech/ML
  feature extraction.
- **`bark`** — Traunmüller's closed-form approximation of Zwicker's 24
  critical bands. Chosen over Zwicker's own atan-based formula specifically
  because it has a simple closed-form inverse (needed to turn evenly-spaced
  points *on* the scale back into Hz bucket edges); it's a standard,
  widely-cited approximation of the same underlying scale, not a different
  one.
- **`erb`** — Glasberg & Moore's ERB-rate scale, the current standard for
  computational auditory modeling; resolves roughly 4x finer than Bark
  below ~500Hz, which is exactly where a linear or naive-log axis distorts
  bass-heavy music the most.
- **`linear`** — bucketing disabled; raw FFT bins pass straight through
  bin-for-bin, GlavaSharp's original (buggy, see
  [Status & Roadmap](../status-roadmap.md)) behavior, kept as an explicit
  opt-out.

For each output bucket, `FrequencyBucketing`'s constructor precomputes
(once, not per-frame) a Hz range from the chosen scale's forward/inverse
functions, converts that to a raw-bin-index range, and stores either: an
inclusive `[loBin, hiBin]` pair when 2+ raw bins fall in range, or a single
fractional center-bin position when the bucket is narrower than one raw
bin (unavoidable at the low end on any perceptual scale with a modest FFT
size — standard technique, not specific to this implementation). `Apply`
(called every frame) then either takes the **max** across the bin range
(preserves peaks — a single loud harmonic in a bucket spanning many quiet
bins should still read as loud; averaging would blur transients) or
linearly interpolates between the two bins nearest the fractional center.

Since this redistribution happens *before* any shader sees the spectrum,
`util/smooth.glsl`'s own `scale_audio` — which every module's
`smooth_audio` calls to map screen position to bin index, and which
applies its own log-ish warp — has to skip that warp when bucketing is
active, or it would warp an already-correctly-spaced spectrum a second
time. `ShaderModule` injects a `_FREQ_PREBUCKETED` macro (same mechanism as
`_USE_ALPHA`, see
[Shader Module Pipeline](shader-module-pipeline.md))
based on `FftSettings.Scale`, and `scale_audio` becomes an identity
pass-through when it's set. See [Status & Roadmap](../status-roadmap.md)
for the bug this whole mechanism was built to fix.
