"""Pip (Explorer) movement physics, mirrored from hero_gen.py, for reachability checks.

Every number here is the one Explorer Create/Step uses (hero_gen.py). If you change Explorer's
movement, change it here too (hero_gen.py asserts the key constants match this module).

Use it from a level checker (run from the repository root or this folder):

    import hero_physics as hp
    print(hp.MAX_JUMP_HEIGHT, hp.max_gap(0))          # 110 px, ~236 px (running)
    grid = hp.RoomGrid.load("Rooms/Tidepool Grotto.room.json")
    result = hp.reach(grid, start=(112, 288), goals=[grid.object_rect("Finish")])
    print(result.report())

What the simulator covers exactly (frame by frame at 60 fps, same order as Explorer Step):
coyote time, jump buffer, the variable-jump cut, walk/run acceleration, gravity and the fall cap,
tile sweeps (same flush/epsilon rules as RoomTileCollisionMap.Sweep), the ceiling corner nudge and
the ledge step-up, Bounce Mushrooms, Hazards-layer thorns (index 32) and falling out of the room.
Not simulated: enemies (assume they can be avoided or stomped), Moving Leaves, wind, water and
power-ups. A checker for a room with those adds them itself (e.g. treat a leaf's path as a
static platform at its worst position) and says what it assumed.

Hitbox: the "Gameplay mask" of every Explorer sprite is 14x34 at 9,6 with origin 16,40, so relative
to (x, y) = feet centre the box is x-7..x+7, y-34..y. image_xscale -1 mirrors it onto itself.
"""
import json
import math
import os

# ---------------------------------------------------------------- constants (Explorer Create / Step)
FPS = 60
DT = 1.0 / FPS
WALK_SPEED = 180.0          # walkSpeed
RUN_SPEED = 260.0           # runSpeed (Shift / pad B)
ACCEL_RATE = 16.0           # vx += (target - vx) * Min(1, dt * 16)
JUMP_SPEED = 525.0          # jumpSpeed
GRAVITY = 1250.0            # gravityAccel
MAX_FALL = 680.0            # vy = Min(vy + gravity * dt, 680)
JUMP_CUT = 220.0            # releasing jump while vy < -220 sets vy = -220
BOUNCE_SPEED = 760.0        # Bounce Mushroom (keeps full lift: no jump cut)
STOMP_BOUNCE = 340.0        # stomping an enemy
BEE_STOMP_BOUNCE = 380.0    # stomping a Buzz Bee
HURT_KNOCKBACK = (90.0, 260.0)
COYOTE = 0.10               # seconds after leaving ground that a jump still works
JUMP_BUFFER = 0.12          # seconds a jump press is remembered before landing
GLIDE_FALL = 75.0           # Glide Seed max fall speed (power-up: never required)
SWIM_SPEED = 120.0
SWIM_STROKE = 200.0
SWIM_MAX_UP = 280.0
SWIM_MAX_DOWN = 220.0
SWIM_EXIT_HOP = 420.0       # leaving water with jump held: vy = Min(vy, -420)
WIND_GRIP_GROUND = 0.35
WIND_DRAG = 2.2
CORNER_NUDGE = 5            # px: a rising head that clips a ceiling corner by <= 5 px slides past it
STEP_UP = 6                 # px: a wall whose top is <= 6 px above the feet is stepped onto
FALL_DEATH_BELOW = 64       # y > room height + 64 loses a life

# Hitbox relative to (x, y) = feet centre.
BOX_LEFT, BOX_TOP, BOX_RIGHT, BOX_BOTTOM = -7.0, -34.0, 7.0, 0.0
WIDTH, HEIGHT = 14.0, 34.0

TILE = 32
# Solid tile indices of every MM tileset (Usage.Tileset.Collision of Meadow/Cavern/Grotto/... Tiles).
SOLID_TILES = frozenset(list(range(0, 16)) + [24, 25, 26, 27, 28, 33, 34, 38])
HAZARD_TILE = 32
EPS = 1e-4

