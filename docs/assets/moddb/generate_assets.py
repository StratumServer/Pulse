#!/usr/bin/env python3
"""Generates the small animated rain divider tile used on the ModDB listing
page, between sections. The hero banner and the side-gutter rain tile are
generated separately (generate_hero.py, generate_gutter.py, next to this
file); this script only owns the divider tile.

Regenerate with: python3 docs/assets/moddb/generate_assets.py
Requires Pillow (pip install pillow). Uses Noto Sans CJK for the rain tile's
katakana glyphs if present (Linux: usually /usr/share/fonts/noto-cjk/); falls
back to a plain block character if the font is missing, so the script still
runs, just less pretty.

The rain tile wraps its glyph columns by exactly one glyph height per loop,
so frame 0 and the frame after the last one are pixel-identical and the loop
has no visible seam.

Alpha note: every frame is composed on its own fresh RGBA image and
flattened (or left with real alpha) *before* it is handed to Pillow's GIF
or WebP encoder. Never draw with ImageDraw straight onto a 'P' (palette)
mode image and never call convert('P') before compositing: palettizing
first snaps semi-transparent / anti-aliased edge pixels to the nearest
palette entry, which is what causes the halo/banding pitfall around glow
and text edges. This asset keeps full RGBA (lossless WebP) since it is
composited over page content.
"""

import os
import random

from PIL import Image, ImageDraw, ImageFont

OUT_DIR = os.path.dirname(os.path.abspath(__file__))

GREEN = (0, 255, 65)

_KATAKANA = "ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ0123456789"


def _rain_font(glyph_size):
    font_path = "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc"
    try:
        return ImageFont.truetype(font_path, glyph_size - 4, index=0)
    except Exception:
        return ImageFont.load_default()


def _rain_frames(tile_w, tile_h, glyph_size, n_cols, seed, step=1):
    """Builds an RGBA frame list for a falling-katakana rain tile that tiles
    seamlessly in *both* directions: horizontally because every column sits in
    its own tile_w/n_cols-wide slot (the same trick as repeating one tile
    side by side), and vertically because tile_h is an exact multiple of
    glyph_size, with no partial glyph and no padding row. A whole number of
    glyphs (tile_h // glyph_size) exactly fills the column with no gap and no
    overlap, so stacking a second copy of this same tile directly under the
    first continues the same even spacing rather than visibly repeating or
    seaming, the same "wraps exactly modulo its height" property the old
    pulse-rain-full.webp had. The animation just cycles which glyph occupies
    which slot, so every frame keeps that same exact tiling.
    """
    assert tile_h % glyph_size == 0, "tile_h must be an exact multiple of glyph_size to tile seamlessly"
    rows = tile_h // glyph_size

    font = _rain_font(glyph_size)
    rng = random.Random(seed)  # fixed seed: reproducible regeneration
    col_glyphs = [[rng.choice(_KATAKANA) for _ in range(rows)] for _ in range(n_cols)]
    col_spacing = tile_w / n_cols
    col_x = [int(col_spacing * c + (col_spacing - glyph_size) / 2) for c in range(n_cols)]

    # step must divide glyph_size so the last frame's shift plus one more step
    # lands exactly back on frame 0 -> still a seamless loop, just fewer frames.
    assert glyph_size % step == 0, "step must divide glyph_size to keep the loop seamless"
    n_frames = glyph_size // step
    frames = []
    for i in range(n_frames):
        f = i * step
        # Fresh RGBA canvas per frame; composited, never palettized before this point.
        layer = Image.new("RGBA", (tile_w, tile_h), (0, 0, 0, 0))
        draw = ImageDraw.Draw(layer)
        for c in range(n_cols):
            for row in range(rows):
                y = (row * glyph_size - f) % tile_h
                # Brightness fades from bright at the top of the tile to dim at the
                # bottom, so it reads as a falling trail rather than a static grid;
                # because the cycle is exactly tile_h, this fade also repeats
                # cleanly at each tile boundary once stacked.
                brightness = max(0.15, 1.0 - (y / tile_h))
                alpha = int(255 * brightness)
                color = (
                    int(GREEN[0] * brightness),
                    int(GREEN[1] * brightness) + 40,
                    int(GREEN[2] * brightness),
                    alpha,
                )
                draw.text((col_x[c], y), col_glyphs[c][row], font=font, fill=color)
        frames.append(layer)

    return frames


def _save_rain_webp(frames, name, duration):
    out_path = os.path.join(OUT_DIR, name)
    frames[0].save(
        out_path,
        format="WEBP",
        save_all=True,
        append_images=frames[1:],
        duration=duration,
        loop=0,
        lossless=True,
        method=6,
    )
    print(out_path, os.path.getsize(out_path), "bytes,", len(frames), "frames,", frames[0].size)


def make_rain_divider_tile():
    # A short, single-column tile repeated many times *horizontally* (plain
    # <img> tags in a row, no gaps) to build the thin rain bands between
    # sections.
    frames = _rain_frames(tile_w=40, tile_h=80, glyph_size=20, n_cols=1, seed=20260928)
    _save_rain_webp(frames, "pulse-rain-tile.webp", duration=90)


if __name__ == "__main__":
    make_rain_divider_tile()
