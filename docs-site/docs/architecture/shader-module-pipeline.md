# Shader Module Pipeline

`Shaders/ShaderModule.cs`

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
<name>`, see [Shader Preprocessing](shader-preprocessing.md)),
plus the two audio spectrum textures as `audio_l`/`audio_r`. The last
*enabled* pass (not necessarily the last file — a trailing disabled pass
shouldn't swallow the real output) renders directly to the default
framebuffer.

`ShaderModule` also resolves module directories from a second, sibling
location — see [GlavaSharp-Original Modules](original-modules.md) — so
`--module waterfall` works exactly like `--module bars` without callers
needing to know which shader tree it actually lives under.
