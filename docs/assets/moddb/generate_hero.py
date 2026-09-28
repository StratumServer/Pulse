#!/usr/bin/env python3
"""
Deterministic generator for the Pulse ModDB hero banner (final = "banner"
variant, fixed against the judges' notes).

Layout: Neo's glasses (big, hero-weight) on the left with a full bright ECG
trace always visible in both lenses; a single pulse dot with a smooth tapered
trail sweeps continuously left-lens -> right-lens -> out onto a wire that
curves (not a diagonal cable) under PULSE / the tagline and beats four times
across the banner to the right edge, then wraps back to the left lens.
Matrix rain (half-width katakana + digits) fills the background, dimmed
behind the glasses and the text block so both stay legible. Rendered at 2x
and Lanczos-downscaled for antialiased rims. Seeded RNG only -> byte-identical
output on every run.

Run: python3 generate.py
"""
import bisect
import hashlib
import math
import os
import random

from PIL import Image, ImageDraw, ImageFont, ImageFilter

# ---------------------------------------------------------------- constants

OUT_DIR = os.path.dirname(os.path.abspath(__file__))

W, H = 900, 400          # final (native desktop) size
SS = 2                    # supersample factor, fixes non-AA rims
RW, RH = W * SS, H * SS
N_FRAMES = 24              # 1.5s loop
FPS = 16
SEED = 42

BLACK = (0, 0, 0, 255)
BRIGHT = (0, 255, 65, 255)        # #00ff41
HEAD_WHITE = (214, 255, 227, 255)  # near-white-green comet head
DIM_FRAME = (42, 122, 68, 255)     # #2a7a44 rim
DIM_RAIN = (13, 130, 52, 255)      # base rain green
RAIN_HILITE = (170, 255, 195, 255)  # bright rain head
LENS_FILL = (2, 12, 5, 255)
LENS_SPEC = (157, 255, 176, 22)
TAGLINE_COL = (196, 255, 214, 235)  # lighter, upright tagline

KATAKANA = [chr(c) for c in range(0xFF66, 0xFF9D + 1)]  # half-width katakana
DIGITS = list("0123456789")
GLYPHS = KATAKANA + DIGITS

JP_FONT = "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc"  # index 0 = JP
MONO_BOLD = "/usr/share/fonts/TTF/DejaVuSansMono-Bold.ttf"
MONO_REG = "/usr/share/fonts/TTF/DejaVuSansMono.ttf"


def S(v):
    return v * SS


rng_glyph_font = ImageFont.truetype(JP_FONT, S(17), index=0)
pulse_font = ImageFont.truetype(MONO_BOLD, S(78))
frame_num_font = ImageFont.truetype(MONO_BOLD, 16)

TEXT_X = 485
TAGLINE_TEXT = "Wake up, admin. Your server has a pulse."


def fit_tagline_font(text, max_width_logical, max_size=26, min_size=13):
    """Largest upright mono size that keeps the tagline on-canvas."""
    size = max_size
    while size > min_size:
        f = ImageFont.truetype(MONO_REG, S(size))
        if f.getlength(text) / SS <= max_width_logical:
            return f
        size -= 1
    return ImageFont.truetype(MONO_REG, S(min_size))


tagline_font = fit_tagline_font(TAGLINE_TEXT, W - TEXT_X - 20)

# ---------------------------------------------------------------- helpers


def new_layer():
    return Image.new("RGBA", (RW, RH), (0, 0, 0, 0))


def glow_composite(base, layer, blur=6, glow_alpha_mult=1.0):
    glow = layer.filter(ImageFilter.GaussianBlur(S(blur)))
    if glow_alpha_mult != 1.0:
        r, g, b, a = glow.split()
        a = a.point(lambda v: min(255, int(v * glow_alpha_mult)))
        glow = Image.merge("RGBA", (r, g, b, a))
    base.alpha_composite(glow)
    base.alpha_composite(layer)


