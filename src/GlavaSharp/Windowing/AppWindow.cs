using System;
using System.Diagnostics;
using System.Linq;
using GlavaSharp.Audio;
using GlavaSharp.Control;
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
    private readonly IFft _fft;
    private readonly ShaderModule _module;
    private readonly AudioSpectrumTexture _texL;
    private readonly AudioSpectrumTexture _texR;
    private readonly PropertyStore _propertyStore = new();
    private ControlServer? _controlServer;
    private Window* _handle;
    private IntPtr _desktopModeHandle;

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
        // Without this, the window's alpha channel exists (GLFW defaults to
        // 8 alpha bits) but the X server composites the window as opaque
        // regardless of what the shaders write into it -- GLava's own bars
        // shader already writes alpha=0 for "no bar here" pixels (see
        // shaders/glava/bars/1.frag's default `fragment = vec4(0,0,0,0)`),
        // it just needs a compositor-visible alpha channel to show through.
        // Requires a running compositing manager (xfwm4 has one built in).
        if (options.DesktopMode) GLFWBind.WindowHint(WindowHintBool.TransparentFramebuffer, true);

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
        Log.Info($"GL: {GL.GetString(StringName.Version)} / {GL.GetString(StringName.Renderer)}");

        if (options.DesktopMode) SetUpDesktopMode(options);

        // Audio pipeline: ring buffer -> tail window -> FFT (CPU or GPU,
        // per FftSettings.Device) -> two 1D spectrum textures.
        var device = fftSettings?.Device ?? FftDevice.Cpu;
        Log.Debug($"before fft (device={device})");
        _fft = device switch
        {
            FftDevice.Gpu => new GpuFft(fftSettings),
            _ => new CpuFft(fftSettings)
        };
        Log.Debug("after fft");
        _texL = new AudioSpectrumTexture(_fft.Bins);
        _texR = new AudioSpectrumTexture(_fft.Bins);
        _audioWindow = new AudioWindow(_fft.N, Math.Max(_audio.Channels, 2));

        // Visual pipeline: GLava module directory (numbered .frag passes).
        // useAlpha: true whenever the window actually has a usable alpha
        // channel (TransparentFramebuffer is only requested in desktop mode,
        // above) -- see ShaderModule's constructor doc for why this matters.
        // freqPrebucketed: mirrors whatever FrequencyScale _fft actually
        // ended up using (defaults to Log2 same as FftSettings itself when
        // fftSettings is null) so util/smooth.glsl's warp gets disabled
        // exactly when CpuFft/GpuFft are actually bucketing upstream.
        var freqPrebucketed = (fftSettings?.Scale ?? FrequencyScale.Log2) != FrequencyScale.Linear;
        _module = new ShaderModule(shaderRootDir, moduleName, options.DesktopMode, freqPrebucketed,
            options.HotReloadEnabled);
        Log.Info($"Loaded module '{moduleName}' from {_module.ModuleDir}");

        SetUpLiveControl(options, fftSettings);
    }

    /// <summary>
    ///     Registers the FFT globals and every module-declared
    ///     <c>#request property</c> into <see cref="_propertyStore" />, then
    ///     starts <see cref="ControlServer" /> unless
    ///     <see cref="WindowOptions.ControlEnabled" /> is false. A bind
    ///     failure (e.g. another GlavaSharp instance already holds the port)
    ///     is logged and swallowed -- the control channel is a nice-to-have,
    ///     not something worth crashing the visualizer over. Independent of
    ///     <see cref="WindowOptions.DesktopMode" />: this runs identically
    ///     whether the window ends up pinned/embedded or normal.
    /// </summary>
    private void SetUpLiveControl(WindowOptions options, FftSettings? fftSettings)
    {
        var settings = fftSettings ?? new FftSettings();
        _propertyStore.Register("fft.attack", "fft", 0f, 1f, settings.Attack);
        _propertyStore.Register("fft.decay", "fft", 0f, 1f, settings.Decay);
        _propertyStore.Register("fft.gain", "fft", 1f, 200f, settings.Gain);

        var feedSources = _module.FeedBindings.ToDictionary(f => f.PropertyName, f => f.Source);
        foreach (var p in _module.PropertyDeclarations)
            _propertyStore.Register(p.Name, _module.ModuleName, p.Min, p.Max, p.Default,
                feedSources.GetValueOrDefault(p.Name));

        if (!options.ControlEnabled) return;
        try
        {
            _controlServer = new ControlServer(_propertyStore, options.ControlBindHost, options.ControlPort);
            Log.Info($"Live control channel: {_controlServer.BoundPrefix}");
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"Live control channel disabled: {ex.Message}");
        }
    }

    /// <summary>
    ///     Applies one queued property change (render-thread only, called
    ///     from <see cref="Run" /> via <see cref="PropertyStore.DrainPending" />) --
    ///     routes the three well-known FFT globals to <see cref="_fft" />'s
    ///     setters and everything else (module-declared properties, named
    ///     exactly as the shader's own <c>#request property</c> declared
    ///     them) straight to <see cref="ShaderModule.SetProperty" />.
    /// </summary>
    private void ApplyPropertyChange(string name, float value)
    {
        switch (name)
        {
            case "fft.attack":
                _fft.SetAttack(value);
                break;
            case "fft.decay":
                _fft.SetDecay(value);
                break;
            case "fft.gain":
                _fft.SetGain(value);
                break;
            default:
                _module.SetProperty(name, value);
                break;
        }
    }

    public void Dispose()
    {
        _controlServer?.Dispose();
        _module.Dispose();
        _texL.Dispose();
        _texR.Dispose();
        _fft.Dispose();

        if (_desktopModeHandle != IntPtr.Zero)
        {
            X11Native.x11shim_desktop_mode_stop(_desktopModeHandle);
            _desktopModeHandle = IntPtr.Zero;
        }

        if (_handle != null)
        {
            GLFWBind.DestroyWindow(_handle);
            _handle = null;
        }

        GLFWBind.Terminate();
    }

    /// <summary>
    ///     GLava's `-d` / `setxwintype "desktop"`. Hands the underlying X11
    ///     window ID to native/x11shim (Rust), which does the actual EWMH
    ///     work -- see Windowing/X11Native.cs and native/x11shim/src/lib.rs.
    ///     Requires GLFW to have actually selected X11 (Program.cs forces
    ///     <see cref="PlatformPreference.X11" /> when --desktop is set, so
    ///     this should only trip if X11 itself isn't available at all).
    /// </summary>
    private void SetUpDesktopMode(WindowOptions options)
    {
        try
        {
            var platform = GLFWBind.GetPlatform();
            if (platform != Platform.X11)
                throw new InvalidOperationException(
                    $"--desktop requires an X11 session, but GLFW selected platform '{platform}'. " +
                    "Desktop-embedded mode isn't implemented for native Wayland yet -- under a Wayland " +
                    "session this needs at least XWayland running.");
        }
        catch (EntryPointNotFoundException)
        {
            // GLFW build predates glfwGetPlatform() (same fallback as
            // LogSelectedPlatform below) -- we already forced the X11 init
            // hint before GLFW's Init() succeeded, so proceed on that basis.
        }

        var xid = (ulong)GLFWBind.GetX11Window(_handle);
        // 0/0/0/0 tells x11shim "no override, cover the whole screen" -- its
        // own default. DesktopWidth/Height <= 0 (unset) short-circuits to
        // that regardless of what X/Y are, matching X11Native's contract.
        var geomX = options.DesktopX ?? 0;
        var geomY = options.DesktopY ?? 0;
        var geomW = options.DesktopWidth ?? 0;
        var geomH = options.DesktopHeight ?? 0;

        if (options.DesktopMonitorIndex is { } monitorIndex)
            (geomX, geomY, geomW, geomH) = ResolveMonitorRect(monitorIndex);
        _desktopModeHandle = X11Native.x11shim_desktop_mode_start(xid, geomX, geomY, geomW, geomH);
        if (_desktopModeHandle == IntPtr.Zero)
            Log.Warn(
                "--desktop setup failed (see x11shim error above); continuing as a normal window.");
        else if (geomW > 0 && geomH > 0)
            Log.Info(
                "Desktop mode enabled (X11 EWMH: NORMAL+below+sticky+skip_taskbar/pager, " +
                $"auto-restacked above xfdesktop as a fallback, click-through), geometry {geomW}x{geomH}+{geomX}+{geomY}.");
        else
            Log.Info(
                "Desktop mode enabled (X11 EWMH: NORMAL+below+sticky+skip_taskbar/pager, " +
                "auto-restacked above xfdesktop as a fallback, click-through), covering the whole screen.");
    }

    /// <summary>
    ///     Resolves --desktop-monitor's index into a concrete (x, y, width,
    ///     height) rect via GLFW's cross-platform monitor API (backed by
    ///     RandR on X11) -- same data --list-monitors prints. Done here
    ///     rather than in Program.cs because monitor enumeration needs GLFW
    ///     already initialized, which only happens once AppWindow's
    ///     constructor gets this far.
    /// </summary>
    private unsafe (int X, int Y, int Width, int Height) ResolveMonitorRect(int index)
    {
        var monitors = GLFWBind.GetMonitors();
        if (index < 0 || index >= monitors.Length)
            throw new InvalidOperationException(
                $"--desktop-monitor {index} is out of range -- {monitors.Length} monitor(s) detected " +
                "(run --list-monitors to see indices).");

        var monitor = monitors[index];
        GLFWBind.GetMonitorPos(monitor, out var mx, out var my);
        var mode = GLFWBind.GetVideoMode(monitor);
        if (mode is null)
            throw new InvalidOperationException(
                $"--desktop-monitor {index}: GLFW couldn't read that monitor's video mode.");

        return (mx, my, mode->Width, mode->Height);
    }

    private static void LogSelectedPlatform()
    {
        try
        {
            var platform = GLFWBind.GetPlatform();
            Log.Info($"GLFW selected platform: {platform}");
        }
        catch (EntryPointNotFoundException)
        {
            Log.Debug("GLFW build predates GetPlatform(); platform unknown at this log point.");
        }
    }

    public void Run()
    {
        // Debug-only: how many frames actually got rendered since the last
        // report, and a wall-clock stopwatch for "has a second gone by" --
        // both reset every report rather than free-running, so this is a
        // plain instantaneous-fps counter, not a smoothed/rolling average.
        var frameCount = 0;
        var fpsTimer = Stopwatch.StartNew();

        while (!GLFWBind.WindowShouldClose(_handle))
        {
            GLFWBind.PollEvents();

            // Render-thread-only work queued from other threads: live
            // control channel property changes (HTTP handler thread) and
            // shader hot-reload recompiles (FileSystemWatcher callback
            // thread) both funnel through here since this is the only
            // thread with the GL context current.
            _propertyStore.DrainPending(ApplyPropertyChange);
            // Applied after DrainPending so an enabled feed always wins over
            // a stale manual value from before it was turned on -- see
            // PropertyStore.ApplyFeeds's doc comment.
            _propertyStore.ApplyFeeds(ApplyPropertyChange);
            _module.ReloadIfDirty();

            _audioWindow.Pump(_audio);
            // ProcessToTexture: CpuFft computes on the CPU and uploads the
            // result same as before; GpuFft writes straight into _texL/_texR
            // from its compute shader, no CPU round trip (see IFft.ProcessToTexture).
            _fft.ProcessToTexture(_audioWindow.Snapshot, _texL, _texR);

            GLFWBind.GetFramebufferSize(_handle, out var fbWidth, out var fbHeight);
            if (fbWidth > 0 && fbHeight > 0) _module.Render(fbWidth, fbHeight, _texL.Handle, _texR.Handle, _fft.Bins);

            GLFWBind.SwapBuffers(_handle);

            frameCount++;
            var elapsed = fpsTimer.Elapsed.TotalSeconds;
            if (elapsed >= 1.0)
            {
                Log.Debug($"{frameCount / elapsed:F1} fps");
                frameCount = 0;
                fpsTimer.Restart();
            }
        }
    }
}