cmake_minimum_required(VERSION 3.20)

# No C/C++/Fortran compiler required at the CMake level — we're only
# orchestrating external toolchains (dotnet, cargo), not compiling
# anything CMake's own compiler detection cares about.
project(GlavaSharp LANGUAGES NONE)

# ------------------------------------------------------------------------
# Tool discovery — fail fast with a clear message instead of a cryptic
# error partway through the build.
# ------------------------------------------------------------------------

find_program(DOTNET_EXECUTABLE dotnet)
if (NOT DOTNET_EXECUTABLE)
    message(FATAL_ERROR
            "dotnet SDK not found on PATH. Install .NET 10 SDK: "
            "https://dotnet.microsoft.com/download")
endif ()

execute_process(
        COMMAND ${DOTNET_EXECUTABLE} --version
        OUTPUT_VARIABLE DOTNET_VERSION_RAW
        OUTPUT_STRIP_TRAILING_WHITESPACE
        RESULT_VARIABLE DOTNET_VERSION_RESULT
)
if (NOT DOTNET_VERSION_RESULT EQUAL 0)
    message(FATAL_ERROR "Found dotnet at ${DOTNET_EXECUTABLE} but `dotnet --version` failed.")
endif ()

# Compare only the major version (dotnet --version prints e.g. "10.0.100").
string(REGEX MATCH "^([0-9]+)" DOTNET_MAJOR_MATCH "${DOTNET_VERSION_RAW}")
if (CMAKE_MATCH_1 LESS 10)
    message(FATAL_ERROR
            "GlavaSharp requires .NET 10 SDK or newer; found ${DOTNET_VERSION_RAW} "
            "at ${DOTNET_EXECUTABLE}.")
endif ()
message(STATUS "Found dotnet ${DOTNET_VERSION_RAW} at ${DOTNET_EXECUTABLE}")

find_program(CARGO_EXECUTABLE cargo)
if (NOT CARGO_EXECUTABLE)
    message(FATAL_ERROR
            "cargo not found on PATH. Install Rust via https://rustup.rs")
endif ()

find_program(RUSTC_EXECUTABLE rustc)
if (NOT RUSTC_EXECUTABLE)
    message(FATAL_ERROR
            "rustc not found alongside cargo at ${CARGO_EXECUTABLE} — broken Rust install?")
endif ()

execute_process(
        COMMAND ${RUSTC_EXECUTABLE} --version
        OUTPUT_VARIABLE RUSTC_VERSION_RAW
        OUTPUT_STRIP_TRAILING_WHITESPACE
)
message(STATUS "Found ${RUSTC_VERSION_RAW} at ${RUSTC_EXECUTABLE}")

# libspa-sys (a transitive dependency of the pipewire crate) uses bindgen,
# which shells out to clang to parse PipeWire's C headers. libclang alone
# isn't enough — without the full clang package installed, bindgen can
# fail with a confusing "stdbool.h not found" error well into the build.
# Check for it now so that failure mode surfaces here instead.
find_program(CLANG_EXECUTABLE clang)
if (NOT CLANG_EXECUTABLE)
    message(FATAL_ERROR
            "clang not found on PATH. The Rust PipeWire bindings (libspa-sys) "
            "need it for bindgen, even though only libclang is linked at build "
            "time. Install it: sudo apt install clang libclang-dev\n"
            "If you still hit a 'stdbool.h not found' error after installing "
            "clang, set: export BINDGEN_EXTRA_CLANG_ARGS=\"-I/usr/lib/gcc/$(gcc -dumpmachine)/$(gcc -dumpversion | cut -d. -f1)/include\"")
endif ()
message(STATUS "Found clang at ${CLANG_EXECUTABLE}")

find_package(PkgConfig REQUIRED)
pkg_check_modules(PIPEWIRE REQUIRED libpipewire-0.3)
message(STATUS "Found libpipewire-0.3 (${PIPEWIRE_VERSION}) via pkg-config")

# ------------------------------------------------------------------------
# Configuration
# ------------------------------------------------------------------------

# .NET Runtime Identifier for the final executable. Override with
# -DGLAVASHARP_RID=linux-arm64 etc. Default follows the host architecture.
if (NOT DEFINED GLAVASHARP_RID)
    execute_process(COMMAND uname -m OUTPUT_VARIABLE HOST_ARCH OUTPUT_STRIP_TRAILING_WHITESPACE)
    if (HOST_ARCH STREQUAL "aarch64")
        set(GLAVASHARP_RID "linux-arm64")
    else ()
        set(GLAVASHARP_RID "linux-x64")
    endif ()
endif ()
message(STATUS "Target runtime identifier: ${GLAVASHARP_RID}")

if (NOT DEFINED GLAVASHARP_CONFIGURATION)
    set(GLAVASHARP_CONFIGURATION "Release")
endif ()

