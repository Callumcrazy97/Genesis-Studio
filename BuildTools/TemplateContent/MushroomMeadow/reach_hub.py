"""Playability check for the HUB rooms (Hearthollow and the Overworld), as hub_gen.py writes them.

Run from anywhere after hub_gen.py:
    python BuildTools/TemplateContent/MushroomMeadow/reach_hub.py
Exit code 0 when every check passes, 1 otherwise.

Hearthollow: Pip's real controller (Explorer Create/Step: walk 180, run 260, jumpSpeed 525, gravity
1250, fall cap 680, coyote 0.10 s, hitbox 14x34 with the feet at y) is simulated frame by frame at
60 fps against the room's solid tiles, with NO power-ups. Every grounded spot Pip can reach from the
start is found by trying walks and jumps (with the direction held from the start or only after a
delay). The run is repeated with a safety margin: jumps 85% as high and moves 85% as fast.
  - every villager is talkable: Pip can stand within the NPC Step's talk box (40 px across, 48 px up/down)
  - the shop's villager (Chanterelle) is one of them, and the way out is reachable (the Exit Sign's
    box, or the room's right edge, which also leaves)
  - no soft-lock: from every spot Pip can reach, the start (and so everything else) can be reached again
Overworld: the Map Marker's own input rule (the neighbour whose path points most along the pressed
direction, dot > 0.2) is applied for the four directions at every node: each open neighbour must be
reachable from each node, every node must name an existing room, and each traced trail must join its
two nodes.
"""
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hub_gen  # noqa: E402

ASSETS = hub_gen.ASSETS
DT = 1.0 / 60.0
HALF_W, HEIGHT = 7, 34          # Explorer mask 9,6 14x34 at origin 16,40: x-7..x+7, y-34..y
FAILS = []


def check(ok, text):
    print(("  ok    " if ok else "  FAIL  ") + text)
    if not ok:
        FAILS.append(text)


# ---------------------------------------------------------------------------------- Hearthollow
class Room:
    def __init__(self, name):
        with open(os.path.join(ASSETS, "Rooms", name + ".room.json"), encoding="utf-8") as handle:
            self.doc = json.load(handle)
        self.width = self.doc["settings"]["width"]
        self.height = self.doc["settings"]["height"]
        self.solid = set()
        for node in self.doc["nodes"]:
            layer = node.get("tileLayer")
            if node["kind"] == "TileLayer" and layer and layer["collisionEnabled"]:
                for cell in layer["cells"]:
                    if cell["tileY"] * 8 + cell["tileX"] in hub_gen.SOLID:
                        self.solid.add((cell["x"], cell["y"]))
        self.objects = {}
        for node in self.doc["nodes"]:
            if node["kind"] == "GameObject":
                prefab = os.path.basename(node["gameObject"]["prefab"]).replace(".object.json", "")
                self.objects.setdefault(prefab, []).append(tuple(node["transform"]["position"][:2]))

    def box_hits(self, x, y):
        """True when Pip's box with the feet at (x, y) overlaps a solid tile."""
        x0, x1 = x - HALF_W, x + HALF_W
        y0, y1 = y - HEIGHT, y
        for c in range(int(math.floor(x0 / 32)), int(math.floor((x1 - 1e-6) / 32)) + 1):
            for r in range(int(math.floor(y0 / 32)), int(math.floor((y1 - 1e-6) / 32)) + 1):
                if (c, r) in self.solid:
                    return True
        return False


def move_axis(room, x, y, d, axis):
    """TileMoveX / TileMoveY: move up to d, stopping flush against solid tiles (1/8 px steps)."""
    step = 0.125 if d > 0 else -0.125
    n = int(abs(d) / 0.125)
    for _ in range(n):
        nx, ny = (x + step, y) if axis == 0 else (x, y + step)
        if room.box_hits(nx, ny):
            return x, y
        x, y = nx, ny
    rest = d - n * step
    nx, ny = (x + rest, y) if axis == 0 else (x, y + rest)
    if not room.box_hits(nx, ny):
        x, y = nx, ny
    return x, y


def snap(room, x, y):
    """A grounded spot on a 2 px grid (to keep the search small), never inside a wall."""
    y = round(y)
    for gx in (round(x / 2) * 2, math.floor(x / 2) * 2, math.ceil(x / 2) * 2):
        if not room.box_hits(gx, y) and room.box_hits(gx, y + 1):
            return (gx, y)
    return (x, y)


def simulate(room, x, y, direction, run, jump, steer_delay, hold, margin):
    """One action from a grounded spot: the direction is held from frame steer_delay for `hold`
    frames (a jump holds it until landing). Returns the grounded spot it ends on, or None."""
    walk, runs, jump_speed = 180 * margin, 260 * margin, 525 * math.sqrt(margin)
    vx = vy = 0.0
    coyote = 0.10
    jumped = False
    for f in range(300):
        grounded = room.box_hits(x, y + 1)
        if jump:
            landed = jumped and f > 3 and grounded and vy >= 0
        else:   # a walk ends once Pip has stopped (or stands after walking off a ledge)
            landed = f >= hold and grounded and abs(vx) < 1
        if landed:
            return snap(room, x, y)
        coyote = 0.10 if grounded else max(0.0, coyote - DT)
        if jump and not jumped and coyote > 0:
            vy = -jump_speed
            jumped = True
        held = direction if steer_delay <= f < (999 if jump else hold) else 0
        target = held * (runs if run else walk)
        vx += (target - vx) * min(1, DT * 16)
        x2, y2 = move_axis(room, x, y, vx * DT, 0)
        if abs(x2 - (x + vx * DT)) > 0.001:
            vx = 0
        x = min(max(x2, 8), room.width - 8)
        vy = min(vy + 1250 * DT, 680)
        x, y2 = move_axis(room, x, y, vy * DT, 1)
        if abs(y2 - (y + vy * DT)) > 0.001:
            vy = 0
        y = y2
        if y > room.height + 64:
            return None   # fell out of the room
    return None


