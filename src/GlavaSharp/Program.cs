using System;
using System.IO;
using System.Linq;
using GlavaSharp;
using GlavaSharp.Audio;
using GlavaSharp.Shaders;
using GlavaSharp.Windowing;

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
if (args.Contains("--list-gpus"))
{
    var gpus = GpuEnumerator.List();
    if (gpus.Count == 0)
    {
        Console.WriteLine("No GPUs found via /sys/class/drm or lspci.");
    }
    else
    {
        Console.WriteLine("Available GPUs (use --gpu <index>):");
        for (var gi = 0; gi < gpus.Count; gi++)
            Console.WriteLine($"  [{gi}] {gpus[gi]}");
    }

    return;
}

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
    {
        // Default GPU (Intel)
        Environment.SetEnvironmentVariable("DRI_PRIME", null);
    }
    else
    {
        // First offload GPU
        Environment.SetEnvironmentVariable("DRI_PRIME", gidx.ToString());
    }
    // NVIDIA PRIME render offload (proprietary driver, hybrid laptops):
    // harmless no-op if you don't have an NVIDIA GPU at all.
    if (gidx > 0)
    {
        Environment.SetEnvironmentVariable("__NV_PRIME_RENDER_OFFLOAD", "1");
        Environment.SetEnvironmentVariable("__GLX_VENDOR_LIBRARY_NAME", "nvidia");
        Environment.SetEnvironmentVariable("__VK_LAYER_NV_optimus", "NVIDIA_only");
    }

    Console.WriteLine($"[GlavaSharp] Requesting GPU index {gidx} (DRI_PRIME={gidx}" +
                      (gidx > 0 ? ", NVIDIA prime-offload vars set" : "") + "). " +
                      "Check the 'GL: ... / <renderer>' line below to confirm which GPU actually got used. " +
                      "This also picks the GPU used by --fft-device gpu, since it shares the same GL context.");
}

if (args.Contains("--list-sinks") || args.Contains("--list-targets"))
{
    var targets = AudioTargetEnumerator.List();
    if (targets.Count == 0)
    {
        Console.WriteLine("No PipeWire audio sinks/sources found. Is pipewire running?");
    }
    else
    {
        Console.WriteLine("Available capture targets (use --sink <id>):");
        foreach (var t in targets) Console.WriteLine("  " + t);
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
            Console.Error.WriteLine($"No sink/source matching \"{val}\". Run --list-sinks to see options.");
            return;
        }

        targetId = match.Id;
        Console.WriteLine($"[GlavaSharp] Resolved \"{val}\" -> node id {targetId}");
    }
}

var shaderRootDir = GetArgValue(args, "--shaders")
                    ?? Path.Combine(AppContext.BaseDirectory, "shaders", "glava");
if (!Directory.Exists(shaderRootDir))
{
    Console.Error.WriteLine(
        $"Shader directory not found: {shaderRootDir}\n" +
        "Pass --shaders <path-to-glava-shaders-dir> (the one containing rc.glsl and bars/).");
    return;
}

var rcPath = Path.Combine(shaderRootDir, "rc.glsl");
var rc = File.Exists(rcPath) ? RcConfig.Load(rcPath) : new RcConfig();
var moduleName = GetArgValue(args, "--module") ?? rc.Module;

var fftSize = GetArgIntValue(args, "--fft-size") ?? NextPowerOfTwo(rc.BufSize);
if ((fftSize & (fftSize - 1)) != 0)
{
    Console.Error.WriteLine($"--fft-size must be a power of two, got {fftSize}.");
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
        Console.Error.WriteLine($"--fft-device must be \"cpu\" or \"gpu\", got \"{fftDeviceArg}\".");
        return;
}

if (fftDevice == FftDevice.Gpu && fftSize > 2048)
{
    Console.Error.WriteLine(
        $"--fft-device gpu requires --fft-size <= 2048 (single-workgroup limit), got {fftSize}.");
    return;
}

var sampleRate = GetArgIntValue(args, "--sample-rate") ?? rc.SampleRate;
var fftAttack = GetArgFloatValue(args, "--fft-attack") ?? 0.6f;
var fftDecay = GetArgFloatValue(args, "--fft-decay") ?? 0.08f;
var fftGain = GetArgFloatValue(args, "--fft-gain") ?? 40.0f;
var fftSettings = new FftSettings
{
    Size = fftSize,
    Attack = fftAttack,
    Decay = fftDecay,
    Gain = fftGain,
    Device = fftDevice
};
Console.WriteLine($"[GlavaSharp] FFT: device={fftDevice.ToString().ToLowerInvariant()}, " +
                  $"size={fftSize} (bins={fftSize / 2}), " +
                  $"attack={fftAttack}, decay={fftDecay}, gain={fftGain}, sampleRate={sampleRate}");

using var audio = new PipeWireAudioSource(sampleRate, targetId: targetId);
audio.Start();
Console.WriteLine(targetId < 0
    ? "[GlavaSharp] Capturing default sink's monitor (\"what you hear\")."
    : $"[GlavaSharp] Capturing PipeWire node id {targetId}.");

var options = new WindowOptions
{
    Title = rc.Title,
    Width = rc.Width,
    Height = rc.Height,
    // GLFW_PLATFORM_ANY lets GLFW auto-select Wayland when running inside a
    // Wayland session and fall back to X11 otherwise.
    Platform = PlatformPreference.Any,
    // GpuFft needs GL_ARB_compute_shader + SSBOs (core in 4.3). CpuFft has
    // no such requirement, so let it run on GLava's original 3.3 floor --
    // useful on older/lighter-weight GL implementations.
    GLMajor = fftDevice == FftDevice.Gpu ? 4 : 3,
    GLMinor = fftDevice == FftDevice.Gpu ? 3 : 3
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

static int NextPowerOfTwo(int n)
{
    if (n <= 1) return 1;
    var p = 1;
    while (p < n) p <<= 1;
    return p;
}