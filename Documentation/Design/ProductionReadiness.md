# Genesis against Unity, Godot, GameMaker and Unreal, and the road to a production release

24 October 2026. What Genesis has and lacks next to the four engines people compare it with, what
was improved today, and the improvements recommended so a complex 2D or 3D game can be made
without fighting the tools. Each recommendation follows the house rules: automatic by default,
one setting or starter, one route per function, and everything reachable as `Engine.Area.Part.Value`
from PGSL. No plugins or asset store, and no system made for one kind of game: each improvement is
to a system or editor that exists, which PGSL then reaches.

## Where Genesis stands

| Area | Unity | Godot | GameMaker | Unreal | Genesis |
|---|---|---|---|---|---|
| 2D: sprites, tiles, autotile, 2D lights | Yes | Yes | Yes (best 2D workflow) | Weak | Yes: Image editor with animation and 2D rig, tile layers, autotile (today), 2D lights and shadows |
| 3D: PBR, shadows, terrain, foliage, water | Yes | Yes | No | Best | Yes: forward PBR, cascades, lamp shadows, terrain with grass and scatter, water, sky with clouds, decals |
| Reflections and bounced light | Probes (placed by hand), SSR, baked GI | SDFGI, VoxelGI, probes | No | Lumen | Automatic light probe (today): reflections and bounced light with nothing to place |
| Depth of field | Post stack | Yes | No | Yes | Yes (today): `Engine.Camera.Focus.*` |
| Render scale / dynamic resolution, upscaling | Yes (FSR, DLSS) | Yes (FSR) | No | Yes (TSR) | Yes (25 October): `Engine.Display.Resolution.*`, with dynamic resolution and a sharpened stretch |
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

Genesis now covers what a single developer needs for complex 2D and 3D games, from scratch or from
the templates it ships. The gaps that remain are platforms, render scaling, a cutscene timeline
(declined) and node-based shaders.

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
6. **Release work.** Run This Room (Shift+F5; on 25 October, at Cal's word, made Run This Resource:
   it runs the selected or open resource as its own sandbox does); Export checks the project first; crash reports with
   the game's version, engine build, renderer, room and last log lines, one file per crash; the
   game's version in Preferences and `Engine.Game.Version`; the Inspector's settings list named
   without "Engine." so typing finds an area; light probes only in games (editor views showed
   stale ones); and every full-regression failure fixed, most of them tests left behind by the
   16 October redesigns, the 22 October Animation Controllers and the 15 October Mushroom Meadow
   content.

## How people use the editors, and what was found

A game in Genesis is made from scratch or from a template: draw or import images and models; make
Objects (look, behaviour, physics); build rooms (place, light, play); script in PGSL; press F5 to
play, F6 to look inside; export. What was looked at on 24 October, with Cal's answers:

- **Starting.** Not a limitation. A project can start from scratch or from a template, and the
  templates Genesis ships, Mushroom Meadow (2D) and Golden Stag (3D), come with a main menu,
  options, save slots, a pause menu and loading screens, so people play them and learn from them.
- **Engine settings in the Inspector.** About 300 values in one "Add a setting" list, sorted by
  area and part. Long, and kept that way on purpose: it shows everything a game can do. Not the
  easiest list for a novice, but the right one for someone making a good game.
- **Seeing what a value does.** On tomorrow's list (below). Done in part on 24 October: the "Add a
  setting" list shows each value's default (`Rendering.Fog.Start (10)`) and each setting's
  description ends with its default.
