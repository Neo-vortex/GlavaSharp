/* GlavaSharp-original module. Procedural noise / flow-field primitives used
   by the aurora feedback pass (see 1.frag). Everything here is purely
   spatial, buffer-space noise -- there is deliberately no time uniform
   anywhere in this pipeline (see aurora.glsl's header comment), so every
   bit of motion still has to emerge from repeatedly sampling history
   *through* these fields frame after frame, exactly the trick the
   original hand-written sine sway used, just with a much richer,
   non-repeating field standing in for that one sine wave. */

/* Cheap 2D hash -> [0,1). Not cryptographic, just needs to look
   uncorrelated at the grid scale valueNoise samples it at. */
float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

/* Quintic-interpolated value noise. A quintic (6t^5-15t^4+10t^3) blend has
   a zero second derivative at the cell boundaries, unlike a cubic
   smoothstep -- that matters here specifically because curlNoise() below
   differentiates this function a second time (a derivative of a
   derivative), and cubic interpolation's visible second-derivative
   discontinuities show up as faint creases right on the grid lines once
   you curl them. */
float valueNoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

/* Fractal Brownian motion: layered octaves of valueNoise. Each octave is
   rotated by a fixed, non-axis-aligned matrix before being scaled up --
   without that rotation, successive octaves stack on the exact same grid
   axes and the sum reads as a subtle but very recognisable plaid/tiled
   pattern. The rotation breaks that alignment so the result reads as
   genuinely irregular instead of "obviously layered sine grids." */
const mat2 OCTAVE_ROT = mat2(0.8, -0.6, 0.6, 0.8);
float fbm(vec2 p, int octaves) {
    float sum = 0.0;
    float amp = 0.5;
    for (int i = 0; i < octaves; i++) {
        sum += amp * valueNoise(p);
        p = OCTAVE_ROT * p * 2.02;
        amp *= 0.5;
    }
    return sum;
}

/* Curl of a scalar FBM potential field, via central differences. Curling a
   potential this way guarantees the resulting vector field is
   divergence-free, which is the specific property that makes curl-driven
   flow look fluid rather than "leaky": raw gradient-following noise tends
   to visibly suck material into low points or blow it apart from high
   points, while a curl field only ever swirls things around one another.
   That swirl-not-source-or-sink behaviour is most of what reads as
   "organic fluid motion" versus "noisy wobble." */
vec2 curlNoise(vec2 p, int octaves) {
    const float eps = 0.06;
    float n1 = fbm(p + vec2(0.0, eps), octaves);
    float n2 = fbm(p - vec2(0.0, eps), octaves);
    float n3 = fbm(p + vec2(eps, 0.0), octaves);
    float n4 = fbm(p - vec2(eps, 0.0), octaves);
    float dx = (n1 - n2) / (2.0 * eps);
    float dy = (n3 - n4) / (2.0 * eps);
    /* (dPotential/dy, -dPotential/dx) is the standard 2D curl-of-scalar
       construction -- swapping the sign on one axis is what keeps it
       divergence-free instead of just being another gradient. */
    return vec2(dx, -dy);
}

/* Domain warp: push a sample point through its own noise field before the
   caller uses it -- and do it through *two* rounds of FBM (fbm-of-fbm),
   not one, so the warp itself has internal structure (folds within folds)
   rather than reading as a single uniform wobble applied everywhere. This
   is the specific technique that turns a flat feedback shear into the
   folding/stretching/tearing look real aurora curtains have. */
vec2 domainWarp(vec2 p, float strength, int octaves) {
    vec2 q = vec2(fbm(p + vec2(1.7, 9.2), octaves),
                  fbm(p + vec2(8.3, 2.8), octaves));
    vec2 r = vec2(fbm(p + 4.0 * q + vec2(1.3, 6.7), octaves),
                  fbm(p + 4.0 * q + vec2(8.1, 3.4), octaves));
    return p + strength * (r - 0.5);
}
