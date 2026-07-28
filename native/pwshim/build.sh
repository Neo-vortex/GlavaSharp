#!/usr/bin/env bash
set -euo pipefail

# Manual/standalone build of the pwshim crate (lives at native/pwshim/, a
# sibling of src/GlavaSharp/, not nested inside the C# project). CMake
# normally calls `cargo build --release` directly and points the .csproj's
# <NativeLibrary> at native/pwshim/target/release/libpwshim.a — this script
# is just for building the shim in isolation, e.g. to sanity-check it
# compiles before running the full CMake build.
#
# Requires: Rust toolchain (rustup) + libpipewire-0.3-dev.
# Ubuntu/Debian: sudo apt install libpipewire-0.3-dev pkg-config clang

cd "$(dirname "$0")"
cargo build --release
echo "Built target/release/libpwshim.a"
