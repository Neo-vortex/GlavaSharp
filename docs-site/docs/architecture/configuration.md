# Configuration

`Shaders/RcConfig.cs`

A tiny reader for the handful of top-level `rc.glsl` values GlavaSharp
actually acts on today (module name, window title/size/position, FFT
buffer size, sample rate, and `setxwintype "desktop"` — see
[Desktop-Embedded Mode](desktop-embedded-mode.md)) — not a general
`#request` interpreter. Most of `rc.glsl`'s directive surface (window
decoration/floating/opacity hints, other `setxwintype` values like
`"dock"`/`"panel"`, etc.) is parsed by nothing yet and simply has no
effect.
