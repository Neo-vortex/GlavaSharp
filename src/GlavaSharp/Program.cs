using GlavaSharp;
using GlavaSharp.Audio;
using GlavaSharp.Shaders;
using GlavaSharp.Windowing;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.GraphicsLibraryFramework;
using GLFWBind = OpenTK.Windowing.GraphicsLibraryFramework.GLFW;

// --- CLI ---------------------------------------------------------------
// --list-sinks              enumerate PipeWire capture targets and exit
// --sink <id|name>          capture a specific target instead of the
//                           default sink's monitor
// --shaders <dir>           path to a GLava shader tree (a directory
//                           containing rc.glsl + module subdirs like
//                           bars/). Defaults to ./shaders/glava next to
//                           the executable.
// --module <name>           override the module GLava's rc.glsl requests
//                           (e.g. "bars"); defaults to whatever rc.glsl says.
// --list-gpus                enumerate GPUs (DRM render nodes) and exit
// --gpu <index>              force rendering onto a specific GPU (sets
//                           DRI_PRIME / NVIDIA prime-offload env vars
//                           *before* the GL context is created). Index
//                           matches the order from --list-gpus.
// --fft-size <n>             FFT/audio window size in samples, must be a
//                           power of two. Bigger = more frequency bins
//                           (Bins = n/2) and more "gravity" (slower to
//                           react), smaller = fewer bins but snappier.
//                           Defaults to rc.glsl's `setbufsize` if present
//                           (rounded up to a power of two), else 2048.
// --sample-rate <hz>         PipeWire capture sample rate. Defaults to
//                           rc.glsl's `setsamplerate` if present, else 48000.
// --fft-attack <0..1>        gravity smoothing: how fast bins rise on a
//                           louder reading. Default 0.6.
// --fft-decay <0..1>         gravity smoothing: how fast bins fall back
//                           down on a quieter reading. Default 0.08.
// --fft-gain <n>             log-compression contrast for bin magnitudes
//                           before display; higher = more contrast
//                           between quiet and loud bins. Default 40.
// --fft-device <cpu|gpu>     which FFT backend to run: "cpu" (default,
//                           works everywhere) or "gpu" (GLSL compute
//                           shader; requires a GL 4.3 context with
//                           compute-shader + SSBO support, and caps
//                           --fft-size at 2048 -- single workgroup).
//                           The GPU FFT runs on whichever GPU got
//                           selected via --gpu below.
// --freq-scale <name>        perceptual frequency scale raw FFT bins get
//                           bucketed into before any shader sees them:
//                           "log2" (default, octave spacing), "mel",
//                           "bark", "erb", or "linear" (no bucketing --
//                           GlavaSharp's original raw-bin behavior, left
//                           to each module's own smooth.glsl warp). Fixes
//                           bass reading as static/underused and treble as
//                           disproportionately "active" with raw linear
//                           bins, where most musical energy is crammed
//                           into a handful of the lowest bins.
// --desktop                  GLava's `-d` / `setxwintype "desktop"`: render
//                           pinned behind desktop icons via X11 EWMH hints
//                           instead of a normal top-level window. X11 only
//                           (forces --platform x11); also honored via
//                           rc.glsl's `#request setxwintype "desktop"` (e.g.
//                           shaders/glava/env_Xfwm4.glsl already requests
//                           it) when this flag isn't passed.
// --desktop-geometry X,Y,W,H  GLava's `setgeometry` equivalent for desktop
//                           mode: place/size the desktop-mode window at
//                           that exact rect instead of covering the whole
//                           screen. Falls back to rc.glsl's own
//                           `setgeometry` (if present) when --desktop is
//                           set and this flag isn't; only applies with
//                           --desktop / setxwintype "desktop".
// --list-monitors             enumerate connected monitors (index, name,
//                           position, resolution) and exit.
// --desktop-monitor <index>  desktop mode covers exactly this monitor
//                           (index from --list-monitors) instead of the
//                           whole virtual screen. Mutually exclusive with
//                           --desktop-geometry; only applies with
//                           --desktop / setxwintype "desktop".
// --log-level <level>        minimum severity to print: "debug", "info"
//                           (default), "warn", or "error". "debug" also
//                           turns on the per-second FPS line and the
//                           per-shader-pass compile chatter that's
//                           otherwise silent.
// --no-hot-reload             disable shader hot-reload -- by default,
//                           saving a change to any .frag/.glsl file the
//                           active module actually pulled in (via
//                           #include, transitively) recompiles just the
//                           affected pass(es) in place. A failed recompile
//                           logs an error and keeps the previous, still-
//                           working pass running rather than crashing.
// --no-control                disable the live control channel entirely
//                           (see --control-bind/--control-port below).
// --control-bind <host>      live control channel bind host. Defaults to
//                           127.0.0.1 (loopback only, no auth needed). Set
//                           to e.g. 0.0.0.0 for LAN access (a phone/tablet
//                           on the same network) -- there's no
//                           authentication, so only widen this on a
//                           network you trust; anyone who can reach
//                           host:port can change any registered property.
// --control-port <n>         live control channel port. Default 8642.
//                           Running multiple GlavaSharp instances at once
//                           needs a distinct port per instance -- a bind
//                           failure (e.g. the port's already taken) just
//                           disables the control channel for that
//                           instance with a logged warning, it isn't fatal.
//                           Open http://<host>:<port>/ in a browser: every
//                           registered property (the fft.attack/decay/gain
//                           globals, plus whatever the active module
//                           declared via #request property) shows up as a
//                           slider there, live.
// --benchmark-fft             time IFft.Process() across a few window sizes
//                           and exit -- no window, no visualization, just a
//                           ms/call, calls/sec, checksum table on stdout.
//                           Respects --fft-device cpu|gpu (GPU creates a
//                           hidden, never-shown GL context just for this --
//                           still no visible window) and --fft-attack/
//                           -decay/-gain/--sample-rate; ignores --fft-size
//                           itself (sweeps its own fixed list) and skips any
//                           GPU size that would exceed this GPU's actual
//                           compute workgroup limit rather than risk it. For
//                           CpuFft, compare against the scalar fallback on
//                           the same hardware with DOTNET_EnableAVX2=0 in
//                           the environment (see the docs site's Benchmarks
//                           page for reference numbers).

