# shaders/glavasharp/

GlavaSharp-original visualizer modules — **not** part of GLava's own shader
tree. `shaders/glava/` is GLava's actual bundled shaders, reproduced
unmodified; this directory is the opposite: modules that don't exist
upstream at all, written for GlavaSharp specifically.

They still follow GLava's own module convention (a directory of numbered
`N.frag` passes, loaded by `Shaders/ShaderModule.cs` exactly like a
`shaders/glava/` module) and get resolved the same way — `--module
waterfall` works without needing to know which of the two trees it
actually lives under, since `ShaderModule` falls back to this sibling
directory when a module isn't found under the primary `--shaders` root.

## Modules

- **`waterfall/`** — a scrolling spectrogram: the audio spectrum's history
  over time, color-mapped (blue → cyan → green → yellow → red → white) and
  scrolling downward as new data arrives. Unlike every GLava module (which
  redraws from scratch each frame), this one needs state that survives
  *across* frames — an accumulation buffer it shifts by one row and
  appends a new row to, every frame. GLava's module format has no
  mechanism for that, so `ShaderModule` gained one: a pass can declare
  `#request uniform "history" <name>` to get a persistent ping-pong
  texture pair that isn't cleared every frame like the normal one is —
  see the class doc comment on `Shaders/ShaderModule.cs` and
  `waterfall/1.frag`'s comments for the details.
- **`aurora/`** — a calming, ambient desktop visualizer: soft curtains of
  color drift upward and sway gently like the northern lights, driven by
  the audio spectrum, fading into a fully transparent background so it
  reads well as a desktop backdrop (`--desktop`) rather than a foreground
  visualizer. Also uses the persistent "history" buffer, but as a
  decay+drift feedback loop instead of waterfall's hard scroll — no clock
  or time uniform involved, the motion comes entirely from re-sampling the
  buffer's own previous frame through a fixed sideways sway each frame; see
  `aurora/1.frag`'s comments for how that produces organic-looking drift
  from pure feedback.
