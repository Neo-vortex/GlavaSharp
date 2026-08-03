#!/usr/bin/env bash
set -euo pipefail

# Packs build/dist/ (the CMake/dotnet-publish output -- see CMakeLists.txt)
# into a single-file AppImage: build/dist/ today is GlavaSharp +
# libglfw*.so (OpenTK's GLFW native package, dynamically linked -- unlike
# pwshim/x11shim, which *are* statically linked, see native/*/) + shaders/,
# which contradicts README's "single self-contained executable" claim the
# moment you `ls` the actual output directory. This script doesn't change
# any of that linking -- it wraps the existing multi-file output into one
# AppImage, the standard Linux single-file-distributable format.
#
# Usage: packaging/build-appimage.sh [--avx2-cpu-fft]
# Requires: build/dist/GlavaSharp already built (run the normal CMake build
# first -- with -DGLAVASHARP_AVX2_CPU_FFT=ON first if passing --avx2-cpu-fft
# here, see CMakeLists.txt), mksquashfs (used internally by appimagetool),
# and network access on first run only, to fetch appimagetool itself
# (cached afterwards).
#
# --avx2-cpu-fft only changes the OUTPUT FILENAME
# (GlavaSharp-x86_64-avx2.AppImage instead of GlavaSharp-x86_64.AppImage) --
# this script doesn't rebuild anything itself, it just mirrors whatever's
# already in build/dist/. The distinct name exists so an AVX2+FMA-requiring
# build (see TECHNICAL.md's Benchmarks section -- it refuses to start on
# CPUs without both) can't be mistaken for, or silently overwrite, the
# portable one on disk.

AVX2_CPU_FFT=0
for arg in "$@"; do
    case "$arg" in
        --avx2-cpu-fft) AVX2_CPU_FFT=1 ;;
        *)
            echo "error: unknown argument: $arg" >&2
            exit 1
            ;;
    esac
done

cd "$(dirname "$0")/.."
REPO_ROOT="$(pwd)"
DIST_DIR="$REPO_ROOT/build/dist"
TOOLS_DIR="$REPO_ROOT/build/tools"
APPDIR="$REPO_ROOT/build/AppDir"
if [ "$AVX2_CPU_FFT" = 1 ]; then
    OUT="$REPO_ROOT/build/GlavaSharp-x86_64-avx2.AppImage"
    echo "warning: packing an AVX2+FMA-requiring build -- this AppImage will" >&2
    echo "         refuse to start (or crash) on CPUs without AVX2+FMA." >&2
else
    OUT="$REPO_ROOT/build/GlavaSharp-x86_64.AppImage"
fi
APPIMAGETOOL="$TOOLS_DIR/appimagetool-x86_64.AppImage"

if [ ! -x "$DIST_DIR/GlavaSharp" ]; then
    echo "error: $DIST_DIR/GlavaSharp not found -- run the normal build first:" >&2
    echo "  cmake --build build" >&2
    exit 1
fi

if ! command -v mksquashfs >/dev/null 2>&1; then
    echo "error: mksquashfs not found on PATH. Install it: sudo apt install squashfs-tools" >&2
    exit 1
fi

mkdir -p "$TOOLS_DIR"
if [ ! -x "$APPIMAGETOOL" ]; then
    echo "Fetching appimagetool (cached at $APPIMAGETOOL for future runs) ..."
    curl -L -o "$APPIMAGETOOL" \
        https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$APPIMAGETOOL"
fi

# Fresh AppDir every run -- cheap (build/dist/ is a few MB) and avoids
# stale files from a previous module/shader layout lingering.
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/icons/hicolor/256x256/apps"

# Mirrors build/dist/ verbatim except GlavaSharp.dbg -- debug symbols aren't
# needed to run and just bloat the AppImage; anyone who needs them still
# has them in build/dist/ from the normal build.
for entry in "$DIST_DIR"/*; do
    name="$(basename "$entry")"
    [ "$name" = "GlavaSharp.dbg" ] && continue
    cp -a "$entry" "$APPDIR/usr/bin/$name"
done

install -m 755 "$REPO_ROOT/packaging/appimage/AppRun" "$APPDIR/AppRun"
install -m 644 "$REPO_ROOT/packaging/appimage/GlavaSharp.desktop" "$APPDIR/GlavaSharp.desktop"
install -m 644 "$REPO_ROOT/packaging/appimage/glavasharp.png" "$APPDIR/glavasharp.png"
install -m 644 "$REPO_ROOT/packaging/appimage/glavasharp.png" \
    "$APPDIR/usr/share/icons/hicolor/256x256/apps/glavasharp.png"

# APPIMAGE_EXTRACT_AND_RUN: appimagetool is itself shipped as an AppImage,
# which normally FUSE-mounts itself -- forcing the extract-and-run fallback
# here means this script works the same whether or not FUSE/(/dev/fuse) is
# available on the machine actually running the build (many CI containers
# don't have it). This only affects how appimagetool runs *right now*; the
# GlavaSharp AppImage this produces supports both modes for whoever runs it
# later, same as any AppImage.
APPIMAGE_EXTRACT_AND_RUN=1 ARCH=x86_64 "$APPIMAGETOOL" "$APPDIR" "$OUT"

chmod +x "$OUT"
echo "Built: $OUT"
