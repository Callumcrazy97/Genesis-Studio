"""Mushroom Meadow world art generator (ART-WORLD).

Deterministic: re-running produces byte-identical PNGs and JSON.
Writes the Meadow Tiles tileset, the parallax backgrounds (Morning Sky, Far Hills,
Cloud Hills, Meadow Forest), Wayfarer Lodge and the Summit Shrine sprite into the
TwoDShowcase template, plus a 3x tileset contact sheet under TestResults.

Usage:  python BuildTools/TemplateContent/MushroomMeadow/world_art.py
"""
import hashlib
import json
import math
import os
import shutil
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[3]
SPRITES = ROOT / "Source" / "Genesis.Application.Core" / "Projects" / "Templates" / "Assets" / "TwoDShowcase" / "Assets" / "Sprites"
PREVIEW = ROOT / "TestResults" / "MushroomMeadow" / "tiles_preview.png"

CLEAR = (0, 0, 0, 0)
OUT = (42, 29, 46, 255)

# Palette ------------------------------------------------------------------
G_HI = (155, 225, 93, 255)
G_MID = (76, 175, 60, 255)
G_DK = (47, 125, 50, 255)
G_DEEP = (30, 84, 40, 255)
D_LT = (184, 135, 82, 255)
D_MID = (155, 106, 60, 255)
D_DK = (109, 69, 38, 255)
D_DEEP = (78, 48, 30, 255)
RED = (216, 67, 59, 255)
RED_HI = (244, 122, 100, 255)
RED_DK = (156, 40, 44, 255)
CREAM = (250, 236, 200, 255)
CREAM_DK = (214, 190, 150, 255)
GOLD = (255, 210, 63, 255)
GOLD_DK = (232, 154, 27, 255)
GOLD_HI = (255, 244, 170, 255)
BROWN = (120, 72, 40, 255)

BAYER4 = [[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]]


def bayer(x, y):
    return (BAYER4[y & 3][x & 3] + 0.5) / 16.0


def hsh(x, y, s=0):
    n = (x * 374761393 + y * 668265263 + s * 1442695041) & 0xFFFFFFFF
    n = ((n ^ (n >> 13)) * 1274126177) & 0xFFFFFFFF
    return ((n ^ (n >> 16)) & 0xFFFF) / 65535.0


def new(w, h):
    return Image.new("RGBA", (w, h), CLEAR)


def put(px, w, h, x, y, c):
    if 0 <= x < w and 0 <= y < h:
        px[x, y] = c


def rect(px, w, h, x0, y0, x1, y1, c):
    for y in range(max(0, y0), min(h, y1 + 1)):
        for x in range(max(0, x0), min(w, x1 + 1)):
            px[x, y] = c


def outline(img, color=OUT):
    """Add a 1px outline into transparent pixels 4-adjacent to opaque ones."""
    w, h = img.size
    px = img.load()
    add = []
    for y in range(h):
        for x in range(w):
            if px[x, y][3] != 0:
                continue
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < w and 0 <= ny < h and px[nx, ny][3] != 0 and px[nx, ny] != color:
                    add.append((x, y))
                    break
    for x, y in add:
        px[x, y] = color
    return img


def disc(px, w, h, cx, cy, r, shade):
    """Filled disc; shade(dx, dy, d) -> colour or None."""
    for y in range(int(cy - r - 1), int(cy + r + 2)):
        for x in range(int(cx - r - 1), int(cx + r + 2)):
            dx, dy = x + 0.5 - cx, y + 0.5 - cy
            d = math.hypot(dx, dy)
            if d <= r:
                c = shade(dx, dy, d)
                if c is not None:
                    put(px, w, h, x, y, c)


# Tiles ------------------------------------------------------------------
TS = 32


def dirt_pixel(x, y, seed=0, pal=(D_LT, D_MID, D_DK, D_DEEP)):
    lt, mid, dk, deep = pal
    n = hsh(x, y, 11 + seed)
    # soft horizontal strata, periodic in 32
    band = math.sin((y + 3 * math.sin(x * math.pi / 16)) * math.pi / 8)
    if n < 0.05:
        return deep
    if n < 0.14 or (band > 0.82 and n < 0.5):
        return dk
    if n > 0.95:
        return lt
    return mid


def pebble(px, cx, cy, big=False):
    shape = [(0, 0), (1, 0), (2, 0), (0, 1), (1, 1), (2, 1)] if big else [(0, 0), (1, 0), (0, 1), (1, 1)]
    for dx, dy in shape:
        px[cx + dx, cy + dy] = (196, 178, 160, 255)
    px[cx, cy] = (236, 224, 206, 255)
    for dx, dy in shape:
        if dy == max(s[1] for s in shape):
            px[cx + dx, cy + dy] = (140, 120, 108, 255)
    # shadow under
    for dx in range(3 if big else 2):
        y = cy + (2 if big else 2)
        if y < TS:
            px[cx + dx, y] = D_DEEP


def fill_dirt(px, seed=0, pal=None, pebbles=()):
    for y in range(TS):
        for x in range(TS):
            px[x, y] = dirt_pixel(x, y, seed) if pal is None else dirt_pixel(x, y, seed, pal)
    for (cx, cy, big) in pebbles:
        pebble(px, cx, cy, big)


DIRT_PEBBLES = [(6, 20, False), (21, 26, True)]


def grass_lip(x):
    return 9 + int(round(1.6 * math.sin(2 * math.pi * x * 2 / 32 + 0.6))) + (1 if hsh(x % 32, 0, 5) > 0.72 else 0)


def grass_top(px, left_cap=False, right_cap=False):
    fill_dirt(px, pebbles=[(22, 24, True)])
    for x in range(TS):
        lip = grass_lip(x)
        for y in range(lip + 1):
            if y == 0:
                c = OUT
            elif y <= 2:
                c = G_HI
            elif y == lip:
                c = G_DEEP
            elif y == lip - 1:
                c = G_DK
            else:
                n = hsh(x, y, 3)
                c = G_MID
                if y == 3 and n < 0.45:
                    c = G_HI
                elif n < 0.08:
                    c = G_DK
                elif n > 0.93:
                    c = G_HI
            px[x, y] = c
        # shadow just under the lip
        if lip + 1 < TS:
            px[x, lip + 1] = D_DK
    # little grass blade notches on the top highlight
    for x in (4, 13, 19, 27):
        px[x, 1] = G_MID
    if left_cap:
        cap_edge(px, mirror=False)
    if right_cap:
        cap_edge(px, mirror=True)


def cap_edge(px, mirror):
    def X(x):
        return TS - 1 - x if mirror else x
    # rounded corner
    for (x, y) in [(0, 0), (1, 0), (0, 1)]:
        px[X(x), y] = CLEAR
    for (x, y) in [(2, 0), (1, 1), (0, 2)]:
        px[X(x), y] = OUT
    # grass drapes down the side
    drape = 15
    for y in range(3, TS):
        px[X(0), y] = OUT
    for y in range(2, drape + 1):
        px[X(1), y] = G_HI if y < drape - 3 else G_DK
        px[X(2), y] = G_MID if y < drape - 1 else G_DEEP
        px[X(3), y] = G_MID if y < drape - 4 else (G_DEEP if y == drape - 2 else px[X(3), y])
    px[X(1), drape] = G_DEEP
    for y in range(drape + 1, TS):
        px[X(1), y] = D_DK
        if hsh(X(2), y, 9) < 0.5:
            px[X(2), y] = D_DK


def dirt_side(px, mirror):
    fill_dirt(px, pebbles=DIRT_PEBBLES if not mirror else [(10, 8, False), (18, 22, True)])
    for y in range(TS):
        x0 = TS - 1 if mirror else 0
        x1 = TS - 2 if mirror else 1
        x2 = TS - 3 if mirror else 2
        px[x0, y] = OUT
        px[x1, y] = D_DEEP if mirror else D_DK
        if hsh(x2, y, 4) < 0.35:
            px[x2, y] = D_DK


def bevel_block(px, base, hi, dk, out=OUT, bev=2):
    rect(px, TS, TS, 0, 0, 31, 31, base)
    for i in range(bev):
        rect(px, TS, TS, 1 + i, 1 + i, 30 - i, 1 + i, hi)
        rect(px, TS, TS, 1 + i, 1 + i, 1 + i, 30 - i, hi)
        rect(px, TS, TS, 1 + i, 30 - i, 30 - i, 30 - i, dk)
        rect(px, TS, TS, 30 - i, 1 + i, 30 - i, 30 - i, dk)
    for i in range(32):
        px[i, 0] = out
        px[i, 31] = out
        px[0, i] = out
        px[31, i] = out


def rivets(px, col_dk, col_hi):
    for (x, y) in [(4, 4), (26, 4), (4, 26), (26, 26)]:
        px[x, y] = col_hi
        px[x + 1, y] = col_dk
        px[x, y + 1] = col_dk
        px[x + 1, y + 1] = OUT


def blob(px, cx, cy, rx, ry, shade, w=TS, h=TS):
    """Filled ellipse; shade(nx, ny, d) -> colour or None (nx, ny normalised to -1..1, d radial 0..1)."""
    for y in range(int(cy - ry - 1), int(cy + ry + 2)):
        for x in range(int(cx - rx - 1), int(cx + rx + 2)):
            nx, ny = (x + 0.5 - cx) / rx, (y + 0.5 - cy) / ry
            d = math.hypot(nx, ny)
            if d <= 1.0:
                c = shade(nx, ny, d)
                if c is not None:
                    put(px, w, h, x, y, c)


