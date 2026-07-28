using System;
using GlavaSharp.Audio;
using GlavaSharp.Shaders;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.GraphicsLibraryFramework;
using GLFWBind = OpenTK.Windowing.GraphicsLibraryFramework.GLFW;

namespace GlavaSharp.Windowing;

/// <summary>
///     Thin GLFW wrapper (deliberately not OpenTK's GameWindow) so we control
///     init hints, platform selection, and the frame loop directly — mirroring
///     GLava's own minimal C host rather than inheriting a game-engine loop.
///     Owns the render pipeline: pump audio -> GPU FFT -> shader module passes.
/// </summary>
public sealed unsafe class AppWindow : IDisposable
{
    private readonly IAudioSource _audio;
    private readonly AudioWindow _audioWindow;
    private readonly CpuFft _fft;
    private readonly ShaderModule _module;
    private readonly AudioSpectrumTexture _texL;
    private readonly AudioSpectrumTexture _texR;
    private Window* _handle;

    public AppWindow(WindowOptions options, IAudioSource audio, string shaderRootDir, string moduleName,
        FftSettings? fftSettings = null)
    {
        _audio = audio;

        if (options.Platform != PlatformPreference.Any)
        {
            var platform = options.Platform switch
            {
                PlatformPreference.Wayland => Platform.Wayland,
                PlatformPreference.X11 => Platform.X11,
                _ => Platform.Any
            };
            GLFWBind.InitHint(InitHintPlatform.Platform, platform);
        }

        if (!GLFWBind.Init())
            throw new InvalidOperationException("GLFW initialization failed.");

        LogSelectedPlatform();

        GLFWBind.WindowHint(WindowHintInt.ContextVersionMajor, options.GLMajor);
        GLFWBind.WindowHint(WindowHintInt.ContextVersionMinor, options.GLMinor);
        GLFWBind.WindowHint(WindowHintOpenGlProfile.OpenGlProfile, OpenGlProfile.Core);
        GLFWBind.WindowHint(WindowHintBool.OpenGLForwardCompat, true);
        GLFWBind.WindowHint(WindowHintBool.Resizable, true);

        _handle = GLFWBind.CreateWindow(options.Width, options.Height, options.Title, null, null);
        if (_handle == null)
        {
            GLFWBind.Terminate();
            throw new InvalidOperationException(
                "GLFW window creation failed. If this is an OpenGL 4.3 context error, " +
                "your GPU/driver may not support compute shaders (needed for the GPU FFT).");
        }

        GLFWBind.MakeContextCurrent(_handle);
        GLFWBind.SwapInterval(1); // vsync; revisit once we care about latency vs. audio sync

        GL.LoadBindings(new GLFWBindingsContext());
        Console.WriteLine($"[GlavaSharp] GL: {GL.GetString(StringName.Version)} / {GL.GetString(StringName.Renderer)}");

        // Audio pipeline: ring buffer -> tail window -> CPU FFT -> two 1D spectrum textures.
        Console.WriteLine($"[GlavaSharp] before fft");
        _fft = new CpuFft(fftSettings);
        Console.WriteLine($"[GlavaSharp] after fft");
        _texL = new AudioSpectrumTexture(_fft.Bins);
        _texR = new AudioSpectrumTexture(_fft.Bins);
        _audioWindow = new AudioWindow(_fft.N, Math.Max(_audio.Channels, 2));

        // Visual pipeline: GLava module directory (numbered .frag passes).
        _module = new ShaderModule(shaderRootDir, moduleName);
        Console.WriteLine($"[GlavaSharp] Loaded module '{moduleName}' from {_module.ModuleDir}");
    }

    public void Dispose()
    {
        _module.Dispose();
        _texL.Dispose();
        _texR.Dispose();
        _fft.Dispose();

        if (_handle != null)
        {
            GLFWBind.DestroyWindow(_handle);
            _handle = null;
        }

        GLFWBind.Terminate();
    }

    private static void LogSelectedPlatform()
    {
        try
        {
            var platform = GLFWBind.GetPlatform();
            Console.WriteLine($"[GlavaSharp] GLFW selected platform: {platform}");
        }
        catch (EntryPointNotFoundException)
        {
            Console.WriteLine("[GlavaSharp] GLFW build predates GetPlatform(); platform unknown at this log point.");
        }
    }

    public void Run()
    {
        while (!GLFWBind.WindowShouldClose(_handle))
        {
            GLFWBind.PollEvents();

            _audioWindow.Pump(_audio);
            var (magL, magR) = _fft.Process(_audioWindow.Snapshot);


            _texL.Upload(magL);
            _texR.Upload(magR);

            GLFWBind.GetFramebufferSize(_handle, out var fbWidth, out var fbHeight);
            if (fbWidth > 0 && fbHeight > 0) _module.Render(fbWidth, fbHeight, _texL.Handle, _texR.Handle, _fft.Bins);

            GLFWBind.SwapBuffers(_handle);
        }
    }
}