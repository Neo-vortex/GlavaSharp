namespace GlavaSharp.Windowing;

public enum PlatformPreference
{
    /// <summary>Let GLFW auto-select (Wayland if available, else X11).</summary>
    Any,
    Wayland,
    X11
}

public sealed class WindowOptions
{
    public required string Title { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public PlatformPreference Platform { get; init; } = PlatformPreference.Any;

    /// <summary>
    ///     OpenGL context version. Bumped from GLava's 3.3 floor to 4.3
    ///     because the FFT runs as a GPU compute shader (GL_ARB_compute_shader
    ///     + SSBOs, both 4.3 core). If your GPU/driver can't do 4.3, the window
    ///     will fail to create — see AppWindow's error message.
    /// </summary>
    public int GLMajor { get; init; } = 4;

    public int GLMinor { get; init; } = 3;

    /// <summary>
    ///     GLava's `-d` / `setxwintype "desktop"`: render pinned behind
    ///     desktop icons via X11 EWMH hints (see Windowing/X11Native.cs +
    ///     native/x11shim/) instead of as a normal top-level window. X11
    ///     only -- <see cref="AppWindow" /> forces <see cref="Platform" />
    ///     to <see cref="PlatformPreference.X11" /> when this is set, and
    ///     fails loudly if GLFW doesn't actually select X11.
    /// </summary>
    public bool DesktopMode { get; init; }

    /// <summary>
    ///     Desktop-mode placement/size, GLava's `setgeometry` equivalent for
    ///     `-d` (see `--desktop-geometry` in Program.cs). All four null (the
    ///     default) means "cover the whole screen", matching GlavaSharp's
    ///     original --desktop behavior. Ignored when <see cref="DesktopMode" />
    ///     is false.
    /// </summary>
    public int? DesktopX { get; init; }

    /// <summary>See <see cref="DesktopX" />.</summary>
    public int? DesktopY { get; init; }

    /// <summary>See <see cref="DesktopX" />.</summary>
    public int? DesktopWidth { get; init; }

    /// <summary>See <see cref="DesktopX" />.</summary>
    public int? DesktopHeight { get; init; }

    /// <summary>
    ///     Desktop mode, pinned to a specific monitor instead of an exact
    ///     rect or the whole (multi-monitor) virtual screen -- index matches
    ///     `--list-monitors`. Mutually exclusive with
    ///     <see cref="DesktopX" />/Y/Width/Height (Program.cs rejects passing
    ///     both `--desktop-geometry` and `--desktop-monitor`); resolved to a
    ///     concrete rect in <see cref="AppWindow" /> once GLFW's monitor list
    ///     is available (Program.cs runs before GLFW is initialized, so it
    ///     can't resolve this itself). Ignored when <see cref="DesktopMode" />
    ///     is false.
    /// </summary>
    public int? DesktopMonitorIndex { get; init; }
}