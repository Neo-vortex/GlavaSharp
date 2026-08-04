# Architecture Overview

## High-level pipeline

```
 PipeWire "what you hear" monitor
            │  (Rust: native/pwshim, staticlib, statically linked via Native AOT)
            ▼
   PipeWireAudioSource (Audio/)  ──▶  RingBuffer  ──▶  AudioWindow (tail buffer)
                                                              │
                                                              ▼
                                           IFft.Process() (Shaders/CpuFft.cs or GpuFft.cs,
                                              per --fft-device) windowed FFT →
                                              log-compressed, gravity-smoothed
                                                magnitude spectra (left, right)
                                                              │
                                                              ▼
                                      AudioSpectrumTexture × 2 (1D R32F textures)
                                                              │
                                                              ▼
                    ShaderModule (GLava module dir: 1.frag, 2.frag, ...)
              each pass: fullscreen triangle, samples audio_l/audio_r + tex0
              (previous pass's output), renders to ping-pong FBOs, last pass
                              to the default framebuffer
                                                              │
                                                              ▼
                                                 AppWindow: GLFW SwapBuffers
```

Every frame (`AppWindow.Run`): pump whatever new PCM PipeWire has produced
into the ring buffer, run one FFT over the most recent window, upload the
two resulting spectra as 1D textures, run the active module's pass chain,
swap buffers.

## Project layout

```
GlavaSharp/
├── GlavaSharp.slnx              solution file (new XML .slnx format)
├── CMakeLists.txt               orchestrates: cargo build (native/pwshim, native/x11shim) → dotnet publish (AOT)
├── .github/workflows/ci.yml     CI: rust jobs (pwshim, x11shim), dotnet job, full AOT integration job
├── src/
│   └── GlavaSharp/              the .NET project — all C# source lives here
│       ├── GlavaSharp.csproj
│       ├── Program.cs           CLI parsing, wiring, entry point
│       ├── FftSettings.cs
│       ├── GpuEnumerator.cs     --list-gpus / --gpu N (DRI_PRIME etc.)
│       ├── Audio/               PipeWire capture (P/Invoke into native/pwshim)
│       ├── Shaders/             FFT, shader preprocessing, module pass pipeline
│       ├── Windowing/           GLFW window/context/frame-loop,
│       │                       X11 desktop-mode P/Invoke (X11Native.cs)
│       ├── shaders/glava/       GLava's own shader tree, bundled as-is
│       ├── shaders/glavasharp/  GlavaSharp-original modules (e.g. waterfall) --
│       │                         NOT part of GLava's tree, see below
│       └── shaders/fft/         GpuFft's compute kernel(s) -- not a GLava
│                                 module tree at all, loaded directly by
│                                 Shaders/GpuFft.cs, not ShaderModule
└── native/
    ├── pwshim/                  standalone Rust crate — NOT nested in the
    │                             C# project, since it isn't C# code and has
    │                             its own independent build/test lifecycle
    │   ├── Cargo.toml
    │   └── src/lib.rs           PipeWire stream capture, exposed via a
    │                             small extern "C" FFI surface
    └── x11shim/                 same reasoning as pwshim: standalone Rust
                                  crate, statically linked, not nested in
                                  the C# project
        ├── Cargo.toml
        └── src/lib.rs           X11 EWMH desktop-mode (--desktop): window
                                  type/state, decorations, geometry,
                                  click-through, and a background
                                  re-lower-on-restack watcher, exposed via
                                  a small extern "C" FFI surface
```

`native/pwshim/` living outside `src/GlavaSharp/` (rather than e.g. a
`native-rs/` folder nested inside the C# project, which is how this
started out) is deliberate: it's a fully independent build unit with its
own `Cargo.lock`, its own CI job, and its own release cadence — nesting it
inside the .NET project directory implied an ownership relationship that
doesn't reflect how the two are actually built, versioned, or tested.
`native/x11shim/` follows the same reasoning.
