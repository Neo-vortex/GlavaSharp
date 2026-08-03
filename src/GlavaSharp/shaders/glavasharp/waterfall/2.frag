/* GlavaSharp-original module -- see 1.frag. Displays the accumulated
   spectrogram (fixed-resolution, independent of window size) stretched to
   fill the actual window. */

#request uniform "screen" screen
uniform ivec2 screen;

#request uniform "prev" tex
uniform sampler2D tex;

out vec4 fragment;

void main() {
    vec2 uv = gl_FragCoord.xy / vec2(screen);
    fragment = texture(tex, uv);
}
