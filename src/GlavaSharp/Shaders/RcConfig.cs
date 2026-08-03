using System.IO;
using System.Text.RegularExpressions;

namespace GlavaSharp.Shaders;

/// <summary>
///     Minimal reader for GLava's rc.glsl config DSL. Only pulls out the
///     handful of `#request` keys this engine actually acts on (including
///     `setxwintype "desktop"`, see <see cref="Desktop" />); everything else
///     in rc.glsl (opacity modes, non-"desktop" window types, etc.) doesn't
///     apply to a Wayland/GL-window app and is ignored rather than ported.
/// </summary>
public sealed class RcConfig
{
    private static readonly Regex ReqMod = new("""#request\s+mod\s+(\S+)""");
    private static readonly Regex ReqGeom = new("""#request\s+setgeometry\s+(-?\d+)\s+(-?\d+)\s+(\d+)\s+(\d+)""");

    private static readonly Regex ReqTitle = new(
        """#request\s+settitle\s+"([^"]*)"""
    );

    private static readonly Regex ReqMirror = new("""#request\s+setmirror\s+(true|false)""");
    private static readonly Regex ReqBufSize = new("""#request\s+setbufsize\s+(\d+)""");
    private static readonly Regex ReqSampleRate = new("""#request\s+setsamplerate\s+(\d+)""");

    private static readonly Regex ReqXwinType = new(
        """#request\s+setxwintype\s+"([^"]*)"""
    );

    public string Module { get; private set; } = "bars";
    public int Width { get; private set; } = 800;
    public int Height { get; private set; } = 600;
    public string Title { get; private set; } = "GlavaSharp";
    public bool Mirror { get; private set; }

    /// <summary>
    ///     GLava's `setxwintype "desktop"` (e.g. shaders/glava/env_Xfwm4.glsl)
    ///     -- true if rc.glsl asked for desktop-embedded mode. Overridden by
    ///     the CLI's --desktop, which wins either way; this just makes the
    ///     WM-specific env_*.glsl files that already ship this directive
    ///     actually take effect. Every other window type ("dock", "panel",
    ///     etc.) is ignored -- only "desktop" has an implementation.
    /// </summary>
    public bool Desktop { get; private set; }

    /// <summary>
    ///     GLava's `setgeometry`'s x/y (window position) — unlike
    ///     <see cref="Width" />/<see cref="Height" />, GlavaSharp previously
    ///     parsed but discarded these. Used as the default desktop-mode
    ///     placement (see <see cref="Windowing.WindowOptions.DesktopX" />)
    ///     when `--desktop-geometry` isn't passed; only meaningful when
    ///     <see cref="HasGeometry" /> is true.
    /// </summary>
    public int GeomX { get; private set; }

    /// <summary>See <see cref="GeomX" />.</summary>
    public int GeomY { get; private set; }

    /// <summary>Whether rc.glsl actually had a `setgeometry` line at all.</summary>
    public bool HasGeometry { get; private set; }

    /// <summary>
    ///     GLava's `setbufsize` — the audio window/FFT size in samples.
    ///     Used as the default for <see cref="FftSettings.Size" /> when not
    ///     overridden on the CLI with --fft-size.
    /// </summary>
    public int BufSize { get; private set; } = 2048;

    /// <summary>
    ///     GLava's `setsamplerate` — used as the default capture sample
    ///     rate when not overridden on the CLI with --sample-rate.
    /// </summary>
    public int SampleRate { get; private set; } = 48_000;

    public static RcConfig Load(string rcPath)
    {
        var cfg = new RcConfig();
        var text = File.ReadAllText(rcPath);

        var mod = ReqMod.Match(text);
        if (mod.Success) cfg.Module = mod.Groups[1].Value;

        var geom = ReqGeom.Match(text);
        if (geom.Success)
        {
            cfg.GeomX = int.Parse(geom.Groups[1].Value);
            cfg.GeomY = int.Parse(geom.Groups[2].Value);
            cfg.Width = int.Parse(geom.Groups[3].Value);
            cfg.Height = int.Parse(geom.Groups[4].Value);
            cfg.HasGeometry = true;
        }

        var title = ReqTitle.Match(text);
        if (title.Success) cfg.Title = title.Groups[1].Value;

        var mirror = ReqMirror.Match(text);
        if (mirror.Success) cfg.Mirror = mirror.Groups[1].Value == "true";

        var bufSize = ReqBufSize.Match(text);
        if (bufSize.Success) cfg.BufSize = int.Parse(bufSize.Groups[1].Value);

        var sampleRate = ReqSampleRate.Match(text);
        if (sampleRate.Success) cfg.SampleRate = int.Parse(sampleRate.Groups[1].Value);

        var xwinType = ReqXwinType.Match(text);
        if (xwinType.Success) cfg.Desktop = xwinType.Groups[1].Value == "desktop";

        return cfg;
    }
}