def draw_rotated_ellipse(layer, cx, cy, rx, ry, angle_deg, fill):
    pad = S(4)
    w, h = rx * 2 + pad * 2, ry * 2 + pad * 2
    patch = Image.new("RGBA", (int(w), int(h)), (0, 0, 0, 0))
    d = ImageDraw.Draw(patch)
    d.ellipse([pad, pad, pad + rx * 2, pad + ry * 2], fill=fill)
    patch = patch.rotate(angle_deg, resample=Image.BICUBIC, expand=True)
    px = int(cx - patch.width / 2)
    py = int(cy - patch.height / 2)
    layer.alpha_composite(patch, dest=(px, py))


def cubic_bezier(p0, p1, p2, p3, steps):
    pts = []
    for i in range(steps + 1):
        t = i / steps
        mt = 1 - t
        x = mt**3 * p0[0] + 3 * mt**2 * t * p1[0] + 3 * mt * t**2 * p2[0] + t**3 * p3[0]
        y = mt**3 * p0[1] + 3 * mt**2 * t * p1[1] + 3 * mt * t**2 * p2[1] + t**3 * p3[1]
        pts.append((x, y))
    return pts


def smooth_polyline(draw, pts, color, width):
    """Round-jointed stroke: consecutive segments + a circle at every vertex."""
    draw.line(pts, fill=color, width=width, joint="curve")
    r = width / 2
    for x, y in pts:
        draw.ellipse([x - r, y - r, x + r, y + r], fill=color)


# ---------------------------------------------------------------- geometry (logical px)

LEFT_CX, LEFT_CY = 175, 195
RIGHT_CX, RIGHT_CY = 365, 195
LENS_RX, LENS_RY = 85, 64

# one heartbeat shape: box 126 wide, baseline at HEART_BASE. Small bump, dip,
# tall spike, deep S, flat -- same silhouette as pulse-logo-matrix.svg.
HEART_PTS = [(0, 132), (32, 132), (42, 118), (54, 146), (66, 96),
             (80, 154), (90, 132), (126, 132)]
HEART_W = 126
HEART_BASE = 132
SPIKE_IDX = 4  # index of the tall spike point (66, 96) in HEART_PTS


def heart_points(start_x, base_y, scale_x, scale_y):
    return [(start_x + px * scale_x, base_y + (py - HEART_BASE) * scale_y)
            for px, py in HEART_PTS]


def lens_trace(cx, cy):
    scale_x = (LENS_RX * 2) / HEART_W
    scale_y = 0.85
    return heart_points(cx - LENS_RX, cy, scale_x, scale_y)


LEFT_TRACE = lens_trace(LEFT_CX, LEFT_CY)
RIGHT_TRACE = lens_trace(RIGHT_CX, RIGHT_CY)


def rim_point(cx, cy, rx, ry, dx):
    """Point on the lens ellipse's rim, offset dx from its center."""
    ratio = max(-1.0, min(1.0, dx / rx))
    dy = ry * math.sqrt(max(0.0, 1 - ratio**2))
    return (cx + dx, cy - dy)


# arched nose bridge between the lenses, anchored ON the rim curves (not
# floating above them the way fixed-offset points would at this lens size)
_lb = rim_point(LEFT_CX, LEFT_CY, LENS_RX, LENS_RY, LENS_RX * 0.75)
_rb = rim_point(RIGHT_CX, RIGHT_CY, LENS_RX, LENS_RY, -LENS_RX * 0.75)
BRIDGE_PTS = [_lb, ((_lb[0] + _rb[0]) / 2, min(_lb[1], _rb[1]) - 10), _rb]

# bridge connector between the lenses (part of the travelling path, not drawn
# as its own visible line -- the decorative bridge arc is drawn separately)
BRIDGE = [LEFT_TRACE[-1], RIGHT_TRACE[0]]

