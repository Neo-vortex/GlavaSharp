# Design Trade-offs

- **CPU FFT as the default, GPU FFT available as opt-in.** `GpuFft` is a
  real, working, bit-for-bit-equivalent GPU implementation (see
  [FFT & Frequency Bucketing](architecture/fft.md)), but it's newer and
  has already surfaced two driver-level gotchas (a compile hang, a
  uniform type mismatch) during bring-up on a single machine. `CpuFft`
  stays the default until `GpuFft` has more mileage across GPU
  vendors/drivers; pass `--fft-device gpu` to try it.
- **A small preprocessor subset instead of a full GLava-language
  reimplementation.** Enough to run real, unmodified GLava shader files
  for the common cases. Reimplementing 100% of GLava's preprocessor was
  judged not worth doing before validating the rest of the pipeline end
  to end — see [Status & Roadmap](status-roadmap.md) for what's still
  unimplemented (none of it currently blocks any bundled module).
- **Rust for the audio backend instead of direct P/Invoke onto PipeWire's
  C API.** Adds a second toolchain and a second CI job, in exchange for
  keeping the trickiest, most callback-heavy native surface in a language
  that can actually check it. `native/pwshim` is deliberately narrow —
  start/stop/list/read, nothing else — specifically so this trade stays
  worth it rather than becoming "half the app is now in Rust."
- **Native AOT + static linking instead of a dynamically linked
  executable.** Produces one self-contained binary with no sibling `.so`
  and no shared shader/config install step (yet), at the cost of AOT's
  usual constraints (no runtime codegen, trimming-sensitive reflection —
  hence `EnableTrimAnalyzer`/`EnableAotAnalyzer` as build errors, not
  warnings, in the `.csproj`) and a build pipeline that requires both the
  Rust and .NET toolchains present together at publish time, orchestrated
  by CMake rather than `dotnet publish` alone. This trade-off directly
  caused a real regression once — see [Status & Roadmap](status-roadmap.md)
  for the `DirectPInvoke` story.
- **GLFW via OpenTK instead of talking to Xlib/Wayland directly.** Costs a
  dependency, buys X11 *and* Wayland support from day one instead of
  committing to one compositor API, which is also why this is one of the
  two concrete feature wins over upstream GLava's mainline branch.
- **`x11rb` (pure-Rust X11 protocol) in `native/x11shim` instead of raw
  Xlib P/Invoke from C#, for desktop-embedded mode.** GLFW's window/context
  creation stays untouched — only the EWMH property/stacking work that GLFW
  has no concept of moves to Rust, mirroring the same reasoning as
  `native/pwshim`: keep the trickiest native surface (here, background
  thread + X server connection, watching for and reacting to async stacking
  events) in a language whose type system can actually check it, behind a
  narrow `extern "C"` surface. Using `x11rb` over `libX11`/Xlib-sys bindgen
  specifically avoids repeating pwshim's `clang`/`libclang-dev` build
  dependency for a crate whose actual protocol surface (a handful of
  `ChangeProperty`/`ConfigureWindow`/`ChangeWindowAttributes`/
  `ShapeRectangles` requests) is far narrower than PipeWire's.
- **Click-through via the SHAPE extension instead of relying on window
  stacking.** Once live testing showed xfwm4 won't reliably restack a
  client strictly below xfdesktop no matter how the request is phrased,
  making the window unconditionally click-through sidesteps the fight
  entirely instead of chasing WM-specific stacking behavior further — see
  [Status & Roadmap](status-roadmap.md).
- **A persistent "history" buffer as a `ShaderModule` extension, rather
  than a separate module type/interface, for `waterfall`.** GLava's module
  format has no feedback/persistence concept at all, but bolting the
  minimum needed (one more `#request uniform` role, one more ping-pong
  pair that isn't cleared per-frame) onto the existing pass-chain
  machinery reused ~90% of it, instead of forking a parallel "native
  module" abstraction with its own render path.