var logLevelArg = GetArgValue(args, "--log-level") ?? "info";
switch (logLevelArg.Trim().ToLowerInvariant())
{
    case "debug":
        Log.MinLevel = LogLevel.Debug;
        break;
    case "info":
        Log.MinLevel = LogLevel.Info;
        break;
    case "warn":
        Log.MinLevel = LogLevel.Warn;
        break;
    case "error":
        Log.MinLevel = LogLevel.Error;
        break;
    default:
        Log.Error(
            $"--log-level must be \"debug\", \"info\", \"warn\", or \"error\", got \"{logLevelArg}\".");
        return;
}

if (args.Contains("--list-monitors"))
{
    if (!GLFWBind.Init())
    {
        Log.Error("GLFW initialization failed -- can't enumerate monitors.");
        return;
    }

    try
    {
        unsafe
        {
            var monitors = GLFWBind.GetMonitors();
            if (monitors.Length == 0)
            {
                Log.Info("No monitors found.");
            }
            else
            {
                Log.Info("Available monitors (use --desktop-monitor <index>):");
                for (var mi = 0; mi < monitors.Length; mi++)
                {
                    var mon = monitors[mi];
                    GLFWBind.GetMonitorPos(mon, out var mx, out var my);
                    var mode = GLFWBind.GetVideoMode(mon);
                    var name = GLFWBind.GetMonitorName(mon);
                    Log.Info(mode is null
                        ? $"  [{mi}] {name} @ ({mx},{my})"
                        : $"  [{mi}] {name}  {mode->Width}x{mode->Height}  at ({mx},{my})");
                }
            }
        }
    }
    finally
    {
        GLFWBind.Terminate();
    }

    return;
}

