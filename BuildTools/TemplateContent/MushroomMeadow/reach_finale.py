"""Reachability proof for Tidepool Grotto (level 3) and Duskmantle Hollow (level 5).

Reads the GENERATED rooms (Rooms/*.room.json), the tileset solidity list, the Current zone table
(Objects/Current/Create.pgsl), Duskmantle's arena constants and the Explorer's tuning
(hero_physics.py when present, else Objects/Explorer/Create.pgsl), then proves:

Tidepool Grotto (free 8-direction swimming, the whole room is water):
  * every node is a feet position on a 4 px lattice where Pip's 14 x 34 mask PLUS a margin on every
    side is clear of solid tiles and hazard tiles (so passages are wide enough with room to spare);
  * moves are 4 px steps in 8 directions, allowed only when some swim input (0 or 120 px/s in 8
    directions, no strokes) makes the net velocity (swim + current push) point within 22.5 degrees
    of the step at 15 px/s or more, or slides along a wall the current presses Pip against;
    blocks (prize boxes, breakable coral) are always solid, so nothing that can change can seal
    a passage;
  * the Glowspore is reachable from the start and from the checkpoint respawn without touching a
    hazard, and again with every Bloatfin / Driftjelly swept area as a solid obstacle (enemies can
    always be swum around, never need to be fought);
  * EVERY position Pip can physically reach (exact mask, urchins passable at the cost of a heart)
    can still reach the Glowspore: no trap, no current that pins Pip.

Duskmantle Hollow (platforming):
  * a frame-exact 60 fps re-implementation of the Explorer's ground/jump/fall step (coyote time,
    jump buffer, variable-jump cut, run/walk easing, tile sweeps, hazard probes) is run with a
    SAFETY MARGIN: jump speed scaled to 85% of the jump height and run speed scaled so a running
    jump covers 85% of its full length;
  * scripted input families (run-up length, walk/run, jump hold time, air control) from every
    standing position build a directed graph of standing positions; falling out of the room is a
    death (respawn), never a trap;
  * the nest (Duskmantle's trigger zone) is reachable from the start and the checkpoint, and every
    reachable standing position can still reach it; the Glowspore's spawn point is reachable from
    the nest floor by a jump.

Exit code 0 = all proofs hold. Run from anywhere: python reach_finale.py [--verbose]
"""
import json
import math
import os
import re
import sys
from collections import deque

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
ASSETS = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates", "Assets",
                      "TwoDShowcase", "Assets")
VERBOSE = "--verbose" in sys.argv
FAILS = []


def fail(msg):
    FAILS.append(msg)
    print("FAIL: " + msg)


def ok(msg):
    print("ok:   " + msg)


# ------------------------------------------------------------------ inputs
def hero_constants():
    consts = {}
    try:
        sys.path.insert(0, HERE)
        import hero_physics  # written by the MEADOW agent; exact constants
        for key in ("walkSpeed", "runSpeed", "jumpSpeed", "gravityAccel", "swimSpeed", "maxFall",
                    "coyote", "jumpBuffer", "jumpCut"):
            for name in (key, key.upper(), re.sub(r"([A-Z])", r"_\1", key).upper()):
                if hasattr(hero_physics, name):
                    consts[key] = float(getattr(hero_physics, name))
                    break
        source = "hero_physics.py"
    except Exception:
        source = "Explorer Create.pgsl"
    text = open(os.path.join(ASSETS, "Objects", "Explorer", "Create.pgsl"), encoding="utf-8").read()
    for key in ("walkSpeed", "runSpeed", "jumpSpeed", "gravityAccel", "swimSpeed", "bounceSpeed"):
        if key not in consts:
            m = re.search(r"\b%s\s*=\s*([0-9.]+)" % key, text)
            consts[key] = float(m.group(1))
    step = open(os.path.join(ASSETS, "Objects", "Explorer", "Step.pgsl"), encoding="utf-8").read()
    # Values that live in Step: max fall, coyote, buffer, variable-jump cut. Read them so a retune is seen.
    m = re.search(r"vy = Min\(vy \+ gravityAccel \* dt, ([0-9.]+)\)", step)
    consts.setdefault("maxFall", float(m.group(1)) if m else 680.0)
    m = re.search(r"grounded = 1; coyote = ([0-9.]+);", step)
    consts.setdefault("coyote", float(m.group(1)) if m else 0.10)
    m = re.search(r"jumpBuffer = ([0-9.]+); \}", step)
    consts.setdefault("jumpBuffer", float(m.group(1)) if m else 0.12)
    m = re.search(r"!jumpHeld && vy < -([0-9.]+)\) \{ vy = -", step)
    consts.setdefault("jumpCut", float(m.group(1)) if m else 220.0)
    return consts, source


