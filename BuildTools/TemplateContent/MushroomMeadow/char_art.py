"""Mushroom Meadow character / pickup / UI sprite generator (ART-CHAR).

Deterministic: re-running produces byte-identical PNGs and JSON.
Run from anywhere:  python BuildTools/TemplateContent/MushroomMeadow/char_art.py
"""
import base64
import hashlib
import json
import math
import os
import uuid
import shutil

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
SPRITES = os.path.join(REPO, "Source", "Genesis.Application.Core", "Projects", "Templates", "Assets",
                       "TwoDShowcase", "Assets", "Sprites")
PREVIEW = os.path.join(REPO, "TestResults", "MushroomMeadow", "chars_preview.png")


def hx(s, a=255):
    s = s.lstrip("#")
    return (int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16), a)


# ---------------------------------------------------------------- palette
OUT = hx("2a1d2e")
CLEAR = (0, 0, 0, 0)
RED = [hx("f0705a"), hx("d8433b"), hx("a12a2f"), hx("6e1c2a")]   # light .. dark
CREAM = [hx("fff8e0"), hx("f2e2b6"), hx("d4bd8a")]
SKIN = [hx("ffd9a8"), hx("f2b47e"), hx("c98457")]
GREEN = [hx("9be15d"), hx("4caf3c"), hx("2f7d32"), hx("1f5226")]
DIRT = [hx("c48a52"), hx("9b6a3c"), hx("6d4526"), hx("4a2e1c")]
GOLD = [hx("fff3a6"), hx("ffd23f"), hx("e89a1b"), hx("a65f12")]
BLUE = [hx("8fd3ff"), hx("4f8fd8"), hx("2f5a9e")]
WHITE = hx("ffffff")
GREY = [hx("c9c3d6"), hx("8e88a3"), hx("5c5670")]
BLACK = hx("1a1220")
PINK = hx("ff9aa8")
WING = [hx("f4fbff"), hx("c6e8ff"), hx("8fb8d8")]


def stable_id(*parts):
    return hashlib.md5("|".join(("mm-char",) + parts).encode("utf-8")).hexdigest()


# ---------------------------------------------------------------- canvas
class Canvas:
    def __init__(self, w, h):
        self.w, self.h = w, h
        self.px = [[CLEAR] * w for _ in range(h)]

    def get(self, x, y):
        if 0 <= x < self.w and 0 <= y < self.h:
            return self.px[y][x]
        return CLEAR

    def set(self, x, y, c):
        x, y = int(round(x)), int(round(y))
        if 0 <= x < self.w and 0 <= y < self.h:
            self.px[y][x] = c

    def rect(self, x0, y0, x1, y1, c):  # inclusive
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                self.set(x, y, c)

    def hline(self, x0, x1, y, c):
        self.rect(min(x0, x1), y, max(x0, x1), y, c)

    def thick_line(self, x0, y0, x1, y1, r, c):
        n = int(max(abs(x1 - x0), abs(y1 - y0)) * 2) + 1
        for i in range(n + 1):
            t = i / n
            cx, cy = x0 + (x1 - x0) * t, y0 + (y1 - y0) * t
            for yy in range(int(math.floor(cy - r)), int(math.ceil(cy + r)) + 1):
                for xx in range(int(math.floor(cx - r)), int(math.ceil(cx + r)) + 1):
                    if (xx - cx) ** 2 + (yy - cy) ** 2 <= r * r + 0.25:
                        self.set(xx, yy, c)

    def shaded_ellipse(self, cx, cy, rx, ry, ramp, y_min=None, y_max=None, light=(-0.55, -0.75), spec=True):
        """Filled ellipse shaded by a top-left light. ramp is light..dark."""
        for y in range(int(cy - ry) - 1, int(cy + ry) + 2):
            for x in range(int(cx - rx) - 1, int(cx + rx) + 2):
                nx = (x + 0.5 - cx) / max(rx, 0.01)
                ny = (y + 0.5 - cy) / max(ry, 0.01)
                d = nx * nx + ny * ny
                if d > 1.0:
                    continue
                if y_min is not None and y < y_min:
                    continue
                if y_max is not None and y > y_max:
                    continue
                lit = -(nx * light[0] + ny * light[1])
                if spec and lit > 0.55 and d < 0.55:
                    idx = 0
                elif lit > 0.05 or d < 0.35:
                    idx = 1 if len(ramp) > 2 else 0
                elif lit > -0.45:
                    idx = min(2, len(ramp) - 1) if len(ramp) > 3 else len(ramp) - 2
                else:
                    idx = len(ramp) - 1 if len(ramp) > 3 else len(ramp) - 1
                if len(ramp) == 3:
                    idx = 0 if (spec and lit > 0.55 and d < 0.55) else (1 if lit > -0.35 else 2)
                self.set(x, y, ramp[idx])

    def outline(self, color=OUT, diagonal=False):
        add = []
        for y in range(self.h):
            for x in range(self.w):
                if self.px[y][x][3] != 0:
                    continue
                nbrs = [(1, 0), (-1, 0), (0, 1), (0, -1)]
                if diagonal:
                    nbrs += [(1, 1), (-1, 1), (1, -1), (-1, -1)]
                for dx, dy in nbrs:
                    c = self.get(x + dx, y + dy)
                    if c[3] != 0 and c != color:
                        add.append((x, y))
                        break
        for x, y in add:
            self.px[y][x] = color

    def image(self):
        img = Image.new("RGBA", (self.w, self.h))
        img.putdata([c for row in self.px for c in row])
        return img

    def rgba_bytes(self):
        return bytes(v for row in self.px for c in row for v in c)


