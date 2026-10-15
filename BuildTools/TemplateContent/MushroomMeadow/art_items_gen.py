"""Mushroom Meadow round 4 item / gore / gamepad-glyph sprite generator (ART).

Item pickups, held-item art, arrows, thrown spins, blood stain / gore chunk / slice flash sprites,
the `Pad Glyphs` sheet and an optional `Key Cap`.

Deterministic: re-running produces byte-identical PNGs and JSON.
Run from anywhere:  python BuildTools/TemplateContent/MushroomMeadow/art_items_gen.py
Preview only (writes nothing into the template): add --preview
Contact sheets:     TestResults/MushroomMeadow/art4_*.png

Reuses the char_art palette / Canvas and the creature_art shape toolkit.
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
from char_art import Canvas, hx, OUT, CLEAR, RED, CREAM, GREEN, DIRT, GOLD, BLUE, WHITE, GREY, BLACK  # noqa: E402
from creature_art import (S_ellipse, S_rect, S_poly, S_line, S_curve, bezier, fill, paint, blit,  # noqa: E402
                          sparkle, WOOD, VIOLET, AMBER, GLOWG, hval)

REPO = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
SPRITES = os.path.join(REPO, "Source", "Genesis.Application.Core", "Projects", "Templates", "Assets",
                       "TwoDShowcase", "Assets", "Sprites")
PREVIEW_DIR = os.path.join(REPO, "TestResults", "MushroomMeadow")

STEEL = [hx("f6f8ff"), hx("cdd6ea"), hx("93a0c4"), hx("5e6a94")]
PETAL = [hx("fff6a8"), hx("ffd23f"), hx("f0a020"), hx("b0661a")]
SEEDS = [hx("a8743c"), hx("7a4e2a"), hx("4a2e1c")]
BARK = [hx("c48a52"), hx("9b6a3c"), hx("6d4526"), hx("4a2e1c")]
TEAL = [hx("c8f4ea"), hx("5ad6c4"), hx("2a9a9a"), hx("1a6a70")]


def stable_id(*parts):
    return hashlib.md5("|".join(("mm-items",) + parts).encode("utf-8")).hexdigest()


def rot90(src, k=1):
    """Rotate a canvas clockwise by k quarter turns (lossless)."""
    cur = src
    for _ in range(k % 4):
        n = Canvas(cur.h, cur.w)
        for y in range(cur.h):
            for x in range(cur.w):
                n.px[x][cur.h - 1 - y] = cur.px[y][x]
        cur = n
    return cur


def centered(src, w, h):
    c = Canvas(w, h)
    blit(c, src, (w - src.w) // 2, (h - src.h) // 2)
    return c


def grid(rows, legend):
    h, w = len(rows), max(len(r) for r in rows)
    c = Canvas(w, h)
    for y, row in enumerate(rows):
        for x, ch in enumerate(row):
            if ch != ".":
                c.set(x, y, legend[ch])
    return c


# ---------------------------------------------------------------- the sword (stout shortsword, mushroom-cap guard)
def sword_pickup(glint=False):
    c = Canvas(16, 16)
    # blade (up-right)
    blade = S_curve([(8.0, 8.0), (13.0, 3.0)], 1.35, 0.35)
    blade |= {(14, 2), (14, 1)}
    paint(c, blade, STEEL[:3], lw=(1,), sw=(1,))
    for p in ((9, 7), (10, 6), (11, 5)):          # fuller groove
        c.set(*p, STEEL[2])
    # mushroom-cap guard across the blade foot: red dome bulging toward the hilt, cream spots
    cap = S_curve([(4.0, 6.0), (6.5, 8.5), (9.0, 11.0)], 1.5, 1.5) | S_curve([(5.0, 8.6), (6.5, 10.0)], 1.5, 1.5)
    cap = {p for p in cap if 3 <= p[0] <= 10 and 5 <= p[1] <= 12}
    paint(c, cap, RED[:3], lw=(1,), sw=(1,))
    c.set(5, 7, CREAM[0]); c.set(8, 10, CREAM[0]); c.set(6, 9, CREAM[1])
    # grip + knob
    grip = S_line(5.5, 10.5, 3.5, 12.5, 0.7)
    paint(c, grip, WOOD[:3], lw=(1,), sw=(1,))
    c.set(5, 11, WOOD[3]); c.set(4, 12, WOOD[3])
    knob = S_ellipse(2.5, 13.5, 1.6, 1.6)
    paint(c, knob, WOOD[:3], lw=(1,), sw=(1,))
    c.outline()
    if glint:
        sparkle(c, 13, 1, WHITE, 1, STEEL[1])
    return c


def sword_held(frame):
    """32x16, origin (4,8), blade to the right. frame 0..3 = idle, swing smear grows, peaks, fades."""
    c = Canvas(32, 16)
    # smear first (behind): a pale arc hugging the blade
    if frame >= 1:
        W, CY = WHITE, hx("bfeaff")
        rows = {1: [(5, 16, 29, W), (4, 22, 29, CY)],
                2: [(5, 13, 30, W), (4, 17, 30, W), (3, 23, 30, CY), (10, 15, 30, W), (11, 20, 30, W), (12, 25, 30, CY)],
                3: [(4, 22, 30, CY), (11, 22, 30, CY)]}[frame]
        for (y, x0, x1, col) in rows:
            for x in range(x0, x1 + 1):
                if frame == 3 and x % 2:
                    continue
                c.set(x, y, col)
    # blade: base 4 high tapering to a point
    for x in range(12, 31):
        t = (x - 12) / 18.0
        half = 2.0 - 1.6 * t * t - (0.3 if t > 0.8 else 0)
        top = int(round(7.5 - half))
        bot = int(round(8.5 + half)) - 1
        if x == 30:
            top = bot = 8
        for y in range(top, bot + 1):
            col = STEEL[0] if y == top else (STEEL[2] if y == bot else STEEL[1])
            if (y == top + 1 and bot - top >= 3) or x == 30:
                col = STEEL[1]
            c.set(x, y, col)
        if bot - top >= 1:
            pass
    for x in range(14, 27):                      # fuller line
        c.set(x, 8, STEEL[2] if x % 4 else STEEL[1])
    if frame == 0:
        sparkle(c, 25, 7, WHITE, 1, STEEL[0])
    # mushroom-cap guard: red dome, bulging toward the hand, cream spots
    cap = S_ellipse(11.8, 8, 3.7, 5.6) & S_rect(7, 2, 11, 14)
    paint(c, cap, RED[:3], lw=(1,), sw=(1,))
    for (x, y) in ((9, 4), (10, 8), (9, 11)):
        c.set(x, y, CREAM[0])
    c.set(10, 5, CREAM[1]); c.set(9, 12, CREAM[1])
    # grip with leather-wrap marks + knob
    for x in range(4, 8):
        for y in range(7, 10):
            c.set(x, y, WOOD[0] if y == 7 else WOOD[1] if y == 8 else WOOD[2])
    c.set(5, 7, WOOD[2]); c.set(5, 8, WOOD[3]); c.set(6, 9, WOOD[3])
    kn = S_ellipse(2.5, 8.5, 1.9, 2.1)
    paint(c, kn, WOOD[:3], lw=(1,), sw=(1,))
    c.outline()
    return c


# ---------------------------------------------------------------- shield (round bark, red spotted cap boss)
def shield_face(cv, cx, cy, rx, ry, boss=True, detail=True):
    body = S_ellipse(cx, cy, rx, ry)
    paint(cv, body, BARK, lw=(1,), sw=(1, 2))
    if detail and rx >= 4:
        for k in (-0.5, 0.0, 0.5):                      # plank seams
            x = int(round(cx + k * rx * 1.1 - 0.5))
            for y in range(int(cy - ry), int(cy + ry) + 1):
                if (x, y) in body and ((x, y - 1) in body) and ((x, y + 1) in body):
                    cv.set(x, y, BARK[3] if (y + int(k * 4)) % 5 else BARK[2])
    if detail:
        for (x, y) in body:                              # rim band
            if any((x + dx, y + dy) not in body for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
                cv.set(x, y, BARK[3] if (x + y) % 2 else BARK[2])
    if boss and rx >= 4:
        bs = max(2.0, min(rx, ry) * 0.5)
        cap = S_ellipse(cx, cy, bs, bs * 0.95)
        paint(cv, cap, RED[:3], lw=(1,), sw=(1,), edge=RED[3])
        cv.set(int(cx - bs * 0.45), int(cy - bs * 0.45), CREAM[0])
        cv.set(int(cx + bs * 0.35), int(cy), CREAM[1])
        cv.set(int(cx - bs * 0.1), int(cy + bs * 0.5), CREAM[1])
    return body


def shield_pickup(glint=False):
    c = Canvas(16, 16)
    shield_face(c, 8, 8.5, 6.4, 6.8)
    # moss tuft
    c.set(4, 3, GREEN[1]); c.set(5, 2, GREEN[0]); c.set(6, 2, GREEN[1])
    c.outline()
    if glint:
        sparkle(c, 12, 3, WHITE, 1, CREAM[1])
    return c


def shield_held(raised):
    c = Canvas(24, 28)
    shield_face(c, 12, 14, 10.3, 12.3)
    c.set(5, 5, GREEN[1]); c.set(6, 4, GREEN[0]); c.set(7, 4, GREEN[1]); c.set(8, 3, GREEN[1]); c.set(4, 6, GREEN[2])
    c.outline()
    if raised:
        # ready-to-block sheen: bright rim arc and a sparkle
        for (x, y) in ((4, 9), (4, 10), (5, 7), (5, 8), (6, 6), (7, 5), (8, 4), (9, 3)):
            c.set(x, y, CREAM[0])
        c.set(3, 11, CREAM[1])
        sparkle(c, 7, 8, WHITE, 1, CREAM[0])
    return c


def shield_thrown(frame):
    """frame 0 face, 1 three-quarter, 2 edge-on, 3 three-quarter (flipped glint)."""
    c = Canvas(20, 20)
    rxs = [8.6, 5.4, 1.6, 5.4]
    rx = rxs[frame]
    if rx < 3:
        body = S_rect(8, 2, 11, 17)
        paint(c, body, BARK, lw=(1,), sw=(1,))
        for y in range(3, 17, 3):
            c.set(9, y, BARK[3])
        c.rect(9, 8, 10, 11, RED[1]); c.set(9, 8, RED[0])
        c.outline()
        return c
    shield_face(c, 10, 10, rx, 8.6, boss=(frame != 3))
    if frame == 3:
        # back of the shield: bark grain + two straps
        c.rect(int(10 - rx * 0.6), 7, int(10 + rx * 0.6), 8, BARK[3])
        c.rect(int(10 - rx * 0.6), 12, int(10 + rx * 0.6), 13, BARK[3])
    c.outline()
    return c


# ---------------------------------------------------------------- bow (bent sapling, vine string)
def bow_curve(cx_top, cx_bot, y0, y1, belly, steps=26):
    ym = (y0 + y1) / 2.0
    return bezier((cx_top, y0), (belly, ym), (cx_bot, y1), steps)


def draw_bow(c, y0, y1, tip_x, belly, nock_x, wrap_h=3, leaf=True, wood=WOOD):
    pts = bow_curve(tip_x, tip_x, y0, y1, belly)
    limb = S_curve(pts, 1.5, 0.6, steps=6)
    # taper: thick at the grip, thin at the tips
    limb2 = set()
    n = len(pts)
    for i, (px, py) in enumerate(pts):
        t = abs(i - (n - 1) / 2.0) / ((n - 1) / 2.0)
        r = 1.7 - 1.1 * t
        limb2 |= S_ellipse(px, py, r, r)
    limb = limb2
    # string (vine) first, so the limb draws over it
    ym = (y0 + y1) / 2.0
    for (ax, ay, bx, by) in ((tip_x, y0, nock_x, ym), (nock_x, ym, tip_x, y1)):
        steps = int(max(abs(bx - ax), abs(by - ay)) * 2) + 1
        for i in range(steps + 1):
            t = i / steps
            c.set(int(round(ax + (bx - ax) * t)), int(round(ay + (by - ay) * t)), GREEN[1])
    paint(c, limb, wood[:3], lw=(1,), sw=(1,))
    # grip wrap
    gy = int(round(ym))
    gx = int(round(belly * 0.5 + tip_x * 0.5 + 0.5))
    for y in range(gy - wrap_h // 2 - 1, gy + wrap_h // 2 + 1):
        for x in range(gx - 1, gx + 3):
            if (x, y) in limb:
                c.set(x, y, RED[1] if (y + x) % 2 else RED[2])
    if leaf:
        for (x, y, col) in ((tip_x + 1, y0, GREEN[0]), (tip_x + 2, y0 - 1, GREEN[1]), (tip_x + 1, y1, GREEN[0]),
                            (tip_x + 2, y1 + 1, GREEN[1])):
            c.set(x, y, col)
    return limb


def bow_pickup(glint=False):
    c = Canvas(16, 16)
    draw_bow(c, 1, 14, 4, 16.0, 4, wrap_h=3)
    c.outline()
    if glint:
        sparkle(c, 12, 4, WHITE, 1, CREAM[1])
    return c


def bow_held(pull):
    c = Canvas(20, 28)
    tip_x = 6 + int(round(pull * 0.5))
    nock_x = 6 - pull
    draw_bow(c, 2, 25, tip_x, 17.0 - pull * 0.5, nock_x, wrap_h=5)
    c.outline()
    return c


def bow_thrown(frame):
    base = Canvas(16, 16)
    draw_bow(base, 1, 14, 5, 13.5, 5, wrap_h=3)
    base.outline()
    return rot90(centered(base, 20, 20), frame)


# ---------------------------------------------------------------- propeller flower
def bloom(cv, cx, cy, R, nb, phase, squash=1.0, disc=0.32, bw=0.55):
    """Sunflower-like bloom of seed-blade petals, drawn back to front; blades have a pinwheel twist."""
    blades = []
    for i in range(nb):
        a = phase + i * 2 * math.pi / nb
        blades.append((math.sin(a), a))
    blades.sort(key=lambda b: b[0])
    for _, a in blades:
        dx, dy = math.cos(a), math.sin(a) * squash
        px, py = -math.sin(a), math.cos(a) * squash          # perpendicular (twist)
        w = R * bw * 0.5
        pts = [(cx + px * w * 0.6, cy + py * w * 0.6),
               (cx + dx * R * 0.55 + px * w * 1.05, cy + dy * R * 0.55 + py * w * 1.05),
               (cx + dx * R - px * w * 0.25, cy + dy * R - py * w * 0.25),
               (cx + dx * R * 0.5 - px * w * 0.7, cy + dy * R * 0.5 - py * w * 0.7),
               (cx - px * w * 0.5, cy - py * w * 0.5)]
        shape = S_poly(pts)
        paint(cv, shape, PETAL[:3], lw=(1,), sw=(1,), edge=PETAL[3])
        # seed stripe along the blade
        mx, my = int(round(cx + dx * R * 0.55)), int(round(cy + dy * R * 0.55))
        if (mx, my) in shape:
            cv.set(mx, my, PETAL[2])
    d = S_ellipse(cx, cy, max(1.5, R * disc), max(1.1, R * disc * squash))
    paint(cv, d, SEEDS, lw=(1,), sw=(1,), edge=SEEDS[2])
    if R * disc >= 2.4:
        cv.set(int(cx), int(cy), PETAL[2])
        cv.set(int(cx - 1), int(cy - 1), PETAL[2]) if squash > 0.6 else None


def flower_pickup(phase, glint=False):
    c = Canvas(16, 16)
    # stem + leaves
    for y in range(9, 16):
        c.set(8, y, GREEN[2]); c.set(9, y, GREEN[1])
    lf = S_poly([(9, 13), (13, 10), (14, 12), (10, 14)])
    paint(c, lf, GREEN[:3], lw=(1,), sw=(1,))
    lf2 = S_poly([(8, 14), (4, 11), (3, 13), (7, 15)])
    paint(c, lf2, GREEN[:3], lw=(1,), sw=(1,))
    bloom(c, 8, 6, 6.6, 6, 0.2 + phase * 0.26)
    c.outline()
    if glint:
        sparkle(c, 14, 2, WHITE, 1, PETAL[0])
    return c


def flower_held_closed():
    c = Canvas(24, 24)
    for y in range(13, 24):
        c.set(11, y, GREEN[2]); c.set(12, y, GREEN[1])
    lf = S_poly([(12, 19), (17, 16), (19, 18), (13, 21)])
    paint(c, lf, GREEN[:3], lw=(1,), sw=(1,))
    # folded bud: tulip-like seed-blades pressed together
    bud = S_ellipse(12, 8, 4.6, 7.0)
    bud |= S_poly([(8, 8), (16, 8), (15, 13), (9, 13)])
    paint(c, bud, PETAL[:3], lw=(1,), sw=(1, 2), edge=PETAL[3])
    for y in range(3, 14):
        if (11, y) in bud:
            c.set(11, y, PETAL[3] if y % 3 else PETAL[2])
    for (x, y) in ((9, 6), (14, 6), (10, 3)):
        c.set(x, y, PETAL[0])
    c.outline()
    return c


def flower_held_spin(k):
    """24x24 origin (12,20): spinning blades above a short stem; frames differ by twist and blur."""
    c = Canvas(24, 24)
    for y in range(11, 24):
        c.set(11, y, GREEN[2]); c.set(12, y, GREEN[1])
    lf = S_poly([(12, 19), (17, 16), (19, 18), (13, 21)])
    paint(c, lf, GREEN[:3], lw=(1,), sw=(1,))
    bloom(c, 12, 8, 11.2, 6, 0.1 + k * 0.26, squash=0.36, disc=0.2, bw=0.58)
    c.outline()
    # motion blur ticks
    for (x, y) in ((2, 3 + (k % 2)), (21, 12 - (k % 2)), (6, 13), (18, 3)):
        if c.get(x, y)[3] == 0:
            c.set(x, y, WHITE)
    return c


def flower_thrown(frame):
    c = Canvas(16, 16)
    squash = [1.0, 0.6, 0.3, 0.6][frame]
    # short dangling stem tumbling around the bloom
    sx = [(8, 14), (11, 13), (8, 11), (5, 13)][frame]
    for t in range(0, 4):
        c.set(int(8 + (sx[0] - 8) * t / 3.0), int(8 + (sx[1] - 8) * t / 3.0), GREEN[1])
    bloom(c, 8, 8, 6.6, 6, 0.3 + frame * 0.5, squash=squash)
    c.outline()
    return c


# ---------------------------------------------------------------- arrow
def arrow_art():
    legend = {"O": OUT, "g": GREEN[0], "G": GREEN[1], "h": GREEN[2], "w": WOOD[1], "x": WOOD[2],
              "s": STEEL[0], "t": STEEL[1], "u": STEEL[2]}
    rows = [
        "OOOO.........O..",
        "OgGOOOOOOOOOOtO.",
        "hGOwwwwwwwwxstuO",
        "OgGOOOOOOOOOOtO.",
        "OOOO.........O..",
    ]
    c = grid(rows, legend)
    # fletching feather bars, arrow head spade
    c.set(2, 1, GREEN[0]); c.set(1, 1, GREEN[1])
    c.set(1, 3, GREEN[1]); c.set(2, 3, GREEN[2])
    c.set(0, 2, OUT)
    return c


# ---------------------------------------------------------------- gore sprites
BLOOD = {
    0: [hx("f0705a"), hx("d8433b"), hx("a12a2f"), hx("6e1c2a")],     # red
    1: [hx("c8f080"), hx("78c844"), hx("3e8e34"), hx("245e28")],     # green sap
    2: [hx("fff3a6"), hx("ffd23f"), hx("e89a1b"), hx("a65f12")],     # yellow
    3: [hx("d6b8ff"), hx("9a7ad8"), hx("6c50b0"), hx("46327e")],     # violet ichor
    4: TEAL,                                                         # teal goo
    5: [hx("ffe08a"), hx("f5a844"), hx("cc6e2c"), hx("8e4420")],     # amber sap
}


def stain_art(variant, kind=0):
    r = BLOOD[kind]
    c = Canvas(16, 6)
    if variant == 0:
        shape = S_ellipse(8, 3.6, 6.2, 2.0) | S_ellipse(4.2, 3.2, 2.2, 1.4) | S_ellipse(12.2, 3.8, 2.4, 1.3)
        extra = {(1, 4), (14, 2), (15, 4)}
    elif variant == 1:
        shape = S_ellipse(7, 3.2, 4.6, 1.9) | S_ellipse(11.5, 3.8, 3.0, 1.5) | S_ellipse(2.4, 3.8, 1.6, 1.0)
        extra = {(13, 1), (5, 0), (15, 3)}
    else:
        shape = S_ellipse(8, 3.8, 7.4, 1.5) | S_ellipse(8, 2.8, 3.0, 1.0)
        extra = {(3, 1), (12, 1), (9, 0)}
    shape = {p for p in shape if 0 <= p[0] < 16 and 0 <= p[1] < 6}
    for (x, y) in shape:
        below = (x, y + 1) in shape
        above = (x, y - 1) in shape
        col = r[1]
        if not below or (x + y) % 7 == 0 and not above:
            col = r[2]
        c.set(x, y, col)
    for (x, y) in sorted(shape):
        if (x, y - 1) not in shape:
            c.set(x, y, r[1] if (x + variant) % 3 else r[0])
    for p in extra:
        c.set(*p, r[1])
    # tiny wet highlights
    hl = sorted(shape)[len(shape) // 3]
    c.set(*hl, r[0])
    return c


def chunk_art(variant, kind=0):
    r = BLOOD[kind]
    legend = {"O": OUT, "r": r[0], "R": r[1], "Q": r[2], "D": r[3], "m": CREAM[0], "n": CREAM[1]}
    if variant == 0:
        rows = ["..OO..", ".OrRO.", "OrRRQO", "ORRQQO", ".OQQO.", "..OO.."]
    elif variant == 1:
        rows = [".OOOO.", "OrRRRO", "ORRQmO", "OQQnnO", ".OOOO.", "......"]
    else:
        rows = ["OO....", "ORO...", "ORRQO.", ".OQQRO", "..OQQO", "...OO."]
    return grid(rows, legend)


def slice_flash(frame):
    c = Canvas(32, 32)
    pts = []
    for y in range(32):
        for x in range(32):
            px, py = x + 0.5, y + 0.5
            ang = math.degrees(math.atan2(py - 30, px - 30))
            if ang < 0:
                ang += 360
            if not (180 <= ang <= 270):
                continue
            t = (ang - 180) / 90.0
            d_out = math.hypot(px - 30, py - 30)
            if frame == 0:
                lim = (0.0, 0.55)
                shift, rout, rin = 1.4, 27.0, 25.2
            elif frame == 1:
                lim = (0.0, 1.0)
                shift, rout, rin = 2.6, 27.0, 24.6
            else:
                lim = (0.42, 1.0)
                shift, rout, rin = 1.1, 27.5, 26.0
            if not (lim[0] <= t <= lim[1]):
                continue
            d_in = math.hypot(px - 30 - shift * 0.7071, py - 30 - shift * 0.7071)
            if d_out <= rout and d_in >= rin + (shift * 0.0):
                edge = d_out > rout - 1.2
                pts.append((x, y, edge))
    for (x, y, edge) in pts:
        if frame == 2 and (x * 3 + y * 5) % 4 == 0:
            continue
        c.set(x, y, hx("bfeaff") if edge else WHITE)
    if frame == 1:
        sparkle(c, 12, 13, WHITE, 2, hx("bfeaff"))
        sparkle(c, 22, 6, WHITE, 1, hx("bfeaff"))
    if frame == 2:
        for (x, y) in ((6, 22), (14, 15), (20, 9), (25, 4), (9, 26)):
            c.set(x, y, WHITE)
    return c


# ---------------------------------------------------------------- pad glyphs (our own look)
FONT3 = {
    "A": ["010", "101", "111", "101", "101"], "B": ["110", "101", "110", "101", "110"],
    "X": ["101", "101", "010", "101", "101"], "Y": ["101", "101", "010", "010", "010"],
    "L": ["100", "100", "100", "100", "111"], "R": ["110", "101", "110", "101", "101"],
    "T": ["111", "010", "010", "010", "010"], "3": ["111", "001", "011", "001", "111"],
}


def text3(cv, s, x, y, col):
    for ch in s:
        for gy, row in enumerate(FONT3[ch]):
            for gx, v in enumerate(row):
                if v == "1":
                    cv.set(x + gx, y + gy, col)
        x += 4


def pad_disc(cv, cx, cy, r, ring, ring_dark, face):
    for y in range(16):
        for x in range(16):
            d = math.hypot(x - 7.5 - (cx - 8), y - 7.5 - (cy - 8))
            if d <= r:
                cv.set(x, y, OUT)
            if d <= r - 1:
                cv.set(x, y, ring if (x - y) > -3 or d < r - 1.4 else ring_dark)
            if d <= r - 2.2:
                cv.set(x, y, face)


FACE_COLS = {
    "A": (hx("6ac45a"), hx("2f7d32")), "B": (hx("e0507a"), hx("8c2048")),
    "X": (hx("4aa8e8"), hx("24609c")), "Y": (hx("f6b82a"), hx("a8661a")),
}
PLATE = [hx("b8c4e0"), hx("7c8cb8"), hx("4e5a86")]
CLAY = [hx("e8b888"), hx("c4845a"), hx("8a5238")]
PLUM = [hx("c8b0e0"), hx("8a6cb0"), hx("56407e")]
INKC = hx("2a1d2e")


def glyph_face(letter):
    c = Canvas(16, 16)
    ring, dark = FACE_COLS[letter]
    pad_disc(c, 8, 8, 7.2, ring, dark, hx("fff8e0"))
    text3(c, letter, 6, 5, dark)
    return c


def glyph_plate(letters, plate, tall=False, left=True):
    c = Canvas(16, 16)
    if not tall:
        shape = S_rect(1, 4, 14, 11)
        for p in ((1, 4), (14, 4), (1, 11), (14, 11)):
            shape.discard(p)
        # bumper curve: a bulge on the outer top edge
        shape |= S_rect(3, 3, 12, 3)
    else:
        shape = S_rect(2, 1, 13, 14)
        for p in ((2, 1), (13, 1), (2, 14), (13, 14), (2, 2), (13, 2)):
            shape.discard(p)
        # flared trigger: slanted outer shoulder
        for k in range(4):
            if left:
                shape.discard((2 + k // 2, 3 + k // 2)) if False else None
    fill(c, shape, plate[1])
    for (x, y) in shape:
        if (x, y - 1) not in shape:
            c.set(x, y, plate[0])
        elif (x, y + 1) not in shape or (x, y + 2) not in shape:
            c.set(x, y, plate[2])
    c.outline()
    ty = 5 if not tall else 5
    tx = 4 if not tall else 4
    text3(c, letters, tx, ty + (0 if not tall else 1), hx("fff8e0"))
    # shadow pixel line under letters for legibility
    return c


def glyph_stick(letters):
    c = Canvas(16, 16)
    pad_disc(c, 8, 8, 7.2, hx("a8a2bc"), hx("5c5670"), hx("d8d4e6"))
    # concave rubber ring dots
    for a in range(0, 360, 45):
        x = int(round(7.5 + math.cos(math.radians(a)) * 5.4))
        y = int(round(7.5 + math.sin(math.radians(a)) * 5.4))
        c.set(x, y, hx("8e88a3"))
    text3(c, letters, 4, 5, hx("3a3050"))
    return c


def glyph_pill(kind):
    c = Canvas(16, 16)
    shape = S_rect(1, 5, 14, 10)
    for p in ((1, 5), (14, 5), (1, 10), (14, 10)):
        shape.discard(p)
    fill(c, shape, PLUM[1])
    for (x, y) in shape:
        if (x, y - 1) not in shape:
            c.set(x, y, PLUM[0])
        elif (x, y + 1) not in shape:
            c.set(x, y, PLUM[2])
    c.outline()
    tri = [(6, 6), (6, 7), (6, 8), (6, 9), (7, 7), (7, 8), (8, 7), (8, 8), (9, 8)] if False else None
    if kind == "start":      # play triangle pointing right
        pts = [(6, 6), (6, 7), (6, 8), (6, 9), (7, 6), (7, 7), (7, 8), (7, 9), (8, 7), (8, 8), (9, 7), (9, 8)]
        pts = [(5, 6), (5, 7), (5, 8), (5, 9), (6, 6), (6, 7), (6, 8), (6, 9), (7, 7), (7, 8), (8, 7), (8, 8), (9, 7)]
        pts = [(5, 6), (5, 7), (5, 8), (5, 9), (6, 6), (6, 7), (6, 8), (6, 9), (7, 7), (7, 8), (8, 7)]
        pts = [(x + 1, y) for (x, y) in pts if y in range(6, 10)]
    else:                    # back: triangle pointing left
        pts = [(10, 6), (10, 7), (10, 8), (10, 9), (9, 6), (9, 7), (9, 8), (9, 9), (8, 7), (8, 8), (7, 7)]
        pts = [(x - 1, y) for (x, y) in pts]
    for p in pts:
        c.set(*p, hx("fff8e0"))
    return c


def glyph_dpad(active=None, arrows=False):
    c = Canvas(16, 16)
    arms = {"up": S_rect(6, 1, 9, 6), "down": S_rect(6, 9, 9, 14), "left": S_rect(1, 6, 6, 9),
            "right": S_rect(9, 6, 14, 9)}
    base = S_rect(6, 6, 9, 9)
    cross = set(base)
    for a in arms.values():
        cross |= a
    fill(c, cross, hx("7c8cb8"))
    for (x, y) in cross:
        if (x, y - 1) not in cross or (x - 1, y) not in cross:
            c.set(x, y, hx("b8c4e0"))
        elif (x, y + 1) not in cross or (x + 1, y) not in cross:
            c.set(x, y, hx("4e5a86"))
    keys = [active] if active else (list(arms) if arrows else [])
    for k in keys:
        if active:
            fill(c, arms[k], hx("ffd23f"))
            for (x, y) in arms[k]:
                if (x, y + 1) not in cross or (x + 1, y) not in cross or (x, y + 1) in base and False:
                    c.set(x, y, hx("e89a1b"))
    c.outline()
    # arrow ticks on the arms
    tick = hx("2a1d2e") if active else hx("e8ecff")
    for k in (list(arms) if arrows or active else []):
        col = tick if (active == k) else (hx("e8ecff") if arrows else hx("e8ecff"))
        if k == "up":
            pts = [(7, 3), (8, 3), (7, 4), (8, 4)] if False else [(7, 4), (8, 4), (7, 3), (8, 3)]
            pts = [(7, 3), (8, 3), (6, 4), (9, 4)] if False else [(7, 3), (8, 3)]
            pts += [(7, 4), (8, 4)] if False else []
        elif k == "down":
            pts = [(7, 12), (8, 12)]
        elif k == "left":
            pts = [(3, 7), (3, 8)]
        else:
            pts = [(12, 7), (12, 8)]
        for p in pts:
            c.set(*p, col)
    return c


def glyph_stickmove():
    c = Canvas(16, 16)
    pad_disc(c, 8, 8, 4.4, hx("a8a2bc"), hx("5c5670"), hx("d8d4e6"))
    col = hx("ffd23f")
    for (cx, cy, dx, dy) in ((8, 1, 0, 1), (8, 14, 0, -1), (1, 8, 1, 0), (14, 8, -1, 0)):
        for k in range(3):
            for s in range(-k, k + 1):
                if dx == 0:
                    c.set(7 + s + (0 if True else 0), cy + dy * k + (0 if dy > 0 else 0), col)
                else:
                    c.set(cx + dx * k, 7 + s, col)
    # fix arrow pixels centred on the 2-pixel centre line
    c2 = Canvas(16, 16)
    pad_disc(c2, 8, 8, 4.8, hx("a8a2bc"), hx("5c5670"), hx("d8d4e6"))
    for k in range(3):
        # up
        for s in range(-k, k + 2):
            c2.set(7 + s, 0 + (2 - k), col)
        # down
        for s in range(-k, k + 2):
            c2.set(7 + s, 15 - (2 - k), col)
        # left
        for s in range(-k, k + 2):
            c2.set(0 + (2 - k), 7 + s, col)
        # right
        for s in range(-k, k + 2):
            c2.set(15 - (2 - k), 7 + s, col)
    return c2


def glyph_mouse(which=None):
    c = Canvas(16, 16)
    body = S_ellipse(8, 8, 5.2, 7.2) & S_rect(2, 1, 13, 14)
    fill(c, body, hx("e8e4f4"))
    for (x, y) in body:
        if (x, y + 1) not in body or (x + 1, y) not in body:
            c.set(x, y, hx("a8a2bc"))
        elif (x, y + 2) not in body:
            c.set(x, y, hx("c8c4dc"))
    # button split and wheel
    for y in range(2, 7):
        c.set(8, y, hx("7c7690")) if False else None
    c.hline(4, 11, 7, hx("7c7690"))
    c.rect(7, 2, 8, 6, hx("7c7690"))
    c.rect(7, 3, 8, 5, hx("a8a2bc"))
    if which == "left":
        c.rect(4, 2, 6, 6, hx("ffd23f")); c.set(4, 6, hx("e89a1b")); c.hline(5, 6, 6, hx("e89a1b"))
    elif which == "right":
        c.rect(9, 2, 11, 6, hx("ffd23f")); c.hline(9, 10, 6, hx("e89a1b")); c.set(11, 6, hx("e89a1b"))
    elif which == "middle":
        c.rect(7, 2, 8, 6, hx("ffd23f")); c.rect(7, 6, 8, 6, hx("e89a1b"))
        c.set(8, 4, hx("fff6a8"))
    c.outline()
    if which == "middle":
        # scroll ticks either side of the wheel
        c.set(5, 3, INKC); c.set(10, 5, INKC) if False else None
    return c


def key_cap():
    c = Canvas(24, 24)
    shape = S_rect(0, 0, 23, 23)
    for (x, y) in ((0, 0), (23, 0), (0, 23), (23, 23), (1, 0), (0, 1), (22, 0), (23, 1), (0, 22), (1, 23), (23, 22),
                   (22, 23)):
        shape.discard((x, y))
    for (x, y) in shape:
        if y >= 19:
            col = CREAM[2]
        elif y == 1 or x == 1:
            col = hx("ffffff")
        else:
            col = CREAM[0]
        c.set(x, y, col)
    for (x, y) in shape:
        if x in (0, 23) or y in (0, 23):
            c.set(x, y, OUT)
    c.hline(2, 21, 18, OUT)
    c.hline(2, 21, 19, hx("e0cfa0")) if False else None
    return c


# ---------------------------------------------------------------- resource writing
WRITTEN = []


def write_meta(name):
    path = os.path.join(SPRITES, f"{name}.image.json.meta")
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            meta = json.load(f)
        if meta.get("kind") == "Image" and meta.get("displayName") == name:
            return meta["guid"]
    guid = uuid.UUID(hashlib.md5(name.encode("utf-8")).hexdigest()).hex
    meta = {"guid": guid, "kind": "Image", "displayName": name,
            "createdUtc": "2026-10-15T00:00:00Z", "modifiedUtc": "2026-10-15T00:00:00Z"}
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(meta, f, indent=2)
        f.write("\n")
    return guid


def write_image(name, canvases, origin, mask, tags, durations, usage="sprite", write=True):
    """tags: list of (tagName, startIndex, endIndex, loop)."""
    w, h = canvases[0].w, canvases[0].h
    for cv in canvases:
        assert (cv.w, cv.h) == (w, h), name
    WRITTEN.append((name, w, h, origin, mask, [(t[0], t[2] - t[1] + 1) for t in tags], len(canvases)))
    if not write:
        return canvases
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
    return canvases


def contact_sheet(rows, path, bg=(143, 211, 255, 255), scale=8, pad=4):
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


# ---------------------------------------------------------------- main
GLYPH_ORDER = ["A", "B", "X", "Y", "LB", "RB", "LT", "RT", "L3", "R3", "Start", "Back", "DpadUp", "DpadDown",
               "DpadLeft", "DpadRight", "DpadAll", "StickMove", "MouseBody", "MouseLeft", "MouseRight",
               "MouseMiddle"]


def glyph_frames():
    return [glyph_face("A"), glyph_face("B"), glyph_face("X"), glyph_face("Y"),
            glyph_plate("LB", PLATE), glyph_plate("RB", PLATE),
            glyph_plate("LT", CLAY, tall=True, left=True), glyph_plate("RT", CLAY, tall=True, left=False),
            glyph_stick("L3"), glyph_stick("R3"), glyph_pill("start"), glyph_pill("back"),
            glyph_dpad("up"), glyph_dpad("down"), glyph_dpad("left"), glyph_dpad("right"),
            glyph_dpad(None, arrows=True), glyph_stickmove(),
            glyph_mouse(None), glyph_mouse("left"), glyph_mouse("right"), glyph_mouse("middle")]


def main():
    preview_only = "--preview" in sys.argv
    wr = not preview_only
    sheets = {}
    P16 = (2, 2, 12, 12)

    def idle2(fn):
        return [fn(False), fn(True)]

    sw = write_image("Item Sword Sprite", [sword_pickup(False), sword_pickup(True)], (8, 8), P16,
                     [("Idle", 0, 1, True)], [520, 160], write=wr)
    sh = write_image("Item Shield Sprite", [shield_pickup(False), shield_pickup(True)], (8, 8), P16,
                     [("Idle", 0, 1, True)], [520, 160], write=wr)
    bw = write_image("Item Bow Sprite", [bow_pickup(False), bow_pickup(True)], (8, 8), P16,
                     [("Idle", 0, 1, True)], [520, 160], write=wr)
    fl = write_image("Item Propeller Flower Sprite", [flower_pickup(0, False), flower_pickup(1, True)], (8, 8), P16,
                     [("Idle", 0, 1, True)], [200, 200], write=wr)

    hs = write_image("Held Sword", [sword_held(i) for i in range(4)], (4, 8), (4, 2, 26, 12),
                     [("Swing", 0, 3, False)], 40, write=wr)
    hsh = write_image("Held Shield", [shield_held(False), shield_held(True)], (12, 14), (1, 1, 22, 26),
                      [("Idle", 0, 0, True), ("Raised", 1, 1, True)], 1000, write=wr)
    hb = write_image("Held Bow", [bow_held(0), bow_held(2), bow_held(4), bow_held(6)], (10, 14), (2, 1, 16, 26),
                     [("Idle", 0, 0, True), ("Draw", 1, 3, False)], [1000, 150, 150, 150], write=wr)
    hf = write_image("Held Flower", [flower_held_closed()] + [flower_held_spin(k) for k in range(4)], (12, 20),
                     (1, 2, 22, 20),
                     [("Closed", 0, 0, True), ("Spin", 1, 4, True)], [1000, 50, 50, 50, 50], write=wr)
    ar = write_image("Arrow Sprite", [arrow_art()], (8, 3), (0, 0, 16, 5), [("Fly", 0, 0, True)], 1000, write=wr)

    tsw = write_image("Thrown Sword", [rot90(centered(sword_pickup(False), 20, 20), k) for k in range(4)], (10, 10),
                      (3, 3, 14, 14), [("Spin", 0, 3, True)], 50, write=wr)
    tsh = write_image("Thrown Shield", [shield_thrown(k) for k in range(4)], (10, 10), (3, 3, 14, 14),
                      [("Spin", 0, 3, True)], 60, write=wr)
    tbw = write_image("Thrown Bow", [bow_thrown(k) for k in range(4)], (10, 10), (3, 3, 14, 14),
                      [("Spin", 0, 3, True)], 60, write=wr)
    tfl = write_image("Thrown Flower", [flower_thrown(k) for k in range(4)], (8, 8), (2, 2, 12, 12),
                      [("Spin", 0, 3, True)], 70, write=wr)

    # blood stain: Splat1..3 (red) then per-kind recolours (kinds 1..5) so the Gore Kit can pick by bloodKind
    stain_frames, stain_tags = [], []
    for k in range(6):
        for v in range(3):
            stain_frames.append(stain_art(v, k))
    stain_tags = [("Splat1", 0, 0, True), ("Splat2", 1, 1, True), ("Splat3", 2, 2, True)]
    for k in range(1, 6):
        for v in range(3):
            stain_tags.append((f"K{k}Splat{v + 1}", k * 3 + v, k * 3 + v, True))
    bs = write_image("Blood Stain Sprite", stain_frames, (8, 3), (0, 0, 16, 6), stain_tags, 1000, write=wr)
    chunk_frames, chunk_tags = [], [("Chunk1", 0, 0, True), ("Chunk2", 1, 1, True), ("Chunk3", 2, 2, True)]
    for k in range(6):
        for v in range(3):
            chunk_frames.append(chunk_art(v, k))
    for k in range(1, 6):
        for v in range(3):
            chunk_tags.append((f"K{k}Chunk{v + 1}", k * 3 + v, k * 3 + v, True))
    gc = write_image("Gore Chunk Sprite", chunk_frames, (3, 3), (0, 0, 6, 6), chunk_tags, 1000, write=wr)
    sf = write_image("Slice Flash", [slice_flash(i) for i in range(3)], (16, 16), (0, 0, 32, 32),
                     [("Flash", 0, 2, False)], 40, write=wr)

    gl = glyph_frames()
    assert len(gl) == 22
    pg = write_image("Pad Glyphs", gl, (0, 0), (0, 0, 16, 16),
                     [(n, i, i, True) for i, n in enumerate(GLYPH_ORDER)], 1000, write=wr)
    kc = write_image("Key Cap", [key_cap()], (0, 0), (0, 0, 24, 24), [("Idle", 0, 0, True)], 1000, write=wr)

    bg = (143, 211, 255, 255)
    contact_sheet([sw + sh + bw + fl + ar, hs, hsh + hb + hf, tsw + tsh + tbw + tfl],
                  os.path.join(PREVIEW_DIR, "art4_items.png"), bg=bg, scale=6)
    contact_sheet([bs[:3] + bs[3:6] + bs[6:9], gc[:3] + gc[3:6] + gc[6:9] + gc[9:12] + gc[12:15] + gc[15:18], sf],
                  os.path.join(PREVIEW_DIR, "art4_gore.png"), bg=(120, 150, 110, 255), scale=8)
    contact_sheet([pg[:11], pg[11:], kc], os.path.join(PREVIEW_DIR, "art4_glyphs.png"), bg=(70, 80, 110, 255), scale=6)
    print("%s (%d images)" % ("preview only" if preview_only else "wrote template", len(WRITTEN)))
    for (name, w, h, origin, mask, tags, n) in WRITTEN:
        print(f"  {name}: {w}x{h} origin {origin} mask {mask} frames {n} tags {tags}")


if __name__ == "__main__":
    main()
