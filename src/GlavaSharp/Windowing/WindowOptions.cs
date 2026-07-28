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
}