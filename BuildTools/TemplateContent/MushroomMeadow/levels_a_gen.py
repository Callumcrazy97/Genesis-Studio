"""Mushroom Meadow LEVELS-A generator: Rootdeep Caverns (level 2) and Windswept Canopy (level 4).

Writes, deterministically, into Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets:
  Rooms      Rootdeep Caverns, Windswept Canopy (built from the ASCII maps below)
  Objects    Flit Bat, Glow Grub, Stalactite, Crumble Block, Pebble, Grumblecrag, Cavern Glow,
             Dusk Moth, Burr Hopper, Moving Leaf, Wind Controller
  Particles  Cave Dust, Crystal Glint, Bat Poof, Grub Glow, Shell Spark, Boss Debris, Boss Burst,
             Pebble Crack, Canopy Leaf, Gust Leaf, Moth Dust, Burr Fluff
  Shaders    Golden Hour (Canopy post effect), Cavern Damp (Caverns post effect)

    python levels_a_gen.py            write everything, render the previews, prove both rooms playable
    python levels_a_gen.py --map      also print the two maps
    python levels_a_gen.py --no-check skip the playability proof

The playability proof is reach_levels_a.py (Pip's real physics from hero_physics.py, plus moving
leaves, the pool and wind): from the start and the checkpoint the goal is reachable with no
power-ups at the design margin, and no reachable place is a soft-lock. It fails the run. The previews go to
TestResults/MushroomMeadow/level_<name>.png (raw) and level_<name>_lit.png (Caverns, lit like the game).
"""
import hashlib
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
ASSETS = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates", "Assets",
                      "TwoDShowcase", "Assets")
PREVIEW_DIR = os.path.join(ROOT, "TestResults", "MushroomMeadow")
STAMP = "2026-10-11T00:00:00Z"
ROWS = 12
CELL = 32


def guid(name):
    return hashlib.md5(("mushroom-meadow-levels-a:" + name).encode("utf-8")).hexdigest()


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


# =====================================================================================
# Maps. Rows 0..11 (32 px each; the room is 360 px tall so row 11 is half visible).
# Every non-empty row of a section must have the section's width.
#
# Shared legend
#   #  terrain (auto-tiled: top 0 / caps 5,6 / edges 24,25 / underside 38 / fill 1 or 15)   World
#   S  hard block (7)     C  crystal/amber block (33)     P..T  2-wide column (8-11)        World
#   =  scaffold / rope bridge (12-14)     M  crystal / giant-leaf ledge (26-28)            World
#   B  breakable (2)   ?  seed pod paying a coin (3)   1..5  seed pod with a Prize marker         Blocks
#      (1 Prize Glide, 2 Prize Spore, 3 Prize Shield, 4 Prize Star, 5 Prize Heart)
#   K  Crumble Block (Blocks tile 7 + object; falls 0.5 s after Pip stands on it, back after 4 s)
#   ^  spikes / thorns (32)                                                               Hazards
#   F  fake rock: drawn in front of Pip, not solid (secret passages)                        Secret
#   ~  water (Water layer; Pip swims)   q water + Sun Coin   J water + fake rock
#   Y  water + fake rock + Big Sun Coin
#   |  gate cell of the boss arena (Gate layer: blank until the fight closes it)
#   Actors: @ Explorer, o Sun Coin, O Big Sun Coin, k Checkpoint, u Bounce Mushroom
#   Caverns: b Flit Bat (hangs from the ceiling cell above), w Glow Grub, t Stalactite
#            (hangs from the ceiling cell above), G Grumblecrag
#   Canopy:  m Dusk Moth, H Burr Hopper, L Moving Leaf (top-left cell; its walking surface is
#            that row's top edge; motion from the section's leaf list), y Glide Seed, Z Glowspore
# =====================================================================================

CAVERN_DECOR = {"m": 16, "x": 17, "n": 18, "r": 19, "_": 20, "s": 21, "v": 22, "a": 23, "i": 29,
                "l": 35, "f": 36, "c": 37}
CANOPY_DECOR = {"g": 16, "f": 17, "n": 18, "r": 19, "e": 20, "h": 21, "v": 22, "x": 23, "i": 29,
                "l": 35, "F": 36, "c": 37}

CAVERN_SECTIONS = [
    ("Cave mouth: safe start, first lantern light, first coins, seed pod; a glint hides a pocket behind Pip", [
        "##############################",
        "##############################",
        "####v......l.......v.....#####",
        "##............................",
        "##............................",
        "##.........?..................",
        "##............................",
        "vF............................",
        "OF@...m....o.o.o...w....a...m.",
        "##############################",
        "##############################",
        "##############################",
    ], []),
    ("Drip gallery: first stalactites over two small pits", [
        "##############################",
        "##############################",
        "...t.........t..v.........t.l.",
        "",
        "",
        "........o.o...........ooo.....",
        "",
        "",
        "..m..........w.......x......f.",
        "########...########....#######",
        "########...########....#######",
        "########...########....#######",
    ], []),
    ("Crystal hall: Flit Bats, crystal ledges, the Spore Puff seed pod", [
        "##################################",
        "....b..........v...........b......",
        "",
        "..................oo..............",
        ".................MMMM.............",
        "",
        ".....?2?.....MMM.......MMM........",
        "..............i.........i.........",
        "..x....m......i...f.w...i..x......",
        "##################################",
        "##################################",
        "##################################",
    ], []),
    ("Crumble bridge over a bottomless drop, resting pillar, a stalactite on the pillar", [
        "##############################",
        "##############################",
        "######.......t.......v.....###",
        "###...........................",
        "",
        "..........ooo.....ooo.........",
        "",
        "",
        "..f.........................f.",
        "######KKKKKKKKSSKKKKKKKK######",
        "######........SS........######",
        "######........SS........######",
    ], []),
    ("Glow pool: scaffolds over a swimmable pool, coins underwater, Big Sun Coin in a hidden alcove", [
        "##############################",
        "##############################",
        "#####v...l........l.....v#####",
        "",
        "............oo....oo..........",
        "............==....==..........",
        "..m.....f..................x..",
        "########~~~~~~~~~~~~~~########",
        "########~~q~~~~~~q~~~~JJJ#####",
        "########~~~~q~~q~~~~~~JJY#####",
        "##############################",
        "##############################",
    ], []),
    # Column tops at 256 / 224 px: every spike trench (floor 320) is climbable at the design margin.
    ("Bat roost: stone columns over spikes, the Bark Shield seed pod", [
        "################################",
        "################################",
        "...b......b...l...v.....b...l...",
        "............3...................",
        "........oo..........oo....oo....",
        "",
        "",
        "............PP..................",
        "..m...PP....PP....PP....PP..f...",
        "####^^PP^^^^PP^^^^PP^^^^PP^^####",
        "################################",
        "################################",
    ], []),
    ("Mushroom shaft: bounce to the high crystal ledge (Big Sun Coin), crumble walkway", [
        "############################",
        "##.....v...........v......##",
        "...O........................",
        "..MMMM...KKKK...oo..........",
        "...i.i......................",
        "...i.i......................",
        "...i.i......?.?.............",
        "...i.i......................",
        "...i.i.u....w.......x...m...",
        "############################",
        "############################",
        "############################",
    ], []),
    ("Rail tunnel: low ceiling, a stalactite run, grubs on the rails", [
        "##############################",
        "##############################",
        "##############################",
        "####v.t..t..####...t.t.v######",
        "..............t...............",
        "",
        "......o.o.o......o.o.o........",
        "",
        "__w____________________w______",
        "###############...############",
        "###############...############",
        "###############...############",
    ], []),
    ("Antechamber: the checkpoint, a Heart Berry seed pod, lanterns", [
        "################",
        "################",
        "..l....v....l...",
        "",
        "",
        "........5.......",
        "",
        "",
        ".n...k......f...",
        "################",
        "################",
        "################",
    ], []),
    ("Boss arena (exactly one screen): Grumblecrag; the gate closes behind Pip", [
        "####################",
        "####################",
        "|..l......v.....l..#",
        "|..................#",
        "|..................#",
        "|..................#",
        "|..===........===..#",
        "|..................#",
        "|.m.......G.....m..#",
        "####################",
        "####################",
        "####################",
    ], []),
]

# Leaf motion: (axis "x"|"y", amplitude px, period s, phase degrees), one entry per L, ordered by
# column (left to right; top to bottom within a column) inside the section.
CANOPY_SECTIONS = [
    ("Treetop start: a calm branch, the Glide Seed, the first Burr Hopper", [
        "",
        "",
        "",
        "",
        "",
        "..........?...ooo.......",
        "",
        "",
        "..@...g.....y.....H..f..",
        "########################",
        "########################",
        "....TT............TT....",
    ], []),
    ("Rope bridges: first Dusk Moths, a broken plank", [
        "",
        "",
        "",
        "",
        "........m..........m..........",
        "",
        "..........ooo......ooo........",
        "",
        "#####========...========..####",
        "#####..l..........l.......####",
        "..TT.......................TT.",
        "..TT.......................TT.",
    ], []),
    ("Leaf crossing: three Moving Leaf platforms over the drop (the rising middle leaf reaches a Big Sun Coin)", [
        "",
        "...............O................",
        "",
        "..........o..o..o..o..o.........",
        "",
        "",
        "",
        "..............L..............H..",
        "#####..L.............L.....#####",
        "#####......................#####",
        "..TT........................TT..",
        "..TT........................TT..",
    ], [("x", 64, 3.0, 0), ("y", 48, 2.6, 90), ("x", 64, 3.2, 180)]),
    ("Glide gap: Prize Glide on the high branch; glide across, or ride two leaves", [
        "",
        "",
        ".........1........................",
        "",
        "..........o..o..o..o..o..o........",
        ".......#####......................",
        "....MMM#####......................",
        "..................L...............",
        "####......L.......................",
        "####....................##########",
        "..TT......................TT......",
        "..TT......................TT......",
    ], [("x", 96, 3.6, 0), ("y", 40, 2.8, 0)]),
    ("Thorny boughs: Burr Hoppers, thorn patches, the Starlit Firefly seed pod, checkpoint", [
        "",
        "",
        "",
        "",
        "",
        "..........4...................",
        "...............oo.....oo......",
        "..............###.....###.....",
        "...H.......c.H............k...",
        "######^^^#########^^^#########",
        "##############################",
        "....TT................TT......",
    ], []),
    ("Gust gauntlet: broken rope bridges while the wind blows, moths overhead, Heart Berry box", [
        "",
        "",
        "",
        ".....m.............m..........",
        "...............5..............",
        ".........ooo.........ooo......",
        "",
        "",
        "=======....======....=====...#",
        "..l..........l........l......#",
        "",
        "",
    ], []),
    ("Long glide: bounce to the secret sky branch (Big Sun Coin), glide or ride three leaves", [
        "",
        "..........O.........................",
        ".......######.......................",
        "............................oo......",
        ".........MMM........................",
        "...............o..o..o..o..o........",
        "......MMM.....L...........L.........",
        "...u.................L........######",
        "######......................m.######",
        "######..........................TT..",
        "..TT............................TT..",
        "..TT............................TT..",
    ], [("x", 80, 3.4, 0), ("y", 48, 3.0, 0), ("x", 48, 2.8, 120)]),
    ("Hopper hollow: ? row with the Bark Shield, Big Sun Coin above it, thorns and hoppers", [
        "",
        ".........O......................",
        "",
        ".......??3??..........m.........",
        "..MMM...........................",
        "",
        "....H............ooooo..........",
        "##########......................",
        "##########............H.......H.",
        "##########..#####^^^#####^^#####",
        "..........######################",
        "...TT...........TT.......TT.....",
    ], []),
    ("Final ascent: rising leaves to the Giant Blossom and the Glowspore", [
        "",
        "",
        "............m.....................",
        ".....................ff..Z.fF.....",
        "...........o.....o...MMMMMMMM.....",
        ".................L......TT........",
        ".............L..........TT........",
        "........................TT........",
        ".........L..............TT........",
        "########................TT........",
        "########................TT........",
        "...TT...................TT........",
    ], [("y", 48, 3.0, 180), ("y", 48, 2.8, 0), ("x", 32, 2.6, 0)]),
]

