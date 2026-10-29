# Automation: Studio's commands for AIs, Nova and tools

Genesis Studio can be driven by code as well as by hand. The same commands serve an AI client (over
the Model Context Protocol), Nova, scripts on the command line and the test suites. Making content
for a Genesis project goes through them, never through side scripts that write project files.

Every command says what it does and what it takes (a JSON schema), so a caller lists them first and
needs no knowledge of Genesis's source.

## Running it

From the published Studio folder:

```
"Genesis Application.exe" --automation "<project folder>" list
"Genesis Application.exe" --automation "<project folder>" call resource.create "{\"kind\":\"GameObject\",\"name\":\"Hero\"}"
"Genesis Application.exe" --automation "<project folder>" call text.replace @replace.json
"Genesis Application.exe" --automation "<project folder>" mcp
"Genesis Application.exe" --automation new "<parent folder>" "My Game" [Blank | 2D | TumbleIsles ...]
```

- `list` prints every command with its description and input schema.
- `call` runs one command and prints its result; the exit code is 0 when it worked, 1 when it did
  not, 2 for a mistake in the command line itself. The input can be inline JSON or `@file.json`; from Windows PowerShell use `@file.json`, as it strips the quotes from inline JSON passed to a program.
- **From PowerShell, capture the output:** `& "Genesis Application.exe" --automation ... | Out-String`.
  Studio is a windowed program, so a bare `&` neither waits for it nor shows what it prints, and a
  `new` or `call` seems to do nothing. Write batch files as JSON text (a here-string or a file you
  edit), not with `ConvertTo-Json` of nested hashtables, which can hang or turn strings into objects.
- `new` makes a project, as the Project Hub's New Project does, and prints its folder.
- `mcp` is a Model Context Protocol server on standard input and output. Add it to an MCP client
  (Claude Code: `claude mcp add genesis -- "<Studio folder>\Genesis Application.exe" --automation
  "<project folder>" mcp`). Commands appear as tools with underscores for dots
  (`resource.create` is `resource_create`).

No window opens and no graphics device is made, so it can run while a game is being played.

## What every call does

- **Paths are project-relative** (`Assets/Objects/Hero.object.json`) and never leave the project.
- **The input is checked against the schema:** unknown fields and missing required ones are refused
  with an explanation that includes the schema.
- **Destructive commands need `"confirm": true`** (`resource.delete`, `text.replace`). Deleting
  moves to the project's trash; replacing backs each file up first.
- **After a change the project is checked:** the project check and the strict script check Studio's
  Run makes. What they find comes back in the result's `issues`, with the path, line and column of
  a script error, so the caller can fix it at once. Pass `"check": false` to skip this for a batch.
- **Every change is written to `.genesis/Automation/audit.log`:** when, who (`cli`, `mcp`, `nova`,
  `test`), the command, whether it worked, what it changed and its input.

A result is `{ "ok", "message", "data", "changed", "issues" }`.

## Commands

