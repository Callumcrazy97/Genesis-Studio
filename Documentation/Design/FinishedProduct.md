# What a finished Genesis looks like, measured

28 October 2026. Cal's question: a quick three.js game written in half an hour looks better than a
game that takes days in Genesis. Is there a reason Genesis cannot reach that level? This page sets
the bar, measures Genesis against it on the same small game, and records what was changed so the
bar is met.

## The bar

A finished Genesis lets a person, or an AI driving Studio, make a small good-looking game **as fast
as a quick three.js game** and better in what an engine adds (editing, saving, exporting, running
well). Measured on one game built both ways, the **Nature Walk**: a walk round a lake through
rolling hills, trees, grass and wildflowers under a sky, with wind, birdsong and footsteps.

| Measure | three.js (the yardstick) | Genesis must |
|---|---|---|
| Time to the first good picture | ~15 minutes, 292 lines in one file | take no longer |
| How it looks | [reference-threejs.html](../../BuildTools/Benchmarks/NatureWalk/reference-threejs.html) | look as good side by side |
| Change something and look again | about 2 seconds (save, refresh) | take seconds, not a minute |
| A player who can walk round it | `PointerLockControls`, ~25 lines | be one card or one word |
| Frame time | 60 fps in a browser | 60 fps with room to spare |

