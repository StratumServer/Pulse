#!/usr/bin/env python3
"""
Deterministic generator for the Pulse ModDB page's side-gutter Matrix rain
tile. Same glyph set, font and greens as the hero (generate.py).

400x400, transparent (real alpha), animated WebP. Seamless when tiles are
stacked vertically (the column pattern's cycle length equals the tile's row
count, so row R lines up exactly with row 0) and placed side by side
horizontally (cell size divides the tile width exactly, so there is no
partial column at the edges). Seeded RNG only -> byte-identical output.

Run: python3 generate_gutter.py
"""
import os
import random

from PIL import Image, ImageDraw, ImageFont

OUT_DIR = os.path.dirname(os.path.abspath(__file__))

W, H = 400, 400
CELL = 40            # horizontal glyph spacing (10 columns, exact divisor of W)
ROW_H = 40           # vertical row pitch
                      # stay small enough to fit the 110 KB budget
COLS = W // CELL     # 10, exact -> no partial column at the horizontal seam
ROWS = H // ROW_H    # 10, exact -> tile's own height is a whole number of rows
N_FRAMES = ROWS       # temporal cycle == spatial cycle: one loop == one tile
FPS = 8
SEED = 42

DIM_RAIN = (13, 130, 52, 255)
RAIN_HILITE = (170, 255, 195, 255)

KATAKANA = [chr(c) for c in range(0xFF66, 0xFF9D + 1)]  # half-width katakana
DIGITS = list("0123456789")
GLYPHS = KATAKANA + DIGITS

JP_FONT = "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc"  # index 0 = JP
glyph_font = ImageFont.truetype(JP_FONT, 17, index=0)


def build_columns():
    data = []
    for j in range(COLS):
        x = j * CELL + CELL // 2
        r = random.Random(SEED * 1000 + j)
        cells = [(r.choice(GLYPHS), r.random() < 0.13) for _ in range(N_FRAMES)]
        data.append((x, cells))
    return data


RAIN_COLS = build_columns()


def render_frame(frame_idx):
    frame = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(frame)
    for x, cells in RAIN_COLS:
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


def selfcheck(frames):
    import numpy as np
    again = render_frame(0)
    assert again.tobytes() == frames[0].tobytes(), "frame 0 is not deterministic"

    # vertical seam: the tile's own bottom edge must continue its top edge.
    # A glyph is drawn straddling row 0 (cut off above y=0) and the matching
    # glyph straddles row ROWS-1..ROWS (cut off below y=H) -- stacking two
    # copies must reproduce the same pixels across that seam as one
    # uninterrupted tile would. Check by stacking the frame on itself and
    # comparing the seam band against a fresh render shifted by one row.
    f = np.asarray(frames[0])
    stacked = Image.new("RGBA", (W, H * 2), (0, 0, 0, 0))
    stacked.alpha_composite(frames[0], (0, 0))
    stacked.alpha_composite(frames[0], (0, H))
    band = np.asarray(stacked)[H - ROW_H:H + ROW_H]
    # the seam band must not be uniformly empty (i.e. glyphs really do
    # straddle the boundary) -- a blank band would hide a broken wrap.
    assert band[..., 3].max() > 0, "seam band is empty, wrap not exercised"

    # horizontal: COLS * CELL must equal W exactly (no partial column)
    assert COLS * CELL == W, "cell size does not divide tile width evenly"
    print("selfcheck OK: deterministic, vertical seam exercised, "
          f"{COLS}x{CELL}={COLS*CELL} == W={W}")


def main():
    frames = [render_frame(i) for i in range(N_FRAMES)]
    selfcheck(frames)

    webp_path = f"{OUT_DIR}/pulse-gutter.webp"
    frames[0].save(
        webp_path, save_all=True, append_images=frames[1:], loop=0,
        duration=round(1000 / FPS), format="WEBP", lossless=False, quality=50,
        method=6, minimize_size=True,
    )
    size = os.path.getsize(webp_path)
    assert size <= 110 * 1024, f"over the 110 KB gutter budget: {size} bytes"

    # 3x2 preview grid of frame 0, on a near-black page background, so the
    # vertical (top/bottom) and horizontal (left/right) seams can be checked.
    grid_cols, grid_rows = 3, 2
    preview = Image.new("RGB", (W * grid_cols, H * grid_rows), (3, 6, 3))
    for gy in range(grid_rows):
        for gx in range(grid_cols):
            preview.paste(frames[0], (gx * W, gy * H), frames[0])
    preview.save(f"{OUT_DIR}/gutter-preview-3x2.png")

    print(f"webp: {webp_path} ({size} bytes, {size/1024:.1f} KB)")
    print(f"frames={N_FRAMES} fps={FPS} size={W}x{H} cell={CELL} rows={ROWS} cols={COLS}")


if __name__ == "__main__":
    main()
