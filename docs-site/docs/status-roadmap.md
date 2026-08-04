# Status & Roadmap

This is **early alpha** software. Every item below is a checkbox — checked
items are done (with the full writeup of what the bug/feature actually
was, kept for context), unchecked items are open. If you hit something not
listed here, it's a real bug — please file an issue with the
`--list-gpus`/`--list-sinks` output and the module you were running.

## Modules

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
      [GlavaSharp-Original Modules](architecture/original-modules.md).
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
      [GlavaSharp-Original Modules](architecture/original-modules.md).
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
      [GlavaSharp-Original Modules](architecture/original-modules.md).
- [x] **`clock`** — an analog clock face (real hour/minute/second hands)
      drawn over an ordinary audio-reactive radial spectrum. `1.frag` is
      `#include ":radial/1.frag"` and nothing else — the glowing
      circle+bars are `radial`'s own first pass, reused verbatim rather
      than copy-pasted, since `RootDir` stays fixed throughout a nested
      `#include` regardless of `clock/`'s own directory being resolved via
      the sibling-module fallback. `2.frag`'s hands are driven by one
      `#request property "seconds_since_midnight"` uniform carrying a
      `#request feed "seconds_since_midnight" clock` binding (see
      [Live Control Channel & Hot-Reload](architecture/control-channel.md)
      below) — proves the feed mechanism end to end: no host code
      anywhere has a hardcoded notion of "time," `AppWindow` just samples
      whatever `Control/FeedRegistry.cs`'s `"clock"` entry returns
      (`DateTime.Now.TimeOfDay.TotalSeconds`) into a property that would
      otherwise be a completely ordinary manually-tunable slider. See
      [GlavaSharp-Original Modules](architecture/original-modules.md).
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

## FFT

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
      [FFT & Frequency Bucketing](architecture/fft.md).
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
      them -- see [FFT & Frequency Bucketing](architecture/fft.md) for the
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

## Desktop-embedded mode

- [x] **X11/xfwm4 (XFCE) implemented and verified live.** `--desktop` (or
      rc.glsl's `setxwintype "desktop"`, which `env_Xfwm4.glsl` already
      requests) pins a transparent-background, click-through window behind
      desktop icons via EWMH hints — see
      [Desktop-Embedded Mode](architecture/desktop-embedded-mode.md).
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

## Live control channel & shader hot-reload

- [x] **Shader hot-reload.** `ShaderModule` tracks, per compiled pass, the
      full set of files it pulled in via `#include` (transitively —
      `GlavaPreprocessor.Process` now returns that set alongside the usual
      preprocessed source). A `FileSystemWatcher` over the shader tree
      (`RootDir`, plus a second watcher over the sibling `glavasharp/`
      directory for modules resolved via that fallback — see
      [GlavaSharp-Original Modules](architecture/original-modules.md))
      marks a file dirty on save; `ShaderModule.ReloadIfDirty()`, called
      once per frame from the render thread (never from the watcher's own
      callback thread — no GL context there), recompiles every pass whose
      dependency set contains a dirty file. Editing a module's own `.frag`
      recompiles just that pass; editing a shared file like
      `util/smooth.glsl` or `aurora.glsl` recompiles every pass across the
      module that included it. A failed recompile logs an error and leaves
      the previous, still-working GL program running rather than tearing
      anything down.
- [x] **Live-tweakable per-module properties.** A pass can declare
      `#request property "name" float default min max` (a GlavaSharp
      extension to GLava's `#request` convention, parsed the same way
      `#request uniform` already was) right next to the `uniform float
      name;` it already needs — see `shaders/glavasharp/aurora/1.frag`'s
      `amplify` for the worked example (replaced what used to be a
      `#define AMPLIFY 2.6` in `aurora.glsl`). `ShaderModule` re-applies
      the current value to every pass that declared it on each `Render()`
      call, so a change takes effect on the very next frame with no
      recompile involved.