# Section order sanity: leaf lists are matched to L cells in reading order per section.


def build_grid(sections):
    grid = [[] for _ in range(ROWS)]
    leaves = []
    for name, rows, leaf_specs in sections:
        assert len(rows) == ROWS, name
        width = max(len(r) for r in rows)
        for i, r in enumerate(rows):
            if r and len(r) != width:
                raise SystemExit("Section %r row %d is %d wide, expected %d" % (name, i, len(r), width))
        start = len(grid[0])
        cells = [(r, c) for c in range(width) for r in range(ROWS) if rows[r] and rows[r][c] == "L"]
        if len(cells) != len(leaf_specs):
            raise SystemExit("Section %r has %d leaves but %d leaf specs" % (name, len(cells), len(leaf_specs)))
        for (r, c), spec in zip(cells, leaf_specs):
            leaves.append((start + c, r) + tuple(spec))
        for i in range(ROWS):
            grid[i].extend(rows[i].ljust(width, "."))
    return grid, leaves


# =====================================================================================
# Room building
# =====================================================================================
SOLID_TILES = set(range(0, 16)) | set(range(24, 29)) | {33, 34, 38}
TERRAIN = "#"


class Room:
    def __init__(self, key, name, sections, decor, tileset, world):
        self.key, self.name, self.decor, self.tileset, self.world = key, name, decor, tileset, world
        self.grid, self.leaf_specs = build_grid(sections)
        self.W = len(self.grid[0])
        self.layers = {"World": [], "Blocks": [], "Decor": [], "Hazards": [], "Water": [], "Secret": [], "Gate": []}
        self.solid = set()        # (c, r) solid for the hero
        self.hazard = set()
        self.water = set()
        self.actors = {}
        self.nodes = []

    def at(self, r, c):
        return self.grid[r][c] if 0 <= r < ROWS and 0 <= c < self.W else "."

    def put(self, layer, c, r, idx):
        self.layers[layer].append({"x": c, "y": r, "tileX": idx % 8, "tileY": idx // 8})
        if layer in ("World", "Blocks") and idx in SOLID_TILES:
            self.solid.add((c, r))

    def gid(self, text):
        return guid(self.key + ":" + text)

    def build_tiles(self):
        g, at = self.grid, self.at
        is_terrain = lambda r, c: at(r, c) == TERRAIN or (r >= ROWS or c < 0 or c >= self.W) and False
        for c in range(self.W):
            for r in range(ROWS):
                ch = g[r][c]
                if ch == TERRAIN:
                    up = at(r - 1, c) == TERRAIN or r == 0
                    left = at(r, c - 1) == TERRAIN or c == 0
                    right = at(r, c + 1) == TERRAIN or c == self.W - 1
                    down = at(r + 1, c) == TERRAIN or r == ROWS - 1
                    if not up:
                        idx = 0 if (left and right) or (not left and not right) else (5 if not left else 6)
                    elif not down:
                        idx = 38
                    elif not left:
                        idx = 24
                    elif not right:
                        idx = 25
                    else:
                        idx = 15 if (c * 7 + r * 13) % 11 == 0 else 1
                    self.put("World", c, r, idx)
                elif ch == "S":
                    self.put("World", c, r, 7)
                elif ch == "C":
                    self.put("World", c, r, 33)
                elif ch in "PT" and (self.world == "cavern" and ch == "P" or self.world == "canopy" and ch == "T"):
                    top = at(r - 1, c) != ch
                    run, k = 0, c
                    while at(r, k) == ch:
                        run += 1
                        k -= 1
                    is_left = run % 2 == 1
                    self.put("World", c, r, (8 if is_left else 9) if top else (10 if is_left else 11))
                elif ch == "=":
                    left, right = at(r, c - 1) == "=", at(r, c + 1) == "="
                    self.put("World", c, r, 13 if left and right else (12 if not left else 14))
                elif ch == "M":
                    left, right = at(r, c - 1) == "M", at(r, c + 1) == "M"
                    self.put("World", c, r, 27 if left and right else (26 if not left else 28))
                elif ch == "B":
                    self.put("Blocks", c, r, 2)
                elif ch == "?" or ch in "12345":
                    self.put("Blocks", c, r, 3)
                elif ch == "K":
                    self.put("Blocks", c, r, 7)
                elif ch == "^":
                    self.put("Hazards", c, r, 32)
                    self.hazard.add((c, r))
                elif ch == "F":
                    self.put("Secret", c, r, 1)
                elif ch in "~qJY":
                    surface = at(r - 1, c) not in "~qJY"
                    self.put("Water", c, r, 30 if surface else 31)
                    self.water.add((c, r))
                    if ch in "JY":
                        self.put("Secret", c, r, 1)
                    if ch in "qY":
                        self.actors.setdefault(ch, []).append((c, r))
                elif ch == "|":
                    self.put("Gate", c, r, 39)
                elif ch in self.decor:
                    self.put("Decor", c, r, self.decor[ch])
                elif ch != ".":
                    self.actors.setdefault(ch, []).append((c, r))
        # Decor next to the actors that stand on decor cells is not needed; check unknown actors.

    # ------------------------------------------------------------------ nodes
    def background(self, name, asset, layout, depth, repeat):
        self.nodes.append({"id": self.gid("bg:" + name), "kind": "Background", "name": name, "enabled": True,
                           "layerId": "scenery",
                           "transform": {"position": [0, 0, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                           "background": {"asset": asset, "mode": "TwoD", "layout": layout, "depth": depth,
                                          "repeatX": repeat, "opacity": 1}})

    def tile_layer(self, name, depth, collision):
        cells = sorted(self.layers[name], key=lambda cell: (cell["x"], cell["y"]))
        if not cells:
            return
        self.nodes.append({"id": self.gid("tiles:" + name), "kind": "TileLayer", "name": name, "enabled": True,
                           "layerId": "tiles",
                           "transform": {"position": [0, 0, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                           "tileLayer": {"tileset": "Assets/Sprites/%s.image.json" % self.tileset,
                                         "cellWidth": CELL, "cellHeight": CELL, "collisionEnabled": collision,
                                         "depth": depth, "cells": cells}})

    def instance(self, label, prefab, x, y, order=0):
        self.nodes.append({"id": self.gid("node:" + label), "kind": "GameObject", "name": label, "enabled": True,
                           "layerId": "actors", "order": order,
                           "transform": {"position": [x, y, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                           "gameObject": {"prefab": "Assets/Objects/%s.object.json" % prefab}})

    def write(self, environment, backgrounds, background_colour):
        for args in backgrounds:
            self.background(*args)
        self.tile_layer("World", 500, True)
        self.tile_layer("Blocks", 450, True)
        self.tile_layer("Gate", 440, True)
        self.tile_layer("Decor", 700, False)
        self.tile_layer("Hazards", 480, False)
        self.tile_layer("Water", -20, False)
        self.tile_layer("Secret", -40, False)
        # Instances come after the tiles so the room editor lists them together.
        actor_nodes = list(self.instance_plan)
        for label, prefab, x, y, order in actor_nodes:
            self.instance(label, prefab, x, y, order)
        room = {
            "$schema": "genesis.room", "version": 1, "id": self.gid("room"), "name": self.name, "dimension": "TwoD",
            "settings": {"width": self.W * CELL, "height": 360, "depth": 720, "windowWidth": 1280,
                         "windowHeight": 720, "scaleViewportsWithWindow": True, "targetFps": 60, "fixedFps": 60,
                         "vsync": True, "captureMouse": False, "gridSize": 32, "snapEnabled": True,
                         "pixelArtSampling": True},
            "environment": {"backgroundColor": background_colour, "fogEnabled": False, "postEffects": environment},
            "viewports": [{"enabled": True, "sourceX": 0, "sourceY": 0, "sourceWidth": 640, "sourceHeight": 360,
                           "portX": 0, "portY": 0, "portWidth": 1280, "portHeight": 720, "followTarget": "Explorer",
                           "followMarginX": 220, "followMarginY": 64, "followSpeedX": 20, "followSpeedY": -1}],
            "layers": [{"id": "scenery", "name": "Backdrop", "order": 0, "enabled": True},
                       {"id": "tiles", "name": "Painted world", "order": 1, "enabled": True},
                       {"id": "actors", "name": "Actors and pickups", "order": 2, "enabled": True}],
            "nodes": self.nodes,
        }
        rel = "Rooms/%s.room.json" % self.name
        write_json(rel, room)
        write_meta(rel, "Room", self.name)


# Actor placement: (prefab, anchor) where anchor is "center", "feet", "hang" (top at the cell top),
# "bat" (y = cell top + 12), "topleft", "leaf".
def plan_instances(room, mapping):
    plan = [("Level Rules", "Level Rules", 16, 16, -200)]
    counters = {}
    errors = []
    for ch, cells in sorted(room.actors.items()):
        if ch not in mapping:
            errors.append("Unknown map character %r at %s" % (ch, cells[:3]))
            continue
    if errors:
        raise SystemExit("\n".join(errors))
    order_keys = ["@"] + [k for k in mapping if k != "@"]
    for ch in order_keys:
        for (c, r) in sorted(room.actors.get(ch, []), key=lambda p: (p[0], p[1])):
            prefab, anchor, label = mapping[ch]
            counters[prefab] = counters.get(prefab, 0) + 1
            if anchor == "center":
                x, y = c * CELL + 16, r * CELL + 16
            elif anchor == "feet":
                x, y = c * CELL + 16, (r + 1) * CELL
            elif anchor == "hang":
                x, y = c * CELL + 16, r * CELL
            elif anchor == "bat":
                x, y = c * CELL + 16, r * CELL + 12
            elif anchor == "topleft":
                x, y = c * CELL, r * CELL
            elif anchor == "leaf":
                x, y = c * CELL, r * CELL - 4
            else:
                raise SystemExit(anchor)
            name = label if counters[prefab] == 1 and ch in "@kGZ" else "%s %02d" % (label, counters[prefab])
            order = -100 if ch == "@" else 0
            plan.append((name, prefab, x, y, order))
    return plan


CAVERN_ACTORS = {
    "@": ("Explorer", "feet", "Explorer"),
    "o": ("Sun Coin", "center", "Coin"),
    "q": ("Sun Coin", "center", "Coin"),
    "O": ("Big Sun Coin", "center", "Big Coin"),
    "Y": ("Big Sun Coin", "center", "Big Coin"),
    "k": ("Checkpoint", "feet", "Checkpoint"),
    "u": ("Bounce Mushroom", "feet", "Bounce"),
    "b": ("Flit Bat", "bat", "Bat"),
    "w": ("Glow Grub", "feet", "Grub"),
    "t": ("Stalactite", "hang", "Stalactite"),
    "G": ("Grumblecrag", "feet", "Grumblecrag"),
}
CANOPY_ACTORS = {
    "@": ("Explorer", "feet", "Explorer"),
    "o": ("Sun Coin", "center", "Coin"),
    "O": ("Big Sun Coin", "center", "Big Coin"),
    "k": ("Checkpoint", "feet", "Checkpoint"),
    "u": ("Bounce Mushroom", "feet", "Bounce"),
    "m": ("Dusk Moth", "center", "Moth"),
    "H": ("Burr Hopper", "feet", "Hopper"),
    "L": ("Moving Leaf", "leaf", "Leaf"),
    "y": ("Glide Seed", "center", "Glide Seed"),
    "Z": ("Glowspore", "center", "Glowspore"),
}
PRIZES = {"1": "Prize Glide", "2": "Prize Spore", "3": "Prize Shield", "4": "Prize Star", "5": "Prize Heart"}
GLOW_TILES = {35, 36, 17, 16, 22}


def prize_and_extra_instances(room, plan):
    n = 0
    for c in range(room.W):
        for r in range(ROWS):
            ch = room.grid[r][c]
            if ch in PRIZES:
                n += 1
                plan.append(("%s %02d" % (PRIZES[ch], n), PRIZES[ch], c * CELL + 16, r * CELL + 16, 0))
            if ch == "K":
                plan.append(("Crumble %03d" % (c * 100 + r), "Crumble Block", c * CELL, r * CELL, 0))


def glow_instances(room, plan):
    count = 0
    for cell in room.layers["Decor"]:
        idx = cell["tileX"] + cell["tileY"] * 8
        if idx in GLOW_TILES:
            # Small cyan mushrooms and hanging roots only glow every other one (light budget).
            if idx in (16, 22) and (cell["x"] + cell["y"]) % 2 == 1:
                continue
            count += 1
            plan.append(("Glow %03d" % count, "Cavern Glow", cell["x"] * CELL + 16, cell["y"] * CELL + 16, 0))
    for cell in room.layers["World"]:
        if cell["tileX"] + cell["tileY"] * 8 == 33:
            count += 1
            plan.append(("Glow %03d" % count, "Cavern Glow", cell["x"] * CELL + 16, cell["y"] * CELL + 16, 0))
    return count


# =====================================================================================
# PGSL: objects
# =====================================================================================
ENEMY_NOTE = ("// Enemy contract: the Explorer reads enemy/hp/stompable/contactDamage/points/dead and adds 1 to\n"
              "// hurt for a stomp, a spore hit or the Starlit Firefly. This object spends hurt itself.\n")

SHAKE_FN = ("var shakeOn = 1;\n"
            "if (GlobalExists(\"shake\")) { shakeOn = GlobalGet(\"shake\"); }\n")

FLIT_BAT_CREATE = ENEMY_NOTE + """enemy = 1; hp = 1; stompable = 1; contactDamage = 1; points = 200; dead = 0; hurt = 0;
// mode 0 hangs asleep, 1 swoops through where Pip stood, 2 flutters and swoops again.
mode = 0; t = 0; sx = x; sy = y; ex = x; dip = 0; cool = 0; flap = 0; flash = 0; dieTime = 0; vy = 0;
roostY = y; homeX = x; facing = 1; lastX = x;
AnimationPlay("Hang", true); AnimationSetSpeed(0);
ShaderSet("Assets/Shaders/Pixel Flash.shader.json"); ShaderSetParameter("Flash", 0);
"""

FLIT_BAT_STEP = """if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
if (hurt > 0 && dead == 0) {
    hp = hp - hurt; hurt = 0; flash = 0.2;
    if (hp <= 0) {
        dead = 1; dieTime = 0; vy = -140; stompable = 0; contactDamage = 0;
        Particle2DBurst("Assets/Particles/Bat Poof.particle.json", x, y, 16, 90);
    }
}
flash = Max(0, flash - dt);
ShaderSetParameter("Flash", Min(1, flash * 6));
if (dead == 1) {
    dieTime = dieTime + dt; vy = vy + 900 * dt; y = y + vy * dt;
    image_angle = image_angle + 600 * dt; image_alpha = Max(0, 1 - dieTime * 1.5);
    if (dieTime > 0.7) { InstanceDestroy(id); }
    return;
}
var hero = InstanceFind("Explorer", 0);
var hx = x; var hy = y - 1000;
if (hero > 0) { hx = InstanceGetX(hero); hy = InstanceGetY(hero); }
cool = Max(0, cool - dt);
if (mode == 0) {
    // Asleep under the ceiling until Pip walks below.
    if (hy > y + 40 && hy - y < 320 && Abs(hx - x) < 150) {
        mode = 1; t = 0; sx = x; sy = y;
        ex = Clamp(2 * hx - x, x - 300, x + 300);
        dip = Clamp(hy - 22 - y, 40, 230);
        AnimationPlay("Fly", true); AnimationSetSpeed(1);
        PlaySound("Assets/Audio/Bat Screech.audio.json", 0.7, RandomRange(0.92, 1.08), false);
    }
    return;
}
AnimationSetSpeed(1);
if (mode == 1) {
    t = t + dt / 1.15;
    var nx = Lerp(sx, ex, Min(1, t));
    var ny = sy + Sin(Min(1, t) * 180) * dip;
    if (TileMeeting(nx, ny)) { t = 1; } else { x = nx; y = ny; }
    if (t >= 1) { mode = 2; homeX = x; roostY = y; cool = 1.6; flap = 0; facing = Sign(ex - sx); if (facing == 0) { facing = 1; } }
} else {
    flap = flap + dt;
    var wantX = x + facing * 55 * dt;
    if (TileMeeting(wantX, y) || Abs(wantX - homeX) > 120) { facing = -facing; } else { x = wantX; }
    y = roostY + Sin(flap * 260) * 9;
    if (cool <= 0 && hy > y + 30 && hy - y < 300 && Abs(hx - x) < 140) {
        mode = 1; t = 0; sx = x; sy = y;
        ex = Clamp(2 * hx - x, x - 300, x + 300);
        dip = Clamp(hy - 22 - y, 40, 230);
        PlaySound("Assets/Audio/Bat Screech.audio.json", 0.45, RandomRange(1.0, 1.2), false);
    }
}
if (x < lastX - 0.05) { image_xscale = -1; }
if (x > lastX + 0.05) { image_xscale = 1; }
lastX = x;
"""

GLOW_GRUB_CREATE = ENEMY_NOTE + """enemy = 1; hp = 1; stompable = 1; contactDamage = 1; points = 100; dead = 0; hurt = 0;
// A slow crawler whose glowing back is a moving 2D light in the dark caverns.
moveDirection = -1; fallSpeed = 0; dieTime = 0; pulse = x % 360; flash = 0;
// LEVELS-A owns 2D light ids 300-499 (handed out in creation order).
lightId = 300 + RoomValueGet("levelsALightNext", 0) % 200; RoomValueSet("levelsALightNext", RoomValueGet("levelsALightNext", 0) + 1);
AnimationPlay("Crawl", true); AnimationSetSpeed(0);
ShaderSet("Assets/Shaders/Pixel Flash.shader.json"); ShaderSetParameter("Flash", 0);
Light2DPoint(lightId, x, y - 9, 72, 0.9, 0.55, 1.0, 0.5);
"""

GLOW_GRUB_STEP = """if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
if (hurt > 0 && dead == 0) {
    hp = hp - hurt; hurt = 0; flash = 0.2;
    if (hp <= 0) {
        dead = 1; dieTime = 0; stompable = 0; contactDamage = 0;
        Particle2DBurst("Assets/Particles/Grub Glow.particle.json", x, y - 8, 18, 90);
    }
}
flash = Max(0, flash - dt);
ShaderSetParameter("Flash", Min(1, flash * 6));
pulse = (pulse + dt * 160) % 360;
if (dead == 1) {
    dieTime = dieTime + dt;
    image_yscale = Max(0.25, 1 - dieTime * 2.2);
    image_alpha = Max(0, 1 - dieTime * 1.8);
    Light2DPoint(lightId, x, y - 6, Max(1, 72 * (1 - dieTime * 1.8)), 0.9, 0.55, 1.0, 0.5);
    if (dieTime >= 0.5) { Light2DRemove(lightId); InstanceDestroy(id); }
    return;
}
AnimationSetSpeed(1);
if (TileMeeting(x, y + 1)) {
    if (!TileMeeting(x + moveDirection * 18, y + 2)) { moveDirection = -moveDirection; }
}
var requested = x + moveDirection * 32 * dt;
var permitted = TileMoveX(x, y, moveDirection * 32 * dt);
if (Abs(requested - permitted) > 0.001) { moveDirection = -moveDirection; }
x = permitted;
fallSpeed = Min(fallSpeed + 1250 * dt, 600);
var wanted = y + fallSpeed * dt;
y = TileMoveY(x, y, fallSpeed * dt);
if (Abs(y - wanted) > 0.001) { fallSpeed = 0; }
image_xscale = moveDirection;
Light2DPoint(lightId, x, y - 9, 72 + Sin(pulse) * 8, 0.85 + Sin(pulse) * 0.1, 0.55, 1.0, 0.5);
if (y > RoomGetHeight() + 80) { Light2DRemove(lightId); InstanceDestroy(id); }
"""

STALACTITE_CREATE = ENEMY_NOTE + """enemy = 1; hp = 1; stompable = 0; contactDamage = 1; points = 50; dead = 0; hurt = 0;
// mode 0 hangs, 1 trembles (a warning), 2 falls and shatters on the floor.
mode = 0; t = 0; vy = 0; homeX = x;
AnimationPlay("Hang", true); AnimationSetSpeed(0);
"""

STALACTITE_STEP = """if (RoomValueGet("frozen", 0) == 1) { return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
var shatter = 0;
if (hurt > 0) { hurt = 0; shatter = 1; }
if (mode == 0) {
    var hero = InstanceFind("Explorer", 0);
    if (hero > 0) {
        var hx = InstanceGetX(hero); var hy = InstanceGetY(hero);
        if (Abs(hx - x) < 66 && hy > y + 40 && hy - y < 330) {
            mode = 1; t = 0;
            PlaySound("Assets/Audio/Pebble Fall.audio.json", 0.55, RandomRange(0.85, 1.0), false);
            Particle2DBurst("Assets/Particles/Cave Dust.particle.json", x, y + 2, 5, 270);
        }
    }
} else if (mode == 1) {
    t = t + dt;
    x = homeX + Sin(t * 2600) * 1.5;
    if (t >= 0.32) { mode = 2; x = homeX; AnimationPlay("Fall", true); }
} else {
    vy = Min(vy + 1500 * dt, 780);
    y = y + vy * dt;
    if (TileMeeting(x, y)) { shatter = 1; }
    if (y > RoomGetHeight() + 40) { InstanceDestroy(id); return; }
}
if (shatter == 1) {
    dead = 1;
    Particle2DBurst("Assets/Particles/Cave Dust.particle.json", x, y + 24, 14, 90);
    PlaySound("Assets/Audio/Crumble.audio.json", 0.55, RandomRange(1.1, 1.3), false);
    InstanceDestroy(id);
}
"""

CRUMBLE_CREATE = """// Crumble Block: sits on a Blocks-layer tile (index 7). Once Pip stands on it, it shakes for
// 0.5 s, then its tile becomes the blank index 39 (no collision) and it falls away; 4 s later
// the tile comes back (TileSetAt keeps the cell, so the swap is reversible).
mode = 0; t = 0; homeX = x; homeY = y; cx = x + 16; cy = y + 16;
AnimationPlay("Solid", true); AnimationSetSpeed(0);
"""

CRUMBLE_STEP = """if (RoomValueGet("frozen", 0) == 1) { return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
var hero = InstanceFind("Explorer", 0);
var hx = -1000; var hy = -1000;
if (hero > 0) { hx = InstanceGetX(hero); hy = InstanceGetY(hero); }
if (mode == 0) {
    if (Abs(hy - homeY) < 3 && hx + 7 > homeX && hx - 7 < homeX + 32) {
        mode = 1; t = 0; AnimationPlay("Cracking", true); AnimationSetSpeed(1);
        Particle2DBurst("Assets/Particles/Cave Dust.particle.json", cx, homeY, 3, 90);
    }
} else if (mode == 1) {
    t = t + dt;
    x = homeX + Sin(t * 3200) * 1;
    if (t >= 0.5) {
        mode = 2; t = 0; x = homeX;
        TileSetAt(cx, cy, 7, 4, "Blocks");
        SpriteSetVisible(false);
        Particle2DBurst("Assets/Particles/Cave Dust.particle.json", cx, cy, 16, 270);
        PlaySound("Assets/Audio/Crumble.audio.json", 0.7, RandomRange(0.9, 1.1), false);
    }
} else {
    t = t + dt;
    // Come back only when Pip is clear of the cell.
    var inside = hx + 7 > homeX && hx - 7 < homeX + 32 && hy > homeY && hy - 34 < homeY + 32;
    if (t >= 4 && !inside) {
        mode = 0; TileSetAt(cx, cy, 7, 0, "Blocks");
        SpriteSetVisible(true); AnimationPlay("Solid", true); AnimationSetSpeed(0);
        Particle2DBurst("Assets/Particles/Crystal Glint.particle.json", cx, cy, 6, 90);
    }
}
"""

PEBBLE_CREATE = ENEMY_NOTE + """enemy = 1; hp = 1; stompable = 0; contactDamage = 1; points = 0; dead = 0; hurt = 0;
// Falls from the arena ceiling; a contact shadow on the floor warns where it lands.
vy = 40; shadowId = 31000 + id; groundY = y; spin = RandomRange(-400, 400);
var probe = 0;
while (probe < 40 && groundY < RoomGetHeight() && TileIndexAt(x, groundY, "World") < 0) { groundY = groundY + 8; probe = probe + 1; }
groundY = Floor(groundY / 32) * 32;
AnimationPlay("Still", true);
"""

PEBBLE_STEP = """if (RoomValueGet("frozen", 0) == 1) { return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
vy = Min(vy + 1100 * dt, 700);
y = y + vy * dt;
image_angle = image_angle + spin * dt;
var near = Clamp(1 - (groundY - y) / 220, 0.15, 1);
Shadow2DContact(shadowId, x, groundY - 2, 10 + near * 12, 4 + near * 3, 0.25 + near * 0.4);
if (hurt > 0 || y >= groundY - 5 || TileMeeting(x, y)) {
    dead = 1;
    Shadow2DContact(shadowId, x, groundY, 0, 0, 0);
    Particle2DBurst("Assets/Particles/Pebble Crack.particle.json", x, y, 8, 90);
    InstanceDestroy(id);
}
"""

GLOW_CREATE = """// Cavern Glow: an invisible light placed by the level generator on every glowing decor tile.
// It reads the tile under it and picks a colour: lantern (35) warm, tall mushroom (36) pink,
// crystal (17) magenta, small mushrooms (16) cyan, hanging roots (22) green, crystal block (33) ice.
// LEVELS-A owns 2D light ids 300-499 (handed out in creation order).
lightId = 300 + RoomValueGet("levelsALightNext", 0) % 200; RoomValueSet("levelsALightNext", RoomValueGet("levelsALightNext", 0) + 1);
t = (x * 7 + y * 3) % 360; glint = 0.4 + (x % 97) / 60;
kind = TileIndexAt(x, y, "Decor");
if (kind < 0 && TileIndexAt(x, y, "World") == 33) { kind = 33; }
radius = 90; power = 0.8; cr = 1; cg = 1; cb = 1; offY = 0; sparkle = 0; rate = 90;
if (kind == 35) { radius = 132; power = 1.05; cr = 1.0; cg = 0.72; cb = 0.38; offY = 6; rate = 220; }
if (kind == 36) { radius = 104; power = 0.85; cr = 0.95; cg = 0.45; cb = 0.95; offY = -6; sparkle = 1; }
if (kind == 17) { radius = 88; power = 0.85; cr = 1.0; cg = 0.42; cb = 0.85; offY = 2; sparkle = 1; }
if (kind == 16) { radius = 70; power = 0.7; cr = 0.4; cg = 0.95; cb = 1.0; offY = 8; sparkle = 1; }
if (kind == 22) { radius = 60; power = 0.55; cr = 0.5; cg = 1.0; cb = 0.7; offY = 0; }
if (kind == 33) { radius = 96; power = 0.8; cr = 0.45; cg = 0.9; cb = 1.0; offY = 0; sparkle = 1; }
Light2DPoint(lightId, x, y + offY, radius, power, cr, cg, cb);
"""

GLOW_STEP = """var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
var vx = ViewGetPosX(0);
if (x < vx - 240 || x > vx + 880) { return; }
t = (t + dt * rate) % 360;
var wobble = Sin(t) * 0.06 + Sin(t * 2.7) * 0.03;
Light2DPoint(lightId, x, y + offY, radius * (1 + wobble), power * (1 + wobble), cr, cg, cb);
if (sparkle == 1 && RoomValueGet("frozen", 0) == 0) {
    glint = glint - dt;
    if (glint <= 0) {
        glint = RandomRange(0.7, 1.9);
        Particle2DBurst("Assets/Particles/Crystal Glint.particle.json", x + RandomRange(-10, 10), y + RandomRange(-8, 10), 1, 90);
    }
}
"""

GRUMBLE_CREATE = ENEMY_NOTE + """enemy = 1; hp = 3; stompable = 0; contactDamage = 1; points = 5000; dead = 0; hurt = 0;
// Grumblecrag, the rock-shell beetle of Rootdeep. States: 0 asleep, 1 intro roar, 2 walk,
// 3 wind-up, 4 charge, 5 flipped (after slamming a wall: the only time it can be stomped),
// 6 roll, 7 hurt, 8 getting up, 9 dying. Arena = the last 640 px of the room; the gate is the
// "Gate" tile layer in its first column (blank 39 while open, crystal 33 while closed).
state = 0; t = 0; facing = -1; vx = 0; vy = 0; flash = 0; pattern = 0; rolls = 0; rage = 0;
homeX = x; homeY = y; gateX = RoomGetWidth() - 640; deathX = x; burstT = 0; roared = 0; maxHp = 3;
barShown = 0; barHp = 3;
// tilt is the drawn wobble only (see Step); graceT keeps a roll harmless for a moment after a hit.
tilt = 0; graceT = 0; arenaL = gateX + 72; arenaR = RoomGetWidth() - 72;
AnimationPlay("Walk", true); AnimationSetSpeed(0); image_xscale = facing;
ShaderSet("Assets/Shaders/Pixel Flash.shader.json"); ShaderSetParameter("Flash", 0);
RoomValueSet("bossFight", 0);
"""

GRUMBLE_FUNCS = """function GrumbleGate(closed) {
    var gy = 80;
    while (gy < 300) {
        if (closed == 1) { TileSetAt(self.gateX + 16, gy, 1, 4, "Gate"); }
        else { TileSetAt(self.gateX + 16, gy, 7, 4, "Gate"); }
        gy = gy + 32;
    }
}
function GrumbleShake(magnitude, seconds) {
    var on = 1;
    if (GlobalExists("shake")) { on = GlobalGet("shake"); }
    if (on != 0) { ViewShake(0, magnitude, seconds); }
}
function GrumblePebbles(count) {
    var i = 0;
    while (i < count) {
        var px = RandomRange(self.gateX + 56, RoomGetWidth() - 56);
        CreateInstance("Pebble", px, 70 + RandomRange(0, 30), 0);
        i = i + 1;
    }
    PlaySound("Assets/Audio/Pebble Fall.audio.json", 0.8, RandomRange(0.9, 1.05), false);
}
function GrumbleLook(tag) {
    SpriteSet("Assets/Sprites/Grumblecrag Sprite.image.json");
    AnimationPlay(tag, true); AnimationSetSpeed(1);
}
"""

GRUMBLE_STEP = GRUMBLE_FUNCS + """
if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
// Collision always uses the upright mask. A tilted mask's corner dips below the feet, overlaps the
// floor, and a tile sweep ignores a tile it already overlaps, so a wobbling Grumblecrag used to sink
// through the arena floor and fall out of the room (it "vanished" at its first wind-up). The wobble
// is kept in tilt and only applied to image_angle after all movement, for drawing.
image_angle = 0; tilt = 0;
graceT = Max(0, graceT - dt);
if (state >= 2 && state != 9) { AnimationSetSpeed(1); }
var hero = InstanceFind("Explorer", 0);
var hx = x; var hy = y;
if (hero > 0) { hx = InstanceGetX(hero); hy = InstanceGetY(hero); }
t = t + dt;
flash = Max(0, flash - dt);
// Hits only count while it lies flipped; anything else glances off the shell.
if (hurt > 0) {
    if (state == 5) {
        // dead = 1 while it reels: the Explorer ignores it, so Pip's bounce cannot land on a live shell.
        hp = hp - 1; flash = 0.6; stompable = 0; contactDamage = 0; state = 7; t = 0; vx = 0; dead = 1;
        GrumbleLook("Hurt");
        PlaySound("Assets/Audio/Boss Hit.audio.json", 1, 1, false);
        GrumbleShake(4, 0.35);
        Particle2DBurst("Assets/Particles/Boss Debris.particle.json", x, y - 40, 18, 90);
    } else if (dead == 0) {
        flash = 0.12;
        Particle2DBurst("Assets/Particles/Shell Spark.particle.json", x, y - 48, 8, 90);
    }
    hurt = 0;
}
// Pip went back to the checkpoint: the beetle settles down and the gate opens again.
if (state >= 1 && state <= 8 && hero > 0 && hx < gateX - 8) {
    state = 0; t = 0; hp = maxHp; x = homeX; y = homeY; vx = 0; vy = 0; rage = 0; pattern = 0;
    stompable = 0; contactDamage = 1; dead = 0; image_angle = 0; facing = -1; image_alpha = 1; graceT = 0; flash = 0;
    GrumbleGate(0); RoomValueSet("bossFight", 0); GrumbleLook("Walk"); AnimationSetSpeed(0);
}
var walkSpeed = 70 + rage * 30;
if (state == 0) {
    if (hero > 0 && hx > gateX + 60 && hy > 40) {
        state = 1; t = 0; roared = 0; GrumbleGate(1); RoomValueSet("bossFight", 1);
        facing = Sign(hx - x); if (facing == 0) { facing = -1; }
        GrumbleLook("Walk"); AnimationSetSpeed(0.3);
    }
} else if (state == 1) {
    if (t >= 0.45 && roared == 0) {
        roared = 1; flash = 0.3;
        PlaySound("Assets/Audio/Boss Roar.audio.json", 1, 1, false);
        GrumbleShake(5, 1.1);
        Particle2DBurst("Assets/Particles/Cave Dust.particle.json", x, y - 70, 20, 90);
    }
    if (t >= 0.9 && roared == 1) { roared = 2; GrumblePebbles(3); }
    if (t >= 2.2) { state = 2; t = 0; GrumbleLook("Walk"); }
} else if (state == 2) {
    var want = Sign(hx - x);
    if (want != 0 && Abs(hx - x) > 24) { facing = want; }
    vx = facing * walkSpeed;
    if (t >= 1.5 - rage * 0.5) {
        t = 0; vx = 0;
        if (pattern % 2 == 0) { state = 3; flash = 0.25; PlaySound("Assets/Audio/Boss Charge.audio.json", 0.9, 1, false); }
        else { state = 6; rolls = 0; GrumbleLook("Roll"); vy = -200; }
        pattern = pattern + 1;
    }
} else if (state == 3) {
    vx = 0;
    var aim = Sign(hx - x); if (aim != 0) { facing = aim; }
    tilt = Sin(t * 2200) * 3;
    if (Floor(t * 10) % 2 == 0) { flash = Max(flash, 0.05); }
    if (t >= 0.7 - rage * 0.15) { state = 4; t = 0; tilt = 0; }
} else if (state == 4) {
    vx = facing * (330 + rage * 60);
    if (Floor(t * 12) % 2 == 0) { Particle2DBurst("Assets/Particles/Cave Dust.particle.json", x - facing * 30, y - 4, 1, 90); }
    var nx = TileMoveX(x, y, vx * dt);
    if (Abs(nx - (x + vx * dt)) > 0.5 || t > 4) {
        // Slam: the cave shakes, pebbles rain, and the beetle lands on its back.
        x = nx; state = 5; t = 0; vy = -300; vx = -facing * 90;
        stompable = 1; contactDamage = 0;
        GrumbleLook("Flipped");
        GrumbleShake(7, 0.6);
        PlaySound("Assets/Audio/Crumble.audio.json", 1, 0.7, false);
        Particle2DBurst("Assets/Particles/Boss Debris.particle.json", x + facing * 40, y - 30, 14, 90 + facing * 60);
        GrumblePebbles(4 + rage);
    } else { x = nx; }
} else if (state == 5) {
    vx = vx * Max(0, 1 - dt * 3);
    tilt = Sin(t * 900) * 2;
    if (t >= 2.6 - rage * 0.6) { state = 8; t = 0; vy = -220; tilt = 0; GrumbleLook("Walk"); }
} else if (state == 6) {
    vx = facing * (340 + rage * 60);
    var rx = TileMoveX(x, y, vx * dt);
    if (Abs(rx - (x + vx * dt)) > 0.5) {
        x = rx; facing = -facing; rolls = rolls + 1; vy = -230;
        GrumbleShake(2.5, 0.25);
        PlaySound("Assets/Audio/Crumble.audio.json", 0.6, 1.2, false);
        GrumblePebbles(1 + rage);
        if (rolls >= 3) { state = 2; t = 0; vx = 0; GrumbleLook("Walk"); }
    } else { x = rx; }
    // A roll that somehow never meets a wall still ends.
    if (state == 6 && t > 6) { state = 2; t = 0; vx = 0; GrumbleLook("Walk"); }
} else if (state == 7) {
    vx = 0;
    if (t >= 0.9) {
        if (hp <= 0) {
            state = 9; t = 0; dead = 1; stompable = 0; contactDamage = 0; deathX = x; burstT = 0;
            GrumbleLook("Hurt"); AnimationSetSpeed(0);
            PlaySound("Assets/Audio/Boss Roar.audio.json", 1, 0.8, false);
        } else {
            if (hp <= 1) { rage = 1; }
            // Pip has just bounced off the shell and is usually still overlapping it: roll away
            // from him, harmless for the first 0.5 s.
            dead = 0; contactDamage = 0; graceT = 0.5; state = 6; t = 0; rolls = 0; vy = -260;
            facing = -Sign(hx - x); if (facing == 0) { facing = 1; }
            GrumbleLook("Roll");
        }
    }
} else if (state == 8) {
    vx = 0;
    if (t >= 0.45) { state = 2; t = 0; stompable = 0; contactDamage = 1; }
} else if (state == 9) {
    vx = 0;
    x = deathX + Sin(t * 3100) * 2;
    flash = 0.5 + 0.5 * Sin(t * 1500);
    burstT = burstT - dt;
    if (burstT <= 0) {
        burstT = 0.2;
        Particle2DBurst("Assets/Particles/Boss Burst.particle.json", x + RandomRange(-36, 36), y - RandomRange(10, 60), 10, 90);
        Particle2DBurst("Assets/Particles/Boss Debris.particle.json", x + RandomRange(-30, 30), y - 30, 5, 90);
        GrumbleShake(3, 0.3);
    }
    image_alpha = Clamp((2.6 - t) / 0.6, 0, 1);
    if (t >= 2.6) {
        Particle2DBurst("Assets/Particles/Boss Burst.particle.json", x, y - 40, 48, 90);
        Particle2DBurst("Assets/Particles/Boss Debris.particle.json", x, y - 30, 30, 90);
        GrumbleShake(9, 0.8);
        RoomValueSet("bossFight", 2);
        GrumbleGate(0);
        // On the arena floor's centre at Pip's chest height: walking into it takes it.
        CreateInstance("Glowspore", gateX + 320, 254, 0);
        state = 10; InstanceDestroy(id); return;
    }
}
// Gravity and floor.
vy = Min(vy + 1500 * dt, 900);
var wantY = y + vy * dt;
y = TileMoveY(x, y, vy * dt);
if (Abs(y - wantY) > 0.001) { vy = 0; }
if (state != 4 && state != 6 && vx != 0) { x = TileMoveX(x, y, vx * dt); }
// Safety net: the arena floor is flat at homeY and the beetle never leaves the arena.
if (y > homeY) { y = homeY; vy = 0; }
x = Clamp(x, arenaL, arenaR);
if (state == 6 && graceT <= 0 && contactDamage == 0) { contactDamage = 1; }
image_angle = tilt;
image_xscale = facing;
ShaderSetParameter("Flash", Min(1, flash * 3));
if (state == 2) { AnimationSetSpeed(0.6 + Abs(vx) / 140); }
"""

GRUMBLE_GUI = """// Boss bar (1280x720 design space, like the HUD): name, three shell segments, intro banner.
if (state == 0 || state >= 10) { return; }
var sw = WindowGetWidth(); var sh = WindowGetHeight();
var sx = sw / 1280; var sy = sh / 720; var s = Min(sx, sy);
barHp = barHp + (Max(hp, 0) - barHp) * Min(1, DeltaTime * 6);
DrawSetFont("Consolas");
if (state == 1) {
    var a = Clamp(t * 2.5, 0, 1) * Clamp((2.2 - t) * 2.5, 0, 1);
    DrawSetAlpha(a * 0.75); DrawSetColorRgb(20, 12, 24);
    DrawRectangle(0, 300 * sy, sw, 420 * sy);
    DrawSetAlpha(a); DrawSetColorRgb(255, 196, 110);
    var title = "GRUMBLECRAG";
    DrawTextScaled((640 - DrawTextWidth(title, 52) / 2) * sx, 312 * sy, title, 52 * s);
    DrawSetColorRgb(230, 214, 255);
    var sub = "the rock-shell beetle of Rootdeep";
    DrawTextScaled((640 - DrawTextWidth(sub, 22) / 2) * sx, 376 * sy, sub, 22 * s);
}
var alpha = 1;
if (state == 9) { alpha = Clamp((2.6 - t) / 0.8, 0, 1); }
DrawSetAlpha(0.88 * alpha); DrawSetColorRgb(29, 20, 40);
DrawRoundRect(430 * sx, 104 * sy, 850 * sx, 156 * sy, 10 * s, false);
DrawSetAlpha(alpha); DrawSetColorRgb(255, 196, 110);
DrawRoundRect(430 * sx, 104 * sy, 850 * sx, 156 * sy, 10 * s, true);
DrawTextScaled(446 * sx, 110 * sy, "GRUMBLECRAG", 17 * s);
var i = 0;
while (i < maxHp) {
    var x1 = (446 + i * 132) * sx; var x2 = (566 + i * 132) * sx;
    DrawSetColorRgb(58, 44, 66);
    DrawRoundRect(x1, 134 * sy, x2, 148 * sy, 4 * s, false);
    var fill = Clamp(barHp - i, 0, 1);
    if (fill > 0) {
        DrawSetColorRgb(232, 120, 64);
        if (flash > 0.3) { DrawSetColorRgb(255, 240, 210); }
        DrawRoundRect(x1, 134 * sy, x1 + (x2 - x1) * fill, 148 * sy, 4 * s, false);
    }
    i = i + 1;
}
if (state == 5) {
    DrawSetColorRgb(255, 240, 160);
    if (Floor(t * 4) % 2 == 0) { DrawTextScaled(690 * sx, 110 * sy, "STOMP IT NOW!", 17 * s); }
}
DrawSetAlpha(1);
"""

MOTH_CREATE = ENEMY_NOTE + """enemy = 1; hp = 1; stompable = 1; contactDamage = 1; points = 200; dead = 0; hurt = 0;
// Dusk Moth: a lazy figure-eight around its post; when Pip comes close it winds up and swoops.
homeX = x; homeY = y; mode = 0; t = (x * 13) % 360; cool = 1.2; sx = x; sy = y; tx = x; ty = y;
flash = 0; dieTime = 0; vy = 0; trail = 0; lastX = x; wind = 0;
AnimationPlay("Fly", true); AnimationSetSpeed(0);
ShaderSet("Assets/Shaders/Pixel Flash.shader.json"); ShaderSetParameter("Flash", 0);
ShaderSetParameter("Shimmer", 0.4); ShaderSetParameter("ShimmerSpeed", 0.9);
"""

MOTH_STEP = """if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
if (hurt > 0 && dead == 0) {
    hp = hp - hurt; hurt = 0; flash = 0.2;
    if (hp <= 0) {
        dead = 1; dieTime = 0; vy = -100; stompable = 0; contactDamage = 0;
        Particle2DBurst("Assets/Particles/Moth Dust.particle.json", x, y, 20, 90);
    }
}
flash = Max(0, flash - dt);
ShaderSetParameter("Flash", Min(1, flash * 6));
if (dead == 1) {
    dieTime = dieTime + dt; vy = vy + 700 * dt; y = y + vy * dt;
    image_angle = image_angle - 420 * dt; image_alpha = Max(0, 1 - dieTime * 1.6);
    if (dieTime > 0.65) { InstanceDestroy(id); }
    return;
}
AnimationSetSpeed(1);
var hero = InstanceFind("Explorer", 0);
var hx = x; var hy = y + 1000;
if (hero > 0) { hx = InstanceGetX(hero); hy = InstanceGetY(hero) - 18; }
cool = Max(0, cool - dt);
// The wind carries moths a little too.
wind = wind + (RoomValueGet("windForce", 0) * 0.03 - wind) * Min(1, dt * 2);
if (mode == 0) {
    t = (t + dt * 60) % 360;
    x = homeX + Sin(t) * 38 + wind;
    y = homeY + Sin(t * 2) * 14;
    if (cool <= 0 && PointDistance(x, y, hx, hy) < 170) {
        mode = 1; t = 0; sx = x; sy = y;
    }
} else if (mode == 1) {
    // Wind-up: shiver in place for a moment.
    t = t + dt;
    x = sx + Sin(t * 3000) * 2;
    if (t >= 0.4) {
        mode = 2; t = 0; sx = x; sy = y;
        var len = Max(1, PointDistance(sx, sy, hx, hy));
        tx = sx + (hx - sx) / len * Min(260, len * 1.35);
        ty = sy + (hy - sy) / len * Min(260, len * 1.35);
        PlaySound("Assets/Audio/Moth Swoop.audio.json", 0.65, RandomRange(0.95, 1.1), false);
    }
} else if (mode == 2) {
    t = t + dt / 0.75;
    var e = Min(1, t);
    x = Lerp(sx, tx, e * (2 - e)); y = Lerp(sy, ty, e * (2 - e));
    trail = trail - dt;
    if (trail <= 0) { trail = 0.08; Particle2DBurst("Assets/Particles/Moth Dust.particle.json", x, y, 1, 90); }
    if (t >= 1) { mode = 3; t = 0; sx = x; sy = y; }
} else {
    t = t + dt / 1.1;
    var r = Min(1, t);
    x = Lerp(sx, homeX, r * r * (3 - 2 * r)); y = Lerp(sy, homeY, r * r * (3 - 2 * r));
    if (t >= 1) { mode = 0; t = 0; cool = 2.2; }
}
if (x < lastX - 0.05) { image_xscale = -1; }
if (x > lastX + 0.05) { image_xscale = 1; }
lastX = x;
"""

HOPPER_CREATE = ENEMY_NOTE + """enemy = 1; hp = 1; stompable = 1; contactDamage = 1; points = 150; dead = 0; hurt = 0;
// Burr Hopper: sits still (spines flat: stompable), then hops toward Pip with its burrs out
// (not stompable in the air). It never hops off a branch on purpose.
moveDirection = -1; vx = 0; vy = 0; airborne = 0; wait = 0.6 + (x % 50) / 50; flash = 0; dieTime = 0;
AnimationPlay("Idle", true); AnimationSetSpeed(0);
ShaderSet("Assets/Shaders/Pixel Flash.shader.json"); ShaderSetParameter("Flash", 0);
"""

HOPPER_STEP = """if (RoomValueGet("frozen", 0) == 1) { return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
if (hurt > 0 && dead == 0) {
    hp = hp - hurt; hurt = 0; flash = 0.2;
    if (hp <= 0) {
        dead = 1; dieTime = 0; stompable = 0; contactDamage = 0;
        Particle2DBurst("Assets/Particles/Burr Fluff.particle.json", x, y - 12, 16, 90);
    }
}
flash = Max(0, flash - dt);
ShaderSetParameter("Flash", Min(1, flash * 6));
if (dead == 1) {
    dieTime = dieTime + dt;
    image_yscale = Max(0.2, 1 - dieTime * 2.4); image_xscale = moveDirection * (1 + dieTime);
    image_alpha = Max(0, 1 - dieTime * 2);
    if (dieTime >= 0.45) { InstanceDestroy(id); }
    return;
}
var hero = InstanceFind("Explorer", 0);
var hx = x;
if (hero > 0) { hx = InstanceGetX(hero); }
if (airborne == 0) {
    wait = wait - dt;
    if (wait <= 0) {
        if (Abs(hx - x) < 260 && Abs(hx - x) > 4) { moveDirection = Sign(hx - x); }
        var hop = 1;
        if (!TileMeeting(x + moveDirection * 74, y + 2) || TileMeeting(x + moveDirection * 36, y - 6)) {
            moveDirection = -moveDirection;
            if (!TileMeeting(x + moveDirection * 74, y + 2) || TileMeeting(x + moveDirection * 36, y - 6)) { hop = 0; }
        }
        airborne = 1; stompable = 0; vy = -430; vx = moveDirection * 110 * hop;
        AnimationPlay("Jump", true);
        Particle2DBurst("Assets/Particles/Burr Fluff.particle.json", x, y - 2, 3, 90);
    }
} else {
    var reqX = x + vx * dt;
    x = TileMoveX(x, y, vx * dt);
    if (Abs(reqX - x) > 0.001) { vx = 0; }
    vy = Min(vy + 1250 * dt, 600);
    var reqY = y + vy * dt;
    y = TileMoveY(x, y, vy * dt);
    if (Abs(reqY - y) > 0.001 && vy > 0) {
        airborne = 0; vy = 0; vx = 0; stompable = 1; wait = 0.9 + (x % 37) / 50;
        AnimationPlay("Idle", true);
        Particle2DBurst("Assets/Particles/Burr Fluff.particle.json", x, y - 2, 4, 90);
    } else if (Abs(reqY - y) > 0.001) { vy = 0; }
}
image_xscale = moveDirection;
if (y > RoomGetHeight() + 80) { InstanceDestroy(id); }
"""

LEAF_HEAD = """// Moving Leaf: a one-way platform (contract: platformW, platformTop, platformDx, platformDy).
// The level generator gives each leaf its motion below, keyed by where the room places it.
homeX = x; homeY = y; axis = 0; amp = 64; period = 3; phase = 0; t = 0; sag = 0;
platformW = 64; platformTop = y + 4; platformDx = 0; platformDy = 0;
AnimationPlay("Still", true); AnimationSetSpeed(0);
"""

LEAF_STEP = """// Moved in Step Begin so the Explorer's Step already sees this frame's platformDx/Dy.
var dt = Clamp(DeltaTime, 0, 0.05);
if (RoomValueGet("frozen", 0) == 1 || dt <= 0) { platformDx = 0; platformDy = 0; return; }
t = t + dt;
var off = Sin((t / period) * 360 + phase) * amp;
var nx = homeX; var ny = homeY;
if (axis == 0) { nx = homeX + off; } else { ny = homeY + off; }
// It dips a few pixels under Pip's weight.
var riding = 0;
var hero = InstanceFind("Explorer", 0);
if (hero > 0) {
    var hx = InstanceGetX(hero); var hy = InstanceGetY(hero);
    if (Abs(hy - platformTop) < 3 && hx + 7 > x && hx - 7 < x + platformW) { riding = 1; }
}
sag = sag + (riding * 4 - sag) * Min(1, dt * 10);
ny = ny + sag;
platformDx = nx - x; platformDy = ny - y;
x = nx; y = ny; platformTop = y + 4;
"""

WIND_CREATE = """// Wind Controller (one per Windswept Canopy room). Cycles calm -> warning -> gust. During a gust
// it publishes RoomValue windForce (px/s^2, signed; the Explorer adds it to its speed), plays
// Wind Gust, blows leaves across the screen and shows the wind gauge in its Draw GUI.
windForce = 0; phase = 0; t = 0; gustIndex = 0; gustDir = 1; leafT = 0; shown = 0; warnDir = 0;
pattern = [1, 1, -1, 1, 1, -1, 1, -1];
calmTime = 3.4; warnTime = 1.1; gustTime = 2.7; peak = 320;
// Leaves are 2D particles; keep the room fully lit (Particle2DBurst turns 2D effects on).
Light2DAmbient(1, 0, 0, 0);
RoomValueSet("windForce", 0); RoomValueSet("windWarn", 0);
"""

WIND_STEP = """var dt = Clamp(DeltaTime, 0, 0.05);
if (RoomValueGet("frozen", 0) == 1 || dt <= 0) { RoomValueSet("windForce", 0); windForce = 0; return; }
t = t + dt;
var vx = ViewGetPosX(0); var vy = ViewGetPosY(0);
var vw = ViewGetBoundsW(0); var vh = ViewGetBoundsH(0);
if (vw <= 0) { vw = 640; } if (vh <= 0) { vh = 360; }
leafT = leafT - dt;
if (phase == 0) {
    windForce = 0;
    if (leafT <= 0) {
        leafT = RandomRange(0.25, 0.6);
        Particle2DBurst("Assets/Particles/Canopy Leaf.particle.json", vx + RandomRange(0, vw), vy - 6, 1, 270);
    }
    if (t >= calmTime) { phase = 1; t = 0; gustDir = pattern[gustIndex % 8]; warnDir = gustDir; RoomValueSet("windWarn", gustDir); }
} else if (phase == 1) {
    windForce = 0;
    if (leafT <= 0) {
        leafT = 0.18;
        var wx = vx - 6; var wa = 0;
        if (gustDir < 0) { wx = vx + vw + 6; wa = 180; }
        Particle2DBurst("Assets/Particles/Gust Leaf.particle.json", wx, vy + RandomRange(20, vh - 20), 1, wa);
    }
    if (t >= warnTime) {
        phase = 2; t = 0;
        PlaySound("Assets/Audio/Wind Gust.audio.json", 0.85, RandomRange(0.95, 1.05), false);
    }
} else {
    var env = Clamp(t / 0.45, 0, 1) * Clamp((gustTime - t) / 0.6, 0, 1);
    windForce = gustDir * peak * env;
    if (leafT <= 0) {
        leafT = 0.05;
        var gx = vx - 6; var ga = 0;
        if (gustDir < 0) { gx = vx + vw + 6; ga = 180; }
        Particle2DBurst("Assets/Particles/Gust Leaf.particle.json", gx, vy + RandomRange(10, vh - 10), 2, ga);
    }
    if (t >= gustTime) { phase = 0; t = 0; windForce = 0; gustIndex = gustIndex + 1; warnDir = 0; RoomValueSet("windWarn", 0); }
}
RoomValueSet("windForce", windForce);
shown = shown + (windForce - shown) * Min(1, dt * 6);
"""

WIND_GUI = """// Wind gauge, top right under the HUD (1280x720 design space).
var sw = WindowGetWidth(); var sh = WindowGetHeight();
var sx = sw / 1280; var sy = sh / 720; var s = Min(sx, sy);
DrawSetFont("Consolas");
DrawSetAlpha(0.85); DrawSetColorRgb(40, 30, 22);
DrawRoundRect(1036 * sx, 104 * sy, 1252 * sx, 152 * sy, 10 * s, false);
DrawSetAlpha(1); DrawSetColorRgb(255, 214, 140);
DrawRoundRect(1036 * sx, 104 * sy, 1252 * sx, 152 * sy, 10 * s, true);
DrawTextScaled(1048 * sx, 116 * sy, "WIND", 18 * s);
var cx = 1172; var cy = 128;
var strength = Clamp(Abs(shown) / peak, 0, 1);
DrawSetColorRgb(90, 70, 52);
DrawLineWidth((cx - 60) * sx, cy * sy, (cx + 60) * sx, cy * sy, 4 * s);
if (phase == 1) {
    if (Floor(t * 6) % 2 == 0) {
        DrawSetColorRgb(255, 120, 90);
        var warnText = "GUST >>";
        if (warnDir < 0) { warnText = "<< GUST"; }
        DrawTextScaled((cx - 44) * sx, (cy - 12) * sy, warnText, 18 * s);
    }
} else if (strength > 0.02) {
    var dir = Sign(shown);
    var len = 14 + strength * 46;
    DrawSetColorRgb(255, 236, 190);
    DrawLineWidth((cx - dir * len) * sx, cy * sy, (cx + dir * len) * sx, cy * sy, 6 * s);
    DrawTriangle((cx + dir * (len + 12)) * sx, cy * sy, (cx + dir * len) * sx, (cy - 9) * sy, (cx + dir * len) * sx, (cy + 9) * sy, false);
} else {
    DrawSetColorRgb(200, 180, 150);
    DrawTextScaled((cx - 24) * sx, (cy - 11) * sy, "calm", 16 * s);
}
DrawSetAlpha(1);
"""


def object_json(sprite, name, events):
    return {"schemaVersion": 2, "dimension": "TwoD", "sprite": sprite, "model": "",
            "components": [{"type": "ScriptComponent", "enabled": True, "props": {"ScriptClass": name}}],
            "events": events}


def write_object(name, sprite, events):
    rel = "Objects/%s.object.json" % name
    write_json(rel, object_json(("Assets/Sprites/%s Sprite.image.json" % sprite) if sprite else "", name, list(events)))
    write_meta(rel, "GameObject", name)
    for event, body in events.items():
        write_text("Objects/%s/%s.pgsl" % (name, event), body)


def leaf_create(leaves):
    lines = [LEAF_HEAD]
    for (c, r, axis, amp, period, phase) in leaves:
        x, y = c * CELL, r * CELL - 4
        lines.append("if (Abs(homeX - %d) < 1 && Abs(homeY - %d) < 1) { axis = %d; amp = %d; period = %s; phase = %d; }"
                     % (x, y, 0 if axis == "x" else 1, amp, repr(float(period)), phase))
    return "\n".join(lines) + "\n"


def build_objects(all_leaves):
    write_object("Flit Bat", "Flit Bat", {"Create": FLIT_BAT_CREATE, "Step": FLIT_BAT_STEP})
    write_object("Glow Grub", "Glow Grub", {"Create": GLOW_GRUB_CREATE, "Step": GLOW_GRUB_STEP})
    write_object("Stalactite", "Stalactite", {"Create": STALACTITE_CREATE, "Step": STALACTITE_STEP})
    write_object("Crumble Block", "Crumble Block", {"Create": CRUMBLE_CREATE, "Step": CRUMBLE_STEP})
    write_object("Pebble", "Pebble", {"Create": PEBBLE_CREATE, "Step": PEBBLE_STEP})
    write_object("Cavern Glow", "", {"Create": GLOW_CREATE, "Step": GLOW_STEP})
    write_object("Grumblecrag", "Grumblecrag", {"Create": GRUMBLE_CREATE, "Step": GRUMBLE_STEP, "DrawGui": GRUMBLE_GUI})
    write_object("Dusk Moth", "Dusk Moth", {"Create": MOTH_CREATE, "Step": MOTH_STEP})
    write_object("Burr Hopper", "Burr Hopper", {"Create": HOPPER_CREATE, "Step": HOPPER_STEP})
    write_object("Moving Leaf", "Moving Leaf", {"Create": leaf_create(all_leaves), "StepBegin": LEAF_STEP})
    write_object("Wind Controller", "", {"Create": WIND_CREATE, "Step": WIND_STEP, "DrawGui": WIND_GUI})


# =====================================================================================
# Particles (Particle2DBurst: units are room pixels; gravity positive = rises)
# =====================================================================================
def colour(r, g, b, a=1.0):
    return {"r": r, "g": g, "b": b, "a": a}


PARTICLE_BASE = {
    "preview2D": True, "maxParticles": 64, "emitRate": 0.0, "burstCount": 12, "loop": False,
    "duration": 1.0, "shape": "Sphere", "spreadDegrees": 40.0, "emitRadius": 2.0, "speed": 80.0,
    "speedVariance": 0.35, "gravity": 0.0, "drag": 1.0, "lifetime": 0.6, "lifetimeVariance": 0.3,
    "startSize": 4.0, "endSize": 1.0, "emissive": 1.0, "blendMode": "Alpha", "pixelSampling": True,
    "startColor": colour(1, 1, 1, 1), "endColor": colour(1, 1, 1, 0),
}


def particle(name, notes, **overrides):
    data = dict(PARTICLE_BASE)
    data.update(overrides)
    data["effectName"] = name
    data["notes"] = notes + " Sizes and speeds are room pixels (played with Particle2DBurst)."
    rel = "Particles/%s.particle.json" % name
    write_json(rel, data)
    write_meta(rel, "Particle", name)


def build_particles():
    particle("Cave Dust", "Rock grit: crumbling blocks, stalactites shattering, wall slams.",
             spreadDegrees=70, speed=70, emitRadius=6, gravity=-420, drag=1.5, lifetime=0.7, startSize=4,
             endSize=2, startColor=colour(0.62, 0.56, 0.66, 1), midColor=colour(0.45, 0.4, 0.5, 0.9),
             endColor=colour(0.3, 0.26, 0.35, 0), rotationSpeed=300, rotationVariance=1.0)
    particle("Crystal Glint", "Tiny rising sparkles from crystals, glowing mushrooms and restored blocks.",
             spreadDegrees=50, speed=10, emitRadius=3, gravity=14, drag=0.5, lifetime=1.0, startSize=3,
             endSize=0.5, blendMode="Additive", emissive=1.6, startColor=colour(0.9, 1.0, 1.0, 1),
             midColor=colour(0.55, 0.95, 1.0, 0.9), endColor=colour(0.9, 0.5, 1.0, 0))
    particle("Bat Poof", "A Flit Bat bursts into violet fluff.",
             spreadDegrees=180, speed=70, emitRadius=4, drag=3.0, lifetime=0.5, startSize=6, endSize=1,
             startColor=colour(0.85, 0.75, 1.0, 1), midColor=colour(0.55, 0.42, 0.75, 0.9),
             endColor=colour(0.3, 0.2, 0.45, 0))
    particle("Grub Glow", "A Glow Grub's light scatters as green sparks.",
             spreadDegrees=80, speed=60, emitRadius=6, gravity=60, drag=2.0, lifetime=0.8, startSize=4,
             endSize=0.5, blendMode="Additive", emissive=1.5, startColor=colour(0.75, 1.0, 0.7, 1),
             midColor=colour(0.4, 1.0, 0.6, 0.9), endColor=colour(0.2, 0.7, 0.5, 0))
    particle("Shell Spark", "A stomp or spore glancing off Grumblecrag's shell.",
             spreadDegrees=70, speed=140, emitRadius=6, gravity=-300, drag=2.0, lifetime=0.35, startSize=3,
             endSize=0.5, blendMode="Additive", emissive=1.6, startColor=colour(1.0, 0.95, 0.7, 1),
             endColor=colour(1.0, 0.6, 0.2, 0))
    particle("Boss Debris", "Shell chips and rock chunks from Grumblecrag.",
             spreadDegrees=65, speed=170, emitRadius=10, gravity=-640, drag=0.6, lifetime=1.0, startSize=6,
             endSize=4, rotationSpeed=500, rotationVariance=1.0, startColor=colour(0.74, 0.66, 0.52, 1),
             midColor=colour(0.55, 0.48, 0.38, 1), endColor=colour(0.35, 0.3, 0.25, 0))
    particle("Boss Burst", "Golden light bursting out of the defeated Grumblecrag.",
             spreadDegrees=180, speed=210, emitRadius=8, gravity=40, drag=2.2, lifetime=0.9, startSize=7,
             endSize=0.5, blendMode="Additive", emissive=1.8, startColor=colour(1.0, 1.0, 0.85, 1),
             midColor=colour(1.0, 0.8, 0.35, 1), endColor=colour(1.0, 0.45, 0.2, 0))
    particle("Pebble Crack", "A ceiling pebble shatters on the arena floor.",
             spreadDegrees=60, speed=90, emitRadius=3, gravity=-500, drag=1.0, lifetime=0.45, startSize=3,
             endSize=1.5, startColor=colour(0.7, 0.64, 0.56, 1), endColor=colour(0.45, 0.4, 0.36, 0))
    particle("Canopy Leaf", "Calm-weather leaves drifting down through the treetops.",
             spreadDegrees=25, speed=30, emitRadius=6, gravity=-18, drag=0.2, windX=22, lifetime=7.0,
             lifetimeVariance=0.2, startSize=5, endSize=4.5, sizeXScale=1.7, sizeYScale=0.8,
             rotationSpeed=160, rotationVariance=1.0, startColor=colour(0.62, 0.82, 0.3, 1),
             midColor=colour(1.0, 0.72, 0.28, 1), endColor=colour(0.85, 0.42, 0.18, 0), colorJitter=0.2)
    particle("Gust Leaf", "Leaves blown across the screen by a gust (direction 0 = right, 180 = left).",
             spreadDegrees=8, speed=430, speedVariance=0.3, emitRadius=10, gravity=-25, drag=0.15,
             lifetime=1.9, lifetimeVariance=0.2, startSize=5, endSize=4, sizeXScale=1.8, sizeYScale=0.75,
             rotationSpeed=520, rotationVariance=1.0, startColor=colour(0.55, 0.8, 0.28, 1),
             midColor=colour(1.0, 0.75, 0.3, 1), endColor=colour(0.9, 0.45, 0.2, 0), colorJitter=0.25)
    particle("Moth Dust", "Violet scale dust trailing a swooping Dusk Moth.",
             spreadDegrees=180, speed=20, emitRadius=4, gravity=-20, drag=1.5, lifetime=0.7, startSize=3,
             endSize=0.5, blendMode="Additive", emissive=1.4, startColor=colour(0.95, 0.85, 1.0, 1),
             midColor=colour(0.7, 0.5, 0.95, 0.9), endColor=colour(0.4, 0.25, 0.6, 0))
    particle("Burr Fluff", "Brown fluff when a Burr Hopper jumps, lands or pops.",
             spreadDegrees=80, speed=60, emitRadius=5, gravity=-120, drag=2.0, lifetime=0.55, startSize=4,
             endSize=1.5, startColor=colour(0.82, 0.66, 0.42, 1), endColor=colour(0.55, 0.4, 0.25, 0))


# =====================================================================================
# Shaders
# =====================================================================================
GOLDEN_HOUR = """// Golden Hour: Windswept Canopy post effect. Warm grade, slow sun shafts from the top right,
// a soft bloom on bright leaves and a golden haze low in the frame.
Texture2D SceneColor : register(t0);
SamplerState Linear : register(s0);
cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
cbuffer GenesisParameters : register(b5) { float Warmth; float Rays; float Haze; float Bloom; };
struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
float4 MainPS(PreviewVSOut IN) : SV_Target
{
    float2 uv = IN.UV;
    float4 c = SceneColor.Sample(Linear, uv);
    float3 rgb = c.rgb;
    float2 px = 1.0 / max(Resolution, float2(1.0, 1.0));
    float3 blur = SceneColor.Sample(Linear, uv + px * float2(4.0, 0.0)).rgb
                + SceneColor.Sample(Linear, uv - px * float2(4.0, 0.0)).rgb
                + SceneColor.Sample(Linear, uv + px * float2(0.0, 4.0)).rgb
                + SceneColor.Sample(Linear, uv - px * float2(0.0, 4.0)).rgb;
    blur *= 0.25;
    rgb += max(blur - 0.62, 0.0) * 1.6 * saturate(Bloom) * float3(1.0, 0.82, 0.55);
    float2 d = uv - float2(0.88, -0.12);
    float ang = atan2(d.y, d.x);
    float shafts = saturate(0.5 + 0.5 * sin(ang * 23.0 + Time * 0.21) * sin(ang * 9.0 - Time * 0.13));
    shafts = shafts * shafts * shafts;
    float fall = saturate(1.0 - length(d) * 0.9);
    rgb += shafts * fall * saturate(Rays) * float3(1.0, 0.8, 0.48) * 0.24;
    rgb = lerp(rgb, rgb * float3(1.08, 0.98, 0.84) + float3(0.025, 0.012, 0.0), saturate(Warmth));
    rgb = lerp(rgb, float3(1.0, 0.84, 0.62), saturate(Haze) * saturate(uv.y - 0.55) * 0.3);
    return float4(saturate(rgb), c.a);
}
"""

CAVERN_DAMP = """// Cavern Damp: Rootdeep Caverns post effect. Cool, slightly lifted shadows (the 2D lightmap
// already darkens the cave), a deep vignette and faint drifting motes in the dark.
Texture2D SceneColor : register(t0);
SamplerState Linear : register(s0);
cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
cbuffer GenesisParameters : register(b5) { float Coolness; float Vignette; float Motes; float Lift; };
struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float4 MainPS(PreviewVSOut IN) : SV_Target
{
    float2 uv = IN.UV;
    float4 c = SceneColor.Sample(Linear, uv);
    float3 rgb = c.rgb;
    float luma = dot(rgb, float3(0.299, 0.587, 0.114));
    float shadow = saturate(1.0 - luma * 2.2);
    rgb = lerp(rgb, rgb * float3(0.86, 0.95, 1.12) + float3(0.0, 0.006, 0.02) * Lift, saturate(Coolness) * shadow);
    float2 p = uv - 0.5;
    rgb *= lerp(0.6, 1.0, saturate(1.0 - dot(p, p) * 2.2 * saturate(Vignette)));
    float2 grid = floor((uv + float2(Time * 0.004, -Time * 0.011)) * float2(160.0, 90.0));
    float mote = step(0.996, Hash(grid)) * shadow;
    rgb += mote * saturate(Motes) * float3(0.35, 0.55, 0.6) * (0.6 + 0.4 * sin(Time * 2.0 + grid.x));
    return float4(saturate(rgb), c.a);
}
"""


def param(name, value):
    return {"name": name, "type": "float", "value": [value]}


def build_shaders():
    for name, source, params in (
            ("Golden Hour", GOLDEN_HOUR, [param("Warmth", 0.6), param("Rays", 0.8), param("Haze", 0.7), param("Bloom", 0.6)]),
            ("Cavern Damp", CAVERN_DAMP, [param("Coolness", 0.7), param("Vignette", 0.8), param("Motes", 0.8), param("Lift", 1.0)])):
        rel = "Shaders/%s.shader.json" % name
        write_json(rel, {"schemaVersion": 7, "pipeline": "Fullscreen", "authoringMode": "Code",
                         "targetType": "Fullscreen", "preset": "", "targetComponent": "", "entry": "MainPS",
                         "profile": "ps_5_0", "source": source, "previewAsset": "", "parameters": params})
        write_meta(rel, "Shader", name)


# =====================================================================================
# Preview rendering
# =====================================================================================
def render_preview(room, plan, out_name, lit=False):
    try:
        from PIL import Image, ImageDraw
    except ImportError:
        print("  (PIL not available; no preview)")
        return
    W, H = room.W * CELL, 360
    path = os.path.join(ASSETS, "Rooms", "%s.room.json" % room.name)
    data = json.load(open(path, encoding="utf-8"))
    canvas = Image.new("RGBA", (W, H), tuple(int(v * 255) for v in data["environment"]["backgroundColor"][:3]) + (255,))

    def load_image(asset):
        p = os.path.join(os.path.dirname(ASSETS), asset.replace("/", os.sep))
        if not os.path.exists(p):
            return None, None
        doc = json.load(open(p, encoding="utf-8"))
        frame = doc["frames"][0]
        src = frame.get("source") or ""
        png_path = os.path.join(os.path.dirname(p), src.replace("/", os.sep))
        if not src or not os.path.exists(png_path):
            return doc, None
        return doc, Image.open(png_path).convert("RGBA")

    items = []
    for node in data["nodes"]:
        if node["kind"] == "Background":
            doc, png = load_image(node["background"]["asset"])
            items.append((node["background"]["depth"], "bg", node, doc, png))
        elif node["kind"] == "TileLayer":
            doc, png = load_image(node["tileLayer"]["tileset"])
            items.append((node["tileLayer"]["depth"], "tiles", node, doc, png))
    items.sort(key=lambda i: -i[0])
    placeholder = {0: (110, 90, 120), 1: (80, 64, 90)}
    tile_depth_split = [i for i in items if i[0] > 0]
    front = [i for i in items if i[0] <= 0]

    def draw_items(group):
        for depth, kind, node, doc, png in group:
            if kind == "bg":
                if png is None:
                    continue
                layout = node["background"]["layout"]
                if layout == "StretchView":
                    scaled = png.resize((png.width * H // png.height, H))
                    x = 0
                    while x < W:
                        canvas.alpha_composite(scaled, (x, 0))
                        x += scaled.width
                else:
                    x = 0
                    while x < W:
                        canvas.alpha_composite(png, (x, 0))
                        x += png.width
            else:
                tl = node["tileLayer"]
                for cell in tl["cells"]:
                    idx = cell["tileX"] + cell["tileY"] * 8
                    if png is not None:
                        tile = png.crop((cell["tileX"] * CELL, cell["tileY"] * CELL,
                                         cell["tileX"] * CELL + CELL, cell["tileY"] * CELL + CELL))
                        if node["name"] == "Secret":
                            tile = tile.copy()
                            tile.putalpha(tile.getchannel("A").point(lambda a: a * 3 // 4))
                        canvas.alpha_composite(tile, (cell["x"] * CELL, cell["y"] * CELL))
                    else:
                        d = ImageDraw.Draw(canvas)
                        col = (100, 80, 110, 255) if idx in SOLID_TILES else (60, 120, 160, 160)
                        d.rectangle([cell["x"] * CELL, cell["y"] * CELL, cell["x"] * CELL + 31, cell["y"] * CELL + 31], fill=col)

    draw_items(tile_depth_split)
    d = ImageDraw.Draw(canvas)
    sprite_cache = {}
    for label, prefab, x, y, order in plan:
        obj_path = os.path.join(ASSETS, "Objects", prefab + ".object.json")
        sprite = ""
        if os.path.exists(obj_path):
            sprite = json.load(open(obj_path, encoding="utf-8")).get("sprite", "")
        if sprite not in sprite_cache:
            sprite_cache[sprite] = load_image(sprite) if sprite else (None, None)
        doc, png = sprite_cache[sprite]
        if png is not None:
            fw = doc["canvas"]["width"] if "canvas" in doc else png.width
            fh = doc["canvas"]["height"] if "canvas" in doc else png.height
            frame_png = png.crop((0, 0, min(fw, png.width), min(fh, png.height)))
            ox, oy = doc.get("origin", {}).get("x", 0), doc.get("origin", {}).get("y", 0)
            px, py = int(x - ox), int(y - oy)
            if -frame_png.width < px < W and -frame_png.height < py < H:
                canvas.alpha_composite(frame_png, (max(0, px), max(0, py)) if px >= 0 and py >= 0 else (max(0, px), max(0, py)))
        elif prefab not in ("Level Rules", "Cavern Glow", "Wind Controller"):
            d.rectangle([x - 8, y - 16, x + 8, y], outline=(255, 80, 80, 255))
            d.text((x - 6, y - 14), prefab[:2], fill=(255, 255, 255, 255))
    draw_items(front)
    os.makedirs(PREVIEW_DIR, exist_ok=True)

    def save_strips(img, name):
        strip_w = 2240
        n = (W + strip_w - 1) // strip_w
        out = Image.new("RGBA", (strip_w, n * (H + 6)), (0, 0, 0, 255))
        for i in range(n):
            out.alpha_composite(img.crop((i * strip_w, 0, min(W, (i + 1) * strip_w), H)), (0, i * (H + 6)))
        out.save(os.path.join(PREVIEW_DIR, name))

    save_strips(canvas, "level_%s.png" % out_name)
    if lit:
        # Approximate the game's 2D lightmap: ambient 0.32 plus each Cavern Glow / Glow Grub light.
        import array
        amb = 0.32
        lights = []
        lookup = {35: (132, 1.05, (1, .72, .38), 6), 36: (104, .85, (.95, .45, .95), -6), 17: (88, .85, (1, .42, .85), 2),
                  16: (70, .7, (.4, .95, 1), 8), 22: (60, .55, (.5, 1, .7), 0), 33: (96, .8, (.45, .9, 1), 0)}
        decor = {(c["x"], c["y"]): c["tileX"] + c["tileY"] * 8 for c in room.layers["Decor"]}
        world = {(c["x"], c["y"]): c["tileX"] + c["tileY"] * 8 for c in room.layers["World"]}
        for label, prefab, x, y, order in plan:
            if prefab == "Cavern Glow":
                k = decor.get((x // CELL, y // CELL), world.get((x // CELL, y // CELL)))
                if k in lookup:
                    rad, inten, col, oy = lookup[k]
                    lights.append((x, y + oy, rad, inten))
            elif prefab == "Glow Grub":
                lights.append((x, y - 9, 72, .9))
            elif prefab == "Explorer":
                lights.append((x, y - 20, 140, 1.0))
        scale = 4
        mw, mh = W // scale, H // scale
        illum = array.array("f", [amb]) * (mw * mh)
        for (lx, ly, rad, inten) in lights:
            for py in range(max(0, int((ly - rad) / scale)), min(mh, int((ly + rad) / scale) + 1)):
                for px in range(max(0, int((lx - rad) / scale)), min(mw, int((lx + rad) / scale) + 1)):
                    dx, dy = px * scale + 2 - lx, py * scale + 2 - ly
                    d2 = dx * dx + dy * dy
                    if d2 < rad * rad:
                        f = 1 - d2 / (rad * rad)
                        i = py * mw + px
                        illum[i] = min(1.0, illum[i] + f * f * inten)
        mask = Image.new("L", (mw, mh))
        mask.putdata([int(max(0, min(255, (1 - v) * 242))) for v in illum])
        mask = mask.resize((W, H), Image.BILINEAR)
        shade = Image.new("RGBA", (W, H), (3, 6, 15, 255))
        shade.putalpha(mask)
        litimg = canvas.copy()
        litimg.alpha_composite(shade)
        save_strips(litimg, "level_%s_lit.png" % out_name)


# =====================================================================================
# Main
# =====================================================================================
def build_cavern():
    room = Room("rootdeep", "Rootdeep Caverns", CAVERN_SECTIONS, CAVERN_DECOR, "Cavern Tiles", "cavern")
    room.build_tiles()
    plan = plan_instances(room, CAVERN_ACTORS)
    prize_and_extra_instances(room, plan)
    glows = glow_instances(room, plan)
    room.instance_plan = plan
    assert len(room.actors.get("O", [])) + len(room.actors.get("Y", [])) == 3, "three Big Sun Coins"
    assert len(room.actors.get("G", [])) == 1 and len(room.actors.get("k", [])) == 1
    gate_cells = [c for c in room.layers["Gate"]]
    assert gate_cells and all(c["x"] == room.W - 20 for c in gate_cells), "the gate is the arena's first column"
    room.write(["SNES Look", "Cavern Damp"],
               [("Cavern back", "Assets/Sprites/Cavern Back.image.json", "Single", 9500, True),
                ("Cavern mid", "Assets/Sprites/Cavern Mid.image.json", "Single", 9000, True)],
               [0.05, 0.04, 0.09, 1])
    return room, plan, [], glows


def build_canopy():
    room = Room("canopy", "Windswept Canopy", CANOPY_SECTIONS, CANOPY_DECOR, "Canopy Tiles", "canopy")
    room.build_tiles()
    plan = plan_instances(room, CANOPY_ACTORS)
    prize_and_extra_instances(room, plan)
    plan.insert(1, ("Wind Controller", "Wind Controller", 48, 16, -150))
    room.instance_plan = plan
    assert len(room.actors.get("O", [])) == 3, "three Big Sun Coins"
    assert len(room.actors.get("Z", [])) == 1 and len(room.actors.get("k", [])) == 1
    room.write(["SNES Look", "Golden Hour"],
               [("Canopy sky", "Assets/Sprites/Canopy Sky.image.json", "StretchView", 10000, False),
                ("Canopy far trees", "Assets/Sprites/Canopy Far Trees.image.json", "Single", 9500, True),
                ("Canopy branches", "Assets/Sprites/Canopy Branches.image.json", "Single", 9000, True)],
               [0.98, 0.78, 0.52, 1])
    return room, plan, room.leaf_specs


def summarize(room, plan):
    counts = {}
    for label, prefab, x, y, order in plan:
        counts[prefab] = counts.get(prefab, 0) + 1
    print("%s: %d x 360 px (%d columns), %d instances" % (room.name, room.W * CELL, room.W, len(plan)))
    print("  " + ", ".join("%s %d" % (k, v) for k, v in sorted(counts.items())))


def main():
    cavern, cplan, _, glows = build_cavern()
    canopy, nplan, leaves = build_canopy()
    build_objects(leaves)
    build_particles()
    build_shaders()
    summarize(cavern, cplan)
    print("  Cavern Glow lights: %d" % glows)
    summarize(canopy, nplan)
    if "--no-render" not in sys.argv:
        render_preview(cavern, cplan, "rootdeep_caverns", lit=True)
        render_preview(canopy, nplan, "windswept_canopy")
    if "--map" in sys.argv:
        for room in (cavern, canopy):
            print(room.name)
            for r in range(ROWS):
                print("".join(room.grid[r]))
    if "--no-check" not in sys.argv:
        # The playability proof (reach_levels_a.py) reads the rooms just written.
        import reach_levels_a
        if reach_levels_a.main([]) != 0:
            raise SystemExit("levels_a_gen: the playability check failed (see above)")


if __name__ == "__main__":
    main()