if (args.Contains("--list-gpus"))
{
    var gpus = GpuEnumerator.List();
    if (gpus.Count == 0)
    {
        Log.Info("No GPUs found via /sys/class/drm or lspci.");
    }
    else
    {
        Log.Info("Available GPUs (use --gpu <index>):");
        for (var gi = 0; gi < gpus.Count; gi++)
            Log.Info($"  [{gi}] {gpus[gi]}");
    }

    return;
}

// Checked here (early) but only acted on once FftSettings is fully parsed,
// further down -- benchmark mode ignores --fft-size itself (it sweeps its
// own fixed list of sizes across both CPU and GPU) and needs no real audio
// capture or window, so the --fft-size power-of-two / GPU-size-cap checks
// below are skipped in this mode too.
var benchmarkFft = args.Contains("--benchmark-fft");

int? gpuIndex = null;
{
    var gpuArgIndex = Array.IndexOf(args, "--gpu");
    if (gpuArgIndex >= 0 && gpuArgIndex + 1 < args.Length && int.TryParse(args[gpuArgIndex + 1], out var parsedGpu))
        gpuIndex = parsedGpu;
}
if (gpuIndex is { } gidx)
{
    // Must happen before ANY GL/EGL/GLX call in this process -- the driver
    // reads these once, when it first opens the DRI/EGL device, which for
    // us is inside AppWindow's constructor (GLFW window/context creation).
    // Mesa multi-GPU (e.g. Intel iGPU + AMD/Intel dGPU): DRI_PRIME picks
    // the render node by index.
    if (gidx == 0)
        // Default GPU (Intel)
        Environment.SetEnvironmentVariable("DRI_PRIME", null);
    else
        // First offload GPU
        Environment.SetEnvironmentVariable("DRI_PRIME", gidx.ToString());
    // NVIDIA PRIME render offload (proprietary driver, hybrid laptops):
    // harmless no-op if you don't have an NVIDIA GPU at all.
    if (gidx > 0)
    {
        Environment.SetEnvironmentVariable("__NV_PRIME_RENDER_OFFLOAD", "1");
        Environment.SetEnvironmentVariable("__GLX_VENDOR_LIBRARY_NAME", "nvidia");
        Environment.SetEnvironmentVariable("__VK_LAYER_NV_optimus", "NVIDIA_only");
    }

    Log.Info($"Requesting GPU index {gidx} (DRI_PRIME={gidx}" +
                      (gidx > 0 ? ", NVIDIA prime-offload vars set" : "") + "). " +
                      "Check the 'GL: ... / <renderer>' line below to confirm which GPU actually got used. " +
                      "This also picks the GPU used by --fft-device gpu, since it shares the same GL context.");
}

if (args.Contains("--list-sinks") || args.Contains("--list-targets"))
{
    var targets = AudioTargetEnumerator.List();
    if (targets.Count == 0)
    {
        Log.Info("No PipeWire audio sinks/sources found. Is pipewire running?");
    }
    else
    {
        Log.Info("Available capture targets (use --sink <id>):");
        foreach (var t in targets) Log.Info("  " + t);
    }

    return;
}

