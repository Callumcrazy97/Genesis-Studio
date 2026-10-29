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
| Grass | `terrain.setGrass` with only the terrain grows the built-in meadow grass (the Terrain editor's Templates › Foliage: grass): it grows round the camera on the first painted layer unless `layerDensities` says otherwise (`[1, 0.3]` thins it onto the second), and a stock Grass layer turns the darker green between the tufts. Its look is the grass shader's: `baseColor` and `tipColor` (`[r, g, b]`, root and tip), `colorCurve`, an Image `texture` on each blade, and `windStrength`, `windSpeed`, `windGusts`. It keeps off the terrain's paths and parts round animated characters by itself; a game adds pushers with `Engine.Terrain.Grass.Pushers.Set(key, x, y, z, radius)`. |
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

## Music

| Want | Do |
|---|---|
| A tune of your own | New › Music (it lives in Assets/Music) opens the Audio editor's Music mode: tracks of notes, each with an instrument (`Piano`, `Pluck`, `Bass`, `Lead`, `Pad`, `Organ`, `Drum Kit`, or any Make a sound preset), mute, solo and volume. Click the piano roll for a note, drag to move or resize it, drag in the lane under it for velocity; Play, Loop (drag on the ruler) and Undo work as you would expect. |
| A song from another program | Import MIDI… in the Music mode reads a `.mid` file (format 0 or 1): a track per channel, channel 10 as the drum kit. Export MIDI… writes it back for other programs. |
| The music in the game | Render to Audio makes the Audio resource `<piece> Audio` (music bus, repeating) beside your other sounds; play it with `PlaySound("Theme Song Audio", 0.5, 1, true)`. Render again after an edit: the same resource is updated. |
| A bus effect | `Engine.Audio.Music.Reverb.Mix = 0.4;` then `Engine.Audio.Music.Reverb.Enabled = true;` (any bus by name: `BusReverb("ui", "hall", 0.3)`, `BusFilter("sfx", "lowpass", 600)` for underwater, `BusEq("music", 3, 0, -2)`, `BusCompressor("sfx", -18, 4)`). Every effect is off until set. The Audio editor's Options › Mixer and snapshots sets them for the whole project (AudioMixer.json), and the game starts with them. |
| A snapshot blend | Set the buses for a mood and keep it, `AudioSnapshotSave("Explore")`, or keep snapshots in the Mixer page; then `Engine.Audio.Snapshot.Blend("Combat", 1.5);` moves every bus the snapshot names (volume and effects) there over 1.5 s, and `Engine.Audio.Snapshot.Blend("Explore", 3);` goes back. |
| A seamless loop from the synthesiser | `audio.synth` with `"loopSeconds": 4` (wind, rain, an engine hum): exactly 4 s, and its end runs into its start. `"pan"` on a layer makes it stereo. |

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

Camera rigs: named shots the game switches between, blending position, rotation and field of view,
and a dolly that rides a track. In the player's Create:

```pgsl
Engine.Camera.Rigs.Create("wide", "fixed");        // stands where the camera is now (or set OffsetX/Y/Z, Yaw, Pitch)
Engine.Camera.Rigs.Create("chase", "follow");      // 3 m up and 8 m behind, looking at its target
Engine.Camera.Rig.Target = id;
Engine.Camera.Rig.Damping = 0.3;                   // a soft follow; 0 is rigid
Engine.Camera.Rigs.Create("flyby", "dolly");
Engine.Camera.Rig.Path.Use("Intro Track");         // a Pathing resource, or Engine.Camera.Rig.Path.Add(x, y, z) point by point
Engine.Camera.Rig.Path.Speed = 6;                  // metres a second; or set Engine.Camera.Rig.Path.Position (0 to 1)
Engine.Camera.Rig.Target = id;                     // looks at the player; with no target it looks along the track
Engine.Camera.Rigs.Cut("flyby");                   // start on the track
```

Later, from any event:

```pgsl
Engine.Camera.Rigs.BlendTo("chase", 1.5, "easeinout");   // or "linear", "easein", "easeout", "cut"
Engine.Camera.Rigs.Active = "wide";                      // blends over Engine.Camera.Rigs.BlendTime (1 s) along BlendCurve
Engine.Camera.Rigs.Active = "";                          // hands the camera back to the room, the orbit camera or your script
```

Kinds: `fixed`, `follow`, `orbit` and `shoulder` (an arm round the target that the mouse turns:
`Distance`, `Yaw`, `Pitch` looking down, `MouseLook`, `Collision`), `lookat` (stands still, turns to
the target) and `dolly`. `Engine.Camera.Rig.Fov` is 0 to keep the camera's own. The rigs belong to the
room: a room change removes them, so make them in a Create or Room Start event. The same commands
take a rig's name too: `CameraRigCreate(name, kind)`, `CameraSetRigOffset(name, x, y, z)`,
`CameraRigBlendTo(name, seconds, curve)`, `CameraRigPathAdd(name, x, y, z)`.

A character that turns its head to the player and reaches for a lever, on top of whatever it is
playing (a clip, a layer or an Animation Controller). Once set, it keeps looking and reaching; no
need to call it every frame:

```pgsl
player = InstanceFind("Player", 0);
lever = InstanceFind("Lever", 0);
Engine.Animation.LookAt.Follow(player, "Head");    // meet its eyes; Engine.Animation.LookAt.At(x, y, z) for a point
Engine.Animation.LookAt.YawLimit = 60;             // across, degrees (70 unless set); PitchLimit up and down (40)
Engine.Animation.LookAt.BlendTime = 0.4;           // seconds to turn in and back (0.25 unless set)
AnimationReachToInstance("RightHand", lever, "Handle", 1, 0.3);   // or AnimationReachToPoint(bone, x, y, z)
// Later:
Engine.Animation.LookAt.Stop();                    // back to the animation over BlendTime
AnimationReachStop("RightHand");
```

The head bone is `Engine.Animation.LookAt.Bone` ("Head" unless set) and a rig's prefix can be left
off ("Head" finds "mixamorig:Head"); a reach bends the two bones above the one named. A model built
facing another way than +Z: `Engine.Animation.LookAt.SetForward(x, y, z)`. From C#:
`ModelInstance.SetBoneLookAt(world, entity, "Head", target)` and `SetBoneReach`, `ClearBoneLookAt`,
`ClearBoneReach`.

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

Controls a player can change: name actions, read them, and give the player the ready-made
rebinding screen with one call (every action, press-a-key rebinding, reset, save, keyboard and
controller; it pauses the game until Back). More in GameFeatures.md, "Analogue actions...":

```pgsl
// Create
InputActionBind("Jump", "Space"); InputActionBind("Jump", "Pad:A");
InputActionBind("Steer", "Pad:LeftStickX"); InputActionSetDeadZone("Steer", 0.15);
InputActionsLoad();
// Step
if (InputActionPressed("Jump")) { vspeed = -8; }
x += InputActionValue("Steer") * 4;
if (KeyPressed("F1") || GamepadPressed("Back")) { InputRebindingOpen(); }
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

## Make a shader from nodes

A look of your own on a model (a tinted pattern with a glowing rim, a dissolve, lit windows) is a
Shader resource. Build it from nodes rather than HLSL: in Studio with the Shader editor's **Nodes**
view, or with `shader.graph`, which keeps the graph in the resource and writes the code it makes
(a surface shader for Model and Terrain targets, so the engine lights it), checked by compiling it
for DX11 and Vulkan. `shader.nodes` lists the node types and their ports. A texture times a tint,
with a fresnel rim as emission (`Checker` is an Image resource; the test suite runs this exact call):

```json
{
  "shader": "Rim",
  "target": "Model",
  "graph": {
    "nodes": [
      { "id": "out", "type": "Output", "x": 720, "y": 60 },
      { "id": "pattern", "type": "TextureParameter", "name": "Pattern", "texture": "Checker", "x": 0, "y": 0 },
      { "id": "sample", "type": "SampleTexture", "x": 220, "y": 0 },
      { "id": "tint", "type": "ColorParameter", "name": "Tint", "value": [1, 0.55, 0.3, 1], "x": 220, "y": 200 },
      { "id": "colour", "type": "Multiply", "x": 480, "y": 40 },
      { "id": "rim", "type": "Fresnel", "inputs": { "Power": [2.5] }, "x": 220, "y": 340 },
      { "id": "rimColour", "type": "ColorParameter", "name": "RimColour", "value": [0.2, 0.7, 1, 1], "x": 220, "y": 480 },
      { "id": "glow", "type": "Multiply", "x": 480, "y": 380 }
    ],
    "connections": [
      { "fromNode": "pattern", "fromPort": "Texture", "toNode": "sample", "toPort": "Texture" },
      { "fromNode": "sample", "fromPort": "RGB", "toNode": "colour", "toPort": "A" },
      { "fromNode": "tint", "fromPort": "RGB", "toNode": "colour", "toPort": "B" },
      { "fromNode": "colour", "fromPort": "Out", "toNode": "out", "toPort": "Colour" },
      { "fromNode": "rim", "fromPort": "Out", "toNode": "glow", "toPort": "A" },
      { "fromNode": "rimColour", "fromPort": "RGB", "toNode": "glow", "toPort": "B" },
      { "fromNode": "glow", "fromPort": "Out", "toNode": "out", "toPort": "Emission" }
    ]
  }
}
```

Make the resource first (`resource.create` with `"kind": "Shader"`), then put it on an Object
(`object.setComponent` with a `ShaderComponent`) or a model's material. `Tint` and `RimColour` show in the
Inspector and scripts set them per instance (`ShaderSetVector("RimColour", 1, 0.3, 0.2, 1)`). An
Output input left unwired keeps the model's own value: unwired Roughness keeps its material's. The
Shader editor opens the resource on its graph; [the node view](README.md#node-view-29-october-2026)
describes the nodes.

## Things that move: plants, banners, water

A surface shader can move its model's vertices before they are drawn ([vertex stage](MeshShaders.md#vertex-stage)),
so a bush sways, a banner ripples and a pond's surface rolls, with no animation and no script.

- **In Studio:** the Shader editor's **Wind Sway** (plants, trees, banners: the higher above the
  model's base, the more it bends, following the weather's wind) and **Wave** (water planes, flags)
  presets. Pick one in Quick setup, preview it on your model, tune `Strength`, `Speed` and `Height`
  (or `Amplitude`, `Wavelength` and `Speed`), save, and put it on the Object (Use in game).
- **In code:** add `void Vertex(GenesisVertexInput i, inout GenesisVertex v)` to a surface shader
  (the Code page's **Vertex** button adds an empty one) and change `v.Position` (the model's own
  space) and `v.Normal`. Add `i.ObjectPosition` to the phase so copies do not move as one.
- **From automation:** `shader.graph` with something wired into the Output's `VertexOffset`; a
  gentle bob, 10 cm up and down:

```json
{ "shader": "Bob", "target": "Model", "graph": {
  "nodes": [
    { "id": "out", "type": "Output" }, { "id": "time", "type": "Time" }, { "id": "sine", "type": "Sin" },
    { "id": "up", "type": "Vector", "value": [0, 0.1, 0, 0] }, { "id": "bob", "type": "Multiply" } ],
  "connections": [
    { "fromNode": "time", "fromPort": "Out", "toNode": "sine", "toPort": "In" },
    { "fromNode": "up", "fromPort": "XYZ", "toNode": "bob", "toPort": "A" },
    { "fromNode": "sine", "fromPort": "Out", "toNode": "bob", "toPort": "B" },
    { "fromNode": "bob", "fromPort": "Out", "toNode": "out", "toPort": "VertexOffset" } ] } }
```

Keep the movement small for things that cast big shadows: the shadow still falls from the shape
before it moved. The Terrain editor's grass has its own wind (`terrain.setGrass`); this is for
models.

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