# ---------------------------------------------------------------- explorer
def draw_explorer(bob=0, legs=((3, 0), (-3, 0)), arms=((4, 6), (-3, 6)), eyes="open", mouth=False,
                  lean=0, cap_dy=0):
    """legs: (front, back) foot (dx from hip, lift). arms: (front, back) hand offset from shoulder."""
    c = Canvas(32, 40)
    hip_y = 29 + bob
    hip_x = 15 + lean
    feet_y = 38
    # back arm (behind body)
    sx, sy = 14 + lean, 21 + bob
    bx, by = sx + arms[1][0], sy + arms[1][1]
    c.thick_line(sx, sy, bx, by, 1.0, GREEN[3])
    c.rect(int(bx) - 1, int(by), int(bx), int(by) + 1, SKIN[2])
    # back leg
    (fdx, flift), (bdx, blift) = legs
    def leg(dx, lift, pants, boot, boot_hi):
        fx, fy = hip_x + dx, feet_y - lift
        c.thick_line(hip_x, hip_y, fx, fy - 3, 1.3, pants)
        c.rect(int(fx) - 1, int(fy) - 2, int(fx) + 3, int(fy), boot)
        c.hline(int(fx) - 1, int(fx) + 2, int(fy) - 2, boot_hi)
        c.hline(int(fx) - 1, int(fx) + 3, int(fy), DIRT[3])
    leg(bdx, blift, DIRT[2], DIRT[3], DIRT[2])
    # backpack
    c.rect(8 + lean, 19 + bob, 10 + lean, 27 + bob, DIRT[2])
    c.rect(8 + lean, 19 + bob, 10 + lean, 19 + bob, DIRT[1])
    c.rect(9 + lean, 22 + bob, 10 + lean, 22 + bob, GOLD[2])
    # torso (tunic)
    tx0, tx1, ty0, ty1 = 11 + lean, 19 + lean, 19 + bob, 29 + bob
    c.rect(tx0, ty0, tx1, ty1, GREEN[1])
    c.rect(tx0, ty0, tx0 + 1, ty1, GREEN[2])
    c.rect(tx1 - 2, ty0 + 1, tx1 - 1, ty0 + 4, GREEN[0])
    c.hline(tx0, tx1, ty1, GREEN[2])
    # belt
    c.hline(tx0, tx1, 26 + bob, DIRT[2])
    c.rect(tx1 - 3, 26 + bob, tx1 - 2, 26 + bob, GOLD[1])
    # backpack strap
    c.set(tx0 + 2, ty0, DIRT[2]); c.set(tx0 + 3, ty0 + 1, DIRT[2]); c.set(tx0 + 4, ty0 + 2, DIRT[2])
    # front leg
    leg(fdx, flift, DIRT[1], DIRT[3], DIRT[1])
    # head
    hy = 11 + bob
    c.rect(12 + lean, hy, 20 + lean, hy + 7, SKIN[1])
    c.rect(13 + lean, hy + 7, 19 + lean, hy + 8, SKIN[1])
    c.rect(17 + lean, hy + 1, 20 + lean, hy + 3, SKIN[0])
    c.rect(12 + lean, hy, 13 + lean, hy + 6, DIRT[2])          # hair at back
    c.set(14 + lean, hy, DIRT[2])
    c.rect(14 + lean, hy + 4, 14 + lean, hy + 5, SKIN[2])      # ear
    c.set(21 + lean, hy + 4, SKIN[1]); c.set(21 + lean, hy + 5, SKIN[1])  # nose
    c.set(19 + lean, hy + 6, SKIN[2])                          # cheek line
    ex = 18 + lean
    if eyes == "open":
        c.rect(ex, hy + 2, ex, hy + 4, BLACK)
        c.set(ex, hy + 2, WHITE)
        c.rect(ex - 2, hy + 1, ex, hy + 1, DIRT[3])            # brow
    elif eyes == "blink":
        c.hline(ex - 1, ex, hy + 4, BLACK)
    elif eyes == "hurt":
        c.set(ex - 1, hy + 2, BLACK); c.set(ex + 1, hy + 2, BLACK)
        c.set(ex, hy + 3, BLACK)
        c.set(ex - 1, hy + 4, BLACK); c.set(ex + 1, hy + 4, BLACK)
    elif eyes == "up":
        c.rect(ex, hy + 1, ex, hy + 3, BLACK)
        c.set(ex, hy + 1, WHITE)
        c.rect(ex - 2, hy, ex, hy, DIRT[3])
    if mouth:
        c.rect(19 + lean, hy + 6, 20 + lean, hy + 7, RED[3])
    else:
        c.hline(19 + lean, 20 + lean, hy + 7, SKIN[2])
    # mushroom-cap hat
    cy = 10 + bob + cap_dy
    c.shaded_ellipse(16 + lean, cy, 10.5, 8.5, RED, y_max=cy)
    # brim
    c.hline(6 + lean, 26 + lean, cy + 1, RED[2])
    c.hline(7 + lean, 25 + lean, cy + 2, CREAM[2])
    c.hline(8 + lean, 24 + lean, cy + 2, CREAM[1])
    # spots
    for sx_, sy_, w in ((10, -5, 2), (18, -7, 3), (22, -3, 2), (14, -2, 2)):
        x0 = sx_ + lean
        y0 = cy + sy_
        c.rect(x0, y0, x0 + w - 1, y0 + w - 2 if w > 2 else y0, CREAM[0])
        if w > 2:
            c.hline(x0, x0 + w - 1, y0 + w - 1, CREAM[1])
        else:
            c.hline(x0, x0 + w - 1, y0 + 1, CREAM[1])
    # front arm
    sx, sy = 16 + lean, 21 + bob
    fx_, fy_ = sx + arms[0][0], sy + arms[0][1]
    c.thick_line(sx, sy, fx_, fy_, 1.1, GREEN[1])
    c.set(sx, sy - 1, GREEN[0])
    c.rect(int(round(fx_)), int(round(fy_)), int(round(fx_)) + 1, int(round(fy_)) + 1, SKIN[1])
    c.set(int(round(fx_)), int(round(fy_)), SKIN[0])
    c.outline()
    return c


