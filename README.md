<p align="center">
  <img src="docs/glavasharp-icon.png" width="120" height="120" alt="GlavaSharp icon — an aurora curtain over a starfield" />
</p>

<h1 align="center">GlavaSharp</h1>
<p align="center"><i>Turns whatever's playing on your speakers into light.</i></p>

GlavaSharp listens to your system audio and turns it into a live OpenGL
visualizer — spectrum bars, a radial burst, a scrolling heat-mapped
spectrogram, or a slow-drifting aurora that can sit pinned behind your
desktop icons like ambient wallpaper. It's a from-scratch C#/.NET rebuild
of [GLava](https://github.com/jarcode-foss/glava)'s rendering model: same
well-designed module/`rc.glsl`/`#request` shader convention, on a more
portable, memory-safe host.

**Status: early alpha.** It runs and renders, and the core pipeline is
solid, but this isn't a polished GLava replacement yet. See the
**[Status & Roadmap](https://neo-vortex.github.io/GlavaSharp/status-roadmap/)**
page for the full, warts-and-all breakdown of what's done, what's shaky,
and how every known quirk was (or wasn't yet) fixed.

> GlavaSharp is an independent reimplementation and is not affiliated
> with or endorsed by the GLava project.

---

## See it in action

### `aurora` — the one we're proudest of

Not part of GLava at all — a GlavaSharp original, built for people who
just want something calm and pretty breathing on their desktop. Soft
curtains of color rise and fold like the real northern lights, driven by
a curl-noise flow field with no clock or timer anywhere in the pipeline —
every bit of motion is the audio, re-sampling its own history, forever.

<p align="center">
  <img src="docs/screenshots/aurora.gif" width="640" alt="aurora module: teal-to-violet curtains rising and folding over a starfield, driven live by music" />
</p>

<sub>(driven by real music, not synthetic noise — this is what it
actually looks like breathing)</sub>

### ...and the rest of the family

<table>
<tr>
<td align="center" width="25%">
  <img src="docs/screenshots/radial.png" width="220" alt="radial module: a circular spectrum burst" /><br/>
  <sub><b>radial</b> — a circular burst, GLava's own</sub>
</td>
<td align="center" width="25%">
  <img src="docs/screenshots/bars.png" width="220" alt="bars module: a classic vertical spectrum" /><br/>
  <sub><b>bars</b> — the classic, GLava's own</sub>
</td>
<td align="center" width="25%">
  <img src="docs/screenshots/waterfall.jpg" width="220" alt="waterfall module: a scrolling heat-mapped spectrogram" /><br/>
  <sub><b>waterfall</b> — a scrolling spectrogram, GlavaSharp original</sub>
</td>
<td align="center" width="25%">
  <img src="docs/screenshots/clock.gif" width="220" alt="clock module: an analog clock face with hour, minute, and second hands over a radial spectrum, hands thickening and glowing with bass/mid/treble" /><br/>
  <sub><b>clock</b> — hands driven by the system clock, GlavaSharp original</sub>
</td>
</tr>
</table>

Plus `circle`, `graph`, and `wave` straight from GLava's own tree — see
[What it does](#what-it-does) below for the full lineup.

---

## What it does

- Audio-reactive OpenGL visualizer — captures whatever's playing via
  PipeWire and feeds a live FFT spectrum into a GLSL shader chain, every
  frame.
- Runs GLava's own shader modules essentially unmodified: `bars`,
  `radial`, `circle`, `graph`, `wave` — same `rc.glsl`/module-directory
  convention, same shader files.
- Plus three GlavaSharp-original modules: **`waterfall`**, a scrolling
  color-mapped spectrogram; **`aurora`**, the calming ambient visualizer
  above; and **`clock`**, an analog clock face over an ordinary radial
  spectrum, hands driven live by the system clock.
- Two interchangeable FFT backends: CPU (default, works everywhere) and
  a GPU compute-shader FFT (`--fft-device gpu`).
- Perceptual frequency bucketing (`--freq-scale log2|mel|bark|erb|linear`,
  default `log2`) — redistributes the FFT spectrum onto a scale that
  matches how humans actually resolve pitch, so bass doesn't read as
  static and treble as disproportionately "active." See the
  [docs site](https://neo-vortex.github.io/GlavaSharp/architecture/fft/)
  for the full writeup.
- Runs on both X11 and Wayland (GLFW picks whichever the session is
  actually running).
- **Desktop-embedded mode** (`--desktop`, GLava's `-d` equivalent) —
  renders pinned behind desktop icons via X11 EWMH hints, transparent and
  click-through. `--desktop-geometry X,Y,W,H` or `--desktop-monitor
  <index>` constrain it to a rect or a single monitor. Verified on
  XFCE/xfwm4; GNOME and native Wayland aren't implemented yet.
- GPU picker for hybrid-graphics laptops (`--list-gpus` / `--gpu <n>`).
- **Live control channel** — a local web page
  (`http://127.0.0.1:8642/` by default) with an auto-generated slider for
  every tweakable property, updating instantly with no restart. Properties
  can also be fed from a live data source instead of a slider (e.g.
  `clock`'s hands). `--no-control` disables it; `--control-bind 0.0.0.0`
  opts into LAN access at your own risk (no authentication).
- **Shader hot-reload** — edit any `.frag`/`.glsl` file the running module
  uses and it recompiles in place on save, on by default
  (`--no-hot-reload` to turn it off).
- Builds as a self-contained Native AOT executable — no installed .NET
  runtime needed. `cmake --build build --target appimage` packs the whole
  build output into one single-file `.AppImage` — see
  [Building](#building) below.

## Building

Requires: .NET 10 SDK, a Rust toolchain (via [rustup](https://rustup.rs)),
`clang`/`libclang-dev`, and `libpipewire-0.3-dev`. On Ubuntu:

```bash
sudo apt install dotnet-sdk-10.0 libpipewire-0.3-dev pkg-config clang libclang-dev cmake
```

Then:

```bash
cmake -S . -B build
cmake --build build
# -> build/dist/GlavaSharp (+ libglfw*.so + shaders/ alongside it)
```

For an actual single file to hand someone else, pack that into an AppImage
(needs `squashfs-tools`; fetches `appimagetool` from GitHub on first run,
cached under `build/tools/` afterwards):

```bash
cmake --build build --target appimage
# -> build/GlavaSharp-x86_64.AppImage
```

For a faster CPU FFT on machines you know have AVX2+FMA (most x86_64 CPUs
since ~2013), reconfigure with `-DGLAVASHARP_AVX2_CPU_FFT=ON` before
building — 1.2x-1.6x faster (see the
[Benchmarks page](https://neo-vortex.github.io/GlavaSharp/benchmarks/)),
at the cost of the resulting binary refusing to run at all on CPUs
without AVX2+FMA. The
`appimage` target names its output `GlavaSharp-x86_64-avx2.AppImage`
instead of the plain name when this is on, so the two can't be mixed up:

```bash
cmake -S . -B build -DGLAVASHARP_AVX2_CPU_FFT=ON
cmake --build build && cmake --build build --target appimage
```

See the [docs site](https://neo-vortex.github.io/GlavaSharp/getting-started/building/)
for building each piece independently, cleaning, and why a plain
`dotnet build` alone can't run the app end-to-end.

## Running

```bash
./build/dist/GlavaSharp                    # default sink monitor, bars module from rc.glsl
./build/dist/GlavaSharp --module waterfall # scrolling spectrogram
./build/dist/GlavaSharp --list-sinks       # see capture targets
./build/dist/GlavaSharp --list-gpus        # see DRM render nodes (for --gpu)
./build/dist/GlavaSharp --fft-device gpu   # run the FFT on the GPU instead of the CPU
./build/dist/GlavaSharp --freq-scale erb   # try a different perceptual scale (default: log2)
./build/dist/GlavaSharp --desktop          # desktop-embedded mode (X11/xfwm4), whole screen
./build/dist/GlavaSharp --list-monitors    # see connected monitors (for --desktop-monitor)
./build/dist/GlavaSharp --desktop --desktop-monitor 1                  # ...on just monitor 1
./build/dist/GlavaSharp --desktop --desktop-geometry 100,100,800,600   # ...at a specific rect
./build/dist/GlavaSharp --desktop --module aurora                      # calming ambient desktop visualizer
./build/dist/GlavaSharp --module clock                                 # analog clock + radial spectrum
```

Then open `http://127.0.0.1:8642/` in a browser for live sliders (FFT
attack/decay/gain, plus module properties like `aurora`'s `amplify`) --
edit a `.frag`/`.glsl` file and it hot-reloads on save, no restart needed
for either.

See the top of `src/GlavaSharp/Program.cs` for the full CLI flag
reference, or the **[docs site](https://neo-vortex.github.io/GlavaSharp/)**
for how each piece actually works under the hood.

## License

This project is licensed under the MIT License. See the LICENSE file for the full license text.

The bundled shader tree under `src/GlavaSharp/shaders/glava/` originates
from GLava and remains subject to its own license. See the original GLava
project for the licensing terms that apply to those files.
