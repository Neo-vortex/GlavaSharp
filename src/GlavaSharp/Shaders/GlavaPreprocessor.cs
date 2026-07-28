using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GlavaSharp.Shaders;

/// <summary>
///     A deliberately small subset of GLava's own shader preprocessor —
///     enough to load real GLava module files (rc.glsl, bars.glsl, bars/*.frag,
///     util/*.glsl) as plain GLSL. What it does NOT do: evaluate the
///     `#request transform ...` pipeline (windowing/fft/gravity/avg are
///     implemented natively in <see cref="GpuFft" /> instead of as chained
///     GLava transform shaders), or the full `@fg:`/`@bg:` foreground/background
///     compositing model (we just draw the resulting color with normal alpha
///     blending). Everything else — #include resolution, #request stripping,
///     hex-color literals, #expand — behaves the way GLava's shaders expect.
/// </summary>
public static class GlavaPreprocessor
{
    private static readonly Regex RequestLine = new(@"^\s*#request\b.*$", RegexOptions.Multiline);

    // A handful of `#request set<name> <value>` directives don't just
    // configure the C host (window geometry, title, etc. — safely dropped
    // by the generic RequestLine strip below) — their value is read back
    // as a plain GLSL identifier by shipped shaders (util/smooth.glsl's
    // _SMOOTH_FACTOR / _PRE_SMOOTHED_AUDIO). Real GLava's preprocessor
    // turns these into #defines before compiling; we only implement the
    // ones actually referenced by module fragment shaders we compile.
    private static readonly Regex RequestSetSmoothFactor =
        new(@"^\s*#request\s+setsmoothfactor\s+([0-9.]+)\s*$", RegexOptions.Multiline);

    private static readonly Regex RequestSetSmoothPass =
        new(@"^\s*#request\s+setsmoothpass\s+(true|false)\s*$", RegexOptions.Multiline);

    private static readonly Regex IncludeLine = new("""^\s*#include\s*"([@:]?)([^"]+)"\s*$""", RegexOptions.Multiline);
    private static readonly Regex ExpandLine = new(@"^\s*#expand\s+(\w+)\s+(\w+)\s*$", RegexOptions.Multiline);
    private static readonly Regex HexColor = new(@"#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})\b");

    private static readonly Regex FgBgTag = new(@"@(fg|bg):");

    // gl_FragCoord is already an implicit `in vec4` in core-profile GLSL;
    // GLava's own shaders redeclare it (legacy compat with older GLSL
    // versions), which some strict drivers reject. Safe to drop.
    private static readonly Regex RedeclareFragCoord =
        new(@"^\s*in\s+vec4\s+gl_FragCoord\s*;\s*$", RegexOptions.Multiline);

    /// <param name="entryFile">Absolute path to the .frag/.glsl file to preprocess.</param>
    /// <param name="moduleDir">Directory of the active module (e.g. .../glava/bars) — resolves "@x" includes.</param>
    /// <param name="rootDir">Shader root (e.g. .../glava) — resolves ":x" includes.</param>
    public static string Process(string entryFile, string moduleDir, string rootDir)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ExpandIncludes(entryFile, moduleDir, rootDir, seen, 0);
    }

    private static string ExpandIncludes(string file, string moduleDir, string rootDir, HashSet<string> seen, int depth)
    {
        if (depth > 32) throw new InvalidOperationException($"#include recursion too deep starting at {file}");
        var full = Path.GetFullPath(file);
        // GLava treats "@x" and ":x" as the same logical include once resolved
        // to a real path (bars.glsl is #included both ways in bars/1.frag) —
        // dedupe like a #pragma once so re-including doesn't redefine macros.
        if (!seen.Add(full))
            return string.Empty;

        var text = File.ReadAllText(full);

        text = IncludeLine.Replace(text, m =>
        {
            var kind = m.Groups[1].Value; // "@", ":" or "" (bare relative)
            var rel = m.Groups[2].Value;
            var baseDir = kind == "@" ? moduleDir : rootDir;
            var resolved = Path.Combine(baseDir, rel);
            if (File.Exists(resolved)) return ExpandIncludes(resolved, moduleDir, rootDir, seen, depth + 1);
            // fall back to the other base, GLava is lenient about this
            var alt = Path.Combine(kind == "@" ? rootDir : moduleDir, rel);
            if (File.Exists(alt)) resolved = alt;
            return ExpandIncludes(resolved, moduleDir, rootDir, seen, depth + 1);
        });

        return ProcessLeaf(text);
    }

    /// <summary>Strips/rewrites directives that don't map onto plain GLSL.</summary>
    private static string ProcessLeaf(string text)
    {
        text = RequestSetSmoothFactor.Replace(text, m => $"#define _SMOOTH_FACTOR {m.Groups[1].Value}");
        text = RequestSetSmoothPass.Replace(text,
            m => $"#define _PRE_SMOOTHED_AUDIO {(m.Groups[1].Value == "true" ? 1 : 0)}");
        text = RequestLine.Replace(text, "");

        // #expand NAME COUNT -> NAME(0) NAME(1) ... NAME(COUNT-1), one per line.
        // COUNT must already be a literal integer by the time we see it (GLava
        // resolves the same way — it's meant to pair with a preceding #define
        // of an integer constant, which real GLSL #define handles for us; we
        // just need the literal here since we're not running a full C
        // preprocessor ourselves).
        text = ExpandLine.Replace(text, m =>
        {
            var macro = m.Groups[1].Value;
            var countTok = m.Groups[2].Value;
            if (!int.TryParse(countTok, out var count))
                return m.Value; // leave alone; not resolvable without a real macro evaluator
            var sb = new StringBuilder();
            for (var i = 0; i < count; i++)
                sb.Append(macro).Append('(').Append(i).Append(")\n");
            return sb.ToString();
        });

        // #RRGGBB[AA] -> vec4(r,g,b,a) literals (GLava's `@fg:`/`@bg:` color macros use these)
        text = HexColor.Replace(text, m =>
        {
            var hex = m.Groups[1].Value;
            var r = Convert.ToInt32(hex[..2], 16) / 255f;
            var g = Convert.ToInt32(hex[2..4], 16) / 255f;
            var b = Convert.ToInt32(hex[4..6], 16) / 255f;
            var a = hex.Length == 8 ? Convert.ToInt32(hex[6..8], 16) / 255f : 1f;
            return $"vec4({r:0.####}, {g:0.####}, {b:0.####}, {a:0.####})";
        });

        // Strip the foreground/background compositing tag; we don't implement
        // GLava's separate fg/bg compositing pass, just draw the color.
        text = FgBgTag.Replace(text, "");
        text = RedeclareFragCoord.Replace(text, "");

        return text;
    }
}