# GlavaSharp — Technical Details

This is the in-depth companion to [README.md](README.md): architecture,
design trade-offs, the full status/roadmap checklist, and detailed build
instructions. Start with the README if you just want to build and run it.

> **Convention used in this file:** every known issue, bug, and roadmap
> item is tracked as a checkbox (`- [ ]` / `- [x]`). When something gets
> fixed or implemented, the box gets checked — the item and its writeup
> stay, they're not deleted. That way this file doubles as a running log of
> what actually happened, not just a snapshot of what's currently true.

---

## Table of contents

- [Why this exists](#why-this-exists)
- [How it compares to GLava](#how-it-compares-to-glava)
- [Status & Roadmap](#status--roadmap)
- [Architecture](#architecture)
  - [High-level pipeline](#high-level-pipeline)
  - [Project layout](#project-layout)
  - [Audio capture (`Audio/` + `native/pwshim/`)](#audio-capture-audio--nativepwshim)
  - [FFT (`Shaders/CpuFft.cs`, `Shaders/GpuFft.cs`)](#fft-shaderscpufftcs-shadersgpufftcs)
  - [GPU selection (`GpuEnumerator.cs`)](#gpu-selection-gpuenumeratorcs)
  - [Shader preprocessing (`Shaders/GlavaPreprocessor.cs`)](#shader-preprocessing-shadersglavapreprocessorcs)
  - [Shader module pipeline (`Shaders/ShaderModule.cs`)](#shader-module-pipeline-shadersshadermodulecs)
  - [GlavaSharp-original modules (`shaders/glavasharp/`)](#glavasharp-original-modules-shadersglavasharp)
  - [Windowing (`Windowing/AppWindow.cs`)](#windowing-windowingappwindowcs)
  - [Desktop-embedded mode (`Windowing/X11Native.cs`, `native/x11shim/`)](#desktop-embedded-mode-windowingx11nativecs-nativex11shim)
  - [Configuration (`Shaders/RcConfig.cs`)](#configuration-shadersrcconfigcs)
- [Design trade-offs](#design-trade-offs)
- [Building (detailed)](#building-detailed)
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
| FFT | Runs as GLava's own chained compute-shader "transform" passes (`window` → `fft` → `gravity` → `avg`) on the GPU | Two interchangeable backends, selected with `--fft-device`: a CPU FFT (`CpuFft`, the default) and a single-workgroup GLSL compute-shader FFT (`GpuFft`) that's bit-for-bit equivalent to it — see [FFT](#fft-shaderscpufftcs-shadersgpufftcs) |
| GPU selection | N/A | `--list-gpus` enumerates DRM render nodes; `--gpu <index>` pins rendering to one — see [GPU selection](#gpu-selection-gpuenumeratorcs) |
| Distributable artifact | Dynamically linked binary + installed shader/config tree under `/etc/xdg` or `~/.config/glava` | Single self-contained Native AOT executable with the Rust audio shim statically linked in; shader tree ships alongside it, not installed system-wide (yet) |
| Desktop-embedded mode (`glava -d` / `setxwintype "desktop"`) | Supported, X11 EWMH-based | **X11/xfwm4 implemented and verified** (`--desktop`, `--desktop-geometry`) — see [Desktop-embedded mode](#desktop-embedded-mode-windowingx11nativecs-nativex11shim); GNOME/Wayland not yet |
| Module maturity | All bundled modules (bars, radial, circle, graph, wave, ...) are production-quality | `bars`, `radial`, `circle`, `graph`, `wave` all verified working, plus a GlavaSharp-original module (`waterfall`, a scrolling spectrogram) GLava doesn't have — see [Status](#status--roadmap) |
| Build system | Meson (2.x) / legacy Makefile (1.x) | CMake orchestrating `cargo` + `dotnet publish` |

The short version: GlavaSharp is GLava's *shader-facing* design ported onto
a different, more memory-safe, cross-compositor host stack. It is not a
drop-in replacement, doesn't read GLava's installed config paths, and is
missing some GLava features (most of the `#request` surface, IPC/pipe
control) that GLava has had for years — though desktop-embedded mode, one
of the bigger gaps, now has a working X11 implementation.

## Status & Roadmap

This is **early alpha** software. Every item below is a checkbox — checked
items are done (with the full writeup of what the bug/feature actually
was, kept for context), unchecked items are open. If you hit something not
listed here, it's a real bug — please file an issue with the
`--list-gpus`/`--list-sinks` output and the module you were running.

### Modules

- [x] **`bars`** renders correctly against live PipeWire audio.
- [x] **`radial`** renders correctly against live PipeWire audio.
- [x] **`circle`**, **`graph`**, **`wave`** render correctly. These were
      broken until a real bug got fixed: every GLava module with a real
      (non-default-disabled) multi-pass chain declares its previous-pass
      sampler via `#request uniform "prev" tex"` — the bundled tree always
      names it `tex`, never `tex0` — but `ShaderModule.cs` bound the
      previous pass's output to a hardcoded uniform name `"tex0"`, which
      doesn't exist in any of these shaders, so the sampler was never
      actually bound and the pass rendered nothing. `bars`/`radial` only
      have a *disabled-by-default* second pass (`USE_ALPHA`/
      `_PREMULTIPLY_ALPHA` off), so this never got exercised there.
      `GlavaPreprocessor.Process` now captures `#request uniform "<role>"
      <name>` bindings (not just the `setsmoothfactor`/`setsmoothpass`
      ones it already handled) and `ShaderModule` binds each pass's
      previous-output sampler by the name the shader actually declared,
      falling back to `"tex0"` only if a pass declares none.
- [x] **`waterfall`** (GlavaSharp-original, not part of GLava) — a
      scrolling spectrogram: the audio spectrum's history over time,
      color-mapped (blue → cyan → green → yellow → red → white) and
      falling downward as new data arrives. See
      [GlavaSharp-original modules](#glavasharp-original-modules-shadersglavasharp).
- [ ] GLava's `#request transform ...` pipeline (chaining `window`/`fft`/
      `gravity`/`avg` as GPU shader passes) isn't implemented — GlavaSharp
      does windowing/FFT/gravity natively in `CpuFft`/`GpuFft` instead.
      None of the six current modules need it. A future module that leans
      on it would show the same symptom class as the `tex0`/`tex` bug:
      compiles fine, renders wrong or empty.
- [ ] GLava's full `@fg:`/`@bg:` foreground/background compositing model
      isn't implemented — GlavaSharp strips the tags and just draws the
      resulting color with normal alpha blending. Not currently needed by
      any bundled module.

### FFT

- [x] **`GpuFft` implemented and working.** The GPU compute-shader FFT
      (`Shaders/GpuFft.cs`) is a complete, from-scratch radix-2
      Cooley-Tukey implementation, architecturally in the same spot
      GLava's own `fft_radix*.glsl` compute kernels occupy, and produces
      the same spectrum as `CpuFft` (windowing, bit-reversal, twiddle
      math, and log-compressed normalization all match).
  - [x] `glCompileShader`/`glLinkProgram` could hang on at least one real
        driver stack (Mesa/Intel iris) with no error reported when `LOGN`
        was baked in as a compile-time constant, since the driver's NIR
        unroller would try to fully unroll the stage loop and
        scalar-replace the per-invocation `shared` arrays. Fixed by
        keeping `LOGN` (and the other tunables) as a `uniform` instead of
        a constant.
  - [x] The `u_logN` uniform is declared `uint` in GLSL but was being
        uploaded with the signed-int GL entry point (`glUniform1i` instead
        of `glUniform1ui`); on drivers that enforce the spec's
        type-matching rule this left the uniform at `0`, silently
        skipping every FFT stage and producing time-domain noise instead
        of a spectrum. Fixed by uploading it through the correct unsigned
        overload.
- [ ] Validate `GpuFft` across more GPU vendors/drivers and switch the
      default FFT path (`--fft-device`) to GPU once it has enough
      real-world mileage. `CpuFft` remains the default (`--fft-device
      cpu`); pass `--fft-device gpu` to try it. See
      [FFT](#fft-shaderscpufftcs-shadersgpufftcs).

### Desktop-embedded mode

- [x] **X11/xfwm4 (XFCE) implemented and verified live.** `--desktop` (or
      rc.glsl's `setxwintype "desktop"`, which `env_Xfwm4.glsl` already
      requests) pins a transparent-background, click-through window behind
      desktop icons via EWMH hints — see
      [Desktop-embedded mode](#desktop-embedded-mode-windowingx11nativecs-nativex11shim).
      Confirmed against a live xfwm4/XFCE session:
  - [x] `_NET_WM_WINDOW_TYPE_DESKTOP` and decoration-stripping (via
        `_MOTIF_WM_HINTS`) both land correctly.
  - [x] The window resizes/repositions to the requested rect (whole screen
        by default, or a specific one via `--desktop-geometry`/rc.glsl's
        `setgeometry` — see below).
  - [x] **Transparency.** `AppWindow` requests GLFW's
        `TransparentFramebuffer` hint whenever `--desktop` is set —
        without it, GLava's shaders already writing `alpha = 0` for
        "nothing here" pixels (e.g. `shaders/glava/bars/1.frag`'s default
        `fragment = vec4(0,0,0,0)`) didn't matter, because the X server
        still composited the window as fully opaque. With the hint set
        and a compositor running (xfwm4 ships one), the wallpaper/icons
        show through everywhere the active module doesn't draw. Verified
        visually on a live session.
  - [x] **Click-through.** First attempt: restack the window explicitly
        below xfdesktop's own window (matched by `WM_CLASS`), not just a
        plain sibling-less `Below` request. Didn't work — xfwm4 doesn't
        honor it enough to place the window strictly *underneath*
        xfdesktop specifically in `_NET_CLIENT_LIST_STACKING`; xfwm4
        appears to keep xfdesktop pinned at the true bottom regardless of
        what other desktop-typed clients request. Fixed differently:
        `--desktop` now gives the window an empty SHAPE-extension input
        region (`x11rb`'s `shape_rectangles(SET, INPUT, ..., &[])`),
        making the entire window click-through unconditionally,
        independent of stacking order — verified via `python-xlib`
        (`win.shape_get_rectangles(Input)` returns `[]`) on the same live
        session.
  - [x] One thing observed but not chased down: `xprop` on the running
        window showed `_NET_WM_STATE` as `STICKY, SKIP_PAGER,
        SKIP_TASKBAR` — xfwm4 appears to overwrite the `BELOW` state
        GlavaSharp sets with its own computed set rather than keeping it.
        Didn't visibly matter (a `DESKTOP`-typed window is already
        implicitly bottom-of-normal-stack for xfwm4), so left as-is.
- [x] **Desktop-mode geometry control** (`--desktop-geometry X,Y,W,H`,
      GLava's `setgeometry` equivalent for `-d`). Previously the window
      always covered the whole screen; `RcConfig` was already parsing
      `setgeometry`'s width/height but silently discarding x/y. Now both
      the CLI flag and rc.glsl's own `setgeometry` (when `--desktop` is
      set and `--desktop-geometry` isn't passed) can place/size the
      desktop-mode window at an exact rect instead. Verified live:
      `--desktop --desktop-geometry 200,150,900,500` produced a window at
      exactly `900x500+200+150` (confirmed via `xwininfo`), rendering
      correctly and staying transparent/click-through at that size.
- [ ] **GNOME** — Mutter doesn't honor `_NET_WM_WINDOW_TYPE_DESKTOP`
      stacking the way xfwm4 does, so this likely needs its own approach
      rather than reusing `x11shim`'s as-is. Not started.
- [ ] **Native Wayland** — no EWMH at all under Wayland; would need a
      completely different mechanism (likely a `wlr-layer-shell` client
      for wlroots-based compositors, with no obvious equivalent on GNOME/
      KDE's Wayland sessions). Not started.
- [ ] Other desktop environments (KDE, Sway, etc.) — not started,
      contributions welcome.

### Everything else

- [x] **Fixed: AOT static-linking regression.** Found while testing
      desktop mode live — the working tree had uncommitted edits to
      `GlavaSharp.csproj` that had dropped `<DirectPInvoke
      Include="pwshim"/>` and the `-lpipewire-0.3` linker arg. Without
      `DirectPInvoke`, ILC doesn't bind `pwshim_start`/`pwshim_stop` at
      compile time, so the AOT build falls back to `dlopen("pwshim.so")`
      at runtime — which doesn't exist, since `pwshim` is meant to be
      statically linked — crashing with `DllNotFoundException` the moment
      audio capture started. Restored both, and added the matching
      `DirectPInvoke` entry for `x11shim` (which would have hit the exact
      same bug).
- [x] **Fixed: GLFW `glfwGetPlatform()` crash.** The bundled GLFW build
      predates `glfwGetPlatform()` (a GLFW 3.4+ API) — already guarded
      elsewhere in `AppWindow` (`LogSelectedPlatform`), but the desktop-mode
      X11 platform check didn't have the same `EntryPointNotFoundException`
      fallback, so `--desktop` crashed instead of just skipping the check
      and proceeding on the assumption that the X11 init hint already
      forced GLFW onto X11 (which it does, or `Init()` would have failed
      earlier).
- [ ] **No installed config tree.** GlavaSharp reads `shaders/glava/` next
      to the executable (or wherever `--shaders` points); it doesn't yet
      look in `~/.config/glavasharp` the way GLava looks in
      `~/.config/glava`.

## Architecture

### High-level pipeline

```
 PipeWire "what you hear" monitor
            │  (Rust: native/pwshim, staticlib, statically linked via Native AOT)
            ▼
   PipeWireAudioSource (Audio/)  ──▶  RingBuffer  ──▶  AudioWindow (tail buffer)
                                                              │
                                                              ▼
                                           IFft.Process() (Shaders/CpuFft.cs or GpuFft.cs,
                                              per --fft-device) windowed FFT →
                                              log-compressed, gravity-smoothed
                                                magnitude spectra (left, right)
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
├── CMakeLists.txt               orchestrates: cargo build (native/pwshim, native/x11shim) → dotnet publish (AOT)
├── .github/workflows/ci.yml     CI: rust jobs (pwshim, x11shim), dotnet job, full AOT integration job
├── src/
│   └── GlavaSharp/              the .NET project — all C# source lives here
│       ├── GlavaSharp.csproj
│       ├── Program.cs           CLI parsing, wiring, entry point
│       ├── FftSettings.cs
│       ├── GpuEnumerator.cs     --list-gpus / --gpu N (DRI_PRIME etc.)
│       ├── Audio/               PipeWire capture (P/Invoke into native/pwshim)
│       ├── Shaders/             FFT, shader preprocessing, module pass pipeline
│       ├── Windowing/           GLFW window/context/frame-loop,
│       │                       X11 desktop-mode P/Invoke (X11Native.cs)
│       ├── shaders/glava/       GLava's own shader tree, bundled as-is
│       └── shaders/glavasharp/  GlavaSharp-original modules (e.g. waterfall) --
│                                 NOT part of GLava's tree, see below
└── native/
    ├── pwshim/                  standalone Rust crate — NOT nested in the
    │                             C# project, since it isn't C# code and has
    │                             its own independent build/test lifecycle
    │   ├── Cargo.toml
    │   └── src/lib.rs           PipeWire stream capture, exposed via a
    │                             small extern "C" FFI surface
    └── x11shim/                 same reasoning as pwshim: standalone Rust
                                  crate, statically linked, not nested in
                                  the C# project
        ├── Cargo.toml
        └── src/lib.rs           X11 EWMH desktop-mode (--desktop): window
                                  type/state, decorations, geometry,
                                  click-through, and a background
                                  re-lower-on-restack watcher, exposed via
                                  a small extern "C" FFI surface
```

`native/pwshim/` living outside `src/GlavaSharp/` (rather than e.g. a
`native-rs/` folder nested inside the C# project, which is how this
started out) is deliberate: it's a fully independent build unit with its
own `Cargo.lock`, its own CI job, and its own release cadence — nesting it
inside the .NET project directory implied an ownership relationship that
doesn't reflect how the two are actually built, versioned, or tested.
`native/x11shim/` follows the same reasoning.

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
  artifact is one file, no sibling `libpwshim.so` to lose track of. See
  [Status](#status--roadmap) for a real regression this project hit when
  `DirectPInvoke` got accidentally dropped from the csproj.

### FFT (`Shaders/CpuFft.cs`, `Shaders/GpuFft.cs`)

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
opt-in today (pass `--fft-device gpu`) — see [Status](#status--roadmap)
for the two driver-level bugs already found and fixed during bring-up.

### GPU selection (`GpuEnumerator.cs`)

`--list-gpus` enumerates the system's DRM render nodes and prints an
indexed list of the GPUs GlavaSharp can render on:

```
Available GPUs (use --gpu <index>):
  [0] AMD (pci id 0x1002:0x73df, driver amdgpu) [card0]
  [1] Intel (pci id 0x8086:0x4680, driver i915) [card1]
```

Each entry shows the vendor, PCI device ID, kernel driver, and DRM card
node backing that index. Pass the index to `--gpu <index>` to pin
rendering to that GPU (useful on hybrid-graphics laptops where the
default render node isn't the one you want driving the visualizer).
`--gpu` affects which GPU renders the window/shader pipeline; it's
independent of `--fft-device gpu`, which only controls where the FFT
itself runs.

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
  plain GLSL identifiers (`_SMOOTH_FACTOR`, `_PRE_SMOOTHED_AUDIO`).
- `#request uniform "<role>" <name>` — GLava lets a pass declare its own
  GLSL identifier for a semantic role (`screen`, `audio_sz`, `audio_l`,
  `audio_r`, `prev`, `history` [GlavaSharp-original, see below], ...)
  instead of GlavaSharp assuming a fixed name. `Process()` returns these
  role → name bindings alongside the source (not just stripping the line
  like everything else here) so `ShaderModule` can bind each pass's
  previous-output sampler by whatever name the shader actually used,
  instead of guessing. This matters in practice: the bundled tree always
  names it `tex`, and `ShaderModule` used to hardcode `"tex0"` — see
  [Status](#status--roadmap) for the bug that caused. Every other
  `#request` line is stripped as a no-op.
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
pipeline and the full compositing model behind `@fg:`/`@bg:` — see
[Status](#status--roadmap).

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
`_fboB`), each one receiving the previous enabled pass's output (as
whatever uniform name that pass declared via `#request uniform "prev"
<name>`, see above), plus the two audio spectrum textures as
`audio_l`/`audio_r`. The last *enabled* pass (not necessarily the last
file — a trailing disabled pass shouldn't swallow the real output) renders
directly to the default framebuffer.

`ShaderModule` also resolves module directories from a second, sibling
location — see the next section — so `--module waterfall` works exactly
like `--module bars` without callers needing to know which shader tree it
actually lives under.

### GlavaSharp-original modules (`shaders/glavasharp/`)

GLava's own module format has no concept of state that survives *across*
frames — every one of its modules redraws from scratch every frame, reading
only the current spectrum. That's fine for bars/waves/graphs, but it can't
express something like a scrolling spectrogram, which needs to remember
what it drew last frame and shift it.

`shaders/glavasharp/` holds modules written for GlavaSharp specifically —
not part of GLava's own bundled tree (`shaders/glava/`, reproduced
unmodified) — that need this. They still follow GLava's own module
convention (numbered `N.frag` passes) and load through the exact same
`ShaderModule` pipeline as any GLava module; `ShaderModule`'s constructor
just falls back to this sibling directory when a module name isn't found
under the primary `shaders/glava/` root.

The mechanism that makes this possible is a GlavaSharp-original extension
to the `#request uniform "<role>" <name>` convention: a pass that declares
`#request uniform "history" <name>` gets a **persistent** ping-pong
texture pair (fixed 1024×512 resolution, independent of window size) that
is *not* cleared every frame like the normal ping-pong buffers are. The
pass reads the other buffer (last frame's content) and writes into "its"
buffer; the two swap roles every frame. A later pass in the same module
reads the just-written buffer as its own `#request uniform "prev"`
texture, same as any other multi-pass chain.

**`waterfall`** (the current — and so far only — module here) uses this
for a scrolling spectrogram:

- `1.frag` (the history/accumulate pass): for the topmost row of the
  history texture, it samples the current spectrum (both channels,
  smoothed the same way `bars.glsl`/`circle.glsl` do via
  `util/smooth.glsl`) and maps the magnitude through a heat-gradient color
  ramp (dark blue → cyan → green → yellow → red → white). For every other
  row, it copies the pixel directly above it from last frame's texture —
  shifting the whole image down by one row, so old data "falls" and
  eventually scrolls off the bottom.
- `2.frag` (the display pass): samples the accumulated history texture,
  stretched to fill the actual window.

Verified live: a proper scrolling, color-mapped spectrogram reacting to
real audio — see [Status](#status--roadmap).

### Windowing (`Windowing/AppWindow.cs`)

A deliberately thin GLFW wrapper — not OpenTK's `GameWindow` — so init
hints, platform selection, and the frame loop are all explicit and
GLava-shaped rather than inherited from a general-purpose game-engine
loop. `PlatformPreference.Any` lets GLFW pick Wayland when running inside
a Wayland session and fall back to X11 otherwise, which is what actually
gives GlavaSharp both-compositor support — GLava's mainline branch talks
to Xlib directly and is X11-only (its `unstable` branch has experimented
with GLFW too, for the same reason).

### Desktop-embedded mode (`Windowing/X11Native.cs`, `native/x11shim/`)

GLava's `-d` / `setxwintype "desktop"` renders pinned behind desktop icons
instead of as a normal top-level window — GLFW has no concept of this (it's
inherently below GLFW's cross-platform abstraction), so GlavaSharp drops to
raw X11 for just this piece, the same way it drops to Rust for PipeWire:
`AppWindow` still owns window/context creation via GLFW, but once the
window exists, `--desktop` hands its X11 window ID (via GLFW's
`GetX11Window`) to `native/x11shim`, a small Rust crate that does the
actual EWMH work on its own connection to the X server:

- marks the window `_NET_WM_WINDOW_TYPE_DESKTOP`
- adds `_NET_WM_STATE_BELOW` + `_NET_WM_STATE_STICKY` ("below"/"pinned" —
  the same two states GLava's own `shaders/glava/env_Xfwm4.glsl` requests
  via `#request addxwinstate`, which GlavaSharp's `RcConfig` now actually
  reads)
- strips decorations via `_MOTIF_WM_HINTS`
- positions/sizes the window — the whole screen by default, or an exact
  rect when the caller passes one (`--desktop-geometry` / rc.glsl's
  `setgeometry`, GLava's equivalent for desktop mode)
- restacks it below xfdesktop's own window when it can find one (matched
  by `WM_CLASS`, via the SHAPE extension's underlying `query_tree`-walked
  top-level/frame window; see `find_desktop_owner_toplevel`/
  `toplevel_ancestor` in `src/lib.rs`) as a best-effort visual-ordering
  nicety
- gives the window an empty SHAPE-extension input region so it's fully
  click-through *unconditionally*, regardless of where the WM actually
  ends up placing it in the stack — this, not the restacking above, is
  what actually guarantees desktop icons stay clickable; see
  [Status](#status--roadmap) for why the restack alone wasn't enough
- spawns a background thread that watches the root window's
  `_NET_CLIENT_LIST_STACKING` property for changes and re-lowers the window
  (throttled to a 200ms minimum interval, since our own lower call is
  itself a stacking change) — the same "keep re-lowering" behavior GLava
  relies on to stay behind desktop icons if something else restacks it (a
  WM restart, xfdesktop remapping its icon layer, etc.)

All of this happens through `x11rb`, a pure-Rust library that speaks the
X11 wire protocol directly over a Unix socket, rather than linking
`libX11`/Xlib. That means, unlike `native/pwshim` (whose `pipewire`
dependency needs `bindgen`/`clang`/`libpipewire-0.3-dev` at build time),
`native/x11shim` has no system dependencies beyond a Rust toolchain — see
[Design trade-offs](#design-trade-offs).

`--desktop` forces `WindowOptions.Platform` to X11 (rather than
`PlatformPreference.Any`) so a Wayland session doesn't silently swallow the
flag; `AppWindow` fails loudly (not silently) if GLFW doesn't actually end
up on X11. It's also settable from `rc.glsl` via GLava's own
`#request setxwintype "desktop"` directive — `--desktop` on the CLI and
`setxwintype "desktop"` in `rc.glsl` both work, CLI wins if both differ.
Same CLI-wins-over-rc.glsl precedence applies to
`--desktop-geometry`/`setgeometry`.

Currently targets xfwm4 (XFCE) first, since it's one of the more
EWMH-compliant window managers for this particular behavior — hence why
GLava's own `env_Xfwm4.glsl` is only three lines. GNOME and native Wayland
are unimplemented follow-ups — see [Status](#status--roadmap).

### Configuration (`Shaders/RcConfig.cs`)

A tiny reader for the handful of top-level `rc.glsl` values GlavaSharp
actually acts on today (module name, window title/size/position, FFT
buffer size, sample rate, and `setxwintype "desktop"` — see
[Desktop-embedded mode](#desktop-embedded-mode-windowingx11nativecs-nativex11shim))
— not a general `#request` interpreter. Most of `rc.glsl`'s directive
surface (window decoration/floating/opacity hints, other `setxwintype`
values like `"dock"`/`"panel"`, etc.) is parsed by nothing yet and simply
has no effect.

## Design trade-offs

- **CPU FFT as the default, GPU FFT available as opt-in.** `GpuFft` is a
  real, working, bit-for-bit-equivalent GPU implementation (see
  [FFT](#fft-shaderscpufftcs-shadersgpufftcs)), but it's newer and has
  already surfaced two driver-level gotchas (a compile hang, a uniform
  type mismatch) during bring-up on a single machine. `CpuFft` stays the
  default until `GpuFft` has more mileage across GPU vendors/drivers;
  pass `--fft-device gpu` to try it.
- **A small preprocessor subset instead of a full GLava-language
  reimplementation.** Enough to run real, unmodified GLava shader files
  for the common cases. Reimplementing 100% of GLava's preprocessor was
  judged not worth doing before validating the rest of the pipeline end
  to end — see [Status](#status--roadmap) for what's still unimplemented
  (none of it currently blocks any bundled module).
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
  by CMake rather than `dotnet publish` alone. This trade-off directly
  caused a real regression once — see [Status](#status--roadmap) for the
  `DirectPInvoke` story.
- **GLFW via OpenTK instead of talking to Xlib/Wayland directly.** Costs a
  dependency, buys X11 *and* Wayland support from day one instead of
  committing to one compositor API, which is also why this is one of the
  two concrete feature wins over upstream GLava's mainline branch.
- **`x11rb` (pure-Rust X11 protocol) in `native/x11shim` instead of raw
  Xlib P/Invoke from C#, for desktop-embedded mode.** GLFW's window/context
  creation stays untouched — only the EWMH property/stacking work that GLFW
  has no concept of moves to Rust, mirroring the same reasoning as
  `native/pwshim`: keep the trickiest native surface (here, background
  thread + X server connection, watching for and reacting to async stacking
  events) in a language whose type system can actually check it, behind a
  narrow `extern "C"` surface. Using `x11rb` over `libX11`/Xlib-sys bindgen
  specifically avoids repeating pwshim's `clang`/`libclang-dev` build
  dependency for a crate whose actual protocol surface (a handful of
  `ChangeProperty`/`ConfigureWindow`/`ChangeWindowAttributes`/
  `ShapeRectangles` requests) is far narrower than PipeWire's.
- **Click-through via the SHAPE extension instead of relying on window
  stacking.** Once live testing showed xfwm4 won't reliably restack a
  client strictly below xfdesktop no matter how the request is phrased,
  making the window unconditionally click-through sidesteps the fight
  entirely instead of chasing WM-specific stacking behavior further — see
  [Status](#status--roadmap).
- **A persistent "history" buffer as a `ShaderModule` extension, rather
  than a separate module type/interface, for `waterfall`.** GLava's module
  format has no feedback/persistence concept at all, but bolting the
  minimum needed (one more `#request uniform` role, one more ping-pong
  pair that isn't cleared per-frame) onto the existing pass-chain
  machinery reused ~90% of it, instead of forking a parallel "native
  module" abstraction with its own render path.

## Building (detailed)

Requires: .NET 10 SDK, a Rust toolchain (via [rustup](https://rustup.rs)),
`clang`/`libclang-dev` (for PipeWire's bindgen-based bindings, needed by
`native/pwshim` only — `native/x11shim` is pure Rust and needs no system
dev headers at all), and `libpipewire-0.3-dev`. On Ubuntu/Debian:

```bash
sudo apt install libpipewire-0.3-dev pkg-config clang libclang-dev cmake
```

Then:

```bash
cmake -S . -B build
cmake --build build
# -> build/dist/GlavaSharp
```

`CMakeLists.txt` orchestrates two `cargo build --release` invocations
(`native/pwshim`, `native/x11shim`) followed by `dotnet publish
-p:PublishAot=true`, statically linking both Rust staticlibs into the
final executable via `<NativeLibrary>`/`<DirectPInvoke>` in the `.csproj`.

To clean everything, including dotnet's `obj`/`bin` and both crates'
`target/` directories (not just CMake's own `build/` directory):

```bash
cmake --build build --target clean-all
```

You can also build each side independently:

```bash
# Rust shims only
./native/pwshim/build.sh
./native/x11shim/build.sh

# .NET project only (plain build, no AOT, doesn't need either Rust lib at all)
dotnet build GlavaSharp.slnx
```

Note: a plain `dotnet build`/`dotnet run` (no `PublishAot`) can't actually
run the app end-to-end — `PipeWireNative`/`X11Native`'s `LibraryImport`
calls need the statically-linked AOT build to resolve; without it they'll
throw `DllNotFoundException` trying to `dlopen` a `.so` that doesn't
exist. Use it for editing/compiling C# quickly; use the full `cmake
--build build` flow to actually run GlavaSharp.

## License

This project is licensed under the MIT License. See the LICENSE file for the full license text.

The bundled shader tree under `src/GlavaSharp/shaders/glava/` originates
from GLava and remains subject to its own license. See the original GLava
project for the licensing terms that apply to those files.
`src/GlavaSharp/shaders/glavasharp/` (GlavaSharp-original modules) is
covered by this project's own MIT license, same as the rest of the
codebase.
