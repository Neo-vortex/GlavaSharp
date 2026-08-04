using System;
using System.Collections.Generic;

namespace GlavaSharp.Control;

/// <summary>
///     Named, built-in live data sources a shader property can opt into via
///     <c>#request feed "name" source</c> (see
///     <see cref="Shaders.FeedBinding" />) instead of being purely
///     manually-tunable. Deliberately a plain name -> sampler lookup, not a
///     plugin system -- there's exactly one source today (<c>clock</c>), and
///     new ones get added here directly rather than through some
///     registration API, the same way a new CLI flag just gets added to
///     Program.cs rather than going through a command-plugin framework.
/// </summary>
public static class FeedRegistry
{
    private static readonly Dictionary<string, Func<float>> Sources = new()
    {
        // Wall-clock time of day, in seconds since local midnight -- what
        // shaders/glavasharp/clock/2.frag drives its hour/minute/second
        // hands from. Local (not UTC): a desktop clock visualizer should
        // show the time on the wall, not in Greenwich.
        ["clock"] = () => (float)DateTime.Now.TimeOfDay.TotalSeconds
    };

    /// <summary>True if <paramref name="source" /> is a known feed name.</summary>
    public static bool Exists(string source) => Sources.ContainsKey(source);

    /// <summary>Render-thread only, called once per frame per enabled feed (see <see cref="PropertyStore" />/<see cref="Windowing.AppWindow.Run" />) -- these are cheap system reads, not I/O, so no caching is needed.</summary>
    public static bool TrySample(string source, out float value)
    {
        if (Sources.TryGetValue(source, out var sampler))
        {
            value = sampler();
            return true;
        }

        value = 0f;
        return false;
    }
}
