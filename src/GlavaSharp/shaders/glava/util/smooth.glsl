 #ifndef _SMOOTH_GLSL
#define _SMOOTH_GLSL

#include ":util/common.glsl"

#include "@smooth_parameters.glsl"
#include ":smooth_parameters.glsl"

#define average 0
#define maximum 1
#define hybrid 2

/* _FREQ_PREBUCKETED is a GlavaSharp-original macro (like _USE_ALPHA),
   injected by Shaders/ShaderModule.cs when FftSettings.Scale (see
   Shaders/FrequencyBucketing.cs) isn't FrequencyScale.Linear. In that case
   the raw FFT spectrum has already been redistributed onto the chosen
   perceptual scale (log2/mel/bark/erb) before it ever reaches this
   texture -- bin index already *is* a perceptually-spaced position, so this
   function's own log warp on top would skew the spectrum a second time.
   Kept as an explicit #if (default 0, i.e. today's exact GLava-derived
   behavior) rather than deleting the warp outright, since --freq-scale
   linear still needs it. */
float scale_audio(float idx) {
    #if _FREQ_PREBUCKETED
    return idx;
    #else
    return -log((-(SAMPLE_RANGE) * idx) + 1) / (SAMPLE_SCALE);
    #endif
}

float iscale_audio(float idx) {
    return -log((SAMPLE_RANGE) * idx) / (SAMPLE_SCALE);
}

/* Note: the _SMOOTH_FACTOR macro is defined by GLava itself, from `#request setsmoothfactor`*/

float smooth_audio(in sampler1D tex, int tex_sz, highp float idx) {
    
    #if _PRE_SMOOTHED_AUDIO < 1
    float
        smin = scale_audio(clamp(idx - _SMOOTH_FACTOR, 0, 1)) * tex_sz,
        smax = scale_audio(clamp(idx + _SMOOTH_FACTOR, 0, 1)) * tex_sz;
    float m = ((smax - smin) / 2.0F), s, w;
    float rm = smin + m; /* middle */
    
    #if SAMPLE_MODE == average
    float avg = 0, weight = 0;
    for (s = smin; s <= smax; s += 1.0F) {
        w = ROUND_FORMULA(clamp((m - abs(rm - s)) / m, 0, 1));
        weight += w;
        avg += texelFetch(tex, int(round(s)), 0).r * w;
    }
    avg /= weight;
    return avg;
    #elif SAMPLE_MODE == hybrid
    float vmax = 0, avg = 0, weight = 0, v;
    for (s = smin; s < smax; s += 1.0F) {
        w = ROUND_FORMULA(clamp((m - abs(rm - s)) / m, 0, 1));
        weight += w;
        v = texelFetch(tex, int(round(s)), 0).r * w;
        avg += v;
        if (vmax < v)
            vmax = v;
    }
    return (vmax * (1 - SAMPLE_HYBRID_WEIGHT)) + ((avg / weight) * SAMPLE_HYBRID_WEIGHT);
    #elif SAMPLE_MODE == maximum
    float vmax = 0, v;
    for (s = smin; s < smax; s += 1.0F) {
        w = texelFetch(tex, int(round(s)), 0).r * ROUND_FORMULA(clamp((m - abs(rm - s)) / m, 0, 1));
        if (vmax < w)
            vmax = w;
    }
    return vmax;
    #endif
    #else
    return texelFetch(tex, int(round(idx * tex_sz)), 0).r;
    #endif
}

/* Applies the audio smooth sampling function three times to the adjacent values */
float smooth_audio_adj(in sampler1D tex, int tex_sz, highp float idx, highp float pixel) {
    float
        al = smooth_audio(tex, tex_sz, max(idx - pixel, 0.0F)),
        am = smooth_audio(tex, tex_sz, idx),
        ar = smooth_audio(tex, tex_sz, min(idx + pixel, 1.0F));
    return (al + am + ar) / 3.0F;
}

#ifdef TWOPI
#undef TWOPI
#endif
#ifdef PI
#undef PI
#endif
#endif /* _SMOOTH_GLSL */
