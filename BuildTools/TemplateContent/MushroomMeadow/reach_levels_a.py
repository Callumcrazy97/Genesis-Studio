"""Reachability proof for Rootdeep Caverns (level 2) and Windswept Canopy (level 4).

Run after levels_a_gen.py (it reads the GENERATED rooms):   python reach_levels_a.py [--room cavern|canopy]
levels_a_gen.py runs it at the end and fails when it fails. Exit code 0 = every check passed.

Physics: hero_physics.py (Pip's real Explorer constants, frame-exact 60 fps step with coyote time,
jump buffer, the variable-jump cut, walk/run easing, tile sweeps with the engine's flush/epsilon
rules, the ledge step-up, the ceiling corner nudge, Bounce Mushrooms, thorns and falling out of
the room). This file adds what those two rooms also have, mirroring Explorer Step:
  * Moving Leaves: one-way platforms (Explorer's landing test: x within leaf-4..leaf+68, falling
    through the walking surface). A leaf's whole path is sampled (16 phases); riding a leaf reaches
    every sampled position (Pip can wait on it), and a jump can land on any sampled position of any
    leaf (the player can time it). Every flight that lands on a leaf is also re-flown without
    leaves, so a leaf never hides a route below it.
  * Water (the Glow pool): Pip swims freely (8 directions), so a pool is one node; he leaves it at
    the surface of any of its columns with jump held, which sets vy to -420 (Explorer), then flies
    normally with full air control.
  * Wind (Canopy): a gust pushes Pip with windForce up to 320 px/s^2 (Wind Controller peak), grip
    0.35 on the ground. Every take-off is also flown in a steady full gust either way, and the
    landings it causes count as reachable places (for the soft-lock check only; routes to the goal
    are proven in calm air, which always comes back: gusts last 2.7 s of every 7.2 s).

Checks (power-ups are never used; Glide Seed, Spore Puff, Bark Shield, Starlit and Heart Berries are
optional; seed pods stay solid before and after a bump, so no bump can seal a path):
  1. From the start and from the checkpoint respawn (flag x + 24), the goal is reachable at the DESIGN
     MARGIN (85% jump height, 92% run speed: 85% of a running jump's length; bounces and the swim
     exit hop are scaled like the jump). Caverns' goal is the boss arena; inside it, with the gate
     closed, the Glowspore's spawn point must be reachable too. Canopy's goal is the Glowspore.
  2. No soft-lock: every place Pip can reach with the REAL physics (calm or in a gust) can still reach
     the goal. Falling out of the room is a death and respawn, never a trap; thorns are treated as
     walls (a route never needs to touch them).
  3. Crumble Blocks: the route is also checked with every Crumble Block gone. If it needs them, they
     must be stepping stones Pip only crosses (each holds 0.5 s and comes back 4 s later), which is
     reported; nothing a route needs can be used up for good.
  4. Optional extras (Big Sun Coins, seed pods with prizes) are reported with the real physics.
"""
import json
import math
import multiprocessing
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hero_physics as hp  # noqa: E402

TILE = hp.TILE
DT = hp.DT
WIND_PEAK = 320.0          # Wind Controller: peak = 320 (px/s^2)
LEAF_PHASES = 16
MARGIN_JUMP = math.sqrt(hp.MARGIN)

# Power-ups that sprout from a seed pod and then hover where Pip cannot reach them. Rootdeep Caverns' were
# fixed (the Heart Berry before the boss arena and the Bark Shield in the Bat roost). This one is the same
# bug in another level; Cal asked for only the caverns fix, so it is reported here instead of failing.
KNOWN_UNREACHABLE_ITEMS = {
    ("canopy", "Prize Heart 03 item"): "the Heart Berry pod at column 165 row 4 in Windswept Canopy",
}
MARGIN_SPEED = 0.92
MAX_FRAMES = 300