# Recommended design margin (brief: design for ~85% of the maximum).
MARGIN = 0.85


def _free_arc(vx0=0.0, direction=1, run=True, hold_frames=10 ** 6, jump=True, frames=240,
              jump_speed=JUMP_SPEED):
    """Free-space trajectory of the feet from a standing take-off at (0, 0): list of (x, y, vy)."""
    x = y = 0.0
    vx = vx0
    vy = -jump_speed if jump else 0.0
    out = []
    for f in range(frames):
        held = f < hold_frames
        if not held and vy < -JUMP_CUT:
            vy = -JUMP_CUT
        target = direction * (RUN_SPEED if run else WALK_SPEED)
        vx += (target - vx) * min(1.0, DT * ACCEL_RATE)
        x += vx * DT
        vy = min(vy + GRAVITY * DT, MAX_FALL)
        y += vy * DT
        out.append((x, y, vy))
    return out


def jump_height(hold_frames=10 ** 6, jump_speed=JUMP_SPEED):
    """Highest the feet rise (px, positive) for a jump held for hold_frames frames."""
    return -min(p[1] for p in _free_arc(0.0, 0, hold_frames=hold_frames, jump_speed=jump_speed))


MAX_JUMP_HEIGHT = jump_height()                     # ~108 px (discrete 60 fps)
MIN_JUMP_HEIGHT = jump_height(hold_frames=1)        # tap
BOUNCE_HEIGHT = jump_height(jump_speed=BOUNCE_SPEED)
DESIGN_JUMP_HEIGHT = MAX_JUMP_HEIGHT * MARGIN


def max_gap(rise=0.0, run=True, vx0=None):
    """Widest gap (edge to edge, px) Pip clears jumping to a ledge `rise` px higher (negative =
    lower), starting at full speed (vx0 defaults to run/walk speed). Uses the 14 px hitbox: Pip
    can take off while his box still overlaps the ledge and land with 1 px of box on the far one."""
    speed = RUN_SPEED if run else WALK_SPEED
    arc = _free_arc(speed if vx0 is None else vx0, 1, run=run)
    best = -1.0
    prev_y = 0.0
    for x, y, vy in arc:
        if vy > 0 and prev_y <= -rise <= y:
            best = x
            break
        prev_y = y
    if best < 0:
        return 0.0
    return max(0.0, best + WIDTH - 2.0)


def design_gap(rise=0.0, run=True):
    """max_gap with the design margin applied to both height and length."""
    return max_gap(rise / MARGIN, run) * MARGIN


def envelope(run=True):
    """Reachable feet offsets of a full running jump: list of (dx, dy) samples per frame."""
    return [(p[0], p[1]) for p in _free_arc(RUN_SPEED if run else 0.0, 1, run=run)]


