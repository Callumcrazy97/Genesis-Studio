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
3. **Release checklist in Export.** Done in part today: Export runs the project validator and the
   strict script check F5 makes, lists the problems in the Console and asks before exporting a
   project with errors. Still to do: load every room once as part of it.
4. **Full Build and install drill green on the release build** (run today, see below).

**P1: makes complex games effortless**

5. **Render scale and dynamic resolution.** `Engine.Display.Resolution.Scale` (1 unless set) and
   `Engine.Display.Resolution.Dynamic.Enabled` with `TargetFps`: the 3D scene drawn smaller and
   upscaled in the composite, the GUI kept sharp. Large: about ten passes read scene-sized
   images (composite, outlines, AA, held items, TAA history), so each must take the scene size.
6. **Play from here.** Done in part today: **Run This Room** (Shift+F5, File menu) starts the game
   in the Room open in the editor instead of the start room. Still to do: start with the camera (3D)
   or view (2D) where the editor's is.
7. **Engine settings picker by area.** Done in part today: the Inspector's "Add a setting" list
   shows each value's default beside it (`Rendering.Fog.Start (10)`), and each setting's
   description ends with its default. Still to do: a two-level menu (Rendering, then Fog, then
   Start) and each value's range.
8. **More parts.** The 444 values still marked + in `EngineApiAreas.md`, in the order there:
   Weather, Water, Terrain, Particles, Navigation, Instance.Update, Ui, Tile layers.
9. **Genre starters.** A 2D platformer, a top-down 2D action game, a 3D third-person game and a
   first-person game, each small and complete, built from the game starter (2).
10. **PGSL to IL.** Hot functions compiled to real code (on the to-do list); the biggest remaining
    speed gap against GameMaker's YYC and Unity's IL2CPP.

11. **Engine values and an Object's own variables previewed in the editors** (Cal, 24 October;
    end of day if there is time after 21:00, otherwise next). A value set in the Inspector, an
    `Engine.Area.Part.Value` or a variable an Object declares, shows at once in the Room, Object,
    Model, Image and Terrain views, drawn by the same code the game runs, without pressing F5.
    One route: the editors read the same values the game reads, so there is nothing per editor.

**P2: later**

12. Distant stand-ins (impostors) and level-of-detail fades, automatic like detail is today.
13. Node view in the Shader editor (if wanted).
14. Linux (the Player already runs on Vulkan and OpenGL).
15. Update path for shipped games (a patch file the installer applies).

## What games Genesis can make today, and what blocks the rest

The rule for every blocker below: no system for one kind of game. Each is an improvement to a
system or editor that already exists, reachable from PGSL as `Engine.Area.Part.Value`.

