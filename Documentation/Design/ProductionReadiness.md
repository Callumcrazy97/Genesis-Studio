# Genesis against Unity, Godot, GameMaker and Unreal, and the road to a production release

24 October 2026. What Genesis has and lacks next to the four engines people compare it with, what
was improved today, and the improvements recommended so a complex 2D or 3D game can be made
without fighting the tools. Each recommendation follows the house rules: automatic by default,
one setting or starter, one route per function, and everything reachable as `Engine.Area.Part.Value`
from PGSL. No plugins or asset store.

## Where Genesis stands

| Area | Unity | Godot | GameMaker | Unreal | Genesis |
|---|---|---|---|---|---|
| 2D: sprites, tiles, autotile, 2D lights | Yes | Yes | Yes (best 2D workflow) | Weak | Yes: Image editor with animation and 2D rig, tile layers, autotile (today), 2D lights and shadows |
| 3D: PBR, shadows, terrain, foliage, water | Yes | Yes | No | Best | Yes: forward PBR, cascades, lamp shadows, terrain with grass and scatter, water, sky with clouds, decals |
| Reflections and bounced light | Probes (placed by hand), SSR, baked GI | SDFGI, VoxelGI, probes | No | Lumen | Automatic light probe (today): reflections and bounced light with nothing to place |
| Depth of field | Post stack | Yes | No | Yes | Yes (today): `Engine.Camera.Focus.*` |
| Render scale / dynamic resolution, upscaling | Yes (FSR, DLSS) | Yes (FSR) | No | Yes (TSR) | **No** (recommended, P1) |
| Post effects | Volume stack | Environment | Shaders | Volumes | Bloom, AO, contact shadows, fog with shafts, eye adaptation, grading, AA (FXAA/SMAA/TAA), motion blur, project post effects |
| Physics | PhysX/Box2D | Godot/Jolt | Box2D | Chaos | Jolt-style bodies, characters, joints, ray/shape casts; one API `Engine.Physics.*` |
| Animation | Mecanim, Timeline | AnimationTree | Sequences | AnimBP, Sequencer | Clips, state machine editor, blend trees, layers, IK (look, reach), root motion, 2D rig |
| AI and navigation | NavMesh | NavServer | mp_grid | NavMesh, BT | NavMesh and grid baking, agents with avoidance, links, area costs |
| Audio | Mixer, effects | Buses, effects | Basic | MetaSounds | Buses, ducking, 3D sound, acoustics (today), music muffle |
| UI | UI Toolkit | Control nodes | Basic | UMG | UI toolkit: lists, focus, typing, themes; GUI drawing |
| Networking | Netcode | High-level MP | Rollback | Replication | LAN discovery, relay, replication with interest distance, messages |
| Scripting | C# | GDScript, C# | GML | Blueprints, C++ | PGSL (GameMaker-like, compiled tier) and C# |
| Composition | Prefabs, nested | Scenes in scenes | Objects, parents | Actors, BPs | Objects with parents, Objects inside Objects (today) |
| Multithreading | Jobs, Burst | Threads | No | Task graph | Engine jobs; culling shared between cores (today); script jobs |
| Profiling and debugging | Profiler, debugger | Profiler, debugger | Debugger | Insights | F6 debug screen with profiler, script breakpoints, engine values live (today) |
| Platforms | All | All | All | All | **Windows only** (Linux declined for now) |
| Build and install | Player builds | Export templates | Builds | Packaging | Exports and a Windows installer with an install drill |

Genesis now covers what a single developer needs for complex 2D and 3D games. The gaps that remain
are platforms, render scaling, a cutscene timeline (declined), node-based shaders, and production
features around the game rather than in it (below).

## Improved today (24 October)

1. **Engine jobs used by the 3D draw list.** Culling is shared between the cores: Objects 3D
   2.99 ms to 2.40 ms, Complex Scene object drawing 1.00 ms to 0.55 ms. `Engine.Game.Threads`.
2. **Automatic reflections and bounced light.** One probe at the camera, captured at room start and
   as the camera travels or the sun moves, on DX11, DX12, Vulkan and OpenGL. `Engine.Lighting.Bounce.*`,
   `Engine.Lighting.Reflections.*`, `Engine.Lighting.Probe.*`.
3. **Every area in parts with Enabled and defaults.** `Engine.Area.Part.Value` across Display
   (window management and monitors), Camera, Rendering, Lighting, Sky, Audio, Physics, Input and
   Game; a part turned on turns its area on; Inspector settings start at each value's default.
