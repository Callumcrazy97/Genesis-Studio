# Cookbook: a good-looking game, quickly

What to reach for first when making a game with Genesis, whether you are a person in Studio or an
AI driving Studio's automation ([Automation.md](Automation.md)). Every recipe here uses Genesis's own
editors and commands; nothing writes project files by hand. The PGSL snippets are checked by the
test suite, so they always compile.

The worked example is [NatureWalk.batch.json](NatureWalk.batch.json), beside this guide: one
`batch` of 33 commands, one a line, that you can copy and change (`call batch @NatureWalk.batch.json`
in a new project). It builds a walk round a lake (terrain, trees, grass, water, sky, sound and a
first-person walker) from nothing in about two seconds.

## The loop: make, look, change

A quick game is good because it is looked at many times. Keep each round short:

1. **Make** in one batch: `batch` runs a list of commands in one go and checks the project once
   at the end. Separate `call`s each start Studio afresh, about a second each.
2. **Look**: `game.run` plays the game with scripted input and takes pictures at the seconds you
   ask for (`shots`), into the project's `Debug/Images`. Look at them.
3. **Change** the numbers and go round again. `game.benchmark` gives frame times when it matters.

```
"Genesis Application.exe" --automation "<project>" call batch @scene.json
"Genesis Application.exe" --automation "<project>" call game.run "{\"steps\":[{\"seconds\":4},{\"seconds\":3,\"hold\":[\"W\"]}],\"shots\":[3.5,7]}"
```

`game.run` starts the input with the first frame of play, once the first room is on screen, and
plays it by the clock: `steps` and `shots` both count seconds of play from that frame, at any frame
rate. A new project already has a room called Start, so give yours another name.

## A 3D place

