# Desktop-Embedded Mode

`Windowing/X11Native.cs`, `native/x11shim/`

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
  name suggests; see [Status & Roadmap](../status-roadmap.md) for why
  claiming the `DESKTOP` type actively works against this on xfwm4
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
  [Status & Roadmap](../status-roadmap.md) for why rc.glsl's own
  `setgeometry` is deliberately *not* used as an implicit fallback here)
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
[Design Trade-offs](../design-tradeoffs.md).

`--desktop` forces `WindowOptions.Platform` to X11 (rather than
`PlatformPreference.Any`) so a Wayland session doesn't silently swallow the
flag; `AppWindow` fails loudly (not silently) if GLFW doesn't actually end
up on X11. It's also settable from `rc.glsl` via GLava's own
`#request setxwintype "desktop"` directive — `--desktop` on the CLI and
`setxwintype "desktop"` in `rc.glsl` both work, CLI wins if both differ.
Geometry works differently: only `--desktop-geometry`/`--desktop-monitor`
constrain desktop mode's rect — rc.glsl's `setgeometry` is deliberately
*not* consulted for this (see [Status & Roadmap](../status-roadmap.md)
for the bug that caused, when it was).

Currently targets xfwm4 (XFCE) first, since it's one of the more
EWMH-compliant window managers for this particular behavior — hence why
GLava's own `env_Xfwm4.glsl` is only three lines. GNOME and native Wayland
are unimplemented follow-ups — see [Status & Roadmap](../status-roadmap.md).
