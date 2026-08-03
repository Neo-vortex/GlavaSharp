/* Aurora -- a calming, ambient desktop visualizer: soft, volumetric
   curtains of color that drift upward and fold like the northern lights,
   driven by the audio spectrum, fading into a fully transparent
   background. Not part of upstream GLava -- a GlavaSharp-original module
   (see shaders/glavasharp/README.md) built on the same persistent
   "history" feedback-buffer mechanism waterfall uses, but run as a
   decay+drift loop instead of a hard scroll -- see 1.frag.

   IMPORTANT, unchanged from the original: there is still no time/clock
   uniform anywhere in this pipeline. Everything that moves does so purely
   because the feedback loop keeps re-sampling its own history through a
   *fixed*, buffer-space flow field -- originally a single sine "sway",
   now a much richer static curl-noise field (see noise.glsl). A parcel of
   color takes a different, non-repeating path each frame not because the
   field itself changes, but because the parcel's position relative to
   that field, and the audio energy warping it, both keep changing. This
   keeps the whole rewrite a drop-in replacement: zero new uniforms, zero
   host-side changes required. */

#include "@noise.glsl"

/* ---- Feedback / layering ----------------------------------------------
   The single history buffer still holds one RGBA field -- there's no
   second or third persistent render target here, so "multiple layers"
   can't mean literally-independent persistent state without host-side
   changes (extra #request uniform "history" targets and extra ping-pong
   buffers in ShaderModule.cs). Instead, each frame's read of `hist` is
   itself a depth-weighted blend of NUM_VLAYERS virtual layers, each with
   its own decay/drift/sway/noise-scale/warp, sampled at its own
   flow-warped offset and blended by LAYER_WEIGHT (which sums to 1, so the
   blend can't runaway-brighten the way naive layer addition would). That
   gives the layered-parallax look multi-layer aurora is going for, at the
   cost of the layers not being literally independently addressable. */
#define NUM_VLAYERS 3
#define FBM_OCTAVES 4

const float LAYER_DECAY[3]       = float[3](0.994, 0.996, 0.998);
const float LAYER_DRIFT[3]       = float[3](0.0065, 0.0045, 0.0028);
const float LAYER_SWAY_AMT[3]    = float[3](0.055, 0.035, 0.020);
const float LAYER_SWAY_FREQ[3]   = float[3](2.0, 3.3, 5.1);
const float LAYER_NOISE_SCALE[3] = float[3](1.6, 3.1, 6.0);
const float LAYER_WARP[3]        = float[3](0.35, 0.55, 0.80);
const float LAYER_WEIGHT[3]      = float[3](0.55, 0.32, 0.13);
const float LAYER_HUE[3]         = float[3](0.0, 0.06, -0.05);

/* Per-column drift variation: a slow, x-only value-noise field (sampled
   once per layer with a per-layer seed offset so layers don't all lag in
   the same columns) that speeds up or slows down each layer's vertical
   drift by column. Without this every column of a given layer rises at
   exactly the same rate, which is the single biggest tell that a "fluid"
   sim is actually a uniform scroll underneath. */
#define COLUMN_NOISE_SCALE 2.2
#define COLUMN_SPEED_VARIATION 0.5

/* Anisotropic feedback sampling: instead of one texture read per layer,
   take a short streak of samples along the local flow direction (from the
   layer's curl vector) and weight them so nearby samples dominate. A
   single isotropic sample makes feedback look "smeared"; sampling along
   the direction it's actually moving makes it look "transported" -- the
   single highest-impact change here for making the sim read as fluid
   rather than blurred. */
#define ANISO_STRIDE 0.010

/* Chromatic feedback separation: R and B are read with a tiny offset
   along (resp. against) the local flow direction from G/A, so fast-moving
   color picks up a faint prismatic leading/trailing edge instead of
   staying perfectly achromatic as it moves. Kept tiny -- this is a subtle
   trailing-edge cue, not a chromatic-aberration filter. */
#define CHROMA_OFFSET 0.004

/* Filament thresholding: where the local flow magnitude is very high
   (chaotic, fast-changing curl), the layer's contribution is thinned out
   rather than left fully opaque -- this is what breaks a solid curtain up
   into branching strands that split and rejoin instead of one continuous
   sheet. FILAMENT_STRENGTH is how much of that thinning applies (0 = off,
   1 = full). */
#define FILAMENT_THRESHOLD 0.55
#define FILAMENT_SOFTNESS 0.35
#define FILAMENT_STRENGTH 0.6

/* ---- Audio bands --------------------------------------------------------
   Positions into the 1D spectrum textures (audio_l/audio_r), 0 = lowest
   frequency bin, 1 = highest. Each band is averaged over a small range
   rather than sampled at one point, so a single loud bin doesn't make the
   whole band flicker. */
