# Shader Preprocessing

`Shaders/GlavaPreprocessor.cs`

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
  `audio_r`, `prev`, `history` [GlavaSharp-original, see
  [GlavaSharp-Original Modules](original-modules.md)], ...)
  instead of GlavaSharp assuming a fixed name. `Process()` returns these
  role → name bindings alongside the source (not just stripping the line
  like everything else here) so `ShaderModule` can bind each pass's
  previous-output sampler by whatever name the shader actually used,
  instead of guessing. This matters in practice: the bundled tree always
  names it `tex`, and `ShaderModule` used to hardcode `"tex0"` — see
  [Status & Roadmap](../status-roadmap.md) for the bug that caused. Every
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
pipeline and the full compositing model behind `@fg:`/`@bg:` — see
[Status & Roadmap](../status-roadmap.md).