def load_room(name):
    return json.load(open(os.path.join(ASSETS, "Rooms", name + ".room.json"), encoding="utf-8"))


def tile_layers(room):
    layers = {}
    solid_ids = None
    for node in room["nodes"]:
        if node["kind"] != "TileLayer":
            continue
        tl = node["tileLayer"]
        if solid_ids is None:
            img = json.load(open(os.path.join(os.path.dirname(ASSETS), tl["tileset"].replace("/", os.sep)),
                                 encoding="utf-8"))
            solid_ids = set(img["usage"]["tileset"]["collision"])
        layers[node["name"]] = (tl, {(c["x"], c["y"]): c["tileY"] * 8 + c["tileX"] for c in tl["cells"]})
    solid = set()
    for name in ("World", "Blocks"):
        tl, cells = layers[name]
        if tl["collisionEnabled"]:
            solid |= {cell for cell, idx in cells.items() if idx in solid_ids}
    hazards = {cell for cell, idx in layers["Hazards"][1].items() if idx == 32}
    blocks = dict(layers["Blocks"][1])
    return solid, hazards, blocks


def objects(room):
    out = []
    for node in room["nodes"]:
        if node["kind"] == "GameObject":
            prefab = node["gameObject"]["prefab"].split("/")[-1].replace(".object.json", "")
            p = node["transform"]["position"]
            out.append((prefab, node["name"], p[0], p[1]))
    return out


def first(objs, prefab):
    hits = [o for o in objs if o[0] == prefab]
    return hits[0] if hits else None


# ------------------------------------------------------------------ Tidepool Grotto
MARGIN = 3          # px of clearance required on every side of Pip's mask while swimming
STEP = 4            # lattice step (px)
DIRS = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)]


def current_zones():
    text = open(os.path.join(ASSETS, "Objects", "Current", "Create.pgsl"), encoding="utf-8").read()
    zones = []
    for m in re.finditer(r"Abs\(x - (-?\d+)\) < 2 && Abs\(y - (-?\d+)\) < 2\) \{ zoneW = (\d+); zoneH = (\d+); "
                         r"forceX = (-?\d+); forceY = (-?\d+);", text):
        x, y, w, h, fx, fy = (int(v) for v in m.groups())
        zones.append((x, y, w, h, fx, fy))
    return zones


def swim_options(swim):
    """Net swim velocities Pip can choose (no strokes): rest or 8 directions at swimSpeed."""
    opts = [(0.0, 0.0)]
    for dx, dy in DIRS:
        n = math.hypot(dx, dy)
        opts.append((dx / n * swim, dy / n * swim))
    return opts


