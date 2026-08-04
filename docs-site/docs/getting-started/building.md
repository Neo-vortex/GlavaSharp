# Building

Requires: .NET 10 SDK, a Rust toolchain (via [rustup](https://rustup.rs)),
`clang`/`libclang-dev` (for PipeWire's bindgen-based bindings, needed by
`native/pwshim` only — `native/x11shim` is pure Rust and needs no system
dev headers at all), and `libpipewire-0.3-dev`. On Ubuntu:

```bash
sudo apt install dotnet-sdk-10.0 libpipewire-0.3-dev pkg-config clang libclang-dev cmake
```

Then:

```bash
cmake -S . -B build
cmake --build build
# -> build/dist/GlavaSharp (+ libglfw*.so + shaders/ alongside it --
#    see Packaging for turning this into one actual file)
```

`CMakeLists.txt` orchestrates two `cargo build --release` invocations
(`native/pwshim`, `native/x11shim`) followed by `dotnet publish
-p:PublishAot=true`, statically linking both Rust staticlibs into the
final executable via `<NativeLibrary>`/`<DirectPInvoke>` in the `.csproj`.

## AVX2+FMA CPU FFT

For a faster CPU FFT on machines you know have AVX2+FMA (most x86_64 CPUs
since ~2013), reconfigure with `-DGLAVASHARP_AVX2_CPU_FFT=ON` before
building — 1.2x-1.6x faster (see [Benchmarks](../benchmarks.md)), at the
cost of the resulting binary refusing to run at all on CPUs without
AVX2+FMA:

```bash
cmake -S . -B build -DGLAVASHARP_AVX2_CPU_FFT=ON
cmake --build build && cmake --build build --target appimage
```

`-DGLAVASHARP_AVX2_CPU_FFT=ON` adds `-p:IlcInstructionSet=avx2` to the
publish command, enabling `CpuFft`'s AVX2+FMA path in the AOT build (see
[Benchmarks](../benchmarks.md) for why this is off by default and what it
actually costs). Two things worth knowing if you're touching this option
itself: the value has to be `avx2` alone, not `avx2,fma` — `fma` isn't a
standalone `--instruction-set` token this ILC version recognizes (`ilc
--help` lists the valid x64 set; AVX2 already implies FMA in its grouping,
confirmed by checking `CpuFft.UsingAvx2` on the resulting binary), and a
literal comma-containing value hits a separate MSBuild command-line
parsing quirk (`MSB1006: Property is not valid. Switch: fma`) if you do
add one without quoting the whole property. And
`packaging/build-appimage.sh --avx2-cpu-fft`'s `Text file busy` if
`mksquashfs` can't write the output file isn't a bug in the script — it
means the AppImage it's trying to overwrite is currently
FUSE-mounted/running; close that first.

The `appimage` target names its output `GlavaSharp-x86_64-avx2.AppImage`
instead of the plain name when this is on, so the two artifacts can't be
mixed up — see [Packaging](packaging.md).

## Cleaning

To clean everything, including dotnet's `obj`/`bin` and both crates'
`target/` directories (not just CMake's own `build/` directory):

```bash
cmake --build build --target clean-all
```

## Building each piece independently

```bash
# Rust shims only
./native/pwshim/build.sh
./native/x11shim/build.sh

# .NET project only (plain build, no AOT, doesn't need either Rust lib at all)
dotnet build GlavaSharp.slnx
```

Note: a plain `dotnet build`/`dotnet run` (no `PublishAot`) can't actually
run the app end-to-end — `PipeWireNative`/`X11Native`'s `LibraryImport`
calls need the statically-linked AOT build to resolve; without it they'll
throw `DllNotFoundException` trying to `dlopen` a `.so` that doesn't
exist. Use it for editing/compiling C# quickly; use the full `cmake
--build build` flow to actually run GlavaSharp.
