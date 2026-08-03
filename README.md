# GlavaSharp

GlavaSharp is a from-scratch, C#/.NET reimplementation of
[GLava](https://github.com/jarcode-foss/glava)'s rendering model: an
audio-reactive OpenGL visualizer driven by chains of numbered GLSL
fragment shaders, configured through GLava's own `rc.glsl` / module
directory convention. It captures system audio via PipeWire, runs an FFT,
and feeds the resulting spectrum into your choice of shader module (bars,
radial, circle, graph, wave, or GlavaSharp's own waterfall spectrogram) as
a pair of 1D textures.

**Status: early alpha.** It runs, it renders, and the core pipeline works,
but this is not yet a polished GLava replacement.

> GlavaSharp is an independent reimplementation and is not affiliated with
> or endorsed by the GLava project. It exists because GLava's shader
> ecosystem (the module/`rc.glsl`/`#request` convention) is genuinely well
> designed, and it seemed worth having that ecosystem sitting on top of a
> different, more portable host.

<img width="511" height="427" alt="image" src="https://github.com/user-attachments/assets/86837b73-992e-4e8f-8c0b-10df5b3c215e" />

For architecture, design rationale, the full status/roadmap checklist, and
detailed build notes, see **[TECHNICAL.md](TECHNICAL.md)**.

---

## What it does

- Audio-reactive OpenGL visualizer — captures whatever's playing via
  PipeWire and feeds a live FFT spectrum into a GLSL shader chain, every
  frame.
- Runs GLava's own shader modules essentially unmodified: `bars`,
  `radial`, `circle`, `graph`, `wave` — same `rc.glsl`/module-directory
  convention GLava uses, same shader files.
- Plus a module GLava doesn't have: **`waterfall`**, a scrolling,
  color-mapped spectrogram (frequency history over time).
- Two interchangeable FFT backends: a CPU FFT (default, works everywhere)
  and a GPU compute-shader FFT (`--fft-device gpu`).
- Runs on both X11 and Wayland sessions (GLFW picks whichever the session
  is actually running).
- **Desktop-embedded mode** (`--desktop`, GLava's `-d` equivalent) —
  renders pinned behind desktop icons via X11 EWMH hints, transparent and
  click-through, with adjustable position/size (`--desktop-geometry`).
  Verified working on XFCE/xfwm4; GNOME and native Wayland aren't
  implemented yet.
- GPU picker for hybrid-graphics laptops (`--list-gpus` / `--gpu <n>`).
- Ships as a single self-contained Native AOT executable — no installed
  runtime, no sibling `.so` files to lose track of.

See **[TECHNICAL.md's status checklist](TECHNICAL.md#status--roadmap)**
for the complete, honest breakdown of what's done vs. still open —
including known quirks found during testing and exactly how they were (or
weren't yet) fixed.

## Building

Requires: .NET 10 SDK, a Rust toolchain (via [rustup](https://rustup.rs)),
`clang`/`libclang-dev`, and `libpipewire-0.3-dev`. On Ubuntu/Debian:

```bash
sudo apt install libpipewire-0.3-dev pkg-config clang libclang-dev cmake
```

Then:

```bash
cmake -S . -B build
cmake --build build
# -> build/dist/GlavaSharp
```

See [TECHNICAL.md](TECHNICAL.md#building-detailed) for building each
piece independently, cleaning, and why a plain `dotnet build` alone can't
run the app end-to-end.

## Running

```bash
./build/dist/GlavaSharp                    # default sink monitor, bars module from rc.glsl
./build/dist/GlavaSharp --module waterfall # scrolling spectrogram
./build/dist/GlavaSharp --list-sinks       # see capture targets
./build/dist/GlavaSharp --list-gpus        # see DRM render nodes (for --gpu)
./build/dist/GlavaSharp --fft-device gpu   # run the FFT on the GPU instead of the CPU
./build/dist/GlavaSharp --desktop          # desktop-embedded mode (X11/xfwm4)
./build/dist/GlavaSharp --desktop --desktop-geometry 100,100,800,600   # ...at a specific rect
```

See the top of `src/GlavaSharp/Program.cs` for the full CLI flag
reference, or [TECHNICAL.md](TECHNICAL.md) for how each piece actually
works under the hood.

## License

This project is licensed under the MIT License. See the LICENSE file for the full license text.

The bundled shader tree under `src/GlavaSharp/shaders/glava/` originates
from GLava and remains subject to its own license. See the original GLava
project for the licensing terms that apply to those files.