def stamp(px, pattern, ox, oy, cols):
    """Paint a character pattern; cols maps characters to colours ('.' is skipped)."""
    for j, row in enumerate(pattern):
        for i, ch in enumerate(row):
            if ch in cols:
                put(px, TS, TS, ox + i, oy + j, cols[ch])


# Cracked mossy fieldstone (breakable block): irregular stones in dark mortar.
FIELD_STONES = [(7, 6, 0), (20, 5, 1), (28, 13, 2), (9, 16, 3), (21, 17, 0), (4, 26, 2), (16, 27, 1), (27, 26, 3)]
FIELD_PAL = [((196, 186, 164, 255), (172, 162, 142, 255), (124, 114, 100, 255)),
             ((186, 180, 168, 255), (160, 154, 144, 255), (112, 106, 100, 255)),
             ((204, 190, 160, 255), (178, 162, 134, 255), (128, 112, 92, 255)),
             ((180, 178, 160, 255), (154, 152, 134, 255), (108, 106, 92, 255))]
MORTAR = (74, 62, 56, 255)


def brick(px):
    fieldstone(px, FIELD_PAL, MORTAR, (G_HI, G_MID, G_DK), OUT)


def fieldstone(px, pal, mortar_col, moss, out):
    """Cracked fieldstone block: pal = four (hi, mid, dk) stone tints, moss = (hi, mid, dk) or None."""
    owner, mortar = {}, set()
    for y in range(TS):
        for x in range(TS):
            wx = x + 1.4 * math.sin(y * 0.55 + 1.0)
            wy = y + 1.2 * math.sin(x * 0.6)
            ds = sorted((math.hypot(wx - sx, (wy - sy) * 1.15), k) for k, (sx, sy, _) in enumerate(FIELD_STONES))
            owner[(x, y)] = ds[0][1]
            if ds[1][0] - ds[0][0] < 1.5:
                mortar.add((x, y))
    for y in range(TS):
        for x in range(TS):
            if (x, y) in mortar:
                px[x, y] = mortar_col
                continue
            hi, mid, dk = pal[FIELD_STONES[owner[(x, y)]][2]]
            c = mid
            if (x, y - 1) in mortar or (x - 1, y) in mortar:
                c = hi
            elif (x, y + 1) in mortar or (x + 1, y) in mortar or (x + 1, y + 1) in mortar:
                c = dk
            elif hsh(x, y, 21) < 0.07:
                c = dk
            elif hsh(x, y, 22) > 0.95:
                c = hi
            px[x, y] = c
    # a crack splitting two stones
    for (x, y) in [(18, 12), (17, 13), (17, 14), (16, 15), (15, 16), (15, 17), (14, 18), (14, 19), (13, 20)]:
        px[x, y] = mortar_col
        if px[x + 1, y] != mortar_col:
            px[x + 1, y] = pal[0][0]
    # moss creeping over the top and into the upper joints
    if moss is not None:
        m_hi, m_mid, m_dk = moss
        for x in range(TS):
            depth = 2 + (1 if hsh(x, 0, 23) > 0.55 else 0) + (2 if x in (5, 6, 18, 19, 20, 27) else 0)
            for y in range(1, depth + 1):
                px[x, y] = m_hi if y == 1 else (m_dk if y == depth else m_mid)
        for (x, y) in sorted(mortar):
            if y < 18 and hsh(x, y, 24) < 0.45:
                px[x, y] = m_dk if hsh(x, y, 25) < 0.6 else m_mid
    for i in range(32):
        px[i, 0] = out
        px[i, 31] = out
        px[0, i] = out
        px[31, i] = out


# Seed pod block (prize block) and its opened husk -------------------------
POD_FRAME = ((166, 114, 68, 255), (206, 156, 100, 255), (112, 72, 44, 255))
POD_RECESS = (52, 36, 34, 255)
POD_GLOW = ((255, 248, 196, 255), (238, 232, 112, 255), (176, 214, 76, 255), (104, 164, 58, 255), (40, 84, 40, 255))
SPROUT = [
    ".##...##.",
    "####.####",
    ".###s###.",
    "...#s#...",
    "....s....",
    "....s....",
]