Both versions are built by `BuildTools\Benchmarks\NatureWalk\`: `reference-threejs.html` is the
three.js game and `NatureWalk.ps1` builds the Genesis one with Studio's own commands (terrain
brushes, scatter rules, grass, water, the Model editor's primitives, the synthesiser, particles and
an Object recipe), then plays it for pictures (`-Play`) and frame times (`-Benchmark`).

## What was measured first (28 October, before the changes)

The Genesis version was first built call by call, as an AI session would, with no changes to Genesis.

- **The look is not the problem.** With Genesis's own terrain, scatter, grass and sky, the walk
  looked as good as the three.js one on the first run: dense swaying grass, a worn path, pines and
  oaks in clumps, hills fading into haze, sun shadows. (Picture: [side-by-side-before.jpg](../../BuildTools/Benchmarks/NatureWalk/side-by-side-before.jpg).)
- **Making it was slow and full of traps:**
  - Each command was its own Studio start: 0.15 to 9 s a call, about 30 calls.
  - A new room was 2D. Making it 3D meant knowing the file's `dimension` field.
  - A new 3D room had no sky: a dark blue-grey void until the right five environment fields were set.
  - No Object recipe could walk in 3D: the first-person walker was 45 lines written by hand, and
    one of them used a built-in name (`speed`) by mistake.
  - An Object with no model was made 2D, and Studio could make an Object 3D only by giving it a model.
- **Looking at a change took about a minute:** `game.run` took 66 s for 8 s of play, with 11 s
  before the game started and 35 s before its first log line (just after another build replaced
  the Player's files).

## What was changed

| Gap | Change |
|---|---|
| A call per command | `batch`: a list of commands in one go, checked once at the end. The whole walk builds in **1.6 s** (33 commands) instead of minutes. |
| A 2D room by default, and no sky | The Room editor's 3D switch and the Terrain editor's Use in game give a room with no sky a sunny day (one undo takes it back). `room.create` with `"dimension": "3D"` starts on a sunny day; `"scene"` takes any of the Room editor's Sky cards (now one list, `RoomScenePresets`, shared by the editor and automation). `room.setEnvironment` takes `"scene"` too. |
| No 3D player | Object recipes **Walk in first person**, **Third-person character** and a 3D **Spin**, as ordinary PGSL in the Object editor's Behaviour cards and through automation (`object.create` `recipes`, `object.addRecipe`, listed by `object.info`). An Object that shows nothing yet is offered the 3D players, and choosing one makes it 3D. |
| Your code against a recipe's | `object.create` puts recipes first and the caller's code after, so it can change their numbers. |
| Learning PGSL and the commands each time | [Cookbook.md](../Cookbook.md): the loop, a 3D place, a 2D level, a player, the PGSL wanted most, the look, the pitfalls. Its PGSL is compiled by the test suite. |
| A 3D window the size of the room in metres | `room.create` gives a 3D room a 1280 by 720 window unless told otherwise (a 400 by 80 m valley had played in a 400 by 80 window). |
| No way to paint a 2D level's tiles | `room.paintTiles` (cells and rectangles from a tile set, solid on request, autotiled sets choosing their own edges) and tile sets with tile size and solid tiles from `image.create`. |
| How long a look takes | `game.run` reports `startSeconds`, launch to the first room ready. |

## Tried and not kept

- **Hemisphere sky light and eye adaptation on the Sky cards.** On the sunny walk, switching them on
  (strength 1.5, key 0.18) made the picture darker and bluer, not brighter in the shade, so the
  scene presets were left as they were. The walk lightens its shade instead with
  `Engine.Rendering.Shadows.Strength = 0.7`, a look of its own in its own script.

## Scorecard

| Measure | three.js | Genesis before | Genesis now |
|---|---|---|---|
| Building the walk | ~15 min of writing | ~30 separate calls, minutes, several traps | one batch, 1.6 s |
| Player | ~25 lines | 45 lines by hand | one recipe |
| Looks | the yardstick | as good (grass, trees, haze, shadows) | as good, with flowers, a clear path and lighter shade ([side-by-side-after.jpg](../../BuildTools/Benchmarks/NatureWalk/side-by-side-after.jpg)) |
| Change and look again | ~2 s | 66 s (cold, after another build replaced the files) | 1.4 s to rebuild the whole scene; the game then starts in 1.7 s (`startSeconds`), so a 12.5 s walk with three pictures took 17 s in all |
| Frame time | 60 fps (vsync) | not measured | uncapped, Direct3D 11, 1600 by 900: median 1.12 ms (890 fps), 99th percentile 1.61 ms, worst 3.86 ms, 3.3 KB allocated a frame, 28 draw calls, 602,000 triangles, 572 MB peak |

## Checked on the published build (28 October, 05:24)

`Build.bat --tiers` passed (editor 297, engine 330, PGSL 159 checks, the DX11 smoke; the attended
full-screen check is not run unattended) and was published. On it the walk's own checks pass (the
walker stands on the ground, the walk starts on the path), and a third-person character made from the
recipe walks where the camera looks and stays on the ground.

Found and fixed: `game.benchmark` given scripted input (`steps`) did not finish and was stopped at
its time limit. A replay plays a recorded frame per rendered frame, so uncapped (about 890 fps here)
its 24 s of input ran out in under two seconds, and a replay's end closes the game, which hung
while the first room was still behind its cover. A benchmark now carries on when its input ends and
finishes on its own clock. (The standard benchmark scenes never pass input, so this had not shown.)
The 06:58 build's "walking with W held" run (median 1.06 ms) was **not** a walk: the replay also
plays the recorded 1/60 s time step, so its 15 s of input were played out in about a second, inside
the warm-up, and the measured frames stood still (found by the Task Checker, 28 October). Scripted
input is being changed to play by clock time; the walking figure will be measured again on that build.

## Measured again, honestly (28 October, daytime)

The Task Checker pointed out that the scorecard set three.js *writing* time against Genesis *running*
a script tuned all night. Two measurements replace that row:

- **Blind trial (an AI, the shipped guides only).** A fresh agent with no notes, no source and no
  benchmark script, only the published Studio, its Documentation folder and the three.js file, built
  the walk through automation. It made a good picture after **about 29 minutes of its own working
  time** (34 in all; another 148 minutes were spent waiting for the shared GPU), in 30 calls of which
  8 failed, 5 of them its own PowerShell mistakes. Its picture: a path through a flowery meadow
  between clumped pines and oaks under a blue sky, close to the three.js version but not as good
  (grass one bright green, pale turquoise water, a flat sky, one looping birdsong). Its worst traps,
  now fixed in the guides and commands: no worked example a user has (the Cookbook now ships
  [NatureWalk.batch.json](../NatureWalk.batch.json), kept building by a test); a key example that did
  not exist (`LeftShift`; now `Shift`, with the left and right names accepted); Studio from PowerShell
  seeming to do nothing without `| Out-String`; terrain height limits read as the relief; drawing
  functions listed without their arguments.
- **A person in Studio's UI** (traced in the code, not timed with a person): about 115 clicks and
  values over 7 editors, an estimated 60 to 75 minutes the first time and 20 practised. The traps:
  Run played the new project's empty Start room, not the terrain's room; forests were under Options
  and needed models made first; there was no way to make a sound in the UI. Fixed the same day: the
  terrain's room starts the game when the first room is empty, Decorate makes pine, oak and boulder
  models and their layers, meadow flowers and grass, water and paths in about 12 clicks, and the
  Audio editor makes sounds (Wind, Birdsong, footsteps...). Not yet timed with a person.

| Measure | three.js | Genesis now |
|---|---|---|
| An AI, from the guides, to a good picture | about 15 minutes (writing) | about 29 minutes of working time, before the guide fixes above |
| A person in Studio | (needs code) | estimated 35 to 45 minutes the first time after the fixes; not yet timed |

## Why a game used to take days

The engine was never the limit. The days went on:

1. **Learning the tools afresh every session**: which command, which field, which name is built in.
   The cookbook and the recipes now carry that.
2. **Writing content instead of using the editors**: earlier attempts generated meshes and grass in
   Python or Blender, which bypassed Genesis's terrain, scatter and grass and then needed workarounds.
   The Verdant Hollow template (`NatureWalkTemplate.cs`, about 1,900 lines of C# that generate a
   web reference's woodland, hidden in the 16 October template purge) was built that way. Built
   with Genesis's own tools, the same kind of walk looks right first time.
3. **Slow looks**: a minute per look means few looks, and quality comes from looking.
4. **Scope**: a three.js game is one scene; Genesis games were asked for whole (menus, saves, rooms).
   Start from one good scene, as the benchmark does, then grow it.
