using System;
using System.Runtime.InteropServices;

namespace GlavaSharp.Windowing;

/// <summary>
///     P/Invoke surface onto native/x11shim (Rust), which does the actual
///     EWMH desktop-mode work (see native/x11shim/src/lib.rs) -- setting
///     _NET_WM_WINDOW_TYPE_NORMAL/_NET_WM_STATE (below/sticky/skip_taskbar/
///     skip_pager), stripping decorations, and restacking above xfdesktop on
///     restack as a fallback. Mirrors Audio/PipeWireNative.cs: a thin
///     LibraryImport layer over a statically-linked Rust staticlib, not a C#
///     reimplementation of the X11 protocol work.
/// </summary>
internal static partial class X11Native
{
    private const string Lib = "x11shim"; // statically linked in via Native AOT (see native/x11shim/ + <NativeLibrary> in GlavaSharp.csproj) — no separate .so is shipped

    /// <summary>
    ///     Starts desktop mode for the given X11 window (an XID, e.g. from
    ///     GLFW's <c>GetX11Window</c>). Returns an opaque handle to pass to
    ///     <see cref="x11shim_desktop_mode_stop" />, or <see cref="IntPtr.Zero" />
    ///     if setup failed (already logged to stderr on the native side) --
    ///     callers should treat that as "continue as a normal window."
    ///     <paramref name="geomWidth" />/<paramref name="geomHeight" /> &lt;= 0
    ///     means "cover the whole screen" (pass 0 for all four when no
    ///     explicit geometry was requested); otherwise the window is placed
    ///     at exactly <paramref name="geomX" />,<paramref name="geomY" />
    ///     sized <paramref name="geomWidth" />x<paramref name="geomHeight" />.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial IntPtr x11shim_desktop_mode_start(
        ulong windowXid, int geomX, int geomY, int geomWidth, int geomHeight);

    [LibraryImport(Lib)]
    public static partial void x11shim_desktop_mode_stop(IntPtr ctx);
}