def explorer_frames():
    idle = [
        draw_explorer(0, ((3, 0), (-3, 0)), ((3, 7), (-2, 7))),
        draw_explorer(0, ((3, 0), (-3, 0)), ((3, 7), (-2, 7)), cap_dy=-1),
        draw_explorer(1, ((3, 0), (-3, 0)), ((3, 6), (-2, 6))),
        draw_explorer(1, ((3, 0), (-3, 0)), ((3, 6), (-2, 6)), eyes="blink"),
    ]
    # 6-frame run: contact, down, pass, contact(mirror), down, pass
    cyc = [
        (0, (6, 0), (-6, 2), (5, 3), (-5, 2)),
        (1, (3, 0), (-4, 5), (3, 5), (-3, 4)),
        (-1, (-1, 0), (1, 6), (-1, 6), (1, 6)),
        (0, (-6, 2), (6, 0), (-4, 2), (5, 3)),
        (1, (-4, 5), (3, 0), (-3, 4), (3, 5)),
        (-1, (1, 6), (-1, 0), (1, 6), (-1, 6)),
    ]
    run = []
    for bob, front, back, afront, aback in cyc:
        run.append(draw_explorer(bob, (front, back), (afront, aback), lean=1))
    jump = [draw_explorer(-1, ((4, 5), (-3, 1)), ((7, -5), (-5, 2)), eyes="up", lean=0)]
    fall = [draw_explorer(-2, ((3, 2), (-4, 3)), ((7, -3), (-6, -5)), eyes="open", mouth=True)]
    hurt = [draw_explorer(0, ((5, 3), (-5, 1)), ((6, -2), (-6, -1)), eyes="hurt", mouth=True, lean=-2)]
    return idle, run, jump, fall, hurt