4. **Depth of field.** `Engine.Camera.Focus.Enabled/Distance/Range/Blur/Auto`, off unless wanted.
5. **Objects inside Objects, autotiling and acoustics** (earlier today).
6. **Release work.** Run This Room (Shift+F5); Export checks the project first; crash reports with
   the game's version, engine build, renderer, room and last log lines, one file per crash; the
   game's version in Preferences and `Engine.Game.Version`; the Inspector's settings list named
   without "Engine." so typing finds an area; light probes only in games (editor views showed
   stale ones); and every full-regression failure fixed, most of them tests left behind by the
   16 October redesigns, the 22 October Animation Controllers and the 15 October Mushroom Meadow
   content.

## How people use the editors, and what gets in the way

A game in Genesis is made in this order: start a project from a template; draw or import images and
models; make Objects (look, behaviour, physics); build rooms (place, light, play); script in PGSL;
press F5 to play, F6 to look inside; export. The friction points found while working through the
templates and today's features:

- **Starting.** Two templates (Mushroom Meadow 2D, Tumble Isles 3D). Most games need a menu, an
  options screen, save slots, a pause menu and a loading screen; each project writes them again.
- **Engine settings in the Inspector.** About 300 values in one "Add a setting" list. Sorted by
  area and part, but long.
- **Seeing what a value does.** Values have defaults and descriptions in EngineApi.md, but the
  Inspector shows only the description.
- **Playtesting.** F5 starts at the room's start; there is no "play from here" with the camera
  where the editor's camera is.
- **Performance on small machines.** No render scale, so a handheld (Legion Go) runs every pixel.
- **Releasing.** Installer and exports work; there is no crash report a player can send, no
  version shown in the game, and no update path.

## Recommendations

**P0: needed for a production release**

1. **Crash reports.** Done today: the Player's crash report holds the game and its version
   (`Engine.Game.Version`, set in Preferences > Project > Release), the engine build, renderer, room,
   machine and the last 40 lines of the game's log, in a file of its own named by the time of the
   crash (`crash-20261024-153012.txt`, the id a player quotes), and its error dialog says where it is.
2. **A game starter with the screens every game needs.** Main menu, options (display mode,
   resolution, VSync, frame cap, volume per bus, key and pad rebinding through `Engine.Input.Actions`),
   pause menu, save slots and a loading screen, as a template both new projects and the existing
   templates can start from. All of it uses the engine API, so a game edits it like its own code.
3. **Release checklist in Export.** Done in part today: Export runs the project validator first,
   lists the problems in the Console and asks before exporting a project with errors. Still to do:
   load every room once and compile every script as part of it.
4. **Full Build and install drill green on the release build** (run today, see below).

**P1: makes complex games effortless**

5. **Render scale and dynamic resolution.** `Engine.Display.Resolution.Scale` (1 unless set) and
   `Engine.Display.Resolution.Dynamic.Enabled` with `TargetFps`: the 3D scene drawn smaller and
   upscaled in the composite, the GUI kept sharp. Large: about ten passes read scene-sized
   images (composite, outlines, AA, held items, TAA history), so each must take the scene size.
6. **Play from here.** Done in part today: **Run This Room** (Shift+F5, File menu) starts the game
   in the Room open in the editor instead of the start room. Still to do: start with the camera (3D)
   or view (2D) where the editor's is.
7. **Engine settings picker by area.** The Inspector's "Add a setting" as a two-level menu
   (Rendering, then Fog, then Start), showing each value's default and range beside it.
8. **More parts.** The 444 values still marked + in `EngineApiAreas.md`, in the order there:
   Weather, Water, Terrain, Particles, Navigation, Instance.Update, Ui, Tile layers.
9. **Genre starters.** A 2D platformer, a top-down 2D action game, a 3D third-person game and a
   first-person game, each small and complete, built from the game starter (2).
10. **PGSL to IL.** Hot functions compiled to real code (on the to-do list); the biggest remaining
    speed gap against GameMaker's YYC and Unity's IL2CPP.

**P2: later**

11. Distant stand-ins (impostors) and level-of-detail fades, automatic like detail is today.
12. Node view in the Shader editor (if wanted).
13. Linux (the Player already runs on Vulkan and OpenGL).
14. Update path for shipped games (a patch file the installer applies).

## Release run

(Filled in after the Full Build.)
