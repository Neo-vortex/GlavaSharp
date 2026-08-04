# Writing a Module

A GlavaSharp module is exactly what a GLava module is: a directory of
numbered fragment shader passes (`1.frag`, `2.frag`, ...), loaded and
compiled in order by [`ShaderModule`](../architecture/shader-module-pipeline.md).
Everything in this page works for real, unmodified GLava modules — this is
what `bars`, `radial`, `circle`, `graph`, and `wave` actually are. On top
of that, GlavaSharp adds three things GLava's own format has no concept
of at all: a **persistent buffer** that survives across frames, **live
properties** that show up as sliders in a web UI with zero extra code,
and **feeds** that let a built-in data source (like the system clock)
drive a property instead of a human. This page covers both: the GLava
basics, and what GlavaSharp specifically brings to the table.

## The minimum a module needs

A module is a directory containing at least a `1.frag`. Drop one under
`shaders/glavasharp/<your-module-name>/` (see
[Where your module lives](#where-your-module-lives) below) and run:

```bash
./GlavaSharp --module your-module-name
```

The simplest possible pass — solid color, ignoring audio entirely:

```glsl title="1.frag"
out vec4 fragment;

void main() {
    fragment = vec4(0.1, 0.1, 0.15, 1.0);
}
```

That's a complete, if boring, module. Everything below is about making it
actually react to audio and feel alive.

## Reading audio

Declare the uniforms you need and tell the host which GLSL identifier you
used for each semantic role via `#request uniform "<role>" <name>` — this
is a real GLava directive, not a GlavaSharp addition. The bundled tree's
convention (and the one every example on this page follows):

```glsl
#request uniform "screen" screen
uniform ivec2 screen;              // window size in pixels

#request uniform "audio_sz" audio_sz
uniform int audio_sz;              // number of bins in audio_l/audio_r

#request uniform "audio_l" audio_l
uniform sampler1D audio_l;         // left channel spectrum, 1D texture

#request uniform "audio_r" audio_r
uniform sampler1D audio_r;         // right channel spectrum, 1D texture
```

`audio_l`/`audio_r` are 1D textures of magnitudes already normalized to
`[0, 1]` (log-compressed, gravity-smoothed, and — unless you passed
`--freq-scale linear` — already perceptually bucketed; see
[FFT & Frequency Bucketing](../architecture/fft.md)). Sample them with
`util/smooth.glsl`'s `smooth_audio(tex, audio_sz, position)`, the same
helper every bundled module uses, where `position` is `0..1` across the
spectrum:

```glsl
#include ":util/smooth.glsl"

float level = smooth_audio(audio_l, audio_sz, 0.5); // spectrum midpoint, left channel
```

## Multiple passes

If your module has a `2.frag`, it receives `1.frag`'s output as a
`sampler2D` — declare the uniform name you want via
`#request uniform "prev" <name>` (GlavaSharp binds by whatever name you
declared here; it doesn't guess):

```glsl title="2.frag"
#request uniform "prev" tex
uniform sampler2D tex;

out vec4 fragment;

void main() {
    fragment = texture(tex, gl_FragCoord.xy / vec2(textureSize(tex, 0)));
}
```

The last *enabled* pass renders to the screen; every earlier pass renders
to an offscreen ping-pong buffer.

### Conditionally disabling a pass

GLava's `#error __disablestage` sentinel skips a pass entirely (its
predecessor's output passes straight through) — useful for an optional
second pass gated behind a `#define`, e.g. `bars/2.frag`:

```glsl title="2.frag"
#if USE_ALPHA == 0
#error __disablestage
#endif

#include ":util/premultiply.frag"
```

## GlavaSharp extensions

Everything above is pure GLava. These three are GlavaSharp-original —
none of it exists upstream, and each one exists because a real module
(`waterfall`, `aurora`, `clock`) needed it.

### 1. Persistent state: the `history` buffer

GLava's module format has no idea of state that survives between frames
— every module redraws from scratch every frame. If your effect needs to
remember what it drew last frame (a scrolling spectrogram, a feedback-driven
fluid sim, anything with trails or decay), declare `#request uniform
"history" <name>` on a pass:

```glsl title="1.frag — waterfall's accumulate pass"
#request uniform "history" hist
uniform sampler2D hist;
```

That pass gets a **persistent** 1024×512 ping-pong texture pair
(fixed resolution, independent of window size) that is *not* cleared
every frame. Read `hist` for last frame's content, write your new frame's
content as this pass's output — `ShaderModule` swaps the pair's roles
every frame automatically. A later pass in the same module reads the
just-written buffer as an ordinary `#request uniform "prev"` texture.

```glsl title="waterfall/1.frag, abbreviated — shift down a row, stamp a new one on top"
void main() {
    vec2 uv = gl_FragCoord.xy / vec2(screen);
    float dy = 1.0 / float(screen.y);

    if (uv.y > 1.0 - dy) {
        float mag = max(smooth_audio(audio_l, audio_sz, uv.x),
                         smooth_audio(audio_r, audio_sz, uv.x));
        fragment = vec4(heatmap(mag), 1.0);       // newest row: today's spectrum
    } else {
        fragment = texture(hist, uv + vec2(0.0, dy)); // every other row: copy from above, shifted down
    }
}
```

A module gets exactly **one** history buffer. `aurora` works around that
limit by blending several *virtual* layers inside its own math rather than
requesting more buffers — see
[GlavaSharp-Original Modules](../architecture/original-modules.md) for the
full technique breakdown if you want to push this further.

### 2. Live properties: sliders for free

Declare `#request property "name" float default min max` next to a
`uniform float name;` you already need, and it becomes tunable from the
**live control channel** (`http://127.0.0.1:8642/` by default) with zero
UI code — the control page introspects every loaded module's properties
and builds a slider for each one automatically.

```glsl title="aurora/1.frag"
#request property "amplify" float 2.6 0.5 6.0
uniform float amplify;
```

Open the control page while `aurora` is running and an `amplify` slider
(range 0.5–6.0, starting at 2.6) just appears, alongside the global
`fft.attack`/`fft.decay`/`fft.gain` knobs every module gets automatically.
Drag it — the running shader updates on the very next frame, no recompile,
no restart. This is what makes it practical to tune a module against real,
live music instead of guessing constants and restarting repeatedly.

Only `float` is implemented today — that covers every tunable a shader
constant would otherwise hardcode.

### 3. Feeds: let a data source drive a property instead of a human

A property can *also* opt into being driven by a named built-in data
source instead of manual slider input, via a second, separate line:
`#request feed "name" source`.

```glsl title="clock/2.frag"
#request property "seconds_since_midnight" float 0 0 86400
#request feed "seconds_since_midnight" clock
uniform float seconds_since_midnight;
```

`clock` is the one built-in feed source today (`Control/FeedRegistry.cs`
— wall-clock time of day, in seconds since local midnight). With the feed
bound, the control page shows a checkbox (`auto: clock`) next to the
slider, **on by default**, that samples the feed once per frame instead of
reading the slider. Flip it off to freeze the value at whatever the
slider says — handy for lining up a specific moment for a screenshot (see
[Common Scenarios](scenarios.md)).

Nothing about "time" is special-cased anywhere in the host beyond that one
`FeedRegistry` entry — from `ShaderModule`'s point of view, a fed value and
a slider-set value are indistinguishable. Adding a second feed source
(say, CPU load, or a currently-playing track's elapsed time) means adding
one more `Dictionary` entry to `FeedRegistry`, not a new subsystem.

## Where your module lives

`ShaderModule` resolves `--module <name>` two ways, in order:

1. `<shaders-root>/<name>` — the primary tree (`shaders/glava/` by
   default, or wherever `--shaders` points).
2. `<shaders-root>/../glavasharp/<name>` — a sibling `glavasharp/`
   directory next to the primary tree.

For your own modules, drop them under `shaders/glavasharp/<name>/` right
next to `waterfall`/`aurora`/`clock` — `--module <name>` then works
exactly like `--module bars` without you needing to know or care which
tree it actually loaded from. If you'd rather keep modules somewhere else
entirely, point `--shaders` at your own directory (it needs its own
`rc.glsl` — see [Configuration](../architecture/configuration.md)) and put
your module directly under that instead.

## Iterating: hot-reload

Shader hot-reload is on by default (`--no-hot-reload` to turn it off).
Save any `.frag`/`.glsl` file the running module actually pulled in — via
`#include`, transitively, including shared files like `util/smooth.glsl`
— and the affected pass(es) recompile in place on the next frame. A
compile error is logged and the previous, still-working program keeps
running rather than crashing, so you can iterate against live audio
without ever losing the picture. `--log-level debug` also turns on
per-pass compile chatter if you want to confirm a save actually triggered
a reload.

## `#request` directive reference

| Directive | Origin | What it does |
|---|---|---|
| `#request uniform "<role>" <name>` | GLava | Declares which GLSL identifier this pass uses for a semantic role (`screen`, `audio_sz`, `audio_l`, `audio_r`, `prev`, `history`). |
| `#request setsmoothfactor <n>` | GLava | Becomes `#define _SMOOTH_FACTOR <n>`, read by `util/smooth.glsl`. |
| `#request setsmoothpass <bool>` | GLava | Becomes `#define _PRE_SMOOTHED_AUDIO 0\|1`. |
| `#include "@x"` / `#include ":x"` | GLava | `@` = module-relative, `:` = shader-root-relative. Recursive, deduplicated, depth-capped at 32. |
| `#expand NAME COUNT` | GLava | Expands to `NAME(0) NAME(1) ... NAME(COUNT-1)`, one per line (`COUNT` must already be a literal integer). |
| `#RRGGBB[AA]` | GLava | Hex color literal → `vec4(...)`. |
| `@fg:` / `@bg:` | GLava | Stripped — GlavaSharp draws the result with normal alpha blending instead of implementing GLava's separate compositing pass. |
| `#error __disablestage` | GLava | Skips this pass entirely; the previous pass's output passes straight through. |
| `#request property "name" float default min max` | **GlavaSharp** | Declares a live-tweakable property — shows up as a slider on the control page automatically. |
| `#request feed "name" source` | **GlavaSharp** | Marks an already-declared property as drivable by a named built-in data source (see `Control/FeedRegistry.cs`) instead of manual slider input. |

Not implemented: GLava's `#request transform ...` pipeline (windowing/FFT/
gravity/avg are done natively in `CpuFft`/`GpuFft` instead — see
[FFT & Frequency Bucketing](../architecture/fft.md)) and the full `@fg:`/
`@bg:` compositing model. See
[Shader Preprocessing](../architecture/shader-preprocessing.md) for why.
