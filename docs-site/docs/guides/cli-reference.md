# CLI Reference

Every flag GlavaSharp accepts, grouped by what it affects. This mirrors
the reference comment block at the top of `src/GlavaSharp/Program.cs` —
that file is the source of truth if this ever drifts.

## Info / one-shot flags

These enumerate something and exit immediately — no window, no audio
capture.

| Flag | What it does |
|---|---|
| `--list-sinks` | List PipeWire capture targets (sinks and sources) with their IDs, for `--sink`. |
| `--list-gpus` | List GPUs (DRM render nodes) with their index, for `--gpu`. |
| `--list-monitors` | List connected monitors (index, name, position, resolution), for `--desktop-monitor`. |
| `--benchmark-fft` | Time `IFft.Process()` across a spread of window sizes and print a results table — see [Benchmarks](../benchmarks.md). |

## Audio capture

| Flag | Default | What it does |
|---|---|---|
| `--sink <id\|name>` | default sink's monitor | Capture a specific target instead of "what you hear." Accepts a numeric ID from `--list-sinks` or a name/description match. |
| `--sample-rate <hz>` | rc.glsl's `setsamplerate`, else 48000 | PipeWire capture sample rate. |

## Shaders & modules

| Flag | Default | What it does |
|---|---|---|
| `--shaders <dir>` | `./shaders/glava` next to the executable | Path to a GLava shader tree (a directory containing `rc.glsl` + module subdirectories). |
| `--module <name>` | whatever rc.glsl's `#request mod` says | Override the active module, e.g. `bars`, `waterfall`, `aurora`, `clock`. See [Writing a Module](writing-a-module.md) for the full module format. |

## GPU selection

| Flag | Default | What it does |
|---|---|---|
| `--gpu <index>` | driver default | Force rendering onto a specific GPU (index from `--list-gpus`). Sets `DRI_PRIME`/NVIDIA prime-offload env vars before the GL context is created. Also picks the GPU `--fft-device gpu` runs on, since it shares the same context. |

See [GPU Selection](../architecture/gpu-selection.md) for how the index
maps to a real device.

## FFT

| Flag | Default | What it does |
|---|---|---|
| `--fft-size <n>` | rc.glsl's `setbufsize` (rounded up to a power of two), else 2048 | FFT/audio window size in samples, must be a power of two. Bigger = more bins (`bins = n/2`) and more smoothing lag; smaller = fewer bins but snappier. |
| `--fft-attack <0..1>` | 0.6 | Gravity smoothing: how fast bins rise on a louder reading. |
| `--fft-decay <0..1>` | 0.08 | Gravity smoothing: how fast bins fall back down on a quieter reading. |
| `--fft-gain <n>` | 40 | Log-compression contrast for bin magnitudes before display; higher = more contrast between quiet and loud. |
| `--fft-device <cpu\|gpu>` | `cpu` | Which FFT backend runs: `cpu` (works everywhere) or `gpu` (GLSL compute shader; needs GL 4.3 + compute shaders + SSBOs, and caps `--fft-size` at 2048). See [FFT & Frequency Bucketing](../architecture/fft.md). |
| `--freq-scale <name>` | `log2` | Perceptual scale raw FFT bins get bucketed into before any shader sees them: `log2`, `mel`, `bark`, `erb`, or `linear` (no bucketing, raw bins). See [FFT & Frequency Bucketing](../architecture/fft.md) for why this matters. |

## Desktop-embedded mode

| Flag | Default | What it does |
|---|---|---|
| `--desktop` | off | Render pinned behind desktop icons via X11 EWMH hints instead of a normal window (GLava's `-d`). X11 only — forces `--platform x11` internally. Also honored via rc.glsl's `#request setxwintype "desktop"` when this flag isn't passed. |
| `--desktop-geometry X,Y,W,H` | whole virtual screen | Place/size the desktop-mode window at an exact rect instead of covering the whole screen. Only applies with `--desktop`. |
| `--desktop-monitor <index>` | — | Cover exactly this monitor (index from `--list-monitors`) instead of the whole virtual screen. Mutually exclusive with `--desktop-geometry`. Only applies with `--desktop`. |

Verified on XFCE/xfwm4 only — see
[Desktop-Embedded Mode](../architecture/desktop-embedded-mode.md) for what's
implemented and what isn't (GNOME, native Wayland).

## Live control channel & hot-reload

| Flag | Default | What it does |
|---|---|---|
| `--no-control` | control channel on | Disable the live control channel entirely. |
| `--control-bind <host>` | `127.0.0.1` | Live control channel bind host. Set to `0.0.0.0` for LAN access (e.g. a phone on the same network) — **there's no authentication**, so only widen this on a network you trust. |
| `--control-port <n>` | `8642` | Live control channel port. Running multiple instances at once needs a distinct port per instance — a bind failure just disables the control channel for that instance (logged warning, not fatal). |
| `--no-hot-reload` | hot-reload on | Disable shader hot-reload. |

See [Live Control Channel & Hot-Reload](../architecture/control-channel.md)
for how properties, feeds, and reload actually work.

## Logging

| Flag | Default | What it does |
|---|---|---|
| `--log-level <level>` | `info` | Minimum severity to print: `debug`, `info`, `warn`, or `error`. `debug` also turns on the per-second FPS line and per-shader-pass compile chatter. |

## Everything together — a worked example

```bash
./GlavaSharp \
  --module aurora \
  --desktop --desktop-monitor 1 \
  --fft-device gpu --freq-scale erb \
  --gpu 1 \
  --control-bind 0.0.0.0 --control-port 9000 \
  --log-level debug
```

Runs `aurora` pinned behind desktop icons on monitor 1, FFT on the GPU
with ERB perceptual bucketing, rendering pinned to GPU index 1, control
channel reachable from the LAN on port 9000, with debug logging on.
