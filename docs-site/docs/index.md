# GlavaSharp

GlavaSharp listens to your system audio and turns it into a live OpenGL
visualizer — spectrum bars, a radial burst, a scrolling heat-mapped
spectrogram, or a slow-drifting aurora that can sit pinned behind your
desktop icons like ambient wallpaper. It's a from-scratch C#/.NET rebuild
of [GLava](https://github.com/jarcode-foss/glava)'s rendering model.

This site is the in-depth companion to the
[project README](https://github.com/Neo-vortex/GlavaSharp): architecture,
design trade-offs, the full status/roadmap checklist, and detailed build
instructions. If you just want to build and run GlavaSharp, start with
[Building](getting-started/building.md) and [Running](getting-started/running.md).

!!! info "Status: early alpha"
    It runs, it renders, and the core pipeline is solid — but this is a
    project still finding its edges, not a polished GLava replacement yet.
    See [Status & Roadmap](status-roadmap.md) for the honest,
    warts-and-all breakdown of what's done, what's shaky, and exactly how
    every known quirk was (or wasn't yet) fixed.

> GlavaSharp is an independent reimplementation and is not affiliated
> with or endorsed by the GLava project.

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

See [How it compares to GLava](comparison.md) for the point-by-point
breakdown.

## Where to go next

<div class="grid cards" markdown>

- **New to GlavaSharp?** Start with [Building](getting-started/building.md)
  and [Running](getting-started/running.md).
- **Want to write your own module?** See
  [Writing a Module](guides/writing-a-module.md).
- **Looking for a specific flag?** See the
  [CLI Reference](guides/cli-reference.md).
- **Trying to do something specific?** See
  [Common Scenarios](guides/scenarios.md) for task-oriented recipes.
- **Curious how it works?** Start with the
  [Architecture overview](architecture/overview.md).
- **Wondering what's solid vs. shaky?** See
  [Status & Roadmap](status-roadmap.md).
- **Why a decision was made a certain way?** See
  [Design Trade-offs](design-tradeoffs.md).

</div>
