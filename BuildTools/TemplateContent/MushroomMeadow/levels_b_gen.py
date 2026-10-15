"""Mushroom Meadow LEVELS-B generator: Tidepool Grotto (level 3) and Duskmantle Hollow (level 5).

Re-runnable and deterministic. Run from anywhere:
    python BuildTools/TemplateContent/MushroomMeadow/levels_b_gen.py            (write content)
    python BuildTools/TemplateContent/MushroomMeadow/levels_b_gen.py --map      (also print maps)
    python BuildTools/TemplateContent/MushroomMeadow/levels_b_gen.py --preview  (also render previews
                                         to TestResults/MushroomMeadow/level_<name>.png)

Writes into Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets:
  Rooms/Tidepool Grotto.room.json, Rooms/Duskmantle Hollow.room.json
  Objects: Bloatfin, Driftjelly, Current, Grotto Controller, Duskmantle, Gloom Orb, Hollow Controller
  Shaders: Underwater, Gloom (Fullscreen post effects)
  Particles: Grotto Bubbles, Vent Bubbles, Bloatfin Pop, Puff Bubbles, Jelly Sparkle, Current Streak *,
             Gloom Burst, Gloom Trail, Boss Dust, Daze Stars, Orb Pop, Hollow Motes, Summon Smoke,
             Glowspore Reveal
  Scripts: Levels B Lib (LbSolidAt / LbHeroFits helpers)

Rooms are built from the ASCII sections below (32 px cells). Objects owned by other agents are
placed by name: Explorer + Level Rules (HERO), Sun Coin / Big Sun Coin / Checkpoint (phase 1),
Glowspore + Prize markers (HERO), Dusk Moth (LEVELS-A).

Particle units: the Grotto uses ParticleBurstAt / SpawnParticleEmitter (12 px per unit, the
phase-1 system). The Hollow is a dark room, so it uses Particle2DBurst (pixel units) whose glow is
drawn above the 2D light mask. NOTE: Particle2DBurst and Light2DPoint switch the room's 2D light
mask on (default ambient 0.32), so a lit room must call Light2DAmbient(1, ...) before using them;
the Grotto Controller does that defensively.
"""
import hashlib
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
TEMPLATE = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates", "Assets", "TwoDShowcase")
ASSETS = os.path.join(TEMPLATE, "Assets")
STAMP = "2026-10-11T00:00:00Z"
SOLID_TILES = set(range(0, 16)) | set(range(24, 29)) | {33, 34, 38}


# ---------------------------------------------------------------- file helpers
def guid(text):
    return hashlib.md5(("mushroom-meadow-levels-b:" + text).encode("utf-8")).hexdigest()


