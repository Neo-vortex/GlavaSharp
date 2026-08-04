using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GlavaSharp.Shaders;

/// <summary>
///     A live-tweakable property a pass opts into via
///     <c>#request property "name" type default min max</c> — a
///     GlavaSharp-original extension, not a GLava directive. Declares a
///     uniform that starts at <paramref name="Default" /> like any other,
///     but can also be poked at runtime through the live control channel
///     (see <see cref="Control.PropertyStore" />/<see cref="ShaderModule" />).
///     Only <c>float</c> is implemented for now — <paramref name="Type" />
///     is captured for forward compat but everything currently flows through
///     as a GLSL <c>float</c>/<c>uniform float</c>.
/// </summary>
public sealed record PropertyDeclaration(string Name, string Type, float Default, float Min, float Max);

/// <summary>
///     Marks a previously-declared <see cref="PropertyDeclaration" /> as
///     eligible to be driven by a named built-in data source instead of
///     manual slider input, via <c>#request feed "name" source</c> — e.g.
///     <c>#request feed "seconds_since_midnight" clock</c> (see
///     <see cref="Control.FeedRegistry" /> for what source names exist).
///     Deliberately a separate line from <c>#request property</c> rather
///     than folded into it: the property declaration alone is already
///     complete and valid GLSL-adjacent metadata (a manually-tunable
///     uniform with a range) -- feed-eligibility is an orthogonal,
///     optional annotation on top, not a different kind of property. A
///     pass can in principle mark any property this way, not just
///     time-related ones.
/// </summary>
public sealed record FeedBinding(string PropertyName, string Source);

/// <summary>
///     Everything <see cref="GlavaPreprocessor.Process" /> collects while
///     preprocessing one pass: the resulting GLSL <paramref name="Source" />;
///     the role -> GLSL-identifier map from any `#request uniform "&lt;role&gt;"
///     &lt;name&gt;` lines (e.g. "prev" -> "tex") -- roles a pass doesn't declare
///     simply aren't in the dictionary, callers should fall back to GLava's
///     conventional default names; the <see cref="PropertyDeclaration" />s
///     and <see cref="FeedBinding" />s collected from any `#request
///     property`/`#request feed` lines; and the full set of absolute file
///     paths this pass pulled in via `#include` (entry file included) --
///     used by <see cref="ShaderModule" /> to know which files on disk
///     should trigger a recompile of this pass for hot-reload.
/// </summary>
public sealed record PreprocessResult(
    string Source,
    IReadOnlyDictionary<string, string> UniformBindings,
    IReadOnlyList<PropertyDeclaration> Properties,
    IReadOnlyList<FeedBinding> Feeds,
    IReadOnlySet<string> IncludedFiles);

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

    // GlavaSharp-original extension (see PropertyDeclaration) -- captured
    // before the generic RequestLine strip below removes it, same pattern
    // RequestUniform already uses.
    private static readonly Regex RequestProperty =
        new(@"^\s*#request\s+property\s+""(\w+)""\s+(\w+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s*$",
            RegexOptions.Multiline);

    // GlavaSharp-original extension (see FeedBinding) -- same capture-then-strip
    // pattern as RequestProperty above.
    private static readonly Regex RequestFeed =
        new(@"^\s*#request\s+feed\s+""(\w+)""\s+(\w+)\s*$", RegexOptions.Multiline);

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

    // `#request uniform "<role>" <glslName>` tells GLava's host which GLSL
    // identifier a pass used for a given semantic role -- e.g. every shipped
    // module names its previous-pass sampler2D differently in spirit (GLava
    // lets authors pick), though in practice the bundled tree always uses
    // "screen"/"audio_sz"/"audio_l"/"audio_r" for those roles and "tex" (not
    // "tex0") for "prev". Captured here (before RequestLine strips it) so
    // ShaderModule can bind by the name the shader actually declared instead
    // of a hardcoded guess.
    private static readonly Regex RequestUniform =
        new(@"^\s*#request\s+uniform\s+""(\w+)""\s+(\w+)\s*$", RegexOptions.Multiline);

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
    public static PreprocessResult Process(string entryFile, string moduleDir, string rootDir)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bindings = new Dictionary<string, string>();
        var properties = new List<PropertyDeclaration>();
        var feeds = new List<FeedBinding>();
        var source = ExpandIncludes(entryFile, moduleDir, rootDir, seen, 0, bindings, properties, feeds);
        return new PreprocessResult(source, bindings, properties, feeds, seen);
    }

    private static string ExpandIncludes(string file, string moduleDir, string rootDir, HashSet<string> seen,
        int depth, Dictionary<string, string> bindings, List<PropertyDeclaration> properties,
        List<FeedBinding> feeds)
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
            if (File.Exists(resolved))
                return ExpandIncludes(resolved, moduleDir, rootDir, seen, depth + 1, bindings, properties, feeds);
            // fall back to the other base, GLava is lenient about this
            var alt = Path.Combine(kind == "@" ? rootDir : moduleDir, rel);
            if (File.Exists(alt)) resolved = alt;
            return ExpandIncludes(resolved, moduleDir, rootDir, seen, depth + 1, bindings, properties, feeds);
        });

        return ProcessLeaf(text, bindings, properties, feeds);
    }

    /// <summary>Strips/rewrites directives that don't map onto plain GLSL.</summary>
    private static string ProcessLeaf(string text, Dictionary<string, string> bindings,
        List<PropertyDeclaration> properties, List<FeedBinding> feeds)
    {
        foreach (Match m in RequestUniform.Matches(text))
            bindings[m.Groups[1].Value] = m.Groups[2].Value;

        foreach (Match m in RequestProperty.Matches(text))
        {
            var name = m.Groups[1].Value;
            var type = m.Groups[2].Value;
            // Malformed numeric literals are a shader-authoring bug, not a
            // runtime condition -- fail loudly at load time rather than
            // silently registering a garbage 0/0/0 property.
            if (!float.TryParse(m.Groups[3].Value, out var def) ||
                !float.TryParse(m.Groups[4].Value, out var min) ||
                !float.TryParse(m.Groups[5].Value, out var max))
                throw new InvalidOperationException(
                    $"Malformed #request property \"{name}\" -- default/min/max must be numeric: {m.Value.Trim()}");
            properties.Add(new PropertyDeclaration(name, type, def, min, max));
        }

        foreach (Match m in RequestFeed.Matches(text))
            feeds.Add(new FeedBinding(m.Groups[1].Value, m.Groups[2].Value));

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