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
```

- `list` prints every command with its description and input schema.
- `call` runs one command and prints its result; the exit code is 0 when it worked, 1 when it did
  not, 2 for a mistake in the command line itself. The input can be inline JSON or `@file.json`; from Windows PowerShell use `@file.json`, as it strips the quotes from inline JSON passed to a program.
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
| `particle.create` | Makes a Particle effect from one of the engine's presets (Fire, Smoke, Embers, Sparks, Explosion, Magic, Fountain, Snow, Rain, Portal...), as the Particle editor's gallery does, with any settings changed. |
| `audio.synth` | Makes a sound with Genesis's synthesiser and imports it as an Audio resource (as a dropped WAV): presets (Coin, Jump, Laser, Explosion, Hit, Power Up, Blip, Footstep, Heartbeat, Wind, Drone, Choir Pad, Organ Chord), layers (sine, square, saw, triangle, noise or organ oscillators with a note or Hz, pitch glide, ADSR envelope, vibrato, unison detune, low-pass, tremolo) and melodies as note strings (C4 E4 G4, - rests, C4:2 lengths, C4+E4+G4 chords), with an echo; the resource can loop and take a volume. 16-bit 44.1 kHz mono. |
| `image.create` | Makes an Image resource of a size and number of frames (animation), every frame filled with a colour; Sprite, Background, Tileset or Texture use. Frames are stored as the Image editor stores them. |
| `image.draw` | Draws on a frame (or every frame): rect, ellipse, line, polygon, text, fill, pixels, clear; filled or outlined, crisp for pixel art or smoothed; colours as #RGB, #RRGGBB, #RRGGBBAA or names. Backs the frame up first. |
| `image.info` | An Image's size, use, layers and frames, each with the full path of its picture, so whoever drew it can look at it. |
| `text.replacePreview` | What `text.replace` would change: each file, how many times, the lines before and after. |
| `text.replace` | Replaces text in scripts and resource JSON under a folder, backing files up. Needs confirm. |
| `terrain.create` | Makes a Terrain from a landform (Flatlands, RollingHills, Hills, Mountains, ErodedMountains, RidgeValleys, Badlands, Volcanic, Islands, Canyon), as the Terrain editor's landform gallery does: size, height range, seed, erosion, terraces, rivers. |
| `terrain.info` | A terrain's landform, size, x and z extent, height limits, the heights it reaches, its paint layers, and the ground height at any points asked about. |
| `terrain.sculpt` | One stroke of the Terrain editor's own brushes (Raise, Lower, Smooth, Flatten, InFill, Paint) along world points, dabbed every quarter radius as dragging does; moves the ground exactly as the editor's brush. |
| `terrain.nature` | A terrain's grass rule and scatter layers, as the Terrain editor's Decorate step shows them. |
| `terrain.setGrass` | The grass grown around the camera (the Terrain editor's grass): on or off, radius, spacing, species, slope, scale. Only the fields given change. |
| `terrain.addScatterLayer` / `terrain.updateScatterLayer` / `terrain.removeScatterLayer` | Many copies of a Model over a terrain by rules: density, spacing, slope, height, painted layer, clumps, scale, draw distance, shadows, collision. Removing needs confirm. |
| `room.nodes` | A room's instances with their objects, positions, rotations and scales. |
| `room.addInstance` / `room.moveInstance` / `room.removeInstance` | Places, moves or removes instances of Objects, as the Room editor does. Removing needs confirm. |
| `room.setEnvironment` | Sets environment fields as named in the room file (time of day, weather, sky, fog, shadow distance...); the room must still load, so a value of the wrong kind is refused. |
| `object.create` | Makes an Object as the Object editor does: an Image (2D) or a Model (3D), solid, visible, persistent, and its first PGSL events. |
| `object.info` | What an Object shows, its components with their settings, its events, and the components it could have. |
| `object.setComponent` / `object.removeComponent` | Adds or changes a component (physics, light, audio, particles, material, shader, animation, camera, navigation...); only the settings given change, and asset paths become resource names. Removing needs confirm. |
| `object.setEvent` / `object.removeEvent` | Writes or removes one of an Object's PGSL events (Create, Step, Draw, DrawGui, KeyPressed, Collision, Alarm0...), kept where the runtime finds them; the script check's findings come back with their lines. Removing needs confirm. |
| `game.benchmark` | Measures the game running for a set time after a warm-up (optionally with scripted input, as game.run takes it): median, 95th and 99th percentile and worst frame times, allocation a frame, collector pauses, peak memory, draw calls, triangles and the costliest scripts. Budgets given (95th, 99th percentile, KB a frame) fail the run when exceeded. Uses the Player's own --benchmark-output. |
| `script.write` | Writes a library PGSL Script, making the resource if it is new; the script check's findings come back with their lines. |
| `game.run` | Plays the game unattended: scripted input step by step (keys held or tapped, mouse-look, pointer, clicks, a controller stick, typed text) written as an engine input recording and played with the Player's replay; pictures at set seconds of play (GENESIS_SHOTS); optionally a profile. Reports the checks the game's scripts made with `TestCheck(name, passed, detail)` (a failed check fails the run), the exit code, the log's problems and tail, the pictures and the profile report. The game closes when the input ends. |

## What comes next

These are the first stage and the second (terrain shape, sculpting and nature, room instances and
environment, Objects and scripts). Next, each editor's operations become commands that its buttons and the
automation share: Model, Shader and Physics presets, the Image editor's layers and tools beyond drawing, and a live mode that works on the Studio that is open, with undo.