# ---------------------------------------------------------------- acorn walker
def draw_acorn(step=0, squashed=False):
    c = Canvas(32, 28)
    if squashed:
        # flattened nut
        c.shaded_ellipse(16, 23, 13.5, 4.5, DIRT[:4], spec=False)
        c.shaded_ellipse(16, 20.5, 14, 3.5, [DIRT[1], DIRT[2], DIRT[3], DIRT[3]], y_max=21)
        for x in range(4, 28, 3):
            c.set(x, 19, DIRT[3]); c.set(x + 1, 20, DIRT[3])
        c.rect(15, 15, 16, 17, DIRT[2])
        # X eyes
        for ex in (11, 20):
            c.set(ex - 1, 22, BLACK); c.set(ex + 1, 22, BLACK); c.set(ex, 23, BLACK)
            c.set(ex - 1, 24, BLACK); c.set(ex + 1, 24, BLACK)
        c.outline()
        return c
    bob = 1 if step in (1, 3) else 0
    # feet
    foot = [(-1, 0), (0, 1), (1, 0), (0, 1)][step]
    fl = (10 + foot[0] * 2, 26 - (1 if foot[0] < 0 else 0))
    fr = (20 - foot[0] * 2, 26 - (1 if foot[0] > 0 else 0))
    for fx, fy in (fl, fr):
        c.rect(fx, fy - 1, fx + 3, fy, DIRT[3])
        c.hline(fx, fx + 2, fy - 1, DIRT[2])
    # nut body
    c.shaded_ellipse(16, 16 + bob, 11, 9.5, [hx("e8b070"), hx("c98a4a"), hx("9b6a3c"), hx("6d4526")])
    # cap
    capy = 11 + bob
    c.shaded_ellipse(16, capy, 13, 7, [hx("a77a4a"), hx("7d5530"), hx("5c3c22"), hx("3e2817")], y_max=capy + 2)
    for y in range(capy - 6, capy + 3):
        for x in range(3, 30):
            col = c.get(x, y)
            if col[3] and (x + y * 2) % 4 == 0 and col != hx("3e2817"):
                c.set(x, y, hx("5c3c22"))
    c.hline(4, 28, capy + 3, hx("3e2817"))
    # stem
    c.rect(15, capy - 9, 16, capy - 7, hx("5c3c22"))
    c.set(17, capy - 9, hx("7d5530"))
    # angry eyes (facing right)
    ey = 17 + bob
    for ex in (16, 22):
        c.rect(ex, ey, ex + 1, ey + 2, WHITE)
        c.rect(ex + 1, ey + 1, ex + 1, ey + 2, BLACK)
    c.hline(15, 17, ey - 1, BLACK); c.set(17, ey, BLACK)
    c.hline(22, 24, ey - 1, BLACK); c.set(22, ey, BLACK)
    c.hline(18, 21, ey + 5, hx("6d4526"))
    c.outline()
    return c


# ---------------------------------------------------------------- buzz bee
def draw_bee(frame):
    c = Canvas(32, 24)
    # wings behind body
    if frame == 0:
        c.shaded_ellipse(12, 5, 4.5, 3.5, WING, spec=False)
        c.shaded_ellipse(18, 4.5, 5, 3.5, WING, spec=False)
    else:
        c.shaded_ellipse(11, 9, 5, 2.5, WING, spec=False)
        c.shaded_ellipse(18, 9, 5.5, 2.5, WING, spec=False)
    # stinger
    c.hline(3, 5, 14, BLACK)
    c.set(2, 14, GREY[1])
    # body
    c.shaded_ellipse(14, 14, 8.5, 6.5, GOLD)
    for x0 in (9, 14):
        for y in range(7, 22):
            for x in range(x0, x0 + 2):
                if c.get(x, y)[3] and c.get(x, y) in GOLD:
                    c.set(x, y, BLACK if y > 10 else hx("3a2a3e"))
    # head
    c.shaded_ellipse(23, 12, 4.5, 4.5, [hx("5a4a60"), hx("3a2a3e"), BLACK])
    c.rect(24, 10, 25, 12, WHITE)
    c.set(25, 11, BLACK); c.set(25, 12, BLACK)
    c.set(26, 14, PINK)
    # antennae
    c.thick_line(22, 8, 21, 4, 0.4, BLACK)
    c.thick_line(25, 8, 27, 4, 0.4, BLACK)
    c.set(21, 3, GOLD[1]); c.set(27, 3, GOLD[1])
    # legs
    c.set(13, 21, BLACK); c.set(17, 21, BLACK)
    c.outline()
    # re-wing translucency highlight
    return c


# ---------------------------------------------------------------- bounce mushroom
def draw_bounce(squashed):
    c = Canvas(32, 32)
    if not squashed:
        cap_cy, rx, ry, stem_top = 21, 15, 10, 21
    else:
        cap_cy, rx, ry, stem_top = 25, 15, 7, 25
    # stem
    c.rect(10, stem_top, 21, 30, CREAM[1])
    c.rect(10, stem_top, 11, 30, CREAM[2])
    c.rect(19, stem_top + 1, 20, 29, CREAM[0])
    c.hline(10, 21, 30, CREAM[2])
    ey = 26 if not squashed else 28
    c.rect(13, ey, 13, ey + 1, BLACK); c.rect(18, ey, 18, ey + 1, BLACK)
    c.set(15, ey + 2 if not squashed else ey + 1, PINK); c.set(16, ey + 2 if not squashed else ey + 1, PINK)
    # cap dome
    c.shaded_ellipse(16, cap_cy, rx, ry, RED, y_max=cap_cy)
    c.hline(1, 30, cap_cy + 1, RED[2])
    c.hline(3, 28, cap_cy + 2, RED[3])
    # spots
    top = cap_cy - ry
    sp = [(8, 0.45, 3), (15, 0.12, 4), (23, 0.35, 3), (4, 0.75, 2), (27, 0.7, 2)]
    for sx, f, w in sp:
        sy = int(top + f * ry * 1.4) + 1
        h = max(1, w - 1 if not squashed else w - 2)
        c.rect(sx, sy, sx + w - 1, sy + h - 1, CREAM[0])
        c.hline(sx, sx + w - 1, sy + h, CREAM[2])
    c.outline()
    return c