- **Playtesting.** Nothing missing: the Room editor has its own ▶ Play and Blender-like view and
  HUD camera options. (The Run This Room command added to the File menu on 24 October, Shift+F5,
  does what the Room editor's ▶ Play already does: one route per function says one of them goes.)
- **Performance on small machines.** Done 25 October: render scale and dynamic resolution (`Engine.Display.Resolution.*`).
- **Releasing.** Genesis has no users yet and no service to receive reports or send updates, so
  crash reporting and an update path are not needed now. The Player still writes a crash report
  file of its own (game, version, engine build, renderer, room, last log lines).

## Tomorrow (25 October), agreed with Cal

1. **What a value does, in the Inspector.** Done 25 October. Each engine setting shows its default and its range
   beside it, as well as its description, so the Inspector says what a value does without
   EngineApi.md.
2. **Render scale and dynamic resolution.** Done 25 October. `Engine.Display.Resolution.Scale` (1 unless set) and
   `Engine.Display.Resolution.Dynamic.Enabled` with `TargetFps`: the 3D scene drawn with fewer
   pixels and stretched up, the GUI and text kept sharp; dynamic resolution lowers the scale when
   frames run slow and raises it again when there is room. Large: about ten passes read
   scene-sized images (composite, outlines, AA, held items, TAA history), so each must take the
   scene's size.
3. **PGSL to IL.** Looked at 25 October: hot functions already compile to IL (the compiled tier, through .NET expression trees), with the interpreter's exact results. Compiling event bodies too was built and measured slower (29 against 18 ms for a loop-heavy Step: an event's variables are the instance's, kept by name), so it was taken out; heavy work goes in functions. What remains is giving events' own locals slots, which means settling what a top-level `var` is (today it stays on the instance). Hot functions compiled to real code; the biggest remaining speed gap against
   GameMaker's YYC and Unity's IL2CPP, and what many NPCs or units thinking at once need.
4. **Terrain streamed by tile.** Done 25 October for tiles: terrains laid edge to edge meet as each
   joins the room (shared edges set to the average height, meshes and collision remade), so a big world
   built from tiles streams by distance with no seams. And one large terrain is split into tiles from
   the Room editor (Split terrain into tiles on a Terrain in the outliner): tiles share their edges,
   the nature goes with them, and the room streams them (LargeWorlds.md, In the editors).
5. **Remembered rooms in save files.** Done 25 October: `Engine.Room.Streaming.Save()` and
   `Load(text)`, and Objects a script made at run time come back with a persistent room.
6. **Engine values and an Object's own variables previewed in the editors** (Cal, 24 October). Done 25 October: the Room and Terrain views show their engine settings as the game will (the game's own look code, with and without them, laid over the view), the Object view and the Model viewer show an Object's and a model's settings on a stand-in instance given them by the game's code, and an Object's variables show in its live sandbox, which runs its events (Images carry no settings of their own). A
   value set in the Inspector, an `Engine.Area.Part.Value` or a variable an Object declares, shows
   at once in the Room, Object, Model, Image and Terrain views, drawn by the same code the game
   runs, without pressing F5. One route: the editors read the same values the game reads, so there
   is nothing per editor. Design note: settings are applied through the running game's context
   (`EngineSettings.Apply` via `PgslCommands.ActiveGameContext`), and some are process-wide (light
   probes, depth of field, threads), so an editor view must apply them to its own preview scene
   under a context of its own and put the process-wide ones back afterwards, or one view's preview
   leaks into every other (as the light probes did in editor views on 24 October).
7. **More parts.** The values still marked + in `EngineApiAreas.md`, in the order there: Water,
   Terrain, Particles, Navigation, Instance.Update, Ui, Tile layers. Done 25 October: Engine.Water.Surface,
   Engine.Terrain.Edit and Detail.Quality, Engine.Particle.Emitter and Quality, Engine.Navigation.Mesh,
   Baked and Avoidance Radius and Priority, Engine.Instance.Update.Distance, Engine.Ui Enabled, Scale and
   focus navigation, Engine.Tile.Layer (GameFeatures.md).
8. **Export loads every room once** as part of its check, as it already compiles every script. Done 25 October (`RoomCheck`: each room loads, and every Object and Terrain it places exists).

**Ragdolls, and anything like them** (Cal, 24 October): no system of their own. A ragdoll is
Objects with physics, and Bepu, which Genesis uses, handles them in 2D and 3D; PGSL only gives it
what it needs. The joints are there (`PhysicsCreateJoint`, with `PhysicsJointHinge`,
`PhysicsJointLimitSwing` and `PhysicsJointLimitTwist` since 24 October), a bone's place is read with
`ModelSocketX/Y/Z(bone)` and `AnimationBoneLookAt` aims a bone at the next limb. An explosion
throwing the limbs is plain PGSL, with no dedicated function:

