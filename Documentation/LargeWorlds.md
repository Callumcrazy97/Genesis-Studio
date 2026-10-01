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
| Models | A static model with no authored levels of detail gets up to three simplified versions, made in the background the first time it is seen small. The version drawn follows the model's size on screen. A model smaller than about 0.25% of the screen's height is not drawn. Skinned models are never simplified. |
| Scatter | A terrain's scatter layers place copies of a Model by rule: density, height range, slope, painted layer, clumping. Nothing is stored; each 256 m cell is worked out when it comes into range. Near cells draw every copy as an instance with shadows; further cells draw as one merged mesh each. |
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
| Sun shadow distance (m) | `shadowDistance` | 0 | How far from the camera the sun casts shadows. 0 keeps close-range shadows only. 2000 to 4000 shades mountainsides. |
| Object activity distance (m) | `simulationDistance` | 0 | Scripted objects with a 3D model further than this skip Step events. 0 runs everything. |
| Ambient intensity | `ambientIntensity` | 1 | Now also scales the dynamic sky's ambient light. Raise it when shaded slopes are too dark. |

Set the camera's far plane to cover the view you want (`Engine.SetCameraFarPlane(16000)`) and its
near plane to 0.5 or more (`Engine.SetCameraNearPlane(0.5)`). The depth buffer has 24 bits; a near
plane of 0.1 with a 16 km far plane causes flicker on distant surfaces.

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
| `Sites(count, minHeight, maxHeight, radius, separation)` | Finds level ground, near rivers when there are any, and flattens it. |
| `SitePlace(object, count, innerRadius, outerRadius, spacing)` | Arranges Objects around each site of the last `Sites`, facing the centre. |
| `SitePaint(layer)` | Paints a layer under each site of the last `Sites`. |

New functions for the per-sample code: `fbm(x, z, octaves)`, `ridge(x, z, octaves)`,
`lerp(a, b, t)`, `smoothstep(edge0, edge1, value)`, `length(x, z)`.

Generating an 8192 m terrain at 2 m spacing (16.8 million samples) with erosion, rivers, paint,
seven scatter layers and twelve sites takes about 17 seconds.

### Scatter layers

A scatter layer names a Model, not an Object: copies are drawn, they do not run scripts and have
no collision. Use Objects placed with `SitePlace`, or in the Room editor, for anything a player
interacts with.

Distant copies are merged meshes that keep each material's colour and texture and cast no shadows. Give small things (bushes, pebbles) a draw distance so they are not built for
the whole world.

At most 22,000 scattered copies are drawn as instances in one frame, nearest first; the renderer's
instance buffer holds 32,768 for everything. The Player's performance snapshot reports copies that
did not fit (`scatter= ... dropped`) and instances the renderer dropped (`instancesDropped`).

## Building a project without the editor

The headless harness can do what the editor does, for scripts and tests:

```powershell
$tool = "Tests\Genesis.Application.Headless\bin\Release\net10.0-windows\Genesis.Application.Headless.exe"
& $tool --project-tool new "C:\Projects" "MyWorld"
& $tool --project-tool import "C:\Projects\MyWorld" Pine.glb Cottage.glb Grass.png
& $tool --project-tool terrain-from-code "C:\Projects\MyWorld" Island World.terrain.pgsl 20261001
& $tool --project-tool terrain-room "C:\Projects\MyWorld" Island World
& $tool --project-tool start-room "C:\Projects\MyWorld" World
```

`terrain-from-code` uses the Terrain editor's own Create From Code route in a hidden window.

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

## Not done

- **Depth precision.** The scene depth buffer is 24-bit with a standard range. A 16 km view works
  with a near plane of 0.5 m; reversed depth, which would allow a smaller near plane, is not
  implemented.
- **One terrain in memory.** A terrain is one file loaded whole. 8 km at 2 m is 100 MB and fine;
  a world of many terrains streamed from disk is not implemented.
- **Room contents load together.** Placed Objects all exist from room load. Activity distance
  stops distant scripts running but does not unload the objects.
- **Scatter has no collision** and distant scatter casts no shadows.
- **Shadows from behind the camera.** Terrain tiles outside the view are not drawn, so a mountain
  behind the camera does not shade what is in front of it.
- **Editors.** The Terrain editor does not preview scatter layers, and its water list has no
  Ocean option; both come from a script today and appear in the running game.
- **Multiplayer.** There is a network transport and nothing above it: no replication, interest
  management or server authority. A shared-world game on the scale of Palworld needs those.
- **Platforms.** Windows x64 only.