| Game | Today | What it already has | What blocks it, as an improvement to an existing system |
|---|---|---|---|
| 2D platformer, metroidvania | **Ready** | Tiles with autotile and solid tiles, 2D lights and darkness, Image animation and 2D rig, particles, saves (Mushroom Meadow is one) | Only the game starter screens (P0 2) |
| Top-down 2D RPG or action game | **Ready** | Rooms, tiles, room changes without a freeze, UI toolkit lists, languages, saves (Nuvema Town plays end to end) | The game starter screens |
| Voxel or block-building game | **Ready** | Meshes a script builds, chunk meshing on jobs (6,700 to 13,700 chunks a second), compact grids, mesh streaming that holds memory level | None found |
| Third-person action-adventure | **Ready, with gaps** | Orbit, shoulder and collision camera, root motion, look-at and reach IK, Animation Controllers, navigation agents, decals, flash lights | Ragdolls (below); play from the editor's camera (P1 6) |
| First-person shooter, solo campaign (Halo 1) | **Possible now, with gaps** | First-person arms and held items, character bodies, ray and shape casts, bullet-hole decals, muzzle flashes, navigation with jumps, gaps and ladders, 3D sound and acoustics, outdoor terrain, sky and weather, rebindable actions, several pads | **Ragdolls** are Objects with physics, no system of their own: one body per limb joined by `PhysicsCreateJoint`, each limb's turn copied onto the character's bones with `AnimationBoneSetRotation`. The one general piece missing, joint limits and hinges, was added on the evening of 24 October (`PhysicsJointHinge`, `PhysicsJointLimitSwing`, `PhysicsJointLimitTwist`); a bone's place in the world was already readable (`ModelSocketX/Y/Z(bone)`), and `AnimationBoneLookAt` aims a bone at the next limb's body. A ragdoll starter Object is still to do. **Split screen works already**, in 3D as in 2D: each enabled viewport gets a full pass and follows its own Object, with a pad per player (two physical keyboards cannot be told apart on Windows; keyboard players share one). **Many enemies thinking at once:** sight is ray casts and moving is navigation, all PGSL, so PGSL to IL (P1 10). Vehicles are Objects with a body and a ray cast per wheel in PGSL; a genre starter would show one. Cutscenes are camera moves and animation events from PGSL (the timeline was declined) |
| Open-world RPG (Skyrim-like) | **The world yes, the game in part** | Worlds kilometres across, terrain drawn by distance, forests of hundreds of thousands of trees with collision near what moves, scenery streaming, automatic levels of detail, texture streaming, several terrains, time of day, seasons and weather, routes that prefer roads, model layers for armour, UI lists for inventory and dialogue, saves, languages | ~~People and things that stream and remember~~ (done 24 October: streamed Objects come back where they were with their variables, destroyed ones stay gone, persistent rooms come back as left, persistent Objects are no longer made twice; still to do: keeping that in save files, and Objects made at run time). **Terrain streaming by tile:** each terrain file is read whole and neighbours are not stitched. **Far shadows:** done for terrain (24 October: a mountain behind the camera shades the view); distant scatter and Objects behind the camera still cast none. **Far towns:** distant stand-ins (P2 12). **Hundreds of NPC schedules:** PGSL to IL. Quests and dialogue are PGSL with the UI toolkit; a genre starter would show the pattern rather than a quest system |
| Online co-op, small sessions | **Ready, small scale** | LAN discovery, relay, replication with interest distance, each player's character shown to the others | Measured only at small player counts |
| Strategy with many units | **Possible, speed untested** | Navigation agents with avoidance, GPU culling of instanced batches, script jobs | PGSL to IL for hundreds of units' logic; box selection and orders are PGSL and GUI drawing |
| Arcade racing | **Possible** | Bodies, ray casts, motion blur, depth of field | A starter showing it; a simulation tyre model is out of scope (one kind of game) |
| Any game on a handheld PC | **Runs, every pixel** | Quality tiers, frame cap, VSync | Render scale and dynamic resolution (P1 5) |
| Any game on Linux, Mac or consoles | **Blocked** | Player renders on Vulkan and OpenGL | Windows only (P2 14) |

**In short:** 2D games of any common kind, voxel games and third-person or first-person games of
Halo 1's size can be made today; the work is content and PGSL. A Skyrim-like game's world can be
made today, and since the evening of 24 October its people and things stream and remember within
a game session (saving that to a save file is next). The blockers shared by the most games are
now render scale, PGSL to IL, terrain streamed by tile, and the game starter screens, in that
order of work (joint limits, which make ragdolls a PGSL recipe, were added after the release run
and are checked by `--test engine-systems`). (Split screen was
listed here by mistake: 3D rooms already draw several views.)

**Fixed on 24 October evening** (each with a regression case in the Full Build): streamed Objects
remember their state and destroyed ones stay gone (`Engine.Room.Streaming.*`); the Room's
Persistent setting, which the runtime ignored, now brings a room back as it was left; persistent
Objects are no longer duplicated on returning to their room; terrain behind the camera casts its
shadow into the view; weather values settable and held from PGSL (`Engine.Weather.*`); Export
compiles every script first; the Inspector's settings list shows each default.

## Release run

24 October 2026, 19:37, commit 0598c56 (Studio revision 23), on a quiet machine:

- `Build.bat --full --installer`: **passed** in 28.5 minutes. Full regression **1407 of 1407**
  checks (859 images), none left unrun; renderer smokes passed on DX11, DX12, Vulkan, OpenGL and
  software; installer written to `Dist\GenesisStudio-Setup.exe`. Unsigned (by choice; signing is
  ready through `GENESIS_SIGN_COMMAND`).
- `BuildTools\InstallDrill.ps1`: **passed**, 6 steps: a silent install for the current user with
  no administrator (2,686 files, 579 MB, version 0.1.0), Studio started from a folder it may only
  read, an upgrade over the top, and an uninstall.
- The day's earlier runs: release3 had one failure, from a compile of mine during its regression
  (the 2D gate passes alone); the 18:58 tiers run had one, the post-effect worker test whose game
  stopped drawing under another session's GPU load (the stall its notes describe); both passed here.