# Off by default: Native AOT (ILC) compiles Avx2.IsSupported/Fma.IsSupported
# in Shaders/CpuFft.cs to a hardcoded `false` unless the target instruction
# set is explicitly widened past its conservative SSE2 baseline -- see
# TECHNICAL.md's Benchmarks section for the measured speedup (1.2x-1.6x)
# and, more importantly, why this isn't a free upgrade: turning it on makes
# ILC bake AVX2+FMA in as a hard *requirement* rather than a runtime-checked
# option, deleting CpuFft's scalar fallback from the compiled output and
# making the resulting binary refuse to start (fail-fast, occasionally a
# raw illegal-instruction crash) on any CPU that turns out not to have
# AVX2+FMA. Only turn this on if you know every machine that will actually
# run the resulting build has both -- the `appimage` target names its output
# GlavaSharp-x86_64-avx2.AppImage instead of the plain name specifically so
# this can't be handed out/mistaken for the portable build by accident.
#
# Usage: cmake -S . -B build -DGLAVASHARP_AVX2_CPU_FFT=ON
option(GLAVASHARP_AVX2_CPU_FFT
        "Require AVX2+FMA for the Native AOT build's CpuFft (faster, but the binary won't run on CPUs without AVX2+FMA)"
        OFF)
if (GLAVASHARP_AVX2_CPU_FFT)
    message(WARNING
            "GLAVASHARP_AVX2_CPU_FFT is ON: the published binary will require "
            "AVX2+FMA and refuse to start (or crash) on CPUs without them. "
            "This is a portability/speed tradeoff, not a strict upgrade -- see "
            "TECHNICAL.md's Benchmarks section.")
endif ()

# Repo layout:
#   src/GlavaSharp/    the .NET project (csproj + all C# sources)
#   native/pwshim/      the Rust PipeWire capture shim (separate, not nested
#                        inside the C# project — it's a standalone crate the
#                        .csproj statically links against, not C# code)
#   native/x11shim/     the Rust X11 EWMH desktop-mode shim (--desktop),
#                        same reasoning as pwshim -- a standalone crate, not
#                        C# code, statically linked via the .csproj
set(DOTNET_PROJECT_DIR ${CMAKE_SOURCE_DIR}/src/GlavaSharp)
set(DOTNET_PROJECT ${DOTNET_PROJECT_DIR}/GlavaSharp.csproj)
set(RUST_SHIM_DIR ${CMAKE_SOURCE_DIR}/native/pwshim)
set(RUST_SHIM_LIB ${RUST_SHIM_DIR}/target/release/libpwshim.a)
set(X11_SHIM_DIR ${CMAKE_SOURCE_DIR}/native/x11shim)
set(X11_SHIM_LIB ${X11_SHIM_DIR}/target/release/libx11shim.a)
set(PUBLISH_DIR ${CMAKE_BINARY_DIR}/dist)
set(FINAL_EXECUTABLE ${PUBLISH_DIR}/GlavaSharp)

# ------------------------------------------------------------------------
# Step 1: build the Rust native shims as static libraries.
#
# NOTE: this always invokes `cargo build`, rather than only running when
# the .a is missing. cargo's own incremental cache keeps a no-op rebuild
# fast, and the alternative (an OUTPUT-based custom_command with no DEPENDS
# on the actual .rs/Cargo.toml sources) silently goes stale — CMake/Make
# sees the .a already exists and never re-invokes cargo again, even after
# you edit lib.rs. That bit us once already; don't repeat it.
# ------------------------------------------------------------------------

add_custom_target(pwshim ALL
        COMMAND ${CARGO_EXECUTABLE} build --release
        WORKING_DIRECTORY ${RUST_SHIM_DIR}
        BYPRODUCTS ${RUST_SHIM_LIB}
        COMMENT "Building PipeWire capture shim (Rust static lib)"
        VERBATIM
)

# x11rb (x11shim's only dependency) is pure Rust and needs no clang/bindgen
# or system X11 dev headers, so unlike pwshim there's no extra find_program/
# pkg_check_modules needed before this target.
add_custom_target(x11shim ALL
        COMMAND ${CARGO_EXECUTABLE} build --release
        WORKING_DIRECTORY ${X11_SHIM_DIR}
        BYPRODUCTS ${X11_SHIM_LIB}
        COMMENT "Building X11 desktop-mode shim (Rust static lib)"
        VERBATIM
)

# ------------------------------------------------------------------------
# Step 2: publish the .NET project as a single Native AOT executable,
# statically linking the Rust shims (see <NativeLibrary> in the .csproj).
#
# Same always-run reasoning as above: dotnet's own build/publish
# incremental checks won't necessarily notice that the .a files changed
# underneath it, so we always re-publish rather than gate on a file
# timestamp. dotnet's own caching keeps a no-op publish reasonably fast.
# ------------------------------------------------------------------------

