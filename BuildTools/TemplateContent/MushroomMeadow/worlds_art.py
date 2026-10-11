"""Mushroom Meadow phase-2 world art generator (ART-WORLDS).

Deterministic: re-running produces byte-identical PNGs and JSON.
Writes the five world tilesets (Cavern, Grotto, Canopy, Hollow, Village Tiles; same 40-index
layout and solid list as Meadow Tiles) and every level/village background into the TwoDShowcase
template, plus labelled contact sheets, background stacks and mock-level scenes under
TestResults/MushroomMeadow/worlds_*.png.

Helpers are imported from world_art.py (phase 1); its outputs are not touched.

Usage:  python BuildTools/TemplateContent/MushroomMeadow/worlds_art.py [world ...]
        (world = cavern grotto canopy hollow village; default all)
"""
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import world_art as wa  # noqa: E402
from world_art import TS, CLEAR, hsh, bayer, new, put, rect, disc, lerp, outline  # noqa: E402

PREVIEW_DIR = wa.ROOT / "TestResults" / "MushroomMeadow"
SOLID = list(wa.SOLID)
TAU = 2 * math.pi
BW, BH = 1024, 360   # parallax strip size
SW, SH = 640, 360    # sky size


# ---------------------------------------------------------------------------
# small helpers
# ---------------------------------------------------------------------------
def A(c, a):
    return (c[0], c[1], c[2], a)


def mix(a, b, t):
    """Lerp two colours; 3- and 4-tuples may be mixed (alpha defaults to 255)."""
    a = tuple(a) + (255,) * (4 - len(a))
    b = tuple(b) + (255,) * (4 - len(b))
    return lerp(a, b, max(0.0, min(1.0, t)))


def poly(img, pts, fill):
    ImageDraw.Draw(img).polygon([(round(x), round(y)) for x, y in pts], fill=fill)


def line(img, pts, fill, width=1):
    ImageDraw.Draw(img).line([(round(x), round(y)) for x, y in pts], fill=fill, width=width)


def halo(img, cx, cy, r, col, a1=90, a2=40):
    """Two-step translucent glow into transparent pixels only."""
    px = img.load()
    w, h = img.size
    for y in range(int(cy - r) - 1, int(cy + r) + 2):
        for x in range(int(cx - r) - 1, int(cx + r) + 2):
            if 0 <= x < w and 0 <= y < h and px[x, y][3] == 0:
                d = math.hypot(x + 0.5 - cx, y + 0.5 - cy)
                if d <= r * 0.55:
                    px[x, y] = A(col, a1)
                elif d <= r:
                    px[x, y] = A(col, a2)


def pod_window(px, frame, out, recess, glow=None):
    """Prize-pod block frame: bevelled (base, hi, dk) frame around a dark recess (6..25).
    glow tints the recess round the pod while it is still closed."""
    base, hi, dk = frame
    wa.bevel_block(px, base, hi, dk, out, 2)
    for y in range(6, 26):
        for x in range(6, 26):
            c = recess
            if glow is not None:
                d = math.hypot(x + 0.5 - 16, y + 0.5 - 16.5)
                if d < 12.2 and bayer(x, y) < (12.2 - d) / 3.0:
                    c = glow
            px[x, y] = c
    for i in range(6, 26):
        px[i, 6] = out
        px[6, i] = out
        px[i, 25] = hi
        px[25, i] = hi


def rivets(px, hi, dk, out, pts=((4, 4), (26, 4), (4, 26), (26, 26))):
    for (x, y) in pts:
        px[x, y] = hi
        px[x + 1, y] = dk
        px[x, y + 1] = dk
        px[x + 1, y + 1] = out


def cyl(u, x0, x1, ramp):
    """Cylinder shading across [x0, x1]; ramp = (lt, hi, mid, dk, deep). None on the outline columns."""
    lt, hi, mid, dk, deep = ramp
    if u == x0 or u == x1:
        return None
    t = (u - x0) / (x1 - x0)
    if t < 0.08:
        return mid
    if t < 0.14:
        return lt
    if t < 0.24:
        return hi
    if t < 0.32:
        return lt
    if t < 0.62:
        return mid
    if t < 0.70:
        return dk if (u % 2) else mid
    if t < 0.86:
        return dk
    return deep


def pn(x, W, terms):
    """Periodic 1-D noise over width W."""
    return sum(a * math.sin(TAU * k * x / W + p) for (a, k, p) in terms)


def wrapd(dx, W):
    dx = dx % W
    return dx - W if dx > W / 2 else dx


# Jittered, wrapping Voronoi texture for 32x32 rock tiles ---------------------
_VOR = {}


def vor(seed, gx, gy, sy=1.0, stagger=True):
    key = (seed, gx, gy, sy, stagger)
    if key in _VOR:
        return _VOR[key]
    pts = []
    cw, ch = 32 / gx, 32 / gy
    for j in range(gy):
        for i in range(gx):
            off = (cw * 0.5 if (stagger and j % 2) else 0)
            pts.append(((i * cw + off + cw * (0.25 + 0.5 * hsh(i, j, seed))) % 32,
                        (j * ch + ch * (0.25 + 0.5 * hsh(i, j, seed + 1))) % 32))
    grid = {}
    for y in range(32):
        for x in range(32):
            ds = []
            for k, (cx, cy) in enumerate(pts):
                dx = x + 0.5 - cx
                dx -= 32 * round(dx / 32)
                dy = (y + 0.5 - cy)
                dy -= 32 * round(dy / 32)
                dy *= sy
                ds.append((dx * dx + dy * dy, k, dx, dy))
            ds.sort()
            d1, k1, dx1, dy1 = ds[0]
            d2, k2, dx2, dy2 = ds[1]
            vx, vy = dx1 - dx2, dy1 - dy2
            L = math.hypot(vx, vy)
            e = (d2 - d1) / (2 * L) if L > 0 else 9.0
            grid[(x, y)] = (e, vx, vy, k1, dx1, dy1)
    _VOR[key] = grid
    return grid


def rock_px(info, pal, x, y, seed, mortar_w=0.75):
    """pal = (hi, lt, mid, mid2, dk, mortar)."""
    hi, lt, mid, mid2, dk, mortar = pal
    e, vx, vy, k, dx, dy = info
    if e < mortar_w:
        return mortar
    if e < mortar_w + 1.3:
        return dk if (vx * 0.45 + vy * 0.9) < 0 else hi
    c = mid if k % 2 == 0 else mid2
    n = hsh(x, y, seed)
    if n < 0.06:
        return dk
    if n > 0.965:
        return lt
    if (-dx * 0.45 - dy * 0.9) > 3.2 and bayer(x, y) < 0.5:
        return lt
    if (dx * 0.45 + dy * 0.9) > 4.0 and bayer(x, y) < 0.35:
        return dk
    return c


# Background helpers ----------------------------------------------------------
def vgrad(img, stops, steps=8, x_dither=True):
    """Vertical gradient over the whole image, quantised to `steps` colours with Bayer dithering."""
    W, H = img.size
    ramp = []
    for i in range(steps):
        y = i / (steps - 1) * (H - 1)
        for s in range(len(stops) - 1):
            (y0, c0), (y1, c1) = stops[s], stops[s + 1]
            if y0 <= y <= y1:
                ramp.append(mix(c0, c1, (y - y0) / max(1, y1 - y0)))
                break
        else:
            ramp.append(stops[-1][1])
    px = img.load()
    for y in range(H):
        f = y / (H - 1) * (steps - 1)
        base = int(f)
        frac = f - base
        for x in range(W):
            i = base + (1 if frac > bayer(x, y) else 0)
            px[x, y] = ramp[min(steps - 1, i)]
    return ramp


def glow(img, cx, cy, rx, ry, col, strength, wrap=True):
    """Dithered radial brighten of opaque pixels toward col."""
    W, H = img.size
    px = img.load()
    for y in range(max(0, int(cy - ry)), min(H, int(cy + ry) + 1)):
        for xx in range(int(cx - rx), int(cx + rx) + 1):
            x = xx % W if wrap else xx
            if not (0 <= x < W):
                continue
            d = math.hypot((xx + 0.5 - cx) / rx, (y + 0.5 - cy) / ry)
            if d >= 1:
                continue
            amt = (1 - d) * strength
            p = px[x, y]
            if p[3] == 0:
                continue
            # quantise into 3 levels with dithering for a pixel-art falloff
            lvl = amt * 3
            q = int(lvl) + (1 if (lvl - int(lvl)) > bayer(x, y) else 0)
            if q > 0:
                px[x, y] = A(mix(p, col, q / 3 * 0.75), p[3])


def sparkle(px, W, H, x, y, core, arm, big=False):
    for (dx, dy, c) in [(0, 0, core), (1, 0, arm), (-1, 0, arm), (0, 1, arm), (0, -1, arm)] + (
            [(2, 0, arm), (-2, 0, arm), (0, 2, arm), (0, -2, arm)] if big else []):
        if 0 <= y + dy < H:
            px[(x + dx) % W, y + dy] = c


def mask_paint(img, mask, base, rim=None, rim_dark=None, inner=None, inner_depth=0, tex=None, rim_right=False):
    """Paint a boolean mask (bytearray W*H) onto img with a light rim on top/left edges,
    an optional dark rim on bottom/right edges and an optional dithered inner colour that
    grows with distance from the top edge (inner_depth px)."""
    W, H = img.size
    px = img.load()
    for y in range(H):
        row = y * W
        for x in range(W):
            if not mask[row + x]:
                continue
            up = mask[row - W + x] if y > 0 else 1
            left = mask[row + (x - 1) % W]
            right = mask[row + (x + 1) % W]
            down = mask[row + W + x] if y < H - 1 else 1
            c = base
            if rim is not None and (not up or not (right if rim_right else left)):
                c = rim
            elif rim_dark is not None and (not down or not (left if rim_right else right)):
                c = rim_dark
            elif tex is not None:
                c = tex(x, y) or base
            px[x, y] = c
    return img


class Mask:
    """Wrapping (in x) boolean mask for background silhouettes."""

    def __init__(self, W, H):
        self.W, self.H = W, H
        self.m = bytearray(W * H)

    def set(self, x, y):
        if 0 <= y < self.H:
            self.m[y * self.W + x % self.W] = 1

    def get(self, x, y):
        return 0 <= y < self.H and self.m[y * self.W + x % self.W]

    def disc(self, cx, cy, r, ry=None):
        ry = ry or r
        for y in range(max(0, int(cy - ry) - 1), min(self.H, int(cy + ry) + 2)):
            for x in range(int(cx - r) - 1, int(cx + r) + 2):
                if ((x + 0.5 - cx) / r) ** 2 + ((y + 0.5 - cy) / ry) ** 2 <= 1:
                    self.m[y * self.W + x % self.W] = 1

    def vspan(self, x, y0, y1):
        for y in range(max(0, int(y0)), min(self.H, int(y1) + 1)):
            self.m[y * self.W + x % self.W] = 1

    def hspan(self, y, x0, x1):
        if 0 <= y < self.H:
            for x in range(int(x0), int(x1) + 1):
                self.m[y * self.W + x % self.W] = 1

    def curve(self, pts, r0, r1, steps=400):
        """Quadratic/cubic Bezier stroke with radius tapering r0 -> r1."""
        n = len(pts) - 1
        for i in range(steps + 1):
            t = i / steps
            x = y = 0.0
            for k, (px_, py_) in enumerate(pts):
                b = math.comb(n, k) * (t ** k) * ((1 - t) ** (n - k))
                x += b * px_
                y += b * py_
            self.disc(x, y, r0 + (r1 - r0) * t)


def outline_wrap(img, color, alpha_min=1):
    W, H = img.size
    px = img.load()
    add = []
    for y in range(H):
        for x in range(W):
            if px[x, y][3] >= alpha_min:
                continue
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                ny = y + dy
                if 0 <= ny < H:
                    q = px[(x + dx) % W, ny]
                    if q[3] >= 200 and q != color:
                        add.append((x, y))
                        break
    for x, y in add:
        px[x, y] = color


def fold_canvas(W, H, pad=200):
    return Image.new("RGBA", (W + 2 * pad, H), CLEAR), pad


def fold(big, W, pad=200):
    H = big.size[1]
    out = big.crop((pad, 0, pad + W, H))
    out.alpha_composite(big.crop((0, 0, pad, H)), (W - pad, 0))
    out.alpha_composite(big.crop((pad + W, 0, W + 2 * pad, H)), (0, 0))
    return out