def grotto(consts):
    print("\n== Tidepool Grotto (swimming)")
    room = load_room("Tidepool Grotto")
    W, H = room["settings"]["width"], room["settings"]["height"]
    solid, hazards, blocks = tile_layers(room)
    objs = objects(room)
    zones = current_zones()
    swim = consts["swimSpeed"]
    opts = swim_options(swim)
    hero = first(objs, "Explorer")
    glow = first(objs, "Glowspore")
    flags = [o for o in objs if o[0] == "Checkpoint"]
    if not glow:
        fail("Grotto has no Glowspore")
        return
    cos_limit = math.cos(math.radians(22.5)) - 1e-9

    def make_free(cells, margin, extra_boxes=()):
        cache = {}

        def is_free(i, j):
            key = (i, j)
            hit = cache.get(key)
            if hit is not None:
                return hit
            x, y = i * STEP, j * STEP
            x0, x1 = x - 7 - margin, x + 7 + margin
            y0, y1 = y - 34 - margin, y + margin
            result = 8 <= x <= W - 8 and y0 >= 0 and y1 <= H
            if result:
                for c in range(int(x0 // 32), int((x1 - 0.001) // 32) + 1):
                    for r in range(int(y0 // 32), int((y1 - 0.001) // 32) + 1):
                        if (c, r) in cells:
                            result = False
            if result:
                for bx0, by0, bx1, by1 in extra_boxes:
                    if x0 < bx1 and x1 > bx0 and y0 < by1 and y1 > by0:
                        result = False
            cache[key] = result
            return result
        return is_free

    def force_at(i, j):
        x, cy = i * STEP, j * STEP - 17
        for zx, zy, zw, zh, fx, fy in zones:
            if zx < x < zx + zw and zy < cy < zy + zh:
                return fx, fy
        return 0.0, 0.0

    def can_move(i, j, k, is_free):
        """Some swim input moves Pip from (i, j) one step along DIRS[k]: the net velocity points
        along the step, or (cardinal steps) it has a 15 px/s component along the step and its
        sideways part pushes into a wall, so Pip slides along that wall (Current never pushes
        into a solid tile; TileMove stops at walls)."""
        fx, fy = force_at(i, j)
        dx, dy = DIRS[k]
        n = math.hypot(dx, dy)
        ux, uy = dx / n, dy / n
        for sx, sy in opts:
            vx, vy = sx + fx, sy + fy
            sp = math.hypot(vx, vy)
            if sp < 15:
                continue
            along = vx * ux + vy * uy
            if along / sp >= cos_limit:
                return True
            if dx and dy:
                continue
            if along >= 15:
                px, py = vx - along * ux, vy - along * uy
                side = (int(math.copysign(1, px)) if abs(px) > 1e-6 else 0,
                        int(math.copysign(1, py)) if abs(py) > 1e-6 else 0)
                if not is_free(i + side[0], j + side[1]):
                    return True
        return False

    def step_ok(i, j, k, is_free):
        dx, dy = DIRS[k]
        if not is_free(i + dx, j + dy):
            return False
        if dx and dy and not (is_free(i + dx, j) or is_free(i, j + dy)):
            return False   # no corner cutting through a diagonal pinch
        return can_move(i, j, k, is_free)

    def nearest(is_free, x, y):
        best = None
        for dj in range(-6, 7):
            for di in range(-6, 7):
                i, j = int(round(x / STEP)) + di, int(round(y / STEP)) + dj
                if is_free(i, j) and (best is None or di * di + dj * dj < best[0]):
                    best = (di * di + dj * dj, (i, j))
        return best[1] if best else None

    def goal_set(is_free):
        gx, gy = glow[2], glow[3]
        goal = set()
        for j in range(int((gy - 26 + 17) // STEP) - 1, int((gy + 26 + 17) // STEP) + 2):
            for i in range(int((gx - 26) // STEP) - 1, int((gx + 26) // STEP) + 2):
                if math.hypot(i * STEP - gx, j * STEP - 17 - gy) < 26 and is_free(i, j):
                    goal.add((i, j))
        return goal

    def forward(is_free, start):
        seen = {start}
        q = deque([start])
        while q:
            i, j = q.popleft()
            for k, (dx, dy) in enumerate(DIRS):
                n = (i + dx, j + dy)
                if n not in seen and step_ok(i, j, k, is_free):
                    seen.add(n)
                    q.append(n)
        return seen

    def backward(is_free, goal, within):
        back = set(goal)
        q = deque(goal)
        while q:
            i, j = q.popleft()
            for k, (dx, dy) in enumerate(DIRS):
                p = (i - dx, j - dy)
                if p in back or p not in within:
                    continue
                if step_ok(p[0], p[1], k, is_free):
                    back.add(p)
                    q.append(p)
        return back

    starts = [("start", hero[2], hero[3])] + [("checkpoint %s" % f[1], f[2] + 24, f[3]) for f in flags]

    def require_path(is_free, label):
        goal = goal_set(is_free)
        if not goal:
            fail("%s: no clear position within reach of the Glowspore" % label)
            return
        for name, sx, sy in starts:
            s = nearest(is_free, sx, sy)
            if s is None:
                fail("%s: the %s (%d,%d) is not clear" % (label, name, sx, sy))
                continue
            seen = forward(is_free, s)
            if seen & goal:
                ok("%s: Glowspore reachable from the %s (%d positions)" % (label, name, len(seen)))
            else:
                far = max(seen)
                fail("%s: Glowspore NOT reachable from the %s (furthest x %d)" % (label, name, far[0] * STEP))

    # 1. A hazard-free path with MARGIN px of clearance all round, every block solid.
    strict = make_free(solid | hazards, MARGIN)
    require_path(strict, "Grotto path (mask + %d px clearance, no hazard touched, blocks solid)" % MARGIN)
    # 2. The same with every enemy's whole swept area solid: no enemy ever has to be fought or passed.
    boxes = []
    for prefab, name, x, y in objs:
        if prefab == "Bloatfin":
            boxes.append((x - 80 - 10, y - 6 - 8, x + 80 + 10, y + 6 + 8))
        elif prefab == "Driftjelly":
            boxes.append((x - 18 - 8, y - 40 - 12, x + 18 + 8, y + 40 + 8))
    require_path(make_free(solid | hazards, MARGIN, tuple(boxes)),
                 "Grotto path around every Bloatfin / Driftjelly swept area")
    # 3. No trap: every position Pip can physically reach (exact mask, hazards passable at the cost
    #    of a heart: a hit is never a soft-lock) can still reach the Glowspore.
    phys = make_free(solid, 0)
    union = set()
    for name, sx, sy in starts:
        s = nearest(phys, sx, sy)
        if s is not None:
            union |= forward(phys, s)
    back = backward(phys, goal_set(phys), union)
    traps = union - back
    if traps:
        cells = sorted({(int(i * STEP // 32), int((j * STEP - 17) // 32)) for i, j in traps})
        fail("Grotto: %d reachable positions cannot reach the Glowspore (trap); chest cells %s" % (len(traps), cells[:40]))
    else:
        ok("Grotto: all %d physically reachable positions can still reach the Glowspore (no traps, no pinning current)" % len(union))
    # 4. Information: places where a current makes an urchin hit unavoidable (not a soft-lock).
    hazard_free = make_free(solid | hazards, 0)
    safe_back = backward(hazard_free, goal_set(hazard_free), {p for p in union if hazard_free(*p)})
    forced = {p for p in union if hazard_free(*p)} - safe_back
    if forced:
        cells = sorted({(int(i * STEP // 32), int((j * STEP - 17) // 32)) for i, j in forced})
        print("note: %d positions (%.2f%% of the reachable water) can only go on by touching an urchin "
              "(a hit, then invulnerability); chest cells %s" % (len(forced), 100.0 * len(forced) / len(union), cells))
    for prefab, name, x, y in objs:
        if prefab == "Big Sun Coin":
            hit = any(abs(i * STEP - x) < 7 + 12 and j * STEP - 34 < y + 12 and j * STEP > y - 12 for i, j in union)
            print("info: %s at %d,%d %s without swim strokes" % (name, x, y, "reachable" if hit else "NOT reachable"))
    for zx, zy, zw, zh, fx, fy in zones:
        if VERBOSE:
            print("   current %d,%d %dx%d force %d,%d" % (zx, zy, zw, zh, fx, fy))
    # 5. Blocks: only prize boxes (3 -> used 4, still solid) and breakable stones (removed) live on the
    #    Blocks layer, so blocks can only ever open space, never close it. Proof 1 kept them all solid.
    kinds = sorted(set(blocks.values()))
    if set(kinds) <= {2, 3}:
        ok("Grotto: the Blocks layer holds only prize boxes / breakable stones %s (can only open space)" % kinds)
    else:
        fail("Grotto: unexpected Blocks tiles %s" % kinds)




# ------------------------------------------------------------------ Duskmantle Hollow
DT = 1.0 / 60.0


class Platformer:
    def __init__(self, solid, hazards, width, height, consts, margin):
        self.solid = solid
        self.hazards = hazards
        self.W, self.H = width, height
        k = margin
        self.jump = consts["jumpSpeed"] * math.sqrt(k)
        self.g = consts["gravityAccel"]
        full_t = 2 * consts["jumpSpeed"] / self.g
        cut_t = 2 * self.jump / self.g
        # run speed chosen so a full running jump covers `margin` of the real length
        self.run = consts["runSpeed"] * k * full_t / cut_t
        self.walk = consts["walkSpeed"] * self.run / consts["runSpeed"]
        self.maxfall = consts["maxFall"]
        self.coyote_t = consts["coyote"]
        self.cut = consts["jumpCut"] * math.sqrt(k)

    def hits(self, x, y):
        x0, x1, y0, y1 = x - 7, x + 7, y - 34, y
        for c in range(int(math.floor(x0 / 32)), int(math.floor((x1 - 1e-6) / 32)) + 1):
            for r in range(int(math.floor(y0 / 32)), int(math.floor((y1 - 1e-6) / 32)) + 1):
                if (c, r) in self.solid:
                    return True
        return False

    def sweep_x(self, x, y, d):
        if d == 0 or not self.hits(x + d, y):
            return x + d
        # flush against the first solid column
        if d > 0:
            nx = x + d
            edge = math.floor((nx + 7) / 32) * 32
            cand = edge - 7
            while cand > x and self.hits(cand, y):
                cand -= 32
            return max(x, cand) if not self.hits(max(x, cand), y) else x
        nx = x + d
        edge = (math.floor((nx - 7) / 32) + 1) * 32
        cand = edge + 7
        while cand < x and self.hits(cand, y):
            cand += 32
        return min(x, cand) if not self.hits(min(x, cand), y) else x

    def sweep_y(self, x, y, d):
        if d == 0 or not self.hits(x, y + d):
            return y + d, False
        if d > 0:
            ny = y + d
            cand = math.floor(ny / 32) * 32
            while cand > y and self.hits(x, cand):
                cand -= 32
            return (max(y, cand) if not self.hits(x, max(y, cand)) else y), True
        ny = y + d
        cand = (math.floor((ny - 34) / 32) + 1) * 32 + 34
        while cand < y and self.hits(x, cand):
            cand += 32
        return (min(y, cand) if not self.hits(x, min(y, cand)) else y), True

    def grounded(self, x, y):
        return self.hits(x, y + 1)

    def thorn(self, x, y):
        for px, py in ((x, y - 4), (x - 6, y - 4), (x + 6, y - 4), (x, y - 20)):
            if (int(math.floor(px / 32)), int(math.floor(py / 32))) in self.hazards:
                return True
        return False

    def run_script(self, x, y, d, run, runup, hold, air):
        """Start standing at rest. Hold direction d (run or walk) for `runup` frames on the ground,
        press jump (hold for `hold` frames; hold 0 = walk off, no jump), air control `air`:
        'hold' keep d, 'stop' release d at the apex, 'back' reverse at the apex.
        Returns ('land', x, y) / ('dead',) / ('hurt',) / None (time out)."""
        vx = vy = 0.0
        coyote = 0.0
        buffer = 0.0
        speed = self.run if run else self.walk
        frames = 0
        airborne = False
        apex = False
        while frames < 400:
            frames += 1
            # Same order as Explorer Step: ground test, coyote, buffer, jump, variable cut, x, gravity, y.
            g = self.grounded(x, y)
            coyote = self.coyote_t if g else max(0.0, coyote - DT)
            buffer = max(0.0, buffer - DT)
            if hold > 0 and frames == runup + 1:
                buffer = 0.12
            jump_held = hold > 0 and runup < frames <= runup + hold
            if buffer > 0 and coyote > 0:
                vy = -self.jump
                coyote = 0
                buffer = 0
            if not jump_held and vy < -self.cut:
                vy = -self.cut
            if airborne and vy >= 0:
                apex = True
            if hold == 0 and frames > runup + 120 and not airborne:
                return None   # walked into a wall without leaving the ground
            dirn = d
            if apex and air == "stop":
                dirn = 0
            elif apex and air == "back":
                dirn = -d
            target = dirn * speed
            vx = vx + (target - vx) * min(1.0, DT * 16)
            nxp = self.sweep_x(x, y, vx * DT)
            if abs(nxp - (x + vx * DT)) > 0.001:
                vx = 0
            x = min(max(nxp, 8), self.W - 8)
            vy = min(vy + self.g * DT, self.maxfall)
            ny, blocked = self.sweep_y(x, y, vy * DT)
            y = ny
            if blocked:
                vy = 0
            if y > self.H + 64:
                return ("dead",)
            if self.thorn(x, y):
                return ("hurt",)
            g2 = self.grounded(x, y)
            if not g2:
                airborne = True
            elif airborne and vy >= 0:
                return ("land", x, y)
        return None


def hollow(consts):
    print("\n== Duskmantle Hollow (platforming)")
    room = load_room("Duskmantle Hollow")
    W, H = room["settings"]["width"], room["settings"]["height"]
    solid, hazards, blocks = tile_layers(room)
    objs = objects(room)
    text = open(os.path.join(ASSETS, "Objects", "Duskmantle", "Create.pgsl"), encoding="utf-8").read()
    m = re.search(r"arenaL = (\d+); arenaR = (\d+); floorY = (\d+);", text)
    arenaL, arenaR, floorY = (int(v) for v in m.groups())
    arenaCx = (arenaL + arenaR) / 2
    hero = first(objs, "Explorer")
    flags = [o for o in objs if o[0] == "Checkpoint"]
    # Duskmantle's landing spot (Step: dive target = Pip's x, clamped, moved off the platforms) must
    # leave a stompable stretch of her back: >= 22 px wide with 40 px of open space above it.
    step = open(os.path.join(ASSETS, "Objects", "Duskmantle", "Step.pgsl"), encoding="utf-8").read()
    rules = [tuple(int(v) for v in m.groups()) for m in re.finditer(
        r"if \(targetX > (\d+) && targetX < (\d+)\) \{ if \(targetX < (\d+)\) \{ targetX = (\d+); \} else \{ targetX = (\d+); \} \}", step)]
    worst = None
    for hx in range(arenaL, arenaR + 1, 2):
        t = min(max(hx, arenaL + 72), arenaR - 72)
        for lo, hi, mid, a, b in rules:
            if lo < t < hi:
                t = a if t < mid else b
        t = min(max(t, arenaL + 72), arenaR - 72)
        top = floorY - 32 - 28
        open_px = 0
        run = 0
        for px in range(int(t) - 36, int(t) + 36):
            covered = any((px // 32, r) in solid for r in range(int((top - 40) // 32), int(top // 32) + 1))
            run = 0 if covered else run + 1
            open_px = max(open_px, run)
        if worst is None or open_px < worst[0]:
            worst = (open_px, hx, t)
    if worst[0] >= 22:
        ok("Duskmantle: wherever Pip stands, she lands with >= %d px of her back open to a stomp (%d platform rules)" % (worst[0], len(rules)))
    else:
        fail("Duskmantle: with Pip at x %d she lands at %d with only %d px of her back open to a stomp" % (worst[1], worst[2], worst[0]))
    # Independent cross-check with MEADOW's hero_physics.reach (it also models the corner nudge and step-up).
    try:
        import hero_physics as hp
        grid = hp.RoomGrid.load("Rooms/Duskmantle Hollow.room.json")
        nest = (arenaL + 72, floorY - 120, arenaR, floorY)
        for name, sx, sy in [("start", hero[2], hero[3])] + [("checkpoint", f[2] + 24, f[3]) for f in flags]:
            res = hp.reach(grid, start=(sx, sy), goals=[nest])
            if res.ok():
                ok("Hollow (hero_physics.reach, design margin): nest reached from the %s, no soft-lock segments" % name)
            else:
                fail("Hollow (hero_physics.reach): " + res.report(name))
    except ImportError:
        print("note: hero_physics.py not present; cross-check skipped")
    for label, margin in (("85% margin", 0.85), ("real physics", 1.0)):
        P = Platformer(solid, hazards, W, H, consts, margin)
        print("-- %s: jump %.0f px/s (height %.1f px), run %.0f px/s, walk %.0f px/s" %
              (label, P.jump, P.jump ** 2 / (2 * P.g), P.run, P.walk))
        # Standing positions: every 4 px along every walkable surface.
        stands = set()
        for (c, r) in solid:
            if (c, r - 1) in solid:
                continue
            ytop = r * 32
            for x in range(c * 32 - 4, c * 32 + 36, 4):
                if 8 <= x <= W - 8 and not P.hits(x, ytop) and P.grounded(x, ytop) and not P.thorn(x, ytop):
                    stands.add((x, ytop))
        edges = {}
        scripts = []
        for d in (-1, 1):
            for run in (True, False):
                for runup in (0, 4, 8, 14, 22, 32):
                    for hold in (0, 4, 9, 15, 40):
                        for air in ("hold", "stop", "back"):
                            if hold == 0 and air != "hold":
                                continue
                            scripts.append((d, run, runup, hold, air))
        scripts.append((0, False, 0, 40, "hold"))

        def snap(x, y):
            x4 = int(round(x / 4.0)) * 4
            for cand in (x4, x4 - 4, x4 + 4):
                if (cand, int(round(y))) in stands:
                    return (cand, int(round(y)))
            return None

        def successors(s):
            if s in edges:
                return edges[s]
            out = set()
            for nxp in (s[0] - 4, s[0] + 4):   # walking along the same surface
                if (nxp, s[1]) in stands:
                    out.add((nxp, s[1]))
            for sc in scripts:
                res = P.run_script(s[0], s[1], *sc)
                if res and res[0] == "land":
                    t = snap(res[1], res[2])
                    if t and t != s:
                        out.add(t)
            edges[s] = out
            return out

        def in_nest(s):
            return s[0] > arenaL + 72 and s[0] < arenaR and s[1] > floorY - 120

        starts = [("start", hero[2], hero[3])] + [("checkpoint", f[2] + 24, f[3]) for f in flags]
        union = set()
        for name, sx, sy in starts:
            s = snap(sx, sy)
            if not s:
                fail("Hollow %s: %s %d,%d is not a standing position" % (label, name, sx, sy))
                continue
            seen = {s}
            q = deque([s])
            while q:
                cur = q.popleft()
                for n in successors(cur):
                    if n not in seen:
                        seen.add(n)
                        q.append(n)
            union |= seen
            if any(in_nest(v) for v in seen):
                ok("Hollow %s: the nest (boss trigger) is reachable from the %s (%d standing positions)" % (label, name, len(seen)))
            else:
                far = max(seen)
                fail("Hollow %s: the nest is NOT reachable from the %s; furthest standing position x=%d y=%d (col %d)" %
                     (label, name, far[0], far[1], far[0] // 32))
        # traps: reachable positions that cannot get to the nest
        good = {v for v in union if in_nest(v)}
        changed = True
        while changed:
            changed = False
            for v in union:
                if v not in good and successors(v) & good:
                    good.add(v)
                    changed = True
        traps = sorted(union - good)
        if traps:
            cols = sorted({(v[0] // 32, v[1] // 32) for v in traps})
            fail("Hollow %s: %d standing positions cannot reach the nest (soft-lock); cells %s" % (label, len(traps), cols))
        else:
            ok("Hollow %s: every reachable standing position can still reach the nest (no soft-locks)" % label)
        # nest: every floor position can reach every other (flat floor) and the Glowspore spawn
        nest = sorted(v for v in union if in_nest(v))
        gx, gy = arenaCx, floorY - 80
        best = None
        for sx in range(int(gx) - 40, int(gx) + 44, 4):
            if not P.grounded(sx, floorY):
                continue
            for hold in (4, 9, 15, 40):
                # straight-up jump, sample the chest path
                x, y, vy = float(sx), float(floorY), -P.jump
                held = 0
                for _ in range(120):
                    held += 1
                    if held > hold and vy < -P.cut:
                        vy = -P.cut
                    vy = min(vy + P.g * DT, P.maxfall)
                    y, blocked = P.sweep_y(x, y, vy * DT)
                    if blocked:
                        vy = 0
                    dist = min(math.hypot(x - gx, (y - 17) - (gy + bob)) for bob in (-4, 0, 4))
                    if best is None or dist < best:
                        best = dist
                    if P.grounded(x, y) and vy >= 0:
                        break
        if best is not None and best < 26:
            ok("Hollow %s: the Glowspore spawn (%d,%d) is reachable from the nest floor (closest chest distance %.1f px < 26)" % (label, gx, gy, best))
        else:
            fail("Hollow %s: the Glowspore spawn (%d,%d) is out of reach from the nest floor (closest %.1f px)" % (label, gx, gy, best or -1))
        # Dazed Duskmantle: her mask top is 28 px above her centre, she lands at floorY - 32.
        top = floorY - 32 - 28
        rise = P.jump ** 2 / (2 * P.g)
        if floorY - rise < top - 8:
            ok("Hollow %s: a jump from the nest floor rises %.0f px, above the dazed Duskmantle's back (%d px up)" % (label, rise, floorY - top))
        else:
            fail("Hollow %s: Pip cannot jump over the dazed Duskmantle" % label)
        if VERBOSE:
            print("   nest standing positions: %d, total standing %d" % (len(nest), len(stands)))


def main():
    consts, source = hero_constants()
    print("Hero constants (%s): %s" % (source, ", ".join("%s=%g" % kv for kv in sorted(consts.items()))))
    if "--hollow" not in sys.argv:
        grotto(consts)
    if "--grotto" not in sys.argv:
        hollow(consts)
    print()
    if FAILS:
        print("%d FAILURE(S)" % len(FAILS))
        sys.exit(1)
    print("ALL REACHABILITY PROOFS HOLD")


if __name__ == "__main__":
    main()
