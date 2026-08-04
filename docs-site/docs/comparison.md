# How it compares to GLava

| | GLava | GlavaSharp |
|---|---|---|
| Host language | C | C# (.NET, Native AOT) |
| Windowing/GL | Xlib directly (GLFW on the `unstable` branch) | GLFW via OpenTK, always |
| Display server support | **X11 only** | **X11 and Wayland** (GLFW's `PlatformPreference.Any` picks whichever the session is running) |
| Audio backend | PulseAudio (libpulse), linked into the main process | PipeWire, isolated in a separate Rust static library behind a tiny FFI shim |
| Shader preprocessor | Full custom C preprocessor (`#request`, `#include`, `#expand`, `@fg:`/`@bg:` compositing, GLava's transform pipeline for FFT/window/gravity/avg as *chained shaders*) | A deliberately small subset (see [Shader Preprocessing](architecture/shader-preprocessing.md)) — enough to load real GLava module files, not a full reimplementation of every directive |
| FFT | Runs as GLava's own chained compute-shader "transform" passes (`window` → `fft` → `gravity` → `avg`) on the GPU | Two interchangeable backends, selected with `--fft-device`: a CPU FFT (`CpuFft`, the default) and a single-workgroup GLSL compute-shader FFT (`GpuFft`) that's bit-for-bit equivalent to it — see [FFT & Frequency Bucketing](architecture/fft.md) |
| GPU selection | N/A | `--list-gpus` enumerates DRM render nodes; `--gpu <index>` pins rendering to one — see [GPU Selection](architecture/gpu-selection.md) |
| Distributable artifact | Dynamically linked binary + installed shader/config tree under `/etc/xdg` or `~/.config/glava` | Self-contained Native AOT executable with the Rust audio/X11 shims statically linked in, but `build/dist/` itself is still multiple files (GLFW's `libglfw*.so`, dynamically linked, + `shaders/` alongside it, not installed system-wide); `cmake --build build --target appimage` packs that into one real single-file `.AppImage` — see [Packaging](getting-started/packaging.md) |
| Desktop-embedded mode (`glava -d` / `setxwintype "desktop"`) | Supported, X11 EWMH-based | **X11/xfwm4 implemented and verified** (`--desktop`, `--desktop-geometry`) — see [Desktop-Embedded Mode](architecture/desktop-embedded-mode.md); GNOME/Wayland not yet |
| Module maturity | All bundled modules (bars, radial, circle, graph, wave, ...) are production-quality | `bars`, `radial`, `circle`, `graph`, `wave` all verified working, plus two GlavaSharp-original modules GLava doesn't have: `waterfall` (a scrolling spectrogram) and `aurora` (a calming ambient desktop visualizer) — see [Status & Roadmap](status-roadmap.md) |
| Build system | Meson (2.x) / legacy Makefile (1.x) | CMake orchestrating `cargo` + `dotnet publish` |

The short version: GlavaSharp is GLava's *shader-facing* design ported onto
a different, more memory-safe, cross-compositor host stack. It is not a
drop-in replacement, doesn't read GLava's installed config paths, and is
missing some GLava features (most of the `#request` surface, IPC/pipe
control) that GLava has had for years — though desktop-embedded mode, one
of the bigger gaps, now has a working X11 implementation.
