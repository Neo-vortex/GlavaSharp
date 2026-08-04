/* GlavaSharp-original module (see shaders/glavasharp/README.md) -- pass 1
   is deliberately just the existing radial module's own first pass,
   included verbatim rather than copy-pasted: a glowing center circle plus
   a ring of audio-reactive bars, completely ordinary FFT-of-sink/source
   visualization, no relation to the clock hands 2.frag draws on top of it.
   ":radial/1.frag" resolves relative to RootDir (shaders/glava), which
   stays the same throughout this #include regardless of clock/ itself
   being resolved via the sibling-module fallback -- so radial/1.frag's own
   nested #include "@radial.glsl"/":util/smooth.glsl" lines resolve exactly
   as they do for radial itself. */
#include ":radial/1.frag"