set(GLAVASHARP_PUBLISH_ARGS -p:PublishAot=true)
if (GLAVASHARP_AVX2_CPU_FFT)
    # "avx2" alone is enough -- ILC's x64 instruction-set table bundles FMA
    # into it (every real AVX2 CPU has had FMA since Haswell), confirmed by
    # checking CpuFft.UsingAvx2 (Avx2.IsSupported && Fma.IsSupported) on the
    # resulting binary. A literal "avx2,fma" is REJECTED by ILC -- "fma"
    # isn't a standalone --instruction-set token in this SDK (`ilc --help`
    # lists the valid x64 set; there's no bare "fma" entry) -- and the comma
    # inside "-p:...=avx2,fma" additionally trips an MSBuild command-line
    # parsing quirk (MSB1006 "Property is not valid") if you do add a second
    # value, so don't add one without quoting the whole property value.
    list(APPEND GLAVASHARP_PUBLISH_ARGS -p:IlcInstructionSet=avx2)
endif ()

add_custom_target(glavasharp ALL
        COMMAND ${DOTNET_EXECUTABLE} publish ${DOTNET_PROJECT}
        -c ${GLAVASHARP_CONFIGURATION}
        -r ${GLAVASHARP_RID}
        --self-contained true
        ${GLAVASHARP_PUBLISH_ARGS}
        -o ${PUBLISH_DIR}
        WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
        BYPRODUCTS ${FINAL_EXECUTABLE}
        COMMENT "Publishing GlavaSharp as a single Native AOT executable"
        VERBATIM
)
add_dependencies(glavasharp pwshim x11shim)

# ------------------------------------------------------------------------
# Convenience: `cmake --install` copies just the final binary somewhere.
# ------------------------------------------------------------------------

install(PROGRAMS ${FINAL_EXECUTABLE} DESTINATION bin)

add_custom_command(TARGET glavasharp POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E echo "Built: ${FINAL_EXECUTABLE}"
)
if (GLAVASHARP_AVX2_CPU_FFT)
    add_custom_command(TARGET glavasharp POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E echo
            "  requires AVX2+FMA at runtime -- GLAVASHARP_AVX2_CPU_FFT=ON"
            VERBATIM
    )
endif ()

# ------------------------------------------------------------------------
# `clean-all` — CMake's built-in `clean` target only removes files it knows
# it generated (mainly stuff under CMAKE_BINARY_DIR), so it doesn't touch
# dotnet's obj/bin directories or cargo's target/ directory, both of which
# have their own out-of-band incremental caches. This target wipes all of
# it for a genuinely from-scratch build:
#
#   rm -rf build obj bin           (this project's usual out-of-source
#                                    build dir, plus dotnet's obj/ and bin/)
#   + the Rust target/ directories (cargo's build caches, native/pwshim/target
#                                    and native/x11shim/target)
#
# Usage (run from anywhere, doesn't require a configured build dir first,
# since it only shells out to `rm`/`cmake -E rm`):
#   cmake --build build --target clean-all
#   # or, once configured:
#   cd build && make clean-all
# ------------------------------------------------------------------------

add_custom_target(clean-all
        COMMAND ${CMAKE_COMMAND} -E rm -rf ${CMAKE_SOURCE_DIR}/build
        COMMAND ${CMAKE_COMMAND} -E rm -rf ${DOTNET_PROJECT_DIR}/obj
        COMMAND ${CMAKE_COMMAND} -E rm -rf ${DOTNET_PROJECT_DIR}/bin
        COMMAND ${CMAKE_COMMAND} -E rm -rf ${RUST_SHIM_DIR}/target
        COMMAND ${CMAKE_COMMAND} -E rm -rf ${X11_SHIM_DIR}/target
        COMMENT "Removing build/, obj/, bin/, and the Rust target/ directories"
        VERBATIM
)

# ------------------------------------------------------------------------
# `appimage` — packs ${PUBLISH_DIR} (GlavaSharp + libglfw*.so + shaders/,
# see packaging/build-appimage.sh) into one AppImage file. NOT part of the
# default ALL build: it fetches appimagetool from GitHub on first run
# (cached under build/tools/ afterwards), which the plain `glavasharp`
# target deliberately doesn't require just to build and run locally.
#
# Usage: cmake --build build --target appimage
# ------------------------------------------------------------------------

# --avx2-cpu-fft here only changes the *output filename*
# (GlavaSharp-x86_64-avx2.AppImage vs GlavaSharp-x86_64.AppImage) -- by the
# time this script runs, build/dist/ already reflects whatever
# GLAVASHARP_AVX2_CPU_FFT built above, so the script isn't rebuilding
# anything differently, just making sure the AVX2-requiring artifact can't
# be mistaken for (or overwrite) the portable one on disk.
set(GLAVASHARP_APPIMAGE_ARGS "")
if (GLAVASHARP_AVX2_CPU_FFT)
    set(GLAVASHARP_APPIMAGE_ARGS --avx2-cpu-fft)
endif ()

add_custom_target(appimage
        COMMAND ${CMAKE_SOURCE_DIR}/packaging/build-appimage.sh ${GLAVASHARP_APPIMAGE_ARGS}
        WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
        COMMENT "Packing GlavaSharp into a single-file AppImage"
        VERBATIM
)
add_dependencies(appimage glavasharp)
