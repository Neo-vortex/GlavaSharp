using System;

namespace GlavaSharp.Shaders;

/// <summary>
///     Redistributes a raw, linearly-spaced FFT magnitude spectrum (bin i's
///     center frequency is <c>i * sampleRate / N</c>) into a perceptually-
///     spaced one, per <see cref="FrequencyScale" />. Applied once, on the
///     CPU, right after <see cref="CpuFft" />/<see cref="GpuFft" /> compute
///     raw per-bin magnitude and before gravity smoothing -- shared by both
///     backends instead of duplicated, and upstream of every shader/module,
///     which is why <c>util/smooth.glsl</c>'s own log-ish warp needs to
///     become a no-op (`_FREQ_PREBUCKETED`) once this is active: applying
///     both would warp the spectrum twice.
///
///     Why this exists: with raw linear bins, a typical track's audible
///     energy is concentrated in a handful of the lowest bins (bass/low-mid),
///     while the rest -- most of the bin count -- covers the sparser, noisier
///     high end. Whatever "screen position -&gt; bin" mapping a shader uses on
///     top of that either (a) redundantly samples nearly the same few bass
///     bins across a wide swath of screen space, which looks static/underused
///     even though that's where the real energy is, or (b) spreads the sparse
///     high-frequency bins across proportionally more screen space, which
///     looks disproportionately "active" from frame-to-frame variance alone.
///     Bucketing by actual perceptual spacing fixes the mapping at the
///     source: each output bucket already corresponds to a frequency *range*
///     humans resolve roughly as one unit, not a fixed slice of the raw
///     linear axis.
/// </summary>
public sealed class FrequencyBucketing
{
    // Below this, none of the four scales' formulas are meaningfully
    // different (all compress toward 0), and Bark's/ERB's forward functions
    // aren't well-behaved arbitrarily close to 0 Hz -- floor matches the
    // usual "20 Hz - 20 kHz" audible range convention.
    private const float MinHz = 20f;
    private const float MaxHz = 20000f;

    private readonly BucketMap[] _map;

    /// <param name="scale">Must not be <see cref="FrequencyScale.Linear" /> -- callers should skip bucketing entirely in that case rather than construct this.</param>
    /// <param name="bucketCount">Number of output buckets (the array length <see cref="Apply" /> will fill).</param>
    /// <param name="rawBinCount">Number of raw FFT bins (N/2) bucket edges are resolved against.</param>
    /// <param name="sampleRate">Audio sample rate in Hz, for bin-index &lt;-&gt; Hz conversion.</param>
    public FrequencyBucketing(FrequencyScale scale, int bucketCount, int rawBinCount, int sampleRate)
    {
        if (scale == FrequencyScale.Linear)
            throw new ArgumentException(
                "FrequencyScale.Linear needs no bucketing -- callers should skip FrequencyBucketing entirely.",
                nameof(scale));

        BucketCount = bucketCount;
        var n = rawBinCount * 2;
        var maxHz = MathF.Min(sampleRate / 2f, MaxHz);
        var (forward, inverse) = ScaleFunctions(scale);
        var sMin = forward(MinHz);
        var sMax = forward(maxHz);

        _map = new BucketMap[bucketCount];
        for (var b = 0; b < bucketCount; b++)
        {
            var loHz = inverse(sMin + (sMax - sMin) * b / bucketCount);
            var hiHz = inverse(sMin + (sMax - sMin) * (b + 1) / bucketCount);
            var loBinF = loHz * n / sampleRate;
            var hiBinF = hiHz * n / sampleRate;

            // Integer bin range fully inside [loBinF, hiBinF] -- when this
            // spans 2+ raw bins, Apply() aggregates (max) over them. When it
            // doesn't (typical at the low end, where buckets can be narrower
            // than one raw bin regardless of scale), loBin ends up > hiBin
            // here and Apply() falls back to interpolating around the
            // bucket's center bin instead.
            var loBin = (int)MathF.Ceiling(loBinF);
            var hiBin = Math.Min((int)MathF.Floor(hiBinF), rawBinCount - 1);
            var centerBinF = Math.Clamp((loBinF + hiBinF) * 0.5f, 0f, rawBinCount - 1);

            _map[b] = new BucketMap(loBin, hiBin, centerBinF);
        }
    }

