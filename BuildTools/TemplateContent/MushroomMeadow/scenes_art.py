"""Mushroom Meadow scene art generator (ART-SCENES).

Deterministic: re-running produces byte-identical PNGs and JSON.
Writes the Overworld Map, Overworld Clouds, Intro Panel 1..5, Ending Panel 1..3, Menu Backdrop,
Credits Backdrop and Lantern Tree Lights 1..5 into the TwoDShowcase template Sprites folder, plus
previews under TestResults/MushroomMeadow/scenes.

Pip is composited from the real Explorer sprite frames so the hero always matches gameplay.
Close-up panels (Intro 4, Ending 1, Ending 3) are painted at 320x180 and scaled 2x (nearest) so every
pixel stays square; wide shots are painted at native 640x360.

Usage:  python BuildTools/TemplateContent/MushroomMeadow/scenes_art.py
"""
import hashlib
import json
import math
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[3]
SPRITES = ROOT / "Source" / "Genesis.Application.Core" / "Projects" / "Templates" / "Assets" / "TwoDShowcase" / "Assets" / "Sprites"
PREVIEW = ROOT / "TestResults" / "MushroomMeadow" / "scenes"


def hx(s, a=255):
    return (int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16), a)


CLEAR = (0, 0, 0, 0)
OUT = hx("2a1d2e")
RED = [hx("f0705a"), hx("d8433b"), hx("a12a2f"), hx("6e1c2a")]
CREAM = [hx("fff8e0"), hx("f2e2b6"), hx("d4bd8a"), hx("a8916a")]
GOLD = [hx("fff3a6"), hx("ffd23f"), hx("e89a1b"), hx("a65f12")]
GREEN = [hx("9be15d"), hx("4caf3c"), hx("2f7d32"), hx("1f5226")]
WOOD = [hx("c48a52"), hx("9b6a3c"), hx("6d4526"), hx("4a2e1c")]
WHITE = (255, 255, 255, 255)
SPORE_CORE = hx("fffbe8")
SPORE_GLOW = hx("ffd86a")
VIOLET = [hx("d6a8ff"), hx("a46ae0"), hx("6e3ea8"), hx("43236e"), hx("26143f")]

# Exact Glowspore positions in the 1024x360 Village Far strip (crown of the Great Lantern Tree centred at x 512).
TREE_LIGHT_SPOTS = [(456, 140), (486, 104), (514, 74), (544, 106), (572, 140)]

# Overworld node coordinates (contract): Hearthollow, Meadow, Rootdeep, Tidepool, Canopy, Duskmantle Hollow.
NODES = [(90, 250), (190, 205), (300, 250), (395, 292), (470, 170), (570, 88)]

BAYER4 = [[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]]


def bayer(x, y):
    return (BAYER4[y & 3][x & 3] + 0.5) / 16.0


def hsh(x, y, s=0):
    n = (int(x) * 374761393 + int(y) * 668265263 + int(s) * 1442695041) & 0xFFFFFFFF
    n = ((n ^ (n >> 13)) * 1274126177) & 0xFFFFFFFF
    return ((n ^ (n >> 16)) & 0xFFFF) / 65535.0


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def mul(c, m, add=(0, 0, 0)):
    return (max(0, min(255, int(round(c[0] * m[0] + add[0])))),
            max(0, min(255, int(round(c[1] * m[1] + add[1])))),
            max(0, min(255, int(round(c[2] * m[2] + add[2])))), c[3])


def smooth(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3 - 2 * t)


# Canvas ------------------------------------------------------------------------------------
class Cv:
    def __init__(self, w, h, fill=CLEAR):
        self.w, self.h = w, h
        self.img = Image.new("RGBA", (w, h), fill)
        self.px = self.img.load()

    def layer(self):
        return Cv(self.w, self.h)

    def ok(self, x, y):
        return 0 <= x < self.w and 0 <= y < self.h

    def get(self, x, y):
        x, y = int(x), int(y)
        return self.px[x, y] if self.ok(x, y) else CLEAR

    def put(self, x, y, c):
        x, y = int(math.floor(x)), int(math.floor(y))
        if self.ok(x, y):
            self.px[x, y] = c

    def mix(self, x, y, c, a):
        """Composite colour c with opacity a over the pixel (works on transparent canvases too)."""
        if a <= 0:
            return
        x, y = int(math.floor(x)), int(math.floor(y))
        if not self.ok(x, y):
            return
        a = min(1.0, a)
        d = self.px[x, y]
        da = d[3] / 255.0
        oa = a + da * (1 - a)
        if oa <= 0:
            return
        r = [(c[i] * a + d[i] * da * (1 - a)) / oa for i in range(3)]
        self.px[x, y] = (int(round(r[0])), int(round(r[1])), int(round(r[2])), int(round(oa * 255)))

    def rect(self, x0, y0, x1, y1, c):
        for y in range(max(0, int(y0)), min(self.h, int(y1) + 1)):
            for x in range(max(0, int(x0)), min(self.w, int(x1) + 1)):
                self.px[x, y] = c

    def fill(self, x0, y0, x1, y1, fn):
        for y in range(max(0, int(x0 * 0 + y0)), min(self.h, int(y1) + 1)):
            for x in range(max(0, int(x0)), min(self.w, int(x1) + 1)):
                c = fn(x, y)
                if c is not None:
                    self.px[x, y] = c

    def disc(self, cx, cy, r, c, sq=1.0):
        def f(x, y):
            return c if math.hypot(x + 0.5 - cx, (y + 0.5 - cy) / sq) <= r else None
        self.fill(cx - r - 1, cy - r * sq - 1, cx + r + 1, cy + r * sq + 1, f)

    def poly(self, pts, c):
        ImageDraw.Draw(self.img).polygon([(round(x), round(y)) for (x, y) in pts], fill=c)

    def line(self, x0, y0, x1, y1, c, r=0.5):
        n = int(max(abs(x1 - x0), abs(y1 - y0)) * 2) + 1
        for i in range(n + 1):
            t = i / n
            cx, cy = x0 + (x1 - x0) * t, y0 + (y1 - y0) * t
            if r <= 0.5:
                self.put(cx, cy, c)
                continue
            for yy in range(int(math.floor(cy - r)), int(math.ceil(cy + r)) + 1):
                for xx in range(int(math.floor(cx - r)), int(math.ceil(cx + r)) + 1):
                    if (xx + 0.5 - cx) ** 2 + (yy + 0.5 - cy) ** 2 <= r * r:
                        self.put(xx, yy, c)

    def over(self, other, dx=0, dy=0):
        src = other.img if isinstance(other, Cv) else other
        sx0, sy0 = max(0, -dx), max(0, -dy)
        sx1, sy1 = min(src.width, self.w - dx), min(src.height, self.h - dy)
        if sx1 <= sx0 or sy1 <= sy0:
            return
        crop = src.crop((sx0, sy0, sx1, sy1))
        self.img.alpha_composite(crop, (dx + sx0, dy + sy0))


def outline(cv, color=OUT, diag=False):
    w, h = cv.w, cv.h
    px = cv.px
    add = []
    nb = ((1, 0), (-1, 0), (0, 1), (0, -1)) + (((1, 1), (-1, 1), (1, -1), (-1, -1)) if diag else ())
    for y in range(h):
        for x in range(w):
            if px[x, y][3] != 0:
                continue
            for dx, dy in nb:
                nx, ny = x + dx, y + dy
                if 0 <= nx < w and 0 <= ny < h and px[nx, ny][3] > 0 and px[nx, ny] != color:
                    add.append((x, y))
                    break
    for x, y in add:
        px[x, y] = color
    return cv


def tint(cv, m, add=(0, 0, 0)):
    px = cv.px
    for y in range(cv.h):
        for x in range(cv.w):
            c = px[x, y]
            if c[3]:
                px[x, y] = mul(c, m, add)
    return cv


def scale2(cv):
    out = Cv(cv.w * 2, cv.h * 2)
    out.img = cv.img.resize((cv.w * 2, cv.h * 2), Image.NEAREST)
    out.px = out.img.load()
    return out


# Atmosphere --------------------------------------------------------------------------------
def ramp_colors(stops, n):
    out = []
    for i in range(n):
        t = i / (n - 1)
        for k in range(len(stops) - 1):
            t0, c0 = stops[k]
            t1, c1 = stops[k + 1]
            if t0 <= t <= t1:
                out.append(lerp(c0, c1, (t - t0) / max(1e-6, t1 - t0)))
                break
        else:
            out.append(stops[-1][1])
    return out


def sky(cv, stops, n=14, y0=0, y1=None, curve=1.0):
    y1 = cv.h if y1 is None else y1
    ramp = ramp_colors(stops, n)
    for y in range(y0, y1):
        t = ((y - y0) / max(1, (y1 - y0 - 1))) ** curve
        lv = t * (n - 1)
        b = int(lv)
        f = lv - b
        for x in range(cv.w):
            i = min(n - 1, b + (1 if f > bayer(x, y) else 0))
            cv.px[x, y] = ramp[i]


def stars(cv, seed, y_max, density=0.004, colors=(hx("fff6d8"), hx("c8d4ff"), hx("ffe0f0")), fade=True, keep=None):
    for y in range(0, y_max):
        for x in range(cv.w):
            h = hsh(x, y, seed)
            dens = density * ((1 - y / y_max) if fade else 1.0)
            if h < dens:
                if keep is not None and not keep(x, y):
                    continue
                c = colors[int(hsh(x, y, seed + 1) * len(colors)) % len(colors)]
                big = hsh(x, y, seed + 2) < 0.12
                cv.put(x, y, c)
                if big:
                    dim = lerp(cv.get(x + 1, y), c, 0.5)
                    for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        cv.mix(x + dx, y + dy, c, 0.45)


def halo(cv, cx, cy, r, c, amax=0.5, bands=4, sq=1.0, power=1.7):
    """Banded radial glow (pixel-art rings)."""
    for y in range(int(cy - r * sq) - 1, int(cy + r * sq) + 2):
        for x in range(int(cx - r) - 1, int(cx + r) + 2):
            d = math.hypot(x + 0.5 - cx, (y + 0.5 - cy) / sq)
            if d >= r:
                continue
            t = 1 - d / r
            q = math.ceil(t * bands) / bands
            cv.mix(x, y, c, amax * q ** power)


def vignette(cv, c, strength=0.45, inner=0.55, bands=5):
    cx, cy = cv.w / 2, cv.h / 2
    for y in range(cv.h):
        for x in range(cv.w):
            d = math.hypot((x + 0.5 - cx) / cx, (y + 0.5 - cy) / cy) / math.sqrt(2)
            if d > inner:
                t = (d - inner) / (1 - inner)
                q = math.ceil(t * bands) / bands
                cv.mix(x, y, c, strength * q * q)


def ridge(cv, base, terms, c_body, c_rim=None, c_shadow=None, x0=0, x1=None, rim=1, shade_from=None, seed=0):
    """Silhouette band: everything below the height curve gets c_body."""
    x1 = cv.w if x1 is None else x1
    for x in range(x0, x1):
        top = int(round(base + sum(a * math.sin(2 * math.pi * k * x / 640 + p) for (a, k, p) in terms)))
        for y in range(max(0, top), cv.h):
            c = c_body
            if c_rim is not None and y < top + rim:
                c = c_rim
            elif c_shadow is not None and shade_from is not None and y > top + shade_from and bayer(x, y) < min(0.85, (y - top - shade_from) / 40):
                c = c_shadow
            cv.px[x, y] = c


def fireflies(cv, seed, n, box, c=hx("fff0a0"), glow=hx("ffe070")):
    x0, y0, x1, y1 = box
    for i in range(n):
        x = x0 + hsh(i, 1, seed) * (x1 - x0)
        y = y0 + hsh(i, 2, seed) * (y1 - y0)
        halo(cv, x, y, 5, glow, 0.35, 2)
        cv.put(x, y, c)


# Glowspore ---------------------------------------------------------------------------------
def glowspore(cv, cx, cy, r, lit=True, halo_r=None, amax=0.55):
    if lit:
        halo(cv, cx, cy, halo_r or r * 4.2, SPORE_GLOW, amax, 4)
    for y in range(int(cy - r) - 2, int(cy + r) + 3):
        for x in range(int(cx - r) - 2, int(cx + r) + 3):
            dx, dy = x + 0.5 - cx, y + 0.5 - cy
            d = math.hypot(dx, dy)
            if d > r + 0.3:
                continue
            if lit:
                if d > r - 0.9:
                    c = GOLD[2]
                elif d > r * 0.62:
                    c = GOLD[1]
                elif d > r * 0.3 or (dx + dy) > 0.8:
                    c = GOLD[0]
                else:
                    c = SPORE_CORE
            else:
                c = hx("3a3150") if d > r - 0.9 else (hx("6a6288") if dx + dy < -0.5 else hx("524a6e"))
            cv.put(x, y, c)
    if lit and r >= 3:
        cv.put(cx - r * 0.45, cy - r * 0.45, WHITE)


# Great Lantern Tree ------------------------------------------------------------------------
TREE_PUFFS = [  # unit coords relative to the trunk base, drawn in order (back first)
    (0, -228, 34), (-40, -206, 40), (42, -208, 40), (-82, -176, 34), (84, -178, 34),
    (0, -182, 52), (-62, -142, 44), (64, -144, 46), (-104, -126, 30), (106, -128, 30),
    (-28, -132, 40), (30, -130, 40), (-70, -108, 28), (72, -108, 30), (0, -112, 30),
]
# Five lantern spots (unit coords, order of lighting). Same arch as TREE_LIGHT_SPOTS.
TREE_LANTERNS = [(-86, -112), (-46, -158), (2, -200), (48, -154), (88, -112)]

TREE_PAL = {
    "dusk": dict(leaf=[hx("4f8072"), hx("38605e"), hx("284650"), hx("1c303f")], out=hx("10162a"),
                 bark=[hx("7a5a6a"), hx("5a4054"), hx("3e2c40"), hx("2a1d2e")]),
    "gloom": dict(leaf=[hx("5a5c80"), hx("45476c"), hx("343656"), hx("252642")], out=hx("120e22"),
                  bark=[hx("5e4e66"), hx("463a52"), hx("30283e"), hx("1e1828")]),
    "day": dict(leaf=[hx("9be15d"), hx("5cb848"), hx("3a8a3e"), hx("25603a")], out=hx("1a3424"),
                bark=[hx("b07e52"), hx("8a5c3a"), hx("624028"), hx("3e281c")]),
    "blaze": dict(leaf=[hx("ffe6a0"), hx("d8c070"), hx("94a056"), hx("587848")], out=hx("2a2620"), warm=hx("fff0b0"),
                  bark=[hx("d8a066"), hx("a8724a"), hx("74492e"), hx("4a2e1c")]),
    "night": dict(leaf=[hx("3e6a66"), hx("2a4e54"), hx("1c3644"), hx("122434")], out=hx("0a1020"),
                  bark=[hx("4e3c50"), hx("3a2c40"), hx("281e30"), hx("1a1222")]),
}


