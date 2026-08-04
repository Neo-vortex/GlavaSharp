# Audio Capture

`Audio/` + `native/pwshim/`

PipeWire has no first-class C# bindings, and hand-writing raw P/Invoke
signatures against PipeWire's C API (which leans heavily on callbacks,
`spa_pod`s, and manual memory management) is exactly the kind of surface
where memory-safety bugs live. Instead:

- `native/pwshim` (Rust, crate name `pwshim`) uses the
  [`pipewire`](https://crates.io/crates/pipewire) crate to open a capture
  stream on the default sink's monitor (or a specific node, for
  `--sink`/`--list-sinks`), and exposes a minimal `extern "C"` surface:
  `pwshim_start`, `pwshim_stop`, `pwshim_list_targets`,
  `pwshim_free_string`. All the PipeWire-specific complexity (stream
  negotiation, format callbacks, buffer lifetime) stays inside Rust, where
  the type system and borrow checker actually catch misuse.
- `Audio/PipeWireNative.cs` is the thin `LibraryImport` (source-generated
  P/Invoke) layer over that FFI surface.
- `Audio/PipeWireAudioSource.cs` wraps it in `IAudioSource`, GlavaSharp's
  own abstraction, so the rest of the app (`AudioWindow`, `CpuFft`, ...)
  never touches PipeWire types directly.
- `Audio/RingBuffer.cs` + `Audio/AudioWindow.cs`: the ring buffer is a
  destructive read cursor fed by the native callback thread; `AudioWindow`
  sits on top to keep a fixed-size *tail* of the most recent N interleaved
  stereo frames available to the render thread every frame, even when a
  given frame produces fewer new samples than the FFT window needs.
- Built as `crate-type = ["staticlib"]` and statically linked into the
  final Native AOT executable via `<NativeLibrary>` +
  `<DirectPInvoke Include="pwshim"/>` in the `.csproj` — the shipped
  artifact is one file, no sibling `libpwshim.so` to lose track of. See
  [Status & Roadmap](../status-roadmap.md) for a real regression this
  project hit when `DirectPInvoke` got accidentally dropped from the
  csproj.