var targetId = -1;
var sinkArgIndex = Array.IndexOf(args, "--sink");
if (sinkArgIndex >= 0 && sinkArgIndex + 1 < args.Length)
{
    var val = args[sinkArgIndex + 1];
    if (int.TryParse(val, out var id))
    {
        targetId = id;
    }
    else
    {
        // Resolve by name/description against a fresh scan.
        var match = AudioTargetEnumerator.List()
            .FirstOrDefault(t => t.Name.Equals(val, StringComparison.OrdinalIgnoreCase)
                                 || t.Description.Equals(val, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Log.Error($"No sink/source matching \"{val}\". Run --list-sinks to see options.");
            return;
        }

        targetId = match.Id;
        Log.Info($"Resolved \"{val}\" -> node id {targetId}");
    }
}

var shaderRootDir = GetArgValue(args, "--shaders")
                    ?? Path.Combine(AppContext.BaseDirectory, "shaders", "glava");
if (!Directory.Exists(shaderRootDir))
{
    Log.Error(
        $"Shader directory not found: {shaderRootDir}\n" +
        "Pass --shaders <path-to-glava-shaders-dir> (the one containing rc.glsl and bars/).");
    return;
}

var rcPath = Path.Combine(shaderRootDir, "rc.glsl");
var rc = File.Exists(rcPath) ? RcConfig.Load(rcPath) : new RcConfig();
var moduleName = GetArgValue(args, "--module") ?? rc.Module;
var desktopMode = args.Contains("--desktop") || rc.Desktop;

// Only an explicit --desktop-geometry constrains desktop mode -- rc.glsl's
// own setgeometry is deliberately NOT used as a fallback here, even though
// RcConfig parses it (rc.GeomX/Y + Width/Height): GLava's stock rc.glsl
// ships `#request setgeometry 0 0 800 600` unconditionally as the default
// *windowed*-mode size, so treating "rc.glsl has a setgeometry line" as
// "the user wants desktop mode constrained to that rect" made --desktop
// silently shrink to 800x600 on literally every rc.glsl that hadn't been
// hand-edited to remove it -- the opposite of --desktop's own "cover the
// whole screen by default" contract. Null all around means "cover the
// whole screen" -- x11shim's own default.
int? desktopX = null, desktopY = null, desktopWidth = null, desktopHeight = null;
var desktopGeomArg = GetArgValue(args, "--desktop-geometry");
if (desktopGeomArg is not null)
{
    var parts = desktopGeomArg.Split(',');
    if (parts.Length != 4
        || !int.TryParse(parts[0], out var gx) || !int.TryParse(parts[1], out var gy)
        || !int.TryParse(parts[2], out var gw) || !int.TryParse(parts[3], out var gh)
        || gw <= 0 || gh <= 0)
    {
        Log.Error(
            $"--desktop-geometry must be \"X,Y,W,H\" with positive W/H, got \"{desktopGeomArg}\".");
        return;
    }

    (desktopX, desktopY, desktopWidth, desktopHeight) = (gx, gy, gw, gh);
}

int? desktopMonitorIndex = null;
var desktopMonitorArg = GetArgValue(args, "--desktop-monitor");
if (desktopMonitorArg is not null)
{
    if (desktopGeomArg is not null)
    {
        Log.Error("--desktop-monitor and --desktop-geometry are mutually exclusive -- pick one.");
        return;
    }

    if (!int.TryParse(desktopMonitorArg, out var monIdx) || monIdx < 0)
    {
        Log.Error($"--desktop-monitor must be a non-negative index, got \"{desktopMonitorArg}\".");
        return;
    }

    desktopMonitorIndex = monIdx;
}

var fftSize = GetArgIntValue(args, "--fft-size") ?? NextPowerOfTwo(rc.BufSize);
if (!benchmarkFft && (fftSize & (fftSize - 1)) != 0)
{
    Log.Error($"--fft-size must be a power of two, got {fftSize}.");
    return;
}

var fftDeviceArg = GetArgValue(args, "--fft-device") ?? "cpu";
FftDevice fftDevice;
switch (fftDeviceArg.Trim().ToLowerInvariant())
{
    case "cpu":
        fftDevice = FftDevice.Cpu;
        break;
    case "gpu":
        fftDevice = FftDevice.Gpu;
        break;
    default:
        Log.Error($"--fft-device must be \"cpu\" or \"gpu\", got \"{fftDeviceArg}\".");
        return;
}

if (!benchmarkFft && fftDevice == FftDevice.Gpu && fftSize > 2048)
{
    Log.Error(
        $"--fft-device gpu requires --fft-size <= 2048 (single-workgroup limit), got {fftSize}.");
    return;
}

var sampleRate = GetArgIntValue(args, "--sample-rate") ?? rc.SampleRate;
var fftAttack = GetArgFloatValue(args, "--fft-attack") ?? 0.6f;
var fftDecay = GetArgFloatValue(args, "--fft-decay") ?? 0.08f;
var fftGain = GetArgFloatValue(args, "--fft-gain") ?? 40.0f;

var freqScaleArg = GetArgValue(args, "--freq-scale") ?? "log2";
FrequencyScale freqScale;
switch (freqScaleArg.Trim().ToLowerInvariant())
{
    case "linear":
        freqScale = FrequencyScale.Linear;
        break;
    case "log2":
        freqScale = FrequencyScale.Log2;
        break;
    case "mel":
        freqScale = FrequencyScale.Mel;
        break;
    case "bark":
        freqScale = FrequencyScale.Bark;
        break;
    case "erb":
        freqScale = FrequencyScale.Erb;
        break;
    default:
        Log.Error(
            $"--freq-scale must be \"linear\", \"log2\", \"mel\", \"bark\", or \"erb\", got \"{freqScaleArg}\".");
        return;
}

var fftSettings = new FftSettings
{
    Size = fftSize,
    Attack = fftAttack,
    Decay = fftDecay,
    Gain = fftGain,
    SampleRate = sampleRate,
    Scale = freqScale,
    Device = fftDevice
};

if (benchmarkFft)
{
    RunFftBenchmark(fftSettings);
    return;
}

Log.Info($"FFT: device={fftDevice.ToString().ToLowerInvariant()}, " +
                  $"size={fftSize} (bins={fftSize / 2}), " +
                  $"attack={fftAttack}, decay={fftDecay}, gain={fftGain}, sampleRate={sampleRate}, " +
                  $"freq-scale={freqScale.ToString().ToLowerInvariant()}");

using var audio = new PipeWireAudioSource(sampleRate, targetId: targetId);
audio.Start();
Log.Info(targetId < 0
    ? "Capturing default sink's monitor (\"what you hear\")."
    : $"Capturing PipeWire node id {targetId}.");

if (desktopMode)
    Log.Info("Desktop mode requested (X11 only): forcing --platform x11.");

var options = new WindowOptions
{
    Title = rc.Title,
    Width = rc.Width,
    Height = rc.Height,
    // GLFW_PLATFORM_ANY lets GLFW auto-select Wayland when running inside a
    // Wayland session and fall back to X11 otherwise -- except under
    // --desktop, which has no Wayland implementation yet and needs X11
    // forced so AppWindow doesn't silently end up on Wayland and no-op.
    Platform = desktopMode ? PlatformPreference.X11 : PlatformPreference.Any,
    // GpuFft needs GL_ARB_compute_shader + SSBOs (core in 4.3). CpuFft has
    // no such requirement, so let it run on GLava's original 3.3 floor --
    // useful on older/lighter-weight GL implementations.
    GLMajor = fftDevice == FftDevice.Gpu ? 4 : 3,
    GLMinor = 3,
    DesktopMode = desktopMode,
    DesktopX = desktopX,
    DesktopY = desktopY,
    DesktopWidth = desktopWidth,
    DesktopHeight = desktopHeight,
    DesktopMonitorIndex = desktopMonitorIndex,
    ControlEnabled = !args.Contains("--no-control"),
    ControlBindHost = GetArgValue(args, "--control-bind") ?? "127.0.0.1",
    ControlPort = GetArgIntValue(args, "--control-port") ?? 8642,
    HotReloadEnabled = !args.Contains("--no-hot-reload")
};

using var window = new AppWindow(options, audio, shaderRootDir, moduleName, fftSettings);
window.Run();
return;

static string? GetArgValue(string[] args, string flag)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int? GetArgIntValue(string[] args, string flag)
{
    var v = GetArgValue(args, flag);
    return v is not null && int.TryParse(v, out var parsed) ? parsed : null;
}

static float? GetArgFloatValue(string[] args, string flag)
{
    var v = GetArgValue(args, flag);
    return v is not null && float.TryParse(v, out var parsed) ? parsed : null;
}

// --benchmark-fft: times IFft.Process() (CpuFft or GpuFft, per
// --fft-device) across a representative spread of window sizes, no window/
// visualization -- reports ms/call, calls/sec, and a checksum of the
// returned spectrum straight to the console. The checksum isn't for
// anything at runtime, it's so two separate runs (e.g. CpuFft with the
// AVX2+FMA path active vs disabled via DOTNET_EnableAVX2=0, or CPU vs GPU)
// can be diffed to confirm the code paths actually agree, not just that
// one of them is faster. Fixed RNG seed and Scale=Linear (skips
// FrequencyBucketing entirely) so runs are directly comparable and only
// the FFT backend's own math differs between them.
//
// GPU needs a real GL context (compute shaders don't exist without one),
// but not a visible one -- WindowHintBool.Visible false gets a normal GLFW
// window/context pair that just never gets shown or rendered to, which is
// all IFft.Process() ever touches on the GPU path anyway (upload -> dispatch
// -> readback, no framebuffer involved).
static unsafe void RunFftBenchmark(FftSettings baseSettings)
{
    int[] sizes = [1024, 2048, 4096, 8192];
    const int warmupIters = 200; // also long enough for ApplyGravity's attack/decay state to converge on this constant input
    const int timedIters = 2000;
    var rng = new Random(12345);
    var device = baseSettings.Device;

    Window* hiddenWindow = null;
    var maxWorkGroupInvocations = int.MaxValue;

    if (device == FftDevice.Gpu)
    {
        if (!GLFWBind.Init())
        {
            Log.Error("GLFW initialization failed -- can't create a GL context to benchmark the GPU FFT.");
            return;
        }

        GLFWBind.WindowHint(WindowHintBool.Visible, false);
        GLFWBind.WindowHint(WindowHintInt.ContextVersionMajor, 4);
        GLFWBind.WindowHint(WindowHintInt.ContextVersionMinor, 3);
        GLFWBind.WindowHint(WindowHintOpenGlProfile.OpenGlProfile, OpenGlProfile.Core);
        GLFWBind.WindowHint(WindowHintBool.OpenGLForwardCompat, true);
        hiddenWindow = GLFWBind.CreateWindow(1, 1, "GlavaSharp FFT benchmark (hidden)", null, null);
        if (hiddenWindow == null)
        {
            Log.Error(
                "GL context creation failed -- can't benchmark the GPU FFT (needs a GL 4.3 context with compute shaders).");
            GLFWBind.Terminate();
            return;
        }

        GLFWBind.MakeContextCurrent(hiddenWindow);
        GL.LoadBindings(new GLFWBindingsContext());
        Log.Info($"GL: {GL.GetString(StringName.Version)} / {GL.GetString(StringName.Renderer)}");

        // GpuFft dispatches a single workgroup of N/2 invocations -- sizes
        // whose N/2 exceeds what this GPU actually allows get skipped below
        // rather than attempted, since a compute shader that violates this
        // limit is exactly the class of misconfiguration that's hung
        // glCompileShader/glLinkProgram with no error on some driver paths
        // (see the GpuFft bring-up notes on the docs site) rather than
        // failing cleanly.
        maxWorkGroupInvocations = GL.GetInteger(GetPName.MaxComputeWorkGroupInvocations);
        Log.Debug($"GL_MAX_COMPUTE_WORK_GROUP_INVOCATIONS = {maxWorkGroupInvocations}");
    }

    try
    {
        Log.Info($"FFT benchmark ({device}): warmup={warmupIters}, timed={timedIters} iterations/size");
        Log.Info($"{"size",-6} {"ms/call",-9} {"calls/sec",-10} checksum");

        foreach (var size in sizes)
        {
            if (device == FftDevice.Gpu && size / 2 > maxWorkGroupInvocations)
            {
                Log.Info(
                    $"{size,-6} skipped (needs {size / 2} compute invocations, this GPU allows {maxWorkGroupInvocations})");
                continue;
            }

            var settings = new FftSettings
            {
                Size = size,
                Attack = baseSettings.Attack,
                Decay = baseSettings.Decay,
                Gain = baseSettings.Gain,
                SampleRate = baseSettings.SampleRate,
                Scale = FrequencyScale.Linear,
                Device = device
            };

            IFft fft;
            try
            {
                fft = device == FftDevice.Gpu ? new GpuFft(settings) : new CpuFft(settings);
            }
            catch (ArgumentException ex)
            {
                Log.Info($"{size,-6} skipped ({ex.Message})");
                continue;
            }

            try
            {
                // Interleaved stereo, 2x the window length so Process()'s
                // `take` (min(N, available)) is always the full N -- same
                // shape a real ring buffer presents once it's past the
                // initial fill.
                var samples = new float[size * 2 * 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = MathF.Sin(i * 0.01f) * 0.3f + MathF.Sin(i * 0.037f) * 0.2f +
                                 ((float)rng.NextDouble() - 0.5f) * 0.05f;

                for (var i = 0; i < warmupIters; i++) fft.Process(samples);

                var result = fft.Process(samples);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < timedIters; i++) result = fft.Process(samples);
                sw.Stop();

                var msPerCall = sw.Elapsed.TotalMilliseconds / timedIters;
                var callsPerSec = timedIters / sw.Elapsed.TotalSeconds;
                var checksum = 0.0;
                foreach (var v in result.left) checksum += v;
                foreach (var v in result.right) checksum += v;

                Log.Info($"{size,-6} {msPerCall,-9:F4} {callsPerSec,-10:F0} {checksum:F6}");

                // ProcessToTexture is a second code path (see IFft.ProcessToTexture)
                // that has to land on the same numbers as Process() above --
                // GpuFft does bucketing/gravity on the GPU via
                // shaders/fft/post.comp instead of the CPU there, so this is
                // the only thing that actually exercises/validates that
                // kernel. GPU-only: it's the only device with a live GL
                // context here (see the `device == FftDevice.Gpu` check
                // above), and CpuFft's ProcessToTexture is just Process()
                // followed by an upload, nothing new to cross-check. Read
                // the textures back purely for this checksum; the whole
                // point of ProcessToTexture is that real callers never do
                // that.
                //
                // ProcessToTexture keeps its own gravity state (the GPU-side
                // SSBO in GpuFft, separate from the CPU _smoothL/_smoothR
                // Process() just converged above over warmupIters+timedIters
                // calls) -- it needs the same warmup on the same constant
                // input before comparing, or this "mismatch" is just two
                // different points on the same attack/decay convergence
                // curve, not an actual bug.
                if (device == FftDevice.Gpu)
                {
                    using var texL = new AudioSpectrumTexture(fft.Bins);
                    using var texR = new AudioSpectrumTexture(fft.Bins);
                    for (var i = 0; i < warmupIters + timedIters; i++) fft.ProcessToTexture(samples, texL, texR);
                    var texChecksum = 0.0;
                    foreach (var tex in new[] { texL, texR })
                    {
                        var buf = new float[tex.Size];
                        GL.BindTexture(TextureTarget.Texture1D, tex.Handle);
                        GL.GetTexImage(TextureTarget.Texture1D, 0, PixelFormat.Red, PixelType.Float, buf);
                        GL.BindTexture(TextureTarget.Texture1D, 0);
                        foreach (var v in buf) texChecksum += v;
                    }

                    var delta = Math.Abs(texChecksum - checksum);
                    if (delta > 0.01)
                        Log.Error(
                            $"{size,-6} ProcessToTexture checksum mismatch: {texChecksum:F6} vs Process() {checksum:F6} (delta {delta:F6})");
                    else
                        Log.Debug($"{size,-6} ProcessToTexture checksum OK: {texChecksum:F6} (delta {delta:F6})");
                }
            }
            finally
            {
                fft.Dispose();
            }
        }
    }
    finally
    {
        if (hiddenWindow != null)
        {
            GLFWBind.DestroyWindow(hiddenWindow);
            GLFWBind.Terminate();
        }
    }
}

static int NextPowerOfTwo(int n)
{
    if (n <= 1) return 1;
    var p = 1;
    while (p < n) p <<= 1;
    return p;
}