#define BASS_LO 0.0
#define BASS_HI 0.10
#define MID_LO  0.10
#define MID_HI  0.45
#define TREB_LO 0.45
#define TREB_HI 1.0
#define BAND_SAMPLES 4

/* How strongly each band drives its assigned property. Bass drives large-
   scale vertical motion, mid drives warping/folding, treble drives fine
   shimmer/sparkle -- kept as separate multipliers (not "audio * brightness")
   so the three bands read as different *kinds* of visual response instead
   of everything just pulsing together. */
#define BASS_DRIFT_BOOST   1.8
#define BASS_HEIGHT_BOOST  0.6
#define MID_TURBULENCE     0.9
#define TREBLE_SHIMMER     0.35
#define TREBLE_SPARKLE_THRESHOLD 0.986
#define SPARKLE_DRIFT_STRENGTH 0.05

/* Ribbon shaping (unchanged idea from the original: several phase-offset
   x-samples so ribbons don't peak in lockstep) and overall injection
   shape. */
#define NBANDS 3
#define AMPLIFY 2.6
#define INJECT_HEIGHT 0.16

/* Nonlinear persistence: feedback that's pure `prev * decay` slowly turns
   to visual mush because bilinear sampling of `hist` blends neighbouring
   colors together every single frame, and that blur compounds. Nudging
   saturation back up and gamma-sharpening alpha each pass counteracts that
   drift without needing an extra unsharp-mask pass. */
#define TEMPORAL_SHARPEN_GAMMA 0.92
#define SATURATION_RESTORE 1.06

/* ---- Display pass (2.frag): bloom, edges, haze -------------------------- */
#define GLOW 1.6
#define BLOOM_STRENGTH 0.45
#define BLOOM_RADIUS 2.2
#define BLOOM_STRENGTH2 0.25
#define BLOOM_RADIUS2 5.5
#define EDGE_STRENGTH 0.5
const vec3 EDGE_COLOR = vec3(0.85, 0.95, 1.0);
const vec3 LUMA_WEIGHTS = vec3(0.299, 0.587, 0.114);
#define HAZE_STRENGTH 0.22
const vec3 HAZE_COLOR = vec3(0.02, 0.05, 0.10);

/* ---- Palette ------------------------------------------------------------
   Hand-authored northern-lights gradient (unchanged stops), now sampled
   as a *base* color that gets modulated per-pixel rather than used as the
   final color directly -- see aurora_dynamic_color below. */
vec3 aurora_palette(float t) {
    t = clamp(t, 0.0, 1.0);
    vec3 c0 = vec3(0.02, 0.55, 0.45); /* teal   */
    vec3 c1 = vec3(0.05, 0.75, 0.35); /* green  */
    vec3 c2 = vec3(0.10, 0.45, 0.75); /* blue   */
    vec3 c3 = vec3(0.45, 0.25, 0.85); /* violet */
    vec3 c4 = vec3(0.85, 0.35, 0.65); /* pink   */
    float seg = t * 4.0;
    if (seg < 1.0) return mix(c0, c1, seg);
    if (seg < 2.0) return mix(c1, c2, seg - 1.0);
    if (seg < 3.0) return mix(c2, c3, seg - 2.0);
    return mix(c3, c4, seg - 3.0);
}

vec3 rgb2hsv(vec3 c) {
    vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

vec3 hsv2rgb(vec3 c) {
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

/* Compositional palette shift: slide *which part of the gradient* gets
   sampled based on the bass/treble balance, rather than only tinting the
   result -- bass-heavy moments pull sampling toward the teal/green end,
   treble-heavy moments push it toward violet/pink. `balance` is expected
   in roughly [-1, 1]: positive = bass-dominant, negative = treble-dominant. */
vec3 aurora_blend_color(float x, float balance) {
    float shift = clamp(balance, -1.0, 1.0) * 0.22;
    return aurora_palette(clamp(x - shift, 0.0, 1.0));
}

/* Dynamic coloring: hue is nudged by altitude and local flow speed, and
   saturation/value respond to energy -- so two ribbons at the same x but
   different heights, speeds or loudness read as distinguishably different
   colors instead of identical copies of the same gradient sample. Hue
   nudges are kept small and altitude-driven, not time-driven (avoid
   rainbow cycling): this shifts *around* the compositionally-shifted
   gradient, it doesn't replace it. */
vec3 aurora_dynamic_color(float x, float altitude, float energy, float flowSpeed, float layerHue, float balance) {
    vec3 base = aurora_blend_color(x, balance);
    vec3 hsv = rgb2hsv(base);
    hsv.x = fract(hsv.x + layerHue + 0.05 * sin(altitude * 3.0) + 0.04 * flowSpeed);
    hsv.y = clamp(hsv.y * (0.85 + 0.30 * energy), 0.0, 1.0);
    hsv.z = clamp(hsv.z, 0.0, 1.0);
    return hsv2rgb(hsv);
}