# external wire: leaves the right lens where a temple arm would be, curves
# smoothly (horizontal tangent first, no diagonal "cable") down to a baseline
# well clear of the tagline, then beats 4x to the right edge.
EXIT_PT = (RIGHT_CX + LENS_RX, RIGHT_CY)         # (450, 195)
WIRE_BASE_Y = 300
# The dip must fully clear the tagline's bounding box (starts at TEXT_X, a
# little below the lens) before running under the text -- so it finishes
# BEFORE reaching TEXT_X, not diagonally through it.
CURVE_END = (TEXT_X - 16, WIRE_BASE_Y)
EXT_CURVE = cubic_bezier(EXIT_PT, (EXIT_PT[0] + 6, RIGHT_CY),
                          (EXIT_PT[0] + 12, WIRE_BASE_Y), CURVE_END, 20)

N_BEATS = 4
BEAT_PERIOD = (W - CURVE_END[0]) / N_BEATS
EXT_BEATS = []
x = CURVE_END[0]
for _ in range(N_BEATS):
    seg = heart_points(x, WIRE_BASE_Y, BEAT_PERIOD / HEART_W, 1.05)
    EXT_BEATS.extend(seg[1:])
    x += BEAT_PERIOD

EXT_PATH = EXT_CURVE + EXT_BEATS   # (450,195) -> (900,300)

# ---- single continuous path the pulse travels: left lens -> bridge ->
# right lens -> wire. Looping back from the end to the start is a deliberate
# "monitor sweep" wrap (like a real ECG display re-tracing from the left).
FULL_PATH = LEFT_TRACE + BRIDGE[1:] + RIGHT_TRACE[1:] + EXT_PATH[1:]

_cum = [0.0]
for a, b in zip(FULL_PATH, FULL_PATH[1:]):
    _cum.append(_cum[-1] + math.hypot(b[0] - a[0], b[1] - a[1]))
TOTAL_LEN = _cum[-1]

LEFT_START_LEN = 0.0
LEFT_END_LEN = _cum[len(LEFT_TRACE) - 1]
RIGHT_START_LEN = _cum[len(LEFT_TRACE)]
RIGHT_END_LEN = _cum[len(LEFT_TRACE) + len(RIGHT_TRACE) - 1]
LEFT_SPIKE_LEN = _cum[SPIKE_IDX]
RIGHT_SPIKE_LEN = _cum[len(LEFT_TRACE) + SPIKE_IDX]


def point_at_length(target):
    target = target % TOTAL_LEN
    i = bisect.bisect_left(_cum, target)
    i = max(1, min(i, len(FULL_PATH) - 1))
    seg_len = _cum[i] - _cum[i - 1]
    t = 0.0 if seg_len == 0 else (target - _cum[i - 1]) / seg_len
    ax, ay = FULL_PATH[i - 1]
    bx, by = FULL_PATH[i]
    return ax + (bx - ax) * t, ay + (by - ay) * t


def hann_window(pos, lo, hi):
    """0 outside [lo,hi], raised-cosine bump peaking at the middle inside it."""
    if pos <= lo or pos >= hi:
        return 0.0
    t = (pos - lo) / (hi - lo)
    return 0.5 - 0.5 * math.cos(2 * math.pi * t)


# frame 0 should land just past a spike (poster frame), not on a flat run
STEP_LEN = TOTAL_LEN / N_FRAMES
PHASE0 = (LEFT_SPIKE_LEN + 10) % TOTAL_LEN


def head_len_at(frame_idx):
    return (frame_idx * STEP_LEN + PHASE0) % TOTAL_LEN


def flare_at(frame_idx):
    """(flare_left, flare_right) in [0,1], smooth bumps while the head is
    inside that lens's stretch of the path."""
    pos = head_len_at(frame_idx)
    fl = hann_window(pos, LEFT_START_LEN, LEFT_END_LEN)
    fr = hann_window(pos, RIGHT_START_LEN, RIGHT_END_LEN)
    return fl, fr


# ---------------------------------------------------------------- rain

RAIN_CELL = S(26)
RAIN_ROWS = RH // RAIN_CELL + 2


