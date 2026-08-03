#!/usr/bin/env bash
set -euo pipefail

# Manual/standalone build of the x11shim crate (lives at native/x11shim/, a
# sibling of src/GlavaSharp/ and native/pwshim/). CMake normally calls
# `cargo build --release` directly and points the .csproj's <NativeLibrary>
# at native/x11shim/target/release/libx11shim.a -- this script is just for
# building the shim in isolation, e.g. to sanity-check it compiles before
# running the full CMake build.
#
# Requires: Rust toolchain (rustup). Unlike pwshim, no system dev headers or
# clang are needed -- x11rb speaks the X11 wire protocol directly.

cd "$(dirname "$0")"
cargo build --release
echo "Built target/release/libx11shim.a"
