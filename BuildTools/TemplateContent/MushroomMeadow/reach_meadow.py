"""Reachability proof for Mushroom Meadow (level 1), using Pip's real physics (hero_physics.py).

Run after level_gen.py (it reads the generated room):  python reach_meadow.py
Exit code 0 = every check passed. Checks:
  1. From the start and from the checkpoint respawn, the Summit Shrine (Finish) is reachable with no
     power-ups, at the design margin (85% jump height, 92% run speed), in two worlds: every
     breakable stone still there, and every stone already broken. Seed pods stay solid either way
     (a bumped pod becomes a used block), so no bump can seal or open the route.
  2. No reachable floor is a soft-lock: from every floor Pip can stand on, the shrine is still reachable.
  3. Falling into a thorn trench is survivable: from the trench floor Pip can climb out and finish.
  4. Spawn and checkpoint respawn stand on solid ground.
  5. Optional extras (Big Sun Coins, Glide Seed, seed pods with prizes) are reachable with the real
     (full) physics, so no secret is broken; whether they also fit the design margin is reported.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hero_physics as hp  # noqa: E402

ROOM = "Rooms/Mushroom Meadow.room.json"


def main():
    failures = []
    base = hp.RoomGrid.load(ROOM)
    no_stones = hp.RoomGrid.load(ROOM, removed_tiles={("Blocks", 2)})
    goal = base.object_rect("Finish")
    (_, sx, sy), = base.objects("Explorer")
    (_, cx, cy), = base.objects("Checkpoint")
    starts = [("start", (sx, sy)), ("checkpoint", (cx + 24, cy))]   # Explorer: spawnX = flag x + 24

    for label, (x, y) in starts:
        if not base.on_ground(x, y) or base.body_hits(x, y):
            failures.append("%s %r is not standing on clear ground" % (label, (x, y)))

    for world_label, grid in (("stones intact", base), ("stones broken", no_stones)):
        for label, start in starts:
            result = hp.reach(grid, start, [goal])
            print(result.report("%s, %s" % (label, world_label)))
            if not result.ok():
                failures.append("%s (%s): goal %s, traps %r" % (label, world_label, result.goal_reached, result.traps))

    # Thorn trenches: with the thorns made harmless, every trench floor must lead back out.
    safe = base.copy(hazard=set())
    trench_floors = sorted({(c, r) for (c, r) in base.hazard})
    seen = set()
    for c, r in trench_floors:
        floor_y = (r + 1) * hp.TILE
        x = c * hp.TILE + 16
        if (c, r + 1) not in base.solid or c in seen:
            continue
        seen.add(c)
        result = hp.reach(safe, (x, floor_y), [goal])
        status = "ok" if result.goal_reached else "TRAPPED"
        print("thorn trench column %d floor y=%d: climb out and finish %s" % (c, floor_y, status))
        if not result.goal_reached:
            failures.append("thorn trench at column %d traps Pip" % c)

    # Optional extras with the real physics (and the design margin, for information).
    extras = []
    for prefab in ("Big Sun Coin", "Glide Seed", "Heart Berry"):
        for i, (name, _, _) in enumerate(base.objects(prefab)):
            extras.append((name, base.object_rect(prefab, i)))
    pods = [(c, r) for (c, r) in base.solid if _blocks_tile(base, c, r) == 3]
    for margin_label, js, ss in (("full physics", 1.0, 1.0), ("design margin", None, None)):
        result = hp.reach(base, starts[0][1], [goal], jump_scale=js, speed_scale=ss,
                          touch=[rect for _, rect in extras])
        for i, (name, _) in enumerate(extras):
            ok = i in result.touched
            print("  %-14s %-24s %s" % (margin_label, name, "reachable" if ok else "NOT reachable"))
            if not ok and js == 1.0:
                failures.append("optional %s is unreachable even with full physics" % name)
        for cell in sorted(pods):
            ok = cell in result.bonked
            print("  %-14s seed pod column %d row %-6d %s" % (margin_label, cell[0], cell[1], "bumpable" if ok else "NOT bumpable"))
            if not ok and js == 1.0:
                failures.append("seed pod %r cannot be bumped even with full physics" % (cell,))

    # The input-only bot of Acceptance.MushroomMeadow.CompleteGame (same probes, no enemies).
    frames, outcome, where = bot_run(base, (sx, sy), goal)
    print("acceptance bot (no enemies): %s after %d frames at %r" % (outcome, frames, where))
    if outcome != "finished":
        failures.append("the acceptance bot's input-only route %s at %r" % (outcome, where))

    if failures:
        print("FAILED:")
        for f in failures:
            print("  " + f)
        return 1
    print("Mushroom Meadow: completable from start and checkpoint without power-ups; no soft-locks.")
    return 0


def bot_run(grid, start, goal, max_frames=10800):
    """Mirror of the traversal loop in MushroomMeadowAcceptanceSuite (D + Shift held, jump when an
    obstacle or gap is ahead, hold off over a pit unless the jump was meant to cross it)."""
    p = hp.Pip(grid, *start)
    space = False
    crossing = False
    hits = grid.box_hits
    for frame in range(max_frames):
        x, y = p.x, p.y
        ground = hits(x - 7, y - 33, x + 7, y + 1)
        obstacle = hits(x + 34, y - 33, x + 48, y - 1)
        gap = not hits(x + 44, y + 1, x + 58, y + 4)
        pit_ahead = not hits(x + 52, 289, x + 66, 291)
        was_space = space
        if ground:
            jump = not space and (obstacle or gap)
            crossing = jump and pit_ahead
            space = jump
        right = (not pit_ahead) or ground or crossing
        p.step(1 if right else 0, True, held=space, pressed=space and not was_space)
        if p.dead:
            return frame, "fell out of the room", (round(p.x), round(p.y))
        if p.hurt:
            return frame, "touched thorns", (round(p.x), round(p.y))
        if hp.rects_overlap(p.box(), goal):
            return frame, "finished", (round(p.x), round(p.y))
    return max_frames, "timed out", (round(p.x), round(p.y))


def _blocks_tile(grid, c, r):
    for node in grid_nodes_tiles(grid):
        if node["name"] == "Blocks":
            for cell in node["tileLayer"]["cells"]:
                if cell["x"] == c and cell["y"] == r:
                    return cell["tileY"] * 8 + cell["tileX"]
    return -1


def grid_nodes_tiles(grid):
    import json
    if not hasattr(grid, "_tile_nodes"):
        with open(os.path.join(grid.assets, ROOM), encoding="utf-8") as handle:
            grid._tile_nodes = [n for n in json.load(handle)["nodes"] if n.get("kind") == "TileLayer"]
    return grid._tile_nodes


if __name__ == "__main__":
    sys.exit(main())