# ------------------------------------------------------------------------------------- world
class World:
    """A generated room: hero_physics grid plus leaves, water, crumble cells, goal and pickups."""

    def __init__(self, key, closed_gate=False, no_crumble=False, no_thorns=False):
        self.key = key
        name = {"cavern": "Rootdeep Caverns", "canopy": "Windswept Canopy"}[key]
        self.room_name = name
        removed = {("Blocks", 7)} if no_crumble else set()
        self.grid = hp.RoomGrid.load("Rooms/%s.room.json" % name, removed_tiles=removed)
        with open(os.path.join(self.grid.assets, "Rooms", "%s.room.json" % name), encoding="utf-8") as handle:
            room = json.load(handle)
        self.width = room["settings"]["width"]
        self.water = set()
        self.gate = set()
        self.blocks = {}
        for node in room["nodes"]:
            if node.get("kind") != "TileLayer":
                continue
            for cell in node["tileLayer"]["cells"]:
                key_cell = (cell["x"], cell["y"])
                if node["name"] == "Water":
                    self.water.add(key_cell)
                elif node["name"] == "Gate":
                    self.gate.add(key_cell)
                elif node["name"] == "Blocks":
                    self.blocks[key_cell] = cell["tileY"] * 8 + cell["tileX"]
        self.thorns = set(self.grid.hazard)
        if closed_gate:
            self.grid = self.grid.copy(solid=set(self.grid.solid) | self.gate)
        if no_thorns:
            self.grid = self.grid.copy(hazard=set())
        self.crumble = {c for c, i in self.blocks.items() if i == 7}
        # Moving Leaves: home position from the room, motion from the generator's leaf table.
        self.leaves = []
        motion = {}
        if key == "canopy":
            import levels_a_gen as gen
            _, specs = gen.build_grid(gen.CANOPY_SECTIONS)
            for (c, r, axis, amp, period, phase) in specs:
                motion[(c * TILE, r * TILE - 4)] = (axis, amp)
        for (_, x, y) in self.grid.objects("Moving Leaf"):
            axis, amp = motion[(int(round(x)), int(round(y)))]
            path = []
            for k in range(LEAF_PHASES):
                off = amp * math.sin(2 * math.pi * k / LEAF_PHASES)
                path.append((x + (off if axis == "x" else 0.0), y + 4 + (off if axis == "y" else 0.0)))
            self.leaves.append(path)
        self.leaf_samples = [(lx, top, li) for li, path in enumerate(self.leaves) for (lx, top) in path]
        # Water regions (4-connected) and their surface columns.
        self.region_of = {}
        self.regions = []
        for cell in sorted(self.water):
            if cell in self.region_of:
                continue
            rid = len(self.regions)
            stack, cells = [cell], []
            self.region_of[cell] = rid
            while stack:
                c, r = stack.pop()
                cells.append((c, r))
                for n in ((c + 1, r), (c - 1, r), (c, r + 1), (c, r - 1)):
                    if n in self.water and n not in self.region_of:
                        self.region_of[n] = rid
                        stack.append(n)
            self.regions.append(sorted(cells))
        # Goal.
        self.goal_circle = None
        self.goal_rect = None
        if key == "cavern":
            self.gate_x = self.width - 640
            self.goal_rect = (self.gate_x + 40, 0, self.width - 32, 289)
            # Grumblecrag drops the Glowspore here (levels_a_gen GRUMBLE_STEP).
            self.glowspore_spawn = (self.gate_x + 320, 254)
        else:
            (_, gx, gy), = self.grid.objects("Glowspore")
            self.goal_circle = (gx, gy, 26.0)   # Explorer takes it within 30 px of (x, y-17); 4 px bob
        # Starts.
        (_, sx, sy), = self.grid.objects("Explorer")
        (_, cx, cy), = self.grid.objects("Checkpoint")
        self.starts = [("start", (sx, sy)), ("checkpoint respawn", (cx + 24, cy))]
        # Optional extras: Big Sun Coins (rect) and prize seed pods (cells).
        self.extras = []
        for i, (name, x, y) in enumerate(self.grid.objects("Big Sun Coin")):
            self.extras.append((name, (x - 12, y - 12, x + 12, y + 12)))
        self.prize_pods = []
        for prefab in ("Prize Glide", "Prize Spore", "Prize Shield", "Prize Star", "Prize Heart"):
            for (name, x, y) in self.grid.objects(prefab):
                self.prize_pods.append((name, (int(x // TILE), int(y // TILE))))
                # The power-up sprouts at the pod's centre and rises 30 px, so it hovers on top of the
                # pod: Pip has to be able to reach it there (item masks are about 20 px square).
                self.extras.append((name + " item", (x - 10, y - 40, x + 10, y - 20)))
        self.segs = hp.segments(self.grid)
        self.by_row = {}
        for s in self.segs:
            self.by_row.setdefault(s.y, []).append(s)

    def in_water(self, x, y):
        return (int(math.floor(x / TILE)), int(math.floor(y / TILE))) in self.water

    def seg_at(self, x, y):
        for s in self.by_row.get(int(round(y)), ()):
            if s.x0 - 0.5 <= x <= s.x1 + 0.5:
                return s
        return None

    def at_goal(self, x, y):
        if self.goal_rect is not None:
            return hp.rects_overlap((x - 7, y - 34, x + 7, y), self.goal_rect)
        gx, gy, rad = self.goal_circle
        return (x - gx) ** 2 + (y - 17 - gy) ** 2 < rad * rad

    def seg_reaches_goal(self, s):
        for x in range(s.x0, s.x1 + 1, 2):
            if self.at_goal(x, s.y):
                return True
        return self.at_goal(s.x1, s.y)


# ------------------------------------------------------------------------------------- Pip+
class PipPlus(hp.Pip):
    """hero_physics.Pip plus Moving Leaves (riding / landing), wind and water entry, in Explorer
    Step order. With no leaves, no wind and no water it is step-for-step hero_physics.Pip
    (checked by self_test)."""

    def __init__(self, world, x, y, vx=0.0, vy=0.0, jump_scale=1.0, speed_scale=1.0, wind=0.0,
                 wind_vx=0.0, riding=None, leaves=True):
        super().__init__(world.grid, x, y, vx, vy, jump_scale, speed_scale)
        self.w = world
        self.jump_scale = jump_scale
        self.wind, self.wind_vx = wind, wind_vx
        self.riding = riding
        self.use_leaves = leaves
        self.water = False
        self.leaf = None
        if riding is not None:
            self.grounded = True

    def step(self, direction=0, run=False, held=False, pressed=False):
        g, w = self.g, self.w
        self.bonked = False
        if w.water and w.in_water(self.x, self.y - 16):
            self.water = True
            return self
        if self.riding is not None:
            lx, top, li = self.riding
            self.y = top
            if self.x < lx - 6 or self.x > lx + 70:
                self.riding = None
        self.grounded = g.on_ground(self.x, self.y) or self.riding is not None
        self.coyote = hp.COYOTE if self.grounded else max(0.0, self.coyote - DT)
        self.jump_buffer = max(0.0, self.jump_buffer - DT)
        if pressed:
            self.jump_buffer = hp.JUMP_BUFFER
        if self.jump_buffer > 0 and self.coyote > 0:
            self.vy = -self.jump_speed
            self.grounded = False
            self.coyote = 0.0
            self.jump_buffer = 0.0
            self.bounce_lift = False
            self.riding = None
        if self.vy >= 0:
            self.bounce_lift = False
        if not self.bounce_lift and not held and self.vy < -hp.JUMP_CUT:
            self.vy = -hp.JUMP_CUT
        target = direction * (hp.RUN_SPEED if run else hp.WALK_SPEED) * self.speed_scale
        self.vx += (target - self.vx) * min(1.0, DT * hp.ACCEL_RATE)
        if self.wind != 0 or self.wind_vx != 0:
            grip = hp.WIND_GRIP_GROUND if self.grounded else 1.0
            self.wind_vx += self.wind * grip * DT
            self.wind_vx -= self.wind_vx * min(1.0, DT * hp.WIND_DRAG)
        move = (self.vx + self.wind_vx) * DT
        want = self.x + move
        nx = g.sweep_x(self.x, self.y, move)
        if abs(nx - want) > 0.001:
            stepped = False
            if self.vy >= 0:
                for k in range(1, hp.STEP_UP + 1):
                    if not g.body_hits(self.x, self.y - k):
                        sx = g.sweep_x(self.x, self.y - k, want - self.x)
                        if abs(sx - want) <= 0.001:
                            nx, self.y, stepped = sx, self.y - k, True
                            break
            if not stepped:
                self.vx = 0.0
                self.wind_vx = 0.0
        self.x = min(max(nx, 8.0), g.width - 8.0)
        old_y = self.y
        self.vy = min(self.vy + hp.GRAVITY * DT, hp.MAX_FALL)
        descending = self.vy > 0
        want = self.y + self.vy * DT
        ny = g.sweep_y(self.x, self.y, self.vy * DT)
        if abs(ny - want) > 0.001:
            nudged = False
            if self.vy < 0:
                for k in range(1, hp.CORNER_NUDGE + 1):
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
        if self.riding is None and self.vy >= 0 and self.use_leaves:
            for (lx, top, li) in w.leaf_samples:
                if lx - 4 <= self.x <= lx + 68 and old_y <= top + 2 and self.y >= top - 1:
                    self.y, self.vy, self.riding, self.leaf = top, 0.0, (lx, top, li), li
                    break
        for sx, sy in g.springs:
            if descending and old_y <= sy - 14 and self.x - 7 < sx + 14 and sx - 14 < self.x + 7 \
                    and self.y - 34 < sy and sy - 20 < self.y:
                self.y = sy - 20
                self.vy = -hp.BOUNCE_SPEED * self.jump_scale
                self.bounce_lift = True
                self.riding = None
                break
        self.grounded = g.on_ground(self.x, self.y) or self.riding is not None
        if self.y > g.height + hp.FALL_DEATH_BELOW:
            self.dead = True
        if g.in_hazard(self.x, self.y):
            self.hurt = True
        return self


def self_test():
    """PipPlus with no leaves/wind/water must fly exactly like hero_physics.Pip."""
    w = World("cavern")
    s = max((s for s in w.segs if s.y == 288), key=lambda s: s.x1 - s.x0)
    for d, hold, vx0 in ((1, 10 ** 6, 260.0), (-1, 7, 0.0), (1, 12, 180.0), (0, 10 ** 6, 0.0)):
        x = (s.x0 + s.x1) // 2
        a = hp.Pip(w.grid, x, s.y, vx=d * vx0)
        b = PipPlus(w, x, s.y, vx=d * vx0, leaves=False)
        for f in range(90):
            a.step(d, True, held=f < hold, pressed=f == 0)
            b.step(d, True, held=f < hold, pressed=f == 0)
            if (abs(a.x - b.x) > 1e-9 or abs(a.y - b.y) > 1e-9 or abs(a.vy - b.vy) > 1e-9):
                raise SystemExit("self-test: PipPlus drifted from hero_physics.Pip at frame %d" % f)


# ------------------------------------------------------------------------------------- search
def maneuvers(d, vx_options, at_edge):
    out = list(hp._maneuvers(d, vx_options, at_edge))
    for vx0 in vx_options:
        # Overshoot and come back (landing on a ledge behind the apex).
        out.append((d, vx0, True, 10 ** 6, lambda f, vy, d=d: d if vy < 0 else -d))
        out.append((d, vx0, True, 10 ** 6, lambda f, vy, d=d: d if f < 10 else 0))
    return out


_W = {}


def _world(key, variant):
    k = (key, variant)
    if k not in _W:
        _W[k] = World(key, closed_gate=(variant == "closed"), no_crumble=(variant == "nocrumble"),
                      no_thorns=(variant == "nothorns"))
    return _W[k]


def node_origins(w, node, speed_scale):
    """(x, y, vx options per direction, at-edge flags, riding, vy0) take-off points of a node."""
    kind, idx = node
    out = []
    if kind == "s":
        s = w.segs[idx]
        xs = list(range(s.x0, s.x1 + 1, 8))
        if xs[-1] != s.x1:
            xs.append(s.x1)
        for x in xs:
            for d in (-1, 1, 0):
                behind = (x - s.x0) if d > 0 else (s.x1 - x)
                if d == 0:
                    opts = [0.0]
                else:
                    opts = sorted({0.0, round(hp._speed_after(False, behind) * speed_scale, 3),
                                   round(hp._speed_after(True, behind) * speed_scale, 3)})
                at_edge = (d > 0 and x == s.x1) or (d < 0 and x == s.x0)
                out.append((x, s.y, d, opts, at_edge, None, 0.0))
    elif kind == "l":
        for (lx, top) in w.leaves[idx]:
            for x in (lx - 5, lx + 8, lx + 32, lx + 56, lx + 69):
                if w.grid.body_hits(x, top):
                    continue
                for d in (-1, 1, 0):
                    behind = (x - (lx - 6)) if d > 0 else ((lx + 70) - x)
                    opts = [0.0] if d == 0 else sorted({0.0, round(hp._speed_after(False, behind) * speed_scale, 3),
                                                        round(hp._speed_after(True, behind) * speed_scale, 3)})
                    at_edge = (d > 0 and x >= lx + 69) or (d < 0 and x <= lx - 5)
                    out.append((x, top, d, opts, at_edge, (lx, top, idx), 0.0))
    elif kind == "w":
        cells = w.regions[idx]
        surface = [(c, r) for (c, r) in cells if (c, r - 1) not in w.water]
        for (c, r) in surface:
            for x in (c * TILE + 7, c * TILE + 16, c * TILE + 25):
                y = r * TILE + 15.5
                if w.grid.body_hits(x, y):
                    continue
                for d in (-1, 1, 0):
                    opts = [0.0] if d == 0 else [0.0, hp.SWIM_SPEED * speed_scale]
                    out.append((x, y, d, opts, False, None, -hp.SWIM_EXIT_HOP))
    return out


def fly(w, x, y, vx, vy0, man, riding, js, ss, wind=0.0, leaves=True):
    """Fly one maneuver; return (outcome node or None, goal_touched, extras touched, bonked cells)."""
    d, vx0, jump, hold, steer = man
    swimming_exit = vy0 < 0
    if swimming_exit:
        vy0 = vy0 * js
        jump = False
    wind_vx = wind * hp.WIND_GRIP_GROUND / hp.WIND_DRAG if wind else 0.0
    p = PipPlus(w, x, y, vx=d * vx0 if d else 0.0, vy=vy0, jump_scale=js, speed_scale=ss, wind=wind,
                wind_vx=wind_vx, riding=riding, leaves=leaves)
    if swimming_exit:
        p.grounded = False
    run = True      # run held (shorter hops come from the release / steering variants)
    left = swimming_exit
    goal = False
    touched = set()
    bonked = set()
    start_leaf = riding[2] if riding else None
    for f in range(MAX_FRAMES):
        sd = steer(f, p.vy)
        held = (jump and f < hold) or swimming_exit
        p.step(sd, run, held=held, pressed=jump and f == 0)
        if p.water:
            if left or f > 0:
                return ("w", w.region_of[(int(math.floor(p.x / TILE)), int(math.floor((p.y - 16) / TILE)))]), goal, touched, bonked
            return None, goal, touched, bonked
        if p.dead or p.hurt:
            return None, goal, touched, bonked
        if p.bonked:
            for px in (p.x, p.x - 6, p.x + 6):
                cell = (int(math.floor(px / TILE)), int(math.floor((p.y - 35) / TILE)))
                if cell in w.grid.solid:
                    bonked.add(cell)
                    break
        box = p.box()
        for i, (_, rect) in enumerate(w.extras):
            if hp.rects_overlap(box, rect):
                touched.add(i)
        if w.at_goal(p.x, p.y):
            goal = True
            return "GOAL", goal, touched, bonked
        if not p.grounded:
            left = True
            continue
        if not left:
            if not jump:
                continue
            return None, goal, touched, bonked
        if p.riding is not None:
            li = p.riding[2]
            return (("l", li) if li != start_leaf else None), goal, touched, bonked
        t = w.seg_at(p.x, p.y)
        return (("s", t.id) if t is not None else None), goal, touched, bonked
    return None, goal, touched, bonked


def explore(args):
    """All edges out of one node. Returns (node, calm edges, wind-only edges, goal, touched, bonked)."""
    key, variant, node, js, ss, with_wind = args
    w = _world(key, variant)
    calm, windy = set(), set()
    goal = False
    touched, bonked = set(), set()
    if node[0] == "s" and w.seg_reaches_goal(w.segs[node[1]]):
        goal = True
    for (x, y, d, opts, at_edge, riding, vy0) in node_origins(w, node, ss):
        if d == 0:
            mans = [(0, 0.0, True, h, (lambda f, vy: 0)) for h in (10 ** 6, 7)]
        else:
            mans = maneuvers(d, opts, at_edge)
        if vy0 < 0:
            mans = [(d, opts[-1], False, 0, (lambda f, vy, d=d: d)),
                    (d, opts[-1], False, 0, (lambda f, vy, d=d: d if vy < 0 else 0)),
                    (d, opts[-1], False, 0, (lambda f, vy, d=d: d if vy < 0 else -d)),
                    (d, 0.0, False, 0, (lambda f, vy, d=d: 0 if vy < 0 else d))]
        for man in mans:
            res, g, t, b = fly(w, x, y, man[1], vy0, man, riding, js, ss)
            touched |= t
            bonked |= b
            if res == "GOAL":
                goal = True
            elif res is not None and res != node:
                calm.add(res)
            if w.leaves and res is not None and res != "GOAL" and res[0] == "l":
                res2, g2, t2, b2 = fly(w, x, y, man[1], vy0, man, riding, js, ss, leaves=False)
                if res2 == "GOAL":
                    goal = True
                elif res2 is not None and res2 != node:
                    calm.add(res2)
            if with_wind:
                for force in (WIND_PEAK, -WIND_PEAK):
                    res3, g3, t3, b3 = fly(w, x, y, man[1], vy0, man, riding, js, ss, wind=force)
                    if res3 is not None and res3 != "GOAL" and res3 != node and res3 not in calm:
                        windy.add(res3)
    return node, calm, windy, goal, touched, bonked


class Graph:
    def __init__(self):
        self.calm, self.windy, self.goal, self.touched, self.bonked = {}, {}, set(), set(), set()


def search(pool, key, variant, starts, js, ss, with_wind, graph=None):
    """Explore every node reachable from the start nodes (calm + wind edges)."""
    w = _world(key, variant)
    graph = graph or Graph()
    frontier = [n for n in starts if n not in graph.calm]
    seen = set(graph.calm) | set(frontier)
    while frontier:
        results = pool.map(explore, [(key, variant, n, js, ss, with_wind) for n in frontier], chunksize=1)
        frontier = []
        for node, calm, windy, goal, touched, bonked in results:
            graph.calm[node], graph.windy[node] = calm, windy
            if goal:
                graph.goal.add(node)
            graph.touched |= touched
            graph.bonked |= bonked
            for n in calm | windy:
                if n not in seen:
                    seen.add(n)
                    frontier.append(n)
    return graph


def forward(graph, starts, windy=True):
    reach = set(starts)
    stack = list(starts)
    while stack:
        n = stack.pop()
        nxt = set(graph.calm.get(n, ()))
        if windy:
            nxt |= graph.windy.get(n, set())
        for m in nxt:
            if m not in reach:
                reach.add(m)
                stack.append(m)
    return reach


def can_finish(graph):
    can = set(graph.goal)
    changed = True
    while changed:
        changed = False
        for n, e in graph.calm.items():
            if n not in can and e & can:
                can.add(n)
                changed = True
    return can


def route(graph, start):
    """Shortest chain of places from start to a place that touches the goal (calm edges)."""
    parent = {start: None}
    queue = [start]
    while queue:
        n = queue.pop(0)
        if n in graph.goal:
            out = []
            while n is not None:
                out.append(n)
                n = parent[n]
            return out[::-1]
        for m in sorted(graph.calm.get(n, ())):
            if m not in parent:
                parent[m] = n
                queue.append(m)
    return []


def start_node(w, start):
    x, y = start
    s = w.seg_at(x, y)
    if s is None:
        p = hp.Pip(w.grid, x, y)
        for _ in range(240):
            p.step()
            if p.grounded:
                break
        s = w.seg_at(p.x, p.y)
    return ("s", s.id) if s else None


def describe(w, node):
    kind, idx = node
    if kind == "s":
        s = w.segs[idx]
        return "floor y=%d x=%d..%d (columns %d..%d)" % (s.y, s.x0, s.x1, s.x0 // TILE, s.x1 // TILE)
    if kind == "l":
        lx, top = w.leaves[idx][0]
        return "Moving Leaf %d (home column %d, row %d)" % (idx, int(lx // TILE), int(top // TILE))
    return "water region %d (columns %d..%d)" % (idx, min(c for c, r in w.regions[idx]), max(c for c, r in w.regions[idx]))


def check_room(pool, key):
    failures, lines = [], []
    w = _world(key, "open")
    lines.append("%s: %d segments, %d leaves, %d water regions, %d crumble cells" % (
        w.room_name, len(w.segs), len(w.leaves), len(w.regions), len(w.crumble)))
    starts = []
    for label, pos in w.starts:
        n = start_node(w, pos)
        if n is None or w.grid.body_hits(*pos) or not w.grid.on_ground(*pos):
            failures.append("%s %r is not standing on clear ground" % (label, pos))
        starts.append((label, n))
    # 1. design margin, calm.
    t0 = time.time()
    margin = search(pool, key, "open", [n for _, n in starts if n], MARGIN_JUMP, MARGIN_SPEED, False)
    can_m = can_finish(margin)
    for label, n in starts:
        ok = n in can_m
        lines.append("  design margin: goal from %s %s" % (label, "REACHABLE" if ok else "NOT REACHABLE"))
        if ok and "--route" in sys.argv:
            for step in route(margin, n):
                lines.append("      " + describe(w, step))
        if not ok:
            failures.append("%s: goal not reachable from %s at the design margin" % (w.room_name, label))
    lines.append("  (design-margin search: %d places, %.1f s)" % (len(margin.calm), time.time() - t0))
    # 2. real physics, calm + gusts: soft-locks.
    t0 = time.time()
    real = search(pool, key, "open", [n for _, n in starts if n], 1.0, 1.0, key == "canopy")
    can_r = can_finish(real)
    reach_r = forward(real, [n for _, n in starts if n], windy=True)
    traps = sorted(n for n in reach_r if n not in can_r)
    lines.append("  real physics%s: %d reachable places, %d soft-locks (%.1f s)" % (
        " with gusts" if key == "canopy" else "", len(reach_r), len(traps), time.time() - t0))
    for n in traps:
        lines.append("    TRAP: " + describe(w, n))
        failures.append("%s: soft-lock at %s" % (w.room_name, describe(w, n)))
    # 3. crumble blocks gone.
    if w.crumble:
        wn = _world(key, "nocrumble")
        starts_n = [start_node(wn, pos) for _, pos in w.starts]
        g = search(pool, key, "nocrumble", [n for n in starts_n if n], MARGIN_JUMP, MARGIN_SPEED, False)
        canc = can_finish(g)
        needs = [label for (label, _), n in zip(w.starts, starts_n) if n not in canc]
        if needs:
            lines.append("  crumble blocks: the route from %s crosses Crumble Blocks (stepping stones that hold 0.5 s"
                         " and return after 4 s; never used up)" % ", ".join(needs))
        else:
            lines.append("  crumble blocks: not needed by any route")
    # Caverns: inside the arena with the gate closed, the Glowspore spawn is reachable.
    if key == "cavern":
        wc = _world(key, "closed")
        entry = (wc.gate_x + 96, 288)
        gx, gy = wc.glowspore_spawn
        n = start_node(wc, entry)
        s = wc.segs[n[1]] if n else None
        ok = s is not None and s.x0 - 30 <= gx <= s.x1 + 30 and abs((s.y - 17) - gy) < 30
        lines.append("  arena, gate closed: Glowspore spawn (%d, %d) %s from the arena floor" % (
            gx, gy, "collectable" if ok else "NOT collectable"))
        if not ok:
            failures.append("Glowspore spawn point is not collectable from the arena floor")
        if n:
            arena = search(pool, key, "closed", [n], MARGIN_JUMP, MARGIN_SPEED, False)
            out = [m for m in forward(arena, [n], False) if m[0] == "s" and wc.segs[m[1]].x0 < wc.gate_x + 32]
            if out:
                failures.append("Pip can leave the closed arena")
            arena_traps = [m for m in forward(arena, [n], False)
                           if not (m[0] == "s" and wc.segs[m[1]].x0 - 30 <= gx <= wc.segs[m[1]].x1 + 30)
                           and not (forward(arena, [m], False) & {n})]
            for m in arena_traps:
                failures.append("arena soft-lock at %s" % describe(wc, m))
            lines.append("  arena, gate closed: %d places, every one leads back to the floor: %s" % (
                len(arena.calm), "yes" if not arena_traps else "NO"))
    # Thorn trenches: Pip who lands in one is hurt (one heart, a hop, 1.5 s invulnerable); with the
    # thorns made harmless every trench floor must lead back to the goal (no pit that traps).
    wt = _world(key, "nothorns")
    floors = sorted({(c, r) for (c, r) in w.thorns if (c, r + 1) in w.grid.solid})
    trench_starts = []
    for (c, r) in floors:
        n = start_node(wt, (c * TILE + 16, (r + 1) * TILE))
        if n and n not in trench_starts:
            trench_starts.append(n)
    if trench_starts:
        gt = search(pool, key, "nothorns", trench_starts, MARGIN_JUMP, MARGIN_SPEED, False)
        cant = can_finish(gt)
        stuck = [n for n in trench_starts if n not in cant]
        lines.append("  thorn trenches: %d floors, Pip climbs out of %s" % (len(trench_starts), "all" if not stuck else "NOT all"))
        for n in stuck:
            failures.append("%s: thorn trench %s traps Pip" % (w.room_name, describe(wt, n)))
    # 4. optional extras with the real physics (a pool's extras count once Pip can swim in it).
    for i, (name, rect) in enumerate(w.extras):
        cell = (int(((rect[0] + rect[2]) / 2) // TILE), int(((rect[1] + rect[3]) / 2) // TILE))
        if cell in w.region_of and ("w", w.region_of[cell]) in reach_r:
            real.touched.add(i)
        lines.append("  optional %-12s %s" % (name, "reachable" if i in real.touched else "NOT reachable"))
        if i not in real.touched:
            if (key, name) in KNOWN_UNREACHABLE_ITEMS:
                lines.append("    WARNING: known, reported to Cal, not changed yet: %s" % KNOWN_UNREACHABLE_ITEMS[(key, name)])
            else:
                failures.append("%s: optional %s unreachable even with the real physics" % (w.room_name, name))
    for name, cell in w.prize_pods:
        ok = cell in real.bonked
        lines.append("  optional %-12s seed pod at column %d row %d %s" % (name, cell[0], cell[1], "bumpable" if ok else "NOT bumpable"))
        if not ok:
            failures.append("%s: %s seed pod cannot be bumped" % (w.room_name, name))
    return failures, lines


def main(argv):
    self_test()
    keys = ["cavern", "canopy"]
    if "--room" in argv:
        keys = [argv[argv.index("--room") + 1]]
    failures = []
    with multiprocessing.Pool(max(2, min(20, (os.cpu_count() or 4) - 4))) as pool:
        for key in keys:
            f, lines = check_room(pool, key)
            print("\n".join(lines))
            failures.extend(f)
    if failures:
        print("FAILED:")
        for f in failures:
            print("  " + f)
        return 1
    print("Rootdeep Caverns / Windswept Canopy: completable from start and checkpoint without power-ups; no soft-locks.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
