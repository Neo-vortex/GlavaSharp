using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace GlavaSharp.Control;

/// <summary>
///     Registry for every live-tweakable property the control channel (see
///     <see cref="ControlServer" />) can list/set -- both the global FFT
///     knobs (<c>fft.attack</c>/<c>fft.decay</c>/<c>fft.gain</c>, registered
///     by <see cref="Windowing.AppWindow" />) and whatever a module declared
///     via <c>#request property</c> (registered by
///     <see cref="Shaders.ShaderModule" />, namespaced as <c>module.&lt;name&gt;</c>).
///
///     <see cref="TrySet" /> runs on the HTTP server's own thread and never
///     touches OpenGL -- it only queues the change. <see cref="DrainPending" />
///     must be called once per frame from the render thread (the only thread
///     allowed to make GL calls), which is where a queued change actually
///     reaches <c>IFft.SetAttack</c>/<c>ShaderModule.SetProperty</c>/etc. via
///     the caller-supplied <c>apply</c> callback.
///
///     A property declared with a <c>#request feed</c> binding (see
///     <see cref="Shaders.FeedBinding" />/<see cref="FeedRegistry" />) also
///     carries a <see cref="Descriptor.FeedSource" /> and a mutable
///     feed-enabled flag (on by default -- a clock module with its time feed
///     off at startup would just show a frozen hand, which is never what you
///     want). While enabled, <see cref="ApplyFeeds" /> overrides manual
///     slider input every frame with whatever <see cref="FeedRegistry" />
///     reports for that source.
/// </summary>
public sealed class PropertyStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Descriptor> _descriptors = new();
    private readonly Dictionary<string, float> _current = new();
    private readonly Dictionary<string, bool> _feedEnabled = new();

    // Coalescing, not queuing: if the same property gets set multiple times
    // before the render thread's next DrainPending (e.g. a browser slider
    // firing on every "input" event), only the latest value survives --
    // there's no reason to apply five stale intermediate values one frame
    // after the fact.
    private readonly ConcurrentDictionary<string, float> _pending = new();

    public void Register(string name, string category, float min, float max, float defaultValue,
        string? feedSource = null)
    {
        lock (_lock)
        {
            _descriptors[name] = new Descriptor(name, category, min, max, defaultValue, feedSource);
            _current[name] = defaultValue;
            // On by default: a feed-eligible property with its feed off at
            // load time (e.g. a clock's hands) would just sit frozen at the
            // shader's own #request property default until someone finds
            // the checkbox -- not a useful first impression.
            if (feedSource != null) _feedEnabled[name] = true;
        }
    }

    public IReadOnlyList<Descriptor> Descriptors
    {
        get
        {
            lock (_lock)
            {
                return _descriptors.Values.OrderBy(d => d.Category, StringComparer.Ordinal)
                    .ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>Current value snapshot, keyed by property name -- what the control page's GET reflects.</summary>
    public IReadOnlyDictionary<string, float> CurrentValues
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, float>(_current);
            }
        }
    }

    /// <summary>Whether <paramref name="name" />'s feed (if it has one) is currently driving its value. False for a property with no feed source at all.</summary>
    public bool IsFeedEnabled(string name)
    {
        lock (_lock)
        {
            return _feedEnabled.GetValueOrDefault(name);
        }
    }

    /// <summary>
    ///     Toggles a property's feed on/off. Safe to call from any thread
    ///     (the control channel's HTTP handler) -- this only flips a flag;
    ///     <see cref="ApplyFeeds" /> is what actually samples/applies it, on
    ///     the render thread, on the next frame.
    /// </summary>
    public bool TrySetFeedEnabled(string name, bool enabled, out string? error)
    {
        lock (_lock)
        {
            if (!_descriptors.TryGetValue(name, out var descriptor))
            {
                error = $"unknown property \"{name}\"";
                return false;
            }

            if (descriptor.FeedSource is null)
            {
                error = $"\"{name}\" has no feed source to toggle";
                return false;
            }

            _feedEnabled[name] = enabled;
        }

        error = null;
        return true;
    }

    /// <summary>
    ///     Validates and queues a property change. Safe to call from any
    ///     thread (this is what <see cref="ControlServer" />'s HTTP handler
    ///     calls) -- never touches GL, the actual effect is applied later by
    ///     <see cref="DrainPending" /> on the render thread. Note that a
    ///     manual set on a property whose feed is currently enabled has no
    ///     visible effect -- <see cref="ApplyFeeds" /> overwrites it again
    ///     next frame -- the control page disables the slider in that case
    ///     rather than letting you fight the feed.
    /// </summary>
    public bool TrySet(string name, float value, out string? error)
    {
        Descriptor descriptor;
        lock (_lock)
        {
            if (!_descriptors.TryGetValue(name, out descriptor))
            {
                error = $"unknown property \"{name}\"";
                return false;
            }
        }

        if (value < descriptor.Min || value > descriptor.Max)
        {
            error = $"\"{name}\" must be within [{descriptor.Min}, {descriptor.Max}], got {value}";
            return false;
        }

        _pending[name] = value;
        error = null;
        return true;
    }

    /// <summary>
    ///     Render-thread only. Applies every property change queued since the
    ///     last call, via <paramref name="apply" /> (the caller's job is to
    ///     route each name to whichever object actually owns that uniform/
    ///     setting -- see <see cref="Windowing.AppWindow.Run" />).
    /// </summary>
    public void DrainPending(Action<string, float> apply)
    {
        if (_pending.IsEmpty) return;

        foreach (var name in _pending.Keys.ToArray())
        {
            if (!_pending.TryRemove(name, out var value)) continue;
            lock (_lock)
            {
                _current[name] = value;
            }

            apply(name, value);
        }
    }

    /// <summary>
    ///     Render-thread only, call once per frame after <see cref="DrainPending" />.
    ///     Samples <see cref="FeedRegistry" /> for every property whose feed
    ///     is currently enabled and applies the result via
    ///     <paramref name="apply" /> -- same dispatch a manual set uses, so
    ///     from <c>ShaderModule</c>'s/<c>IFft</c>'s point of view a fed value
    ///     looks identical to one a slider set.
    /// </summary>
    public void ApplyFeeds(Action<string, float> apply)
    {
        List<(string Name, string Source)> active;
        lock (_lock)
        {
            active = _descriptors.Values
                .Where(d => d.FeedSource != null && _feedEnabled.GetValueOrDefault(d.Name))
                .Select(d => (d.Name, Source: d.FeedSource!))
                .ToList();
        }

        foreach (var (name, source) in active)
        {
            if (!FeedRegistry.TrySample(source, out var value)) continue;
            lock (_lock)
            {
                _current[name] = value;
            }

            apply(name, value);
        }
    }

    public readonly record struct Descriptor(
        string Name,
        string Category,
        float Min,
        float Max,
        float Default,
        string? FeedSource = null);
}