- [x] **Feed-driven properties.** A property can also declare
      `#request feed "name" source` (a second, separate line from
      `#request property` — feed-eligibility is an orthogonal annotation
      on an already-complete property declaration, not a different kind of
      property) to opt into being driven by a named built-in data source
      instead of manual slider input — e.g.
      `shaders/glavasharp/clock/2.frag`'s `#request feed
      "seconds_since_midnight" clock`. `Control/FeedRegistry.cs` is a
      small, deliberately non-pluggable name → `Func<float>` lookup (one
      entry today: `"clock"` → `DateTime.Now.TimeOfDay.TotalSeconds`).
      `PropertyStore` tracks a mutable enabled flag per feed-eligible
      property, **on by default** (a clock with its time feed off at
      startup would just show frozen hands, never what you want), and the
      control page renders a checkbox (`auto: clock`) next to the slider,
      disabling the slider while the feed is active. `AppWindow.Run` calls
      `PropertyStore.ApplyFeeds` once per frame, right after
      `DrainPending`, which samples every enabled feed and routes it
      through the exact same `ApplyPropertyChange` dispatch a manual
      slider edit uses — so from `ShaderModule`'s point of view a fed
      value is indistinguishable from one a slider set. Verified live:
      two samples of `seconds_since_midnight` two seconds apart advanced
      by ~2.0s; toggling the feed off and setting a manual value froze it
      there (confirmed unchanged two seconds later); this required zero
      host-side special-casing of "time" as a concept anywhere outside
      the one `FeedRegistry` entry.
- [x] **Live control channel.** `Control/ControlServer.cs` is a plain
      `System.Net.HttpListener` (deliberately not Kestrel/ASP.NET Core —
      HttpListener is already in the BCL, trims cleanly under `PublishAot`,
      and doesn't grow the single-file AppImage) serving one self-contained
      HTML/JS page (inline CSS/JS, no CDN, no build step) with a small hand-
      written-JSON API over `Control/PropertyStore.cs`. Every registered
      property — `fft.attack`/`fft.decay`/`fft.gain` (the same knobs
      `--fft-attack`/`-decay`/`-gain` set at startup) plus whatever the
      active module declared via `#request property` — shows up there as a
      slider automatically; no per-property UI code to write as new
      properties get added. `PropertyStore.TrySet` (called from the HTTP
      handler thread) only validates and queues a change;
      `PropertyStore.DrainPending` (called once per frame from
      `AppWindow.Run`, the only thread with the GL context current) is what
      actually applies it, via `IFft.SetAttack/SetDecay/SetGain` or
      `ShaderModule.SetProperty`. `System.Text.Json`'s reflection-based
      serializer would trip the csproj's `IL2026`/`IL3050`
      warnings-as-errors without a source-generated `JsonSerializerContext`
      — not worth the ceremony for a payload this small, so the JSON is
      hand-written instead.
    - Binds `127.0.0.1:8642` by default (`--control-port`); `--control-bind
      0.0.0.0` opts into LAN access (e.g. a phone/tablet on the same
      network) — there's no authentication, so only widen this on a network
      you trust. `--no-control` disables it entirely, `--no-hot-reload`
      disables the file watcher.
    - A bind failure (most commonly: another GlavaSharp instance already
      holds the port) is non-fatal — logged as a warning, the app keeps
      running without a control channel. Verified this path directly:
      starting a second instance on the same default port logs `Live control
      channel disabled: ... Address already in use` and renders normally.
    - Independent of `--desktop`/pinned-embedded mode by construction — the
      control server is a background thread that doesn't know or care which
      windowing mode is active. Verified live: `--desktop` with the control
      channel on a distinct `--control-port` serves properties and reflects
      changes exactly the same as windowed mode.

## Everything else

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
      [Shader Module Pipeline](architecture/shader-module-pipeline.md)).
      Not a regression, just a README claim ("single self-contained
      executable ... no sibling `.so` files to lose track of") that didn't
      match what `build/dist/` actually contained. Fixed by adding a
      packaging step rather than re-architecting the linking: `cmake
      --build build --target appimage` (`packaging/build-appimage.sh`)
      packs `build/dist/` into one real single-file AppImage via
      `appimagetool` — see [Packaging](getting-started/packaging.md) for
      the full writeup, including the `.desktop`-file validation gotcha
      (`Categories=` needs registered/`X-`-prefixed values) hit during
      setup. Verified live: `GlavaSharp-x86_64.AppImage --module aurora`,
      run from `/tmp` (nowhere near the repo), correctly resolved and
      compiled shaders from its own FUSE mount point.
