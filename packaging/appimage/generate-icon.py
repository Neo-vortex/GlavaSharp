#!/usr/bin/env python3
"""Generates packaging/appimage/glavasharp.png.

A static 2D reduction of the aurora module's own look (see
shaders/glavasharp/aurora/): layered wavy curtains colored by altitude
(warm teal/green low, cool blue/violet high -- same idea as
aurora.glsl's "altitude picks the base hue" dynamic coloring, just
without per-frame feedback since this only needs to render once),
soft bloom, a static starfield, and atmospheric haze, on the same dark
rounded-square backdrop the previous icon used. Not run at build time --
regenerate and re-commit the PNG by hand if you want to retune it:

    python3 packaging/appimage/generate-icon.py

Requires numpy + Pillow (not a build dependency otherwise).
"""

import os

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

SS = 1024          # supersampled render size
OUT = 256           # final icon size
PAD = int(SS * 8 / 256)
RADIUS = int(SS * 40 / 256)

rng = np.random.default_rng(7)

xs = np.linspace(0.0, 1.0, SS)
ys = np.linspace(0.0, 1.0, SS)
X, Y = np.meshgrid(xs, ys)  # Y: 0 at top, 1 at bottom


def hsv2rgb(h, s, v):
    h = np.mod(h, 1.0) * 6.0
    i = np.floor(h).astype(int)
    f = h - i
    p = v * (1 - s)
    q = v * (1 - s * f)
    t = v * (1 - s * (1 - f))
    i = i % 6
    r = np.select([i == 0, i == 1, i == 2, i == 3, i == 4, i == 5], [v, q, p, p, t, v])
    g = np.select([i == 0, i == 1, i == 2, i == 3, i == 4, i == 5], [t, v, v, q, p, p])
    b = np.select([i == 0, i == 1, i == 2, i == 3, i == 4, i == 5], [p, p, t, v, v, q])
    return np.stack([r, g, b], axis=-1)


# ---- Ribbon layers: each a wavy horizontal curtain, dominated by ONE hue
# (bottom = warm teal/green, top = cool blue/violet -- same "altitude picks
# the base hue" idea aurora.glsl's dynamic coloring uses) with a gentle
# per-x wobble on top, not a full rainbow sweep across x -- that read as a
# muddy smear rather than aurora curtains. ----
layers = [
    # y_center(x),                                                    sigma, base_hue, hue_wobble, sat, val, fold_freq, fold_amt, weight
    (lambda x: 0.62 + 0.055*np.sin(2*np.pi*(1.1*x+0.05)) + 0.02*np.sin(2*np.pi*(2.6*x+0.5)), 0.11, 0.42, 0.04, 0.80, 0.95, 5.0, 0.14, 1.00),
    (lambda x: 0.48 + 0.065*np.sin(2*np.pi*(0.8*x+0.35)) + 0.018*np.sin(2*np.pi*(2.1*x+0.15)), 0.08, 0.53, 0.05, 0.70, 0.95, 4.0, 0.12, 0.85),
    (lambda x: 0.34 + 0.045*np.sin(2*np.pi*(1.4*x+0.7)) + 0.015*np.sin(2*np.pi*(3.2*x+0.05)), 0.055, 0.78, 0.04, 0.50, 0.95, 6.0, 0.10, 0.65),
]

rgb = np.zeros((SS, SS, 3))
edge_env = np.clip(np.minimum(X, 1 - X) * 6.0, 0.0, 1.0)  # soft fade near left/right edges
band_mask = np.zeros((SS, SS))

for center_fn, sigma, hue0, hue_wobble, sat, val, fold_freq, fold_amt, weight in layers:
    yc = center_fn(xs)[None, :]
    core = np.exp(-((Y - yc) ** 2) / (2 * sigma ** 2))
    # Smooth, low-frequency vertical "fold" modulation -- gentle pleats
    # while staying one continuous sheet, not separated into blobs. No
    # high-frequency content to alias when downsampled.
    fold = 1.0 - fold_amt * (0.5 + 0.5 * np.sin(2 * np.pi * fold_freq * xs + 1.7))
    intensity = core * fold[None, :] * edge_env * weight
    hue = hue0 + hue_wobble * np.sin(2 * np.pi * 1.3 * X + 2.1)
    color = hsv2rgb(hue, np.full_like(X, sat), np.full_like(X, val))
    rgb += color * intensity[..., None]
    band_mask = np.maximum(band_mask, intensity)

# Soft tonemap so overlapping layers glow instead of clipping to flat white.
rgb = 1.0 - np.exp(-rgb * 1.35)

# ---- Stars: sparse, brightness-varied points in the empty sky above the
# ribbons. ----
star_img = np.zeros((SS, SS))
n_stars = 40
sx = rng.uniform(0.06, 0.94, n_stars)
sy = rng.uniform(0.04, 0.30, n_stars)
sb = rng.uniform(0.35, 1.0, n_stars)
for x0, y0, b in zip(sx, sy, sb):
    px, py = int(x0 * SS), int(y0 * SS)
    r = 1 if b < 0.7 else 2
    y0i, y1i = max(0, py - r), min(SS, py + r + 1)
    x0i, x1i = max(0, px - r), min(SS, px + r + 1)
    star_img[y0i:y1i, x0i:x1i] = np.maximum(star_img[y0i:y1i, x0i:x1i], b)
star_rgb = star_img[..., None] * np.array([0.85, 0.90, 1.0])
star_rgb *= np.clip(1.0 - band_mask[..., None] * 3.0, 0.0, 1.0)
rgb += star_rgb

# ---- Atmospheric haze: faint cool tint growing toward the top. ----
haze_strength = np.clip((0.5 - Y) * 0.6, 0.0, 1.0) * 0.08
rgb += haze_strength[..., None] * np.array([0.05, 0.10, 0.20])

rgb = np.clip(rgb, 0.0, 1.0)
alpha_field = np.clip(band_mask * 1.3 + star_img, 0.0, 1.0)

# ---- Compose onto a dark rounded-square backdrop with a bloom pass. ----
base = Image.new("RGBA", (SS, SS), (0, 0, 0, 0))
draw = ImageDraw.Draw(base)
draw.rounded_rectangle([PAD, PAD, SS - PAD, SS - PAD], radius=RADIUS, fill=(8, 10, 18, 255))

# Bloom: blur a bright-pass copy and screen-blend it back underneath the sharp layer.
bright = np.clip((rgb - 0.30) / 0.70, 0.0, 1.0)
bloom_src = Image.fromarray((bright * 255).astype(np.uint8), mode="RGB")
bloom = bloom_src.filter(ImageFilter.GaussianBlur(radius=SS * 0.018))
bloom_arr = np.asarray(bloom).astype(np.float32) / 255.0
screened = 1.0 - (1.0 - rgb) * (1.0 - bloom_arr * 0.85)
screened = np.clip(screened, 0.0, 1.0)

fg_bloom = Image.fromarray((screened * 255).astype(np.uint8), mode="RGB").convert("RGBA")
fg_bloom.putalpha(Image.fromarray((alpha_field * 255).astype(np.uint8), mode="L"))

mask = base.split()[3]
composed = Image.alpha_composite(base, Image.composite(fg_bloom, Image.new("RGBA", (SS, SS), (0, 0, 0, 0)), mask))

final = composed.resize((OUT, OUT), Image.LANCZOS)
out_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "glavasharp.png")
final.save(out_path)
print(f"saved {out_path}")