# ---------------------------------------------------------------- room grid
class RoomGrid:
    """Solid cells, hazard cells and objects of one generated room JSON."""

    def __init__(self, width, height, solid, hazard=None, springs=None, nodes=None, assets=None):
        self.width = width
        self.height = height
        self.solid = set(solid)
        self.hazard = set(hazard or ())
        self.springs = list(springs or ())   # (x, y) feet positions of Bounce Mushrooms
        self.nodes = nodes or []
        self.assets = assets

    @staticmethod
    def load(room_path, assets=None, removed_layers=(), removed_tiles=()):
        """Load a room. room_path may be relative to the template Assets folder.
        removed_layers: tile layer names whose cells are treated as gone (e.g. "Blocks").
        removed_tiles: {(layer name, tile index)} treated as gone (e.g. breakable stones)."""
        if assets is None:
            here = os.path.dirname(os.path.abspath(__file__))
            assets = os.path.normpath(os.path.join(here, "..", "..", "..", "Source", "Genesis.Application.Core",
                                                   "Projects", "Templates", "Assets", "TwoDShowcase", "Assets"))
        path = room_path if os.path.isabs(room_path) else os.path.join(assets, room_path)
        with open(path, encoding="utf-8") as handle:
            room = json.load(handle)
        solid, hazard, springs = set(), set(), []
        removed_tiles = set(removed_tiles)
        tilesets = {}
        for node in room["nodes"]:
            if node.get("kind") != "TileLayer" or not node.get("enabled", True):
                continue
            layer = node["tileLayer"]
            ts = layer["tileset"]
            if ts not in tilesets:
                tilesets[ts] = _tileset_info(assets, ts)
            columns, collision = tilesets[ts]
            name = node.get("name", "")
            for cell in layer["cells"]:
                index = cell["tileY"] * columns + cell["tileX"]
                key = (cell["x"], cell["y"])
                if name.lower() == "hazards" and index == HAZARD_TILE:
                    hazard.add(key)
                if not layer.get("collisionEnabled"):
                    continue
                if name in removed_layers or (name, index) in removed_tiles:
                    continue
                if collision is None or index in collision:
                    solid.add(key)
        nodes = [n for n in room["nodes"] if n.get("kind") == "GameObject" and n.get("enabled", True)]
        for n in nodes:
            if n["gameObject"]["prefab"].endswith("/Bounce Mushroom.object.json"):
                p = n["transform"]["position"]
                springs.append((p[0], p[1]))
        s = room["settings"]
        return RoomGrid(s["width"], s["height"], solid, hazard, springs, nodes, assets)

    def copy(self, solid=None, hazard=None, springs=None):
        """A variant of this room (e.g. hazard=set() to test climbing out of a thorn trench)."""
        return RoomGrid(self.width, self.height, self.solid if solid is None else solid,
                        self.hazard if hazard is None else hazard,
                        self.springs if springs is None else springs, self.nodes, self.assets)

    def objects(self, prefab):
        """Instances of an object (by prefab name), as (name, x, y)."""
        out = []
        for n in self.nodes:
            if os.path.basename(n["gameObject"]["prefab"]) == prefab + ".object.json":
                p = n["transform"]["position"]
                out.append((n["name"], p[0], p[1]))
        return out

    def object_rect(self, prefab, index=0):
        """World rectangle (l, t, r, b) of an instance's sprite Gameplay mask."""
        name, x, y = self.objects(prefab)[index]
        with open(os.path.join(self.assets, "Objects", prefab + ".object.json"), encoding="utf-8") as handle:
            sprite = json.load(handle)["sprite"]
        l, t, r, b = sprite_mask(self.assets, sprite)
        return (x + l, y + t, x + r, y + b)

    # -- collision, same rules as RoomTileCollisionMap (strict overlap, flush sweeps)
    def box_hits(self, l, t, r, b):
        solid = self.solid
        for c in range(int(math.floor(l / TILE)), int(math.ceil(r / TILE))):
            for rr in range(int(math.floor(t / TILE)), int(math.ceil(b / TILE))):
                if (c, rr) in solid:
                    return True
        return False

    def body_hits(self, x, y):
        return self.box_hits(x + BOX_LEFT, y + BOX_TOP, x + BOX_RIGHT, y + BOX_BOTTOM)

    def on_ground(self, x, y):
        return self.body_hits(x, y + 1)

    def sweep_x(self, x, y, amount):
        if amount == 0:
            return x
        l, t, r, b = x + BOX_LEFT, y + BOX_TOP, x + BOX_RIGHT, y + BOX_BOTTOM
        r0, r1 = int(math.floor((t + EPS) / TILE)), int(math.ceil((b - EPS) / TILE))
        permitted = amount
        if amount > 0:
            for c in range(int(math.floor((r - EPS) / TILE)), int(math.floor((r + amount) / TILE)) + 1):
                if c * TILE < r - EPS:
                    continue
                if any((c, rr) in self.solid for rr in range(r0, r1)):
                    permitted = min(permitted, max(0.0, c * TILE - r))
        else:
            for c in range(int(math.floor((l + amount) / TILE)), int(math.floor((l + EPS) / TILE)) + 1):
                if (c + 1) * TILE > l + EPS:
                    continue
                if any((c, rr) in self.solid for rr in range(r0, r1)):
                    permitted = max(permitted, min(0.0, (c + 1) * TILE - l))
        return x + permitted

    def sweep_y(self, x, y, amount):
        if amount == 0:
            return y
        l, t, r, b = x + BOX_LEFT, y + BOX_TOP, x + BOX_RIGHT, y + BOX_BOTTOM
        c0, c1 = int(math.floor((l + EPS) / TILE)), int(math.ceil((r - EPS) / TILE))
        permitted = amount
        if amount > 0:
            for rr in range(int(math.floor((b - EPS) / TILE)), int(math.floor((b + amount) / TILE)) + 1):
                if rr * TILE < b - EPS:
                    continue
                if any((c, rr) in self.solid for c in range(c0, c1)):
                    permitted = min(permitted, max(0.0, rr * TILE - b))
        else:
            for rr in range(int(math.floor((t + amount) / TILE)), int(math.floor((t + EPS) / TILE)) + 1):
                if (rr + 1) * TILE > t + EPS:
                    continue
                if any((c, rr) in self.solid for c in range(c0, c1)):
                    permitted = max(permitted, min(0.0, (rr + 1) * TILE - t))
        return y + permitted

    def in_hazard(self, x, y):
        hz = self.hazard
        if not hz:
            return False
        for px, py in ((x, y - 4), (x - 6, y - 4), (x + 6, y - 4), (x, y - 20)):
            if (int(math.floor(px / TILE)), int(math.floor(py / TILE))) in hz:
                return True
        return False


