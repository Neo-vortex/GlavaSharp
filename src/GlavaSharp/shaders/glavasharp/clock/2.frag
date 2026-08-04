/* GlavaSharp-original module (see shaders/glavasharp/README.md) -- draws
   analog clock hands on top of 1.frag's radial spectrum visualization.

   The only new host-facing thing here is `seconds_since_midnight`: a
   #request property (so it's a normal, manually-settable-from-the-control-
   channel uniform, range 0..86400) that also carries a #request feed
   binding to the built-in "clock" source (see Control/FeedRegistry.cs).
   With that feed enabled (the default -- see PropertyStore.Register),
   AppWindow samples DateTime.Now every frame and pokes the result in here
   exactly like a slider drag would; flip the checkbox off in the control
   page and it freezes at whatever value the slider holds, e.g. to line up
   a screenshot. Nothing in this shader, or in ShaderModule, needed to
   know "time" is special -- it's just another fed property.

   Hand *angles* stay purely time-driven -- this still has to actually tell
   the time. What's audio-reactive is each hand's thickness and a soft
   additive glow around it, each hand tied to a different band (hour ->
   bass, minute -> mid, second -> treble/overall) so the whole face visibly
   breathes with the music without the hands ever lying about what time it
   is. `audio_reactivity` (also a #request property, plain slider, no feed)
   dials the overall strength of that effect, default 1. */

#request uniform "screen" screen
uniform ivec2 screen;

#request uniform "prev" tex
uniform sampler2D tex;

/* C_RADIUS -- reused so the hands are sized relative to the same circle
   1.frag (radial/1.frag) draws, not an independent guess. */
#include ":radial.glsl"
#include ":util/smooth.glsl"

#request uniform "audio_sz" audio_sz
uniform int audio_sz;

#request uniform "audio_l" audio_l
uniform sampler1D audio_l;

#request uniform "audio_r" audio_r
uniform sampler1D audio_r;

#request property "seconds_since_midnight" float 0 0 86400
#request feed "seconds_since_midnight" clock
uniform float seconds_since_midnight;

#request property "audio_reactivity" float 1.0 0.0 3.0
uniform float audio_reactivity;

out vec4 fragment;

#define TWOPI 6.28318530718
#define BAND_SAMPLES 6

/* Signed distance from p to the segment a->b. */
float sdSegment(vec2 p, vec2 a, vec2 b) {
    vec2 pa = p - a, ba = b - a;
    float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
    return length(pa - ba * h);
}

/* Average smooth_audio over [lo, hi], both channels -- same banded-average
   idea aurora/1.frag's bandEnergy uses, so a single loud bin doesn't make a
   whole hand flicker frame to frame. */
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

/* Draws one hand as a rounded line from `center`, `length_` pixels long,
   pointing at `angle` (0 = 12 o'clock, clockwise). `glowRadius`/`glowAmount`
   add a soft additive halo (tinted `color`, brighter -- and, in desktop/
   transparent mode, more opaque -- the louder that hand's band is) before
   the crisp `thickness`-wide core is blended on top. */
vec4 drawHand(vec2 fragPos, vec2 center, float angle, float length_, float thickness,
              float glowRadius, float glowAmount, vec4 color, vec4 base) {
    vec2 dir = vec2(sin(angle), cos(angle));
    float d = sdSegment(fragPos, center, center + dir * length_);

    float glow = exp(-(d * d) / max(glowRadius * glowRadius, 0.0001)) * glowAmount;
    vec4 c = base + color * glow;

    float coreA = 1.0 - smoothstep(thickness - 1.0, thickness + 1.0, d);
    return mix(c, color, coreA * color.a);
}

void main() {
    vec2 center = vec2(screen) / 2.0;
    vec2 fragPos = gl_FragCoord.xy;
    vec4 c = texture(tex, fragPos / vec2(screen));

    /* Each hand's own angle is just "how far through its own period has
       seconds_since_midnight gotten" -- no host-side hour/minute/second
       splitting needed, it's all here in the shader. */
    float hourAngle = (mod(seconds_since_midnight, 43200.0) / 43200.0) * TWOPI;
    float minuteAngle = (mod(seconds_since_midnight, 3600.0) / 3600.0) * TWOPI;
    float secondAngle = (mod(seconds_since_midnight, 60.0) / 60.0) * TWOPI;

    float bass = bandEnergy(0.0, 0.15);
    float mid = bandEnergy(0.15, 0.5);
    float treble = bandEnergy(0.5, 1.0);
    float overall = (bass + mid + treble) / 3.0;
    float react = audio_reactivity;

    /* Hour: thick and slow, so it only reacts to the thing that's actually
       slow and heavy -- bass. */
    c = drawHand(fragPos, center, hourAngle, C_RADIUS * 0.50,
        4.0 + bass * 2.5 * react, 7.0, bass * 0.6 * react, vec4(0, 0, 0, 1), c);

    /* Minute: mid frequencies. */
    c = drawHand(fragPos, center, minuteAngle, C_RADIUS * 0.80,
        3.0 + mid * 2.0 * react, 6.0, mid * 0.5 * react, vec4(0, 0, 0, 1), c);

    /* Second: already the most "alive" hand visually (thin, always
       sweeping) -- gets the most dramatic reaction, tied to overall energy
       so it reads as "the beat" rather than one specific band. */
    c = drawHand(fragPos, center, secondAngle, C_RADIUS * 0.90,
        1.5 + overall * 3.0 * react, 9.0, overall * 1.2 * react,
        vec4(0.85, 0.05, 0.05, 1), c);

    /* Pivot: pulses with overall energy too, tying the center dot to the
       same beat as the second hand's glow. */
    float pivotD = length(fragPos - center);
    float pivotR = 3.0 + overall * 3.0 * react;
    c = mix(c, vec4(0, 0, 0, 1), 1.0 - smoothstep(pivotR - 1.0, pivotR + 1.0, pivotD));

    fragment = vec4(c.rgb, clamp(c.a, 0.0, 1.0));
}