```gml
// The explosion's Create event: each limb thrown away from it, harder the nearer it is.
var ex = x, ey = y, ez = z, power = 60;
with (Limb) {
    var dx = x - ex, dy = y - ey, dz = z - ez;
    var distance = Max(0.5, Sqrt(dx * dx + dy * dy + dz * dz));
    var push = power / (distance * distance);
    PhysicsApplyImpulse(dx / distance * push, (dy / distance + 0.3) * push, dz / distance * push);
}
```

The same holds for everything on this page: an improvement to a system or editor that exists,
reached from PGSL, never a system for one kind of game.

## Later

- Distant stand-ins (impostors) and level-of-detail fades, automatic like detail is today.
- Node view in the Shader editor (if wanted).
- Linux (the Player already runs on Vulkan and OpenGL).
- Crash reports sent to a service and an update path for shipped games, once Genesis has users
  and somewhere to receive them.

## Not needed (Cal, 24 October)

- Game starter screens and genre starters: the shipped templates carry menus, options, save
  slots, pause and loading screens, and a project can start from scratch.
- A two-level settings picker: the full list is the point.
- Play from here: the Room editor has ▶ Play and its own view and HUD camera options.
- 3D split screen: 3D rooms already draw several views.

## What games Genesis can make today, and what blocks the rest

The rule for every blocker below: no system for one kind of game. Each is an improvement to a
system or editor that already exists, reachable from PGSL as `Engine.Area.Part.Value`.

| Game | Today | What it already has | What blocks it, as an improvement to an existing system |
|---|---|---|---|
| 2D platformer, metroidvania | **Ready** | Tiles with autotile and solid tiles, 2D lights and darkness, Image animation and 2D rig, particles, saves (Mushroom Meadow is one) | None (Mushroom Meadow ships as a template) |
| Top-down 2D RPG or action game | **Ready** | Rooms, tiles, room changes without a freeze, UI toolkit lists, languages, saves (Nuvema Town plays end to end) | None |
| Voxel or block-building game | **Ready** | Meshes a script builds, chunk meshing on jobs (6,700 to 13,700 chunks a second), compact grids, mesh streaming that holds memory level | None found |
| Third-person action-adventure | **Ready, with gaps** | Orbit, shoulder and collision camera, root motion, look-at and reach IK, Animation Controllers, navigation agents, decals, flash lights | None: ragdolls are Objects with physics and joint limits (see Tomorrow); the Room editor's ▶ Play runs a room |
| First-person shooter, solo campaign (Halo 1) | **Possible now, with gaps** | First-person arms and held items, character bodies, ray and shape casts, bullet-hole decals, muzzle flashes, navigation with jumps, gaps and ladders, 3D sound and acoustics, outdoor terrain, sky and weather, rebindable actions, several pads | **Ragdolls** are Objects with physics, no system of their own: one body per limb joined by `PhysicsCreateJoint`, each limb's turn copied onto the character's bones with `AnimationBoneSetRotation`. The one general piece missing, joint limits and hinges, was added on the evening of 24 October (`PhysicsJointHinge`, `PhysicsJointLimitSwing`, `PhysicsJointLimitTwist`); a bone's place in the world was already readable (`ModelSocketX/Y/Z(bone)`), and `AnimationBoneLookAt` aims a bone at the next limb's body. An explosion throwing the limbs is plain PGSL (see Tomorrow). **Split screen works already**, in 3D as in 2D: each enabled viewport gets a full pass and follows its own Object, with a pad per player (two physical keyboards cannot be told apart on Windows; keyboard players share one). **Many enemies thinking at once:** sight is ray casts and moving is navigation, all PGSL, so PGSL to IL (tomorrow). Vehicles are Objects with a body and a ray cast per wheel in PGSL; an example would show one. Cutscenes are camera moves and animation events from PGSL (the timeline was declined) |
| Open-world RPG (Skyrim-like) | **The world yes, the game in part** | Worlds kilometres across, terrain drawn by distance, forests of hundreds of thousands of trees with collision near what moves, scenery streaming, automatic levels of detail, texture streaming, several terrains, time of day, seasons and weather, routes that prefer roads, model layers for armour, UI lists for inventory and dialogue, saves, languages | ~~People and things that stream and remember~~ (done 24 October: streamed Objects come back where they were with their variables, destroyed ones stay gone, persistent rooms come back as left, persistent Objects are no longer made twice; still to do: keeping that in save files, and Objects made at run time). **Terrain streaming by tile:** done 25 October for worlds built from tiles (neighbours now meet); one huge terrain file is still read whole. **Far shadows:** done for terrain (24 October: a mountain behind the camera shades the view); distant scatter and Objects behind the camera still cast none. **Far towns:** distant stand-ins (later). **Hundreds of NPC schedules:** PGSL to IL. Quests and dialogue are PGSL with the UI toolkit; Golden Stag, shipped as a template, shows the pattern rather than a quest system |
| Online co-op, small sessions | **Ready, small scale** | LAN discovery, relay, replication with interest distance, each player's character shown to the others | Measured only at small player counts |
| Strategy with many units | **Possible, speed untested** | Navigation agents with avoidance, GPU culling of instanced batches, script jobs | PGSL to IL for hundreds of units' logic; box selection and orders are PGSL and GUI drawing |
| Arcade racing | **Possible** | Bodies, ray casts, motion blur, depth of field | An example showing it; a simulation tyre model is out of scope (one kind of game) |
| Any game on a handheld PC | **Ready** (25 October) | Quality tiers, frame cap, VSync, render scale and dynamic resolution | None |
| Any game on Linux, Mac or consoles | **Blocked** | Player renders on Vulkan and OpenGL | Windows only (later) |