# ---------------------------------------------------------------- coins
def draw_coin(w, h, rx_full, phase):
    c = Canvas(w, h)
    cx, cy = w / 2, h / 2
    ry = h / 2 - 1.5
    rx = [rx_full, rx_full * 0.6, max(1.0, rx_full * 0.18), rx_full * 0.6][phase]
    if phase == 2:
        c.rect(int(cx) - 1, int(cy - ry) + 1, int(cx), int(cy + ry) - 1, GOLD[1])
        c.rect(int(cx), int(cy - ry) + 1, int(cx), int(cy + ry) - 1, GOLD[2])
        c.set(int(cx) - 1, int(cy - ry) + 2, GOLD[0])
        c.outline()
        return c
    c.shaded_ellipse(cx, cy, rx, ry, GOLD)
    # rim + emblem
    inner_rx, inner_ry = rx - (2 if w > 16 else 1.6), ry - (2 if w > 16 else 1.6)
    if inner_rx > 1:
        for y in range(h):
            for x in range(w):
                nx = (x + 0.5 - cx) / inner_rx
                ny = (y + 0.5 - cy) / inner_ry
                d = nx * nx + ny * ny
                if 0.78 < d <= 1.0:
                    c.set(x, y, GOLD[2])
    if phase == 0:
        if w > 16:
            # sun emblem: disc + rays
            for ang in range(0, 360, 45):
                a = math.radians(ang)
                c.thick_line(cx - 0.5 + math.cos(a) * 6.5, cy - 0.5 + math.sin(a) * 6.5,
                             cx - 0.5 + math.cos(a) * 9, cy - 0.5 + math.sin(a) * 9, 0.75, GOLD[3])
            c.shaded_ellipse(cx, cy, 6, 6, [GOLD[3], GOLD[3], GOLD[3]], spec=False)
            c.shaded_ellipse(cx, cy, 5, 5, [GOLD[0], GOLD[0], GOLD[1]], spec=False)
            c.rect(int(cx) - 3, int(cy) - 2, int(cx) - 2, int(cy) - 1, GOLD[3])
            c.rect(int(cx) + 1, int(cy) - 2, int(cx) + 2, int(cy) - 1, GOLD[3])
            c.hline(int(cx) - 2, int(cx) + 1, int(cy) + 2, GOLD[3])
            c.set(int(cx) - 3, int(cy) + 1, GOLD[3]); c.set(int(cx) + 2, int(cy) + 1, GOLD[3])
            c.set(cx - 7, cy - 6, WHITE); c.set(cx - 6, cy - 7, WHITE)
        else:
            c.rect(int(cx) - 1, int(cy) - 3, int(cx), int(cy) + 2, GOLD[2])
            c.rect(int(cx) - 2, int(cy) - 1, int(cx) + 1, int(cy), GOLD[2])
            c.set(int(cx) - 1, int(cy) - 3, GOLD[0])
            c.set(int(cx) - 3, int(cy) - 5, WHITE)
    elif phase in (1, 3):
        x = int(cx) + (-1 if phase == 1 else 0)
        c.rect(x, int(cy - ry) + 3, x, int(cy + ry) - 3, GOLD[2] if phase == 1 else GOLD[0])
    c.outline()
    return c


# ---------------------------------------------------------------- checkpoint
def draw_checkpoint(lit):
    c = Canvas(32, 72)
    # glow sparkles (lit) drawn first so outline doesn't wrap them
    pole_x = 11
    # base: mossy stone
    c.shaded_ellipse(12, 68, 8.5, 3.5, GREY[:3])
    c.rect(4, 68, 20, 70, GREY[1])
    c.hline(4, 20, 70, GREY[2])
    c.hline(5, 19, 66, GREEN[1]); c.hline(7, 16, 65, GREEN[0])
    c.set(5, 67, GREEN[2]); c.set(19, 67, GREEN[2])
    # pole
    c.rect(pole_x - 1, 7, pole_x + 1, 65, DIRT[1])
    c.rect(pole_x + 1, 7, pole_x + 1, 65, DIRT[2])
    c.rect(pole_x - 1, 7, pole_x - 1, 65, DIRT[0])
    for y in range(12, 64, 9):
        c.hline(pole_x - 1, pole_x + 1, y, DIRT[2])
    # top orb
    orb = GOLD[:3] if lit else GREY[:3]
    c.shaded_ellipse(pole_x + 0.5, 4.5, 3, 3, orb)
    # pennant (triangle: from pole at rows 12..28 to tip at x 26, y 20)
    fill = [GOLD[0], GOLD[1], GOLD[2]] if lit else [hx("8fb8e8"), BLUE[1], BLUE[2]]
    for y in range(11, 30):
        t = abs(y - 20) / 9.0
        xe = int(round(13 + (26 - 13) * (1 - t)))
        for x in range(13, xe + 1):
            if y <= 13:
                col = fill[0]
            elif y >= 26 or x >= xe - 1 and y > 20:
                col = fill[2]
            else:
                col = fill[1]
            c.set(x, y, col)
    # pennant emblem: little mushroom (unlit) / sun (lit)
    if lit:
        c.rect(16, 18, 18, 22, WHITE if False else GOLD[0])
        c.rect(15, 19, 19, 21, GOLD[0])
        c.set(17, 17, GOLD[3]); c.set(17, 23, GOLD[3]); c.set(14, 20, GOLD[3]); c.set(20, 20, GOLD[3])
        c.set(17, 20, GOLD[3])
    else:
        c.rect(15, 18, 19, 19, RED[1]); c.hline(16, 18, 17, RED[1]); c.set(16, 18, CREAM[0])
        c.rect(16, 20, 18, 22, CREAM[1])
    c.outline()
    if lit:
        # glow sparkles (crisp, no alpha blend) around orb and pennant
        for (x, y) in ((5, 3), (18, 2), (4, 9), (19, 7), (28, 14), (29, 25), (22, 32), (6, 18)):
            col = GOLD[0] if (x + y) % 2 else WHITE
            c.set(x, y, col)
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                if c.get(x + dx, y + dy)[3] == 0:
                    c.set(x + dx, y + dy, GOLD[1] + () if False else (GOLD[1][0], GOLD[1][1], GOLD[1][2], 170))
    return c


