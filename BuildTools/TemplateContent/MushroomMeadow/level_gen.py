"""Mushroom Meadow 1-1 room generator.

Deterministic: the room JSON is built entirely from the ASCII sections below.
Edit a section, run `python level_gen.py`, and the room is rewritten.

Rows 0..11 (32 px each, room is 360 px tall). Ground surface is row 9 (y = 288).

Legend
  .  empty
  G  ground (auto-tiled: grass top / caps / dirt / edges)        World
  S  stone block (7)                                              World
  P  pipe, 2 columns wide (auto top/body, left/right)             World
  L  log platform (auto 12/13/14)                                 World
  M  giant mushroom cap (auto 26/27/28)                           World
  C  crystal block (33)                                           World
  B  brick (2, breakable)                                         Blocks
  ?  question block (3, pays a coin)                              Blocks
  ^  thorn bramble (32, touching hurts)                           Hazards
  Decor (non-solid): t tuft, f flowers, m small mushrooms, b bush, n fence,
        s arrow sign, v vine, r rock, | mushroom stem, l lantern, F tall flower, c clover
  Actors: @ Explorer, o Sun Coin, O Big Sun Coin, a Acorn Walker, e Buzz Bee,
          u Bounce Mushroom, k Checkpoint, Z Finish (Summit Shrine), H Wayfarer Lodge (background),
          R Level Rules (room rules + music), g Glide Seed, h Heart Berry, W Glowspore (story orb)
  Prize markers (power-ups sprouting from seed pods) are listed in PRIZES by column/row, because
  they share their cell with the '?' block.
"""
import hashlib
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
ROOM = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates", "Assets",
                    "TwoDShowcase", "Assets", "Rooms", "Mushroom Meadow.room.json")
ROWS = 12

# Each section: name, rows 0..11. Short rows are padded with '.'.
SECTIONS = [
    ("Teach: safe start, first coins, first seed pod, first acorn", [
        ".R",
        "",
        "",
        "",
        "",
        "",
        "..........?.............",
        "",
        ".f.@..s.tf..o.o.c..a..bt",
        "GGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGG",
    ]),
    ("Teach: first pit, stone row with seed pods", [
        "",
        "",
        "",
        "..............?.........",
        "",
        ".....ooo................",
        "............B?B?B.......",
        "....................S...",
        "..t..r...r.f.o.o.o..St.c",
        "GGGGGG...GGGGGGGGGGGGGGG",
        "GGGGGG...GGGGGGGGGGGGGGG",
        "GGGGGG...GGGGGGGGGGGGGGG",
    ]),
    ("Test: pipes with an acorn pen", [
        "",
        "",
        "",
        "",
        "..oo..................oo....",
        "",
        "",
        "..PP..................PP....",
        ".tPP.f..a......o.o.a..PP.bf.",
        "GGGGGGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGGGGGG",
    ]),
    ("Teach: giant mushroom caps and logs climb to a secret", [
        "",
        "...................g.O....",
        "...................MMMM.e.",
        ".............o..o...||....",
        ".............LLLL...||....",
        "........o..o........||....",
        "........MMMM........||....",
        ".......S.||.........||....",
        ".c.t...S.||.f..m.b..||.t..",
        "GGGGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGGGG",
    ]),
    ("Midpoint checkpoint", [
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        ".thnnnl.k.lnnn.f",
        "GGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGG",
    ]),
    ("Test: gap sequence and a thorn trench", [
        "",
        "",
        "",
        "",
        "",
        "......o.......o.......o.........",
        "",
        "",
        ".t..r.....f.....c.......t.F..f..",
        "GGGGG...GGGGG...GGGGG^^^GGGGGGGG",
        "GGGGG...GGGGG...GGGGGGGGGGGGGGGG",
        "GGGGG...GGGGG...GGGGGGGGGGGGGGGG",
    ]),
    ("Twist: bounce mushroom to the high coin trail", [
        "",
        ".............O..........",
        "........ooooo...........",
        ".......MMMMMMM..........",
        "....o.....||............",
        "....o.....||............",
        "....o.....||............",
        "..........||............",
        ".f.tu.c...||....t...f...",
        "GGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGG",
    ]),
    ("Test: bee meadow (stomp bees from the logs)", [
        "",
        "",
        "",
        ".....e..........e.......",
        "",
        "...ooo.....ooo.....ooo..",
        "...LLL.....LLL.....LLL..",
        "",
        ".t..f..c..b...m..r..F.t.",
        "GGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGG",
    ]),
    ("Twist: pipe pen, plateau, wide pit and a hidden sky log", [
        "....................O.......",
        "...................LLLL.....",
        "",
        "",
        "",
        "",
        "............oooo.u..........",
        "..PP........GGGGGG..........",
        "..PP....a...GGGGGG..f.t.c...",
        "GGGGGGGGGGGGGGGGGG....GGGGGG",
        "GGGGGGGGGGGGGGGGGG....GGGGGG",
        "GGGGGGGGGGGGGGGGGG....GGGGGG",
    ]),
    ("Finale: thorn trench, staircase, Summit Shrine and Wayfarer Lodge", [
        "",
        "",
        ".................................W",
        "",
        "....................oo..................",
        "..................SSSS..................",
        "................SSSSSS..................",
        "..............SSSSSSSS..................",
        ".ts.......f.SSSSSSSSSS..t..c..f..Z.H..tf",
        "GGGG^^^GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG",
        "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG",
    ]),
]