def pod_frame(px, glow=True):
    """Mossy wooden frame with a dark recess; glow lights the recess around the pod."""
    base, hi, dk = POD_FRAME
    bevel_block(px, base, hi, dk)
    for y in range(3, 29):
        for x in range(3, 29):
            rail = y < 6 or y > 25
            if px[x, y] == base and (hsh(x // 4, y, 61) < 0.22 if rail else hsh(x, y // 4, 62) < 0.22):
                px[x, y] = dk
    for y in range(6, 26):
        for x in range(6, 26):
            c = POD_RECESS
            if glow:
                d = math.hypot(x + 0.5 - 16, y + 0.5 - 16.5)
                if d < 12.2 and bayer(x, y) < (12.2 - d) / 3.0:
                    c = (92, 88, 44, 255)
            px[x, y] = c
    for i in range(6, 26):
        px[i, 6] = OUT
        px[6, i] = OUT
        px[i, 25] = hi
        px[25, i] = hi
    # wooden pegs in the lower corners
    for (x, y) in [(3, 27), (27, 27)]:
        px[x, y] = hi
        px[x + 1, y] = base
        px[x, y + 1] = base
        px[x + 1, y + 1] = OUT
    # moss over the top rail with a few drips
    for x in range(1, 31):
        depth = 3 + (1 if hsh(x, 0, 63) > 0.6 else 0) + (2 if x in (4, 5, 12, 22, 23, 24) else 0)
        for y in range(1, depth + 1):
            px[x, y] = G_HI if y == 1 else (G_DK if y == depth else (G_MID if hsh(x, y, 64) > 0.15 else G_HI))
    for (x, y) in [(1, 20), (1, 21), (2, 21), (1, 22), (2, 22), (3, 22), (30, 14), (30, 15), (29, 15)]:
        px[x, y] = G_MID if y < 22 else G_DK


def question(px):
    """Closed seed pod: a glowing green-gold pod bulging out of the mossy frame."""
    pod_frame(px, glow=True)
    hi_w, gold, lime, green, rim = POD_GLOW

    def shade(nx, ny, d):
        if d > 0.9:
            return rim
        if abs(abs(nx) - 0.45) < 0.07 and d < 0.86:
            return green
        if (nx + 0.38) ** 2 + (ny + 0.45) ** 2 < 0.035:
            return hi_w
        v = d + 0.3 * (nx + ny)
        if v < 0.3:
            return hi_w
        if v < 0.62:
            return gold
        if v < 0.86:
            return lime
        return green
    blob(px, 16, 16.5, 9.6, 9.6, shade)
    # stalk where the pod hangs from the frame
    for (x, y, c) in [(16, 5, (96, 68, 40, 255)), (16, 6, (96, 68, 40, 255)), (16, 7, rim),
                      (17, 5, G_DK), (18, 5, G_MID), (19, 4, G_HI)]:
        px[x, y] = c
    stamp(px, SPROUT, 12, 13, {"#": (58, 132, 50, 255), "s": (36, 94, 42, 255)})
    # drifting spores
    for (x, y) in [(8, 8), (24, 9), (8, 24), (23, 24)]:
        px[x, y] = (255, 250, 170, 255)


def used_block(px):
    """Opened husk: the same frame, the pod split and empty, no glow."""
    pod_frame(px, glow=False)
    husk_hi, husk, husk_dk, rim = (214, 182, 118, 255), (172, 132, 80, 255), (122, 88, 54, 255), (70, 48, 34, 255)
    cavity, lining = (36, 24, 24, 255), (196, 170, 112, 255)

    def shade(nx, ny, d):
        if ny < -0.55 and abs(nx) < 0.22 + 0.5 * (-0.55 - ny):
            return None  # the split at the top where the pod burst
        de = math.hypot(nx / 0.6, (ny + 0.02) / 0.84)
        if de < 0.8:
            return (58, 40, 32, 255) if ny < -0.35 and bayer(int(nx * 40), int(ny * 40)) < 0.5 else cavity
        if de < 1.0:
            return lining
        if d > 0.9:
            return rim
        if nx < 0 and ny < 0:
            return husk_hi
        return husk if nx < 0.3 and ny < 0.4 else husk_dk
    blob(px, 16, 17, 9.6, 9.2, shade)
    # peeled tips curling outward over the split
    for (x, y) in [(11, 8), (10, 7), (20, 8), (21, 7)]:
        px[x, y] = husk_hi
    for (x, y) in [(15, 23), (16, 23)]:
        px[x, y] = (84, 60, 40, 255)


def stone_block(px):
    bevel_block(px, (148, 150, 168, 255), (204, 206, 222, 255), (98, 100, 124, 255), bev=3)
    crack = [(9, 6), (10, 7), (10, 8), (11, 9), (12, 9), (13, 10), (20, 22), (21, 21), (22, 21), (23, 20)]
    for x, y in crack:
        px[x, y] = (80, 82, 104, 255)
        px[x, y + 1] = (184, 186, 204, 255)
    for x in range(5, 27):
        for y in range(5, 27):
            if hsh(x, y, 41) < 0.04:
                px[x, y] = (124, 126, 146, 255)


# Hollow tree stump column (8/9 ring-cut top, 10/11 bark body) -------------
BARK_RAMP = ((188, 136, 86, 255), (156, 106, 64, 255), (124, 82, 50, 255), (92, 60, 38, 255), (64, 42, 30, 255),
             (44, 30, 24, 255))
STUMP_X0, STUMP_X1 = 3, 60
TAU_W = 2 * math.pi


def stump_bark(u, y, top):
    """Bark across the stump width (u in 0..63); y wraps every 32 so the body stacks seamlessly."""
    if u == STUMP_X0 or u == STUMP_X1:
        return OUT
    t = (u - STUMP_X0) / (STUMP_X1 - STUMP_X0)
    lvl = 2 if t < 0.06 else (0 if t < 0.28 else (1 if t < 0.6 else (2 if t < 0.84 else 3)))
    fx = u + int(round(1.4 * math.sin(TAU_W * y / 32 + u * 0.9)))
    if fx % 6 == 0:
        lvl += 2
    elif fx % 6 == 1 and hsh(u, y // 3, 71) < 0.7:
        lvl += 1
    elif fx % 6 == 3 and lvl > 0 and hsh(u, y, 72) < 0.5:
        lvl -= 1
    c = BARK_RAMP[min(lvl, 5)]
    if not top:
        # moss patch and a knot hole on the body only
        mx, my = (u - 10.5) / 6.5, (y - 23.5) / 5.0
        if math.hypot(mx, my) + 0.25 * hsh(u, y, 73) < 1.0:
            c = G_MID if my > -0.2 or hsh(u, y, 74) < 0.5 else G_HI
            if my > 0.6:
                c = G_DK
        kx, ky = (u - 43.5) / 2.6, (y - 10.5) / 3.4
        kd = math.hypot(kx, ky)
        if kd < 1.0:
            c = BARK_RAMP[5] if kd < 0.6 else BARK_RAMP[0]
        elif kd < 1.35:
            c = BARK_RAMP[3]
    return c


def pipe_tile(px, half, top):
    ring_lt, ring_dk = (232, 196, 140, 255), (190, 148, 96, 255)
    hollow_back, hollow_deep = (78, 52, 36, 255), (30, 20, 20, 255)
    cx, cy, rx, ry = 31.5, 7.0, 28.5, 6.6
    for y in range(TS):
        for x in range(TS):
            u = x + 32 * half
            if top and y <= 14:
                nx, ny = (u + 0.5 - cx) / rx, (y + 0.5 - cy) / ry
                d = math.hypot(nx, ny)
                if d > 1.0:
                    if y > cy and STUMP_X0 <= u <= STUMP_X1:
                        px[x, y] = stump_bark(u, y, True)
                    continue
                if d > 0.9:
                    c = OUT
                elif d > 0.76:
                    c = BARK_RAMP[0] if ny < 0 else BARK_RAMP[2]
                    if ny < -0.1 and hsh(u, y, 75) < 0.55:
                        c = G_MID if hsh(u, y, 76) < 0.7 else G_HI
                elif d > 0.58:
                    c = ring_dk if abs(d - 0.67) < 0.035 else ring_lt
                elif d > 0.5:
                    c = BARK_RAMP[4]
                else:
                    c = hollow_back if ny < -0.15 and bayer(u, y) < 0.6 else hollow_deep
                px[x, y] = c
            elif STUMP_X0 <= u <= STUMP_X1:
                px[x, y] = stump_bark(u, y, top)
    if top:
        # moss spilling over the front of the rim
        for (u0, n) in [(7, 3), (8, 5), (9, 4), (10, 2), (24, 2), (25, 3), (46, 4), (47, 6), (48, 3), (53, 2)]:
            if u0 // 32 != half:
                continue
            x = u0 - 32 * half
            for j in range(n):
                y = 13 + j
                px[x, y] = G_HI if j == 0 else (G_DK if j == n - 1 else G_MID)


def log_tile(px, part):
    bark_hi, bark, bark_dk = (176, 118, 70, 255), (138, 88, 50, 255), (96, 58, 34, 255)
    ring_lt, ring_dk = (232, 196, 140, 255), (186, 140, 90, 255)
    top, bot = 1, 15
    for x in range(TS):
        for y in range(top, bot + 1):
            if y == top or y == bot:
                c = OUT
            elif y == top + 1:
                c = bark_hi
            elif y >= bot - 2:
                c = bark_dk
            else:
                c = bark
                n = hsh(x, y, 51)
                if (y in (5, 10) and n < 0.7) or n < 0.05:
                    c = bark_dk
                elif y in (4, 9) and n < 0.5:
                    c = bark_hi
            px[x, y] = c
    # knots on mid
    if part == 1:
        for (x, y) in [(10, 7), (11, 7), (10, 8), (11, 8)]:
            px[x, y] = bark_dk
        px[10, 7] = OUT
    # cut ends with rings
    if part in (0, 2):
        cx = 7.5 if part == 0 else 24.5
        for y in range(top, bot + 1):
            for x in range(TS):
                dx, dy = (x + 0.5 - cx) / 6.5, (y + 0.5 - 8.5) / 7.5
                d = math.hypot(dx, dy)
                beyond = (x < cx) if part == 0 else (x > cx)
                if beyond and d > 1.0:
                    px[x, y] = CLEAR
                elif d <= 1.0:
                    if d > 0.86:
                        px[x, y] = OUT
                    elif d > 0.7:
                        px[x, y] = bark_dk
                    elif 0.36 < d < 0.48 or d < 0.12:
                        px[x, y] = ring_dk
                    else:
                        px[x, y] = ring_lt
    # little support shadow
    for x in range(TS):
        if px[x, bot][3] and 3 < x < 28 and part == 1 and x % 8 == 0:
            pass


def decor_tuft(img):
    px = img.load()
    blades = [(9, -1, 9), (12, 0, 13), (15, 1, 11), (18, 0, 14), (21, 1, 10), (23, 2, 7), (7, -2, 6)]
    for (bx, lean, hgt) in blades:
        for i in range(hgt):
            x = bx + int(round(lean * i / max(1, hgt)))
            y = 31 - i
            c = G_HI if i > hgt - 3 else (G_MID if i > 2 else G_DK)
            px[x, y] = c
            if i < hgt - 3:
                px[x + 1, y] = G_DK
    outline(img)


def decor_flowers(img):
    decor_tuft(img)
    px = img.load()
    for (cx, cy, petal, center) in [(9, 21, (255, 255, 250, 255), GOLD), (17, 17, (255, 160, 190, 255), (255, 240, 140, 255)), (24, 22, (255, 224, 80, 255), (214, 110, 30, 255))]:
        for x in range(cx, cx + 1):
            for y in range(cy + 2, 30):
                px[x, y] = G_DK
        for dx, dy in [(-1, 0), (1, 0), (0, -1), (0, 1)]:
            px[cx + dx, cy + dy] = petal
        px[cx, cy] = center
    outline(img)


def small_mushroom(px, cx, base, r, h):
    for y in range(base - h, base + 1):
        for x in range(cx - 1, cx + 2):
            px[x, y] = CREAM if x < cx + 1 else CREAM_DK
    cy = base - h
    for y in range(cy - r, cy + 2):
        for x in range(cx - r - 1, cx + r + 2):
            dx, dy = (x + 0.5 - (cx + 0.5)) / (r + 1.2), (y + 0.5 - (cy + 1)) / (r + 0.8)
            if dx * dx + dy * dy <= 1 and y <= cy + 1:
                c = RED
                if dx < -0.2 and dy < -0.3:
                    c = RED_HI
                elif y >= cy + 1:
                    c = RED_DK
                px[x, y] = c
    px[cx - 1, cy - r + 1] = CREAM
    px[cx + 2, cy - 1] = CREAM


def decor_mushrooms(img):
    px = img.load()
    small_mushroom(px, 11, 30, 4, 6)
    small_mushroom(px, 21, 30, 3, 4)
    for x in (5, 6, 26, 27):
        px[x, 30] = G_MID
        px[x, 29] = G_HI if x % 2 else G_MID
    outline(img)


def decor_bush(img):
    px = img.load()

    def sh(dx, dy, d):
        r = 1.0
        k = (dx * -0.6 + dy * -0.8) / max(d, 0.01)
        if d > 0 and k > 0.55 and d > 3:
            return G_HI
        return G_MID

    for (cx, cy, r) in [(10, 22, 8), (22, 22, 8), (16, 16, 9)]:
        disc(px, 32, 32, cx, cy, r, sh)
    for y in range(26, 32):
        for x in range(1, 31):
            if px[x, y][3]:
                px[x, y] = G_DK if y < 29 else G_DEEP
    rect(px, 32, 32, 2, 30, 29, 30, G_DEEP)
    for (x, y) in [(13, 13), (19, 17), (8, 20), (24, 21)]:
        px[x, y] = G_DK
    for (x, y) in [(10, 18), (21, 19)]:
        px[x, y] = (250, 250, 240, 255)
    outline(img)


def decor_fence(img):
    px = img.load()
    wood, wood_hi, wood_dk = (214, 168, 108, 255), (240, 204, 150, 255), (150, 104, 62, 255)
    for (y0, y1) in [(15, 17), (23, 25)]:
        for x in range(32):
            for y in range(y0, y1 + 1):
                px[x, y] = wood_hi if y == y0 else (wood_dk if y == y1 else wood)
    for px0 in (13,):
        for y in range(9, 32):
            for x in range(px0, px0 + 6):
                c = wood
                if x == px0:
                    c = wood_hi
                elif x == px0 + 5:
                    c = wood_dk
                px[x, y] = c
        # pointed top
        for x in range(px0, px0 + 6):
            px[x, 9] = CLEAR
        for x in range(px0 + 1, px0 + 5):
            px[x, 8] = wood_hi
        for x in range(px0 + 2, px0 + 4):
            px[x, 7] = wood_hi
        px[px0 + 2, 18] = wood_dk
        px[px0 + 2, 26] = wood_dk
    outline(img)


def decor_sign(img):
    px = img.load()
    wood, wood_hi, wood_dk = (214, 168, 108, 255), (240, 204, 150, 255), (150, 104, 62, 255)
    rect(px, 32, 32, 14, 16, 17, 31, wood_dk)
    rect(px, 32, 32, 14, 16, 14, 31, wood)
    rect(px, 32, 32, 4, 6, 27, 18, wood)
    rect(px, 32, 32, 4, 6, 27, 6, wood_hi)
    rect(px, 32, 32, 4, 18, 27, 18, wood_dk)
    for x in range(5, 27):
        if hsh(x, 12, 61) < 0.15:
            px[x, 12] = wood_dk
    # arrow
    arrow = (250, 250, 236, 255)
    rect(px, 32, 32, 8, 11, 18, 13, arrow)
    for i in range(5):
        for y in range(12 - (4 - i), 12 + (4 - i) + 1):
            px[19 + i, y] = arrow
    outline(img)
    # tidy: outline of arrow inside board
    for y in range(7, 18):
        for x in range(5, 27):
            if px[x, y] == arrow:
                for dx, dy in ((0, 1), (1, 1)):
                    if px[x + dx, y + dy] == wood:
                        px[x + dx, y + dy] = wood_dk


def decor_vine(img):
    px = img.load()
    for y in range(32):
        x = 16 + int(round(2.2 * math.sin(2 * math.pi * y / 32)))
        px[x, y] = G_DK
        px[x + 1, y] = G_MID
    for (y, side) in [(4, 1), (12, -1), (20, 1), (28, -1)]:
        x = 16 + int(round(2.2 * math.sin(2 * math.pi * y / 32)))
        for i in range(1, 5):
            xx = x + side * i + (1 if side > 0 else 0)
            px[xx, y + (1 if i > 2 else 0)] = G_HI if i < 3 else G_MID
            px[xx, y + 1 + (1 if i > 2 else 0)] = G_MID
    # little flower bud
    px[15, 24] = (255, 160, 190, 255)
    outline(img)
    # keep the stem continuous at the tile edges (no outline cap)
    for y in (0, 31):
        for x in range(32):
            if px[x, y] == OUT and y in (0, 31):
                pass


def decor_rock(img):
    px = img.load()
    hi, mid, dk = (200, 200, 214, 255), (150, 150, 166, 255), (102, 102, 122, 255)

    def sh(dx, dy, d):
        if dy > 3:
            return dk
        if dx < -2 and dy < -1:
            return hi
        return mid

    for y in range(18, 32):
        for x in range(4, 28):
            dx, dy = (x + 0.5 - 16) / 12, (y + 0.5 - 31) / 13
            if dx * dx + dy * dy <= 1:
                c = sh(x - 16, y - 25, 0)
                if (x - 12) ** 2 + (y - 22) ** 2 < 5:
                    c = hi
                px[x, y] = c
    for (x, y) in [(18, 23), (19, 24), (19, 25), (20, 26)]:
        px[x, y] = dk
    for x in range(8, 24):
        if px[x, 20][3] and hsh(x, 20, 71) < 0.5:
            px[x, 20] = G_MID
    outline(img)


def mushroom_cap(px, part):
    gill_top, gill_bot = 20, 25
    for x in range(TS):
        if part == 0:
            top = 14 - math.sqrt(max(0, 14 ** 2 - (14 - x - 0.5) ** 2)) if x < 14 else 0
            bot = gill_bot - (2 if x < 2 else (1 if x < 4 else 0))
        elif part == 2:
            xr = TS - 1 - x
            top = 14 - math.sqrt(max(0, 14 ** 2 - (14 - xr - 0.5) ** 2)) if xr < 14 else 0
            bot = gill_bot - (2 if xr < 2 else (1 if xr < 4 else 0))
        else:
            top, bot = 0, gill_bot
        top = int(round(top))
        for y in range(top, bot + 1):
            if y == top or y == bot:
                c = OUT
            elif y >= gill_top:
                c = CREAM_DK if (y == gill_top or (x % 3 == 0 and y < bot - 1)) else CREAM
                if y == gill_top:
                    c = RED_DK
            elif y == top + 1:
                c = RED_HI
            elif y >= gill_top - 3:
                c = RED_DK if (y >= gill_top - 2 or bayer(x, y) < 0.5) else RED
            else:
                c = RED
            px[x, y] = c
        # side outline for rounded parts
    for y in range(TS):
        for x in range(TS):
            if px[x, y][3] and px[x, y] != OUT:
                for dx in (-1, 1):
                    nx = x + dx
                    if 0 <= nx < TS and px[nx, y][3] == 0:
                        px[x, y] = OUT
    # spots
    spots = {0: [(12, 7, 3.2), (24, 13, 2.2), (6, 15, 1.6)], 1: [(9, 9, 4.2), (23, 5, 2.4), (21, 15, 1.8)], 2: [(18, 6, 3.4), (8, 13, 2.4), (25, 15, 1.6)]}[part]
    for (cx, cy, r) in spots:
        for y in range(TS):
            for x in range(TS):
                d = math.hypot(x + 0.5 - cx, y + 0.5 - cy)
                if d <= r and px[x, y] in (RED, RED_HI, RED_DK):
                    px[x, y] = CREAM_DK if (y + 0.5 - cy) > r * 0.45 else CREAM
    # cap highlight shine
    if part == 0:
        for (x, y) in [(5, 9), (6, 8), (7, 7), (4, 11)]:
            if px[x, y] == RED:
                px[x, y] = RED_HI


def mushroom_stem(px):
    for y in range(TS):
        for x in range(9, 23):
            if x in (9, 22):
                c = OUT
            elif x < 12:
                c = (255, 248, 226, 255)
            elif x > 18:
                c = CREAM_DK
            else:
                c = CREAM
                if hsh(x, y, 81) < 0.07:
                    c = CREAM_DK
            px[x, y] = c
    for y in range(4, 28, 8):
        px[15, y] = CREAM_DK
        px[15, y + 1] = CREAM_DK
    for y in range(TS):
        if bayer(20, y) < 0.5:
            px[18, y] = CREAM_DK


W_CREST = (232, 248, 255, 235)
W_HI = (140, 210, 246, 200)
W_BODY = (66, 146, 222, 176)
W_DEEP = (48, 112, 196, 186)


def water(px, surface):
    for y in range(TS):
        for x in range(TS):
            if surface:
                top = 6 + int(round(1.5 * math.sin(2 * math.pi * x / 16)))
                if y < top:
                    continue
                if y == top:
                    c = W_CREST
                elif y <= top + 2:
                    c = W_HI
                else:
                    c = W_BODY
            else:
                c = W_DEEP if hsh(x // 3, y // 2, 92) < 0.12 else W_BODY
            # shimmer streaks
            if c == W_BODY and hsh(x // 4, y, 91) < 0.09 and (x % 4) != 3:
                c = W_HI
            px[x, y] = c


def bramble(img):
    px = img.load()
    vine, vine_dk, thorn = (104, 66, 104, 255), (66, 40, 72, 255), (236, 222, 196, 255)
    leaf = (60, 110, 60, 255)
    for k, (amp, ph, y0) in enumerate([(5, 0.0, 22), (4, 1.7, 26), (3, 3.1, 18)]):
        for x in range(32):
            y = y0 + int(round(amp * math.sin(2 * math.pi * x / 32 * (k + 1) + ph)))
            y = max(9, min(31, y))
            px[x, y] = vine
            if y + 1 < 32:
                px[x, y + 1] = vine_dk
            if (x + k * 3) % 6 == 0:
                put(px, 32, 32, x, y - 1, thorn)
                put(px, 32, 32, x, y - 2, thorn)
            if (x + k * 5) % 11 == 0:
                put(px, 32, 32, x + 1, y - 1, leaf)
    for y in range(29, 32):
        for x in range(32):
            if not px[x, y][3]:
                px[x, y] = vine_dk if hsh(x, y, 95) < 0.6 else vine
    outline(img)


def crystal(px):
    base, hi, dk, deep = (126, 210, 246, 255), (226, 250, 255, 255), (70, 130, 214, 255), (88, 70, 170, 255)
    bevel_block(px, base, hi, deep, bev=3)
    # inner diamond facet: light upper-left faces, dark lower-right faces
    for y in range(4, 28):
        for x in range(4, 28):
            dx, dy = x + 0.5 - 16, y + 0.5 - 16
            if abs(dx) + abs(dy) <= 11:
                if abs(dx) + abs(dy) > 10:
                    c = deep
                elif dx <= 0 and dy <= 0:
                    c = hi
                elif dx > 0 and dy > 0:
                    c = dk
                else:
                    c = base
                px[x, y] = c
    for (x, y) in [(13, 9), (12, 10), (11, 11), (8, 6), (24, 24), (23, 25)]:
        px[x, y] = (255, 255, 255, 255)


def cave_wall(px):
    pal = ((112, 78, 64, 255), (82, 56, 48, 255), (62, 42, 38, 255), (44, 30, 32, 255))
    fill_dirt(px, seed=7, pal=pal)
    for (x, y) in [(8, 9), (9, 9), (22, 20), (23, 20), (24, 21)]:
        px[x, y] = (128, 96, 82, 255)


def lantern(img):
    px = img.load()
    wood, wood_dk = (150, 104, 62, 255), (104, 68, 42, 255)
    rect(px, 32, 32, 8, 6, 10, 31, wood)
    rect(px, 32, 32, 10, 6, 10, 31, wood_dk)
    rect(px, 32, 32, 8, 6, 22, 7, wood)
    px[20, 8] = (60, 60, 70, 255)
    metal = (70, 66, 80, 255)
    glow, glow_hi = (255, 200, 80, 255), (255, 246, 190, 255)
    rect(px, 32, 32, 17, 9, 23, 10, metal)
    rect(px, 32, 32, 17, 11, 23, 18, glow)
    rect(px, 32, 32, 19, 12, 21, 16, glow_hi)
    rect(px, 32, 32, 17, 11, 17, 18, metal)
    rect(px, 32, 32, 23, 11, 23, 18, metal)
    rect(px, 32, 32, 17, 19, 23, 19, metal)
    rect(px, 32, 32, 6, 30, 12, 31, wood_dk)
    outline(img)


def tall_flower(img):
    px = img.load()
    for y in range(9, 32):
        px[16, y] = G_DK
        px[15, y] = G_MID
    for (y, side) in [(20, -1), (25, 1)]:
        for i in range(1, 5):
            x = 16 + side * i if side > 0 else 15 + side * i
            px[x, y - (i // 2)] = G_MID
            px[x, y - (i // 2) + 1] = G_DK
    petals = (255, 214, 60, 255)
    petals_dk = (230, 150, 30, 255)
    for (dx, dy) in [(0, -5), (4, -3), (5, 1), (3, 4), (-1, 5), (-4, 3), (-5, -1), (-3, -4)]:
        for ox in range(-1, 2):
            for oy in range(-1, 2):
                if abs(ox) + abs(oy) < 2:
                    px[15 + dx + ox, 7 + dy + oy] = petals if dy < 2 else petals_dk
    disc(px, 32, 32, 15.5, 7.5, 3.0, lambda dx, dy, d: (120, 70, 40, 255) if dx + dy > 0 else (170, 104, 52, 255))
    outline(img)


def clover(img):
    px = img.load()
    for (cx, cy) in [(9, 24), (21, 22), (15, 27)]:
        for (dx, dy) in [(-2, 0), (2, 0), (0, -2)]:
            for ox in range(-1, 1):
                for oy in range(-1, 1):
                    px[cx + dx + ox, cy + dy + oy] = G_MID
            px[cx + dx - 1, cy + dy - 1] = G_HI
        for y in range(cy + 1, 32):
            px[cx, y] = G_DK
    outline(img)


def make_tileset():
    sheet = new(256, 160)
    builders = {}

    def reg(i):
        def deco(fn):
            builders[i] = fn
            return fn
        return deco

    def solid(fn):
        def run():
            t = new(TS, TS)
            fn(t.load())
            return t
        return run

    def deco(fn):
        def run():
            t = new(TS, TS)
            fn(t)
            return t
        return run

    builders[0] = solid(lambda p: grass_top(p))
    builders[1] = solid(lambda p: fill_dirt(p, pebbles=[(5, 7, False), (20, 18, False)]))
    builders[2] = solid(brick)
    builders[3] = solid(question)
    builders[4] = solid(used_block)
    builders[5] = solid(lambda p: grass_top(p, left_cap=True))
    builders[6] = solid(lambda p: grass_top(p, right_cap=True))
    builders[7] = solid(stone_block)
    builders[8] = solid(lambda p: pipe_tile(p, 0, True))
    builders[9] = solid(lambda p: pipe_tile(p, 1, True))
    builders[10] = solid(lambda p: pipe_tile(p, 0, False))
    builders[11] = solid(lambda p: pipe_tile(p, 1, False))
    builders[12] = solid(lambda p: log_tile(p, 0))
    builders[13] = solid(lambda p: log_tile(p, 1))
    builders[14] = solid(lambda p: log_tile(p, 2))
    builders[15] = solid(lambda p: fill_dirt(p, pebbles=[(4, 4, True), (14, 12, False), (24, 6, False), (9, 22, True), (22, 25, True), (27, 16, False)]))
    builders[16] = deco(decor_tuft)
    builders[17] = deco(decor_flowers)
    builders[18] = deco(decor_mushrooms)
    builders[19] = deco(decor_bush)
    builders[20] = deco(decor_fence)
    builders[21] = deco(decor_sign)
    builders[22] = deco(decor_vine)
    builders[23] = deco(decor_rock)
    builders[24] = solid(lambda p: dirt_side(p, False))
    builders[25] = solid(lambda p: dirt_side(p, True))
    builders[26] = solid(lambda p: mushroom_cap(p, 0))
    builders[27] = solid(lambda p: mushroom_cap(p, 1))
    builders[28] = solid(lambda p: mushroom_cap(p, 2))
    builders[29] = solid(mushroom_stem)
    builders[30] = solid(lambda p: water(p, True))
    builders[31] = solid(lambda p: water(p, False))
    builders[32] = deco(bramble)
    builders[33] = solid(crystal)
    builders[34] = solid(cave_wall)
    builders[35] = deco(lantern)
    builders[36] = deco(tall_flower)
    builders[37] = deco(clover)

    def bottom_edge(p):
        fill_dirt(p, pebbles=[(8, 8, False), (20, 14, True)])
        for x in range(TS):
            p[x, 31] = OUT
            p[x, 30] = D_DEEP
            if hsh(x, 29, 3) < 0.5:
                p[x, 29] = D_DK
    builders[38] = solid(bottom_edge)
    builders[39] = lambda: new(TS, TS)

    for i in range(40):
        t = builders[i]()
        sheet.paste(t, ((i % 8) * TS, (i // 8) * TS))
    return sheet


SOLID = list(range(0, 16)) + [24, 25, 26, 27, 28, 33, 34, 38]


# Backgrounds --------------------------------------------------------------
def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def morning_sky():
    W, H = 640, 360
    img = Image.new("RGBA", (W, H))
    px = img.load()
    top, bot = (143, 211, 255, 255), (216, 243, 255, 255)
    steps = 7
    ramp = [lerp(top, bot, i / (steps - 1)) for i in range(steps)]
    sun_x, sun_y = 506, 74
    for y in range(H):
        t = (y / (H - 1)) ** 0.85
        level = t * (steps - 1)
        for x in range(W):
            i = int(level + bayer(x, y) - 0.5 + 0.5)
            i = max(0, min(steps - 1, int(level) + (1 if (level - int(level)) > bayer(x, y) else 0)))
            c = ramp[i]
            d = math.hypot(x - sun_x, (y - sun_y) * 1.0)
            if d < 18:
                c = (255, 252, 226, 255) if d < 15 else (255, 240, 180, 255)
            elif d < 30:
                if bayer(x, y) < 0.5:
                    c = lerp(c, (255, 250, 220, 255), 0.55)
            elif d < 48:
                if bayer(x, y) < 0.25:
                    c = lerp(c, (255, 250, 230, 255), 0.4)
            px[x, y] = c
    # faint wisps (tileable: wrap x)
    for (cx, cy, rw, rh) in [(90, 60, 70, 7), (150, 70, 40, 5), (300, 120, 90, 6), (420, 40, 50, 4), (600, 150, 60, 5), (230, 190, 70, 5)]:
        for y in range(cy - rh, cy + rh + 1):
            for xx in range(cx - rw, cx + rw + 1):
                x = xx % W
                dx, dy = (xx - cx) / rw, (y - cy) / rh
                v = dx * dx + dy * dy
                if v <= 1:
                    thresh = 0.75 if v < 0.45 else 0.35
                    if bayer(x, y) < thresh:
                        px[x, y] = lerp(px[x, y], (255, 255, 255, 255), 0.5)
    return img


def hill_height(x, base, terms, W):
    return base + sum(a * math.sin(2 * math.pi * k * x / W + p) for (a, k, p) in terms)


def far_hills():
    W, H = 1024, 360
    img = new(W, H)
    px = img.load()
    back_terms = [(26, 2, 0.3), (13, 5, 1.1), (6, 11, 2.0), (3, 23, 0.7)]
    front_terms = [(18, 3, 2.0), (10, 7, 0.5), (5, 13, 1.4)]
    back = (164, 208, 214, 255)
    back_rim = (204, 234, 236, 255)
    back_sh = (146, 192, 204, 255)
    front = (124, 184, 176, 255)
    front_rim = (160, 210, 196, 255)
    front_sh = (104, 162, 160, 255)
    for x in range(W):
        hb = int(round(hill_height(x, 196, back_terms, W)))
        hf = int(round(hill_height(x, 246, front_terms, W)))
        for y in range(hb, H):
            c = back
            if y <= hb + 1:
                c = back_rim
            elif y > hb + 26 and bayer(x, y) < min(0.75, (y - hb - 26) / 60):
                c = back_sh
            px[x, y] = c
        for y in range(hf, H):
            c = front
            if y <= hf + 1:
                c = front_rim
            elif y > hf + 30 and bayer(x, y) < min(0.8, (y - hf - 30) / 50):
                c = front_sh
            px[x, y] = c
    # distant tree clumps on the front range
    k = 0
    for x0 in range(6, W, 23):
        if hsh(x0, 1, 101) < 0.45:
            continue
        hf = hill_height(x0, 246, front_terms, W)
        r = 3 + int(hsh(x0, 2, 102) * 3)
        cy = hf + 2 + hsh(x0, 3, 103) * 14
        for y in range(int(cy - r - 2), int(cy + r + 1)):
            for xx in range(x0 - r - 1, x0 + r + 2):
                d = math.hypot(xx - x0, (y - cy) * 1.2)
                if d <= r:
                    x = xx % W
                    px[x, y] = front_sh if (xx - x0) + (y - cy) > -1 else front_rim
        k += 1
    return img


def cloud_hills():
    W, H = 1024, 360
    img = new(W, H)
    px = img.load()
    # clouds: list of (cx, cy, puffs)
    c_white = (255, 255, 255, 255)
    c_sh = (214, 236, 252, 255)
    c_sh2 = (182, 216, 244, 255)
    c_out = (140, 182, 226, 255)
    clouds = [(110, 92, 1.0), (380, 54, 0.8), (620, 112, 1.2), (850, 70, 0.9)]
    for ci, (cx, cy, s) in enumerate(clouds):
        puffs = [(-38, 6, 16), (-18, -4, 22), (6, -12, 26), (30, -2, 20), (50, 8, 14), (-2, 10, 18), (24, 12, 16)]
        puffs = [(cx + dx * s, cy + dy * s, r * s) for (dx, dy, r) in puffs]
        flat = cy + 14 * s
        minx = int(min(p[0] - p[2] for p in puffs)) - 2
        maxx = int(max(p[0] + p[2] for p in puffs)) + 2
        miny = int(min(p[1] - p[2] for p in puffs)) - 2
        maxy = int(flat) + 2
        for y in range(miny, maxy + 1):
            for xx in range(minx, maxx + 1):
                inside = False
                edge = False
                lower = 0.0
                for (pxc, pyc, r) in puffs:
                    d = math.hypot(xx + 0.5 - pxc, y + 0.5 - pyc)
                    if d <= r:
                        inside = True
                        lower = max(lower, (y - pyc) / r)
                if not inside or y > flat:
                    continue
                # edge test
                for (dx, dy) in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, ny = xx + dx + 0.5, y + dy + 0.5
                    if ny > flat + 0.5 or not any(math.hypot(nx - a, ny - b) <= r for (a, b, r) in puffs):
                        edge = True
                        break
                x = xx % W
                if edge:
                    c = c_out
                elif y > flat - 3:
                    c = c_sh2
                elif lower > 0.45 or y > flat - 8:
                    c = c_sh if bayer(x, y) < 0.75 or y > flat - 6 else c_white
                else:
                    c = c_white
                px[x, y] = c
    # rounded green hills (domes), back to front
    h_out = (38, 96, 52, 255)
    domes = [
        (70, 120, 120, (118, 196, 96, 255), (156, 220, 120, 255), (88, 160, 76, 255)),
        (300, 150, 92, (118, 196, 96, 255), (156, 220, 120, 255), (88, 160, 76, 255)),
        (520, 110, 128, (118, 196, 96, 255), (156, 220, 120, 255), (88, 160, 76, 255)),
        (760, 160, 100, (118, 196, 96, 255), (156, 220, 120, 255), (88, 160, 76, 255)),
        (930, 100, 80, (118, 196, 96, 255), (156, 220, 120, 255), (88, 160, 76, 255)),
        (190, 90, 66, (96, 182, 82, 255), (140, 212, 106, 255), (70, 146, 66, 255)),
        (420, 80, 58, (96, 182, 82, 255), (140, 212, 106, 255), (70, 146, 66, 255)),
        (650, 96, 70, (96, 182, 82, 255), (140, 212, 106, 255), (70, 146, 66, 255)),
        (880, 84, 52, (96, 182, 82, 255), (140, 212, 106, 255), (70, 146, 66, 255)),
    ]
    base = 360
    for (cx, hw, hh, mid, hi, dk) in domes:
        for xx in range(cx - hw - 1, cx + hw + 2):
            u = (xx + 0.5 - cx) / hw
            if abs(u) > 1:
                continue
            top = base - hh * math.sqrt(1 - u * u) - 18
            x = xx % W
            ty = int(round(top))
            for y in range(max(0, ty), H):
                if y == ty:
                    c = h_out
                elif u < -0.35 and y < ty + 1 + (1 - abs(u)) * 30:
                    c = hi if bayer(x, y) < 0.8 else mid
                elif u > 0.45:
                    c = dk if bayer(x, y) < min(1.0, (u - 0.45) * 4) else mid
                else:
                    c = mid
                px[x, y] = c
            # side outline where dome edge is steep
        for xx in (cx - hw - 1, cx + hw + 1):
            pass
        # little vertical dash texture
        for k in range(6):
            dx = int((hsh(cx, k, 111) - 0.5) * hw * 1.2)
            u = dx / hw
            top = base - hh * math.sqrt(max(0, 1 - u * u)) - 18
            dy = int(top + 14 + hsh(cx, k, 112) * (hh * 0.6))
            for j in range(4):
                put(px, W, H, (cx + dx) % W, dy + j, dk)
                put(px, W, H, (cx + dx + 1) % W, dy + j, mid if j == 0 else dk)
    # outline steep dome sides: any coloured pixel with transparent left/right neighbour
    edge = []
    for y in range(H):
        for x in range(W):
            if px[x, y][3] and y > 180:
                if px[(x - 1) % W, y][3] == 0 or px[(x + 1) % W, y][3] == 0:
                    edge.append((x, y))
    for (x, y) in edge:
        px[x, y] = h_out
    return img


def meadow_forest():
    W, H = 1024, 360
    img = new(W, H)
    px = img.load()
    trunk, trunk_dk, trunk_hi = (104, 70, 48, 255), (70, 46, 34, 255), (140, 98, 64, 255)
    out = (22, 48, 34, 255)
    mid, hi, dk = (52, 132, 60, 255), (96, 172, 72, 255), (32, 92, 50, 255)
    trees = []
    x = 20
    i = 0
    while x < W - 10:
        r = 22 + int(hsh(i, 1, 121) * 12)
        cy = 258 + int(hsh(i, 2, 122) * 30)
        trees.append((x, cy, r, i))
        x += int(r * 1.7 + hsh(i, 3, 123) * 44)
        i += 1
    # trunks
    for (cx, cy, r, i) in trees:
        tw = 5 + int(r / 8)
        for y in range(cy, H):
            for xx in range(cx - tw, cx + tw + 1):
                xw = xx % W
                if xx == cx - tw or xx == cx + tw:
                    c = out
                elif xx < cx - tw + 3:
                    c = trunk_hi
                elif xx > cx + tw - 3:
                    c = trunk_dk
                else:
                    c = trunk
                    if hsh(xx, y // 3, 124) < 0.08:
                        c = trunk_dk
                px[xw, y] = c
    # canopies: clusters of discs with scalloped outline
    for (cx, cy, r, i) in trees:
        puffs = [(0, 0, r), (-r * 0.62, r * 0.26, r * 0.66), (r * 0.62, r * 0.22, r * 0.68), (-r * 0.25, -r * 0.55, r * 0.6), (r * 0.32, -r * 0.48, r * 0.58)]
        for (dx, dy, pr) in puffs:
            pcx, pcy = cx + dx, cy + dy
            for y in range(int(pcy - pr - 1), int(pcy + pr + 2)):
                for xx in range(int(pcx - pr - 1), int(pcx + pr + 2)):
                    ddx, ddy = xx + 0.5 - pcx, y + 0.5 - pcy
                    d = math.hypot(ddx, ddy)
                    if d > pr:
                        continue
                    xw = xx % W
                    if d > pr - 1.3:
                        c = out
                    else:
                        k = (-ddx * 0.55 - ddy * 0.83) / max(pr, 1)
                        if k > 0.42:
                            c = hi if (k > 0.55 or bayer(xw, y) < 0.6) else mid
                        elif k < -0.30:
                            c = dk if (k < -0.45 or bayer(xw, y) < 0.6) else mid
                        else:
                            c = mid
                        if c == mid and hsh(xw // 2, y // 2, 125 + i) < 0.06:
                            c = dk
                    if 0 <= y < H:
                        px[xw, y] = c
    # front hedge band
    g_mid, g_hi, g_dk = (70, 160, 62, 255), (126, 200, 86, 255), (44, 112, 52, 255)
    hx = 0
    j = 0
    while hx < W + 20:
        r = 16 + int(hsh(j, 4, 126) * 10)
        cy = 322 + int(hsh(j, 5, 127) * 10)
        for y in range(cy - r - 1, H):
            for xx in range(hx - r - 1, hx + r + 2):
                ddx, ddy = xx + 0.5 - hx, y + 0.5 - cy
                d = math.hypot(ddx, ddy)
                if y < cy and d > r:
                    continue
                if y >= cy and abs(ddx) > r:
                    continue
                xw = xx % W
                if y < cy and d > r - 1.3:
                    c = out
                else:
                    k = (-ddx * 0.5 - ddy * 0.86) / r
                    c = g_hi if k > 0.5 else (g_dk if (y > cy + 8 or k < -0.4) else g_mid)
                    if c == g_mid and bayer(xw, y) < 0.2 and y > cy + 4:
                        c = g_dk
                if y < H:
                    px[xw, y] = c
        hx += int(r * 1.3)
        j += 1
    # tiny flowers in hedge
    for k in range(70):
        x = int(hsh(k, 6, 128) * W)
        y = 318 + int(hsh(k, 7, 129) * 36)
        if px[x, y][3] and px[x, y] != out:
            px[x, y] = (255, 246, 214, 255) if k % 3 else (255, 170, 200, 255)
    return img


def wayfarer_lodge():
    W, H = 160, 144
    img = new(W, H)
    px = img.load()
    log, log_hi, log_dk = (178, 116, 66, 255), (214, 154, 98, 255), (124, 76, 44, 255)
    stone, stone_hi, stone_dk = (150, 146, 156, 255), (196, 192, 200, 255), (100, 96, 110, 255)
    roof, roof_hi, roof_dk = (196, 70, 56, 255), (232, 116, 90, 255), (136, 44, 44, 255)
    # chimney
    rect(px, W, H, 112, 10, 127, 46, stone)
    for y in range(10, 47):
        for x in range(112, 128):
            if (y - 10) % 6 == 5 or ((x - 112 + (4 if ((y - 10) // 6) % 2 else 0)) % 8 == 7):
                px[x, y] = stone_dk
            elif (y - 10) % 6 == 0:
                px[x, y] = stone_hi
    rect(px, W, H, 110, 8, 129, 11, stone_hi)
    # walls (logs)
    for y in range(70, 128):
        row = (y - 70) // 7
        ly = (y - 70) % 7
        for x in range(14, 146):
            if ly == 6:
                c = log_dk
            elif ly == 0:
                c = log_hi
            else:
                c = log
                if hsh(x // 3, y, 141) < 0.07:
                    c = log_dk
            px[x, y] = c
        # log ends at corners
        for cx in (13, 146):
            for x in range(cx - 3, cx + 4):
                d = math.hypot(x + 0.5 - (cx + 0.5), (ly + 0.5 - 3.5))
                if d <= 3.6:
                    px[x, y] = (230, 196, 140, 255) if d < 2.4 else log_dk
    # roof: trapezoid with shingles
    for y in range(18, 78):
        t = (y - 18) / 59
        x0 = int(round(26 - 26 * t))
        x1 = int(round(133 + 26 * t))
        for x in range(x0, x1 + 1):
            ry = (y - 18) % 6
            off = 5 if ((y - 18) // 6) % 2 else 0
            if ry == 5:
                c = roof_dk
            elif ry == 0:
                c = roof_hi
            else:
                c = roof
                if (x + off) % 10 == 0:
                    c = roof_dk
            px[x, y] = c
    # roof ridge and eave shadow
    rect(px, W, H, 24, 16, 135, 18, (110, 36, 40, 255))
    rect(px, W, H, 14, 78, 145, 80, (90, 56, 40, 255))
    # windows
    for wx in (28, 108):
        rect(px, W, H, wx - 2, 88, wx + 25, 111, (110, 70, 40, 255))
        rect(px, W, H, wx, 90, wx + 23, 109, (255, 214, 110, 255))
        rect(px, W, H, wx + 2, 92, wx + 9, 99, (255, 244, 196, 255))
        rect(px, W, H, wx + 11, 90, wx + 12, 109, (110, 70, 40, 255))
        rect(px, W, H, wx, 99, wx + 23, 100, (110, 70, 40, 255))
        # flower box
        rect(px, W, H, wx - 3, 112, wx + 26, 117, (140, 90, 50, 255))
        rect(px, W, H, wx - 3, 112, wx + 26, 112, (180, 122, 70, 255))
        for k in range(0, 29, 3):
            put(px, W, H, wx - 2 + k, 111, G_MID)
            put(px, W, H, wx - 2 + k, 110, (255, 120, 150, 255) if k % 2 else (255, 230, 90, 255))
            put(px, W, H, wx - 1 + k, 111, G_DK)
    # door with arch
    for y in range(90, 128):
        for x in range(68, 92):
            dx = x + 0.5 - 80
            if y < 102 and math.hypot(dx, (y + 0.5 - 102) * 1.0) > 12:
                continue
            c = (130, 80, 46, 255) if (x - 68) % 6 else (96, 58, 34, 255)
            if x in (68, 91):
                c = (80, 48, 30, 255)
            px[x, y] = c
    rect(px, W, H, 86, 108, 87, 110, GOLD)
    # hanging sign
    rect(px, W, H, 70, 82, 89, 87, (230, 196, 140, 255))
    rect(px, W, H, 70, 87, 89, 87, (170, 124, 74, 255))
    for x in (74, 77, 80, 83, 86):
        px[x, 84] = (110, 70, 40, 255)
        px[x, 85] = (110, 70, 40, 255)
    # stone foundation
    for y in range(128, 144):
        for x in range(6, 154):
            sy = (y - 128) % 8
            off = 6 if ((y - 128) // 8) % 2 else 0
            if sy == 7 or (x + off) % 12 == 11:
                c = stone_dk
            elif sy == 0 or (x + off) % 12 == 0:
                c = stone_hi
            else:
                c = stone
            px[x, y] = c
    # steps
    rect(px, W, H, 64, 128, 95, 135, (170, 166, 176, 255))
    rect(px, W, H, 64, 128, 95, 128, (210, 206, 214, 255))
    outline(img)
    return img


SHRINE_WOOD = ((214, 162, 106, 255), (178, 124, 76, 255), (140, 92, 56, 255), (100, 64, 42, 255), (68, 44, 32, 255))
SHRINE_CARVING = [
    "..####..",
    ".######.",
    "########",
    "...##...",
    "...##...",
    "..####..",
]


def summit_shrine():
    """Lantern shrine at the level's end: a carved post under a mushroom-cap roof with the
    Glowspore's lantern cradle hanging beneath it."""
    W, H = 64, 192
    img = new(W, H)
    px = img.load()
    lt, hi, mid, dk, deep = SHRINE_WOOD
    # carved post (x 27..36) with a flared foot
    for y in range(18, 178):
        flare = 2 if y >= 168 else (1 if y >= 164 else 0)
        x0, x1 = 27 - flare, 36 + flare
        for x in range(x0, x1 + 1):
            t = (x - x0) / (x1 - x0)
            c = hi if t < 0.3 else (mid if t < 0.7 else dk)
            if (x * 7 + y // 9) % 5 == 0 and hsh(x, y // 3, 81) < 0.6:
                c = dk if c != dk else deep
            if x in (x0, x1):
                c = OUT
            px[x, y] = c
    # mushroom carvings down the post
    for cy in (84, 116, 148):
        for j, row in enumerate(SHRINE_CARVING):
            for i, ch in enumerate(row):
                x, y = 28 + i, cy + j
                if ch == "#":
                    if j < 3:
                        c = RED_HI if (i < 3 and j < 2) else (RED if i < 6 else RED_DK)
                        if (i, j) in ((2, 1), (5, 2)):
                            c = CREAM
                    else:
                        c = CREAM if i < 4 else CREAM_DK
                    px[x, y] = c
                elif ch == "." and (j < 3 or 2 <= i <= 5):
                    px[x, y] = deep
    # vine spiralling round the post (front half only)
    for y in range(62, 172):
        a = y / 7.5
        if math.cos(a) > 0:
            x = int(round(31.5 + 5.6 * math.sin(a)))
            if 27 < x < 36:
                px[x, y] = G_MID if math.sin(a) < 0.3 else G_DK
                if y % 13 == 0:
                    put(px, W, H, x + 1, y - 1, G_HI)
                    put(px, W, H, x + 2, y - 1, G_MID)
    # cross beam with rounded ends (y 56..61)
    for y in range(56, 62):
        for x in range(5, 59):
            if (x in (5, 58) and y in (56, 61)):
                continue
            c = hi if y <= 57 else (mid if y <= 59 else dk)
            if hsh(x // 5, y, 82) < 0.2:
                c = dk
            px[x, y] = c
    for x in range(5, 59):
        if hsh(x, 0, 83) < 0.55:
            px[x, 56] = G_MID if hsh(x, 1, 83) < 0.6 else G_HI
    # small spore lanterns hanging from the beam ends
    for lx in (10, 53):
        for y in range(62, 66):
            px[lx, y] = deep
        for y in range(66, 76):
            for x in range(lx - 3, lx + 4):
                edge = x in (lx - 3, lx + 3) or y in (66, 75)
                px[x, y] = dk if edge else ((255, 244, 170, 255) if y < 70 and x <= lx else (232, 214, 96, 255))
        px[lx, 76] = deep
    # mushroom-cap roof (x 3..60, y 4..20) with cream gills and spots
    for x in range(3, 61):
        t = (x + 0.5 - 32) / 28.5
        top = int(round(20 - 15 * math.sqrt(max(0.0, 1 - t * t)) ** 0.8))
        for y in range(top, 21):
            if y >= 18:
                c = CREAM_DK if (x % 3 == 0 or y == 20) else CREAM
            else:
                c = RED_HI if (t < -0.2 and y < top + 4) else (RED_DK if (t > 0.45 or y >= 16) else RED)
            px[x, y] = c
    for (sx, sy, r) in [(18, 12, 2.6), (33, 8, 3.2), (47, 13, 2.4), (10, 16, 1.6), (54, 16, 1.4), (26, 15, 1.5)]:
        disc(px, W, H, sx, sy, r, lambda dx, dy, d: CREAM if dx + dy < 1 else CREAM_DK)
    # sprout finial on the roof
    for (x, y, c) in [(31, 4, G_DK), (32, 4, G_DK), (31, 3, G_DK), (32, 2, G_MID), (33, 1, G_HI), (34, 1, G_HI),
                      (30, 2, G_MID), (29, 1, G_HI), (28, 1, G_MID)]:
        px[x, y] = c
    # lantern cradle hanging from the roof (y 21..52), drawn over the post
    for y in range(21, 25):
        px[31, y] = deep
        px[32, y] = dk
    for y in range(25, 29):
        for x in range(21 + (28 - y), 43 - (28 - y)):
            px[x, y] = hi if y == 25 else (mid if y < 28 else dk)
    glow_hi, glow_mid, glow_lo = (255, 252, 214, 255), (240, 236, 130, 255), (170, 214, 86, 255)
    for y in range(29, 47):
        for x in range(22, 42):
            if x in (22, 27, 36, 41):
                px[x, y] = dk if x in (22, 27) else deep
                continue
            d = math.hypot(x + 0.5 - 32, (y + 0.5 - 38) * 1.1)
            px[x, y] = glow_mid if d < 9 else glow_lo
    disc(px, W, H, 32.0, 38.0, 6.4, lambda dx, dy, d: glow_hi if (d < 3.4 or (dx < -1 and dy < -1)) else (POD_GLOW[1] if d < 5.4 else POD_GLOW[2]))
    for y in range(47, 51):
        for x in range(20 + (y - 47), 44 - (y - 47)):
            px[x, y] = mid if y < 49 else dk
    for (x, y) in [(31, 51), (32, 51), (31, 52), (32, 53)]:
        px[x, y] = deep
    # stone steps
    stone, stone_hi, stone_dk = (150, 146, 138, 255), (196, 190, 178, 255), (104, 100, 96, 255)
    for (x0, x1, y0, y1) in [(19, 44, 176, 183), (12, 51, 184, 191)]:
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                c = stone_hi if y == y0 else (stone_dk if (y == y1 or x == x1) else stone)
                if hsh(x, y, 84) < 0.06:
                    c = stone_dk
                if (x - x0) % 9 == 8 and y > y0:
                    c = stone_dk
                px[x, y] = c
        for x in range(x0, x1 + 1):
            if hsh(x, y0, 85) < 0.3:
                px[x, y0] = G_MID
    # post foot over the upper step
    for y in range(172, 178):
        for x in range(25, 39):
            px[x, y] = OUT if x in (25, 38) else (hi if x < 29 else (mid if x < 34 else dk))
    outline(img)
    # soft light round the lanterns (after the outline so it stays translucent)
    halo_px(img, 32, 38, 16, (255, 246, 160))
    halo_px(img, 10, 71, 7, (255, 236, 140))
    halo_px(img, 53, 71, 7, (255, 236, 140))
    return img


def halo_px(img, cx, cy, r, col):
    px = img.load()
    w, h = img.size
    for y in range(int(cy - r), int(cy + r) + 1):
        for x in range(int(cx - r), int(cx + r) + 1):
            if 0 <= x < w and 0 <= y < h and px[x, y][3] == 0:
                d = math.hypot(x + 0.5 - cx, y + 0.5 - cy)
                if d <= r:
                    px[x, y] = (col[0], col[1], col[2], 70 if d < r * 0.6 else 34)


# Resource writing ----------------------------------------------------------
def det_id(*parts):
    return hashlib.md5(("MushroomMeadow/" + "/".join(parts)).encode("utf-8")).hexdigest()


def remove_image(name):
    """Delete an Image resource that a rename replaced (json, meta and frame folder)."""
    for p in (SPRITES / f"{name}.image.json", SPRITES / f"{name}.image.json.meta"):
        if p.exists():
            p.unlink()
    shutil.rmtree(SPRITES / f"{name}.spritedata", ignore_errors=True)


def write_image(name, img, usage_allowed, origin, collision=None, tag_name="Still", shapes=None, template_name="Cloud Hills", parallax=None, ids=None):
    """ids (optional) pins guid/frame/layer/tag/created for a resource that kept an older name's identity."""
    ids = ids or {}
    path = SPRITES / f"{name}.image.json"
    meta_path = SPRITES / f"{name}.image.json.meta"
    if path.exists():
        doc = json.loads(path.read_text(encoding="utf-8"))
    else:
        doc = json.loads((SPRITES / f"{template_name}.image.json").read_text(encoding="utf-8"))
        frame_id = ids.get("frame", det_id(name, "frame"))
        doc["frames"][0]["id"] = frame_id
        doc["frames"][0]["name"] = f"{name} 01"
        doc["layers"][0]["id"] = ids.get("layer", det_id(name, "layer"))
        doc["layers"][0]["cels"][0]["frameId"] = frame_id
        if doc.get("tags"):
            doc["tags"][0]["id"] = ids.get("tag", det_id(name, "tag"))
            doc["tags"][0]["startFrameId"] = frame_id
            doc["tags"][0]["endFrameId"] = frame_id
        doc["collisionShapes"] = []
    w, h = img.size
    frame_id = doc["frames"][0]["id"]
    rel = f"{name}.spritedata/frames/{frame_id}.png"
    doc["canvas"]["width"] = w
    doc["canvas"]["height"] = h
    doc["origin"] = {"x": origin[0], "y": origin[1], "space": "pixels"}
    doc["usage"]["allowed"] = usage_allowed
    if collision is not None:
        doc["usage"]["tileset"]["collision"] = collision
    # 2D rooms scroll a background at this fraction of the camera (1 = with the world).
    if parallax is not None:
        doc["usage"]["background"] = {"mode": "fixed", "repeatX": True, "repeatY": False,
                                      "parallaxX": parallax, "parallaxY": 1}
    else:
        doc["usage"].pop("background", None)
    doc["frames"][0]["source"] = rel
    doc["frames"][0]["sourceRectangle"] = {"x": 0, "y": 0, "width": w, "height": h}
    doc["layers"][0]["cels"][0]["source"] = rel
    if tag_name is not None and doc.get("tags"):
        doc["tags"][0]["name"] = tag_name
    if shapes is not None:
        doc["collisionShapes"] = shapes
    frames_dir = SPRITES / f"{name}.spritedata" / "frames"
    frames_dir.mkdir(parents=True, exist_ok=True)
    for old in frames_dir.glob("*.png"):
        if old.stem != frame_id:
            old.unlink()
    img.save(frames_dir / f"{frame_id}.png", optimize=True)
    path.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
    if meta_path.exists():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        meta["modifiedUtc"] = "2026-10-11T00:00:00Z"
    else:
        meta = {"guid": ids.get("guid", det_id(name, "guid")), "kind": "Image", "displayName": name,
                "createdUtc": ids.get("created", "2026-10-11T00:00:00Z"), "modifiedUtc": "2026-10-11T00:00:00Z"}
    meta_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    print(f"{name}: {w}x{h} -> {rel}")


def contact_sheet(tiles):
    s = 3
    cell = TS * s
    pad = 14
    W = 8 * (cell + 2) + 2
    H = 5 * (cell + 2 + pad) + 2
    sheet = Image.new("RGBA", (W, H), (40, 40, 48, 255))
    d = ImageDraw.Draw(sheet)
    for i in range(40):
        cx, cy = i % 8, i // 8
        x0 = 2 + cx * (cell + 2)
        y0 = 2 + cy * (cell + 2 + pad)
        # checker so transparency is visible
        bg = Image.new("RGBA", (cell, cell))
        bp = bg.load()
        for y in range(cell):
            for x in range(cell):
                bp[x, y] = (150, 200, 240, 255) if ((x // 12 + y // 12) % 2) else (176, 220, 250, 255)
        t = tiles.crop((cx * TS, cy * TS, cx * TS + TS, cy * TS + TS)).resize((cell, cell), Image.NEAREST)
        bg.alpha_composite(t)
        sheet.paste(bg, (x0, y0 + pad))
        d.text((x0 + 2, y0 + 1), f"{i}{' S' if i in SOLID else ''}", fill=(255, 255, 255, 255))
    PREVIEW.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(PREVIEW)
    print(f"preview: {PREVIEW}")


def main():
    tiles = make_tileset()
    write_image("Meadow Tiles", tiles, "tileset", (0, 0), collision=SOLID, tag_name=None)
    write_image("Morning Sky", morning_sky(), "background", (0, 0))
    write_image("Far Hills", far_hills(), "background", (0, 0), parallax=0.15)
    write_image("Cloud Hills", cloud_hills(), "background", (0, 0), parallax=0.35)
    write_image("Meadow Forest", meadow_forest(), "background", (0, 0), parallax=0.6)
    write_image("Wayfarer Lodge", wayfarer_lodge(), "background", (0, 144))
    # The level-end goal was the "Summit Flag"; it is now the Summit Shrine and keeps the old identity.
    shrine_shape = [{"id": "a6685cb2d7305fb085e2aecd4d798f94", "name": "Gameplay mask", "kind": "rectangle",
                     "position": {"x": 28, "y": 4}, "size": {"x": 8, "y": 188}, "isTrigger": False}]
    remove_image("Summit Flag")
    write_image("Summit Shrine", summit_shrine(), "sprite", (32, 192), tag_name="Shrine", shapes=shrine_shape,
                ids={"guid": "de03c5cd711957ae8772f8c5c36180f7", "frame": "5cdbc781d69455e2a39a139d137735bc",
                     "layer": "3b292648ab585060b14961fd46c0d9ac", "tag": "e9945ba69bdf580f8690903dd42549cb",
                     "created": "2026-09-22T00:00:00Z"})
    contact_sheet(tiles)


if __name__ == "__main__":
    main()