**In short:** 2D games of any common kind, voxel games and third-person or first-person games of
Halo 1's size can be made today; the work is content and PGSL. A Skyrim-like game's world can be
made today, and since the evening of 24 October its people and things stream and remember within
a game session, and since 25 October it saves to a save file. The blockers shared by the most
games on 24 October (render scale, PGSL to IL, terrain streamed by tile) were dealt with on 25
October: render scale and dynamic resolution are built, a big terrain is split into streamed tiles,
and heavy PGSL belongs in functions, which compile to IL. Joint limits, which make ragdolls plain
PGSL, are checked by `--test engine-systems`. (Split screen and starter screens were listed here by mistake: 3D rooms
already draw several views, and the shipped templates carry the screens every game needs.)

**Fixed on 24 October evening** (each with a regression case in the Full Build): streamed Objects
remember their state and destroyed ones stay gone (`Engine.Room.Streaming.*`); the Room's
Persistent setting, which the runtime ignored, now brings a room back as it was left; persistent
Objects are no longer duplicated on returning to their room; terrain behind the camera casts its
shadow into the view; weather values settable and held from PGSL (`Engine.Weather.*`); Export
compiles every script first; the Inspector's settings list shows each default.

## Release run

25 October 2026, 15:41 to 16:50, commit b863f4e (Studio revision 23), after the day's list:

- `Build.bat --full --installer`: **passed** in 68 minutes. Full regression **1422 of 1422** checks
  (859 images), none left unrun; renderer smokes passed on DX11, DX12, Vulkan, OpenGL and software;
  installer written to `Dist\GenesisStudio-Setup.exe`. Unsigned (by choice).
- `BuildTools\InstallDrill.ps1`: **passed**, 6 steps: a silent install for the current user with no
  administrator (2,702 files, 579 MB, version 0.1.0), Studio started from a folder it may only read,
  an upgrade over the top, and an uninstall.
- The day's focused runs: one pgsl-logic run failed `Jobs.AReservedListKeepsItsRoomInAJob` on
  allocation noise (352.7 KB measured) and passed on the rerun and in the Full Build.

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
