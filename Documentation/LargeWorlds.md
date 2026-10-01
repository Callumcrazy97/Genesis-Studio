# Large worlds

Genesis can build, draw and run an open world several kilometres across: one terrain with a sea
around it, rivers, forests of hundreds of thousands of trees, and settlements. This page says what
the engine does for you, which settings to change for a large world, how to make one from a
script, and what is not done yet.

Nothing here changes a small room. Every large-world behaviour is either automatic and invisible
at small scale, or an opt-in setting whose default is the old behaviour.

## What the engine does

| Part | Behaviour |
|---|---|
| Terrain drawing | A terrain of 1024 cells or more per side is drawn as a quadtree. Detail follows distance, so an 8 km terrain costs about 0.8 million triangles from any viewpoint. Tiles are built on worker threads and uploaded a few per frame. |
| Terrain collision | Large terrains keep collision only near the camera and near moving physics bodies, in 64-cell tiles. |
| Terrain files | `.gterrain` loads and saves in one block instead of a sample at a time. The test room below, with a 100 MB terrain, is running under four seconds after the Player starts. |
| Terrain shading | On terrains 1000 m or wider, painted layers blend to a broader repeat with distance and carry a slow variation in tone, so distant ground shows neither tiles nor one flat colour. |
| Models | A static model with no authored levels of detail gets up to three simplified versions, made in the background the first time it is seen small. The version drawn follows the model's size on screen. A model smaller than about 0.25% of the screen's height is not drawn. Animated (skinned) meshes are simplified too, one step later than static ones, and every vertex kept is an original vertex with its own bone weights. |
| Model loading | A model of 256 KB or more is kept a second time in the project's `.genesis/Cache/Models` folder with its vertices, indices and animation frames as raw bytes, which reads back many times faster than the model's text. A room's models are read on worker threads while the room's terrain loads and its objects are created. See [Loading](#loading). |
| Scatter | A terrain's scatter layers place copies of a Model by rule: density, height range, slope, painted layer, clumping. Nothing is stored; each 256 m cell is worked out when it comes into range. Near cells draw every copy as an instance with shadows; further cells draw as one merged mesh each. |
| Scatter collision | A scatter layer with a collision size is solid: copies within 14 m of the camera or of a moving physics body get an upright box collider, and lose it when everything has moved away. A forest of 200,000 trees costs the few dozen colliders around what can touch them. |
| Scenery | With a scenery distance set, placed Objects that only show a model exist only while the camera is within that distance. Anything scripted, moving or remembered loads with the room as before. |
| Several terrains | With a terrain distance set, each Terrain in a room is read when the camera comes within that distance of its edge and released when the camera has left it behind. Later terrains are read on a worker thread. |
| Shared worlds | A host shares Objects with the players joined to it, each player is sent only what is near it, and each player's own character is shown to the others. See [Multiplayer](#multiplayer). |
| Sea | An ocean water body follows the camera to the horizon and has a dark floor under open water. |
| Rivers | Rivers are traced downhill to the sea and cut into the terrain when a world is generated. |
| Sun shadows | With a shadow distance set, a third shadow cascade covers kilometres, so mountains shade valleys. |
| Haze | With a visibility set, haze is defined by how far you can see instead of a fixed short-range density. |
| Scripts | With an activity distance set, scripted objects far from the camera skip their step events. |

## Room settings for a large world

These are in the Room editor under **Lighting & atmosphere**, and in the room file's `environment`.

| Setting | Key | Default | Use |
|---|---|---|---|
| Visibility (km) | `visibilityKilometres` | 0 | Distance at which haze has removed about 95% of contrast. 0 keeps the short-range haze. 20 to 50 suits an island. |
| Weather fog | `weatherFogScale` | 1 | Multiplies the fog that rain, overcast, storm and fog weather bring. At 1, rain leaves about 10% contrast at 50 m, which suits a street and hides a valley. Lower it to keep the rain, the wet ground and the cloud with a longer view; 0 leaves only the clear-day haze. Scripts can change `Atmosphere.Options.WeatherFogScale` while the game runs. |
| Sun shadow distance (m) | `shadowDistance` | 0 | How far from the camera the sun casts shadows. 0 keeps close-range shadows only. 2000 to 4000 shades mountainsides. |
| Object activity distance (m) | `simulationDistance` | 0 | Scripted objects with a 3D model further than this skip Step events. 0 runs everything. |
| Ambient intensity | `ambientIntensity` | 1 | Now also scales the dynamic sky's ambient light. Raise it when shaded slopes are too dark. |
| Scenery loading distance (m) | `sceneryDistance` | 0 | Objects that only show a model are created within this distance of the camera and destroyed beyond 1.2 times it. 0 creates everything with the room. |
| Terrain loading distance (m) | `terrainDistance` | 0 | A Terrain is loaded when the camera is within this distance of its edge. 0 loads every terrain with the room. |

Set the camera's far plane to cover the view you want (`Engine.SetCameraFarPlane(16000)`). The
depth buffer has 24 bits, and a near plane of 0.1 with a 16 km far plane makes distant surfaces
flicker, so a game camera whose far plane is 4000 or more uses a near plane of at least
far / 30000 (0.53 m at 16 km). A larger near plane you set yourself is kept. Views shorter than
4000 are unchanged.

An Object is scenery when its components are only a transform, a model and its material or shader:
no script, no events, no physics preset, not persistent. While it is loaded a streamed Object is
as solid as any placed model (it gets the same collider fitted to its model), and the collider goes
when the Object does. Something that moves on its own far from the camera can therefore pass
through a building that is not loaded; give the building a physics preset to load it with the room.

An object that must keep running however far away it is sets `"AlwaysActive": true` on its
`ScriptComponent`. Objects with nothing to draw (managers, cameras, HUDs) always run.

## Making a world from a script

**Terrain › New › Create From Code**, with **Replace editable base terrain** on, runs the script
for every sample at the spacing you ask for (up to 8192 cells per side) and then applies the
commands below to the whole terrain. The preview stays limited to 384 cells per side.

Commands are written once, above the per-sample code:

```pgsl
TerrainSize(8192, 8192);
TerrainSpacing(2);
TerrainHeights(-120, 1400);

Ocean(0);                        // a sea at height 0, to the horizon
Erode(0.45);                     // weather the land with running water
Rivers(10, 0.9);                 // up to 10 rivers, each draining at least 0.9 square km
PaintNatural(4, 600, 41);        // sand below 4 m, snow above 600 m, rock steeper than 41 degrees

Layer(1, "Grass", "Grass Ground", 7);    // slot, name, Image, metres per repeat
Scatter("Pine", 85, 25, 520, 32, 1, 5, 380);

Sites(7, 8, 150, 72, 850);       // 7 level sites between 8 and 150 m, radius 72 m, 850 m apart
SitePaint(3);                    // paint layer 3 under each
SitePlace("Cottage", 8, 30, 68, 12);

// per-sample code
coast = length(x, z) / 4096 + 0.16 * fbm(x * 0.0007, z * 0.0007, 5);
land = smoothstep(0.86, 0.52, coast);
height = -90 + land * (100 + 900 * pow(ridge(x * 0.0003, z * 0.0003, 5), 1.9));
```

| Command | Meaning |
|---|---|
| `TerrainHeights(min, max)` | Height range the terrain stores. |
| `Ocean(level)` | Adds a sea at this height. Rivers drain to it. |
| `Erode(strength)` | Hydraulic erosion, 0 to about 1. |
| `Rivers(count, basinSquareKilometres)` | Traces the largest rivers, cuts channels, adds the water and paints the banks with layer 3. |
| `PaintNatural(beach, snow, rockSlope)` | Paints layers 1 to 4 as grass, rock, sand and snow. |
| `Layer(slot, name, image, tileMetres)` | Names a painted layer and gives it an Image. |
| `Scatter(model, perHectare, minHeight, maxHeight, maxSlope, layer, spacing, clumpMetres, drawDistance)` | Adds a scatter layer. `layer` 1 to 4 limits it to that paint; 0 allows any. `drawDistance` hides small things beyond that many metres; 0 draws them to the horizon. |
| `ScatterCollision(radius, height)` | Makes the copies of the last `Scatter` solid: an upright box `radius` wide from its centre and `height` tall, at scale 1. Size it to the trunk, not the canopy. |
| `Sites(count, minHeight, maxHeight, radius, separation)` | Finds level ground, near rivers when there are any, and flattens it. |
| `SitePlace(object, count, innerRadius, outerRadius, spacing)` | Arranges Objects around each site of the last `Sites`, facing the centre. |
| `SitePaint(layer)` | Paints a layer under each site of the last `Sites`. |

New functions for the per-sample code: `fbm(x, z, octaves)`, `ridge(x, z, octaves)`,
`lerp(a, b, t)`, `smoothstep(edge0, edge1, value)`, `length(x, z)`.

Generating an 8192 m terrain at 2 m spacing (16.8 million samples) with erosion, rivers, paint,
seven scatter layers and twelve sites takes about 17 seconds.

### Scatter layers

A scatter layer names a Model, not an Object: copies are drawn and can be solid, and they do not
run scripts. Use Objects placed with `SitePlace`, or in the Room editor, for anything a player
interacts with.

In the Terrain editor, **Options > Forests and scatter...** lists a terrain's layers and edits each
one: the model, how many per hectare, spacing and clumping, the heights, slope and painted layers
it grows on, its size range, how far it sinks into the ground, whether copies tilt to the ground
and cast shadows, its draw distance and its solid size. The change
is one undo step. The Terrain editor draws the layers with the game's own scatter renderer, so the
forest in the editor is the forest in the game.

Distant copies are merged meshes that keep each material's colour and texture and cast no shadows. Give small things (bushes, pebbles) a draw distance so they are not built for
the whole world.

At most 22,000 scattered copies are drawn as instances in one frame, nearest first; the renderer's
instance buffer holds 32,768 for everything. The Player's performance snapshot reports copies that
did not fit (`scatter= ... dropped`) and instances the renderer dropped (`instancesDropped`).

## Loading

| Part | Behaviour |
|---|---|
| Starting a game | The loading screen prepares what is in the project's `Assets` folder, as many files as fit in 6 ms of each frame. It no longer walks the whole project folder or counts source models (`.glb`, `.fbx`) the game never reads. |
| Model cache | The first load of a model of 256 KB or more writes `.genesis/Cache/Models/<name>-<hash>.gmc`. Later loads read that file. A project that is moved or renamed keeps its caches. The folder is not part of an exported game, which makes its own on first run. The cache records the size and modified time of the model it was made from and is ignored when either differs, so an edited or reimported model is never shown stale. It also records a hash of its own content, and a cache that does not match its hash is ignored. Deleting the folder costs one slow load. `GENESIS_MODEL_CACHE=0` turns it off. |
| Reading ahead | When a room loads, the models its Objects and scatter layers name are read on worker threads (half the processor's cores, at most 8) while the main thread loads the terrain and creates the objects. A load that asks for a model takes the worker's result, waiting if it is not finished. |
| The next room | `RoomPreload("Cellar")` starts reading another room's models in the background and returns how many it names. Call it when the player nears a door; a `RoomGoto` within ten minutes finds the models read. `RoomPreloadPending()` is how many are still being read. `RoomGotoWhenLoaded("Cellar")` does both: it reads the room's models in the background while the current room keeps running, and changes room when they are ready (or after 30 seconds); `RoomLoadProgress()` goes from 0 to 1 meanwhile, for a progress bar. |

The Player's performance snapshot reports `modelCache=read N written N` and
`modelReadAhead=started N used N`.

## Multiplayer

`NetHost(port)` and `NetConnect(ip, port)` join machines, as before. On top of them:

| Command | Meaning |
|---|---|
| `NetReplicate(id)` | Host only. Share this object: every player near it sees a copy that follows it. Returns a network id. |
| `NetOwn(id)` | Any machine. This object is controlled here (the player's character): it moves with no delay on this machine, and the host and the other players see a copy. On the host it is the same as `NetReplicate`. |
| `NetForget(id)` | Stop sharing an object. Its copies elsewhere are removed. |
| `NetInterestDistance(metres)` | How far from a player's camera an object is sent to that player. 250 by default. |
| `NetCopyCount()` | Copies of other machines' objects that exist on this machine. |

A player reports where its camera is five times a second. The host sends an object's arrival
reliably when it comes within the interest distance of a player, its movement twenty times a
second while it moves, and its departure when it is 15% beyond the distance. A world with thousands
of shared objects costs each player the ones around them. Distance is measured from the 3D camera;
in a 2D room every shared object is sent to every player. An object that stops is sent three more
times, because movement messages may be lost. A copy that is removed on one machine while its
object lives on is asked for again.

Copies are made from the Object's own definition and run none of its scripts. Position and
rotation are shared, scale as it was when the copy was made, and the animation a model is playing
(its clip, whether it loops, its speed, and whether it is paused) whenever that changes, so a copy
walks when its object walks. Script variables and physics are not shared: a copy is not solid and
is not given the physics body or the collider its Object would have, because the machine that owns
the object decides where it is. There is no prediction or
rollback and nothing prevents cheating: a player's own objects are trusted. All players are
assumed to be in the same room; when a machine changes room, what it shared is withdrawn and its
scripts share the new room's objects again.

## Building a project without the editor

The headless harness can do what the editor does, for scripts and tests:

```powershell
$tool = "Tests\Genesis.Application.Headless\bin\Release\net10.0-windows\Genesis.Application.Headless.exe"
& $tool --project-tool new "C:\Projects" "MyWorld"
& $tool --project-tool import "C:\Projects\MyWorld" Pine.glb Cottage.glb Grass.png
& $tool --project-tool terrain-from-code "C:\Projects\MyWorld" Island World.terrain.pgsl 20261001
& $tool --project-tool terrain-room "C:\Projects\MyWorld" Island World
& $tool --project-tool start-room "C:\Projects\MyWorld" World
& $tool --project-tool capture "C:\Projects\MyWorld" Room World world-editor.png
```

`terrain-from-code` uses the Terrain editor's own Create From Code route in a hidden window.
`capture` opens a Room or Terrain in its editor and saves what the editor's 3D view shows.

## In the editors

A Room whose standard opening view would be under a terrain's surface opens framed on its contents
instead. Editor 3D views that reach more than 3 km move their near plane out with the far plane
(far / 30000), which keeps distant water, shore and ground from flickering through each other.
The test world's room opens in the Room editor in about 5 seconds and its terrain in the Terrain
editor in about 5 seconds.

## Measuring

Run the Player with `--benchmark-output <folder> --benchmark-seconds N`. `frames.json` records, for
every frame: frame time, simulation time, time gathering draws, time issuing them, time presenting,
GPU time, time the garbage collector paused the game, draw calls, triangles, instances and memory.

`--autoshot N --perf-label name` writes a screenshot and a snapshot with terrain detail
(`terrainLod`), scatter (`scatter`), model detail (`modelLod`) and dropped instances.

If submitting a terrain takes more than 25 ms in a frame, the Player prints which part was slow.

## Measured

Test world: an 8192 m island at 2 m spacing, 242,864 scattered copies in seven layers (four tree
species, two rocks, a bush), twelve settlements with 287 placed Objects, four rivers, a sea, a
16 km far plane, 3.5 km sun shadows. RTX 5060 Ti 8 GB, 1600 × 900, Direct3D 11, vsync off.

A 437-second flight at 70 m/s, 55 m above the ground, over every settlement and the highest peak:

| Measure | Result |
|---|---|
| Frames | 199,346 (456 per second on average) |
| Frame time | median 2.0 ms, 95% under 4.0 ms, 99% under 6.7 ms, 99.9% under 11.5 ms |
| Longest frame | 45 ms, in the first second (first collision tiles); 3 frames over 16.7 ms in the whole flight |
| Draw calls | 508 on average, 1,744 at most |
| Triangles | 4.2 million on average, 11.2 million at most |
| Instances | 2,309 on average, 8,698 at most; none dropped |
| Time gathering draws | 0.5 ms on average, 7.5 ms at most |
| Garbage collector pauses | 3.7 ms at most |
| Memory | 1.62 GB working set at most |

Still views (frames per second): village at ground level 437, forest valley from the air 309, the
island from its highest peak 361, the coast from the air 328, the whole island from 3 km up 406.

Some earlier flights had occasional frames of 200 to 500 ms. They were not garbage collection,
which is measured, and did not recur in the last three flights. Another program was using the
machine during the flights that had them; that is the likely cause but it was not confirmed.

### Loading, streaming and solid scatter

The same world with `ScatterCollision` on its four tree species and two rocks and a scenery
distance of 4000 m (the 287 placed Objects are all plain scenery), measured on 2 October 2026:

| Measure | Result |
|---|---|
| Models read ahead | 18 started on worker threads (11 village models, 7 scatter models), 18 used by the load |
| Model cache | first run: 11 written, 0 read; second run: 11 read, 0 written |
| Player start to first frame | 1.14 s on the first run, 0.88 s with the cache. The room's terrain step, which loads the scatter models, went from 257 ms to 72 ms. These models are 1 to 2 MB each; the saving grows with model size |
| Scatter colliders | 8 around a camera standing in forest beside a village; 0 over open sea |
| Scenery | 287 of 287 loaded from the middle of the island; 73 of 287 from its far corner, which is the number within the 4800 m unload distance |
| 150-second flight with all of it on | 59,170 frames, median 2.3 ms, 99% under 5.5 ms, 99.9% under 11.8 ms, longest 32 ms; 6 frames over 16.7 ms |

With scenery streaming the longest frame of the flight's first minute fell from 44 ms to 24 ms,
because the buildings' colliders are no longer all made in the first second. Single frames of 9 to
19 ms remain where terrain collision and buildings with fitted colliders first arrive; those were
in the flight above as well.

## Verification

`Build.bat --test large-world` runs twenty-five checks. Twelve cover the terrain, detail and view
work: bulk terrain files, collision tiles following what can touch the ground, an 8 km terrain
drawn with distance detail and no gaps, a world made from a recipe (sea, rivers running downhill
in channels, paint following the land, level sites, objects on the ground), the simplifier (shape,
seams, open borders, error limit), automatic levels and how they follow size on screen, scatter
placement (repeatable, seamless between cells, obeying height, slope and paint, clumping), the wide
shadow cascade leaving the near cascades alone, room settings surviving save and reload, distant
objects resting, the editor near plane on long views, and the sea reaching the horizon.

Thirteen cover loading, streaming and sharing:

- the model cache: same model back, faster, never stale after an edit, ignored when damaged;
- reading ahead: workers read what the load then takes, a result is handed out once, a model saved
  after it was read loads as saved;
- scatter colliders made near what can touch them and removed behind it;
- scenery loading near the camera and unloading behind it, and scripted Objects never streaming;
- distant terrains loading as the camera reaches them;
- the automatic near plane of a long view;
- the scatter editor leaving a layer's pattern and other fields alone when one field is edited;
- animated meshes simplifying to a subset of their own vertices, bone weights intact;
- camera shake moving the view and not the camera, and coming to rest;
- game speed being set by scripts and kept in range;
- a background room change waiting for the models and giving way to an ordinary one;
- lights added during an update reaching the next frame once, and staying lit between steps;
- a host and two players over an in-memory link: who is sent what, copies following movement
  and animation, lost copies coming back, and leaving cleaning up.

What those checks do not show, and has not been seen in a running game yet:

- multiplayer over a real network connection (the check uses an in-memory link);
- lights added from Step events on screen, and a script's ambient colour under the dynamic sky;
- `GameSetSpeed`, `CameraShake3D` and `RoomGotoWhenLoaded` in play;
- terrain streaming in the Player, and the Terrain editor's scatter preview on screen;
- animated models' simplified versions on screen;
- the cost saved by drawing each local shadow face only its own casters. The shadow and rendering
  suites pass with it (82 checks), which shows the picture is not broken, not how much faster a
  room of lights and characters is.

Scatter drawing, the long-view terrain shading, the far water and the shadow cascade on screen are
checked by the captures and the flights above, not by a harness case.

Full Build `20261001-225728-58203d29` (2 October 2026): 1124 checks passed, with the dx11, dx12,
vulkan, opengl and software renderer smokes; nothing skipped. Earlier: `20261001-175935-a25c1956`
passed 1111 checks and `20261001-163347-0103944c` passed 1110, each with the same five smokes.

Four other full runs during this work each failed one or two checks that pass alone:

- `Editor.Model.Profile.PointerDrawAndVisibleTriangleCost` (twice). A fault in the check: it waited
  80 ms for a status line that refreshes every 250 ms. It now waits for the refresh.
- `Runtime.PGSL.TwoD`, "the player never reported loading the room" (once, and once before this
  work). The Player dropped a log line whenever another program was reading the log at that
  instant, and the check reads it every 50 ms. The Player now waits for the reader.
- `Studio.LibraryTags.Browser.TagEditAndSharedUndoRedoUseMetadataHistory` (twice). Cause not found.
  It passed in the final run and three times alone, and had not failed in the 59 recorded runs
  before this work, so this work cannot be ruled out as the trigger.

## Not done

- **Depth precision.** The scene depth buffer is 24-bit with a standard range. Long views move
  the near plane out to about half a metre; reversed depth, which would allow a near plane of a
  few centimetres at any view distance, is not implemented.
- **Terrain streaming is whole terrains.** Each terrain file is read whole when the camera nears
  it. One very large terrain is not split on disk, and neighbouring terrains are not stitched: a
  seam shows unless their edge heights match.
- **Scripted objects load together.** Scenery streams; an Object with a script, events, physics or
  persistence exists from room load. Activity distance stops its Step events but does not unload it.
- **Scatter collision is boxes.** A solid copy is an upright box, not the model's shape, and
  distant scatter casts no shadows.
- **Shadows from behind the camera.** Terrain tiles outside the view are not drawn, so a mountain
  behind the camera does not shade what is in front of it.
- **Room changes block for what is left.** `RoomGotoWhenLoaded` reads the next room's models
  while the game runs, which is most of the wait. Creating the room's objects, loading its terrain
  and sending its meshes and textures to the graphics card still happen in one step before the
  next frame, and the engine draws no loading screen of its own for that.
- **Multiplayer limits.** Copies share position, rotation and the animation being played. No
  sharing of script variables, no prediction or lag compensation, no cheat prevention, no
  dedicated server, and one room for all players. Enough for players to see each other and a
  shared world's creatures move; not yet a complete basis for a Palworld-scale online game.
- **Exported games start without a model cache.** The cache is not part of an export; the game
  writes its own on first run, so a player's first load reads every model the slow way.
- **A streamed building's collider is made when it arrives.** A model with a fitted mesh collider
  costs up to about 10 ms in the frame it is created.
- **Platforms.** Windows x64 only.