| Want | Do |
|---|---|
| A 3D room with a sky | `room.create` with `"dimension": "3D"`; it starts on a sunny day. `"scene"`: `SunnyDay`, `GoldenHour`, `Overcast`, `Stormy`, `Snowy`, `Night`, `Alien`, `Indoor` (the Room editor's Sky cards). `"start": true` makes it the first room. |
| A mood later | `room.setEnvironment` with `"scene"`, then `"values"` to fine-tune (`timeOfDayHours`, `visibilityKilometres`, `shadowDistance`, `atmosphericHaze`). |
| Ground | `terrain.create` from a landform (`RollingHills`, `Hills`, `Mountains`, `Islands`...), then `room.addTerrain`. `minHeight` and `maxHeight` are limits, not the hills you get: rolling hills use a part of them (-10 to 30 gave about 9 m of relief), so sculpt for more. The terrain is centred on 0, 0. |
| Hills, a basin, a flat path | `terrain.sculpt` strokes: `Raise`, `Lower`, `Flatten` (`flattenHeight`), `Smooth`, along world points `[x, z]`. A `Raise` pass at strength 1 adds about 5 m; run more passes for more. |
| A path you can see | `terrain.sculpt` with `"brush": "Paint", "layer": "Dirt"` along it. Scripts read it back with `Engine.Terrain.MaterialAt(x, z)` (1 grass, 2 dirt, 3 rock, 4 snow). |
| Trees kept off the path | Give tree layers `"paintLayerMask": 1, "minimumPaintWeight": 0.92` (only nearly pure grass), and paint a faint, wider dirt verge (`"radius": 6, "strength": 0.05`) along the path under the real one. |
| Grass | `terrain.setGrass` (`MeadowGrass`, `TallGrass`, `Wildflower`...); it grows round the camera, on the first painted layer unless `layerDensities` says otherwise (`[1, 0.3]` thins it onto the second); `maximumDrawnTufts` caps it. Its colour comes from the species, not a setting. |
| Trees and rocks | `model.create` from primitives (Cylinder trunk, Cone or Sphere crowns), then `terrain.addScatterLayer` with density, spacing, height, slope, clumps, `collisionRadius` for trunks. |
| A lake, a river, the sea | `terrain.addWater` (`Lake` with `sizeX`, `sizeZ`, `surfaceHeight`; `River` along points; `Ocean`). |
| Dust in the light, fire, rain | `particle.create` from a preset (`DustMotes`, `Fire`, `Smoke`, `Snow`, `Rain`, `Magic`...), on an Object with `object.setComponent` `ParticleComponent`. |
| An effect of your own | Start from the nearest preset, then shape it module by module with `particle.module`: `list` shows the stack; `set` `spawn.rate` `rate`, `update.colour` `gradient`, `update.size` `curve` (points over the particle's life); `add` `render.trail` or `update.collision`; `off` what you do not want. The Particle editor's **Modules** shows the same stack. |
| Wind, birdsong, footsteps | `audio.synth` with a preset (`Wind`, `Birdsong`, `Footstep Grass`, `Footstep Path`), or the Audio editor's Make a sound; then the room's `windAudio` and `wildlifeAudio`: the engine plays them. |

## A 2D level

| Want | Do |
|---|---|
| A 2D room | `room.create` (2D is the default); its width and height are in pixels and are the window. |
| Tiles to build with | `image.create` with `"usage": "Tileset"`, `tileWidth`/`tileHeight`, and `solidTiles` (tile numbers, left to right and top to bottom from 0) for the ones that are floor and wall; draw them with `image.draw`. `"autotile": true` with the 16 tiles for each mix of neighbours in a 4 by 4 block lets a path or a wall pick its own ends and corners. |
| Floors, walls, platforms | `room.paintTiles` with `"solid": true` and `rects` (`{ "x", "y", "width", "height", "tile" }` in cells) or `cells`. The Platformer and Move with arrow keys recipes stop at solid tiles. |
| A player | `object.create` with a `sprite` and `"recipes": ["Platformer"]`, placed above the floor. |

## A player

`object.create` with `recipes` gives an Object the Object editor's Behaviour cards, as ordinary
PGSL in its events. Your own `events` code goes after the recipe's, so it can change its numbers.

| Recipe | What it does |
|---|---|
| `FirstPerson` | Mouse look, WASD, Shift to run, Space to jump, controller too; the engine's character mover (walls, steps, slopes, tree trunks). Faces the way it is placed (`room.addInstance` `yaw`). Set `stepSound` to an Audio resource for footsteps. |
| `ThirdPerson` | Walks where the camera looks, turns its model to face the way it walks; the orbit camera follows. Give the Object a model. |
| `Spin3D`, `Float3D`, `Timed` | Spin on the spot, bob up and down, disappear after three seconds. |
| `MoveArrows`, `Platformer`, `Patrol`, `Chase`, `Projectile`, `Collectible`... | The 2D ones. `object.info` lists those that suit an Object. |

```json
{ "command": "object.create", "input": { "name": "Walker", "recipes": ["FirstPerson"],
  "events": { "Create": "stepSound = \"Step Grass\";\nwalkSpeed = 3;" } } }
```

## PGSL you will want

Angles are in degrees (`Sin`, `Cos`, `ArcTan2`). 3D rooms measure in metres with y up. Some names
belong to every instance (`x`, `y`, `z`, `speed`, `direction`, `id`...): a `var` of the same name is
refused, so call yours `moveSpeed`.

Footsteps that follow the ground under the walker:

```pgsl
if (Engine.Terrain.MaterialAt(x, z) == 2) { stepSound = "Step Path"; } else { stepSound = "Step Grass"; }
```

Stand something on the ground wherever it is:

```pgsl
y = Engine.Terrain.GetHeight(x, z);
```

A camera of your own (after turning off the engine's free-fly camera, which a script moving the
camera does for you):

```pgsl
var lookYaw = 40;
var lookPitch = -10;
SetCameraPosition(x, y + 1.7, z);
SetCameraTarget(x + Sin(lookYaw) * Cos(lookPitch), y + 1.7 + Sin(lookPitch), z + Cos(lookYaw) * Cos(lookPitch));
Engine.Camera.Fov = 68;
```

A sound once, a little varied, and one that loops:

```pgsl
PlaySound("Coin", 0.6, RandomRange(0.95, 1.05), false);
music = PlaySound("Theme", 0.5, 1, true);
```

Something every few seconds (Alarm0 runs after the frames given; at 60 a second, 180 is three
seconds):

```pgsl
SetAlarm(0, 180);
```

Keys, the mouse and a controller:

```pgsl
var goAhead = KeyCheck("W") || KeyCheck("Up") || GamepadAxis("LeftY") > 0.5;
var jumpNow = KeyPressed("Space") || GamepadPressed("A");
var turn = GetMouseLookDeltaX() * 0.14;
```

Turn an Object to face the way it moves (degrees about y):

```pgsl
var headX = 1;
var headZ = 0.5;
Engine.Instance.Yaw = ArcTan2(headX, headZ);
```

Words and a HUD, in an Object's DrawGui event (screen pixels from the top left):
`DrawSetFontSize(size)`, `DrawSetColorRgb(r, g, b)`, `DrawSetAlpha(a)`, `DrawSetTextAlign(align)`,
`DrawText(x, y, text)`, `DrawRectangle(x1, y1, x2, y2)`. Studio's Script editor shows every
function with its arguments as you type.

Checks a test run reports (`game.run` lists them, and a failed one fails the run):

```pgsl
TestCheck("walker stands on the ground", Abs(y - Engine.Terrain.GetHeight(x, z)) < 0.2, "y " + String(y));
```

## The look

- **Light**: a scene preset gives sun, sky, clouds and haze together. Reflections and light bounced
  off the ground and walls are automatic in a game (`Engine.Lighting.*` to tune or turn off).
- **Distance**: `visibilityKilometres` sets how far the haze lets you see; 2 to 3 km gives depth
  without hiding the hills.
- **Shadows**: `shadowDistance` (metres) covers what the player walks among; 100 to 150 for a walk. `Engine.Rendering.Shadows.Strength` (0 to 1) lightens shade that looks too dark for a sunny scene; 0.7 is gentle.
- **Colour**: give terrain paint layers images for real ground (`Layer(slot, name, image, tileMetres)`
  or the Terrain editor's layers); a model's parts take colours in `model.create`.
- **Exposure**: `Engine.Rendering.AutoExposure(0.18, 1.5)` lets the picture adapt between sun and
  shade; `Engine.Rendering.Exposure` brightens or darkens on top.

## Pitfalls

- A room's width and height are its size; with no window size of its own the game window takes
  them, which is right in 2D (pixels) and wrong in 3D (metres). `room.create` gives a 3D room a
  1280 by 720 window unless you pass `windowWidth` and `windowHeight`; set them yourself in rooms
  made another way.

- Resource names are unique across kinds: an Object and a Model both called "Tree" break
  `CreateInstance`. Call the model "Tree Model" or the Object "Tree Thing".
- A project's saves live in the user's folder by project id: test with copies made by Studio
  (they get a new id), not folder copies.
- `game.run` and `game.benchmark` open a game window. On a shared machine take the test lock
  (`Global\GenesisAgentTestRun`) for each run and release it straight after.