def settle(room, x, y):
    while not room.box_hits(x, y + 1) and y < room.height:
        y += 1
    return snap(room, x, y)


def explore(room, start, margin):
    actions = []
    for direction in (-1, 1):
        for run in (0, 1):
            actions.append((direction, run, False, 0, 5))
    for direction in (-1, 0, 1):
        for run in (0, 1):
            for delay in (0, 12, 22):
                if direction == 0 and (run or delay):
                    continue
                actions.append((direction, run, True, delay, 150))
    graph = {}
    todo = [start]
    seen = {start}
    while todo:
        s = todo.pop()
        outs = set()
        for (direction, run, jump, delay, frames) in actions:
            t = simulate(room, s[0], s[1], direction, run, jump, delay, frames, margin)
            if t is not None and t != s:
                outs.add(t)
                if t not in seen:
                    seen.add(t)
                    todo.append(t)
        graph[s] = outs
    return graph


def check_hearthollow():
    print("Hearthollow")
    room = Room("Hearthollow")
    sx, sy = room.objects["Explorer"][0]
    start = settle(room, sx, sy)
    check(abs(start[1] - sy) < 1, "Pip starts standing on the ground (feet y %d, placed %d)" % (start[1], sy))
    npcs = [(name, room.objects[name][0]) for (name, _, _, _, _) in hub_gen.NPCS]
    exit_x, exit_y = room.objects["Exit Sign"][0]
    for margin in (1.0, 0.85):
        graph = explore(room, start, margin)
        label = "full physics" if margin == 1.0 else "85% margin"
        print(" %s: %d grounded spots reachable" % (label, len(graph)))
        for name, (nx, ny) in npcs:
            spots = [s for s in graph if abs(s[0] - nx) <= 40 and abs(s[1] - ny) <= 48]
            wander = 36 if name == "NPC Bram" else 0
            if wander:   # Bram walks 36 px either way: he must be talkable wherever he is
                ok = all(any(abs(s[0] - bx) <= 40 and abs(s[1] - ny) <= 48 for s in graph)
                         for bx in range(int(nx - wander), int(nx + wander) + 1, 4))
            else:
                ok = bool(spots)
            check(ok, "%s talkable (%s at %d,%d; %d spots in reach)" % (name, label, nx, ny, len(spots)))
        out = [s for s in graph if (abs(s[0] - exit_x) <= 30 and abs(s[1] - exit_y) <= 40) or s[0] >= room.width - 20]
        check(bool(out), "the way out (Exit Sign / right edge) is reachable (%s)" % label)
        # Soft-locks: everything reachable must lead back to the start.
        back = {start}
        reverse = {}
        for s, outs in graph.items():
            for t in outs:
                reverse.setdefault(t, set()).add(s)
        todo = [start]
        while todo:
            s = todo.pop()
            for p in reverse.get(s, ()):
                if p not in back:
                    back.add(p)
                    todo.append(p)
        stuck = sorted(set(graph) - back)
        check(not stuck, "no soft-lock: every reachable spot leads back to the start (%s)%s"
              % (label, "" if not stuck else " stuck at %s" % stuck[:5]))
        if margin == 0.85:
            px, py = room.objects["NPC Puffball"][0]
            on_cap = [s for s in graph if s[1] == py and abs(s[0] - px) <= 64]
            check(bool(on_cap), "Pip can stand on Puffball's mushroom cap with 85%% jumps (%d spots)" % len(on_cap))
    check("Village Host" in room.objects and "Level Rules" in room.objects, "Village Host and Level Rules are placed")


# ---------------------------------------------------------------------------------- Overworld
def check_overworld():
    print("Overworld")
    nodes = hub_gen.NODES
    for name in hub_gen.NODE_NAMES:
        check(os.path.exists(os.path.join(ASSETS, "Rooms", name + ".room.json")), "node room '%s' exists" % name)
    directions = {"right": (1, 0), "left": (-1, 0), "up": (0, -1), "down": (0, 1)}
    for node in range(len(nodes)):
        for k in (node - 1, node + 1):
            if not 0 <= k < len(nodes):
                continue
            keys = []
            for key, (ix, iy) in directions.items():
                pick, best = -1, 0.2
                for j in (node - 1, node + 1):
                    if 0 <= j < len(nodes):
                        vx, vy = nodes[j][0] - nodes[node][0], nodes[j][1] - nodes[node][1]
                        dot = (vx * ix + vy * iy) / (max(1, math.hypot(vx, vy)) * math.hypot(ix, iy))
                        if dot > best:
                            best, pick = dot, j
                if pick == k:
                    keys.append(key)
            check(bool(keys), "map: node %d -> node %d by %s" % (node, k, "/".join(keys) or "no direction"))
    trails = hub_gen.trace_map_paths()
    for k, (xs, ys, cum) in enumerate(trails):
        joined = (xs[0], ys[0]) == tuple(nodes[k]) and (xs[-1], ys[-1]) == tuple(nodes[k + 1])
        check(joined and len(xs) > 3 and cum[-1] > 0, "map trail %d -> %d follows the painted dots (%d points)" % (k, k + 1, len(xs)))
    room = Room("Overworld")
    placed = sorted(room.objects.get("Map Node", []))
    check(len(placed) == 6 and all(tuple(n) in [tuple(p) for p in placed] for n in nodes), "six Map Nodes on the six spots")
    check(len(room.objects.get("Map Marker", [])) == 1, "one Map Marker")


def main():
    check_hearthollow()
    check_overworld()
    print("%d failure(s)" % len(FAILS))
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
