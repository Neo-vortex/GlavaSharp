# GlavaSharp-Original Modules

`shaders/glavasharp/`

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

## The `history` buffer mechanism

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
real audio — see [Status & Roadmap](../status-roadmap.md).

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

## Why `aurora` is a different category of effect than anything in GLava

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
