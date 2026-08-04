# Live Control Channel & Shader Hot-Reload

`Control/`, `Shaders/ShaderModule.cs`

## Shader hot-reload

`ShaderModule` tracks, per compiled pass, the full set of files it pulled
in via `#include` (transitively — `GlavaPreprocessor.Process` returns
that set alongside the preprocessed source). A `FileSystemWatcher` over
the shader tree (`RootDir`, plus a second watcher over the sibling
`glavasharp/` directory for modules resolved via that fallback — see
[GlavaSharp-Original Modules](original-modules.md)) marks a file dirty on
save; `ShaderModule.ReloadIfDirty()`, called once per frame from the
render thread (never from the watcher's own callback thread — no GL
context there), recompiles every pass whose dependency set contains a
dirty file. Editing a module's own `.frag` recompiles just that pass;
editing a shared file like `util/smooth.glsl` or `aurora.glsl` recompiles
every pass across the module that included it. A failed recompile logs an
error and leaves the previous, still-working GL program running rather
than tearing anything down.

## Live-tweakable per-module properties

A pass can declare `#request property "name" float default min max` (a
GlavaSharp extension to GLava's `#request` convention, parsed the same
way `#request uniform` already was) right next to the `uniform float
name;` it already needs — see `shaders/glavasharp/aurora/1.frag`'s
`amplify` for the worked example (replaced what used to be a
`#define AMPLIFY 2.6` in `aurora.glsl`). `ShaderModule` re-applies the
current value to every pass that declared it on each `Render()` call, so
a change takes effect on the very next frame with no recompile involved.

## Feed-driven properties

A property can also declare `#request feed "name" source` (a second,
separate line from `#request property` — feed-eligibility is an
orthogonal annotation on an already-complete property declaration, not a
different kind of property) to opt into being driven by a named built-in
data source instead of manual slider input — e.g.
`shaders/glavasharp/clock/2.frag`'s
`#request feed "seconds_since_midnight" clock`.
`Control/FeedRegistry.cs` is a small, deliberately non-pluggable
name → `Func<float>` lookup (one entry today: `"clock"` →
`DateTime.Now.TimeOfDay.TotalSeconds`). `PropertyStore` tracks a mutable
enabled flag per feed-eligible property, **on by default** (a clock with
its time feed off at startup would just show frozen hands, never what you
want), and the control page renders a checkbox (`auto: clock`) next to
the slider, disabling the slider while the feed is active. `AppWindow.Run`
calls `PropertyStore.ApplyFeeds` once per frame, right after
`DrainPending`, which samples every enabled feed and routes it through the
exact same `ApplyPropertyChange` dispatch a manual slider edit uses — so
from `ShaderModule`'s point of view a fed value is indistinguishable from
one a slider set.

## The control server

`Control/ControlServer.cs` is a plain `System.Net.HttpListener`
(deliberately not Kestrel/ASP.NET Core — `HttpListener` is already in the
BCL, trims cleanly under `PublishAot`, and doesn't grow the single-file
AppImage) serving one self-contained HTML/JS page (inline CSS/JS, no CDN,
no build step) with a small hand-written-JSON API over
`Control/PropertyStore.cs`. Every registered property —
`fft.attack`/`fft.decay`/`fft.gain` (the same knobs
`--fft-attack`/`-decay`/`-gain` set at startup) plus whatever the active
module declared via `#request property` — shows up there as a slider
automatically; no per-property UI code to write as new properties get
added. `PropertyStore.TrySet` (called from the HTTP handler thread) only
validates and queues a change; `PropertyStore.DrainPending` (called once
per frame from `AppWindow.Run`, the only thread with the GL context
current) is what actually applies it, via
`IFft.SetAttack/SetDecay/SetGain` or `ShaderModule.SetProperty`.
`System.Text.Json`'s reflection-based serializer would trip the csproj's
`IL2026`/`IL3050` warnings-as-errors without a source-generated
`JsonSerializerContext` — not worth the ceremony for a payload this
small, so the JSON is hand-written instead.

- Binds `127.0.0.1:8642` by default (`--control-port`); `--control-bind
  0.0.0.0` opts into LAN access (e.g. a phone/tablet on the same network)
  — there's no authentication, so only widen this on a network you trust.
  `--no-control` disables it entirely, `--no-hot-reload` disables the
  file watcher.
- A bind failure (most commonly: another GlavaSharp instance already
  holds the port) is non-fatal — logged as a warning, the app keeps
  running without a control channel.
- Independent of `--desktop`/pinned-embedded mode by construction — the
  control server is a background thread that doesn't know or care which
  windowing mode is active.

See [Status & Roadmap](../status-roadmap.md) for the live verification of
each of these pieces.
