/* GlavaSharp-original module -- not part of GLava's own shader tree
   (see shaders/glavasharp/README.md). Spectrogram waterfall: this pass
   accumulates a scrolling history of the audio spectrum into a persistent
   off-screen buffer that survives across frames; 2.frag displays it.
   GLava's own module format has no equivalent to this -- every GLava
   module redraws from scratch each frame. The persistence is a GlavaSharp
   engine extension: see Shaders/ShaderModule.cs's `#request uniform
   "history" <name>` handling. */

#request uniform "screen" screen
uniform ivec2 screen;

#request uniform "audio_sz" audio_sz
uniform int audio_sz;

/* Last frame's accumulated image -- this pass both reads it (to shift the
   existing rows) and writes the new one (this frame's shifted + newest
   row), via ShaderModule's persistent ping-pong pair. */
#request uniform "history" hist
uniform sampler2D hist;

#include ":util/smooth.glsl"

#request uniform "audio_l" audio_l
uniform sampler1D audio_l;

#request uniform "audio_r" audio_r
uniform sampler1D audio_r;

out vec4 fragment;

/* Gain applied to the spectrum magnitude (already log-compressed and
   clamped to [0,1] by CpuFft/GpuFft) before mapping it to a color --
   tune like bars.glsl's AMPLIFY. */
#define GAIN 2.5

/* Blue -> cyan -> green -> yellow -> red -> white heat gradient, the
   classic spectrogram color ramp. */
vec3 heatmap(float t) {
    t = clamp(t, 0.0, 1.0);
    vec3 c0 = vec3(0.01, 0.01, 0.04);
    vec3 c1 = vec3(0.05, 0.05, 0.55);
    vec3 c2 = vec3(0.00, 0.65, 0.85);
    vec3 c3 = vec3(0.10, 0.85, 0.25);
    vec3 c4 = vec3(0.95, 0.90, 0.05);
    vec3 c5 = vec3(0.95, 0.25, 0.02);
    vec3 c6 = vec3(1.00, 1.00, 1.00);
    float seg = t * 6.0;
    if (seg < 1.0) return mix(c0, c1, seg);
    if (seg < 2.0) return mix(c1, c2, seg - 1.0);
    if (seg < 3.0) return mix(c2, c3, seg - 2.0);
    if (seg < 4.0) return mix(c3, c4, seg - 3.0);
    if (seg < 5.0) return mix(c4, c5, seg - 4.0);
    return mix(c5, c6, seg - 5.0);
}

void main() {
    vec2 uv = gl_FragCoord.xy / vec2(screen);
    float dy = 1.0 / float(screen.y);

    if (uv.y > 1.0 - dy) {
        /* Newest row (top edge): sample the current spectrum across the
           width, using the same smoothed sampling bars.glsl/circle.glsl
           use, blending both channels. */
        float l = smooth_audio(audio_l, audio_sz, uv.x);
        float r = smooth_audio(audio_r, audio_sz, uv.x);
        float mag = max(l, r) * GAIN;
        fragment = vec4(heatmap(mag), 1.0);
    } else {
        /* Every other row: shift down by one -- this pixel takes what was
           directly above it last frame, so the whole image "falls". */
        fragment = texture(hist, uv + vec2(0.0, dy));
    }
}