def _tileset_info(assets, tileset):
    try:
        with open(os.path.join(assets, "..", tileset) if not tileset.startswith("Assets/")
                  else os.path.join(os.path.dirname(assets), tileset), encoding="utf-8") as handle:
            data = json.load(handle)
        ts = data.get("usage", {}).get("tileset", {})
        return ts.get("columns", 8), set(ts.get("collision", SOLID_TILES))
    except OSError:
        return 8, set(SOLID_TILES)


def sprite_mask(assets, sprite):
    """(l, t, r, b) of a sprite's first collision rectangle relative to its origin."""
    with open(os.path.join(os.path.dirname(assets), sprite), encoding="utf-8") as handle:
        data = json.load(handle)
    o = data["origin"]
    shape = data["collisionShapes"][0]
    l = shape["position"]["x"] - o["x"]
    t = shape["position"]["y"] - o["y"]
    return (l, t, l + shape["size"]["x"], t + shape["size"]["y"])


# ---------------------------------------------------------------- one body, frame by frame
class Pip:
    """Explorer's non-swimming controller (Explorer Step), one frame per step()."""

    def __init__(self, grid, x, y, vx=0.0, vy=0.0, jump_scale=1.0, speed_scale=1.0):
        self.g = grid
        self.x, self.y, self.vx, self.vy = float(x), float(y), float(vx), float(vy)
        self.grounded = grid.on_ground(self.x, self.y)
        self.coyote = 0.0
        self.jump_buffer = 0.0
        self.bounce_lift = False
        self.jump_speed = JUMP_SPEED * jump_scale
        self.speed_scale = speed_scale
        self.hurt = False
        self.dead = False
        self.bonked = False

    def step(self, direction=0, run=False, held=False, pressed=False):
        g = self.g
        self.bonked = False
        self.grounded = g.on_ground(self.x, self.y)
        self.coyote = COYOTE if self.grounded else max(0.0, self.coyote - DT)
        self.jump_buffer = max(0.0, self.jump_buffer - DT)
        if pressed:
            self.jump_buffer = JUMP_BUFFER
        if self.jump_buffer > 0 and self.coyote > 0:
            self.vy = -self.jump_speed
            self.coyote = 0.0
            self.jump_buffer = 0.0
            self.bounce_lift = False
        if self.vy >= 0:
            self.bounce_lift = False
        if not self.bounce_lift and not held and self.vy < -JUMP_CUT:
            self.vy = -JUMP_CUT
        target = direction * (RUN_SPEED if run else WALK_SPEED) * self.speed_scale
        self.vx += (target - self.vx) * min(1.0, DT * ACCEL_RATE)
        want = self.x + self.vx * DT
        nx = g.sweep_x(self.x, self.y, self.vx * DT)
        if abs(nx - want) > 0.001:
            stepped = False
            if self.vy >= 0:
                for k in range(1, STEP_UP + 1):
                    if not g.body_hits(self.x, self.y - k):
                        sx = g.sweep_x(self.x, self.y - k, want - self.x)
                        if abs(sx - want) <= 0.001:
                            nx, self.y, stepped = sx, self.y - k, True
                            break
            if not stepped:
                self.vx = 0.0
        self.x = min(max(nx, 8.0), g.width - 8.0)
        old_y = self.y
        self.vy = min(self.vy + GRAVITY * DT, MAX_FALL)
        descending = self.vy > 0
        want = self.y + self.vy * DT
        ny = g.sweep_y(self.x, self.y, self.vy * DT)
        if abs(ny - want) > 0.001:
            nudged = False
            if self.vy < 0:
                for k in range(1, CORNER_NUDGE + 1):
                    for s in (k, -k):
                        if not g.body_hits(self.x + s, want) and not g.body_hits(self.x + s, self.y):
                            self.x += s
                            ny = want
                            nudged = True
                            break
                    if nudged:
                        break
                if not nudged:
                    self.bonked = True
            if not nudged:
                self.vy = 0.0
        self.y = ny
        for sx, sy in g.springs:
            if descending and old_y <= sy - 14 and self.x - 7 < sx + 14 and sx - 14 < self.x + 7 \
                    and self.y - 34 < sy and sy - 20 < self.y:
                self.y = sy - 20
                self.vy = -BOUNCE_SPEED
                self.bounce_lift = True
                break
        self.grounded = g.on_ground(self.x, self.y)
        if self.y > g.height + FALL_DEATH_BELOW:
            self.dead = True
        if g.in_hazard(self.x, self.y):
            self.hurt = True
        return self

    def box(self):
        return (self.x + BOX_LEFT, self.y + BOX_TOP, self.x + BOX_RIGHT, self.y + BOX_BOTTOM)


