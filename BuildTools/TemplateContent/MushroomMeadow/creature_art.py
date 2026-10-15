"""Mushroom Meadow (full game) creature / NPC / boss / item / prop sprite generator (ART-CREATURES).

Deterministic: re-running produces byte-identical PNGs and JSON.
Run from anywhere:  python BuildTools/TemplateContent/MushroomMeadow/creature_art.py
Contact sheets:     TestResults/MushroomMeadow/creatures_preview*.png

Reuses the phase-1 palette, Canvas and Explorer (Pip) drawing style from char_art.py without changing its outputs.
"""
import hashlib
import json
import math
import os
import sys
import uuid

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import char_art as ca  # noqa: E402
from char_art import (Canvas, hx, OUT, CLEAR, RED, CREAM, SKIN, GREEN, DIRT, GOLD, BLUE, WHITE,  # noqa: E402
                      GREY, BLACK, PINK, WING)

REPO = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
SPRITES = os.path.join(REPO, "Source", "Genesis.Application.Core", "Projects", "Templates", "Assets",
                       "TwoDShowcase", "Assets", "Sprites")
PREVIEW_DIR = os.path.join(REPO, "TestResults", "MushroomMeadow")


def stable_id(*parts):
    return hashlib.md5("|".join(("mm-creature",) + parts).encode("utf-8")).hexdigest()


def hval(*parts):
    """Deterministic pseudo random 0..1 from parts."""
    h = hashlib.md5("|".join(str(p) for p in parts).encode("utf-8")).hexdigest()
    return int(h[:8], 16) / 0xFFFFFFFF


def alpha(c, a):
    return (c[0], c[1], c[2], a)


def mix(c1, c2, t):
    return tuple(int(round(c1[i] + (c2[i] - c1[i]) * t)) for i in range(3)) + (255,)


# ---------------------------------------------------------------- extra palettes
MOREL = [hx("f0d49a"), hx("cfa86a"), hx("a27846"), hx("70502c"), hx("4a321c")]
ROBE = [hx("a8d07e"), hx("74a052"), hx("4e7638"), hx("324e26")]
BEARD = [hx("ffffff"), hx("e8e4f0"), hx("bdb6cc"), hx("8c85a2")]
TRUF = [hx("a88068"), hx("7e5a46"), hx("5a3e30"), hx("3c2820")]
APRON = [hx("fff6dc"), hx("f2dca6"), hx("cfae72")]
SHELL = [hx("ffe08a"), hx("f5a844"), hx("cc6e2c"), hx("8e4420")]
SLUG = [hx("ffe8d0"), hx("f4c4a2"), hx("d4967c"), hx("9c6458")]
NAVY = [hx("7f9cf0"), hx("4a64c4"), hx("2e3e8c"), hx("1e2758")]
PUFF = [hx("ffffff"), hx("f6f0e2"), hx("ddd2b8"), hx("ada084")]
INK = [hx("f6f2ee"), hx("d6cec8"), hx("a29896"), hx("675e68"), hx("3a3042")]
COAT = [hx("6cc0bc"), hx("3e8a92"), hx("2a5f6a"), hx("1b3d48")]
FLAME = [hx("fffbe0"), hx("ffe27a"), hx("ffb43c"), hx("e0702a")]
BAT = [hx("a898d8"), hx("7464ac"), hx("4c3e80"), hx("2e2652")]
MEMB = [hx("e0b0d4"), hx("b07cb4"), hx("7c5090")]
GRUB = [hx("fff4d6"), hx("e6cfa0"), hx("b89a6c"), hx("7c6446")]
GLOWG = [hx("f4ffe0"), hx("a8ff96"), hx("4fe0a0"), hx("2a9a7a")]
FISH = [hx("fff0a0"), hx("ffc850"), hx("ec8c2c"), hx("b0581c")]
JELLY = [hx("ffe4fa"), hx("f6aee8"), hx("cc78d0"), hx("8a4ca6")]
MOTH = [hx("dcc4ec"), hx("aa8cc8"), hx("7a5c9c"), hx("4e3a6c")]
FUZZ = [hx("fff4e4"), hx("ead2c0"), hx("c4a496")]
BURR = [hx("d6e47c"), hx("a2b84c"), hx("6e8432"), hx("46561e")]
STONE = [hx("d2ccdc"), hx("a29cb4"), hx("736c88"), hx("4a445c"), hx("312c40")]
ROCK = [hx("d6c6a8"), hx("ae9a7c"), hx("847058"), hx("5c4c3c"), hx("3a3028")]
BEETLE = [hx("7a6a8e"), hx("564868"), hx("3a2e4a"), hx("241c30")]
BELLY = [hx("f4e2b8"), hx("d8c090"), hx("ae9468"), hx("7c6648")]
AMBER = [hx("fff4c0"), hx("ffd060"), hx("f09030"), hx("b05a1c")]
EMBER = [hx("fff6c8"), hx("ffc040"), hx("ff7a1c")]
VIOLET = [hx("c8aef0"), hx("9a7ad8"), hx("6c50b0"), hx("46327e"), hx("2a1c4c")]
MAGENTA = [hx("ffb4ec"), hx("f064c0"), hx("b0348e"), hx("701c5e")]
LILAC = [hx("fbf4ff"), hx("e2d2f6"), hx("b8a2dc"), hx("8a74b4")]
GLOW = [hx("ffffff"), hx("fff6c4"), hx("ffe07a"), hx("ffb84a")]
WOOD = [hx("e0a868"), hx("b47a42"), hx("84542c"), hx("5a361c")]
LEAF = [hx("b4ec6a"), hx("6cc444"), hx("3e8e34"), hx("245e28")]
WATERB = [hx("e8fbff"), hx("a8e6ff"), hx("5cb4e8")]


# ---------------------------------------------------------------- shape toolkit (sets of pixel coords)
def S_ellipse(cx, cy, rx, ry):
    s = set()
    for y in range(int(math.floor(cy - ry)) - 1, int(math.ceil(cy + ry)) + 2):
        for x in range(int(math.floor(cx - rx)) - 1, int(math.ceil(cx + rx)) + 2):
            nx = (x + 0.5 - cx) / max(rx, 0.01)
            ny = (y + 0.5 - cy) / max(ry, 0.01)
            if nx * nx + ny * ny <= 1.0:
                s.add((x, y))
    return s


def S_rect(x0, y0, x1, y1):
    return {(x, y) for y in range(y0, y1 + 1) for x in range(x0, x1 + 1)}


def S_poly(pts):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    s = set()
    n = len(pts)
    for y in range(int(math.floor(min(ys))) - 1, int(math.ceil(max(ys))) + 2):
        py = y + 0.5
        for x in range(int(math.floor(min(xs))) - 1, int(math.ceil(max(xs))) + 2):
            px = x + 0.5
            inside = False
            j = n - 1
            for i in range(n):
                xi, yi = pts[i]
                xj, yj = pts[j]
                if (yi > py) != (yj > py):
                    xint = xi + (py - yi) * (xj - xi) / (yj - yi)
                    if px < xint:
                        inside = not inside
                j = i
            if inside:
                s.add((x, y))
    return s


def S_line(x0, y0, x1, y1, r):
    s = set()
    n = int(max(abs(x1 - x0), abs(y1 - y0)) * 2) + 1
    for i in range(n + 1):
        t = i / n
        cx, cy = x0 + (x1 - x0) * t, y0 + (y1 - y0) * t
        for yy in range(int(math.floor(cy - r)) - 1, int(math.ceil(cy + r)) + 2):
            for xx in range(int(math.floor(cx - r)) - 1, int(math.ceil(cx + r)) + 2):
                if (xx + 0.5 - cx - 0.5) ** 2 + (yy + 0.5 - cy - 0.5) ** 2 <= r * r + 0.3:
                    s.add((xx, yy))
    return s


def S_curve(pts, r0, r1=None, steps=40):
    """Thick polyline through pts with radius tapering r0 -> r1."""
    if r1 is None:
        r1 = r0
    s = set()
    segs = len(pts) - 1
    total = steps * segs
    for k in range(total + 1):
        t = k / total
        seg = min(int(t * segs), segs - 1)
        lt = t * segs - seg
        x = pts[seg][0] + (pts[seg + 1][0] - pts[seg][0]) * lt
        y = pts[seg][1] + (pts[seg + 1][1] - pts[seg][1]) * lt
        r = r0 + (r1 - r0) * t
        for yy in range(int(math.floor(y - r)) - 1, int(math.ceil(y + r)) + 2):
            for xx in range(int(math.floor(x - r)) - 1, int(math.ceil(x + r)) + 2):
                if (xx - x) ** 2 + (yy - y) ** 2 <= r * r + 0.3:
                    s.add((xx, yy))
    return s


def bezier(p0, p1, p2, n=12):
    out = []
    for i in range(n + 1):
        t = i / n
        a = (1 - t) ** 2
        b = 2 * (1 - t) * t
        c = t * t
        out.append((a * p0[0] + b * p1[0] + c * p2[0], a * p0[1] + b * p1[1] + c * p2[1]))
    return out


def fill(cv, shape, col):
    for x, y in shape:
        cv.set(x, y, col)


LDIRS = ((-1, -1), (0, -1), (-1, 0))
SDIRS = ((1, 1), (0, 1), (1, 0))


def _dist(shape, x, y, dirs, cap=12):
    best = cap
    for dx, dy in dirs:
        k = 1
        while k < best and (x + dx * k, y + dy * k) in shape:
            k += 1
        best = min(best, k)
    return best


def paint(cv, shape, ramp, lw=(1,), sw=(1, 3), ldirs=LDIRS, sdirs=SDIRS, edge=None, only=None):
    """Directional rim shading. ramp = lights(len lw) + [base] + shades(len sw), light -> dark.
    lw: highlight band widths (ascending) mapped to ramp[0..]; sw: shadow widths (ascending) darkest first.
    edge: colour for boundary pixels whose outside neighbour is already painted (internal contour)."""
    nl = len(lw)
    base = ramp[nl]
    shades = ramp[nl + 1:]
    for (x, y) in shape:
        if only is not None and (x, y) not in only:
            continue
        if edge is not None:
            internal = False
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                q = (x + dx, y + dy)
                if q not in shape and cv.get(*q)[3] != 0:
                    internal = True
                    break
            if internal:
                cv.set(x, y, edge)
                continue
        dl = _dist(shape, x, y, ldirs)
        ds = _dist(shape, x, y, sdirs)
        col = base
        if dl <= ds:
            for i, wdt in enumerate(lw):
                if dl <= wdt:
                    col = ramp[i]
                    break
        else:
            for i, wdt in enumerate(sw):
                if ds <= wdt:
                    col = shades[len(shades) - 1 - i] if i < len(shades) else shades[0]
                    break
        cv.set(x, y, col)


def speckle(cv, shape, cols, density, seed, only_on=None):
    for (x, y) in sorted(shape):
        if only_on is not None and cv.get(x, y) not in only_on:
            continue
        v = hval(seed, x, y)
        if v < density:
            cv.set(x, y, cols[int(hval(seed, "c", x, y) * len(cols)) % len(cols)])


def blit(dst, src, ox, oy):
    for y in range(src.h):
        for x in range(src.w):
            c = src.px[y][x]
            if c[3]:
                dst.set(x + ox, y + oy, c)


def shifted(src, dx, dy, w=None, h=None):
    c = Canvas(w or src.w, h or src.h)
    blit(c, src, dx, dy)
    return c


def flipped(src):
    c = Canvas(src.w, src.h)
    for y in range(src.h):
        for x in range(src.w):
            c.px[y][src.w - 1 - x] = src.px[y][x]
    return c


def glow(cv, cx, cy, r, col, levels=((1.0, 70), (0.6, 120))):
    """Stepped alpha halo on transparent pixels only (no anti-aliasing)."""
    for y in range(int(cy - r) - 1, int(cy + r) + 2):
        for x in range(int(cx - r) - 1, int(cx + r) + 2):
            if cv.get(x, y)[3] != 0 or not (0 <= x < cv.w and 0 <= y < cv.h):
                continue
            d = math.hypot(x + 0.5 - cx, y + 0.5 - cy) / r
            a = 0
            for frac, al in levels:
                if d <= frac:
                    a = al
            if a:
                cv.set(x, y, alpha(col, a))


def sparkle(cv, x, y, col=WHITE, arm=1, mid=None):
    cv.set(x, y, col)
    for k in range(1, arm + 1):
        cc = col if k < arm or mid is None else mid
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            if cv.get(x + dx * k, y + dy * k)[3] == 0:
                cv.set(x + dx * k, y + dy * k, cc)


def eye(cv, x, y, h=2, big=False, glint=True):
    cv.rect(x, y, x, y + h - 1, BLACK)
    if big:
        cv.rect(x + 1, y, x + 1, y + h - 1, BLACK)
    if glint:
        cv.set(x, y, WHITE)


# ---------------------------------------------------------------- Pip (Explorer) with extra expressions
def draw_pip(bob=0, legs=((3, 0), (-3, 0)), arms=((4, 6), (-3, 6)), eyes="open", mouth=None,
             lean=0, cap_dy=0, outline=True, front_arm=True):
    """Same construction as char_art.draw_explorer (identical with defaults) plus extra faces."""
    c = Canvas(32, 40)
    hip_y = 29 + bob
    hip_x = 15 + lean
    feet_y = 38
    sx, sy = 14 + lean, 21 + bob
    bx, by = sx + arms[1][0], sy + arms[1][1]
    c.thick_line(sx, sy, bx, by, 1.0, GREEN[3])
    c.rect(int(bx) - 1, int(by), int(bx), int(by) + 1, SKIN[2])
    (fdx, flift), (bdx, blift) = legs

    def leg(dx, lift, pants, boot, boot_hi):
        fx, fy = hip_x + dx, feet_y - lift
        c.thick_line(hip_x, hip_y, fx, fy - 3, 1.3, pants)
        c.rect(int(fx) - 1, int(fy) - 2, int(fx) + 3, int(fy), boot)
        c.hline(int(fx) - 1, int(fx) + 2, int(fy) - 2, boot_hi)
        c.hline(int(fx) - 1, int(fx) + 3, int(fy), DIRT[3])
    leg(bdx, blift, DIRT[2], DIRT[3], DIRT[2])
    c.rect(8 + lean, 19 + bob, 10 + lean, 27 + bob, DIRT[2])
    c.rect(8 + lean, 19 + bob, 10 + lean, 19 + bob, DIRT[1])
    c.rect(9 + lean, 22 + bob, 10 + lean, 22 + bob, GOLD[2])
    tx0, tx1, ty0, ty1 = 11 + lean, 19 + lean, 19 + bob, 29 + bob
    c.rect(tx0, ty0, tx1, ty1, GREEN[1])
    c.rect(tx0, ty0, tx0 + 1, ty1, GREEN[2])
    c.rect(tx1 - 2, ty0 + 1, tx1 - 1, ty0 + 4, GREEN[0])
    c.hline(tx0, tx1, ty1, GREEN[2])
    c.hline(tx0, tx1, 26 + bob, DIRT[2])
    c.rect(tx1 - 3, 26 + bob, tx1 - 2, 26 + bob, GOLD[1])
    c.set(tx0 + 2, ty0, DIRT[2]); c.set(tx0 + 3, ty0 + 1, DIRT[2]); c.set(tx0 + 4, ty0 + 2, DIRT[2])
    leg(fdx, flift, DIRT[1], DIRT[3], DIRT[1])
    hy = 11 + bob
    c.rect(12 + lean, hy, 20 + lean, hy + 7, SKIN[1])
    c.rect(13 + lean, hy + 7, 19 + lean, hy + 8, SKIN[1])
    c.rect(17 + lean, hy + 1, 20 + lean, hy + 3, SKIN[0])
    c.rect(12 + lean, hy, 13 + lean, hy + 6, DIRT[2])
    c.set(14 + lean, hy, DIRT[2])
    c.rect(14 + lean, hy + 4, 14 + lean, hy + 5, SKIN[2])
    c.set(21 + lean, hy + 4, SKIN[1]); c.set(21 + lean, hy + 5, SKIN[1])
    c.set(19 + lean, hy + 6, SKIN[2])
    ex = 18 + lean
    if eyes == "open":
        c.rect(ex, hy + 2, ex, hy + 4, BLACK)
        c.set(ex, hy + 2, WHITE)
        c.rect(ex - 2, hy + 1, ex, hy + 1, DIRT[3])
    elif eyes == "blink":
        c.hline(ex - 1, ex, hy + 4, BLACK)
    elif eyes == "happy":
        c.set(ex - 1, hy + 3, BLACK); c.set(ex, hy + 2, BLACK); c.set(ex + 1, hy + 3, BLACK)
        c.rect(ex - 2, hy + 1, ex - 1, hy + 1, DIRT[3])
    elif eyes == "up":
        c.rect(ex, hy + 1, ex, hy + 3, BLACK)
        c.set(ex, hy + 1, WHITE)
        c.rect(ex - 2, hy, ex, hy, DIRT[3])
    elif eyes == "squint":
        c.hline(ex - 1, ex + 1, hy + 3, BLACK)
        c.rect(ex - 2, hy + 1, ex, hy + 1, DIRT[3])
    if mouth == "open":
        c.rect(19 + lean, hy + 6, 20 + lean, hy + 7, RED[3])
    elif mouth == "smile":
        c.set(18 + lean, hy + 6, RED[3]); c.hline(19 + lean, 20 + lean, hy + 7, RED[3])
        c.set(17 + lean, hy + 5, PINK)
    elif mouth == "o":
        c.rect(20 + lean, hy + 6, 20 + lean, hy + 7, RED[3])
    else:
        c.hline(19 + lean, 20 + lean, hy + 7, SKIN[2])
    cy = 10 + bob + cap_dy
    c.shaded_ellipse(16 + lean, cy, 10.5, 8.5, RED, y_max=cy)
    c.hline(6 + lean, 26 + lean, cy + 1, RED[2])
    c.hline(7 + lean, 25 + lean, cy + 2, CREAM[2])
    c.hline(8 + lean, 24 + lean, cy + 2, CREAM[1])
    for sx_, sy_, w in ((10, -5, 2), (18, -7, 3), (22, -3, 2), (14, -2, 2)):
        x0 = sx_ + lean
        y0 = cy + sy_
        c.rect(x0, y0, x0 + w - 1, y0 + w - 2 if w > 2 else y0, CREAM[0])
        if w > 2:
            c.hline(x0, x0 + w - 1, y0 + w - 1, CREAM[1])
        else:
            c.hline(x0, x0 + w - 1, y0 + 1, CREAM[1])
    if front_arm:
        sx, sy = 16 + lean, 21 + bob
        fx_, fy_ = sx + arms[0][0], sy + arms[0][1]
        c.thick_line(sx, sy, fx_, fy_, 1.1, GREEN[1])
        c.set(sx, sy - 1, GREEN[0])
        c.rect(int(round(fx_)), int(round(fy_)), int(round(fx_)) + 1, int(round(fy_)) + 1, SKIN[1])
        c.set(int(round(fx_)), int(round(fy_)), SKIN[0])
    if outline:
        c.outline()
    return c


def front_hand(c, lean, bob, arm, outline_after=False):
    sx, sy = 16 + lean, 21 + bob
    fx_, fy_ = sx + arm[0], sy + arm[1]
    c.thick_line(sx, sy, fx_, fy_, 1.1, GREEN[1])
    c.set(sx, sy - 1, GREEN[0])
    c.rect(int(round(fx_)), int(round(fy_)), int(round(fx_)) + 1, int(round(fy_)) + 1, SKIN[1])
    c.set(int(round(fx_)), int(round(fy_)), SKIN[0])
    return int(round(fx_)), int(round(fy_))


def mini_spore(c, cx, cy, phase=0):
    """4-5px red spotted spore ball (matches Spore Shot)."""
    c.rect(cx - 1, cy - 2, cx + 1, cy + 2, RED[1])
    c.rect(cx - 2, cy - 1, cx + 2, cy + 1, RED[1])
    c.rect(cx + 1, cy, cx + 2, cy + 1, RED[2]); c.set(cx, cy + 2, RED[2])
    if phase == 0:
        c.set(cx - 1, cy - 1, CREAM[0]); c.set(cx + 1, cy + 1, CREAM[1])
    else:
        c.set(cx + 1, cy - 1, CREAM[0]); c.set(cx - 1, cy + 1, CREAM[1])


def bubble(c, x, y, r=1):
    if r <= 1:
        c.set(x, y, WATERB[0]); c.set(x + 1, y, WATERB[1]); c.set(x, y + 1, WATERB[1]); c.set(x + 1, y + 1, WATERB[2])
    else:
        for (px, py) in S_ellipse(x + 0.5, y + 0.5, r + 0.5, r + 0.5):
            c.set(px, py, alpha(WATERB[1], 150))
        for (px, py) in S_ellipse(x + 0.5, y + 0.5, r + 0.5, r + 0.5):
            if (px + 1, py) not in S_ellipse(x + 0.5, y + 0.5, r + 0.5, r + 0.5) or \
               (px, py + 1) not in S_ellipse(x + 0.5, y + 0.5, r + 0.5, r + 0.5):
                c.set(px, py, WATERB[2])
        c.set(x - r + 1, y - r + 1, WHITE)


