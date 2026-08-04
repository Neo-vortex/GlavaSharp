# Running

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
attack/decay/gain, plus module properties like `aurora`'s `amplify`) —
edit a `.frag`/`.glsl` file and it hot-reloads on save, no restart needed
for either. See [Live Control Channel & Hot-Reload](../architecture/control-channel.md)
for how that works under the hood.

See the top of `src/GlavaSharp/Program.cs` for the full CLI flag
reference, or the [Architecture](../architecture/overview.md) section for
how each piece actually works under the hood.

## Standalone FFT benchmark mode

`--benchmark-fft` runs entirely outside the normal app flow — no window,
no `ShaderModule`, no audio capture, just `IFft.Process()` timed in a loop
and a results table on stdout. See [Benchmarks](../benchmarks.md) for
sample output and how to read it.