def rects_overlap(a, b):
    return a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]


# ---------------------------------------------------------------- segment graph reachability
class Segment:
    __slots__ = ("id", "y", "x0", "x1")

    def __init__(self, sid, y, x0, x1):
        self.id, self.y, self.x0, self.x1 = sid, y, x0, x1

    def __repr__(self):
        return "Seg%d(y=%d x=%d..%d, cols %d..%d)" % (self.id, self.y, self.x0, self.x1,
                                                     self.x0 // TILE, self.x1 // TILE)


def segments(grid):
    """Walkable floor spans: every integer x in [x0, x1] at feet height y is standable (body clear,
    ground under the box, no thorns), so Pip can walk the whole span."""
    out = []
    rows = int(math.ceil(grid.height / TILE)) + 1
    for r in range(0, rows + 1):
        y = r * TILE
        run_start = None
        for x in range(8, int(grid.width - 8) + 1):
            ok = grid.on_ground(x, y) and not grid.body_hits(x, y) and not grid.in_hazard(x, y)
            if ok and run_start is None:
                run_start = x
            if not ok and run_start is not None:
                out.append(Segment(len(out), y, run_start, x - 1))
                run_start = None
        if run_start is not None:
            out.append(Segment(len(out), y, run_start, int(grid.width - 8)))
    return out


def _runway_speeds(run):
    """distance travelled from rest -> speed, holding a direction (run or walk)."""
    table = []
    vx = d = 0.0
    target = RUN_SPEED if run else WALK_SPEED
    for _ in range(90):
        vx += (target - vx) * min(1.0, DT * ACCEL_RATE)
        d += vx * DT
        table.append((d, vx))
    return table


_RUNWAY = {True: _runway_speeds(True), False: _runway_speeds(False)}


def _speed_after(run, distance):
    best = 0.0
    for d, v in _RUNWAY[run]:
        if d <= distance:
            best = v
        else:
            break
    return best


class ReachResult:
    def __init__(self, segs, edges, start_seg, reach, goal_segs, coreach, goal_hit, notes):
        self.segments, self.edges, self.start = segs, edges, start_seg
        self.reachable, self.goal_segments, self.can_finish = reach, goal_segs, coreach
        self.goal_reached = goal_hit
        self.notes = notes

    @property
    def traps(self):
        """Reachable segments from which the goal can no longer be reached (soft-locks)."""
        return [s for s in self.segments if s.id in self.reachable and s.id not in self.can_finish]

    def ok(self):
        return self.goal_reached and not self.traps

    def report(self, label="room"):
        lines = ["%s: start %r, goal %s, reachable segments %d/%d, soft-lock segments %d"
                 % (label, self.start, "REACHED" if self.goal_reached else "NOT REACHED",
                    len(self.reachable), len(self.segments), len(self.traps))]
        for s in self.traps:
            lines.append("  trap: %r" % s)
        lines.extend("  note: " + n for n in self.notes)
        return "\n".join(lines)


def _maneuvers(direction, vx0_options, at_edge):
    """(direction, vx0, jump, hold_frames, steer) tuples. steer(frame, vy) -> direction."""
    out = []
    d = direction
    for vx0 in vx0_options:
        for hold in (10 ** 6, 7):
            out.append((d, vx0, True, hold, lambda f, vy, d=d: d))
            out.append((d, vx0, True, hold, lambda f, vy, d=d: d if vy < 0 else 0))
            if vx0 == 0:
                out.append((d, vx0, True, hold, lambda f, vy, d=d: 0 if f < 14 else d))
                out.append((d, vx0, True, hold, lambda f, vy, d=d: 0 if vy < 0 else d))
        if at_edge:
            out.append((d, vx0, False, 0, lambda f, vy, d=d: d))
            out.append((d, vx0, False, 0, lambda f, vy, d=d: d if f < 5 else -d))
            out.append((d, vx0, False, 0, lambda f, vy, d=d: d if f < 4 else 0))
    return out


def reach(grid, start, goals, jump_scale=None, speed_scale=None, step=8, max_frames=360,
          extra_takeoffs=None, verbose=False, touch=()):
    """Prove a room completable without power-ups.

    start: (x, y) feet position (spawn or checkpoint respawn).
    goals: list of (l, t, r, b) rectangles; touching any of them counts as finishing.
    touch: optional rectangles (pickups, Big Sun Coins); result.touched holds the indices of those
    some explored trajectory overlapped. result.bonked holds the (column, row) tiles Pip's head hit.
    jump_scale / speed_scale default to the design margin (85% jump height, 92% run speed), so a
    route found here has slack in the real game.
    Returns ReachResult; result.ok() means the goal is reachable and no reachable floor is a trap."""
    if jump_scale is None:
        jump_scale = math.sqrt(MARGIN)
    if speed_scale is None:
        speed_scale = 0.92
    segs = segments(grid)
    by_row = {}
    for s in segs:
        by_row.setdefault(s.y, []).append(s)

    def seg_at(x, y):
        for s in by_row.get(int(round(y)), ()):
            if s.x0 - 0.5 <= x <= s.x1 + 0.5:
                return s
        return None

    sx, sy = start
    start_seg = seg_at(sx, sy)
    notes = []
    if start_seg is None:
        p = Pip(grid, sx, sy)
        for _ in range(240):
            p.step()
            if p.grounded:
                break
        start_seg = seg_at(p.x, p.y)
        if start_seg is None:
            notes.append("start %r is not on a walkable floor" % (start,))
            return ReachResult(segs, {}, None, set(), set(), set(), False, notes)

    goal_segs = set()
    for s in segs:
        for gr in goals:
            if rects_overlap((s.x0 + BOX_LEFT, s.y + BOX_TOP, s.x1 + BOX_RIGHT, s.y), gr):
                goal_segs.add(s.id)

    def interesting(s, x):
        if x - s.x0 < 16 or s.x1 - x < 16:
            return True
        for o in segs:
            if o.id != s.id and o.y < s.y + 400 and o.y > s.y - 150 and o.x1 > x - 340 and o.x0 < x + 340:
                return True
        for gr in goals:
            if gr[2] > x - 340 and gr[0] < x + 340:
                return True
        for (sxp, syp) in grid.springs:
            if abs(sxp - x) < 340:
                return True
        return False

    edges = {}
    goal_from = set()
    touched, bonked = set(), set()

    def explore(s):
        out = set()
        xs = list(range(s.x0, s.x1 + 1, step))
        if xs[-1] != s.x1:
            xs.append(s.x1)
        if extra_takeoffs:
            xs.extend(x for x in extra_takeoffs.get(s.id, ()) if s.x0 <= x <= s.x1)
        for x in xs:
            if not interesting(s, x):
                continue
            for d in (-1, 1):
                behind = (x - s.x0) if d > 0 else (s.x1 - x)
                options = sorted({0.0, round(_speed_after(False, behind) * speed_scale, 3),
                                  round(_speed_after(True, behind) * speed_scale, 3)})
                at_edge = (d > 0 and x == s.x1) or (d < 0 and x == s.x0)
                for (dd, vx0, jump, hold, steer) in _maneuvers(d, options, at_edge):
                    p = Pip(grid, x, s.y, vx=dd * vx0, jump_scale=jump_scale, speed_scale=speed_scale)
                    run = vx0 > WALK_SPEED * speed_scale + 1 or vx0 == 0
                    left_ground = False
                    for f in range(max_frames):
                        sd = steer(f, p.vy)
                        p.step(sd, run, held=jump and f < hold, pressed=jump and f == 0)
                        if p.dead or p.hurt:
                            break
                        if p.bonked:
                            for px in (p.x, p.x - 6, p.x + 6):
                                cell = (int(math.floor(px / TILE)), int(math.floor((p.y - 35) / TILE)))
                                if cell in grid.solid:
                                    bonked.add(cell)
                                    break
                        if touch:
                            pb = p.box()
                            for ti, tr in enumerate(touch):
                                if ti not in touched and rects_overlap(pb, tr):
                                    touched.add(ti)
                        if goals and any(rects_overlap(p.box(), gr) for gr in goals):
                            out.add("GOAL")
                            break
                        if not p.grounded:
                            left_ground = True
                            continue
                        if not left_ground:
                            if not jump:
                                continue
                            break
                        t = seg_at(p.x, p.y)
                        if t is not None and t.id != s.id:
                            out.add(t.id)
                        break
        return out

    reach_set = {start_seg.id}
    frontier = [start_seg.id]
    goal_hit = start_seg.id in goal_segs
    while frontier:
        sid = frontier.pop()
        e = explore(segs[sid])
        if "GOAL" in e:
            goal_from.add(sid)
            e.discard("GOAL")
            goal_hit = True
        edges[sid] = e
        if verbose:
            print("  %r -> %s" % (segs[sid], sorted(e)))
        for t in e:
            if t not in reach_set:
                reach_set.add(t)
                frontier.append(t)
        if sid in goal_segs:
            goal_hit = True
    # Backwards: which reachable segments can still reach the goal.
    can = set(goal_from) | (goal_segs & reach_set)
    changed = True
    while changed:
        changed = False
        for sid, e in edges.items():
            if sid not in can and e & can:
                can.add(sid)
                changed = True
    result = ReachResult(segs, edges, start_seg, reach_set, goal_segs, can, goal_hit, notes)
    # Standing on a floor also touches what overlaps it.
    for sid in reach_set:
        s = segs[sid]
        for ti, tr in enumerate(touch):
            if rects_overlap((s.x0 + BOX_LEFT, s.y + BOX_TOP, s.x1 + BOX_RIGHT, s.y), tr):
                touched.add(ti)
    result.touched, result.bonked = touched, bonked
    return result