# ---------------------------------------------------------------- icons
def draw_heart():
    c = Canvas(16, 16)
    rows = [
        "..##...##..",
        ".####.####.",
        "###########",
        "###########",
        "###########",
        ".#########.",
        "..#######..",
        "...#####...",
        "....###....",
        ".....#.....",
    ]
    ox, oy = 2, 3
    for y, row in enumerate(rows):
        for x, ch in enumerate(row):
            if ch == "#":
                col = RED[1]
                if y >= 5 or x >= 8 and y >= 3:
                    col = RED[2]
                c.set(ox + x, oy + y, col)
    c.rect(ox + 2, oy + 1, ox + 3, oy + 2, RED[0])
    c.set(ox + 2, oy + 1, WHITE)
    c.outline()
    return c


def draw_coin_icon():
    c = draw_coin(16, 16, 6.5, 0)
    return c


# ---------------------------------------------------------------- title logo
FONT = {
    "M": ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
    "U": ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
    "S": ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
    "H": ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
    "R": ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
    "O": ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
    "E": ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
    "A": ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
    "D": ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
    "W": ["10001", "10001", "10001", "10101", "10101", "11011", "10001"],
}


def text_mask(text, scale, gap):
    w = len(text) * 5 * scale + (len(text) - 1) * gap
    h = 7 * scale
    m = [[False] * w for _ in range(h)]
    for i, ch in enumerate(text):
        g = FONT[ch]
        ox = i * (5 * scale + gap)
        for gy, row in enumerate(g):
            for gx, bit in enumerate(row):
                if bit == "1":
                    for yy in range(scale):
                        for xx in range(scale):
                            m[gy * scale + yy][ox + gx * scale + xx] = True
        # round outer corners of each glyph cell block a bit: chip 1px off convex corners
    for y in range(h):
        for x in range(w):
            if not m[y][x]:
                continue
            up = y > 0 and m[y - 1][x]
            dn = y < h - 1 and m[y + 1][x]
            lf = x > 0 and m[y][x - 1]
            rt = x < w - 1 and m[y][x + 1]
            if (not up and not lf) or (not up and not rt) or (not dn and not lf) or (not dn and not rt):
                m[y][x] = "corner"
    for y in range(h):
        for x in range(w):
            if m[y][x] == "corner":
                m[y][x] = False
    return m, w, h


def stamp_text(c, text, x0, y0, ramp, shadow, scale=4, gap=4):
    m, w, h = text_mask(text, scale, gap)
    pts = [(x, y) for y in range(h) for x in range(w) if m[y][x]]
    # shadow (offset 3,3)
    for x, y in pts:
        for dx in range(0, 4):
            c.set(x0 + x + dx, y0 + y + 3, shadow)
    # thick outline
    for x, y in pts:
        for dx in (-2, -1, 0, 1, 2):
            for dy in (-2, -1, 0, 1, 2):
                if abs(dx) + abs(dy) <= 3:
                    c.set(x0 + x + dx, y0 + y + dy, OUT)
    for x, y in pts:
        f = y / (h - 1)
        if f < 0.18:
            col = ramp[0]
        elif f < 0.6:
            col = ramp[1]
        else:
            col = ramp[2]
        c.set(x0 + x, y0 + y, col)
    # top-left glints
    for x, y in pts:
        if (y % 28) in (1,) and (x % 4) == 1 and not m[y - 1][x]:
            c.set(x0 + x, y0 + y, WHITE)
    return w, h


