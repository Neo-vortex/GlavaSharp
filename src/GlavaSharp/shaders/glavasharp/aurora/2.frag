/* GlavaSharp-original module -- see 1.frag. Displays the drifting aurora
   feedback buffer (fixed resolution, independent of window size) stretched
   to fill the actual window: cheap dual-radius bloom, luminance-gradient
   edge highlighting (reusing the bloom taps, not extra fetches), and
   atmospheric haze on top of the original glow/alpha handling. */

#request uniform "screen" screen
uniform ivec2 screen;

#request uniform "prev" tex
uniform sampler2D tex;

#include "@aurora.glsl"

out vec4 fragment;

/* Samples the 8-neighbour ring around `uv` at a given radius once, and
   hands back both the averaged "bloom" color and the luminance-gradient
   magnitude across that same ring -- edge highlighting piggybacks on the
   bloom fetches instead of paying for its own set of texture reads. */
void sampleNeighborhood(sampler2D tx, vec2 uv, vec2 texel, float radius, out vec3 bloomColor, out float edgeMag) {
    vec2 r = texel * radius;
    vec3 up    = texture(tx, uv + vec2(0.0,  r.y)).rgb;
    vec3 down  = texture(tx, uv - vec2(0.0,  r.y)).rgb;
    vec3 right = texture(tx, uv + vec2(r.x,  0.0)).rgb;
    vec3 left  = texture(tx, uv - vec2(r.x,  0.0)).rgb;
    vec3 ur    = texture(tx, uv + vec2( r.x,  r.y)).rgb;
    vec3 ul    = texture(tx, uv + vec2(-r.x,  r.y)).rgb;
    vec3 dr    = texture(tx, uv + vec2( r.x, -r.y)).rgb;
    vec3 dl    = texture(tx, uv + vec2(-r.x, -r.y)).rgb;

    bloomColor = (up + down + left + right + ur + ul + dr + dl) / 8.0;

    /* Luminance gradient across the cardinal taps -- a cheap stand-in for
       a proper Sobel kernel (which would need its own 3x3 fetch grid).
       Large where brightness changes sharply, i.e. right at a curtain's
       edge, which is exactly where a thin bright rim reads as "this is a
       lit, three-dimensional sheet" rather than a flat blurry blob. */
    float lumUp = dot(up, LUMA_WEIGHTS);
    float lumDown = dot(down, LUMA_WEIGHTS);
    float lumLeft = dot(left, LUMA_WEIGHTS);
    float lumRight = dot(right, LUMA_WEIGHTS);
    vec2 grad = vec2(lumRight - lumLeft, lumUp - lumDown);
    edgeMag = length(grad);
}

void main() {
    vec2 uv = gl_FragCoord.xy / vec2(screen);
    vec2 texel = 1.0 / vec2(screen);

    vec4 c = texture(tex, uv);

    vec3 bloomNear; float edgeMag;
    sampleNeighborhood(tex, uv, texel, BLOOM_RADIUS, bloomNear, edgeMag);

    vec3 bloomFar; float edgeMagFar; /* wider, dimmer ring -- edge unused here */
    sampleNeighborhood(tex, uv, texel, BLOOM_RADIUS2, bloomFar, edgeMagFar);

    c.rgb += bloomNear * BLOOM_STRENGTH + bloomFar * BLOOM_STRENGTH2;
    c.rgb += edgeMag * EDGE_STRENGTH * EDGE_COLOR;

    /* Atmospheric haze: a faint cool tint that grows with height, standing
       in for aerial perspective -- the "higher = further into the sky, so
       fainter/cooler" depth cue real aurora photos have. */
    float haze = smoothstep(0.3, 1.0, uv.y) * HAZE_STRENGTH;
    c.rgb = mix(c.rgb, c.rgb + HAZE_COLOR, haze);

    c.rgb *= GLOW;

    /* Alpha follows brightness, not a separate channel -- fully transparent
       wherever the aurora hasn't reached (or has fully faded), so the
       desktop shows through everywhere else, the same "nothing drawn here"
       convention radial/bars/circle use. */
    fragment = vec4(c.rgb, clamp(max(max(c.r, c.g), c.b), 0.0, 1.0));
}