# ---------------------------------------------------------------------------
# World base class: structural tiles shared by every set
# ---------------------------------------------------------------------------
class World:
    key = ""
    tiles_name = ""
    OUT = wa.OUT
    seed = 0
    lip = None            # (hi, mid, dk, deep)
    lip_shadow = None
    edge_l = None
    edge_r = None
    edge_dk = None
    lip_base = 9
    lip_amp = 1.6
    drape = 15
    water = None          # (crest, hi, body, deep) with alpha
    checker = ((60, 60, 70, 255), (74, 74, 86, 255))
    bg_color = (0, 0, 0)

    # -- textures ---------------------------------------------------------
    def tex(self, x, y):
        raise NotImplementedError

    def fill(self, px):
        for y in range(TS):
            for x in range(TS):
                px[x, y] = self.tex(x, y)

    def lip_depth(self, x):
        return self.lip_base + int(round(self.lip_amp * math.sin(TAU * x * 2 / 32 + 0.6))) + (
            1 if hsh(x, 0, 5 + self.seed) > 0.72 else 0)

    def lip_px(self, x, y, d):
        hi, mid, dk, deep = self.lip
        if y <= 2:
            return hi
        if y == d:
            return deep
        if y == d - 1:
            return dk
        n = hsh(x, y, 3 + self.seed)
        if y == 3 and n < 0.45:
            return hi
        if n < 0.08:
            return dk
        if n > 0.93:
            return hi
        return mid

    def paint_lip(self, px):
        for x in range(TS):
            d = self.lip_depth(x)
            for y in range(d + 1):
                px[x, y] = self.OUT if y == 0 else self.lip_px(x, y, d)
            if d + 1 < TS and self.lip_shadow:
                px[x, d + 1] = self.lip_shadow

    def lip_extra(self, px):
        pass

    def cap(self, px, mirror):
        def X(x):
            return TS - 1 - x if mirror else x
        hi, mid, dk, deep = self.lip
        for (x, y) in [(0, 0), (1, 0), (0, 1)]:
            px[X(x), y] = CLEAR
        for (x, y) in [(2, 0), (1, 1), (0, 2)]:
            px[X(x), y] = self.OUT
        dr = self.drape
        for y in range(3, TS):
            px[X(0), y] = self.OUT
        for y in range(2, dr + 1):
            px[X(1), y] = hi if y < dr - 3 else dk
            px[X(2), y] = mid if y < dr - 1 else deep
            if y < dr - 4:
                px[X(3), y] = mid
            elif y == dr - 2:
                px[X(3), y] = deep
        px[X(1), dr] = deep
        for y in range(dr + 1, TS):
            px[X(1), y] = self.edge_r if mirror else self.edge_l
            if hsh(X(2), y, 4) < 0.35:
                px[X(2), y] = self.edge_dk

    def top(self, lcap=False, rcap=False):
        img = new(TS, TS)
        px = img.load()
        self.fill(px)
        self.paint_lip(px)
        self.lip_extra(px)
        if lcap:
            self.cap(px, False)
        if rcap:
            self.cap(px, True)
        return img

    def plain(self):
        img = new(TS, TS)
        self.fill(img.load())
        return img

    def side(self, mirror):
        img = new(TS, TS)
        px = img.load()
        self.fill(px)
        x0, x1, x2 = (31, 30, 29) if mirror else (0, 1, 2)
        for y in range(TS):
            px[x0, y] = self.OUT
            px[x1, y] = self.edge_r if mirror else self.edge_l
            if hsh(x2, y, 4) < 0.35:
                px[x2, y] = self.edge_dk
        return img

    def bottom(self):
        img = new(TS, TS)
        px = img.load()
        self.fill(px)
        for x in range(TS):
            px[x, 31] = self.OUT
            px[x, 30] = self.edge_r
            if hsh(x, 29, 3) < 0.5:
                px[x, 29] = self.edge_dk
        return img

    def liquid(self, surface):
        crest, hi, body, deep = self.water
        img = new(TS, TS)
        px = img.load()
        for y in range(TS):
            for x in range(TS):
                if surface:
                    top = 6 + int(round(1.5 * math.sin(TAU * x / 16)))
                    if y < top:
                        continue
                    c = crest if y == top else (hi if y <= top + 2 else body)
                else:
                    c = deep if hsh(x // 3, y // 2, 92 + self.seed) < 0.12 else body
                if c == body and hsh(x // 4, y, 91 + self.seed) < 0.09 and (x % 4) != 3:
                    c = hi
                px[x, y] = c
        return img

    def column(self, half, top):
        img = new(TS, TS)
        px = img.load()
        for y in range(TS):
            for x in range(TS):
                c = self.col_px(x + 32 * half, y, top)
                if c is not None:
                    px[x, y] = c
        return img

    # -- organic column (8/9 crown, 10/11 body) ------------------------------
    # Uneven sides that wobble with height (period 32 so bodies stack seamlessly), a lumpy crown that
    # never overhangs the body, and soft left-lit shading: no tube rim, no parallel stripes.
    def col_edges(self, y):
        y %= 32
        p = self.seed * 0.013
        xl = 5 + 2.0 * math.sin(TAU * y / 32 + p) + 1.0 * math.sin(TAU * 2 * y / 32 + 1.7 + p)
        xr = 58 + 2.0 * math.sin(TAU * y / 32 + 2.4 + p) + 1.0 * math.sin(TAU * 3 * y / 32 + 0.6 + p)
        return int(round(xl)), int(round(xr))

    def crown(self, u):
        return 2 + int(round(1.6 * math.sin(u * 0.45 + self.seed) + 1.2 * math.sin(u * 1.13 + 0.4 * self.seed)))

    def col_inside(self, u, y, top):
        if top and y < 0:
            return False
        xl, xr = self.col_edges(y)
        return xl <= u <= xr and (not top or y >= 32 or y >= self.crown(u))

    def col_shade(self, u, y, t):
        """Ramp index 0..4 lit from the upper left, dithered so it never bands into stripes."""
        v = t + 0.22 * (bayer(u, y) - 0.5)
        return 2 if v < 0.06 else (0 if v < 0.3 else (1 if v < 0.56 else (2 if v < 0.8 else (3 if v < 0.93 else 4))))

    def stack_seam(self, u, y):
        """Distance (rows) below the wavy crack between stacked lumps; two lumps per 32 rows."""
        best = 99
        for base, ph in ((0, 0.0), (16, 2.1)):
            sy = base + 2.2 * math.sin(u * 0.21 + ph + self.seed) + 1.0 * math.sin(u * 0.57 + ph)
            d = (y - int(round(sy))) % 32
            best = min(best, d)
        return best

    def organic_col(self, u, y, top, ramp, tex):
        if not self.col_inside(u, y, top):
            return None
        for (du, dy) in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny = y + dy
            if not top:
                ny %= 32
            if not self.col_inside(u + du, ny, top):
                return self.OUT
        xl, xr = self.col_edges(y)
        t = (u - xl) / max(1, xr - xl)
        idx = self.col_shade(u, y, t)
        c = tex(u, y, top, t, idx)
        return ramp[idx] if c is None else c

    # -- assembly -----------------------------------------------------------
    def tile(self, i):
        if i == 0:
            return self.top()
        if i == 1:
            return self.plain()
        if i == 5:
            return self.top(lcap=True)
        if i == 6:
            return self.top(rcap=True)
        if 8 <= i <= 11:
            return self.column((i - 8) % 2, i < 10)
        if 12 <= i <= 14:
            return self.plat(i - 12)
        if i == 24:
            return self.side(False)
        if i == 25:
            return self.side(True)
        if 26 <= i <= 28:
            return self.ledge(i - 26)
        if i == 30:
            return self.liquid(True)
        if i == 31:
            return self.liquid(False)
        if i == 38:
            return self.bottom()
        if i == 39:
            return new(TS, TS)
        fn = getattr(self, "t%d" % i)
        img = new(TS, TS)
        r = fn(img)
        return r if r is not None else img

    def tileset(self):
        sheet = new(256, 160)
        for i in range(40):
            sheet.paste(self.tile(i), ((i % 8) * TS, (i // 8) * TS))
        return sheet


# ---------------------------------------------------------------------------
# Generic decor painters (parameterised by colour)
# ---------------------------------------------------------------------------
def sign_board(img, wood, hi, dk, glyph, out, glyph_fn=None):
    px = img.load()
    rect(px, 32, 32, 14, 16, 17, 31, dk)
    rect(px, 32, 32, 14, 16, 15, 31, wood)
    rect(px, 32, 32, 4, 6, 27, 18, wood)
    rect(px, 32, 32, 4, 6, 27, 6, hi)
    rect(px, 32, 32, 4, 18, 27, 18, dk)
    for x in range(5, 27):
        if hsh(x, 12, 61) < 0.15:
            px[x, 12] = dk
    if glyph_fn:
        glyph_fn(px)
    else:
        rect(px, 32, 32, 8, 11, 18, 13, glyph)
        for i in range(5):
            for y in range(12 - (4 - i), 12 + (4 - i) + 1):
                px[19 + i, y] = glyph
    outline(img, out)


def tuft(img, cols, blades=None, out=None):
    hi, mid, dk = cols
    px = img.load()
    blades = blades or [(9, -1, 9), (12, 0, 13), (15, 1, 11), (18, 0, 14), (21, 1, 10), (23, 2, 7), (7, -2, 6)]
    for (bx, lean, hgt) in blades:
        for i in range(hgt):
            x = bx + int(round(lean * i / max(1, hgt)))
            y = 31 - i
            px[x, y] = hi if i > hgt - 3 else (mid if i > 2 else dk)
            if i < hgt - 3:
                px[x + 1, y] = dk
    if out:
        outline(img, out)


def mini_mushroom(px, cx, base, r, h, cap, cap_hi, cap_dk, stem, stem_dk, spot=None):
    for y in range(base - h, base + 1):
        for x in range(cx - 1, cx + 2):
            px[x, y] = stem if x < cx + 1 else stem_dk
    cy = base - h
    for y in range(cy - r, cy + 2):
        for x in range(cx - r - 1, cx + r + 2):
            dx, dy = (x + 0.5 - (cx + 0.5)) / (r + 1.2), (y + 0.5 - (cy + 1)) / (r + 0.8)
            if dx * dx + dy * dy <= 1 and y <= cy + 1:
                c = cap
                if dx < -0.2 and dy < -0.3:
                    c = cap_hi
                elif y >= cy + 1:
                    c = cap_dk
                px[x, y] = c
    if spot:
        px[cx - 1, cy - r + 1] = spot
        px[cx + 2, cy - 1] = spot


def shard(img, cx, base, w, h, lean, light, dark, tip=None):
    """A crystal shard: hexagon-ish prism, light left face, dark right face."""
    top = (cx + lean, base - h)
    left = [(cx - w, base), (cx - w + lean * 0.7, base - h + w * 1.2), top, (cx + lean * 0.5, base)]
    right = [(cx + lean * 0.5, base), top, (cx + w + lean * 0.7, base - h + w * 1.2), (cx + w, base)]
    poly(img, left, light)
    poly(img, right, dark)
    if tip:
        put(img.load(), img.size[0], img.size[1], round(top[0]), round(top[1]) + 1, tip)


def cracked_block(img, base, hi, dk, crack, out, seed, pores=0.07):
    """Two-course masonry block with a big crack: the shared 'breakable' look."""
    px = img.load()
    wa.bevel_block(px, base, hi, dk, out, 2)
    for x in range(2, 30):
        px[x, 15] = crack
        px[x, 16] = hi
    for y in range(2, 15):
        px[15, y] = crack
        px[16, y] = hi
    for y in range(17, 30):
        for j in (8, 23):
            px[j, y] = crack
            px[j + 1, y] = hi
    for x in range(3, 29):
        for y in range(3, 29):
            if px[x, y] == base and hsh(x, y, seed) < pores:
                px[x, y] = dk
    pts = [(5, 2), (8, 6), (7, 10), (11, 13), (13, 18), (18, 21), (17, 25), (21, 29)]
    line(img, [(p[0] + 1, p[1]) for p in pts], hi)
    line(img, pts, crack)
    line(img, [(11, 13), (6, 19), (4, 21)], crack)
    line(img, [(18, 21), (24, 19), (27, 20)], crack)


def facet_block(px, base, hi, dk, deep, out, glints=((13, 9), (12, 10), (11, 11), (8, 6), (24, 24), (23, 25))):
    wa.bevel_block(px, base, hi, deep, out, 3)
    for y in range(4, 28):
        for x in range(4, 28):
            dx, dy = x + 0.5 - 16, y + 0.5 - 16
            s = abs(dx) + abs(dy)
            if s <= 11:
                if s > 10:
                    c = deep
                elif dx <= 0 and dy <= 0:
                    c = hi
                elif dx > 0 and dy > 0:
                    c = dk
                else:
                    c = base
                px[x, y] = c
    for (x, y) in glints:
        px[x, y] = (255, 255, 255, 255)


def dome_ledge(img, part, colour, out, gill_bot=25):
    """Mushroom-cap style ledge silhouette (26/27/28): colour(gx, y, top, bot) paints it."""
    px = img.load()
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
            px[x, y] = out if (y == top or y == bot) else colour(x + 32 * part, y, top, bot)
    for y in range(TS):
        for x in range(TS):
            if px[x, y][3] and px[x, y] != out:
                for dx in (-1, 1):
                    nx = x + dx
                    if 0 <= nx < TS and px[nx, y][3] == 0:
                        px[x, y] = out


def leafy(px, x, y, hi, mid, dk, n):
    """Pick a foliage colour with a top-left light."""
    if n < 0.12:
        return dk
    if n > 0.88:
        return hi
    return mid


# ---------------------------------------------------------------------------
# CAVERN: dark violet-slate rock, teal glow moss, cyan + magenta crystals
# ---------------------------------------------------------------------------
class Cavern(World):
    key = "cavern"
    tiles_name = "Cavern Tiles"
    OUT = (20, 16, 36, 255)
    seed = 100
    ROCK = ((150, 140, 192, 255), (112, 104, 154, 255), (84, 78, 126, 255), (78, 72, 118, 255),
            (56, 52, 94, 255), (34, 30, 62, 255))
    DEEP = ((70, 64, 108, 255), (58, 52, 94, 255), (44, 40, 76, 255), (40, 36, 70, 255),
            (30, 26, 54, 255), (16, 12, 30, 255))
    lip = ((168, 240, 200, 255), (82, 186, 154, 255), (44, 128, 120, 255), (28, 82, 92, 255))
    lip_shadow = (40, 36, 70, 255)
    edge_l = (56, 52, 94, 255)
    edge_r = (36, 32, 64, 255)
    edge_dk = (56, 52, 94, 255)
    lip_base = 7
    lip_amp = 1.3
    drape = 13
    water = ((180, 250, 255, 235), (96, 206, 226, 196), (34, 112, 156, 176), (26, 82, 132, 186))
    checker = ((30, 26, 52, 255), (40, 36, 66, 255))
    bg_color = (16, 12, 32)
    GLOW = (210, 255, 236, 255)
    CYAN = ((220, 255, 255, 255), (110, 232, 236, 255), (50, 160, 196, 255), (30, 96, 150, 255))
    PINK = ((255, 200, 246, 255), (232, 110, 214, 255), (164, 60, 170, 255), (96, 36, 120, 255))

    def tex(self, x, y):
        return rock_px(vor(7, 3, 3)[(x, y)], self.ROCK, x, y, 101)

    def lip_extra(self, px):
        for (x, k) in [(4, 3), (5, 5), (13, 2), (20, 4), (21, 2), (27, 3)]:
            d = self.lip_depth(x)
            for j in range(1, k + 1):
                if d + j < TS:
                    px[x, d + j] = self.lip[2] if j < k else self.lip[3]
            if d + k + 1 < TS:
                px[x, d + k + 1] = self.GLOW if k >= 3 else self.lip[3]
        for (x, y) in [(9, 4), (17, 3), (25, 5), (2, 4)]:
            px[x, y] = self.GLOW

    def t2(self, img):  # cracked breakable rock
        px = img.load()
        base, hi, dk, crack = (124, 100, 138, 255), (170, 146, 180, 255), (84, 64, 104, 255), (36, 26, 56, 255)
        wa.bevel_block(px, base, hi, dk, self.OUT, 2)
        for x in range(2, 30):
            px[x, 15] = crack
            px[x, 16] = hi
        for y in range(2, 15):
            px[15, y] = crack
            px[16, y] = hi
        for y in range(17, 30):
            for j in (8, 23):
                px[j, y] = crack
                px[j + 1, y] = hi
        for x in range(3, 29):
            for y in range(3, 29):
                if px[x, y] == base and hsh(x, y, 121) < 0.07:
                    px[x, y] = dk
        pts = [(5, 2), (8, 6), (7, 10), (11, 13), (13, 18), (18, 21), (17, 25), (21, 29)]
        line(img, pts, crack)
        line(img, [(p[0] + 1, p[1]) for p in pts], hi)
        line(img, pts, crack)
        line(img, [(11, 13), (6, 19), (4, 21)], crack)
        line(img, [(18, 21), (24, 19), (27, 20)], crack)

    GEODE = ((150, 140, 192, 255), (112, 104, 154, 255), (78, 72, 118, 255), (50, 46, 84, 255))

    def t3(self, img):  # crystal geode pod: a rough rock egg with glowing cyan seams
        px = img.load()
        pod_window(px, ((72, 66, 112, 255), (126, 118, 168, 255), (40, 36, 70, 255)), self.OUT,
                   (22, 18, 40, 255), (36, 70, 96, 255))
        r_hi, r_mid, r_dk, r_deep = self.GEODE
        c_hi, c_lt, c_mid, c_dk = self.CYAN

        def shade(nx, ny, d):
            if d > 0.9:
                return self.OUT
            seam = abs(nx + 0.22 * math.sin(ny * 5.5) - 0.05)
            branch = abs(ny - 0.15 + 0.5 * nx) if nx > 0.05 else 9
            if seam < 0.07 or branch < 0.07:
                return c_hi
            if seam < 0.15 or branch < 0.14:
                return c_lt
            v = d + 0.35 * (nx + ny) + 0.25 * hsh(int(nx * 30), int(ny * 30), 131)
            return r_hi if v < 0.35 else (r_mid if v < 0.75 else (r_dk if v < 1.05 else r_deep))
        wa.blob(px, 16, 17.5, 9.2, 8.6, shade)
        shard(img, 12, 10, 1.5, 5, -1, c_lt, c_mid, c_hi)
        shard(img, 20, 10, 1.5, 4, 1, c_lt, c_mid, c_hi)
        shard(img, 16, 9.5, 2, 6, 0, c_hi, c_lt, (255, 255, 255, 255))
        for (x, y) in [(8, 9), (23, 8), (9, 24), (24, 23)]:
            px[x, y] = self.GLOW

    def t4(self, img):  # cracked-open geode, emptied: dull crystal teeth round a dark hollow
        px = img.load()
        pod_window(px, ((78, 72, 112, 255), (112, 106, 146, 255), (48, 44, 78, 255)), self.OUT, (22, 18, 40, 255))
        r_hi, r_mid, r_dk, r_deep = self.GEODE

        def shade(nx, ny, d):
            if d > 0.9:
                return self.OUT
            if d > 0.7:
                return r_hi if nx + ny < -0.4 else (r_mid if nx + ny < 0.5 else r_dk)
            ang = math.atan2(ny, nx)
            f = (ang * 7 / math.pi) % 2
            tooth = 0.7 - 0.38 * (1 - abs(f - 1))
            if d > tooth:
                return (132, 150, 176, 255) if f < 1 else (84, 96, 128, 255)
            return (30, 26, 50, 255)
        wa.blob(px, 16, 17, 9.2, 8.8, shade)

    def t7(self, img):  # hard stone with carved rune
        px = img.load()
        base, hi, dk = (98, 94, 132, 255), (150, 146, 184, 255), (58, 54, 92, 255)
        wa.bevel_block(px, base, hi, dk, self.OUT, 3)
        for x in range(5, 27):
            for y in range(5, 27):
                if hsh(x, y, 141) < 0.05:
                    px[x, y] = (84, 80, 118, 255)
        for y in range(8, 25):
            for x in range(8, 25):
                d = abs(x - 16) + abs(y - 16)
                if d in (8, 4):
                    px[x, y] = dk
                    if px[x + 1, y + 1] == base:
                        px[x + 1, y + 1] = hi
        px[16, 16] = self.CYAN[1]
        px[15, 16] = self.CYAN[2]

    COL_RAMP = ((176, 168, 214, 255), (140, 132, 182, 255), (106, 98, 148, 255), (78, 72, 118, 255), (52, 46, 86, 255))

    def crown(self, u):  # jagged rock crags
        return 1 + int(round(2.6 * abs(math.sin(u * math.pi / 11 + 0.4)) + 1.4 * hsh(u // 3, 0, 153)))

    def col_px(self, u, y, top):
        ramp = self.COL_RAMP
        c_hi, c_lt, c_mid, c_dk = self.CYAN

        def tex(u, y, top, t, idx):
            if top and y <= self.crown(u) + 1:
                return self.lip[1] if hsh(u, y, 151) < 0.45 else self.lip[0]
            d = self.stack_seam(u, y)
            if d == 0:
                return ramp[4]
            if d == 1 and idx > 0:
                return ramp[idx - 1]
            # crystal clusters growing from the cracks
            for (cx, cy) in ((44, 17), (14, 2)):
                dx, dy = u - cx, y - cy
                if 0 <= dy <= 4 and abs(dx) <= 2 - dy // 2:
                    return c_hi if dx < 0 else (c_lt if dx == 0 else c_mid)
            if hsh(u, y, 152) < 0.03:
                return ramp[min(4, idx + 1)]
            return None
        return self.organic_col(u, y, top, ramp, tex)

    def plat(self, part):
        img = new(TS, TS)
        px = img.load()
        hi, mid, dk = (184, 132, 88, 255), (142, 96, 62, 255), (98, 62, 44, 255)
        x0 = 2 if part == 0 else 0
        x1 = 29 if part == 2 else 31
        for x in range(x0, x1 + 1):
            for y in range(2, 10):
                c = hi if y == 2 else (dk if y == 9 else mid)
                if (x + part * 32) % 11 == 0:
                    c = dk
                elif y in (5,) and hsh(x, y, 161) < 0.4:
                    c = dk
                px[x, y] = c
        for x in range(x0, x1 + 1):
            if (x + part * 32) % 11 in (2, 9):
                px[x, 3] = (200, 200, 214, 255)
        if part == 1:
            rect(px, 32, 32, 0, 10, 31, 12, dk)
            rect(px, 32, 32, 0, 10, 31, 10, mid)
            for x in (6, 22):
                for y in range(10, 13):
                    px[x, y] = (196, 176, 130, 255)
                    px[x + 1, y] = (150, 128, 90, 255)
        else:
            m = part == 2
            def X(x):
                return 31 - x if m else x
            for y in range(10, 22):
                for x in range(5, 9):
                    px[X(x), y] = hi if x == 5 else (dk if x == 8 else mid)
            line(img, [(X(8), 20), (X(20), 10)], mid, 3)
            line(img, [(X(9), 21), (X(21), 11)], dk)
            rect(px, 32, 32, min(X(5), X(8)), 21, max(X(5), X(8)), 21, dk)
        outline(img, self.OUT)
        return img

    def t15(self, img):
        px = img.load()
        self.fill(px)
        for (cx, cy, pal) in [(7, 8, self.CYAN), (22, 6, self.PINK), (14, 20, self.PINK), (25, 24, self.CYAN), (5, 25, self.CYAN)]:
            hi, lt, mid, dk = pal
            for (dx, dy) in [(0, -2), (-1, -1), (0, -1), (1, -1), (-2, 0), (-1, 0), (0, 0), (1, 0), (2, 0), (-1, 1), (0, 1), (1, 1), (0, 2)]:
                c = hi if (dx + dy) < -1 else (lt if dx <= 0 and dy <= 0 else (mid if dx + dy < 2 else dk))
                px[cx + dx, cy + dy] = c
            for (dx, dy) in [(0, -3), (-3, 0), (3, 0), (0, 3), (-2, -1), (2, -1), (-2, 1), (2, 1), (-1, -2), (1, -2), (-1, 2), (1, 2)]:
                if px[cx + dx, cy + dy] not in (hi, lt, mid, dk):
                    px[cx + dx, cy + dy] = self.OUT

    def t16(self, img):  # glow mushrooms
        px = img.load()
        hi, lt, mid, dk = self.CYAN
        for (cx, base, r, h) in [(10, 30, 4, 7), (20, 30, 3, 4), (25, 30, 2, 2)]:
            mini_mushroom(px, cx, base, r, h, lt, hi, mid, (210, 206, 236, 255), (150, 146, 190, 255), spot=(255, 255, 255, 255))
        for x in (4, 5, 15, 29, 30):
            px[x, 30] = self.lip[1]
            px[x, 29] = self.lip[0] if x % 2 else self.lip[1]
        outline(img, self.OUT)
        halo(img, 10.5, 21, 9, lt, 70, 30)
        halo(img, 20.5, 25, 6, lt, 70, 30)

    def t17(self, img):  # crystal cluster (pink)
        hi, lt, mid, dk = self.PINK
        shard(img, 10, 31, 3, 14, -2, lt, mid)
        shard(img, 17, 31, 4, 22, 1, hi, mid, tip=(255, 255, 255, 255))
        shard(img, 23, 31, 3, 11, 3, lt, dk)
        shard(img, 6, 31, 2, 7, -2, lt, dk)
        outline(img, self.OUT)
        halo(img, 16, 16, 12, lt, 50, 22)

    def t18(self, img):  # bones
        px = img.load()
        bone, shade, deep = (238, 228, 210, 255), (194, 180, 166, 255), (130, 116, 120, 255)
        disc(px, 32, 32, 10.5, 23.5, 6.0, lambda dx, dy, d: shade if dx + dy > 3 else bone)
        rect(px, 32, 32, 7, 27, 14, 30, bone)
        for x in (8, 10, 12):
            px[x, 30] = deep
        for (x, y) in [(7, 22), (8, 22), (7, 23), (8, 23), (12, 22), (13, 22), (12, 23), (13, 23)]:
            px[x, y] = (40, 30, 50, 255)
        px[10, 26] = deep
        for x in range(17, 29):
            px[x, 29] = bone
            px[x, 30] = shade
        for (x, y) in [(16, 28), (16, 29), (16, 31), (29, 28), (29, 29), (29, 31), (15, 29), (30, 29)]:
            px[x, y] = bone
        px[30, 30] = shade
        px[15, 30] = shade
        outline(img, self.OUT)

    def t19(self, img):  # mossy boulder
        px = img.load()
        hi, lt, mid, mid2, dk, mo = self.ROCK

        def sh(dx, dy, d):
            if dy < -3 + 1.2 * math.sin(dx):
                return self.lip[1] if dy > -6 else self.lip[0]
            if dx + dy > 3:
                return dk
            if dx < -2 and dy < 0:
                return lt
            return mid
        disc(px, 32, 32, 16, 25, 9.5, sh)
        for y in range(25, 32):
            for x in range(4, 29):
                if abs(x + 0.5 - 16) < 10 and y > 24:
                    if not px[x, y][3]:
                        px[x, y] = dk if x > 16 else mid
        rect(px, 32, 32, 6, 31, 26, 31, dk)
        for (x, y) in [(12, 17), (19, 16), (15, 18)]:
            px[x, y] = self.GLOW
        outline(img, self.OUT)

    def t20(self, img):  # minecart rails (tile horizontally)
        px = img.load()
        tie, tie_hi, tie_dk = (126, 86, 58, 255), (162, 116, 78, 255), (84, 56, 40, 255)
        for k in (1, 17):
            for x in range(k, k + 12):
                for y in range(27, 32):
                    c = tie_hi if y == 27 else (tie_dk if y >= 30 or x == k + 11 else tie)
                    if x == k or x == k + 11:
                        c = self.OUT
                    px[x, y] = c
        for x in range(32):
            px[x, 22] = self.OUT
            px[x, 23] = (214, 218, 236, 255)
            px[x, 24] = (140, 142, 172, 255)
            px[x, 25] = (92, 92, 124, 255)
            px[x, 26] = self.OUT
            if x % 16 in (5, 6):
                px[x, 24] = (60, 60, 84, 255)
            if x % 16 in (3, 9):
                px[x, 26] = (92, 92, 124, 255)
                px[x, 27] = self.OUT

    def t21(self, img):  # mine sign with down-pointing pick
        def glyph(px):
            c, d = (250, 236, 200, 255), (98, 62, 44, 255)
            for (x, y) in [(9, 10), (10, 9), (11, 9), (12, 9), (13, 9), (14, 9), (15, 9), (16, 10), (17, 11), (8, 11), (7, 12)]:
                px[x, y] = c
            for i in range(6):
                px[12 + i, 10 + i] = c
                px[13 + i, 10 + i] = d if i > 0 else c
            for (x, y) in [(20, 11), (21, 12), (22, 13), (23, 12), (24, 11), (22, 14)]:
                px[x, y] = self.CYAN[1]
        sign_board(img, (132, 94, 70, 255), (172, 130, 96, 255), (90, 60, 48, 255), None, self.OUT, glyph)

    def t22(self, img):  # hanging glow roots (tile vertically)
        px = img.load()
        root, root_dk = (120, 86, 104, 255), (78, 54, 76, 255)
        for y in range(32):
            x = 15 + int(round(2.4 * math.sin(TAU * y / 32)))
            px[x, y] = root
            px[x + 1, y] = root_dk
            x2 = 22 + int(round(1.5 * math.sin(TAU * y / 32 + 2.0)))
            if y < 20:
                px[x2, y] = root_dk
        for x in range(0, 32):
            pass
        outline(img, self.OUT)
        for y in (0, 31):
            for x in range(32):
                if px[x, y] == self.OUT and px[x, 1 if y == 0 else 30] in (root, root_dk):
                    px[x, y] = CLEAR
        for (bx, by) in [(18, 9), (13, 22), (22, 20)]:
            px[bx, by] = self.CYAN[0]
            px[bx, by + 1] = self.CYAN[1]
        halo(img, 18.5, 10, 4, self.CYAN[1], 60, 25)
        halo(img, 13.5, 23, 4, self.CYAN[1], 60, 25)
        halo(img, 22.5, 21, 4, self.CYAN[1], 60, 25)

    def t23(self, img):  # stalagmite
        hi, lt, mid, mid2, dk, mo = self.ROCK
        poly(img, [(6, 31), (14, 6), (16, 5), (17, 12), (21, 18), (26, 31)], mid)
        poly(img, [(6, 31), (14, 6), (15, 6), (12, 31)], lt)
        poly(img, [(18, 31), (17, 12), (21, 18), (26, 31)], dk)
        px = img.load()
        for y in range(14, 31, 5):
            for x in range(9, 24):
                if px[x, y] == mid and hsh(x, y, 171) < 0.5:
                    px[x, y] = dk
        px[15, 6] = hi
        outline(img, self.OUT)

    def ledge(self, part):  # crystal ledge
        img = new(TS, TS)
        px = img.load()
        top_hi, top_c, face, face_lt, face_dk, under = (250, 240, 255, 255), (214, 190, 252, 255), (164, 118, 228, 255), (196, 160, 246, 255), (112, 72, 176, 255), (78, 46, 136, 255)
        for x in range(TS):
            u = x if part == 0 else (TS - 1 - x if part == 2 else 16)
            t = 2 + max(0, 7 - u) if part != 1 else 2
            b = 22 - max(0, int((7 - u) * 1.4)) if part != 1 else 22
            for y in range(t, b + 1):
                gx = x + part * 32
                if y <= t + 3:
                    c = top_hi if y == t + 1 else top_c
                elif y >= b - 2:
                    c = under
                elif y >= b - 6:
                    c = face_dk
                else:
                    c = face
                    if (gx + y) % 13 == 0 or (gx - y) % 17 == 0:
                        c = face_lt
                px[x, y] = c
        # hanging crystal points
        for (cx, w, h) in {0: [(10, 3, 5), (22, 2, 4)], 1: [(5, 3, 6), (16, 2, 3), (25, 3, 5)], 2: [(9, 2, 4), (20, 3, 5)]}[part]:
            for k in range(h):
                hw = w - int(k * w / h)
                for x in range(cx - hw, cx + hw + 1):
                    px[x, 23 + k] = face_dk if x > cx else face
        outline(img, self.OUT)
        for x in range(TS):
            for y in range(TS):
                if px[x, y] == self.OUT and (part == 1 or 0 < x < 31):
                    pass
        if part == 1:
            for y in range(TS):
                for x in (0, 31):
                    if px[x, y] == self.OUT and 2 < y < 23:
                        px[x, y] = px[1 if x == 0 else 30, y]
        else:
            m = part == 2
            ex = 31 if not m else 0
            for y in range(TS):
                if px[ex, y] == self.OUT and 2 < y < 23:
                    px[ex, y] = px[30 if not m else 1, y]
        for (x, y) in [(6 + part * 7, 3), (19 - part * 3, 4)]:
            px[x % 32, y] = (255, 255, 255, 255)
        return img

    def t29(self, img):  # crystal stem
        px = img.load()
        for y in range(32):
            for x in range(10, 22):
                if x in (10, 21):
                    c = self.OUT
                elif x < 14:
                    c = (206, 186, 250, 255)
                elif x < 18:
                    c = (160, 118, 228, 255)
                else:
                    c = (110, 72, 176, 255)
                if (y + x // 4 * 3) % 11 == 0 and x not in (10, 21):
                    c = (226, 210, 255, 255) if x < 16 else (140, 100, 206, 255)
                px[x, y] = c

    def t32(self, img):  # spikes (hazard)
        hi, lt, mid, mid2, dk, mo = self.ROCK
        tip = (255, 130, 176, 255)
        for (cx, h, w) in [(4, 15, 4), (12, 20, 4), (20, 17, 4), (28, 13, 4)]:
            poly(img, [(cx - w, 30), (cx, 30 - h), (cx + w, 30)], mid)
            poly(img, [(cx - w, 30), (cx, 30 - h), (cx, 30)], lt)
            px = img.load()
            for k in range(3):
                put(px, 32, 32, cx, 30 - h + k + 1, tip)
                put(px, 32, 32, cx - 1, 30 - h + k + 2, tip if k < 2 else lt)
        px = img.load()
        rect(px, 32, 32, 0, 29, 31, 31, dk)
        rect(px, 32, 32, 0, 29, 31, 29, mid)
        outline(img, self.OUT)

    def t33(self, img):  # cyan crystal block
        px = img.load()
        base, hi, dk, deep = (96, 222, 232, 255), (226, 255, 255, 255), (44, 152, 196, 255), (48, 72, 150, 255)
        wa.bevel_block(px, base, hi, deep, self.OUT, 3)
        for y in range(4, 28):
            for x in range(4, 28):
                dx, dy = x + 0.5 - 16, y + 0.5 - 16
                s = abs(dx) + abs(dy)
                if s <= 11:
                    if s > 10:
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
        px[20, 12] = self.PINK[1]

    def t34(self, img):
        px = img.load()
        g = vor(7, 3, 3)
        for y in range(TS):
            for x in range(TS):
                px[x, y] = rock_px(g[(x, y)], self.DEEP, x, y, 101)

    def t35(self, img):  # hanging lantern
        px = img.load()
        chain, chain_dk = (150, 150, 176, 255), (90, 90, 120, 255)
        for y in range(0, 10):
            px[16, y] = chain if y % 3 != 2 else chain_dk
            if y % 3 == 2:
                px[15, y] = chain_dk
                px[17, y] = chain_dk
        metal, metal_hi = (70, 64, 96, 255), (120, 112, 150, 255)
        rect(px, 32, 32, 12, 10, 20, 11, metal)
        rect(px, 32, 32, 13, 10, 19, 10, metal_hi)
        glow_c, glow_hi = (255, 196, 84, 255), (255, 246, 196, 255)
        rect(px, 32, 32, 12, 12, 20, 21, glow_c)
        rect(px, 32, 32, 14, 13, 18, 19, glow_hi)
        for y in range(12, 22):
            px[12, y] = metal
            px[20, y] = metal
            px[16, y] = metal if y in (12, 21) else px[16, y]
        rect(px, 32, 32, 12, 22, 20, 23, metal)
        px[16, 24] = metal
        outline(img, self.OUT)
        px[16, 0] = chain
        for x in (15, 17):
            if px[x, 0] == self.OUT:
                px[x, 0] = CLEAR
        halo(img, 16.5, 17, 12, (255, 190, 90), 64, 26)

    def t36(self, img):  # tall glowing mushroom
        px = img.load()
        hi, lt, mid, dk = self.PINK
        for y in range(13, 32):
            for x in range(14, 18):
                px[x, y] = (226, 220, 244, 255) if x < 16 else (168, 160, 200, 255)
        for y in range(4, 15):
            for x in range(4, 29):
                dx, dy = (x + 0.5 - 16) / 12, (y + 0.5 - 13) / 9
                if dx * dx + dy * dy <= 1 and y <= 13:
                    c = mid
                    if dy < -0.55 and dx < 0.2:
                        c = lt
                    if y >= 12:
                        c = dk
                    px[x, y] = c
        for (x, y) in [(10, 8), (11, 8), (16, 6), (17, 6), (21, 9), (22, 9), (8, 11), (24, 11)]:
            px[x, y] = hi
        mini_mushroom(px, 25, 31, 2, 3, mid, lt, dk, (226, 220, 244, 255), (168, 160, 200, 255), spot=hi)
        outline(img, self.OUT)
        halo(img, 16, 10, 15, lt, 50, 22)

    def t37(self, img):  # ground roots (tile horizontally)
        px = img.load()
        root, root_hi, root_dk = (128, 92, 108, 255), (170, 130, 140, 255), (84, 58, 78, 255)
        for (amp, ph, y0, k) in [(3.0, 0.0, 25, 1), (1.5, 2.2, 28, 2)]:
            for x in range(32):
                y = y0 + int(round(amp * math.sin(TAU * k * x / 32 + ph)))
                put(px, 32, 32, x, y, root_hi)
                put(px, 32, 32, x, y + 1, root)
                put(px, 32, 32, x, y + 2, root_dk)
        for (x0, dirn) in [(8, 1), (22, -1)]:
            for i in range(6):
                put(px, 32, 32, x0 + dirn * i // 2, 25 - i, root)
        outline(img, self.OUT)
        for x in (0, 31):
            for y in range(32):
                if px[x, y] == self.OUT and px[1 if x == 0 else 30, y][3] and px[1 if x == 0 else 30, y] != self.OUT:
                    px[x, y] = px[1 if x == 0 else 30, y]

    # -- backgrounds -----------------------------------------------------------
    def backgrounds(self):
        return [("Cavern Back", self.bg_back(), 0.15), ("Cavern Mid", self.bg_mid(), 0.45)]

    def bg_back(self):
        W, H = BW, BH
        img = Image.new("RGBA", (W, H))
        vgrad(img, [(0, (10, 8, 24)), (110, (22, 18, 46)), (210, (42, 32, 78)), (290, (32, 26, 62)), (359, (14, 10, 30))], 9)
        for (cx, cy, rx, ry, col, s) in [(150, 200, 170, 110, (60, 170, 180), 0.55), (530, 170, 200, 120, (130, 80, 200), 0.5),
                                          (850, 210, 150, 90, (60, 170, 180), 0.5)]:
            glow(img, cx, cy, rx, ry, col, s)
        # far formations
        far = bytearray(W * H)
        near = bytearray(W * H)
        ceil_f = [38 + pn(x, W, [(14, 3, 0.2), (8, 7, 1.3), (4, 17, 2.2)]) for x in range(W)]
        floor_f = [258 + pn(x, W, [(16, 2, 1.0), (9, 5, 0.4), (4, 13, 2.9)]) for x in range(W)]
        ceil_n = [18 + pn(x, W, [(10, 2, 2.0), (6, 9, 0.1)]) for x in range(W)]
        floor_n = [300 + pn(x, W, [(14, 3, 2.4), (7, 8, 1.1)]) for x in range(W)]
        spikes_f = [(int(hsh(i, 1, 201) * W), 5 + hsh(i, 2, 201) * 9, 20 + hsh(i, 3, 201) * 60) for i in range(34)]
        mites_f = [(int(hsh(i, 4, 201) * W), 6 + hsh(i, 5, 201) * 10, 14 + hsh(i, 6, 201) * 46) for i in range(26)]
        spikes_n = [(int(hsh(i, 1, 202) * W), 8 + hsh(i, 2, 202) * 12, 30 + hsh(i, 3, 202) * 70) for i in range(16)]
        mites_n = [(int(hsh(i, 4, 202) * W), 9 + hsh(i, 5, 202) * 12, 20 + hsh(i, 6, 202) * 50) for i in range(14)]

        cf, ff, cn, fn = ceil_f[:], floor_f[:], ceil_n[:], floor_n[:]
        for (cx, w, L) in spikes_f:
            for dx in range(-int(w), int(w) + 1):
                x = (cx + dx) % W
                cf[x] = max(cf[x], ceil_f[x] + L * (1 - abs(dx) / w) ** 1.3)
        for (cx, w, L) in mites_f:
            for dx in range(-int(w), int(w) + 1):
                x = (cx + dx) % W
                ff[x] = min(ff[x], floor_f[x] - L * (1 - abs(dx) / w) ** 1.3)
        for (cx, w, L) in spikes_n:
            for dx in range(-int(w), int(w) + 1):
                x = (cx + dx) % W
                cn[x] = max(cn[x], ceil_n[x] + L * (1 - abs(dx) / w) ** 1.3)
        for (cx, w, L) in mites_n:
            for dx in range(-int(w), int(w) + 1):
                x = (cx + dx) % W
                fn[x] = min(fn[x], floor_n[x] - L * (1 - abs(dx) / w) ** 1.3)
        pillars_f = [(300, 26), (700, 34), (980, 22)]
        pillars_n = [(80, 40), (560, 46)]

        def pillar_hw(base, y):
            return base * (0.55 + 0.15 * math.sin(y / 23.0 + base)) + 46 * math.exp(-y / 45.0) + 40 * math.exp(-(H - y) / 50.0)
        for y in range(H):
            row = y * W
            for x in range(W):
                if y < cf[x] or y > ff[x]:
                    far[row + x] = 1
                if y < cn[x] or y > fn[x]:
                    near[row + x] = 1
            for (cx, b) in pillars_f:
                hw = pillar_hw(b, y)
                for dx in range(-int(hw), int(hw) + 1):
                    far[row + (cx + dx) % W] = 1
            for (cx, b) in pillars_n:
                hw = pillar_hw(b, y)
                for dx in range(-int(hw), int(hw) + 1):
                    near[row + (cx + dx) % W] = 1

        def far_tex(x, y):
            if hsh(x // 2, y // 7, 211) < 0.12:
                return (40, 32, 74, 255)
            return None
        mask_paint(img, far, (50, 40, 88, 255), rim=(78, 66, 130, 255), tex=far_tex)

        def near_tex(x, y):
            if hsh(x // 2, y // 9, 212) < 0.10:
                return (22, 18, 44, 255)
            return None
        mask_paint(img, near, (30, 24, 56, 255), rim=(58, 48, 104, 255), tex=near_tex)
        # crystal clusters on the near floor + far glints
        px = img.load()
        for i in range(14):
            cx = int(hsh(i, 7, 203) * W)
            y = int(fn[cx]) + 2
            pal = self.CYAN if i % 3 else self.PINK
            glow(img, cx, y - 6, 34, 22, pal[1], 0.55)
            big, pad = fold_canvas(W, H)
            for k in range(3):
                h = 7 + hsh(i, k, 204) * 12
                shard(big, pad + cx + (k - 1) * 5, y + 2, 2 + (k == 1), h, (k - 1) * 2, pal[1], pal[2], tip=pal[0])
            img.alpha_composite(fold(big, W))
        for i in range(10):
            cx = int(hsh(i, 8, 205) * W)
            y = int(cn[cx]) - 2
            pal = self.PINK if i % 2 else self.CYAN
            glow(img, cx, y + 4, 26, 16, pal[1], 0.45)
            big, pad = fold_canvas(W, H)
            for k in range(2):
                h = 6 + hsh(i, k, 206) * 9
                shard(big, pad + cx + k * 5, y - 3, 2, -h, 0, pal[1], pal[2])
            img.alpha_composite(fold(big, W))
        for i in range(120):
            x = int(hsh(i, 9, 207) * W)
            y = int(hsh(i, 10, 207) * H)
            p = px[x, y]
            if p[:3] in ((50, 40, 88), (40, 32, 74), (30, 24, 56), (22, 18, 44)):
                c = (170, 250, 255, 255) if i % 3 else (255, 180, 240, 255)
                if i % 7 == 0:
                    sparkle(px, W, H, x, y, (255, 255, 255, 255), A(c, 255), big=False)
                else:
                    px[x, y] = c
        # drips of light falling from ceiling cracks
        for i in range(6):
            cx = int(hsh(i, 11, 208) * W)
            for y in range(int(cn[cx]), int(cn[cx]) + 120):
                if y < H and bayer(cx, y) < 0.5 * (1 - (y - cn[cx]) / 120):
                    for dx in (-1, 0, 1):
                        x = (cx + dx) % W
                        if px[x, y][:3] not in ((30, 24, 56), (58, 48, 104), (22, 18, 44)):
                            px[x, y] = A(mix(px[x, y], (90, 200, 210, 255), 0.35), 255)
        return img

    def bg_mid(self):
        W, H = BW, BH
        img = new(W, H)
        m = bytearray(W * H)
        top = [8 + pn(x, W, [(5, 3, 0.4), (3, 11, 1.7)]) for x in range(W)]
        bot = [H - 26 + pn(x, W, [(6, 2, 1.1), (3, 9, 0.3)]) for x in range(W)]
        t2, b2 = top[:], bot[:]
        for i in range(22):
            cx = int((i + hsh(i, 1, 221) * 0.6) * W / 22)
            w = 10 + hsh(i, 2, 221) * 22
            L = 40 + hsh(i, 3, 221) * 110 * (1 if i % 3 else 0.5)
            for dx in range(-int(w), int(w) + 1):
                x = (cx + dx) % W
                t2[x] = max(t2[x], top[x] + L * (1 - abs(dx) / w) ** 1.6)
        for i in range(14):
            cx = int((i + 0.5 + hsh(i, 4, 221) * 0.5) * W / 14)
            w = 12 + hsh(i, 5, 221) * 20
            L = 30 + hsh(i, 6, 221) * 80
            for dx in range(-int(w), int(w) + 1):
                x = (cx + dx) % W
                b2[x] = min(b2[x], bot[x] - L * (1 - abs(dx) / w) ** 1.6)
        for y in range(H):
            row = y * W
            for x in range(W):
                if y < t2[x] or y > b2[x]:
                    m[row + x] = 1
        base, rim, dark = (36, 28, 64, 255), (96, 78, 156, 255), (24, 18, 44, 255)

        def tex(x, y):
            # lighter towards the tips: dithered
            if y < 180:
                depth = t2[x] - y
            else:
                depth = y - b2[x]
            if depth < 14 and bayer(x, y) < 0.5:
                return (52, 42, 90, 255)
            if hsh(x // 2, y // 6, 222) < 0.12:
                return dark
            return None
        mask_paint(img, m, base, rim=rim, rim_dark=dark, tex=tex)
        px = img.load()
        # moss beards and glowing tips on stalactites
        for x in range(W):
            if t2[x] - top[x] > 40 and hsh(x, 0, 223) < 0.08:
                y = int(t2[x])
                for k in range(int(4 + hsh(x, 1, 223) * 10)):
                    if y + k < H:
                        px[x, y + k] = (60, 150, 130, 255) if k < 3 else (44, 110, 104, 255)
                if y + 14 < H:
                    px[x, min(H - 1, y + int(4 + hsh(x, 1, 223) * 10))] = (200, 255, 236, 255)
        for i in range(30):
            x = int(hsh(i, 7, 224) * W)
            y = int(b2[x]) + 1
            if y < H - 3:
                pal = self.CYAN if i % 2 else self.PINK
                px[x, y] = pal[1]
                px[x, y - 1] = pal[0]
                halo(img, x + 0.5, y, 3.5, pal[1], 70, 30)
        return img

    def sky(self):
        return None


# ---------------------------------------------------------------------------
# Previews
# ---------------------------------------------------------------------------
class Grotto(World):
    key = "grotto"
    tiles_name = "Grotto Tiles"
    OUT = (14, 34, 54, 255)
    seed = 300
    ROCK = ((132, 196, 200, 255), (100, 164, 176, 255), (72, 130, 150, 255), (66, 122, 144, 255),
            (46, 94, 120, 255), (28, 60, 88, 255))
    DEEP = ((52, 92, 124, 255), (42, 78, 110, 255), (32, 62, 94, 255), (30, 58, 88, 255),
            (24, 46, 74, 255), (14, 28, 50, 255))
    SAND = (218, 198, 150, 255)
    lip = ((252, 240, 196, 255), (230, 208, 154, 255), (190, 164, 116, 255), (140, 118, 92, 255))
    lip_shadow = (46, 94, 120, 255)
    edge_l = (46, 94, 120, 255)
    edge_r = (28, 60, 88, 255)
    edge_dk = (46, 94, 120, 255)
    lip_base = 8
    lip_amp = 1.2
    drape = 14
    water = ((226, 255, 255, 220), (160, 236, 240, 170), (96, 204, 222, 120), (76, 176, 210, 132))
    checker = ((30, 86, 116, 255), (36, 100, 132, 255))
    bg_color = (14, 52, 84)
    CORAL = ((255, 216, 198, 255), (255, 170, 150, 255), (236, 112, 114, 255), (182, 70, 94, 255), (118, 44, 80, 255))
    KELP = ((176, 218, 104, 255), (114, 174, 76, 255), (62, 116, 64, 255))
    PEARL = ((255, 255, 255, 255), (226, 240, 255, 255), (170, 196, 230, 255))

    def tex(self, x, y):
        c = rock_px(vor(13, 2, 3)[(x, y)], self.ROCK, x, y, 301)
        if c != self.ROCK[5] and hsh(x, y, 302) < 0.03:
            return self.SAND
        return c

    def lip_extra(self, px):
        rip = (206, 180, 130, 255)
        for x in range(32):
            if (x // 3) % 3 != 2:
                y = 5 + int(round(0.8 * math.sin(TAU * x / 16)))
                if px[x, y] == self.lip[1]:
                    px[x, y] = rip
        for (x, y, c) in [(10, 4, (250, 150, 150, 255)), (11, 4, (250, 150, 150, 255)), (10, 3, (255, 210, 200, 255)),
                          (24, 6, (255, 255, 255, 255)), (25, 6, (200, 214, 230, 255)), (3, 6, (120, 180, 200, 255))]:
            px[x, y] = c

    def t2(self, img):  # cracked coral
        cracked_block(img, (236, 122, 124, 255), (255, 182, 168, 255), (180, 74, 96, 255), (110, 40, 72, 255), self.OUT, 311, 0.10)
        px = img.load()
        for (x, y) in [(5, 5), (11, 8), (21, 5), (26, 10), (5, 21), (13, 25), (20, 23), (27, 26)]:
            px[x, y] = (110, 40, 72, 255)
            px[x - 1, y - 1] = (255, 200, 186, 255)

    GROTTO_FRAME = ((100, 164, 176, 255), (150, 206, 208, 255), (46, 94, 120, 255))

    def clam_shell(self, px, cy, ry, flip, pal, inner=None):
        """Ridged half-shell: flip=False is a bowl (hinge at the bottom), True a lid (hinge at the top)."""
        hi, mid, dk = pal

        def shade(nx, ny, d):
            if (ny < 0) != flip and abs(ny) > 0.05:
                return None
            if d > 0.88:
                return self.OUT
            if inner is not None and d < 0.72 and abs(ny) < 0.62:
                return inner(nx, ny, d)
            ang = math.atan2(nx, abs(ny) + 0.3)
            ridge = int((ang + 2) * 7) % 2
            c = mid if ridge else hi
            if nx > 0.35:
                c = dk if ridge else mid
            return c
        wa.blob(px, 16, cy, 10, ry, shade)

    def t3(self, img):  # clam pearl pod: a closed shell with pearl light leaking from its lip
        px = img.load()
        pod_window(px, self.GROTTO_FRAME, self.OUT, (16, 40, 62, 255), (64, 104, 128, 255))
        hi, mid, dk, deep = self.CORAL[0], self.CORAL[1], self.CORAL[2], self.CORAL[3]
        self.clam_shell(px, 16.5, 7.5, True, (hi, mid, dk))
        self.clam_shell(px, 16.5, 8.5, False, (mid, dk, deep))
        for x in range(8, 25):
            y = 16 + (1 if 11 <= x <= 20 else 0)
            px[x, y] = self.PEARL[0]
            if 12 <= x <= 19:
                px[x, y - 1] = (255, 250, 214, 255)
        for (x, y) in [(16, 25), (15, 25), (17, 25), (16, 24)]:
            px[x, y] = self.CORAL[4]
        for (x, y) in [(8, 9), (24, 10), (23, 23)]:
            px[x, y] = self.PEARL[1]

    def t4(self, img):  # clam opened and emptied: lid raised, nacre bowl with an empty pearl dimple
        px = img.load()
        pod_window(px, ((86, 140, 152, 255), (124, 176, 182, 255), (40, 82, 106, 255)), self.OUT, (16, 40, 62, 255))
        pale = ((226, 204, 196, 255), (196, 168, 164, 255), (150, 118, 124, 255))
        nacre, nacre_dk = (238, 214, 220, 255), (200, 168, 184, 255)

        self.clam_shell(px, 12.5, 5.5, True, pale)
        self.clam_shell(px, 19.5, 6.0, False, pale)

        def inside(nx, ny, d):
            if d > 0.86:
                return self.OUT
            if math.hypot(nx * 1.0, ny * 0.5) < 0.2:
                return (150, 112, 132, 255)
            return nacre if ny < 0.2 and nx < 0.4 else nacre_dk
        wa.blob(px, 16, 19.5, 9.4, 3.6, inside)
        # the lid's pale underside
        for x in range(9, 24):
            px[x, 13] = nacre_dk

    def barnacle(self, px, x, y):
        for (dx, dy, c) in [(-1, 0, (214, 220, 214, 255)), (0, 0, (60, 70, 80, 255)), (1, 0, (170, 176, 176, 255)),
                            (-1, 1, (190, 196, 190, 255)), (0, 1, (214, 220, 214, 255)), (1, 1, (150, 156, 160, 255)), (0, -1, (240, 244, 240, 255))]:
            px[x + dx, y + dy] = c

    def t7(self, img):
        px = img.load()
        wa.bevel_block(px, (98, 146, 160, 255), (150, 196, 204, 255), (56, 98, 120, 255), self.OUT, 3)
        for x in range(5, 27):
            for y in range(5, 27):
                if hsh(x, y, 331) < 0.05:
                    px[x, y] = (80, 126, 142, 255)
        for (x, y) in [(8, 9), (22, 7), (15, 18), (24, 23), (9, 24)]:
            self.barnacle(px, x, y)

    CORAL_FINGERS = ((9, 3, 3.0), (18, 0, 3.4), (27, 4, 3.0), (36, 1, 3.4), (45, 3, 3.0), (54, 5, 2.8))

    def crown(self, u):  # rounded coral fingers rising from the rock, with gaps between them
        best = 12
        for (c, tip, hw) in self.CORAL_FINGERS:
            dx = abs(u - c) / hw
            if dx <= 1.0:
                best = min(best, tip + int(round(2.0 * dx * dx)))
        return best

    def col_px(self, u, y, top):
        r = self.ROCK
        ramp = (r[0], r[1], r[2], r[4], r[5])
        co = self.CORAL

        def tex(u, y, top, t, idx):
            if top and y < 13 + int(round(1.5 * math.sin(u * 0.7))):
                # coral branches: lit tips over darker roots
                if y <= self.crown(u) + 1:
                    return co[0]
                return co[1] if t < 0.45 else (co[2] if t < 0.8 else co[3])
            d = self.stack_seam(u, y)
            if d == 0:
                return r[5]
            if d == 1 and idx > 0:
                return ramp[idx - 1]
            # barnacles
            if hsh(u // 2, y // 2, 342) < 0.07 and (u % 2 + y % 2) < 2:
                return (60, 70, 80, 255) if (u % 2 == 1 and y % 2 == 1) else (214, 220, 214, 255)
            # coral and kelp tufts clinging to the sides
            if t < 0.16 and 18 <= y <= 23:
                return co[1] if (u + y) % 2 else co[2]
            if t > 0.84 and 6 <= y <= 10:
                return self.KELP[1] if t < 0.92 else self.KELP[2]
            if hsh(u, y, 343) < 0.04:
                return self.SAND
            return None
        return self.organic_col(u, y, top, ramp, tex)

    def plat(self, part):  # shell platform
        img = new(TS, TS)
        px = img.load()
        base, rib, dk, hi = (250, 228, 214, 255), (228, 190, 182, 255), (192, 146, 152, 255), (255, 244, 236, 255)
        for x in range(TS):
            u = x + part * 32
            yb = 10 + int(round(2.5 * math.sqrt(max(0.0, 1 - (((u % 8) - 3.5) / 4.0) ** 2))))
            for y in range(2, yb + 1):
                if part == 0 and x < 6 and math.hypot((x + 0.5 - 6) / 6, (y + 0.5 - 7) / 6) > 1:
                    continue
                if part == 2 and x > 25 and math.hypot((x + 0.5 - 26) / 6, (y + 0.5 - 7) / 6) > 1:
                    continue
                c = base
                if u % 4 == 0:
                    c = rib
                if y == 2:
                    c = hi
                if y >= yb - 1:
                    c = dk
                if y == 3 and u % 4 != 0:
                    c = (255, 214, 206, 255)
                px[x, y] = c
        outline(img, self.OUT)
        return img

    def t15(self, img):
        px = img.load()
        self.fill(px)
        cx, cy = 11.5, 12.5
        shell, sh_dk = (226, 210, 176, 255), (160, 140, 110, 255)
        for y in range(5, 21):
            for x in range(4, 20):
                d = math.hypot(x + 0.5 - cx, y + 0.5 - cy)
                if d <= 6.5:
                    ang = math.atan2(y + 0.5 - cy, x + 0.5 - cx)
                    spiral = (d - ang * 1.1) % 3.4
                    px[x, y] = sh_dk if (d > 5.8 or spiral < 0.9) else shell
        for (x, y) in [(22, 24), (23, 24), (24, 24), (22, 25), (24, 25), (23, 23)]:
            px[x, y] = (240, 170, 160, 255)
        px[23, 25] = (190, 110, 120, 255)

    def t16(self, img):  # kelp (stacks vertically)
        px = img.load()
        hi, mid, dk = self.KELP
        for y in range(32):
            x = 15 + int(round(2 * math.sin(TAU * y / 32)))
            px[x, y] = mid
            px[x + 1, y] = dk
        for (yy, side) in [(4, 1), (14, -1), (24, 1)]:
            x0 = 16 + int(round(2 * math.sin(TAU * yy / 32)))
            for i in range(1, 9):
                xx = x0 + side * i
                ylift = yy - i // 2
                for k in range(3 - (i > 6)):
                    put(px, 32, 32, xx, ylift + k, hi if k == 0 else (mid if k == 1 else dk))
        outline(img, self.OUT)

    def t17(self, img):  # anemone
        px = img.load()
        body, body_dk, tent, tip = (150, 80, 170, 255), (104, 50, 130, 255), (220, 120, 200, 255), (255, 210, 240, 255)
        for i in range(9):
            bx = 8 + i * 2
            hgt = 9 + int(4 * math.sin(i * 1.7))
            for k in range(hgt):
                x = bx + int(round(1.6 * math.sin(k / 3.0 + i)))
                y = 25 - k
                px[x, y] = tip if k >= hgt - 2 else tent
        for y in range(24, 32):
            for x in range(6, 27):
                if abs(x + 0.5 - 16) <= 10 - (31 - y) * 0.3:
                    px[x, y] = body if x < 17 else body_dk
        for x in range(8, 25, 3):
            px[x, 27] = (190, 110, 200, 255)
        outline(img, self.OUT)

    def t18(self, img):  # shells
        px = img.load()
        c1, c1d = (255, 226, 200, 255), (220, 170, 150, 255)
        for y in range(18, 31):
            for x in range(4, 18):
                dx, dy = x + 0.5 - 10, y + 0.5 - 26
                if dy <= 3 and math.hypot(dx * 0.8, dy * 1.2) <= 6 + max(0, -dy) * 0.3 and abs(dx) <= 7 - max(0, -dy - 2) * 0.9:
                    px[x, y] = c1d if (int(math.atan2(dx, -dy + 6) * 6) % 2) else c1
        s1, s1d = (250, 170, 180, 255), (200, 110, 140, 255)
        for y in range(22, 31):
            for x in range(18, 30):
                d = math.hypot(x + 0.5 - 24, (y + 0.5 - 27) * 1.3)
                if d <= 5:
                    px[x, y] = s1d if (d - math.atan2(y - 27, x - 24)) % 2.5 < 0.8 else s1
        outline(img, self.OUT)

    def t19(self, img):  # starfish on a rock
        px = img.load()
        hi, lt, mid, mid2, dk, mo = self.ROCK
        disc(px, 32, 32, 16, 31, 13, lambda dx, dy, d: lt if (dx < -3 and dy < -6) else (dk if dx > 6 else mid))
        star, star_dk = (255, 150, 70, 255), (214, 90, 52, 255)
        pts = []
        for k in range(10):
            ang = -math.pi / 2 + k * math.pi / 5
            r = 8 if k % 2 == 0 else 3.5
            pts.append((16 + r * math.cos(ang), 19 + r * math.sin(ang)))
        poly(img, pts, star)
        for (x, y) in [(16, 14), (16, 16), (12, 18), (20, 18), (14, 22), (18, 22)]:
            px[x, y] = (255, 210, 160, 255)
        px[17, 21] = star_dk
        outline(img, self.OUT)

    def t20(self, img):  # sea grass (tiles horizontally)
        px = img.load()
        hi, mid, dk = (130, 200, 150, 255), (70, 150, 120, 255), (40, 100, 96, 255)
        for bx in range(0, 32, 3):
            hgt = 5 + int(4 * (0.5 + 0.5 * math.sin(TAU * bx / 32 * 2 + 0.7)))
            for i in range(hgt):
                x = (bx + int(round(1.2 * math.sin(i / 2.5)))) % 32
                px[x, 31 - i] = hi if i > hgt - 2 else (mid if i > 1 else dk)

    def t21(self, img):  # carved ruin tablet
        def glyph(px):
            c = (40, 80, 96, 255)
            rect(px, 32, 32, 8, 11, 18, 13, c)
            for i in range(5):
                for y in range(12 - (4 - i), 12 + (4 - i) + 1):
                    px[19 + i, y] = c
            for x in range(6, 26, 4):
                px[x, 8] = (120, 170, 180, 255)
        sign_board(img, (164, 196, 196, 255), (206, 230, 226, 255), (110, 146, 156, 255), None, self.OUT, glyph)
        px = img.load()
        for (x, y) in [(6, 16), (26, 7)]:
            px[x, y] = self.KELP[1]
            px[x, y + 1] = self.KELP[2]

    def t22(self, img):  # hanging pearl strand (stacks vertically)
        px = img.load()
        hi, mid, dk = self.KELP
        for y in range(32):
            x = 16 + int(round(1.5 * math.sin(TAU * y / 32 + 1)))
            px[x, y] = mid
            px[x + 1, y] = dk
        for (yy, side) in [(8, -1), (24, 1)]:
            x0 = 16 + int(round(1.5 * math.sin(TAU * yy / 32 + 1)))
            for i in range(1, 6):
                put(px, 32, 32, x0 + side * i + (1 if side > 0 else 0), yy + i // 2, hi if i < 4 else mid)
        outline(img, self.OUT)
        for (cx, cy) in [(13, 16), (20, 4)]:
            disc(px, 32, 32, cx + 0.5, cy + 0.5, 2.2, lambda dx, dy, d: self.PEARL[0] if dx + dy < -0.5 else self.PEARL[2])
        halo(img, 13.5, 16.5, 5, (200, 240, 255), 60, 26)

    def t23(self, img):  # brain coral
        px = img.load()
        hi, mid, dk = (230, 220, 140, 255), (190, 180, 96, 255), (130, 120, 70, 255)
        for y in range(14, 32):
            for x in range(3, 29):
                dx, dy = (x + 0.5 - 16) / 13, (y + 0.5 - 31) / 16
                if dx * dx + dy * dy <= 1:
                    v = math.sin(x * 0.9 + 2.0 * math.sin(y * 0.6)) + math.sin(y * 1.1)
                    c = dk if abs(v) < 0.35 else (hi if (dx < -0.3 and dy < -0.4) else mid)
                    px[x, y] = c
        outline(img, self.OUT)

    def ledge(self, part):  # giant scallop shell ledge
        img = new(TS, TS)
        pink, rib, hi, lipc, lipd = (252, 176, 176, 255), (218, 124, 144, 255), (255, 222, 214, 255), (255, 240, 226, 255), (196, 150, 150, 255)

        def colour(gx, y, top, bot):
            if y == top + 1:
                return hi
            if y >= bot - 3:
                return lipd if y == bot - 3 else lipc
            fx = 16 + (gx // 32) * 32
            ang = math.atan2(gx + 0.5 - fx, 34 - y)
            if (ang * 7) % 1.0 < 0.2:
                return rib
            if y > bot - 7 and bayer(gx, y) < 0.4:
                return rib
            return pink
        dome_ledge(img, part, colour, self.OUT, 24)
        return img

    def t29(self, img):  # coral stalk
        px = img.load()
        ramp = self.CORAL
        for y in range(32):
            for x in range(12, 21):
                c = cyl(x, 12, 20, ramp)
                px[x, y] = c if c is not None else self.OUT
        for (x, y, side) in [(11, 8, -1), (20, 20, 1)]:
            for i in range(3):
                px[x + side * i, y - i] = ramp[2]
                px[x + side * i, y - i - 1] = ramp[1]
        outline(img, self.OUT)

    def t32(self, img):  # sea urchins
        px = img.load()
        body, body_hi, spine, tip = (70, 40, 100, 255), (120, 80, 150, 255), (40, 24, 64, 255), (220, 180, 255, 255)
        urchins = [(9, 26, 4.5, 6), (22, 25, 5.5, 7), (15, 29, 3, 4)]
        for (cx, cy, r, L) in urchins:
            disc(px, 32, 32, cx, cy, r, lambda dx, dy, d: body_hi if (dx < -1 and dy < -1) else body)
        rect(px, 32, 32, 2, 30, 29, 31, body)
        outline(img, self.OUT)
        spine_c = (176, 140, 220, 255)
        for (cx, cy, r, L) in urchins:
            for k in range(11):
                ang = math.pi + (k + 0.5) * math.pi / 11
                for s in range(int(r) + 1, int(r + L)):
                    x, y = int(round(cx + s * math.cos(ang))), int(round(cy + s * math.sin(ang)))
                    if 0 <= x < 32 and 0 <= y < 32 and px[x, y][3] == 0:
                        px[x, y] = tip if s >= r + L - 2 else spine_c
            px[int(cx) - 1, int(cy) - 2] = (220, 190, 250, 255)

    def t33(self, img):  # sea-glass block
        px = img.load()
        facet_block(px, (130, 230, 214, 255), (232, 255, 248, 255), (62, 176, 182, 255), (40, 108, 150, 255), self.OUT)
        for (x, y) in [(20, 8), (21, 7), (9, 21)]:
            px[x, y] = (210, 250, 255, 255)

    def t34(self, img):
        px = img.load()
        g = vor(13, 2, 3)
        for y in range(TS):
            for x in range(TS):
                px[x, y] = rock_px(g[(x, y)], self.DEEP, x, y, 301)

    def t35(self, img):  # glowing pearl lamp
        px = img.load()
        ramp = self.CORAL
        for y in range(19, 32):
            for x in range(14, 19):
                px[x, y] = ramp[1] if x < 16 else ramp[3]
        rect(px, 32, 32, 10, 29, 22, 31, ramp[3])
        rect(px, 32, 32, 10, 29, 22, 29, ramp[2])
        sh, sh_dk = (250, 214, 200, 255), (210, 150, 150, 255)
        for x in range(7, 26):
            d = abs(x + 0.5 - 16.5)
            ybot = 19
            ytop = 19 - int(4 * math.sqrt(max(0, 1 - (d / 9.5) ** 2)))
            for y in range(ytop, ybot + 1):
                px[x, y] = sh_dk if (x % 3 == 0 or y == ybot) else sh
            yt2 = 8 + int(3 * (d / 9.5) ** 2)
            for y in range(yt2, yt2 + 3):
                px[x, y] = sh_dk if (x % 3 == 0 or y == yt2 + 2) else sh
        outline(img, self.OUT)
        disc(px, 32, 32, 16.5, 13.5, 4.0, lambda dx, dy, d: (255, 255, 255, 255) if (dx < 0 and dy < 0) else ((226, 240, 255, 255) if d < 3 else (170, 200, 236, 255)))
        halo(img, 16.5, 13.5, 13, (200, 240, 255), 64, 26)

    def t36(self, img):  # tube coral
        px = img.load()
        for (x0, w, h, pal) in [(6, 5, 15, ((200, 150, 230, 255), (150, 96, 196, 255), (96, 56, 150, 255))),
                                (13, 6, 24, ((255, 190, 120, 255), (236, 140, 80, 255), (180, 90, 60, 255))),
                                (21, 5, 11, ((200, 150, 230, 255), (150, 96, 196, 255), (96, 56, 150, 255)))]:
            hi, mid, dk = pal
            for y in range(31 - h, 32):
                for x in range(x0, x0 + w):
                    px[x, y] = hi if x == x0 + 1 else (dk if x == x0 + w - 1 else mid)
            for x in range(x0, x0 + w):
                px[x, 31 - h] = hi
            for x in range(x0 + 1, x0 + w - 1):
                px[x, 32 - h] = (60, 30, 60, 255)
        outline(img, self.OUT)

    def t37(self, img):  # sea sprouts and pebbles (tiles horizontally)
        px = img.load()
        for (x, h) in [(3, 5), (5, 7), (14, 4), (16, 6), (25, 5), (27, 8)]:
            for i in range(h):
                px[x + (1 if i > h // 2 else 0), 31 - i] = self.KELP[0] if i > h - 3 else self.KELP[1]
        for (cx, r, c) in [(9, 2.2, (170, 190, 200, 255)), (21, 1.8, (210, 196, 170, 255)), (30, 1.5, (150, 176, 190, 255))]:
            disc(px, 32, 32, cx + 0.5, 30.5, r, lambda dx, dy, d, c=c: c if dy < 0.5 else mix(c, (60, 80, 100, 255), 0.4))
        outline(img, self.OUT)

    # -- backgrounds ---------------------------------------------------------
    def sky(self):
        W, H = SW, SH
        img = Image.new("RGBA", (W, H))
        vgrad(img, [(0, (140, 230, 228)), (50, (78, 192, 208)), (170, (30, 118, 160)), (359, (10, 42, 78))], 10)
        px = img.load()
        for x in range(W):
            s = 7 + 2.2 * math.sin(TAU * x / 80) + 1.2 * math.sin(TAU * x / 23 + 1)
            for y in range(0, int(s) + 1):
                px[x, y] = (196, 250, 246, 255) if y < s - 1 else (244, 255, 255, 255)
        # caustic net under the surface
        for y in range(10, 90):
            fade = (1 - (y - 10) / 80) ** 2
            for x in range(W):
                v = math.sin(x / 9.0 + 1.6 * math.sin(y / 7.0)) * math.sin(y / 6.0 + 1.6 * math.sin(x / 11.0))
                if abs(v) < 0.045 and bayer(x, y) < fade:
                    px[x, y] = A(mix(px[x, y], (220, 255, 250), 0.45), 255)
        # light shafts
        for (x0, slope, w0, s) in [(60, 0.55, 16, 0.5), (170, 0.5, 26, 0.6), (300, 0.45, 14, 0.4), (390, 0.5, 34, 0.55), (540, 0.42, 20, 0.45)]:
            for y in range(H):
                cx = x0 + y * slope
                w = w0 + y * 0.12
                amt = s * max(0.0, 1 - y / 330)
                for x in range(int(cx - w), int(cx + w) + 1):
                    if 0 <= x < W:
                        edge = abs(x + 0.5 - cx) / w
                        a = amt * (1 - edge * edge)
                        if bayer(x, y) < a:
                            px[x, y] = A(mix(px[x, y], (200, 250, 240), 0.35), 255)
        for i in range(90):
            x = int(hsh(i, 1, 351) * W)
            y = int(20 + hsh(i, 2, 351) * (H - 30))
            px[x, y] = A(mix(px[x, y], (230, 255, 255), 0.6), 255)
        return ("Grotto Water", img)

    def backgrounds(self):
        return [("Grotto Reef Far", self.bg_reef(), 0.2), ("Grotto Kelp", self.bg_kelp(), 0.55)]

    def bg_reef(self):
        W, H = BW, BH
        img = new(W, H)
        far = Mask(W, H)
        for x in range(W):
            far.vspan(x, 236 + pn(x, W, [(14, 2, 0.3), (8, 5, 1.7), (4, 13, 0.2)]), H)
        # arches
        for (cx, R, t, base) in [(180, 70, 16, 250), (690, 54, 13, 246)]:
            for y in range(int(base - R) - 1, int(base) + 1):
                for xx in range(cx - R - 1, cx + R + 2):
                    d = math.hypot(xx + 0.5 - cx, (y + 0.5 - base) * 1.1)
                    if R - t <= d <= R:
                        far.set(xx, y)
        # sunken ruin columns
        for (cx, top, w) in [(380, 120, 12), (420, 150, 12), (460, 104, 12), (880, 140, 14), (930, 176, 12)]:
            for y in range(top, H):
                jag = 2 if (y - top) < 4 and hsh(cx, y, 361) < 0.5 else 0
                far.hspan(y, cx - w + jag, cx + w)
            far.hspan(top + 6, cx - w - 4, cx + w + 4)
            far.hspan(top + 7, cx - w - 4, cx + w + 4)
        for (cx, cy, r) in [(560, 230, 30), (600, 220, 22), (90, 228, 26), (800, 232, 30)]:
            far.disc(cx, cy, r, r * 0.8)

        def far_tex(x, y):
            if x % 6 == 0 and 130 < y and hsh(x, y // 8, 362) < 0.3:
                return (66, 140, 164, 255)
            return None
        mask_paint(img, far.m, (74, 152, 172, 255), rim=(120, 196, 206, 255), tex=far_tex)
        near = Mask(W, H)
        for x in range(W):
            near.vspan(x, 290 + pn(x, W, [(12, 3, 1.1), (6, 7, 0.4)]), H)
        for i in range(16):
            cx = int((i + hsh(i, 1, 363)) * W / 16)
            r = 14 + hsh(i, 2, 363) * 22
            near.disc(cx, 292, r, r * 0.9)
        # sea fans
        fans = []
        for i in range(8):
            cx = int((i + 0.3 + hsh(i, 3, 363) * 0.4) * W / 8)
            R = 26 + hsh(i, 4, 363) * 20
            fans.append((cx, 286, R))
            for y in range(int(286 - R), 287):
                for xx in range(int(cx - R), int(cx + R) + 1):
                    d = math.hypot(xx + 0.5 - cx, y + 0.5 - 286)
                    ang = math.atan2(xx + 0.5 - cx, 286 - y)
                    if d <= R and ((ang * 6) % 1.0 < 0.22 or (d % 7) < 1.2) and abs(ang) < 1.25:
                        near.set(xx, y)
        mask_paint(img, near.m, (44, 108, 136, 255), rim=(86, 160, 176, 255))
        px = img.load()
        # fish schools
        for g in range(5):
            gx, gy = hsh(g, 1, 364) * W, 70 + hsh(g, 2, 364) * 120
            for k in range(9):
                fx = int(gx + (k % 3) * 9 + hsh(g, k, 365) * 6)
                fy = int(gy + (k // 3) * 6 + hsh(g, k, 366) * 3)
                for (dx, dy) in [(0, 0), (1, 0), (2, 0), (1, -1), (1, 1), (3, -1), (3, 1)]:
                    if 0 <= fy + dy < H:
                        px[(fx + dx) % W, fy + dy] = (60, 140, 170, 255)
        # coral colour dots on near reef
        for i in range(160):
            x = int(hsh(i, 1, 367) * W)
            y = 280 + int(hsh(i, 2, 367) * 70)
            if px[x, y][3]:
                px[x, y] = [(220, 130, 150, 255), (240, 180, 110, 255), (170, 130, 210, 255)][i % 3]
        return img

    def bg_kelp(self):
        W, H = BW, BH
        img = new(W, H)
        px = img.load()
        hi, mid, dk = (86, 160, 116, 255), (46, 116, 96, 255), (30, 82, 80, 255)
        for i in range(11):
            x0 = (i + 0.2 + hsh(i, 1, 371) * 0.6) * W / 11
            top = 30 + hsh(i, 2, 371) * 130
            amp = 5 + hsh(i, 3, 371) * 6
            ph = hsh(i, 4, 371) * 6
            # a broad wavy ribbon blade: width swells and narrows, light on the left edge
            for y in range(int(top), H):
                t = (y - top) / (H - top)
                xc = x0 + amp * math.sin(y / 34.0 + ph) + 4 * math.sin(y / 9.0 + ph * 2) * (1 - t)
                w = 3 + 3.5 * abs(math.sin(y / 23.0 + ph)) * (1 - 0.5 * t)
                if y - top < 10:
                    w *= (y - top + 1) / 11
                for x in range(int(round(xc - w)), int(round(xc + w)) + 1):
                    u = (x + 0.5 - (xc - w)) / max(1.0, 2 * w)
                    c = hi if u < 0.28 else (dk if u > 0.75 else mid)
                    if c == mid and abs(u - 0.5) < 0.06:
                        c = dk
                    px[x % W, y] = c
            # a second, thinner side blade
            for y in range(int(top + 40), H):
                xc = x0 + 9 + (amp + 3) * math.sin(y / 28.0 + ph + 1.5)
                w = 1.5 + 1.5 * abs(math.sin(y / 17.0 + ph))
                for x in range(int(round(xc - w)), int(round(xc + w)) + 1):
                    if px[x % W, y][3] == 0:
                        px[x % W, y] = mid if x < xc + w * 0.4 else dk
            bx = int(round(x0 + amp * math.sin(top / 34.0 + ph) + 4 * math.sin(top / 9.0 + ph * 2)))
            disc(px, W, H, bx + 0.5, top - 1, 3, lambda dx, dy, d: (170, 200, 110, 255) if dx + dy < -1 else (110, 150, 80, 255))
        # seabed rocks
        bed = Mask(W, H)
        for x in range(W):
            bed.vspan(x, 336 + pn(x, W, [(6, 5, 0.2), (3, 17, 1.4)]), H)
        for i in range(12):
            cx = hsh(i, 5, 374) * W
            bed.disc(cx, 340, 14 + hsh(i, 6, 374) * 16, 12)
        mask_paint(img, bed.m, (26, 74, 92, 255), rim=(60, 120, 130, 255))
        outline_wrap(img, (18, 54, 68, 255))
        for i in range(40):
            x = int(hsh(i, 7, 375) * W)
            y = int(30 + hsh(i, 8, 375) * 300)
            r = 1.5 + hsh(i, 9, 375) * 2.5
            for yy in range(int(y - r) - 1, int(y + r) + 2):
                for xx in range(int(x - r) - 1, int(x + r) + 2):
                    d = math.hypot(xx + 0.5 - x, yy + 0.5 - y)
                    if 0 <= yy < H and r - 1 <= d <= r and px[xx % W, yy][3] == 0:
                        px[xx % W, yy] = (210, 250, 255, 200)
            if 0 <= int(y - r * 0.4) < H:
                px[int(x - r * 0.4) % W, int(y - r * 0.4)] = (255, 255, 255, 230)
        return img


class Canopy(World):
    key = "canopy"
    tiles_name = "Canopy Tiles"
    OUT = (56, 30, 24, 255)
    seed = 400
    WOOD = ((240, 178, 104, 255), (210, 142, 80, 255), (172, 106, 62, 255), (128, 74, 46, 255), (88, 50, 36, 255))
    DWOOD = ((140, 90, 60, 255), (116, 72, 50, 255), (92, 56, 42, 255), (70, 42, 34, 255), (50, 30, 28, 255))
    lip = ((232, 230, 120, 255), (128, 182, 70, 255), (78, 134, 56, 255), (48, 92, 46, 255))
    lip_shadow = (128, 74, 46, 255)
    edge_l = (128, 74, 46, 255)
    edge_r = (88, 50, 36, 255)
    edge_dk = (128, 74, 46, 255)
    lip_base = 7
    lip_amp = 1.3
    drape = 13
    water = ((255, 236, 180, 230), (150, 206, 200, 190), (76, 150, 160, 170), (60, 124, 146, 180))
    checker = ((150, 120, 130, 255), (170, 138, 146, 255))
    bg_color = (250, 196, 140)
    LEAF = ((214, 230, 110, 255), (120, 180, 70, 255), (72, 130, 58, 255), (44, 86, 46, 255))

    def wood(self, x, y, pal, seed):
        lt, hi, mid, dk, deep = pal
        warp = 1.0 * math.sin(TAU * x / 32) + 0.6 * math.sin(TAU * 2 * x / 32 + 1.3)
        v = (y + warp) / 8.0
        f = v - math.floor(v)
        n = hsh(x, y, seed)
        if f < 0.13:
            return dk
        if f < 0.2 and n < 0.5:
            return deep
        if 0.48 < f < 0.58 and n < 0.6:
            return hi
        if n < 0.04:
            return dk
        if n > 0.97:
            return lt
        return mid

    def tex(self, x, y):
        return self.wood(x, y, self.WOOD, 401)

    def lip_extra(self, px):
        hi, mid, dk, deep = self.LEAF
        for (x0, y0) in [(5, 0), (17, 0), (27, 0)]:
            d = self.lip_depth(x0)
            for (dx, dy, c) in [(0, 1, mid), (1, 1, mid), (0, 2, dk), (1, 2, mid), (2, 2, dk), (1, 3, deep)]:
                if d + dy < TS:
                    px[x0 + dx, d + dy] = c
        for (x, y, c) in [(10, 3, (255, 170, 120, 255)), (22, 4, (255, 236, 150, 255)), (23, 3, (255, 236, 150, 255))]:
            px[x, y] = c

    def t2(self, img):  # cracked bark block
        px = img.load()
        base, hi, dk, crack = (156, 100, 60, 255), (204, 146, 90, 255), (104, 64, 42, 255), (60, 34, 28, 255)
        wa.bevel_block(px, base, hi, dk, self.OUT, 2)
        for y in range(3, 29):
            for x in range(3, 29):
                f = (x + int(round(1.5 * math.sin(y / 5.0 + x))))
                if f % 6 == 0:
                    px[x, y] = dk
                elif f % 6 == 1 and hsh(x, y, 411) < 0.5:
                    px[x, y] = hi
        pts = [(9, 2), (12, 7), (10, 12), (15, 16), (14, 21), (19, 25), (18, 29)]
        line(img, [(p[0] + 1, p[1]) for p in pts], hi)
        line(img, pts, crack)
        line(img, [(15, 16), (22, 13), (26, 14)], crack)
        line(img, [(14, 21), (8, 24)], crack)

    def acorn(self, px, x, y):
        cap, cap_dk, nut, nut_dk = (130, 84, 50, 255), (90, 56, 36, 255), (226, 170, 90, 255), (180, 120, 60, 255)
        for (dx, dy, c) in [(0, 0, cap), (1, 0, cap), (2, 0, cap_dk), (-1, 1, cap), (0, 1, cap), (1, 1, cap), (2, 1, cap), (3, 1, cap_dk),
                            (0, 2, nut), (1, 2, nut), (2, 2, nut_dk), (0, 3, nut), (1, 3, nut_dk), (1, 4, nut_dk), (1, -1, cap_dk)]:
            px[x + dx, y + dy] = c

    ACORN_CAP = ((176, 120, 72, 255), (136, 88, 52, 255), (98, 62, 40, 255))

    def acorn_cap(self, px):
        hi, mid, dk = self.ACORN_CAP

        def shade(nx, ny, d):
            if ny > 0.55:
                return None
            if d > 0.88 or ny > 0.4:
                return self.OUT if (d > 0.88 or ny > 0.47) else dk
            xs = (nx * 7 + ny * 5) % 2 < 1
            ys = (nx * 7 - ny * 5) % 2 < 1
            c = hi if (xs and ys) else (mid if xs != ys else dk)
            if nx > 0.45 and c == hi:
                c = mid
            return c
        wa.blob(px, 16, 13, 10.4, 6.6, shade)
        for (x, y, c) in [(16, 6, self.OUT), (16, 5, (98, 62, 40, 255)), (17, 4, (98, 62, 40, 255)), (17, 6, self.OUT)]:
            px[x, y] = c

    def t3(self, img):  # acorn-cap pod: a glowing honey-gold nut under a crosshatched cap
        px = img.load()
        pod_window(px, (self.WOOD[2], self.WOOD[1], self.WOOD[3]), self.OUT, (56, 32, 28, 255), (112, 80, 42, 255))
        n_hi, n_lt, n_mid, n_dk = (255, 246, 196, 255), (255, 214, 110, 255), (236, 164, 62, 255), (178, 108, 44, 255)

        def nut(nx, ny, d):
            if d > 0.9:
                return self.OUT
            if (nx + 0.35) ** 2 + (ny + 0.1) ** 2 < 0.05:
                return n_hi
            v = d + 0.35 * (nx + ny)
            return n_lt if v < 0.55 else (n_mid if v < 0.95 else n_dk)
        wa.blob(px, 16, 17.5, 8.4, 8.6, nut)
        px[16, 26] = n_dk
        px[16, 27] = self.OUT
        self.acorn_cap(px)
        for (x, y) in [(8, 23), (24, 22), (9, 9)]:
            px[x, y] = (255, 236, 150, 255)

    def t4(self, img):  # emptied acorn: the cap hangs over a dark hollow, shell bits below
        px = img.load()
        pod_window(px, ((150, 100, 64, 255), (186, 140, 96, 255), (106, 70, 46, 255)), self.OUT, (56, 32, 28, 255))
        self.acorn_cap(px)
        for (x, y, c) in [(10, 23, (170, 120, 70, 255)), (11, 23, (130, 86, 50, 255)), (11, 22, (190, 140, 84, 255)),
                          (19, 24, (170, 120, 70, 255)), (20, 24, (130, 86, 50, 255)), (21, 23, (190, 140, 84, 255)),
                          (15, 24, (130, 86, 50, 255))]:
            px[x, y] = c

    def t7(self, img):  # heartwood end grain
        px = img.load()
        base, hi, dk = (220, 166, 104, 255), (246, 206, 146, 255), (150, 98, 60, 255)
        wa.bevel_block(px, base, hi, dk, self.OUT, 3)
        for y in range(4, 28):
            for x in range(4, 28):
                ang = math.atan2(y - 15.5, x - 15.5)
                d = math.hypot(x - 15.5, y - 15.5) * (1 + 0.08 * math.sin(ang * 5))
                if d < 11.5 and int(d) % 3 == 0:
                    px[x, y] = dk if d > 1 else hi
        line(img, [(16, 16), (22, 21), (25, 26)], (100, 60, 40, 255))

    def crown(self, u):  # leafy tufts
        return 2 + int(round(2.2 * math.sin(u * 0.55 + 0.8) + 1.0 * math.sin(u * 1.4))) + (1 if hsh(u, 0, 432) < 0.25 else 0)

    def col_px(self, u, y, top):
        ramp = ((240, 182, 112, 255), (208, 142, 84, 255), (162, 102, 62, 255), (120, 72, 46, 255), (82, 48, 34, 255))
        hi, mid, dk, deep = self.LEAF

        def tex(u, y, top, t, idx):
            if top:
                lb = self.crown(u) + 4 + int(round(2 * abs(math.sin(u * math.pi / 6 + 1))))
                if y <= lb:
                    if y == lb:
                        return deep
                    if y <= self.crown(u) + 1:
                        return hi
                    n = hsh(u, y, 431)
                    return (dk if n < 0.5 else mid) if t > 0.7 else (hi if n > 0.85 else (dk if n < 0.12 else mid))
            # twisting bark furrows (period 32 vertically)
            f = u + int(round(3.0 * math.sin(TAU * y / 32 + u * 0.12)))
            if f % 7 == 0:
                return ramp[min(4, idx + 1)]
            if f % 7 == 2 and t < 0.5:
                return ramp[max(0, idx - 1)]
            # a moss cushion and a burl
            md = math.hypot((u - 11) / 5.0, (y - 23) / 3.2)
            if md < 1.0:
                return hi if (md < 0.5 and y < 23) else (mid if y < 25 else dk)
            if math.hypot(u - 40, (y - 9) * 1.3) < 3:
                return ramp[4] if math.hypot(u - 40, (y - 9) * 1.3) < 1.6 else ramp[3]
            return None
        return self.organic_col(u, y, top, ramp, tex)

    def plat(self, part):  # vine rope bridge
        img = new(TS, TS)
        px = img.load()
        hi, mid, dk = (230, 176, 110, 255), (190, 132, 78, 255), (130, 84, 52, 255)
        rope, rope_dk = (220, 196, 140, 255), (160, 132, 90, 255)
        x0 = 4 if part == 0 else 0
        x1 = 27 if part == 2 else 31
        for x in range(x0, x1 + 1):
            u = (x + part * 32) % 8
            for y in range(3, 10):
                if u == 7:
                    continue
                c = hi if y == 3 else (dk if y == 9 or u == 6 else mid)
                px[x, y] = c
        for x in range(32):
            if (part == 0 and x < 2) or (part == 2 and x > 29):
                continue
            y = 11 + int(round(1.0 * math.sin(math.pi * (x % 16) / 16)))
            px[x, y] = rope
            px[x, y + 1] = rope_dk
        for x in range(x0, x1 + 1):
            if (x + part * 32) % 8 == 3:
                px[x, 10] = rope
                px[x, 9] = rope_dk
        if part in (0, 2):
            m = part == 2

            def X(x):
                return 31 - x if m else x
            for y in range(0, 18):
                for x in range(0, 4):
                    px[X(x), y] = hi if x == 0 else (dk if x == 3 else mid)
            for y in (5, 6, 9, 10):
                for x in range(0, 4):
                    px[X(x), y] = rope if y in (5, 9) else rope_dk
            for (x, y) in [(1, 0), (2, 0)]:
                px[X(x), y] = (110, 160, 60, 255)
        outline(img, self.OUT)
        return img

    def t15(self, img):
        px = img.load()
        self.fill(px)
        cx, cy = 15.5, 15.5
        for y in range(8, 24):
            for x in range(9, 23):
                d = math.hypot((x + 0.5 - cx) / 6, (y + 0.5 - cy) / 7.5)
                if d <= 1:
                    px[x, y] = (40, 20, 18, 255) if d < 0.62 else (d > 0.85 and (98, 58, 40, 255) or (226, 164, 100, 255))
        for (x, y) in [(13, 12), (14, 11)]:
            px[x, y] = (70, 40, 30, 255)

    def t16(self, img):
        tuft(img, (self.LEAF[0], self.LEAF[1], self.LEAF[2]), out=self.OUT)

    def t17(self, img):
        tuft(img, (self.LEAF[0], self.LEAF[1], self.LEAF[2]))
        px = img.load()
        for (cx, cy, petal, center) in [(8, 20, (255, 150, 110, 255), (255, 230, 120, 255)), (16, 16, (255, 120, 150, 255), (255, 236, 160, 255)),
                                        (24, 21, (255, 214, 100, 255), (220, 110, 40, 255))]:
            for y in range(cy + 2, 30):
                px[cx, y] = self.LEAF[2]
            for dx, dy in [(-1, 0), (1, 0), (0, -1), (0, 1)]:
                px[cx + dx, cy + dy] = petal
            px[cx, cy] = center
        outline(img, self.OUT)

    def t18(self, img):  # nest with eggs
        px = img.load()
        egg, egg_dk = (170, 220, 236, 255), (110, 170, 200, 255)
        for (cx, cy) in [(12, 21), (18, 20), (15, 19)]:
            disc(px, 32, 32, cx + 0.5, cy + 0.5, 3.2, lambda dx, dy, d: (230, 250, 255, 255) if (dx < -1 and dy < -1) else (egg if dx + dy < 2 else egg_dk))
        twig, twig_hi, twig_dk = (170, 120, 70, 255), (214, 166, 104, 255), (110, 74, 46, 255)
        for y in range(22, 30):
            for x in range(4, 28):
                dx, dy = (x + 0.5 - 16) / 12.5, (y + 0.5 - 22) / 7.5
                if dx * dx + dy * dy <= 1:
                    v = (x * 2 + y * 3) % 7
                    px[x, y] = twig_hi if v == 0 else (twig_dk if v in (3, 5) else twig)
        for (x, y) in [(3, 22), (2, 21), (28, 22), (29, 21), (27, 24)]:
            px[x, y] = twig
        outline(img, self.OUT)

    def t19(self, img):  # leafy bush with golden rim
        px = img.load()
        hi, mid, dk, deep = self.LEAF

        def sh(dx, dy, d):
            k = (dx * 0.7 - dy * 0.7) / max(d, 0.01)
            if d > 3 and k > 0.5:
                return hi
            if dy > 3:
                return dk
            return mid
        for (cx, cy, r) in [(10, 22, 8), (22, 22, 8), (16, 15, 9)]:
            disc(px, 32, 32, cx, cy, r, sh)
        for y in range(26, 32):
            for x in range(1, 31):
                if px[x, y][3]:
                    px[x, y] = dk if y < 29 else deep
        for (x, y) in [(13, 12), (19, 16), (8, 20), (24, 21)]:
            px[x, y] = dk
        for (x, y) in [(10, 18), (21, 19), (16, 22)]:
            px[x, y] = (255, 160, 120, 255)
        outline(img, self.OUT)

    def t20(self, img):  # rope railing (tiles horizontally)
        px = img.load()
        hi, mid, dk = (214, 158, 96, 255), (172, 116, 70, 255), (120, 78, 48, 255)
        rope, rope_dk = (226, 200, 146, 255), (164, 136, 92, 255)
        for xp in (6, 22):
            for y in range(10, 32):
                for x in range(xp - 1, xp + 2):
                    px[x, y] = hi if x == xp - 1 else (dk if x == xp + 1 else mid)
            px[xp, 9] = mid
        for x in range(32):
            s = math.sin(math.pi * ((x - 6) % 16) / 16)
            for (y0, a) in [(12, 3), (21, 2)]:
                y = y0 + int(round(a * s))
                if px[x, y][3] == 0 or px[x, y] == rope_dk:
                    px[x, y] = rope
                    px[x, y + 1] = rope_dk
        outline(img, self.OUT)
        for y in range(32):
            for x in (0, 31):
                if px[x, y] == self.OUT:
                    inner = px[1 if x == 0 else 30, y]
                    if inner in (rope, rope_dk):
                        px[x, y] = inner

    def t21(self, img):  # birdhouse
        px = img.load()
        post, post_dk = (150, 100, 60, 255), (104, 66, 42, 255)
        rect(px, 32, 32, 14, 20, 17, 31, post)
        rect(px, 32, 32, 16, 20, 17, 31, post_dk)
        body, body_hi, body_dk = (110, 170, 190, 255), (160, 210, 220, 255), (70, 120, 150, 255)
        rect(px, 32, 32, 9, 10, 22, 21, body)
        rect(px, 32, 32, 9, 10, 10, 21, body_hi)
        rect(px, 32, 32, 21, 10, 22, 21, body_dk)
        disc(px, 32, 32, 16, 14.5, 2.2, lambda dx, dy, d: (40, 30, 30, 255))
        rect(px, 32, 32, 15, 18, 17, 18, (200, 150, 90, 255))
        roof, roof_hi, roof_dk = (216, 84, 64, 255), (246, 136, 104, 255), (156, 50, 46, 255)
        for i in range(8):
            for x in range(16 - i - 1, 16 + i + 1):
                px[x, 3 + i] = roof_hi if i == 0 or x < 16 - i + 1 else (roof_dk if i == 7 else roof)
        outline(img, self.OUT)

    def t22(self, img):  # hanging vine with leaves (stacks vertically)
        px = img.load()
        hi, mid, dk, deep = self.LEAF
        for y in range(32):
            x = 16 + int(round(2.2 * math.sin(TAU * y / 32)))
            px[x, y] = dk
            px[x + 1, y] = mid
        for (y, side) in [(4, 1), (12, -1), (20, 1), (28, -1)]:
            x = 16 + int(round(2.2 * math.sin(TAU * y / 32)))
            for i in range(1, 5):
                xx = x + side * i + (1 if side > 0 else 0)
                px[xx, y + (1 if i > 2 else 0)] = hi if i < 3 else mid
                px[xx, y + 1 + (1 if i > 2 else 0)] = mid
        px[15, 24] = (255, 170, 120, 255)
        px[19, 8] = (255, 236, 150, 255)
        outline(img, self.OUT)

    def t23(self, img):  # bunting garland (tiles horizontally)
        px = img.load()
        string = (110, 70, 50, 255)
        cols = [((236, 84, 70, 255), (180, 50, 50, 255)), ((255, 210, 90, 255), (220, 150, 50, 255)),
                ((90, 190, 180, 255), (50, 130, 140, 255)), ((250, 140, 170, 255), (200, 90, 130, 255))]
        sag = [3 + int(round(3 * math.sin(math.pi * x / 32))) for x in range(32)]
        for k, x0 in enumerate((2, 10, 18, 26)):
            c, cd = cols[k]
            y0 = sag[x0 + 2]
            for j in range(6):
                for x in range(x0 + j // 2, x0 + 6 - j // 2):
                    px[x, y0 + 1 + j] = c if x < x0 + 3 else cd
        outline(img, self.OUT)
        for x in range(32):
            px[x, sag[x]] = string

    def ledge(self, part):  # giant leaf
        img = new(TS, TS)
        px = img.load()
        top_hi, top_c, body, rib, vein, under = (232, 236, 130, 255), (170, 214, 92, 255), (112, 176, 66, 255), (196, 226, 120, 255), (78, 136, 54, 255), (58, 104, 52, 255)
        for x in range(TS):
            gx = x + part * 32
            if part == 0:
                t = x / 10.0
                top = 2 + int(round(max(0, 1 - t) * 3 - max(0, 1 - x / 3.0) * 2))
                thick = max(2, int(round(min(1.0, (x + 1) / 12.0) * 11)))
            elif part == 2:
                xr = 31 - x
                top = 2 + int(round(max(0, 1 - xr / 10.0) * 3 - max(0, 1 - xr / 3.0) * 2))
                thick = max(2, int(round(min(1.0, (xr + 1) / 12.0) * 11)))
            else:
                top, thick = 2, 11
            bot = top + thick
            mid_y = top + thick // 2
            for y in range(top, bot + 1):
                if y <= top + 1:
                    c = top_hi if y == top else top_c
                elif y >= bot - 1:
                    c = under
                elif y == mid_y:
                    c = rib
                else:
                    c = body
                    if (gx - (y - mid_y) * 2) % 9 == 0 and y < mid_y:
                        c = vein
                    if (gx + (y - mid_y) * 2) % 9 == 0 and y > mid_y:
                        c = vein
                px[x, y] = c
        outline(img, self.OUT)
        if part == 1:
            for y in range(TS):
                for x in (0, 31):
                    if px[x, y] == self.OUT and 2 < y < 14:
                        px[x, y] = px[1 if x == 0 else 30, y]
        else:
            ex = 31 if part == 0 else 0
            for y in range(TS):
                if px[ex, y] == self.OUT and 2 < y < 14:
                    px[ex, y] = px[30 if part == 0 else 1, y]
        return img

    def t29(self, img):  # leaf stalk
        px = img.load()
        for y in range(32):
            for x in range(13, 19):
                if x in (13, 18):
                    c = self.OUT
                elif x == 14:
                    c = self.LEAF[0]
                elif x == 17:
                    c = self.LEAF[2]
                else:
                    c = self.LEAF[1]
                px[x, y] = c
            if y % 9 == 4:
                px[15, y] = self.LEAF[2]
                px[16, y] = self.LEAF[2]

    def t32(self, img):  # thorny bramble
        px = img.load()
        vine, vine_dk, thorn = (150, 70, 54, 255), (96, 40, 40, 255), (250, 226, 170, 255)
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
                    put(px, 32, 32, x + 1, y - 1, self.LEAF[2])
        for y in range(29, 32):
            for x in range(32):
                if not px[x, y][3]:
                    px[x, y] = vine_dk if hsh(x, y, 495) < 0.6 else vine
        outline(img, self.OUT)

    def t33(self, img):  # amber block with a trapped bug
        px = img.load()
        facet_block(px, (250, 170, 60, 255), (255, 232, 150, 255), (214, 120, 40, 255), (150, 70, 30, 255), self.OUT)
        for (x, y) in [(17, 17), (18, 17), (17, 18), (18, 18), (16, 16), (19, 16), (16, 19), (19, 19), (17, 15)]:
            px[x, y] = (110, 50, 30, 255)

    def t34(self, img):
        px = img.load()
        for y in range(TS):
            for x in range(TS):
                px[x, y] = self.wood(x, y, self.DWOOD, 401)

    def t35(self, img):  # paper lantern
        px = img.load()
        for y in range(0, 8):
            px[16, y] = (90, 60, 50, 255)
        gold, gold_dk = (240, 190, 80, 255), (180, 120, 40, 255)
        rect(px, 32, 32, 13, 8, 19, 9, gold)
        rect(px, 32, 32, 13, 9, 19, 9, gold_dk)
        red, red_hi, red_dk, glowc = (236, 90, 60, 255), (255, 170, 110, 255), (176, 50, 44, 255), (255, 220, 140, 255)
        for y in range(10, 23):
            for x in range(8, 25):
                dx, dy = (x + 0.5 - 16.5) / 8.5, (y + 0.5 - 16.5) / 7
                if dx * dx + dy * dy <= 1:
                    c = red
                    if dx * dx + dy * dy < 0.25:
                        c = glowc
                    elif dx < -0.3 and dy < 0:
                        c = red_hi
                    elif dx > 0.5:
                        c = red_dk
                    if (x - 8) % 4 == 0 and c not in (glowc,):
                        c = red_dk
                    px[x, y] = c
        rect(px, 32, 32, 13, 23, 19, 24, gold)
        for y in range(25, 30):
            px[16, y] = gold if y < 28 else (236, 90, 60, 255)
        outline(img, self.OUT)
        px[16, 0] = (90, 60, 50, 255)
        for x in (15, 17):
            if px[x, 0] == self.OUT:
                px[x, 0] = CLEAR
        halo(img, 16.5, 16.5, 14, (255, 180, 100), 60, 24)

    def t36(self, img):  # foxglove
        px = img.load()
        for y in range(6, 32):
            px[16, y] = self.LEAF[2]
            px[15, y] = self.LEAF[1]
        for (y, side) in [(24, -1), (28, 1)]:
            for i in range(1, 6):
                x = 16 + side * i if side > 0 else 15 + side * i
                px[x, y - (i // 2)] = self.LEAF[1]
                px[x, y - (i // 2) + 1] = self.LEAF[2]
        bell, bell_hi, bell_dk = (240, 130, 190, 255), (255, 196, 226, 255), (190, 80, 140, 255)
        for k, y in enumerate((7, 11, 15, 19)):
            side = 1 if k % 2 else -1
            x0 = 17 if side > 0 else 11
            for dy in range(4):
                for dx in range(3 + (dy > 1)):
                    px[x0 + dx, y + dy] = bell_hi if dx == 0 else (bell_dk if dy == 3 else bell)
        px[15, 5] = self.LEAF[0]
        px[16, 4] = self.LEAF[0]
        outline(img, self.OUT)

    def t37(self, img):  # acorns
        px = img.load()
        for (x, y) in [(6, 26), (13, 27), (21, 25)]:
            self.acorn(px, x, y)
        for i in range(6):
            px[24 + i, 30 - i // 2] = self.LEAF[1]
            px[24 + i, 31 - i // 2] = self.LEAF[2]
        outline(img, self.OUT)

    # -- backgrounds ---------------------------------------------------------
    def sky(self):
        W, H = SW, SH
        img = Image.new("RGBA", (W, H))
        vgrad(img, [(0, (96, 108, 182)), (80, (168, 136, 188)), (170, (244, 168, 142)), (260, (255, 206, 140)), (359, (255, 232, 172))], 11)
        px = img.load()
        sx, sy = 470, 236
        for y in range(H):
            for x in range(W):
                d = math.hypot(x + 0.5 - sx, y + 0.5 - sy)
                if d < 26:
                    px[x, y] = (255, 252, 230, 255) if d < 23 else (255, 236, 170, 255)
                elif d < 60:
                    if bayer(x, y) < (60 - d) / 40:
                        px[x, y] = A(mix(px[x, y], (255, 240, 190), 0.5), 255)
                elif d < 150:
                    if bayer(x, y) < (150 - d) / 220:
                        px[x, y] = A(mix(px[x, y], (255, 230, 170), 0.35), 255)
        for (cx, cy, rw, rh) in [(120, 90, 110, 10), (210, 104, 60, 6), (360, 150, 90, 8), (560, 120, 70, 7), (620, 180, 80, 6), (60, 200, 70, 6), (300, 214, 60, 5)]:
            for y in range(cy - rh, cy + rh + 1):
                for x in range(cx - rw, cx + rw + 1):
                    if not (0 <= x < W):
                        continue
                    dx, dy = (x - cx) / rw, (y - cy) / rh
                    v = dx * dx + dy * dy
                    if v <= 1:
                        under = dy > 0.15
                        c = (255, 214, 150) if under else (246, 170, 176)
                        if bayer(x, y) < (0.85 if v < 0.5 else 0.4):
                            px[x, y] = A(mix(px[x, y], c, 0.7), 255)
        for (bx, by) in [(250, 70), (262, 76), (240, 80), (520, 60)]:
            for (dx, dy) in [(-3, -1), (-2, 0), (-1, 0), (0, 1), (1, 0), (2, 0), (3, -1)]:
                px[bx + dx, by + dy] = (96, 70, 104, 255)
        return ("Canopy Sky", img)

    def backgrounds(self):
        return [("Canopy Far Trees", self.bg_far(), 0.2), ("Canopy Branches", self.bg_branches(), 0.5)]

    def bg_far(self):
        W, H = BW, BH
        img = new(W, H)
        for (layer, trees, col, rim, floor, crown_y, scale) in [
                (0, [(60, 7), (300, 9), (520, 6), (760, 8), (930, 6)], (232, 170, 160, 255), (255, 222, 176, 255), 236, 118, 0.75),
                (1, [(180, 12), (430, 14), (650, 11), (860, 13)], (196, 128, 136, 255), (250, 192, 140, 255), 268, 150, 1.0)]:
            m = Mask(W, H)
            for (cx, w) in trees:
                for y in range(int(crown_y), H):
                    hw = w * (0.9 + 0.1 * math.sin(y / 30.0 + cx))
                    m.hspan(y, cx - hw, cx + hw)
                # umbrella crown: a flat-bottomed cluster of puffs
                for k in range(9):
                    a = (k - 4) / 4.0
                    r = (16 + hsh(cx, k, 503 + layer) * 10) * scale
                    m.disc(cx + a * 62 * scale, crown_y - (1 - a * a) * 22 * scale + hsh(cx, k, 502) * 6, r, r * 0.8)
                m.curve([(cx, crown_y + 30), (cx + 24 * scale, crown_y + 14), (cx + 46 * scale, crown_y + 8)], 3 * scale, 1.5)
                m.curve([(cx, crown_y + 40), (cx - 22 * scale, crown_y + 26), (cx - 44 * scale, crown_y + 20)], 3 * scale, 1.5)
            for x in range(W):
                m.vspan(x, floor + pn(x, W, [(8, 4, layer), (5, 11, 1.3)]), H)
            for i in range(26):
                cx = (i + hsh(i, 1, 504 + layer)) * W / 26
                r = (14 + hsh(i, 2, 504 + layer) * 14) * (0.8 + 0.4 * layer)
                m.disc(cx, floor + 6, r, r * 0.75)

            def tex(x, y, col=col, floor=floor):
                if y > floor + 20 and bayer(x, y) < min(0.6, (y - floor - 20) / 120):
                    return A(mix(col, (120, 70, 110), 0.35), 255)
                return None
            mask_paint(img, m.m, col, rim=rim, tex=tex, rim_right=True)
        # light shafts falling between trees
        px = img.load()
        for (x0, w) in [(140, 18), (640, 24), (860, 14)]:
            for y in range(0, H):
                cx = x0 - y * 0.35
                for x in range(int(cx - w), int(cx + w)):
                    p = px[x % W, y]
                    if p[3] and bayer(x, y) < 0.25 * (1 - y / H):
                        px[x % W, y] = A(mix(p, (255, 230, 170), 0.4), 255)
        return img

    def bg_branches(self):
        W, H = BW, BH
        img = new(W, H)
        bark = Mask(W, H)
        bark.curve([(-80, 40), (150, 10), (330, 70), (520, 20)], 13, 6)
        bark.curve([(560, 330), (700, 300), (820, 340), (1100, 300)], 16, 10)
        bark.curve([(300, 360), (360, 300), (430, 270), (520, 280)], 12, 5)
        bark.curve([(700, -10), (760, 40), (880, 50), (980, 20)], 10, 5)
        leaves = Mask(W, H)
        for i in range(26):
            cx = (i + hsh(i, 1, 511)) * W / 26
            n = 3 + int(hsh(i, 2, 511) * 4)
            for k in range(n):
                leaves.disc(cx + (hsh(i, k, 512) - 0.5) * 50, hsh(i, k, 513) * 46 + k * 6, 10 + hsh(i, k, 514) * 12)
        for i in range(10):
            cx = (i + hsh(i, 3, 515)) * W / 10
            for k in range(4):
                leaves.disc(cx + (hsh(i, k, 516) - 0.5) * 60, 326 + hsh(i, k, 517) * 20, 12 + hsh(i, k, 518) * 10)

        def ltex(x, y):
            # scattered leaf glints and shadows on a 3px grain
            n = hsh(x // 3, y // 3, 519)
            if n > 0.62 and x % 3 == 1 and y % 3 == 0:
                return (122, 160, 76, 255)
            if n < 0.3 and x % 3 != 2 and y % 3 == 2:
                return (44, 80, 46, 255)
            return None
        mask_paint(img, leaves.m, (64, 108, 56, 255), rim=(214, 210, 110, 255), rim_dark=(36, 64, 40, 255), tex=ltex, rim_right=True)

        def btex(x, y):
            if (x + int(3 * math.sin(y / 4.0))) % 9 == 0:
                return (70, 40, 34, 255)
            return None
        mask_paint(img, bark.m, (104, 62, 46, 255), rim=(236, 168, 90, 255), rim_dark=(62, 36, 30, 255), tex=btex, rim_right=True)
        outline_wrap(img, (46, 26, 26, 255))
        px = img.load()
        # drifting leaves
        for i in range(24):
            x = int(hsh(i, 1, 520) * W)
            y = int(80 + hsh(i, 2, 520) * 220)
            c = [(236, 170, 80, 255), (214, 210, 100, 255), (226, 120, 70, 255)][i % 3]
            for (dx, dy) in [(0, 0), (1, 0), (1, 1), (2, 1)]:
                if px[(x + dx) % W, y + dy][3] == 0:
                    px[(x + dx) % W, y + dy] = c
        return img


def pset(px, W, H, x, y, c):
    if 0 <= y < H:
        px[int(x) % W, int(y)] = c


class Hollow(World):
    key = "hollow"
    tiles_name = "Hollow Tiles"
    OUT = (22, 12, 36, 255)
    seed = 500
    ROCK = ((160, 132, 190, 255), (124, 100, 158, 255), (96, 74, 130, 255), (90, 68, 122, 255),
            (66, 48, 96, 255), (40, 26, 62, 255))
    DEEP = ((72, 54, 98, 255), (58, 42, 84, 255), (44, 30, 68, 255), (40, 28, 64, 255),
            (30, 20, 50, 255), (16, 8, 28, 255))
    lip = ((204, 180, 232, 255), (134, 106, 176, 255), (94, 70, 138, 255), (62, 42, 100, 255))
    lip_shadow = (66, 48, 96, 255)
    edge_l = (66, 48, 96, 255)
    edge_r = (40, 26, 62, 255)
    edge_dk = (66, 48, 96, 255)
    lip_base = 6
    lip_amp = 1.0
    drape = 12
    water = ((236, 206, 255, 220), (156, 114, 206, 190), (72, 46, 122, 178), (56, 34, 100, 188))
    checker = ((40, 28, 60, 255), (52, 38, 76, 255))
    bg_color = (24, 14, 44)
    SILK = (234, 228, 246, 255)
    SILK_DK = (178, 168, 206, 255)
    AMETH = ((250, 224, 255, 255), (214, 152, 250, 255), (156, 92, 212, 255), (96, 50, 150, 255))
    GLOWP = ((255, 226, 250, 255), (250, 150, 222, 255), (200, 90, 182, 255), (120, 50, 122, 255))
    MOON = ((236, 240, 255, 255), (180, 220, 255, 255), (110, 160, 230, 255))

    def tex(self, x, y):
        return rock_px(vor(23, 2, 4)[(x, y)], self.ROCK, x, y, 501)

    def lip_extra(self, px):
        for (x0, x1, sag) in [(2, 13, 3), (17, 29, 4)]:
            d0 = self.lip_depth(x0)
            for x in range(x0, x1 + 1):
                t = (x - x0) / (x1 - x0)
                y = d0 + 1 + int(round(sag * math.sin(math.pi * t)))
                if y < TS:
                    px[x, y] = self.SILK if t < 0.5 else self.SILK_DK
        for (x, y) in [(8, 3), (23, 2), (14, 4)]:
            px[x, y] = self.GLOWP[0]

    def t2(self, img):
        cracked_block(img, (124, 88, 142, 255), (168, 132, 186, 255), (84, 56, 102, 255), (40, 24, 58, 255), self.OUT, 511)

    def silk_threads(self, img, col):
        for pts in [[(7, 7), (12, 9)], [(24, 7), (19, 9)], [(7, 24), (12, 23)], [(24, 24), (20, 23)]]:
            line(img, pts, col)

    def t3(self, img):  # violet cocoon pod: silk bands wrapped round a pink glow
        px = img.load()
        pod_window(px, ((64, 50, 92, 255), (112, 98, 142, 255), (36, 26, 58, 255)), self.OUT,
                   (22, 12, 34, 255), (70, 38, 82, 255))
        self.silk_threads(img, self.SILK_DK)
        g_hi, g_lt, g_mid, g_dk = self.GLOWP

        def shade(nx, ny, d):
            if d > 0.9:
                return self.OUT
            band = (ny * 3.2 + nx * 1.1 + 10) % 1.0
            if band < 0.34:
                return self.SILK if nx < 0.25 else self.SILK_DK
            if band < 0.42:
                return self.SILK_DK
            return g_hi if d < 0.35 else (g_lt if d < 0.65 else g_mid)
        wa.blob(px, 16, 16.5, 7.2, 10.2, shade)
        for (x, y) in [(9, 13), (23, 18), (10, 22)]:
            px[x, y] = g_hi

    def t4(self, img):  # torn, empty cocoon: grey silk split open over a dark hollow
        px = img.load()
        pod_window(px, ((74, 60, 96, 255), (108, 94, 132, 255), (46, 34, 66, 255)), self.OUT, (22, 12, 34, 255))
        self.silk_threads(img, (120, 108, 140, 255))
        silk, silk_dk = (176, 166, 190, 255), (128, 116, 148, 255)

        def shade(nx, ny, d):
            torn = 0.34 + 0.1 * math.sin(ny * 9)
            if abs(nx) < torn and ny > -0.75:
                if abs(nx) > torn - 0.12:
                    return silk_dk
                return (34, 22, 44, 255)
            if d > 0.9:
                return self.OUT
            band = (ny * 3.2 + nx * 1.1 + 10) % 1.0
            return silk if (band < 0.5 and nx < 0) else silk_dk
        wa.blob(px, 16, 16.5, 7.2, 10.2, shade)

    def t7(self, img):  # obsidian
        px = img.load()
        wa.bevel_block(px, (54, 40, 80, 255), (100, 84, 136, 255), (30, 20, 48, 255), self.OUT, 3)
        for i in range(16):
            put(px, 32, 32, 7 + i, 24 - i, (196, 176, 236, 255) if 3 < i < 13 else (130, 110, 176, 255))
            put(px, 32, 32, 8 + i, 24 - i, (110, 92, 150, 255))
        for i in range(6):
            put(px, 32, 32, 18 + i, 26 - i, (130, 110, 176, 255))

    def crown(self, u):  # broken, splintered spire top
        return 1 + int(round(6 * hsh(u // 3, 0, 531) * abs(math.sin(u * 0.3 + 0.5))))

    def col_px(self, u, y, top):
        ramp = ((176, 160, 196, 255), (140, 122, 164, 255), (108, 92, 134, 255), (80, 64, 106, 255), (56, 42, 78, 255))

        def tex(u, y, top, t, idx):
            # silk wound diagonally round the spire, so it reads as twisted, not banded
            w = (y + u * 0.5) % 16
            if 6 <= w < 8.5:
                return self.SILK if t < 0.5 else self.SILK_DK
            if 8.5 <= w < 9.5 and t > 0.3:
                return ramp[4]
            if (u * 2 + y) % 13 == 0:
                return ramp[min(4, idx + 1)]
            if t < 0.35 and hsh(u, y, 532) < 0.03:
                return self.GLOWP[1]
            return None
        return self.organic_col(u, y, top, ramp, tex)

    def plat(self, part):
        img = new(TS, TS)
        px = img.load()
        hi, mid, dk = (178, 162, 194, 255), (138, 122, 158, 255), (96, 82, 120, 255)
        x0 = 2 if part == 0 else 0
        x1 = 29 if part == 2 else 31
        for x in range(x0, x1 + 1):
            jag = 1 if (part != 1 and hsh(x, 0, 541) < 0.4 and (x < 5 or x > 26)) else 0
            for y in range(2 + jag, 10):
                c = hi if y == 2 + jag else (dk if y == 9 else mid)
                if (x + part * 32) % 13 == 0:
                    c = dk
                if (x * 3 + y * 7) % 23 == 0:
                    c = dk
                px[x, y] = c
        for (a, b) in [(6, 9), (21, 24)]:
            for x in range(a, b + 1):
                for y in range(1, 11):
                    px[x, y] = self.SILK if x < b else self.SILK_DK
        if part == 1:
            for (x, L) in [(12, 4), (28, 3)]:
                for y in range(10, 10 + L):
                    px[x, y] = self.SILK_DK
        outline(img, self.OUT)
        return img

    def t15(self, img):
        px = img.load()
        self.fill(px)
        for (cx, cy) in [(8, 9), (22, 18), (12, 24)]:
            hi, lt, mid, dk = self.AMETH
            for (dx, dy) in [(0, -2), (-1, -1), (0, -1), (1, -1), (-1, 0), (0, 0), (1, 0), (-1, 1), (0, 1), (1, 1), (0, 2)]:
                px[cx + dx, cy + dy] = hi if dy < -1 else (lt if dx < 1 and dy < 1 else mid)
            for (dx, dy) in [(0, -3), (-2, -1), (2, -1), (-2, 0), (2, 0), (-2, 1), (2, 1), (-1, 2), (1, 2), (0, 3)]:
                px[cx + dx, cy + dy] = self.OUT

    def t16(self, img):
        tuft(img, ((198, 182, 210, 255), (146, 128, 166, 255), (100, 84, 122, 255)), out=self.OUT)

    def t17(self, img):  # night bells
        px = img.load()
        stem = (110, 130, 120, 255)
        for (x, top) in [(9, 14), (17, 9), (24, 17)]:
            for y in range(top, 32):
                px[x, y] = stem
            hi, mid, dk = self.MOON
            for dy in range(4):
                for dx in range(-2 + (dy == 0), 3 - (dy == 0)):
                    px[x + dx, top + dy] = hi if dx < 0 else (mid if dx < 2 else dk)
        outline(img, self.OUT)
        for (x, top) in [(9, 14), (17, 9), (24, 17)]:
            halo(img, x + 0.5, top + 3, 6, self.MOON[1], 56, 24)

    def t18(self, img):  # pale glow mushrooms
        px = img.load()
        hi, lt, mid, dk = self.GLOWP
        for (cx, base, r, h) in [(11, 30, 4, 6), (21, 30, 3, 4)]:
            mini_mushroom(px, cx, base, r, h, lt, hi, mid, (226, 214, 236, 255), (168, 152, 190, 255), spot=(255, 255, 255, 255))
        outline(img, self.OUT)
        halo(img, 11.5, 22, 9, lt, 60, 26)
        halo(img, 21.5, 25, 6, lt, 60, 26)

    def t19(self, img):  # thorny dead shrub
        px = img.load()
        br, br_dk, thorn = (110, 86, 130, 255), (74, 56, 96, 255), (230, 150, 220, 255)
        for (ang, L) in [(-1.2, 15), (-0.6, 18), (0.0, 20), (0.5, 17), (1.1, 14), (-0.3, 11), (0.8, 10)]:
            x, y = 16.0, 31.0
            for i in range(L):
                x += math.sin(ang + 0.15 * math.sin(i / 2.0))
                y -= math.cos(ang) * 0.95
                put(px, 32, 32, int(round(x)), int(round(y)), br if i < L - 4 else br_dk)
                if i % 4 == 3:
                    put(px, 32, 32, int(round(x)) + (1 if ang >= 0 else -1), int(round(y)) - 1, thorn)
        rect(px, 32, 32, 12, 30, 20, 31, br_dk)
        outline(img, self.OUT)

    def t20(self, img):  # broken iron fence (tiles horizontally)
        px = img.load()
        iron, iron_hi, iron_dk = (72, 62, 96, 255), (124, 112, 156, 255), (44, 36, 62, 255)
        for (xp, top) in [(4, 8), (12, 10), (20, 15), (28, 8)]:
            for y in range(top + 2, 32):
                px[xp, y] = iron_hi
                px[xp + 1, y] = iron
            px[xp, top + 1] = iron_hi
            px[xp + 1, top + 1] = iron
            px[xp, top] = iron_hi
            if top < 12:
                px[xp - 1, top + 2] = iron
                px[xp + 2, top + 2] = iron_dk
        for x in range(32):
            for y in (13, 25):
                if not (16 < x < 22 and y == 13):
                    px[x, y] = iron
                    px[x, y + 1] = iron_dk
        outline(img, self.OUT)
        for y in range(32):
            for x in (0, 31):
                if px[x, y] == self.OUT and px[1 if x == 0 else 30, y] in (iron, iron_dk):
                    px[x, y] = px[1 if x == 0 else 30, y]

    def t21(self, img):  # crooked sign with a moth emblem
        def glyph(px):
            c = self.SILK
            for (x, y) in [(14, 12), (15, 12), (16, 12), (17, 12), (15, 11), (16, 11), (15, 13), (16, 13)]:
                px[x, y] = (60, 40, 70, 255)
            for (dx, dy) in [(-4, -2), (-3, -2), (-5, -1), (-4, -1), (-3, -1), (-2, -1), (-4, 0), (-3, 0), (-2, 0), (-3, 1), (-2, 1), (-4, 2), (-3, 2)]:
                px[15 + dx, 12 + dy] = c
                px[16 - dx, 12 + dy] = c
        sign_board(img, (122, 104, 136, 255), (160, 144, 176, 255), (84, 68, 100, 255), None, self.OUT, glyph)

    def t22(self, img):  # silk strand with dew (stacks vertically)
        px = img.load()
        for y in range(32):
            x = 16 + int(round(1.0 * math.sin(TAU * y / 32)))
            px[x, y] = self.SILK if y % 4 else self.SILK_DK
        for (y, side) in [(6, 1), (19, -1)]:
            x = 16 + int(round(math.sin(TAU * y / 32)))
            for i in range(1, 6):
                put(px, 32, 32, x + side * i, y + i, self.SILK_DK)
        for (x, y) in [(16, 11), (17, 26)]:
            px[x, y] = (200, 230, 255, 255)
            px[x, y + 1] = self.MOON[1]
        halo(img, 16.5, 12, 3.5, self.MOON[1], 50, 22)

    def t23(self, img):  # hanging cocoon
        px = img.load()
        for y in range(0, 8):
            px[16, y] = self.SILK_DK
        for y in range(7, 29):
            for x in range(9, 24):
                dx, dy = (x + 0.5 - 16.5) / 6.5, (y + 0.5 - 17.5) / 10.5
                if dx * dx + dy * dy <= 1:
                    band = (y + int(2 * dx)) % 4
                    c = self.SILK if band else self.SILK_DK
                    if dx > 0.45:
                        c = self.SILK_DK if band else (140, 128, 170, 255)
                    px[x, y] = c
        outline(img, self.OUT)
        px[16, 0] = self.SILK_DK
        for x in (15, 17):
            if px[x, 0] == self.OUT:
                px[x, 0] = CLEAR

    def ledge(self, part):  # violet shelf fungus
        img = new(TS, TS)
        body, hi, dark, rim, rim_dk, spot = (134, 82, 178, 255), (198, 148, 232, 255), (98, 56, 146, 255), (224, 206, 240, 255), (168, 148, 196, 255), (250, 176, 240, 255)

        def colour(gx, y, top, bot):
            if y == top + 1:
                return hi
            if y >= bot - 4:
                return rim_dk if y == bot - 4 else (rim if y < bot - 1 else rim_dk)
            if y >= bot - 8 and bayer(gx, y) < 0.5:
                return dark
            if hsh(gx // 3, y // 3, 561) < 0.1 and gx % 3 == 1 and y % 3 == 1:
                return spot
            return body
        dome_ledge(img, part, colour, self.OUT, 24)
        return img

    def t29(self, img):  # fungal stalk
        px = img.load()
        ramp = ((236, 226, 244, 255), (214, 200, 230, 255), (180, 164, 206, 255), (140, 124, 170, 255), (104, 90, 136, 255))
        for y in range(32):
            for x in range(11, 21):
                c = cyl(x, 11, 20, ramp)
                px[x, y] = c if c is not None else self.OUT
            if y % 10 == 3:
                for x in range(13, 19):
                    px[x, y] = ramp[3]

    def t32(self, img):  # shadow thorns
        dark, mid, tip = (44, 26, 66, 255), (76, 48, 104, 255), (255, 110, 206, 255)
        for (cx, h, lean) in [(5, 14, -3), (12, 21, 2), (19, 16, -2), (26, 19, 3)]:
            poly(img, [(cx - 4, 31), (cx + lean, 31 - h), (cx + 4, 31)], dark)
            poly(img, [(cx - 4, 31), (cx + lean, 31 - h), (cx - 1, 31)], mid)
            px = img.load()
            for k in range(3):
                put(px, 32, 32, cx + lean + (k > 1) * (1 if lean < 0 else -1) * 0, 31 - h + k + 1, tip)
        px = img.load()
        rect(px, 32, 32, 0, 29, 31, 31, dark)
        outline(img, self.OUT)

    def t33(self, img):
        px = img.load()
        facet_block(px, (176, 112, 232, 255), (248, 218, 255, 255), (122, 64, 190, 255), (70, 30, 120, 255), self.OUT)

    def t34(self, img):
        px = img.load()
        g = vor(23, 2, 4)
        for y in range(TS):
            for x in range(TS):
                px[x, y] = rock_px(g[(x, y)], self.DEEP, x, y, 501)

    def t35(self, img):  # dim violet lamp
        px = img.load()
        iron, iron_hi = (60, 50, 84, 255), (110, 98, 140, 255)
        for y in range(14, 30):
            px[15, y] = iron_hi
            px[16, y] = iron
        rect(px, 32, 32, 12, 29, 19, 31, iron)
        rect(px, 32, 32, 12, 29, 19, 29, iron_hi)
        rect(px, 32, 32, 11, 3, 20, 4, iron)
        rect(px, 32, 32, 13, 2, 18, 2, iron_hi)
        flame, core = (196, 140, 255, 255), (255, 232, 255, 255)
        rect(px, 32, 32, 11, 5, 20, 12, flame)
        rect(px, 32, 32, 14, 7, 17, 11, core)
        for y in range(5, 13):
            px[11, y] = iron
            px[20, y] = iron
            if y in (5, 12):
                for x in range(11, 21):
                    px[x, y] = iron
        px[15, 13] = iron
        px[16, 13] = iron
        outline(img, self.OUT)
        halo(img, 16, 8.5, 12, (190, 130, 255), 56, 22)

    def t36(self, img):  # moon flower
        px = img.load()
        for y in range(10, 32):
            px[16, y] = (90, 120, 110, 255)
            px[15, y] = (120, 150, 136, 255)
        for (y, side) in [(22, -1), (26, 1)]:
            for i in range(1, 5):
                x = 16 + side * i if side > 0 else 15 + side * i
                px[x, y - i // 2] = (120, 150, 136, 255)
        hi, mid, dk = self.MOON
        for k in range(5):
            ang = -math.pi / 2 + k * TAU / 5
            for s in range(1, 6):
                x, y = 15.5 + s * math.cos(ang), 7.5 + s * math.sin(ang)
                for (ox, oy) in [(0, 0), (1, 0), (0, 1)]:
                    put(px, 32, 32, int(x) + ox, int(y) + oy, hi if s < 4 else mid)
        disc(px, 32, 32, 15.5, 7.5, 1.6, lambda dx, dy, d: (255, 236, 160, 255))
        outline(img, self.OUT)
        halo(img, 15.5, 7.5, 11, mid, 50, 22)

    def t37(self, img):  # luminous moss sprouts (tile horizontally)
        px = img.load()
        for (x, h, c) in [(2, 4, 0), (5, 6, 1), (9, 3, 0), (13, 5, 1), (18, 4, 0), (21, 7, 1), (26, 3, 0), (29, 5, 1)]:
            for i in range(h):
                px[x, 31 - i] = self.lip[1] if i < h - 1 else ((180, 240, 255, 255) if c else self.GLOWP[1])
        for x in range(32):
            if hsh(x, 31, 571) < 0.6:
                px[x, 31] = self.lip[2]

    # -- backgrounds ---------------------------------------------------------
    def sky(self):
        W, H = SW, SH
        img = Image.new("RGBA", (W, H))
        vgrad(img, [(0, (16, 10, 38)), (120, (50, 30, 92)), (240, (118, 62, 140)), (320, (194, 102, 150)), (359, (228, 140, 152))], 11)
        px = img.load()
        for i in range(140):
            x = int(hsh(i, 1, 581) * W)
            y = int(hsh(i, 2, 581) * 230)
            if hsh(i, 3, 581) < 0.12:
                sparkle(px, W, H, x, y, (255, 255, 255, 255), (190, 170, 240, 255))
            else:
                px[x, y] = (230, 220, 255, 255) if i % 3 else (170, 150, 220, 255)
        mx, my, mr = 190, 112, 56
        for y in range(H):
            for x in range(W):
                d = math.hypot(x + 0.5 - mx, y + 0.5 - my)
                if d <= mr:
                    c = (236, 228, 250, 255)
                    for (cx, cy, r) in [(-18, -14, 11), (14, 8, 15), (-6, 24, 8), (22, -22, 7), (-30, 10, 6)]:
                        if math.hypot(x - mx - cx, y - my - cy) < r:
                            c = (206, 194, 232, 255)
                    if x - mx > 30 and bayer(x, y) < (x - mx - 30) / 40:
                        c = (196, 182, 226, 255)
                    px[x, y] = c
                elif d <= mr + 2:
                    px[x, y] = (252, 246, 255, 255)
                elif d < mr + 70:
                    if bayer(x, y) < (mr + 70 - d) / 110:
                        px[x, y] = A(mix(px[x, y], (220, 180, 250), 0.35), 255)
        # wisps of cloud below the moon
        for (cx, cy, rw, rh) in [(120, 210, 120, 7), (380, 190, 90, 6), (560, 240, 110, 8), (300, 270, 140, 6)]:
            for y in range(cy - rh, cy + rh + 1):
                for x in range(cx - rw, cx + rw + 1):
                    if 0 <= x < W:
                        v = ((x - cx) / rw) ** 2 + ((y - cy) / rh) ** 2
                        if v <= 1 and bayer(x, y) < (0.7 if v < 0.4 else 0.3):
                            px[x, y] = A(mix(px[x, y], (90, 50, 110), 0.6), 255)
        # Duskmantle crossing the moon
        moth = new(W, H)
        sil, rim = (30, 16, 48, 255), (88, 52, 120, 255)
        bx, by = 236, 128
        for s in (-1, 1):
            # forewing: curved leading edge sweeping to a hooked tip, scalloped trailing edge
            fw = [(bx + s * 3, by - 6)]
            for k in range(1, 9):
                t = k / 8
                fw.append((bx + s * (3 + 72 * t), by - 6 - 30 * math.sin(t * 1.9)))
            fw += [(bx + s * 80, by - 30), (bx + s * 74, by - 18), (bx + s * 62, by - 12), (bx + s * 54, by - 4),
                   (bx + s * 40, by - 2), (bx + s * 28, by + 4), (bx + s * 8, by + 6)]
            poly(moth, fw, sil)
            # hindwing: rounded lobe with a trailing tail
            hw = [(bx + s * 4, by + 2), (bx + s * 34, by + 2), (bx + s * 46, by + 12), (bx + s * 44, by + 24),
                  (bx + s * 32, by + 30), (bx + s * 24, by + 42), (bx + s * 20, by + 30), (bx + s * 8, by + 20)]
            poly(moth, hw, sil)
        mp = moth.load()
        disc(mp, W, H, bx + 0.5, by + 6, 5, lambda dx, dy, d: sil, )
        for y in range(by - 6, by + 26):
            for x in range(bx - 3, bx + 4):
                if abs(x + 0.5 - bx - 0.5) <= 3 - max(0, (y - by - 18)) * 0.4:
                    mp[x, y] = sil
        for k in range(14):
            for s in (-1, 1):
                mp[bx + s * (2 + k // 2), by - 8 - k] = sil
                if k % 3 == 0:
                    mp[bx + s * (3 + k // 2), by - 8 - k] = sil
        for (cx, cy) in [(bx - 46, by - 18), (bx + 46, by - 18)]:
            disc(mp, W, H, cx, cy, 4, lambda dx, dy, d: (150, 70, 170, 255) if d < 2.5 else (70, 36, 96, 255))
        for y in range(H):
            for x in range(W):
                if mp[x, y][3] and mp[x, y] == sil:
                    if mp[x, y - 1][3] == 0 or mp[x - 1, y][3] == 0:
                        mp[x, y] = rim
        img.alpha_composite(moth)
        return ("Hollow Sky", img)

    def backgrounds(self):
        return [("Hollow Spires", self.bg_spires(), 0.25), ("Hollow Mist", self.bg_mist(), 0.6)]

    def spire(self, m, cx, ytop, w0, bend, seed):
        H = m.H
        for y in range(int(ytop), H):
            t = max(0.0, (y - ytop) / (H - ytop))
            xc = cx + bend * (1 - t) ** 2 + 3 * math.sin(y / 17.0 + seed)
            w = 2 + w0 * t ** 0.7 + 2 * math.sin(y / 9.0 + seed * 2)
            m.hspan(y, xc - w, xc + w)
        # one crooked hook (a broken rock spur) part-way up
        yb = ytop + 50 + hsh(seed, 1, 591) * 40
        if yb < H - 60:
            t = (yb - ytop) / (H - ytop)
            xc = cx + bend * (1 - t) ** 2
            s = 1 if seed % 2 else -1
            m.curve([(xc, yb + 8), (xc + s * 12, yb), (xc + s * 16, yb - 14)], 4, 1)

    def bg_spires(self):
        W, H = BW, BH
        img = new(W, H)
        for (layer, col, rim, n) in [(0, (66, 40, 98, 255), (124, 84, 168, 255), 9), (1, (40, 22, 62, 255), (98, 60, 140, 255), 6)]:
            m = Mask(W, H)
            for x in range(W):
                m.vspan(x, 300 + layer * 20 + pn(x, W, [(10, 3, layer), (6, 8, 1.2)]), H)
            for i in range(n):
                cx = (i + 0.2 + hsh(i, 1, 592 + layer) * 0.6) * W / n
                if layer == 1 and abs(wrapd(cx - 700, W)) < 90:
                    continue
                self.spire(m, cx, 90 + layer * 60 + hsh(i, 2, 592 + layer) * 100, 14 + layer * 8 + hsh(i, 3, 592) * 10,
                           (hsh(i, 4, 592) - 0.5) * 60, i + layer * 10)
            if layer == 1:
                self.spire(m, 700, 30, 30, -20, 77)
                m.disc(696, 92, 44, 36)
                m.disc(700, 60, 22, 18)
            mask_paint(img, m.m, col, rim=rim)
        px = img.load()
        # silk wrapping on the nest + glowing openings
        for y in range(56, 128):
            for x in range(650, 742):
                if px[x, y][3] and (y + int(6 * math.sin(x / 9.0))) % 7 == 0:
                    px[x, y] = (110, 80, 150, 255)
        for (x, y, r) in [(684, 90, 4), (712, 100, 3), (700, 66, 3), (668, 108, 2.5)]:
            disc(px, W, H, x, y, r, lambda dx, dy, d: (255, 210, 255, 255) if d < r * 0.5 else (220, 120, 240, 255))
            glow(img, x, y, r * 4, r * 4, (200, 110, 230), 0.6)
        # silk strands between spires
        for (x0, x1, y0, sag) in [(560, 650, 150, 30), (742, 840, 170, 26), (120, 230, 190, 20)]:
            for x in range(x0, x1):
                t = (x - x0) / (x1 - x0)
                y = int(y0 + sag * math.sin(math.pi * t))
                if px[x % W, y][3] == 0:
                    px[x % W, y] = (170, 140, 210, 160)
        return img

    def bg_mist(self):
        W, H = BW, BH
        img = new(W, H)
        px = img.load()
        col = (206, 176, 236)
        for y in range(H):
            for x in range(W):
                band1 = 236 + pn(x, W, [(14, 3, 0.4), (8, 7, 2.1), (4, 17, 0.9)])
                band2 = 150 + pn(x, W, [(10, 2, 1.6), (6, 9, 0.2)])
                d = 0.0
                if y > band1:
                    d = min(1.0, (y - band1) / 50.0) * (0.7 + 0.3 * math.sin(TAU * 5 * x / W + y / 13.0))
                elif abs(y - band2) < 9:
                    d = (1 - abs(y - band2) / 9) * 0.45 * (0.5 + 0.5 * math.sin(TAU * 4 * x / W + 1.0))
                if d <= 0:
                    continue
                lvl = d * 3
                q = int(lvl) + (1 if (lvl - int(lvl)) > bayer(x, y) else 0)
                if q > 0:
                    px[x, y] = A(col, (0, 64, 104, 148)[min(3, q)])
        for i in range(60):
            x = int(hsh(i, 1, 599) * W)
            y = int(120 + hsh(i, 2, 599) * 220)
            px[x, y] = (255, 200, 250, 230) if i % 2 else (190, 230, 255, 230)
        return img


class Village(World):
    key = "village"
    tiles_name = "Village Tiles"
    OUT = (44, 26, 34, 255)
    seed = 600
    SOIL = ((160, 104, 76, 255), (126, 80, 60, 255), (96, 58, 48, 255), (68, 40, 40, 255))
    lip = ((160, 190, 96, 255), (100, 148, 74, 255), (66, 110, 62, 255), (46, 78, 52, 255))
    lip_shadow = (68, 40, 40, 255)
    edge_l = (96, 58, 48, 255)
    edge_r = (68, 40, 40, 255)
    edge_dk = (96, 58, 48, 255)
    COB = ((222, 206, 198, 255), (180, 162, 164, 255), (132, 114, 126, 255), (86, 66, 78, 255))
    drape = 13
    water = ((255, 216, 170, 226), (150, 150, 214, 190), (82, 90, 164, 172), (66, 72, 144, 182))
    checker = ((80, 60, 92, 255), (96, 72, 106, 255))
    bg_color = (60, 40, 80)
    WOOD = ((232, 176, 120, 255), (198, 136, 88, 255), (150, 96, 64, 255), (104, 64, 48, 255))
    WARM = ((255, 246, 200, 255), (255, 214, 120, 255), (236, 160, 70, 255))
    CAPS = [((236, 96, 80, 255), (255, 150, 120, 255), (170, 56, 60, 255)),
            ((240, 150, 70, 255), (255, 196, 120, 255), (186, 100, 50, 255)),
            ((80, 160, 156, 255), (130, 206, 190, 255), (50, 108, 116, 255)),
            ((160, 90, 140, 255), (206, 140, 180, 255), (110, 56, 100, 255))]

    def tex(self, x, y):
        c = wa.dirt_pixel(x, y, 17, self.SOIL)
        return c

    def lip_depth(self, x):
        return 12

    def lip_px(self, x, y, d):
        ghi, gmid, gdk, gdeep = self.lip
        if y == 1:
            return gmid if x % 7 in (2, 5) else ghi
        if y == 2:
            return gmid if hsh(x, y, 601) < 0.7 else gdk
        hi, mid, dk, mo = self.COB
        if y == 12:
            return mo
        row = 0 if y <= 7 else 1
        ly = y - (3 if row == 0 else 8)
        off = 0 if row == 0 else 4
        lx = (x + off) % 8
        if lx == 7 or (ly == 4 and row == 0):
            return mo
        if (lx in (0, 6)) and ly in (0, 4 if row == 1 else 3):
            return mo
        tint = hsh((x + off) // 8, row, 602)
        if ly == 0 or lx == 0:
            return hi
        if lx >= 5 or ly >= 3:
            return dk if tint < 0.5 else mid
        return mid if tint < 0.7 else hi

    def lip_extra(self, px):
        for (x, y) in [(7, 3), (15, 8), (26, 3), (30, 8)]:
            px[x, y] = self.lip[1]
            px[x, y + 1] = self.lip[2]

    STONES = [((232, 200, 172, 255), (204, 168, 142, 255), (150, 112, 100, 255)),
              ((216, 192, 182, 255), (184, 160, 154, 255), (132, 108, 112, 255)),
              ((238, 208, 166, 255), (210, 174, 128, 255), (156, 120, 90, 255)),
              ((208, 186, 172, 255), (176, 152, 140, 255), (124, 100, 100, 255))]

    def t2(self, img):  # cracked rosy fieldstone with ivy (breakable)
        wa.fieldstone(img.load(), self.STONES, (92, 56, 62, 255), self.lip[:3], self.OUT)

    def crate(self, px, base, hi, dk):
        wa.bevel_block(px, base, hi, dk, self.OUT, 2)
        for x in (10, 21):
            for y in range(3, 29):
                px[x, y] = dk
                px[x + 1, y] = hi
        for y in range(3, 29):
            for x in range(3, 29):
                if px[x, y] == base and hsh(x, y // 3, 611) < 0.08:
                    px[x, y] = dk

    BUD = ((255, 236, 226, 255), (255, 168, 186, 255), (226, 104, 146, 255), (156, 58, 106, 255))

    def planter(self, img, wood, glow):
        """Window box: a wooden frame round a dark niche with a soil bed along the bottom."""
        px = img.load()
        pod_window(px, wood, self.OUT, (70, 44, 46, 255), glow)
        s_hi, s_mid, s_dk, s_deep = self.SOIL
        for y in range(22, 25):
            for x in range(7, 25):
                px[x, y] = s_mid if hsh(x, y, 631) > 0.25 else s_dk
        for x in range(7, 25):
            px[x, 21] = s_hi if x % 3 else self.lip[1]
        return px

    def t3(self, img):  # flower-bud planter: a big closed bud glowing at its tip
        px = self.planter(img, ((198, 136, 88, 255), (232, 176, 120, 255), (150, 96, 64, 255)), (126, 92, 70, 255))
        b_hi, b_lt, b_mid, b_dk = self.BUD
        g_hi, g_mid, g_dk, _ = self.lip
        for y in range(16, 22):
            px[16, y] = g_dk
            px[15, y] = g_mid

        def bud(nx, ny, d):
            w = 1.0 - 0.45 * max(0.0, -ny) ** 1.6
            if abs(nx) > w:
                return None
            if d > 0.92 or abs(nx) > w - 0.1:
                return self.OUT
            if ny > 0.45:
                return g_mid if nx < 0.2 else g_dk
            if abs(abs(nx) - 0.38 * w) < 0.08:
                return b_dk
            if ny < -0.55:
                return b_hi
            return b_lt if nx < -0.1 else b_mid
        wa.blob(px, 16, 13.5, 6.6, 8.6, bud)
        for (x, y) in [(10, 18), (11, 17), (21, 18), (20, 17)]:
            px[x, y] = g_mid
        px[16, 4] = self.WARM[0]
        px[16, 5] = self.WARM[1]
        for (x, y) in [(9, 9), (23, 10), (22, 8)]:
            px[x, y] = self.WARM[0]

    def t4(self, img):  # spent bloom: petals opened and drooping, the seed head emptied
        px = self.planter(img, ((150, 104, 74, 255), (184, 140, 104, 255), (104, 70, 52, 255)), None)
        g_hi, g_mid, g_dk, _ = self.lip
        for y in range(15, 22):
            px[16, y] = g_dk
            px[15, y] = g_mid
        petal, petal_dk = (190, 150, 166, 255), (140, 100, 124, 255)
        for (cx, cy, rx, ry) in [(10.5, 15, 4.2, 2.4), (21.5, 15, 4.2, 2.4), (12.5, 11, 3, 2.6), (19.5, 11, 3, 2.6)]:
            wa.blob(px, cx, cy, rx, ry, lambda nx, ny, d: self.OUT if d > 0.85 else (petal if ny < 0.1 else petal_dk))
        wa.blob(px, 16, 13, 3.4, 3.0, lambda nx, ny, d: self.OUT if d > 0.8 else (
            (84, 56, 44, 255) if (int((nx + 2) * 4) + int((ny + 2) * 4)) % 2 else (60, 38, 34, 255)))

    def t7(self, img):
        px = img.load()
        base, hi, dk = (160, 150, 164, 255), (208, 200, 210, 255), (106, 96, 116, 255)
        wa.bevel_block(px, base, hi, dk, self.OUT, 3)
        for x in range(6, 26):
            px[x, 6] = dk
            px[x, 25] = hi
        for y in range(6, 26):
            px[6, y] = dk
            px[25, y] = hi
        for x in range(8, 24):
            for y in range(8, 24):
                if hsh(x, y, 621) < 0.06:
                    px[x, y] = (140, 130, 146, 255)

    def col_px(self, u, y, top):
        ramp = ((226, 214, 216, 255), (196, 182, 188, 255), (160, 146, 156, 255), (122, 108, 124, 255), (88, 74, 92, 255))
        ghi, gmid, gdk, gdeep = self.lip

        def tex(u, y, top, t, idx):
            if top and y <= self.crown(u) + 1:
                return ghi if hsh(u, y, 631) < 0.5 else gmid
            d = self.stack_seam(u, y)
            if d == 0:
                return ramp[4]
            # uneven vertical joints, staggered per course
            course = 0 if (y - self.stack_seam(u, y)) % 32 < 16 else 1
            jx = (13, 29, 45) if course == 0 else (21, 37, 53)
            if any(abs(u - j - int(round(1.2 * math.sin(y * 0.7 + j)))) < 0.5 for j in jx):
                return ramp[4]
            if d == 1 and idx > 0:
                return ramp[idx - 1]
            # one ivy vine winding down the left side, leaves every few rows
            vx = 12 + int(round(2.5 * math.sin(TAU * y / 32 + 0.5)))
            if u == vx:
                return gdk
            if y % 6 == 2 and u in (vx - 1, vx + 1, vx + 2 if (y // 6) % 2 else vx - 2):
                return gmid if u != vx + 2 and u != vx - 2 else ghi
            return None
        return self.organic_col(u, y, top, ramp, tex)

    def plat(self, part):  # wooden balcony with carved brackets
        img = new(TS, TS)
        px = img.load()
        lt, hi, mid, dk = self.WOOD
        x0 = 1 if part == 0 else 0
        x1 = 30 if part == 2 else 31
        for x in range(x0, x1 + 1):
            for y in range(2, 9):
                c = lt if y == 2 else (dk if y == 8 else hi if y == 3 else mid)
                if (x + part * 32) % 8 == 0 and y > 2:
                    c = dk
                px[x, y] = c
        rect(px, 32, 32, x0, 9, x1, 10, dk)
        if part != 1:
            m = part == 2

            def X(x):
                return 31 - x if m else x
            for y in range(11, 26):
                for x in range(4, 8):
                    px[X(x), y] = hi if x == 4 else (dk if x == 7 else mid)
            for k in range(10):
                x = 8 + k
                y = 11 + int(round(0.12 * k * k))
                for t in range(2):
                    put(px, 32, 32, X(x), y + t, mid if t == 0 else dk)
        else:
            for x in (8, 24):
                for y in range(11, 13):
                    px[x, y] = mid
        outline(img, self.OUT)
        return img

    def t15(self, img):
        px = img.load()
        self.fill(px)
        for (cx, cy, big) in [(5, 6, True), (20, 4, False), (12, 20, True), (26, 24, False)]:
            wa.pebble(px, cx, cy, big)
        worm = (236, 140, 150, 255)
        for (x, y) in [(18, 14), (19, 14), (20, 15), (21, 15), (22, 14), (23, 14)]:
            px[x, y] = worm
        px[23, 13] = (40, 20, 30, 255)

    def t16(self, img):
        tuft(img, (self.lip[0], self.lip[1], self.lip[2]), out=self.OUT)

    def t17(self, img):  # flower planter box
        px = img.load()
        lt, hi, mid, dk = self.WOOD
        for (x, top, c) in [(7, 15, (255, 120, 150, 255)), (11, 12, (255, 220, 90, 255)), (16, 14, (180, 140, 255, 255)),
                            (20, 11, (255, 120, 150, 255)), (25, 14, (255, 220, 90, 255))]:
            for y in range(top + 2, 23):
                px[x, y] = self.lip[2]
            for dx, dy in [(-1, 0), (1, 0), (0, -1), (0, 1)]:
                px[x + dx, top + dy] = c
            px[x, top] = (255, 250, 220, 255)
        for x in range(4, 29):
            for y in range(20, 23):
                if hsh(x, y, 641) < 0.5:
                    px[x, y] = self.lip[1]
        rect(px, 32, 32, 3, 23, 28, 31, mid)
        rect(px, 32, 32, 3, 23, 28, 23, lt)
        rect(px, 32, 32, 3, 30, 28, 31, dk)
        for x in (10, 21):
            for y in range(24, 30):
                px[x, y] = dk
        outline(img, self.OUT)

    def t18(self, img):
        px = img.load()
        for (cx, base, r, h) in [(11, 30, 4, 6), (21, 30, 3, 4)]:
            mini_mushroom(px, cx, base, r, h, wa.RED, wa.RED_HI, wa.RED_DK, wa.CREAM, wa.CREAM_DK, spot=wa.CREAM)
        for x in (5, 6, 26, 27):
            px[x, 30] = self.lip[1]
            px[x, 29] = self.lip[0] if x % 2 else self.lip[1]
        outline(img, self.OUT)

    def t19(self, img):  # hedge with flowers
        px = img.load()
        hi, mid, dk, deep = self.lip

        def sh(dx, dy, d):
            k = (dx * -0.6 + dy * -0.8) / max(d, 0.01)
            if d > 3 and k > 0.55:
                return hi
            return mid
        for (cx, cy, r) in [(10, 22, 8), (22, 22, 8), (16, 16, 9)]:
            disc(px, 32, 32, cx, cy, r, sh)
        for y in range(26, 32):
            for x in range(1, 31):
                if px[x, y][3]:
                    px[x, y] = dk if y < 29 else deep
        for (x, y, c) in [(10, 17, (255, 180, 200, 255)), (20, 14, (255, 240, 200, 255)), (24, 21, (255, 180, 200, 255)), (14, 23, (255, 240, 200, 255))]:
            px[x, y] = c
        outline(img, self.OUT)

    def t20(self, img):  # picket fence (tiles horizontally)
        px = img.load()
        paint, paint_hi, paint_dk = (226, 214, 200, 255), (250, 244, 232, 255), (170, 156, 150, 255)
        for x in range(32):
            for y in (14, 24):
                px[x, y] = paint_hi
                px[x, y + 1] = paint
                px[x, y + 2] = paint_dk
        for xp in (2, 10, 18, 26):
            for y in range(9, 32):
                for x in range(xp, xp + 5):
                    px[x, y] = paint_hi if x == xp else (paint_dk if x == xp + 4 else paint)
            for x in range(xp, xp + 5):
                px[x, 9] = CLEAR if x in (xp, xp + 4) else paint_hi
            px[xp + 2, 8] = paint_hi
        outline(img, self.OUT)
        for y in range(32):
            for x in (0, 31):
                if px[x, y] == self.OUT and px[1 if x == 0 else 30, y] in (paint, paint_hi, paint_dk):
                    px[x, y] = px[1 if x == 0 else 30, y]

    def t21(self, img):  # little well
        px = img.load()
        hi, mid, dk, mo = self.COB
        for y in range(20, 32):
            for x in range(5, 27):
                lx = (x - 5 + (3 if (y // 4) % 2 else 0)) % 6
                c = mo if (y % 4 == 3 or lx == 5) else (hi if y % 4 == 0 else mid)
                if x > 22 and c == mid:
                    c = dk
                px[x, y] = c
        rect(px, 32, 32, 6, 19, 25, 20, (60, 70, 120, 255))
        lt, whi, wmid, wdk = self.WOOD
        for x in (6, 24):
            for y in range(6, 20):
                px[x, y] = whi
                px[x + 1, y] = wdk
        for i in range(12):
            for x in range(4 + i, 28 - i):
                if 5 - i // 2 >= 0:
                    pass
        poly(img, [(3, 8), (16, 1), (28, 8)], self.CAPS[0][0])
        poly(img, [(3, 8), (16, 1), (16, 8)], self.CAPS[0][1])
        rect(px, 32, 32, 8, 10, 23, 10, wdk)
        for y in range(11, 15):
            px[15, y] = (110, 100, 100, 255)
        rect(px, 32, 32, 13, 15, 18, 18, wmid)
        rect(px, 32, 32, 13, 15, 18, 15, whi)
        outline(img, self.OUT)

    def t22(self, img):  # hanging ivy (stacks vertically)
        px = img.load()
        hi, mid, dk, deep = self.lip
        for y in range(32):
            x = 16 + int(round(2.2 * math.sin(TAU * y / 32)))
            px[x, y] = dk
            px[x + 1, y] = mid
        for (y, side) in [(4, 1), (12, -1), (20, 1), (28, -1)]:
            x = 16 + int(round(2.2 * math.sin(TAU * y / 32)))
            for (dx, dy) in [(1, 0), (2, 0), (1, 1), (2, 1), (3, 1), (2, 2)]:
                px[x + side * dx + (1 if side > 0 else 0), y + dy] = hi if dy == 0 else mid
        px[14, 16] = (255, 180, 200, 255)
        px[20, 8] = (255, 240, 200, 255)
        outline(img, self.OUT)

    def t23(self, img):  # mailbox
        px = img.load()
        lt, hi, mid, dk = self.WOOD
        for y in range(17, 32):
            px[15, y] = hi
            px[16, y] = dk
        box, box_hi, box_dk = (90, 130, 190, 255), (140, 180, 226, 255), (60, 86, 140, 255)
        for y in range(7, 18):
            for x in range(8, 24):
                top_r = 4
                if y < 7 + top_r and math.hypot(max(0, abs(x + 0.5 - 16) - 4), 7 + top_r - y) > top_r:
                    continue
                px[x, y] = box_hi if x < 11 else (box_dk if x > 20 else box)
        rect(px, 32, 32, 8, 16, 23, 17, box_dk)
        for y in range(5, 12):
            px[25, y] = (220, 70, 60, 255)
        rect(px, 32, 32, 25, 5, 28, 7, (236, 96, 80, 255))
        rect(px, 32, 32, 13, 11, 18, 12, (250, 236, 200, 255))
        outline(img, self.OUT)

    def ledge(self, part):  # striped awning
        img = new(TS, TS)
        px = img.load()
        red, red_dk, cream, cream_dk = (214, 80, 72, 255), (160, 52, 56, 255), (248, 232, 204, 255), (204, 182, 160, 255)
        for x in range(TS):
            u = x + part * 32
            if part == 0 and x < 3:
                continue
            if part == 2 and x > 28:
                continue
            yb = 14 + int(round(3 * math.sqrt(max(0.0, 1 - (((u % 8) - 3.5) / 4.0) ** 2))))
            for y in range(2, yb + 1):
                stripe = (u // 6) % 2 == 0
                c = red if stripe else cream
                if y <= 3:
                    c = (255, 150, 130, 255) if stripe else (255, 250, 236, 255)
                elif y >= yb - 1 or y >= 12:
                    c = red_dk if stripe else cream_dk
                px[x, y] = c
        outline(img, self.OUT)
        return img

    def t29(self, img):  # wooden post
        px = img.load()
        lt, hi, mid, dk = self.WOOD
        for y in range(32):
            for x in range(13, 19):
                c = self.OUT if x in (13, 18) else (hi if x == 14 else (dk if x == 17 else mid))
                if y % 11 == 5 and 13 < x < 18:
                    c = dk
                px[x, y] = c

    def t32(self, img):  # thistles (hazard)
        px = img.load()
        leaf, leaf_dk = (100, 140, 90, 255), (60, 96, 70, 255)
        head, head_hi, spike = (170, 90, 200, 255), (220, 150, 240, 255), (236, 226, 200, 255)
        for (cx, top) in [(7, 12), (16, 7), (25, 13)]:
            for y in range(top + 4, 32):
                px[cx, y] = leaf
            for (dy, s) in [(10, -1), (15, 1), (20, -1)]:
                for i in range(1, 5):
                    put(px, 32, 32, cx + s * i, top + dy - i // 2, leaf_dk if i > 2 else leaf)
                    put(px, 32, 32, cx + s * i, top + dy - i // 2 - 1, spike if i == 4 else None or leaf)
            for y in range(top, top + 4):
                for x in range(cx - 2, cx + 3):
                    px[x, y] = head_hi if x < cx else head
            for x in range(cx - 2, cx + 3):
                px[x, top + 4] = leaf_dk
                px[x, top + 5] = leaf
            for (dx, dy) in [(-3, 0), (3, 0), (-2, -1), (2, -1), (0, -1)]:
                px[cx + dx, top + dy] = spike
        rect(px, 32, 32, 2, 30, 29, 31, leaf_dk)
        outline(img, self.OUT)

    def t33(self, img):  # hay bale
        px = img.load()
        base, hi, dk, deep = (236, 196, 100, 255), (255, 232, 150, 255), (196, 150, 64, 255), (150, 104, 44, 255)
        wa.bevel_block(px, base, hi, deep, self.OUT, 2)
        for y in range(3, 29):
            for x in range(3, 29):
                n = hsh(x // 3, y, 661)
                if n < 0.25:
                    px[x, y] = dk
                elif n > 0.85:
                    px[x, y] = hi
        for x in (9, 22):
            for y in range(1, 31):
                px[x, y] = (150, 70, 50, 255)
                px[x + 1, y] = (110, 50, 40, 255)

    def t34(self, img):  # dark stone foundation
        px = img.load()
        for y in range(TS):
            for x in range(TS):
                row = y // 8
                off = 8 if row % 2 else 0
                if y % 8 == 7 or (x + off) % 16 == 15:
                    c = (40, 28, 40, 255)
                elif y % 8 == 0 or (x + off) % 16 == 0:
                    c = (104, 88, 106, 255)
                else:
                    c = (78, 64, 82, 255) if hsh(x, y, 671) > 0.06 else (64, 50, 68, 255)
                px[x, y] = c

    def t35(self, img):  # lamp post
        px = img.load()
        iron, iron_hi = (60, 54, 70, 255), (110, 104, 124, 255)
        for y in range(12, 30):
            px[15, y] = iron_hi
            px[16, y] = iron
        rect(px, 32, 32, 12, 28, 19, 31, iron)
        rect(px, 32, 32, 12, 28, 19, 28, iron_hi)
        rect(px, 32, 32, 11, 1, 20, 2, iron)
        rect(px, 32, 32, 13, 0, 18, 0, iron_hi)
        hi, mid, lo = self.WARM
        rect(px, 32, 32, 11, 3, 20, 10, mid)
        rect(px, 32, 32, 13, 4, 18, 9, hi)
        for y in range(3, 11):
            px[11, y] = iron
            px[20, y] = iron
            px[15, y] = iron if y in (3, 10) else px[15, y]
        rect(px, 32, 32, 12, 11, 19, 11, iron)
        outline(img, self.OUT)
        halo(img, 16, 6.5, 13, (255, 200, 110), 64, 26)

    def t36(self, img):  # bench
        px = img.load()
        lt, hi, mid, dk = self.WOOD
        rect(px, 32, 32, 3, 13, 28, 14, hi)
        rect(px, 32, 32, 3, 16, 28, 17, mid)
        rect(px, 32, 32, 2, 21, 29, 22, hi)
        rect(px, 32, 32, 2, 23, 29, 23, dk)
        iron = (60, 54, 70, 255)
        for x in (5, 26):
            for y in range(12, 32):
                px[x, y] = iron
            px[x - 1, 31] = iron
            px[x + 1, 31] = iron
        outline(img, self.OUT)

    def t37(self, img):  # pumpkins
        px = img.load()
        for (cx, cy, r) in [(10, 25, 6), (22, 27, 4.5)]:
            disc(px, 32, 32, cx, cy, r, lambda dx, dy, d: (255, 170, 80, 255) if (dx < -1 and dy < -1) else ((236, 120, 50, 255) if (int(dx + 20) % 3) else (190, 86, 40, 255)))
            px[int(cx), int(cy - r) - 1] = (100, 120, 60, 255)
            px[int(cx), int(cy - r) - 2] = (100, 120, 60, 255)
        for i in range(8):
            px[13 + i, 31 - (i % 3 == 0)] = self.lip[1]
        px[17, 29] = self.lip[0]
        px[18, 29] = self.lip[1]
        outline(img, self.OUT)

    # -- backgrounds ---------------------------------------------------------
    def sky(self):
        W, H = SW, SH
        img = Image.new("RGBA", (W, H))
        vgrad(img, [(0, (34, 34, 84)), (100, (82, 60, 126)), (190, (168, 96, 140)), (270, (238, 146, 120)), (330, (255, 194, 138)), (359, (255, 214, 160))], 12)
        px = img.load()
        for i in range(80):
            x = int(hsh(i, 1, 681) * W)
            y = int(hsh(i, 2, 681) * 120 * hsh(i, 3, 681) ** 0.5)
            px[x, y] = (255, 250, 230, 255) if i % 3 else (200, 190, 240, 255)
        sparkle(px, W, H, 470, 54, (255, 255, 255, 255), (255, 230, 200, 255), big=True)
        mx, my = 96, 62
        for y in range(my - 12, my + 13):
            for x in range(mx - 12, mx + 13):
                if math.hypot(x + 0.5 - mx, y + 0.5 - my) <= 11 and math.hypot(x + 0.5 - mx - 5, y + 0.5 - my + 3) > 10:
                    px[x, y] = (255, 246, 214, 255)
        for (cx, cy, rw, rh) in [(140, 170, 100, 8), (260, 190, 70, 6), (430, 160, 120, 9), (590, 205, 80, 6), (40, 220, 70, 5)]:
            for y in range(cy - rh, cy + rh + 1):
                for x in range(cx - rw, cx + rw + 1):
                    if 0 <= x < W:
                        v = ((x - cx) / rw) ** 2 + ((y - cy) / rh) ** 2
                        if v <= 1:
                            under = (y - cy) > rh * 0.2
                            c = (255, 176, 140) if under else (196, 120, 160)
                            if bayer(x, y) < (0.85 if v < 0.5 else 0.4):
                                px[x, y] = A(mix(px[x, y], c, 0.7), 255)
        return ("Village Sky", img)

    def backgrounds(self):
        return [("Village Far", self.bg_far(), 0.2), ("Village Houses", self.bg_houses(), 0.6)]

    # Spore pod anchors in Village Far (pixel centres): ART-SCENES' Lantern Tree Lights overlays light these.
    SPORE_PODS = [(456, 140), (486, 104), (514, 74), (544, 106), (572, 140)]

    def bg_far(self):
        W, H = BW, BH
        img = new(W, H)
        far = Mask(W, H)
        for x in range(W):
            far.vspan(x, 236 + pn(x, W, [(16, 2, 0.5), (9, 5, 1.9), (4, 11, 0.3)]), H)
        mask_paint(img, far.m, (120, 86, 146, 255), rim=(186, 132, 176, 255))
        near = Mask(W, H)
        for x in range(W):
            near.vspan(x, 268 + pn(x, W, [(14, 3, 2.2), (7, 7, 0.8)]), H)
        tree = Mask(W, H)
        cx = 512
        for y in range(110, 290):
            t = (y - 110) / 180
            hw = 14 + 10 * t + 34 * math.exp(-(280 - y) / 14.0)
            tree.hspan(y, cx - hw + 2 * math.sin(y / 11.0), cx + hw + 2 * math.sin(y / 13.0))
        for (sx, sy, ex, ey, r) in [(512, 170, 440, 150, 7), (512, 160, 590, 140, 7), (512, 140, 470, 110, 6), (512, 135, 560, 104, 6)]:
            tree.curve([(sx, sy), ((sx + ex) / 2, sy - 6), (ex, ey)], r, 3)
        for k in range(15):
            a = (k - 7) / 7.0
            r = 30 + hsh(k, 1, 691) * 16
            tree.disc(cx + a * 112, 112 - (1 - a * a) * 62 + hsh(k, 2, 691) * 14, r, r * 0.85)
        for k in range(8):
            a = (k - 3.5) / 3.5
            tree.disc(cx + a * 80, 60 - (1 - a * a) * 30 + hsh(k, 3, 691) * 10, 26, 22)
        tree.disc(cx, 112, 92, 46)   # solid crown core: no sky holes behind the lights
        mask_paint(img, tree.m, (64, 44, 92, 255), rim=(140, 100, 168, 255), rim_dark=(48, 32, 72, 255))
        mask_paint(img, near.m, (86, 60, 116, 255), rim=(150, 104, 160, 255))
        px = img.load()
        # unlit spore pods (the overlays put light here)
        for (x0, y0) in self.SPORE_PODS:
            for y in range(y0 - 5, y0 + 6):
                for x in range(x0 - 4, x0 + 5):
                    d = math.hypot((x + 0.5 - x0 - 0.5) / 4, (y + 0.5 - y0 - 0.5) / 5.5)
                    if d <= 1:
                        px[x, y] = (40, 26, 60, 255) if d > 0.78 else ((112, 92, 132, 255) if (x < x0 and y < y0) else (84, 66, 108, 255))
        # leaf texture on the tree crown
        for y in range(0, 230):
            for x in range(380, 650):
                p = px[x, y]
                if p[:3] == (64, 44, 92) and hsh(x // 3, y // 3, 692) < 0.18 and x % 3 == 0 and y % 3 == 1:
                    px[x, y] = (84, 60, 112, 255)
        # far village windows twinkling on the hills
        for i in range(36):
            x = int(hsh(i, 1, 693) * W)
            if abs(wrapd(x - 512, W)) < 70:
                continue
            y = int(near_y(x, W) + 6 + hsh(i, 2, 693) * 30)
            if y < H - 1:
                px[x, y] = (255, 214, 120, 255)
                px[(x + 1) % W, y] = (255, 190, 100, 255)
        return img

    def house(self, px, W, H, cx, base, w, hs, cap_r, caps, door_side=0, windows=2):
        cap, cap_hi, cap_dk = caps
        stem, stem_hi, stem_dk = (232, 214, 184, 255), (250, 238, 214, 255), (186, 162, 140, 255)
        top = base - hs
        for y in range(top, base + 1):
            t = (y - top) / max(1, hs)
            hw = w / 2 + 3 * t
            for x in range(int(cx - hw), int(cx + hw) + 1):
                u = (x + 0.5 - (cx - hw)) / (2 * hw)
                c = stem_hi if u < 0.25 else (stem_dk if u > 0.75 else stem)
                pset(px, W, H, x, y, c)
        # door
        dx0 = cx - 7 + door_side * w // 4
        for y in range(base - 22, base + 1):
            for x in range(int(dx0), int(dx0) + 14):
                if y < base - 15 and math.hypot(x + 0.5 - dx0 - 7, y + 0.5 - (base - 15)) > 7:
                    continue
                c = (126, 78, 52, 255) if (x - int(dx0)) % 4 else (92, 56, 40, 255)
                pset(px, W, H, x, y, c)
        pset(px, W, H, dx0 + 10, base - 10, (255, 214, 120, 255))
        # windows
        wins = [(cx - w // 4 - 2, top + hs * 0.4), (cx + w // 4 + 2, top + hs * 0.4)][:windows]
        if door_side != 0:
            wins = [(cx - door_side * w // 4, top + hs * 0.45)]
        for (wx, wy) in wins:
            for y in range(int(wy - 7), int(wy + 8)):
                for x in range(int(wx - 7), int(wx + 8)):
                    d = math.hypot(x + 0.5 - wx, y + 0.5 - wy)
                    if d <= 6.5:
                        if d > 5.2:
                            c = (110, 70, 50, 255)
                        elif abs(x + 0.5 - wx) < 0.8 or abs(y + 0.5 - wy) < 0.8:
                            c = (150, 96, 60, 255)
                        else:
                            c = self.WARM[0] if (x < wx and y < wy) else self.WARM[1]
                        pset(px, W, H, x, y, c)
        # cap
        cy = top + 4
        ry = cap_r * 0.62
        for y in range(int(cy - ry) - 1, int(cy) + 5):
            for x in range(int(cx - cap_r) - 2, int(cx + cap_r) + 3):
                dxn = (x + 0.5 - cx) / cap_r
                if y <= cy:
                    inside = dxn * dxn + ((y + 0.5 - cy) / ry) ** 2 <= 1
                else:
                    inside = abs(dxn) <= 1 - (y - cy) * 0.035
                if not inside:
                    continue
                k = -dxn * 0.6 - ((y - cy) / ry) * 0.8
                c = cap_hi if k > 0.75 else (cap_dk if (y > cy + 1 or dxn > 0.62) else cap)
                pset(px, W, H, x, y, c)
        for k in range(5):
            sx = cx + (hsh(int(cx), k, 694) - 0.5) * cap_r * 1.3
            sy = cy - ry * (0.2 + hsh(int(cx), k, 695) * 0.6)
            r = 2 + hsh(int(cx), k, 696) * 3
            for y in range(int(sy - r), int(sy + r) + 1):
                for x in range(int(sx - r), int(sx + r) + 1):
                    if math.hypot(x + 0.5 - sx, y + 0.5 - sy) <= r:
                        dxn = (x + 0.5 - cx) / cap_r
                        if dxn * dxn + ((y + 0.5 - cy) / ry) ** 2 <= 0.85:
                            pset(px, W, H, x, y, (250, 238, 214, 255) if y < sy + r * 0.4 else (214, 196, 170, 255))

    def bg_houses(self):
        W, H = BW, BH
        img = new(W, H)
        px = img.load()
        base = 304
        houses = [(70, 46, 78, 54, 0, 0, 2), (210, 40, 104, 48, 2, 1, 1), (330, 52, 70, 60, 1, 0, 2), (470, 38, 92, 44, 3, -1, 1),
                  (600, 50, 82, 58, 0, 0, 2), (730, 42, 112, 52, 2, 1, 1), (860, 48, 74, 56, 3, 0, 2), (970, 36, 90, 42, 1, -1, 1)]
        # chimneys first so caps overlap them
        for (cx, w, hs, cr, ci, ds, nw) in houses:
            chx = cx + cr * 0.45
            chy = base - hs - cr * 0.45
            for y in range(int(chy - 16), int(chy + 4)):
                for x in range(int(chx - 4), int(chx + 4)):
                    pset(px, W, H, x, y, (150, 120, 120, 255) if x < chx else (110, 86, 94, 255))
            for (k, (dx, dy, r)) in enumerate([(1, -22, 4), (4, -30, 5), (2, -40, 6), (6, -50, 6)]):
                for y in range(int(chy + dy - r), int(chy + dy + r) + 1):
                    for x in range(int(chx + dx - r), int(chx + dx + r) + 1):
                        if math.hypot(x + 0.5 - chx - dx, y + 0.5 - chy - dy) <= r and px[x % W, y][3] == 0:
                            pset(px, W, H, x, y, (220, 200, 220, 110 - k * 20))
        for (cx, w, hs, cr, ci, ds, nw) in houses:
            self.house(px, W, H, cx, base, w, hs, cr, self.CAPS[ci], ds, nw)
        # fences and hedges along the lane
        paint, paint_dk = (226, 214, 200, 255), (176, 160, 156, 255)
        for x in range(W):
            if px[x, base - 6][3] == 0:
                if x % 7 in (0, 1, 2):
                    for y in range(base - 14, base + 4):
                        pset(px, W, H, x, y, paint if x % 7 < 2 else paint_dk)
                for y in (base - 10, base - 3):
                    pset(px, W, H, x, y, paint)
        for i in range(40):
            cx = (i + hsh(i, 1, 697)) * W / 40
            r = 8 + hsh(i, 2, 697) * 8
            for y in range(int(base + 2 - r), H):
                for x in range(int(cx - r), int(cx + r) + 1):
                    if math.hypot(x + 0.5 - cx, max(0, base + 2 - y)) <= r:
                        k = (x + 0.5 - cx) / r
                        pset(px, W, H, x, y, self.lip[0] if (k < -0.3 and y < base) else (self.lip[1] if y < base + 8 else self.lip[2]))
        outline_wrap(img, self.OUT)
        # lamp posts with glow
        for lx in (140, 400, 666, 800):
            for y in range(base - 46, base + 4):
                pset(px, W, H, lx, y, (60, 54, 70, 255))
                pset(px, W, H, lx + 1, y, (40, 36, 50, 255))
            for y in range(base - 56, base - 45):
                for x in range(lx - 4, lx + 6):
                    pset(px, W, H, x, y, self.WARM[1] if 1 < (x - lx + 4) < 9 and base - 55 < y < base - 46 else (60, 54, 70, 255))
            for y in range(base - 76, base - 25):
                for x in range(lx - 26, lx + 28):
                    d = math.hypot(x + 0.5 - lx - 1, y + 0.5 - (base - 50))
                    if d < 25 and px[x % W, y][3] == 0:
                        pset(px, W, H, x, y, (255, 200, 120, 54 if d < 13 else 26))
        # window glow onto the walls
        for (cx, w, hs, cr, ci, ds, nw) in houses:
            glow(img, cx, base - hs * 0.55, w * 0.8, hs * 0.5, (255, 200, 120), 0.35)
        return img


def near_y(x, W):
    return 268 + pn(x, W, [(14, 3, 2.2), (7, 7, 0.8)])


LABEL = {0: "top", 1: "fill", 2: "break", 3: "pod", 4: "husk", 5: "capL", 6: "capR", 7: "hard",
         8: "colTL", 9: "colTR", 10: "colL", 11: "colR", 12: "platL", 13: "platM", 14: "platR", 15: "fill2",
         24: "edgeL", 25: "edgeR", 26: "ledgL", 27: "ledgM", 28: "ledgR", 29: "stem", 30: "surf", 31: "liq",
         32: "HAZ", 33: "block", 34: "wall", 35: "lamp", 38: "botm", 39: "blank"}


def contact_sheet(world, tiles, path):
    s = 3
    cell = TS * s
    pad = 14
    W = 8 * (cell + 2) + 2
    H = 5 * (cell + 2 + pad) + 2 + 18
    sheet = Image.new("RGBA", (W, H), (34, 34, 42, 255))
    d = ImageDraw.Draw(sheet)
    d.text((4, 3), "%s  (S = solid)" % world.tiles_name, fill=(255, 255, 255, 255))
    c0, c1 = world.checker
    for i in range(40):
        cx, cy = i % 8, i // 8
        x0 = 2 + cx * (cell + 2)
        y0 = 20 + cy * (cell + 2 + pad)
        bg = Image.new("RGBA", (cell, cell))
        bp = bg.load()
        for y in range(cell):
            for x in range(cell):
                bp[x, y] = c1 if ((x // 12 + y // 12) % 2) else c0
        t = tiles.crop((cx * TS, cy * TS, cx * TS + TS, cy * TS + TS)).resize((cell, cell), Image.NEAREST)
        bg.alpha_composite(t)
        sheet.paste(bg, (x0, y0 + pad))
        d.text((x0 + 2, y0 + 1), "%d%s %s" % (i, " S" if i in SOLID else "", LABEL.get(i, "")), fill=(255, 255, 255, 255))
    sheet.save(path)


# Mock level chunk used for the scene preview (same auto-tiling rules as level_gen.py)
MOCK = [
    "v.........................",
    "v...........l.............",
    "v.........................",
    "............B?BuS.........",
    "....LLL..............C....",
    "...................MMMM...",
    ".........GGGG.......||....",
    "..PP.....GGGG.......||....",
    ".tPP.f.m......b.c.F.||.r.s",
    "GGGGGGGG..GGGGGGG^^^GGGGGG",
    "GGGGGGGGwwGGGGGGGGGGGGDDGG",
    "GGGGGGGGWWGGGGGGGGGGGGDDGG",
]
MOCK_DECOR = {"t": 16, "f": 17, "m": 18, "b": 19, "n": 20, "s": 21, "v": 22, "r": 23, "|": 29,
              "l": 35, "F": 36, "c": 37, "w": 30, "W": 31, "^": 32, "B": 2, "?": 3, "u": 4, "S": 7,
              "C": 33, "D": 34}


def mock_cells():
    rows = len(MOCK)
    W = max(len(r) for r in MOCK)
    grid = [list(r.ljust(W, ".")) for r in MOCK]
    at = lambda r, c: grid[r][c] if 0 <= r < rows and 0 <= c < W else "."
    cells = []
    for c in range(W):
        for r in range(rows):
            ch = grid[r][c]
            if ch in ("G", "D"):
                g = lambda rr, cc: at(rr, cc) in ("G", "D")
                up, left, right, down = g(r - 1, c), g(r, c - 1), g(r, c + 1), g(r + 1, c)
                if r == rows - 1:
                    down = True
                if c == 0:
                    left = True
                if c == W - 1:
                    right = True
                if ch == "D" and up:
                    idx = 34
                elif not up:
                    idx = 0 if (left and right) or (not left and not right) else (5 if not left else 6)
                elif not left:
                    idx = 24
                elif not right:
                    idx = 25
                elif not down:
                    idx = 38
                else:
                    idx = 15 if (c * 7 + r * 13) % 11 == 0 else 1
                cells.append((c, r, idx))
            elif ch == "P":
                top = at(r - 1, c) != "P"
                run = 0
                k = c
                while at(r, k) == "P":
                    run += 1
                    k -= 1
                is_left = run % 2 == 1
                cells.append((c, r, (8 if is_left else 9) if top else (10 if is_left else 11)))
            elif ch in ("L", "M"):
                left, right = at(r, c - 1) == ch, at(r, c + 1) == ch
                b = 12 if ch == "L" else 26
                cells.append((c, r, b + 1 if left and right else (b if not left else b + 2)))
            elif ch in MOCK_DECOR:
                cells.append((c, r, MOCK_DECOR[ch]))
    return cells


def scene_preview(world, tiles, layers, sky, path, cam=48):
    W, H = 640, 360
    img = Image.new("RGBA", (W, H), A(world.bg_color, 255))
    if sky is not None:
        img.alpha_composite(sky.resize((W, H), Image.NEAREST))
    for (name, bg, par) in layers:
        off = int(-cam * par) % bg.size[0]
        for k in (-1, 0, 1):
            x = off + k * bg.size[0]
            if x < W and x + bg.size[0] > 0:
                tmp = Image.new("RGBA", (W, H), CLEAR)
                tmp.paste(bg, (x, 0))
                img.alpha_composite(tmp)
    for (c, r, idx) in mock_cells():
        x, y = c * 32 - cam, r * 32
        if -32 < x < W:
            t = tiles.crop(((idx % 8) * 32, (idx // 8) * 32, (idx % 8) * 32 + 32, (idx // 8) * 32 + 32))
            layer = Image.new("RGBA", (W, H), CLEAR)
            layer.paste(t, (x, y))
            img.alpha_composite(layer)
    img = img.resize((W * 2, H * 2), Image.NEAREST)
    img.save(path)


def bg_sheet(world, layers, sky, path):
    rows = ([("Sky (640x360, StretchView)", sky)] if sky is not None else []) + [
        ("%s (%dx%d, parallax %.2f)" % (n, b.size[0], b.size[1], p), b) for (n, b, p) in layers]
    H = sum(b.size[1] + 16 for (_, b) in rows) + 4
    W = max(BW + 256, 640)
    sheet = Image.new("RGBA", (W, H), (34, 34, 42, 255))
    d = ImageDraw.Draw(sheet)
    y = 2
    for (label, b) in rows:
        d.text((4, y), label + ("   | seam check ->" if b.size[0] == BW else ""), fill=(255, 255, 255, 255))
        y += 14
        chk = Image.new("RGBA", b.size)
        cp = chk.load()
        for yy in range(b.size[1]):
            for xx in range(b.size[0]):
                cp[xx, yy] = (120, 120, 132, 255) if ((xx // 16 + yy // 16) % 2) else (150, 150, 162, 255)
        chk.alpha_composite(b)
        sheet.paste(chk, (0, y))
        if b.size[0] == BW:
            seam = Image.new("RGBA", (256, b.size[1]))
            seam.paste(chk.crop((BW - 128, 0, BW, b.size[1])), (0, 0))
            seam.paste(chk.crop((0, 0, 128, b.size[1])), (128, 0))
            sheet.paste(seam, (BW, y))
        y += b.size[1] + 2
    sheet.save(path)


# ---------------------------------------------------------------------------
WORLDS = {"cavern": Cavern, "grotto": Grotto, "canopy": Canopy, "hollow": Hollow, "village": Village}


def lantern_check(sky, far):
    """Composite Village Far with ART-SCENES' Lantern Tree Lights 5 overlay (if present), 2x crop around the tree."""
    import json
    doc_path = wa.SPRITES / "Lantern Tree Lights 5.image.json"
    base = Image.new("RGBA", (BW, BH))
    base.paste(sky.resize((BW, BH), Image.NEAREST), (0, 0))
    base.alpha_composite(far)
    if doc_path.exists():
        doc = json.loads(doc_path.read_text(encoding="utf-8"))
        ov = Image.open(wa.SPRITES / doc["frames"][0]["source"]).convert("RGBA")
        base.alpha_composite(ov)
    base.crop((352, 0, 672, 300)).resize((640, 600), Image.NEAREST).save(PREVIEW_DIR / "worlds_village_lantern_check.png")


def run_world(cls):
    w = cls()
    tiles = w.tileset()
    wa.write_image(w.tiles_name, tiles, "tileset", (0, 0), collision=SOLID, tag_name=None, template_name="Meadow Tiles")
    sky = w.sky()
    layers = w.backgrounds()
    if sky is not None:
        wa.write_image(sky[0], sky[1], "background", (0, 0), template_name="Morning Sky")
    for (name, img, par) in layers:
        wa.write_image(name, img, "background", (0, 0), parallax=par, template_name="Far Hills")
    PREVIEW_DIR.mkdir(parents=True, exist_ok=True)
    contact_sheet(w, tiles, PREVIEW_DIR / ("worlds_%s_tiles.png" % w.key))
    bg_sheet(w, layers, sky[1] if sky else None, PREVIEW_DIR / ("worlds_%s_backgrounds.png" % w.key))
    scene_preview(w, tiles, layers, sky[1] if sky else None, PREVIEW_DIR / ("worlds_%s_scene.png" % w.key))
    if w.key == "village":
        lantern_check(sky[1], dict((n, b) for (n, b, p) in layers)["Village Far"])
    print("previews: worlds_%s_*.png" % w.key)


def main(argv):
    keys = [a.lower() for a in argv] or list(WORLDS)
    for k in keys:
        run_world(WORLDS[k])


if __name__ == "__main__":
    main(sys.argv[1:])
