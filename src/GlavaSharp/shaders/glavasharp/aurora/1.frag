/* GlavaSharp-original module -- not part of GLava's own shader tree (see
   shaders/glavasharp/README.md). Aurora feedback pass.

   Unlike waterfall's hard scroll (shift every row down by exactly one
   pixel, replace the top row), this treats the persistent buffer as a
   decaying feedback loop: each frame, last frame's content is faded and
   re-sampled through a *fixed*, buffer-space flow field, then new
   audio-driven energy is injected along the bottom edge. Because content
   drifts upward through that fixed field frame after frame, it ends up
   bending and folding as it rises and fades -- organic-looking motion
   with no clock/time uniform involved, purely emergent from the feedback
   itself (the buffer's own history *is* the state; see aurora.glsl's
   header comment for why that constraint is kept deliberately).

   This version replaces the original's single sine "sway" with a static
   curl-noise flow field (noise.glsl), warps the sampling coordinates
   through domain warping before every history read, blends three
   differently-tuned "virtual layers" of that field for a volumetric,
   depth-parallax look, samples feedback anisotropically along the local
   flow direction (so it reads as transported, not smeared), separates R/B
   slightly along that same direction for a faint chromatic trailing edge,
   thins layers where the flow is locally chaotic (branching filaments
   instead of one solid sheet), varies drift speed per column, and splits
   the injected audio energy into bass/mid/treble bands that each drive a
   different kind of visual response. See aurora.glsl for every tunable
   and the palette. */

#request uniform "screen" screen
uniform ivec2 screen;

#request uniform "audio_sz" audio_sz
uniform int audio_sz;

/* Last frame's accumulated buffer -- read (decayed + flow-warped) to carry
   existing color forward, and this pass's own output becomes the next
   frame's `hist` via ShaderModule's persistent ping-pong pair. */
#request uniform "history" hist
uniform sampler2D hist;

#include ":util/smooth.glsl"
#include "@aurora.glsl"

#request uniform "audio_l" audio_l
uniform sampler1D audio_l;

#request uniform "audio_r" audio_r
uniform sampler1D audio_r;

out vec4 fragment;

#define TWOPI 6.28318530718

/* Average smooth_audio over [lo, hi] using BAND_SAMPLES taps, across both
   channels. A banded average (rather than one sample point) is what keeps
   a single loud bin from making the whole band flicker frame to frame. */
float bandEnergy(float lo, float hi) {
    float sum = 0.0;
    for (int i = 0; i < BAND_SAMPLES; i++) {
        float t = (float(i) + 0.5) / float(BAND_SAMPLES);
        float pos = clamp(mix(lo, hi, t), 0.0, 1.0);
        float l = smooth_audio(audio_l, audio_sz, pos);
        float r = smooth_audio(audio_r, audio_sz, pos);
        sum += max(l, r);
    }
    return sum / float(BAND_SAMPLES);
}

/* Anisotropic streak sample: rather than one isotropic texture read,
   walks a short line of samples along `dir` (the local flow direction)
   and weights them so the center dominates. Reads as feedback being
   *carried* along the flow instead of just blurred in place -- the
   biggest single "does this look fluid" lever in this rewrite. Weights
   sum to 1.0, so this can't brighten or dim the buffer on its own. */
vec4 anisoSample(sampler2D tx, vec2 uv, vec2 dir) {
    vec2 d = dir * ANISO_STRIDE;
    vec4 c = texture(tx, uv) * 0.40;
    c += texture(tx, uv + d) * 0.25;
    c += texture(tx, uv - d) * 0.15;
    c += texture(tx, uv + 2.0 * d) * 0.12;
    c += texture(tx, uv - 2.0 * d) * 0.08;
    return c;
}

/* One virtual layer's contribution to this frame's feedback read. Returns
   a value already weighted by LAYER_WEIGHT[i] so the caller can just sum
   all layers. `columnSeed` offsets the per-column drift-speed noise so
   different layers lag in different columns rather than all together. */
vec4 sampleLayer(int i, vec2 uv, float bass, float turbulence) {
    vec2 warped = domainWarp(uv * LAYER_NOISE_SCALE[i],
                             LAYER_WARP[i] * turbulence,
                             FBM_OCTAVES);
    vec2 curl = curlNoise(warped, FBM_OCTAVES);
    float curlLen = length(curl);
    vec2 flowDir = curlLen > 0.0001 ? curl / curlLen : vec2(0.0, 1.0);

    /* Per-column drift variation: a slow, x-only value-noise field (one
       per layer, offset so layers don't share the same lagging columns)
       speeds up or slows down this column's rise independent of its
       neighbours -- without it every column in a layer rises in perfect
       lockstep, which is the clearest tell of a scroll dressed up as a
       sim. */
    float colNoise = valueNoise(vec2(uv.x * COLUMN_NOISE_SCALE, float(i) * 17.0));
    float columnFactor = 1.0 + COLUMN_SPEED_VARIATION * (colNoise - 0.5) * 2.0;

    float sway = sin(uv.y * LAYER_SWAY_FREQ[i] * TWOPI) * LAYER_SWAY_AMT[i];
    float drift = LAYER_DRIFT[i] * columnFactor * (1.0 + BASS_DRIFT_BOOST * bass);

    /* To make content flow *upward*, this pixel's new value has to come
       from whatever was BELOW it last frame (smaller uv.y is "about to
       rise into this row") -- sampling a larger uv.y instead just piles
       everything up at the bottom on top of the injection zone and never
       reads as "rising." Same reasoning as the original single-layer
       version, just with curl added on top of the sway. */
    vec2 feedback_uv = vec2(uv.x + sway + curl.x * 0.06,
                             uv.y - drift + curl.y * 0.015);

    /* Chromatic feedback separation: G/A come from the anisotropic streak
       sample; R and B are nudged a hair forward/back along the same flow
       direction, so fast-moving color picks up a faint prismatic
       leading/trailing edge instead of moving as one flat achromatic
       blob. Single-tap (not a full anisoSample) for each so this only
       costs 2 extra fetches per layer, not 10. */
    vec4 baseSample = anisoSample(hist, feedback_uv, flowDir);
    float rSample = texture(hist, feedback_uv + flowDir * CHROMA_OFFSET).r;
    float bSample = texture(hist, feedback_uv - flowDir * CHROMA_OFFSET).b;
    vec4 prevLayer = vec4(rSample, baseSample.g, bSample, baseSample.a) * LAYER_DECAY[i];

    /* Filament thinning: where the local curl magnitude is large (the
       field is changing fast/chaotically here), thin this layer's
       contribution out instead of leaving it solid -- this is what makes
       curtains branch and fray into strands rather than staying one
       continuous sheet. */
    float turbulentMask = 1.0 - smoothstep(FILAMENT_THRESHOLD, FILAMENT_THRESHOLD + FILAMENT_SOFTNESS, curlLen);
    float filament = mix(1.0, turbulentMask, FILAMENT_STRENGTH);
    prevLayer *= filament;

    return prevLayer * LAYER_WEIGHT[i];
}

