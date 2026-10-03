#!/usr/bin/env python3
"""
Deterministic generator for the Pulse ModDB page's side-gutter Matrix rain
tiles. Same glyph set, font and greens as the hero (generate_hero.py).

Three variants of a 400x400 tile, transparent (real alpha) and animated, one
seed each, everything else identical. Each is written out as a 400x1600 WebP
(see STACK below). A wide page repeats the same tile many times side by
side, and one tile repeating verbatim reads as an obvious grid. Rotating
variants across page columns breaks that up.

Each variant is seamless when stacked vertically on itself (the column
pattern's cycle length equals the tile's row count, so row R lines up
exactly with row 0) and placed side by side horizontally (cell size divides
the tile width exactly, so there is no partial column at the edges). Those
two properties depend only on a tile's own geometry, never on a
neighbour's content, so mixing variants left-to-right is always safe; the
one rule that does not come for free is vertical -- a falling glyph that
runs off a tile's bottom must land back at the SAME variant's top, so any
page column of stacked tiles has to stick to one variant for its whole
height (enforced in docs/moddb/listing.html, not here). Seeded RNG only ->
byte-identical output per variant.

Each variant is written as STACK copies of its tile stacked vertically
(400x1600), which the seamless vertical property makes identical on screen
to STACK separate 400x400 tiles. That is purely to cut the number of <img>
tags on the page: ModDB stores a mod description in a TEXT column and
refuses anything over 65535 bytes, and 16 rows of 13 single tiles alone
took about 46 KB of markup.

Run: python3 generate_gutter.py
"""
import os
import random

from PIL import Image, ImageDraw, ImageFont

OUT_DIR = os.path.dirname(os.path.abspath(__file__))

W, H = 400, 400
CELL = 40            # horizontal glyph spacing (10 columns, exact divisor of W)
ROW_H = 40           # vertical row pitch
COLS = W // CELL     # 10, exact -> no partial column at the horizontal seam
ROWS = H // ROW_H    # 10, exact -> tile's own height is a whole number of rows
N_FRAMES = ROWS       # temporal cycle == spatial cycle: one loop == one tile
FPS = 8
STACK = 4             # tiles per written image, stacked vertically

# (output filename, seed). Variant A keeps the seed of the original single
# tile, so it draws that tile's rain, now stacked STACK high.
VARIANTS = [
    ("pulse-gutter-tall.webp", 42),
    ("pulse-gutter-tall-b.webp", 43),
    ("pulse-gutter-tall-c.webp", 44),
]

DIM_RAIN = (13, 130, 52, 255)
RAIN_HILITE = (170, 255, 195, 255)

KATAKANA = [chr(c) for c in range(0xFF66, 0xFF9D + 1)]  # half-width katakana
DIGITS = list("0123456789")
GLYPHS = KATAKANA + DIGITS

JP_FONT = "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc"  # index 0 = JP
glyph_font = ImageFont.truetype(JP_FONT, 17, index=0)


def build_columns(seed):
    data = []
    for j in range(COLS):
        x = j * CELL + CELL // 2
        r = random.Random(seed * 1000 + j)
        cells = [(r.choice(GLYPHS), r.random() < 0.13) for _ in range(N_FRAMES)]
        data.append((x, cells))
    return data


def render_frame(frame_idx, columns):
    frame = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(frame)
    for x, cells in columns:
        for row in range(ROWS):
            # (row - frame) mod ROWS: same falling-glyph rule as the hero,
            # and periodic in row with period ROWS -> tiles seamlessly.
            src = (row - frame_idx) % ROWS
            glyph, bright = cells[src]
            y = row * ROW_H + ROW_H // 2
            base = RAIN_HILITE if bright else DIM_RAIN
            alpha = 255 if bright else 185
            d.text((x, y), glyph, font=glyph_font,
                    fill=(base[0], base[1], base[2], alpha), anchor="mm")
    return frame


def selfcheck(frames, columns, label):
    import numpy as np
    again = render_frame(0, columns)
    assert again.tobytes() == frames[0].tobytes(), f"{label}: frame 0 is not deterministic"

    # vertical seam: glyphs are centred in their rows and smaller than a row,
    # so none is cut off at the tile's top or bottom edge, and the last row
    # is followed by the next copy's first row at the same pitch. That holds
    # by construction and is not compared pixel by pixel here. What is
    # checked is that the band of one row on each side of the seam, taken
    # from two copies of the frame stacked, is not blank.
    stacked = Image.new("RGBA", (W, H * 2), (0, 0, 0, 0))
    stacked.alpha_composite(frames[0], (0, 0))
    stacked.alpha_composite(frames[0], (0, H))
    band = np.asarray(stacked)[H - ROW_H:H + ROW_H]
    # a blank band would say nothing about the seam: there must be glyphs
    # right next to it
    assert band[..., 3].max() > 0, f"{label}: seam band is empty, wrap not exercised"

    # horizontal: COLS * CELL must equal W exactly (no partial column)
    assert COLS * CELL == W, "cell size does not divide tile width evenly"


def mixed_grid_preview(variant_frames):
    """3x2 grid, one variant per column, the same variant top and bottom in
    that column -- exactly the rule listing.html follows for its page-wide
    grid. Renders both claims in one image: no vertical seam within a
    column (same variant stacked twice), and nothing to mismatch at the
    column boundaries either, since a glyph never straddles two tiles
    horizontally in the first place (COLS * CELL == W already guarantees
    that column-of-glyphs sit entirely inside one tile)."""
    names = [name for name, _ in VARIANTS]
    preview = Image.new("RGB", (W * len(names), H * 2), (3, 6, 3))
    for gx, name in enumerate(names):
        frame0 = variant_frames[name][0]
        preview.paste(frame0, (gx * W, 0), frame0)
        preview.paste(frame0, (gx * W, H), frame0)
    return preview


def stacked(frame):
    """STACK copies of one frame, top to bottom."""
    tall = Image.new("RGBA", (W, H * STACK), (0, 0, 0, 0))
    for k in range(STACK):
        tall.alpha_composite(frame, (0, H * k))
    return tall


def main():
    variant_frames = {}
    for name, seed in VARIANTS:
        columns = build_columns(seed)
        frames = [render_frame(i, columns) for i in range(N_FRAMES)]
        selfcheck(frames, columns, name)
        variant_frames[name] = frames

        webp_path = f"{OUT_DIR}/{name}"
        tall = [stacked(frame) for frame in frames]
        tall[0].save(
            webp_path, save_all=True, append_images=tall[1:], loop=0,
            duration=round(1000 / FPS), format="WEBP", lossless=False, quality=50,
            method=6, minimize_size=True,
        )
        size = os.path.getsize(webp_path)
        # the budget is 100 KB per stacked tile, so STACK * 100 KB per written image
        assert size <= STACK * 100 * 1024, f"{name}: over the {STACK * 100} KB gutter budget: {size} bytes"
        print(f"{name}: {size} bytes ({size / 1024:.1f} KB)")

    # 3x2 preview grid mixing all variants, on a near-black page background,
    # so the vertical (top/bottom, same variant) and horizontal (left/right,
    # different variants) seams can both be checked by eye.
    preview = mixed_grid_preview(variant_frames)
    preview.save(f"{OUT_DIR}/gutter-preview-mixed-3x2.png")

    print(f"frames={N_FRAMES} fps={FPS} size={W}x{H} stacked x{STACK} cell={CELL} rows={ROWS} cols={COLS}")
    print("selfcheck OK for all variants: deterministic, vertical seam exercised, "
          f"{COLS}x{CELL}={COLS * CELL} == W={W}")


if __name__ == "__main__":
    main()