def dim_factor(x_logical):
    """Column brightness multiplier: dim behind the glasses and the text
    block, a little denser at the outer margins."""

    def dip(cx, half_width, floor):
        d = abs(x_logical - cx) / half_width
        d = min(1.0, d)
        return floor + (1.0 - floor) * (0.5 - 0.5 * math.cos(math.pi * d))

    glasses = dip(cx=270, half_width=230, floor=0.20)
    text = dip(cx=660, half_width=250, floor=0.12)
    return min(glasses, text)


def build_rain_columns():
    cols = RW // RAIN_CELL
    data = []
    for j in range(cols):
        x = j * RAIN_CELL + RAIN_CELL // 2
        alpha_scale = dim_factor(x / SS)
        r = random.Random(SEED * 1000 + j)
        cells = [(r.choice(GLYPHS), r.random() < 0.07) for _ in range(N_FRAMES)]
        data.append((x, alpha_scale, cells))
    return data


RAIN_COLS = build_rain_columns()


def render_rain(frame_idx):
    layer = new_layer()
    d = ImageDraw.Draw(layer)
    for x, alpha_scale, cells in RAIN_COLS:
        for row in range(-1, RAIN_ROWS):
            # (row - frame_idx) mod N -> the same glyph moves to row+1 next
            # frame, i.e. the pattern falls DOWNWARD.
            src = (row - frame_idx) % N_FRAMES
            glyph, bright = cells[src]
            y = row * RAIN_CELL
            alpha = alpha_scale * (2.1 if bright else 0.85)
            alpha = max(0.0, min(1.0, alpha))
            base = RAIN_HILITE if bright else DIM_RAIN
            color = (base[0], base[1], base[2], int(255 * alpha))
            d.text((x, y), glyph, font=rng_glyph_font, fill=color, anchor="mm")
    return layer


# ---------------------------------------------------------------- static layers


def build_glasses_layer():
    layer = new_layer()
    d = ImageDraw.Draw(layer)
    for cx, cy in ((LEFT_CX, LEFT_CY), (RIGHT_CX, RIGHT_CY)):
        cx, cy, rx, ry = S(cx), S(cy), S(LENS_RX), S(LENS_RY)
        d.ellipse([cx - rx - S(4), cy - ry - S(4), cx + rx + S(4), cy + ry + S(4)],
                  fill=(0, 0, 0, 190))
        d.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=LENS_FILL)
    for cx, cy in ((LEFT_CX, LEFT_CY), (RIGHT_CX, RIGHT_CY)):
        draw_rotated_ellipse(layer, S(cx - 28), S(cy - 26), S(34), S(16), -18, LENS_SPEC)

    base = new_layer()
    base.alpha_composite(layer)

    # persistent base ECG trace, always fully visible in both lenses
    trace_layer = new_layer()
    td = ImageDraw.Draw(trace_layer)
    for trace in (LEFT_TRACE, RIGHT_TRACE):
        pts = [(S(x), S(y)) for x, y in trace]
        smooth_polyline(td, pts, (0, 190, 68, 235), S(6))
    glow_composite(base, trace_layer, blur=4, glow_alpha_mult=1.1)

    frame_layer = new_layer()
    fd = ImageDraw.Draw(frame_layer)
    for cx, cy in ((LEFT_CX, LEFT_CY), (RIGHT_CX, RIGHT_CY)):
        cx, cy, rx, ry = S(cx), S(cy), S(LENS_RX), S(LENS_RY)
        fd.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], outline=DIM_FRAME, width=S(5))
    bridge_pts = [(S(x), S(y)) for x, y in BRIDGE_PTS]
    smooth_polyline(fd, bridge_pts, DIM_FRAME, S(6))
    base.alpha_composite(frame_layer)
    return base


def build_flare_layer(frame_idx):
    """Extra brightness/thickness over the base trace while the sweep head
    is crossing that lens (the 'beat')."""
    fl, fr = flare_at(frame_idx)
    if fl < 0.02 and fr < 0.02:
        return None
    layer = new_layer()
    d = ImageDraw.Draw(layer)
    for trace, amt in ((LEFT_TRACE, fl), (RIGHT_TRACE, fr)):
        if amt < 0.02:
            continue
        pts = [(S(x), S(y)) for x, y in trace]
        width = S(6) + S(5) * amt
        alpha = int(120 * amt)
        smooth_polyline(d, pts, (120, 255, 160, alpha), int(width))
    base = new_layer()
    glow_composite(base, layer, blur=6, glow_alpha_mult=1.3 * max(fl, fr))
    return base


