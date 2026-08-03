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
- [Packaging (`packaging/`)](#packaging-packaging)
- [Benchmarks](#benchmarks)
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
| Distributable artifact | Dynamically linked binary + installed shader/config tree under `/etc/xdg` or `~/.config/glava` | Self-contained Native AOT executable with the Rust audio/X11 shims statically linked in, but `build/dist/` itself is still multiple files (GLFW's `libglfw*.so`, dynamically linked, + `shaders/` alongside it, not installed system-wide); `cmake --build build --target appimage` packs that into one real single-file `.AppImage` — see [Packaging](#packaging-packaging) |
| Desktop-embedded mode (`glava -d` / `setxwintype "desktop"`) | Supported, X11 EWMH-based | **X11/xfwm4 implemented and verified** (`--desktop`, `--desktop-geometry`) — see [Desktop-embedded mode](#desktop-embedded-mode-windowingx11nativecs-nativex11shim); GNOME/Wayland not yet |
| Module maturity | All bundled modules (bars, radial, circle, graph, wave, ...) are production-quality | `bars`, `radial`, `circle`, `graph`, `wave` all verified working, plus two GlavaSharp-original modules GLava doesn't have: `waterfall` (a scrolling spectrogram) and `aurora` (a calming ambient desktop visualizer) — see [Status](#status--roadmap) |
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
- [x] **`aurora`** (GlavaSharp-original, not part of GLava) — a calming
      ambient desktop visualizer: soft curtains of color drift upward and
      sway like the northern lights, fading into a fully transparent
      background. Reuses the same persistent "history" feedback buffer as
      `waterfall`, but as a decay+drift loop instead of a hard scroll — no
      time/clock uniform involved, the motion comes entirely from
      re-sampling the buffer's own previous frame through a fixed sideways
      sway each frame. Two bugs found and fixed during initial live
      testing, both specific to feedback-loop shaders rather than typical
      GLava modules: (1) the feedback read direction was inverted (sampling
      *above* instead of *below* the current row), which pulled content
      down into the injection zone instead of letting it rise, so it never
      visibly drifted; (2) additively combining the decayed feedback with
      freshly injected energy, inside the injection zone, is an unbounded
      integrator (steady state ≈ `injected/(1-DECAY)`, hundreds of times
      over at a DECAY this close to 1) that clips straight to white within
      seconds — switched to `max(prev, injected)`, which lets new energy
      refresh the zone without the two terms compounding. See
      [GlavaSharp-original modules](#glavasharp-original-modules-shadersglavasharp).
- [x] **`aurora` rewritten into a curl-noise-driven volumetric sim —
      something GLava's own module format has no path to at all.** The
      original single-sine-sway version above still works exactly as
      described, but the current `aurora.glsl`/`1.frag`/`2.frag`/`noise.glsl`
      go considerably further: a real (divergence-free) curl-noise flow
      field with domain warping stands in for the one sine wave, blended
      across three differently-tuned virtual layers for a parallax look,
      sampled anisotropically along the local flow direction so feedback
      reads as *transported* rather than blurred, separated chromatically
      for a faint prismatic trailing edge, and thinned along
      high-curl regions so curtains fray into branching filaments instead
      of staying one solid sheet — still with **zero time/clock uniform
      anywhere in the pipeline** and **zero new host-side plumbing**: it's
      still just the one `history` buffer `waterfall` already established,
      read back through a much richer static field. Every bundled GLava
      module redraws from scratch every frame with no persistent state at
      all, and the shipped GLava shader tree has no noise/FBM/curl
      primitives anywhere in it — this isn't a GLava feature ported over,
      it's a category of effect GLava's format has no mechanism to express.
      Full technique breakdown in
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
- [x] **Bug found and fixed: bass reads as static/underused, treble as
      disproportionately "active," across every module.** Root cause:
      every module's `util/smooth.glsl` maps screen position to a raw,
      *linearly-spaced* FFT bin via `scale_audio`'s log-ish warp
      (`-log(1 - SAMPLE_RANGE*idx) / SAMPLE_SCALE`). With the stock
      constants, that warp's slope is nearly flat near `idx=0` -- a wide
      swath of screen space samples nearly the *same* few bass bins, which
      reads as static even though that's where the real energy is -- and
      steep near `idx=1`, where adjacent screen positions sample
      meaningfully different (sparser, noisier) high bins, which reads as
      "active" from frame-to-frame variance alone regardless of actual
      magnitude. Confirmed live: `bars --freq-scale linear` (the old,
      only, behavior) showed real bar height in roughly the first 5 of ~30
      visible bars, everything past that reading as flat, against
      broadband pink noise. Fixed with proper perceptual bucketing instead
      of retuning the existing warp's constants: raw FFT bins are now
      redistributed by actual frequency (Hz), on a user-selectable
      perceptual scale (`--freq-scale log2` default, or `mel`/`bark`/`erb`;
      `linear` keeps the old raw-bin behavior), *before* any shader sees
      them -- see [FFT](#fft-shaderscpufftcs-shadersgpufftcs) for the
      bucket-edge math and why it lives once, shared by both `CpuFft` and
      `GpuFft`, rather than in GLSL. Since the redistribution now happens
      upstream, `util/smooth.glsl`'s own warp has to become a no-op or it'd
      warp the (already-correctly-spaced) spectrum a second time -- gated
      behind a new GlavaSharp-original `_FREQ_PREBUCKETED` macro (same
      injection mechanism as `_USE_ALPHA`), defaulting to today's exact
      behavior unless a module was compiled with bucketing active. Verified
      live: `bars --freq-scale log2` against the same pink-noise signal
      showed real, varied bar height across roughly 30 bars instead of 5,
      and all seven bundled/original modules (`bars`, `radial`, `circle`,
      `graph`, `wave`, `waterfall`, `aurora`) still compile+link cleanly
      across all five `--freq-scale` values.

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
        below xfdesktop's own window (matched by `WM_CLASS`), relying on
        stacking order alone to keep clicks reaching desktop icons. Traded
        away later (see the stacking bug below) once it became clear the
        window needs to stay *above* xfdesktop to be visible at all, which
        would have put input-routing and visibility at odds. Fixed
        properly: `--desktop` gives the window an empty SHAPE-extension
        input region (`x11rb`'s `shape_rectangles(SET, INPUT, ..., &[])`),
        making the entire window click-through unconditionally, fully
        independent of stacking order — verified via `python-xlib`
        (`win.shape_get_rectangles(Input)` returns `[]`) on a live session.
  - [x] One thing observed but not chased down: `xprop` on the running
        window showed `_NET_WM_STATE` as `STICKY, SKIP_PAGER,
        SKIP_TASKBAR` — xfwm4 appears to overwrite the `BELOW` state
        GlavaSharp sets with its own computed set rather than keeping it.
        Didn't visibly matter (a `DESKTOP`-typed window is already
        implicitly bottom-of-normal-stack for xfwm4), so left as-is.
- [x] **Desktop-mode geometry control** (`--desktop-geometry X,Y,W,H`,
      GLava's `setgeometry` equivalent for `-d`). Previously the window
      always covered the whole screen; `RcConfig` was already parsing
      `setgeometry`'s width/height but silently discarding x/y. Now the
      CLI flag can place/size the desktop-mode window at an exact rect
      instead. Verified live: `--desktop --desktop-geometry
      200,150,900,500` produced a window at exactly `900x500+200+150`
      (confirmed via `xwininfo`), rendering correctly and staying
      transparent/click-through at that size.
  - [x] **Bug found and fixed: desktop mode wasn't actually fullscreen by
        default.** The first version of this feature also fell back to
        rc.glsl's own `setgeometry` (x/y/width/height) whenever
        `--desktop-geometry` wasn't passed. That was wrong: GLava's *stock*
        `rc.glsl` ships `#request setgeometry 0 0 800 600` unconditionally
        as the default *windowed*-mode size, so "rc.glsl has a
        `setgeometry` line" is true for nearly every rc.glsl, not just ones
        where the user actually wants desktop mode constrained. Result:
        `--desktop` alone silently shrank to an 800x600 box instead of
        covering the screen, on any unmodified rc.glsl. Fixed by removing
        the rc.glsl fallback entirely — only an explicit
        `--desktop-geometry` (or `--desktop-monitor`, below) constrains
        desktop mode now; no flag means "cover the whole screen," always.
        Verified live: same window went from `800x600+0+0` to the full
        `3286x1080+0+0` virtual screen after the fix.
- [x] **Per-monitor desktop mode** (`--desktop-monitor <index>`,
      `--list-monitors`). `--desktop-geometry` needs the user to already
      know pixel coordinates; `--desktop-monitor N` covers exactly monitor
      `N`'s rect instead, resolved via GLFW's own cross-platform monitor
      API (`GetMonitors`/`GetMonitorPos`/`GetVideoMode` — the same RandR
      data `--list-monitors` prints), not new X11 code. Resolved inside
      `AppWindow` rather than `Program.cs`, since monitor enumeration needs
      GLFW already initialized. Mutually exclusive with
      `--desktop-geometry`. Verified live on a real two-monitor setup:
      `--desktop-monitor 0` (a `1920x1080 at (1366,0)` monitor, confirmed
      via `--list-monitors`) produced a window at exactly
      `1920x1080+1366+0`, rendering only on that monitor — the other
      monitor's wallpaper was completely untouched.
- [x] **Bug found and fixed: window went permanently invisible after
      clicking empty desktop space.** Root-caused live against a real
      two-monitor xfwm4/XFCE session (not a compositor setting — first
      suspected xfwm4's "unredirect fullscreen windows"
      `/general/unredirect_overlays`, toggled it off, bug persisted, ruled
      it out). `xprop -root _NET_CLIENT_LIST_STACKING` while the window was
      hidden showed it sandwiched *below* the xfdesktop window for its own
      monitor (xfdesktop maps one window per monitor; `xwininfo` confirmed
      both windows shared the identical `1920x1080+1366+0` rect). Since
      xfdesktop paints the wallpaper opaquely across its whole window,
      GlavaSharp's alpha-blended output is only ever visible while stacked
      *above* xfdesktop — being below it means fully hidden, not "behind
      icons." Two compounding bugs in `x11shim`'s original restack logic:
      (1) it deliberately requested `StackMode::BELOW` xfdesktop, which is
      backwards from what actually makes the window visible; (2) it picked
      its restack target as "topmost mapped window with `WM_CLASS`
      containing xfdesktop," not the one actually covering the monitor
      GlavaSharp renders to — wrong on any multi-monitor xfdesktop setup.
      xfwm4's `click_to_focus` + `raise_on_click` (both on by default) raise
      the clicked monitor's xfdesktop window on every click; the old
      relower thread only ever lowered GlavaSharp further in response,
      never raised it back, so the first click on that monitor's desktop
      permanently sank the window. Fixed: `x11shim` now matches the
      xfdesktop window by root-relative geometry overlap with its own rect
      (`TranslateCoordinates` + `GetGeometry`, not just `WM_CLASS`) and
      restacks *above* it, re-asserting on every `_NET_CLIENT_LIST_STACKING`
      change so a later click-triggered raise gets immediately countered.
      Verified live: after the fix, `_NET_CLIENT_LIST_STACKING` placed the
      window above both xfdesktop windows, and a screenshot confirmed the
      module rendering over the wallpaper on its target monitor.
- [x] **Follow-up: same disappear-on-click symptom reported after the fix
      above, on a single-monitor XFCE/X11 session.** Two robustness gaps in
      the re-raise loop, not the restack-target logic: (1) the loop's error
      path treated *any* failed re-raise attempt as fatal and `break`s out of
      the thread entirely -- a single transient X error (very plausible when
      actively fighting the WM for z-order) silently and permanently disabled
      re-raising for the rest of the process's life, which reads exactly like
      "disappears once and never comes back." (2) the loop only ever woke up
      on `_NET_CLIENT_LIST_STACKING` `PropertyNotify`, with no fallback if
      xfwm4 doesn't regenerate that property for every internal restack it
      performs. Fixed: failed re-raises now log to stderr and keep the loop
      alive instead of killing it, and a `PERIODIC_RERAISE_INTERVAL` (500ms)
      timer re-asserts stacking independently of the property watch as a
      self-healing fallback. **Live re-test showed this wasn't the actual
      cause**: no stderr output appeared when the window disappeared on
      click, meaning the re-raise attempt either wasn't the failure point or
      was failing silently rather than erroring -- see the next entry.
- [x] **Real root cause of the disappear-on-click symptom: claiming
      `_NET_WM_WINDOW_TYPE_DESKTOP` on our own window, not a bug in the
      re-raise loop.** This window is WM-managed (xfwm4 reparented it), so a
      `ConfigureWindow` restack issued against it isn't applied directly by
      the X server -- root has `SubstructureRedirect` set, so the request is
      redirected to xfwm4 as a `ConfigureRequest` *event*, and xfwm4 decides
      whether to actually honor it. `.check()` only surfaces X protocol
      errors, not "the WM silently declined it," so a declined restack looks
      identical to a successful one from our side -- explaining the lack of
      any log output. Per EWMH, `_NET_WM_WINDOW_TYPE_DESKTOP` windows are
      meant to always sit beneath *every other window type*, enforced by the
      WM, not just requested -- and the entry above (in the first "Bug
      found and fixed" section) already had the evidence for this without
      it being connected yet: `xprop` showed xfwm4 silently overwriting our
      requested `_NET_WM_STATE` (dropping `BELOW`) whenever this window
      claimed type `DESKTOP`. By also claiming `_DESKTOP` (to mirror
      xfdesktop, on the theory that matching its type would make relative
      stacking requests meaningful), this window landed in the same
      bottom-of-everything bucket as xfdesktop itself, where xfwm4
      tie-breaks in xfdesktop's favor on every stacking recalculation --
      exactly what a desktop click triggers via `click_to_focus` +
      `raise_on_click`. Cross-checked against `xwinwrap` (the standard X11
      "app as desktop background" tool), which sidesteps this entire class
      of problem via override-redirect (exempting the window from WM
      management/redirect entirely) -- not adopted here since it would mean
      creating the X11 window ourselves instead of letting GLFW own
      creation. Fixed instead: the window now claims
      `_NET_WM_WINDOW_TYPE_NORMAL` with `_NET_WM_STATE_BELOW` +
      `_NET_WM_STATE_STICKY` + `_NET_WM_STATE_SKIP_TASKBAR` +
      `_NET_WM_STATE_SKIP_PAGER` requested explicitly (`DESKTOP` no longer
      implies them). `BELOW` on a `NORMAL` window is a different, ordinary,
      well-honored layer that every EWMH-compliant WM keeps *above* the
      desktop layer as a structural invariant rather than a per-window
      tie-break, so there's no fight to lose. The restack-above-xfdesktop
      thread (previously the primary mechanism) stays as a defensive
      fallback only. **Verified live**: the user confirmed the
      disappear-on-click symptom is gone after this change.
- [x] **Follow-up: fallback re-raise thread logging a repeating `BadWindow`
      (error code 3) on `ConfigureWindow`, major opcode 12, after the
      NORMAL+BELOW fix above.** Harmless to visibility (the primary
      BELOW-layer fix already made the fallback thread unnecessary for
      correctness) but pointed at a real staleness bug: `my_toplevel` (this
      window's frame, per `toplevel_ancestor`) was resolved exactly *once*
      at startup, immediately after `map_window`, then cached for the
      thread's entire lifetime. `map_window` on a WM-managed window is
      itself redirected (a `MapRequest` event, not an immediate map), so
      xfwm4's actual reparenting happens asynchronously -- and separately,
      xfwm4 appears to swap in a fresh frame shortly after seeing the Motif
      "no decorations" hint on this same remap. Either way, the
      once-at-startup `toplevel_ancestor` call could race ahead of xfwm4 and
      cache a frame ID that got destroyed moments later, which then never
      becomes valid again -- explaining the *repeating* (not one-off) error
      on every subsequent retry. Fixed: `raise_above_desktop_owner` now
      resolves `toplevel_ancestor` fresh on every call (initial raise and
      every re-raise alike) instead of accepting a cached value, mirroring
      how the xfdesktop-side sibling was already being re-resolved fresh
      each time.
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
- [x] **Bug found and fixed: "single self-contained executable" was only
      true of the one `GlavaSharp` file, not the directory you'd actually
      need to distribute.** `ls build/dist/` shows `GlavaSharp`,
      `GlavaSharp.dbg`, `libglfw.so.3.3`, `libglfw-wayland.so.3.3`, and
      `shaders/` — Native AOT statically links the Rust shims
      (pwshim/x11shim) but not GLFW (OpenTK's native GLFW package is
      dynamically loaded), and the shader tree was always meant to ship
      alongside rather than be embedded (see
      [Shader module pipeline](#shader-module-pipeline-shadersshadermodulecs)).
      Not a regression, just a README claim ("single self-contained
      executable ... no sibling `.so` files to lose track of") that didn't
      match what `build/dist/` actually contained. Fixed by adding a
      packaging step rather than re-architecting the linking: `cmake
      --build build --target appimage` (`packaging/build-appimage.sh`)
      packs `build/dist/` into one real single-file AppImage via
      `appimagetool` — see [Packaging](#packaging-packaging) for the full
      writeup, including the `.desktop`-file validation gotcha
      (`Categories=` needs registered/`X-`-prefixed values) hit during
      setup. Verified live: `GlavaSharp-x86_64.AppImage --module aurora`,
      run from `/tmp` (nowhere near the repo), correctly resolved and
      compiled shaders from its own FUSE mount point.

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
│       ├── shaders/glavasharp/  GlavaSharp-original modules (e.g. waterfall) --
│       │                         NOT part of GLava's tree, see below
│       └── shaders/fft/         GpuFft's compute kernel(s) -- not a GLava
│                                 module tree at all, loaded directly by
│                                 Shaders/GpuFft.cs, not ShaderModule
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

#### Perceptual frequency bucketing (`Shaders/FrequencyBucketing.cs`)

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
  [Status](#status--roadmap)) behavior, kept as an explicit opt-out.

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
`_USE_ALPHA`, see [Shader module pipeline](#shader-module-pipeline-shadersshadermodulecs))
based on `FftSettings.Scale`, and `scale_audio` becomes an identity
pass-through when it's set. See [Status](#status--roadmap) for the bug
this whole mechanism was built to fix.

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

**`waterfall`** uses this for a scrolling spectrogram:

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

**`aurora`** uses the same persistent buffer completely differently: not a
hard scroll, but a decaying feedback loop, tuned for a calming ambient
desktop backdrop rather than a literal spectrum readout. Its first version
read last frame's buffer at `uv - vec2(sway, DRIFT_SPEED)` — a smaller Y
(so whatever was *below* this row rises into it) offset sideways by
`sway = sin(uv.y * SWAY_FREQ * 2π) * SWAY_AMOUNT`, a fixed function of Y
alone. Since a given parcel of color's Y position changes every frame as it
drifts upward, it passed through a different sway value each step, tracing
an S-curve as it rose — organic-looking motion with no time/clock uniform
anywhere, purely from feedback (the buffer's own history *is* the state).
That's still exactly how the module stays animated with zero host-side
plumbing beyond the `history` mechanism above; what's changed is *what*
stands in for that one sine wave.

#### Why this is a different category of effect than anything in GLava

Every bundled GLava module redraws its output from scratch every single
frame — GLava's format has no concept of a value that survives between
frames at all (`history` is a GlavaSharp-original extension precisely
*because* nothing like it exists upstream). `waterfall` already stretches
that as far as a literal accumulator goes (shift a buffer down a row,
stamp a new one on top). `aurora` goes somewhere GLava's module format has
no path to reach regardless of how many `#request`s or passes you throw at
it: a real, mathematically fluid-like simulation, running entirely off
repeated spatial feedback through procedural noise fields, with the
*entire* animation state living in one 1024×512 RGBA texture and not one
extra uniform. The bundled GLava shader tree (`shaders/glava/`) has no
noise, FBM, curl, or domain-warp primitives anywhere in it — `noise.glsl`
(new, GlavaSharp-original) is the first thing in this codebase that needed
them, and it exists specifically because nothing upstream does this.

`noise.glsl` supplies the actual math, all of it deliberately time-free —
motion still has to emerge purely from re-sampling history through a
*fixed* field, never from an evolving one:

- **`valueNoise`** — quintic-interpolated (not cubic) value noise. The
  quintic blend has a zero second derivative at cell boundaries; that
  specifically matters here because `curlNoise` differentiates this
  function a second time, and cubic interpolation's visible
  second-derivative discontinuities would show up as faint creases right
  on the grid lines once curled.
- **`fbm`** — fractal Brownian motion (layered noise octaves), each octave
  rotated by a fixed non-axis-aligned matrix before scaling up. Without
  that rotation, octaves stack on the same grid axes and the sum reads as
  a recognizable plaid/tiled pattern rather than genuine irregularity.
- **`curlNoise`** — the curl of an FBM potential field, via central
  differences. Curling a potential this way *guarantees* a
  divergence-free vector field — the specific property that makes
  curl-driven flow look like real fluid instead of "noisy wobble": raw
  gradient-following noise visibly sucks material into low points or blows
  it apart from high points, while a curl field only ever swirls things
  around one another, never sourcing or sinking. That swirl-not-leak
  behavior is most of what actually reads as organic fluid motion.
- **`domainWarp`** — pushes a sample point through *two* rounds of FBM
  (fbm-of-fbm) before the caller uses it, so the warp has internal
  structure (folds within folds) instead of one uniform wobble applied
  everywhere. This is the specific technique behind the
  folding/stretching/tearing look real aurora curtains have, as opposed to
  a flat sheared gradient.

`1.frag`'s feedback pass then builds several techniques on top of that
field, each addressing a specific way naive feedback-through-noise reads
as fake:

- **Depth-weighted virtual layering.** `ShaderModule` only gives a module
  one persistent history buffer (a second/third *literally* independent
  persistent layer would need host-side changes — extra `#request uniform
  "history"` targets and matching ping-pong buffers in `ShaderModule.cs`).
  Instead, every frame's feedback read is a blend of `NUM_VLAYERS=3`
  virtual layers, each with its own decay/drift/sway-frequency/noise-scale/
  warp-strength/hue, sampled at its own flow-warped offset and combined by
  `LAYER_WEIGHT` (which sums to 1, so blending — unlike naive addition —
  can't runaway-brighten). Gets the layered-parallax look multi-layer
  aurora photography has, without the layers being independently
  addressable render targets.
- **Per-column drift variation.** A slow, x-only value-noise field (one
  per layer, offset so layers don't share the same lagging columns) speeds
  up or slows down each column's rise independently. Without it, every
  column in a layer rises in perfect lockstep — the single clearest tell
  that a "fluid" effect is actually a uniform scroll with noise sprinkled
  on top.
- **Anisotropic streak sampling.** Rather than one isotropic texture read
  per layer, `anisoSample` walks a short line of samples along the local
  flow direction (from that layer's curl vector) and weights them so the
  center dominates. A single isotropic sample makes feedback look
  *smeared*; sampling along the direction it's actually moving makes it
  look *transported* — the single highest-impact change for reading as
  fluid rather than blurred.
- **Chromatic feedback separation.** R and B are read with a tiny offset
  along (resp. against) the local flow direction from G/A, so fast-moving
  color picks up a faint prismatic leading/trailing edge instead of
  staying perfectly achromatic as it moves — kept small enough to be a
  trailing-edge cue, not a chromatic-aberration filter.
- **Filament thresholding.** Where the local flow magnitude is high
  (chaotic, fast-changing curl), a layer's contribution is thinned rather
  than left fully opaque, via `smoothstep` against `FILAMENT_THRESHOLD`.
  This is what breaks a solid curtain into branching strands that split
  and rejoin, instead of one continuous sheet.
- **Band-split audio response.** Bass, mid, and treble (banded averages
  of `smooth_audio` over three spectrum ranges — a banded average, not a
  single sample point, so one loud bin can't make a whole band flicker)
  each drive a *different kind* of visual response rather than everything
  pulsing together: bass boosts vertical drift speed and injection height,
  mid drives turbulence/warping (feeding `domainWarp`'s strength and a
  fold applied to the injection silhouette's own x-sampling), treble drives
  fine shimmer (noise modulating injected energy) and sparkles.
- **Sparkles.** Sparse, sharp bright points gated to only appear where
  treble is present *and* the ribbon already has presence (so they read as
  glints, not random static), hashed from a grid position that's itself
  been pushed through a cheap low-octave curl sample — so sparkles visibly
  drift and swirl with the current instead of twinkling fixed in place.
- **Temporal sharpening / nonlinear persistence.** Pure `prev * decay`
  feedback slowly turns to visual mush, because bilinear sampling of
  `hist` blends neighboring colors together every single frame and that
  blur compounds over hundreds of frames. Nudging HSV saturation back up
  and gamma-sharpening alpha each pass counteracts that drift without
  needing a whole extra unsharp-mask pass.
- **Compositional + dynamic coloring.** The hand-authored palette gradient
  is unchanged, but it's no longer sampled once and used directly:
  *which part* of the gradient gets sampled shifts with the bass/treble
  balance (bass-heavy moments pull toward teal/green, treble-heavy moments
  push toward violet/pink), and on top of that, hue is nudged by altitude
  and local flow speed (not time — avoids rainbow-cycling) so two ribbons
  at the same x but different height/speed/loudness read as distinguishably
  different colors instead of identical gradient copies.

`2.frag`'s display pass adds a few cheap, high-impact finishing touches:

- **Dual-radius bloom with edge highlighting piggybacked on the same
  fetches.** `sampleNeighborhood` reads an 8-neighbor ring at a given
  radius once, returning both the averaged bloom color *and* a
  luminance-gradient magnitude across those same taps (a cheap stand-in
  for a proper Sobel kernel, which would need its own fetch grid) — a
  thin bright rim right where brightness changes sharply is what reads as
  "a lit, three-dimensional sheet" rather than a flat blurry blob, and it
  costs nothing extra since it reuses the bloom ring's reads.
- **A fixed starfield** in the empty sky — hash-thresholded per grid cell
  with a second hash for per-star brightness variance, masked out
  anywhere the aurora already has presence. No time uniform in this pass
  either, so stars are a still backdrop, not a twinkling one.
- **Atmospheric haze** — a faint cool tint that grows with height via
  `smoothstep`, standing in for the "higher = further into the sky, so
  fainter/cooler" aerial-perspective depth cue real aurora photography
  has.

Verified live (screenshots against a synthetic pink-noise signal, both
before and after this rewrite): color correctly rises, folds, and frays
without the earlier runaway-brightness bug; exact pacing (per-layer decay/
drift/sway, band-response strengths, bloom/haze/star tunables, all in
`aurora.glsl`) is meant to be tuned to taste against real music, the same
as any other module's `#define` constants.

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

- marks the window `_NET_WM_WINDOW_TYPE_NORMAL` — deliberately *not*
  `_DESKTOP`, even though that's what GLava's own `setxwintype "desktop"`
  name suggests; see [Status](#status--roadmap) for why claiming the
  `DESKTOP` type actively works against this on xfwm4
- adds `_NET_WM_STATE_BELOW` + `_NET_WM_STATE_STICKY` ("below"/"pinned" —
  the same two states GLava's own `shaders/glava/env_Xfwm4.glsl` requests
  via `#request addxwinstate`, which GlavaSharp's `RcConfig` now actually
  reads) plus `_NET_WM_STATE_SKIP_TASKBAR` + `_NET_WM_STATE_SKIP_PAGER`
  (requested explicitly since the `DESKTOP` type no longer implies them)
- strips decorations via `_MOTIF_WM_HINTS`
- positions/sizes the window — the whole (multi-monitor) virtual screen by
  default, or an exact rect when the caller passes one, via either
  `--desktop-geometry X,Y,W,H` (exact pixels) or `--desktop-monitor
  <index>` (resolved from GLFW's monitor list — see
  [Status](#status--roadmap) for why rc.glsl's own `setgeometry` is
  deliberately *not* used as an implicit fallback here)
- gives the window an empty SHAPE-extension input region so it's fully
  click-through *unconditionally*, regardless of where the WM actually
  ends up placing it in the stack — this is what guarantees desktop icons
  stay clickable, independent of the stacking mechanics below
- as a defensive fallback (the primary "stay above xfdesktop" mechanism is
  now the `BELOW`-vs-`DESKTOP` layer ordering above, which xfwm4 enforces
  itself), spawns a background thread that watches the root window's
  `_NET_CLIENT_LIST_STACKING` property for changes, plus a periodic timer,
  and restacks above whichever xfdesktop window(s) overlap its own geometry
  (matched by `WM_CLASS` + root-relative rect, via `find_desktop_owner_toplevel`/
  `toplevel_ancestor` in `src/lib.rs`) — throttled to a 200ms minimum
  interval, since our own restack call is itself a stacking change; a
  failed attempt logs to stderr and retries rather than giving up

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
Geometry works differently: only `--desktop-geometry`/`--desktop-monitor`
constrain desktop mode's rect — rc.glsl's `setgeometry` is deliberately
*not* consulted for this (see [Status](#status--roadmap) for the bug that
caused, when it was).

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
dev headers at all), and `libpipewire-0.3-dev`. On Ubuntu:

```bash
sudo apt install dotnet-sdk-10.0 libpipewire-0.3-dev pkg-config clang libclang-dev cmake
```

Then:

```bash
cmake -S . -B build
cmake --build build
# -> build/dist/GlavaSharp (+ libglfw*.so + shaders/ alongside it --
#    see Packaging below for turning this into one actual file)
```

`CMakeLists.txt` orchestrates two `cargo build --release` invocations
(`native/pwshim`, `native/x11shim`) followed by `dotnet publish
-p:PublishAot=true`, statically linking both Rust staticlibs into the
final executable via `<NativeLibrary>`/`<DirectPInvoke>` in the `.csproj`.

`-DGLAVASHARP_AVX2_CPU_FFT=ON` adds `-p:IlcInstructionSet=avx2` to that
publish command, enabling `CpuFft`'s AVX2+FMA path in the AOT build (see
[Benchmarks](#benchmarks) for why this is off by default and what it
actually costs). Two things worth knowing if you're touching this option
itself: the value has to be `avx2` alone, not `avx2,fma` -- `fma` isn't a
standalone `--instruction-set` token this ILC version recognizes (`ilc
--help` lists the valid x64 set; AVX2 already implies FMA in its grouping,
confirmed by checking `CpuFft.UsingAvx2` on the resulting binary), and a
literal comma-containing value hits a separate MSBuild command-line
parsing quirk (`MSB1006: Property is not valid. Switch: fma`) if you do
add one without quoting the whole property. And `packaging/build-appimage.sh
--avx2-cpu-fft`'s `Text file busy` if `mksquashfs` can't write the output
file isn't a bug in the script -- it means the AppImage it's trying to
overwrite is currently FUSE-mounted/running; close that first.

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

## Packaging (`packaging/`)

`build/dist/` — Native AOT's idea of "self-contained" — is a directory,
not a file: `GlavaSharp` (the pwshim/x11shim Rust static libs *are* linked
in here), `GlavaSharp.dbg` (debug symbols, split out separately), and two
files Native AOT doesn't and can't statically link — `libglfw.so.3.3` /
`libglfw-wayland.so.3.3`, OpenTK's GLFW native package, dynamically loaded
at startup — plus `shaders/`, deliberately shipped alongside rather than
embedded (see [Shader module pipeline](#shader-module-pipeline-shadersshadermodulecs) —
modules are loaded from disk by directory, not compiled in). None of that
is a bug; it just means "single self-contained executable" was only ever
true of the one `GlavaSharp` file itself, not the directory you actually
need to hand someone.

`cmake --build build --target appimage` (`packaging/build-appimage.sh`,
not part of the default `ALL` build since it needs network access on first
run) packs all of it into one real single-file `.AppImage`:

- Assembles `build/AppDir/` from `build/dist/` verbatim (minus
  `GlavaSharp.dbg` — debug symbols aren't needed to run, and bloat the
  AppImage by more than the rest of it combined) under `usr/bin/`, plus
  `packaging/appimage/`'s `AppRun` (a thin argv-passthrough script),
  `GlavaSharp.desktop`, and `glavasharp.png` — a static 2D reduction of the
  `aurora` module's own look (layered wavy curtains colored by altitude,
  soft bloom, a starfield, atmospheric haze — see
  [GlavaSharp-original modules](#glavasharp-original-modules-shadersglavasharp)
  for what it's echoing), generated by
  `packaging/appimage/generate-icon.py` (numpy + Pillow, not a build
  dependency otherwise — rerun by hand and re-commit the PNG to retune it,
  it isn't regenerated as part of `cmake --build build --target appimage`).
- Fetches `appimagetool` from its GitHub releases on first run (cached
  under `build/tools/` afterwards) and runs it against `build/AppDir/`.
- Runs `appimagetool` itself with `APPIMAGE_EXTRACT_AND_RUN=1` — it's
  shipped as an AppImage too, which would otherwise try to FUSE-mount
  itself, and not every machine running this build has `/dev/fuse`
  available (many CI containers don't). This only affects how
  `appimagetool` runs during packaging; the `GlavaSharp` AppImage this
  produces supports both FUSE-mount and `--appimage-extract-and-run` for
  whoever runs it later, same as any AppImage.

Why `AppRun` needs no logic beyond an argv passthrough: `Program.cs`
resolves the shader tree via `AppContext.BaseDirectory` (wherever the
running executable actually lives), and `libglfw*.so` resolution is
already relative to that same directory today (confirmed live — running
`build/dist/GlavaSharp` directly, with no `LD_LIBRARY_PATH` set, already
finds `libglfw.so.3.3` sitting right next to it). Since
`packaging/build-appimage.sh` preserves that exact same relative layout
inside `AppDir/usr/bin/`, both keep resolving correctly once mounted —
verified live: `GlavaSharp-x86_64.AppImage --module aurora`, run from
`/tmp` (nowhere near the actual repo), logged shader paths resolving under
its own FUSE mount point (`/tmp/.mount_.../usr/bin/shaders/...`) and
compiled/linked normally.

The `.desktop` file exists because `appimagetool` requires (and validates)
one — `Categories=` values have to be real freedesktop.org registered
categories or prefixed `X-`; an early attempt using `Visualization`
unprefixed failed validation. `Terminal=true` since GlavaSharp is
fundamentally CLI-flag-driven (`--desktop`, `--module`, ...), not a
double-click GUI app with no arguments to pass.

## Benchmarks

### `CpuFft`: AVX2+FMA vectorization

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
`CpuFft.cs` -- it's Native AOT deliberately choosing portability over
performance by default, since an AOT binary (unlike JIT) is compiled once
and run on whatever hardware it's copied to, which might not be the build
machine. Setting `<IlcInstructionSet>avx2,fma</IlcInstructionSet>` doesn't
add a runtime check the way JIT has -- it bakes AVX2 in as a hard
requirement, deletes the scalar fallback from the compiled output
entirely, and makes the runtime fail-fast at startup (or, per a known ILC
edge case, occasionally a raw illegal-instruction crash) on any CPU that
turns out not to have it. Native AOT has no JIT to fall back to, so there
is no single-binary way to get "AVX2 when present, scalar otherwise" the
way this file's own source code implies -- that pattern only works for
JIT/framework-dependent builds.

**Resolved**: rather than picking one default for everyone, `build/dist/`
stays on the safe scalar baseline by default, and
`-DGLAVASHARP_AVX2_CPU_FFT=ON` (see [Building](#building-detailed)) opts a
build into the AVX2+FMA requirement explicitly -- `cmake --build build
--target appimage` names its output `GlavaSharp-x86_64-avx2.AppImage`
instead of the plain name whenever this is on, specifically so the
AVX2-requiring artifact can't be mistaken for, or silently overwrite, the
portable one. Verified live both ways: `CpuFft.UsingAvx2`/`--benchmark-fft`
reports `no` on a plain `cmake --build build` of this AVX2-capable CPU and
`yes` after reconfiguring with the option on and rebuilding -- same
machine, only the ILC instruction-set flag differs.

### `--benchmark-fft`: standalone CPU/GPU benchmark mode

`--benchmark-fft` runs entirely outside the normal app flow -- no window,
no `ShaderModule`, no audio capture, just `IFft.Process()` timed in a loop
and a results table on stdout. `--fft-device cpu|gpu` (default `cpu`)
picks which `IFft` implementation gets benchmarked, reusing the same flag
the real app uses; `--fft-attack`/`-decay`/`-gain`/`--sample-rate` apply as
normal, but `--fft-size` itself is ignored -- the benchmark always sweeps
its own fixed list (1024/2048/4096/8192) so one run reports the full
picture rather than needing four separate invocations.

`--fft-device gpu` needs a real GL context (compute shaders don't exist
without one) but deliberately never shows a window: it creates a GLFW
window with `WindowHintBool.Visible` false, uses it purely to get a
current GL 4.3 context, and never calls `SwapBuffers` or renders anything
-- exactly what `GpuFft.Process()` itself touches on the GPU side anyway
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
risking a hang --

```
size   ms/call   calls/sec  checksum
1024   0.1672    5980       17.431565
2048   0.1866    5359       21.986066
4096   skipped (needs 2048 compute invocations, this GPU allows 1024)
8192   skipped (needs 4096 compute invocations, this GPU allows 1024)
```

-- and the checksums matched `CpuFft`'s own (`17.431567`/`21.986067` at
the same sizes) to 5-6 significant figures, cross-checking correctness
between the two backends the same way the AVX2-vs-scalar comparison above
does. GPU numbers here are slower than CPU's at these sizes -- expected,
since every `GpuFft.Process()` call pays real upload/dispatch/readback
round-trip overhead that a single-workgroup, N≤2048-sized FFT is too small
to amortize; `GpuFft`'s actual purpose is freeing up the CPU core FFT
would otherwise occupy, not raw throughput at this scale.

## License

This project is licensed under the MIT License. See the LICENSE file for the full license text.

The bundled shader tree under `src/GlavaSharp/shaders/glava/` originates
from GLava and remains subject to its own license. See the original GLava
project for the licensing terms that apply to those files.
`src/GlavaSharp/shaders/glavasharp/` (GlavaSharp-original modules) is
covered by this project's own MIT license, same as the rest of the
codebase.
