# GlavaSharp

GlavaSharp is a from-scratch, C#/.NET reimplementation of [GLava](https://github.com/jarcode-foss/glava)'s
rendering model: an audio-reactive OpenGL visualizer driven by chains of
numbered GLSL fragment shaders, configured through GLava's own `rc.glsl` /
module directory convention. It captures system audio via PipeWire, runs an
FFT, and feeds the resulting spectrum into your choice of shader module
(bars, radial, etc.) as a pair of 1D textures.

**Status: early alpha.** It runs, it renders, and the core pipeline works,
but this is not yet a polished GLava replacement — see
[Status & known issues](#status--known-issues) below before you file a bug
about a broken module.

> GlavaSharp is an independent reimplementation and is not affiliated with
> or endorsed by the GLava project. It exists because GLava's shader
> ecosystem (the module/`rc.glsl`/`#request` convention) is genuinely well
> designed, and it seemed worth having that ecosystem sitting on top of a
> different, more portable host.
<img width="511" height="427" alt="image" src="https://github.com/user-attachments/assets/86837b73-992e-4e8f-8c0b-10df5b3c215e" />

---

## Table of contents

- [Why this exists](#why-this-exists)
- [How it compares to GLava](#how-it-compares-to-glava)
- [Status & known issues](#status--known-issues)
- [Architecture](#architecture)
  - [High-level pipeline](#high-level-pipeline)
  - [Project layout](#project-layout)
  - [Audio capture (`Audio/` + `native/pwshim/`)](#audio-capture-audio--nativepwshim)
  - [FFT (`Shaders/Cpufft.cs`, `Shaders/GpuFft.cs`)](#fft-shaderscpufftcs-shadersgpufftcs)
  - [Shader preprocessing (`Shaders/GlavaPreprocessor.cs`)](#shader-preprocessing-shadersglavapreprocessorcs)
  - [Shader module pipeline (`Shaders/ShaderModule.cs`)](#shader-module-pipeline-shadersshadermodulecs)
  - [Windowing (`Windowing/AppWindow.cs`)](#windowing-windowingappwindowcs)
  - [Configuration (`Shaders/RcConfig.cs`)](#configuration-shadersrcconfigcs)
- [Design trade-offs](#design-trade-offs)
- [Roadmap](#roadmap)
- [Building](#building)
- [Running](#running)
- [License](#license)

---

## Why this exists

GLava is a mature, well-designed C project: a small preprocessor layered
over GLSL, a module system where a visualizer is just a numbered stack of
`.frag` passes, and a config format (`rc.glsl` + `#request` directives) that
lets you reconfigure behavior without touching the host program at all.
That design is worth keeping. What this project changes is everything
*underneath* it:

- a memory-safe, garbage-collected host (C#) instead of hand-rolled C,
- a portable windowing/GL layer (GLFW via OpenTK) instead of directly
  targeting Xlib,
- a sandboxed, memory-safe native audio backend (Rust + PipeWire) instead
  of linking libpulse directly into the main process,
- a single statically-linked Native AOT executable as the distributable
  artifact, rather than a dynamically linked C binary plus shared config/
  module directories.

None of that changes what a module author sees: `rc.glsl` and the module
`.frag` files are still ordinary GLava shader source. GlavaSharp reuses
GLava's actual shader tree (bundled under `src/GlavaSharp/shaders/glava/`)
essentially unmodified.

## How it compares to GLava

| | GLava | GlavaSharp |
|---|---|---|
| Host language | C | C# (.NET, Native AOT) |
| Windowing/GL | Xlib directly (GLFW on the `unstable` branch) | GLFW via OpenTK, always |
| Display server support | **X11 only** | **X11 and Wayland** (GLFW's `PlatformPreference.Any` picks whichever the session is running) |
| Audio backend | PulseAudio (libpulse), linked into the main process | PipeWire, isolated in a separate Rust static library behind a tiny FFI shim |
| Shader preprocessor | Full custom C preprocessor (`#request`, `#include`, `#expand`, `@fg:`/`@bg:` compositing, GLava's transform pipeline for FFT/window/gravity/avg as *chained shaders*) | A deliberately small subset (see [below](#shader-preprocessing-shadersglavapreprocessorcs)) — enough to load real GLava module files, not a full reimplementation of every directive |
| FFT | Runs as GLava's own chained compute-shader "transform" passes (`window` → `fft` → `gravity` → `avg`) on the GPU | CPU-side FFT today (`CpuFft`); a GPU compute-shader FFT exists (`GpuFft`) but is currently disabled — see [Status](#status--known-issues) |
| Distributable artifact | Dynamically linked binary + installed shader/config tree under `/etc/xdg` or `~/.config/glava` | Single self-contained Native AOT executable with the Rust audio shim statically linked in; shader tree ships alongside it, not installed system-wide (yet) |
| Desktop-embedded mode (`glava -d` / `setxwintype "desktop"`) | Supported, X11 EWMH-based | **Not yet implemented** — planned, GNOME first (see [Roadmap](#roadmap)) |
| Module maturity | All bundled modules (bars, radial, circle, graph, wave, ...) are production-quality | Only `bars` and `radial` are verified working; `circle`, `graph`, and `wave` render but have known bugs (see [Status](#status--known-issues)) |
| Build system | Meson (2.x) / legacy Makefile (1.x) | CMake orchestrating `cargo` + `dotnet publish` |

The short version: GlavaSharp is GLava's *shader-facing* design ported onto
a different, more memory-safe, cross-compositor host stack. It is not a
drop-in replacement, doesn't read GLava's installed config paths, and is
missing GLava features (desktop embedding, most of the `#request` surface,
IPC/pipe control) that GLava has had for years.

## Status & known issues

This is **early alpha** software.

- **Working:** `bars` and `radial` modules render correctly against live
  PipeWire audio, on both X11 and Wayland sessions.
- **Buggy / not working yet:** `circle`, `graph`, and `wave` compile and
  run but produce incorrect or broken output — they haven't been debugged
  against GlavaSharp's smaller preprocessor yet and may be relying on
  `#request`/compositing behavior that isn't implemented (see
  [Shader preprocessing](#shader-preprocessing-shadersglavapreprocessorcs)).
  Treat them as "known incomplete," not "regressions to report."
- **`GpuFft` is present but unused.** The GPU compute-shader FFT
  (`Shaders/GpuFft.cs`) is a complete, from-scratch radix-2 Cooley-Tukey
  implementation, architecturally in the same spot GLava's own
  `fft_radix*.glsl` compute kernels occupy. It's disabled today because it
  can hang `glCompileShader`/`glLinkProgram` on at least one real driver
  stack (Mesa/Intel iris) with no error reported — see the extensive
  comment at the top of `GpuFft.cs` for the specific NIR-unrolling theory.
  `CpuFft` is the stand-in until that's root-caused and fixed (or worked
  around) on real hardware; re-enabling GPU FFT is on the
  [roadmap](#roadmap).
- **No desktop-embedded mode.** GLava's `-d` flag / `setxwintype "desktop"`
  behavior (rendering pinned behind desktop icons, EWMH-managed) has no
  equivalent yet.
- **No installed config tree.** GlavaSharp reads `shaders/glava/` next to
  the executable (or wherever `--shaders` points); it doesn't yet look in
  `~/.config/glavasharp` the way GLava looks in `~/.config/glava`.

If you hit something outside this list, it's a real bug — please file an
issue with the `--list-gpus`/`--list-sinks` output and the module you were
running.

## Architecture

### High-level pipeline

```
 PipeWire "what you hear" monitor
            │  (Rust: native/pwshim, staticlib, statically linked via Native AOT)
            ▼
   PipeWireAudioSource (Audio/)  ──▶  RingBuffer  ──▶  AudioWindow (tail buffer)
                                                              │
                                                              ▼
                                                   CpuFft.Process() (Shaders/)
                                              windowed FFT → log-compressed,
                                              gravity-smoothed magnitude spectra
                                                       (left, right)
                                                              │
                                                              ▼
                                      AudioSpectrumTexture × 2 (1D R32F textures)
                                                              │
                                                              ▼
                    ShaderModule (GLava module dir: 1.frag, 2.frag, ...)
              each pass: fullscreen triangle, samples audio_l/audio_r + tex0
              (previous pass's output), renders to ping-pong FBOs, last pass
                              to the default framebuffer
                                                              │
                                                              ▼
                                                 AppWindow: GLFW SwapBuffers
```

Every frame (`AppWindow.Run`): pump whatever new PCM PipeWire has produced
into the ring buffer, run one FFT over the most recent window, upload the
two resulting spectra as 1D textures, run the active module's pass chain,
swap buffers.

### Project layout

```
GlavaSharp/
├── GlavaSharp.slnx              solution file (new XML .slnx format)
├── CMakeLists.txt               orchestrates: cargo build (native/pwshim) → dotnet publish (AOT)
├── .github/workflows/ci.yml     CI: rust job, dotnet job, full AOT integration job
├── src/
│   └── GlavaSharp/              the .NET project — all C# source lives here
│       ├── GlavaSharp.csproj
│       ├── Program.cs           CLI parsing, wiring, entry point
│       ├── FftSettings.cs
│       ├── GpuEnumerator.cs     --list-gpus / --gpu N (DRI_PRIME etc.)
│       ├── Audio/               PipeWire capture (P/Invoke into native/pwshim)
│       ├── Shaders/             FFT, shader preprocessing, module pass pipeline
│       ├── Windowing/           GLFW window/context/frame-loop
│       └── shaders/glava/       GLava's own shader tree, bundled as-is
└── native/
    └── pwshim/                  standalone Rust crate — NOT nested in the
                                  C# project, since it isn't C# code and has
                                  its own independent build/test lifecycle
        ├── Cargo.toml
        └── src/lib.rs           PipeWire stream capture, exposed via a
                                  small extern "C" FFI surface
```

`native/pwshim/` living outside `src/GlavaSharp/` (rather than e.g. a
`native-rs/` folder nested inside the C# project, which is how this
started out) is deliberate: it's a fully independent build unit with its
own `Cargo.lock`, its own CI job, and its own release cadence — nesting it
inside the .NET project directory implied an ownership relationship that
doesn't reflect how the two are actually built, versioned, or tested.

### Audio capture (`Audio/` + `native/pwshim/`)

PipeWire has no first-class C# bindings, and hand-writing raw P/Invoke
signatures against PipeWire's C API (which leans heavily on callbacks,
`spa_pod`s, and manual memory management) is exactly the kind of surface
where memory-safety bugs live. Instead:

- `native/pwshim` (Rust, crate name `pwshim`) uses the
  [`pipewire`](https://crates.io/crates/pipewire) crate to open a capture
  stream on the default sink's monitor (or a specific node, for
  `--sink`/`--list-sinks`), and exposes a minimal `extern "C"` surface:
  `pwshim_start`, `pwshim_stop`, `pwshim_list_targets`,
  `pwshim_free_string`. All the PipeWire-specific complexity (stream
  negotiation, format callbacks, buffer lifetime) stays inside Rust, where
  the type system and borrow checker actually catch misuse.
- `Audio/PipeWireNative.cs` is the thin `LibraryImport` (source-generated
  P/Invoke) layer over that FFI surface.
- `Audio/PipeWireAudioSource.cs` wraps it in `IAudioSource`, GlavaSharp's
  own abstraction, so the rest of the app (`AudioWindow`, `CpuFft`, ...)
  never touches PipeWire types directly.
- `Audio/RingBuffer.cs` + `Audio/AudioWindow.cs`: the ring buffer is a
  destructive read cursor fed by the native callback thread; `AudioWindow`
  sits on top to keep a fixed-size *tail* of the most recent N interleaved
  stereo frames available to the render thread every frame, even when a
  given frame produces fewer new samples than the FFT window needs.
- Built as `crate-type = ["staticlib"]` and statically linked into the
  final Native AOT executable via `<NativeLibrary>` +
  `<DirectPInvoke Include="pwshim"/>` in the `.csproj` — the shipped
  artifact is one file, no sibling `libpwshim.so` to lose track of.

### FFT (`Shaders/Cpufft.cs`, `Shaders/GpuFft.cs`)

`CpuFft` is an iterative radix-2 Cooley-Tukey FFT with precomputed
bit-reversal and twiddle-factor tables, a Hann window, and gravity
smoothing (fast attack, slow decay — the same feel as GLava's
`util/gravity_pass.frag`). It runs entirely on the CPU and uploads the
resulting spectra as textures every frame.

`GpuFft` is the "real" architectural target: a single-workgroup GLSL 4.3
compute shader doing the same radix-2 transform on the GPU, matching the
spot GLava's own `fft_radix*.glsl` kernels occupy, so the CPU only feeds
windowed PCM in via SSBOs and reads magnitude bins back out. It's currently
unused (see [Status](#status--known-issues)) because of a driver-level
shader-compiler hang; the class is kept in the tree, with the discovered
workaround (keep `LOGN` as a `uniform` rather than baking it in as a
compile-time constant, since the constant-bound version made the crash far
more reliably reproducible) documented in comments, as a starting point
for whoever picks this back up.

### Shader preprocessing (`Shaders/GlavaPreprocessor.cs`)

GLava's own preprocessor is a full custom language extension: it handles
`#request` (configuring the host and defining values shaders read back),
`#include` (with `@` = module-relative and `:` = shader-root-relative
paths), `#expand`, hex-color literals, and a `@fg:`/`@bg:`
foreground/background compositing model with a dedicated blending pass.

`GlavaPreprocessor` implements a deliberately small subset — enough to
load real, unmodified GLava module files as plain GLSL:

- `#include "@x"` / `#include ":x"` — resolved and inlined recursively,
  deduplicated per top-level `Process()` call (so re-including the same
  file, which GLava's own shaders do routinely, e.g. `bars.glsl` via both
  `@` and `:` paths in `bars/1.frag`, doesn't redefine macros), capped at
  depth 32 as a sanity backstop against genuine include cycles.
- `#request setsmoothfactor <n>` / `#request setsmoothpass <bool>` —
  turned into `#define`s, because `util/smooth.glsl` reads them back as
  plain GLSL identifiers (`_SMOOTH_FACTOR`, `_PRE_SMOOTHED_AUDIO`). Every
  other `#request` line is stripped as a no-op.
- `#expand NAME COUNT` → `NAME(0) NAME(1) ... NAME(COUNT-1)`, one call per
  line, when `COUNT` is already a literal integer.
- `#RRGGBB[AA]` hex-color literals → `vec4(...)`.
- The `@fg:`/`@bg:` tags are stripped rather than driving a real
  compositing pass — GlavaSharp just draws the resulting color with normal
  alpha blending.
- A redundant `in vec4 gl_FragCoord;` redeclaration (legacy GLSL-version
  compat in GLava's own shaders) is stripped, since core-profile GLSL
  already declares it implicitly and strict drivers reject the
  redeclaration.

What's explicitly **not** implemented: GLava's `#request transform ...`
pipeline (chaining `window`/`fft`/`gravity`/`avg` as GPU shader passes —
GlavaSharp does windowing/FFT/gravity natively in `CpuFft`/`GpuFft`
instead) and the full compositing model behind `@fg:`/`@bg:`. This is the
most likely source of the `circle`/`graph`/`wave` bugs noted in
[Status](#status--known-issues): those modules may lean on `#request`
directives or compositing behavior this preprocessor silently drops
instead of honoring.

### Shader module pipeline (`Shaders/ShaderModule.cs`)

A GLava "module" is a directory of numbered fragment passes (`1.frag`,
`2.frag`, ...). `ShaderModule` loads them in order, wraps each in a shared
trivial vertex shader (a fullscreen triangle, no vertex buffer needed),
and compiles/links each into its own program. A pass containing GLava's
`#error __disablestage` sentinel (e.g. `bars/2.frag` is a no-op unless
`USE_ALPHA=1`) is recognized and skipped rather than treated as a compile
failure, and its predecessor's output passes straight through to the next
real pass.

At render time, passes ping-pong between two offscreen FBOs (`_fboA`/
`_fboB`), each one receiving the previous enabled pass's output as
`tex0`, plus the two audio spectrum textures as `audio_l`/`audio_r`. The
last *enabled* pass (not necessarily the last file — a trailing disabled
pass shouldn't swallow the real output) renders directly to the default
framebuffer.

### Windowing (`Windowing/AppWindow.cs`)

A deliberately thin GLFW wrapper — not OpenTK's `GameWindow` — so init
hints, platform selection, and the frame loop are all explicit and
GLava-shaped rather than inherited from a general-purpose game-engine
loop. `PlatformPreference.Any` lets GLFW pick Wayland when running inside
a Wayland session and fall back to X11 otherwise, which is what actually
gives GlavaSharp both-compositor support — GLava's mainline branch talks
to Xlib directly and is X11-only (its `unstable` branch has experimented
with GLFW too, for the same reason).

### Configuration (`Shaders/RcConfig.cs`)

A tiny reader for the handful of top-level `rc.glsl` values GlavaSharp
actually acts on today (module name, window title/size, FFT buffer size,
sample rate) — not a general `#request` interpreter. Most of `rc.glsl`'s
directive surface (window decoration/floating/opacity hints, geometry,
`setxwintype`, etc.) is parsed by nothing yet and simply has no effect.

## Design trade-offs

- **CPU FFT instead of GPU FFT, for now.** Costs some CPU time and a
  texture upload every frame that GLava avoids entirely by keeping the
  transform on the GPU. Chosen because `GpuFft` is real but currently
  blocked on a driver bug (see [Status](#status--known-issues)); `CpuFft`
  trades peak performance for "actually starts up reliably today."
- **A small preprocessor subset instead of a full GLava-language
  reimplementation.** Enough to run real, unmodified GLava shader files
  for the common cases, at the cost of some modules (relying on the
  `#request transform` chain or full `@fg:`/`@bg:` compositing)
  not working yet. Reimplementing 100% of GLava's preprocessor was judged
  not worth doing before validating the rest of the pipeline end to end.
- **Rust for the audio backend instead of direct P/Invoke onto PipeWire's
  C API.** Adds a second toolchain and a second CI job, in exchange for
  keeping the trickiest, most callback-heavy native surface in a language
  that can actually check it. `native/pwshim` is deliberately narrow —
  start/stop/list/read, nothing else — specifically so this trade stays
  worth it rather than becoming "half the app is now in Rust."
- **Native AOT + static linking instead of a dynamically linked
  executable.** Produces one self-contained binary with no sibling `.so`
  and no shared shader/config install step (yet), at the cost of AOT's
  usual constraints (no runtime codegen, trimming-sensitive reflection —
  hence `EnableTrimAnalyzer`/`EnableAotAnalyzer` as build errors, not
  warnings, in the `.csproj`) and a build pipeline that requires both the
  Rust and .NET toolchains present together at publish time, orchestrated
  by CMake rather than `dotnet publish` alone.
- **GLFW via OpenTK instead of talking to Xlib/Wayland directly.** Costs a
  dependency, buys X11 *and* Wayland support from day one instead of
  committing to one compositor API, which is also why this is one of the
  two concrete feature wins over upstream GLava's mainline branch.

## Roadmap

- Root-cause (or work around) the `GpuFft` driver hang and switch the
  default FFT path back to the GPU.
- Debug `circle`, `graph`, and `wave` against the current preprocessor;
  extend `GlavaPreprocessor`/`RcConfig` as needed rather than assuming the
  modules themselves are at fault.
- Desktop-embedded rendering mode, equivalent to GLava's `-d` /
  `setxwintype "desktop"`. Initial target is **GNOME only**; other
  desktop environments (KDE, Sway, etc.) after that, contributions
  welcome.
- Installed config path (`~/.config/glavasharp`), mirroring GLava's
  `~/.config/glava` convention, instead of only reading `shaders/`
  next to the executable.

## Building

Requires: .NET 10 SDK, a Rust toolchain (via [rustup](https://rustup.rs)),
`clang`/`libclang-dev` (for PipeWire's bindgen-based bindings), and
`libpipewire-0.3-dev`. On Ubuntu/Debian:

```bash
sudo apt install libpipewire-0.3-dev pkg-config clang libclang-dev cmake
```

Then:

```bash
cmake -S . -B build
cmake --build build
# -> build/dist/GlavaSharp
```

To clean everything, including dotnet's `obj`/`bin` and cargo's `target/`
(not just CMake's own `build/` directory):

```bash
cmake --build build --target clean-all
```

You can also build each side independently:

```bash
# Rust shim only
./native/pwshim/build.sh

# .NET project only (plain build, no AOT, doesn't need the Rust lib at all)
dotnet build GlavaSharp.slnx
```

## Running

```bash
./build/dist/GlavaSharp                    # default sink monitor, bars module from rc.glsl
./build/dist/GlavaSharp --list-sinks       # see capture targets
./build/dist/GlavaSharp --list-gpus        # see DRM render nodes (for --gpu)
./build/dist/GlavaSharp --module radial    # force a specific module
```

See the top of `src/GlavaSharp/Program.cs` for the full CLI flag reference
(`--shaders`, `--gpu`, `--fft-size`, `--fft-attack`/`--fft-decay`/
`--fft-gain`, `--sample-rate`).

## License

No license has been chosen yet for this repository — add a `LICENSE` file
before treating this as open source in any legal sense. The bundled shader
tree under `src/GlavaSharp/shaders/glava/` originates from
[GLava](https://github.com/jarcode-foss/glava); refer to that project's
license for terms covering those files specifically, independent of
whatever license you choose for the rest of this repo.