def build_ext_wire_layer():
    base = new_layer()
    layer = new_layer()
    d = ImageDraw.Draw(layer)
    pts = [(S(x), S(y)) for x, y in EXT_PATH]
    smooth_polyline(d, pts, (0, 200, 60, 150), S(4))
    glow_composite(base, layer, blur=4, glow_alpha_mult=0.8)
    return base


def measure_cap_height(font):
    bbox = font.getbbox("E")
    return bbox[3] - bbox[1]


def build_text_layer(beat_boost=0.0):
    base = new_layer()
    text_x, text_y = S(TEXT_X), S(118)
    letters = "PULSE"
    spacing = S(16)
    pulse_layer = new_layer()
    pd = ImageDraw.Draw(pulse_layer)
    x = text_x
    for ch in letters:
        pd.text((x, text_y), ch, font=pulse_font, fill=BRIGHT)
        bbox = pd.textbbox((x, text_y), ch, font=pulse_font)
        x = bbox[2] + spacing
    glow_composite(base, pulse_layer, blur=7, glow_alpha_mult=1.15 + 0.5 * beat_boost)
    if beat_boost > 0.05:
        boost_layer = new_layer()
        bd = ImageDraw.Draw(boost_layer)
        x = text_x
        for ch in letters:
            bd.text((x, text_y), ch, font=pulse_font, fill=(220, 255, 230, int(140 * beat_boost)))
            bbox = bd.textbbox((x, text_y), ch, font=pulse_font)
            x = bbox[2] + spacing
        base.alpha_composite(boost_layer)

    tagline_layer = new_layer()
    td = ImageDraw.Draw(tagline_layer)
    td.text((S(TEXT_X + 2), S(219)), TAGLINE_TEXT, font=tagline_font, fill=TAGLINE_COL)
    glow_composite(base, tagline_layer, blur=2, glow_alpha_mult=0.5)
    return base


GLASSES_LAYER = build_glasses_layer()
EXT_WIRE_LAYER = build_ext_wire_layer()
TEXT_LAYER_STATIC = build_text_layer(0.0)
CAP_H = measure_cap_height(pulse_font) / SS
assert LENS_RY * 2 >= 1.5 * CAP_H, (
    f"lenses ({LENS_RY*2}px tall) must be >= 1.5x PULSE cap height ({CAP_H}px)")

# ---------------------------------------------------------------- sweep (animated)

TRAIL_N = 46
TRAIL_STEP = 4.2  # logical arclength px between trail samples


def render_sweep(frame_idx):
    layer = new_layer()
    d = ImageDraw.Draw(layer)
    head_len = head_len_at(frame_idx)  # logical arclength units
    for k in range(TRAIL_N, -1, -1):
        length = head_len - k * TRAIL_STEP
        x, y = point_at_length(length)
        x, y = S(x), S(y)
        t = 1.0 - k / TRAIL_N
        r = S(3) + S(11) * t
        alpha = int(255 * (0.06 + 0.94 * t**1.3))
        color = (HEAD_WHITE[0], HEAD_WHITE[1], HEAD_WHITE[2], 255) if k == 0 \
            else (0, 255, 65, alpha)
        d.ellipse([x - r, y - r, x + r, y + r], fill=color)
    base = new_layer()
    glow_composite(base, layer, blur=6, glow_alpha_mult=1.6)
    return base


# ---------------------------------------------------------------- compose frames