def hero_swim():
    cyc = [
        # bob, front leg, back leg, front arm, back arm, mouth
        (0, (-2, 4), (-6, 7), (7, -5), (5, -6), None),
        (1, (-4, 6), (-3, 3), (9, 1), (-5, 1), "o"),
        (0, (-3, 3), (-6, 6), (2, 8), (-4, 8), None),
        (-1, (-6, 6), (-2, 3), (6, 3), (3, 4), None),
    ]
    frames = []
    for i, (bob, fl, bl, fa, ba, m) in enumerate(cyc):
        c = draw_pip(bob, (fl, bl), (fa, ba), eyes="open", mouth=m, lean=1, outline=False)
        c.outline()
        # breath bubbles drifting up in front of the face
        if i == 1:
            bubble(c, 25, 14)
        if i == 2:
            bubble(c, 26, 9)
            c.set(24, 14, WATERB[1])
        if i == 3:
            bubble(c, 27, 4)
        frames.append(c)
    return frames


def hero_throw():
    f0 = draw_pip(1, ((4, 0), (-4, 0)), ((3, 6), (-3, 5)), eyes="open", lean=-1, outline=False, front_arm=False)
    hx_, hy_ = front_hand(f0, -1, 1, (-7, -3))
    mini_spore(f0, hx_ - 1, hy_ - 2, 0)
    f0.outline()
    f1 = draw_pip(0, ((5, 0), (-5, 1)), ((9, -1), (-6, 3)), eyes="open", mouth="open", lean=1)
    # little motion streaks behind the throwing hand
    for (x, y) in ((22, 17), (21, 18), (20, 17)):
        if f1.get(x, y)[3] == 0:
            f1.set(x, y, alpha(WHITE, 140))
    return [f0, f1]


def dandelion(c, cx, top, sway=0):
    """Dome-shaped dandelion clock parasol, its stem goes down from (cx, top+8)."""
    dome = S_ellipse(cx + sway, top + 7, 12.5, 7.5)
    dome = {(x, y) for (x, y) in dome if y <= top + 7}
    for (x, y) in dome:
        c.set(x, y, WHITE if hval("dd", x, y) < 0.55 else LILAC[1])
    # fluffy scalloped rim
    for x in range(int(cx + sway - 12), int(cx + sway + 13)):
        if (x + sway) % 3 != 0:
            c.set(x, top + 8, LILAC[1] if x % 2 else WHITE)
    # seed spokes under the dome
    for k in range(-4, 5):
        x = cx + sway + k * 2.6
        c.thick_line(x, top + 8, cx + sway * 0.5, top + 11, 0.3, LILAC[2])
    for (x, y) in dome:
        if (x, y - 1) not in dome:
            c.set(x, y, WHITE)
    # soft shadow on the right
    for (x, y) in dome:
        if (x + 2, y) not in dome and y > top + 2:
            c.set(x, y, LILAC[2])
    c.set(int(cx + sway) - 4, top + 2, WHITE)


def dandelion2(c, cx, cy, sway=0, rx=12.5, ry=10.0):
    """Dandelion-clock parasol: thin seed rays fan up from the seed head at (cx, cy) to fluffy white tufts.
    Drawn on its own layer (tufts outlined, rays not) and merged UNDER existing pixels of c."""
    p = Canvas(c.w, c.h)
    n = 11
    tips = []
    for k in range(n):
        a = math.pi + (k / (n - 1)) * math.pi  # pi..2pi = upper half
        tx = cx + sway + math.cos(a) * rx
        ty = cy + math.sin(a) * ry * (0.85 if k in (0, n - 1) else 1.0)
        tips.append((tx, ty))
    for (tx, ty) in tips:
        for (x, y) in S_ellipse(tx, ty, 2.1, 1.7):
            p.set(x, y, WHITE)
        p.set(int(tx) + 1, int(ty) + 1, LILAC[1])
    p.outline(LILAC[3])
    for (tx, ty) in tips:
        n_ = int(max(abs(tx - cx), abs(ty - cy)) * 2) + 1
        for i in range(n_ + 1):
            t = i / n_
            x = int(round(cx + sway * 0.5 + (tx - cx - sway * 0.5) * t))
            y = int(round(cy + (ty - cy) * t))
            if p.get(x, y)[3] == 0:
                p.set(x, y, LILAC[1] if t > 0.45 else LILAC[2])
    for (x, y) in S_ellipse(cx + sway * 0.5, cy, 2.2, 1.6):
        p.set(x, y, DIRT[1])
    p.set(int(cx + sway * 0.5) - 1, int(cy) - 1, DIRT[0])
    for y in range(p.h):
        for x in range(p.w):
            if p.px[y][x][3] and c.px[y][x][3] == 0:
                c.px[y][x] = p.px[y][x]


def hero_glide():
    frames = []
    for i in range(2):
        sway = 0 if i == 0 else 1
        legs = ((4, 8), (-3, 10)) if i == 0 else ((3, 10), (-4, 8))
        body = draw_pip(0, legs, ((3, 6), (-2, 6)), eyes="open", mouth="smile" if i else None, outline=False,
                        front_arm=False)
        body = shifted(body, 0, 9)
        # both arms reach up to the stem
        stem_x = 21 + sway
        for arm_from, col in (((14, 30), GREEN[3]), ((16, 30), GREEN[1])):
            body.thick_line(arm_from[0], arm_from[1], stem_x - 1, 22, 1.0, col)
        for k in range(15):
            t = k / 14
            body.set(round(stem_x + (17 + sway - stem_x) * t), round(24 + (12 - 24) * t),
                     GREEN[2] if t < 0.5 else GREEN[1])
        body.rect(stem_x - 1, 21, stem_x, 22, SKIN[1])
        body.set(stem_x - 1, 21, SKIN[0])
        body.outline()
        dandelion2(body, 17 + sway, 11.5, sway, rx=12.5, ry=9.0)
        frames.append(body)
    return frames


def glowspore_small(c, cx, cy, r=4.5, phase=0):
    for (x, y) in S_ellipse(cx, cy, r, r):
        d = math.hypot(x + 0.5 - cx, y + 0.5 - cy) / r
        col = GLOW[0] if d < 0.35 else GLOW[1] if d < 0.62 else GLOW[2] if d < 0.85 else GLOW[3]
        c.set(x, y, col)
    c.set(int(cx) - 1, int(cy) - 2, WHITE)


def hero_victory():
    frames = []
    for i in range(2):
        bob = 0 if i == 0 else -2
        legs = ((3, 0), (-3, 0)) if i == 0 else ((3, 2), (-3, 3))
        c = draw_pip(bob, legs, ((3, 7), (-4, 2)), eyes="happy", mouth="smile", outline=False, front_arm=False)
        hx_, hy_ = front_hand(c, 0, bob, (6, -10))
        glowspore_small(c, hx_ + 2.5, hy_ - 4, 4.5)
        c.outline()
        orb_x, orb_y = hx_ + 2.5, hy_ - 4
        glow(c, orb_x, orb_y, 9.5 if i else 8.5, GLOW[2], ((1.0, 60), (0.75, 110)))
        sp = [(4, 3), (29, 14), (8, 1)] if i == 0 else [(5, 6), (30, 9), (13, 0), (28, 26)]
        for (x, y) in sp:
            if c.get(x, y)[3] == 0 or c.get(x, y)[3] < 255:
                sparkle(c, x, y, WHITE, 1, GLOW[2])
        frames.append(c)
    return frames


def hero_talk():
    return [draw_pip(0, ((3, 0), (-3, 0)), ((6, 2), (-2, 7)), eyes="open", mouth="open")]


# ---------------------------------------------------------------- Bark Shield Ring (44x48, origin 22,44)
def shield_ring(phase):
    c = Canvas(44, 48)
    cx, cy, rx, ry = 22, 25, 19.5, 21.5
    n = 10
    behind, front = [], []
    for k in range(n):
        a0 = (k / n) * 2 * math.pi + phase * (math.pi / n)
        a1 = a0 + 2 * math.pi / n * 0.62
        seg = set()
        for t in range(14):
            a = a0 + (a1 - a0) * t / 13
            px, py = cx + math.cos(a) * rx, cy + math.sin(a) * ry
            seg |= S_ellipse(px, py, 2.3, 2.3)
        (front if math.sin((a0 + a1) / 2) > -0.1 else behind).append((k, seg))
    for k, seg in behind + front:
        paint(c, seg, WOOD, lw=(1,), sw=(1,))
        # bark grain
        for (x, y) in seg:
            if hval("grain", k, x, y) < 0.18:
                c.set(x, y, WOOD[3])
    c.outline()
    # leaves sprouting from a few bark plates
    for k in range(0, n, 3):
        a = (k / n) * 2 * math.pi + phase * (math.pi / n) + 0.25
        px, py = cx + math.cos(a) * (rx + 2.5), cy + math.sin(a) * (ry + 2.5)
        lf = S_ellipse(px, py, 2.2, 1.6)
        for (x, y) in lf:
            c.set(x, y, LEAF[1] if (x + y) % 3 else LEAF[0])
        c.set(int(px) + 1, int(py) + 1, LEAF[2])
    # inner protective shimmer
    for k in range(16):
        a = k / 16 * 2 * math.pi + phase * 0.2
        x, y = int(cx + math.cos(a) * (rx - 4)), int(cy + math.sin(a) * (ry - 4))
        if c.get(x, y)[3] == 0 and k % 2 == phase % 2:
            c.set(x, y, alpha(GOLD[0], 150))
    return c


# ---------------------------------------------------------------- map marker + node
def pip_map(step):
    c = Canvas(16, 20)
    bob = 1 if step in (1, 3) else 0
    # legs
    lf = {0: (0, 1), 1: (0, 0), 2: (1, 0), 3: (0, 0)}[step]
    c.rect(4, 16 + bob - (1 - lf[0]) * 0, 6, 18 - lf[1] + 0, DIRT[3])
    c.rect(9, 16 + bob, 11, 18 - lf[0], DIRT[3])
    c.rect(5, 15 + bob, 6, 16 + bob, DIRT[1]); c.rect(9, 15 + bob, 10, 16 + bob, DIRT[1])
    # body + backpack straps
    c.rect(3, 11 + bob, 12, 15 + bob, GREEN[1])
    c.rect(3, 11 + bob, 4, 15 + bob, GREEN[2])
    c.rect(11, 12 + bob, 12, 15 + bob, GREEN[2])
    c.set(5, 11 + bob, DIRT[2]); c.set(10, 11 + bob, DIRT[2]); c.set(5, 12 + bob, DIRT[2]); c.set(10, 12 + bob, DIRT[2])
    c.hline(3, 12, 14 + bob, DIRT[2]); c.set(7, 14 + bob, GOLD[1]); c.set(8, 14 + bob, GOLD[1])
    # arms swing
    arm = 1 if step == 0 else -1 if step == 2 else 0
    c.rect(1, 11 + bob + max(0, arm), 2, 13 + bob + max(0, arm), GREEN[2])
    c.rect(13, 11 + bob + max(0, -arm), 14, 13 + bob + max(0, -arm), GREEN[1])
    c.set(1, 14 + bob + max(0, arm), SKIN[1]); c.set(14, 14 + bob + max(0, -arm), SKIN[1])
    # face
    c.rect(4, 7 + bob, 11, 11 + bob, SKIN[1])
    c.rect(4, 7 + bob, 4, 9 + bob, DIRT[2]); c.rect(11, 7 + bob, 11, 9 + bob, DIRT[2])
    c.rect(6, 8 + bob, 6, 9 + bob, BLACK); c.rect(9, 8 + bob, 9, 9 + bob, BLACK)
    c.set(6, 8 + bob, WHITE); c.set(9, 8 + bob, WHITE)
    c.set(5, 10 + bob, PINK); c.set(10, 10 + bob, PINK)
    c.hline(7, 8, 11 + bob, SKIN[2])
    # cap
    cap = S_ellipse(8, 7 + bob, 7.6, 6.6)
    cap = {(x, y) for (x, y) in cap if y <= 7 + bob}
    paint(c, cap, RED[:4], lw=(1,), sw=(1,))
    c.hline(1, 14, 7 + bob, RED[2])
    c.hline(2, 13, 8 + bob, CREAM[1])
    c.rect(4, 3 + bob, 5, 3 + bob, CREAM[0]); c.rect(9, 2 + bob, 11, 2 + bob, CREAM[0]); c.set(10, 3 + bob, CREAM[1])
    c.set(7, 5 + bob, CREAM[0]); c.set(12, 5 + bob, CREAM[0]); c.set(2, 5 + bob, CREAM[0])
    c.outline()
    return c


def map_node(state):
    c = Canvas(24, 16)
    top = S_ellipse(12, 7, 10.5, 5.5)
    side = S_ellipse(12, 9, 10.5, 5.5) - top
    if state == 0:
        ramp_t, ramp_s = STONE[:4], STONE[2:]
    elif state == 1:
        ramp_t, ramp_s = [hx("ffb0a0"), RED[0], RED[1], RED[2]], [RED[2], RED[3]]
    else:
        ramp_t, ramp_s = GOLD[:4], [GOLD[2], GOLD[3]]
    fill(c, side, ramp_s[0])
    for (x, y) in side:
        if (x, y + 1) not in side:
            c.set(x, y, ramp_s[1])
    paint(c, top, ramp_t, lw=(1,), sw=(1, 2))
    inner = S_ellipse(12, 7, 6.5, 3.2)
    if state == 0:
        fill(c, inner, STONE[3])
        # padlock
        c.rect(10, 5, 13, 8, GREY[1]); c.hline(10, 13, 5, GREY[0]); c.hline(10, 13, 8, GREY[2])
        c.set(11, 3, GREY[2]); c.set(12, 3, GREY[2]); c.set(10, 4, GREY[2]); c.set(13, 4, GREY[2])
        c.set(11, 6, BLACK); c.set(12, 6, BLACK); c.set(11, 7, BLACK)
    elif state == 1:
        fill(c, inner, RED[2])
        for (x, y) in inner:
            if (x, y - 1) not in inner:
                c.set(x, y, RED[3])
        # cream spot / arrow emblem (enter)
        c.rect(10, 5, 13, 8, CREAM[0]); c.hline(11, 12, 4, CREAM[0]); c.hline(11, 12, 9, CREAM[2])
        c.rect(13, 5, 13, 8, CREAM[1])
    else:
        fill(c, inner, GOLD[2])
        for (x, y) in inner:
            if (x, y - 1) not in inner:
                c.set(x, y, GOLD[3])
        glowspore_small(c, 12, 6.5, 3.2)
    c.outline()
    if state == 2:
        for (x, y) in ((3, 1), (20, 2), (1, 9)):
            sparkle(c, x, y, WHITE, 1, alpha(GOLD[1], 200))
    return c