def great_tree(cv, cx, by, sc, mode="dusk", lit=5, sockets=True, light=(-0.55, -0.83), spore_r=None, glow=1.0):
    """Draw the Great Lantern Tree. lit = number of lit Glowspores (in TREE_LANTERNS order)."""
    pal = TREE_PAL[mode]
    bark, leaf, out = pal["bark"], pal["leaf"], pal["out"]
    L = cv.layer()
    # roots + trunk
    th = 118 * sc
    for y in range(int(by - th), int(by) + 1):
        t = (by - y) / th  # 0 at base, 1 at top
        hw = (17 + 12 * (1 - t)) * sc + 30 * sc * max(0.0, 1 - t * 5) ** 2
        ox = 3 * sc * math.sin(t * 3.4)
        for x in range(int(cx + ox - hw), int(cx + ox + hw) + 1):
            u = (x + 0.5 - (cx + ox)) / max(hw, 1)
            if u < -0.55:
                c = bark[0] if u > -0.85 else bark[1]
            elif u > 0.45:
                c = bark[2] if u < 0.8 else bark[3]
            else:
                c = bark[1]
                # vertical bark grooves
                g = math.sin((u * 7 + 0.4 * math.sin(y * 0.09)) * math.pi)
                if g > 0.86:
                    c = bark[2]
            L.put(x, y, c)
    # root flares
    for (dx, ln, ang) in [(-1, 46, 0.22), (1, 50, 0.2), (-1, 30, 0.5), (1, 28, 0.55)]:
        x0 = cx + dx * 20 * sc
        x1 = cx + dx * (20 + ln) * sc
        for i in range(int(ln * sc) + 1):
            t = i / max(1, ln * sc)
            x = x0 + (x1 - x0) * t
            hgt = (14 * (1 - t) ** 1.5 + 1.5) * sc
            for y in range(int(by - hgt), int(by) + 1):
                L.put(x, y, bark[1] if y > by - hgt + 1.5 * sc else bark[0])
    # branches
    for (bx, byy, ex, ey, r0) in [(0, -100, -70, -150, 7), (0, -100, 72, -152, 7), (-4, -110, -40, -190, 6),
                                  (4, -110, 44, -192, 6), (0, -112, 0, -200, 7), (-30, -132, -98, -122, 4),
                                  (30, -132, 100, -124, 4)]:
        n = 24
        for i in range(n + 1):
            t = i / n
            x = cx + (bx + (ex - bx) * t) * sc
            y = by + (byy + (ey - byy) * t - 10 * math.sin(t * math.pi)) * sc
            r = max(1.0, r0 * (1 - 0.6 * t) * sc)
            for yy in range(int(y - r), int(y + r) + 1):
                for xx in range(int(x - r), int(x + r) + 1):
                    if (xx + 0.5 - x) ** 2 + (yy + 0.5 - y) ** 2 <= r * r:
                        L.put(xx, yy, bark[1] if xx < x else bark[2])
    # crown
    puffs = [(cx + dx * sc, by + dy * sc, r * sc) for (dx, dy, r) in TREE_PUFFS]
    lit_pts = [(cx + lx * sc, by + ly * sc) for (lx, ly) in TREE_LANTERNS[:lit]]
    warm_r = 34 * sc * glow
    warm = pal.get("warm", hx("ffd47a"))
    minx = int(min(p[0] - p[2] for p in puffs)) - 1
    maxx = int(max(p[0] + p[2] for p in puffs)) + 1
    miny = int(min(p[1] - p[2] for p in puffs)) - 1
    maxy = int(max(p[1] + p[2] for p in puffs)) + 1
    def prad(i, pr, ddx, ddy):
        n = max(5, int(pr / 3.2))
        return pr * (1 + 0.075 * math.sin(math.atan2(ddy, ddx) * n + i * 1.7))

    def inside_any(px_, py_):
        for i, (pcx, pcy, pr) in enumerate(puffs):
            ddx, ddy = px_ - pcx, py_ - pcy
            if math.hypot(ddx, ddy) <= prad(i, pr, ddx, ddy):
                return True
        return False

    for y in range(max(0, miny - 3), min(cv.h, maxy + 4)):
        for x in range(max(0, minx - 3), min(cv.w, maxx + 4)):
            top = None
            for i, (pcx, pcy, pr) in enumerate(puffs):
                ddx, ddy = x + 0.5 - pcx, y + 0.5 - pcy
                d = math.hypot(ddx, ddy)
                rr = prad(i, pr, ddx, ddy)
                if d <= rr:
                    top = (i, ddx, ddy, d, rr)
            if top is None:
                continue
            i, ddx, ddy, d, pr = top
            if d > pr - 1.2 * max(1, sc):
                edge = False
                for ex, ey in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    if not inside_any(x + ex + 0.5, y + ey + 0.5):
                        edge = True
                        break
                c = out if edge else leaf[3]
            else:
                k = (ddx * light[0] + ddy * light[1]) / pr
                if k > 0.5:
                    c = leaf[0] if (k > 0.62 or bayer(x, y) < 0.5) else leaf[1]
                elif k > 0.05:
                    c = leaf[1]
                elif k > -0.4:
                    c = leaf[2] if bayer(x, y) < (0.05 - k) * 2.2 else leaf[1]
                else:
                    c = leaf[3] if (k < -0.6 or bayer(x, y) < 0.6) else leaf[2]
                if hsh(x // 2, y // 2, 17 + i) < 0.05:
                    c = leaf[min(3, leaf.index(c) + 1)] if c in leaf else c
                # warm light spilling from lit Glowspores onto the leaves (banded)
                best = 0.0
                for (sx, sy) in lit_pts:
                    dd = math.hypot(x + 0.5 - sx, (y + 0.5 - sy) * 1.15)
                    if dd < warm_r:
                        best = max(best, 1 - dd / warm_r)
                if best > 0:
                    q = math.ceil(best * 3) / 3
                    c = lerp(c, warm, 0.62 * q * q)
            L.px[x, y] = c
    outline(L, out)
    cv.over(L)
    # lanterns
    sr = spore_r if spore_r is not None else max(2.0, 6.5 * sc)
    for j, (lx, ly) in enumerate(TREE_LANTERNS):
        x, y = cx + lx * sc, by + ly * sc
        on = j < lit
        if not on and not sockets:
            continue
        # hanging stem
        cv.line(x, y - sr - 4 * sc, x, y - sr, out)
        if on:
            glowspore(cv, x, y, sr, True, sr * 4.6 * glow, 0.5)
        else:
            glowspore(cv, x, y, sr * 0.8, False)


def _union_edge(puffs, x, y):
    for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        nx, ny = x + dx + 0.5, y + dy + 0.5
        if not any(math.hypot(nx - a, ny - b) <= r for (a, b, r) in puffs):
            return True
    return False


def tree_lantern_points(cx, by, sc):
    return [(cx + lx * sc, by + ly * sc) for (lx, ly) in TREE_LANTERNS]


# Mushroom houses ---------------------------------------------------------------------------
def mush_house(cv, x, by, w, h, cap=None, dim=(1, 1, 1), dim_add=(0, 0, 0), lit=1.0, seed=0, chimney=False, glow=True):
    cap = cap or RED
    L = cv.layer()
    stem_top = by - h * 0.58
    # stem body
    for y in range(int(stem_top), int(by) + 1):
        t = (y - stem_top) / max(1, by - stem_top)
        hw = w * 0.5 * (0.74 + 0.22 * t)
        for xx in range(int(x - hw), int(x + hw) + 1):
            u = (xx + 0.5 - x) / hw
            c = CREAM[1]
            if u < -0.6:
                c = CREAM[0]
            elif u > 0.55:
                c = CREAM[2] if u < 0.85 else CREAM[3]
            if y < stem_top + 3:
                c = CREAM[3]
            L.put(xx, y, c)
    # door
    dw, dh = max(3, w * 0.13), h * 0.3
    dx0 = x + w * 0.04
    for y in range(int(by - dh), int(by) + 1):
        for xx in range(int(dx0 - dw), int(dx0 + dw) + 1):
            if y < by - dh + dw and math.hypot(xx + 0.5 - dx0, y + 0.5 - (by - dh + dw)) > dw:
                continue
            c = WOOD[2] if (int(xx - dx0 + 20) % 3) else WOOD[3]
            if xx <= dx0 - dw + 0.5 or xx >= dx0 + dw - 0.5:
                c = WOOD[3]
            L.put(xx, y, c)
    L.put(dx0 + dw * 0.5, by - dh * 0.45, GOLD[1])
    # cap
    ccy = stem_top + 1
    rx, ry = w * 0.74, h * 0.5
    for y in range(int(ccy - ry), int(ccy + 3)):
        for xx in range(int(x - rx) - 1, int(x + rx) + 2):
            nx, ny = (xx + 0.5 - x) / rx, (y + 0.5 - ccy) / ry
            if y <= ccy:
                if nx * nx + ny * ny > 1:
                    continue
            else:
                if abs(nx) > 1 - 0.08 * (y - ccy):
                    continue
            k = -(nx * -0.6 + ny * -0.8)
            if y > ccy - 1:
                c = cap[3]
            elif k < -0.25 and nx < -0.2 and ny < -0.2:
                c = cap[0]
            elif nx > 0.5:
                c = cap[2] if (nx > 0.75 or bayer(xx, y) < 0.5) else cap[1]
            else:
                c = cap[1]
            L.put(xx, y, c)
    # spots
    for i in range(5):
        a = -2.6 + i * 1.05 + (hsh(i, seed, 41) - 0.5) * 0.5
        rr = 0.55 + hsh(i, seed, 42) * 0.25
        sx, sy = x + math.cos(a) * rx * rr * 0.95, ccy + math.sin(a) * ry * rr - ry * 0.05
        if sy > ccy - 2:
            continue
        s = max(1.0, w * 0.06 * (0.8 + hsh(i, seed, 43) * 0.6))
        for yy in range(int(sy - s), int(sy + s) + 1):
            for xx in range(int(sx - s - 1), int(sx + s + 1) + 1):
                if math.hypot((xx + 0.5 - sx) / 1.3, yy + 0.5 - sy) <= s and L.get(xx, yy)[3]:
                    L.put(xx, yy, CREAM[0] if yy < sy else CREAM[2])
    if chimney:
        cxm = x + rx * 0.45
        for y in range(int(ccy - ry * 0.95), int(ccy - ry * 0.55)):
            for xx in range(int(cxm - 2), int(cxm + 2)):
                L.put(xx, y, hx("8e88a3") if xx < cxm else hx("5c5670"))
    outline(L, OUT)
    tint(L, dim, dim_add)
    cv.over(L)
    # window
    wr = max(2.0, w * 0.1)
    wx, wy = x - w * 0.22, by - h * 0.33
    win = lerp(hx("4a3a50"), hx("ffd86a"), lit)
    for y in range(int(wy - wr) - 1, int(wy + wr) + 2):
        for xx in range(int(wx - wr) - 1, int(wx + wr) + 2):
            d = math.hypot(xx + 0.5 - wx, y + 0.5 - wy)
            if d <= wr:
                c = win
                if lit > 0.5 and d < wr * 0.5 and (xx + y) % 2 == 0:
                    c = hx("fff3c0")
                if abs(xx + 0.5 - wx) < 0.6 or abs(y + 0.5 - wy) < 0.6:
                    c = mul(WOOD[3], dim, dim_add)
                cv.put(xx, y, c)
            elif d <= wr + 1:
                cv.put(xx, y, mul(WOOD[2], dim, dim_add))
    if glow and lit > 0.3:
        halo(cv, wx, wy, wr * 4.5, hx("ffc850"), 0.32 * lit, 3)


# Pip (real Explorer frames) ----------------------------------------------------------------
_FRAMES = {}


def pip_frame(name="Explorer Idle", idx=0):
    key = (name, idx)
    if key not in _FRAMES:
        doc = json.loads((SPRITES / f"{name}.image.json").read_text(encoding="utf-8"))
        _FRAMES[key] = Image.open(SPRITES / doc["frames"][idx]["source"]).convert("RGBA")
    return _FRAMES[key]


def pip(cv, x, by, name="Explorer Idle", idx=0, flip=False, m=(1, 1, 1), add=(0, 0, 0)):
    """Paste Pip with feet at (x, by); returns the sprite's top-left."""
    im = pip_frame(name, idx)
    if flip:
        im = im.transpose(Image.FLIP_LEFT_RIGHT)
    t = Cv(im.width, im.height)
    t.img = im.copy()
    t.px = t.img.load()
    if m != (1, 1, 1) or add != (0, 0, 0):
        tint(t, m, add)
    ox, oy = int(x) - 16, int(by) - 40
    cv.over(t, ox, oy)
    return ox, oy


def hand_lantern(cv, hx_, hy, lit=True, big=1):
    """Small hand lantern hanging from (hx_, hy)."""
    cv.put(hx_, hy, OUT)
    cv.put(hx_, hy + 1, OUT)
    x0, y0 = hx_ - 2, hy + 2
    if lit:
        halo(cv, hx_ + 0.5, y0 + 4, 16 * big, hx("ffc850"), 0.42, 4)
    for y in range(y0, y0 + 7):
        for x in range(x0, x0 + 5):
            edge = x in (x0, x0 + 4) or y in (y0, y0 + 6)
            c = hx("5c5670") if edge else (hx("fff0a0") if lit else hx("6a6288"))
            if lit and not edge and y == y0 + 3 and x == x0 + 2:
                c = WHITE
            cv.put(x, y, c)
    cv.rect(x0 + 1, y0 - 1, x0 + 3, y0 - 1, OUT)


# Characters (scene-scale drawings) ---------------------------------------------------------
MOREL = [hx("b08a5a"), hx("8a6438"), hx("6a4a2a"), hx("4a3020")]
ROBE = [hx("8fb46a"), hx("5f8a4a"), hx("3e6438"), hx("284628")]
BEARD = [hx("ffffff"), hx("e4e0ec"), hx("b8b2c8")]


def npc_sprite(cv, name, x, by, flip=False, m=(1, 1, 1), add=(0, 0, 0), idx=0):
    """Paste ART-CREATURES' canonical NPC sprite (origin = feet) when it exists; returns False otherwise."""
    # ART-CREATURES renames NPC images to "<Name> Sprite" (unique-name amendment); accept either.
    p = SPRITES / f"{name} Sprite.image.json"
    if not p.exists():
        p = SPRITES / f"{name}.image.json"
    if not p.exists():
        return False
    doc = json.loads(p.read_text(encoding="utf-8"))
    frames = doc.get("frames") or []
    if not frames:
        return False
    src = SPRITES / frames[min(idx, len(frames) - 1)]["source"]
    if not src.exists():
        return False
    im = Image.open(src).convert("RGBA")
    ox, oy = int(doc["origin"]["x"]), int(doc["origin"]["y"])
    if flip:
        im = im.transpose(Image.FLIP_LEFT_RIGHT)
        ox = im.width - ox
    t = Cv(im.width, im.height)
    t.img = im
    t.px = t.img.load()
    if m != (1, 1, 1) or add != (0, 0, 0):
        tint(t, m, add)
    cv.over(t, int(x) - ox, int(by) - oy)
    return True


def elder_morel(cv, x, by, face=-1, arm_out=False, m=(1, 1, 1), add=(0, 0, 0)):
    """Elder Morel, feet at by, facing left (face=-1) or right (1)."""
    if npc_sprite(cv, "Elder Morel", x, by, face < 0, m, add, 3 if arm_out else 0):
        return (int(x) - 13 if face < 0 else int(x) + 13, int(by) - 22)
    L = Cv(60, 60)
    ox = 30
    b = 58
    # robe
    for y in range(b - 18, b + 1):
        t = (y - (b - 18)) / 18
        hw = 7 + 4 * t
        for xx in range(int(ox - hw), int(ox + hw) + 1):
            u = (xx + 0.5 - ox) / hw
            c = ROBE[1]
            if u < -0.5:
                c = ROBE[0]
            elif u > 0.45:
                c = ROBE[2]
            if y >= b - 1:
                c = ROBE[3]
            L.put(xx, y, c)
    # belt
    L.rect(ox - 8, b - 10, ox + 8, b - 9, WOOD[2])
    L.put(ox - 3, b - 10, GOLD[1])
    # feet
    L.rect(ox - 6, b, ox - 2, b, WOOD[3])
    L.rect(ox + 2, b, ox + 6, b, WOOD[3])
    # face (stem)
    for y in range(b - 30, b - 17):
        for xx in range(ox - 7, ox + 8):
            if math.hypot((xx + 0.5 - ox) / 7.2, (y + 0.5 - (b - 23)) / 6.5) <= 1:
                u = (xx + 0.5 - ox) / 7
                L.put(xx, y, CREAM[0] if u < -0.4 else (CREAM[2] if u > 0.5 else CREAM[1]))
    # beard
    for y in range(b - 24, b - 6):
        t = (y - (b - 24)) / 18
        hw = 7.5 * (1 - t) ** 0.8 + 0.5
        cxb = ox - 1 + math.sin(t * 5) * 1.0
        for xx in range(int(cxb - hw), int(cxb + hw) + 1):
            u = (xx + 0.5 - cxb) / max(hw, 1)
            c = BEARD[0] if u < -0.2 else (BEARD[1] if u < 0.5 else BEARD[2])
            if (xx + y) % 5 == 0 and u > -0.5:
                c = BEARD[1] if c == BEARD[0] else BEARD[2]
            L.put(xx, y, c)
    # moustache
    L.rect(ox - 6, b - 21, ox + 5, b - 20, BEARD[0])
    L.put(ox - 7, b - 20, BEARD[1])
    L.put(ox + 6, b - 20, BEARD[1])
    # nose
    L.rect(ox - 1, b - 23, ox + 1, b - 22, hx("e8a87a"))
    # eyes + brows
    for ex in (ox - 4, ox + 3):
        L.put(ex, b - 25, OUT)
        L.rect(ex - 1, b - 27, ex + 1, b - 27, BEARD[0])
    # morel cap (tall, pitted)
    ccy, rx, ry = b - 38, 9.5, 12.5
    for y in range(int(ccy - ry) - 1, b - 27):
        for xx in range(ox - 12, ox + 13):
            ny = (y + 0.5 - ccy) / ry
            if ny < -1 or ny > 1.0:
                continue
            w = rx * (0.35 + 0.65 * ((ny + 1) / 2) ** 0.55)
            if abs(xx + 0.5 - ox) > w:
                continue
            u = (xx + 0.5 - ox) / max(w, 1)
            # honeycomb pits
            row = int((y - (ccy - ry)) // 3)
            col = int((xx + (2 if row % 2 else 0)) // 4)
            fy = (y - (ccy - ry)) % 3
            fx = (xx + (2 if row % 2 else 0)) % 4
            pit = fy >= 1 and fx in (1, 2)
            if pit:
                c = MOREL[2] if u < 0.4 else MOREL[3]
            else:
                c = MOREL[0] if u < -0.3 else (MOREL[1] if u < 0.5 else MOREL[2])
            L.put(xx, y, c)
    # arm
    if arm_out:
        L.line(ox - 4, b - 15, ox - 13, b - 17, ROBE[1], 1.6)
        L.disc(ox - 14.5, b - 17, 2.2, CREAM[1])
    else:
        L.line(ox - 5, b - 15, ox - 10, b - 10, ROBE[1], 1.6)
    outline(L, OUT)
    # walking stick (held on the far side)
    sx = ox + 10
    for y in range(b - 32, b + 1):
        L.put(sx, y, WOOD[1])
        L.put(sx + 1, y, WOOD[2])
    L.rect(sx - 1, b - 34, sx + 2, b - 33, WOOD[1])
    L.put(sx - 2, b - 33, WOOD[1])
    L.put(sx + 3, b - 35, GREEN[1])
    L.put(sx + 4, b - 36, GREEN[0])
    L.put(sx + 4, b - 35, GREEN[1])
    L.line(ox + 7, b - 15, sx, b - 18, ROBE[2], 1.4)
    L.disc(sx + 0.5, b - 18, 1.8, CREAM[2])
    if face > 0:
        L.img = L.img.transpose(Image.FLIP_LEFT_RIGHT)
        L.px = L.img.load()
    if m != (1, 1, 1) or add != (0, 0, 0):
        tint(L, m, add)
    cv.over(L, int(x) - 30, int(by) - 58)
    return (int(x) - 14 if face < 0 else int(x) + 14, int(by) - 17)  # hand position (arm_out)


def chanterelle(cv, x, by, m=(1, 1, 1), add=(0, 0, 0), face=1):
    if npc_sprite(cv, "Chanterelle", x, by, face < 0, m, add, 2):
        return
    L = Cv(44, 44)
    ox, b = 22, 42
    TR = [hx("8a6a5a"), hx("6a4c42"), hx("4a3430"), hx("30201e")]
    for y in range(b - 32, b - 2):
        for xx in range(ox - 17, ox + 18):
            nx, ny = (xx + 0.5 - ox) / 16.5, (y + 0.5 - (b - 17)) / 15
            lump = 0.06 * math.sin(math.atan2(ny, nx) * 7)
            if nx * nx + ny * ny > (1 + lump) ** 2:
                continue
            k = nx * -0.6 + ny * -0.8
            c = TR[0] if k > 0.45 else (TR[1] if k > -0.3 else TR[2])
            if hsh(xx // 2, y // 2, 61) < 0.12:
                c = TR[min(3, TR.index(c) + 1)]
            L.put(xx, y, c)
    # apron
    for y in range(b - 15, b - 3):
        hw = 8 + (y - (b - 15)) * 0.25
        for xx in range(int(ox - hw), int(ox + hw) + 1):
            L.put(xx, y, CREAM[1] if xx < ox + hw - 2 else CREAM[2])
    L.rect(ox - 8, b - 16, ox + 8, b - 16, CREAM[2])
    L.rect(ox - 3, b - 11, ox + 3, b - 8, CREAM[2])
    # face
    for ex in (ox - 5, ox + 4):
        L.rect(ex, b - 23, ex + 1, b - 22, OUT)
        L.put(ex, b - 23, WHITE)
    L.rect(ox - 2, b - 19, ox + 1, b - 19, OUT)
    L.put(ox - 3, b - 20, OUT)
    L.put(ox + 2, b - 20, OUT)
    L.put(ox - 8, b - 20, hx("e88a8a"))
    L.put(ox + 7, b - 20, hx("e88a8a"))
    # feet
    L.rect(ox - 8, b - 2, ox - 4, b, TR[3])
    L.rect(ox + 4, b - 2, ox + 8, b, TR[3])
    outline(L, OUT)
    if face < 0:
        L.img = L.img.transpose(Image.FLIP_LEFT_RIGHT)
        L.px = L.img.load()
    tint(L, m, add)
    cv.over(L, int(x) - 22, int(by) - 42)


def bram(cv, x, by, m=(1, 1, 1), add=(0, 0, 0), face=1):
    if npc_sprite(cv, "Bram", x, by, face < 0, m, add, 2):
        return
    L = Cv(48, 40)
    ox, b = 24, 38
    SK = [hx("c8e0a0"), hx("9cc078"), hx("6e9458"), hx("4a6a40")]
    SH = [hx("f0b070"), hx("d08048"), hx("a05a34"), hx("6e3a24")]
    # foot / body
    for xx in range(ox - 18, ox + 16):
        top = b - 6 - (5 if xx > ox + 6 else 0)
        for y in range(top, b + 1):
            L.put(xx, y, SK[1] if y < b - 1 else SK[2])
    # neck + head
    for y in range(b - 22, b - 4):
        for xx in range(ox + 8, ox + 17):
            if math.hypot((xx + 0.5 - (ox + 12)) / 4.5, (y + 0.5 - (b - 15)) / 9) <= 1:
                L.put(xx, y, SK[0] if xx < ox + 11 else SK[1])
    # eye stalks
    L.line(ox + 11, b - 22, ox + 10, b - 28, SK[1], 0.5)
    L.line(ox + 14, b - 22, ox + 16, b - 28, SK[1], 0.5)
    L.disc(ox + 10.5, b - 28.5, 1.8, WHITE)
    L.disc(ox + 16.5, b - 28.5, 1.8, WHITE)
    L.put(ox + 11, b - 28, OUT)
    L.put(ox + 17, b - 28, OUT)
    # postman cap
    L.rect(ox + 8, b - 25, ox + 16, b - 22, hx("4f8fd8"))
    L.rect(ox + 14, b - 22, ox + 19, b - 22, hx("2f5a9e"))
    L.put(ox + 11, b - 24, GOLD[1])
    # smile
    L.put(ox + 13, b - 16, OUT)
    L.put(ox + 14, b - 15, OUT)
    L.put(ox + 15, b - 16, OUT)
    # shell spiral
    scx, scy, sr = ox - 5, b - 17, 12
    for y in range(int(scy - sr), int(scy + sr) + 1):
        for xx in range(int(scx - sr), int(scx + sr) + 1):
            dx, dy = xx + 0.5 - scx, y + 0.5 - scy
            d = math.hypot(dx, dy)
            if d > sr:
                continue
            a = math.atan2(dy, dx)
            spiral = (d / sr * 3.0 - a / (2 * math.pi)) % 1.0
            c = SH[1]
            if spiral < 0.18:
                c = SH[3]
            elif spiral < 0.4:
                c = SH[0] if dx + dy < 0 else SH[1]
            elif dx + dy > 6:
                c = SH[2]
            L.put(xx, y, c)
    # satchel
    L.rect(ox + 1, b - 12, ox + 8, b - 6, WOOD[1])
    L.rect(ox + 1, b - 12, ox + 8, b - 11, WOOD[0])
    L.line(ox + 2, b - 12, ox + 9, b - 20, WOOD[2], 0.5)
    L.put(ox + 5, b - 9, CREAM[0])
    outline(L, OUT)
    if face < 0:
        L.img = L.img.transpose(Image.FLIP_LEFT_RIGHT)
        L.px = L.img.load()
    tint(L, m, add)
    cv.over(L, int(x) - 24, int(by) - 38)


def puffball(cv, x, by, m=(1, 1, 1), add=(0, 0, 0), jump=0):
    if npc_sprite(cv, "Puffball", x, by - jump, False, m, add, 2 if jump else 0):
        return
    L = Cv(28, 32)
    ox, b = 14, 30 - jump
    for y in range(b - 20, b - 2):
        for xx in range(ox - 10, ox + 11):
            nx, ny = (xx + 0.5 - ox) / 10, (y + 0.5 - (b - 11)) / 9
            if nx * nx + ny * ny > 1:
                continue
            k = nx * -0.6 + ny * -0.8
            c = WHITE if k > 0.35 else (hx("f0ece4") if k > -0.35 else hx("cfc6bc"))
            if hsh(xx, y, 77) < 0.07:
                c = hx("e2dccf")
            L.put(xx, y, c)
    L.rect(ox - 5, b - 13, ox - 4, b - 11, OUT)
    L.rect(ox + 3, b - 13, ox + 4, b - 11, OUT)
    L.put(ox - 5, b - 13, WHITE)
    L.put(ox + 3, b - 13, WHITE)
    L.rect(ox - 1, b - 8, ox + 1, b - 7, hx("c0504a"))
    L.put(ox - 7, b - 9, hx("ff9aa8"))
    L.put(ox + 6, b - 9, hx("ff9aa8"))
    L.put(ox, b - 21, hx("cfc6bc"))
    L.put(ox + 1, b - 22, hx("cfc6bc"))
    L.rect(ox - 5, b - 2, ox - 2, b, WOOD[2])
    L.rect(ox + 2, b - 2, ox + 5, b, WOOD[2])
    outline(L, OUT)
    tint(L, m, add)
    cv.over(L, int(x) - 14, int(by) - 30)


def old_wick(cv, x, by, m=(1, 1, 1), add=(0, 0, 0), face=1, lamp=True):
    if npc_sprite(cv, "Old Wick", x, by, face < 0, m, add, 0):
        if lamp:
            halo(cv, int(x) + 10 * face, int(by) - 35, 14, hx("ffc850"), 0.4, 3)
        return int(x) + 10 * face, int(by) - 35
    L = Cv(40, 64)
    ox, b = 16, 62
    COAT = [hx("8e88c8"), hx("5c5ea0"), hx("3e3e78"), hx("2a2a54")]
    # legs
    L.rect(ox - 4, b - 10, ox - 2, b, hx("4a3a50"))
    L.rect(ox + 2, b - 10, ox + 4, b, hx("4a3a50"))
    L.rect(ox - 5, b, ox - 1, b, OUT)
    L.rect(ox + 1, b, ox + 5, b, OUT)
    # long coat
    for y in range(b - 30, b - 8):
        t = (y - (b - 30)) / 22
        hw = 5 + 3 * t
        for xx in range(int(ox - hw), int(ox + hw) + 1):
            u = (xx + 0.5 - ox) / hw
            L.put(xx, y, COAT[0] if u < -0.5 else (COAT[2] if u > 0.5 else COAT[1]))
    for y in range(b - 28, b - 10, 4):
        L.put(ox + 1, y, GOLD[1])
    # head: candle-stub face with drippy wax cap and a wick
    for y in range(b - 42, b - 30):
        for xx in range(ox - 5, ox + 6):
            u = (xx + 0.5 - ox) / 5
            L.put(xx, y, CREAM[0] if u < -0.3 else (CREAM[1] if u < 0.5 else CREAM[2]))
    for xx in range(ox - 6, ox + 7):
        drip = 2 + (2 if xx in (ox - 4, ox + 3) else 0)
        for y in range(b - 44, b - 42 + drip):
            L.put(xx, y, hx("f4f0e8") if xx < ox + 3 else hx("d8d0c4"))
    L.put(ox + 2, b - 37, OUT)
    L.put(ox - 2, b - 37, OUT)
    L.rect(ox - 1, b - 34, ox + 1, b - 34, hx("b06050"))
    # pole
    px_ = ox + 10
    for y in range(b - 56, b + 1):
        L.put(px_, y, WOOD[1])
    L.line(px_, b - 56, px_ + 6, b - 56, WOOD[2], 0.5)
    L.line(ox + 5, b - 26, px_, b - 28, COAT[1], 1.3)
    L.disc(px_, b - 28, 1.6, CREAM[1])
    outline(L, OUT)
    # wick flame
    L.put(ox, b - 45, OUT)
    L.put(ox, b - 46, hx("ffd23f"))
    L.put(ox, b - 47, hx("fff3a6"))
    if face < 0:
        L.img = L.img.transpose(Image.FLIP_LEFT_RIGHT)
        L.px = L.img.load()
    tint(L, m, add)
    cv.over(L, int(x) - 16, int(by) - 62)
    lx = int(x) - 16 + (px_ + 6 if face > 0 else 40 - 1 - (px_ + 6))
    if lamp:
        hand_lantern(cv, lx, int(by) - 62 + b - 55)
    return lx, int(by) - 62 + (b - 46)


# Duskmantle ---------------------------------------------------------------------------------
def duskmantle(cv, cx, cy, S, mode="color", spread=1.0, tilt=0.0):
    """Duskmantle, the dusk-moth queen. S = wingspan in px. mode 'color' or 'silhouette'."""
    sil = mode == "silhouette"
    W = VIOLET
    HIND = [hx("f09ae0"), hx("c0509e"), hx("8a2e7a"), hx("561c56"), hx("2a0c30")]
    L = cv.layer()
    fringe = hx("e8d4ff")
    sil_c, sil_edge = hx("1c1030"), hx("2e1a48")

    def wing(side, ang, length, width, hind):
        a = math.radians(ang)
        ca, sa = math.cos(a), math.sin(a)
        rx0 = cx + side * S * 0.03
        ry0 = cy + (S * 0.04 if hind else -S * 0.02)
        ext = 1.55 if hind else 1.0
        bb = length * ext + width * 1.6
        for y in range(int(ry0 - bb), int(ry0 + bb) + 1):
            for x in range(int(rx0 - bb), int(rx0 + bb) + 1):
                dx, dy = (x + 0.5 - rx0) * side, y + 0.5 - ry0
                u = dx * ca + dy * sa
                v = -dx * sa + dy * ca
                if u < 0 or u > length * ext:
                    continue
                t = u / length
                tail = False
                if hind:
                    if t <= 1:
                        hw = width * math.sin(math.pi * min(1.0, t ** 0.62)) ** 0.7
                        lo, hi = -hw * 0.85, hw * 1.0
                    else:
                        tt = (t - 1) / 0.55
                        tw = width * 0.2 * (1 - tt) + 0.6
                        vc = -width * 0.5 * tt * tt + width * 0.1
                        lo, hi = vc - tw, vc + tw
                        tail = True
                    if t <= 1 and t > 0.85:
                        # blend into the tail base
                        lo = min(lo, width * 0.1 - width * 0.2)
                else:
                    if t < 0.72:
                        hw = width * (0.2 + 0.8 * (t / 0.72) ** 0.8)
                    else:
                        hw = width * math.sqrt(max(0.0, 1 - ((t - 0.72) / 0.28) ** 2))
                    lo, hi = -hw * 0.45 - width * 0.12 * math.sin(math.pi * t), hw * 1.25
                if v < lo or v > hi:
                    continue
                edge = min(v - lo, hi - v)
                if not hind and t > 0.72:
                    edge = min(edge, (1 - t) * length * 1.2)
                if sil:
                    c = sil_edge if edge < 1.5 and t > 0.3 else sil_c
                else:
                    Wp = HIND if hind else W
                    if t < 0.16:
                        c = Wp[3]
                    elif t < 0.38:
                        c = Wp[2]
                    elif t < 0.8:
                        c = Wp[1]
                    else:
                        c = Wp[2]
                    if tail:
                        c = Wp[2] if t < 1.4 else Wp[0]
                    band = 0.6 + 0.035 * math.sin(v * 0.35)
                    if abs(t - band) * length < 1.1 and not tail:
                        c = hx("d8b8ff") if not hind else hx("f0b0e8")
                    if abs(t - 0.34 - 0.02 * math.sin(v * 0.5)) * length < 0.8 and not tail:
                        c = Wp[3]
                    for kv in (-0.15, 0.35, 0.85):
                        if abs(v - kv * width * min(1, t)) < 0.5 and 0.18 < t < 0.78 and not tail:
                            c = Wp[3] if c not in (hx("d8b8ff"), hx("f0b0e8")) else c
                    if edge < 2.2 and t > 0.22:
                        sc_ = hsh(int(u / 3) + int(v / 3) * 7, side, 91 + (5 if hind else 0)) > 0.45
                        c = fringe if sc_ else Wp[0]
                    if edge < 0.9:
                        c = Wp[4]
                L.put(x, y, c)
        return rx0, ry0, ca, sa

    def to_world(side, rx0, ry0, ca, sa, u, v):
        dx = u * ca - v * sa
        dy = u * sa + v * ca
        return rx0 + side * dx, ry0 + dy

    hs = spread
    spots = []
    # hind wings first
    for side in (-1, 1):
        rx0, ry0, ca, sa = wing(side, 56 * hs + tilt * side, S * 0.27, S * 0.13, True)
        ex, ey = to_world(side, rx0, ry0, ca, sa, S * 0.27 * 0.55, S * 0.13 * 0.1)
        spots.append((ex, ey, S * 0.03))
    for side in (-1, 1):
        rx0, ry0, ca, sa = wing(side, -28 * hs + tilt * side, S * 0.5, S * 0.17, False)
        ex, ey = to_world(side, rx0, ry0, ca, sa, S * 0.5 * 0.7, S * 0.17 * 0.35)
        spots.append((ex, ey, S * 0.055))
    # eye-spots
    for (ex, ey, er) in spots:
        for y in range(int(ey - er) - 1, int(ey + er) + 2):
            for x in range(int(ex - er) - 1, int(ex + er) + 2):
                d = math.hypot(x + 0.5 - ex, y + 0.5 - ey)
                if d > er:
                    continue
                if sil:
                    c = hx("ff8ad8") if d < er * 0.35 else (hx("8a2ea0") if d < er * 0.72 else None)
                else:
                    if er < S * 0.04:
                        c = HIND[4] if d > er * 0.8 else (hx("ffd23f") if d > er * 0.3 else hx("6a3a10"))
                    else:
                        c = W[4] if d > er * 0.86 else (hx("ff7ad0") if d > er * 0.66 else (hx("f2dcff") if d > er * 0.3 else hx("ff5ab8")))
                if c is not None:
                    L.put(x, y, c)
    # body
    body = sil_c if sil else W[3]
    body_hi = sil_edge if sil else hx("8a5cc0")
    for y in range(int(cy - S * 0.08), int(cy + S * 0.26)):
        for x in range(int(cx - S * 0.06), int(cx + S * 0.06) + 1):
            dx, dy = x + 0.5 - cx, y + 0.5 - cy
            fuzz = 1 + 0.16 * (hsh(x, y, 7) - 0.5)
            thorax = dy < S * 0.06
            if thorax:
                ok = math.hypot(dx / (S * 0.055), (dy - S * 0.0) / (S * 0.08)) <= fuzz
            else:
                ok = math.hypot(dx / (S * 0.042), (dy - S * 0.14) / (S * 0.12)) <= 1
            if not ok:
                continue
            c = body_hi if dx < -S * 0.012 else body
            if not sil and not thorax and int(dy) % max(3, int(S / 50)) == 0:
                c = W[4]
            if not sil and thorax and hsh(x, y, 5) < 0.3:
                c = hx("b090e0")
            if thorax and dy < -S * 0.035:
                c = (fringe if hsh(x, y, 9) < 0.7 else W[0]) if not sil else sil_edge
            L.put(x, y, c)
    # head
    hy = cy - S * 0.1
    hr = S * 0.035
    L.disc(cx, hy, hr, sil_c if sil else W[3])
    # antennae (feathery)
    for side in (-1, 1):
        prev = None
        for i in range(21):
            t = i / 20
            ax = cx + side * (hr * 0.5 + S * 0.14 * t)
            ay = hy - hr - S * 0.2 * t + S * 0.07 * t * t
            L.put(ax, ay, sil_c if sil else W[3])
            if i % 2 == 0 and 0.15 < t < 0.95:
                ln = 2 + int(3 * math.sin(t * math.pi) * S / 160)
                for k in range(1, ln + 1):
                    L.put(ax + side * k * 0.4, ay - k, sil_c if sil else W[2])
    outline(L, hx("12081e") if sil else OUT)
    # tiara + eyes on top of outline
    for k, dx in enumerate((-2, 0, 2)):
        h = 3 if dx == 0 else 2
        for j in range(h):
            L.put(cx + dx * S / 128, hy - hr - 1 - j, hx("e0c0ff") if not sil else hx("6a3c90"))
    eye = hx("ff60d0") if sil else hx("ffe040")
    ew = max(1, int(S / 110))
    for side in (-1, 1):
        ex = cx + side * hr * 0.45
        for k in range(ew + 1):
            L.put(ex + side * k, hy + (k * 0.5 if True else 0), eye)
        if not sil and ew > 1:
            L.put(ex - side, hy - 1, OUT)  # angry brow
    if not sil:
        L.put(cx, hy - hr * 0.55, hx("ff5ab8"))
        L.put(cx, hy - hr * 0.55 - 1, hx("ffb0e0"))
    cv.over(L)
    for (ex, ey, er) in spots:
        if sil:
            halo(cv, ex, ey, er * 2.4, hx("c040ff"), 0.2, 3)
    return spots


# Image resource writing --------------------------------------------------------------------
def det_id(*parts):
    return hashlib.md5(("MushroomMeadow/" + "/".join(parts)).encode("utf-8")).hexdigest()


def write_image(name, cv, parallax=None, repeat_x=False):
    img = cv.img if isinstance(cv, Cv) else cv
    path = SPRITES / f"{name}.image.json"
    meta_path = SPRITES / f"{name}.image.json.meta"
    doc = json.loads((SPRITES / "Morning Sky.image.json").read_text(encoding="utf-8"))
    frame_id = det_id(name, "frame")
    w, h = img.size
    rel = f"{name}.spritedata/frames/{frame_id}.png"
    doc["canvas"]["width"] = w
    doc["canvas"]["height"] = h
    doc["origin"] = {"x": 0, "y": 0, "space": "pixels"}
    doc["usage"]["allowed"] = "background"
    if parallax is not None or repeat_x:
        doc["usage"]["background"] = {"mode": "fixed", "repeatX": bool(repeat_x), "repeatY": False,
                                      "parallaxX": parallax if parallax is not None else 1, "parallaxY": 1}
    else:
        doc["usage"].pop("background", None)
    doc["frames"] = [{"id": frame_id, "name": f"{name} 01", "durationMilliseconds": 110, "source": rel,
                      "sourceRectangle": {"x": 0, "y": 0, "width": w, "height": h}}]
    doc["tags"] = [{"id": det_id(name, "tag"), "name": "Still", "startFrameId": frame_id, "endFrameId": frame_id,
                    "direction": "forward", "loop": True}]
    layer = doc["layers"][0]
    layer["id"] = det_id(name, "layer")
    layer["cels"] = [dict(layer["cels"][0], frameId=frame_id, source=rel)]
    doc["layers"] = [layer]
    doc["collisionShapes"] = []
    frames_dir = SPRITES / f"{name}.spritedata" / "frames"
    frames_dir.mkdir(parents=True, exist_ok=True)
    for old in frames_dir.glob("*.png"):
        if old.stem != frame_id:
            old.unlink()
    img.save(frames_dir / f"{frame_id}.png", optimize=True)
    path.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
    meta = {"guid": det_id(name, "guid"), "kind": "Image", "displayName": name,
            "createdUtc": "2026-10-11T00:00:00Z", "modifiedUtc": "2026-10-11T00:00:00Z"}
    meta_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    PREVIEW.mkdir(parents=True, exist_ok=True)
    prev = img
    if img.mode == "RGBA" and img.getextrema()[3][0] < 255:
        bg = Image.new("RGBA", img.size, (60, 70, 96, 255))
        bgp = bg.load()
        for y in range(h):
            for x in range(w):
                if ((x // 16) + (y // 16)) % 2:
                    bgp[x, y] = (74, 84, 112, 255)
        bg.alpha_composite(img)
        prev = bg
    prev.save(PREVIEW / f"{name}.png")
    print(f"{name}: {w}x{h} -> {rel}")


# Shared scenery ----------------------------------------------------------------------------
DUSK_SKY = [(0.0, hx("1a1840")), (0.42, hx("43306e")), (0.72, hx("a0566e")), (0.9, hx("e88a6a")), (1.0, hx("ffbe78"))]
DUSK_DIM = (0.62, 0.58, 0.82)


def meadow_ground(cv, base, terms, body, rim, dark, seed=0, tufts=True):
    ridge(cv, base, terms, body, rim, dark, rim=2, shade_from=10, seed=seed)
    if tufts:
        for x in range(0, cv.w, 3):
            top = int(round(base + sum(a * math.sin(2 * math.pi * k * x / 640 + p) for (a, k, p) in terms)))
            if hsh(x, 0, seed + 3) < 0.45:
                h = 1 + int(hsh(x, 1, seed + 3) * 3)
                for j in range(h):
                    cv.put(x, top - 1 - j, rim)


def lamp_post(cv, x, by, h=26, dim=DUSK_DIM, lit=True):
    for y in range(int(by - h), int(by) + 1):
        cv.put(x, y, mul(hx("3e3a50"), dim))
        cv.put(x + 1, y, mul(hx("2a2638"), dim))
    cv.rect(x - 2, by - h - 6, x + 3, by - h - 1, mul(hx("3e3a50"), dim))
    cv.rect(x - 1, by - h - 5, x + 2, by - h - 2, hx("ffe08a") if lit else hx("4a4466"))
    cv.rect(x - 3, by - h - 7, x + 4, by - h - 7, mul(hx("2a2638"), dim))
    if lit:
        halo(cv, x + 0.5, by - h - 3.5, 22, hx("ffc850"), 0.4, 4)


def bunting(cv, x0, y0, x1, y1, sag, colors, seed=0, step=7):
    n = int(math.hypot(x1 - x0, y1 - y0) / step)
    for i in range(int(math.hypot(x1 - x0, y1 - y0)) + 1):
        t = i / max(1, math.hypot(x1 - x0, y1 - y0))
        cv.put(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t + sag * 4 * t * (1 - t), OUT)
    for k in range(1, n):
        t = k / n
        x = x0 + (x1 - x0) * t
        y = y0 + (y1 - y0) * t + sag * 4 * t * (1 - t)
        c = colors[(k + seed) % len(colors)]
        for j in range(4):
            for dx in range(-2 + j // 2, 3 - (j + 1) // 2):
                cv.put(x + dx, y + 1 + j, c)


def string_lights(cv, x0, y0, x1, y1, sag, step=9, colors=(hx("ffe08a"), hx("ff9a7a"), hx("a8e0ff"), hx("c8ff9a"))):
    L = math.hypot(x1 - x0, y1 - y0)
    for i in range(int(L) + 1):
        t = i / max(1, L)
        cv.put(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t + sag * 4 * t * (1 - t), hx("2a2236"))
    for k in range(1, int(L / step)):
        t = k * step / L
        x = x0 + (x1 - x0) * t
        y = y0 + (y1 - y0) * t + sag * 4 * t * (1 - t) + 2
        c = colors[k % len(colors)]
        halo(cv, x + 0.5, y + 0.5, 6, c, 0.35, 2)
        cv.put(x, y, c)
        cv.put(x, y + 1, mul(c, (0.8, 0.8, 0.8)))


# Panels ------------------------------------------------------------------------------------
def intro_1():
    cv = Cv(640, 360)
    sky(cv, DUSK_SKY, 16, 0, 300, 1.1)
    stars(cv, 11, 170, 0.006)
    # tree glow behind everything
    halo(cv, 320, 150, 230, hx("ffb860"), 0.32, 6, 0.8)
    ridge(cv, 238, [(10, 2, 0.4), (6, 5, 1.3), (3, 11, 0.2)], hx("5a4a86"), hx("7a64a6"))
    ridge(cv, 262, [(9, 3, 2.1), (5, 7, 0.6), (2, 15, 1.0)], hx("3c3a6c"), hx("54508a"))
    great_tree(cv, 320, 318, 1.0, "dusk", 5)
    meadow_ground(cv, 304, [(4, 2, 0.5), (2, 6, 1.4)], hx("2a4450"), hx("3e6466"), hx("1e3240"), seed=5)
    # path to the tree
    for y in range(306, 360):
        t = (y - 306) / 54
        hw = 10 + 60 * t
        cxp = 320 + 14 * math.sin(t * 2.4)
        for x in range(int(cxp - hw), int(cxp + hw) + 1):
            c = hx("5e5070") if (x * 3 + y * 7) % 11 else hx("4a3e5e")
            if abs(x - cxp) > hw - 2:
                c = hx("3e3450")
            cv.put(x, y, c)
    halo(cv, 320, 318, 90, hx("ffc870"), 0.3, 4, 0.35)
    mush_house(cv, 96, 330, 62, 72, RED, DUSK_DIM, seed=1, chimney=True)
    mush_house(cv, 186, 346, 42, 48, [hx("f0a05a"), hx("d8743b"), hx("a14a2f"), hx("6e2c2a")], DUSK_DIM, seed=2)
    mush_house(cv, 536, 334, 66, 76, [hx("b07ad8"), hx("8a52b8"), hx("643a8c"), hx("402460")], DUSK_DIM, seed=3, chimney=True)
    mush_house(cv, 448, 350, 40, 46, RED, DUSK_DIM, seed=4)
    lamp_post(cv, 250, 346, 30)
    lamp_post(cv, 392, 346, 30)
    fireflies(cv, 21, 26, (20, 230, 620, 330))
    vignette(cv, hx("0c0a20"), 0.5)
    return cv


def caption_band(cv, h=56, c=hx("0a0818"), amax=0.55):
    for y in range(cv.h - h, cv.h):
        t = (y - (cv.h - h)) / h
        q = math.ceil(t * 4) / 4
        for x in range(cv.w):
            cv.mix(x, y, c, amax * q)


def moon(cv, cx, cy, r, body=hx("f6eedc"), shade=hx("d8cbe0"), crater=hx("e2d6d6"), glow=hx("d8c8ff")):
    halo(cv, cx, cy, r * 2.1, glow, 0.32, 6)
    for y in range(int(cy - r) - 1, int(cy + r) + 2):
        for x in range(int(cx - r) - 1, int(cx + r) + 2):
            dx, dy = x + 0.5 - cx, y + 0.5 - cy
            d = math.hypot(dx, dy)
            if d > r:
                continue
            c = body
            k = (dx * 0.6 + dy * 0.5) / r
            if k > 0.55 and bayer(x, y) < (k - 0.55) * 3:
                c = shade
            cv.put(x, y, c)
    for i in range(9):
        a = hsh(i, 1, 301) * 6.283
        rr = hsh(i, 2, 301) * r * 0.75
        ccx, ccy = cx + math.cos(a) * rr, cy + math.sin(a) * rr
        cr = 3 + hsh(i, 3, 301) * r * 0.12
        for y in range(int(ccy - cr), int(ccy + cr) + 1):
            for x in range(int(ccx - cr), int(ccx + cr) + 1):
                d = math.hypot(x + 0.5 - ccx, y + 0.5 - ccy)
                if d <= cr and math.hypot(x + 0.5 - cx, y + 0.5 - cy) < r - 1:
                    cv.put(x, y, crater if d < cr - 1 or (x - ccx) < 0 else shade)


def rays(cv, cx, cy, r, n, c, amax=0.25, width=0.35, phase=0.0, r0=0):
    for y in range(max(0, int(cy - r)), min(cv.h, int(cy + r) + 1)):
        for x in range(max(0, int(cx - r)), min(cv.w, int(cx + r) + 1)):
            dx, dy = x + 0.5 - cx, y + 0.5 - cy
            d = math.hypot(dx, dy)
            if d >= r or d < r0:
                continue
            a = (math.atan2(dy, dx) / (2 * math.pi) * n + phase) % 1.0
            if a < width:
                t = 1 - d / r
                q = math.ceil(t * 4) / 4
                cv.mix(x, y, c, amax * q)


def sparkles(cv, seed, n, box, colors=(hx("fff3a6"), WHITE, hx("ffd86a"))):
    x0, y0, x1, y1 = box
    for i in range(n):
        x = int(x0 + hsh(i, 1, seed) * (x1 - x0))
        y = int(y0 + hsh(i, 2, seed) * (y1 - y0))
        c = colors[i % len(colors)]
        cv.put(x, y, c)
        if hsh(i, 3, seed) < 0.35:
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                cv.mix(x + dx, y + dy, c, 0.6)
            if hsh(i, 4, seed) < 0.4:
                for dx, dy in ((2, 0), (-2, 0), (0, 2), (0, -2)):
                    cv.mix(x + dx, y + dy, c, 0.35)


def mushroom(cv, x, by, h, r, cap=None, m=(1, 1, 1), add=(0, 0, 0), lean=0.0, seed=0, silhouette=None):
    cap = cap or RED
    L = cv.layer()
    top = by - h
    sw = max(2.0, r * 0.28)
    for y in range(int(top), int(by) + 1):
        t = (by - y) / max(1, h)
        cxs = x + lean * t * t * h
        hw = sw * (1.15 - 0.25 * t)
        for xx in range(int(cxs - hw), int(cxs + hw) + 1):
            u = (xx + 0.5 - cxs) / hw
            L.put(xx, y, CREAM[0] if u < -0.45 else (CREAM[2] if u > 0.4 else CREAM[1]))
    ccx = x + lean * h
    ry = r * 0.62
    for y in range(int(top - ry) - 1, int(top + 3)):
        for xx in range(int(ccx - r) - 1, int(ccx + r) + 2):
            nx, ny = (xx + 0.5 - ccx) / r, (y + 0.5 - top) / ry
            if y <= top:
                if nx * nx + ny * ny > 1:
                    continue
            elif abs(nx) > 1 - 0.1 * (y - top):
                continue
            if y > top - 1:
                c = cap[3]
            elif nx < -0.25 and ny < -0.35:
                c = cap[0] if bayer(xx, y) < 0.7 else cap[1]
            elif nx > 0.45:
                c = cap[2] if (nx > 0.7 or bayer(xx, y) < 0.5) else cap[1]
            else:
                c = cap[1]
            L.put(xx, y, c)
    for i in range(6):
        a = -2.7 + i * 0.95 + (hsh(i, seed, 51) - 0.5) * 0.4
        rr = 0.5 + hsh(i, seed, 52) * 0.3
        sx, sy = ccx + math.cos(a) * r * rr, top + math.sin(a) * ry * rr - ry * 0.08
        if sy > top - 2:
            continue
        s = max(1.0, r * 0.11 * (0.7 + hsh(i, seed, 53) * 0.6))
        for yy in range(int(sy - s), int(sy + s) + 1):
            for xx in range(int(sx - s * 1.3) - 1, int(sx + s * 1.3) + 2):
                if math.hypot((xx + 0.5 - sx) / 1.3, yy + 0.5 - sy) <= s and L.get(xx, yy)[3]:
                    L.put(xx, yy, CREAM[0] if yy < sy else CREAM[2])
    if silhouette is not None:
        for yy in range(L.h):
            for xx in range(L.w):
                if L.px[xx, yy][3]:
                    L.px[xx, yy] = silhouette
        outline(L, silhouette)
    else:
        outline(L, OUT)
        tint(L, m, add)
    cv.over(L)


def spire(cv, x, by, h, w, c, rim=None, seed=0, nest=False):
    """Crooked dark spire (Duskmantle Hollow)."""
    for y in range(int(by - h), int(by) + 1):
        t = (by - y) / h
        hw = w * (1 - t) ** 0.9 * 0.5 + 0.6
        cx_ = x + math.sin(t * 3.0 + seed) * w * 0.35 * t
        for xx in range(int(cx_ - hw), int(cx_ + hw) + 1):
            cc = c
            if rim is not None and xx < cx_ - hw + 1.5:
                cc = rim
            cv.put(xx, y, cc)
        # side thorns
        if hsh(int(y), seed, 71) < 0.06 and t < 0.85:
            sd = 1 if hsh(int(y), seed, 72) < 0.5 else -1
            for k in range(int(2 + hw)):
                cv.put(cx_ + sd * (hw + k), y - k * 0.6, c)


def path_perspective(cv, pts, w0, w1, col, edge, seed=0):
    """Winding path from pts[0] (near, width w0) to pts[-1] (far, width w1) via Catmull-Rom points."""
    samp = []
    for i in range(len(pts) - 1):
        p0 = pts[max(0, i - 1)]
        p1, p2 = pts[i], pts[i + 1]
        p3 = pts[min(len(pts) - 1, i + 2)]
        for k in range(40):
            t = k / 40
            t2, t3 = t * t, t * t * t
            x = 0.5 * ((2 * p1[0]) + (-p0[0] + p2[0]) * t + (2 * p0[0] - 5 * p1[0] + 4 * p2[0] - p3[0]) * t2 + (-p0[0] + 3 * p1[0] - 3 * p2[0] + p3[0]) * t3)
            y = 0.5 * ((2 * p1[1]) + (-p0[1] + p2[1]) * t + (2 * p0[1] - 5 * p1[1] + 4 * p2[1] - p3[1]) * t2 + (-p0[1] + 3 * p1[1] - 3 * p2[1] + p3[1]) * t3)
            samp.append((x, y))
    samp.append(pts[-1])
    n = len(samp)
    for i, (x, y) in enumerate(samp):
        w = w0 + (w1 - w0) * (i / (n - 1))
        for xx in range(int(x - w), int(x + w) + 1):
            for yy in range(int(y - 1), int(y + 2)):
                e = abs(xx + 0.5 - x) > w - max(1, w * 0.18)
                cv.put(xx, yy, edge if e else (col if hsh(xx, yy, seed) > 0.12 else edge))


def dark_grass_front(cv, base, color, seed=0, hmax=14):
    for x in range(cv.w):
        h = int(3 + hsh(x, 0, seed) * hmax * (0.5 + 0.5 * math.sin(x * 0.07 + seed)))
        for y in range(base - h, cv.h):
            cv.put(x, y, color)


def intro_2():
    cv = Cv(640, 360)
    sky(cv, [(0.0, hx("0c0a22")), (0.5, hx("261a4c")), (0.82, hx("43286a")), (1.0, hx("6a3a7a"))], 14, 0, 360, 1.0)
    stars(cv, 23, 300, 0.008)
    moon(cv, 400, 150, 98)
    # wisps of cloud across the moon
    for (cy, x0, x1, th) in [(206, 250, 560, 3), (220, 300, 620, 2), (96, 300, 470, 2)]:
        for x in range(x0, x1):
            t = (x - x0) / (x1 - x0)
            hh = th * math.sin(t * math.pi)
            for y in range(int(cy - hh), int(cy + hh) + 1):
                cv.mix(x, y, hx("3a2458"), 0.75)
    duskmantle(cv, 392, 142, 330, "silhouette", 1.0, 0)
    ridge(cv, 300, [(12, 2, 0.4), (6, 5, 2.3), (3, 13, 0.4)], hx("1a1430"), hx("2a2048"))
    ridge(cv, 322, [(6, 3, 1.0), (3, 9, 0.2)], hx("100c20"), hx("1c1634"))
    great_tree(cv, 112, 322, 0.36, "night", 5, glow=1.4)
    for (hxp, hw) in [(64, 16), (160, 14), (186, 11)]:
        mush_house(cv, hxp, 330, hw, hw * 1.1, RED, (0.3, 0.28, 0.45), seed=hxp, glow=True)
    caption_band(cv)
    vignette(cv, hx("06040e"), 0.55)
    return cv


def intro_3():
    cv = Cv(640, 360)
    sky(cv, [(0.0, hx("120c26")), (0.45, hx("34264e")), (0.8, hx("5a4068")), (1.0, hx("7a5a72"))], 14, 0, 300, 1.0)
    stars(cv, 31, 120, 0.003)
    ridge(cv, 246, [(10, 2, 1.4), (5, 6, 0.3)], hx("3a3054"), hx("4c4068"))
    ridge(cv, 274, [(8, 3, 0.2), (4, 8, 1.9)], hx("2a2442"), hx("3a3258"))
    great_tree(cv, 176, 330, 0.95, "gloom", 0)
    meadow_ground(cv, 314, [(4, 2, 0.2), (2, 7, 1.0)], hx("2a2a40"), hx("3a3a56"), hx("1e1c30"), seed=9)
    # Duskmantle flying off with the five Glowspores
    gx, gy = 500, 92
    sockets = tree_lantern_points(176, 330, 0.95)
    ends = [(gx - 70, gy + 52), (gx - 40, gy + 64), (gx - 6, gy + 70), (gx + 28, gy + 62), (gx + 58, gy + 48)]
    for j, ((sx, sy), (ex, ey)) in enumerate(zip(sockets, ends)):
        mx, my = (sx + ex) / 2, min(sy, ey) - 60 - j * 8
        # streak trail (fading dotted arc)
        for k in range(46):
            t = k / 46
            x = (1 - t) ** 2 * sx + 2 * (1 - t) * t * mx + t * t * ex
            y = (1 - t) ** 2 * sy + 2 * (1 - t) * t * my + t * t * ey
            if t > 0.15:
                a_ = 0.12 + 0.6 * t
                cv.mix(x, y, hx("ffe08a"), a_)
                if t > 0.5:
                    cv.mix(x, y + 1, hx("ffb84a"), 0.5 * a_)
                    cv.mix(x + 1, y, hx("ffb84a"), 0.3 * a_)
        glowspore(cv, ex, ey, 5, True, 22, 0.45)
    # gloom tendrils connecting the spores to the moth
    for (ex, ey) in ends:
        for k in range(20):
            t = k / 20
            x = ex + (gx - ex) * t + math.sin(t * 9 + ex) * 3
            y = ey + (gy + 10 - ey) * t
            cv.mix(x, y, hx("b070ff"), 0.45)
    duskmantle(cv, gx, gy, 210, "color", 1.05, -6)
    # creeping gloom mist
    for y in range(230, 360):
        for x in range(640):
            n = math.sin(x * 0.03 + y * 0.11) + math.sin(x * 0.011 - y * 0.05 + 2)
            t = (y - 230) / 130
            a = 0.0
            if n > 0.6 - t:
                a = 0.22 + (0.15 if n > 1.2 - t else 0)
            cv.mix(x, y, hx("4a2a70"), a)
    for (hxp, by_, hw, hh, cap) in [(70, 342, 50, 56, RED), (318, 348, 40, 44, RED), (430, 340, 54, 60, VIOLET[1:])]:
        mush_house(cv, hxp, by_, hw, hh, cap if len(cap) == 4 else [VIOLET[0], VIOLET[1], VIOLET[2], VIOLET[3]],
                   (0.5, 0.48, 0.66), seed=hxp, lit=0.25)
    # falling leaves
    for i in range(18):
        x = 120 + hsh(i, 1, 41) * 420
        y = 120 + hsh(i, 2, 41) * 190
        c = [hx("4f4b70"), hx("6e6a8c"), hx("383458")][i % 3]
        cv.put(x, y, c)
        cv.put(x + 1, y, c)
        cv.put(x + 1, y + 1, c)
    caption_band(cv)
    vignette(cv, hx("0a0618"), 0.6)
    return cv


def intro_4():
    cv = Cv(320, 180)
    sky(cv, [(0.0, hx("181430")), (0.6, hx("3e2c5a")), (1.0, hx("7a5070"))], 12, 0, 150, 1.0)
    stars(cv, 43, 80, 0.006)
    moon(cv, 150, 34, 11, glow=hx("b8a8e8"))
    cv.disc(155, 31, 10, hx("1e1838"))
    ridge(cv, 128, [(4, 2, 0.4), (2, 7, 1.0)], hx("2e2848"), hx("3c3458"))
    for (hxp, hw) in [(26, 22), (66, 18)]:
        mush_house(cv, hxp, 146, hw, hw * 1.1, RED, (0.5, 0.48, 0.66), seed=hxp)
    great_tree(cv, 292, 166, 1.45, "gloom", 0)
    meadow_ground(cv, 148, [(1.5, 2, 0.4), (1, 7, 1.0)], hx("283040"), hx("3a4656"), hx("1c2230"), seed=13, tufts=True)
    # lantern light pool on the ground
    halo(cv, 146, 150, 80, hx("ffb860"), 0.35, 5, 0.32)
    pip(cv, 124, 157, "Explorer Idle", 0, False, (0.82, 0.76, 0.86), (10, 4, 0))
    hand = elder_morel(cv, 168, 157, -1, True, (0.84, 0.78, 0.86), (10, 4, 0))
    halo(cv, hand[0] + 0.5, hand[1] + 6, 60, hx("ffc860"), 0.34, 5)
    hand_lantern(cv, hand[0], hand[1] + 1, True)
    fireflies(cv, 47, 9, (10, 60, 220, 140))
    sparkles(cv, 49, 6, (120, 118, 180, 140), (hx("fff3a6"),))
    vignette(cv, hx("080614"), 0.5)
    big = scale2(cv)
    caption_band(big)
    return big


def intro_5():
    cv = Cv(320, 180)
    sky(cv, [(0.0, hx("1e1a46")), (0.4, hx("4a3a78")), (0.7, hx("a26a8a")), (0.88, hx("f0a88a")), (1.0, hx("ffd8a0"))], 14, 0, 125, 1.0)
    stars(cv, 51, 60, 0.006)
    halo(cv, 180, 125, 100, hx("ffd8a0"), 0.28, 5, 0.5)
    # distant valley layers
    ridge(cv, 98, [(7, 2, 0.8), (4, 5, 2.0), (1.5, 12, 0.5)], hx("7a6aa0"), hx("9a86b8"))
    # Hearthollow's darkened Lantern Tree far behind on the left
    great_tree(cv, 34, 112, 0.13, "gloom", 0, sockets=False)
    # Duskmantle Hollow far away on the right
    halo(cv, 287, 84, 36, hx("9040d0"), 0.32, 4)
    for (sx, h, w, sd) in [(274, 27, 5, 0.3), (283, 37, 7, 1.1), (295, 23, 5, 2.0), (302, 17, 4, 0.7)]:
        spire(cv, sx, 104, h, w, hx("3a2858"), hx("5a3c80"), sd)
    cv.rect(279, 64, 287, 66, hx("3a2858"))
    cv.put(283, 63, hx("e080ff"))
    ridge(cv, 111, [(5, 3, 1.2), (3, 7, 0.1), (1, 17, 0.4)], hx("5a5a8a"), hx("7270a0"))
    ridge(cv, 122, [(4, 2, 2.4), (2.5, 6, 1.0)], hx("3e5a6a"), hx("527484"), hx("344c5c"), shade_from=6)
    # winding path into the valley
    path_perspective(cv, [(20, 186), (95, 160), (150, 143), (180, 133), (215, 126), (235, 123)], 15, 1,
                     hx("8a7488"), hx("665468"), 3)
    # midground meadow hill (partly over the path's far end)
    for x in range(320):
        top = 135 + 5 * math.sin(x * 0.024 + 0.6) + 2 * math.sin(x * 0.1)
        for y in range(int(top), 150):
            if cv.get(x, y) not in (hx("8a7488"), hx("665468")):
                cv.put(x, y, hx("2e4a50") if y > top + 1 else hx("44686a"))
    path_perspective(cv, [(20, 186), (95, 160), (150, 143), (180, 134)], 15, 3, hx("8a7488"), hx("665468"), 3)
    mushroom(cv, 262, 150, 24, 13, RED, (0.55, 0.5, 0.7), seed=3, lean=0.1)
    mushroom(cv, 280, 152, 13, 7, RED, (0.55, 0.5, 0.7), seed=4, lean=-0.2)
    mushroom(cv, 236, 143, 8, 5, RED, (0.5, 0.48, 0.66), seed=5)
    # foreground crest where Pip stands
    for x in range(320):
        top = 150 + 0.0045 * (x - 80) ** 2 * (1 if x > 80 else 0.35) - math.sin(x * 0.4)
        for y in range(int(top), 180):
            c = hx("1e3038") if y > top + 1 else hx("304c50")
            cv.put(x, y, c)
        if hsh(x, 1, 55) < 0.4:
            for j in range(1 + int(hsh(x, 2, 55) * 3)):
                cv.put(x, top - 1 - j, hx("304c50"))
    path_perspective(cv, [(20, 186), (60, 171), (95, 160), (122, 152)], 15, 9, hx("6e5c70"), hx("4c4058"), 3)
    halo(cv, 102, 152, 46, hx("ffb860"), 0.3, 4, 0.4)
    pip(cv, 98, 160, "Explorer Idle", 0, False, (0.8, 0.74, 0.88), (8, 3, 0))
    hand_lantern(cv, 98 - 16 + 20, 160 - 40 + 29, True, 1.2)
    for (fx, fy, c) in [(35, 158, hx("ff9aa8")), (52, 161, hx("fff3a6")), (140, 162, hx("ff9aa8")), (18, 166, hx("c6e8ff")),
                        (170, 170, hx("fff3a6"))]:
        cv.put(fx, fy, c)
        cv.put(fx, fy + 1, hx("2a4a40"))
    fireflies(cv, 53, 10, (15, 100, 310, 160))
    vignette(cv, hx("0a0818"), 0.5)
    big = scale2(cv)
    caption_band(big)
    return big


def ending_1():
    cv = Cv(320, 180)
    sky(cv, [(0.0, hx("2a2660")), (0.55, hx("6a4a8a")), (1.0, hx("e8a088"))], 12, 0, 150, 1.0)
    stars(cv, 61, 60, 0.004)
    great_tree(cv, 160, 230, 0.95, "dusk", 4, glow=1.2)
    meadow_ground(cv, 150, [(1.5, 2, 1.4), (1, 6, 0.3)], hx("2c4a4a"), hx("447068"), hx("1e3238"), seed=17)
    # beam from the raised Glowspore to the empty fifth socket
    sx, sy = tree_lantern_points(160, 230, 0.95)[4]
    px_, py_ = 150, 158
    ox, oy = px_ - 16, py_ - 40
    hxp, hyp = ox + 25, oy + 9
    for k in range(80):
        t = k / 80
        x = hxp + (sx - hxp) * t
        y = hyp + (sy - hyp) * t
        for w in range(-2, 3):
            cv.mix(x + w, y, hx("fff3c0"), 0.35 if abs(w) < 2 else 0.15)
    halo(cv, sx, sy, 22, hx("ffe8a0"), 0.5, 4)
    halo(cv, hxp, hyp, 70, hx("ffd070"), 0.38, 5)
    pip(cv, px_, py_, "Explorer Jump", 0, False, (0.95, 0.9, 0.9), (16, 10, 0))
    glowspore(cv, hxp + 0.5, hyp - 1, 6, True, 26, 0.55)
    elder_morel(cv, 270, 158, -1, False, (0.86, 0.8, 0.86), (10, 6, 0))
    puffball(cv, 298, 158, (0.86, 0.8, 0.86), (10, 6, 0))
    sparkles(cv, 63, 24, (90, 40, 230, 150))
    vignette(cv, hx("100a20"), 0.45)
    big = scale2(cv)
    caption_band(big, 48, amax=0.45)
    return big


def ending_2():
    cv = Cv(640, 360)
    sky(cv, [(0.0, hx("2e2c6e")), (0.35, hx("6e4e96")), (0.65, hx("e08a86")), (0.85, hx("ffc48a")), (1.0, hx("fff0b0"))], 16, 0, 300, 1.0)
    stars(cv, 71, 80, 0.003)
    rays(cv, 320, 150, 420, 22, hx("fff0b0"), 0.22, 0.4)
    halo(cv, 320, 150, 260, hx("ffd070"), 0.45, 7, 0.85)
    ridge(cv, 240, [(10, 2, 0.4), (6, 5, 1.3), (3, 11, 0.2)], hx("b07a9a"), hx("e0a8a8"))
    ridge(cv, 264, [(9, 3, 2.1), (5, 7, 0.6), (2, 15, 1.0)], hx("6e6a8a"), hx("a898b0"))
    great_tree(cv, 320, 318, 1.0, "blaze", 5, glow=1.6)
    meadow_ground(cv, 304, [(4, 2, 0.5), (2, 6, 1.4)], hx("4a7a52"), hx("8ac070"), hx("3a6046"), seed=19)
    halo(cv, 320, 316, 160, hx("ffe090"), 0.35, 5, 0.3)
    warm = (0.96, 0.9, 0.86)
    mush_house(cv, 96, 330, 62, 72, RED, warm, seed=1, chimney=True)
    mush_house(cv, 186, 346, 42, 48, [hx("f0a05a"), hx("d8743b"), hx("a14a2f"), hx("6e2c2a")], warm, seed=2)
    mush_house(cv, 536, 334, 66, 76, [hx("b07ad8"), hx("8a52b8"), hx("643a8c"), hx("402460")], warm, seed=3, chimney=True)
    mush_house(cv, 448, 350, 40, 46, RED, warm, seed=4)
    lamp_post(cv, 250, 346, 30, warm)
    lamp_post(cv, 392, 346, 30, warm)
    # Duskmantle fleeing, small, towards the top-right
    duskmantle(cv, 584, 52, 44, "silhouette", 1.1, 8)
    sparkles(cv, 73, 90, (60, 30, 600, 300))
    for i in range(28):
        x = 160 + hsh(i, 1, 75) * 320
        y = 40 + hsh(i, 2, 75) * 240
        halo(cv, x, y, 5, hx("ffe8a0"), 0.4, 2)
        cv.put(x, y, hx("fff8d8"))
    caption_band(cv, 48, amax=0.4)
    return cv


def ending_3():
    cv = Cv(320, 180)
    sky(cv, [(0.0, hx("141838")), (0.6, hx("2c2a5e")), (1.0, hx("5a3e70"))], 12, 0, 150, 1.0)
    stars(cv, 81, 110, 0.007)
    # fireworks (spore bursts)
    for (fx, fy, r, c) in [(60, 34, 16, hx("ffe08a")), (250, 26, 18, hx("ff9ac0")), (290, 70, 11, hx("a8e0ff"))]:
        for k in range(14):
            a = k / 14 * 6.283
            for s in range(3, int(r)):
                if s % 2 == 0:
                    cv.mix(fx + math.cos(a) * s, fy + math.sin(a) * s, c, 0.4 + 0.5 * s / r)
        halo(cv, fx, fy, r * 1.4, c, 0.18, 3)
        cv.put(fx, fy, WHITE)
    halo(cv, 160, 60, 150, hx("ffc070"), 0.3, 5)
    great_tree(cv, 160, 300, 1.15, "dusk", 5, glow=1.5)
    for (hxp, hw) in [(26, 34), (300, 30)]:
        mush_house(cv, hxp, 150, hw, hw * 1.1, RED if hxp < 100 else [hx("b07ad8"), hx("8a52b8"), hx("643a8c"), hx("402460")], (0.85, 0.8, 0.9), seed=hxp)
    meadow_ground(cv, 146, [(1.2, 2, 0.4), (1, 6, 1.1)], hx("3a5a4a"), hx("5e8a62"), hx("2a4038"), seed=23)
    halo(cv, 160, 150, 140, hx("ffd070"), 0.3, 5, 0.3)
    string_lights(cv, 0, 70, 128, 62, 14)
    string_lights(cv, 192, 62, 320, 72, 14)
    bunting(cv, 0, 96, 140, 92, 10, [RED[1], GOLD[1], GREEN[1], hx("8fd3ff")], 0)
    bunting(cv, 180, 92, 320, 98, 10, [GOLD[1], RED[1], hx("b07ad8"), GREEN[1]], 1)
    warm = (0.92, 0.86, 0.86)
    wm = (8, 4, 0)
    old_wick(cv, 40, 166, warm, wm, 1)
    chanterelle(cv, 92, 166, warm, wm, 1)
    pip(cv, 160, 160, "Explorer Fall", 0, False, (0.95, 0.9, 0.9), (12, 8, 0))
    elder_morel(cv, 212, 166, -1, False, warm, wm)
    bram(cv, 256, 166, warm, wm, -1)
    puffball(cv, 292, 166, warm, wm, 6)
    # confetti
    for i in range(70):
        x = hsh(i, 1, 85) * 320
        y = hsh(i, 2, 85) * 150
        c = [RED[0], GOLD[1], GREEN[0], hx("8fd3ff"), hx("d6a8ff")][i % 5]
        cv.put(x, y, c)
        if i % 3 == 0:
            cv.put(x + 1, y, c)
    vignette(cv, hx("0a0818"), 0.4)
    big = scale2(cv)
    caption_band(big, 48, amax=0.35)
    return big


def menu_backdrop():
    cv = Cv(640, 360)
    sky(cv, [(0.0, hx("16163e")), (0.45, hx("3a3070")), (0.75, hx("8a5684")), (0.92, hx("e08a7a")), (1.0, hx("ffb880"))], 16, 0, 280, 1.0)
    stars(cv, 91, 200, 0.006)
    halo(cv, 498, 168, 120, hx("ffc070"), 0.3, 5)
    ridge(cv, 236, [(10, 2, 0.9), (6, 5, 2.2), (3, 13, 0.5)], hx("5a4a86"), hx("7a64a6"))
    great_tree(cv, 498, 262, 0.52, "dusk", 5, glow=1.3)
    ridge(cv, 256, [(8, 3, 1.4), (4, 8, 0.2)], hx("3a3e6c"), hx("525888"))
    meadow_ground(cv, 280, [(6, 2, 0.1), (3, 5, 1.2)], hx("26404c"), hx("3a6266"), hx("1c3040"), seed=29)
    for (mx, by_, h, r, sd) in [(250, 296, 18, 10, 1), (402, 300, 14, 8, 2), (588, 292, 26, 14, 3)]:
        mushroom(cv, mx, by_, h, r, RED, (0.55, 0.52, 0.74), seed=sd)
    # foreground framing: giant mushrooms left and right
    mushroom(cv, 52, 372, 120, 62, RED, (0.6, 0.55, 0.78), lean=0.15, seed=7)
    mushroom(cv, 128, 368, 56, 30, [hx("f0a05a"), hx("d8743b"), hx("a14a2f"), hx("6e2c2a")], (0.58, 0.54, 0.76), lean=-0.1, seed=8)
    mushroom(cv, 600, 372, 88, 46, [hx("b07ad8"), hx("8a52b8"), hx("643a8c"), hx("402460")], (0.6, 0.55, 0.78), lean=-0.15, seed=9)
    dark_grass_front(cv, 352, hx("142430"), 31, 16)
    fireflies(cv, 93, 24, (10, 150, 630, 340))
    # keep the menu centre readable
    for y in range(140, 360):
        for x in range(170, 470):
            d = math.hypot((x - 320) / 150, (y - 260) / 110)
            if d < 1:
                cv.mix(x, y, hx("0c0a22"), 0.22 * math.ceil((1 - d) * 3) / 3)
    vignette(cv, hx("080616"), 0.45)
    return cv


def credits_backdrop():
    cv = Cv(640, 360)
    sky(cv, [(0.0, hx("0a0a24")), (0.5, hx("1c1a46")), (0.85, hx("3a2a62")), (1.0, hx("5a3a6e"))], 14, 0, 360, 1.0)
    stars(cv, 101, 300, 0.009)
    # aurora ribbons in warm glowspore gold and violet
    for (base, amp, c) in [(70, 14, hx("70e0c0")), (96, 10, hx("b080ff"))]:
        for x in range(640):
            y0 = base + amp * math.sin(x * 0.012 + base) + 6 * math.sin(x * 0.041)
            for k in range(26):
                t = k / 26
                cv.mix(x, y0 + k, c, 0.16 * (1 - t) * (0.6 + 0.4 * math.sin(x * 0.05 + k * 0.2)))
    halo(cv, 320, 290, 120, hx("ffc070"), 0.28, 5)
    ridge(cv, 312, [(6, 2, 0.4), (4, 5, 1.6)], hx("241e44"), hx("342a5a"))
    great_tree(cv, 320, 330, 0.36, "night", 5, glow=1.5)
    ridge(cv, 326, [(3, 3, 0.3), (2, 9, 1.0)], hx("161230"), hx("221c40"))
    mushroom(cv, 40, 372, 96, 52, RED, silhouette=hx("100c22"), lean=0.12, seed=11)
    mushroom(cv, 110, 368, 40, 22, RED, silhouette=hx("100c22"), lean=-0.1, seed=12)
    mushroom(cv, 604, 372, 110, 56, RED, silhouette=hx("100c22"), lean=-0.14, seed=13)
    mushroom(cv, 540, 368, 34, 18, RED, silhouette=hx("100c22"), lean=0.1, seed=14)
    dark_grass_front(cv, 352, hx("0c0a1c"), 41, 12)
    fireflies(cv, 103, 22, (10, 150, 630, 350))
    vignette(cv, hx("04040c"), 0.45)
    return cv


# Overworld ---------------------------------------------------------------------------------
def vnoise(x, y, s, seed):
    gx, gy = x / s, y / s
    x0, y0 = int(math.floor(gx)), int(math.floor(gy))
    fx, fy = smooth(gx - x0), smooth(gy - y0)
    a, b = hsh(x0, y0, seed), hsh(x0 + 1, y0, seed)
    c, d = hsh(x0, y0 + 1, seed), hsh(x0 + 1, y0 + 1, seed)
    return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy


def sea_v(x, y):
    return ((x - 530) / 200) ** 2 + ((y - 405) / 135) ** 2 - 0.05 * math.sin(x * 0.08) - 0.035 * math.sin(y * 0.13 + x * 0.03)


RIVER = [(350, 70), (362, 100), (356, 140), (382, 188), (404, 228), (430, 268), (446, 302)]
MAP_PATHS = [(NODES[0], (128, 206), NODES[1]), (NODES[1], (232, 254), NODES[2]), (NODES[2], (340, 296), NODES[3]),
             (NODES[3], (448, 268), NODES[4]), (NODES[4], (500, 100), NODES[5])]


def bez(p0, p1, p2, t):
    return ((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
            (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1])


def river_samples():
    out = []
    for i in range(len(RIVER) - 1):
        p0 = RIVER[max(0, i - 1)]
        p1, p2 = RIVER[i], RIVER[i + 1]
        p3 = RIVER[min(len(RIVER) - 1, i + 2)]
        for k in range(30):
            t = k / 30
            t2, t3 = t * t, t * t * t
            out.append(tuple(0.5 * ((2 * p1[j]) + (-p0[j] + p2[j]) * t + (2 * p0[j] - 5 * p1[j] + 4 * p2[j] - p3[j]) * t2
                                    + (-p0[j] + 3 * p1[j] - 3 * p2[j] + p3[j]) * t3) for j in range(2)))
    out.append(RIVER[-1])
    return out


def path_samples():
    pts = []
    for (a, c, b) in MAP_PATHS:
        for k in range(120):
            pts.append(bez(a, c, b, k / 120))
    return pts


def map_tree(cv, x, y, r, pal, out):
    hi, mid, dk = pal
    # ground shadow
    for yy in range(int(y - 1), int(y + 3)):
        for xx in range(int(x - r), int(x + r + 3)):
            if math.hypot((xx + 0.5 - x - 1.5) / (r + 1), (yy + 0.5 - y - 1) / 2.2) <= 1:
                cv.mix(xx, yy, hx("1e3a2a"), 0.35)
    cv.rect(x, y - 3, x + 1, y, hx("5a3a2a"))
    cy = y - 3 - r * 0.8
    cv.disc(x + 0.5, cy, r + 1, out)
    for yy in range(int(cy - r) - 1, int(cy + r) + 2):
        for xx in range(int(x - r) - 1, int(x + r) + 2):
            dx, dy = xx + 0.5 - (x + 0.5), yy + 0.5 - cy
            d = math.hypot(dx, dy)
            if d > r:
                continue
            k = (-dx * 0.6 - dy * 0.8) / r
            c = hi if k > 0.35 else (dk if k < -0.35 else mid)
            cv.put(xx, yy, c)


def map_pine(cv, x, y, h, pal, out):
    hi, mid, dk = pal
    cv.rect(x, y - 2, x + 1, y, hx("4a3024"))
    for yy in range(int(y - h), int(y - 1)):
        t = (yy - (y - h)) / h
        hw = 0.5 + t * h * 0.36 + (1.2 if (yy - (y - h)) % 4 == 3 else 0) * t
        for xx in range(int(x + 0.5 - hw - 1), int(x + 0.5 + hw + 1) + 1):
            u = xx + 0.5 - (x + 1)
            if abs(u) > hw + 1:
                continue
            c = out if abs(u) > hw else (hi if u < -hw * 0.3 else (dk if u > hw * 0.3 else mid))
            cv.put(xx, yy, c)
    cv.put(x, y - h - 1, out)


def map_dead_tree(cv, x, y, h, c, seed):
    cv.line(x, y, x + math.sin(seed) * 2, y - h, c, 0.6)
    for k in range(3):
        t = 0.45 + k * 0.18
        bx, by_ = x + math.sin(seed) * 2 * t, y - h * t
        sd = 1 if (k + int(seed)) % 2 else -1
        cv.line(bx, by_, bx + sd * (3 + k), by_ - 3 - k, c, 0.5)


def map_house(cv, x, by, w, cap, out=OUT):
    h = w * 1.05
    stem_top = by - h * 0.55
    cv.rect(x - w * 0.36 - 1, stem_top - 1, x + w * 0.36 + 1, by + 1, out)
    cv.rect(x - w * 0.36, stem_top, x + w * 0.36, by, CREAM[1])
    cv.rect(x + w * 0.18, stem_top, x + w * 0.36, by, CREAM[2])
    cv.rect(x - 1, by - 3, x + 1, by, WOOD[2])
    cv.put(x - w * 0.22, by - h * 0.32, hx("ffd86a"))
    cv.put(x - w * 0.22 + 1, by - h * 0.32, hx("ffd86a"))
    rx, ry = w * 0.62, h * 0.5
    for yy in range(int(stem_top - ry) - 1, int(stem_top) + 2):
        for xx in range(int(x - rx) - 1, int(x + rx) + 2):
            nx, ny = (xx + 0.5 - x) / rx, (yy + 0.5 - stem_top) / ry
            d = nx * nx + ny * ny
            if yy > stem_top or d > 1.25:
                continue
            if d > 1.0 or yy == int(stem_top):
                cv.put(xx, yy, out)
                continue
            c = cap[0] if (nx < -0.2 and ny < -0.4) else (cap[2] if nx > 0.4 else cap[1])
            if hsh(xx, yy, 33) < 0.12 and ny < -0.2:
                c = CREAM[0]
            cv.put(xx, yy, c)


def map_mushroom(cv, x, by, h, r, cap):
    cv.rect(x - 1, by - h, x + 1, by, OUT)
    cv.rect(x, by - h, x, by - 1, CREAM[1])
    for yy in range(int(by - h - r * 0.7) - 1, int(by - h) + 2):
        for xx in range(int(x - r) - 1, int(x + r) + 2):
            nx, ny = (xx + 0.5 - (x + 0.5)) / r, (yy + 0.5 - (by - h)) / (r * 0.7)
            d = nx * nx + ny * ny
            if yy > by - h + 1 or d > 1.3:
                continue
            if d > 1.0 or yy >= by - h:
                cv.put(xx, yy, OUT)
            else:
                c = cap[0] if nx < -0.3 and ny < -0.3 else (cap[2] if nx > 0.4 else cap[1])
                if (xx + yy) % 4 == 0 and ny < -0.3 and abs(nx) < 0.7:
                    c = CREAM[0]
                cv.put(xx, yy, c)


def overworld_map():
    W, H = 640, 360
    cv = Cv(W, H)
    rs = river_samples()
    ps = path_samples()

    def hollow_w(x, y):
        d = math.hypot(x - 612, (y - 40) * 1.3) + (vnoise(x, y, 18, 5) - 0.5) * 40
        return smooth((175 - d) / 50)

    def canopy_w(x, y):
        return smooth((78 - math.hypot(x - 470, (y - 190) * 1.1)) / 30)

    grass = [hx("8ccf6a"), hx("74bc5c"), hx("62a852"), hx("50944a")]
    gold_g = [hx("c8c86a"), hx("aab45a"), hx("8ea04e"), hx("748a46")]
    purp = [hx("6c5a8a"), hx("5c4a7a"), hx("4c3c6a"), hx("3e305a")]
    for y in range(H):
        for x in range(W):
            n = vnoise(x, y, 26, 1) * 0.65 + vnoise(x, y, 7, 2) * 0.35
            if hollow_w(x, y) > 0.5:
                n = vnoise(x, y, 30, 1)
            lv = n * 3.2 + (bayer(x, y) - 0.5) * 0.7
            i = max(0, min(3, int(lv)))
            hw_, cw = hollow_w(x, y), canopy_w(x, y)
            pal = grass
            if cw > bayer(x, y) * 0.9 + 0.05:
                pal = gold_g
            if hw_ > bayer(x, y) * 0.9 + 0.05:
                pal = purp
            c = pal[i]
            if hsh(x, y, 3) < 0.03 and pal is grass:
                c = grass[0] if i > 1 else grass[3]
            cv.px[x, y] = c
    # village fields (patchwork) bottom-left
    for (fx0, fy0, fx1, fy1, kind) in [(14, 286, 52, 316, 0), (14, 320, 58, 346, 1), (62, 300, 104, 330, 1),
                                       (110, 310, 150, 344, 0), (60, 334, 104, 348, 0)]:
        for y in range(fy0, fy1 + 1):
            for x in range(fx0, fx1 + 1):
                edge = x in (fx0, fx1) or y in (fy0, fy1)
                if kind == 0:
                    c = hx("c8a060") if (y - fy0) % 3 else hx("9a7444")
                    if (y - fy0) % 3 == 1 and hsh(x, y, 6) < 0.3:
                        c = hx("8ab84a")
                else:
                    c = hx("b4c85a") if (x - fx0) % 4 else hx("8aa046")
                if edge:
                    c = hx("6a5a3a")
                cv.put(x, y, c)
    # distant mountain range along the top
    peaks = [(150, 30, 34), (196, 12, 40), (244, 26, 36), (290, 6, 46), (342, 20, 38), (392, 8, 44), (440, 30, 34)]
    for (pxk, pyk, hw) in peaks:
        for y in range(max(0, pyk), 84):
            t = (y - pyk) / (84 - pyk)
            w = hw * t * 1.6 + 1
            for x in range(int(pxk - w), int(pxk + w) + 1):
                if hollow_w(x, y) > 0.5:
                    continue
                u = (x + 0.5 - pxk) / max(w, 1)
                ridge_ = abs(u) < 0.06 + 0.03 * math.sin(y * 0.7)
                c = hx("a8b0c8") if u < -0.05 else hx("7e86a6")
                if y < pyk + 10 + 3 * math.sin(x * 0.6):
                    c = WHITE if u < 0.05 else hx("d4dcf0")
                elif ridge_:
                    c = hx("6a7090")
                if abs(u) > 0.97:
                    c = hx("4a4e6a")
                cv.put(x, y, c)
    # foothill fade into grass
    for y in range(66, 92):
        for x in range(130, 470):
            if cv.get(x, y)[:3] in ((168, 176, 200), (126, 134, 166), (106, 112, 144), (74, 78, 106)):
                if bayer(x, y) < (y - 66) / 26:
                    cv.put(x, y, grass[2] if (x + y) % 3 else grass[3])
    # lake at the river source
    for y in range(58, 80):
        for x in range(328, 376):
            d = math.hypot((x + 0.5 - 351) / 20, (y + 0.5 - 69) / 8)
            if d <= 1:
                cv.put(x, y, hx("4a8ad0") if d > 0.8 else (hx("6ab0e8") if (x + y * 3) % 9 else hx("a8dcff")))
            elif d <= 1.12:
                cv.put(x, y, hx("d8c890"))
    # sea + beach
    for y in range(H):
        for x in range(W):
            v = sea_v(x, y)
            if v < 1.0:
                deep = v < 0.72
                c = hx("3a78c0") if deep else (hx("4a90d0") if v < 0.9 else hx("62b0e0"))
                if deep and ((x + int(4 * math.sin(y * 0.5))) % 22 < 4) and y % 9 == 0:
                    c = hx("6aa8e0")
                if 0.95 < v < 0.985 and hsh(x // 2, y, 4) < 0.6:
                    c = hx("d8f0ff")
                cv.px[x, y] = c
            elif v < 1.09:
                cv.px[x, y] = hx("ecd8a0") if v < 1.06 else hx("d4bc84")
    # river
    for (rx, ry) in rs:
        w = 2.2 + (ry - 70) / 90
        for y in range(int(ry - w) - 1, int(ry + w) + 2):
            for x in range(int(rx - w) - 1, int(rx + w) + 2):
                d = math.hypot(x + 0.5 - rx, y + 0.5 - ry)
                if d <= w:
                    if sea_v(x, y) >= 1.0:
                        cv.put(x, y, hx("5aa0e0") if d < w - 1 else hx("4a8ad0"))
                elif d <= w + 1 and cv.get(x, y)[:3] not in ((90, 160, 224), (74, 138, 208)) and sea_v(x, y) >= 1.09:
                    cv.put(x, y, hx("3e7a50"))
    for (rx, ry) in rs[::6]:
        cv.put(rx - 0.5, ry, hx("b8e4ff"))
    # Rootdeep mountain with the cave mouth (node 2)
    mbase = 244

    def peak(mx, mtop, hwl, hwr, seed):
        rock = [hx("c4b4a2"), hx("a08e7e"), hx("7e6c62"), hx("5e4e4a")]
        for y in range(mtop, mbase + 1):
            t = (y - mtop) / (mbase - mtop)
            wl = hwl * t ** 0.8 * (1 + 0.5 * (vnoise(1, y, 7, seed) - 0.5)) + 1
            wr = hwr * t ** 0.8 * (1 + 0.5 * (vnoise(2, y, 7, seed + 1) - 0.5)) + 1
            cl = mx + 5 * math.sin(y * 0.07 + seed) * t
            for x in range(int(mx - wl), int(mx + wr) + 1):
                left = x < cl
                u = (x + 0.5 - mx) / (wl if x < mx else wr)
                if left:
                    c = rock[0] if (cl - x) < 4 + 6 * vnoise(x, y, 5, seed + 2) else rock[1]
                else:
                    c = rock[2] if (x - cl) < 10 + 8 * vnoise(x, y, 6, seed + 3) else rock[3]
                if vnoise(x, y * 2, 4, seed + 4) > 0.78:
                    c = rock[min(3, rock.index(c) + 1)]
                if t < 0.12:
                    c = hx("f0ecf4") if left else hx("c8c4d8")
                if t > 0.62 and vnoise(x, y, 5, seed + 5) < (t - 0.62) * 1.9:
                    c = hx("6aa052") if left else hx("4e7c46")
                if abs(u) > 0.95 or y == mtop:
                    c = hx("3e302e")
                cv.put(x, y, c)

    peak(256, 182, 24, 26, 81)
    peak(348, 176, 26, 22, 85)
    peak(300, 140, 50, 60, 91)
    # cave mouth
    for y in range(222, 242):
        for x in range(286, 315):
            dx = (x + 0.5 - 300.5) / 13
            dy = (y + 0.5 - 242) / 19
            d = dx * dx + dy * dy
            if d <= 1:
                cv.put(x, y, hx("1a1220") if d < 0.75 else hx("3e302e"))
    for x in range(290, 312, 4):
        cv.put(x, 223 + abs(x - 300) // 4, hx("ffd86a"))
    halo(cv, 300, 236, 10, hx("ffc860"), 0.25, 2)
    # Grotto rock arch (node 3)
    for y in range(292, 326):
        for x in range(410, 446):
            dx, dy = (x + 0.5 - 428) / 18, (y + 0.5 - 326) / 32
            d = dx * dx + dy * dy
            inner = ((x + 0.5 - 428) / 9) ** 2 + ((y + 0.5 - 326) / 20) ** 2
            if d <= 1 and inner > 1:
                c = hx("8a8aa0") if dx < -0.2 else hx("62627c")
                if d > 0.86:
                    c = hx("3a3a50")
                if hsh(x, y, 12) < 0.12:
                    c = hx("e07a8a")
                cv.put(x, y, c)
            elif inner <= 1 and y > 304:
                cv.put(x, y, hx("1e3a6a") if y < 312 else cv.get(x, y))
    for (rx_, ry_, rr) in [(452, 318, 4), (402, 316, 3), (462, 306, 2)]:
        cv.disc(rx_, ry_, rr + 1, hx("3a3a50"))
        cv.disc(rx_ - 0.5, ry_ - 0.5, rr, hx("8a8aa0"))
    # boats / compass in the sea
    cx_, cy_ = 600, 328
    cv.disc(cx_, cy_, 11, hx("2e6ab0"))
    for a, ln in [(0, 11), (90, 11), (180, 11), (270, 11), (45, 6), (135, 6), (225, 6), (315, 6)]:
        ar = math.radians(a)
        for k in range(ln):
            cv.put(cx_ + math.sin(ar) * k, cy_ - math.cos(ar) * k, hx("fff3c0") if a == 0 else hx("d8e8f8"))
    cv.put(cx_, cy_, RED[1])
    # Hollow decorations: crooked spires and the nest tower
    halo(cv, 600, 60, 90, hx("1a0c30"), 0.35, 4)
    for (sx, sb, sh, sw, sd) in [(528, 56, 34, 9, 0.4), (548, 42, 24, 7, 1.6), (628, 140, 36, 9, 2.2), (502, 66, 20, 6, 1.0),
                                  (546, 128, 18, 6, 2.9), (586, 136, 22, 7, 0.2), (520, 108, 14, 5, 1.3)]:
        spire(cv, sx, sb, sh, sw, hx("2a1c44"), hx("7a5aa8"), sd)
    # glowing violet toadstools in the hollow
    for (gx_, gy_) in [(536, 82), (594, 112), (560, 116), (614, 160), (512, 92), (588, 70)]:
        halo(cv, gx_, gy_ - 2, 7, hx("d080ff"), 0.4, 2)
        map_mushroom(cv, gx_, gy_, 3, 3, [hx("f0c0ff"), hx("c070f0"), hx("8040b0")])
    # nest tower
    tx, tb = 606, 104
    for y in range(18, tb + 1):
        t = (tb - y) / (tb - 18)
        hw = 9 - 4 * t
        cx2 = tx + 2 * math.sin(t * 4)
        for x in range(int(cx2 - hw), int(cx2 + hw) + 1):
            u = (x + 0.5 - cx2) / hw
            c = hx("4e3a6e") if u < -0.3 else (hx("3a2858") if u < 0.5 else hx("2a1c40"))
            if (y % 7 == 0) and abs(u) < 0.9:
                c = hx("5e4a80")
            if abs(u) > 0.88:
                c = hx("1a1028")
            cv.put(x, y, c)
    # nest bowl on top
    for y in range(8, 22):
        for x in range(590, 625):
            dx, dy = (x + 0.5 - 607) / 16, (y + 0.5 - 14) / 7
            d = dx * dx + dy * dy
            if d <= 1 and y >= 11:
                c = hx("6a5070") if (x + y * 2) % 5 else hx("4a3a56")
                if d > 0.8:
                    c = hx("2a1c38")
                cv.put(x, y, c)
    halo(cv, 607, 10, 26, hx("c060ff"), 0.4, 4)
    for x in range(600, 615, 3):
        cv.put(x, 10, hx("f0d0ff"))
    # windows in the tower
    for wy in (40, 60, 80):
        cv.put(606 + int(2 * math.sin((tb - wy) / 80 * 4)), wy, hx("ff80e0"))
    # Canopy giant tree (node 4)
    for y in range(176, 266):
        t = (y - 176) / 90
        hw = 7 + 6 * t ** 3
        for x in range(int(470 - hw), int(470 + hw) + 1):
            u = (x + 0.5 - 470) / hw
            c = hx("9a6a46") if u < -0.3 else (hx("7a5034") if u < 0.5 else hx("5a3824"))
            if abs(u) > 0.88:
                c = hx("3a2418")
            cv.put(x, y, c)
    # roots
    for sd in (-1, 1):
        for k in range(14):
            cv.put(470 + sd * (12 + k), 264 - k // 5, hx("5a3824"))
            cv.put(470 + sd * (12 + k), 265 - k // 5, hx("3a2418"))
    puffs = [(470, 128, 26), (440, 146, 24), (500, 146, 24), (452, 166, 22), (490, 166, 22), (470, 150, 26)]
    leaf = [hx("f4d27a"), hx("d8a84e"), hx("b07c3e"), hx("7a5230")]

    def crown_hit(x, y):
        top = None
        for i, (pcx, pcy, pr) in enumerate(puffs):
            dx, dy = x + 0.5 - pcx, y + 0.5 - pcy
            rr = pr * (1 + 0.08 * math.sin(math.atan2(dy, dx) * 7 + i))
            if math.hypot(dx, dy) <= rr:
                top = (dx, dy, rr)
        return top

    for y in range(96, 196):
        for x in range(410, 532):
            top = crown_hit(x, y)
            if top is None:
                if any(crown_hit(x + ex, y + ey) for (ex, ey) in ((1, 0), (-1, 0), (0, 1), (0, -1))):
                    cv.put(x, y, hx("4a2a18"))
                continue
            dx, dy, rr = top
            d = math.hypot(dx, dy)
            k = (-dx * 0.6 - dy * 0.8) / rr
            c = leaf[0] if k > 0.5 else (leaf[1] if k > -0.1 else (leaf[2] if k > -0.55 else leaf[3]))
            if d > rr - 1.2:
                c = leaf[3]
            if hsh(x // 2, y // 2, 14) < 0.06:
                c = leaf[2]
            cv.put(x, y, c)
    for (bx, by_) in [(446, 140), (494, 132), (480, 186), (452, 180)]:
        cv.put(bx, by_, RED[1])
        cv.put(bx + 1, by_, RED[0])
    # forest scatter (avoid paths, nodes, water, mountain, village, canopy tree)
    items = []
    for gy in range(0, H + 10, 10):
        for gx in range(0, W + 10, 11):
            x = gx + (hsh(gx, gy, 21) - 0.5) * 9
            y = gy + (hsh(gx, gy, 22) - 0.5) * 8
            if not (6 <= x < W - 6 and 10 <= y < H - 6):
                continue
            if sea_v(x, y) < 1.14:
                continue
            if any(math.hypot(x - nx_, y - ny_) < 30 for (nx_, ny_) in NODES):
                continue
            if any(abs(x - px_) < 12 and abs(y - py_) < 12 for (px_, py_) in ps[::3]):
                continue
            if any(abs(x - rx) < 7 and abs(y - ry) < 7 for (rx, ry) in rs[::2]):
                continue
            if 236 <= x <= 364 and 140 <= y <= 252:
                continue
            if math.hypot(x - 351, (y - 69) * 2.4) < 26:
                continue
            if y < 88 and 120 < x < 470 and hollow_w(x, y) < 0.5:
                continue
            if math.hypot(x - 84, y - 252) < 58 or (10 < x < 156 and 280 < y < 352):
                continue
            if math.hypot(x - 470, y - 150) < 66 or (455 < x < 490 and y < 272):
                continue
            hwv, cwv = hollow_w(x, y), canopy_w(x, y)
            dens = vnoise(x, y, 40, 31)
            edge = min(x, W - x, y, H - y)
            if abs(x - 606) < 18 and y < 116:
                continue
            if any(abs(x - sx_) < 8 and sb_ - 8 < y < sb_ + 10 for (sx_, sb_) in
                   [(528, 56), (548, 42), (628, 140), (502, 66), (546, 128), (586, 136), (520, 108)]):
                continue
            if hwv > 0.5:
                p = 0.42
            else:
                p = 0.25 + 0.6 * dens + (0.35 if edge < 40 else 0) + (0.3 if (x < 150 and y < 200) else 0)
                if math.hypot(x - 190, y - 205) < 70:
                    p -= 0.25
            if hsh(gx, gy, 23) > p:
                continue
            items.append((y, x, hwv, cwv, gx, gy))
    items.sort()
    for (y, x, hwv, cwv, gx, gy) in items:
        r = hsh(gx, gy, 24)
        if hwv > 0.5:
            if r < 0.5:
                map_dead_tree(cv, x, y, 7 + r * 8, hx("2a1c40"), gx + gy)
            else:
                map_pine(cv, x, y, 9 + int(r * 5), (hx("6a5490"), hx("4e3c72"), hx("38285a")), hx("1e1430"))
        elif cwv > 0.3:
            map_tree(cv, x, y, 4 + r * 2.5, (hx("f0c86a"), hx("d09a4a"), hx("a0703a")), hx("4a3020"))
        elif (x < 170 and y < 170) or y < 110 or r > 0.82:
            map_pine(cv, x, y, 9 + int(r * 6), (hx("4e9a5e"), hx("357a4c"), hx("245a3c")), hx("143024"))
        elif math.hypot(x - 190, y - 205) < 95 and r < 0.3:
            map_mushroom(cv, x, y, 4 + int(r * 10), 4 + r * 6, RED)
        else:
            map_tree(cv, x, y, 4 + r * 3, (hx("8ed46a"), hx("5cb04e"), hx("3e8a44")), hx("1e4a2c"))
    # meadow: giant mushrooms and flowers near node 1
    for (mx_, my_, h_, r_, cap) in [(160, 186, 10, 7, RED), (226, 190, 8, 6, [hx("f0a05a"), hx("d8743b"), hx("a14a2f")]),
                                    (214, 228, 6, 5, RED), (150, 222, 7, 5, [hx("b07ad8"), hx("8a52b8"), hx("643a8c")])]:
        map_mushroom(cv, mx_, my_, h_, r_, cap)
    for i in range(70):
        a = hsh(i, 1, 61) * 6.283
        rr = 18 + hsh(i, 2, 61) * 52
        x, y = 190 + math.cos(a) * rr, 205 + math.sin(a) * rr * 0.7
        if cv.get(x, y)[:3] in [g[:3] for g in grass]:
            cv.put(x, y, [hx("ff9aa8"), hx("fff3a6"), WHITE, hx("c6a8ff")][i % 4])
    # Hearthollow (node 0): Great Lantern Tree + mushroom houses
    great_tree(cv, 86, 226, 0.19, "day", 0, sockets=False)
    for (hx_, hy_, w_, cap) in [(52, 246, 14, RED), (124, 240, 13, [hx("f0a05a"), hx("d8743b"), hx("a14a2f")]),
                                (60, 280, 13, [hx("b07ad8"), hx("8a52b8"), hx("643a8c")]), (122, 278, 15, RED),
                                (34, 266, 11, RED)]:
        map_house(cv, hx_, hy_, w_, cap)
    # well
    cv.disc(140, 258, 3, OUT)
    cv.disc(140, 258, 2, hx("8a88a0"))
    cv.put(140, 258, hx("4a8ad0"))
    # dotted trails
    for (a, c, b) in MAP_PATHS:
        pts = [bez(a, c, b, k / 400) for k in range(401)]
        acc = 0.0
        last = pts[0]
        for (x, y) in pts[1:]:
            acc += math.hypot(x - last[0], y - last[1])
            last = (x, y)
            if min(math.hypot(x - a[0], y - a[1]), math.hypot(x - b[0], y - b[1])) < 16:
                continue
            # worn trail under the dots
            if cv.get(x, y)[:3] not in ((90, 160, 224), (74, 138, 208)):
                cv.mix(x, y, hx("d8c08a"), 0.35)
            if acc >= 7:
                acc = 0.0
                ix, iy = int(x), int(y)
                cv.rect(ix, iy + 1, ix + 2, iy + 2, hx("3a2a2a"))
                cv.rect(ix, iy, ix + 1, iy + 1, hx("fff4d6"))
    # bridge where the trail crosses the river (node 3 -> 4)
    best = None
    for k in range(401):
        x, y = bez(*MAP_PATHS[3], k / 400)
        d = min(math.hypot(x - rx, y - ry) for (rx, ry) in rs)
        if best is None or d < best[0]:
            best = (d, x, y)
    _, bx, by_ = best
    for dy in range(-2, 3):
        for dx in range(-6, 7):
            c = WOOD[1] if dx % 3 else WOOD[2]
            if abs(dy) == 2:
                c = WOOD[3]
            cv.put(bx + dx, by_ + dy, c)
    # node clearings
    styles = [(hx("e2cc96"), hx("b49a68")), (hx("e8d8a0"), hx("b8a070")), (hx("b8aca0"), hx("7a6e66")),
              (hx("f0e0b0"), hx("c0aa7a")), (WOOD[0], WOOD[2]), (hx("b8a4d4"), hx("6a5490"))]
    for i, (nx_, ny_) in enumerate(NODES):
        light, dark = styles[i]
        for y in range(ny_ - 11, ny_ + 12):
            for x in range(nx_ - 18, nx_ + 19):
                d = ((x + 0.5 - nx_) / 16) ** 2 + ((y + 0.5 - ny_) / 9.5) ** 2
                if d > 1.0:
                    continue
                if i == 4:
                    c = WOOD[0] if (y - ny_) % 3 else WOOD[2]
                    if d > 0.78:
                        c = WOOD[3]
                elif i == 0:
                    c = light if ((x // 3 + y // 2) % 2) else lerp(light, dark, 0.4)
                    if d > 0.8:
                        c = dark
                else:
                    c = light if d < 0.62 else (lerp(light, dark, 0.5) if d < 0.85 else dark)
                    if hsh(x, y, 40 + i) < 0.06:
                        c = dark
                cv.put(x, y, c)
        # little tufts around the rim
        for k in range(8):
            a = k / 8 * 6.283 + 0.3
            tx_, ty_ = nx_ + math.cos(a) * 17, ny_ + math.sin(a) * 10
            col = hx("3e8a44") if i not in (5,) else hx("3a2858")
            if i == 4:
                col = hx("b07c3e")
            cv.put(tx_, ty_, col)
            cv.put(tx_, ty_ - 1, col)
    # region glows
    halo(cv, 570, 88, 46, hx("a040e0"), 0.18, 3)
    # frame
    for y in range(H):
        for x in range(W):
            e = min(x, y, W - 1 - x, H - 1 - y)
            if e == 0:
                cv.px[x, y] = OUT
            elif e == 1:
                cv.px[x, y] = hx("c89a4a")
            elif e == 2:
                cv.px[x, y] = hx("6a4426")
            elif e < 10:
                cv.mix(x, y, hx("2a1d2e"), 0.12 * (10 - e) / 7)
    for (cx3, cy3) in [(3, 3), (W - 4, 3), (3, H - 4), (W - 4, H - 4)]:
        cv.disc(cx3 + 0.5, cy3 + 0.5, 4, OUT)
        cv.disc(cx3 + 0.5, cy3 + 0.5, 3, hx("c89a4a"))
        cv.put(cx3, cy3, hx("fff3a6"))
    return cv


def overworld_clouds():
    W, H = 1024, 360
    cv = Cv(W, H)
    clouds = [(90, 40, 1.0), (330, 300, 0.8), (520, 70, 1.25), (760, 210, 0.9), (930, 30, 0.75), (200, 150, 0.6), (650, 330, 0.7)]
    top_c, mid_c, sh_c = hx("fbf8ff"), hx("e6e0f6"), hx("b8b0d8")
    for ci, (cx, cy, s) in enumerate(clouds):
        puffs = [(-36, 6, 14), (-16, -4, 20), (8, -10, 22), (30, -2, 17), (48, 7, 12), (0, 8, 16), (22, 10, 14), (-30, 12, 10)]
        puffs = [(cx + dx * s, cy + dy * s, r * s) for (dx, dy, r) in puffs]
        flat = cy + 14 * s
        minx = int(min(p[0] - p[2] for p in puffs)) - 2
        maxx = int(max(p[0] + p[2] for p in puffs)) + 2
        miny = int(min(p[1] - p[2] for p in puffs)) - 2
        for y in range(miny, int(flat) + 1):
            for xx in range(minx, maxx + 1):
                best = None
                for (pcx, pcy, r) in puffs:
                    d = math.hypot(xx + 0.5 - pcx, y + 0.5 - pcy)
                    if d <= r:
                        q = (y + 0.5 - pcy) / r
                        if best is None or q < best:
                            best = q
                if best is None or not (0 <= y < H):
                    continue
                edge = any(not any(math.hypot(xx + dx + 0.5 - a, y + dy + 0.5 - b) <= r for (a, b, r) in puffs)
                           for (dx, dy) in ((1, 0), (-1, 0), (0, -1), (0, 2), (2, 0), (-2, 0)))
                x = xx % W
                if y > flat - 3 * s:
                    c, a = sh_c, 0.78
                elif best > 0.35:
                    c, a = (mid_c if bayer(x, y) < 0.7 else sh_c), 0.8
                else:
                    c, a = (top_c if best < 0.0 or bayer(x, y) < 0.6 else mid_c), 0.82
                if edge:
                    a = 0.5 if bayer(x, y) < 0.5 else 0.35
                cv.mix(x, y, c, a)
    return cv


def lantern_lights(n):
    cv = Cv(1024, 360)
    for k in range(n):
        x, y = TREE_LIGHT_SPOTS[k]
        halo(cv, x + 0.5, y + 0.5, 26, SPORE_GLOW, 0.55, 5)
    for k in range(n):
        x, y = TREE_LIGHT_SPOTS[k]
        cv.line(x + 0.5, y - 9, x + 0.5, y - 5, hx("2a1d2e"))
        glowspore(cv, x + 0.5, y + 0.5, 4.6, True, 1, 0)
    return cv


SCENES = {}


def scene(name):
    def deco(fn):
        SCENES[name] = fn
        return fn
    return deco


def main(only=None):
    jobs = [("Intro Panel 1", intro_1, None, False), ("Intro Panel 2", intro_2, None, False),
            ("Intro Panel 3", intro_3, None, False), ("Intro Panel 4", intro_4, None, False),
            ("Intro Panel 5", intro_5, None, False), ("Ending Panel 1", ending_1, None, False),
            ("Ending Panel 2", ending_2, None, False), ("Ending Panel 3", ending_3, None, False),
            ("Menu Backdrop", menu_backdrop, None, False), ("Credits Backdrop", credits_backdrop, None, False),
            ("Overworld Map", overworld_map, None, False), ("Overworld Clouds", overworld_clouds, 1.0, True)]
    for n in range(1, 6):
        jobs.append((f"Lantern Tree Lights {n}", (lambda n=n: lantern_lights(n)), 0.2, True))
    for name, fn, par, rep in jobs:
        if only and not any(o.lower() in name.lower() for o in only):
            continue
        write_image(name, fn(), parallax=par, repeat_x=rep)


if __name__ == "__main__":
    import sys
    main(sys.argv[1:])