def draw_logo():
    c = Canvas(320, 96)
    # mushroom mascot
    m = Canvas(84, 96)
    m.rect(26, 50, 55, 88, CREAM[1])
    m.rect(26, 50, 31, 88, CREAM[2])
    m.rect(48, 52, 52, 86, CREAM[0])
    m.hline(26, 55, 88, CREAM[2])
    m.rect(34, 60, 36, 66, BLACK); m.rect(45, 60, 47, 66, BLACK)
    m.set(34, 60, WHITE); m.set(45, 60, WHITE)
    m.rect(39, 70, 42, 71, RED[3]); m.hline(38, 43, 69, RED[3])
    m.rect(30, 67, 32, 68, PINK); m.rect(49, 67, 51, 68, PINK)
    m.shaded_ellipse(41, 50, 38, 34, RED, y_max=50)
    m.rect(3, 51, 79, 52, RED[2])
    m.rect(6, 53, 76, 54, RED[3])
    for sx, sy, w, h in ((14, 34, 9, 7), (34, 21, 12, 8), (58, 30, 9, 8), (24, 44, 6, 4), (66, 44, 7, 4), (48, 40, 6, 5)):
        m.rect(sx + 1, sy, sx + w - 2, sy + h - 1, CREAM[0])
        m.rect(sx, sy + 1, sx + w - 1, sy + h - 2, CREAM[0])
        m.hline(sx + 1, sx + w - 2, sy + h - 1, CREAM[2])
    # grass tuft at base
    for x in range(14, 68):
        hgt = 3 + ((x * 7) % 5)
        for y in range(91 - hgt, 92):
            m.set(x, y, GREEN[1] if (x + y) % 3 else GREEN[0])
    m.hline(14, 67, 91, GREEN[2])
    m.outline()
    m.outline()  # doubled outline for logo weight
    for y in range(96):
        for x in range(84):
            if m.px[y][x][3]:
                c.px[y][x + 2] = m.px[y][x]
    # words
    red_ramp = [hx("fff3c8"), hx("ffd23f"), hx("e89a1b")]
    green_ramp = [hx("d8ffa0"), GREEN[0], GREEN[1]]
    stamp_text(c, "MUSHROOM", 106, 10, red_ramp, RED[3])
    stamp_text(c, "MEADOW", 130, 54, green_ramp, GREEN[3])
    return c


# ---------------------------------------------------------------- resource writing
def frame_png_path(name, fid):
    return os.path.join(SPRITES, f"{name}.spritedata", "frames", f"{fid}.png")


def migrate_image(old, new):
    """Rename an image resource old -> new (contract amendment 1: unique names across kinds).
    Keeps the guid (meta displayName updated), moves the .spritedata folder, rewrites frame paths,
    and removes the old files. Idempotent: does nothing once the old files are gone."""
    oj = os.path.join(SPRITES, f"{old}.image.json")
    nj = os.path.join(SPRITES, f"{new}.image.json")
    om, nm = oj + ".meta", nj + ".meta"
    od = os.path.join(SPRITES, f"{old}.spritedata")
    nd = os.path.join(SPRITES, f"{new}.spritedata")
    if os.path.exists(om):
        if not os.path.exists(nm):
            with open(om, "r", encoding="utf-8") as f:
                meta = json.load(f)
            meta["displayName"] = new
            with open(nm, "w", encoding="utf-8", newline="\n") as f:
                json.dump(meta, f, indent=2)
                f.write("\n")
        os.remove(om)
    if os.path.exists(oj):
        if not os.path.exists(nj):
            with open(oj, "r", encoding="utf-8") as f:
                text = f.read()
            text = text.replace(f'"{old}.spritedata/', f'"{new}.spritedata/')
            with open(nj, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
        os.remove(oj)
    if os.path.isdir(od):
        if not os.path.isdir(nd):
            shutil.move(od, nd)
        else:
            shutil.rmtree(od)


# Phase-1 images renamed so no image shares a name with an object (Player rejects duplicate names).
RENAMES = {
    "Acorn Walker": "Acorn Walker Sprite",
    "Big Sun Coin": "Big Sun Coin Sprite",
    "Bounce Mushroom": "Bounce Mushroom Sprite",
    "Buzz Bee": "Buzz Bee Sprite",
    "Sun Coin": "Sun Coin Sprite",
    "Checkpoint": "Checkpoint Sprite",
}


def write_meta(name):
    path = os.path.join(SPRITES, f"{name}.image.json.meta")
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            meta = json.load(f)
        return meta["guid"]
    guid = uuid.UUID(stable_id("guid", name)).hex
    meta = {"guid": guid, "kind": "Image", "displayName": name,
            "createdUtc": "2026-10-11T00:00:00Z", "modifiedUtc": "2026-10-11T00:00:00Z"}
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(meta, f, indent=2)
        f.write("\n")
    return guid


def write_image(name, canvases, origin, mask, tags, durations, usage="sprite"):
    """tags: list of (tagName, startIndex, endIndex, loop)."""
    w, h = canvases[0].w, canvases[0].h
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
    guid = write_meta(name)
    return guid


def repaint_rig_image(name, canvas):
    """Keep the existing JSON (rig, ids, tags) and only replace the frame pixels + rig bind pixels."""
    path = os.path.join(SPRITES, f"{name}.image.json")
    with open(path, "r", encoding="utf-8") as f:
        doc = json.load(f)
    assert doc["canvas"]["width"] == canvas.w and doc["canvas"]["height"] == canvas.h
    frame = doc["frames"][0]
    frames_dir = os.path.join(SPRITES, f"{name}.spritedata", "frames")
    canvas.image().save(os.path.join(SPRITES, frame["source"].replace("/", os.sep)), compress_level=9)
    for fn in os.listdir(frames_dir):
        if fn != f"{frame['id']}.png":
            os.remove(os.path.join(frames_dir, fn))
    for rig in doc.get("pixelRigs", []):
        if rig.get("sourceFrameId") == frame["id"]:
            rig["bindPixels"] = base64.b64encode(canvas.rgba_bytes()).decode("ascii")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, indent=2)
        f.write("\n")


