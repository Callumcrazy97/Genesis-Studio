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
  not, 2 for a mistake in the command line itself. The input can be inline JSON or `@file.json`.
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
| `text.replacePreview` | What `text.replace` would change: each file, how many times, the lines before and after. |
| `text.replace` | Replaces text in scripts and resource JSON under a folder, backing files up. Needs confirm. |
| `terrain.nature` | A terrain's grass rule and scatter layers, as the Terrain editor's Decorate step shows them. |
| `terrain.setGrass` | The grass grown around the camera (the Terrain editor's grass): on or off, radius, spacing, species, slope, scale. Only the fields given change. |
| `terrain.addScatterLayer` / `terrain.updateScatterLayer` / `terrain.removeScatterLayer` | Many copies of a Model over a terrain by rules: density, spacing, slope, height, painted layer, clumps, scale, draw distance, shadows, collision. Removing needs confirm. |
| `room.nodes` | A room's instances with their objects, positions, rotations and scales. |
| `room.addInstance` / `room.moveInstance` / `room.removeInstance` | Places, moves or removes instances of Objects, as the Room editor does. Removing needs confirm. |
| `room.setEnvironment` | Sets environment fields as named in the room file (time of day, weather, sky, fog, shadow distance...); the room must still load, so a value of the wrong kind is refused. |

## What comes next

These are the first stage and the start of the second (terrain nature, room instances and
environment). Next, each editor's operations become commands that its buttons and the automation
share: terrain creation and sculpting, Object and Script, then the run, test and profile loop (an unattended run with scripted inputs, timed
screenshots and a structured report), then Image, Model, Shader, Particle, Physics and audio
synthesis, and a live mode that works on the Studio that is open, with undo.