| Command | Does |
|---|---|
| `commands.list` | Every command, what it does and its input schema. |
| `batch` | Several commands in order, in one go: `{ "calls": [{ "command": "...", "input": { ... } }, ...] }`. The project is checked once at the end instead of after each, and Studio starts once: the Nature Walk benchmark's 33 commands run in under a second, where 33 separate calls took minutes. Stops at the first failure (what came before stays done) unless `keepGoing`; gives each call's result. |
| `project.info` | The project's name, folder and resource counts by kind. |
| `project.check` | The project check and strict script check; lists errors and warnings. |
| `resource.tree` | The resources and folders under a folder, with their kinds. |
| `folder.create` | Makes a folder. |
| `resource.create` | Makes a resource of a kind (Image, Audio, Shader, PgslScript, GameObject, Room, Model, Particle, Physics, Terrain, UserInterface, Note...), as the Assets dock's New does. |
| `resource.rename` / `resource.move` / `resource.copy` / `resource.duplicate` | As the Assets dock does them; references by identity keep working. |
| `resource.delete` | Moves to the project's trash. Needs confirm. |
| `resource.import` | Imports files from anywhere as resources, as dropping them on the Assets dock does. |
| `resource.find` | Finds resources by name, path or the text inside them (Studio's Finder). |
| `resource.read` | The text of a script, a resource's JSON or a note. |
| `resource.set` | Sets fields of any JSON resource (particle effect, physics material, shader, audio, model settings...) as named in the file; objects merge, arrays are replaced. The engine's loader must still read it, so a value of the wrong kind is refused. An Object's components and events, and a terrain's size, have their own commands. |
| `shader.graph` | Builds a Shader resource from a node graph, as the Shader editor's Nodes view does: the graph (`nodes` and `connections`) is kept in the resource and the HLSL it makes becomes its code (a surface shader for Model and Terrain targets, a pixel shader for Image, Particle and Fullscreen), checked by compiling it for DX11 and Vulkan; `target` sets what it is for. Returns the code, the parameters and textures. A graph that cannot make a shader (a loop, an unknown node, too many parameters) is refused and the resource is left as it was. [Cookbook](Cookbook.md#make-a-shader-from-nodes). |
| `shader.nodes` | The node types `shader.graph` takes: what each does, its input and output ports with their value types, its modes and starting value. |
| `particle.create` | Makes a Particle effect from one of the engine's presets (Fire, Smoke, Embers, Sparks, Explosion, Magic, Fountain, Snow, Rain, Portal...), as the Particle editor's gallery does, with any settings changed. |
| `particle.module` | The Particle editor's module stack for one emitter (`emitter` by name, default the first): `list` (the default) gives its Spawn, Initialise, Update and Render modules, on or off, with their fields; `add`, `remove`, `on`, `off`, `reset` a `module` (`spawn.rate`, `update.gravity`, `update.colour`, `render.trail`...); `set` a `field` to a `value` given as JSON text (`"75"`, `"[0.8, 1.4]"` for a random range, gradient keys, or curve points `[{"time":0,"value":0,"smooth":true}, ...]`). It writes the effect's ordinary settings as the editor does; a switched-off module keeps its values to give back. |
| `audio.synth` | Makes a sound with Genesis's synthesiser and imports it as an Audio resource (as a dropped WAV), the same sound the Audio editor's Make a sound gives: presets (Coin, Jump, Laser, Explosion, Hit, Power Up, Blip, Footstep, Footstep Grass, Footstep Path, Heartbeat, Wind, Birdsong, Drone, Choir Pad, Organ Chord; the Music mode's instruments Piano, Pluck, Bass, Lead, Pad, Organ, Drum Kit and drums Kick Drum, Snare Drum, Clap, Hi-Hat, Open Hi-Hat, Crash Cymbal, Tom), layers (sine, square, saw, triangle, noise, organ, whiteNoise, pinkNoise or brownNoise oscillators with a note or Hz, pitch glide, ADSR envelope (`"envelope": "Exponential"` for curves), vibrato, unison detune, low-pass, `highPass` and `bandPass` with `q`, tremolo, `bandLimited` saw and square without aliasing or offset, and `pan` -1 to 1) and melodies as note strings (C4 E4 G4, - rests, C4:2 lengths, C4+E4+G4 chords), with an echo; `loopSeconds` makes it a seamless loop of exactly that length; the resource can loop and take a volume. 16-bit 44.1 kHz, mono unless a layer is panned (then stereo); up to 16,384 layers and notes. |
| `image.create` | Makes an Image resource of a size and number of frames (animation), every frame filled with a colour; Sprite, Background, Tileset or Texture use. Frames are stored as the Image editor stores them. A Tileset takes its tile size, its solid tiles (by number, left to right and top to bottom from 0) and autotiling. |
| `image.draw` | Draws on a frame (or every frame): rect, ellipse, line, polygon, text, fill, pixels, clear; filled or outlined, crisp for pixel art or smoothed; colours as #RGB, #RRGGBB, #RRGGBBAA or names. Backs the frame up first. |
| `image.info` | An Image's size, use, layers and frames, each with the full path of its picture, so whoever drew it can look at it. |
| `model.create` | Makes a Model from primitive parts (Cube, Sphere, Cylinder, Cone, Pyramid, Torus, Quad), each with its centre, turn, size and colour, as the Model editor's kitbash and the templates store them; the engine's model loader bakes it and must read it back. Optional glow and double-sided faces. |
| `model.info` | A Model as the engine loads it: meshes, vertices, triangles, bounds, materials, animations, rigged or not. |
| `text.replacePreview` | What `text.replace` would change: each file, how many times, the lines before and after. |
| `text.replace` | Replaces text in scripts and resource JSON under a folder, backing files up. Needs confirm. |
| `terrain.create` | Makes a Terrain from a landform (Flatlands, RollingHills, Hills, Mountains, ErodedMountains, RidgeValleys, Badlands, Volcanic, Islands, Canyon), as the Terrain editor's landform gallery does: size, height range, seed, erosion, terraces, rivers. |
| `terrain.fromModel` | Makes a Terrain from a Model's ground: the surfaces of the materials named (all, if none), the highest at every sample, each sample painted with the layer of the material it came from (up to 8 layers, several materials each). `position` puts it under the model where the model stands; gaps take the nearest ground. `hideSurfaces` also lists those materials in the Model's `hiddenMaterials`, so the model stops drawing the ground the terrain now stands in for (the geometry stays in the model). For an imported scene or a ground made outside the Terrain editor. The model is read as it is (its cooked copy, or its source afresh); nothing is cooked into the project. |
| `terrain.info` | A terrain's landform, size, x and z extent, height limits, the heights it reaches, its paint layers, and the ground height at any points asked about. |
| `terrain.sculpt` | One stroke of the Terrain editor's own brushes (Raise, Lower, Smooth, Flatten, InFill, Paint) along world points, dabbed every quarter radius as dragging does; moves the ground exactly as the editor's brush. |
| `terrain.nature` | A terrain's grass rule and scatter layers, as the Terrain editor's Decorate step shows them. |
| `terrain.setGrass` | The grass grown around the camera (the Terrain editor's grass): on or off, radius, spacing, species, slope, scale. Only the fields given change. |
| `terrain.addScatterLayer` / `terrain.updateScatterLayer` / `terrain.removeScatterLayer` | Many copies of a Model over a terrain by rules: density, spacing, slope, height, painted layer, clumps, scale, draw distance, shadows, collision. Removing needs confirm. |
| `terrain.addWater` / `terrain.updateWater` / `terrain.removeWater` | A terrain's water, as the Terrain editor's Add water makes it: `Water` (a lake or pond, an ellipse `sizeX` by `sizeZ` at `surfaceHeight`), `River` (along `riverPoints` [x, y, z, width, depth], y its surface there, so it can fall), or `Ocean` (the sea to the horizon). Also its waves, flow, colours, wading (`physicsMode` Shallow) or swimming, and a mesh Shader to draw it with (kept in the terrain's `componentShaders`). `terrain.nature` lists them. Removing needs confirm. |
| `room.create` | Makes a Room, `"dimension"` 2D (default) or 3D. A 3D room opens on a scene preset, a sunny day unless `"scene"` names another of the Room editor's Sky cards (`SunnyDay`, `GoldenHour`, `Overcast`, `Stormy`, `Snowy`, `Night`, `Alien`, `Indoor`), so it has a sky rather than a dark void. Optional size (a 3D room's in metres), window size (a 3D room gets 1280 by 720 unless given, never its size in metres) and `"start": true` to make it the room the game starts in. |
| `room.nodes` | A room's instances with their objects, positions, rotations and scales. |
| `room.paintTiles` | Paints a 2D room's tiles from a Tileset image, as the Room editor's tile brush does: cells (`[x, y]` or `[x, y, tile]`) and filled rectangles in grid units, or takes them away; one tile layer per name, `"solid": true` for floors and walls that `TileMoveX`/`TileMoveY` stop at. An autotiled set picks each tile from its neighbours. |
| `room.addInstance` / `room.moveInstance` / `room.removeInstance` | Places, moves or removes instances of Objects, as the Room editor does. Removing needs confirm. |
| `room.addTerrain` | Puts a Terrain in a 3D room, as dragging it in from the palette does; its grass and scatter layers come with it. |
| `room.makeStart` | Makes a room the one the game starts in. |
| `room.setEnvironment` | Sets environment fields as named in the room file (time of day, weather, sky, fog, shadow distance...); the room must still load, so a value of the wrong kind is refused. `"scene"` applies a Sky card first; the values then fine-tune it. |
| `object.create` | Makes an Object as the Object editor does: an Image (2D) or a Model (3D), solid, visible, persistent, its first PGSL events, and `"recipes"`: the Object editor's Behaviour cards (`FirstPerson`, `ThirdPerson`, `Spin3D`, `Float3D`, `Platformer`, `MoveArrows`...) as ordinary PGSL, with the caller's own event code after theirs so it can change their numbers. A 3D-only recipe or `"threeD": true` makes a 3D Object without a model. |
| `object.addRecipe` | Adds one Behaviour recipe to an Object's events, never replacing its code and not twice. |
| `object.info` | What an Object shows, its components with their settings, its events, the components it could have, and the recipes that suit it. |
| `object.setComponent` / `object.removeComponent` | Adds or changes a component (physics, light, audio, particles, material, shader, animation, camera, navigation...); only the settings given change, and asset paths become resource names. Removing needs confirm. |
| `object.setEvent` / `object.removeEvent` | Writes or removes one of an Object's PGSL events (Create, Step, Draw, DrawGui, KeyPressed, Collision, Alarm0...), kept where the runtime finds them; the script check's findings come back with their lines. Removing needs confirm. |
| `game.benchmark` | Measures the game running for a set time after a warm-up (optionally with scripted input, as game.run takes it and played by the clock, so uncapped too its seconds are seconds of play; it starts with the first frame of play, after the first room's cover, which the warm-up already counts, and the game stands still after it): median, 95th and 99th percentile and worst frame times, allocation a frame, collector pauses, peak memory, draw calls, triangles and the costliest scripts. Budgets given (95th, 99th percentile, KB a frame) fail the run when exceeded. Uncapped by default (GENESIS_UNCAPPED: vsync and frame caps off) so the times show real headroom; `uncapped: false` keeps the game's pacing. Uses the Player's own --benchmark-output. 19 Oct, uncapped, DX11: Golden Stag's village 1.91 ms median (p99 2.47), its apothecary 2.06 ms, Cathedral Test 2.36 ms. |
| `script.write` | Writes a library PGSL Script, making the resource if it is new; the script check's findings come back with their lines. |
| `game.run` | Plays the game unattended: scripted input step by step (keys held or tapped, mouse-look, pointer, clicks, a controller stick, typed text) written as an engine input recording and played with the Player's replay by the clock, so a step lasts its seconds at any frame rate; pictures at set seconds of play (GENESIS_SHOTS), counted like the steps from the first frame with the first room on screen; optionally a profile. Reports the checks the game's scripts made with `TestCheck(name, passed, detail)` (a failed check fails the run), the exit code, the log's problems and tail, the pictures and the profile report. The game closes when the input ends. |

## Benchmark scenes

`powershell -ExecutionPolicy Bypass -File BuildTools\Benchmarks\BenchmarkScenes.ps1 [-Rebuild]` builds
a test project with these commands (in `.build\benchmark-scenes`) and measures five standard scenes
uncapped with `game.benchmark`: 2D sprites (3000 bouncing), 3D objects (4096 crates), terrain with
grass and scattered rocks, a physics pile (500 crates) and a heavy GUI (400 labelled panels). It writes
`TestResults\Benchmarks\<time>.md` and `.json` and fails when a scene is over the budgets in
`BuildTools\Benchmarks\budgets.json`. First run, 19 Oct (DX11), median ms: sprites 2.85, objects
3.78, terrain 0.36, physics 0.56, GUI 0.52. Close Studio and games first: each run opens a game
window.

The seventh scene, **Complex Scene**, is synthetic: 64 chunk meshes built in worker jobs
(`JobScriptMesh`) and placed once on the static layer (`MeshStaticAdd`), 1000 instances moved by four
jobs a frame through queued scene changes (`SceneQueuePosition`, made at `JobTake`), 200
`DrawMesh3DTransform` calls a frame, a hot numbers-only script function and a GUI. `-Scenes` picks
scenes; `-Track` adds each scene's numbers to `TestResults\Benchmarks\history.jsonl` and compares
them with its last ten runs (a p95 more than 25% over the best is called out). Every publish that
changes the engine (`Build.bat --quick` or `--tiers`; not focused test runs) runs the complex scene
with `-Track` after promotion, so each engine change has its number; `--no-track` or
`GENESIS_SKIP_BENCH_TRACK=1` skips it. `game.benchmark` results also carry `frameParts`: each part of
the frame (subsystem updates and draws, physics, scripts' events) on average and at its longest.
First run, 23 Oct (DX11): median 1.08 ms, p95 1.2 ms, 73 KB a frame.
## The Nature Walk benchmark

`powershell -ExecutionPolicy Bypass -File BuildTools\Benchmarks\NatureWalk\NatureWalk.ps1 [-Fresh] [-Play] [-Benchmark]`
builds a whole small game with these commands in one `batch` (a walk round a lake: sculpted and
painted terrain, scattered trees, rocks and flowers, grass, water, a sky, synthesised wind, birds and
footsteps, dust motes and a first-person walker), then plays it for pictures and measures it. It is the
Genesis half of a comparison with a quick three.js game (`reference-threejs.html` beside it); what
it measured is in `Design/FinishedProduct.md`. The game checks itself with `TestCheck` a second in.
The [Cookbook](Cookbook.md) explains each step.

## What comes next

These are the first stage and the second (terrain shape, sculpting and nature, room instances and
environment, Objects and scripts). Next, each editor's operations become commands that its buttons and the
automation share: the Model editor's mesh tools and rigging, Shader and Physics presets, the Image editor's layers and tools beyond drawing, and a live mode that works on the Studio that is open, with undo.