DECOR = {"t": 16, "f": 17, "m": 18, "b": 19, "n": 20, "s": 21, "v": 22, "r": 23, "|": 29,
         "l": 35, "F": 36, "c": 37}
ACTORS = set("@oOaeukZHRghW")
# (column, row) of a seed pod -> Prize marker object placed at the block's centre.
PRIZES = {(37, 6): "Prize Shield", (39, 6): "Prize Spore", (38, 3): "Prize Star"}
# Optional pickups: the Sword just past the first acorn, the Propeller Flower beside the first giant cap.
ITEM_PICKUPS = [("Item Sword", 21, 8), ("Item Propeller Flower", 89, 8)]
SOLID_TILES = set(range(0, 16)) | set(range(24, 29)) | {33, 34, 38}


def build_grid():
    grid = [[] for _ in range(ROWS)]
    for name, rows in SECTIONS:
        assert len(rows) == ROWS, name
        width = max(len(r) for r in rows)
        for i in range(ROWS):
            grid[i].extend(rows[i].ljust(width, "."))
    return grid


def gid(text):
    return hashlib.md5(("mushroom-meadow-1-1:" + text).encode("utf-8")).hexdigest()


def main():
    grid = build_grid()
    W = len(grid[0])
    at = lambda r, c: grid[r][c] if 0 <= r < ROWS and 0 <= c < W else "."
    layers = {"World": [], "Blocks": [], "Decor": [], "Hazards": []}
    solid = set()

    def put(layer, c, r, idx):
        layers[layer].append({"x": c, "y": r, "tileX": idx % 8, "tileY": idx // 8})
        if layer in ("World", "Blocks") and idx in SOLID_TILES:
            solid.add((c, r))

    # Ground that reaches the bottom map row continues to the bottom of the view (the room is
    # 360 px, so row 11 is half visible) instead of ending in an edge strip above the sky.
    for c in range(W):
        if grid[ROWS - 2][c] == "G" and grid[ROWS - 1][c] == ".":
            grid[ROWS - 1][c] = "G"

    for c in range(W):
        for r in range(ROWS):
            ch = grid[r][c]
            if ch == "G":
                up, left, right, down = at(r - 1, c) == "G", at(r, c - 1) == "G", at(r, c + 1) == "G", at(r + 1, c) == "G"
                if r == ROWS - 1:
                    down = True
                if c == 0:
                    left = True
                if c == W - 1:
                    right = True
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
                put("World", c, r, idx)
            elif ch == "S":
                put("World", c, r, 7)
            elif ch == "C":
                put("World", c, r, 33)
            elif ch == "P":
                top = at(r - 1, c) != "P"
                run = 0
                k = c
                while at(r, k) == "P":
                    run += 1
                    k -= 1
                is_left = run % 2 == 1
                put("World", c, r, (8 if is_left else 9) if top else (10 if is_left else 11))
            elif ch == "L":
                left, right = at(r, c - 1) == "L", at(r, c + 1) == "L"
                put("World", c, r, 13 if left and right else (12 if not left else 14))
            elif ch == "M":
                left, right = at(r, c - 1) == "M", at(r, c + 1) == "M"
                put("World", c, r, 27 if left and right else (26 if not left else 28))
            elif ch == "B":
                put("Blocks", c, r, 2)
            elif ch == "?":
                put("Blocks", c, r, 3)
            elif ch == "^":
                put("Hazards", c, r, 32)
            elif ch in DECOR:
                put("Decor", c, r, DECOR[ch])
            elif ch in ACTORS or ch == ".":
                pass
            else:
                raise SystemExit("Unknown map character %r at column %d row %d" % (ch, c, r))

    # Actors -------------------------------------------------------------
    def cell_center(c, r):
        return c * 32 + 16, r * 32 + 16

    def cell_feet(c, r):
        return c * 32 + 16, (r + 1) * 32

    # mask boxes relative to (x,y): (left, top, right, bottom)
    masks = {"@": (-7, -34, 7, 0), "o": (-6, -8, 6, 8), "O": (-12, -12, 12, 12), "a": (-13, -22, 13, 0),
             "e": (-10, -8, 10, 8), "u": (-14, -20, 14, 0), "k": (-8, -66, 16, 0), "Z": (-3, -182, 3, -10),
             "g": (-10, -10, 10, 10), "h": (-7, -7, 7, 7), "W": (-12, -12, 12, 12), "R": (0, 0, 1, 1)}
    found = {k: [] for k in ACTORS}
    for c in range(W):
        for r in range(ROWS):
            ch = grid[r][c]
            if ch in ACTORS:
                found[ch].append((c, r))
    assert len(found["@"]) == 1 and len(found["k"]) == 1 and len(found["Z"]) == 1 and len(found["H"]) == 1
    assert len(found["O"]) == 3, "three Big Sun Coins"

    def clear(ch, x, y):
        l, t, rgt, b = masks[ch]
        x0, y0, x1, y1 = x + l, y + t, x + rgt - 0.01, y + b - 0.01
        for cc in range(int(x0 // 32), int(x1 // 32) + 1):
            for rr in range(int(max(0, y0) // 32), int(y1 // 32) + 1):
                if (cc, rr) in solid:
                    raise SystemExit("%s at %s,%s overlaps solid tile %d,%d" % (ch, x, y, cc, rr))

    nodes = []

    def background(node_id, name, asset, layout, depth, repeat, x=0, y=0):
        nodes.append({"id": node_id, "kind": "Background", "name": name, "enabled": True, "layerId": "scenery",
                      "transform": {"position": [x, y, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                      "background": {"asset": asset, "mode": "TwoD", "layout": layout, "depth": depth,
                                     "repeatX": repeat, "opacity": 1}})

    background("c24e1cbba0c25e5db4de46bf8e4fc90c", "Morning sky", "Assets/Sprites/Morning Sky.image.json", "StretchView", 10000, False)
    background(gid("far-hills"), "Far hills", "Assets/Sprites/Far Hills.image.json", "Single", 9500, True)
    background("eb02d2011ff154ad9ebd815e2920475f", "Cloud-covered hills", "Assets/Sprites/Cloud Hills.image.json", "Single", 9000, True)
    background(gid("meadow-forest"), "Meadow forest", "Assets/Sprites/Meadow Forest.image.json", "Single", 8500, True)
    hc, hr = found["H"][0]
    background("35c140d389a35e7e9f1979454085e133", "Wayfarer Lodge", "Assets/Sprites/Wayfarer Lodge.image.json", "Single", 900, False,
               hc * 32, (hr + 1) * 32 - 144)

    def tiles(node_id, name, depth, collision, cells):
        cells.sort(key=lambda cell: (cell["x"], cell["y"]))
        nodes.append({"id": node_id, "kind": "TileLayer", "name": name, "enabled": True, "layerId": "tiles",
                      "transform": {"position": [0, 0, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                      "tileLayer": {"tileset": "Assets/Sprites/Meadow Tiles.image.json", "cellWidth": 32, "cellHeight": 32,
                                    "collisionEnabled": collision, "depth": depth, "cells": cells}})

    tiles("3e47635bda1054a0b88e02210a3f98af", "World", 500, True, layers["World"])
    tiles(gid("blocks"), "Blocks", 450, True, layers["Blocks"])
    tiles(gid("decor"), "Decor", 700, False, layers["Decor"])
    tiles(gid("hazards"), "Hazards", 480, False, layers["Hazards"])

    def instance(name, prefab, x, y, order=0, node_id=None):
        nodes.append({"id": node_id or gid("node:" + name), "kind": "GameObject", "name": name, "enabled": True,
                      "layerId": "actors", "order": order,
                      "transform": {"position": [x, y, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                      "gameObject": {"prefab": "Assets/Objects/%s.object.json" % prefab}})

    def place(ch, pos):
        c, r = pos
        x, y = cell_center(c, r) if ch in "oOegWR" else cell_feet(c, r)
        if ch == "h":
            x, y = c * 32 + 16, (r + 1) * 32 - 10
        clear(ch, x, y)
        return x, y

    x, y = place("@", found["@"][0])
    assert (x, y) == (112, 288), "the regression suite expects the hero at 112,288"
    instance("Explorer", "Explorer", x, y, -100, "56919488dca35c98a3dae1c9ad575835")
    x, y = place("k", found["k"][0])
    instance("Midpoint flag", "Checkpoint", x, y, 0, "d7a65223e4265b2c9c4837db4abae9b8")
    x, y = place("Z", found["Z"][0])
    instance("Summit shrine", "Finish", x, y, 0, "91e233f97ffa53648600e01503a106c5")
    for i, p in enumerate(found["a"], 1):
        x, y = place("a", p)
        instance("Acorn %d" % i, "Acorn Walker", x, y, 0, "97ecf9c6b43d5301a53e86fa4a01b11d" if i == 1 else None)
    # Plain coins must come before Big Sun Coins: tests look up the first "Sun Coin" prefab.
    for i, p in enumerate(found["o"], 1):
        x, y = place("o", p)
        instance("Coin %02d" % i, "Sun Coin", x, y)
    for i, p in enumerate(found["O"], 1):
        x, y = place("O", p)
        instance("Big Coin %d" % i, "Big Sun Coin", x, y)
    for i, p in enumerate(found["e"], 1):
        x, y = place("e", p)
        instance("Bee %d" % i, "Buzz Bee", x, y)
    for i, p in enumerate(found["u"], 1):
        x, y = place("u", p)
        instance("Bounce %d" % i, "Bounce Mushroom", x, y)
    # Phase 2: the room rules object, power-ups, seed pod prizes and the Glowspore above the shrine.
    assert len(found["R"]) == 1 and len(found["W"]) == 1
    x, y = place("R", found["R"][0])
    instance("Level Rules", "Level Rules", x, y)
    for i, p in enumerate(found["g"], 1):
        x, y = place("g", p)
        instance("Glide Seed %d" % i, "Glide Seed", x, y)
    for i, p in enumerate(found["h"], 1):
        x, y = place("h", p)
        instance("Heart Berry %d" % i, "Heart Berry", x, y)
    x, y = place("W", found["W"][0])
    instance("Glowspore", "Glowspore", x, y)
    # Round 4: optional item pickups on open floor (Item Kit). (object, column, row of the empty cell above the floor)
    for i, (prefab, c, r) in enumerate(ITEM_PICKUPS, 1):
        assert grid[r][c] in ".tfmbnsvrc|" and grid[r + 1][c] == "G", "item %s needs open floor" % prefab
        x, y = c * 32 + 16, (r + 1) * 32 - 12
        clear("g", x, y)
        instance("%s %d" % (prefab, i), prefab, x, y)
    for (c, r), prize in sorted(PRIZES.items()):
        assert grid[r][c] == "?", "%s must sit on a seed pod (column %d row %d)" % (prize, c, r)
        x, y = cell_center(c, r)
        instance("%s %d-%d" % (prize, c, r), prize, x, y)

    room = {
        "$schema": "genesis.room", "version": 1, "id": "9c7cd65501bf5604bb7bee6c41b18c62",
        "name": "Mushroom Meadow 1-1", "dimension": "TwoD",
        "settings": {"width": W * 32, "height": 360, "depth": 720, "windowWidth": 1280, "windowHeight": 720,
                     "scaleViewportsWithWindow": False, "targetFps": 60, "fixedFps": 60, "vsync": True,
                     "captureMouse": False, "gridSize": 32, "snapEnabled": True, "pixelArtSampling": True},
        "environment": {"backgroundColor": [0.56, 0.83, 1, 1], "fogEnabled": False, "postEffects": ["SNES Look"]},
        "viewports": [{"enabled": True, "sourceX": 0, "sourceY": 0, "sourceWidth": 640, "sourceHeight": 360,
                       "portX": 0, "portY": 0, "portWidth": 1280, "portHeight": 720, "followTarget": "Explorer",
                       "followMarginX": 220, "followMarginY": 64, "followSpeedX": 20, "followSpeedY": -1}],
        "layers": [{"id": "scenery", "name": "Backdrop", "order": 0, "enabled": True},
                   {"id": "tiles", "name": "Painted world", "order": 1, "enabled": True},
                   {"id": "actors", "name": "Actors and pickups", "order": 2, "enabled": True}],
        "nodes": nodes,
    }
    with open(ROOM, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(room, handle, indent=2)
        handle.write("\n")

    objects = sum(1 for n in nodes if n["kind"] == "GameObject")
    print("Room %dx360 (%d columns), solid cells %d, objects %d" % (W * 32, W, len(solid), objects))
    for k, label in (("o", "coins"), ("O", "big coins"), ("a", "acorns"), ("e", "bees"), ("u", "bounce mushrooms")):
        print("  %s: %d" % (label, len(found[k])))
    print("  seed pods: %d, stones: %d" % (sum(1 for c in layers["Blocks"] if c["tileX"] == 3), sum(1 for c in layers["Blocks"] if c["tileX"] == 2)))
    print("  checkpoint x=%d, finish x=%d" % (found["k"][0][0] * 32 + 16, found["Z"][0][0] * 32 + 16))
    if "--map" in os.sys.argv:
        for r in range(ROWS):
            print("".join(grid[r]))


if __name__ == "__main__":
    main()