def render_frame_ss(idx):
    frame = Image.new("RGBA", (RW, RH), BLACK)
    frame.alpha_composite(render_rain(idx))
    frame.alpha_composite(GLASSES_LAYER)
    flare = build_flare_layer(idx)
    if flare is not None:
        frame.alpha_composite(flare)
    frame.alpha_composite(EXT_WIRE_LAYER)
    frame.alpha_composite(render_sweep(idx))
    fl, fr = flare_at(idx)
    beat_boost = max(fl, fr)
    frame.alpha_composite(build_text_layer(beat_boost) if beat_boost > 0.05 else TEXT_LAYER_STATIC)
    return frame


def render_frame(idx):
    big = render_frame_ss(idx)
    return big.resize((W, H), Image.LANCZOS)


def selfcheck(frames):
    """One runnable check: determinism, rain direction, hero geometry."""
    import numpy as np

    # determinism: re-rendering frame 0 must be byte-identical
    again = render_frame(0)
    assert again.tobytes() == frames[0].tobytes(), "frame 0 is not deterministic"

    # rain falls downward: shifting rain(frame+1) UP by one cell should
    # match rain(frame) far better than the unshifted or downward-shifted
    # comparisons (empirical check, not just tautology-from-the-formula).
    a = np.asarray(render_rain(5).convert("L"), dtype=np.int32)
    b = np.asarray(render_rain(6).convert("L"), dtype=np.int32)
    c = RAIN_CELL

    def diff(shift):
        if shift == 0:
            return np.abs(a - b).mean()
        if shift > 0:  # compare b shifted up by `shift` rows against a
            return np.abs(a[:-shift] - b[shift:]).mean()
        return np.abs(a[-shift:] - b[:shift]).mean()

    d_none, d_down, d_up = diff(0), diff(c), diff(-c)
    assert d_down < d_up and d_down < d_none, (
        f"rain does not fall downward (none={d_none:.1f} down={d_down:.1f} up={d_up:.1f})")

    print(f"selfcheck OK: deterministic; rain shift-diff none={d_none:.1f} "
          f"down={d_down:.1f} up={d_up:.1f} (down must be smallest)")


def main():
    frames = [render_frame(i) for i in range(N_FRAMES)]
    selfcheck(frames)

    frames[0].convert("RGB").save(f"{OUT_DIR}/frame0-full.png")
    scaled = frames[0].convert("RGB").resize((360, round(H * 360 / W)), Image.LANCZOS)
    scaled.save(f"{OUT_DIR}/frame0-360.png")

    idxs = [round(i * (N_FRAMES - 1) / 7) for i in range(8)]
    thumb_w, thumb_h = 300, round(300 * H / W)
    cols, rows = 4, 2
    sheet = Image.new("RGB", (thumb_w * cols, thumb_h * rows), (0, 0, 0))
    sd = ImageDraw.Draw(sheet)
    for pos, fidx in enumerate(idxs):
        thumb = frames[fidx].convert("RGB").resize((thumb_w, thumb_h), Image.LANCZOS)
        cx, cy = (pos % cols) * thumb_w, (pos // cols) * thumb_h
        sheet.paste(thumb, (cx, cy))
        sd.text((cx + 6, cy + 6), f"#{fidx}", font=frame_num_font,
                 fill=(0, 255, 65), stroke_width=2, stroke_fill=(0, 0, 0))
    sheet.save(f"{OUT_DIR}/contact-sheet.png")

    webp_path = f"{OUT_DIR}/pulse-hero.webp"
    frames[0].save(
        webp_path, save_all=True, append_images=frames[1:], loop=0,
        duration=round(1000 / FPS), format="WEBP", lossless=False, quality=25,
        method=6, minimize_size=True,
    )
    size = os.path.getsize(webp_path)
    assert size <= 400 * 1024, f"over hard ceiling: {size} bytes"

    h = hashlib.sha256(open(webp_path, "rb").read()).hexdigest()[:16]
    print(f"webp: {webp_path} ({size} bytes, {size/1024:.1f} KB) sha256:{h}")
    print(f"frames={N_FRAMES} fps={FPS} size={W}x{H} ss={SS} cap_h={CAP_H:.1f} lens_h={LENS_RY*2}")


if __name__ == "__main__":
    main()