    public int BucketCount { get; }

    /// <summary>
    ///     Exposes the same per-bucket (loBin, hiBin, centerBinF) map <see cref="Apply" />
    ///     uses, for <see cref="GpuFft" /> to upload once at construction so its
    ///     bucketing compute pass can reproduce this class's max/lerp logic on
    ///     the GPU instead of reading raw bins back to the CPU first. loHi.y &gt;
    ///     loHi.x (a real multi-bin range) means "max over [loHi.x, loHi.y]";
    ///     otherwise (loHi.y &lt;= loHi.x) fall back to lerping around
    ///     centerBinF[b] -- same two branches as <see cref="Apply" />.
    /// </summary>
    public void CopyBucketMap(Span<(int lo, int hi)> loHi, Span<float> centerBinF)
    {
        for (var b = 0; b < BucketCount; b++)
        {
            loHi[b] = (_map[b].LoBin, _map[b].HiBin);
            centerBinF[b] = _map[b].CenterBinF;
        }
    }

    /// <summary>Fills <paramref name="output" /> (length <see cref="BucketCount" />) from <paramref name="rawBins" /> (length rawBinCount, as passed to the constructor).</summary>
    public void Apply(ReadOnlySpan<float> rawBins, Span<float> output)
    {
        for (var b = 0; b < BucketCount; b++)
        {
            var m = _map[b];
            if (m.HiBin > m.LoBin)
            {
                // Max, not average: preserves peaks (a single loud harmonic
                // in a bucket spanning many quiet raw bins should still read
                // as loud), matching standard practice for FFT-bin-to-bar
                // aggregation.
                var v = 0f;
                for (var i = m.LoBin; i <= m.HiBin; i++)
                    if (rawBins[i] > v)
                        v = rawBins[i];
                output[b] = v;
            }
            else
            {
                var lo = Math.Clamp((int)MathF.Floor(m.CenterBinF), 0, rawBins.Length - 1);
                var hi = Math.Clamp(lo + 1, 0, rawBins.Length - 1);
                var frac = m.CenterBinF - lo;
                output[b] = rawBins[lo] * (1f - frac) + rawBins[hi] * frac;
            }
        }
    }

    private static (Func<float, float> Forward, Func<float, float> Inverse) ScaleFunctions(FrequencyScale scale)
    {
        return scale switch
        {
            // Octave spacing: forward = log2(f), inverse = 2^x. Simplest
            // perceptual scale -- each bucket covers a fixed frequency ratio.
            FrequencyScale.Log2 => (MathF.Log2, x => MathF.Pow(2f, x)),

            // Mel (O'Shaughnessy's widely-used closed-form fit to the
            // original Stevens/Volkmann/Newman pitch-matching data).
            FrequencyScale.Mel => (
                f => 2595f * MathF.Log10(1f + f / 700f),
                m => 700f * (MathF.Pow(10f, m / 2595f) - 1f)),

            // Bark (Traunmüller 1990 closed-form approximation of Zwicker's
            // 24 critical bands) -- chosen over Zwicker's own atan-based
            // formula specifically because it has a simple closed-form
            // inverse; Traunmüller's is a standard, widely-cited
            // approximation of the same scale, not a different one.
            FrequencyScale.Bark => (
                f => 26.81f * f / (1960f + f) - 0.53f,
                z => 1960f * (z + 0.53f) / (26.81f - (z + 0.53f))),

            // ERB-rate (Glasberg & Moore 1990) -- the current standard for
            // computational auditory modeling; resolves roughly 4x finer
            // than Bark below ~500Hz, which is exactly the region a fixed
            // linear or naive-log axis distorts the most for bass-heavy
            // music.
            FrequencyScale.Erb => (
                f => 21.4f * MathF.Log10(1f + 0.00437f * f),
                n => (MathF.Pow(10f, n / 21.4f) - 1f) / 0.00437f),

            _ => throw new ArgumentOutOfRangeException(nameof(scale), scale, "Unhandled frequency scale.")
        };
    }

    private readonly struct BucketMap(int loBin, int hiBin, float centerBinF)
    {
        public readonly int LoBin = loBin;
        public readonly int HiBin = hiBin;
        public readonly float CenterBinF = centerBinF;
    }
}