def write_text(rel, text):
    path = os.path.join(ASSETS, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def write_json(rel, data):
    write_text(rel, json.dumps(data, indent=2) + "\n")


def write_meta(rel, kind, display):
    path = os.path.join(ASSETS, rel + ".meta")
    existing = None
    if os.path.exists(path):
        try:
            with open(path, encoding="utf-8") as handle:
                existing = json.load(handle).get("guid")
        except (OSError, ValueError):
            existing = None
    write_json(rel + ".meta", {"guid": existing or guid(rel), "kind": kind, "displayName": display,
                               "createdUtc": STAMP, "modifiedUtc": STAMP})


# ---------------------------------------------------------------- maps
# Legend (both rooms)
#   #  rock (auto-tiled: top 0 / caps 5,6 / edges 24,25 / bottom 38 / fill 1,15)     World
#   S  hard stone 7    P  pillar pair 8-11    L  platform 12-14    M  ledge 26-28
#   C  crystal / amethyst block 33                                                    World
#   B  breakable block 2      ?  prize box 3 (pays a coin)                            Blocks
#   1..5  prize box 3 with a marker: 1 Prize Spore, 2 Prize Shield, 3 Prize Heart,
#         4 Prize Star, 5 Prize Glide                                                 Blocks
#   ^  hazard 32 (sea urchins / shadow thorns)                                        Hazards
#   decor: t 16, f 17, m 18, b 19, n 20, s 21, v 22, r 23, | 29, l 35 lamp, F 36, c 37  Decor
#   actors: @ Explorer, o Sun Coin, O Big Sun Coin, K Checkpoint, Z Glowspore,
#           p Bloatfin, j Driftjelly, d Dusk Moth, G Duskmantle (home point), * bubble vent
#   currents (Grotto): > right, < left, A up, V down  (each connected region = one Current zone)

GROTTO_ROWS = 22
GROTTO_SECTIONS = [
    ("A Sunlit Shallows: start shelf, first Bloatfin, Spore Puff box", 32, [
        "################################",
        "################################",
        "######v###......v######v####..##",
        "#v.......v............v.........",
        "#...............................",
        "#.............o.o.o.............",
        "#.........1.?...................",
        "#.........................p.....",
        "#...........................o...",
        "#.F@t.l.s.c..fbt.m..F..t.f.r....",
        "############################....",
        "############################....",
        "############################....",
        "############################....",
        "############################....",
        "############################....",
        "############################....",
        "############################....",
        "############################....",
        "############################....",
        "################################",
        "################################",
    ]),
    ("B Kelp Drop: a down current and drifting jellies", 28, [
        "############################",
        "############################",
        "###v..vv.......v...#########",
        ".....................v######",
        "..VVVVVV.....o........######",
        "..VVVVVV..j..o....j...v#####",
        "..VVVVVV.....o.........#####",
        "..VVVVVV...............#####",
        "..VVVVVV.....o..........####",
        "..VVVVVV..........j.....v###",
        "..VVVVVV.....o...........###",
        "..VVVVVV..................##",
        "..VVVVVV.....o.............#",
        "..VVVVVV....................",
        "..VVVVVV..j.................",
        "..VVVVVV.....o..............",
        "..VVVVVV....................",
        "........................p...",
        "............................",
        ".t.F.m.f..c..*..b.Ft.r.f..t.",
        "############################",
        "############################",
    ]),
    ("C Sunken Ruins: columns, a cracked-coral vault (Big Sun Coin 1)", 36, [
        "####################################",
        "####################################",
        "####################################",
        "####################################",
        "####################################",
        "##############################v#####",
        "###########################.....####",
        "#######SSSSSSSS######v####...o...###",
        "#####v.S..O....v####......o.o....###",
        "####...S..........v......o...o...v##",
        "###....SSBBBBSS.............o.o....#",
        "##v.........................v.......",
        ".v.........?2?.........v............",
        "....................................",
        "....o.o.....p.......................",
        "...PP...........PP..SS.......SSS..p.",
        "...PP...o.o.o...PP..SS..j....S.S....",
        "...PP...........PP..SS...o...S.S....",
        "...PP....SSS....PP..SS.......SSS....",
        ".t.PP.f.SSSSS.c.PP..SS^^.b.t.S.S.l.m",
        "####################################",
        "####################################",
    ]),
    ("D Updraft: an up current lifts Pip to the checkpoint shelf", 26, [
        "##########################",
        "##########################",
        "###########.v.....v.....v.",
        "###########...............",
        "###########...o.o.o.......",
        "###v#######...............",
        "##.........AAAAAA.........",
        "#..........AAAAAA.........",
        "...........AAAAAA....s.K..",
        "...........AAAAAA..#######",
        ".v.........AAAAAA..#######",
        "...........AAAAAA..#######",
        "...........AAAAAA..#######",
        "...o.......AAAAAA..#######",
        "...........AAAAAA..#######",
        "...o.......AAAAAA..#######",
        "...........AAAAAA..#######",
        "...o.......AAAAAA..#######",
        "...........AAAAAA..#######",
        ".t.....*...AAAAAA..#######",
        "##########################",
        "##########################",
    ]),
    ("E Current Run: an urchin-lined fast lane; a counter-current tunnel hides Big Sun Coin 2", 36, [
        "####################################",
        "####################################",
        "####################################",
        "..>>>>>>^^^>>>>>>>>>>^^^^>>>>>>.....",
        "..>>>>>>>>>>>>o>o>o>>>>>>>>>>>>.....",
        "..>>>>>>>>>>>>>>>>>>>>>>>>>>>>>.....",
        "..>>>>>>>>>>>>>>>>>>>>>>>>>>>>>..o..",
        "..>>>>o>o>o>>>>>>>>>>>>>>>>>>>>.....",
        "..>>>>>>>>>>>^^^>>>>>>>>>>>>>>>..o..",
        "#############################.......",
        "#############################.......",
        "#############################..o....",
        "#####.......................#.......",
        "#####O..>>>>>>>>>>>>>>>>>>>>........",
        "#####...>>>>>>>>>>>>>>>>>>>>........",
        "#####...>>>>>>>>>>>>>>>>>>>>........",
        "#####..c..^^^......^^^......#.......",
        "#############################.......",
        "#############################..p....",
        "#############################.t.f.b.",
        "####################################",
        "####################################",
    ]),
    ("F Bloatfin Reef: weave between hanging rock and coral pillars", 34, [
        "##################################",
        "##################################",
        ".....v....##.vv........v....##....",
        "..........##................##....",
        "....o.o.o.##.......p........##.o..",
        "..........##.......LLL......##....",
        "..........##..3.4...........##....",
        "..p.......##.......j........##..p.",
        "..........##................##....",
        "....LLL...##.........PP.....##..o.",
        "..........##.........PP.....##....",
        "..........##.........PP...........",
        "......j..............PP.......j...",
        "..o.o................PP...........",
        "............LLLL.....PP....LLL....",
        ".....p...............PP...........",
        "...........o.o.o.....PP.......p...",
        "..........j..........PP...........",
        ".....................PP...........",
        ".t.f.m..c.^^^..b..t..PP.f..^^.r.F.",
        "##################################",
        "##################################",
    ]),
    ("G Jelly Trench: dive the trench; a side alcove hides Big Sun Coin 3", 30, [
        "##############################",
        "##############################",
        "..v........v....v.############",
        ".................v############",
        "..o.o.o..........#############",
        "............VVVVVV......O#####",
        "............VVVVVV.j.....#####",
        "............VVVVVV.......#####",
        ".....j......VVVVVV....########",
        "............VVVVVV..##########",
        "###########.VVVVVV..##########",
        "###########^VVVVVV.^##########",
        "###########.VVVVVV..##########",
        "###########^VVVVVV..^#########",
        "###########.VVVVVV.......j....",
        "###########..j................",
        "###########...................",
        "###########........o.o.o......",
        "###########...................",
        "###########.c.t..^^^..f..m..b.",
        "##############################",
        "##############################",
    ]),
    ("H Giant Clam: the Glowspore rests in an open clam", 28, [
        "############################",
        "############################",
        "############################",
        "##########v#####v###########",
        "######v..............v######",
        "####.....o.....o.........###",
        "###.......................##",
        "##.........................#",
        "#..........................#",
        ".........o.o.o.............#",
        "...........................#",
        "...........................#",
        "...........................#",
        "............MMMMM..........#",
        "...........................#",
        "...........................#",
        "..............Z............#",
        "............MMMMMM.........#",
        "...........########........#",
        ".t.l.f.*..c########..*.F.l.#",
        "############################",
        "############################",
    ]),
]

HOLLOW_ROWS = 15
HOLLOW_SECTIONS = [
    ("1 Dusk Path: first Dusk Moths, a Spore Puff box", 32, [
        "................................",
        "................................",
        "................................",
        "................................",
        ".....................d..........",
        "............o.o.o...............",
        "...........?1?..................",
        "......................o.o.o.....",
        "..........................d.....",
        ".t.@..s.l..f.t..m...b.l...o.o.t.",
        "################################",
        "################################",
        "################################",
        "################################",
        "################################",
    ]),
    # Playability (round 3, proven by reach_finale.py at 85% of Pip's jump): pits are at most 5 tiles
    # (160 px), every required rise is at most 2 tiles (64 px), silk platforms sit over pits only
    # (never low over walkable floor), and the spires form a 2-4-2 pyramid so a fall between two
    # spires can always climb back out.
    ("2 Thorn Hollow: pits, moth-silk platforms (Big Sun Coin 1)", 32, [
        "................................",
        "................................",
        "................................",
        "................................",
        ".........O......................",
        "................................",
        "....o.o.o.LLL.......d...........",
        "...................o.o..o.o.o...",
        "....LLL....d............LLL.....",
        ".f.......t.^^.......l........lm.",
        "###.....########...####.....####",
        "###.....########...####.....####",
        "###.....########...####.....####",
        "###.....########...####.....####",
        "###.....########...####.....####",
    ]),
    ("3 Amethyst Spires: climb the crystals (Big Sun Coin 2)", 28, [
        "............................",
        "............................",
        "............................",
        ".............O..............",
        "....d...................d...",
        ".........o.o.....o.o........",
        ".............CC.............",
        ".............CC.............",
        ".......CC....CC....CC...o.o.",
        ".l.....CC..f.CC..^.CC.t...l.",
        "############################",
        "############################",
        "############################",
        "############################",
        "############################",
    ]),
    ("4 Nest Gate: checkpoint, last power-ups (Big Sun Coin 3), then drop into the nest", 16, [
        "................",
        "................",
        "................",
        "........O.......",
        "................",
        "......?1?2?.....",
        "................",
        "................",
        "................",
        ".l..K..s..l...t.",
        "################",
        "################",
        "################",
        "################",
        "################",
    ]),
    ("5 Duskmantle's Nest: the boss arena (exactly one 640 px view)", 20, [
        "...................#",
        "...................#",
        "...................#",
        "...................#",
        "...................#",
        "...................#",
        "..........G........#",
        "...................#",
        "...................#",
        "...................#",
        "...................#",
        ".....LLL.....LLL...#",
        "...................#",
        ".l.f.....t.....m.l.#",
        "####################",
    ]),
]

DECOR = {"t": 16, "f": 17, "m": 18, "b": 19, "n": 20, "s": 21, "v": 22, "r": 23, "|": 29,
         "l": 35, "F": 36, "c": 37}
PRIZES = {"1": "Prize Spore", "2": "Prize Shield", "3": "Prize Heart", "4": "Prize Star", "5": "Prize Glide"}
CURRENTS = {">": (170, 0), "<": (-170, 0), "A": (0, -165), "V": (0, 95)}
ACTORS = set("@oOKZpjdG*")


def build_grid(sections, rows):
    grid = [[] for _ in range(rows)]
    starts = []
    for name, width, lines in sections:
        assert len(lines) == rows, "%s: %d rows, expected %d" % (name, len(lines), rows)
        for i, line in enumerate(lines):
            assert len(line) == width, "%s row %d: %d chars, expected %d: %r" % (name, i, len(line), width, line)
        starts.append(len(grid[0]))
        for i in range(rows):
            grid[i].extend(lines[i])
    return grid, starts


class Level:
    """Turns an ASCII grid into tile layers plus a list of actor cells."""

    def __init__(self, sections, rows):
        self.grid, self.starts = build_grid(sections, rows)
        self.rows = rows
        self.width = len(self.grid[0])
        self.layers = {"World": [], "Blocks": [], "Decor": [], "Hazards": []}
        self.solid = set()
        self.actors = {}
        self.currents = []
        self._tile()
        self._find_currents()

    def at(self, r, c):
        if 0 <= r < self.rows and 0 <= c < self.width:
            return self.grid[r][c]
        return "#"   # outside the map counts as rock, so edges at the room border stay plain fill

    def put(self, layer, c, r, idx):
        self.layers[layer].append({"x": c, "y": r, "tileX": idx % 8, "tileY": idx // 8})
        if layer in ("World", "Blocks") and idx in SOLID_TILES:
            self.solid.add((c, r))

    def _tile(self):
        for c in range(self.width):
            for r in range(self.rows):
                ch = self.grid[r][c]
                if ch == "#":
                    up, left = self.at(r - 1, c) == "#", self.at(r, c - 1) == "#"
                    right, down = self.at(r, c + 1) == "#", self.at(r + 1, c) == "#"
                    if not up:
                        idx = 0 if (left and right) or (not left and not right) else (5 if not left else 6)
                    elif not left:
                        idx = 24
                    elif not right:
                        idx = 25
                    elif not down:
                        idx = 38
                    else:
                        idx = 15 if (c * 7 + r * 13) % 11 == 0 else 1
                    self.put("World", c, r, idx)
                elif ch == "S":
                    self.put("World", c, r, 7)
                elif ch == "C":
                    self.put("World", c, r, 33)
                elif ch == "P":
                    top = self.at(r - 1, c) != "P"
                    run, k = 0, c
                    while self.at(r, k) == "P":
                        run += 1
                        k -= 1
                    is_left = run % 2 == 1
                    self.put("World", c, r, (8 if is_left else 9) if top else (10 if is_left else 11))
                elif ch == "L":
                    left, right = self.at(r, c - 1) == "L", self.at(r, c + 1) == "L"
                    self.put("World", c, r, 13 if left and right else (12 if not left else 14))
                elif ch == "M":
                    left, right = self.at(r, c - 1) == "M", self.at(r, c + 1) == "M"
                    self.put("World", c, r, 27 if left and right else (26 if not left else 28))
                elif ch == "B":
                    self.put("Blocks", c, r, 2)
                elif ch == "?":
                    self.put("Blocks", c, r, 3)
                elif ch in PRIZES:
                    self.put("Blocks", c, r, 3)
                    self.actors.setdefault(ch, []).append((c, r))
                elif ch == "^":
                    self.put("Hazards", c, r, 32)
                elif ch in DECOR:
                    self.put("Decor", c, r, DECOR[ch])
                elif ch in ACTORS:
                    self.actors.setdefault(ch, []).append((c, r))
                elif ch in CURRENTS or ch == ".":
                    pass
                else:
                    raise SystemExit("Unknown map character %r at column %d row %d" % (ch, c, r))

    def _find_currents(self):
        seen = set()
        for c in range(self.width):
            for r in range(self.rows):
                ch = self.grid[r][c]
                if ch not in CURRENTS or (c, r) in seen:
                    continue
                stack, cells = [(c, r)], []
                seen.add((c, r))
                while stack:
                    cc, rr = stack.pop()
                    cells.append((cc, rr))
                    for nc, nr in ((cc + 1, rr), (cc - 1, rr), (cc, rr + 1), (cc, rr - 1)):
                        if (nc, nr) not in seen and self.at(nr, nc) == ch:
                            seen.add((nc, nr))
                            stack.append((nc, nr))
                c0 = min(x for x, _ in cells)
                c1 = max(x for x, _ in cells)
                r0 = min(y for _, y in cells)
                r1 = max(y for _, y in cells)
                fx, fy = CURRENTS[ch]
                self.currents.append({"x": c0 * 32, "y": r0 * 32, "w": (c1 - c0 + 1) * 32,
                                      "h": (r1 - r0 + 1) * 32, "fx": fx, "fy": fy, "char": ch})

    def cells(self, ch):
        return sorted(self.actors.get(ch, []))

    def check_clear(self, label, x, y, box):
        l, t, rgt, b = box
        x0, y0, x1, y1 = x + l, y + t, x + rgt - 0.01, y + b - 0.01
        for cc in range(int(x0 // 32), int(x1 // 32) + 1):
            for rr in range(int(max(0, y0) // 32), int(y1 // 32) + 1):
                if (cc, rr) in self.solid:
                    raise SystemExit("%s at %s,%s overlaps solid tile %d,%d" % (label, x, y, cc, rr))

    def print_map(self, title):
        print("== %s (%d x %d)" % (title, self.width, self.rows))
        for r in range(self.rows):
            print("".join(self.grid[r]))


# ---------------------------------------------------------------- room writing
def background_y(asset, room_height):
    """Place a 360 px band so it meets the room floor when the camera sits at the bottom.

    The 2D pass shifts a Single background by (cameraTop) * (1 - parallaxY); solving for the
    bottom camera position gives nodeY = (H - 360) * parallaxY (parallaxY 1 = H - 360)."""
    path = os.path.join(TEMPLATE, asset.replace("/", os.sep))
    parallax_y = 1.0
    if os.path.exists(path):
        try:
            with open(path, encoding="utf-8") as handle:
                parallax_y = float(json.load(handle).get("usage", {}).get("background", {}).get("parallaxY", 1.0))
        except (OSError, ValueError, TypeError):
            parallax_y = 1.0
    return round((room_height - 360) * parallax_y)


def make_room(level, room_name, room_key, tileset, backgrounds, env_color, post_effects, follow_margin_y,
              follow_speed_y, place_actors):
    nodes = []

    def gid(text):
        return guid(room_key + ":" + text)

    height = level.rows * 32
    for key, label, asset, layout, depth, repeat in backgrounds:
        y = 0 if layout == "StretchView" else background_y(asset, height)
        nodes.append({"id": gid("bg:" + key), "kind": "Background", "name": label, "enabled": True,
                      "layerId": "scenery",
                      "transform": {"position": [0, y, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                      "background": {"asset": asset, "mode": "TwoD", "layout": layout, "depth": depth,
                                     "repeatX": repeat, "opacity": 1}})

    for name, depth, collision in (("World", 500, True), ("Blocks", 450, True), ("Decor", 700, False),
                                   ("Hazards", 480, False)):
        cells = sorted(level.layers[name], key=lambda cell: (cell["x"], cell["y"]))
        nodes.append({"id": gid("tiles:" + name), "kind": "TileLayer", "name": name, "enabled": True,
                      "layerId": "tiles",
                      "transform": {"position": [0, 0, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                      "tileLayer": {"tileset": tileset, "cellWidth": 32, "cellHeight": 32,
                                    "collisionEnabled": collision, "depth": depth, "cells": cells}})

    def instance(name, prefab, x, y, order=0):
        nodes.append({"id": gid("node:" + name), "kind": "GameObject", "name": name, "enabled": True,
                      "layerId": "actors", "order": order,
                      "transform": {"position": [x, y, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                      "gameObject": {"prefab": "Assets/Objects/%s.object.json" % prefab}})

    place_actors(level, instance)

    room = {
        "$schema": "genesis.room", "version": 1, "id": gid("room"), "name": room_name, "dimension": "TwoD",
        "settings": {"width": level.width * 32, "height": height, "depth": 720, "windowWidth": 1280,
                     "windowHeight": 720, "scaleViewportsWithWindow": True, "targetFps": 60, "fixedFps": 60,
                     "vsync": True, "captureMouse": False, "gridSize": 32, "snapEnabled": True,
                     "pixelArtSampling": True},
        "environment": {"backgroundColor": env_color, "fogEnabled": False, "postEffects": post_effects},
        "viewports": [{"enabled": True, "sourceX": 0, "sourceY": 0, "sourceWidth": 640, "sourceHeight": 360,
                       "portX": 0, "portY": 0, "portWidth": 1280, "portHeight": 720, "followTarget": "Explorer",
                       "followMarginX": 220, "followMarginY": follow_margin_y, "followSpeedX": 20,
                       "followSpeedY": follow_speed_y}],
        "layers": [{"id": "scenery", "name": "Backdrop", "order": 0, "enabled": True},
                   {"id": "tiles", "name": "Painted world", "order": 1, "enabled": True},
                   {"id": "actors", "name": "Actors and pickups", "order": 2, "enabled": True}],
        "nodes": nodes,
    }
    rel = "Rooms/%s.room.json" % room_name
    write_json(rel, room)
    write_meta(rel, "Room", room_name)
    return room


# Mask boxes relative to the instance position (left, top, right, bottom), for overlap checks.
MASKS = {"@": (-7, -34, 7, 0), "o": (-6, -8, 6, 8), "O": (-12, -12, 12, 12), "K": (-8, -66, 16, 0),
         "Z": (-14, -14, 14, 14), "p": (-10, -8, 10, 8), "j": (-8, -12, 8, 8), "d": (-10, -8, 10, 8)}


def center(c, r):
    return c * 32 + 16, r * 32 + 16


def feet(c, r):
    return c * 32 + 16, (r + 1) * 32


def place_common(level, instance):
    """Explorer, Level Rules, coins, big coins, checkpoint, prize markers. Returns the hero cell."""
    hero = level.cells("@")
    assert len(hero) == 1, "exactly one Explorer"
    x, y = feet(*hero[0])
    level.check_clear("Explorer", x, y, MASKS["@"])
    instance("Explorer", "Explorer", x, y, -100)
    instance("Level Rules", "Level Rules", 16, 16, -200)
    for i, (c, r) in enumerate(level.cells("K"), 1):
        x, y = feet(c, r)
        level.check_clear("Checkpoint", x, y, MASKS["K"])
        instance("Checkpoint %d" % i, "Checkpoint", x, y)
    for i, (c, r) in enumerate(level.cells("o"), 1):
        x, y = center(c, r)
        level.check_clear("Coin", x, y, MASKS["o"])
        instance("Coin %02d" % i, "Sun Coin", x, y)
    big = level.cells("O")
    assert len(big) == 3, "three Big Sun Coins, found %d" % len(big)
    for i, (c, r) in enumerate(big, 1):
        x, y = center(c, r)
        level.check_clear("Big Sun Coin", x, y, MASKS["O"])
        instance("Big Coin %d" % i, "Big Sun Coin", x, y)
    n = 0
    for ch, prefab in sorted(PRIZES.items()):
        for c, r in level.cells(ch):
            n += 1
            x, y = center(c, r)
            instance("%s %d" % (prefab, n), prefab, x, y)


def place_grotto(level, instance):
    place_common(level, instance)
    instance("Grotto Controller", "Grotto Controller", 48, 16, -150)
    glow = level.cells("Z")
    assert len(glow) == 1
    x, y = center(*glow[0])
    level.check_clear("Glowspore", x, y, MASKS["Z"])
    instance("Glowspore", "Glowspore", x, y)
    for i, (c, r) in enumerate(level.cells("p"), 1):
        x, y = center(c, r)
        level.check_clear("Bloatfin", x, y, MASKS["p"])
        instance("Bloatfin %d" % i, "Bloatfin", x, y)
    for i, (c, r) in enumerate(level.cells("j"), 1):
        x, y = center(c, r)
        level.check_clear("Driftjelly", x, y, MASKS["j"])
        instance("Driftjelly %d" % i, "Driftjelly", x, y)
    for i, zone in enumerate(level.currents, 1):
        instance("Current %d" % i, "Current", zone["x"], zone["y"])


def place_hollow(level, instance):
    place_common(level, instance)
    instance("Hollow Controller", "Hollow Controller", 48, 16, -150)
    for i, (c, r) in enumerate(level.cells("d"), 1):
        x, y = center(c, r)
        level.check_clear("Dusk Moth", x, y, MASKS["d"])
        instance("Dusk Moth %d" % i, "Dusk Moth", x, y)
    boss = level.cells("G")
    assert len(boss) == 1
    x, y = center(*boss[0])
    instance("Duskmantle", "Duskmantle", x, y, -50)


# ---------------------------------------------------------------- objects
def object_json(sprite, script_class, events):
    return {"schemaVersion": 2, "dimension": "TwoD", "sprite": sprite, "model": "",
            "components": [{"type": "ScriptComponent", "enabled": True, "props": {"ScriptClass": script_class}}],
            "events": events}


def write_object(name, sprite, events):
    rel = "Objects/%s.object.json" % name
    write_json(rel, object_json(sprite, name, list(events.keys())))
    write_meta(rel, "GameObject", name)
    folder = os.path.join(ASSETS, "Objects", name)
    if os.path.isdir(folder):
        for existing in os.listdir(folder):
            if existing.endswith(".pgsl") and existing[:-5] not in events:
                os.remove(os.path.join(folder, existing))
    for event, code in events.items():
        write_text("Objects/%s/%s.pgsl" % (name, event), code.strip("\n") + "\n")


LIB = """// Levels B Lib: tile helpers shared by Current zones (LEVELS-B).
// LbSolidAt(px, py): 1 when a solid World or Blocks tile covers the point.
// LbHeroFits(hx, hy): 1 when the Explorer's 14 x 34 mask at feet position (hx, hy) is clear.
function LbSolidAt(px, py) {
    var i = TileIndexAt(px, py, "World");
    if (i < 0) { i = TileIndexAt(px, py, "Blocks"); }
    if (i < 0) { return 0; }
    if (i <= 15 || (i >= 24 && i <= 28) || i == 33 || i == 34 || i == 38) { return 1; }
    return 0;
}

function LbHeroFits(hx, hy) {
    if (LbSolidAt(hx - 7, hy - 33) == 1 || LbSolidAt(hx + 7, hy - 33) == 1) { return 0; }
    if (LbSolidAt(hx - 7, hy - 17) == 1 || LbSolidAt(hx + 7, hy - 17) == 1) { return 0; }
    if (LbSolidAt(hx - 7, hy - 1) == 1 || LbSolidAt(hx + 7, hy - 1) == 1) { return 0; }
    return 1;
}
"""

# ----- Bloatfin: a round reef fish; puffs into a spiky ball (unstompable) when Pip comes close.
BLOATFIN_CREATE = """
// Bloatfin (LEVELS-B). Enemy convention: the Explorer stomps, shoots or star-touches it by adding
// to hurt; this object takes the damage, plays its own death and destroys itself.
// While puffed up (Pip within 84 px) stompable is 0, so touching it hurts Pip.
enemy = 1; hp = 1; stompable = 1; contactDamage = 1; points = 200; dead = 0; hurt = 0;
homeX = x; homeY = y; dir = -1; range = 80; puffed = 0; calm = 0; flash = 0; deadTime = 0;
bob = (x * 3 + y) % 360; hero = 0;
AnimationPlay("Swim", true);
ShaderSet("Assets/Shaders/Pixel Flash.shader.json"); ShaderSetParameter("Flash", 0);
"""

BLOATFIN_STEP = """
if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
AnimationSetSpeed(1);
if (dead == 1) {
    // Belly-up float and fade.
    deadTime = deadTime + dt;
    image_yscale = -1; y = y - 34 * dt; image_alpha = Max(0, 1 - deadTime / 0.8);
    if (deadTime >= 0.8) { InstanceDestroy(id); }
    return;
}
if (hurt > 0) {
    hp = hp - hurt; hurt = 0; flash = 0.3;
    if (hp <= 0) {
        dead = 1; deadTime = 0; stompable = 0; contactDamage = 0;
        AnimationPlay("Swim", false); AnimationSetSpeed(0);
        ParticleBurstAt("Assets/Particles/Bloatfin Pop.particle.json", x, y, 0, 1, 0);
        return;
    }
}
flash = Max(0, flash - dt);
ShaderSetParameter("Flash", Min(1, flash * 4));
if (hero == 0 || !InstanceExists(hero)) { hero = InstanceFind("Explorer", 0); }
var near = 0;
if (hero > 0) {
    var dx = InstanceGetX(hero) - x; var dy = InstanceGetY(hero) - 17 - y;
    if (dx * dx + dy * dy < 84 * 84) { near = 1; }
    if (Abs(dx) > 900) { return; }
}
if (near == 1) {
    calm = 0;
    if (puffed == 0) {
        puffed = 1; stompable = 0;
        AnimationPlay("Puffed", true);
        PlaySound("Assets/Audio/Bounce.audio.json", 0.45, 1.5, false);
        ParticleBurstAt("Assets/Particles/Puff Bubbles.particle.json", x, y, 0, 1, 0);
    }
} else if (puffed == 1) {
    calm = calm + dt;
    if (calm >= 1.1) { puffed = 0; stompable = 1; AnimationPlay("Swim", true); }
}
bob = (bob + dt * 120) % 360;
var swimSpeed = 38;
if (puffed == 1) { swimSpeed = 6; }
var nx = x + dir * swimSpeed * dt;
if (nx < homeX - range || nx > homeX + range || LbSolidAt(nx + dir * 12, y) == 1) { dir = -dir; }
else { x = nx; }
y = homeY + Sin(bob) * 6;
if (puffed == 1) { y = homeY + Sin(bob) * 2; }
image_xscale = dir;
"""

# ----- Driftjelly: drifts on a slow figure-eight; never stompable; spores pop it.
JELLY_CREATE = """
// Driftjelly (LEVELS-B). Never stompable (touching it stings); a Spore Shot or the Starlit
// Firefly pops it (enemy convention: hurt is added by the Explorer or its Spore Shot).
enemy = 1; hp = 1; stompable = 0; contactDamage = 1; points = 300; dead = 0; hurt = 0;
homeX = x; homeY = y; phase = (x * 7 + y * 3) % 360; flash = 0; deadTime = 0; hero = 0;
AnimationPlay("Drift", true);
ShaderSet("Assets/Shaders/Pixel Flash.shader.json");
ShaderSetParameter("Shimmer", 0.5); ShaderSetParameter("ShimmerSpeed", 0.6);
"""

JELLY_STEP = """
if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
AnimationSetSpeed(1);
if (dead == 1) {
    deadTime = deadTime + dt;
    image_alpha = Max(0, 1 - deadTime / 0.45);
    image_xscale = 1 + deadTime; image_yscale = Max(0.1, 1 - deadTime * 1.6);
    if (deadTime >= 0.45) { InstanceDestroy(id); }
    return;
}
if (hurt > 0) {
    hp = hp - hurt; hurt = 0; flash = 0.3;
    if (hp <= 0) {
        dead = 1; deadTime = 0; contactDamage = 0;
        ParticleBurstAt("Assets/Particles/Jelly Sparkle.particle.json", x, y, 0, 1, 0);
        PlaySound("Assets/Audio/Spore Hit.audio.json", 0.7, 1.2, false);
        return;
    }
}
flash = Max(0, flash - dt);
ShaderSetParameter("Flash", Min(1, flash * 4));
if (hero == 0 || !InstanceExists(hero)) { hero = InstanceFind("Explorer", 0); }
if (hero > 0 && Abs(InstanceGetX(hero) - x) > 900) { return; }
phase = (phase + dt * 55) % 720;
// Pulse-and-glide: it rises on each bell pulse and sinks slowly between them.
x = homeX + Sin(phase * 0.5) * 18;
y = homeY + Sin(phase) * 40;
image_yscale = 1 + Sin(phase * 4) * 0.08;
image_xscale = 1 - Sin(phase * 4) * 0.05;
"""

# ----- Current: an invisible rectangular zone that pushes the Explorer.
CURRENT_CREATE_HEAD = """
// Current (LEVELS-B). An invisible water current: while the Explorer's chest is inside the
// zone it is pushed (forceX, forceY px/s) directly, never into a solid tile, on top of its own
// swimming. Zones are placed at their top-left corner; size and force come from the table
// below, written by BuildTools/TemplateContent/MushroomMeadow/levels_b_gen.py from the room map.
// Streak particles show the flow while the zone is on screen.
zoneW = 96; zoneH = 96; forceX = 0; forceY = 0; streak = 0; hero = 0;
streakAsset = "Assets/Particles/Current Streak Right.particle.json";
"""

CURRENT_STEP = """
if (RoomValueGet("frozen", 0) == 1) { return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
if (hero == 0 || !InstanceExists(hero)) { hero = InstanceFind("Explorer", 0); }
if (hero > 0) {
    var hx = InstanceGetX(hero); var hy = InstanceGetY(hero);
    var cy = hy - 17;
    if (hx > x && hx < x + zoneW && cy > y && cy < y + zoneH) {
        var px = forceX * dt; var py = forceY * dt;
        if (px != 0 && LbHeroFits(hx + px, hy) == 1) { hx = hx + px; InstanceSetX(hero, hx); }
        if (py != 0 && LbHeroFits(hx, hy + py) == 1) { InstanceSetY(hero, hy + py); }
    }
}
// Streaks only while the zone overlaps the view.
var vx0 = ViewGetPosX(0); var vy0 = ViewGetPosY(0);
if (x + zoneW < vx0 || x > vx0 + 640 || y + zoneH < vy0 || y > vy0 + 360) { return; }
streak = streak - dt;
if (streak <= 0) {
    streak = 0.16;
    ParticleBurstAt(streakAsset, x + RandomRange(8, zoneW - 8), y + RandomRange(8, zoneH - 8), 0, 1, 0);
}
"""


def current_create(level):
    lines = [CURRENT_CREATE_HEAD.strip("\n")]
    names = {">": "Right", "<": "Left", "A": "Up", "V": "Down"}
    for zone in level.currents:
        lines.append(
            "if (Abs(x - %d) < 2 && Abs(y - %d) < 2) { zoneW = %d; zoneH = %d; forceX = %d; forceY = %d; "
            "streakAsset = \"Assets/Particles/Current Streak %s.particle.json\"; }"
            % (zone["x"], zone["y"], zone["w"], zone["h"], zone["fx"], zone["fy"], names[zone["char"]]))
    return "\n".join(lines) + "\n"


# ----- Grotto Controller: ambient bubbles, vents, underwater post-effect breathing.
def grotto_controller_create(level):
    vents = [center(c, r) for c, r in level.cells("*")]
    lines = [
        "// Grotto Controller (LEVELS-B): one per Tidepool Grotto room. Rising ambient bubbles,",
        "// bubble vents on the sea floor, and the Underwater post effect's gentle swell. The room",
        "// lists \"Underwater\" in environment.postEffects; Level Rules publishes underwater = 1.",
        "// Keep the 2D light mask fully lit: Particle2DBurst / Light2DPoint would otherwise switch",
        "// it on at the engine's default ambient (0.32) and darken this sunlit sea.",
        "Light2DAmbient(1, 0, 0, 0);",
        "SpawnParticleEmitter(\"Assets/Particles/Grotto Bubbles.particle.json\", RoomGetWidth() / 2, RoomGetHeight() - 40, 0, 1);",
        "SpawnParticleEmitter(\"Assets/Particles/Grotto Bubbles.particle.json\", RoomGetWidth() / 2, RoomGetHeight() / 2, 0, 1);",
        "ventCount = %d; ventIndex = 0; ventTimer = 0; ventX = 0; ventY = 0; swell = 0;" % len(vents),
        "PostEffectSetParameter(\"Underwater\", \"Wave\", 1);",
    ]
    return "\n".join(lines) + "\n"


def grotto_controller_step(level):
    vents = [center(c, r) for c, r in level.cells("*")]
    pick = "\n".join("    if (ventIndex == %d) { ventX = %d; ventY = %d; }" % (i, x, y + 12)
                     for i, (x, y) in enumerate(vents))
    return """
if (RoomValueGet("frozen", 0) == 1) { return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
swell = (swell + dt * 36) % 360;
PostEffectSetParameter("Underwater", "Wave", 0.85 + Sin(swell) * 0.3);
ventTimer = ventTimer - dt;
if (ventTimer <= 0 && ventCount > 0) {
    ventTimer = 0.22;
    ventIndex = (ventIndex + 1) % ventCount;
@PICK@
    var vx0 = ViewGetPosX(0); var vy0 = ViewGetPosY(0);
    if (ventX > vx0 - 32 && ventX < vx0 + 672 && ventY > vy0 - 32 && ventY < vy0 + 400) {
        ParticleBurstAt("Assets/Particles/Vent Bubbles.particle.json", ventX + RandomRange(-6, 6), ventY, 0, 1, 0);
    }
}
""".replace("@PICK@", pick)


# ----- Gloom Orb: Duskmantle's projectile.
ORB_CREATE = """
// Gloom Orb (LEVELS-B): Duskmantle's projectile. Enemy convention with stompable 0 and hp 1:
// a Spore Shot pops it; touching it hurts Pip. Duskmantle sets vx / vy after CreateInstance.
enemy = 1; hp = 1; stompable = 0; contactDamage = 1; points = 50; dead = 0; hurt = 0;
vx = 0; vy = 140; life = 6; touch = 0; glow = 700 + id % 60; hero = 0;
AnimationPlay("Pulse", true);
"""

ORB_STEP = """
if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
AnimationSetSpeed(1);
if (hero == 0 || !InstanceExists(hero)) { hero = InstanceFind("Explorer", 0); }
var pop = 0;
if (hurt > 0 || dead == 1) { pop = 1; }
life = life - dt;
if (life <= 0) { pop = 1; }
x = x + vx * dt; y = y + vy * dt;
if (LbSolidAt(x, y + 5) == 1 || LbSolidAt(x, y - 5) == 1 || y > RoomGetHeight() + 32) { pop = 1; }
// Touching Pip: stay for two frames so the Explorer's own collision check sees the orb, then pop.
if (hero > 0 && touch == 0) {
    var hx = InstanceGetX(hero); var hy = InstanceGetY(hero);
    if (x + 6 > hx - 7 && x - 6 < hx + 7 && y + 6 > hy - 34 && y - 6 < hy) { touch = 0.04; }
}
if (touch > 0) { touch = touch - dt; if (touch <= 0) { pop = 1; } }
if (pop == 1) {
    Particle2DBurst("Assets/Particles/Orb Pop.particle.json", x, y, 14, 90);
    Light2DRemove(glow);
    InstanceDestroy(id);
    return;
}
Light2DPoint(glow, x, y, 56, 0.9, 0.72, 0.38, 1.0);
"""


# ----- Hollow Controller: violet lamps, motes, lightning, Gloom post-effect pulse.
def hollow_controller_create(level):
    lamps = []
    for (c, r) in sorted((cell["x"], cell["y"]) for cell in level.layers["Decor"] if cell["tileY"] * 8 + cell["tileX"] == 35):
        lamps.append((c * 32 + 16, r * 32 + 10, 150, 1.0, 0.66, 0.42, 1.0))
    # Amethyst spire tops glow softly.
    crystals = sorted((cell["x"], cell["y"]) for cell in level.layers["World"] if cell["tileY"] * 8 + cell["tileX"] == 33)
    tops = [(c, r) for (c, r) in crystals if (c, r - 1) not in set(crystals) and (c - 1, r) not in set(crystals)]
    for c, r in tops:
        lamps.append((c * 32 + 32, r * 32 + 8, 110, 0.8, 0.78, 0.5, 1.0))
    lines = [
        "// Hollow Controller (LEVELS-B): one per Duskmantle Hollow room. Violet lamp lights (the room",
        "// is dark: Level Rules sets the ambient), drifting moth-silk motes, distant lightning flashes",
        "// and the pulse of the Gloom post effect, which quickens during the boss fight",
        "// (RoomValue bossFight 1) and lifts once Duskmantle is defeated (bossFight 2).",
        "RoomValueSet(\"bossFight\", 0);",
    ]
    for i, (x, y, radius, intensity, r, g, b) in enumerate(lamps):
        lines.append("Light2DPoint(%d, %d, %d, %d, %s, %s, %s, %s);" % (600 + i, x, y, radius, intensity, r, g, b))
    lines += [
        "flash = 0; flashTimer = 5; flashTwice = 0; pulse = 0.45; gloom = 1; moteTimer = 0;",
        "PostEffectSetParameter(\"Gloom\", \"Pulse\", pulse); PostEffectSetParameter(\"Gloom\", \"Gloom\", gloom);",
        "PostEffectSetParameter(\"Gloom\", \"Flash\", 0);",
    ]
    return "\n".join(lines) + "\n"


HOLLOW_STEP = """
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
var fight = RoomValueGet("bossFight", 0);
// Lightning: a bright flash, sometimes a second flicker, more often during the fight.
if (fight != 2) {
    flashTimer = flashTimer - dt;
    if (flashTimer <= 0) {
        flash = 1; flashTwice = 0.16;
        flashTimer = 6 + Random(7);
        if (fight == 1) { flashTimer = 2.5 + Random(3); }
    }
}
if (flashTwice > 0) { flashTwice = flashTwice - dt; if (flashTwice <= 0 && Random(1) < 0.6) { flash = 0.8; } }
flash = Max(0, flash - dt * 3.2);
var targetPulse = 0.45; var targetGloom = 1;
if (fight == 1) { targetPulse = 1; }
if (fight == 2) { targetPulse = 0; targetGloom = 0.2; }
pulse = pulse + (targetPulse - pulse) * Min(1, dt * 1.5);
gloom = gloom + (targetGloom - gloom) * Min(1, dt * 0.6);
PostEffectSetParameter("Gloom", "Pulse", pulse);
PostEffectSetParameter("Gloom", "Gloom", gloom);
PostEffectSetParameter("Gloom", "Flash", flash * 0.8);
PostEffectSetParameter("Gloom", "Speed", 0.45 + pulse * 0.5);
if (RoomValueGet("frozen", 0) == 1) { return; }
// Moth-silk motes drift up around the view.
moteTimer = moteTimer - dt;
if (moteTimer <= 0) {
    moteTimer = 0.18;
    Particle2DBurst("Assets/Particles/Hollow Motes.particle.json", ViewGetPosX(0) + Random(640), ViewGetPosY(0) + 120 + Random(260), 1, 90);
}
"""


# ----- Duskmantle: the final boss.
def duskmantle_create(level, arena_left, arena_right, floor_y):
    return """
// Duskmantle (LEVELS-B), the dusk-moth queen and final boss of Duskmantle Hollow.
// Enemy convention: enemy/hp/stompable/contactDamage/points/dead/hurt. She is stompable only
// while Dazed after a swoop; Spore Shots hurt her any time she is not recovering (0.9 s).
// 6 hits, three phases by health (6-5 swoops; 4-3 swoops + Gloom Orb volleys; 2-1 adds summoned
// Dusk Moths). RoomValue bossFight: 1 when her intro starts, 2 when she is defeated.
// On defeat she spawns the last Glowspore in the middle of the nest. If Pip loses a life the fight
// resets (full health, hidden above the nest until he drops in again). Stomps alone win the fight:
// she is harmless while rising after a daze, so a stomp bounce never costs a heart.
// Arena constants are written by levels_b_gen.py from the room map.
enemy = 1; hp = 6; maxHp = 6; stompable = 0; contactDamage = 1; points = 500; dead = 0; hurt = 0;
arenaL = %d; arenaR = %d; floorY = %d; arenaCx = (arenaL + arenaR) / 2;
homeX = x; homeY = y; y = homeY - 320; image_alpha = 0;
state = 0; stateTime = 0; hoverT = 0; attackTimer = 2; cycle = 0; phase = 1;
sx = x; sy = y; targetX = x; flash = 0; invuln = 0; shots = 0; volleyTimer = 0; summoned = 0;
starTimer = 0; trailTimer = 0; boomTimer = 0; deathX = x; deathY = y; spawned = 0;
bannerTime = 0; bannerKind = 0; shownHp = 6; hero = 0; glow = 690; heroLives = -1; resets = 0;
RoomValueSet("bossFight", 0); RoomValueSet("bossHp", hp);
AnimationPlay("Fly", true);
ShaderSet("Assets/Shaders/Pixel Flash.shader.json"); ShaderSetParameter("Flash", 0);
""" % (arena_left, arena_right, floor_y)


DUSKMANTLE_STEP = """
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
AnimationSetSpeed(1);
if (hero == 0 || !InstanceExists(hero)) { hero = InstanceFind("Explorer", 0); }
var hx = arenaCx; var hy = floorY;
if (hero > 0) { hx = InstanceGetX(hero); hy = InstanceGetY(hero); }
// ---- Pip lost a life (he respawns at the checkpoint, outside the nest): the fight resets cleanly.
// Full health, hidden above the nest again, her orbs and summoned moths cleared, music back to normal.
if (hero > 0) {
    var livesNow = InstanceVariableGet(hero, "lives");
    if (heroLives >= 0 && livesNow < heroLives && dead == 0 && state != 0) {
        resets = resets + 1;
        hp = maxHp; shownHp = maxHp; state = 0; stateTime = 0; hoverT = 0; cycle = 0; attackTimer = 2;
        stompable = 0; contactDamage = 1; hurt = 0; invuln = 0; flash = 0; bannerTime = 0; shots = 0;
        x = homeX; y = homeY - 320; image_alpha = 0; image_xscale = 1;
        AnimationPlay("Fly", true);
        Light2DRemove(glow);
        RoomValueSet("bossFight", 0); RoomValueSet("bossHp", hp);
        var no = InstanceNumber("Gloom Orb");
        for (var io = 0; io < no; io++) { var orbR = InstanceFind("Gloom Orb", io); if (orbR > 0) { InstanceVariableSet(orbR, "dead", 1); } }
        var nm = InstanceNumber("Dusk Moth");
        for (var im = 0; im < nm; im++) {
            var mothR = InstanceFind("Dusk Moth", im);
            if (mothR > 0 && InstanceGetX(mothR) > arenaL) { InstanceVariableSet(mothR, "hurt", InstanceVariableGet(mothR, "hurt") + 9); }
        }
    }
    heroLives = livesNow;
}
// Pip is in the nest once he has dropped below the gate ledge.
var inside = 0;
if (hero > 0 && hx > arenaL + 8 && hx < arenaR && hy > floorY - 120) { inside = 1; }
var shakeOk = 1;
if (GlobalExists("shake") && GlobalGet("shake") == 0) { shakeOk = 0; }
flash = Max(0, flash - dt);
invuln = Max(0, invuln - dt);
bannerTime = Max(0, bannerTime - dt);
ShaderSetParameter("Flash", Min(1, flash * 3));
shownHp = shownHp + (hp - shownHp) * Min(1, dt * 6);

// ---- damage (enemy convention: the Explorer or its Spore Shot adds to hurt)
if (hurt > 0 && dead == 0) {
    if (state < 2 || invuln > 0) { hurt = 0; }
    else {
        hurt = 0; hp = hp - 1; invuln = 0.9; flash = 0.35;
        RoomValueSet("bossHp", hp);
        PlaySound("Assets/Audio/Boss Hit.audio.json", 1, 1, false);
        if (shakeOk == 1) { ViewShake(0, 4, 0.3); }
        Particle2DBurst("Assets/Particles/Gloom Burst.particle.json", x, y, 24, 90);
        AnimationPlay("Hurt", true);
        if (hp <= 0) {
            // ---- defeat: clear her minions, start the death sequence
            dead = 1; stompable = 0; contactDamage = 0; state = 9; stateTime = 0;
            deathX = x; deathY = y; boomTimer = 0;
            RoomValueSet("bossFight", 2);
            bannerTime = 4; bannerKind = 2;
            PlaySound("Assets/Audio/Boss Roar.audio.json", 1, 0.8, false);
            var n = InstanceNumber("Gloom Orb");
            for (var i = 0; i < n; i++) { var o = InstanceFind("Gloom Orb", i); if (o > 0) { InstanceVariableSet(o, "dead", 1); } }
            n = InstanceNumber("Dusk Moth");
            for (var k = 0; k < n; k++) {
                var m = InstanceFind("Dusk Moth", k);
                if (m > 0 && InstanceGetX(m) > arenaL) { InstanceVariableSet(m, "hurt", InstanceVariableGet(m, "hurt") + 9); }
            }
        } else {
            if (hp == 4 || hp == 2) {
                bannerTime = 2.2; bannerKind = 1;
                PlaySound("Assets/Audio/Boss Roar.audio.json", 0.9, 1.1, false);
                if (shakeOk == 1) { ViewShake(0, 6, 0.6); }
            }
            if (state == 5) {
                // Knocked out of her daze: she flees upward at once. She is harmless while she rises
                // (state 6), so Pip's stomp bounce can never carry him back into her.
                state = 6; stateTime = 0; stompable = 0; contactDamage = 0; sx = x; sy = y;
            }
        }
    }
}
phase = 1;
if (hp <= 4) { phase = 2; }
if (hp <= 2) { phase = 3; }
if (state != 0 && spawned == 0) { Light2DPoint(glow, x, y, 170, 1.2, 0.7, 0.4, 1.0); }

// ---- 9: dying
if (state == 9) {
    stateTime = stateTime + dt;
    AnimationPlay("Hurt", true);
    x = deathX + Sin(stateTime * 1500) * 3;
    y = deathY + ((floorY - 60) - deathY) * Min(1, stateTime / 2.6);
    boomTimer = boomTimer - dt;
    if (boomTimer <= 0 && stateTime < 2.8) {
        boomTimer = 0.17; flash = 0.2;
        Particle2DBurst("Assets/Particles/Gloom Burst.particle.json", x + RandomRange(-56, 56), y + RandomRange(-44, 44), 18, 90);
        PlaySound("Assets/Audio/Boss Hit.audio.json", 0.5, 0.7 + Random(0.5), false);
        if (shakeOk == 1) { ViewShake(0, 3, 0.2); }
    }
    if (stateTime > 2.4) { image_alpha = Max(0, 1 - (stateTime - 2.4) / 0.8); }
    if (stateTime >= 3.2 && spawned == 0) {
        spawned = 1;
        Particle2DBurst("Assets/Particles/Glowspore Reveal.particle.json", arenaCx, floorY - 80, 64, 90);
        Particle2DBurst("Assets/Particles/Gloom Burst.particle.json", x, y, 48, 90);
        if (shakeOk == 1) { ViewShake(0, 8, 0.8); }
        // The last Glowspore drives the twilight back: the nest brightens.
        Light2DAmbient(0.72, 0.06, 0.04, 0.12);
        Light2DPoint(689, arenaCx, floorY - 80, 260, 1.5, 1.0, 0.9, 0.6);
        Light2DRemove(glow);
        CreateInstance("Glowspore", arenaCx, floorY - 80, 0);
    }
    if (stateTime >= 4.2) { InstanceDestroy(id); }
    return;
}

// ---- 0: hidden above the nest until Pip drops in
if (state == 0) {
    if (inside == 1 && hx > arenaL + 72) {
        state = 1; stateTime = 0; image_alpha = 1;
        RoomValueSet("bossFight", 1);
        bannerTime = 3.6; bannerKind = 0;
        PlaySound("Assets/Audio/Boss Roar.audio.json", 1, 1, false);
        if (shakeOk == 1) { ViewShake(0, 5, 1.0); }
    }
    return;
}
stateTime = stateTime + dt;
var faceDir = 1;
if (hx < x) { faceDir = -1; }

// ---- 1: intro descent
if (state == 1) {
    var k1 = Min(1, stateTime / 2.0);
    y = (homeY - 320) + 320 * (1 - (1 - k1) * (1 - k1));
    x = homeX + Sin(stateTime * 90) * 18;
    image_xscale = faceDir;
    if (stateTime >= 2.6) { state = 2; stateTime = 0; attackTimer = 1.0; AnimationPlay("Fly", true); }
    return;
}

// ---- 2: hover and choose an attack
if (state == 2) {
    hoverT = hoverT + dt;
    var span = (arenaR - arenaL) * 0.3;
    var tx = homeX + Sin(hoverT * 38) * span; var ty = homeY + Sin(hoverT * 76) * 26;
    x = x + (tx - x) * Min(1, dt * 2.5); y = y + (ty - y) * Min(1, dt * 2.5);
    image_xscale = faceDir;
    if (invuln <= 0) { AnimationPlay("Fly", true); }
    if (inside == 1) { attackTimer = attackTimer - dt; }
    if (attackTimer <= 0) {
        cycle = cycle + 1; stateTime = 0;
        state = 3;
        if (phase == 2 && cycle % 2 == 0) { state = 7; }
        if (phase == 3) {
            if (cycle % 3 == 0) { state = 8; }
            if (cycle % 3 == 2) { state = 7; }
        }
        if (state == 3) {
            AnimationPlay("Swoop", true); flash = 0.15;
            PlaySound("Assets/Audio/Moth Swoop.audio.json", 0.9, 1, false);
        }
        if (state == 7) { shots = 2 + phase; volleyTimer = 0.45; AnimationPlay("Fly", true); }
        if (state == 8) { summoned = 0; PlaySound("Assets/Audio/Boss Charge.audio.json", 0.9, 1, false); }
    }
    return;
}

// ---- 3: swoop wind-up (rise, quiver), then lock on to Pip
if (state == 3) {
    y = y + ((homeY - 46) - y) * Min(1, dt * 4);
    x = x + Sin(stateTime * 2400) * 0.8;
    image_xscale = faceDir;
    var windup = 0.75;
    if (phase == 3) { windup = 0.5; }
    if (stateTime >= windup) {
        state = 4; stateTime = 0; sx = x; sy = y;
        targetX = Clamp(hx, arenaL + 72, arenaR - 72);
@PLATFORMS@
        targetX = Clamp(targetX, arenaL + 72, arenaR - 72);
        PlaySound("Assets/Audio/Boss Charge.audio.json", 0.8, 1.2, false);
    }
    return;
}

// ---- 4: dive to the floor
if (state == 4) {
    var dur = 0.7;
    if (phase == 3) { dur = 0.55; }
    var k4 = Min(1, stateTime / dur);
    x = sx + (targetX - sx) * k4;
    y = sy + ((floorY - 32) - sy) * k4 * k4;
    image_xscale = 1;
    if (targetX < sx) { image_xscale = -1; }
    trailTimer = trailTimer - dt;
    if (trailTimer <= 0) { trailTimer = 0.03; Particle2DBurst("Assets/Particles/Gloom Trail.particle.json", x, y, 2, 90); }
    if (k4 >= 1) {
        state = 5; stateTime = 0; stompable = 1;
        AnimationPlay("Dazed", true);
        Particle2DBurst("Assets/Particles/Boss Dust.particle.json", x, floorY - 4, 28, 90);
        PlaySound("Assets/Audio/Crumble.audio.json", 0.9, 0.8, false);
        if (shakeOk == 1) { ViewShake(0, 5, 0.35); }
    }
    return;
}

// ---- 5: dazed on the floor (stompable)
if (state == 5) {
    var daze = 2.4;
    if (phase == 2) { daze = 2.0; }
    if (phase == 3) { daze = 1.6; }
    starTimer = starTimer - dt;
    if (starTimer <= 0) { starTimer = 0.25; Particle2DBurst("Assets/Particles/Daze Stars.particle.json", x, y - 34, 3, 90); }
    if (stateTime >= daze) {
        state = 6; stateTime = 0; stompable = 0; contactDamage = 0; sx = x; sy = y;
        AnimationPlay("Fly", true);
        PlaySound("Assets/Audio/Moth Swoop.audio.json", 0.6, 1.3, false);
    }
    return;
}

// ---- 6: rise back to the hover height
if (state == 6) {
    var k6 = Min(1, stateTime / 0.9);
    y = sy + (homeY - sy) * (1 - (1 - k6) * (1 - k6));
    if (k6 >= 1) {
        state = 2; stateTime = 0; contactDamage = 1;
        attackTimer = 2.2;
        if (phase == 2) { attackTimer = 1.8; }
        if (phase == 3) { attackTimer = 1.3; }
    }
    return;
}

// ---- 7: Gloom Orb volleys, fanned at Pip
if (state == 7) {
    y = y + ((homeY - 10) - y) * Min(1, dt * 3);
    image_xscale = faceDir;
    volleyTimer = volleyTimer - dt;
    if (volleyTimer <= 0 && shots > 0) {
        shots = shots - 1; volleyTimer = 0.6; flash = 0.12;
        PlaySound("Assets/Audio/Orb Fire.audio.json", 0.9, 1, false);
        var count = 3;
        if (phase == 3) { count = 5; }
        var aim = PointDirection(x, y + 24, hx, hy - 17);
        for (var j = 0; j < count; j++) {
            var d = aim + (j - (count - 1) / 2) * 17;
            var orb = CreateInstance("Gloom Orb", x, y + 24, 0);
            if (orb > 0) {
                InstanceVariableSet(orb, "vx", Cos(d) * 150);
                InstanceVariableSet(orb, "vy", -Sin(d) * 150);
            }
        }
    }
    if (shots <= 0 && volleyTimer <= 0) {
        state = 2; stateTime = 0; attackTimer = 1.2;
        if (phase == 3) { attackTimer = 0.9; }
    }
    return;
}

// ---- 8: summon Dusk Moths from the gloom
if (state == 8) {
    image_xscale = faceDir;
    if (stateTime < 0.6) { x = x + Sin(stateTime * 2000) * 1.2; }
    if (stateTime >= 0.6 && summoned == 0) {
        summoned = 1; flash = 0.25;
        PlaySound("Assets/Audio/Boss Roar.audio.json", 0.8, 1.25, false);
        if (InstanceNumber("Dusk Moth") < 4) {
            Particle2DBurst("Assets/Particles/Summon Smoke.particle.json", arenaL + 90, homeY - 30, 24, 90);
            Particle2DBurst("Assets/Particles/Summon Smoke.particle.json", arenaR - 90, homeY - 30, 24, 90);
            CreateInstance("Dusk Moth", arenaL + 90, homeY - 30, 0);
            CreateInstance("Dusk Moth", arenaR - 90, homeY - 30, 0);
        }
    }
    if (stateTime >= 1.4) { state = 2; stateTime = 0; attackTimer = 1.4; }
    return;
}
"""

def duskmantle_step(level, arena_left, floor_y):
    """DUSKMANTLE_STEP with her dive target kept off every nest platform low enough to cover her
    back (dazed, her mask top is floorY - 60; Pip needs 40 px above it to fall onto her)."""
    first_col = arena_left // 32
    spans = []
    for r in range(level.rows):
        if (r + 1) * 32 <= floor_y - 60 - 40:
            continue
        c = first_col
        while c < level.width:
            if level.grid[r][c] in "LM" and r * 32 < floor_y - 32:
                c0 = c
                while c + 1 < level.width and level.grid[r][c + 1] in "LM":
                    c += 1
                spans.append((c0 * 32, (c + 1) * 32))
            c += 1
    lines = ["        // Never land under a silk platform: her back must stay open to a stomp from above."]
    for x0, x1 in spans:
        lines.append("        if (targetX > %d && targetX < %d) { if (targetX < %d) { targetX = %d; } else { targetX = %d; } }"
                     % (x0 - 36, x1 + 36, (x0 + x1) // 2, x0 - 40, x1 + 40))
    return DUSKMANTLE_STEP.replace("@PLATFORMS@", "\n".join(lines))


DUSKMANTLE_DRAW_GUI = """
// Duskmantle's boss bar and banners (1280 x 720 design space, scaled like the HUD).
if (state == 0) { return; }
var sw = WindowGetWidth(); var sh = WindowGetHeight();
var sx = sw / 1280; var sy = sh / 720; var s = Min(sx, sy);
DrawSetFont("Consolas");
var barAlpha = 1;
if (state == 9) { barAlpha = Max(0, 1 - stateTime / 1.5); }
if (barAlpha > 0) {
    DrawSetAlpha(0.88 * barAlpha); DrawSetColorRgb(24, 12, 36);
    DrawRoundRect(330 * sx, 104 * sy, 950 * sx, 166 * sy, 10 * s, false);
    DrawSetAlpha(barAlpha); DrawSetColorRgb(196, 150, 255);
    DrawRoundRect(330 * sx, 104 * sy, 950 * sx, 166 * sy, 10 * s, true);
    DrawSetColorRgb(42, 20, 56);
    DrawTextScaled(352 * sx, 121 * sy, "DUSKMANTLE", 22 * s);
    DrawSetColorRgb(236, 214, 255);
    DrawTextScaled(350 * sx, 119 * sy, "DUSKMANTLE", 22 * s);
    for (var i = 0; i < maxHp; i++) {
        var x0 = 520 + i * 70;
        DrawSetColorRgb(52, 26, 70);
        DrawRectangle(x0 * sx, 120 * sy, (x0 + 60) * sx, 150 * sy);
        if (i < hp) {
            DrawSetColorRgb(168, 86, 236);
            if (invuln > 0.6) { DrawSetColorRgb(255, 240, 255); }
            DrawRectangle((x0 + 3) * sx, 123 * sy, (x0 + 57) * sx, 147 * sy);
            DrawSetColorRgb(214, 164, 255);
            DrawRectangle((x0 + 3) * sx, 123 * sy, (x0 + 57) * sx, 129 * sy);
        } else if (i < shownHp) {
            DrawSetColorRgb(255, 220, 255);
            DrawRectangle((x0 + 3) * sx, 123 * sy, (x0 + 57) * sx, 147 * sy);
        }
    }
}
if (bannerTime > 0) {
    var a = Min(1, bannerTime * 2);
    var title = "DUSKMANTLE";
    var sub = "Queen of the Endless Dusk";
    if (bannerKind == 1) { title = "DUSKMANTLE GROWS FURIOUS!"; sub = ""; }
    if (bannerKind == 2) { title = "DUSKMANTLE IS DEFEATED!"; sub = "The last Glowspore shines again"; }
    var size = 58 * s;
    if (bannerKind != 0) { size = 40 * s; }
    var tw = DrawTextWidth(title, size);
    DrawSetAlpha(0.55 * a); DrawSetColorRgb(16, 6, 26);
    DrawRectangle(0, 210 * sy, sw, 330 * sy);
    DrawSetAlpha(a); DrawSetColorRgb(60, 20, 80);
    DrawTextScaled((sw - tw) / 2 + 3 * s, 226 * sy + 3 * s, title, size);
    DrawSetColorRgb(232, 200, 255);
    if (bannerKind == 2) { DrawSetColorRgb(255, 236, 170); }
    DrawTextScaled((sw - tw) / 2, 226 * sy, title, size);
    if (sub != "") {
        var sws = DrawTextWidth(sub, 24 * s);
        DrawSetColorRgb(200, 180, 230);
        DrawTextScaled((sw - sws) / 2, 292 * sy, sub, 24 * s);
    }
}
DrawSetAlpha(1);
"""


# ---------------------------------------------------------------- shaders
UNDERWATER = """// Underwater: a Fullscreen post effect for Tidepool Grotto (LEVELS-B).
// Wave: SNES-style row wobble in whole game pixels (0 = off). Caustics: moving light web,
// brightest near the surface. Tint: teal grade. Murk: deep-water fade toward the bottom.
Texture2D SceneColor : register(t0);
SamplerState Linear : register(s0);
cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
cbuffer GenesisParameters : register(b5) { float Wave; float Caustics; float Tint; float Murk; };
struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
float CausticWeb(float2 p, float t)
{
    float a = sin(p.x * 1.7 + sin(p.y * 1.3 + t * 0.9) * 1.6 + t * 0.8);
    float b = sin(p.y * 2.1 + sin(p.x * 1.1 - t * 0.7) * 1.7 - t * 0.6);
    float c = sin((p.x + p.y) * 1.3 + sin(p.x * 0.7 + t) - t * 0.5);
    float web = abs(a + b + c) / 3.0;
    return pow(saturate(1.0 - web * 2.4), 5.0);
}
float4 MainPS(PreviewVSOut IN) : SV_Target
{
    float2 res = max(Resolution, float2(1.0, 1.0));
    float gameScale = max(res.y / 360.0, 1.0);
    float2 gp = floor(IN.UV * res / gameScale);
    float band = floor(gp.y / 2.0);
    float shift = round(sin(band * 0.21 + Time * 2.2) * Wave);
    float2 uv = IN.UV + float2(shift * gameScale / res.x, 0.0);
    float4 c = SceneColor.Sample(Linear, uv);
    float3 rgb = c.rgb;
    float glints = CausticWeb(gp / 24.0, Time * 1.1) * saturate(1.0 - IN.UV.y * 0.7);
    rgb += glints * Caustics * float3(0.55, 0.9, 0.95);
    float luma = dot(rgb, float3(0.299, 0.587, 0.114));
    float3 sea = lerp(float3(0.02, 0.16, 0.22), float3(0.6, 0.96, 0.92), luma);
    rgb = lerp(rgb, rgb * float3(0.8, 1.0, 1.04) + float3(0.0, 0.025, 0.05), saturate(Tint));
    rgb = lerp(rgb, sea, 0.16 * saturate(Tint));
    float2 p = IN.UV - 0.5;
    float deep = saturate(IN.UV.y * 0.8 + dot(p, p) * 0.9);
    rgb = lerp(rgb, float3(0.03, 0.17, 0.25), deep * 0.28 * saturate(Murk));
    return float4(saturate(rgb), c.a);
}
"""

GLOOM = """// Gloom: a Fullscreen post effect for Duskmantle Hollow (LEVELS-B).
// Gloom: violet twilight grade. Pulse: heart-beat purple vignette (Speed beats per second).
// Flash: lightning wash, set from script (Hollow Controller) and decayed there.
Texture2D SceneColor : register(t0);
SamplerState Linear : register(s0);
cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
cbuffer GenesisParameters : register(b5) { float Pulse; float Flash; float Gloom; float Speed; };
struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
float4 MainPS(PreviewVSOut IN) : SV_Target
{
    float2 res = max(Resolution, float2(1.0, 1.0));
    float4 c = SceneColor.Sample(Linear, IN.UV);
    float3 rgb = c.rgb;
    float luma = dot(rgb, float3(0.299, 0.587, 0.114));
    rgb = lerp(rgb, luma * float3(0.8, 0.64, 1.08), 0.38 * saturate(Gloom));
    float beat = 0.5 + 0.5 * sin(Time * 6.28318 * max(Speed, 0.05));
    beat = beat * beat;
    float2 p = IN.UV - 0.5;
    p.x *= res.x / res.y;
    float edge = saturate(dot(p, p) * 1.8);
    float3 violet = float3(0.3, 0.05, 0.42);
    rgb = lerp(rgb, rgb * 0.55 + violet * 0.45, edge * (0.3 + 0.45 * beat) * saturate(Pulse));
    rgb += violet * beat * 0.05 * saturate(Pulse);
    float f = saturate(Flash);
    rgb = lerp(rgb, float3(0.9, 0.86, 1.0), f * saturate(0.6 - IN.UV.y * 0.35));
    return float4(saturate(rgb), c.a);
}
"""


def param(name, value):
    return {"name": name, "type": "float", "value": [value]}


def write_shader(name, source, params):
    rel = "Shaders/%s.shader.json" % name
    write_json(rel, {"schemaVersion": 7, "pipeline": "Fullscreen", "authoringMode": "Code",
                     "targetType": "Fullscreen", "preset": "", "targetComponent": "", "entry": "MainPS",
                     "profile": "ps_5_0", "source": source, "previewAsset": "", "parameters": params})
    write_meta(rel, "Shader", name)


# ---------------------------------------------------------------- particles
def colour(r, g, b, a=1.0):
    return {"r": r, "g": g, "b": b, "a": a}


PARTICLE_BASE = {
    "preview2D": True, "maxParticles": 64, "emitRate": 0.0, "burstCount": 12, "loop": False, "duration": 1.0,
    "shape": "Sphere", "spreadDegrees": 30.0, "emitRadius": 0.1, "speed": 6.0, "speedVariance": 0.35,
    "gravity": 0.0, "drag": 1.0, "lifetime": 0.5, "lifetimeVariance": 0.25, "startSize": 0.3, "endSize": 0.08,
    "emissive": 1.0, "blendMode": "Alpha", "pixelSampling": True,
    "startColor": colour(1, 1, 1, 1), "endColor": colour(1, 1, 1, 0),
}


def particle(name, notes, **overrides):
    data = dict(PARTICLE_BASE)
    data.update(overrides)
    data["effectName"] = name
    data["notes"] = notes
    rel = "Particles/%s.particle.json" % name
    write_json(rel, data)
    write_meta(rel, "Particle", name)


def build_particles(grotto_width):
    # Grotto: phase-1 system (ParticleBurstAt / SpawnParticleEmitter, 12 px per unit, gravity + = up).
    particle("Grotto Bubbles", "Tidepool Grotto ambience: looping emitter across the whole room "
             "(Grotto Controller spawns two, low and mid). Bubbles wobble upward and fade.",
             loop=True, burstCount=0, emitRate=16.0, maxParticles=240, shape="Box",
             boxSizeX=round(grotto_width / 12.0, 1), boxSizeY=2.0, boxSizeZ=0.0, speed=0.3, speedVariance=0.5,
             drag=0.45, gravity=1.4, turbulenceStrength=0.6, lifetime=10.0, lifetimeVariance=0.35,
             startSize=0.2, endSize=0.34, startColor=colour(0.85, 0.98, 1.0, 0.8),
             midColor=colour(0.75, 0.95, 1.0, 0.6), endColor=colour(0.7, 0.92, 1.0, 0.0))
    particle("Vent Bubbles", "A puff of bubbles from a sea-floor vent (Grotto Controller, every vent in turn).",
             burstCount=5, maxParticles=12, shape="Sphere", emitRadius=0.3, spreadDegrees=20, speed=2.5,
             drag=0.6, gravity=3.0, turbulenceStrength=0.4, lifetime=1.8, startSize=0.22, endSize=0.42,
             startColor=colour(0.9, 1.0, 1.0, 0.9), endColor=colour(0.7, 0.95, 1.0, 0.0))
    particle("Puff Bubbles", "A Bloatfin gulps water and puffs up: a ring of small bubbles.",
             burstCount=10, maxParticles=12, shape="Sphere", emitRadius=0.6, speed=3.0, drag=2.5, gravity=2.0,
             lifetime=0.7, startSize=0.25, endSize=0.12,
             startColor=colour(0.92, 1.0, 1.0, 0.95), endColor=colour(0.7, 0.95, 1.0, 0.0))
    particle("Bloatfin Pop", "A defeated Bloatfin: bubbles and a few yellow scales.",
             burstCount=16, maxParticles=20, shape="Sphere", emitRadius=0.4, speed=5.0, drag=2.2, gravity=2.5,
             lifetime=0.8, startSize=0.32, endSize=0.1, colorJitter=0.2,
             startColor=colour(1.0, 0.95, 0.6, 1.0), midColor=colour(0.85, 0.98, 1.0, 0.9),
             endColor=colour(0.7, 0.92, 1.0, 0.0))
    particle("Jelly Sparkle", "A popped Driftjelly: pink-violet glimmers that drift upward.",
             burstCount=18, maxParticles=24, shape="Sphere", emitRadius=0.5, speed=4.0, drag=2.0, gravity=1.5,
             lifetime=0.9, startSize=0.34, endSize=0.06, blendMode="Additive", emissive=1.5,
             startColor=colour(1.0, 0.85, 1.0, 1.0), midColor=colour(0.95, 0.55, 0.95, 0.9),
             endColor=colour(0.55, 0.4, 1.0, 0.0))
    for name, wind, grav in (("Right", 9.0, 0.0), ("Left", -9.0, 0.0), ("Up", 0.0, 14.0), ("Down", 0.0, -8.0)):
        particle("Current Streak %s" % name, "A Current zone's flow (%s): pale streak-bubbles carried "
                 "along by the water." % name.lower(),
                 burstCount=3, maxParticles=6, shape="Sphere", emitRadius=0.8, speed=0.4, drag=0.0,
                 gravity=grav, windX=wind, lifetime=0.8, lifetimeVariance=0.3, startSize=0.16, endSize=0.08,
                 startColor=colour(0.88, 1.0, 1.0, 0.75), endColor=colour(0.7, 0.95, 1.0, 0.0))

    # Hollow: Particle2DBurst (pixel units, gravity + = up, direction 90 = up, spread +-degrees).
    p2d = dict(preview2D=True, emitRate=0.0, loop=False)
    particle("Gloom Burst", "Particle2DBurst (pixels). Purple-black puff when Duskmantle is hit or explodes.",
             maxParticles=64, burstCount=24, spreadDegrees=180, emitRadius=8, speed=120, speedVariance=0.5,
             drag=2.4, gravity=10, lifetime=0.75, lifetimeVariance=0.3, startSize=12, endSize=2,
             startColor=colour(0.95, 0.7, 1.0, 1.0), midColor=colour(0.55, 0.2, 0.8, 0.9),
             endColor=colour(0.15, 0.04, 0.25, 0.0), **p2d)
    particle("Gloom Trail", "Particle2DBurst (pixels). Shadow wisps behind Duskmantle's swoop.",
             maxParticles=8, burstCount=2, spreadDegrees=180, emitRadius=14, speed=12, drag=1.0, gravity=0,
             lifetime=0.45, startSize=14, endSize=3, startColor=colour(0.45, 0.2, 0.65, 0.8),
             endColor=colour(0.1, 0.02, 0.2, 0.0), **p2d)
    particle("Boss Dust", "Particle2DBurst (pixels). Dust and silk thrown up when Duskmantle crashes down.",
             maxParticles=32, burstCount=28, spreadDegrees=75, emitRadius=24, speed=110, speedVariance=0.5,
             drag=3.0, gravity=-160, lifetime=0.7, startSize=8, endSize=3,
             startColor=colour(0.78, 0.7, 0.86, 0.9), endColor=colour(0.4, 0.3, 0.5, 0.0), **p2d)
    particle("Daze Stars", "Particle2DBurst (pixels). Little stars over the dazed Duskmantle.",
             maxParticles=6, burstCount=3, spreadDegrees=180, emitRadius=10, speed=34, drag=1.5, gravity=20,
             lifetime=0.8, startSize=6, endSize=2, startColor=colour(1.0, 0.95, 0.6, 1.0),
             endColor=colour(1.0, 0.75, 0.3, 0.0), **p2d)
    particle("Orb Pop", "Particle2DBurst (pixels). A Gloom Orb bursting.",
             maxParticles=16, burstCount=14, spreadDegrees=180, emitRadius=3, speed=90, drag=3.0, gravity=0,
             lifetime=0.4, startSize=7, endSize=1, startColor=colour(0.95, 0.75, 1.0, 1.0),
             endColor=colour(0.4, 0.1, 0.6, 0.0), **p2d)
    particle("Hollow Motes", "Particle2DBurst (pixels). Glowing moth-silk motes drifting up in the Hollow.",
             maxParticles=2, burstCount=1, spreadDegrees=180, emitRadius=4, speed=7, drag=0.2, gravity=7,
             lifetime=4.0, lifetimeVariance=0.4, startSize=3.5, endSize=2,
             startColor=colour(0.85, 0.7, 1.0, 0.75), midColor=colour(0.7, 0.5, 1.0, 0.6),
             endColor=colour(0.5, 0.35, 0.9, 0.0), **p2d)
    particle("Summon Smoke", "Particle2DBurst (pixels). Gloom smoke where Duskmantle summons Dusk Moths.",
             maxParticles=32, burstCount=24, spreadDegrees=180, emitRadius=10, speed=60, drag=2.0, gravity=20,
             lifetime=0.8, startSize=14, endSize=4, startColor=colour(0.5, 0.25, 0.7, 0.9),
             endColor=colour(0.08, 0.02, 0.14, 0.0), **p2d)
    particle("Glowspore Reveal", "Particle2DBurst (pixels). Warm gold light bursting where the last "
             "Glowspore appears after Duskmantle's defeat.",
             maxParticles=64, burstCount=64, spreadDegrees=180, emitRadius=6, speed=170, speedVariance=0.6,
             drag=1.8, gravity=-20, lifetime=1.3, lifetimeVariance=0.3, startSize=9, endSize=2,
             startColor=colour(1.0, 1.0, 0.85, 1.0), midColor=colour(1.0, 0.85, 0.4, 0.95),
             endColor=colour(1.0, 0.55, 0.2, 0.0), **p2d)


# ---------------------------------------------------------------- preview
PLACEHOLDER = {
    "Bloatfin": (32, 28, 16, 14, (240, 200, 70)), "Driftjelly": (24, 32, 12, 16, (230, 140, 230)),
    "Duskmantle": (128, 112, 64, 56, (120, 60, 170)), "Dusk Moth": (32, 24, 16, 12, (150, 110, 200)),
    "Glowspore": (32, 32, 16, 16, (255, 240, 170)), "Level Rules": None, "Grotto Controller": None,
    "Hollow Controller": None, "Current": None,
}


def render_preview(room, out_name):
    try:
        from PIL import Image, ImageDraw
    except ImportError:
        print("PIL not available; skipping preview")
        return
    W, H = room["settings"]["width"], room["settings"]["height"]
    bgc = room["environment"]["backgroundColor"]
    canvas = Image.new("RGBA", (W, H), tuple(int(v * 255) for v in bgc[:3]) + (255,))
    draw = ImageDraw.Draw(canvas)
    cache = {}

    def load_image(asset):
        if asset in cache:
            return cache[asset]
        path = os.path.join(TEMPLATE, asset.replace("/", os.sep))
        result = None
        if os.path.exists(path):
            with open(path, encoding="utf-8") as handle:
                doc = json.load(handle)
            frame = doc["frames"][0]
            png_path = os.path.join(os.path.dirname(path), frame["source"].replace("/", os.sep))
            if os.path.exists(png_path):
                result = (doc, Image.open(png_path).convert("RGBA"))
        cache[asset] = result
        return result

    tile_colours = {}

    def tile_placeholder(idx):
        if idx in tile_colours:
            return tile_colours[idx]
        img = Image.new("RGBA", (32, 32), (0, 0, 0, 0))
        d = ImageDraw.Draw(img)
        if idx in (0, 5, 6):
            d.rectangle((0, 0, 31, 31), fill=(150, 120, 90, 255)); d.rectangle((0, 0, 31, 6), fill=(230, 210, 150, 255))
        elif idx in (1, 15, 24, 25, 38, 34):
            d.rectangle((0, 0, 31, 31), fill=(110, 90, 80, 255))
        elif idx == 7:
            d.rectangle((0, 0, 31, 31), fill=(120, 130, 140, 255), outline=(60, 60, 70, 255))
        elif idx in (8, 9, 10, 11):
            d.rectangle((0, 0, 31, 31), fill=(230, 120, 120, 255), outline=(120, 50, 60, 255))
        elif idx in (12, 13, 14):
            d.rectangle((0, 0, 31, 12), fill=(240, 220, 200, 255), outline=(120, 90, 80, 255))
        elif idx in (26, 27, 28):
            d.rectangle((0, 0, 31, 16), fill=(220, 200, 240, 255), outline=(110, 90, 130, 255))
        elif idx == 2:
            d.rectangle((0, 0, 31, 31), fill=(240, 150, 140, 255), outline=(90, 40, 40, 255))
        elif idx in (3, 4):
            d.rectangle((0, 0, 31, 31), fill=(250, 210, 80, 255) if idx == 3 else (160, 130, 90, 255), outline=(80, 50, 20, 255))
        elif idx == 32:
            d.polygon([(4, 31), (16, 4), (28, 31)], fill=(70, 40, 90, 255))
        elif idx == 33:
            d.rectangle((0, 0, 31, 31), fill=(170, 120, 230, 255), outline=(80, 40, 120, 255))
        elif idx == 35:
            d.ellipse((10, 4, 22, 16), fill=(255, 230, 180, 255)); d.line((16, 16, 16, 31), fill=(80, 60, 60, 255), width=2)
        else:
            d.rectangle((10, 14, 22, 31), fill=(90, 170, 110, 200))
        tile_colours[idx] = img
        return img

    items = []
    for node in room["nodes"]:
        pos = node["transform"]["position"]
        if node["kind"] == "Background":
            items.append((node["background"]["depth"], "bg", node))
        elif node["kind"] == "TileLayer":
            items.append((node["tileLayer"]["depth"], "tiles", node))
        else:
            items.append((-1 if node["name"] != "Explorer" else -2, "obj", node))
    items.sort(key=lambda item: -item[0])
    for depth, kind, node in items:
        pos = node["transform"]["position"]
        if kind == "bg":
            bg = node["background"]
            loaded = load_image(bg["asset"])
            if not loaded:
                continue
            doc, png = loaded
            if bg["layout"] == "StretchView":
                band = png.resize((640, 360))
                for y in range(0, H, 360):
                    for x in range(0, W, 640):
                        canvas.alpha_composite(band, (x, y))
            else:
                x = 0
                while x < W:
                    canvas.alpha_composite(png, (x, int(pos[1])))
                    x += png.width
                    if not bg.get("repeatX"):
                        break
        elif kind == "tiles":
            tl = node["tileLayer"]
            loaded = load_image(tl["tileset"])
            for cell in tl["cells"]:
                idx = cell["tileY"] * 8 + cell["tileX"]
                if loaded:
                    png = loaded[1]
                    tile = png.crop((cell["tileX"] * 32, cell["tileY"] * 32, cell["tileX"] * 32 + 32, cell["tileY"] * 32 + 32))
                else:
                    tile = tile_placeholder(idx)
                canvas.alpha_composite(tile, (cell["x"] * 32, cell["y"] * 32))
        else:
            prefab = node["gameObject"]["prefab"]
            name = prefab.split("/")[-1].replace(".object.json", "")
            obj_path = os.path.join(ASSETS, "Objects", name + ".object.json")
            sprite = None
            if os.path.exists(obj_path):
                with open(obj_path, encoding="utf-8") as handle:
                    sprite = json.load(handle).get("sprite") or None
            loaded = load_image(sprite) if sprite else None
            x, y = int(pos[0]), int(pos[1])
            if loaded:
                doc, png = loaded
                ox, oy = doc["origin"]["x"], doc["origin"]["y"]
                if 0 <= x - ox < W:
                    canvas.alpha_composite(png, (x - ox, max(0, y - oy)))
            elif name == "Current":
                pass
            elif PLACEHOLDER.get(name):
                w, h, ox, oy, col = PLACEHOLDER[name]
                draw.rectangle((x - ox, y - oy, x - ox + w, y - oy + h), outline=col + (255,), width=2)
                draw.text((x - ox + 2, y - oy + 2), name[:6], fill=col + (255,))
            elif name.startswith("Prize"):
                draw.text((x - 10, y - 6), name[6:10], fill=(40, 20, 0, 255))
            elif name not in PLACEHOLDER:
                draw.rectangle((x - 8, y - 16, x + 8, y), outline=(255, 0, 0, 255))
    # Current zones as translucent overlays with arrows.
    overlay = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    od = ImageDraw.Draw(overlay)
    for zone in getattr(room, "_currents", []) or []:
        pass
    for zone in CURRENT_OVERLAY.get(room["name"], []):
        od.rectangle((zone["x"], zone["y"], zone["x"] + zone["w"] - 1, zone["y"] + zone["h"] - 1),
                     fill=(255, 255, 255, 28), outline=(255, 255, 255, 90))
        cx, cy = zone["x"] + zone["w"] // 2, zone["y"] + zone["h"] // 2
        dx, dy = (zone["fx"] > 0) - (zone["fx"] < 0), (zone["fy"] > 0) - (zone["fy"] < 0)
        for k in range(-1, 2):
            ax, ay = cx + k * 48 * (1 - abs(dx)), cy + k * 48 * (1 - abs(dy))
            od.line((ax - dx * 14, ay - dy * 14, ax + dx * 14, ay + dy * 14), fill=(255, 255, 255, 160), width=3)
            od.polygon([(ax + dx * 20, ay + dy * 20), (ax + dx * 8 - dy * 8, ay + dy * 8 + dx * 8),
                        (ax + dx * 8 + dy * 8, ay + dy * 8 - dx * 8)], fill=(255, 255, 255, 160))
    canvas.alpha_composite(overlay)
    out_dir = os.path.join(ROOT, "TestResults", "MushroomMeadow")
    os.makedirs(out_dir, exist_ok=True)
    full = os.path.join(out_dir, "level_%s_full.png" % out_name)
    canvas.save(full)
    # Stacked strips (2048 px wide) so the whole level can be looked at in one picture.
    strip_w = 2048
    strips = (W + strip_w - 1) // strip_w
    stacked = Image.new("RGBA", (strip_w, strips * (H + 8)), (0, 0, 0, 255))
    for i in range(strips):
        part = canvas.crop((i * strip_w, 0, min(W, (i + 1) * strip_w), H))
        stacked.alpha_composite(part, (0, i * (H + 8)))
    path = os.path.join(out_dir, "level_%s.png" % out_name)
    stacked.save(path)
    print("preview", path)


CURRENT_OVERLAY = {}


# ---------------------------------------------------------------- main
def main():
    grotto = Level(GROTTO_SECTIONS, GROTTO_ROWS)
    hollow = Level(HOLLOW_SECTIONS, HOLLOW_ROWS)
    if "--map" in sys.argv:
        grotto.print_map("Tidepool Grotto")
        hollow.print_map("Duskmantle Hollow")

    write_text("Scripts/Levels B Lib.pgsl", LIB)
    write_meta("Scripts/Levels B Lib.pgsl", "Script", "Levels B Lib")

    write_shader("Underwater", UNDERWATER, [param("Wave", 1.0), param("Caustics", 0.32), param("Tint", 0.85),
                                            param("Murk", 0.7)])
    write_shader("Gloom", GLOOM, [param("Pulse", 0.45), param("Flash", 0.0), param("Gloom", 1.0),
                                  param("Speed", 0.6)])
    build_particles(grotto.width * 32)

    # Contract amendment 1: images that share an object's name are "<Name> Sprite".
    write_object("Bloatfin", "Assets/Sprites/Bloatfin Sprite.image.json", {"Create": BLOATFIN_CREATE, "Step": BLOATFIN_STEP})
    write_object("Driftjelly", "Assets/Sprites/Driftjelly Sprite.image.json", {"Create": JELLY_CREATE, "Step": JELLY_STEP})
    write_object("Current", "", {"Create": current_create(grotto), "Step": CURRENT_STEP})
    write_object("Grotto Controller", "", {"Create": grotto_controller_create(grotto),
                                           "Step": grotto_controller_step(grotto)})
    write_object("Gloom Orb", "Assets/Sprites/Gloom Orb Sprite.image.json", {"Create": ORB_CREATE, "Step": ORB_STEP})
    write_object("Hollow Controller", "", {"Create": hollow_controller_create(hollow), "Step": HOLLOW_STEP})

    arena_left = hollow.starts[-1] * 32
    arena_right = (hollow.width - 1) * 32
    floor_y = (HOLLOW_ROWS - 1) * 32
    write_object("Duskmantle", "Assets/Sprites/Duskmantle Sprite.image.json",
                 {"Create": duskmantle_create(hollow, arena_left, arena_right, floor_y),
                  "Step": duskmantle_step(hollow, arena_left, floor_y), "DrawGui": DUSKMANTLE_DRAW_GUI})

    grotto_room = make_room(
        grotto, "Tidepool Grotto", "tidepool-grotto", "Assets/Sprites/Grotto Tiles.image.json",
        [("water", "Grotto water", "Assets/Sprites/Grotto Water.image.json", "StretchView", 10000, False),
         ("reef", "Far reef", "Assets/Sprites/Grotto Reef Far.image.json", "Single", 9500, True),
         ("kelp", "Kelp forest", "Assets/Sprites/Grotto Kelp.image.json", "Single", 9000, True)],
        [0.07, 0.36, 0.45, 1], ["SNES Look", "Underwater"], 110, 14, place_grotto)
    hollow_room = make_room(
        hollow, "Duskmantle Hollow", "duskmantle-hollow", "Assets/Sprites/Hollow Tiles.image.json",
        [("sky", "Twilight sky", "Assets/Sprites/Hollow Sky.image.json", "StretchView", 10000, False),
         ("spires", "Crooked spires", "Assets/Sprites/Hollow Spires.image.json", "Single", 9500, True),
         ("mist", "Twilight mist", "Assets/Sprites/Hollow Mist.image.json", "Single", 9000, True)],
        [0.12, 0.07, 0.2, 1], ["SNES Look", "Gloom"], 110, 12, place_hollow)

    CURRENT_OVERLAY["Tidepool Grotto"] = grotto.currents
    for room, level in ((grotto_room, grotto), (hollow_room, hollow)):
        objects = [n for n in room["nodes"] if n["kind"] == "GameObject"]
        counts = {}
        for n in objects:
            key = n["gameObject"]["prefab"].split("/")[-1].replace(".object.json", "")
            counts[key] = counts.get(key, 0) + 1
        print("%s: %dx%d px, %d objects, solid cells %d" % (room["name"], room["settings"]["width"],
                                                          room["settings"]["height"], len(objects), len(level.solid)))
        print("   " + ", ".join("%s %d" % kv for kv in sorted(counts.items())))
    print("Grotto currents: %d zones" % len(grotto.currents))
    print("Hollow arena: x %d..%d, floor y %d" % (arena_left, arena_right, floor_y))
    if "--preview" in sys.argv:
        render_preview(grotto_room, "tidepool_grotto")
        render_preview(hollow_room, "duskmantle_hollow")


if __name__ == "__main__":
    main()