# ---------------------------------------------------------------- main
def main():
    for old, new in RENAMES.items():
        migrate_image(old, new)
    preview_rows = []
    idle, run, jump, fall, hurt = explorer_frames()
    E_MASK = (9, 6, 14, 34)
    write_image("Explorer Idle", idle, (16, 40), E_MASK, [("Idle", 0, 3, True)], [320, 220, 320, 160])
    write_image("Explorer Run", run, (16, 40), E_MASK, [("Run", 0, 5, True)], 80)
    write_image("Explorer Jump", jump, (16, 40), E_MASK, [("Jump", 0, 0, True)], 110)
    write_image("Explorer Fall", fall, (16, 40), E_MASK, [("Fall", 0, 0, True)], 110)
    write_image("Explorer Hurt", hurt, (16, 40), E_MASK, [("Hurt", 0, 0, True)], 110)
    preview_rows.append(idle + run + jump + fall + hurt)

    acorn = [draw_acorn(i) for i in range(4)]
    squash = [draw_acorn(squashed=True)]
    write_image("Acorn Walker Sprite", acorn, (16, 28), (3, 6, 26, 22), [("Walk", 0, 3, True)], 140)
    write_image("Acorn Squashed", squash, (16, 28), (3, 6, 26, 22), [("Squash", 0, 0, True)], 110)
    bee = [draw_bee(0), draw_bee(1)]
    write_image("Buzz Bee Sprite", bee, (16, 12), (6, 4, 20, 16), [("Fly", 0, 1, True)], 60)
    shroom = [draw_bounce(False), draw_bounce(True)]
    write_image("Bounce Mushroom Sprite", shroom, (16, 32), (2, 12, 28, 20),
                [("Idle", 0, 0, True), ("Bounce", 1, 1, False)], [1000, 140])
    preview_rows.append(acorn + squash + bee + shroom)

    coin = [draw_coin(16, 18, 6.5, p) for p in range(4)]
    big = [draw_coin(32, 32, 13.5, p) for p in range(4)]
    write_image("Sun Coin Sprite", coin, (8, 9), (2, 1, 12, 16), [("Spin", 0, 3, True)], 110)
    write_image("Big Sun Coin Sprite", big, (16, 16), (4, 4, 24, 24), [("Spin", 0, 3, True)], 110)
    heart, cicon = draw_heart(), draw_coin_icon()
    write_image("Heart Icon", [heart], (0, 0), (0, 0, 16, 16), [("Still", 0, 0, True)], 110)
    write_image("Coin Icon", [cicon], (0, 0), (0, 0, 16, 16), [("Still", 0, 0, True)], 110)
    cp, cpl = draw_checkpoint(False), draw_checkpoint(True)
    repaint_rig_image("Checkpoint Sprite", cp)
    repaint_rig_image("Checkpoint Lit", cpl)
    preview_rows.append(coin + big + [heart, cicon, cp, cpl])

    logo = draw_logo()
    write_image("Title Logo", [logo], (0, 0), (0, 0, 320, 96), [("Still", 0, 0, True)], 110)

    # contact sheet (4x)
    S = 4
    pad = 4
    row_imgs = []
    for row in preview_rows:
        rw = sum(cv.w for cv in row) + pad * (len(row) + 1)
        rh = max(cv.h for cv in row) + pad * 2
        row_imgs.append((row, rw, rh))
    sheet_w = max(max(r[1] for r in row_imgs), logo.w + pad * 2)
    sheet_h = sum(r[2] for r in row_imgs) + logo.h + pad * 2
    sheet = Image.new("RGBA", (sheet_w, sheet_h), (143, 211, 255, 255))
    y = 0
    for row, rw, rh in row_imgs:
        x = pad
        for cv in row:
            sheet.alpha_composite(cv.image(), (x, y + pad + (rh - pad * 2 - cv.h)))
            x += cv.w + pad
        y += rh
    sheet.alpha_composite(logo.image(), (pad, y + pad))
    sheet = sheet.resize((sheet_w * S, sheet_h * S), Image.NEAREST)
    os.makedirs(os.path.dirname(PREVIEW), exist_ok=True)
    sheet.save(PREVIEW)
    print("wrote", PREVIEW)


if __name__ == "__main__":
    main()