# ---------------------------------------------------------------- NPCs
def honeycomb(cv, shape, ramp, cw=4, ch=3, seed="hc"):
    """Morel pits: staggered grid of dark pits with lit ridges above them."""
    for (x, y) in shape:
        row = (y // ch)
        col = (x + (row % 2) * (cw // 2)) // cw
        ox = (x + (row % 2) * (cw // 2)) % cw
        oy = y % ch
        v = hval(seed, row, col)
        x_hi = cw - 1 if v < 0.5 else cw - 2
        if 1 <= ox <= x_hi - (1 if v > 0.8 else 0) and 1 <= oy <= ch - (1 if ch < 4 else 2):
            near_edge = any((x + dx, y + dy) not in shape for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))
            if near_edge:
                continue
            dark = oy == 1 and ox <= 2
            cv.set(x, y, ramp[-1] if dark else ramp[-2])
        elif oy == 0 and ox != 0 and (x, y) in shape and cv.get(x, y) == ramp[len(ramp) // 2 - 1]:
            cv.set(x, y, ramp[0])


def shaggy(cv, shape, ramp, seed, ymax):
    """Inkcap shaggy scales: short upturned curls in a staggered grid."""
    for (x, y) in sorted(shape):
        if y >= ymax:
            continue
        row = y // 4
        if (x + row * 2) % 5 == 0 and y % 4 == 1 and (x, y + 2) in shape:
            cv.set(x, y, ramp[0]); cv.set(x, y + 1, ramp[2]); cv.set(x + 1, y + 1, ramp[3]) if (x + 1, y + 1) in shape else None
            if hval(seed, x, y) < 0.5 and (x, y + 2) in shape:
                cv.set(x, y + 2, ramp[3])


def morel_pits(cv, shape, ramp, seed, cw=5, ch=7):
    """Morel cap: irregular vertical pits separated by pale ridges."""
    xs = [p[0] for p in shape]
    ys = [p[1] for p in shape]
    seeds = []
    for gy in range(min(ys) - ch, max(ys) + ch, ch):
        for gx in range(min(xs) - cw, max(xs) + cw, cw):
            ox = (gy // ch) % 2 * cw * 0.5
            seeds.append((gx + ox + hval(seed, gx, gy) * cw * 0.7, gy + hval(seed, "y", gx, gy) * ch * 0.6))
    ridge = 1.1 if cw < 6 else 1.6
    for (x, y) in shape:
        if any((x + dx, y + dy) not in shape for dx in (-1, 0, 1) for dy in (-1, 0, 1)):
            continue
        best = []
        for (sx, sy) in seeds:
            d = math.hypot(x + 0.5 - sx, (y + 0.5 - sy) * cw / ch)
            best.append((d, sx, sy))
        best.sort()
        d1, sx, sy = best[0]
        d2 = best[1][0]
        if d2 - d1 < ridge:
            continue  # ridge keeps the lit cap colour
        top = (y + 0.5 - sy) * cw / ch < -d1 * 0.2 or (d2 - d1) < ridge + 1.2 and y < sy
        cv.set(x, y, ramp[-1] if top else ramp[-2])


def morel_npc(bob=0, talk=False):
    c = Canvas(32, 48)
    b = bob
    # walking stick (behind hand, in front of robe)
    # robe
    robe = S_poly([(10, 25 + b), (21, 25 + b), (25, 46), (6, 46)])
    paint(c, robe, ROBE, lw=(1,), sw=(1, 3))
    for x in range(7, 25):
        if (x, 45) in robe:
            c.set(x, 45, ROBE[3])
    c.hline(9, 22, 40, ROBE[2])
    c.rect(7, 46, 11, 47, DIRT[3]); c.rect(17, 46, 22, 47, DIRT[3]); c.hline(18, 21, 46, DIRT[2])
    # sash
    c.hline(10, 22, 33 + b, GOLD[2]); c.hline(10, 22, 34 + b, GOLD[3])
    # beard
    beard = S_poly([(12, 22 + b), (25, 22 + b), (24, 30 + b), (20, 37 + b), (17, 38 + b), (14, 33 + b)])
    paint(c, beard, BEARD, lw=(1,), sw=(1, 2))
    for y in range(26 + b, 37 + b, 3):
        c.set(18 + (y % 2), y, BEARD[2]); c.set(22, y - 1, BEARD[2])
    # face (stem-coloured)
    face = S_ellipse(18, 21 + b, 6.5, 4.5)
    face = {p for p in face if p[1] <= 23 + b}
    paint(c, face, CREAM, lw=(1,), sw=(1,))
    # eyes + brows + nose
    c.rect(20, 20 + b, 21, 21 + b, BLACK); c.set(20, 20 + b, WHITE)
    c.hline(18, 22, 19 + b, BEARD[0]); c.set(23, 19 + b, BEARD[1])
    nose = S_ellipse(23.5, 22.5 + b, 2.2, 1.8)
    paint(c, nose, [SKIN[0], SKIN[1], SKIN[2]], lw=(1,), sw=(1,))
    c.set(16, 22 + b, PINK)
    if talk:
        c.rect(20, 26 + b, 22, 27 + b, RED[3])
        c.hline(20, 22, 25 + b, BEARD[3])
    else:
        c.hline(20, 22, 25 + b, BEARD[3])
    # morel cap: tall honeycomb cone
    cap = S_ellipse(16, 11 + b, 8.5, 11.5)
    cap = {p for p in cap if p[1] <= 17 + b}
    paint(c, cap, MOREL, lw=(1,), sw=(1, 3))
    morel_pits(c, cap, MOREL, "morel", 4, 4)
    c.hline(7, 25, 17 + b, MOREL[3]); c.hline(8, 24, 18 + b, MOREL[4])
    # stick + hand
    stick = S_curve([(27, 47), (26, 30), (27, 17), (25, 14)], 1.0, 0.9)
    paint(c, stick, WOOD, lw=(1,), sw=(1,))
    c.set(28, 13, LEAF[1]); c.set(29, 12, LEAF[0]); c.set(28, 12, LEAF[1])
    sleeve = S_line(19, 26 + b, 24, 29 + b, 1.6)
    paint(c, sleeve, ROBE, lw=(1,), sw=(1,), edge=ROBE[3])
    c.rect(25, 28 + b, 27, 30 + b, CREAM[1]); c.set(25, 28 + b, CREAM[0])
    c.outline()
    return c


def chanterelle_npc(bob=0, talk=False):
    c = Canvas(36, 40)
    b = bob
    # feet
    c.rect(10, 36, 15, 38, DIRT[3]); c.rect(21, 36, 26, 38, DIRT[3])
    c.hline(11, 14, 36, DIRT[2]); c.hline(22, 25, 36, DIRT[2])
    # back arm
    arm_b = S_ellipse(6.5, 25 + b, 2.5, 3.5)
    paint(c, arm_b, TRUF, lw=(1,), sw=(1,))
    # lumpy body
    body = S_ellipse(18, 23 + b * 0.5, 13.5 - b * 0.3, 13 + b * 0.5)
    for (bx, by, r) in ((8, 15, 4), (15, 10.5, 4), (23, 11, 4.2), (29, 17, 4), (6, 25, 3.5), (30, 27, 3.5)):
        body |= S_ellipse(bx, by + b, r, r * 0.9)
    body = {p for p in body if p[1] <= 36}
    paint(c, body, TRUF, lw=(1,), sw=(1, 3))
    speckle(c, body, [TRUF[0], TRUF[3]], 0.10, "truf")
    # apron
    apron = S_poly([(10, 24 + b), (27, 24 + b), (28, 32), (25, 36), (12, 36), (9, 32)])
    paint(c, apron, APRON, lw=(1,), sw=(1,))
    for x in range(10, 28, 3):
        for y in range(25 + b, 36):
            if (x, y) in apron and c.get(x, y) == APRON[1]:
                c.set(x, y, hx("e8c27c"))
    c.hline(10, 27, 24 + b, APRON[2])
    pocket = S_rect(15, 29, 22, 33)
    fill(c, pocket, APRON[2]); c.hline(15, 22, 29, APRON[1])
    c.rect(18, 30, 19, 31, GOLD[1]); c.set(18, 30, GOLD[0])
    c.set(11, 23 + b, APRON[1]); c.set(12, 22 + b, APRON[1]); c.set(26, 23 + b, APRON[1]); c.set(25, 22 + b, APRON[1])
    # face
    ey = 16 + b
    for ex in (20, 26):
        c.rect(ex, ey, ex + 1, ey + 2, BLACK); c.set(ex, ey, WHITE)
    c.hline(19, 21, ey - 2, TRUF[3]); c.hline(25, 27, ey - 2, TRUF[3])
    c.rect(17, ey + 3, 18, ey + 3, PINK); c.rect(28, ey + 3, 29, ey + 3, PINK)
    if talk:
        c.rect(22, ey + 4, 25, ey + 6, RED[3]); c.hline(23, 24, ey + 6, PINK)
    else:
        c.set(21, ey + 4, TRUF[3]); c.hline(22, 24, ey + 5, TRUF[3]); c.set(25, ey + 4, TRUF[3])
    # mustache tufts
    c.hline(20, 21, ey + 3, TRUF[3]); c.hline(25, 26, ey + 3, TRUF[3])
    # leaf sprout on top
    c.set(17, 6 + b, LEAF[2]); c.set(17, 5 + b, LEAF[2])
    leaf = S_ellipse(20, 4 + b, 3, 1.6)
    paint(c, leaf, LEAF, lw=(1,), sw=(1,))
    # front arm (waves when talking)
    if talk:
        arm = S_line(29, 23 + b, 32, 16 + b, 1.8)
    else:
        arm = S_line(29, 24 + b, 31, 28 + b, 1.8)
    paint(c, arm, TRUF, lw=(1,), sw=(1,), edge=TRUF[3])
    c.outline()
    return c


def bram_npc(bob=0, talk=False):
    c = Canvas(40, 32)
    b = bob
    # soft foot / body
    foot = S_poly([(3, 31), (4, 27), (12, 25), (30, 25), (36, 28), (37, 31)])
    neck = S_poly([(25, 30), (24, 20 + b), (27, 13 + b), (33, 12 + b), (36, 17 + b), (35, 30)])
    paint(c, foot | neck, SLUG, lw=(1,), sw=(1, 2))
    c.hline(4, 36, 31, SLUG[3])
    for x in range(6, 26, 4):
        c.set(x, 29, SLUG[2])
    # eye stalks
    sway = 1 if b else 0
    for (bx, tx, ty) in ((29, 27 + sway, 4), (33, 35 + sway, 5)):
        st = S_line(bx, 13 + b, tx, ty + 2 + b, 0.8)
        fill(c, st, SLUG[1])
        ball = S_ellipse(tx + 0.5, ty + b + 0.5, 2.6, 2.6)
        fill(c, ball, WHITE)
        for (x, y) in ball:
            if (x + 1, y + 1) not in ball:
                c.set(x, y, BEARD[2])
        c.rect(tx + 1, ty + b - 1, tx + 2, ty + b + 1, BLACK); c.set(tx + 1, ty + b - 1, WHITE)
    # shell (spiral)
    sc = (15, 15 + b * 0.5)
    shell = S_ellipse(sc[0], sc[1], 11, 10.5)
    paint(c, shell, SHELL, lw=(1,), sw=(1, 3))
    for k in range(120):
        t = k / 119
        a = t * 4.2 * math.pi
        r = 9.5 * (1 - t) + 0.5
        c.set(int(sc[0] + math.cos(a) * r), int(sc[1] + math.sin(a) * r * 0.95), SHELL[3])
    # satchel strap + bag
    for k in range(14):
        c.set(8 + k, 6 + k + b // 1, DIRT[3] if k % 5 else DIRT[2])
    bag = S_rect(17, 18 + b, 25, 25)
    paint(c, bag, DIRT, lw=(1,), sw=(1,))
    c.hline(17, 25, 20 + b, DIRT[3])
    c.rect(20, 21 + b, 22, 22 + b, GOLD[1])
    letter = S_rect(18, 15 + b, 23, 18 + b)
    fill(c, letter, WHITE); c.hline(18, 23, 18 + b, GREY[0]); c.set(20, 16 + b, RED[1]); c.set(21, 16 + b, RED[1])
    # postman cap on head
    cap = S_ellipse(31, 12 + b, 5.5, 3)
    cap = {p for p in cap if p[1] <= 12 + b}
    paint(c, cap, NAVY, lw=(1,), sw=(1,))
    c.hline(29, 38, 13 + b, NAVY[3]); c.hline(35, 39, 14 + b, NAVY[3])
    c.set(31, 10 + b, GOLD[1]); c.set(32, 10 + b, GOLD[0])
    # face
    c.rect(33, 17 + b, 34, 17 + b, PINK)
    if talk:
        c.rect(34, 19 + b, 36, 21 + b, RED[3]); c.hline(34, 36, 19 + b, SLUG[3])
    else:
        c.set(33, 19 + b, SLUG[3]); c.hline(34, 35, 20 + b, SLUG[3]); c.set(36, 19 + b, SLUG[3])
    c.outline()
    return c


def puffball_npc(sq=0, talk=False):
    c = Canvas(24, 28)
    c.rect(6, 25, 9, 27, CREAM[2]); c.rect(14, 25, 17, 27, CREAM[2])
    c.hline(6, 9, 27, DIRT[2]); c.hline(14, 17, 27, DIRT[2])
    rx, ry = 10 + sq * 0.6, 10 - sq * 0.8
    cy = 26 - ry
    body = S_ellipse(12, cy, rx, ry)
    body |= S_ellipse(12, cy - ry + 1.5, 2.6, 2.0)
    paint(c, body, PUFF, lw=(1,), sw=(1, 3))
    for (x, y) in sorted(body):
        v = hval("pf", x, y)
        if v < 0.07 and c.get(x, y) == PUFF[1]:
            c.set(x, y, PUFF[2])
    # pore on top
    c.set(12, int(cy - ry) + 1, PUFF[3]); c.set(11, int(cy - ry) + 1, PUFF[2])
    ey = int(cy) - 1
    for ex in (12, 17):
        c.rect(ex, ey, ex + 1, ey + 3, BLACK); c.set(ex, ey, WHITE); c.set(ex + 1, ey + 2, hx("4a3a5a"))
    c.rect(9, ey + 4, 10, ey + 4, PINK); c.rect(19, ey + 4, 20, ey + 4, PINK)
    if talk:
        c.rect(14, ey + 5, 16, ey + 6, RED[3]); c.set(15, ey + 6, PINK)
    else:
        c.set(14, ey + 5, PUFF[3]); c.set(15, ey + 6, PUFF[3]); c.set(16, ey + 5, PUFF[3])
    # tiny arms
    c.rect(1, int(cy) + 2, 2, int(cy) + 3, PUFF[2]); c.rect(21, int(cy) + 2 - (3 if talk else 0), 22,
                                                            int(cy) + 3 - (3 if talk else 0), PUFF[1])
    c.outline()
    if talk:
        for (x, y) in ((10, 1), (14, 0), (12, 2)):
            if c.get(x, y)[3] == 0:
                c.set(x, y, CREAM[1])
    return c


def wick_npc(bob=0, talk=False, flick=0):
    c = Canvas(28, 44)
    b = bob
    # lantern pole (behind body, in hand)
    pole = S_rect(20, 6, 20, 43)
    fill(c, pole, WOOD[2])
    for y in range(7, 43, 2):
        c.set(20, y, WOOD[1])
    c.hline(20, 25, 5, GREY[2]); c.hline(20, 25, 4, GREY[1])
    c.set(25, 6, GREY[2])
    # boots + coat
    c.rect(8, 41, 12, 43, BLACK); c.rect(15, 41, 19, 43, BLACK)
    coat = S_poly([(9, 21 + b), (19, 21 + b), (22, 41), (6, 41)])
    paint(c, coat, COAT, lw=(1,), sw=(1, 3))
    for y in range(23 + b, 40, 4):
        c.set(16, y, GOLD[1])
    c.hline(7, 21, 34, COAT[2])
    # scarf
    c.hline(9, 19, 21 + b, RED[1]); c.hline(9, 19, 22 + b, RED[2]); c.rect(10, 23 + b, 11, 27 + b, RED[2])
    # face
    face = S_ellipse(15, 16.5 + b, 5.5, 5)
    paint(c, face, [hx("f4ece0"), hx("dcd0c0"), hx("b0a090")], lw=(1,), sw=(1,))
    # round spectacles + eyes
    for gx in (15, 18):
        c.rect(gx, 14 + b, gx + 1, 15 + b, alpha(WHITE, 255))
        c.set(gx + 1, 15 + b, BLACK)
        c.set(gx, 13 + b, GOLD[2]); c.set(gx + 1, 13 + b, GOLD[2]); c.set(gx, 16 + b, GOLD[3]); c.set(gx + 1, 16 + b, GOLD[3])
    c.set(17, 14 + b, GOLD[2])
    c.set(20, 16 + b, hx("c8a090")); c.set(13, 17 + b, PINK)
    # mustache
    c.hline(15, 20, 18 + b, BEARD[1]); c.set(14, 19 + b, BEARD[2]); c.set(20, 19 + b, BEARD[2]); c.hline(16, 19, 19 + b, BEARD[2])
    if talk:
        c.rect(17, 20 + b, 18, 20 + b, RED[3])
    # inkcap: tall shaggy bell with dark drips
    cap = S_ellipse(13.5, 7.5 + b, 7, 8.5)
    cap = {p for p in cap if p[1] <= 11 + b}
    paint(c, cap, INK, lw=(1,), sw=(1, 3))
    shaggy(c, cap, INK, "wick", 10 + b)
    for x in range(7, 21):
        c.set(x, 11 + b, INK[4] if x % 2 else INK[3])
        if x % 3 == 0:
            c.set(x, 12 + b, INK[4])
    # front arm holding pole
    arm = S_line(17, 23 + b, 20, 27 + b, 1.5)
    paint(c, arm, COAT, lw=(1,), sw=(1,), edge=COAT[3])
    c.rect(19, 27 + b, 21, 28 + b, hx("dcd0c0"))
    # lantern
    lant = S_rect(23, 7, 27, 12)
    fill(c, lant, FLAME[1])
    c.rect(24, 8, 26, 11, FLAME[0] if flick == 0 else FLAME[1])
    c.set(25, 9 + flick, WHITE)
    c.hline(23, 27, 7, GREY[2]); c.hline(23, 27, 12, GREY[2]); c.rect(23, 7, 23, 12, GREY[2])
    c.rect(27, 7, 27, 12, GREY[2]); c.set(25, 13, GREY[2])
    c.outline()
    glow(c, 25, 9.5, 6.5 + flick, FLAME[2], ((1.0, 50), (0.7, 100)))
    return c


# ---------------------------------------------------------------- portraits (64x64)
def portrait_panel(top, bottom, seed):
    """Rounded framed background panel; character is painted on top, then clip() keeps it inside."""
    c = Canvas(64, 64)
    panel = S_rect(2, 2, 61, 61) - {(2, 2), (61, 2), (2, 61), (61, 61), (3, 2), (2, 3), (60, 2), (61, 3),
                                    (2, 60), (3, 61), (61, 60), (60, 61)}
    for (x, y) in panel:
        t = (y - 2) / 59
        band = min(3, int(t * 4))
        cols = [mix(top, bottom, i / 3) for i in range(4)]
        # ordered-dither between bands for a smooth SNES gradient
        tt = t * 4 - band
        if band < 3 and tt > 0.5 and ((x + y) % 2 == 0):
            c.set(x, y, cols[band + 1])
        else:
            c.set(x, y, cols[band])
    return c, panel


def portrait_finish(c, panel, frame_cols=(hx("fff3c8"), hx("e8b860"), hx("a06a2a"))):
    # remove anything outside the panel, then frame it
    for y in range(64):
        for x in range(64):
            if (x, y) not in panel:
                c.px[y][x] = CLEAR
    ring = {p for p in panel if any((p[0] + dx, p[1] + dy) not in panel for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))}
    inner = {p for p in panel - ring if any((p[0] + dx, p[1] + dy) in ring for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))}
    for (x, y) in inner:
        c.set(x, y, frame_cols[0] if (x <= 4 or y <= 4) else frame_cols[1])
    for (x, y) in ring:
        c.set(x, y, frame_cols[2])
    c.outline()
    return c


def char_layer():
    return Canvas(64, 64)


def merge(dst, src):
    blit(dst, src, 0, 0)


def p_eye(c, x, y, w=3, h=5, look=1, iris=hx("3a2a4a")):
    """Big expressive eye: white sclera, dark iris/pupil toward look side, glints."""
    sc = S_ellipse(x + w / 2, y + h / 2, w / 2 + 0.3, h / 2 + 0.2)
    fill(c, sc, WHITE)
    px0 = x + (w - 2 if look > 0 else 0)
    c.rect(px0, y + 1, px0 + 1, y + h - 1, iris)
    c.rect(px0, y + 2, px0 + 1, y + h - 2, BLACK)
    c.set(px0, y + 1, WHITE)
    for (xx, yy) in sc:
        if (xx, yy - 1) not in sc:
            c.set(xx, yy, OUT)


def portrait_pip():
    c, panel = portrait_panel(hx("bfe8ff"), hx("7cc4f0"), "pip")
    ch = char_layer()
    # shoulders + tunic
    tun = S_ellipse(31, 68, 26, 15)
    paint(ch, tun, GREEN, lw=(1,), sw=(2, 5))
    collar = S_poly([(26, 53), (40, 53), (34, 60)])
    fill(ch, collar, GREEN[2])
    strap = S_line(18, 54, 23, 64, 2.2)
    paint(ch, strap, DIRT, lw=(1,), sw=(1,), edge=DIRT[3])
    bag = S_ellipse(10, 60, 7, 8)
    paint(ch, bag, DIRT, lw=(1,), sw=(1, 3), edge=DIRT[3])
    # neck
    neck = S_rect(28, 46, 37, 55)
    paint(ch, neck, SKIN, lw=(0,), sw=(2,), edge=SKIN[2])
    # face (3/4 to the right)
    face = S_ellipse(34, 39, 13.5, 11.5) | S_ellipse(39, 45, 8, 5)
    face |= S_ellipse(47.5, 41, 2.2, 2.0)  # nose tip on the profile edge
    paint(ch, face, SKIN, lw=(1,), sw=(1, 3))
    # hair at the back + sideburn
    hair = S_ellipse(24, 35, 7.5, 9) & S_ellipse(34, 39, 13.9, 12)
    hair |= S_rect(22, 30, 31, 33) & face
    paint(ch, hair, [DIRT[1], DIRT[2], DIRT[3], DIRT[3]], lw=(1,), sw=(1, 2))
    ear = S_ellipse(27, 40, 3, 4)
    paint(ch, ear, SKIN, lw=(1,), sw=(1,), edge=SKIN[2])
    ch.set(27, 40, SKIN[2]); ch.set(27, 41, SKIN[2])
    # eyes, brows, mouth, blush
    p_eye(ch, 34, 34, 4, 7, 1)
    p_eye(ch, 42, 34, 3, 6, 1)
    ch.hline(33, 37, 32, DIRT[3]); ch.hline(42, 44, 32, DIRT[3]); ch.set(38, 31, DIRT[3])
    ch.set(39, 46, RED[3]); ch.hline(40, 43, 47, RED[3]); ch.set(44, 46, RED[3]); ch.hline(41, 42, 48, RED[1])
    for (x, y) in ((32, 44), (33, 44), (34, 44), (33, 45)):
        ch.set(x, y, PINK)
    ch.set(46, 44, PINK)
    # cap (dome + brim + spots)
    cap = S_ellipse(33, 28, 29, 24)
    cap = {p for p in cap if p[1] <= 27}
    paint(ch, cap, RED, lw=(1, 3), sw=(2, 5))
    brim = S_poly([(3, 27), (63, 25), (62, 29), (5, 31)])
    fill(ch, brim, RED[2])
    under = S_poly([(6, 30), (61, 28), (58, 31), (9, 32)])
    fill(ch, under, CREAM[2])
    for x in range(10, 58, 3):
        ch.set(x, 31, CREAM[1])
    for (sx, sy, rx, ry) in ((14, 18, 4, 3), (30, 10, 5.5, 3.5), (47, 13, 4.5, 3), (22, 23, 2.6, 1.8),
                             (40, 21, 3, 2), (55, 22, 2.5, 2), (8, 24, 2, 1.6)):
        sp = S_ellipse(sx, sy, rx, ry) & cap
        paint(ch, sp, CREAM, lw=(1,), sw=(1,))
    ch.outline()
    merge(c, ch)
    return portrait_finish(c, panel)


def portrait_morel():
    c, panel = portrait_panel(hx("ffe2a8"), hx("e09a5a"), "morel")
    ch = char_layer()
    robe = S_ellipse(32, 70, 30, 17)
    paint(ch, robe, ROBE, lw=(1,), sw=(2, 5))
    sash = S_line(10, 58, 54, 66, 0.8) & robe
    fill(ch, sash, GOLD[2])
    face = S_ellipse(35, 38, 12, 9.5)
    paint(ch, face, CREAM, lw=(1,), sw=(1, 3))
    beard = S_poly([(21, 38), (50, 38), (49, 48), (42, 58), (36, 63), (30, 58), (24, 48)])
    paint(ch, beard, BEARD, lw=(1, 2), sw=(1, 3))
    for k in range(9):
        x0 = 26 + k * 2.6
        for y in range(44, 60, 1):
            if (int(x0 + (y - 44) * 0.15), y) in beard and (y + k) % 4 == 0:
                ch.set(int(x0 + (y - 44) * 0.15), y, BEARD[2])
    # mouth hidden in beard
    ch.hline(38, 44, 44, BEARD[3]); ch.set(37, 43, BEARD[3]); ch.set(45, 43, BEARD[3])
    # eyes (kind, half closed) + bushy brows
    for ex in (35, 45):
        ch.hline(ex, ex + 3, 37, BLACK); ch.rect(ex + 1, 36, ex + 2, 36, BLACK); ch.set(ex + 1, 36, hx("4a3a5a"))
    brow = S_ellipse(36, 33.5, 5, 2) | S_ellipse(46.5, 33.5, 4.5, 2)
    paint(ch, brow, BEARD, lw=(1,), sw=(1,))
    nose = S_ellipse(43, 40, 3.6, 3.2)
    paint(ch, nose, [SKIN[0], SKIN[1], SKIN[2]], lw=(1,), sw=(1,))
    ch.set(30, 40, PINK); ch.set(31, 40, PINK); ch.set(31, 41, PINK)
    # tall morel cap
    cap = S_ellipse(32, 18, 17, 24)
    cap = {p for p in cap if p[1] <= 29}
    paint(ch, cap, MOREL, lw=(1, 2), sw=(2, 4))
    morel_pits(ch, cap, MOREL, "pmorel", 6, 8)
    brim = S_rect(14, 29, 50, 31) - {(14, 31), (50, 31)}
    paint(ch, brim, MOREL[2:], lw=(0,), sw=(1,))
    # staff top with leaf
    staff = S_curve([(58, 64), (57, 40), (59, 30), (56, 26)], 1.6, 1.4)
    paint(ch, staff, WOOD, lw=(1,), sw=(1,))
    lf = S_ellipse(61, 25, 3, 2)
    paint(ch, lf, LEAF, lw=(1,), sw=(1,))
    ch.outline()
    merge(c, ch)
    return portrait_finish(c, panel)


def portrait_chanterelle():
    c, panel = portrait_panel(hx("ffe8c8"), hx("e8a878"), "truf")
    ch = char_layer()
    body = S_ellipse(32, 42, 25, 22)
    for (bx, by, r) in ((12, 28, 7), (24, 20, 7), (40, 19, 7.5), (52, 28, 7), (8, 44, 6), (56, 44, 6)):
        body |= S_ellipse(bx, by, r, r * 0.9)
    paint(ch, body, TRUF, lw=(1,), sw=(2, 5))
    for (x, y) in sorted(body):
        v = hval("ptruf", x // 2, y // 2)
        if v < 0.09 and ch.get(x, y) == TRUF[1]:
            ch.rect(x, y, x + 1, y, TRUF[0]); ch.set(x, y + 1, TRUF[2])
    apron = S_poly([(14, 52), (50, 52), (52, 64), (12, 64)])
    paint(ch, apron, APRON, lw=(1,), sw=(1,))
    for x in range(16, 50, 5):
        for y in range(53, 64):
            ch.set(x, y, hx("e8c27c"))
    ch.hline(14, 50, 52, APRON[2])
    for (sx, ex) in ((16, 22), (42, 48)):
        st = S_line(sx, 52, sx + 2, 40, 1.4) if sx < 30 else S_line(ex, 52, ex - 2, 40, 1.4)
        fill(ch, st, APRON[1])
    # face
    p_eye(ch, 34, 33, 4, 6, 1)
    p_eye(ch, 45, 33, 4, 6, 1)
    ch.hline(33, 38, 31, TRUF[3]); ch.hline(45, 49, 31, TRUF[3])
    mouth = S_ellipse(42, 44, 5, 3.2)
    mouth = {p for p in mouth if p[1] >= 43}
    fill(ch, mouth, RED[3])
    for (x, y) in mouth:
        if y >= 46:
            ch.set(x, y, PINK)
    ch.hline(37, 47, 43, TRUF[3])
    must = S_ellipse(38, 41, 3.5, 1.6) | S_ellipse(46, 41, 3.5, 1.6)
    paint(ch, must, [TRUF[2], TRUF[3], OUT], lw=(1,), sw=(1,))
    for (x, y) in ((30, 40), (31, 40), (30, 41), (51, 40), (52, 40)):
        ch.set(x, y, PINK)
    # sprout
    st = S_line(32, 14, 32, 7, 0.8)
    fill(ch, st, LEAF[2])
    lf = S_ellipse(37, 7, 5, 2.5)
    paint(ch, lf, LEAF, lw=(1,), sw=(1,))
    lf2 = S_ellipse(27.5, 9, 3.5, 2)
    paint(ch, lf2, LEAF, lw=(1,), sw=(1,))
    ch.outline()
    merge(c, ch)
    # coin glint in the corner (shopkeeper)
    return portrait_finish(c, panel)


def portrait_bram():
    c, panel = portrait_panel(hx("d8ecff"), hx("8ab0e8"), "bram")
    ch = char_layer()
    # shell behind (left)
    sc = (14, 40)
    shell = S_ellipse(sc[0], sc[1], 19, 19)
    paint(ch, shell, SHELL, lw=(1,), sw=(2, 5))
    for k in range(240):
        t = k / 239
        a = t * 4.4 * math.pi
        r = 17 * (1 - t) + 1
        for d in (0, 1):
            ch.set(int(sc[0] + math.cos(a) * (r + d * 0.6)), int(sc[1] + math.sin(a) * (r + d * 0.6)), SHELL[3])
    # head / neck
    head = S_ellipse(40, 46, 15, 17) | S_rect(30, 50, 56, 64)
    paint(ch, head, SLUG, lw=(1,), sw=(2, 5))
    # strap + satchel
    strap = S_line(18, 22, 34, 64, 2.0)
    paint(ch, strap, DIRT, lw=(1,), sw=(1,), edge=DIRT[3])
    # eye stalks + eyes
    for (bx, tx, ty) in ((36, 31, 8), (46, 50, 9)):
        st = S_curve([(bx, 34), ((bx + tx) / 2, 20), (tx, ty + 4)], 2.2, 1.6)
        paint(ch, st, SLUG, lw=(1,), sw=(1,), edge=SLUG[3])
        ball = S_ellipse(tx, ty, 5, 5)
        fill(ch, ball, WHITE)
        for (x, y) in ball:
            if (x + 1, y + 1) not in ball or (x + 2, y + 2) not in ball:
                ch.set(x, y, BEARD[2])
        ch.rect(tx, ty - 2, tx + 2, ty + 2, BLACK); ch.set(tx, ty - 2, WHITE); ch.set(tx + 1, ty - 2, WHITE)
    # postman cap
    cap = S_ellipse(41, 33, 13, 6)
    cap = {p for p in cap if p[1] <= 34}
    paint(ch, cap, NAVY, lw=(1,), sw=(1, 3))
    visor = S_poly([(36, 34), (59, 34), (60, 37), (44, 37)])
    paint(ch, visor, NAVY[1:], lw=(0,), sw=(1,))
    badge = S_ellipse(41, 30, 2.3, 2.3)
    paint(ch, badge, GOLD, lw=(1,), sw=(1,))
    # face
    ch.hline(44, 52, 48, SLUG[3]); ch.set(43, 47, SLUG[3]); ch.set(53, 47, SLUG[3])
    ch.hline(46, 50, 49, RED[2])
    for (x, y) in ((53, 43), (54, 43), (55, 43), (54, 44)):
        ch.set(x, y, PINK)
    # letter peeking up from the satchel at the bottom-left
    let = S_rect(20, 55, 30, 62)
    fill(ch, let, WHITE)
    ch.hline(20, 30, 62, GREY[0]); ch.rect(24, 57, 26, 58, RED[1])
    ch.outline()
    merge(c, ch)
    return portrait_finish(c, panel)


def portrait_puffball():
    c, panel = portrait_panel(hx("ffe4f4"), hx("e8a8d8"), "puff")
    ch = char_layer()
    body = S_ellipse(32, 40, 24, 22) | S_ellipse(32, 18.5, 5, 3.5)
    paint(ch, body, PUFF, lw=(1, 3), sw=(2, 5))
    for (x, y) in sorted(body):
        if hval("ppf", x // 2, y // 2) < 0.06 and ch.get(x, y) == PUFF[1]:
            ch.set(x, y, PUFF[2])
    ch.rect(30, 17, 33, 18, PUFF[3])
    # big sparkly eyes
    for ex in (24, 38):
        e = S_ellipse(ex + 3, 38, 3.6, 5.2)
        fill(ch, e, hx("2e2240"))
        for (x, y) in e:
            if y >= 40:
                ch.set(x, y, hx("5a4880"))
        ch.rect(ex + 1, 35, ex + 2, 36, WHITE); ch.set(ex + 4, 41, WHITE)
    for (x, y) in ((19, 45), (20, 45), (21, 45), (20, 46), (44, 45), (45, 45), (46, 45), (45, 46)):
        ch.set(x, y, PINK)
    mouth = S_ellipse(34, 47, 3.5, 2.6)
    mouth = {p for p in mouth if p[1] >= 47}
    fill(ch, mouth, RED[3])
    ch.hline(33, 35, 49, PINK)
    ch.outline()
    merge(c, ch)
    for (x, y) in ((22, 10), (40, 8), (46, 13), (15, 16)):
        sparkle(c, x, y, WHITE, 1, hx("ffd0ec"))
    return portrait_finish(c, panel)


def portrait_wick():
    c, panel = portrait_panel(hx("6a78c8"), hx("2c2c6a"), "wick")
    ch = char_layer()
    coat = S_ellipse(30, 70, 28, 16)
    paint(ch, coat, COAT, lw=(1,), sw=(2, 5))
    scarf = S_ellipse(32, 54, 15, 4) | S_poly([(20, 54), (26, 54), (24, 64), (18, 64)])
    paint(ch, scarf, [RED[0], RED[1], RED[2], RED[3]], lw=(1,), sw=(1, 2))
    face = S_ellipse(34, 41, 12, 11)
    paint(ch, face, [hx("f4ece0"), hx("dcd0c0"), hx("b0a090"), hx("8a7a70")], lw=(1,), sw=(1, 3))
    # spectacles
    for gx in (32, 43):
        ring = S_ellipse(gx + 0.5, 39.5, 4, 4)
        inner = S_ellipse(gx + 0.5, 39.5, 3, 3)
        fill(ch, ring - inner, GOLD[2])
        fill(ch, inner, hx("e8f4ff"))
        ch.rect(gx + 1, 39, gx + 2, 41, BLACK); ch.set(gx + 1, 39, WHITE)
    ch.hline(37, 39, 39, GOLD[2])
    # mustache
    must = S_ellipse(36, 47, 5, 2) | S_ellipse(45, 47, 5, 2)
    paint(ch, must, BEARD, lw=(1,), sw=(1,))
    ch.hline(39, 43, 50, RED[3])
    ch.set(48, 44, hx("c8a090")); ch.set(48, 45, hx("c8a090"))
    # inkcap
    cap = S_ellipse(30, 18, 16, 20)
    cap = {p for p in cap if p[1] <= 32}
    paint(ch, cap, INK, lw=(1, 2), sw=(2, 4))
    shaggy(ch, cap, INK, "pwick", 29)
    for x in range(14, 47):
        ch.set(x, 32, INK[4])
        if x % 3 == 0:
            ch.set(x, 33, INK[4])
            if x % 6 == 0:
                ch.set(x, 34, INK[4])
    # lantern at right
    pole = S_rect(56, 6, 57, 64)
    fill(ch, pole, WOOD[2])
    lant = S_rect(49, 20, 59, 32)
    fill(ch, lant, GREY[2])
    gl = S_rect(51, 22, 57, 30)
    fill(ch, gl, FLAME[1])
    fill(ch, S_rect(53, 24, 55, 28), FLAME[0])
    ch.hline(48, 60, 19, GREY[1]); ch.hline(50, 58, 18, GREY[1]); ch.set(54, 17, GREY[2])
    ch.outline()
    # warm glow on the background behind the lantern
    for (x, y) in sorted(S_ellipse(54, 26, 16, 16)):
        if c.get(x, y)[3] and ch.get(x, y)[3] == 0:
            d = math.hypot(x - 54, y - 26)
            if d < 16 and ((x + y) % 2 == 0 or d < 11):
                c.set(x, y, mix(c.get(x, y), FLAME[2], 0.35 if d > 11 else 0.55))
    merge(c, ch)
    return portrait_finish(c, panel)


def fluff_collar(cx, cy, rx, ry, thick, seed):
    """Crescent fur collar: a band under (cx, cy) with tufted scalloped lower edge."""
    s = set()
    for x in range(int(cx - rx) - 2, int(cx + rx) + 3):
        t = (x - cx) / rx
        if abs(t) > 1:
            continue
        ytop = cy - ry * 0.4 + ry * (t * t) * 0.6 - 2
        depth = thick * (1 - 0.55 * t * t)
        tuft = 2.2 * abs(math.sin((x - cx) * 0.75)) + hval(seed, x) * 1.2
        for y in range(int(ytop), int(ytop + depth + tuft) + 1):
            s.add((x, y))
    return s


def fur_strands(cv, shape, col, seed):
    for (x, y) in sorted(shape):
        if hval(seed, "s", x, y) < 0.09 and (x, y + 2) in shape:
            cv.set(x, y, col); cv.set(x, y + 1, col)


def portrait_duskmantle():
    c, panel = portrait_panel(hx("4a2a78"), hx("140c26"), "gloom")
    ch = char_layer()
    # wings behind
    for s in (-1, 1):
        wing = S_poly([(32, 40), (32 + s * 10, 18), (32 + s * 30, 6), (32 + s * 34, 30), (32 + s * 22, 60)])
        paint(ch, wing, VIOLET[1:], lw=(1,), sw=(2, 4))
        spot = S_ellipse(32 + s * 22, 26, 5.5, 5.5)
        paint(ch, spot, [LILAC[1], LILAC[2], VIOLET[3]], lw=(1,), sw=(1,))
        fill(ch, S_ellipse(32 + s * 22, 26, 2.6, 2.6), MAGENTA[1])
        ch.set(32 + s * 22 - 1, 25, MAGENTA[0])
        edge = {p for p in wing if any((p[0] + dx, p[1] + dy) not in wing for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))}
        for (x, y) in edge:
            if (x + y) % 3:
                ch.set(x, y, LILAC[2])
    # body + fur ruff
    thor = S_ellipse(32, 60, 14, 10)
    paint(ch, thor, VIOLET[1:], lw=(1,), sw=(2, 4))
    ruff = fluff_collar(32, 49, 21, 7, 11, "gruff")
    paint(ch, ruff, LILAC, lw=(1,), sw=(1, 2))
    fur_strands(ch, ruff, LILAC[2], "gruff")
    # head
    head = S_ellipse(32, 36, 13, 12)
    paint(ch, head, VIOLET, lw=(1,), sw=(2, 4))
    for (x, y) in sorted(head):
        if hval("gfur", x, y) < 0.12 and ch.get(x, y) == VIOLET[1]:
            ch.set(x, y, VIOLET[0])
    # glowing eyes, angled menacingly
    for s in (-1, 1):
        e = S_poly([(32 + s * 3, 36), (32 + s * 11, 31), (32 + s * 11, 38), (32 + s * 5, 40)])
        fill(ch, e, GLOW[1])
        for (x, y) in e:
            if abs(x - (32 + s * 7)) <= 1 and 33 <= y <= 37:
                ch.set(x, y, WHITE)
        brow = S_line(32 + s * 2, 34, 32 + s * 12, 28, 0.9)
        fill(ch, brow, VIOLET[4])
    # crown gem
    gem = S_poly([(32, 23), (35, 27), (32, 31), (29, 27)])
    paint(ch, gem, MAGENTA, lw=(1,), sw=(1,))
    # fangs/mouth
    ch.hline(29, 35, 44, VIOLET[4]); ch.set(30, 45, WHITE); ch.set(34, 45, WHITE)
    # feathery antennae
    for s in (-1, 1):
        pts = bezier((32 + s * 4, 26), (32 + s * 8, 6), (32 + s * 24, 4), 16)
        for i, (x, y) in enumerate(pts):
            ch.set(int(x), int(y), VIOLET[3])
            ch.set(int(x) + 1, int(y), VIOLET[3])
            if i % 2 == 0 and i > 2:
                ch.thick_line(x, y, x + s * 2, y - 3, 0.3, LILAC[2])
    ch.outline()
    merge(c, ch)
    for (x, y) in ((8, 50), (56, 52), (12, 8), (52, 14)):
        if c.get(x, y)[3]:
            c.set(x, y, MAGENTA[0])
    return portrait_finish(c, panel, (hx("e2d2f6"), hx("8a74b4"), hx("3c2a70")))


# ---------------------------------------------------------------- enemies
def vflipped(src):
    c = Canvas(src.w, src.h)
    for y in range(src.h):
        c.px[src.h - 1 - y] = list(src.px[y])
    return c


def flit_bat(mode):
    """mode 0 wings up, 1 wings down, 2 hanging (upside down, wrapped)."""
    c = Canvas(32, 24)
    if mode == 2:
        b = Canvas(32, 24)
        wrap = S_ellipse(16, 13, 7, 8.5)
        paint(b, wrap, MEMB[:1] + BAT[1:], lw=(1,), sw=(1, 3))
        for y in range(7, 21, 3):
            b.set(13, y, BAT[3]); b.set(19, y + 1, BAT[3])
        head = S_ellipse(16, 6, 4.5, 4)
        paint(b, head, BAT, lw=(1,), sw=(1,))
        for (x0, s) in ((12, -1), (20, 1)):
            ear = S_poly([(x0 - 2 * s, 5), (x0 + 1.5 * s, 0), (x0 + 2 * s, 5)])
            paint(b, ear, BAT, lw=(1,), sw=(1,))
        b.hline(13, 14, 7, BLACK); b.hline(18, 19, 7, BLACK)  # sleepy closed eyes
        b.set(16, 9, WHITE)
        b.rect(14, 22, 14, 23, BAT[3]); b.rect(18, 22, 18, 23, BAT[3])
        b.outline()
        return vflipped(b)
    body = S_ellipse(16, 13, 5.5, 5.5)
    if mode == 0:
        wl = S_poly([(13, 12), (8, 3), (3, 1), (1, 6), (4, 9), (6, 9), (8, 14)])
        cuts = [S_ellipse(3, 10.5, 2.2, 2.2), S_ellipse(7, 11.5, 1.8, 1.8)]
    else:
        wl = S_poly([(12, 11), (6, 12), (1, 17), (2, 22), (5, 19), (7, 21), (10, 17)])
        cuts = [S_ellipse(3.5, 21, 1.6, 1.8), S_ellipse(8, 21, 1.6, 1.6)]
    for cu in cuts:
        wl -= cu
    wr = {(31 - x, y) for (x, y) in wl}
    for w in (wl, wr):
        paint(c, w, MEMB, lw=(1,), sw=(1,))
        # finger bones
    for s, w in ((-1, wl), (1, wr)):
        tip = (16 + s * 14, 2 if mode == 0 else 19)
        for k in range(10):
            t = k / 9
            x = int(round(16 + s * 4 + (tip[0] - 16 - s * 4) * t))
            y = int(round(12 + (tip[1] - 12) * t))
            if (x, y) in w:
                c.set(x, y, BAT[2])
    paint(c, body, BAT, lw=(1,), sw=(1, 2))
    for (x0, s) in ((13, -1), (19, 1)):
        ear = S_poly([(x0 - 2 * s, 9), (x0 + 1 * s, 3), (x0 + 2.5 * s, 9)])
        paint(c, ear, BAT, lw=(1,), sw=(1,))
        c.set(x0, 7, MEMB[1])
    c.rect(13, 11, 14, 12, GOLD[1]); c.rect(18, 11, 19, 12, GOLD[1])
    c.set(14, 12, BLACK); c.set(18, 12, BLACK)
    c.hline(15, 17, 15, BAT[3]); c.set(15, 16, WHITE); c.set(17, 16, WHITE)
    c.set(14, 18, BAT[3]); c.set(18, 18, BAT[3])
    c.outline()
    return c


def glow_grub(phase):
    c = Canvas(32, 20)
    segs = []
    for k in range(5):
        x = 6 + k * 4.6
        lift = (1.5 if (k + phase) % 2 == 0 else 0)
        segs.append((x, 13 - lift, 4.2, 4.6 + lift * 0.3))
    for k, (x, y, rx, ry) in enumerate(segs):
        seg = S_ellipse(x, y, rx, ry)
        seg = {p for p in seg if p[1] <= 18}
        paint(c, seg, GRUB, lw=(1,), sw=(1, 2), edge=GRUB[3])
        # stubby legs
        c.set(int(x), 18, GRUB[3]); c.set(int(x) + 1, 18, GRUB[3])
    # head
    head = S_ellipse(27, 12.5 - (phase * 0.5), 4.5, 4.5)
    paint(c, head, [hx("ffe0b0"), hx("f0b878"), hx("c88a50"), hx("8a5a30")], lw=(1,), sw=(1, 2), edge=GRUB[3])
    hy = int(12.5 - phase * 0.5)
    c.rect(28, hy - 2, 29, hy - 1, BLACK); c.set(28, hy - 2, WHITE)
    c.set(31, hy + 1, GRUB[3]); c.set(30, hy + 2, GRUB[3])
    c.set(26, hy + 1, PINK)
    # antennae
    c.set(27, hy - 5, GRUB[3]); c.set(26, hy - 6, GRUB[3]); c.set(29, hy - 5, GRUB[3]); c.set(30, hy - 6, GRUB[3])
    # glowing bulbs along the back
    bulbs = []
    for k, (x, y, rx, ry) in enumerate(segs):
        bx, by = x, y - ry + 0.5
        bulb = S_ellipse(bx, by, 2.3, 2.0)
        paint(c, bulb, GLOWG[:3], lw=(1,), sw=(1,))
        c.set(int(bx), int(by) - 1, WHITE)
        bulbs.append((bx, by))
    c.outline()
    for (bx, by) in bulbs:
        glow(c, bx, by - 0.5, 4.2 + (0.6 if phase else 0), GLOWG[2], ((1.0, 60), (0.6, 120)))
    return c


def bloatfin(mode, phase):
    """mode 0 swim, 1 puffed (spiky ball)."""
    c = Canvas(32, 28)
    if mode == 0:
        # tail
        tw = 5 if phase == 0 else 3
        tail = S_poly([(9, 14), (2, 14 - tw), (4, 14), (2, 14 + tw)])
        paint(c, tail, FISH, lw=(1,), sw=(1,))
        body = S_ellipse(17, 14, 10, 7.5)
        paint(c, body, FISH, lw=(1,), sw=(1, 2))
        belly = {p for p in body if p[1] >= 16 and p[0] > 11}
        paint(c, belly, CREAM, lw=(0,), sw=(1,), only=belly)
        for (x, y) in ((13, 10), (17, 9), (11, 13), (20, 11), (15, 12)):
            c.set(x, y, FISH[3]); c.set(x + 1, y, FISH[3])
        # fins
        fin = S_poly([(15, 7), (19, 3 + phase), (21, 7)])
        paint(c, fin, FISH, lw=(1,), sw=(1,))
        pec = S_ellipse(16, 16 + phase, 2.6, 1.6)
        paint(c, pec, [FISH[0], FISH[1], FISH[2]], lw=(1,), sw=(1,), edge=FISH[3])
        # face
        c.rect(22, 10, 24, 13, WHITE); c.rect(23, 11, 24, 13, BLACK); c.set(23, 11, WHITE)
        c.rect(26, 15, 27, 16, hx("ff8a8a")); c.set(27, 15, RED[2])
        c.outline()
        bubble(c, 29, 8 - phase * 2)
        return c
    r = 10.5 + phase * 0.8
    spikes = set()
    n = 14
    for k in range(n):
        a = k / n * 2 * math.pi + phase * 0.22
        L = r + 3.5 + (1 if k % 2 == phase else 0)
        spikes |= S_line(16 + math.cos(a) * (r - 1), 14 + math.sin(a) * (r - 1),
                         16 + math.cos(a) * L, 14 + math.sin(a) * L, 0.55)
    fill(c, spikes, FISH[3])
    for k in range(n):
        a = k / n * 2 * math.pi + phase * 0.22
        L = r + 3.5 + (1 if k % 2 == phase else 0)
        c.set(int(16 + math.cos(a) * L), int(14 + math.sin(a) * L), CREAM[0])
    body = S_ellipse(16, 14, r, r * 0.95)
    paint(c, body, FISH, lw=(1,), sw=(1, 3))
    belly = {p for p in body if p[1] >= 17}
    fill(c, belly, CREAM[1])
    for (x, y) in belly:
        if (x, y + 1) not in body:
            c.set(x, y, CREAM[2])
    for (x, y) in ((10, 8), (15, 6), (21, 9), (8, 13), (24, 13), (13, 11)):
        c.set(x, y, FISH[3]); c.set(x + 1, y, FISH[3])
    # angry face
    for ex in (17, 23):
        c.rect(ex, 11, ex + 2, 13, WHITE); c.rect(ex + 1, 12, ex + 2, 13, BLACK)
    c.hline(16, 19, 10, BLACK); c.hline(22, 25, 10, BLACK); c.set(19, 11, BLACK); c.set(22, 11, BLACK)
    c.rect(20, 17, 23, 18, RED[3]); c.hline(20, 23, 17, hx("ff8a8a"))
    c.outline()
    return c


def driftjelly(phase):
    c = Canvas(24, 32)
    rx, ry = (10, 8) if phase == 0 else (8.8, 9)
    top = 2 if phase == 0 else 1
    tentacles = []
    for k, x0 in enumerate((6, 10, 14, 18)):
        pts = []
        for y in range(12, 31 - (k % 2) * 2):
            i = (y - 12) / 1.8
            x = x0 + math.sin(i * 0.8 + phase * 1.6 + k) * 1.4 * (i / 10)
            pts.append((int(round(x)), y))
        tentacles.append(pts)
    # frilly oral arms
    frill = set()
    for k in range(7):
        frill |= S_ellipse(8 + k * 1.4, 13 + (k % 2) + phase, 1.2, 2.2)
    fill(c, frill, alpha(JELLY[1], 235))
    bell = S_ellipse(12, top + ry, rx, ry)
    bell = {p for p in bell if p[1] <= top + ry + 1}
    for k in range(5):
        bell |= S_ellipse(12 - rx + 2 + k * (rx * 2 - 4) / 4, top + ry + 1.2, 1.8, 1.4)
    paint(c, bell, JELLY, lw=(1,), sw=(1, 2))
    # translucency: interior slightly see-through
    for (x, y) in bell:
        col = c.get(x, y)
        if col == JELLY[1]:
            c.set(x, y, alpha(JELLY[1], 205))
    # glowing inner core + face
    core = S_ellipse(12, top + ry - 2, 3.2, 2.4)
    fill(c, core, alpha(hx("fff0ff"), 230))
    cy = int(top + ry + 1)
    c.set(9, cy - 2, BLACK); c.set(9, cy - 3, BLACK); c.set(15, cy - 2, BLACK); c.set(15, cy - 3, BLACK)
    c.set(12, cy - 1, JELLY[3])
    c.set(7, top + 3, WHITE); c.set(8, top + 2, WHITE)
    c.outline(hx("5a2a6a"))
    # tentacles after the outline: thin, continuous, translucent, with electric sparks
    for k, pts in enumerate(tentacles):
        for j, (x, y) in enumerate(pts):
            if c.get(x, y)[3] == 0:
                c.set(x, y, alpha(JELLY[2] if j % 4 else JELLY[1], 235))
        x, y = pts[-1]
        c.set(x, y, JELLY[3])
    sparks = ((6, 19), (14, 24), (10, 27)) if phase == 0 else ((10, 21), (18, 18), (6, 25))
    for (x, y) in sparks:
        for k, pts in enumerate(tentacles):
            for (px, py) in pts:
                if py == y and abs(px - x) <= 1:
                    c.set(px, py, GOLD[0])
                    if c.get(px + 1, py)[3] == 0:
                        c.set(px + 1, py, alpha(GOLD[1], 150))
                    break
    return c


def dusk_moth(phase):
    c = Canvas(32, 24)
    for s in (-1, 1):
        if phase == 0:
            fore = S_poly([(16, 11), (16 + s * 4, 4), (16 + s * 13, 1), (16 + s * 15, 6), (16 + s * 11, 12)])
            hind = S_poly([(16, 13), (16 + s * 10, 12), (16 + s * 11, 18), (16 + s * 5, 19)])
        else:
            fore = S_poly([(16, 10), (16 + s * 8, 7), (16 + s * 15, 10), (16 + s * 14, 16), (16 + s * 6, 14)])
            hind = S_poly([(16, 13), (16 + s * 8, 15), (16 + s * 8, 22), (16 + s * 3, 20)])
        paint(c, hind, MOTH[1:], lw=(1,), sw=(1,))
        paint(c, fore, MOTH, lw=(1,), sw=(1, 2))
        # eye spot on forewing
        ex = 16 + s * (10 if phase == 0 else 10)
        ey = 6 if phase == 0 else 11
        c.rect(ex - 1, ey - 1, ex, ey, GOLD[1]); c.set(ex - (1 if s > 0 else 0), ey - 1, GOLD[0])
        for (x, y) in fore:
            if any((x + dx, y + dy) not in fore for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))) and (x + y) % 2:
                c.set(x, y, MOTH[0])
    body = S_ellipse(16, 13, 2.8, 6.5)
    paint(c, body, FUZZ, lw=(1,), sw=(1,))
    for y in range(14, 20, 2):
        c.hline(15, 17, y, FUZZ[2])
    head = S_ellipse(16, 7.5, 3, 2.6)
    paint(c, head, FUZZ, lw=(1,), sw=(1,))
    c.set(15, 7, MAGENTA[1]); c.set(17, 7, MAGENTA[1])
    for s in (-1, 1):
        c.set(16 + s, 5, MOTH[3]); c.set(16 + s * 2, 4, MOTH[3]); c.set(16 + s * 3, 3, MOTH[3]); c.set(16 + s * 3, 2, MOTH[3])
        c.set(16 + s * 4, 3, MOTH[2]); c.set(16 + s * 2, 2, MOTH[2])
    c.outline()
    c.set(15, 7, hx("ff9ae0")); c.set(17, 7, hx("ff9ae0"))
    return c


def burr_hopper(jump):
    c = Canvas(28, 28)
    by = 16 if not jump else 10
    # legs (grasshopper style)
    for s in (-1, 1):
        hipx = 14 + s * 5
        if not jump:
            knee = (14 + s * 12, by - 3)
            foot = (14 + s * 11, 26)
        else:
            knee = (14 + s * 8, by + 9)
            foot = (14 + s * 7, 26)
        leg = S_line(hipx, by + 4, knee[0], knee[1], 1.1) | S_line(knee[0], knee[1], foot[0], foot[1], 0.8)
        paint(c, leg, BURR[1:], lw=(1,), sw=(1,))
        c.rect(foot[0] - 1, 26, foot[0] + 1 + s, 27, BURR[3])
    # spiky hooks
    spikes = set()
    n = 16
    for k in range(n):
        a = k / n * 2 * math.pi
        x0, y0 = 14 + math.cos(a) * 7, by + math.sin(a) * 7
        x1, y1 = 14 + math.cos(a) * 11, by + math.sin(a) * 11
        spikes |= S_line(x0, y0, x1, y1, 0.5)
        c.set(int(x1 + math.cos(a + 1.4) * 1.2), int(y1 + math.sin(a + 1.4) * 1.2), DIRT[1])
    fill(c, spikes, DIRT[2])
    body = S_ellipse(14, by, 8, 7.5)
    paint(c, body, BURR, lw=(1,), sw=(1, 2))
    speckle(c, body, [BURR[3]], 0.12, "burr", only_on=[BURR[1]])
    for ex in (14, 19):
        c.rect(ex, by - 2, ex + 1, by, WHITE); c.set(ex + 1, by - 1, BLACK); c.set(ex + 1, by, BLACK)
    c.hline(13, 15, by - 3, BLACK); c.hline(19, 21, by - 3, BLACK); c.set(15, by - 2, BLACK); c.set(19, by - 2, BLACK)
    if jump:
        c.rect(16, by + 3, 18, by + 4, RED[3])
    else:
        c.hline(16, 19, by + 3, BURR[3])
    c.outline()
    return c


def stalactite(falling):
    c = Canvas(16, 32)
    cone = S_poly([(2, 0), (14, 0), (12, 10), (10, 20), (8, 31), (6, 20), (4, 10)])
    paint(c, cone, STONE, lw=(1,), sw=(1, 3))
    # rings / strata
    for y in (4, 9, 15, 21):
        for x in range(16):
            if (x, y) in cone and (x + y) % 3:
                c.set(x, y, STONE[3])
    for (x, y) in ((6, 3), (10, 7), (7, 13)):
        c.set(x, y, STONE[0])
    # mineral drip at the tip
    c.set(8, 29, WATERB[1]); c.set(8, 30, WATERB[0])
    if falling:
        # crack at the root + crumbs
        for (x, y) in ((5, 0), (6, 1), (7, 1), (8, 2), (9, 2), (10, 1), (11, 1)):
            c.set(x, y, OUT)
    c.outline()
    if falling:
        c.set(8, 31, WATERB[0])
        for (x, y) in ((0, 3), (15, 5), (1, 8)):
            c.set(x, y, STONE[2])
    return c


# ---------------------------------------------------------------- bosses
def rock_plates(cv, shape, ramp, seed, cell=9, rot=0.0, center=None, crack=None):
    """Split a shape into craggy Voronoi plates, each rim-shaded on its own (top-left light)."""
    xs = [p[0] for p in shape]
    ys = [p[1] for p in shape]
    if center is None:
        center = ((min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2)
    seeds = []
    for gy in range(min(ys) - cell, max(ys) + cell, cell):
        for gx in range(min(xs) - cell, max(xs) + cell, cell):
            sx = gx + hval(seed, gx, gy) * cell
            sy = gy + hval(seed, "y", gx, gy) * cell
            if rot:
                dx, dy = sx - center[0], sy - center[1]
                sx = center[0] + dx * math.cos(rot) - dy * math.sin(rot)
                sy = center[1] + dx * math.sin(rot) + dy * math.cos(rot)
            seeds.append((sx, sy))
    owner = {}
    for (x, y) in shape:
        best, bi = 1e9, 0
        for i, (sx, sy) in enumerate(seeds):
            d = (x + 0.5 - sx) ** 2 + (y + 0.5 - sy) ** 2
            if d < best:
                best, bi = d, i
        owner[(x, y)] = bi
    plates = {}
    for p, i in owner.items():
        plates.setdefault(i, set()).add(p)
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    for i, pl in plates.items():
        mx = sum(p[0] for p in pl) / len(pl)
        my = sum(p[1] for p in pl) / len(pl)
        t = 0.45 * (mx - x0) / max(1, x1 - x0) + 0.55 * (my - y0) / max(1, y1 - y0)
        if len(ramp) >= 5:
            sub = ramp[0:4] if t < 0.5 else ramp[1:5]
            if t > 0.8:
                sub = ramp[1:4] + [ramp[4]]
                sub = [ramp[2], ramp[2], ramp[3], ramp[4]]
        else:
            sub = ramp
        paint(cv, pl, sub, lw=(1,), sw=(1, 2))
        if hval(seed, "pit", i) < 0.35 and len(pl) > 12:
            # small pock marks
            for (x, y) in sorted(pl):
                if hval(seed, "pk", x, y) < 0.04:
                    cv.set(x, y, sub[-1])
    # dark crevices between plates
    for (x, y), i in owner.items():
        for dx, dy in ((1, 0), (0, 1)):
            q = (x + dx, y + dy)
            if q in owner and owner[q] != i:
                cv.set(x, y, crack or ramp[-1])
                break
    # global form shading: darken the lower-right part of the whole shape
    for (x, y) in shape:
        if (x + 3, y + 3) not in shape and (x + 1, y + 1) in shape and cv.get(x, y) != (crack or ramp[-1]):
            cv.set(x, y, ramp[-2])
    return owner


def crystal(cv, x, y, h, w, lean, ramp=AMBER):
    pts = [(x - w / 2, y), (x + lean - 0.5, y - h), (x + lean + 0.5, y - h), (x + w / 2, y)]
    sh = S_poly(pts)
    paint(cv, sh, ramp, lw=(1,), sw=(1,))
    for k in range(int(h * 0.7)):
        cv.set(int(x + lean * k / h - 0.5), y - 1 - k, ramp[0])
    return sh


def beetle_leg(cv, hip, knee, foot, ramp, r=1.8):
    leg = S_line(hip[0], hip[1], knee[0], knee[1], r) | S_line(knee[0], knee[1], foot[0], foot[1], r * 0.8)
    paint(cv, leg, ramp, lw=(1,), sw=(1,), edge=ramp[-1])
    # claw
    cv.set(int(foot[0]) + 1, int(foot[1]), ramp[-1]); cv.set(int(foot[0]) + 2, int(foot[1]), ramp[-1])


def grumble_head(c, hx_, hy_, mood="angry", flip=False):
    sg = -1 if flip else 1
    # big rock horn curving up and forward (drawn first, rooted in the forehead)
    pts = bezier((hx_ + 1, hy_ - sg * 4), (hx_ + 6, hy_ - sg * 20), (hx_ + 15, hy_ - sg * 24), 16)
    horn = set()
    for i, (x, y) in enumerate(pts):
        r = 5.2 * (1 - i / len(pts)) + 0.6
        horn |= S_ellipse(x, y, r, r)
    paint(c, horn, ROCK[:4], lw=(1,), sw=(1, 2))
    for i in range(3, len(pts) - 2, 3):
        x, y = pts[i]
        c.set(int(x) - 1, int(y), ROCK[4]); c.set(int(x), int(y) + sg, ROCK[4])
    head = S_ellipse(hx_, hy_, 13.5, 11.5)
    paint(c, head, BEETLE, lw=(1,), sw=(1, 3), edge=BEETLE[3])
    # rocky brow plate over the eyes
    brow = S_ellipse(hx_ + 2, hy_ - sg * 6, 10, 4.5)
    brow = {p for p in brow if p in head and ((p[1] <= hy_ - sg * 4) if not flip else (p[1] >= hy_ + 4))}
    paint(c, brow, ROCK[1:5], lw=(1,), sw=(1,))
    c.set(hx_ - 3, hy_ - sg * 7, ROCK[0]); c.set(hx_ + 4, hy_ - sg * 8, ROCK[0])
    # curved pincer mandibles
    sgn = -1 if flip else 1
    for k, (dy, curl) in enumerate(((3, -1), (8, 1))):
        pts = bezier((hx_ + 9, hy_ + sgn * dy), (hx_ + 17, hy_ + sgn * (dy + curl * 1)),
                     (hx_ + 16, hy_ + sgn * (dy + 4 * (1 if k == 0 else -1))), 10)
        m = set()
        for i, (x, y) in enumerate(pts):
            m |= S_ellipse(x, y, 2.0 * (1 - i / 12) + 0.6, 2.0 * (1 - i / 12) + 0.6)
        paint(c, m, BELLY, lw=(1,), sw=(1,), edge=BEETLE[3])
    ey = hy_ - 2 * sgn
    if mood == "angry":
        for ex in (hx_ + 1, hx_ + 8):
            e = S_rect(ex, ey - 1, ex + 3, ey + 2)
            fill(c, e, EMBER[1])
            c.rect(ex + 1, ey, ex + 2, ey + 1, EMBER[0])
            c.hline(ex, ex + 3, ey + 2, EMBER[2])
            # heavy slanted brow
            c.hline(ex - 1, ex + 4, ey - 2, OUT)
            c.set(ex + 4 if ex < hx_ + 4 else ex - 1, ey - 1, OUT)
            c.set(ex + 3 if ex < hx_ + 4 else ex, ey - 1, OUT)
        c.hline(hx_ + 2, hx_ + 11, ey + 6, BEETLE[3])
    elif mood == "dizzy":
        for ex in (hx_ + 3, hx_ + 9):
            for (dx, dy) in ((0, 0), (1, 0), (2, 0), (2, 1), (2, 2), (1, 2), (0, 2), (0, 1), (-1, 1), (-1, -1)):
                c.set(ex + dx - 1, ey + dy - 1, EMBER[1])
            c.set(ex, ey, OUT)
        mo = S_ellipse(hx_ + 6, hy_ + sgn * 5, 2.5, 1.5)
        fill(c, mo, RED[3])
    elif mood == "hurt":
        for ex in (hx_ + 2, hx_ + 8):
            for k in range(3):
                c.set(ex + k, ey - 1 + k, EMBER[1]); c.set(ex + k, ey + 3 - k, EMBER[1])
        mo = S_ellipse(hx_ + 6, hy_ + sgn * 6, 3.5, 2.5)
        fill(c, mo, RED[3])
        c.hline(hx_ + 4, hx_ + 8, hy_ + sgn * 6, WHITE)


def grumble_walk(step):
    c = Canvas(96, 72)
    bob = (0, -1, 0, 1)[step]
    # far legs (darker), tripod gait
    phase = [(0, 1, 0, -1)[step], (0, -1, 0, 1)[step]]
    far = [(28, -1), (48, 1), (68, -1)]
    for (x, ph) in far:
        o = phase[0 if ph > 0 else 1] * 4
        beetle_leg(c, (x + 4, 54 + bob), (x + 2 + o, 60), (x + 8 + o, 70 - (2 if o > 0 else 0)),
                   [BEETLE[2], BEETLE[3], OUT], 2.6)
    # beetle body under the shell
    body = S_ellipse(50, 53 + bob, 36, 9)
    paint(c, body, BEETLE, lw=(1,), sw=(1, 3))
    # rocky shell dome
    dome = S_ellipse(43, 47 + bob, 40, 33)
    dome = {p for p in dome if p[1] <= 53 + bob and p[0] >= 3}
    rock_plates(c, dome, ROCK, "shell", cell=14)
    # shell rim
    rim = {(x, y) for (x, y) in dome if (x, y + 1) not in dome}
    for (x, y) in rim:
        c.set(x, y, ROCK[4]); c.set(x, y - 1, ROCK[3])
    # moss cap with drips
    for (x, y) in sorted(dome):
        if (x, y - 1) not in dome:
            v = hval("moss", x)
            if 10 < x < 74 and v < 0.85:
                depth = 2 + int(hval("mossd", x) * 3) if v < 0.5 else 1
                for k in range(depth):
                    c.set(x, y + k, LEAF[0] if k == 0 else LEAF[1] if k == 1 else LEAF[2])
    # glowing amber crystals jutting from the shell
    cr = [(27, 21, 13, 7, -4), (37, 16, 18, 8, -1), (48, 17, 12, 6, 3), (16, 31, 9, 5, -4), (59, 23, 8, 5, 3)]
    for (x, y, h, w, lean) in cr:
        crystal(c, x, y + bob, h, w, lean)
    # head
    grumble_head(c, 79, 51 + bob)
    # near legs
    near = [(24, 1), (44, -1), (64, 1)]
    for (x, ph) in near:
        o = phase[0 if ph > 0 else 1] * 5
        lift = 3 if o < 0 else 0
        beetle_leg(c, (x, 57 + bob), (x - 4 + o, 63 - lift), (x - 1 + o, 71 - lift), BEETLE, 3.0)
    c.outline()
    for i, (x, y, h, w, lean) in enumerate(cr[:3]):
        sparkle(c, int(x + lean + (1 if i % 2 else -2)), int(y + bob - h - 2 + step % 2), WHITE, 1, AMBER[0])
    # dust puffs at the stepping feet
    if step in (1, 3):
        for (x, y) in ((16, 70), (60, 70)):
            for (dx, dy) in ((0, 0), (1, -1), (-1, -1), (2, 0)):
                if c.get(x + dx, y + dy)[3] == 0:
                    c.set(x + dx, y + dy, alpha(ROCK[0], 170))
    return c


def grumble_roll(phase):
    c = Canvas(96, 72)
    cx, cy, r = 48, 42, 29.5
    ball = S_ellipse(cx, cy, r, r)
    rock_plates(c, ball, ROCK, "ball", cell=12, rot=phase * 0.9, center=(cx, cy))
    # belly seam spiral visible
    for k in range(60):
        t = k / 59
        a = t * 2.4 * math.pi + phase * 0.9
        rr = r * (0.92 - 0.55 * t)
        c.set(int(cx + math.cos(a) * rr), int(cy + math.sin(a) * rr), BEETLE[3])
        c.set(int(cx + math.cos(a) * rr) + 1, int(cy + math.sin(a) * rr), BEETLE[2])
    for k in range(3):
        a = phase * 0.9 + k * 2.1
        crystal(c, int(cx + math.cos(a) * 18), int(cy + math.sin(a) * 18), 6, 4, 0)
    c.outline()
    # speed lines + dust behind (to the left)
    for k, y in enumerate((24, 34, 46, 58)):
        L = 8 + (k + phase) % 2 * 5
        for x in range(4, 4 + L):
            if c.get(x, y)[3] == 0:
                c.set(x, y, alpha(WHITE, 170 if x > 6 else 110))
    for (x, y, rr) in ((14 + phase * 3, 66, 3), (8, 68 - phase, 2.4), (22, 69, 2.2)):
        for p in S_ellipse(x, y, rr, rr * 0.8):
            if c.get(*p)[3] == 0:
                c.set(p[0], p[1], alpha(ROCK[0], 190))
    return c


def grumble_flipped(hurt=False):
    c = Canvas(96, 72)
    sq = 2 if hurt else 0
    # legs flailing upward (behind belly)
    for k, x in enumerate((26, 40, 54, 66)):
        wig = (-3 if k % 2 else 3) * (1 if not hurt else -1)
        beetle_leg(c, (x, 44 + sq), (x - 5 + wig, 33 + sq), (x - 1 + wig * 2, 24 + sq + (3 if hurt else 0)),
                   BEETLE, 2.2)
    # shell dome now on the ground
    dome = S_ellipse(46, 48 + sq, 40, 28 - sq)
    dome = {p for p in dome if p[1] >= 46 + sq and p[1] <= 71}
    rock_plates(c, dome, ROCK, "shellflip", cell=11)
    for (x, y) in dome:
        if (x, y - 1) not in dome:
            c.set(x, y, ROCK[3])
    # pale segmented belly
    belly = S_ellipse(48, 46 + sq, 33, 8 - sq * 0.5)
    paint(c, belly, BELLY, lw=(1,), sw=(1, 2))
    for x in range(22, 76, 7):
        for y in range(38 + sq, 54):
            if (x, y) in belly:
                c.set(x, y, BELLY[3])
    # head upside down at the right, dizzy
    grumble_head(c, 82, 50 + sq, "hurt" if hurt else "dizzy", flip=True)
    c.outline()
    # dizzy stars / impact burst
    if hurt:
        for (x, y) in ((40, 26), (52, 22), (60, 30), (34, 34)):
            sparkle(c, x, y, WHITE, 2, GOLD[1])
        for k in range(8):
            a = k / 8 * 2 * math.pi
            for d in range(9, 13):
                x, y = int(48 + math.cos(a) * d), int(32 + math.sin(a) * d * 0.7)
                if c.get(x, y)[3] == 0:
                    c.set(x, y, GOLD[0] if d < 11 else GOLD[1])
    else:
        for k, (x, y) in enumerate(((70, 26), (84, 22), (94, 30))):
            star = S_poly([(x, y - 3), (x + 1, y - 1), (x + 3, y), (x + 1, y + 1), (x, y + 3), (x - 1, y + 1),
                           (x - 3, y), (x - 1, y - 1)])
            fill(c, star, GOLD[1]); c.set(x, y, GOLD[0])
    return c


# ---- Duskmantle (128x112, origin 64,56 centre; symmetric front view)
SH_Y = 46  # shoulder y


def _rot(u, v, ang):
    return u * math.cos(ang) - v * math.sin(ang), u * math.sin(ang) + v * math.cos(ang)


FORE = [(0, -4), (8, -12), (24, -20), (42, -24), (52, -23), (56, -15), (52, -4), (38, 4), (20, 8), (0, 6)]
HIND = [(0, 2), (16, 4), (30, 10), (36, 20), (33, 32), (23, 40), (12, 38), (4, 26), (0, 14)]
FORE_SPOT = (36, -12)
HIND_SPOT = (22, 24)


def _pip(pt, poly):
    x, y = pt
    inside = False
    j = len(poly) - 1
    for i in range(len(poly)):
        xi, yi = poly[i]
        xj, yj = poly[j]
        if (yi > y) != (yj > y) and x < xi + (y - yi) * (xj - xi) / (yj - yi):
            inside = not inside
        j = i
    return inside


def _edge_dist(pt, poly):
    best = 1e9
    x, y = pt
    for i in range(len(poly)):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % len(poly)]
        dx, dy = x2 - x1, y2 - y1
        t = max(0, min(1, ((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy)))
        best = min(best, math.hypot(x - x1 - t * dx, y - y1 - t * dy))
    return best


def gloom_wings(c, ang_f, ang_h, scale_u=1.0, droop=0.0):
    """Paint both wings. ang: rotation (radians, + = raise tips) around shoulders."""
    for s in (-1, 1):
        shx = 64 + s * 7
        for which, poly, ang in (("h", HIND, ang_h), ("f", FORE, ang_f)):
            for y in range(112):
                for x in range(128):
                    du = (x + 0.5 - shx) * s
                    dv = y + 0.5 - SH_Y
                    # inverse rotate (tip raise = negative v for positive u)
                    u, v = _rot(du, dv, ang)
                    u /= scale_u
                    v -= droop * max(0, u) * 0.3
                    if u < -1 or not _pip((u, v), poly):
                        continue
                    d = _edge_dist((u, v), poly)
                    r = math.hypot(u, v)
                    if which == "f":
                        spot = math.hypot(u - FORE_SPOT[0], v - FORE_SPOT[1])
                        if spot < 3:
                            col = MAGENTA[1] if spot >= 1.4 else MAGENTA[0]
                        elif spot < 5.5:
                            col = LILAC[1]
                        elif spot < 7.5:
                            col = VIOLET[4]
                        elif d < 2.2:
                            col = LILAC[2] if int(u / 3) % 2 == 0 else LILAC[1]
                        elif d < 4.5:
                            col = VIOLET[3]
                        elif r < 14:
                            col = VIOLET[3]
                        elif r < 26:
                            col = VIOLET[2]
                        else:
                            col = VIOLET[1]
                        # dark crescent band across the wing
                        if d > 4.5 and spot > 7.5 and 21.5 <= r < 23 and v > -18:
                            col = VIOLET[4]
                        # moonlight sheen along the leading edge
                        if 2.2 <= d < 3.2 and v < -14:
                            col = VIOLET[0]
                    else:
                        spot = math.hypot(u - HIND_SPOT[0], v - HIND_SPOT[1])
                        if spot < 2.2:
                            col = GLOW[2]
                        elif spot < 4:
                            col = VIOLET[4]
                        elif d < 2.2:
                            col = MAGENTA[2] if int(v / 3) % 2 == 0 else MAGENTA[3]
                        elif r < 14:
                            col = VIOLET[4]
                        elif r < 28:
                            col = VIOLET[3]
                        else:
                            col = VIOLET[2]
                    c.set(x, y, col)


def gloom_body(c, dy=0, eyes="glare", mouth=False, head_dy=0):
    # abdomen
    ab = S_ellipse(64, 70 + dy, 9, 18)
    paint(c, ab, VIOLET, lw=(1,), sw=(2, 4), edge=VIOLET[4])
    for y in range(58 + dy, 88 + dy, 5):
        for x in range(54, 75):
            if (x, y) in ab:
                c.set(x, y, VIOLET[4] if (x, y + 1) in ab else VIOLET[3])
    for (x, y) in ((64, 86 + dy), (63, 87 + dy), (65, 87 + dy)):
        c.set(x, y, MAGENTA[1])
    # thorax
    th = S_ellipse(64, 52 + dy, 12, 11)
    paint(c, th, VIOLET, lw=(1,), sw=(2, 4), edge=VIOLET[4])
    # fur ruff
    ruff = fluff_collar(64, 44 + dy + head_dy, 18, 6, 9, "bruff")
    paint(c, ruff, LILAC, lw=(1,), sw=(1, 2))
    fur_strands(c, ruff, LILAC[2], "bruff")
    # head
    hy = 33 + dy + head_dy
    head = S_ellipse(64, hy, 11, 10)
    paint(c, head, VIOLET, lw=(1,), sw=(2, 4))
    for (x, y) in sorted(head):
        if hval("bfur", x, y) < 0.1 and c.get(x, y) == VIOLET[1]:
            c.set(x, y, VIOLET[0])
    # antennae (feathery)
    for s in (-1, 1):
        pts = bezier((64 + s * 4, hy - 8), (64 + s * 10, hy - 30), (64 + s * 30, hy - 30), 24)
        for i, (x, y) in enumerate(pts):
            c.set(int(round(x)), int(round(y)), VIOLET[3])
        for i in range(4, len(pts) - 1, 3):
            x, y = pts[i]
            nx, ny = pts[i + 1][0] - x, pts[i + 1][1] - y
            L = math.hypot(nx, ny) or 1
            # perpendicular feather barbs on the outer side, shrinking toward the tip
            px_, py_ = -ny / L * s, nx / L * s
            ln = 3.5 * (1 - i / len(pts)) + 1
            for k in range(1, int(ln) + 1):
                c.set(int(round(x - px_ * k)), int(round(y - py_ * k)), LILAC[3] if k > 1 else VIOLET[2])
    # crown gem
    gem = S_poly([(64, hy - 9), (67, hy - 6), (64, hy - 2), (61, hy - 6)])
    paint(c, gem, MAGENTA, lw=(1,), sw=(1,))
    # eyes
    for s in (-1, 1):
        if eyes == "glare":
            e = S_poly([(64 + s * 2, hy + 1), (64 + s * 9, hy - 3), (64 + s * 9, hy + 3), (64 + s * 4, hy + 4)])
            fill(c, e, GLOW[1])
            for (x, y) in e:
                if abs(x - (64 + s * 6)) <= 0 and hy - 1 <= y <= hy + 2:
                    c.set(x, y, WHITE)
            c.thick_line(64 + s * 1, hy - 1, 64 + s * 10, hy - 6, 0.6, VIOLET[4])
        elif eyes == "dizzy":
            ex = 64 + s * 6
            for k in range(14):
                a = k * 0.75
                rr = 0.5 + k * 0.22
                c.set(int(ex + math.cos(a) * rr), int(hy + 1 + math.sin(a) * rr), GLOW[1])
        elif eyes == "hurt":
            ex = 64 + s * 6
            c.thick_line(ex - 3 * s, hy - 2, ex + 2 * s, hy + 1, 0.5, GLOW[1])
            c.thick_line(ex + 2 * s, hy + 1, ex - 3 * s, hy + 3, 0.5, GLOW[1])
    if mouth:
        mo = S_ellipse(64, hy + 7, 3.5, 2.2)
        fill(c, mo, VIOLET[4])
        c.set(62, hy + 6, WHITE); c.set(66, hy + 6, WHITE)
    else:
        c.hline(61, 67, hy + 6, VIOLET[4]); c.set(62, hy + 7, WHITE); c.set(66, hy + 7, WHITE)
    # little legs tucked under thorax
    for s in (-1, 1):
        c.thick_line(64 + s * 6, 58 + dy, 64 + s * 10, 64 + dy, 0.6, VIOLET[4])
        c.thick_line(64 + s * 4, 60 + dy, 64 + s * 6, 67 + dy, 0.6, VIOLET[4])


def duskmantle(mode, k=0):
    c = Canvas(128, 112)
    if mode == "fly":
        ang = [0.45, 0.08, -0.36, 0.08][k]
        gloom_wings(c, ang, ang * 0.75 - 0.05)
        gloom_body(c, dy=[2, 0, -2, 0][k], eyes="glare")
    elif mode == "swoop":
        ang = 1.05 + k * 0.08
        gloom_wings(c, ang, ang * 0.9, scale_u=0.82)
        gloom_body(c, dy=4, eyes="glare", mouth=True, head_dy=2)
    elif mode == "dazed":
        gloom_wings(c, -0.75, -0.55, droop=0.5)
        gloom_body(c, dy=8, eyes="dizzy", head_dy=3)
    elif mode == "hurt":
        gloom_wings(c, 0.35, 0.2, scale_u=0.94)
        gloom_body(c, dy=1, eyes="hurt", mouth=True)
    c.outline()
    if mode == "hurt":
        # hit flash: lift every opaque pixel toward pale lilac
        for y in range(c.h):
            for x in range(c.w):
                p = c.px[y][x]
                if p[3] == 255 and p != OUT:
                    c.px[y][x] = mix(p, LILAC[0], 0.32)
    if mode == "dazed":
        for i in range(4):
            a = i / 4 * 2 * math.pi + 0.4
            x, y = int(64 + math.cos(a) * 18), int(28 + math.sin(a) * 5)
            star = S_poly([(x, y - 3), (x + 1, y - 1), (x + 3, y), (x + 1, y + 1), (x, y + 3), (x - 1, y + 1),
                           (x - 3, y), (x - 1, y - 1)])
            for p in star:
                if c.get(*p)[3] == 0 or p[1] < 30:
                    c.set(p[0], p[1], GOLD[1])
            c.set(x, y, GOLD[0])
    if mode == "hurt":
        for (x, y) in ((40, 20), (88, 24), (52, 12), (78, 10), (64, 4)):
            sparkle(c, x, y, WHITE, 2, MAGENTA[0])
    # magic dust sparkles drifting off the wings
    if mode in ("fly", "swoop"):
        for i in range(6):
            x = int(10 + hval("dust", k, i, mode) * 108)
            y = int(20 + hval("dusty", k, i, mode) * 80)
            if c.get(x, y)[3] == 0:
                c.set(x, y, LILAC[1] if i % 2 else MAGENTA[0])
    return c


# ---------------------------------------------------------------- projectiles
def spore_shot(phase):
    c = Canvas(12, 12)
    ball = S_ellipse(6, 6, 4.6, 4.6)
    paint(c, ball, RED, lw=(1,), sw=(1, 2))
    spots = [(4, 4), (7, 7), (8, 3)] if phase == 0 else [(7, 4), (4, 7), (3, 4)]
    for (x, y) in spots:
        c.set(x, y, CREAM[0]); c.set(x + 1, y, CREAM[1]) if (x + 1, y) in ball else None
    c.set(4, 3, WHITE)
    c.outline()
    # spore sparkle trail pixels
    for (x, y) in (((0, 10), (11, 1)) if phase == 0 else ((0, 1), (11, 10))):
        c.set(x, y, alpha(GOLD[0], 200))
    return c


def gloom_orb(phase):
    c = Canvas(16, 16)
    r = 5.2 if phase == 0 else 5.8
    orb = S_ellipse(8, 8, r, r)
    for (x, y) in orb:
        d = math.hypot(x + 0.5 - 8, y + 0.5 - 8) / r
        col = VIOLET[4] if d < 0.45 else VIOLET[3] if d < 0.7 else MAGENTA[2] if d < 0.9 else MAGENTA[1]
        c.set(x, y, col)
    c.set(6, 6, MAGENTA[0]); c.set(7, 5, LILAC[1])
    c.set(8, 8, MAGENTA[1]); c.set(9, 9, MAGENTA[2])
    c.outline(hx("1a0c2a"))
    glow(c, 8, 8, 8 if phase == 0 else 8.6, MAGENTA[1], ((1.0, 60), (0.82, 120)))
    # crackling wisps
    wisps = [(1, 7), (14, 9), (8, 1)] if phase == 0 else [(2, 3), (13, 13), (8, 14)]
    for (x, y) in wisps:
        c.set(x, y, LILAC[1])
    return c


def pebble():
    c = Canvas(12, 12)
    p = S_poly([(2, 5), (4, 2), (8, 2), (10, 5), (10, 8), (7, 10), (3, 10), (1, 8)])
    paint(c, p, ROCK[:4], lw=(1,), sw=(1, 2))
    c.set(6, 6, ROCK[3]); c.set(7, 5, ROCK[3]); c.set(4, 4, ROCK[0])
    c.outline()
    return c


# ---------------------------------------------------------------- items (bob in place)
def glide_seed_item(bob):
    c = Canvas(24, 24)
    p = Canvas(24, 24)
    cy = 12 + bob
    dandelion2(p, 12, cy + 1, 0, rx=9.5, ry=8.0)
    # stem with seed
    for y in range(cy + 2, cy + 9):
        p.set(12, y, GREEN[2])
    p.rect(11, cy + 8, 13, cy + 10, DIRT[1]); p.set(11, cy + 8, DIRT[0])
    blit(c, p, 0, 0)
    for (x, y) in ((3, 20 - bob), (21, 18 + bob)):
        sparkle(c, x, y, WHITE, 1, alpha(LILAC[1], 200))
    return c


def spore_puff_item(bob):
    c = Canvas(24, 24)
    by = bob
    # chubby red puff mushroom with spore cloud
    stem = S_rect(9, 14 + by, 14, 20 + by)
    paint(c, stem, CREAM, lw=(1,), sw=(1,))
    c.rect(10, 16 + by, 10, 17 + by, BLACK); c.rect(13, 16 + by, 13, 17 + by, BLACK)
    cap = S_ellipse(12, 14 + by, 9, 8)
    cap = {p for p in cap if p[1] <= 14 + by}
    paint(c, cap, RED, lw=(1,), sw=(1, 2))
    c.hline(3, 20, 14 + by, RED[2])
    for (x, y, w) in ((7, 9, 2), (12, 7, 3), (17, 10, 2)):
        c.rect(x, y + by, x + w - 1, y + by, CREAM[0])
    c.outline()
    for (x, y) in (((5, 4), (9, 2), (15, 3), (19, 5), (12, 0)) if bob == 0 else ((6, 3), (10, 1), (16, 2), (18, 6), (13, 1))):
        c.set(x, y, GOLD[0])
        if c.get(x + 1, y)[3] == 0:
            c.set(x + 1, y, alpha(GOLD[1], 170))
    return c


def bark_shield_item(bob):
    c = Canvas(24, 24)
    sh = S_ellipse(12, 12 + bob, 9.5, 10)
    sh |= S_poly([(4, 12 + bob), (20, 12 + bob), (12, 23 + bob)])
    sh = {p for p in sh if p[1] <= 22}
    paint(c, sh, WOOD, lw=(1,), sw=(1, 2))
    # growth rings
    for r in (3, 6):
        for k in range(40):
            a = k / 40 * 2 * math.pi
            x, y = int(12 + math.cos(a) * r), int(12 + bob + math.sin(a) * r * 1.05)
            if (x, y) in sh:
                c.set(x, y, WOOD[2])
    # leaf emblem in the middle
    lf = S_poly([(12, 7 + bob), (15, 11 + bob), (12, 17 + bob), (9, 11 + bob)])
    paint(c, lf, LEAF, lw=(1,), sw=(1,))
    for y in range(9 + bob, 16 + bob):
        c.set(12, y, LEAF[3])
    # rim band
    for (x, y) in sh:
        if any((x + dx, y + dy) not in sh for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
            c.set(x, y, WOOD[3] if (x + y) % 2 else WOOD[2])
    c.outline()
    sparkle(c, 5, 4 + bob, WHITE, 1, alpha(GOLD[1], 200))
    return c


def firefly_item(phase):
    c = Canvas(24, 24)
    by = 1 if phase else 0
    # glowing round abdomen (lower left)
    cx, cy = 10, 14 + by
    tail = S_ellipse(cx, cy, 4.6, 4.2)
    for (x, y) in tail:
        d = math.hypot(x + 0.5 - cx + 1, y + 0.5 - cy + 1)
        c.set(x, y, WHITE if d < 1.6 else GLOW[1] if d < 3.2 else GLOW[2])
    # dark thorax + head (upper right) with big cute eye and antennae
    th = S_ellipse(14.5, 10 + by, 3.2, 2.8)
    paint(c, th, [hx("7a6a8a"), hx("4a3a5a"), hx("2e2440")], lw=(1,), sw=(1,))
    hd = S_ellipse(18, 7.5 + by, 2.6, 2.4)
    paint(c, hd, [hx("7a6a8a"), hx("4a3a5a"), hx("2e2440")], lw=(1,), sw=(1,))
    c.rect(18, 7 + by, 19, 8 + by, WHITE); c.set(19, 8 + by, BLACK)
    c.set(19, 4 + by, OUT); c.set(20, 3 + by, OUT); c.set(17, 4 + by, OUT); c.set(17, 3 + by, OUT)
    c.set(21, 3 + by, GLOW[1]); c.set(16, 2 + by, GLOW[1])
    # wings flutter (translucent)
    wy = 5 if phase == 0 else 8
    wing = S_ellipse(12, wy + by, 3.6, 2.0) | S_ellipse(9, wy + 2 + by, 2.8, 1.6)
    for (x, y) in wing:
        if c.get(x, y)[3] == 0:
            c.set(x, y, alpha(WING[0], 210))
    c.outline()
    glow(c, cx, cy, 9.5 if phase == 0 else 10.5, GLOW[2], ((1.0, 45), (0.75, 90), (0.6, 140)))
    for (x, y) in (((3, 4), (21, 19)) if phase == 0 else ((2, 18), (22, 13))):
        sparkle(c, x, y, WHITE, 2 if (x + phase) % 2 else 1, GLOW[1])
    return c


def heart_berry(bob):
    c = Canvas(16, 16)
    rows = ["..##..##..", ".########.", "##########", "##########", ".########.", "..######..", "...####...",
            "....##...."]
    oy = 5 + bob
    for y, row in enumerate(rows):
        for x, ch in enumerate(row):
            if ch == "#":
                col = RED[1] if not (y >= 4 or x >= 7 and y >= 2) else RED[2]
                c.set(3 + x, oy + y, col)
    # berry seeds / druplets
    for (x, y) in ((5, 2), (8, 3), (10, 2), (6, 4), (9, 5)):
        c.set(3 + x, oy + y, RED[0] if (x + y) % 2 else hx("ff9a8a"))
    c.set(4, oy + 1, WHITE); c.set(5, oy, WHITE)
    # leaf + stem
    c.set(8, oy - 1, GREEN[2]); c.set(8, oy - 2, GREEN[2])
    lf = S_ellipse(10.5, oy - 2.5, 2.5, 1.3)
    paint(c, lf, GREEN[:3], lw=(1,), sw=(1,))
    c.outline()
    if bob:
        sparkle(c, 14, 3, WHITE, 1, alpha(PINK, 200))
    else:
        sparkle(c, 1, 4, WHITE, 1, alpha(PINK, 200))
    return c


def glowspore_orb(k):
    """The story orb: warm white-gold core with soft pulsing rings (4 frames)."""
    c = Canvas(32, 32)
    pulse = [0, 1, 2, 1][k]
    cx = cy = 16
    # soft outer rings (stepped alpha, no AA)
    for y in range(32):
        for x in range(32):
            d = math.hypot(x + 0.5 - cx, y + 0.5 - cy)
            ring_r = 11.5 + pulse * 1.3
            if abs(d - ring_r) < 0.75:
                c.set(x, y, alpha(GLOW[1], 150))
            elif abs(d - (ring_r + 3)) < 0.6 and (x + y + k) % 2 == 0:
                c.set(x, y, alpha(GLOW[2], 110))
            elif d < 10 + pulse * 0.5:
                c.set(x, y, alpha(GLOW[3], 70 if d > 8.5 else 0))
    core = S_ellipse(cx, cy, 7.5, 7.5)
    for (x, y) in core:
        d = math.hypot(x + 0.5 - cx + 1.2, y + 0.5 - cy + 1.2) / 7.5
        col = WHITE if d < 0.35 else GLOW[1] if d < 0.65 else GLOW[2] if d < 0.95 else GLOW[3]
        c.set(x, y, col)
    # spore texture swirl inside
    for i in range(6):
        a = i / 6 * 2 * math.pi + k * 0.5
        x, y = int(cx + math.cos(a) * 4.5), int(cy + math.sin(a) * 4.5)
        c.set(x, y, GLOW[0])
    for (x, y) in core:
        if any((x + dx, y + dy) not in core for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
            c.set(x, y, AMBER[2])
    c.rect(12, 11, 13, 12, WHITE)
    # orbiting motes
    for i in range(3):
        a = i / 3 * 2 * math.pi + k * math.pi / 6
        sparkle(c, int(cx + math.cos(a) * 13), int(cy + math.sin(a) * 13), WHITE, 1, alpha(GLOW[1], 200))
    return c


# ---------------------------------------------------------------- platforms / props
def moving_leaf():
    c = Canvas(64, 16)
    leaf = S_poly([(3, 4), (12, 4), (56, 4), (62, 6), (58, 10), (44, 13), (20, 13), (8, 11), (2, 7)])
    paint(c, leaf, LEAF, lw=(1,), sw=(1, 2))
    # walkable top surface at y=4 : bright rim
    for x in range(3, 60):
        if (x, 4) in leaf:
            c.set(x, 4, LEAF[0])
    # midrib + veins
    for x in range(6, 58):
        y = 8 if x < 50 else 8 - (x - 50) // 4
        c.set(x, y, LEAF[3] if x % 9 else LEAF[2])
    for x0 in range(12, 54, 8):
        for k in range(4):
            c.set(x0 + k, 8 - k - 1, LEAF[2]); c.set(x0 + k, 8 + k + 1, LEAF[3])
    # curled stem at the left end
    for (x, y) in ((1, 8), (0, 9), (0, 10), (1, 11), (2, 11)):
        c.set(x, y, LEAF[3])
    # dew drop
    c.set(40, 5, WATERB[0]); c.set(41, 5, WATERB[1])
    c.outline()
    return c


def crumble_block(cracking):
    c = Canvas(32, 32)
    blk = S_rect(0, 0, 31, 31)
    owner = rock_plates(c, blk, [hx("e4cf9e"), hx("c8a874"), hx("a0804e"), hx("765a34"), hx("4e3a22")],
                        "crumble", cell=11, crack=hx("4e3a22"))
    for x in range(32):
        c.set(x, 0, hx("f4e2b4")); c.set(x, 31, hx("4e3a22"))
    for y in range(32):
        c.set(0, y, hx("e4cf9e")); c.set(31, y, hx("4e3a22"))
    if cracking:
        # big fresh cracks + loosened chips
        for pts in (((4, 0), (9, 8), (7, 15), (12, 22), (10, 31)), ((31, 6), (22, 12), (18, 20), (24, 31)),
                    ((9, 8), (16, 10), (22, 12))):
            for i in range(len(pts) - 1):
                (x0, y0), (x1, y1) = pts[i], pts[i + 1]
                n = max(abs(x1 - x0), abs(y1 - y0))
                for t in range(n + 1):
                    c.set(round(x0 + (x1 - x0) * t / n), round(y0 + (y1 - y0) * t / n), OUT)
        for (x, y) in ((14, 14), (15, 14), (26, 4), (5, 25)):
            c.set(x, y, CLEAR)
        for (x, y) in ((13, 3), (25, 26), (6, 19)):
            c.set(x, y, hx("f4e2b4"))
    return c


def bubble_sprite():
    c = Canvas(8, 8)
    ring = S_ellipse(4, 4, 3.6, 3.6)
    inner = S_ellipse(4, 4, 2.6, 2.6)
    for p in inner:
        c.set(p[0], p[1], alpha(WATERB[1], 70))
    for p in ring - inner:
        c.set(p[0], p[1], alpha(WATERB[2], 230))
    c.set(2, 2, WHITE); c.set(3, 2, WHITE); c.set(2, 3, alpha(WHITE, 200))
    c.set(5, 5, alpha(WHITE, 160))
    return c


TINY = {
    "E": ["111", "100", "110", "100", "111"],
    "X": ["101", "101", "010", "101", "101"],
    "I": ["111", "010", "010", "010", "111"],
    "T": ["111", "010", "010", "010", "010"],
    "S": ["111", "100", "111", "001", "111"],
    "H": ["101", "101", "111", "101", "101"],
    "O": ["111", "101", "101", "101", "111"],
    "P": ["111", "101", "111", "100", "100"],
}


def tiny_text(c, text, x, y, col, shadow=None):
    for i, ch in enumerate(text):
        g = TINY[ch]
        for gy, row in enumerate(g):
            for gx, bit in enumerate(row):
                if bit == "1":
                    if shadow:
                        c.set(x + i * 4 + gx + 1, y + gy + 1, shadow)
                    c.set(x + i * 4 + gx, y + gy, col)


def exit_sign():
    c = Canvas(32, 32)
    post = S_rect(14, 14, 17, 31)
    paint(c, post, WOOD, lw=(1,), sw=(1,))
    c.hline(11, 20, 31, GREEN[2]); c.hline(12, 19, 30, GREEN[1]); c.set(13, 29, GREEN[0]); c.set(18, 29, GREEN[1])
    board = S_poly([(2, 4), (24, 4), (30, 10), (24, 16), (2, 16)])
    paint(c, board, WOOD, lw=(1,), sw=(1, 2))
    for y in (8, 12):
        for x in range(3, 26):
            if (x, y) in board and x % 7:
                c.set(x, y, WOOD[2])
    c.outline()
    tiny_text(c, "EXIT", 5, 8, CREAM[0], WOOD[3])
    # nails
    c.set(3, 6, GREY[1]); c.set(3, 14, GREY[1])
    # little lantern-mushroom on top
    c.rect(14, 1, 17, 3, RED[1]); c.hline(14, 17, 3, RED[2]); c.set(15, 1, CREAM[0])
    return c


def shop_stall():
    c = Canvas(96, 80)
    # back wall with shelves
    back = S_rect(10, 22, 85, 62)
    paint(c, back, [hx("a8784a"), hx("84583a"), hx("6a442c"), hx("4e3020")], lw=(0,), sw=(1, 3))
    for x in range(10, 86, 6):
        for y in range(22, 63):
            c.set(x, y, hx("6a442c"))
    for sy in (34, 48):
        c.hline(12, 83, sy, WOOD[1]); c.hline(12, 83, sy + 1, WOOD[3])
    # jars + potions + goods on shelves
    goods = [(16, 34, "jar", GREEN), (24, 34, "pot", RED), (32, 34, "jar", GOLD), (62, 34, "pot", BLUE),
             (70, 34, "jar", RED), (78, 34, "pot", GREEN), (36, 48, "sack", DIRT), (56, 48, "sack", DIRT),
             (46, 48, "jar", BLUE)]
    for (x, y, kind, ramp) in goods:
        if kind == "jar":
            j = S_rect(x - 2, y - 6, x + 2, y - 1)
            paint(c, j, [ramp[0], ramp[1], ramp[2]], lw=(1,), sw=(1,))
            c.hline(x - 2, x + 2, y - 7, CREAM[2]); c.set(x - 1, y - 5, WHITE)
        elif kind == "pot":
            p = S_ellipse(x, y - 3, 2.6, 2.6) | S_rect(x - 1, y - 8, x, y - 5)
            paint(c, p, [ramp[0], ramp[1], ramp[2]], lw=(1,), sw=(1,))
            c.set(x - 1, y - 8, DIRT[1]); c.set(x, y - 8, DIRT[1])
        else:
            s = S_ellipse(x, y - 4, 4, 4)
            paint(c, s, [CREAM[1], CREAM[2], DIRT[1]], lw=(1,), sw=(1,))
            c.set(x, y - 8, DIRT[2])
    # posts
    for px in (6, 87):
        post = S_rect(px, 14, px + 3, 79)
        paint(c, post, WOOD, lw=(1,), sw=(1,))
    # counter
    counter = S_rect(4, 58, 91, 79)
    paint(c, counter, WOOD, lw=(1,), sw=(1, 3))
    c.hline(2, 93, 57, WOOD[0]); c.hline(2, 93, 58, WOOD[1]); c.hline(2, 93, 59, WOOD[3])
    for x in range(4, 92, 11):
        for y in range(60, 80):
            c.set(x, y, WOOD[3])
    # front display: heart berry, glide seed, coin pile on the counter top
    hb = heart_berry(0)
    blit(c, hb, 14, 45)
    gs = glide_seed_item(0)
    blit(c, gs, 66, 35)
    for i, (x, y) in enumerate(((40, 55), (44, 55), (42, 53), (48, 55), (46, 53), (44, 51))):
        c.rect(x, y, x + 2, y + 1, GOLD[1]); c.set(x, y, GOLD[0]); c.set(x + 2, y + 1, GOLD[2])
    # striped awning (mushroom red & cream) with scalloped edge
    aw = S_poly([(0, 14), (8, 2), (88, 2), (96, 14)])
    for (x, y) in aw:
        stripe = ((x - (14 - y) * 0.6) // 8) % 2
        c.set(x, y, RED[1] if stripe else CREAM[1])
        if y <= 3:
            c.set(x, y, RED[0] if stripe else CREAM[0])
    for x in range(0, 96):
        k = x % 8
        depth = [2, 3, 4, 4, 4, 3, 2, 1][k]
        stripe = (x // 8) % 2
        for y in range(15, 15 + depth):
            c.set(x, y, RED[2] if stripe else CREAM[2])
    # hanging coin sign
    sign = S_ellipse(48, 26, 7, 6)
    paint(c, sign, WOOD, lw=(1,), sw=(1,))
    c.set(45, 18, GREY[1]); c.set(51, 18, GREY[1]); c.set(46, 19, GREY[1]); c.set(50, 19, GREY[1])
    coin = S_ellipse(48, 26, 3.6, 3.6)
    paint(c, coin, GOLD, lw=(1,), sw=(1,))
    c.rect(47, 24, 48, 28, GOLD[2])
    c.outline()
    return c


# ---------------------------------------------------------------- HUD icons (16x16)
def icon_glide():
    c = Canvas(16, 16)
    dandelion2(c, 8, 9, 0, rx=6.5, ry=6.0)
    for y in range(10, 15):
        c.set(8, y, GREEN[2])
    c.set(8, 15, DIRT[2])
    return c


def icon_spore():
    c = Canvas(16, 16)
    ball = S_ellipse(8, 9, 5.5, 5.5)
    paint(c, ball, RED, lw=(1,), sw=(1, 2))
    for (x, y) in ((6, 6), (9, 9), (10, 6), (5, 10)):
        c.set(x, y, CREAM[0])
    c.set(5, 5, WHITE)
    c.outline()
    c.set(13, 2, GOLD[0]); c.set(2, 3, GOLD[0]); c.set(14, 4, alpha(GOLD[1], 200))
    return c


def icon_shield():
    c = Canvas(16, 16)
    sh = S_ellipse(8, 7, 6, 6) | S_poly([(2.5, 8), (13.5, 8), (8, 15)])
    paint(c, sh, WOOD, lw=(1,), sw=(1, 2))
    for (x, y) in sh:
        if any((x + dx, y + dy) not in sh for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
            c.set(x, y, WOOD[2])
    lf = S_ellipse(8, 8, 1.8, 2.6)
    paint(c, lf, LEAF, lw=(1,), sw=(1,))
    c.outline()
    return c


def icon_star():
    c = Canvas(16, 16)
    star = S_poly([(8, 1), (10, 6), (15, 6), (11, 9.5), (12.5, 15), (8, 12), (3.5, 15), (5, 9.5), (1, 6), (6, 6)])
    for (x, y) in star:
        d = math.hypot(x + 0.5 - 8, y + 0.5 - 8.5)
        c.set(x, y, GLOW[0] if d < 2.2 else GLOW[1] if d < 4.2 else GLOW[2])
    c.set(7, 7, BLACK); c.set(9, 7, BLACK)
    c.outline()
    return c


def icon_glowspore():
    c = Canvas(16, 16)
    core = S_ellipse(8, 8, 5, 5)
    for (x, y) in core:
        d = math.hypot(x + 0.5 - 7, y + 0.5 - 7) / 5
        c.set(x, y, WHITE if d < 0.35 else GLOW[1] if d < 0.75 else GLOW[2])
    for (x, y) in core:
        if any((x + dx, y + dy) not in core for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
            c.set(x, y, AMBER[2])
    c.outline()
    for (x, y) in ((1, 1), (14, 2), (2, 14), (14, 13)):
        c.set(x, y, alpha(GLOW[1], 220))
    return c


def icon_heart_empty():
    c = Canvas(16, 16)
    rows = ["..##...##..", ".####.####.", "###########", "###########", "###########", ".#########.",
            "..#######..", "...#####...", "....###....", ".....#....."]
    ox, oy = 2, 3
    shape = set()
    for y, row in enumerate(rows):
        for x, ch in enumerate(row):
            if ch == "#":
                shape.add((ox + x, oy + y))
    for (x, y) in shape:
        edge = any((x + dx, y + dy) not in shape for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))
        c.set(x, y, hx("8a6a7a") if edge else hx("4a3646"))
    c.set(ox + 2, oy + 1, hx("a88a98"))
    c.outline()
    return c


# ---------------------------------------------------------------- resource writing
def write_meta(name):
    path = os.path.join(SPRITES, f"{name}.image.json.meta")
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            meta = json.load(f)
        if meta.get("kind") == "Image" and meta.get("displayName") == name:
            return meta["guid"]
    guid = uuid.UUID(hashlib.md5(name.encode("utf-8")).hexdigest()).hex
    meta = {"guid": guid, "kind": "Image", "displayName": name,
            "createdUtc": "2026-10-11T00:00:00Z", "modifiedUtc": "2026-10-11T00:00:00Z"}
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(meta, f, indent=2)
        f.write("\n")
    return guid


WRITTEN = []

# Contract amendment 1: images get a " Sprite" suffix so they never share a name with their objects
# (the Player rejects duplicate resource names across kinds). Portraits, HUD icons, Pip Map,
# Explorer poses and Bark Shield Ring keep their names.
SUFFIXED = {
    "Map Node", "Elder Morel", "Chanterelle", "Bram", "Puffball", "Old Wick", "Flit Bat", "Glow Grub", "Bloatfin",
    "Driftjelly", "Dusk Moth", "Burr Hopper", "Stalactite", "Grumblecrag", "Duskmantle", "Spore Shot", "Gloom Orb",
    "Pebble", "Glide Seed", "Spore Puff", "Bark Shield", "Starlit Firefly", "Heart Berry", "Glowspore",
    "Moving Leaf", "Crumble Block", "Bubble", "Exit Sign", "Shop Stall",
}


def write_image(name, canvases, origin, mask, tags, durations, usage="sprite"):
    """tags: list of (tagName, startIndex, endIndex, loop)."""
    if name in SUFFIXED:
        ca.migrate_image(name, name + " Sprite")
        name = name + " Sprite"
    w, h = canvases[0].w, canvases[0].h
    for cv in canvases:
        assert (cv.w, cv.h) == (w, h), name
    frames_dir = os.path.join(SPRITES, f"{name}.spritedata", "frames")
    os.makedirs(frames_dir, exist_ok=True)
    keep = set()
    frames, cels = [], []
    for i, cv in enumerate(canvases):
        fid = stable_id("frame", name, str(i))
        rel = f"{name}.spritedata/frames/{fid}.png"
        cv.image().save(os.path.join(frames_dir, f"{fid}.png"), optimize=False, compress_level=9)
        keep.add(f"{fid}.png")
        dur = durations[i] if isinstance(durations, list) else durations
        frames.append({"id": fid, "name": f"{name} {i + 1:02d}", "durationMilliseconds": dur, "source": rel,
                       "sourceRectangle": {"x": 0, "y": 0, "width": w, "height": h}})
        cels.append({"frameId": fid, "source": rel, "offset": {"x": 0, "y": 0}, "opacity": 1})
    for fn in os.listdir(frames_dir):
        if fn not in keep:
            os.remove(os.path.join(frames_dir, fn))
    doc = {
        "schemaVersion": 2,
        "canvas": {"width": w, "height": h, "pixelFormat": "rgba32", "colorSpace": "srgb"},
        "origin": {"x": origin[0], "y": origin[1], "space": "pixels"},
        "usage": {"allowed": usage,
                  "rendering": {"filter": "nearest", "wrap": "clamp", "generateMipmaps": False},
                  "tileset": {"tileWidth": 32, "tileHeight": 32, "margin": 0, "spacing": 0, "columns": 8,
                              "collision": []}},
        "frames": frames,
        "tags": [{"id": stable_id("tag", name, t[0]), "name": t[0], "startFrameId": frames[t[1]]["id"],
                  "endFrameId": frames[t[2]]["id"], "direction": "forward", "loop": t[3]} for t in tags],
        "layers": [{"id": stable_id("layer", name), "name": "Editable pixels", "kind": "raster", "visible": True,
                    "locked": False, "opacity": 1, "blendMode": "normal", "cels": cels}],
        "collisionShapes": [{"id": stable_id("mask", name), "name": "Gameplay mask", "kind": "rectangle",
                             "position": {"x": mask[0], "y": mask[1]}, "size": {"x": mask[2], "y": mask[3]},
                             "isTrigger": False}],
        "import": {},
    }
    with open(os.path.join(SPRITES, f"{name}.image.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, indent=2)
        f.write("\n")
    write_meta(name)
    WRITTEN.append((name, w, h, origin, mask, [(t[0], t[2] - t[1] + 1) for t in tags], len(canvases)))
    return canvases


# ---------------------------------------------------------------- contact sheets
def contact_sheet(rows, path, bg=(143, 211, 255, 255), scale=4, pad=4, mask_rows=None):
    row_imgs = []
    for row in rows:
        rw = sum(cv.w for cv in row) + pad * (len(row) + 1)
        rh = max(cv.h for cv in row) + pad * 2
        row_imgs.append((row, rw, rh))
    sheet_w = max(r[1] for r in row_imgs)
    sheet_h = sum(r[2] for r in row_imgs)
    sheet = Image.new("RGBA", (sheet_w, sheet_h), bg)
    y = 0
    for row, rw, rh in row_imgs:
        x = pad
        for cv in row:
            sheet.alpha_composite(cv.image(), (x, y + pad + (rh - pad * 2 - cv.h)))
            x += cv.w + pad
        y += rh
    sheet = sheet.resize((sheet_w * scale, sheet_h * scale), Image.NEAREST)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    sheet.save(path)
    print("wrote", path)


# ---------------------------------------------------------------- Scared frames (round 4, Fear Kit)
def scared_wipe(cv, x0, y0, x1, y1, src_x, default):
    """Repaint a face box with the body colour found just left of it (keeps the shading rows)."""
    for y in range(y0, y1 + 1):
        col = cv.get(src_x, y)
        if col[3] == 0 or col == OUT:
            col = default
        for x in range(x0, x1 + 1):
            cv.set(x, y, col)


def scared_eye(cv, x0, y0, w, h, px, py):
    """Huge round eye: outer box (with rounded corners) 1px outline, white inside, one tiny pupil."""
    for y in range(y0, y0 + h):
        for x in range(x0, x0 + w):
            corner = x in (x0, x0 + w - 1) and y in (y0, y0 + h - 1)
            if corner:
                continue
            edge = x in (x0, x0 + w - 1) or y in (y0, y0 + h - 1)
            cv.set(x, y, OUT if edge else WHITE)
    cv.set(px, py, BLACK)


def scared_drop(cv, x, y):
    """Blue sweat drop, 5x6."""
    rows = ["..O..", ".OBO.", "OBBCO", "OBCCO", "OCCCO", ".OOO."]
    leg = {"O": OUT, "B": BLUE[0], "C": BLUE[1]}
    for dy, row in enumerate(rows):
        for dx, ch in enumerate(row):
            if ch != ".":
                cv.set(x + dx, y + dy, leg[ch])
    cv.set(x + 1, y + 2, WHITE)


def scared_squash(src, row):
    """Hunch: drop one pixel row at `row`; everything above shifts down 1 (feet stay planted)."""
    c = Canvas(src.w, src.h)
    for y in range(src.h):
        if y < row:
            c.px[y + 1] = list(src.px[y])
        elif y > row:
            c.px[y] = list(src.px[y])
    return c


def scared_shift(src, dx):
    c = Canvas(src.w, src.h)
    for y in range(src.h):
        for x in range(src.w):
            if src.px[y][x][3]:
                if not 0 <= x + dx < src.w:
                    return src  # would clip: keep the unshifted frame
                c.px[y][x + dx] = src.px[y][x]
    return c


def scared_set(name, k):
    """Two frames (k = 0, 1) of the 'Scared' face for one sprite family."""
    j = k  # pupil jitter / shake
    if name == "acorn":
        c = scared_squash(ca.draw_acorn(0), 13)
        scared_wipe(c, 13, 15, 26, 24, 11, hx("c98a4a"))
        scared_eye(c, 13, 15, 6, 7, 15 + j, 18)
        scared_eye(c, 20, 15, 6, 7, 22 + j, 18)
        for i, x in enumerate(range(17, 22)):
            c.set(x, 23 + (i % 2), hx("4a2e1c"))
        scared_drop(c, 25, 1 + k)
        return scared_shift(c, 1) if k else c
    if name == "bee":
        c = ca.draw_bee(k)
        scared_wipe(c, 20, 8, 27, 15, 19, hx("3a2a3e"))
        scared_eye(c, 21, 8, 6, 7, 23 + j, 12)
        c.set(25, 15, BLACK); c.set(26, 16, BLACK)
        scared_drop(c, 26, 15 + k)
        return c
    if name == "bat":
        c = flit_bat(k)
        scared_wipe(c, 11, 9, 21, 17, 10, BAT[1])
        scared_eye(c, 11, 9, 5, 6, 13 + j, 12)
        scared_eye(c, 17, 9, 5, 6, 19 + j, 12)
        for i, x in enumerate(range(14, 19)):
            c.set(x, 16 + (i % 2), BAT[3])
        scared_drop(c, 25, 14 + k)
        return c
    if name == "grub":
        c = glow_grub(k)
        scared_wipe(c, 23, 8, 30, 16, 22, hx("f0b878"))
        scared_eye(c, 25, 8, 6, 7, 27 + j, 12)
        c.set(28, 15, GRUB[3]); c.set(29, 16, GRUB[3])
        scared_drop(c, 19, 0 + k)
        return c
    if name == "moth":
        c = dusk_moth(k)
        scared_eye(c, 12, 4, 5, 6, 14 + j, 7)
        scared_eye(c, 16, 4, 5, 6, 18 + j, 7)
        scared_drop(c, 25, 12 + k)
        return c
    if name == "hopper":
        c = scared_squash(burr_hopper(False), 9)
        scared_wipe(c, 8, 11, 20, 21, 7, BURR[1])
        scared_eye(c, 8, 11, 6, 7, 10 + j, 14)
        scared_eye(c, 15, 11, 6, 7, 17 + j, 14)
        for i, x in enumerate(range(11, 17)):
            c.set(x, 19 + (i % 2), BURR[3])
        scared_drop(c, 22, 2 + k)
        return c
    if name == "fish":
        c = bloatfin(0, k)
        scared_wipe(c, 20, 8, 25, 17, 19, FISH[1])
        scared_eye(c, 20, 8, 6, 7, 22 + j, 12)
        c.set(24, 16, OUT); c.set(25, 17, OUT); c.set(24, 18, OUT)
        scared_drop(c, 3, 3 + k)
        return c
    raise KeyError(name)


def write_scared_variants():
    """Re-writes seven creature images with their original frames/tags/ids plus a trailing 'Scared' tag."""
    rows = []
    sc = lambda n: [scared_set(n, 0), scared_set(n, 1)]  # noqa: E731
    a = [ca.draw_acorn(i) for i in range(4)] + sc("acorn")
    ca.write_image("Acorn Walker Sprite", a, (16, 28), (3, 6, 26, 22), [("Walk", 0, 3, True), ("Scared", 4, 5, True)],
                   [140, 140, 140, 140, 120, 120])
    b = [ca.draw_bee(0), ca.draw_bee(1)] + sc("bee")
    ca.write_image("Buzz Bee Sprite", b, (16, 12), (6, 4, 20, 16), [("Fly", 0, 1, True), ("Scared", 2, 3, True)],
                   [60, 60, 120, 120])
    ba = [flit_bat(0), flit_bat(1), flit_bat(2)] + sc("bat")
    write_image("Flit Bat", ba, (16, 12), (6, 4, 20, 16),
                [("Fly", 0, 1, True), ("Hang", 2, 2, True), ("Scared", 3, 4, True)], [110, 110, 1000, 120, 120])
    g = [glow_grub(0), glow_grub(1)] + sc("grub")
    write_image("Glow Grub", g, (16, 20), (3, 6, 26, 14), [("Crawl", 0, 1, True), ("Scared", 2, 3, True)],
                [220, 220, 120, 120])
    m = [dusk_moth(0), dusk_moth(1)] + sc("moth")
    write_image("Dusk Moth", m, (16, 12), (6, 4, 20, 16), [("Fly", 0, 1, True), ("Scared", 2, 3, True)],
                [90, 90, 120, 120])
    h = [burr_hopper(False), burr_hopper(True)] + sc("hopper")
    write_image("Burr Hopper", h, (14, 28), (4, 6, 20, 22),
                [("Idle", 0, 0, True), ("Jump", 1, 1, True), ("Scared", 2, 3, True)], [1000, 1000, 120, 120])
    f = [bloatfin(0, 0), bloatfin(0, 1), bloatfin(1, 0), bloatfin(1, 1)] + sc("fish")
    write_image("Bloatfin", f, (16, 14), (6, 6, 20, 16),
                [("Swim", 0, 1, True), ("Puffed", 2, 3, True), ("Scared", 4, 5, True)],
                [200, 200, 140, 140, 120, 120])
    rows = [x[-2:] for x in (a, b, ba, g, m, h, f)]
    contact_sheet([sum(rows, [])], os.path.join(PREVIEW_DIR, "art4_scared.png"), bg=(70, 80, 110, 255), scale=6)


# ---------------------------------------------------------------- main
def main():
    only = set(sys.argv[1:])
    E_MASK = (9, 6, 14, 34)
    sheets = {}

    # ---- hero extras, shield, map
    swim = write_image("Explorer Swim", hero_swim(), (16, 40), E_MASK, [("Swim", 0, 3, True)], 130)
    throw = write_image("Explorer Throw", hero_throw(), (16, 40), E_MASK, [("Throw", 0, 1, False)], [90, 180])
    glide = write_image("Explorer Glide", hero_glide(), (16, 40), E_MASK, [("Glide", 0, 1, True)], 220)
    vict = write_image("Explorer Victory", hero_victory(), (16, 40), E_MASK, [("Victory", 0, 1, True)], [360, 260])
    talk = write_image("Explorer Talk", hero_talk(), (16, 40), E_MASK, [("Talk", 0, 0, True)], 1000)
    ring = write_image("Bark Shield Ring", [shield_ring(0), shield_ring(1)], (22, 44), (0, 0, 44, 48),
                       [("Spin", 0, 1, True)], 120)
    pmap = write_image("Pip Map", [pip_map(i) for i in range(4)], (8, 20), (2, 2, 12, 18), [("Walk", 0, 3, True)], 150)
    node = write_image("Map Node", [map_node(i) for i in range(3)], (12, 8), (0, 0, 24, 16),
                       [("Locked", 0, 0, False), ("Open", 1, 1, False), ("Cleared", 2, 2, False)], 1000)
    # ring preview over the idle hero
    idle = ca.explorer_frames()[0][0]
    combo = []
    for r in ring:
        cc = Canvas(44, 48)
        blit(cc, idle, 6, 4)
        blit(cc, r, 0, 0)
        combo.append(cc)
    sheets["1"] = [swim + throw + glide + vict + talk, ring + combo + pmap + node]
    contact_sheet(sheets["1"], os.path.join(PREVIEW_DIR, "creatures_preview1.png"))

    # ---- NPCs: Idle (2f) + Talk (1f)
    npc_specs = [
        ("Elder Morel", [morel_npc(0), morel_npc(1), morel_npc(0, True)], (16, 48), (8, 8, 16, 40)),
        ("Chanterelle", [chanterelle_npc(0), chanterelle_npc(1), chanterelle_npc(0, True)], (18, 40), (5, 8, 26, 32)),
        ("Bram", [bram_npc(0), bram_npc(1), bram_npc(0, True)], (20, 32), (4, 6, 34, 26)),
        ("Puffball", [puffball_npc(0), puffball_npc(1), puffball_npc(0, True)], (12, 28), (2, 6, 20, 22)),
        ("Old Wick", [wick_npc(0, False, 0), wick_npc(1, False, 1), wick_npc(0, True, 0)], (14, 44), (6, 2, 16, 42)),
    ]
    npc_row = []
    for name, frames, org, msk in npc_specs:
        write_image(name, frames, org, msk, [("Idle", 0, 1, True), ("Talk", 2, 2, True)], [700, 700, 1000])
        npc_row += frames
    sheets["2"] = [npc_row]
    contact_sheet(sheets["2"], os.path.join(PREVIEW_DIR, "creatures_preview2.png"), bg=(120, 150, 110, 255), scale=5)
    # ---- portraits
    portraits = []
    for name, fn in (("Portrait Pip", portrait_pip), ("Portrait Elder Morel", portrait_morel),
                     ("Portrait Chanterelle", portrait_chanterelle), ("Portrait Bram", portrait_bram),
                     ("Portrait Puffball", portrait_puffball), ("Portrait Old Wick", portrait_wick),
                     ("Portrait Duskmantle", portrait_duskmantle)):
        portraits += write_image(name, [fn()], (0, 0), (0, 0, 64, 64), [("Still", 0, 0, True)], 1000)
    sheets["2"].append(portraits)
    contact_sheet(sheets["2"], os.path.join(PREVIEW_DIR, "creatures_preview2.png"), bg=(120, 150, 110, 255), scale=5)

    # ---- enemies
    bat = write_image("Flit Bat", [flit_bat(0), flit_bat(1), flit_bat(2)], (16, 12), (6, 4, 20, 16),
                      [("Fly", 0, 1, True), ("Hang", 2, 2, True)], [110, 110, 1000])
    grub = write_image("Glow Grub", [glow_grub(0), glow_grub(1)], (16, 20), (3, 6, 26, 14),
                       [("Crawl", 0, 1, True)], 220)
    puf = write_image("Bloatfin", [bloatfin(0, 0), bloatfin(0, 1), bloatfin(1, 0), bloatfin(1, 1)], (16, 14), (6, 6, 20, 16),
                      [("Swim", 0, 1, True), ("Puffed", 2, 3, True)], [200, 200, 140, 140])
    jel = write_image("Driftjelly", [driftjelly(0), driftjelly(1)], (12, 16), (4, 4, 16, 20),
                      [("Drift", 0, 1, True)], [420, 300])
    moth = write_image("Dusk Moth", [dusk_moth(0), dusk_moth(1)], (16, 12), (6, 4, 20, 16), [("Fly", 0, 1, True)], 90)
    burr = write_image("Burr Hopper", [burr_hopper(False), burr_hopper(True)], (14, 28), (4, 6, 20, 22),
                       [("Idle", 0, 0, True), ("Jump", 1, 1, True)], [1000, 1000])
    stal = write_image("Stalactite", [stalactite(False), stalactite(True)], (8, 0), (3, 4, 10, 26),
                       [("Hang", 0, 0, True), ("Fall", 1, 1, True)], [1000, 1000])
    enemies = bat + grub + puf + jel + moth + burr + stal
    contact_sheet([enemies[:9], enemies[9:]], os.path.join(PREVIEW_DIR, "creatures_preview3.png"),
                  bg=(70, 80, 110, 255), scale=6)

    # ---- bosses
    gc = [grumble_walk(i) for i in range(4)] + [grumble_roll(0), grumble_roll(1)] + \
         [grumble_flipped(False), grumble_flipped(True)]
    write_image("Grumblecrag", gc, (48, 72), (10, 16, 76, 56),
                [("Walk", 0, 3, True), ("Roll", 4, 5, True), ("Flipped", 6, 6, True), ("Hurt", 7, 7, False)],
                [160, 160, 160, 160, 70, 70, 1000, 300])
    gw = [duskmantle("fly", i) for i in range(4)] + [duskmantle("swoop", 0), duskmantle("swoop", 1)] + \
         [duskmantle("dazed"), duskmantle("hurt")]
    write_image("Duskmantle", gw, (64, 56), (28, 28, 72, 60),
                [("Fly", 0, 3, True), ("Swoop", 4, 5, True), ("Dazed", 6, 6, True), ("Hurt", 7, 7, False)],
                [120, 100, 120, 100, 80, 80, 1000, 300])
    contact_sheet([gc[:4], gc[4:]], os.path.join(PREVIEW_DIR, "creatures_preview4.png"), bg=(60, 50, 70, 255),
                  scale=4)
    contact_sheet([gw[:4], gw[4:]], os.path.join(PREVIEW_DIR, "creatures_preview5.png"), bg=(40, 50, 90, 255),
                  scale=3)
    contact_sheet([[gw[0], gc[0]]], os.path.join(PREVIEW_DIR, "creatures_zoom.png"), bg=(40, 50, 90, 255), scale=6)

    # ---- projectiles, items, props, icons
    row_a = []
    row_a += write_image("Spore Shot", [spore_shot(0), spore_shot(1)], (6, 6), (1, 1, 10, 10), [("Spin", 0, 1, True)], 80)
    row_a += write_image("Gloom Orb", [gloom_orb(0), gloom_orb(1)], (8, 8), (2, 2, 12, 12), [("Pulse", 0, 1, True)], 140)
    row_a += write_image("Pebble", [pebble()], (6, 6), (1, 1, 10, 10), [("Still", 0, 0, True)], 1000)
    row_a += write_image("Glide Seed", [glide_seed_item(0), glide_seed_item(1)], (12, 12), (2, 2, 20, 20),
                         [("Idle", 0, 1, True)], 320)
    row_a += write_image("Spore Puff", [spore_puff_item(0), spore_puff_item(1)], (12, 12), (2, 2, 20, 20),
                         [("Idle", 0, 1, True)], 320)
    row_a += write_image("Bark Shield", [bark_shield_item(0), bark_shield_item(1)], (12, 12), (2, 2, 20, 20),
                         [("Idle", 0, 1, True)], 320)
    row_a += write_image("Starlit Firefly", [firefly_item(0), firefly_item(1)], (12, 12), (2, 2, 20, 20),
                         [("Idle", 0, 1, True)], 160)
    row_a += write_image("Heart Berry", [heart_berry(0), heart_berry(1)], (8, 8), (1, 1, 14, 14),
                         [("Idle", 0, 1, True)], 320)
    row_a += write_image("Glowspore", [glowspore_orb(i) for i in range(4)], (16, 16), (4, 4, 24, 24),
                         [("Glow", 0, 3, True)], 150)
    row_b = []
    row_b += write_image("Moving Leaf", [moving_leaf()], (0, 0), (2, 4, 60, 8), [("Still", 0, 0, True)], 1000)
    row_b += write_image("Crumble Block", [crumble_block(False), crumble_block(True)], (0, 0), (0, 0, 32, 32),
                         [("Solid", 0, 0, True), ("Cracking", 1, 1, True)], [1000, 1000])
    row_b += write_image("Bubble", [bubble_sprite()], (4, 4), (1, 1, 6, 6), [("Still", 0, 0, True)], 1000)
    row_b += write_image("Exit Sign", [exit_sign()], (16, 32), (2, 4, 28, 28), [("Still", 0, 0, True)], 1000)
    row_b += write_image("Shop Stall", [shop_stall()], (48, 80), (0, 2, 96, 78), [("Still", 0, 0, True)], 1000)
    for name, fn in (("Icon Glide", icon_glide), ("Icon Spore", icon_spore), ("Icon Shield", icon_shield),
                     ("Icon Star", icon_star), ("Icon Glowspore", icon_glowspore),
                     ("Icon Heart Empty", icon_heart_empty)):
        row_b += write_image(name, [fn()], (0, 0), (0, 0, 16, 16), [("Still", 0, 0, True)], 1000)
    contact_sheet([row_a, row_b], os.path.join(PREVIEW_DIR, "creatures_preview6.png"), bg=(110, 160, 200, 255),
                  scale=5)
    write_scared_variants()


if __name__ == "__main__":
    main()
