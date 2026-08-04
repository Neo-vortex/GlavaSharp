# Windowing

`Windowing/AppWindow.cs`

A deliberately thin GLFW wrapper — not OpenTK's `GameWindow` — so init
hints, platform selection, and the frame loop are all explicit and
GLava-shaped rather than inherited from a general-purpose game-engine
loop. `PlatformPreference.Any` lets GLFW pick Wayland when running inside
a Wayland session and fall back to X11 otherwise, which is what actually
gives GlavaSharp both-compositor support — GLava's mainline branch talks
to Xlib directly and is X11-only (its `unstable` branch has experimented
with GLFW too, for the same reason).