void main() {
    vec2 uv = gl_FragCoord.xy / vec2(screen);

    float bass = bandEnergy(BASS_LO, BASS_HI);
    float mid = bandEnergy(MID_LO, MID_HI);
    float treble = bandEnergy(TREB_LO, TREB_HI);
    float turbulence = MID_TURBULENCE * (0.4 + mid);

    /* ---- Feedback: depth-weighted blend of the virtual layers -------- */
    vec4 prev = vec4(0.0);
    for (int i = 0; i < NUM_VLAYERS; i++) {
        prev += sampleLayer(i, uv, bass, turbulence);
    }

    /* Counteract the blur bilinear-sampled feedback accumulates: restore
       a bit of saturation and gamma-sharpen alpha so the buffer stays
       crisp instead of caking into a flat haze after a few hundred
       frames. */
    vec3 prevHsv = rgb2hsv(prev.rgb);
    prevHsv.y = clamp(prevHsv.y * SATURATION_RESTORE, 0.0, 1.0);
    prev.rgb = hsv2rgb(prevHsv);
    prev.a = pow(clamp(prev.a, 0.0, 1.0), TEMPORAL_SHARPEN_GAMMA);

    /* ---- Injection: bass/mid/treble each drive a different response -- */
    float injectHeight = INJECT_HEIGHT * (1.0 + BASS_HEIGHT_BOOST * bass);
    vec4 injected = vec4(0.0);
    if (uv.y < injectHeight) {
        float fade = 1.0 - (uv.y / injectHeight); /* strongest at the bottom edge */

        /* Mid-driven folding: warp the x-position ribbons are sampled at,
           on top of their existing per-band phase offset, so the
           injection silhouette itself folds with the music instead of
           just changing height. */
        float foldWarp = (fbm(vec2(uv.x * 4.0, uv.y * 4.0), FBM_OCTAVES) - 0.5) * turbulence * 0.3;

        float energy = 0.0;
        for (int i = 0; i < NBANDS; i++) {
            float bandPhase = float(i) / float(NBANDS);
            float pos = clamp(uv.x + foldWarp + 0.15 * sin(uv.x * 3.0 + bandPhase * TWOPI), 0.0, 1.0);
            float l = smooth_audio(audio_l, audio_sz, pos);
            float r = smooth_audio(audio_r, audio_sz, pos);
            energy += max(l, r) / float(NBANDS);
        }
        energy *= AMPLIFY * fade;

        /* Treble-driven shimmer: fine, high-frequency noise riding on top
           of the injected energy (not a separate additive layer -- it
           modulates the same energy value so it only shows up where
           there's already something to shimmer on). */
        float shimmerNoise = valueNoise(uv * 220.0 + vec2(bass * 3.0, mid * 3.0));
        energy *= 1.0 + TREBLE_SHIMMER * treble * (shimmerNoise - 0.5);

        float balance = clamp(bass - treble, -1.0, 1.0);
        vec3 color = aurora_dynamic_color(uv.x, uv.y, energy, bass - treble, 0.0, balance) * energy;

        /* Sparkles: sharp, sparse bright points that only appear where
           trebly and only where the ribbon already has presence, so they
           read as glints catching the light rather than random static.
           The grid position they're hashed from is itself pushed through
           a cheap, low-octave curl sample first, so sparkles visibly
           drift and swirl with the current instead of twinkling fixed in
           place -- low octave count here specifically because this only
           runs inside the (small) injection zone, so the extra cost is
           bounded. */
        vec2 sparkleDrift = curlNoise(uv * 2.0, 2) * SPARKLE_DRIFT_STRENGTH;
        vec2 sparkleUV = uv + sparkleDrift;
        float sparkleMask = step(TREBLE_SPARKLE_THRESHOLD, hash21(floor(sparkleUV * vec2(screen) * 0.5)));
        color += sparkleMask * treble * energy * vec3(1.0);

        injected = vec4(color, energy);
    }

    /* max(), not +: inside the injection zone the feedback read lands only
       fractions of a row below uv each frame, so `prev` there is mostly
       last frame's already-injected value at nearly the same spot --
       adding fresh `injected` on top of that every frame is an unbounded
       integrator that clips straight to white. max() lets new energy
       refresh the zone without the two terms compounding; above the
       injection zone `injected` is always 0 so this is identical to the
       additive version there. */
    fragment = max(prev, injected);
}
