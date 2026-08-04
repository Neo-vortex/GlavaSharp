# Common Scenarios

Task-oriented recipes. Each one links back to the
[CLI Reference](cli-reference.md) flags it uses and the architecture page
that explains *why* it works this way, if you want the full story.

## "I just want to see something pretty"

```bash
./GlavaSharp --module aurora
```

No audio device to pick, no config to write — `aurora` reacts to whatever
your system is currently playing through the default sink's monitor. Try
`--module clock` for an audio-reactive analog clock instead, or
`--module waterfall` for a scrolling spectrogram.

## "I want ambient wallpaper behind my desktop icons"

```bash
./GlavaSharp --desktop --module aurora
```

Pins a transparent, click-through window behind your desktop icons via
X11 EWMH hints, covering the whole (multi-monitor) screen by default.
Verified on XFCE/xfwm4 — see
[Desktop-Embedded Mode](../architecture/desktop-embedded-mode.md) for
which window managers this is known to work on.

**On a specific monitor only:**

```bash
./GlavaSharp --list-monitors                              # find the index
./GlavaSharp --desktop --desktop-monitor 1 --module aurora
```

**At an exact rect instead of a whole monitor:**

```bash
./GlavaSharp --desktop --desktop-geometry 100,100,800,600 --module aurora
```

`--desktop-geometry` and `--desktop-monitor` are mutually exclusive —
pick one.

## "I have a hybrid-graphics laptop and it's rendering on the wrong GPU"

```bash
./GlavaSharp --list-gpus
#   [0] Intel (pci id 0x8086:..., driver i915) [card0]
#   [1] AMD (pci id 0x1002:..., driver amdgpu) [card1]
./GlavaSharp --gpu 1
```

Check the `GL: ... / <renderer>` line GlavaSharp logs on startup to
confirm which GPU actually got used. `--gpu` also controls which GPU
`--fft-device gpu` runs on, since it's the same GL context. See
[GPU Selection](../architecture/gpu-selection.md).

## "I want to tune a module live, without restarting"

Two independent mechanisms work together here — both on by default:

1. Open `http://127.0.0.1:8642/` in a browser. Every registered
   property — the global `fft.attack`/`fft.decay`/`fft.gain`, plus
   whatever the active module declared via `#request property` (e.g.
   `aurora`'s `amplify`) — shows up as a slider. Drag it; the change
   applies on the next frame, no recompile.
2. Edit the module's own `.frag`/`.glsl` files (or any shared file it
   `#include`s) and save. The affected pass(es) recompile in place. A
   compile error is logged and the previous, still-working version keeps
   running.

Together: keep the app running against real audio, tune constants via
the slider until they feel right, then bake the final numbers back into
the shader source once you're happy. See
[Live Control Channel & Hot-Reload](../architecture/control-channel.md).

## "I want to control it from my phone, on the same Wi-Fi"

```bash
./GlavaSharp --control-bind 0.0.0.0 --control-port 8642
```

Then open `http://<this-machine's-LAN-IP>:8642/` from the phone. **There
is no authentication** — anyone who can reach `host:port` can change any
registered property — so only do this on a network you actually trust.
`--no-control` disables the channel entirely if you don't want it
reachable at all.

## "I'm writing a new module and iterating on shader code"

```bash
./GlavaSharp --module my-module --log-level debug
```

`--log-level debug` turns on per-shader-pass compile chatter, so a saved
edit's recompile (or its failure) is visible immediately. See
[Writing a Module](writing-a-module.md) for the full authoring guide,
including the `history` buffer for persistent state and `#request
property`/`#request feed` for live-tweakable values.

## "I want a frozen, repeatable screenshot moment"

Useful for `clock`, whose hands are normally live-driven by the system
clock. Open the control page, find the `seconds_since_midnight` property,
and untick its `auto: clock` checkbox — the hands freeze at whatever the
slider currently shows. Drag the slider to whatever time you want, take
your screenshot, and the value stays put (it won't drift) until you
re-enable the feed. This is the same feed mechanism [Writing a
Module](writing-a-module.md#3-feeds-let-a-data-source-drive-a-property-instead-of-a-human)
describes — any feed-eligible property can be frozen this way, not just
the clock.

## "Audio isn't coming from the device I expect"

```bash
./GlavaSharp --list-sinks
#   [ 62] source  Rapoo Camera Analog Stereo (...)
#   [ 63] sink    Studio 24c Analog Stereo (...)
./GlavaSharp --sink 63
```

`--sink` also accepts a name/description match instead of a numeric ID
(handy in scripts where the ID might shift between reboots):

```bash
./GlavaSharp --sink "Studio 24c Analog Stereo"
```

## "I want to run more than one instance at once"

Each instance needs its own control-channel port, or the second one's
control channel just disables itself (logged warning, not fatal — the
visualizer itself still runs fine):

```bash
./GlavaSharp --module bars   --control-port 8642 &
./GlavaSharp --module aurora --desktop --control-port 8643 &
```

Combine with `--desktop-monitor`/`--desktop-geometry` and `--gpu` to give
each instance its own monitor and GPU on a multi-GPU, multi-monitor rig.

## "I want to know if the AVX2 CPU FFT build is worth it on my machine"

```bash
./GlavaSharp --benchmark-fft --fft-device cpu
DOTNET_EnableAVX2=0 ./GlavaSharp --benchmark-fft --fft-device cpu   # scalar fallback, for comparison
```

Prints ms/call, calls/sec, and a checksum across a fixed spread of window
sizes for both paths on your actual hardware. If the AVX2 numbers look
worth it, rebuild with `-DGLAVASHARP_AVX2_CPU_FFT=ON` — see
[Building](../getting-started/building.md) and
[Benchmarks](../benchmarks.md) for what that flag costs (a binary that
refuses to run at all on non-AVX2 CPUs).

## "I want to compare the CPU and GPU FFT backends"

```bash
./GlavaSharp --benchmark-fft --fft-device cpu
./GlavaSharp --benchmark-fft --fft-device gpu
```

Both print a checksum of the returned spectrum — they should match to
5-6 significant figures at sizes both backends support (`--fft-device
gpu` caps at 2048 in normal operation, and the benchmark additionally
skips any size whose required compute-workgroup size exceeds your GPU's
actual limit, printing why rather than risking a hang). See
[Benchmarks](../benchmarks.md) for a real run's numbers.
