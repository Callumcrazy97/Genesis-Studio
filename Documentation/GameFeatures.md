# Game features: weather, models, animation, controllers, sound, HUD, saves, room changes

Engine features added on 2 October 2026 for games in general. None of them is specific to one
project, and each is off or unchanged until a room, an Object or a script asks for it, except where
this page says a default changed.

[Large worlds](LargeWorlds.md) covers the other half of the same work: streaming, the model cache
and multiplayer. [Menu features](MenuFeatures.md) covers the 3 October additions: text layout,
smooth shapes, clipping, globals, quitting and menu input.

## One engine API: Engine.Physics, Engine.Model, Engine.Audio... (24 October 2026)

Every engine command and value now has one standard name, `Engine.<Area>.<Name>`, and every value
is a property read and set like a variable (PgslLanguage.md, "The engine API"; the full list is
[EngineApi.md](EngineApi.md)). A body's values are the current instance's, so physics reads like
GameMaker's: `Engine.Physics.Mass = 4;`, `Engine.Physics.VelocityY += 3;`,
`with (Crate) { Engine.Physics.Bounciness = 0.8; }`.

| Engine.Physics property | What it is |
|---|---|
| `HasBody`, `BodyType`, `Shape` | Whether the instance has a body; "dynamic", "kinematic" or "static"; "box", "sphere", "capsule" or "cylinder" (setting either remakes the body where it is, moving as it was) |
| `Mass`, `Weight`, `Friction`, `Bounciness` | The body's mass (its inertia follows), how little air slows it, its grip and how much it bounces |
| `GravityScale`, `UseGravity`, `LinearDamping`, `AngularDamping` | How hard gravity pulls it, whether at all, and how fast movement and spin die away |
| `LockRotation`, `Sensor`, `Enabled`, `Layer`, `Mask` | Kept upright; only noticing what passes through; in the simulation at all; its collision layer and the layers it hits |
| `VelocityX/Y/Z`, `Speed`, `SpinX/Y/Z`, `Awake`, `Grounded` | Its velocity (pixels or metres a second), speed, spin in degrees a second, whether it is simulated or asleep, and whether it stands on something |
| `AirDrag`, `GravityStrength`, `GravityX/Y/Z`, `MaxVelocity`, `AllowSleep`, `SleepThreshold`, `RecoveryVelocity` | The world's settings |

`Engine.Physics.Use("Bouncy")` gives the instance the body a Physics resource describes (in place of
any it had), `Engine.Physics.Remove()` takes its body away, and `ApplyForce`, `ApplyImpulse` and
`SetVelocity` act on the current instance when given just x, y and z. The unfinished
`Engine.Physics(type, shape, ...)` and `Engine.PhysicsUse(name)`, which only raised errors, are gone.
Models (`Engine.Model.Glow`, `CastShadows`, `ReceiveShadows`, `EmissionScale`, `Layer`, `ScaleX/Y/Z`),
images (`Engine.Image.Frame`, `Speed`, `Alpha`, `ScaleX/Y`, `BlendR/G/B`, `Width`, `Height`), audio
(`Engine.Audio.MasterVolume`, `MusicVolume`, `SfxVolume`, `Music`), the camera (`Engine.Camera.X/Y/Z`,
`Pitch`, `Yaw`) and the instance in 3D (`Engine.Instance.Pitch/Yaw/Roll`, `ScaleX/Y/Z`) work the same way.

### Engine settings in the Inspector, and live on the debug screen

The same values can be given to resources in Studio, without a script. The Inspector's **Engine
settings** group lists the settings a resource carries, each editable, with **Add a setting** (only
the values that suit it, each with its default) and **Remove a setting**. Each setting shows its
default beside its name (`Model · Glow · Strength (0)`); pointing at it shows what it does, its
default and its range, and a number keeps to its range. (Every Inspector row with a description
shows it when pointed at, since 25 October.)

**The Room editor shows a room's settings** (25 October): its 3D view draws the room as the game will
with the room's engine settings (fog, sun, ambient, shadows, exposure, grading...), without F5. The
room's look is made the game's way with and without them and every value of the frame they change
is laid over the editor's view, so nothing is written per setting; a setting is set only where it
can be read first and is put back afterwards, so values all of Studio shares (depth of field,
render scale, threads) never change for the other views. Settings with nothing to draw in a still
view (audio, input, the window) change nothing there. **The Object editor's view** likewise shows an
Object's own settings on its instance (`Engine.Instance.Yaw`, `Engine.Model.Glow`...), set as a room
sets them (after its body is made) with the view's own game in front, **the Terrain editor's view** shows a terrain's own settings as the Room editor does, and **the Model viewer** draws the model as
an instance showing it is drawn (`Engine.Model.Glow.Strength`, `Engine.Model.Shadows.Cast`,
`Engine.Model.ScaleY`...: the settings given to a stand-in instance by the game's code); an Object's
variables show in its live sandbox, which runs its events. Image resources carry no settings of
their own (an image's look in a game is its Object's). Checked by `Editor.Suite.EngineSettings.RoomViewShowsTheRoomsSettings`.

| Where | What it gives | When the game sets them |
|---|---|---|
| Object | Each of its instances (Engine.Physics.Mass, Engine.Model.Glow, Engine.Instance.Yaw...) | Just before the instance's Create event, once its room has placed it and given it its body |
| A room's instance (Room editor, selected) | That instance, on top of its Object's | The same; its values win |
| Model | Each instance showing it | The same, before its Object's |
| Room (Room editor, nothing selected) | The world (Engine.Physics.AirDrag, Engine.Sky.Coverage, Engine.Audio.MusicVolume...) | Just before the room's Create events |
| Terrain | The world of the room it is placed in | The same, before the room's own |

They are saved with the resource (an Object's as its `EngineSettings` component, a room's as
`engineSettings`), and a game sets each as a script would, so Create sees them and may change them.

On the F6 debug screen, an expanded instance in **World & Inspector** shows its **Engine values** as
they are now (its body's, model's, animation's and image's), and the panel ends with the **World
engine values** (physics, the audio buses, the game's speed): `-` and `+` nudge a number, **Flip**
turns a true/false value over and **Next** moves a choice on (BodyType). The **Resources** tab now
lists the loaded Scripts too, with how many functions and instructions each has, beside the models,
textures and sounds.

## Objects inside Objects (24 October 2026)

An Object can have **parts**: other Objects made with it wherever it is made (placed in a room, or by
`CreateInstance`), kept on it as it moves, turns and scales, and destroyed with it: a cart's wheels, a
knight's sword, a lamp on a post. In the Object's Inspector, **Parts** has **Add a part** (any other
Object), each part's offset (X, Y and, in 3D, Z and turn; the angle in 2D) and scale, and **Remove a
part**. A part is an instance of its own: it runs its own events and has its own engine settings, and a
part may have parts.

| Engine.Instance | What it is |
|---|---|
| `Parent` | The instance this one is kept on (0 for none); setting it keeps it on that instance where it stands now, 0 lets it go |
| `ChildCount`, `Child(index)` | How many instances are kept on this one, and the id of each from 0 (`with (Engine.Instance.Child(0)) { ... }`) |
| `Attach(id, x, y, z)` | Keep another instance on this one, x, y and z from it |
| `Detach()` | Let this instance go from the one it is kept on; it stays where it is |

## Autotiling and acoustics (24 October 2026)

**Autotiling.** Tick **Autotile** in an Image's Tile set settings (Inspector) and painting on a layer
that uses it picks each tile from its neighbours, so a wall or a path is painted in one stroke and its
ends, edges and corners choose themselves; erasing fixes the tiles around. The tile set holds the 16
tiles for every mix of neighbours in a 4×4 block starting at **Autotile column** and **Autotile row**:
a neighbour above adds 1, right 2, below 4, left 8, and tile *n* is at the block's column + *n* % 4,
row + *n* / 4. In a game, `Engine.Tile.PaintAt(x, y, layer?)` and `Engine.Tile.EraseAt(x, y, layer?)` do
the same at a point (the front tile layer there, or the one named), as does `TileRemoveAt`;
`Engine.Tile.Autotile = false` lets a script place exact tiles without the neighbours changing.

**Acoustics.** `Engine.Audio.Acoustics` is how a place sounds for sound effects: `"outdoors"` (no
echo), `"room"`, `"hall"` or `"cave"`, with `Engine.Audio.AcousticsAmount` (0 to 1, 0.5 unless set)
for how much is heard. Each room starts outdoors; a room (or its terrain) chooses other acoustics with
the Engine setting in its Inspector. `Engine.Audio.MusicMuffle` (0 clear to 1 heard through a wall)
muffles the music, for a pause menu, underwater or a dream. Music is never echoed.

## Tile layers by name, how far instances update, and what a frame cost (25 October 2026)

| Value | What it does |
|---|---|
| `Engine.Tile.Layer.SetVisible("Layer", visible)`, `Visible("Layer")` | Shows or hides one of a 2D room's tile layers by its name: a secret wall, a lit and an unlit room |
| `Engine.Tile.Layer.SetAlpha("Layer", alpha)`, `Alpha("Layer")` | Makes it see-through, 0 to 1: a roof fading as the player walks under it |
| `Engine.Tile.Layer.SetOffset("Layer", x, y)`, `OffsetX/OffsetY("Layer")` | Draws it moved by pixels: a background scrolled slower than the view for parallax (what is solid stays where it was painted) |
| `Engine.Tile.Layer.SetDepth("Layer", depth)`, `Depth("Layer")`, `SetSolid("Layer", solid)`, `Solid("Layer")`, `Engine.Tile.Enabled` | A layer drawn in a new place in the order (a wall in front as the player walks behind it); made solid or not as the room runs (a bridge that falls), tile tests and a 2D room's physics following at once; every tile layer hidden |
| `Engine.Instance.Enabled` | This instance's Step events stopped (true unless set): frozen where it is, still drawn and solid, until another instance turns it on with `with` (a statue spell) |
| `Engine.Instance.Update.Distance` | Instances farther than this from the camera skip their Step events (the room's simulation distance unless set; 0 updates all) |
| `Engine.Rendering.Backend`, `DrawCalls`, `Triangles`, `GpuMs`, `Instances.Drawn`, `Instances.Culled` | What the last frame drew and cost (read), for a game's own performance display or to choose its own quality |
| `Engine.Input.Mouse.Sensitivity`, `Engine.Input.Mouse.InvertY` | The player's look settings, 0.05 to 10 and true/false: applied where the mouse's movement enters, so the engine's cameras and every script follow them |
| `Engine.Input.Gamepad.VibrationStrength` | How strongly controllers vibrate, 0 to 1, whatever the game asks for |
| `Engine.Physics.Character.StepHeight`, `SlopeLimit`, `Radius`, `Height` | This instance's character changed as it plays (a crouch, a heavy load, a slippery spell); radius and height read |
| `Engine.Network.Replication.Rate` | How many times a second the host sends shared Objects, 1 to 120 (20 unless set) |
| `Engine.Water.Surface.Waves`, `WaveSpeed`, `FoamWidth`, `Clarity`, `ShallowColor`, `DeepColor` | The look of every water body of the room's terrains, set at once (murky after a storm, rough in a gale); each reads the first body's |
| `Engine.Water.Enabled`, `Engine.Water.Swimming.Buoyancy`, `Drag` | Whether the room's water is there (drawn, lifting and slowing bodies; true unless set), and how strongly it lifts and slows them, 0 to 10 times each water's own (1 unless set): things sink like stones at 0, a swamp at drag 10 |
| `Engine.Water.Surface.Foam.Enabled`, `Engine.Water.Reflections.Resolution`, `Distortion` | Foam off and back (each body keeps its width); the reflection drawn at 0.25 to 1 of the view (0.5 unless set) and rippled 0 to 5 times the usual (1 unless set) |
| `Engine.Terrain.Edit.Raise/Lower(x, z, radius, metres)`, `Flatten(x, z, radius, height)`, `Smooth(x, z, radius, strength)`, `Paint(x, z, radius, layer, strength)` | Changes the ground as the game plays, with the Terrain editor's own brushes: a crater where a shell lands, a floor levelled for a building, a path worn to dirt. Its look, grass and collision follow at once; the resource is untouched |
| `Engine.Terrain.Detail.Quality` | How far large terrains keep full detail, 0.25 to 4 (1 unless set); lasts across rooms |
| `Engine.Terrain.SlopeAt(x, z)`, `NormalX/NormalY/NormalZ(x, z)`, `MaterialAt(x, z)`, `Width`, `Depth` | What the ground is like at a point: how steep, which way it faces, which painted layer (1 to 8) it mostly is (footsteps, grip); how large the room's terrains are |
| `Engine.Terrain.Enabled`, `Engine.Terrain.Detail.Distance`, `Engine.Terrain.Collision.Distance` | The ground drawn or not (what stands on it still stands); how near a streamed tile is read; how far round each moving body collision is built (24 m unless set) |
| `Engine.Terrain.Grass.Enabled`, `Density`, `Distance`, `Height` | Grass on or off for every terrain, and how thick, how far and how tall, each a multiple of the terrain's own rule (1 unless set) |
| `Engine.Terrain.Scatter.Enabled`, `Density`, `Distance`, `Shadows` | The scattered trees and rocks drawn or not, how many (placed again with their collision), how far, and whether they cast shadows |
| `Engine.Particle.Emitter.Enabled`, `Rate`, `Size`, `Speed`, `Life`, `Gravity`, `Color` | This instance's attached effect changed as it plays: a fire that grows as it catches, exhaust at full throttle, a spell in the caster's colour. Size, speed, life and gravity are times the effect's own (1 unless set); the colour multiplies its colours (#ffffff unless set) |
| `Engine.Particle.Quality` | How many particles every effect makes, 0.1 to 1 of what it was authored with (1 unless set), for a slow machine |
| `Engine.Particle.Enabled`, `Bursts.Enabled`, `Particles2D.Enabled`, `Gpu.Collision.Enabled` | Every effect stopped where it is (a photo mode) and carrying on when back on; no new bursts; no new or drawn 2D particles (lights stay); particles passing through the world instead of bouncing, sticking or dying on it |
| `Engine.Navigation.Mesh.AgentRadius`, `AgentHeight`, `MaxSlope`, `StepHeight`, `CellSize` | The agent the next `Engine.Navigation.Bake()` is made for (0.35 m, 1.8 m, 45°, 0.45 m unless set; the cell worked out from the room unless set): a giant's mesh keeps wider of walls |
| `Engine.Navigation.Baked` | Whether the room has a mesh to find routes on (read) |
| `Engine.Navigation.Enabled`, `Engine.Navigation.Agent.Enabled` | Every agent, or this instance's, holding where it is with its route kept (a pause, a stunned guard) and walking on when back on |
| `Engine.Ui.Enabled` | Whether User Interfaces are drawn and answer the pointer, keys and controllers (true unless set): off for a photo mode |
| `Engine.Ui.Scale.Value`, `Engine.Ui.Scale.Auto.Enabled` | How large User Interfaces are drawn, 0.25 to 4 (1 unless set), with what is anchored to a side of the window kept there; and whether they stretch to the window (true unless set) or are drawn at their design pixels, crisp on a large screen |
| `Engine.Ui.Focus.Navigation.Enabled` | Whether the arrows, d-pad and left stick move the focus and Enter, Space or A press (true unless set) |
| `Engine.Navigation.Avoidance.Radius`, `Priority` | How much room this instance's agent keeps from others, and who gives way, 0 to 100 (50 unless set): a guard of 90 walks through a crowd of 50 |

Each command for a layer the room does not have returns false (or 0), as does a terrain edit
where no terrain is. Checked by `Engine.Pgsl.Logic.EngineApiTileLayersUpdateDistanceAndFrameCosts`,
`EngineApiWaterSurfaceLook`, `EngineApiTerrainEditsTheGroundAsItPlays`,
`EngineApiParticleEmitterLookAndQuality` and `EngineApiNavigationBakeAndPriority`; the Ui parts by
`Runtime.UI.Toolkit.ScaleStretchEnabledAndFocusNavigation`.

## Render scale and dynamic resolution (25 October 2026)

A 3D game can draw its scene with fewer pixels than the window has and stretch it to the window,
for a faster frame on a small machine (a handheld, a 4K screen on a modest card). Everything the
scene passes through (lighting, fog, bloom, depth of field, anti-aliasing, outlines, held items,
post effects) runs at the smaller size; the picture is then stretched with a light,
contrast-aware sharpening, and the GUI and text, drawn after, keep every pixel. On DX11, DX12,
Vulkan and OpenGL.

| Value | What it does |
|---|---|
| `Engine.Display.Resolution.Scale` | Share of the window's size the scene is drawn at, 0.25 to 2 (1 unless set). 0.75 shades about 44% fewer pixels; above 1 draws more and shrinks them, for a sharper picture on a fast card |
| `Engine.Display.Resolution.Sharpness` | How much the stretched picture is sharpened, 0 to 1 (0.5 unless set) |
| `Engine.Display.Resolution.Dynamic.Enabled` | Dynamic resolution (false unless set): the scale drops while the scene's frames take longer than the target allows and rises again, a step at a time, when there is room |
| `Engine.Display.Resolution.Dynamic.TargetFps` | The frame rate it holds, 10 to 500 (60 unless set) |
| `Engine.Display.Resolution.Dynamic.MinimumScale` | The lowest it goes, 0.25 to 1 (0.5 unless set); it never goes above `Scale` |
| `Engine.Display.Resolution.Current` | The share the scene was last drawn at (read) |

Dynamic resolution measures the scene's own time on the graphics card each frame, steps down 0.1
after a quarter of a second over 85% of the frame's budget and up 0.05 after two seconds under 70%
of it, so the images the scene is drawn into are remade a few times at most when a room's load
changes, not every frame. An options screen offers the scale as a game's own setting:

```gml
Engine.Display.Resolution.Scale = options.renderScale;   // 0.5 to 1 from a slider
Engine.Display.Resolution.Dynamic.Enabled = options.holdFrameRate;
```

Checked by `Engine.Render.Display.RenderScaleDrawsTheSceneSmallerOnAllBackends` and
`Engine.Render.Display.DynamicResolutionHoldsTheFrameRate` (`--test light-probes`).

## Joints limited like a body's, and ragdolls from Objects (24 October 2026)

A joint made with `PhysicsCreateJoint` turns freely. Three commands limit it, each taking a world
direction and working from how the two bodies are when it is called; `PhysicsDestroyJoint` removes
them with the joint:

| Command | What it does |
|---|---|
| `PhysicsJointHinge(joint, ax, ay, az, minDegrees, maxDegrees)` | Turns only about the axis, between the two angles: a knee, an elbow, a door |
| `PhysicsJointLimitSwing(joint, ax, ay, az, maxDegrees)` | Keeps the second body within a cone about the direction: a shoulder, a hip |
| `PhysicsJointLimitTwist(joint, ax, ay, az, minDegrees, maxDegrees)` | Limits the turn about the direction: a forearm's twist |

A ragdoll needs nothing more: it is Objects with physics. One body Object per limb, placed where
the character's bones are (`ModelSocketX/Y/Z(bone)` reads any bone's place in the world), joined and
limited as a body is, and the character's bones aimed at the limb bodies each Step:

```gml
// When the character falls: a shin hanging from the thigh, bending one way only.
knee = PhysicsCreateJoint(thigh, shin, ModelSocketX("knee"), ModelSocketY("knee"), ModelSocketZ("knee"));
PhysicsJointHinge(knee, 1, 0, 0, 0, 140);
// Each Step: the model's bones follow the bodies.
AnimationBoneLookAt("thigh", InstanceGetX(shin), InstanceGetY(shin), InstanceGetZ(shin), 1, 180);
```

Checked by `Runtime.PGSL.PhysicsJointsHingeAndLimits` (`--test engine-systems`).

## Persistent rooms and Objects (24 October 2026)

- **A persistent Object** (Persistent ticked in the Object editor) goes with the game from room to
  room with its scripts and variables, as before. Now, when the game comes back to the room that
  placed it, the room does not make a second one: the one kept is that room's Object still.
- **A persistent room** (Persistent in the Room's settings, which the game used to ignore) comes
  back as the game left it: each Object it placed is where it was with its script's variables
  (its Create event runs, then the variables it had are put back), and anything a script
  destroyed there stays gone. A room that is not persistent starts as it was made, as before.
  Since 25 October the Objects a script made there (`CreateInstance`: a dropped sword, a placed
  campfire) come back too, where they were with their variables; one made in a Create, Room Start
  or Game Start event is left to that event, which makes it again, so nothing comes back twice.
- **Streamed Objects** in any room come back as they left as well (see
  [Large worlds](LargeWorlds.md)). Both are held for the game's session in one place:
  `Engine.Room.Streaming.Remember = false` turns them off, `Engine.Room.Streaming.Forget()` gives
  every room back as it was made, and a new game forgets them.
- **In a save** (25 October): `Engine.Room.Streaming.Save()` gives everything the rooms remember as
  text, the room the game is in taken as it is now, for the game to keep in its save slot;
  `Engine.Room.Streaming.Load(text)` puts it back, and each room comes back as saved the next time
  the game enters it:

```gml
SaveSlotWrite("slot1", Engine.Room.Streaming.Save());   // with the game's own values in a slot of their own
// ... and on loading:
if (Engine.Room.Streaming.Load(SaveSlotRead("slot1"))) RoomGoto(savedRoom);
```

Checked by `Engine.World.Rooms.PersistentRoomsAndObjectsComeBackAsLeft` and
`Engine.World.Scenery.StreamedObjectsComeBackAsTheyLeft` (`--test large-world`).

## Depth of field (24 October 2026)

`Engine.Camera.Focus.Enabled = true` keeps what is near the focus distance sharp and blurs nearer
and farther things the more the farther they are from it, for cutscenes, aiming down sights or a
photo mode. `Engine.Camera.Focus.Distance` (10 metres unless set), `Range` (4 metres either side
stays sharp), `Blur` (the strongest blur, 8 pixels of a 1080-line picture) and `Auto` (focus on
whatever is in the middle of the screen). Off unless a game turns it on; a sharp thing in front
never smears over a blurred background or the reverse. DX11, DX12, Vulkan and OpenGL; the
Software renderer draws without it.

## Camera rigs: shots that blend, and dolly tracks (29 October 2026)

A game names its shots from script and switches between them: `Engine.Camera.Rigs.Create(name,
kind)` makes a rig (`fixed`, `follow`, `orbit`, `shoulder`, `lookat` or `dolly`), `Engine.Camera.Rig.*`
sets it up (its target, offsets, field of view, damping, angles, arm), and `Engine.Camera.Rigs.Active
= name` or `BlendTo(name, seconds, curve)` makes it drive the camera, blending position, rotation
and field of view over the time given along a cut, linear or eased curve. A dolly rides a track of
points or a Pathing resource's route, set by a 0 to 1 position or at a speed, looking at its target
or along the track. The active rig is applied last in the frame, after the scripts and the orbit
camera; with none active the camera is exactly as it was (the orbit, shoulder and collision cameras,
the FirstPerson recipe, a script's own camera). The rigs belong to the room. In a room drawn
through several 3D views each view keeps its own camera. The F6 world values show
`Camera.Rigs.Active` once a room has rigs. Recipe: Cookbook.md, "Camera rigs". Checked by
`Runtime.Camera.Rigs.*` (`--test camera-rigs`, also in `engine-systems`).

## Heads that look and hands that reach, on any model (29 October 2026)

Any instance with a skinned model can turn its head to a point or another instance and reach a hand
to one, on top of whatever poses it (a clip, a clip on part of the body, an Animation Controller):
`Engine.Animation.LookAt.Follow(other, "Head")` or `.At(x, y, z)`, `Engine.Animation.Reach.At(...)`,
or `AnimationLookAtPoint`, `AnimationLookAtInstance`, `AnimationReachToPoint(bone, ...)`,
`AnimationReachToInstance(bone, ...)`. A look turns as far across and up or down as its limits allow
(`YawLimit` 70 and `PitchLimit` 40 degrees unless set) by its `Weight`; each blends in, out and to a
new weight over its `BlendTime` (0.25 s unless set) and keeps going until stopped. A target instance
is followed wherever it goes (one of its bones or sockets, or its position raised by `Height`). From
C#: `ModelInstance.SetBoneLookAt(world, entity, bone, point or entity, ...)`, `SetBoneReach`,
`ClearBoneLookAt`, `ClearBoneReach`. An instance that never asks is posed exactly as before; one that
does is drawn with a pose of its own while a look or reach is on, and shares the clip's again after.
The older `AnimationBoneLookAt` and `AnimationBoneReach` (a point every frame, on a controller's
bones) are unchanged. Recipe: Cookbook.md. Checked by `Runtime.Animation.LookAt.*` and
`Runtime.Animation.Reach.*` (`--test look-and-reach`, also in `engine-systems`).

## Run This Resource, and a check before export (24-25 October 2026)

**Run This Resource** (Shift+F5, File menu, or the command palette) runs the resource selected in
Assets (while Assets has focus) or the one open in front, as that resource's own sandbox: a Room runs
as the game starting in that room (as the Room editor's ▶ Play), an Object in its live sandbox (as F5
in the Object editor), a sound plays, a model's clip plays, a terrain's preview runs. An editor joins
in by implementing `IRunnableResource`. **Export Game** runs the project's checks, the strict script check and a reading of every room
(each loads, and every Object and Terrain it places exists) first: problems are listed in the Console and the export goes ahead only when you say so.

## The game's version, and crash reports a player can send (24 October 2026)

Preferences, Project page, Release: **Game version** ("1.0" unless set), saved in the project. A
game reads it as `Engine.Game.Version` and its name as `Engine.Game.Name`. When a game stops on an
error, the Player writes a report named by the time of the crash (`crash-20261024-153012.txt`, also
added to `GenesisEngine.crash.log`) beside the game, or in `%LOCALAPPDATA%\Genesis\CrashReports` when
the game's folder cannot be written, with the game and
its version, the engine build, the renderer, the room, the machine and the last 40 lines of the
game's log, and its error message says where the report is.

## Every area in parts, each value with a default (24 October 2026)

The engine API is `Engine.Area.Part.Value`: an area (Display, Camera, Rendering, Lighting, Sky,
Audio, Physics, Input, Game...) has parts, and each part has `Enabled` and its values. Every value
starts at a default (given in its description) and keeps what a script sets; a part turned on turns
its area on, and an area turned off comes back with its parts as they were.

```
Engine.Display.Window.Centre();            // window management
Engine.Display.Window.AlwaysOnTop = true;
Engine.Display.Monitor.Index = 1;          // move to the second monitor
Engine.Camera.Fov = 75;
Engine.Camera.Orbit.Distance = 4;          // this instance's third-person camera
Engine.Rendering.Fog.Enabled = true;
Engine.Rendering.Fog.Color = "#a0c8f0";
Engine.Rendering.Shadows.Strength = 0.7;
Engine.Lighting.Sun.Pitch = 15;            // evening
Engine.Lighting.Toon.Enabled = true;       // turns Engine.Lighting on too
Engine.Sky.Time.OfDay = 18.5;
Engine.Audio.Music.Enabled = false;        // keeps the music volume for later
Engine.Physics.Gravity.Strength = 3.7;
Engine.Game.Paused = true;
```

Each one is also a room or Object Engine setting in the Inspector. `Documentation/Design/EngineApiAreas.md`
lists every area and part, what is built and what can be added; `Documentation/EngineApi.md` lists
every name with its default.

## Frame work on every core (24 October 2026)

The culling of a frame's 3D Objects (their bounds, the view, what hides them, whether they are too
small to see) is shared between the engine's worker threads; what is drawn, and in what order, is
the same as on one thread. Nothing to set up. `Engine.Game.Threads` reads how many threads the
engine shares its frame work between (every core but two unless set); `Engine.Game.Threads = 1` keeps
it all on the game's thread, and 0 goes back to automatic. It lasts until the game ends.

| Benchmark scene (uncapped) | Before | After |
|---|---|---|
| Objects 3D (4096 crates) | 2.99 ms, 334 fps; Object draws 1.77 ms | 2.40 ms, 417 fps; Object draws 1.22 ms |
| Complex Scene | 3.76 ms, p95 8.0; Object draws 1.00 ms | 3.68 ms, p95 5.17; Object draws 0.55 ms |

## Reflections and bounced light, automatically (24 October 2026)

In a running game's 3D rooms (not Studio's editor views, whose scene changes under a still camera,
so they keep the flat look) the engine captures what is around the camera into a light probe: six small views
from one point, with the sky filled in where nothing was drawn. It does this when the room starts,
when a script calls `Engine.Lighting.Probe.Recapture()` (after a big change, such as a door opening),
when the camera has travelled 40 metres from the last capture, and when the light has changed (the sun
turning about 5 degrees through the day, or its light or the ambient colours changing). Those last
two recapture one view a frame while the old probe stays in use, so they never cost a long frame.
Nothing needs placing.

- **Reflections.** Smooth and metal surfaces mirror what is around them; rougher ones show it blurred.
  A room that set no environment reflection gets 1; one that set its own keeps it.
- **Bounced light.** Every surface is lit by the light arriving from each side (the sky where nothing is
  in the way, the sunlit walls and ground where something is) in place of the room's two flat ambient
  colours. A white wall beside a red one turns pink; a room's shaded sides are lit by its floor.

Lighting is an area with parts, each with `Enabled`. Turning a part on turns its area on
(`Engine.Lighting.Bounce.Enabled = true` sets `Engine.Lighting.Enabled` too); turning the area off
turns everything in it off, and its parts come back as they were when it is turned on again. The
same rule holds for every `Engine.Area.Part.Enabled`.

| Value | What it does |
|---|---|
| `Engine.Lighting.Enabled` | The room's 3D lighting, with every part below |
| `Engine.Lighting.Bounce.Enabled` | Bounced light; on in each room unless turned off |
| `Engine.Lighting.Bounce.Strength` | 0 (the room's flat ambient colours) to 1 (all bounced light; unless set) |
| `Engine.Lighting.Reflections.Enabled` | Reflections; on in each room unless turned off (off also turns the room's own environment reflection off) |
| `Engine.Lighting.Reflections.Strength` | 0 to 4; 1 (physically balanced) unless the room set its own environment reflection |
| `Engine.Lighting.Probe.Quality` | 1 to 3: 32, 64 (unless set) or 128 pixels a side |
| `Engine.Lighting.Probe.Distance` | Metres the camera travels before a recapture, 5 to 1000 (40 unless set) |
| `Engine.Lighting.Probe.Radius` | Metres around a capture drawn into it wherever the camera looks, 10 to 2000 (80 unless set) |
| `Engine.Lighting.Probe.Recapture()` | Capture again at the next frame |

Each value is a room Engine setting in the Inspector, and the Enabled and Strength values are world values on the F6 debug screen. The
Software renderer keeps the flat ambient colours. `GENESIS_LIGHT_PROBES=0` turns it off for a whole
run, to compare with the old look. One probe lights the whole view, so light from a wall right
behind the camera reaches everything in front of it.

## From C# and from events

Every feature here can be reached from an Object's events (the commands in the tables) and from a
C# behaviour:

- A command that does not act on "this instance" is an ordinary static method. A behaviour calls
  `PgslCommands.GamepadCheck("A")`, `PgslCommands.SaveSlotWrite("Quick", json)` or
  `PgslCommands.NetReceive(1)` (namespace `Genesis.Runtime.Scripting`).
- What acts on one model is on `ModelInstance` (namespace `Genesis.Runtime.Modeling`), which takes
  the world and the entity: `ModelInstance.PlayLayer(World, Entity, "Strike", "Spine")`.
- Sound is on `Game.Audio` (`IAudioSystem`): `PlayAt`, `FadeChannel`, `SetChannelPitch`,
  `SetChannelBus`, `SetBusVolume`.
- `Game.TryGetSocketWorld(entity, "RightHand", out matrix)` and
  `Game.PlayParticleBurst(asset, position)` are on the game context itself.
- The HUD canvas passed to `OnDrawHud` has `MeasureText`, `Circle`, `Arc` and `Polygon`.

The Player sets the static commands' game and project when it starts a game, so they work from a
game written entirely in C#. Before, only an Object's event set them, and a C# game had to assign
`PgslCommands.ActiveGameContext` and `PgslCommands.ProjectPath` itself. Doing so is now harmless
and unnecessary.

## Weather the engine draws

A 3D room with a dynamic sky can draw its own rain, snow and lightning from the weather it already
has. Turn on **Draw rain, snow and lightning** in the Room editor under **Lighting & atmosphere**
(`weatherEffects` in the room file's `environment`). It is off by default.

| Part | Behaviour |
|---|---|
| Rain and snow | One particle emitter follows the camera and uses the built-in `builtin://Rain` or `builtin://Snow` effect. How much falls follows the local rain, snow or hail the climate reports, so it thickens and thins with the weather. |
| Wind | The rain and snow drift on the wind the climate reports and lean with it: a thunderstorm's rain comes down at a slant. Rain is carried at no more than 7.5 m/s and snow at no more than 2.5 m/s, and the shower starts upwind of the camera so that it stays around it. |
| Lightning | In a thunderstorm the scene is lit by two quick pulses over 0.45 seconds at each strike. |
| Thunder | Set **Thunder audio** (`thunderAudio`) to a sound. It plays after each strike, later for a more distant one (343 m/s). The soundscape's `thunder` level scales it. |

**The weather's values from a script (24 October 2026).** `Engine.Weather.Wind` (Enabled,
Strength in m/s, Direction in degrees), `Engine.Weather.Rain` (Enabled, Amount, Wetness),
`Engine.Weather.Snow` (Enabled, Amount, Cover), `Engine.Weather.Lightning` (Enabled, Amount) and
`Engine.Weather.Temperature.Celsius`. In a room with a dynamic sky (the only rooms with weather),
each reads the weather's own value until a script sets it,
then holds there whatever the weather does, for the rain, snow, wind and lightning drawn here and
for everything else that reads the climate; a part turned off holds its amount at 0 and keeps
what was set. `Engine.Weather.Release()` hands them all back to the weather, as does the next
room. Wetness and Cover set how wet and how snowy surfaces are now; rain, snow and warmth carry
on from there. They are not kept in save files: a game that holds them sets them again after a load.

```gml
Engine.Weather.Preset = "thunderstorm";
Engine.Weather.Rain.Amount = 0.3;          // a storm, but only a light rain
Engine.Weather.Wind.Direction = 90;        // blowing towards +X
Engine.Weather.Snow.Enabled = false;       // never snow in this valley
```

Script commands:

| Command | Meaning |
|---|---|
| `WeatherLightningFlash()` | 0 to 1: the light a strike is adding right now. |
| `WeatherLightningStrikes()` | How many strikes since the room began. Compare with the last value to react to one. |
| `WeatherStrikeLightning(distanceMetres)` | Strike now at this distance, whatever the weather. |

A particle effect that follows the camera now falls from just above the camera
(`FollowCameraHeight` in the effect, 8 m by default) instead of from height 0 in the world, so
weather is seen on a mountain as well as at sea level.

Fixed with this: a particle effect whose centre was behind the camera was not drawn at all, even
when most of it was in view. An effect around the camera, such as rain, was invisible because of
it. The built-in rain is also brighter and denser than it was: 2600 drops a second within 11 m of
the camera, where it was 900 within 14 m.

Any emitter can be given a wind of its own: set `Wind` on its `ParticleComponent` (metres a
second along X and Z). Left unset, an effect drifts on the wind it was authored with. A streak
that is drawn along its motion (rain, sparks) now lies along its real path, wind included.

## Water with its own look

A terrain's water can have its own colours instead of the engine's. In the Terrain editor's water
dialog, the **Appearance** tab has **Use these colours**, a shallow colour, a deep colour, opacity,
the depth at which the water stops being clear, and the width of the foam at the shore. With the
box off the water looks as it did.

## Animation events

A script can be told the frame a model's clip passes a point, such as the moment a blow lands.

| Command | Meaning |
|---|---|
| `ModelAnimationCrossed(fraction)` | True in the one frame the active clip passes this point (0 is its start, 1 its end). Asking twice in a frame gives the same answer. |
| `ModelAnimationGetTime()` | Seconds the active clip has been playing. |
| `ModelAnimationGetLength(clip)` | Length of a clip in seconds; 0 if the model has no such clip. |
| `ModelAnimationGetProgress()` | 0 to 1: how far through the active clip. |

While game time is held still (`GameSetSpeed(0)`) no event is reported.

From C#, `ModelInstance.Crossed(world, entity, fraction)`, `Progress`, `Finished` and
`ClipLength` do the same for any model, and `Crossed` also works for a clip played backwards.

## A model in the world: bones, part-body clips, tint and light

`ModelInstance` is what a script may ask of one model. Nothing in it changes the Model resource.

| Call | Meaning |
|---|---|
| `TryGetSocketWorld(world, entity, name, out matrix)`, `TryGetSocketPosition` | Where a socket is in the world, in the pose the model is drawn in. A bone's name works when the model has no socket of that name. It is the same place an attached Object is put. Events: `ModelSocketX(name)`, `ModelSocketY`, `ModelSocketZ`, `ModelSocketExists`. |
| `Play(world, entity, clip, loop, blendSeconds, startSeconds, speed)` | Play a clip on the whole model, blending from what it was playing, starting part of the way in if asked. |
| `PlayReversed(world, entity, clip)` | Play a clip from its end to its start: a weapon put away with the clip that draws it. |
| `PlayLayer(world, entity, clip, fromBone, loop, fadeSeconds, speed)` | Play a clip on one bone and everything below it while the rest of the body carries on: a strike on `"Spine"` while the legs walk. It fades in, and a clip that does not loop fades out as it ends and lets go. `StopLayer` lets go early; `LayerPlaying` and `LayerCrossed` report on it. |
| `SetTint(world, entity, colour)` | Multiply the whole model's colour. An alpha below 1 fades the model. Events: `ModelSetTint(r, g, b, a)`. |
| `SetGlow(world, entity, amount)` | Add the model's own colours on top of its lighting: 0 none, 1 fully self-lit. Raise it for a few frames for a hit flash, or hold it for a selection. Its shadow is kept. Events: `ModelSetGlow`. |
| `SetEmissionScale(world, entity, scale)` | Scale the light the model's materials give off as authored: 0 puts its lamps out. Events: `ModelSetEmissionScale`. |
| `SetMaterialEmission(world, entity, material, strength)` | Make one material give off light whatever it was authored with: window glass at dusk. A negative strength gives it back its authored light. Events: `ModelSetMaterialEmission`. |
| `SetMaterialEmission(world, entity, material, strength, colour)` | The same, in a colour: glass that glows warm whatever colour it is by day. The material gives off its own colour multiplied by this one, so the colour also tints the material while it is set. A negative strength gives back both. Events: `ModelSetMaterialEmissionColor(material, strength, r, g, b)`. |
| `SetMaterialTint(world, entity, material, colour)` | Multiply one material's colour on this instance; null gives it back. Events: `ModelSetMaterialTint`. |
| `SetMaterialEmissionTint(world, entity, material, colour)` | Colour the light one material gives off on this instance: its emission image and its own glow are multiplied by the colour, its surface is not (1, 1, 1 is as authored; null, or a negative r from a script, gives back the material's own light colour). Events: `ModelSetMaterialEmissionTint(material, r, g, b)`, `InstanceSetMaterialEmissionTint(id, material, r, g, b)`. |

**A material's light colour (15 October 2026).** A model material has a **Light colour**
(`EmissiveColor`, white by default, so every model looks as before) that multiplies the light it
gives off: its emission image and the glow of its `EmissiveFactor`, but not its surface colour nor
a glow a script adds over the whole model. One grey glow pattern can then serve every glow colour.
It is set in the Model editor's MATERIAL & PBR panel (Light colour `…`, `×` for white) and saved
with the model, and per instance by `ModelSetMaterialEmissionTint`. It is drawn by the shared
forward shader on Direct3D 11 and 12, Vulkan and OpenGL; the Software renderer draws no emission
images and ignores it. `ModelSetMaterialEmissionColor` keeps what it did (a tint on the whole
material, which colours only its base-colour glow).

**Box UVs in metres for meshes without UVs (15 October 2026).** A material can ask for box UVs at
a real size: the Model editor's MATERIAL & PBR panel has **Box UVs (m)** (0, the default, is off),
saved with the model as the material's `BoxUvMetres`, or a model's descriptor can name it per
material (`"materialBoxUvMetres": { "Print band": 1 }`), which a re-import keeps. Each face of the
material's meshes takes the two model axes across its normal, one repeat of the images every so
many metres, so a 1 m brick set lies at 1 m on a 10 m wall where Quick UV Unwrap would stretch it
over the wall's bounds. It is applied when the model is loaded, on every renderer. The metres are
the model's own (an Object's scale stretches them), and skinned meshes and meshes whose triangles
use materials with different repeats keep their UVs.

Two things to know:

- **A clip asked to play once now holds its last frame.** A clip imported with its loop flag on
  (the default) used to start again whatever the instance said. `Play(..., loop: false)` and
  `PlayReversed` hold. A clip started by writing `ClipName` on the component directly behaves as
  it did.
- **A part-body clip is for models played by clip name.** A model driven by an
  `AnimationController` has layers and bone masks of its own and ignores `PlayLayer`. Copies made
  for other players in multiplayer do not show the layer.

A glow or a material's light adds the surface's own colour, so a black material stays black. A
material that gives off light casts no shadow, as before; a glow leaves the shadow alone.

## Controllers and the mouse wheel

Scripts can read a controller directly. Before this, a controller could only act as the keyboard
and mouse.

| Command | Meaning |
|---|---|
| `GamepadConnected()` | True while a controller is plugged in. |
| `GamepadCheck(button)` | True while a button is held. |
| `GamepadPressed(button)`, `GamepadReleased(button)` | True in the frame it goes down, or comes up. |
| `GamepadAxis(axis)` | `"LeftX"`, `"LeftY"`, `"RightX"`, `"RightY"` from -1 to 1 (right, and away from you, are positive); `"LeftTrigger"`, `"RightTrigger"` from 0 to 1. Stick movement under 0.18 reads as 0. |
| `GamepadVibrate(low, high, seconds)` | Run the heavy and the light motor, each 0 to 1, for up to 10 seconds. |
| `GamepadKeyboardEmulation(enabled)` | Whether the controller also presses keys. |
| `MouseWheel()` | How far the wheel turned this frame. |

Button names: `A`, `B`, `X`, `Y`, `LB`, `RB`, `LT`, `RT`, `L3`, `R3`, `Start`, `Back`, `DPadUp`,
`DPadDown`, `DPadLeft`, `DPadRight`. The triggers count as buttons once pulled past half way.

The controller still presses keys as it always did unless a script turns that off: A is Space, the
stick clicks are Control and Shift, the triggers are the left and right mouse buttons, and the
right stick turns a captured view. A game that reads the controller itself should call
`GamepadKeyboardEmulation(false)` so one press is not two actions.

### More than one controller (15 October 2026)

Up to four controllers are read, for games two or more people play on one machine. Controller 0 is
the one the commands above read, exactly as before; the next ones plugged in are 1, 2 and 3, in
the order Windows lists them.

| Command | Meaning |
|---|---|
| `GamepadCount()` | How many controllers are plugged in. |
| `GamepadConnectedAt(index)` | True while controller 0 to 3 is plugged in. |
| `GamepadCheckAt(index, button)`, `GamepadPressedAt(index, button)`, `GamepadReleasedAt(index, button)` | A button on that controller, with the same names. |
| `GamepadAxisAt(index, axis)`, `GamepadAxisRawAt(index, axis)` | A stick or trigger on that controller, with the same names, directions and dead zones. |
| `GamepadVibrateAt(index, low, high, seconds)` | Run that controller's motors. |

`GamepadCheckAt(0, "A")` is `GamepadCheck("A")`. Only controller 0 presses keys; the dead zones
set with `GamepadSetDeadZone` apply to all four. A recording of input (below) holds controller 0
only.

### Actions a player can rebind (19 October 2026)

A script can ask for a named action instead of a key, so a settings screen can change what the
action means:

```
// Create of the game's controller object: the defaults, then whatever the player chose before.
InputActionBind("Jump", "Space");
InputActionBind("Jump", "Pad:A");
InputActionBind("Fire", "Mouse:Left");
InputActionsLoad();

// Step of the player.
if (InputActionPressed("Jump")) { jumping = true; }
x = x + InputActionAxis("Left", "Right") * 4;
```

A binding is a key by the name `KeyCheck` takes (`"Space"`, `"W"`, `"Key:W"`), a mouse button
(`"Mouse:Left"`, `"Mouse Right"`) or controller 0's button by the names `GamepadCheck` takes
(`"Pad:A"`, `"Pad LB"`, `"Gamepad Start"`), or a way a stick is pushed or a trigger pulled (`"Pad:LeftStickLeft"`, `"Pad:RightStickUp"`, `"Pad LeftStick Down"`, `"Stick:RightTrigger"`, held from half way); an action can have several, so `"Left"` can be A, the left arrow and the stick at once. `InputActionCheck`,
`InputActionPressed` and `InputActionReleased` answer for any of them, and `InputActionAxis(negative,
positive)` goes from -1 to 1 (smooth with a stick, -1, 0 or 1 with keys); `InputActionValue(action)` is how strongly one is held, 0 to 1. Pushing a stick past half way presses an action, and letting it back releases it, so menus can step with the stick. A rebinding prompt also takes a stick push. For local multiplayer, put the controller's number after `Pad` or `Stick`: `"Pad2:A"`, `"Gamepad3 LB"`, `"Pad4 LeftStick Up"` are controllers 2 to 4 (plain `Pad` is the first); a rebinding prompt binds whichever controller was pressed, and the number is saved with the binding. A key can need modifiers held: `"Ctrl+S"`, `"Shift+Tab"`, `"Alt+Enter"`; a rebinding prompt takes Ctrl then S as Ctrl+S, and a modifier pressed and let go on its own as itself (Shift for sprint).

To rebind, `InputActionListen("Jump")` waits for the next key, mouse button or controller button
pressed, which replaces the action's bindings (`InputActionListen("Jump", false)` adds it instead);
Escape gives up. Call `InputActionListening()` every frame while the prompt shows: it is true while
it still waits. `InputActionBindingCount` and `InputActionBindingName(action, index)` ("W",
"Mouse Left", "Pad A") fill the settings screen. `InputActionsSave()` keeps every action in the
game's saves (`input-bindings.txt`, or a name given), and `InputActionsLoad()` reads it back:
actions it names are rebound, actions it does not name keep the bindings the game gave them, so an
action added in an update keeps its default.

### Analogue actions, each controller's own bindings, chords and the rebinding screen (29 October 2026)

```
// Create of the game's controller object.
InputActionBind("Steer", "Pad:LeftStickX");      // a whole stick axis: -1 to 1
InputActionBind("Steer", "Right");               // a key still reads 1 through the same call
InputActionSetDeadZone("Steer", 0.15);           // the stick counts from 15% of its travel
InputActionSetSensitivity("Steer", 1.5);         // and reaches full value sooner
InputActionBind("Throttle", "Axis:RightTrigger"); // a trigger: 0 to 1
InputActionBind("Special", "LB+A");              // a chord
InputActionsLoad();

// Step.
x = x + InputActionValue("Steer") * 4;
if (InputActionPressed("Special")) { special(); }

// The pause menu's Controls button.
InputRebindingOpen();
```

- **Values.** An action bound to a whole axis (`"Pad:LeftStickX"`, `"Pad:LeftStickY"`,
  `"Pad:RightStickX"`, `"Pad:RightStickY"` from -1 to 1, right and up positive on both sticks;
  `"Axis:LeftTrigger"`, `"Axis:RightTrigger"` from 0 to 1; `"Axis2:..."` for controller 2) reads that
  value from `InputActionValue`, keeping its sign; keys, buttons and chords read 1. Each action has its
  own dead zone (0 to 0.95, 0 unless set: the travel inside reads 0 and the rest is spread over 0 to 1)
  and sensitivity (0.01 to 10, 1 unless set; values stay within -1 to 1), which change sticks and
  triggers only. An axis counts as held past half way, as a stick direction does.
- **Each controller's own bindings** (local multiplayer). `InputActionCheckPad(2, "Jump")`,
  `InputActionPressedPad`, `InputActionReleasedPad` and `InputActionValuePad` read an action on one
  controller (1 to 4): with its own bindings when it has some (`InputActionBindPad(2, "Jump", "Pad:B")`,
  `InputActionListenPad(2, "Jump")`), otherwise the shared controller bindings read on that
  controller, so every player has the game's layout until they change theirs. Keys and the mouse are
  not a controller's. `InputActionClearPad` unbinds an action on one controller, `InputActionResetPad`
  gives it the shared ones back. The bindings file keeps a controller's own under `[Pad 2]`; files
  saved before load as they always did.
- **Chords.** `"LB+A"`, `"Pad2:LB+A"`, `"Q+E"`, `"Shift+LB"`: two to four parts held together. A chord
  is held while every part is and pressed once each time the last part completes it, in any order;
  `A` bound to another action still presses that one too. `"Ctrl+S"` stays a key with a modifier, as
  before. A bare part is a key when it is one, otherwise a controller button; after a controller part,
  a bare part is on the same controller.
- **Putting the game's bindings back.** `InputActionsReset()` restores what the game bound before the
  player changed anything (rebinding, loading, a controller's own) and drops every controller's own.
- **The rebinding screen.** `InputRebindingOpen(pause?, theme?)` (`Engine.Input.Rebinding.Open()`)
  shows every action and its bindings over the game. Choosing an action waits for its new key or button
  (Escape gives up); **For** switches between everyone's bindings and controller 1 to 4's own; **Reset
  to defaults**, **Save** (`input-bindings.txt`, as `InputActionsSave`) and **Back** (or Escape, or B)
  return to the game. The keyboard, mouse and a controller all work it. It pauses the game and stops
  the game's actions reading while it is open (`pause` false leaves the game running), and puts both
  back when it closes; `InputRebindingIsOpen()` / `Engine.Input.Rebinding.Showing` say whether it is
  open, `InputRebindingClose()` closes it, and `Engine.Input.Rebinding.Enabled = false` keeps it shut.
  It is a User Interface drawn by the UI toolkit under the name `Engine.Input.Rebinding`, so
  `UiSetTheme`'s rules apply: name a User Interface as `theme` and its styles **Backdrop**, **Window**,
  **Title**, **Text**, **List** and **Button** replace the screen's own.
- **Engine API.** `Engine.Input.Actions.Enabled` (true unless set), `.SetDeadZone(action, amount)` /
  `.DeadZone(action)`, `.SetSensitivity(action, amount)` / `.Sensitivity(action)`, `.Reset()`;
  `Engine.Input.Rebinding.Open()`, `.Close()`, `.Showing`, `.Enabled`. A game that uses none of these
  reads its actions exactly as before. One change for every game: with keyboard emulation on, a
  rebinding prompt takes a controller button pressed in the same frame as the key it emulates as the
  button (A binds Pad A, not Space).

## Routes that prefer roads (19 October 2026)

A navigation grid (`NavMeshBake`, `NavMeshBakeGrid`) can make some ground cheaper or dearer to cross:
`NavMeshSetAreaCost(x1, z1, x2, z2, cost)` sets every cell whose centre lies in the rectangle (in a
2D room z is y) to a cost from 0.05 to 1000, where 1 is normal ground. A road at 0.3 draws routes
onto it when the detour is worth it; mud at 4 is walked through only when going round costs more;
the route's smoothing never cuts back across ground dearer than the route itself crossed.
`NavMeshGetAreaCost(x, z)` reads a cell's cost back. Costs are not saved with the grid: set them
after baking or after the room loads (its Create event), and routes found before keep their way.

### Floors, sizes and obstacles (22 October 2026)

In a 3D room, `NavMeshBake` also makes floors on the tops of static boxes an agent fits on, such
as a bridge deck, an upper storey or the steps of a stair. Ground under something with headroom
(1.8 m) stays walkable, so routes go under a bridge or up its stairs onto it, whichever way is
asked for. A box low enough to step onto (0.45 m) raises the ground to its top. An agent stands on
the floor under its feet, the highest no more than a step above it. The mesh file keeps the floors
(version 2). 2D rooms bake one floor, as before.

`NavMeshAgentSetRadius(radius)` makes an agent wider than the bake's 0.35, and its routes keep
that much further from walls and edges, so a cart or a giant takes the wide way round. One bake
serves every size. `NavMeshPathFind(..., radius)` does the same for a route a script follows.
`NavMeshAddObstacle(x1, y1, z1, x2, y2, z2)` blocks the floors an agent would touch a box on
(a cart parked, a door shut, a fallen tree), and returns its id. Routes found from then on go
round it. `NavMeshRemoveObstacle(id)` takes it away again. Routes found before keep their way,
as with costs and links. Studio's pathing previews show the ground floor.
### Jumps, gaps and ladders

`NavMeshAddLink(x1, y1, z1, x2, y2, z2)` joins two points that walking cannot join, such as a ledge
to jump down from, a gap to leap, a ladder or a door, and returns the link's id. Routes take a link
when it is the cheaper way. `NavMeshAddLink(..., false)` makes it one way, for a drop that cannot be
climbed back. `NavMeshAddLink(..., true, cost)` gives it a cost: by default the cost is the link's
length, and more makes routes walk round unless the walk is longer.

An agent crosses a link in a straight line from one end to the other, height included, and nothing
in the way stops it. `NavMeshAgentGetLink()` says which link it is on (0 while it walks), and
`NavMeshAgentGetLinkProgress()` says how far across it is, from 0 to 1, for a jump's arc or a climb
animation's time. A script that follows its own route reads
`NavMeshPathGetWaypointLink(path, index)`, which gives the link from that waypoint to the next.
`NavMeshRemoveLink(id)` takes a link away. Like costs, links are not saved with the grid. In a 2D
room, x and y are the point and z is not used.

## Ground from a model, and water that keeps its shape (22 October 2026)

A ground made outside the Terrain editor (an imported scene, a hand-made landscape) becomes a real
Terrain with `terrain.fromModel`: the heights of the surfaces chosen by material, and a paint layer
per material. `hideSurfaces` lists those materials in the Model's new `hiddenMaterials`, so the
model stops drawing the ground the terrain now stands in for (houses, props and trees stay). From
then on the ground has everything a terrain has: grass and scatter layers, painted layers with
their own pictures, paths and water. Water is added with `terrain.addWater`: lakes and ponds,
rivers that fall along their course, and the sea.

`WaterDepthAt`, `WaterSurfaceAt`, swimming and floating now follow each water's own outline
and surface. A river's surface is the height along its course where you stand, a round lake ends
at its shore rather than at the corners of a box around it, and where waters overlap the smallest
one counts, so a pond within the sea's reach is the pond.

Kinfield (Pennywick) was moved over this way: the ground, its stream, pond and sea, and its grass
tufts (scatter layers of its own grass models on the grass paint) are terrain, not models and a
script.
## Online games through a relay (22 October 2026)

Games on a local network find each other with `NetDiscover` and connect by address. Over the
internet, routers stand in the way, so games meet at a **relay**: the Player run as
`GenesisEngine.exe --relay 7777` on a machine both sides can reach (a server, or a PC with that UDP
port forwarded).

- `NetHostOnline("relay.example.com", 7777, maxPlayers)` asks the relay for a game.
  `NetSessionCode()` then gives a six-character code (letters and digits that cannot be confused)
  for the host to share, and `NetConnectState()` says `hosting`.
- `NetJoinOnline("relay.example.com", 7777, code)` joins with it. `NetConnectState()` says
  `connected`, or `failed` with `NetLastError()` giving the reason (`no such game`, `full`
  or `wrong version`; only games of the same title and engine version meet).

After that, everything works as it does on a local network: peers join and leave, and messages,
broadcasts and object replication (with its smoothing of what other players move) all pass through
the relay.

**Direct paths (29 October 2026).** Once a player has joined, the relay tells the host and the player
each other's internet address, and both try to reach each other directly (NAT punch-through): for a
few seconds each sends small packets to the other, which opens its own router for the reply. Most
home routers (full cone, restricted and port restricted) let the packets meet; then the two games'
messages and shared objects go straight between them, without the relay's distance. A router that
gives every destination a new port (symmetric, common on mobile networks and some offices) does not,
and the game stays on the relay as before. A direct path that stops working (a router restarts, a
connection changes) is noticed within about two and a half seconds and the traffic goes back through
the relay. Neither switch loses or reorders a reliable message; movement, which is sent unreliably,
simply carries on from the next update.

- `Engine.Network.Direct.Enabled` (true unless set): false keeps everything on the relay, and this
  machine's address is then never given to the other players.
- `Engine.Network.Direct.Active` reads `direct` when every player is reached directly, `relay`
  otherwise (and `""` when not playing through a relay); `Engine.Network.Direct.Path(peerId)` and
  `NetDirectPath(peerId)` give one player's path, `Engine.Network.Direct.Count` how many are direct.
- `Engine.Network.Direct.Attempts` (3) and `Engine.Network.Direct.Timeout` (5 seconds) say how often
  and how long to try. All three settings are also room settings in the Inspector.

The session still lives at the relay: if the relay goes, the game ends for everyone. Two players
behind the same router only reach each other directly if the router allows it; they can play as a
local game instead. The design, the frames and the tests are in `Design/Networking.md`.
## User Interface toolkit: lists, focus, typing and themes (22 October 2026)

A User Interface resource has two more element types, and `DrawUi`/`UiUpdate` handle them:

- **Scroll List**: a box whose children are stacked one after another and scroll inside it. The
  mouse wheel scrolls the list under the pointer, moving the focus scrolls it to keep the focused
  child in view, and `UiScrollTo(ui, list, offset)` and `UiGetScroll` set and read it. What is
  outside the list is clipped, and the pointer cannot reach it there.
- **Text Input**: a box the player types into once it has the focus, given by a click or by the
  keys or controller. Backspace deletes, `MaxLength` limits the length, and `Placeholder` shows
  a grey hint while it is empty. `UiGetText(ui, box)` reads it, `UiValueChanged` reports typing,
  and `UiSubmitted(ui, box)` reports Enter once. Its letters are not shortcuts while it is typed
  into.

**Layout**: a Panel or Scroll List with `Layout` Vertical or Horizontal places its children one
after another, in their `Order`, with `Spacing` between them and `Padding` inside its edges.
The children's own X and Y are ignored, and a child anchored Stretch fills the width (or the
height). Hidden children keep their place. **Padding** also takes the text of a Text, Button, Toggle
or Text Input element in from its box on every side (a Text Input keeps its own 8 pixels besides);
with no Padding text sits where it always has. The UI Editor's Inspector has rows for `Layout`,
`Spacing`, `Padding`, `Style`, `Placeholder` and `MaxLength`, and its canvas lays children out as the game does.

**Keys and controllers**: the arrow keys, any controller's d-pad and its left stick move the focus
to the nearest button, slider, toggle or text box that way, and the focus takes its Selected look.
Enter, Space or A press the focused element. Left and right move a focused slider by its `Step`
(5% of its range without one). A click moves the focus too. `UiGetFocused(ui)` reads it, and
`UiSetFocus(ui, id)` sets it, for example to open a menu with its first button ready.

**Themes**: the resource can hold named `Styles` (background, gradient, text and accent colours,
border colour, corners, font and size). An element whose `Style` names one takes those values in
place of its own. `UiSetTheme(ui, otherUi)` draws a UI on this instance with another UI
resource's styles, such as a light or dark theme or a team's colours, and `UiSetTheme(ui, "")`
goes back to its own.
## Animation Controllers (22 October 2026)

An **Animation Controller** resource (`Animators` folder, `.animator.json`) holds how a
character's animations follow one another: its states, the transitions between them and the
parameters scripts set. One controller serves every Object that uses it, and each instance gets
its own copy of the state machine. A new controller starts with Idle, Move (a blend from Walk to
Run by `speed`) and Jump, a `speed` parameter, a `jump` trigger, and the transitions
between them.

In Studio's Animation Controller editor, states are boxes and transitions are arrows. Drag a state
to move it. Ctrl+drag from one state to another, or from "Any state", to make a transition.
Double-click empty space for a new state, and press Delete to remove the selection. The panel
beside the graph sets the selected state's clip, looping and blend (a parameter, and clip=value
pairs such as `Walk=1.5, Run=5`), or the selected transition's condition and blend time.
Conditions read as `AnimationStateAddTransition` reads them: a bool or trigger parameter
(`jump`), a comparison (`speed > 0.5`), `AnimationFinished` or `true`. The table on the
left lists the parameters and their defaults. Every change can be undone.

In a game, `AnimationControllerUse("Hero Animator")` builds the controller into the instance's
model, starting from its default state; `AnimationControllerError()` says why when it cannot.
Scripts drive it with `AnimationStateSetFloat`, `AnimationStateSetBool` and
`AnimationStateSetTrigger`, and `AnimationStateCurrent()` gives the state it is in.
## Heads that look at things (19 October 2026)

`AnimationBoneLookAt("Head", x, y, z)` turns a bone to face a point in the world after the animation
is blended: an NPC following the player with their eyes, a guard turning their head to a noise. It
turns at most 70 degrees (`AnimationBoneLookAt(bone, x, y, z, weight, maxDegrees)`; weight 0 to 1
blends it in), about the bone's own position, so the neck stays joined. Call it every frame while the
point moves; `AnimationBoneClearLookAt("Head")` lets the animation have the bone back. Turns are
measured from the way the model faces in its own space, +Z for most imported characters;
`AnimationSetLookForward(x, y, z)` sets another for a model built facing elsewhere. A neck can look
too, with a smaller weight, for a turn shared down the spine.

`AnimationBoneReach("Hand.R", x, y, z)` bends the two bones above a bone so that it reaches a point: a hand to a door handle or a lever, a foot onto a step. The bone lengths stay as they are, the limb keeps the way it already bends (elbows out, knees forward), and a point out of reach is reached towards with the limb straight; `AnimationBoneReach(bone, x, y, z, weight)` blends it in and `AnimationBoneClearReach(bone)` gives the limb back to its animation. Reaches are applied before look-ats, so a character can reach for something and look at it in the same frame. Both work with rigs that list their bones in any order.

Feet on uneven ground are a reach to the ground under the foot. `ModelSocketX/Y/Z` take a bone's name
as well as a socket's, so a Step event can find where the animation put a foot, ask the ground's
height there and plant the foot on it:

```
fx = ModelSocketX("Foot.L"); fy = ModelSocketY("Foot.L"); fz = ModelSocketZ("Foot.L");
if (PhysicsRaycast(fx, fy + 0.5, fz, 0, -1, 0, 1.2) >= 0) {
    AnimationBoneReach("Foot.L", fx, PhysicsRaycastHitY() + 0.08, fz, 1);
} else {
    AnimationBoneClearReach("Foot.L");
}
```

(`GetTerrainHeight(x, z)` gives the height on terrain without a ray; 0.08 is the ankle above the
sole, which depends on the model.) Do the same for the other foot, and lower the body by the
deeper of the two offsets when one foot steps down, so the other leg is not left straight.

## The game's text in several languages (19 October 2026)

Put each language's text in the project's `Assets/Text` folder, one file per language named by its
code (`en.txt`, `fr.txt`, `de.txt`), one line per piece of text:

```
# Title screen
title = Mushroom Meadow
start = Press Start
coins = You have {0} coins
credits = Made by\nCal
```

`LocalText("title")` gives the line in the chosen language. A key the language does not have falls
back to English (or, with no `en.txt`, the first language alphabetically) and then to the key
itself, so an untranslated line shows its key rather than nothing. `LocalTextWith("coins", n)` puts
values in for `{0}`, `{1}` and `{2}`. `\n` in a line is a new line, `\t` a tab; lines starting with
`#` are notes. The language starts as the player's own when the game has it, else English;
`LanguageSet("fr")` changes it (false when there is no `fr.txt`), `LanguageGet()` names it, and
`LanguageCount()` with `LanguageAt(i)` lists them for a language menu. Keep the choice with the
player's other settings (for example `FileWriteText`).

## Sound

| Command | Meaning |
|---|---|
| `PlaySoundAt(path, x, y, z, volume, pitch, loop)` | Play a sound at a place. It is quieter with distance, using the sound's own distance settings, and louder in the ear it is nearer to. Returns a channel. |
| `SoundSetPosition(channel, x, y, z)` | Move a playing sound. |
| `SoundSetVolume(channel, volume)`, `SoundSetPitch(channel, pitch)` | Change a playing sound. |
| `SoundFade(channel, volume, seconds)` | Move a playing sound's volume to a new level over a time. |
| `StopSoundFaded(channel, seconds)` | Fade to silence, then stop. |
| `SetBusVolume(bus, volume)`, `GetBusVolume(bus)` | Volume of a group: `"music"`, `"sfx"`, `"master"` or a bus of the project's own (`"ui"`, `"ambient"`; list them in the project file's `audioBuses` to offer them in the Audio editor). A sound longer than ten seconds is music unless its Audio resource says otherwise. The master volume now applies once: before, it was applied twice, so a master of 0.5 played at 0.25. |
| `SoundSetBus(channel, bus)` | Put a playing sound in a group whatever its length chose for it: a twelve-second wind loop belongs in `"sfx"`. |
| `AudioListenerFollowCamera()` | Give the listener back to the camera after `SetAudioListener`. |

Two defaults changed:

- **In a 3D room the listener is the camera.** It used to stay at the world's origin unless a
  script moved it with `SetAudioListener`, so a positioned sound (an Object's sound emitter set to
  spatial, for example) was as loud as its distance from the origin, not from the player. A script
  that calls `SetAudioListener` keeps control, as before. 2D rooms are unchanged.
- **A positioned sound is panned.** The nearer ear hears it at full level and the further ear at
  down to 15%. A sound ahead, behind, above or within arm's length is equal in both ears, so nothing
  is quieter than it was. On a surround device the front pair is used. Sounds that are not
  positioned are untouched.

Fades run in real time, not game time: music still fades while the game is held still.

A Player opened by a test or a tool (`GENESIS_UNATTENDED_WINDOW=1`) is silent, whatever volume the
game sets. `GENESIS_UNATTENDED_AUDIO=1` lets it be heard.

**Playing a sound costs no memory.** Every play used to make its own copy of the sound's samples
and never gave it back, so a game lost a sound's whole size each time it played it: a footstep's
10 KB twice a second, or 5 to 7 MB in every room for a game that restarts its music and ambience
at each door. Every play now reads the sound's one set of samples. Found by a game measuring its
own room changes with the memory line described under
[Finding what a game holds on to](#finding-what-a-game-holds-on-to).

**Ducking (19 October 2026).** `BusDuck("music", "voice", 0.6)` lowers the music bus to 40% while
any sound on the voice bus plays (put a line there with `SoundSetBus(channel, "voice")`), going down
over 0.15 s and back over 0.6 s; `BusDuck(bus, byBus, amount, attack, release)` sets both times,
and an amount of 0 turns it off. It multiplies the bus's own volume (`SetBusVolume`), so a player's
music setting still holds; `BusDuckLevel("music")` says how far down it is now.

**Music that cross-fades (19 October 2026).** `MusicPlay("Town Theme")` plays a track looping on the
music bus; the next `MusicPlay("Battle Theme", 1.5)` fades the first out and the second in over 1.5
seconds (default 1), and asking for the track already playing only moves it to the volume given
(`MusicPlay(path, fadeSeconds, volume)`). `MusicStop(seconds)` fades it out and stops it, and
`MusicCurrent()` names the track playing. Both return or use ordinary channels, so `SoundFade` and
`IsSoundPlaying` work on them too.

## HUD: measuring text, and shapes

The canvas passed to `OnDrawHud` gained:

| Call | Meaning |
|---|---|
| `MeasureText(text, size, font)` | The width and line height in pixels the text will be drawn at, from the font's own metrics. |
| `Circle(x, y, radius, colour, filled, thickness)` | A filled disc or a ring. |
| `Arc(x, y, radius, startDegrees, sweepDegrees, colour, thickness)` | Part of a ring: a cooldown sweep. Degrees run clockwise from three o'clock. |
| `Polygon(points, colour, filled, thickness)` | A filled shape of any outline, or its outline joined back to the start. |

Filled shapes are drawn as one rectangle for each row of pixels, so their edges are not smoothed.
A ring is one line for about every six pixels of its length.

## A glowing beam in 3D

`DrawBeam3D(x1, y1, z1, x2, y2, z2, width)` draws a thin beam of light between two points in a 3D
room, `width` metres across: tracers, a rail rifle's trace, a laser, a bolt of lightning (a few
beams along a jagged path). It is not lit and its colour is added to what is behind it, so it reads
as a streak of light and brightens the haze it crosses, where `DrawLine3D` is a chain of small lit
cubes that reads as a row of beads. The colour is the draw colour (`DrawSetColorRgb`) and its
strength the draw alpha (`DrawSetAlpha`, 0 to 1). Like every `Draw*3D` command it is called each
frame it should show, from a Draw event. C#: `IPgslDrawSurface.QueueBeam3D`.

## Particles: a burst that cleans up after itself

`Game.PlayParticleBurst(asset, position, scale, emitSeconds)` (events: `ParticleBurstAt`) plays a
Particle resource once and removes the emitter when the last particle has lived its life. An effect
that emits continuously is given `emitSeconds` (a quarter of a second if 0), stopped, and removed
when it has died away.

An emitter that is kept can fire its burst again: set `Restart` on its `ParticleComponent`.

An effect that follows the camera (`FollowCameraXZ`, as the built-in rain and snow do) ignores its
entity's position; that is what makes it weather. Any other effect is drawn where its entity's
`TransformComponent` is, read every update.

### Particle pictures from the Image library (15 October 2026)

A Particle resource's `texturePath` names its picture as an **Image resource**: by name, which is
what the Particle editor's Texture picker (Choose Image…) writes, or by the Image's id (the guid in
its `.meta`). The Image's first frame is drawn, from its cooked texture when it has one. Renaming
the Image in Studio rewrites the name in every Particle resource (an id needs no rewriting), and
Export Game ships the Image with the effect. A file path inside the project still works as before.
The same holds for 3D effects, `ParticleBurstAt` and batched bursts, the Particle editor's preview
and, newly, the 2D script effects (`Particle2DBurst`, `Particle2DStream`, `Particle2DFlow`), which
took only a file path. C#: `ParticleTextureResolver.ResolveFile(project, reference)`.

### A burst with a direction

`ParticleBurstAtDir(particle, x, y, z, dx, dy, dz, scale, seconds)` plays a burst like
`ParticleBurstAt`, turned so the effect's up (the axis of an authored cone, +Y) points along the
direction `(dx, dy, dz)`, of any length: an impact's dust and sparks leaving the surface along its
normal, a blood spray along the round, a muzzle flash along the barrel. It returns the emitter's id,
or -1. A zero or broken direction plays the effect upright, exactly as `ParticleBurstAt` does.
The emitter's turn carries its particles, so it is honoured wherever the effect is drawn on the
graphics card; the Software renderer plays it upright. A batched burst shares one emitter and has no
direction. C#: `ParticleBursts.Play(world, asset, position, scale, emitSeconds, direction)`.

### Hundreds of bursts: batching and a limit

| Command | What it does |
|---|---|
| `ParticleBurstBatched(particle, x, y, z, scale)` | Plays a burst like `ParticleBurstAt`, but every batched burst of one Particle resource shares one emitter: its particles are born where each burst was asked for and then move in the world on their own, so hundreds of them (falling leaves, sparks, chips) cost one simulation and one draw a frame. Returns true when asked for; no id, as there is nothing to keep. C#: `ParticleBursts.PlayBatched(world, asset, position, scale)`. |
| `ParticleSetBurstLimit(count)`, `ParticleBurstLimit()`, `ParticleBurstCount()` | The most `ParticleBurstAt` bursts alive at once in a room (256 unless a game sets it; 0 = no limit): one more removes the oldest. Emitters placed on Objects and batched bursts are never counted or removed. `ParticleBurstCount` says how many are alive. C#: `ParticleBursts.Limit`, `ParticleBursts.LiveCount(world)`. |

A batched effect has to be a single burst (`loop` off) that simulates in the world, drawn as quads
or trails, without linked emitters or a light, and not following the camera. Any other effect, and
every effect on the Software renderer or in a 2D room, plays as `ParticleBurstAt` instead, so the
command is always safe to use. One shared emitter holds the particles of 1,024 bursts (up to
65,536 particles); past that, new ones are not born until some die. An effect that has no live
particles costs nothing.

Every burst also costs less than it did, batched or not: an emitter's GPU definition is made once
rather than twice a frame (`GpuParticleDefinitionBuilder.Move` moves it), the GPU emitter of a burst
that has played out is kept for the next burst of its size, the flipbook quads and the soft default
sprite are made once rather than for every burst, one look at an effect's file serves all its
emitters, the draw arguments a step has written are not written again, and with many emitters the
live counts are read back from the graphics card less often (about 160 reads a second in all; with
a few emitters, as in the Particle Editor, ten a second each as before).

Measured with 300 live one-particle leaf bursts (GenesisCraft's falling leaves), the game thread's
time for a frame (composition update, particle submission, frame) and the managed bytes it
allocated: the median of 180 frames after 60 to warm up (in brackets, the frame one in ten is
slower than), on the development PC's performance cores, 9 Oct 2026 (`Build.bat --test
particle-bursts`). Before is one run; now and batched are the range over three runs.

| Renderer | 300 bursts before | 300 bursts now | 300 batched bursts | Allocated a frame (before / now / batched) |
|---|---|---|---|---|
| Direct3D 12 | 10.2 ms (42) | 5.2-5.9 ms (6.1-6.4) | 0.14-0.17 ms | 360 KB / 6.6 KB / 0.5 KB |
| Direct3D 11 | 4.3 ms (6.8) | 2.9-3.4 ms (3.5-4.2) | 0.08-0.11 ms | 337 KB / 13-14 KB / 0.5 KB |
| Vulkan | 6.0 ms (25) | 6.7-6.9 ms (7.2-7.8) | 0.08-0.10 ms | 517 KB / 151 KB / 1.0 KB |
| OpenGL | 5.5 ms (9.3) | 4.7-5.4 ms (5.2-6.8) | 0.15-0.17 ms | 332 KB / 10 KB / 0.5 KB |

The slow frames before were the frames in which every emitter read its counts back from the
graphics card at once. Making the 300 bursts took 721 ms on Direct3D 12 before and 277-334 ms now. The
Vulkan device still allocates about half a kilobyte for each emitter's dispatches itself. Separate
bursts still cost each emitter's GPU commands every frame (about 17 µs each on Direct3D 12): for
hundreds, batch them.

Since 23 October an emitter's step is timed on the graphics card as often as its counts are read
(ten times a second), not in every frame. 300 standing bursts that day: 2.35 ms on Direct3D 11,
4.87 ms on Direct3D 12, 4.16 ms on OpenGL and 6.72 ms on Vulkan (p90 2.80, 5.07, 4.26, 7.18), against
the 9 October ranges above (other changes came between; not a same-day comparison).

## 2D lights and darkness in a room (11 October 2026)

A 2D room is never darkened unless its game asks: `Light2DAmbient(amount, r, g, b)` darkens the
current room, `amount` (0.05 to 1) being the light left where no light reaches and `r, g, b` (0 to 1)
the colour of the shade. `Light2DPoint` and `Light2DCone` lights glow in any room, and light up the
darkness once `Light2DAmbient` has set an amount below 1. `Particle2DBurst` and `Particle2DFlow`
never touch lighting.

| Command | What it does |
|---|---|
| `Light2DAmbient(amount, r, g, b)` | Darkens the room to `amount` and tints the shade. 1 turns darkness off again. Other rooms are unaffected. |
| `Light2DPoint(id, x, y, radius, intensity, r, g, b)`, `Light2DCone(...)` | A light, kept until removed (zero radius or `Light2DRemove`). Without `Light2DAmbient` it only glows. |
| `Light2DBlocker(id, x, y, width, height)` | A rectangle that casts shadows from the lights. |

Until 11 October 2026 the first light or particle burst in a room turned darkness on at an amount
of 0.32, so a sunlit room went dark unless the game also called `Light2DAmbient(1, ...)`. Every
project then using 2D lights (one, a dark mansion game) sets the amount itself in each lit room, so
none looked different; that call is now unnecessary. A dark game calls `Light2DAmbient` in the
room's first Create, for example `Light2DAmbient(0.32, 0.012, 0.024, 0.06)` for the old default.

## Solid tiles in a 2D room (15 October 2026)

`TileMoveX(x, y, amount)` and `TileMoveY(x, y, amount)` move the caller's mask through the room's
solid tiles and return the new `x` or `y`. The mask is the box around the sprite's collision shapes,
turned with `image_angle` and scaled with `image_xscale` and `image_yscale`, so a rectangle tilted a
few degrees dips a corner a pixel or two into the floor it stands on.

A mask that already overlaps a tile (its angle, scale or sprite changed, or it was placed there) is
never moved further in through the surface it dips into. It can always move back out, and it slides
along that surface without catching on tile seams: across a floor, or up and down a wall it is
pressed into. Until 15 October 2026 an overlapped tile was ignored, so a boss wobbling its angle on a
floor fell through it and out of the room. A mask that overlaps no tile moves exactly as before.

Every collision tile is a whole solid box (a turned tile is the box around it, not a slope). There are
no one-way tiles: a one-way platform is an Object with its own landing rule, as in Mushroom Meadow. A
game that lets a body rise through tiles without `TileMoveY` and expects it to fall back through them
now sees it held where it overlaps a tile's top.

A turned circle or polygon mask is now the box around the turned shape: a circle keeps its size at
any angle (it used to grow by up to 41%, at 45 degrees). Unturned masks, and a single rectangle at
any angle, are unchanged. `Build.bat --test 2d-showcase` checks all of this.

## Flash lights: muzzle flashes and explosions

A game fires a short light from any event (the Step that fires a gun, an explosion's Create) and
forgets it: the engine draws it at full brightness in the frame it was fired, however short its
life, fades it to nothing over its life (brightness × (1 − age / life)²) and removes it.

| Command | What it does |
|---|---|
| `LightFlash(x, y, z, r, g, b, intensity, radius, life) -> flash` | A point light at `x, y, z`, colour 0-255, `intensity` and `radius` as `DrawPointLight3D` takes them, lasting `life` seconds (at most 60). 0 when nothing would be lit (no life, radius or intensity). |
| `LightFlashMove(flash, x, y, z)` | Moves a flash (one that follows a moving muzzle or a rolling fireball); false once it has burnt out. |
| `LightFlashRemove(flash)`, `LightFlashExists(flash)` | Puts a flash out at once; whether it is still lit. |
| `LightFlashClear()`, `LightFlashCount()` | Puts every flash out; how many are lit. |

C#: `FlashLights.Add(position, colour, intensity, radius, life)` (namespace
`Genesis.Runtime.Rendering`, colour 0-1), and for one frame `IRenderController.AddFlashLight`.

A flash is a point light like any other: it lights surfaces with the same response, the
first-person and other model layers, and the volumetric fog. The forward renderer has no four-light
limit: every point light goes into one clustered list (16 × 16 screen tiles × 24 depth slices, up
to 64 lights a cluster, the strongest 256 by distance and brightness when there are more), and a
pixel shades only the lights whose reach covers its cluster, so small flashes cost little however
many there are. What a flash never does is take a local shadow slot (four by default, at most eight):
an ordinary light as bright as a muzzle flash next to the camera wins the slot from the lamp that
held it, so firing made the lamp's shadow blink off and had six shadow faces drawn for a light that
lasts 50 ms. Flashes are unshadowed.

Up to 256 flashes are lit at once; one more replaces the one nearest its end. Their ages follow
game time: they hold still while the game is paused and slow down with `GameSpeed`. A room change
and a new game put them all out. On the Software renderer the frame's eight strongest lights are
lit at the corners of triangles, flashes included.

Measured on the development PC (GeForce RTX 5060 Ti, Direct3D 11, 1920 x 1080, 9 Oct 2026;
`flash-light-cost.txt` from `Build.bat --test flash-lights`; 10 Oct with GTAO, contact shadows,
bloom and volumetric fog on): a sunlit, shadowed yard with 24 crates, 48 flashes of radius 6
re-fired every 9 frames (0.15 s life), against none and against the same 48 as ordinary point
lights, 120 frames after 45, the range over three rounds:

| | GPU a frame | Whole frame (submit, draw, present) | Local shadow lights |
|---|---|---|---|
| No lights | 0.58-0.61 ms | 0.62-0.66 ms | 0 |
| 48 flashes | 0.71 ms | 0.76-0.77 ms | 0 |
| 48 ordinary point lights | 0.72-0.73 ms | 0.77-0.80 ms | 1 |

With those effects off (9 Oct) the same frames took 0.20-0.21, 0.29-0.34 and 0.32-0.35 ms of GPU
time. Dozens of flashes cost about a tenth of a millisecond at 1080p, the fog's share included; the
ordinary lights cost a little more because one of them takes the shadow slot and has its shadow
faces drawn.

## Decals: bullet holes, scorch marks, blood

A game puts a picture on the world from any event (where a shot's ray hit, under a blast, where a
wounded enemy stood) and the engine keeps it, draws it every frame and takes it away at the end of
its life. A decal is a box around a point on a surface, facing along the surface's normal; whatever
opaque surface lies inside the box takes the picture: walls, models, script meshes and terrain
alike, bumps and all, so the same command marks a wall and the ground.

| Command | What it does |
|---|---|
| `DecalAdd(image, x, y, z, nx, ny, nz, size, life) -> decal` | A decal `size` across at `x, y, z`, facing along `nx, ny, nz` (the hit's normal, out of the surface), lasting `life` seconds and fading out over the end of it (0: until removed). `image` is an Image resource, or the number of a texture a script painted (`TextureCreate`). 0 when it could not be placed. |
| `DecalSetColor(decal, r, g, b, alpha)` | Tint (0-255; white leaves the picture as it is) and opacity (0-1). |
| `DecalSetRotation(decal, degrees)` | Turns the picture about its normal, anticlockwise as seen from the front: a random turn makes bullet holes look less alike. |
| `DecalSetSize(decal, width, height, depth)` | The picture's width and height, and how far through the surface the box reaches (half the size at first). |
| `DecalSetBlend(decal, blend)` | `"multiply"` (the default), `"alpha"` or `"add"`: see below. |
| `DecalSetSeeThrough(decal, onSeeThrough)` | A decal on glass or another see-through surface (off by default): see below. |
| `DecalSetFade(decal, seconds)`, `DecalSetLife(decal, seconds)` | How long the fade at the end of its life takes (by default the last quarter of the life, at most 2 s); a new life from now. |
| `DecalMove(decal, x, y, z, nx, ny, nz)` | Moves a decal and turns it to a new normal. |
| `DecalRemove(decal)`, `DecalExists(decal)`, `DecalClear()`, `DecalCount()` | Removes one; whether it is still there; removes them all; how many there are. |
| `DecalSetLimit(count)` | How many decals may be in the world at once (256 at first, at most 4,096). One more removes the oldest, and lowering the limit removes the oldest at once. |

C#: `WorldDecals` (namespace `Genesis.Runtime.Rendering`) has the same operations, and
`IRenderController.DrawDecals` draws a frame's decals directly.

**Layers, normal and roughness maps (29 October 2026).**

| Command | What it does |
|---|---|
| `DecalSetLayer(decal, layer)` | Puts a decal on layer 0 to 5 (0 at first). It shows only on surfaces that take that layer. |
| `Engine.Model.DecalLayers = mask` | Which layers show on this instance's model: bit n for layer n (layer 0 is 1, layer 1 is 2). 0, the default, takes every layer; an Object's ModelRendererComponent can say `"DecalLayers"` too. |
| `Engine.Decal.Layer`, `Engine.Decal.Enabled`, `Engine.Decal.Limit` | The layer new decals go on; whether decals are drawn (kept while off); the limit, as `DecalSetLimit`. All three go back to their defaults at each room's start. |
| `DecalSetNormalMap(decal, image)` | A normal map (tangent space, +Y up the picture): its bumps re-shade the surface under the decal in the room's sun and ambient light. |
| `DecalSetRoughnessMap(decal, image)` | A roughness map (red: 0 mirror-smooth to 1 matte): the sun's highlight on the decal is added at that roughness (wet blood, fresh paint, oil). |

Bullet holes on layer 0 and characters set to `Engine.Model.DecalLayers = 2` keep the holes off the
characters. The Software renderer shows every decal on every surface and draws the pictures alone.

What the maps can and cannot do: decals are painted over the picture after it has been lit, not
fed into the surface's lighting, so their normal and roughness maps are an approximation. A normal
map re-weighs the room's sun and sky light for its bumps and scales the lit colour under the decal
by that ratio: it reads well in daylight and under the sky, but a lamp's light is scaled with the
rest rather than lit from the lamp's own direction, so a dent lit only by a torch shows little
relief, and a surface no light reaches stays dark whatever its bumps. A roughness map adds the
sun's highlight at the decal's roughness in a second, additive draw: it can make a patch shinier
(wet blood, fresh paint, oil) but cannot make one duller, cannot take away the surface's own
highlight, gives no highlight from lamps, and its sheen is not reflected in the environment. Neither
map changes a decal's shape, its shadows or what the depth sees. For a mark that must light exactly
like the surface (a carved groove, a metal plate), model it, or use a material on the mesh.

```pgsl
// Step, when a shot hits: a bullet hole turned at random, gone after 30 seconds.
var hole = DecalAdd("BulletHole", hitX, hitY, hitZ, hitNx, hitNy, hitNz, 0.12, 30);
DecalSetRotation(hole, Random(360));
// An explosion: a scorch mark on whatever is under it.
DecalAdd("Scorch", x, groundY, z, 0, 1, 0, 3, 60);
```

How a decal goes over the surface:

- **Multiply** (the default) multiplies the lit surface by the picture: it darkens and tints and
  keeps the surface's own light and shade, so the same bullet hole is right in sunlight and in a
  dark corridor. White leaves the surface as it was; the picture's alpha fades it in. Bullet holes,
  scorch marks, blood, dirt and footprints are multiply decals. The ambient occlusion of the
  composite still darkens only what is left of the indirect light.
- **Alpha** paints the picture's own colours over the surface, unlit: signs and paint that should
  read in any light. In the dark it glows; use multiply for marks that should take the scene's light.
- **Add** adds the picture's colours as light: embers, hot metal, glowing energy marks.

What it looks like, and what it does not do:

- A surface turned away from the decal takes none of it (the back of a wall, the far side of a
  thin plank), and one at a steep angle to it fades out (full strength within 60 degrees of the
  decal's facing, gone at 80), so a decal near a corner does not smear along the other wall. The
  ends of the box fade softly, so bumpy ground is not cut by a hard edge.
- Decals are drawn after the opaque world and before water, glass, particles and the first-person
  layers: they never land on water, see-through draws or a held weapon. They go under the fog like
  the surface they are on.
- **On glass (15 October 2026).** See-through surfaces write no depth, so a projected decal cannot
  find them. `DecalSetSeeThrough(decal, true)` draws that decal instead as a flat picture in its
  own plane (the box's face, through the point it was put at), right after the see-through draws:
  a bullet hole in a shop window. It is seen from both sides, hidden only by opaque things in front
  of it, and no longer projected onto the opaque world, so it is for a hit on a pane, not on a
  wall. Every blend works as above. Decals without it are drawn exactly as before.
- Decals stay where they were put in the world. On a moving object (a door, a vehicle) a decal stays
  behind unless the game moves it with `DecalMove` every frame, which is cheap. Anything opaque that
  passes through a decal's box takes the picture while it is inside it, which is why the box is
  only half as deep as the decal is wide; a character walking over a blood pool on the floor shows
  it on its feet for that moment.
- Multiply and add decals with the same picture are one draw call however many there are; alpha
  decals keep the order they were added in (a newer one over an older one), one draw call for each
  run of the same picture.
- Ages follow game time: decals hold still while the game is paused. A room change and a new game
  remove them all.
- They are drawn on every renderer: Direct3D 11 and 12, Vulkan and OpenGL in a pass that reads the
  scene's depth, the Software renderer on the CPU the same way. A frame drawn without the post
  composite (an editor preview without a depth buffer) and a water reflection draw no decals.

Measured on the development PC (GeForce RTX 5060 Ti, Direct3D 11, 1920 x 1080, 10 Oct 2026;
`decal-cost.txt` from `Build.bat --test decals`): a 16 m wall over rolling terrain with GTAO,
contact shadows, bloom and volumetric fog on, bullet holes 12-22 cm across at random turns, two
thirds on the wall and a third on the ground, 120 frames after 45, the range over three rounds:

| Decals | GPU a frame | Whole frame (submit, draw, present) | `WorldDecals.Submit` | Draw calls |
|---|---|---|---|---|
| None | 0.73-0.78 ms | 0.78-0.83 ms | - | 0 |
| 500 | 0.73-0.76 ms | 0.79-0.80 ms | 0.02-0.13 ms | 1 |
| 2,000 | 0.75-0.77 ms | 0.80-0.87 ms | 0.07-0.35 ms | 1 |

Small decals cost little more than the screen rectangles they cover: hundreds of bullet holes are
within the frame-to-frame noise on this card. A decal close to the camera costs what its rectangle
covers, a full-screen one about a full-screen pass.

**Decals on moving objects (19 October 2026).** A decal is fixed in the world, so an object moving
under it slides out of it. `DecalFollow(decal, instance)` keeps it on the object instead: from then
on it moves and turns with the object (its model's transform when it draws one) and is removed when
the object is destroyed. `DecalUnfollow(decal)` leaves it where it is. A bullet hole on a door or a
cart, a scorch on a crate that is pushed; the picture's own turn about its normal stays as set.

## Save slots

A game can keep named saves of any text (JSON, usually) under the player's profile, beside the
numeric save.

| Command | Meaning |
|---|---|
| `SaveSlotWrite(slot, text)` | Write a slot, replacing what it held. Up to 4 MiB. False, with `SaveSlotLastError()`, when the name is not allowed or the disk refuses. |
| `SaveSlotRead(slot)` | The slot's text; empty when it does not exist. |
| `SaveSlotExists(slot)`, `SaveSlotDelete(slot)` | |
| `SaveSlotCount()`, `SaveSlotName(index)` | The slots that exist, most recently written first. |

A slot name is 1 to 48 letters, digits, spaces, hyphens or underscores. A slot is written to a
new file and moved into place, so a crash during a save leaves the previous save intact. Slots are
kept in `%LocalAppData%\Genesis\GameSaves\<game>\Slots`, where `<game>` follows the project's id,
so a game that is moved or renamed keeps its saves.

## Changing room without a freeze

A room change used to do all its work between two frames. The game stood still, the window did
not answer, and the frames after it were long while the new room's ground was made solid and its
textures were read. In the Player a room change is now spread over frames. **This is a changed
default**: `RoomGoto` and `RoomGotoWhenLoaded` both use it.

What happens:

1. The old room's RoomEnd and Destroy events run and it is removed, as before.
2. The scene is held. No script steps, no physics runs and nothing of the new room is drawn. The
   engine covers the screen and draws a loading screen on the cover. The window stays responsive,
   music keeps playing and game time keeps moving so a loading screen can animate.
3. The new room is put together a few milliseconds at a time: its terrain is read on a worker
   thread and its objects are placed one after another. Create events still run together once
   every object is in place, then the room-start events, exactly as before.
4. The finished room is drawn behind the cover, without being updated, until its ground is solid,
   its scenery is in place and the models and textures its first view shows have been read and
   sent to the graphics card. Its particle emitters are set up and run for a sixtieth of a second
   there, so their buffers are made and their shaders compiled, and the sounds its Objects play
   on arrival are read. These were the room's long first frames; now nobody sees them.
5. The cover fades over 0.2 seconds and the room starts running.

A 3D room is always prepared this way. A 2D room that is ready within a quarter of a second simply
appears, with no cover, so a game that steps from screen to screen is not interrupted.

The room a 3D game starts in is prepared the same way once the start-up screen has finished: it
is drawn behind the cover until its first view is loaded, then shown. Its Create and room-start
events have already run; its first Step comes when the cover lifts.

A screenshot taken with `--autoshot`, an acceptance run and a benchmark wait for the cover to go:
they record the game, not the loading screen.

| Command | Meaning |
|---|---|
| `RoomChanging()` | True while a room change is under way. |
| `RoomLoadProgress()` | 0 to 1: how far the change has got. (Also how much of a room asked for with `RoomGotoWhenLoaded` has been read.) |
| `RoomChangeBudget(milliseconds)` | How long the change works in each frame; 8 unless set. 0 changes room in one step, with no cover, as before. |
| `RoomChangeProgressBar(show)` | Whether the engine draws its bar and text on the cover. |
| `RoomChangeText(text)` | The words above the bar ("Loading" unless set; empty for none). |
| `RoomChangeColors(coverR, coverG, coverB, barR, barG, barB)` | The colour of the cover, and of the bar and text. |
| `RoomChangeFade(seconds)` | How long the cover takes to fade from the new room; 0.2 unless set. |
| `RoomChangeMinimumTime(seconds)` | The least time the cover stays up, so a loading screen can be read; 0 unless set. |

From C#, the same settings are static properties of `RoomChangeScreen` (namespace
`Genesis.Runtime.Project`), and `ProjectGameContext.IsChangingRoom` and `RoomLoadProgress` report
on a change.

**A loading screen of the game's own.** The ordinary HUD is not drawn during a room change. A
behaviour draws the loading screen by overriding

```csharp
public override bool OnDrawLoadingScreen(IHudCanvas hud, float progress)
```

and returning true, which tells the engine to leave out its own bar and text. The engine has
already covered the screen when it is called. It is called on Objects that outlive the room
change (persistent ones) and on the new room's Objects from the frame after their Create events
have run. Between the old room going and those Create events no room Object exists, and the cover
is bare for that moment (10 to 70 ms in one game).

A game that wants its screen there from the first covered frame, with no persistent Object, sets
a painter once:

```csharp
RoomChangeScreen.Painter = (hud, progress) => { /* draw */ return true; };
```

The painter is asked first and Objects draw on top of it. The engine's bar is left out when
either has drawn.

To see a loading screen as a picture, run the Player with `GENESIS_ROOM_CHANGE_SHOT=<label>`: the
third covered frame of each room change, and of the first room, is saved as
`Debug/Images/<label>-<room>.png`.

`GENESIS_ROOM_CHANGE_BUDGET_MS=0` in the environment changes room in one step for a whole run.
Studio's live reload, which rebuilds a room when an asset is saved, always does it in one step.

What is not split: the Create events and the room-start events each run in one frame, as do the
first use of a particle effect and of a shader the graphics driver has not compiled. They happen
behind the cover, where the loading bar pauses for them. What a script does in its first Step
(loading sounds, spawning a cast) still happens in the room's first running frame; doing it in
Create puts it behind the cover.

### Textures read in the background

In the Player a model's textures are read and decoded on worker threads. A mesh is left out of
the frame until every texture it uses has arrived, then drawn whole, instead of the frame that
first draws it waiting for all of them. A room that showed 25 new textures spent 2.4 seconds on
them in its first frame; that work is now on workers, and a few milliseconds of each frame hand
the results to the graphics card. Behind a room change's cover it is waited for. In the middle of
play a model that appears for the first time may be drawn a few frames late.

Editors, previews and captures still read a texture in the frame that asks for it.
`GENESIS_BACKGROUND_TEXTURES=0` makes the Player do the same. Textures for terrain, sprites and
particles are read where they are first used, as before.

### Finding what made a frame long

The Player writes a line to `Debug/Logs/project_player.log` for every frame over 100 ms
(`GENESIS_SLOW_FRAME_MS` changes the limit), at most 24 for each room:

```
Slow frame: 623 ms in River Road (frame 2, 0.9 s after the room began): update 595 ms, gathering
what to draw 20 ms, drawing 4 ms, HUD and hooks 2 ms, presenting 1 ms; longest parts: RoomTerrain
fixed update 478 ms, ScriptHost update 108 ms; loading in that frame: 2 models read 8 ms, 12
models sent to the graphics card 7 ms, 2 sounds 14 ms; garbage collector paused 0 ms
```

"Longest parts" names the subsystems that took longest. "HUD and hooks" is the HUD and overlay the
game draws and anything attached to the end of a frame, such as a screenshot. "Loading" counts
what the game's own thread read, decoded, compiled or made solid in that frame. When the parts
fall well short of the frame, the line says how many milliseconds were outside the frame's own
work: the window's messages, or another program holding the processor or the graphics card. A room
change adds one line saying how long the room was prepared behind the cover and how long its
longest single piece took.

### The debug screen and profile recordings

In a running game **F6** shows a one-line strip: FPS, frame time, CPU time on the game's thread,
GPU time (when the renderer measures it), memory (working set and private), the managed heap and
video memory. **F7** opens the full panel:

- **Overview**: process CPU, frame-time percentiles, collections per generation, allocation rate,
  threads and handles.
- **Resources**: the textures, models, sounds, particles, objects and instances the game holds.
  Click the search box and type (`grass`, `kind:texture`, `>1mb`); sort by size, count, name or kind.
- **Game**: each PGSL object's instances, event calls and time, the slowest events, script errors.
- **World** and **AI & Navigation**: the inspector, view toggles and navigation telemetry.
- **Draw** (29 October 2026): what is drawn over the frame to see why something looks wrong.
  **Colliders** draws the colliders the game's collisions use, as wireframes in 3D and outlines in
  2D, one colour each: static bodies blue, moving green, kinematic yellow, characters magenta,
  triggers orange, a 2D room's solid tiles red, sprite hitboxes (the mask box collisions test) cyan;
  the kinds can be left out one by one, and the legend counts what was drawn. In 3D they are the
  physics engine's own shapes within 60 m of the camera, model and terrain meshes included (a dense
  mesh is sampled); a game that collides in its own script, as Cathedral Test's walls do, shows none.
  **GUI layer
  outlines** boxes each layer's area with its owner. **View mode** draws the frame in a diagnostic
  way, with a legend in the corner: *LOD level* (models in their level of detail's colour, full
  detail green to coarsest red), *Overdraw* (models added up, brighter where more cover a pixel),
  *Depth*, *Normals* and *Shadow coverage* (the sun's shadow map), and one term of the lighting
  alone: *Albedo* (each surface's own colour, unlit), *Ambient light* (the indirect light: sky and
  ground, the light probe and reflections) and *Direct light* (the sun with its shadows, and the
  lamps), the same views `Engine.DebugView("Albedo")`, `("Ambient")` and `("Direct")` draw; black in
  the ambient view takes no indirect light, black in the direct view gets no sun or lamp. Their
  `Engine.Debug.View.Mode` names are `"albedo"`, `"ambient"` and `"direct"`. *Shadow cascades*,
  *Reflections* and *Light count* are offered but not drawn yet (they need the renderer's forward
  shader); the legend says so and the frame stays shaded. (The World tab's older pass row, with its
  Lighting and Fog buttons, is separate and still draws only Depth and Normals.)
- **GUI**: which instances' Draw GUI events and C# HUDs drew in the last frame, the draw calls each
  made, the area it covered, and the User Interface resources it drew (their elements and the GUI
  size they were laid out for).
- **Object**: the report on one instance. Turn on *Pick with the mouse* and click it in the game,
  or step through the room's instances with Previous and Next. It says its Object, position,
  model, materials and their shaders, body, size on screen and level of detail, and whether it is
  drawn this frame, and if not why: invisible, deactivated, outside the camera's view, behind an
  occluder, too small on screen, past the far plane, in a hidden model layer, off a 2D room's
  view. The reasons come from the same tests the draw makes.
- **Engine** appears only when Studio's developer setting *Show Engine debug category* is on.

Everything on Draw, GUI and Object is off unless turned on, costs nothing while the screen is closed,
and is reachable from a script or a room's Inspector as `Engine.Debug.*` (`Engine.Debug.Overlay.Colliders.Enabled`,
`Engine.Debug.View.Mode = "lod"`, `Engine.Debug.Inspect.Instance`, `Engine.Debug.Inspect.Report`; see
Design/EngineApiAreas.md). For an unattended run, `GENESIS_DEBUG_SCREEN` holds settings separated by `;`
(`Engine.Debug.Overlay.Colliders.Enabled=true;Engine.Debug.View.Mode=lod`), `panel=Object` to open a tab,
`report=4` to write after four seconds of play one line per placed instance to the game's log
(`debug object #12 Pew: not drawn, outside the camera's view (frustum culled)`), and `inspect=culled`
(or `drawn`, or an instance id) to put the Object report on the first instance not drawn (or drawn)
and log it.

**F8** (or the Record button) records a profile. A debug run (Studio's Debug button) records from
the start unless *Start recording when debugging starts* is turned off in Preferences → Runtime.
Each recording is a folder in `Debug/Profiles/` holding `frames.csv` (one row per frame),
`summary.json` and `report.md`: the slowest frames, the hottest PGSL objects and events, and
warnings about allocation, garbage collection, long frames and script errors. The recording
finishes when F8 is pressed again or the game closes.

**When the debug keys act (15 October 2026).** F6, F7, F8, **P** (pause) and **N** (step a frame
while paused) are for whoever is making the game, so they act only in a game started to be worked
on:

| Launch | Debug keys |
|---|---|
| Studio's Debug (the Player's `--debug`) | On |
| Studio's Run (the Player's `--live-reload`) | On |
| Any launch with `GENESIS_DEBUG_KEYS=1` | On |
| An exported game started normally | Off |
| Any launch with `GENESIS_DEBUG_KEYS=0` | Off |

Until then they acted in every game, exported ones too: a player typing "p" in a chat line paused
the game, and F8 started a recording with a hitch of about a second. The Player's log says which
applies (`debug keys on: a debug run`, `debug keys off: ...`). A tool or script that starts the
Player itself and wants the keys (a game's own dev-run launcher, say) passes `--debug` for the full
debug run, or sets `GENESIS_DEBUG_KEYS=1` to have only the keys. A profile for the whole run
(`GENESIS_PROFILE=1`, below) needs no key and is recorded either way.

A game can also stand them aside while it has the keyboard:

| Command | What it does |
|---|---|
| `DebugKeysSetEnabled(enabled)` | False turns the debug keys off (a chat line or a name box is open), true on again. It only takes them away: in an exported game they stay off. |
| `DebugKeysEnabled()` | True when the debug keys act now. |

While a game reads typed text (it called `KeyboardTextTake` this frame or the last, as a text box
does every frame it is open), P and N are letters, not debug keys, with no command needed; F6, F7
and F8 type nothing and still act. The debug screen's own Resources search box already kept the
keyboard to itself while it had it. F12 (a picture of the frame into the game's debug images
folder, as `ScreenshotSave` takes) is not one of them and works in every game. Until 15 October
2026 it saved nothing in a 2D room: the picture was taken at the end of a 3D frame only. It is now
taken where `ScreenshotSave`'s are, in the first frame without a room change's cover, and the
render log says where it went (`screenshot saved: ...`).

### Finding what a game allocates every frame

With `GENESIS_ALLOCATION_LOG=1` (or when the game runs with the debugger) the Player writes, every
five seconds of play, how much managed memory the game allocated and how many collections that
cost; `GENESIS_ALLOCATION_LOG=types` also names the types allocated most:

```
Managed allocations: 14.5 MB in 5.0 s over 300 frames (49.5 KB a frame, 2.9 MB/s), 1 gen0 collections
Most allocated types: System.Double 5.1 MB, System.Threading.ExecutionContext 1.3 MB, ...
```

`GENESIS_ALLOCATION_LOG=commands` also says where in the scripts it was allocated, measured exactly
rather than sampled:
- **By PGSL command:** the bytes each command allocated, its calls and the bytes a call.
- **By event and worker job:** what each one's own code allocated outside the commands it called.
  That is joining text with `+`, new values and the like, so text a game builds itself shows apart
  from text a command returns.

A recording (the debug screen's, or `GENESIS_PROFILE=1`) made with it on has both tables in its
report. It reads the thread's allocation counter around every command, which costs some tens of
nanoseconds a call, so timings taken with it on run a little slow:

```
Allocated by PGSL command: StringOf 41.2 MB in 1203311 calls (36 B each), DsGridGetRegion 12.0 MB in 220 calls (57190 B each), ...
Allocated by scripts outside commands (joining text, new values): Game.Step 30.5 MB in 300 runs (its commands 55.1 MB), job McMeshChunkJob 8.1 MB in 96 runs (its commands 70.3 MB)
```

Frames behind the start-up screen or a room change's cover are not counted. An idle Verdant
Hollow allocated 3.9 MB a frame (a simulated lake rebuilt its whole surface, the foliage planner
regrew its lists and every particle layer rebuilt its colour table, each frame); it now allocates
about 50 KB. The `speed` headless target fails when an idle game allocates 1 MB a frame or more.

A script mesh drawn through a mesh Shader resource (`DrawMeshShader3D`) after setting its
instance's values (`ShaderSetVector`, `ShaderSetParameter`) allocated 128 bytes a draw: a new array
for each value set, and two dictionary enumerators boxed when the draw copied the instance's values
and textures. A game drawing a few hundred chunks and creatures that way made 50 KB of garbage a
frame (`System.Single[]` and `Enumerator[System.String,System.Single[]]` in the type list above).
The values now go into the instance's own arrays and are copied without boxing: nothing is
allocated a draw, and 0.3 KB a frame in all (`--test mesh-upload`, case
`ShaderMeshDrawsAllocateNothing`: 400 such draws a frame on DX11, 10 Oct 2026). Starting a worker
job with `JobScriptShareAll` also grew its table of the game's structures one entry at a time; it is
sized once now, so a start at a voxel game's size (44 grids, 2 500 lists, 65 maps, 2 500 globals)
allocates 322 KB on the game's thread instead of 455 KB, and the copy of a shared map that one side
changes is made in one allocation.

### What a game shows while it starts

The Player draws a loading screen (the engine's name, a bar and what it is doing) as soon as its
graphics device is ready, and again before it reads the textures and builds the first room; the
start-up screen then takes over. Its built-in DX11 shaders compile on worker threads from the
moment it starts (`GENESIS_SHADER_WARMUP=0` turns that off), which matters after an engine update,
when nothing is in the shader cache yet.

The game's own Shader resources are made the same way: compiled (or, in an exported game, read)
on one or two workers while the room loads, and the room's cover stays up until they are (until
then their draws use the engine's own shading; `GENESIS_SHADER_WARMUP=0` turns this off too). An
exported game carries them compiled for every backend. The models of the project's Objects are read
on a low-priority thread once the first room is on screen (each up to 1 MB, 48 MB in all, smallest
first), so the first creature of a kind a script creates does not read and parse its model in that
frame: creating ten new kinds of creature in GenesisCraft took 13-178 ms of one frame (the model
reads), and takes 2 ms with them read ahead. `GENESIS_MODEL_WARMUP=0` turns that off. The log says
what each did:

```
project shaders: 10 programs for Dxil made on workers in 684 ms (0 read from the shader cache, 10 compiled)
object models: 309 of the 309 the project's Objects use read ahead on a worker in 1947 ms (13.1 MB; 0 already held or left to the game, 0 over the size limits)
```

### Finding where loading time goes

With `GENESIS_LOAD_PROFILE=1` the Player writes a timing tree to `Debug/Logs/project_player.log`
when the first room is ready, and again after every room change. Off, it costs nothing.

```
Load profile: the cover waited 0 frames for model files read ahead, 4 for textures, 1 for a subsystem's warm-up and 3 with everything loaded, for frames to settle
Load profile: start-up and first room rm_InGame_Wilds_RiverRoad, 2.65 s after the process started
  moments (s since the process started): player started 0.05, log open 0.28, window open 0.62, graphics device ready 0.89, loading screen shown 0.94, first room built 1.60, start-up screen done 1.72, first room ready 2.65
  game thread (wall time on the game's thread):
    frames: render 1102 ms (12 times, longest 494 ms)
      gathering what to draw 913 ms (10 times, longest 389 ms)
        RoomTerrain draws 452 ms (10 times, longest 205 ms)
          textures 127 ms (11 times, longest 41 ms)
  ...
  worker threads (summed over threads, which overlap):
    model file read ahead 1218 ms (52 times, longest 98 ms)
      model file: read its binary cache 515 ms (52 times, longest 98 ms)
    terrain collision: physics tree made or read on a worker 19 ms
```

A span opened inside another is listed beneath it, with how often it ran and its longest time;
"(not in a smaller span)" is the work no smaller span names. The game thread's figures are time
that held the game up. The workers' are added across threads, so they can exceed the wall time; a
worker job the cover waited for shows in the first line, which says how many frames the cover
stayed up for model files, textures, a subsystem (terrain collision, emitters) or for frames to
settle. The moments line places each stage on one clock from the process start.

What the Player keeps in the project's `.genesis/Cache` folder so that a game's second start is
quicker than its first (deleting the folder costs one slow start; exports and resource scans leave
it alone):

- `Models`: each large model's parsed geometry (since 2 October).
- `Textures`: each picture's decoded pixels and mip chain, deflated, valid while the picture's
  size and modified time are unchanged (`GENESIS_TEXTURE_CACHE=0` turns it off). Only pictures
  inside a project are cached.
- `Colliders`: a terrain's collision mesh and the physics engine's search tree over it, named by a
  hash of its triangles and scale, so a changed terrain can never find an old one
  (`GENESIS_COLLIDER_CACHE=0` turns it off). Verdant Hollow's is about 60 MB.

Frames behind a room change's cover send up to 24 ms of textures a frame to the graphics card
(`ProjectRoomSwitcher.CoverTextureUploadMilliseconds`); a frame of play keeps its 4 ms.

Measured on 5 October 2026 on copies of Golden Stag and of a new 3D Nature Walk, DX11, the base
commit's Player against this one, medians of 4 warm and 2 cold starts on a busy machine (other
sessions were compiling). Cold means no `.genesis/Cache`; Windows' file cache and the shader cache
were warm. "Loading screen" and "First room ready" are seconds from the process start; the columns
between them are how long each stage took, in seconds:

| Room | Start | Loading screen | Room built | Start-up screen | Behind the cover | First room ready |
|---|---|---|---|---|---|---|
| Golden Stag tavern | warm, before → after | 0.84 → 0.86 | 0.25 → 0.26 | 0.09 → 0.05 | 0.95 → 0.56 | **2.15 → 1.72** |
| | cold | 1.60 → 1.09 | 0.42 → 0.27 | 0.09 → 0.05 | 1.51 → 1.16 | **3.62 → 2.56** |
| Alderford outside | warm | 0.86 → 0.89 | 0.28 → 0.29 | 0.13 → 0.05 | 1.15 → 0.80 | **2.42 → 2.03** |
| | cold | 0.93 → 0.93 | 0.34 → 0.32 | 0.14 → 0.06 | 3.04 → 2.84 | **4.45 → 4.15** |
| River road | warm | 0.89 → 0.88 | 0.68 → 0.68 | 0.11 → 0.05 | 1.96 → 1.10 | **3.64 → 2.71** |
| | cold | 1.10 → 0.99 | 0.85 → 0.70 | 0.13 → 0.07 | 2.58 → 1.82 | **4.66 → 3.57** |
| Verdant Hollow (template) | warm | 0.78 → 0.78 | 0.37 → 0.37 | 0.05 → 0.05 | 3.15 → 0.57 | **4.35 → 1.76** |
| | cold | 0.77 → 0.77 | 0.37 → 0.38 | 0.04 → 0.05 | 3.31 → 3.28 | **4.49 → 4.48** |

"Room built" is from the loading screen to the room's objects being placed; "behind the cover" is
from the start-up screen finishing to the first room being shown. The owner had seen about 20 s
behind Golden Stag's loading screen. On this engine and a copy of the project no start with a warm
shader cache took more than 5.4 s, and with an empty one 12.5 s (the extra 9 to 10 s before the
loading screen); one run lost 2.4 s creating the sound device while other programs were busy. So
that figure most likely came from a cold shader cache, an older engine or a loaded machine. What
is left: a cold start
still parses each large model's JSON (8 s of worker time for the tavern's 40 models, 1.8 s for the
largest) and decodes its pictures once; the river road's room-start script takes about 0.35 s; a
cold shader cache (the first run after an engine update) adds 9 to 10 s before the loading screen
while the built-in shaders compile.

### Finding what a game holds on to

Every room change also writes what the game holds once the new room is built:

```
Room change memory: Village holds 283 MB managed in 546 MB of collector heaps, 851 MB in all, 1147 handles
```

"Managed" is what the game's own objects take; the collector's heaps are what it has taken from
Windows to keep them in; the rest of the total is memory outside the collector, such as physics,
sound and what the graphics driver keeps. In a game that goes back and forth between the same
rooms, each room's figures should settle after its first two or three visits. One that climbs
visit after visit is a leak. The total can sit at one of two levels from one visit to the next
without climbing (on the test island they are about 125 MB apart); that is not a leak. For a soak
run, set `GENESIS_ROOM_CHANGE_MEMORY=1`: the Player then collects before measuring, so the managed
figure is what is really still in use, and adds a line naming anything from a room left two or
more changes ago that is still in memory:

```
Room change leak watch: still alive from rooms left two or more changes ago: 11 RoomTerrainSubsystem
```

That switch costs a full collection at every change, so leave it off for play.

## Sky

- **The sun is round wherever it is.** A wide view used to draw the sun as an ellipse away from
  the centre of the screen. A room can also size it: **Sun size** under **Lighting &
  atmosphere** (`sunDiscScale`, 0.25 to 8; `Engine.Sky.SunSize` from a script).
- **A clear sky no longer shows bands.** Where the engine draws a sky, the final image carries
  half a level of fixed noise. Scenes without an engine sky are pixel for pixel as they were.

What the existing settings do, since the numbers are not obvious:

| Setting | What it does |
|---|---|
| Cloud coverage scale | Multiplies the cloud the weather reports, up to full cover. Cloud is drawn only above 8% cover. Clear weather reports little, and the Clear Day preset takes 35% of that, so a clear day shows no cloud until the scale is about 2 to 2.5. Overcast and storm presets set cover themselves. |
| Cloud base height, thickness | The cloud layer is real fog, centred on the base height and as deep as the thickness: from about 140 to 220 m with the defaults of 180 and 85. **Ground that reaches it is inside that fog**, so hilltops and the trees on them can turn pale while the slopes below do not. Raise the base well above the highest ground for a world with hills. |
| Atmospheric haze | Adds fog of density 0.012 per unit, with a floor of 0.002. The Clear Day preset halves the haze first. So nothing changes below about 0.17, or below about 0.33 on a clear day; 0.6 to 0.8 gives two to five times the floor. For a long view, set **Visibility (km)** instead, which states the distance directly. |

## Sky light and eye adaptation

Two room settings for the outdoor light of a modern shooter, added on 10 October 2026. Both are
**off by default**: a room that does not turn them on is drawn exactly as before, pixel for pixel.
They are in the Room editor under **Sky light & exposure**, in the room file's `environment`, and
scripts can change them for the room being played (a new room starts from its own settings).

### Hemisphere sky light

Under a dynamic sky, the light that reaches shade was the sky's zenith colour: deep blue, and dark.
On a clear afternoon (Clear Day at 15:00 with ambient intensity 2.2), lit in linear light, shade got
11% of the light on sunlit ground, and that light was more than twenty times bluer than red.

With **Sky light** set to **Hemisphere**, the engine works out the light of the whole sky dome as it
draws it (the pale horizon as well as the blue zenith, with the atmosphere's own terms) and of the
sunlit ground below it, whenever the time of day, the weather, the preset or the settings change.
A surface facing up takes the sky, one facing down the ground, and one in between a blend, as
before; models, terrain, foliage, water and the CPU renderer all take it. The environment
reflection (`environmentReflection`) then mirrors the sky as drawn, horizon to zenith with the
ground below, instead of the two ambient colours, and still blurs to the sky light as a surface
roughens.

On the same afternoon shade gets 28% of the light on sunlit ground (strength 1.5 gives about 36%,
for a brighter, game-like shade), its light is about twice as blue as red rather than twenty-four times, and
sunlit ground comes out within 5% of neutral instead of blue. With the default colour pipeline,
which lights with the authored colours as they are, the zenith light was less dark (29%) and the
sky light gives 39%, its light half again as blue as red. Working the light out takes about 7
microseconds of CPU, and the hemisphere light costs nothing measurable on the graphics card.
Without a dynamic sky, the room's background colour is the sky; at night the sky light follows the
glow the night sky is drawn with.

| Setting | Key | Default | Use |
|---|---|---|---|
| Sky light | `skyLight` | `"zenith"` | `"hemisphere"` for the sky dome's light. `"zenith"` is the light as before. |
| Sky light strength | `skyLightStrength` | 1 | 0 to 16. 1 is the sky as drawn. The room's ambient intensity, which made up for the dim zenith light, does not apply in hemisphere mode. |
| Sky light colour | `skyLightSaturation` | 0.4 | 0 to 2. 1 is the sky's own colour, 0 grey light of the same brightness. A real clear sky's light is much paler than its blue, since much of it comes from the bright haze near the horizon; at 0.4 sunlit ground under the engine's clear sky is within a few percent of neutral in either colour pipeline. |
| (file and scripts only) | `skyLightTint` | `[1, 1, 1]` | Red, green and blue multipliers, 0 to 4. |

| Command | What it does |
|---|---|
| `SkySetAmbientMode(mode) -> bool` | `"hemisphere"` or `"zenith"`; false for another name. Also `Engine.Sky.AmbientMode`. |
| `SkyGetAmbientMode() -> string` | The room's mode. |
| `SkySetAmbientStrength(strength)`, `SkyGetAmbientStrength()` | Brightness, 0 to 16. Also `Engine.Sky.AmbientStrength`. |
| `SkySetAmbientSaturation(saturation)`, `SkyGetAmbientSaturation()` | Colourfulness, 0 to 2. Also `Engine.Sky.AmbientSaturation`. |
| `SkySetAmbientTint(r, g, b)` | Colour multipliers, 0 to 4 each. Also `Engine.Sky.SetAmbientTint(r, g, b)`. |
| `SkySetAmbientGroundTint(r, g, b)` | A tint of its own for the light from below (the lit ground), 0 to 4 each: the tint above then colours only the light from above (a warm floor bounce under a cool sky). 0, 0, 0 goes back to one tint for both. Also `Engine.Sky.SetAmbientGroundTint(r, g, b)` and the room's `skyLightGroundTint`. Since 29 October 2026. |

A script's own ambient colour still wins over the sky's: while one is set, the hemisphere sky light
is off. Models in a model layer with studio lighting keep the studio's light.

### Sun shafts in the haze (17 October 2026)

With volumetric fog on (a room with haze or fog, `RenderSetQuality("high")` or above), the sun
lights the fog and whatever shades it carves shafts. By default the haze scatters light almost
evenly and each fog cell takes one shadow sample, so indoors, with dense haze, the shafts are
hard-edged bright and dark slabs. Three settings make them look like sunbeams in dusty air; the
defaults draw the fog exactly as before.

| Setting | Default | Use |
|---|---|---|
| `Engine.Rendering.FogShaftAnisotropy` | 0.25 | 0 to 0.95: how strongly the haze scatters forward. Above 0.25 shafts glow towards the sun and fade side-on or with the sun behind (capped at six times side-on brightness). About 0.7 for sunbeams indoors. |
| `Engine.Rendering.FogShaftFloor` | 0.35 | 0 to 1: fog in shadow against fog in sunlight. Higher is subtler shafts. |
| `Engine.Rendering.FogShaftSoftness` | 0 | 0 to 4 metres: the width of a shaft's soft edge (five shadow samples across the light instead of one). |

```
Engine.Rendering.FogShaftAnisotropy = 0.7;
Engine.Rendering.FogShaftFloor = 0.42;
Engine.Rendering.FogShaftSoftness = 0.9;
```

They hold until changed, for every room. Cathedral Test uses the values above with
`visibilityKilometres` 0.6. Softness costs four more shadow lookups per fog cell.

### Eye adaptation (auto exposure)

With **Auto exposure** on (`autoExposure`), the exposure follows the brightness of the scene: walk
from sunlight into a dark hall and the picture brightens over a couple of seconds; walk out again
and it darkens, faster, as eyes do. Each frame the engine measures the HDR scene before the sky,
fog and grade are put on it (the log-average brightness over a 64 by 32 grid, the centre counting
more, and sky pixels measured as the sky is drawn) and moves the exposure towards showing that
average at the key. The GUI and held view models are drawn after the measurement, so a bright HUD
never darkens the world. The exposure set by `Engine.Rendering.Exposure` still multiplies on top,
as a compensation. When a room loads, or a script calls `RenderResetAutoExposure()` after a cut,
the exposure starts from the scene as it is instead of adjusting.

| Setting | Key | Default | Use |
|---|---|---|---|
| Auto exposure | `autoExposure` | off | Turns eye adaptation on. |
| Exposure key | `autoExposureKey` | 0.18 | The brightness an average scene is shown at; 0.18 is mid grey. Higher is brighter. |
| Darken over (s) | `autoExposureDarkenSeconds` | 0.8 | How long the picture takes to darken (about 95% of the way) when the scene gets brighter. 0 is at once. |
| Brighten over (s) | `autoExposureBrightenSeconds` | 2.5 | How long it takes to brighten when the scene gets darker. |
| Darkest (stops) | `autoExposureMinEv` | -4 | The most it darkens the picture: -4 is a sixteenth. |
| Brightest (stops) | `autoExposureMaxEv` | 4 | The most it brightens the picture: 4 is sixteen times. A night stays dark below its limit. |
| (file and scripts only) | `autoExposureCenterWeight` | 0.5 | 0 measures the whole picture evenly, 1 counts its centre most. |

| Command | What it does |
|---|---|
| `RenderSetAutoExposure(on, key, adaptSeconds, minEv, maxEv)` | Turns it on or off with a key, one adaptation time for both directions, and the limits in stops. |
| `RenderSetAutoExposureSpeed(darkenSeconds, brightenSeconds)` | The two adaptation times. |
| `RenderSetAutoExposureMetering(centerWeight)` | 0 even, 1 centre weighted. |
| `RenderResetAutoExposure()` | Jump to the scene at once (a camera cut, a teleport). |
| `RenderGetAutoExposure() -> bool` | Whether it is on. |
| `Engine.Rendering.AutoExposure(key, adaptSeconds)` | Turns it on with a key and one adaptation time. |
| `Engine.Rendering.AutoExposureEnabled` | On or off. |

The adapted value stays on the graphics card: three small passes (a 64 by 32 grid, an 8 by 4 grid
and one texel) and one multiply in the final composite. Measured on Direct3D 11 at 1920 by 1061
(a GeForce RTX 5060 Ti), the frame took 0.009 ms longer with it on (medians of 240 frames each
way). Direct3D 11 and 12, Vulkan and OpenGL run it; the CPU renderer has no eye adaptation and
draws the room as if it were off. Model layers (the
first-person layer, models in the GUI) are drawn for display without the composite, so neither
exposure applies to them, as before. Fog's own light is not measured, so a dense fog is exposed a
little brighter than it would be.

### For a game working round the dark blue shade

A game that lifted its shade by hand (a higher exposure, a raised ambient scale, a weaker shadow, a
white balance in a post effect) can take those out and set, in the room:
`"skyLight": "hemisphere"`, `"skyLightStrength": 1.5`, `"autoExposure": true`, with the exposure
back at 1, the shadow strength at 1 and no white balance.

`Build.bat --test sky-light-exposure` checks the settings from room file to frame, the commands,
the sky light's numbers in both colour pipelines, and pixels on Direct3D 11 and 12, Vulkan and
OpenGL: shade brighter and less blue with sunlit ground neutral, the exposure moving towards the
key over time in both directions, its limits, and frames unchanged with both off. On the CPU
renderer it checks that frames are drawn and that eye adaptation leaves them unchanged. It also
measures the cost at 1080p on Direct3D 11.

## Foliage

- **Grass.** Meadow grass, tall grass and reeds were flat-coloured straight spikes. Near the camera
  each blade is now a tapering strip that bends outward, dark at the root and lighter at the tip.
  Tall grass is a little shorter and less bright.
- **Wildflowers.** Each flower is a tipped cup of petals round a yellow heart, with a leaf on the
  stem, in place of two crossed diamonds.
- **A terrain can refuse kinds of plant.** `excludedSpecies` in a terrain's foliage settings lists
  kinds it does not grow; another kind from the same preset grows where each would have, so the
  ground is as full as it was. There is no editor field for it yet.

To give a terrain its own ground colours, give its paint layers Images (`Layer(slot, name, image,
tileMetres)`, or the Terrain editor's layers): a layered terrain takes its colour from those
Images. The fixed grass, earth, rock and moss colours are only what an unlayered terrain falls
back to.

## Movers, first-person views and moving parts

For a character controller written in script, and for first-person arms, weapons and vehicles.
The shape queries never hit the calling instance's own collider and are read back with the
`PhysicsRaycastHit*` commands (the point is where the shape touches; the shape's centre is then
at the start plus the direction times the distance). They work in 3D rooms, in metres.

| Command | What it does |
|---|---|
| `PhysicsSphereCast(x, y, z, radius, dx, dy, dz, maxDistance)` | Distance a sphere moves before it touches something; 0 when it starts touching, -1 for nothing. |
| `PhysicsCapsuleCast(x, y, z, radius, height, dx, dy, dz, maxDistance)` | The same for an upright capsule centred on x, y, z, `height` tall end to end. |
| `PhysicsSphereRest(x, z, radius, maxCentreY)` | Height of a sphere's centre where it comes to rest when lowered from `maxCentreY`, on a face, an edge or a corner (a rounded foot on a kerb); -1000000 when nothing is below. |
| `PhysicsOverlapCapsule(x, y, z, radius, height)` | Whether an upright capsule there overlaps or touches anything: does a crouched character fit standing up? |
| `SetCameraRoll(degrees)`, `GetCameraRoll()` | Roll the 3D camera about its direction of view; positive leans it right. It stays until set again, and a shake's tilt adds to it. |
| `ModelSetCastShadows(enabled)`, `InstanceSetCastShadows(id, enabled)` | Whether a model casts a shadow: first-person arms and weapons should not. |
| `AnimationBoneSetTranslation(bone, x, y, z)`, `AnimationBoneClearTranslation(bone)` | Move a bone by a local offset (its parent's space, model units) after animation, as `AnimationBoneSetRotation` turns it: a shoulder slid towards a grip. |
| `ModelNodeSetRotation(node, pitch, yaw, roll)`, `ModelNodeSetTranslation(node, x, y, z)`, `ModelNodeClear(node)` | Turn or move one of this instance's model nodes from its authored pose (propellers, a gun's bolt, slide or magazine); the nodes below it follow. Skinned meshes follow their bones instead. Each instance keeps its own poses, however many share the model. |
| `InstanceModelNodeSetRotation(id, node, ...)`, `InstanceModelNodeSetTranslation(id, node, ...)`, `InstanceModelNodeClear(id, node)` | The same for another instance, without a `with` block. |

Bone turns and offsets set on another instance through `with` reach its model at the end of the
block, also on an instance that has no events of its own.

A kinematic character, built on those queries, saves writing the mover yourself. One per
instance; its position is its feet, and the script copies it to the instance:

| Command | What it does |
|---|---|
| `CharacterCreate(radius, height, stepHeight, maxSlopeDegrees)` | Give this instance a character at its own position. Feet placed exactly on (or a little into) the ground are lifted onto it on the first move, so there is no margin to leave. |
| `CharacterMove(dx, dy, dz)` | Move as far as the world allows: slides along walls, climbs steps up to `stepHeight`, follows the ground down slopes and steps. Returns flags: 1 grounded, 2 ceiling, 4 wall, 8 climbed a step. |
| `CharacterX()`, `CharacterY()`, `CharacterZ()`, `CharacterSetPosition(x, y, z)` | Where its feet are; a teleport. |
| `CharacterGrounded()`, `CharacterGroundNormalY()` | Whether it stands on walkable ground, and how upright that ground is. |
| `CharacterSetHeight(height)`, `CharacterFits(height)` | Crouch or stand (false and unchanged when standing would not fit). |
| `CharacterDestroy()` | Remove it. |

In a player's Step event, for example:

```pgsl
CharacterMove(moveX * DeltaTime, fallSpeed * DeltaTime, moveZ * DeltaTime);
if (CharacterGrounded()) { fallSpeed = 0; } else { fallSpeed = fallSpeed - 9.8 * DeltaTime; }
x = CharacterX(); y = CharacterY(); z = CharacterZ();
```

**Shape casts against Mesh colliders (fixed 10 October 2026).** `PhysicsSphereCast`,
`PhysicsCapsuleCast`, `PhysicsOverlapCapsule` and the character commands missed a Mesh collider's
face (a building fitted to its model, a large terrain's tiles) near the end of any cast shorter
than about a metre: a sphere 0.1 m from a wall was not found by a cast shorter than 0.32 m. A
character moving a few centimetres a frame therefore met mesh walls and ground only once inside
them, and was then held there. Casts now find them; casts against boxes, spheres and capsules
return what they did.

## Colliders where things move, far from the camera

In a large world some colliders exist only near what can touch them: streamed scenery, a large
terrain's tiles and solid scatter (see [LargeWorlds.md](LargeWorlds.md)). In a room with a scenery
distance, scenery is solid within the room's `sceneryCollisionDistance` (64 m unless set) of
every moving physics body and every script character made with `CharacterCreate`, however far
they are from the camera. Beyond the scenery distance only the building's collider is made there:
nothing is drawn, no model instance or script is created, and every copy of a model shares one
collision mesh. So a bot 1.5 km from the camera that `CharacterMove`s into a wall stops at it, and
its rays meet the wall. Rooms without a scenery distance are unchanged.

| Command | What it does |
|---|---|
| `PhysicsAddCollisionFocus(id, radius)` | Keep these colliders within `radius` metres (up to 1000) of instance `id` as well, however far from the camera: streamed scenery, terrain tiles (at that radius) and solid scatter (at its usual 14 m). For something moved without a body: a far bot's terrain under `CharacterMove`, a vehicle steered by rays. False when there is no such instance or the radius is not positive. |
| `PhysicsRemoveCollisionFocus(id)` | Stop keeping colliders around it; false when it was not a collision focus. A destroyed instance stops on its own. |

```pgsl
// Create event of a bot: a capsule, solid ground and buildings wherever it goes.
CharacterCreate(0.4, 1.8, 0.35, 45);
PhysicsAddCollisionFocus(id, 32);
```

A character counts for scenery without a focus; for a large terrain's ground far from the camera
it needs one (or a body). Scenery colliders are looked at every eighth frame, so one teleported
beside a building meets it up to eight frames later. At most 512 places are followed
at once (foci first, then characters, then bodies) and at most 2048 stand-in colliders exist; the
debug screen's `sceneryColliders=` line counts the scenery colliders near the camera, the far
ones and the places followed.

## Model layers and models in the GUI

A model layer is a model drawn over the world instead of in it. Layers 1 to 8 follow the game
camera, each with its own field of view and near plane, and are drawn over the world and under the
GUI in number order (layer 2 covers layer 1). A model in a layer is never cut by a wall, does not
zoom when the world's camera zooms, casts no shadow, and is lit by the world like any model. Use one
for anything that must stay in front: a held tool or weapon, a cockpit or dashboard, a compass, a
map in the hand, a magnifying glass. What goes in each layer is up to the game.

| Command | What it does |
|---|---|
| `ModelSetLayer(layer)`, `InstanceSetModelLayer(id, layer)` | Draw a model in a layer: 0 is the world, 1 to 8 are drawn over it. |
| `ModelLayerSetFov(layer, degrees)`, `ModelLayerSetNear(layer, distance)` | A layer's field of view (0 = the camera's) and near plane (default 0.01). |
| `ModelLayerSetVisible(layer, visible)` | Hide or show a whole layer; its models keep animating. |
| `ModelSetViewLayer(enabled)`, `InstanceSetViewLayer(id, enabled)`, `ViewLayerSetFov(degrees)`, `ViewLayerSetNear(distance)` | Shorter names for layer 1. |
| `DrawModelGui(model, x, y, width, height, yaw, pitch, zoom)` | In Draw GUI: a Model drawn into a rectangle, turned by yaw and pitch and framed to fit (zoom 1), layered with the other GUI drawing in call order (an inventory portrait, a character on a menu). |
| `DrawModelGuiPose(model, x, y, width, height, yaw, pitch, zoom, clip, time)` | The same, posed at an animation clip's time. |

A GUI model is lit by its own studio light (a key light from the upper left and a soft ambient),
never by the room's sun, sky, lamps or shadows, so it looks the same at midnight as at noon. It is
drawn while the next frame's 3D is drawn, so it appears one frame after the first call and follows
changes a frame late. The same sequence of calls each frame keeps each model in
its own image; up to 64 are drawn.

## Meshes a script builds

A script can build a mesh of its own (a voxel chunk, a procedural rock, a trail) and draw it as one
draw instead of thousands of instances. A mesh is uploaded again only when drawn after it changed.
It holds up to 65,535 vertices; a bigger world is split into chunks. A texture rectangle of all
zeros (`0, 0, 0, 0`) means the whole texture; a single point (`u0 = u1`, `v0 = v1`) samples one
texel, such as a distant block's colour from an atlas.

| Command | What it does |
|---|---|
| `MeshCreate()`, `MeshClear(mesh)`, `MeshDestroy(mesh)` | A new empty mesh, emptied to build again, or freed. |
| `MeshAddVertex(mesh, x, y, z, nx, ny, nz, u, v, r, g, b, a)` | A vertex (position, normal, texture coordinate, colour 0-255, alpha 0-1); returns its index, or -1 when the mesh is full. |
| `MeshAddTriangle(mesh, a, b, c)` | A triangle of three vertex indices, counter-clockwise seen from its front. |
| `MeshAddCube(mesh, x, y, z, size, faces, r, g, b, u0, v0, u1, v1)` | A cube's chosen faces centred on a point: `faces` adds 1 (+X), 2 (-X), 4 (+Y), 8 (-Y), 16 (+Z) and 32 (-Z), 63 for all, so a voxel adds only the faces that touch air. `u0, v0` to `u1, v1` is its tile of a texture atlas. Returns the faces added. |
| `MeshAddCubeTiles(mesh, x, y, z, size, faces, r, g, b, top u0 v0 u1 v1, side u0 v0 u1 v1, bottom u0 v0 u1 v1)` | The same with its own atlas tile for the top, the four sides and the bottom (a grass block, a log). |
| `MeshAddQuad(mesh, x0, y0, z0, x1, y1, z1, x2, y2, z2, x3, y3, z3, nx, ny, nz, u0, v0, u1, v1, r, g, b, a)` | Any textured four-cornered face in one call (crossed plants, decals, trails): corners in order around it, turning like `MeshAddTriangle`; returns the first corner's index. |
| `MeshAddQuadColors(mesh, corners..., nx, ny, nz, u0, v0, u1, v1, r0, g0, b0, a0, r1, ..., a3, flip)` | `MeshAddQuad` with its own colour and alpha at each corner (baked light, ambient occlusion, gradients); `flip` 1 splits it along the other diagonal so the colours blend evenly. |
| `MeshAddQuadsFromList(mesh, list)` | Many quads in one call from a DsList holding 36 numbers for each, in `MeshAddQuadColors`' order (four corners, normal, `u0 v0 u1 v1`, four colours with alpha, `flip`); returns the quads added. A [worker job](#script-functions-on-worker-threads) can work a chunk's faces out into a list, and the game's thread adds them all at once: 100 to 370 ns a face, against 360 to 740 ns for a `MeshAddQuadColors` call from a script (development PC, 8 Oct 2026). |
| `MeshAddVerticesFromList(mesh, vertices, triangles)` | Many vertices (12 numbers each, `MeshAddVertex`'s order) and triangles (three indices each, counted from the first vertex this call adds) in one call; a vertex that is not a number is skipped with its triangles. Returns the vertices added. |
| `MeshVertexCount(mesh)`, `MeshTriangleCount(mesh)` | Its size. |
| `MeshSetUploadBudget(milliseconds)`, `MeshIsUploaded(mesh)` | How long a frame may spend sending new or changed meshes to the GPU (default 4 ms; 0 = no limit). The rest go on later frames, at least one each frame: a changed mesh draws its previous build meanwhile and a new one appears when its turn comes, so building many at once never stalls a frame. `MeshIsUploaded` says whether a mesh's latest build is drawn yet. |
| `DrawMesh3D(mesh, x, y, z, image)`, `DrawMesh3DTransform(mesh, x, y, z, sx, sy, sz, yaw, image)` | In a Draw event of a 3D room: the mesh at a place, textured by an Image (empty for none), tinted by the instance's image blend and alpha. |
| `DrawMeshShader3D(mesh, shader, x, y, z, sx, sy, sz, yaw, image)` | Draw it through a mesh Shader resource, with the instance's `ShaderSetParameter` / `ShaderSetVector` values and the textures the shader declares. |
| `DrawMeshSetShadows(cast, receive)`, `DrawMeshSetGlow(amount)`, `DrawMeshSetFog(enabled)`, `DrawMeshSetCull(enabled)`, `DrawMeshSetTransparent(enabled)`, `DrawMeshResetState()` | How this instance's later mesh draws look, until changed: shadows, self-lit glow (0 to 1 and beyond), fog, both sides drawn (cull off), blended at full alpha. |
| `InstanceSetMeshCollider(id, mesh)` | A fixed collider of the mesh's triangles for an instance; call it again after the mesh changes. |

```pgsl
// Create: a strip of three blocks, with no faces between them.
m = MeshCreate();
MeshAddCube(m, -1, 0, 0, 1, 63 - 1, 255, 255, 255, 0, 0, 0.25, 0.25);
MeshAddCube(m,  0, 0, 0, 1, 63 - 1 - 2, 255, 255, 255, 0, 0, 0.25, 0.25);
MeshAddCube(m,  1, 0, 0, 1, 63 - 2, 255, 255, 255, 0, 0, 0.25, 0.25);
InstanceSetMeshCollider(id, m);
// Draw:
DrawMesh3D(m, x, y, z, "Blocks");
```

### Streaming meshes and memory

`MeshDestroy` gives the renderer's copy back at once: its GPU buffers, and the previous build's
while a rebuild still waits for its upload turn. The mesh's own vertex and index storage is kept
(up to 48 MB in all) and handed to the next `MeshCreate`, so a game that streams chunks by
destroying and creating meshes no longer grows a new list for every chunk, and `MeshDestroy` with
`MeshCreate` is now as cheap as keeping a pool of cleared meshes. A mesh that is only cleared
(`MeshClear`) keeps its last GPU copy until it is built and drawn again, because a mesh being
rebuilt is drawn as it was until its new build is uploaded: a game's pool of cleared meshes holds
the GPU memory of everything in it, and destroying meshes that are no longer needed frees it.
`MeshAddQuadsFromList` and `MeshAddVerticesFromList` make room for all they add at once.

Checked by `Build.bat --test mesh-soak` (9 Oct 2026): 2,304 chunk-sized meshes (12,000 vertices)
made, drawn and destroyed over 72 frames on Direct3D 11 and 12, Vulkan and OpenGL, and 576 smaller
ones on Software. On every renderer the live GPU buffers went back to where they started once the
meshes were destroyed (also for meshes destroyed while a rebuild waited for its upload turn), and
the managed heap and private memory stayed level. Building a round of 32 chunks allocated 52 MB
before and nothing now.

## Noise

Noise for generated terrain, caves, clouds, textures and wobble, worked out natively. The same
arguments give the same number on every PC, graphics backend and run: only additions,
multiplications, `Floor` and integer hashing are used (no sine), so a world generated from a seed
is the same world everywhere. Any number is a seed, fractions included (`0.5` and `0.7` differ).

| Command | What it does |
|---|---|
| `Noise2D(x, y, seed)`, `Noise3D(x, y, z, seed)` | Smooth gradient (Perlin) noise, -1 to 1. Hills and hollows are about one unit apart, so scale positions down (`Noise2D(x / 32, z / 32, seed)`). Each seed shifts the lattice, so whole-number positions still vary. |
| `ValueNoise2D(x, y, seed)`, `ValueNoise3D(x, y, z, seed)` | Smooth value noise, 0 to 1. At whole-number positions it is a repeatable random number per point (where a tree goes, which ore). |
| `Hash2(x, y, salt)`, `Hash3(x, y, z, salt)` | A repeatable random number from 0 up to (never reaching) 1 for a whole-number point, the one-call form of the above: fractions are dropped down (`Floor`), so `Hash2(x, y, salt)` is exactly `ValueNoise2D(Floor(x), Floor(y), salt)` and `Hash3` is `ValueNoise3D` at `Floor` of each. For ore veins, scattered trees, a block's random variant. Not a number gives 0. |
| `FractalNoise2D(x, y, seed, octaves, lacunarity, gain)`, `FractalNoise3D(x, y, z, seed, octaves, lacunarity, gain)` | Several octaves of gradient noise summed (fBm), -1 to 1: each octave `lacunarity` times finer (2 is usual) and `gain` times weaker (0.5 is usual); 1 to 16 octaves. One octave is exactly `Noise2D` / `Noise3D`. |
| `NoiseFillGrid(grid, x0, y0, step, seed, octaves, lacunarity, gain, scale, offset)` | A whole DsGrid in one call: cell (i, j) becomes `offset + scale * FractalNoise2D(x0 + i * step, y0 + j * step, seed, octaves, lacunarity, gain)`, exactly what that call gives. Returns the cells filled (0 for a bad grid). |
| `NoiseFillGrid3D(grid, x0, y0, z0, step, plane, seed, octaves, lacunarity, gain, scale, offset)` | A flat slice of `FractalNoise3D`: `plane` `"xy"`, `"xz"` or `"yz"` names the axes `i` and `j` step along from `(x0, y0, z0)`; the third stays put (one layer of a cave field). |

The hash behind `Hash2` / `Hash3` (and the value noise), all in wrapping unsigned 64-bit
arithmetic, so another program can reproduce it bit for bit: `Mix(h)` is the SplitMix64 finisher
(`h ^= h >> 30; h *= 0xBF58476D1CE4E5B9; h ^= h >> 27; h *= 0x94D049BB133111EB; h ^= h >> 31`); the salt
becomes `s = Mix(bits ^ 0xD6E8FEB86659FD93)`, where `bits` are the salt's 64 IEEE-754 bits (with -0
taken as 0); a point's hash is `Mix(s + x * 0x9E3779B97F4A7C15 + y * 0xC2B2AE3D27D4EB4F)`, plus
`z * 0x165667B19E3779F9` in 3D, with `x`, `y`, `z` the floored coordinates as 64-bit two's-complement
integers; the number is the top 53 bits divided by 2^53. `Hash2(12, -34, 5)` is 0.8856642354882706
on every machine (the `pgsl-logic` test checks it). `Hash2` costs 3.3 ns and `Hash3` 4 ns called from
C#, 22 and 27 ns in a script loop, against about 150 ns for a lattice hash written in script.

Measured on the development PC's performance cores (8 Oct 2026, `--test pgsl-logic`, called from
C#; the fastest of several runs, other runs up to twice as long): `Noise2D` 13 ns, `Noise3D` 20 ns,
`ValueNoise2D` 14 ns; `NoiseFillGrid` 15 ns a cell with one octave and 53 ns with four,
`NoiseFillGrid3D` 61 ns a cell with three. From a script loop, value
noise written in script (four hashes with a `Sin` each) took 0.8 to 1.2 microseconds a sample,
`FractalNoise2D` with four octaves 0.08 to 0.2. A 16 x 16 height map with four octaves is about
14 microseconds with `NoiseFillGrid`.

```pgsl
// Heights of a 16 x 16 chunk at chunk (cx, cz): rolling land 40 to 88 blocks high.
heights = DsGridCreate(16, 16);
NoiseFillGrid(heights, cx * 16 / 96, cz * 16 / 96, 1 / 96, worldSeed, 4, 2, 0.5, 24, 64);
DsGridFloorRegion(heights, 0, 0, 15, 15);
```

### Whole regions of a grid or list

A loop that reads or writes every cell of a grid costs about 50 ns a call in a script; these do a
region natively. Corners are inclusive and 0-based, as in `DsGridSetRegion`; cells outside the
grid are skipped (or read as 0); a bad handle does nothing and returns 0 (-1 for a find).

| Command | What it does |
|---|---|
| `DsGridCopyRegion(destination, dx, dy, source, x1, y1, x2, y2)` | Copy a region into another grid (or the same one, overlapping is fine) with its top-left at `(dx, dy)`; returns the cells copied. |
| `DsGridToList(grid, x1, y1, x2, y2, list)`, `DsGridFromList(grid, x1, y1, x2, y2, list)` | A region to a list row by row (the list's entries are replaced), or a list back into a region until the list runs out; both return the entries moved. |
| `DsGridCount(grid, x1, y1, x2, y2, value)` | How many cells hold the value. |
| `DsGridFind(grid, x1, y1, x2, y2, value)`, `DsGridFindOther(grid, ...)` | The first cell holding (or not holding) the value, as `x + y * width`, or -1. Rows are searched from `y1` towards `y2` and each row from `x1` towards `x2`, so `DsGridFindOther(column, 0, 127, 0, 0, AIR)` is the highest block that is not air. |
| `DsGridAddRegion`, `DsGridMultiplyRegion(grid, x1, y1, x2, y2, value)` | Add to, or multiply, every cell of a region. |
| `DsGridClampRegion(grid, x1, y1, x2, y2, min, max)`, `DsGridFloorRegion(grid, x1, y1, x2, y2)` | Keep every cell between two values, or round every cell down. |
| `DsGridAddGrid(destination, source, factor)` | `destination += source * factor` cell by cell where both have the cell (layering noise fields, a mask); returns the cells changed. |
| `DsListFill(list, count, value)`, `DsListCopy(destination, source)` | A list of `count` copies of a number, or one list's entries replaced by another's. |
| `DsListReserve(list, count)` | Room for `count` entries (up to 1 000 000), so adding them grows nothing: a list a mesh is built from, filled again for every chunk. |

Storing a number in a list (or a map, stack or queue) costs no allocation for whole numbers from
-1 024 to 65 535, nor for a number stored lately: the last box made for each of 32 768 slots is
reused when the same number is stored again. A mesh builder's texture coordinates, light levels
and half-block corners repeat face after face, so 8 000 such adds to a reserved list allocate
nothing (before 12 Oct 2026 each fraction was a new 24-byte box).

Already there before: `DsGridSetRegion` (fill a region with one value), `DsGridClear` (fill the
grid), `DsGridCopy` (a whole grid), `DsGridGetSum` / `GetMax` / `GetMin` and `DsGridValueExists`.

### Compact grids: small whole numbers in 1, 2 or 4 bytes a cell

A grid's cells are numbers of 8 bytes each. A block world's ids and light levels are small whole
numbers, so `DsGridCreate(width, height, kind)` makes a grid that stores them compactly; without a
kind (or with `"f64"`) a grid holds numbers as it always has.

| Kind | Holds | Bytes a cell |
|---|---|---:|
| (none), `"f64"` | numbers, as before | 8 |
| `"u8"` | whole numbers 0 to 255 (light levels, small flags) | 1 |
| `"u16"` | whole numbers 0 to 65 535 (block ids with flag bits) | 2 |
| `"i32"` | whole numbers -2 147 483 648 to 2 147 483 647 | 4 |
| `"f32"` | single-precision numbers: about 7 significant digits, whole numbers exact to 16 777 216 | 4 |

Every grid command works on every kind, unchanged: `DsGridGet` / `DsGridSet` / `DsGridAdd` /
`DsGridMultiply`, the region commands above, `DsGridToList` / `DsGridFromList`, `DsGridCopyRegion`
(between kinds too: each cell is converted to the destination's kind), `DsGridAddGrid`,
`NoiseFillGrid` / `NoiseFillGrid3D`, `DsGridResize` (keeps the kind), sums and finds, and giving a grid
to a worker job (`JobScriptGrid`, `JobScriptShareAll`, copyBack). Reading a cell gives a number.
**Writing** a number to a whole-number kind drops its fraction (towards zero: 7.9 is 7, -2.7 is -2)
and clamps it to the kind's range: 300 in a `u8` grid is 255, -1 is 0, and NaN is 0. So a light level
taken below 0 stays 0 rather than wrapping round to 255, and `DsGridAdd(light, x, y, -1)` is safe.
`DsGridCopy(destination, source)` makes the destination an exact copy, its kind included.

A grid may take up to 8 MB of cells (and 4096 a side): a number grid up to 1 000 000 cells, as
before, a `u16` grid 4 000 000 and a `u8` grid 8 000 000. A 4096 x 256 strip of a 256-high world
is 2 MB as `u16` (8 MB as numbers, over the limit); a world of 16 such strips of blocks (`u16`) and
16 of light (`u8`) is 48 MB instead of 256 MB. `DsGridCreate` returns 0 for a grid over the limit
or a kind it does not know.

| Command | What it does |
|---|---|
| `DsGridCreate(width, height, kind)` | A grid of that kind: `"u8"`, `"u16"`, `"i32"`, `"f32"` or `"f64"` (any case); 0 when the kind is unknown or the grid too big. |
| `DsGridKind(grid)` | `"f64"`, `"u8"`, `"u16"`, `"i32"` or `"f32"`; empty when there is no such grid. |
| `DsGridBytes(grid)` | The bytes its cells take (width x height x the kind's size). |
| `DsGridGetColumnToList(grid, x, y0, y1, list)` | Column `x`'s cells from row `y0` to `y1`, in rising row order, into a list (its entries replaced): `DsGridToList(grid, x, y0, x, y1, list)`. Returns the entries written. |
| `DsGridSetColumnFromList(grid, x, y0, list)` | The list's entries into column `x`, one row each from `y0` down (y0, y0 + 1, ...), until the list runs out; rows outside the grid are skipped. Returns the entries read. |

`DsGridGet` and `DsGridSet` stay on the VM's direct call path for every kind (a command taking up to
four numbers is called through a delegate, with no arrays in between). `Build.bat --test vm-speed`
reports, for each kind, the bytes a cell takes and what a cell read, a cell write and a 256-high
column through a list cost. Measured on 10 Oct 2026 on the performance cores: from a script loop,
`DsGridGet` and `DsGridSet` cost 42 to 48 ns on every kind, as on a number grid (42 and 43 ns), and
the grid-and-list workload took 0.477 ms (0.48 ms on 8 Oct, before compact grids). Called from C#, a
cell read is 10 ns on a number grid and 11.5 to 12 ns on a compact one; a 256-high column into a list
1.1 microseconds (numbers) to 1.5 (compact), and from a list 0.7 to 1.3 microseconds.

```pgsl
// A 256-high world in strips: block ids in u16 grids, light in u8 grids.
var blocks = []; var light = [];
for (var s = 0; s < 16; s = s + 1) {
    blocks[s] = DsGridCreate(4096, 256, "u16");
    light[s] = DsGridCreate(4096, 256, "u8");
}
DsGridSet(blocks[s], column, y, STONE | FLAG_NATURAL);   // 3071 plus flag bits fits in 16 bits
DsGridAdd(light[s], column, y, -1);                       // never below 0
```

### Priority queues

A priority queue (`ds_priority` in GameMaker) hands back its entries smallest priority first: the
open set of an A* search, events by time, jobs by urgency. It is a binary heap kept in one array, so
adding and taking out cost a few comparisons for each doubling of its size and allocate nothing once
the queue has grown to its largest size. Values are numbers or text, as in a list.

| Command | What it does |
|---|---|
| `DsPriorityCreate()` | A new empty queue; returns its handle. |
| `DsPriorityAdd(queue, value, priority)`, `DsPriorityAddString(queue, text, priority)` | Add a number or text with a priority (any number; smaller comes out first). Up to 1 000 000 entries. |
| `DsPriorityDeleteMin(queue)`, `DsPriorityDeleteMinString(queue)` | Take out the entry of smallest priority and return it as a number (0 when empty) or as text (empty). |
| `DsPriorityFindMin(queue)`, `DsPriorityFindMinString(queue)` | The entry `DsPriorityDeleteMin` would take out, left in the queue. |
| `DsPriorityMinPriority(queue)` | That entry's priority (0 when empty). |
| `DsPrioritySize(queue)`, `DsPriorityEmpty(queue)` | How many entries; whether there are none. |
| `DsPriorityClear(queue)`, `DsPriorityDestroy(queue)` | Remove every entry; release the queue. |

**Ties:** among equal priorities, the entry added first comes out first (first in, first out), on
every machine, so an A* search whose nodes share an f cost explores them in the order it found them
and gives the same path every time. A priority that is not a number counts as the largest of all
and comes out last. A text entry read as a number is read as `Real` reads text; a number read as
text is `StringOf` of it.

A worker job can be given a queue like a list: `JobScriptPriority(job, queue, copyBack)` (or
`JobScriptShareAll`, which shares queues too). The job sees the queue as it was when it started, a
change by either side makes that side's own copy, and copyBack brings the job's queue back at
`JobTake` if the job changed it.

Measured on 10 Oct 2026 (`--test vm-speed`, performance cores): from C#, 100 000 entries are added
in 2.4 ms (24 ns each) and taken out again in 16 ms (164 ns each), with nothing allocated once the
queue has grown; from a script, an add and a delete-min on a queue of 1 000 cost 176 ns together.

```pgsl
// A* on a grid: the open set by f = g + h, node ids x + y * width.
open = DsPriorityCreate();
DsPriorityAdd(open, startX + startY * w, Abs(goalX - startX) + Abs(goalY - startY));
while (!DsPriorityEmpty(open)) {
    node = DsPriorityDeleteMin(open);
    // ... for each neighbour that improves its g: DsPriorityAdd(open, neighbour, g + 1 + h);
}
```

## Script functions on worker threads

A function of the project's Scripts can run as a job on a worker thread, so generating a chunk, a
map or a path does not hold up the frame. The job runs on a VM of its own with a context of its
own, and sees the grids, lists and maps it is given **as they were when it started** (shared with
it, not copied: see [a job that reads the game's own data](#a-job-that-reads-the-games-own-data)):
the game may change its own meanwhile, and nothing the job does reaches the game until the script
takes the result with `JobTake`. Inside the job each grid or list keeps its handle, so the same
function also runs directly on the game's thread and gives the same result; the same arguments and
data always give the same result.

A job may use maths, text, noise, grids, lists, maps, stacks, queues, named arrays, JSON and its
own variables (`VariableSet`). Anything that reaches the game or shared state (instances, `with`,
drawing, meshes, sound, files, input, the clock, `Random`, `Choose`, `Print`, global variables,
running a Script by name) stops the job with "*X* is not available in a worker job". A name the
job never set is an error too ("'*name*' has no value in this job"), not 0 as in an event: an
instance's variables are not there unless the job is given them (`JobScriptShareAll` gives them
all; `JobScriptVariable` one by name), or pass them as arguments. A grid, list or map that was not
given to the job reads as one that does not exist (0, as a destroyed one does). Functions written
in an Object's events are not available to jobs; put the function in a Script.

| Command | What it does |
|---|---|
| `JobScriptCreate(function)` | A prepared job for a function of the project's Scripts (as they are now); 0 when there is no such function (`JobLastError` says why). |
| `JobScriptGrid(job, grid, copyBack)`, `JobScriptList(job, list, copyBack)`, `JobScriptMap(job, map, copyBack)` | Give the job a grid, list or map as it is when the job starts (shared, not copied); with `copyBack` true, `JobTake` puts the job's version in its place (size included) if the job changed it. |
| `JobScriptGridRegion(job, grid, x1, y1, x2, y2, copyBack)` | Give the job a grid it fills part of: it reads the whole grid as it is when the job starts, and with `copyBack` true `JobTake` writes only the rectangle (corners included) into the game's grid, keeping every other cell as the game has it. See [several jobs filling one grid](#several-jobs-filling-one-grid). |
| `JobScriptShareAll(job)` | Give the job every grid, list, map, stack and queue of this Object and its instance variables (numbers, text, true/false), as they are when it starts. See [a job that reads the game's own data](#a-job-that-reads-the-games-own-data). |
| `JobScriptVariable(job, name, value)`, `JobScriptVariableText(job, name, text)` | Give the job a variable of its own by name (a seed, a sea level, a grid's handle), so a function that reads that instance variable runs unchanged in the job. Built-in instance variables (`x`, `speed`...) cannot be given; pass those as arguments. |
| `JobScriptBudget(job, instructions)` | Instructions the job may run in all, every call counted (100 000 000 unless set, 1 000 to 2 000 000 000). The per-call limit of events does not apply. |
| `JobScriptStart(job, arguments...)` | Start it with the function's arguments (numbers, true/false or text). Jobs run in turn on worker threads of their own, as many as all but two of the processors, below the game's own threads in priority. |
| `JobRunScript(function, arguments...)` | Create and start in one, for a job that needs no grids or lists; its result is read with `JobResultNumber` / `JobResultString` / `JobResultBool`. |
| `JobStatus(job)` | `prepared`, `queued`, `running`, `succeeded`, `failed` (`JobError` says why, with the line) or `cancelled`. |
| `JobTake(job)` | Once it has succeeded: the grids, lists and maps marked `copyBack` that the job changed replace the Object's own (once). One the job left alone stays as the game has it. |
| `JobCancel(job)`, `JobRelease(job)` | Stop it (a running job stops within a fraction of a millisecond), or let go of the handle and its data. |

An Object holds up to 16 jobs at a time and the game 32 (with file jobs); release each when done.
An Object's jobs are cancelled when it is destroyed.

```pgsl
// Library Script "Terrain": works on its own data only, so it can run as a job.
function TerrainHeights(heights, cx, cz, seed) {
    NoiseFillGrid(heights, cx * 16 / 96, cz * 16 / 96, 1 / 96, seed, 4, 2, 0.5, 24, 64);
    DsGridFloorRegion(heights, 0, 0, 15, 15);
    return DsGridGetMax(heights, 0, 0, 15, 15);
}

// The world Object. Create: job = 0; heights = DsGridCreate(16, 16);
// Step: start a chunk's heights on a worker...
if (job == 0) {
    job = JobScriptCreate("TerrainHeights");
    JobScriptGrid(job, heights, true);
    JobScriptStart(job, heights, cx, cz, worldSeed);
}
// ...and on a later frame, once it is done, take the heights and mesh the chunk here.
else if (JobStatus(job) == "succeeded") {
    JobTake(job);
    top = JobResultNumber(job);
    JobRelease(job);
    job = 0;
    BuildChunkMesh(heights, cx, cz);   // meshes are made on the game's thread
}
```

Meshes are made on the game's thread, but working out their faces need not be: a job can fill a
list with each face's corners, normal, texture and colours (36 numbers a face), and the game's
thread adds them with one `MeshAddQuadsFromList(mesh, faces)` (see [meshes a script
builds](#meshes-a-script-builds)).

### Several jobs filling one grid

A big grid can be filled by several jobs at once, each lent its own part of it with
`JobScriptGridRegion(job, grid, x1, y1, x2, y2, true)`. For example, each tile of a map or each
area of a world can be filled by its own job. A job reads the whole grid as it was when the job
started, so it can look beyond its edges, and only its rectangle comes back at `JobTake`. The
game is not stopped from changing the grid while the jobs run: its own changes elsewhere are
kept, and a change inside a lent rectangle is replaced by the job's rectangle when the job is
taken. Rectangles that do not overlap all come back whole. Where two overlap, the one taken last
wins. If the game or the job changes the grid's size, nothing comes back.

The job gets a copy of the whole grid when it starts. The copy goes into memory kept for the next
job, so a grid lent again and again is copied but not allocated again: lending a 4 096 x 256 u16
grid costs one copy of its 2 MB. Unlike `JobScriptGrid`, the job does not hold the game's grid,
so the game's changes while the job runs never copy it.

```pgsl
// Four jobs, one for each quarter of a 512 x 512 height map (jobs = DsListCreate() in Create).
for (var q = 0; q < 4; q = q + 1) {
    var x1 = (q % 2) * 256; var y1 = Floor(q / 2) * 256;
    var job = JobScriptCreate("FillHeights");
    JobScriptGridRegion(job, heights, x1, y1, x1 + 255, y1 + 255, true);
    JobScriptStart(job, heights, x1, y1);
    DsListAdd(jobs, job);
}
// Later, each one that has succeeded: JobTake(job); JobRelease(job);
```

Measured on the development PC (8 Oct 2026, `--test pgsl-logic`; an i7-14700F, 8 performance
and 12 efficiency cores, in other use meanwhile) with a chunk of 8 x 8 columns 32 blocks high
(4-octave noise per column, a helper call and three commands per cell, 2 048 cells): 0.46 ms on the
game's thread; as a job, 0.06 to 0.13 ms of the game's thread (making it, the copies, starting,
taking, releasing) and 0.44 ms on a worker alone. With 14 in flight, each took 0.7 to 1.5 ms and
6 700 to 13 700 chunks were done a second on the 14 workers of the performance cores, 5 700 a
second on all 26 (efficiency cores are about half as quick). A worker's first jobs set up its command
table: the first 32 jobs took 13 to 22 ms in all.

`Build.bat --test pgsl-logic` checks that a job fills the same grids as calling the function
directly (and leaves the game's grids alone until `JobTake`), that drawing, `Random` and a name
never set stop a job with those messages, that twelve jobs at once each match the direct call for
their seed, that two Objects' 32 jobs on fewer workers wait their turn (no more threads start than
the limit) and all finish, and that a job is cancelled while running or while waiting, released,
refused when cancelled before it starts, and stopped by its budget; that a mesh's faces worked out
in a job and added with `MeshAddQuadsFromList` make the same mesh as adding them one at a time, and
that `MeshAddVerticesFromList` skips a vertex that is not a number with its triangle. It also checks noise against recorded values, its range and
smoothness, the grid fills against single calls, and each whole-region grid command. Not yet seen:
a game streaming its world through jobs.

### A job that reads the game's own data

A game's world is rarely a few grids passed as arguments: it is blocks in many grids found by
arithmetic on a handle (`wgBase + strip`), tables in lists, lists of grid handles, maps keyed by
position, and hundreds of instance variables. `JobScriptShareAll(job)` gives a job all of it: every
grid, list, map, stack and queue of the Object, and every instance variable holding a number, text
or true/false, exactly as they are at `JobScriptStart`. Nothing is copied when the job starts, so it
costs the same whatever the size of the world (measured below).

The rules a script can rely on:

- **A job sees a snapshot.** Each structure reads, for the whole job, as it was at `JobScriptStart`.
  The game may go on reading and changing its own while the job runs; none of the game's changes
  reach the job, and neither side ever waits for the other.
- **Reading is free; the first change copies.** The game and the job read the same structure, so
  reading costs nothing extra. Whichever side changes a structure first while a job holds it (the
  game or the job) gets a private copy of that one structure at that moment and keeps it; the other
  side goes on reading the original. Once no job holds a structure any more, the game changes it in
  place again without a copy. A 4 096 x 96 grid (3 MB) takes 0.8 to 1 ms to copy, so a game that writes
  the very grid a job is reading pays that once for that job, and nothing at all for a grid it only
  reads.
- **A job's changes stay its own** unless the structure was given with `copyBack` true (with
  `JobScriptGrid`, `JobScriptList` or `JobScriptMap`, which may be combined with
  `JobScriptShareAll`): `JobTake` then puts the job's version in place of the game's, and the game's
  own changes to that structure during the job are replaced. A `copyBack` structure the job did not
  change is left as the game has it.
- **Instance variables are a copy.** The job starts with the Object's numbers, text and true/false
  as they were; `VariableSet` or an assignment in the job changes only the job's. Lists, maps and
  grids held in variables are handles (numbers), so they reach the shared structures. Variables of
  the game's calling functions (`var`s of a caller) are not there: the job's function is called on
  its own. Only the variables the job's code reads are copied: the names its function, and every
  library function it calls, read (worked out once per function). A game with thousands of
  variables pays for the handful a mesher reads. Code that reads a variable by a name worked out as
  it runs (`VariableGet`, `VariableGetText`...) is given every variable, as before 11 Oct 2026.
- **A job still runs only pure commands** (maths, text, noise, data structures): it works out data,
  and the game's thread turns that into meshes, instances or sound. For a mesh, the job fills a list
  with 36 numbers a face and the game adds them with `MeshAddQuadsFromList`.
- **Edits made during a job are not in its result.** A chunk the player changes while its mesh job
  runs is meshed as it was; mark it to be meshed again and start another job.

Several jobs may share the same structures at once (each holds them until it has finished, or ended
without running). With `JobScriptShareAll` at the size of a voxel game (44 grids including 32 of
4 096 x 96, 2 500 lists, 65 maps, 2 500 instance variables) starting a job took 0.16 to 0.34 ms of the
game's thread and 0.14 to 0.27 ms on the worker to set up its own VM (two runs, medians); six 4 096 x 96 grids given with
`JobScriptGrid` took 0.01 to 0.02 ms (before 9 Oct 2026 each was copied as the job started, 0.8 to 1 ms a grid).
Since 11 Oct 2026 the job's table of structures becomes its worker's store as it is (it was copied
again on the worker), and 3 000 instance variables a job never reads add nothing to starting it.

#### Worked example: meshing a chunk on a worker

The world keeps its blocks as a voxel game does: block `(x, y, z)` in grid `wBase + ((z >> 4) & 3)`
at column `(x & 63) + ((z & 15) << 6)` and row `y`, light in four more grids, a flags and a tint
table, each kind's six tiles in a list of lists, and the facing of some blocks in a map keyed
`"x,y,z"`. `Engine.Pgsl.Logic.Jobs.ShareAllMeshesAChunkAsCallingTheFunction` runs this same mesher
directly and as a job and checks that the faces are identical.

```pgsl
// Library Script "ChunkMesher". It reads only the world, so it runs the same directly or as a job.
function BlockAt(bx, by, bz) {
    if (by < 0) { return 1; }
    if (by >= worldHeight || bx < 0 || bx >= 64 || bz < 0 || bz >= 64) { return 0; }
    return DsGridGet(wBase + ((bz >> 4) & 3), (bx & 63) + ((bz & 15) << 6), by);
}
function LightAt(bx, by, bz) {
    if (by < 0 || by >= worldHeight || bx < 0 || bx >= 64 || bz < 0 || bz >= 64) { return 15; }
    return DsGridGet(wLight + ((bz >> 4) & 3), (bx & 63) + ((bz & 15) << 6), by);
}
// One face for MeshAddQuadsFromList: four corners, the normal, the tile's uv rectangle, four colours, flip.
function AddFace(faces, bx, by, bz, dir, tile, tint, light) {
    var nx = 0; var ny = 0; var nz = 0;
    if (dir == 0) { nx = 1; } else if (dir == 1) { nx = -1; } else if (dir == 2) { ny = 1; } else if (dir == 3) { ny = -1; } else if (dir == 4) { nz = 1; } else { nz = -1; }
    var cx = bx + 0.5 + nx * 0.5; var cy = by + 0.5 + ny * 0.5; var cz = bz + 0.5 + nz * 0.5;
    var ax = 0; var az = 0; var uy = 0; var uz = 0;
    if (nx != 0) { az = 0.5; uy = 0.5; } else if (ny != 0) { ax = 0.5; uz = 0.5; } else { ax = 0.5; uy = 0.5; }
    DsListAdd(faces, cx - ax); DsListAdd(faces, cy - uy); DsListAdd(faces, cz - az - uz);
    DsListAdd(faces, cx + ax); DsListAdd(faces, cy - uy); DsListAdd(faces, cz + az - uz);
    DsListAdd(faces, cx + ax); DsListAdd(faces, cy + uy); DsListAdd(faces, cz + az + uz);
    DsListAdd(faces, cx - ax); DsListAdd(faces, cy + uy); DsListAdd(faces, cz - az + uz);
    DsListAdd(faces, nx); DsListAdd(faces, ny); DsListAdd(faces, nz);
    var u0 = (tile % 16) / 16; var v0 = Floor(tile / 16) / atlasRows;
    DsListAdd(faces, u0); DsListAdd(faces, v0); DsListAdd(faces, u0 + 1 / 16); DsListAdd(faces, v0 + 1 / atlasRows);
    for (var k = 0; k < 4; k = k + 1) { DsListAdd(faces, tint * 16); DsListAdd(faces, 255 - k * 8); DsListAdd(faces, light * 17); DsListAdd(faces, 1); }
    DsListAdd(faces, (bx + bz) % 2);
    return 1;
}
// Every visible face of chunk (cx, cz) into the list; returns how many.
function MeshChunkFaces(cx, cz, faces) {
    DsListClear(faces);
    var n = 0;
    for (var lz = 0; lz < 16; lz = lz + 1) {
        for (var lx = 0; lx < 16; lx = lx + 1) {
            var bx = cx * 16 + lx; var bz = cz * 16 + lz;
            for (var y = 0; y < worldHeight; y = y + 1) {
                var k = BlockAt(bx, y, bz);
                if (k > 0) {
                    var tiles = DsListGet(kindTiles, k);          // a list's handle found in another list
                    var turn = 0;
                    if ((DsListGet(kindFlags, k) & 16) != 0) {
                        var key = StringOf(bx) + "," + StringOf(y) + "," + StringOf(bz);
                        if (DsMapExists(facing, key)) { turn = DsMapGet(facing, key); }
                    }
                    for (var dir = 0; dir < 6; dir = dir + 1) {
                        var ox = bx; var oy = y; var oz = bz;
                        if (dir == 0) { ox = bx + 1; } else if (dir == 1) { ox = bx - 1; } else if (dir == 2) { oy = y + 1; } else if (dir == 3) { oy = y - 1; } else if (dir == 4) { oz = bz + 1; } else { oz = bz - 1; }
                        var o = BlockAt(ox, oy, oz);
                        if (o != k && (DsListGet(kindFlags, o) & 2) == 0) {
                            n = n + AddFace(faces, bx, y, bz, dir, DsListGet(tiles, (dir + turn) % 6), DsListGet(kindTint, k), LightAt(ox, oy, oz));
                        }
                    }
                }
            }
        }
    }
    return n;
}
```

```pgsl
// The world Object. Create: meshJob = 0; ... (the world's grids, tables and globals made here)
// Step: start the next chunk's faces on a worker...
if (meshJob == 0 && DsListSize(chunksToMesh) > 0) {
    meshCx = DsListGet(chunksToMesh, 0); meshCz = DsListGet(chunksToMesh, 1);
    DsListDelete(chunksToMesh, 0); DsListDelete(chunksToMesh, 0);
    meshFaces = DsListCreate();
    meshJob = JobScriptCreate("MeshChunkFaces");
    JobScriptShareAll(meshJob);                 // the world as it is now: grids, tables, map, globals
    JobScriptList(meshJob, meshFaces, true);    // the faces come back at JobTake
    JobScriptStart(meshJob, meshCx, meshCz, meshFaces);
}
// ...and on a later frame make the mesh on the game's thread, in one call.
else if (meshJob != 0) {
    var status = JobStatus(meshJob);
    if (status == "succeeded") {
        JobTake(meshJob);
        var m = MeshCreate();
        MeshAddQuadsFromList(m, meshFaces);
        DsListSet(chunkMeshes, meshCx + meshCz * 4, m);
        DsListDestroy(meshFaces);
        JobRelease(meshJob); meshJob = 0;
    } else if (status == "failed" || status == "cancelled") {
        Print("Meshing failed: " + JobError(meshJob));
        JobRelease(meshJob); meshJob = 0;
    }
}
```

The Object may keep several such jobs in flight (up to 16), each with its own face list. Generation
works the same way: a job given the world with `JobScriptShareAll` fills a chunk into a grid of its
own given with `copyBack` (or into a list), and `JobTake` puts it in place on the game's thread.
A function that runs longer than one call's 100 000 instructions on the game's thread (to compare
it with the job, or before it moves to one) needs `ScriptInstructionLimit` raised there; in a job
only the job's budget counts.

`--test pgsl-logic` checks the rules above: the mesher above gives the same faces as a job as
called directly (and fails with "has no value in this job" when the job is not given the
globals); four jobs reading grids, lists, a map and globals see exactly the data of their own start
while the game writes those structures over a hundred thousand times; a job's changes without
`copyBack` never reach the game, and `copyBack` brings back a map the job changed but not a list
it left alone; a structure is copied once when the game changes it while a job holds it, never when
it only reads it or changes it after the job, and a job cancelled before it ran lets go of its
data. It also times sharing at a voxel game's size.

## Video options and the clock

| Command | What it does |
|---|---|
| `WindowSetFullscreen(enabled)`, `WindowIsFullscreen()` | Fill the screen (a borderless window the size of the display) or go back to a window. |
| `WindowHasFocus()`, `WindowIsMinimized()` | Whether the player is in the game's window (false after switching to another window), and whether it is minimised. A minimised game draws no frames and runs no Draw events, but Step events go on: a game that should rest while nobody is looking (pause, stop starting particle bursts) asks these. Outside a game window they are true and false. |
| `WindowSetMode(mode)`, `WindowGetMode()` | `"windowed"`, `"borderless"` or `"fullscreen"` (exclusive); false for any other name. |
| `WindowSetSize(width, height)`, `WindowGetWidth()`, `WindowGetHeight()` | A windowed game's size in pixels. |
| `WindowGetGuiX()`, `WindowGetGuiY()`, `WindowGetGuiWidth()`, `WindowGetGuiHeight()`, `WindowGetGuiScale()` | The GUI area: where the room's window lands in the real one. With Scale viewports with window on, the world is fitted at one scale and centred, leaving bands on a window of another shape; DrawGui laid out in this rectangle (design sizes times the scale) lines up with the world at any size. Without it, the whole window and its scale. |
| `TestCheck(name, passed, detail)` | A self-test: the game reports something it checked (the door opened, the count is right). Written to the project's Logs/checks.log; Studio's automation `game.run` returns the run's checks, and a failed one fails the run. |
| `WindowSetVSync(enabled)`, `WindowGetVSync()` | Wait for the display's refresh (no tearing). On Vulkan a change rebuilds the swap chain once, before the next frame: on waits for the display (FIFO); off shows each frame when it is ready (MAILBOX, else IMMEDIATE, else FIFO where the display offers nothing else). The render log names the mode chosen. |
| `GameSetMaxFps(fps)`, `GameGetMaxFps()` | A frame-rate cap; 0 for none. |
| `GameUseRoomFps()` | Follow the rooms' Frame rate and VSync settings again: the current room's apply at once. |
| `DateNow()` | Now, as seconds since 1970 (UTC): what to store in a save for "last played". |
| `DateYear(t)`, `DateMonth(t)`, `DateDay(t)`, `DateHour(t)`, `DateMinute(t)`, `DateSecond(t)`, `DateWeekday(t)` | The parts of a `DateNow` time in local time (weekday 0 is Sunday). |
| `DateText(t, format)` | A `DateNow` time as text, such as `DateText(saved, "d MMM yyyy HH:mm")`. |

`TimeMs()` is still the millisecond clock for timing within a run.

A room's Frame rate and VSync settings are the game's defaults. Once the game chooses its own,
with `GameSetMaxFps`, `RoomSpeed = n`, `WindowSetVSync`, `Engine.FpsTarget` or `Engine.SetVsync`,
its choice wins for the rest of the run and no room change resets it. That holds even when the
choice is made in a Create event of the first room, which runs before the room's settings are
applied. So a settings menu, and options read from a save at start, simply work. `GameUseRoomFps()`
hands both back to the rooms.

## The mood of a room from a script

A room's atmosphere and grading can follow the hour (a hazy morning, a golden hour, dusk):

| Command | What it does |
|---|---|
| `Engine.Sky.Haze`, `Engine.Sky.SetHaze(amount)` | Atmospheric haze, 0 to 1, as the Room's Haze. |
| `Engine.Sky.Visibility` | How far one sees through the atmosphere's fog, in metres (0 = the preset's own). |
| `Engine.Sky.FogScale` | Scales the fog the weather brings, 0 to 4. |
| `Engine.Sky.AmbientScale` | Scales the sky's ambient light, 0 to 8. |
| `Engine.Sky.SetAtmospherePreset(name)`, `Engine.Sky.AtmospherePreset` | Natural, ClearDay, GoldenHour, Overcast, Storm, Night or Alien; false for an unknown name. |
| `Engine.Rendering.Contrast`, `Engine.Rendering.Saturation`, `Engine.Rendering.Vignette` | Colour grading, beside the existing `Engine.Rendering.Exposure`. |
| `Engine.SetShadowStrength(amount)`, `Engine.GetShadowStrength()` | How dark shadows are, 0 (none) to 1 (full): lower it for a flat look. |
| `Engine.Sky.SunDirectionX/Y/Z`, `Engine.Sky.MoonDirectionX/Y/Z` | The unit direction towards the sun and the moon (y up; below 0 once set), for a game that draws its own sky in line with the engine's light. |
| `Engine.Sky.NightFactor` | 0 by day to 1 at night, as the engine blends sun and moon light. |
| `Engine.Sky.SunDiscVisible`, `Engine.Sky.MoonDiscVisible` | Whether the sun's or the moon's disc (and the sun's glow) is drawn; false keeps their light. |
| `Engine.SetGlobalShadows(enabled)`, `Engine.GetGlobalShadows()` | Turn the scene's shadows off or on (sun and lamps). `Engine.Rendering.ContactShadowsEnabled` controls the small screen-space contact shadows separately. |

Colour tints belong to the game's look, so they are a project post effect (see
[Post effects](PostEffects.md)) with its parameters set by `PostEffectSetParameter`.

## Anti-aliasing, quality tiers and motion blur

### Anti-aliasing

The 3D picture's jagged edges can be smoothed after it is tonemapped. Off by default: a game that
does not ask draws exactly as before.

| Setting or command | Meaning |
|---|---|
| Preferences › Project › **Anti-aliasing (3D)** (`rendering.antiAliasing` in the project file: `"Off"`, `"FXAA"`, `"SMAA"` or `"TAA"`) | What the game starts with, in the Player and in an exported game, and what Studio's 3D viewports show. Off by default. |
| `RenderSetAntiAliasing(mode)` | `"off"`, `"fxaa"`, `"smaa"` or `"taa"`, from the next frame; false for any other name. |
| `RenderGetAntiAliasing()` | The mode asked for. |
| `Engine.Rendering.AntiAliasing` | The same as a property. |
| `GENESIS_ANTI_ALIASING` | Environment override for one run (`off`, `fxaa`, `smaa`, `taa`); it wins over the project file. |

**FXAA** is one full-screen pass: it finds each step whose change in brightness is large enough to
see, walks along the edge to both ends and blends the pixel with its neighbour across the edge by
how far along the step it sits; a single-pixel speck is softened too. Cheapest; it softens fine
texture detail a little. **SMAA** (1x) is three passes: edges by brightness with local contrast
adaptation; then for every edge pixel the staircase is measured (the edge is walked up to 32 pixels
each way and its ends read as an L, U or Z) and the area the true edge would cover is worked out;
then each pixel is blended with its neighbours by those areas. Texture detail stays sharper. It
finds horizontal and vertical edge patterns; the reference's diagonal and corner passes are not
done, and no lookup textures ship. Both blend in linear light, as a multisampled resolve does.

**TAA** (temporal, since 22 October 2026) draws each frame from a camera nudged by under a pixel, a
different nudge each frame (eight points of the Halton 2, 3 sequence), and blends the frame into
the image built up from the frames before it: about the last ten count. Each pixel takes that
history from where its point was last frame, worked out from the depth buffer and the camera's
motion, and keeps it within the range of colours around the pixel now, so a moving thing or a
newly uncovered one does not leave a trail. Edges and fine detail (wires, leaves, distant fences)
are the smoothest of the three; something moving by itself can look a little soft. The held items
are drawn after it, so they stay sharp. Two full-screen passes.

Where it runs in a frame: the composite (fog, bloom, exposure, grading, tonemap) → the held items
(view models) → **anti-aliasing** → the project's post effects → the GUI. The GUI is drawn after
the 3D renderer has finished, so text and HUD are never softened, and a post effect (an outline,
grain, sharpening) reads the anti-aliased picture while its own lines stay as it draws them. It
runs on Direct3D 11, Direct3D 12, Vulkan and OpenGL; the software renderer ignores it and draws as
before. Its shaders ship precompiled with the engine's own. The renderer's log says which mode is
in effect (`Anti-aliasing: smaa`), and so does the debug screen's render passes line.

What each pass costs at 1920x1080, measured on 10 October 2026 on this machine's GeForce RTX 5060
Ti (GPU time of the 3D frame with the pass minus without it; three rounds of 90 frames after a
warm-up, the range of the rounds' medians; a field of 576 small tilted squares, edges everywhere,
which costs 0.15 to 0.19 ms a frame without anti-aliasing):

| Backend | FXAA | SMAA |
|---|---|---|
| Direct3D 11 | 0.05 ms | 0.11 ms |
| Direct3D 12 | 0.04 ms | 0.12 to 0.13 ms |
| OpenGL | 0.05 ms | 0.16 ms |
| Vulkan | not measured: its GPU timestamps read 0 in the test harness | |

**Not done: temporal anti-aliasing (TAA).** It needs, for every pixel, where that point was on
screen last frame. The renderer can work that out for the camera's own motion (from depth), but
not for moving or animated meshes: draws are submitted afresh each frame (`MeshDrawCall` carries
no identity or previous transform) and a skinned mesh keeps only this frame's bones. A TAA without
that ghosts behind running characters and vehicles. The plan: (1) jitter the main projection by a
Halton sequence (the held items too); (2) a velocity target written by the forward, terrain and
water shaders, fed with each draw's previous world matrix (the runtime's object draw pass knows
which entity a draw is) and the previous skin palette (kept per palette), camera-only motion
elsewhere, and a "no history" mask for particles, transparent draws and project mesh shaders;
(3) a resolve with closest-depth velocity, colour clipping in YCoCg against the 3x3 neighbourhood,
history rejection on disocclusion, and a light sharpen; (4) the cloud and fog temporal passes kept
on the unjittered matrices. About a week across the shaders and the four backends, with tests.
The camera-only half is about two days but is not robust enough for a shooter.

### Quality tiers

One call sets the costly rendering features together, for a Graphics menu:

| Command | Meaning |
|---|---|
| `RenderSetQuality(tier)`, `Engine.Rendering.SetQuality(tier)` | `"low"`, `"medium"`, `"high"` or `"ultra"`, from the next frame; false for any other name. |
| `RenderGetQuality()`, `Engine.Rendering.QualityTier` | The tier last set; `"custom"` once one of the settings it covers has been changed since; `""` before any tier. Setting the property applies a tier. |

| Setting | Low | Medium | High | Ultra | Set alone by |
|---|---|---|---|---|---|
| Ambient occlusion (GTAO) | off | on | on | on | `Engine.Rendering.GtaoEnabled` |
| Contact shadows | off | off | on | on | `Engine.Rendering.ContactShadowsEnabled` |
| Sun shadow cascades | 2 | 2 | 3 | 3 | `Engine.Rendering.CascadeCount` |
| Sun shadow map size (each cascade) | 512 | 1024 | 1024 | 2048 | `Engine.Rendering.ShadowResolution` |
| Point lights given a shadow map (4 with no tier) | 0 | 2 | 4 | 8 | `Engine.Rendering.OmniShadowBudget` |
| Local lights in the volumetric fog | off | off | on | on | `Engine.Rendering.LocalVolumetricsEnabled` |
| Bloom | off | on | on | on | `Engine.Rendering.BloomEnabled` |
| Volumetric fog grid quality | 0 | 1 | 1 | 2 | `Engine.Rendering.VolumetricFogQuality` |
| Cloud quality | Performance | Balanced | High | Cinematic | `Engine.Rendering.CloudQuality` |
| Anti-aliasing | FXAA | FXAA | SMAA | SMAA | `RenderSetAntiAliasing` |

The engine's defaults without a tier are 1024 shadow maps (three cascades in a room, unless it sets
two), four point-light shadows, the room's own fog quality, High clouds and everything else off.
Nothing changes until a game sets a tier. Once it
has, these settings decide for every room: a room's own ambient occlusion or third cascade is
turned off on Low, as a player's choice should. Rooms still decide whether they have fog, clouds,
bloom strength, exposure and the rest of their look; a tier only decides how much of the costly
work is done. Change any one setting after the tier to override it (the tier then reads
`"custom"`):

```
RenderSetQuality("high");
Engine.Rendering.BloomEnabled = false;   // this game never blooms
```

**How far shadows reach (29 Oct 2026).** The sun's shadows reach as far as the camera sees, up to
300 m, unless the room sets its own distance (`shadowDistance`, `Engine.Rendering.Shadows.Distance`;
a valley sets thousands). Three cascades share that distance (`shadowCascades`,
`Engine.Rendering.Shadows.Cascades`), each fitted to a sphere round its part of the view and snapped to
whole shadow-map texels, so a shadow never moves or changes shape when only the camera turns; over
the last 15% of the distance (`shadowFade`, `Engine.Rendering.Shadows.Fade`) shadows fade out instead
of stopping at a line. Things outside the view that shade it (a roof above the camera, a wall behind
it) still cast their shadows. Four lamps (point and spot lights) cast shadows at once unless a tier or
`Engine.Rendering.Shadows.LampBudget` says otherwise (up to eight), chosen among the lamps whose light
reaches the view; a lamp's shadow fades in when it takes a slot and out towards 60 m. Where a surface
lies on or in another (a window set into a wall, a sign on a door), `Engine.Model.InFront = true` (or
`"InFront": true` in the Object's ModelRendererComponent) draws it a hair nearer the camera so it wins
instead of flickering with the wall. Bounced light never takes a surface below 35% of the room's own
ambient (`Engine.Lighting.Bounce.Floor`), so a probe that saw a dark room shades it rather than
blacking it out. F6 shows the shadow distance and how many lamps were left unshaded in view.

**See-through surfaces in a room's 3D viewports (29 Oct 2026).** A room drawn through viewports
(split screen, a DS-style top screen) lays each view's finished picture on the window. See-through
surfaces (alpha-blended materials, water, additive glows) used to make that picture partly
transparent where they lay, so the black behind the view showed through: Nuvema Town's sea, an
opaque sea under a half-opaque copy of it, came out at half brightness on Direct3D 11 and 12 and OpenGL. They
now only add to the picture's cover, on every backend. See-through parts of a skinned model are drawn
skinned, where their pose puts them; Vulkan used to leave them out altogether.

To find which part of the frame makes something look wrong, `GENESIS_RENDER_OFF` switches parts off
for a whole run, whatever the game asks, as a comma list: `ao`, `post` (the game's own post effects),
`decals`, `lampshadows`, `cull` (every surface two-sided), `uvslide` (sliding pictures stand still),
`transparent` (see-through surfaces), `probes` (the light probe) and `lighting` (every surface its own
colour). Compare a `game.run` picture with and without each. `GENESIS_RENDER_HIDE` (a comma list) leaves
out every model whose name contains one of the words, to see what is behind it.

**Lighting term views.** `Engine.DebugView("Albedo")`, `("Ambient")` and `("Direct")` draw one term of
the lighting alone: each surface's own colour; the indirect light it takes (sky and ground ambient,
the light probe and reflections, after occlusion); or the direct light of the sun and the lamps.
`GENESIS_RENDER_VIEW=albedo|ambient|direct` does the same for a whole run. A black area in the
ambient view gets no indirect light; one black in the direct view gets no lamp or sun. Surface
shaders show their own material's terms; other project pixel shaders draw as the engine's.

**Sun and fog colours by the hour.** `Engine.Sky.SetSunColorAt(hour, r, g, b)` and
`Engine.Sky.SetFogColorAt(hour, r, g, b)` (0 to 255) give the climate a colour for an hour of the day
that it keeps instead of its own; between the hours given it blends, round the clock.
`Engine.Sky.ClearColorsByHour()` hands both back to the climate. Off until a game sets one.

**Instanced models shade as single ones (29 Oct 2026).** A model drawn through a room's static
instanced layer, or any `DrawMeshInstances` batch, now keeps its whole material: metallic and
roughness factors, ORM and emission maps, emission colour and the picture's repeat and slide. It
used to keep only the picture and normal map, so the same glTF model came out bright in a room and
dark drawn on its own.

New on their own:

| Property | Meaning |
|---|---|
| `Engine.Rendering.ShadowResolution` | Sun shadow map size in texels per side, for every cascade: 512, 1024 (the default), 2048 or 4096; other values are rounded to one of those, and 0 goes back to the default. The maps are remade at the next frame. The software renderer stays at 1024 at most. `GENESIS_SHADOW_RESOLUTION` sets it for one run. |
| `Engine.Rendering.VolumetricFogQuality` | Volumetric fog grid quality 0 to 2 for every room; -1 (the default) leaves each room's own. |

GPU time of the 3D frame per tier at 1920x1080 on the same machine, on a small lit scene (64 boxes
on the floor, sun shadows, fog; no clouds, so cloud quality costs nothing here). A game's frame
costs more; the steps between tiers are what to read:

| Backend | Low | Medium | High | Ultra |
|---|---|---|---|---|
| Direct3D 11 | 0.58 ms | 0.71 to 0.78 ms | 0.90 ms | 1.26 ms |
| Direct3D 12 | 0.54 to 0.56 ms | 0.76 to 0.91 ms | 0.96 ms | 1.26 ms |
| OpenGL | 0.79 ms | 1.18 ms | 1.52 ms | 1.92 ms |

### Camera motion blur

| Command | Meaning |
|---|---|
| `RenderSetMotionBlur(amount)`, `RenderGetMotionBlur()`, `Engine.Rendering.MotionBlur` | 0 (off, the default) to 1: the share of a frame the virtual shutter is open; 0.5 is a film camera's. |
| `GENESIS_MOTION_BLUR` | Environment override for one run (0 to 1). |

Each pixel is smeared along the path its point took across the screen since the last frame, as
the camera moved and turned (twelve samples, in linear light, at most 4% of the picture's width
long). It runs after the composite and before the held items, anti-aliasing, post effects and the
GUI, so a held weapon and the HUD stay sharp. The sky blurs when the camera turns, not when it
walks. A camera that jumps more than 4 m or turns more than about 45 degrees in one frame (a cut,
a respawn) is not blurred that frame. Only the camera's own motion blurs: the renderer keeps no
motion for moving objects (see TAA above), so a running character is as sharp as the wall behind
it would be. Direct3D 11 and 12, Vulkan and OpenGL; the software renderer ignores it.

Its cost at 1920x1080 with the camera turning (amount 0.5, same machine and method as the
anti-aliasing figures): 0.12 to 0.13 ms on Direct3D 11, 0.15 ms on Direct3D 12, 0.16 ms on OpenGL.

**Not done: screen-space reflections.** They need each pixel's normal and roughness (and how
reflective it is); the forward pass writes only colour and its fog and ambient target, so a
reflection pass cannot tell a wet road from a brick wall. The plan: a third forward target
(octahedral normal, roughness, reflectance) written by the forward, terrain and water shaders
(project mesh shaders would count as not reflective), a half-resolution ray march against a depth
pyramid, a roughness-aware blur, fades at the screen's edges and where a ray finds nothing, and the
result added before tonemapping with Fresnel. About three to four days across the backends; without
TAA rough reflections shimmer. A cheaper "wet ground only" variant (upward-facing surfaces, normals
from depth, mirror reflections scaled by the weather's wetness) is about a day but looks wrong on
textured or uneven ground, so it was not added.

### Verification

`Build.bat --test anti-aliasing` draws a white shape with a shallow and a steep edge on black,
with GUI text above it, on every renderer: without anti-aliasing no edge pixel holds a value
between black and white; with FXAA and with SMAA most edge columns and rows do; the GUI text and
flat areas are identical to the last pixel; an inverting post effect returns the inverse of the
anti-aliased picture (so it ran after); a camera turning half a degree a frame gets a soft side
with motion blur and a hard one without, and a still camera is unchanged by it; the software
renderer's picture does not change. It also checks the commands, the project setting through the
Preferences page, save and the Player's read, every tier's table reaching a room's frame state,
overrides after a tier, and tiers switched while drawing on every renderer (sun shadow maps remade
at 512, 2048 and 1024, and still shadowing on the GPU backends). `Build.bat --test
anti-aliasing-cost` measures the costs above.

## Pictures from a script

`ScreenshotSave(name)` saves a picture of the frame once it is drawn, as `name.png` in the
project's debug images folder (the autoshot's folder), so one run can step a camera through many
views. `ScreenshotPending()` counts pictures not yet taken and `ScreenshotLastPath()` gives the file
the last one went to. A picture asked for during a room change's cover waits for the first frame
without it.

## Many GUI rectangles or image parts in one call

A minimap, a world map or a tile layer laid out by script draws thousands of rectangles. One
`DrawRectangle` call each costs the script far more than the drawing: the GUI already puts every
rectangle of a frame into one draw call. A list drawn in one call skips that cost, and draws the very
pixels the single calls would, in call order with the drawing around it, on every renderer.

| Command | What it does |
|---|---|
| `DrawRectanglesFromList(list, x, y, xscale, yscale)` | Filled rectangles from a DsList holding 8 numbers for each: `x, y, width, height, red, green, blue, alpha`, the colour as `DrawSetColorRgb` (0-255) and `DrawSetAlpha` (0-1) take it. Each is placed at `x + its x * xscale`, `y + its y * yscale` and sized by the scales, so a map kept in cells is drawn anywhere at any zoom without rebuilding the list; `x, y, xscale, yscale` may be left out (0, 0, 1, 1). Entries with no width, height or alpha are skipped. Returns the rectangles drawn. |
| `DrawSpritePartsFromList(name, list, x, y, xscale, yscale)` | Parts of one Image from a list holding 10 numbers for each, in `DrawSpritePart`'s order: `frame, u0, v0, u1, v1, x, y, width, height, alpha`, placed and sized the same way and tinted by the image blend. Returns the parts drawn. |

Both work in Draw GUI and in a 2D Draw event, and read the list straight into a reused buffer: a
frame's batch allocates nothing. Measured with a Draw GUI event drawing a list one `DrawSetColorRgb`,
`DrawSetAlpha` and `DrawRectangle` at a time (as a map is drawn from its runs) against one
`DrawRectanglesFromList` of the same list (development PC, Direct3D 11, 9 Oct 2026, mean of 25
frames; `gui-batch-cost.txt` from `Build.bat --test gui-batch`). Both are one GUI draw call:

| Rectangles | One call each: script + GUI = a frame | One batch: script + GUI = a frame |
|---|---|---|
| 1,200 | 2.2 + 0.9 = 3.1 ms | 0.3 + 0.3 = 0.6 ms |
| 5,000 | 8.6 + 2.0 = 10.6 ms | 0.4 + 0.5 = 0.9 ms |

A map that changes rarely costs less still painted once into a [texture](#pictures-a-script-paints)
and drawn with one `DrawTexture` a frame.

## Pictures a script paints

A texture a script paints at run time (a map, a chart, a sign, a pattern) is drawn like an image.
A pixel is **red, green and blue from 0 to 255 and alpha from 0 to 1**, as `DrawSetColorRgb`,
`DrawSetAlpha` and `MeshAddQuadColors` take a colour; lists hold 4 numbers a pixel, row by row from
the top-left corner. The pixels are kept by the engine; when the texture is drawn after a change,
only the rectangle that changed goes to the graphics card, once, however many times it is drawn
that frame, on every renderer.

| Command | What it does |
|---|---|
| `TextureCreate(width, height)` | A new texture, 1 to 4096 pixels each way, transparent; 0 when the size is not allowed. A game's textures may hold 64 million pixels together (256 MB); past that `TextureCreate` gives 0. |
| `TextureSetPixels(texture, list)` | Its pixels from a list of 4 numbers a pixel; a shorter list sets the pixels it holds. Returns the pixels set. |
| `TextureSetRegion(texture, x, y, width, height, list)` | The pixels of a rectangle, row by row; pixels falling outside the texture are left out. The cheap way to change part of a map. |
| `TextureFillRectanglesFromList(texture, list)` | Rectangles painted into it from a list laid out as `DrawRectanglesFromList`'s (`x, y, width, height, red, green, blue, alpha`, in the texture's pixels); each replaces what it covers, alpha included. |
| `TextureSetSmooth(texture, smooth)` | Smoothed when drawn bigger or smaller than its pixels; off at first, so its pixels stay square, as a map's should, whatever the room samples with. |
| `TextureWidth(texture)`, `TextureHeight(texture)`, `TextureExists(texture)` | Its size, 0 when there is no such texture. |
| `TextureDestroy(texture)` | Frees it and its copy on the graphics card. Every texture is freed when the game ends. |
| `DrawTexture(texture, x, y, xscale, yscale, angle, alpha)` | In Draw GUI (in order with the GUI's other drawing) or a 2D Draw event: drawn with its top-left corner at `x, y`, scaled and turned (degrees) about that corner, tinted by the image blend. |
| `DrawTexturePart(texture, u0, v0, u1, v1, x, y, width, height, alpha)` | Part of it into a rectangle, as `DrawSpritePart`: `u0, v0` to `u1, v1` are fractions of the texture, a window onto a big map or a zoom. |

```pgsl
// Create: the map's texture. When the map changes, paint its runs (8 numbers each) in one call.
mapTexture = TextureCreate(256, 256);
TextureFillRectanglesFromList(mapTexture, mapRuns);
// Draw GUI, every frame: one sprite.
DrawTexture(mapTexture, 16, 16, 2, 2, 0, 1);
```

Measured on the development PC (Direct3D 11, 9 Oct 2026; `script-textures-cost.txt` from
`Build.bat --test script-textures`): painting 5,000 rectangles into a 600 x 300 texture with
`TextureFillRectanglesFromList` took 4.0 ms, once; drawing it every frame with one `DrawTexture`
from a Draw GUI event then cost 0.02 ms a frame and one draw call, against 10.6 ms for the same
5,000 rectangles drawn one `DrawRectangle` at a time.

## A sky layer behind the world

A game can draw its own sun, moon, stars or sky dome behind its terrain. `DrawMeshSetSky(true)`
puts this instance's later script-mesh draws (`DrawMesh3D`, `DrawMesh3DTransform`,
`DrawMeshShader3D`, with a mesh Shader resource or without) in the sky layer until it is set off
or `DrawMeshResetState` runs. A mesh there:

- is drawn first, right after the frame is cleared to the sky's colour, and writes no depth, so
  everything the world draws covers it: terrain, water, models, the floor;
- is centred on the camera: the draw's `x, y, z` are an offset from the eye (`0, 0, 0` puts the
  mesh's origin at the eye), so it never comes nearer as the player walks;
- may be any size at any distance: it is scaled about the eye to sit inside the camera's far plane,
  which does not change how it looks, so there is no need to push the far plane out for it;
- is unlit, in its own colours (texture, vertex colour, image blend, alpha), brighter with
  `DrawMeshSetGlow`, blended when see-through (`DrawMeshSetTransparent` or alpha below 1), in call
  order; it casts and receives no shadow;
- is part of the sky for the rest of the frame: the room's haze, clouds and stars go over it as
  over the sky. Under an atmosphere preset the sky's colours are worked out again there, and what
  the layer drew adds where it is brighter than the horizon, as the engine's own sun disc does.

Hide the engine's own discs with `Engine.Sky.SunDiscVisible = false` and
`Engine.Sky.MoonDiscVisible = false`; `Engine.Sky.SunDirectionX/Y/Z` and `MoonDirectionX/Y/Z` say
where to put yours so they match the light. Nothing changes for a game that does not use the layer.

```pgsl
// Draw: a square sun (a quad built round its own centre, facing the eye) 100 out towards the
// engine's sun, behind the land whatever the far plane.
DrawMeshSetSky(true); DrawMeshSetTransparent(true);
DrawMeshShader3D(sunQuad, "SkyShader", Engine.Sky.SunDirectionX * 100, Engine.Sky.SunDirectionY * 100,
    Engine.Sky.SunDirectionZ * 100, 1, 1, 1, 0, "Sun");
DrawMeshResetState();
```

## Recording and replaying input

A run can be recorded once with the real keyboard, mouse and controller and then played back in
the real Player, so controller focus, held triggers, resizing and progress through a game can be
checked again without a person at the machine. A recording keeps, for every frame, everything a
game can read from the devices: the keys, mouse buttons and their presses and releases (a tap
shorter than a frame included), the pointer, the wheel and mouse-look, the controller's buttons,
sticks and triggers (as read and before the dead zone), typed characters, the frame's time step
and the window's size. While a replay plays, the real devices are ignored, each frame steps by the
recorded time step and the window is asked for the recorded size, so a script sees the same input
on the same frame numbers and gets as far as it did.

| Command | Meaning |
|---|---|
| `InputRecordStart(name)` | Record from the next frame into `name.ginput` in the project's debug folder under `Replays` (or at a full path). False when the file cannot be written. |
| `InputRecordStop()` | Stop and save; the number of frames recorded. A recording still running when the game closes is saved. |
| `InputReplayStart(name)` | Play a recording from the next frame. False when there is no such recording, or it is not one. |
| `InputReplayStop()` | Stop a replay; the real devices are read again from the next frame. |
| `InputReplayActive()`, `InputRecordActive()` | True while a replay plays, or a recording records. |
| `InputReplayFrame()` | Frames recorded or played so far: 1 in the first, and the same number in the recording and its replay. |
| `InputReplayFinished()` | True from the frame the last recorded frame is played. The real devices are back from the frame after. |
| `RandomSeed(seed)` | Make `Random`, `RandomRange`, `Choose` and `DsListShuffle` give the same numbers every run. |

Starting a recording gives those random numbers a new seed, kept in the file; starting its replay
gives them the same seed again, so random choices made after the start repeat too. The frame in
which a script starts a recording or a replay is not part of it, and the simulation's fixed-step
clock restarts at the first recorded or replayed frame, so physics takes the same steps as well.
Frames behind a room change's cover, where nothing steps and the count depends on how fast files
are read, are neither recorded nor replayed. Pressing Escape during a replay stops it and hands
the game back to the real devices (a Player started with `--replay` then stays open); that press
is not passed to the game. Starting a recording or a replay stops one already running.

One script can serve both runs: `if (!InputReplayStart("level1")) InputRecordStart("level1");`
records the first time and replays every time after. An acceptance run needs no script at all:
the Player's `--replay <name or file>` (or `GENESIS_INPUT_REPLAY`) plays a recording from the first
frame of play and closes the game when it ends, and `--record <name or file>` (or
`GENESIS_INPUT_RECORD`) records from the first frame of play until the game closes. A plain name is
a file in the debug `Replays` folder; anything with a folder in it is a path. Pictures for such a
run come from `ScreenshotSave` at the frames that matter, for example when
`InputReplayFrame()` reaches them or when `InputReplayFinished()` turns true.

The file is a small binary format (`GENINPUT`, a version number, the seed, then one fixed-size
record per frame with the typed text after it); a Player refuses a version it does not know. A
replay reproduces what the game was given, not what the hardware did: a game whose behaviour
depends on something outside its input, time step and these random numbers (the clock on the
wall, files, the network, `Random` in C# code, how long `ChangeRoomWhenLoaded` waits while the
current room keeps running) can still go another way. A recording also starts
from wherever the game is when it starts, so start both runs from the same point, such as the
first frame of play or a fresh room. When nothing is recorded or replayed the cost is one check
per frame.

`Build.bat --test input-replay` records 40 frames of simulated keys, a tap shorter than a frame,
a click-and-drag, the wheel, a controller and typing at uneven time steps and two window sizes,
replays them while the simulated devices do something else, and checks that the script sees the
same input, time steps, window size and random numbers on every frame and ends in the same place;
then that Escape stops a replay and lets go of what it held, and that other files are refused.
Not yet checked: a recording made with a physical controller and played back in the real Player.

## Files that change while a game runs, and files a game looks for

### Live reload keeps a running game's Scripts

A game run from Studio (the Player with `--live-reload`) watches the project's Assets and, when a
file changes, empties its caches and rebuilds the room. Its Scripts used to be dropped with the
caches and read again only when an Object was next created, so:

- in the frame between the change and the rebuild every call to a Script or to a library
  function failed (`Unknown command: McScreenDraw`), and
- when the rebuild did not happen, because the room file was itself still being written or only a
  terrain's surfaces had changed, they failed for the rest of the run.

Now the Scripts are read again at once, when the change is seen. Only those whose text changed are
compiled again: compiling all 250 Scripts of a large game took 7.4 seconds, a frozen game for each
saved file. When a Script is added, removed or renamed, all are compiled again. A Script that does not compile keeps its earlier version, and if the Scripts cannot be listed
at all (two briefly sharing a name during a rename) the game keeps the ones it has.

A change is turned down, and the game goes on exactly as it was, while any file in it is half
written: JSON (including a resource's `.meta`) that does not parse, or PGSL (a library Script or an
Object's event) that cannot be read or does not compile. The log says which file and why:

```
AssetLiveReload rejected generation=4: 'Step.pgsl' does not compile (line 3: ...); the game keeps the code it is running
```

The save that completes the file is a change of its own and applies. A genuine mistake in a script
is reported the same way, and the game keeps running the code it had until the script compiles.

The watcher no longer reads which resource refers to which before the game starts: it rebuilds
everything on any change, and reading the references took nine seconds before the first frame in a
project of 11,000 resources. A change it cannot follow completely (a `.meta` still being written)
is no longer dropped. `Build.bat --test live-reload` runs a game in the harness and rewrites a
library Script and an Object's event while it runs, writes them cut off part way, and writes the
room as JSON that is not yet a room while a library changes; Scripts must resolve on every frame.

### Why a call to a Script fails

A call to a name that is neither a command nor a loaded Script is `Unknown command: Name`. When the
engine knows more, the error now says it, so a missing Script is not a mystery:

- **The Script did not compile** (the game's own compiler refused it when loading the project's
  Scripts, even if Studio's checker passed it):
  `Unknown command: McTest. The Script 'McTest' did not compile: Assets\Scripts\McTest.pgsl, line 812: ...`
- **The Script is in the project but the Scripts were not loaded** when it was called (a tool or
  editor dropped them): `... The Script 'McTest' (Assets\Scripts\McTest.pgsl) is in the project, but the
  project's Scripts were not loaded when it was called.`
- **Two resources share the Script's name** (a Script and a Model called the same): the error gives
  the resource-name clash and how to put it right.
- **A Script that did not compile may define it**, for a function of a library that failed (as
  before), with each failed Script's file, line and message.
- **In a worker job**, a call to a Script says that a job runs functions of the Scripts, not a Script's
  own code.

`Build.bat --test validate-project` with `GENESIS_VALIDATE_PROJECT` naming a project folder also
lists every Script the game's compiler refused (`LOAD ERROR Name: file, line: message`).

**A function named like a Script.** A call by name runs a function of that name before it looks for a
Script, so when `McAutoTool.pgsl` declares `function McAutoTool(...)`, the call `McAutoTool();` meant
to run the Script's own code runs the function instead: without arguments the Script's code silently
never runs, and with arguments that do not fit the function the call is an error. The project check
(F5, and `PgslScriptValidator.ValidateProject`) warns about every function (in a library Script or an
Object's event) that has the name of a Script with code of its own; a Script of functions only runs
nothing when called, so it is not reported. The error of a call that does not fit says it too:

```
Function 'McAutoTool' expects 2 arguments, got 0. 'McAutoTool' is both a Script (Assets\Scripts\McAutoTool.pgsl) and a function of the project's Scripts: a call McAutoTool(...) runs the function, never the Script's own code. Rename the function or the Script.
```

### A sound's file named beside its Audio resource

An Audio resource's `source` is looked for beside the resource first, then in the project. So
`Assets/Audio/mus_calm1.audio.json` may say `"source": "mus_calm1.ogg"` (or `"../Music/x.ogg"`),
and `PlaySound("mus_calm1")` plays it; before, only a path from the project
(`"Assets/Audio/mus_calm1.ogg"`, what Studio's Audio editor writes) was found, and the sound was
silently missing. Studio's paths still work. A file outside the project is not played, since an
export would not carry it; an export copies the project as it is, so what plays in Studio plays in
the exported game. The Audio editor shows such a source as the file it is. Checked by
`Build.bat --test runtime-resources`, in the project and in a copy laid out as an export.

### Listing a folder's resources

| Command | Meaning |
|---|---|
| `ResourceList(folder, type, subfolders?)` | A new list (a DsList: read it with `DsListSize` and `DsListGetString`, free it with `DsListDestroy`) of the names of the resources of one kind in a folder, sorted by name. `folder` is named from Assets (`"Shaders/Packs"`) or from the project (`"Assets/Shaders/Packs"`); `""` is Assets. `type` is a kind as `ResourceTypeOf` names it (`"Shader"`, `"Image"`, `"Audio"`, `"Object"`, `"Room"`, `"Model"`, `"Particle"`, `"Script"`...), `"Fullscreen shader"`, `"Mesh shader"` or `"Sprite shader"` for one kind of shader, or `""` for every kind. With `subfolders` true, the folders inside count too. A folder outside the project, or one that does not exist, gives an empty list. |

A shader-pack menu can offer every full screen shader the folder holds, without a list kept by hand:

```
packs = ResourceList("Shaders/Packs", "Fullscreen shader");
for (var i = 0; i < DsListSize(packs); i = i + 1) { AddPackButton(DsListGetString(packs, i)); }
```

The names are the resources' own (a resource renamed in Studio is listed by its new name), the
ones `PostEffectAdd` and the other commands take. It reads the project's resource list, which is
read once and kept, so it costs a walk over that list rather than over the disk (one kind of
shader also reads those shaders' files, to see which kind each is): call it when a menu opens,
not every frame. An exported game lists the same. Checked by `Build.bat --test runtime-resources`.

### A post effect turned on while a game runs

A post effect (`PostEffectAdd`, or a room's own list) whose shader the game has not compiled
before is now compiled on a worker thread. Until it is ready the frames are drawn without it (or,
when a running effect's shader file changed, with its previous version), and then it runs; the
renderer's log (`%LOCALAPPDATA%\GenesisRuntime\Logs\render-<process id>.log`, beside the errors of
effects that do not compile) says when:

```
Post effect 'Pack Ink' compiled on a worker in 1840 ms; it runs from this frame
```

Before, the frame that first asked for it waited for the shader compiler: on DX12, with the shader
not yet in the cache, one frame took 10.7 seconds. This applies to every graphics backend in a game
(the Player and exported games); Studio's previews and the capture tools still compile in the frame
that asks, so the frame they read back has the effect. `Build.bat --test post-effects` runs the
Player on DX12 with an empty shader cache, turns on an effect three seconds in, and checks that no
frame after that took 250 ms or more and that the effect then ran.

### Recording a profile without debug mode

`GENESIS_PROFILE=1` (or the Player's `--profile` argument) records the same profile as the debug
screen's Record button, for the whole run, without debug mode: no debug screen is drawn, nothing is
inspected and no live telemetry is written. The recording starts with the first frame of play and
is saved when the game closes, in `Debug/Profiles/<date-time>/` (`frames.csv`, `summary.json`,
`report.md`), and the log names the folder (`profile recording: ...`, `profile saved: ...`; the
Player also prints `GENESIS_PROFILE_RECORDING` and `GENESIS_PROFILE_SAVED`). It has the Engine
columns (update, gathering, drawing, presenting, HUD and the longest parts of each frame), PGSL time
per Object and event, the allocation and collections of every frame and the types allocated most.
What it costs is the recording itself: PGSL events are timed, a row is written per frame and
allocations are sampled. For a headless measurement:

```
set GENESIS_UNATTENDED_WINDOW=1
set GENESIS_PROFILE=1
GenesisEngine.exe
```

Checked by `Build.bat --test debug-screen`, which runs a game for three seconds this way and reads
the folder it leaves. Such a run is started as an exported game is, so its debug keys are off (see
the debug screen above); the recording does not need them.

### Starting a game quickly (11 October 2026)

A fresh install's first launch waits on the antivirus: Windows Defender scans each newly written
file the first time it is opened (about 4 ms a file on this PC), so every file the game opens one
at a time on its own thread adds up. What the Player does about it:

- **Resource index.** An export writes `.genesis/resource-catalog.txt` (each `.meta`'s guid,
  `resourceName` and size) and the game names its resources from it instead of opening every
  `.meta`. A `.meta` of another size, or written after the index (a mod), is read as before; a game
  without the index (Studio's Run) reads them all. The log says which:
  `resource catalog: <n> resources in <ms> ms (<n> identities from .genesis/resource-catalog.txt, 0 .meta files read)`.
- **Texture groups** read their descriptors and decode their pictures on a few threads of their
  own. A picture larger than its group's sheet still makes the stitch fail (the game then draws
  each sprite from its own texture, as before), but that is now found from the PNG headers before
  anything is decoded, instead of after decoding every sprite of the project.
- **Scripts** are read and compiled on a thread of their own from just before the window opens
  (`GENESIS_SCRIPT_WARMUP=0` turns it off). The first PGSL event waits for that load when it is not
  done; it used to do the whole load itself.

Start-up work that runs side by side uses threads of its own (`StartupWorkers`), never the thread
pool, so it never waits for a pool thread while the game's thread holds a lock.

A freshly exported test game once wrote nothing for 90 s and was stopped (10 Oct 2026), and the
change then blamed (an earlier version of the above) was taken back. It was not that change: 25
launches with it never stopped, and on 11 Oct the same symptom hit a build without it, and one of 30
launches of this code before any engine code had run (no `Main` after 60 s), each while about 900 MB
of new files were copied beside the game; a GenesisCraft export started during such a copy took
145 s to its first room, 30 s of it before the Player's own code ran. 30 more fresh launches on a
quiet PC all started, in 7.4 to 8.1 s.
A start competing with a big copy, build or export for the antivirus can look stuck.
`GENESIS_STARTUP_TRACE=<file>` writes each start-up step to that file as it happens (its first line
before any engine assembly loads; the thread pool's state every two seconds), and
`Export.PgslOnly.FreshCopiesStartEveryTime` (`--test export-pgsl`) starts three fresh copies of an
exported game and quotes that trace when one does not finish.

Measured on a copy of GenesisCraft (5,232 resources, 190 Scripts holding 42 MB of PGSL, a Texture
Group whose 1024x2784 picture never fits; i7-14700F, Defender on, other sessions building), with
`GENESIS_LOAD_PROFILE=1`. A fresh copy of its export, first launch, the old code and the new one
started the same way one after the other:

| First launch of a fresh export | Before | After |
|---|---|---|
| Listing the resources | 6.0 s | 4.5 s (index; the rest is listing 17,000 new files) |
| Texture groups | 7.2 s | 3.0 s |
| The first room's Create events | 13.5 s | 3.2 s (Scripts compiled beside, 21 s of reading and compiling) |
| Process start to the first room | 46.3 s | 47.1 s (its graphics device took 11.8 s instead of 2.7 s) |

A first launch is still 25 to 50 s on a busy PC: 3 to 13 s before the Player's own code runs (the
runtime's files scanned), 4.5 s listing the resources, 3.5 s reading the first room (assemblies
loaded and scanned) and a graphics device of 2 to 12 s. Later launches took 6.0 to 6.7 s (7.0 to
8.1 s before). The Player's part of Studio's Run took 6.8 s either way: its Scripts take 4.3 s on
one thread (the 17 MB one 1.8 s; each text is prepared twice, in `ScriptAssetCompiler` and again in
`VMEngine.Compile`, 0.5 s) and the Create events' own work 1.2 s. Next: compile Scripts side by
side or keep their compiled form between launches, and list an exported game's files from its index.

### Counting frames per second

| Command | Meaning |
|---|---|
| `GameGetFps()` | Frames shown per second, counted over the last half second: what an FPS counter should show. `Fps` is 1 divided by this frame's own time, which moves a little from frame to frame. |

A counter that shows `Floor` of `Fps` (or of an average of it) shows 59 in a game held at exactly 60:
frame times alternate between 16.6 and 16.7 ms, and a value that sits a hair under 60 is floored to
59. Measured on 9 October 2026 in unattended runs of the Player (DX12, six seconds each), the cap
holds its rate: a cap of 60 ran at 60.00 frames a second with VSync on or off (every frame 16.6 to
16.7 ms apart), 120 at 119.99, 144 at 143.98, Unlimited at about 9,000 in an empty room, and VSync
with no cap at 60.00. Show `Round(GameGetFps())`.

### Music decoded off the frame

A game's sound files of 1 MB or more (music, long ambience) are decoded on a worker thread. A play
of one counts as playing at once (`IsSoundPlaying` is true) and is heard as soon as the samples are
ready, a moment later; the frame that asked goes on. Each piece of music used to be decoded whole
in the frame that first played it: a game's log showed such frames of 143 to 733 ms
(`loading in that frame: 1 sound 495 ms`). A sound that cannot be decoded stops counting as
playing. Every Ogg Vorbis sound is decoded on a worker too, whatever its size (see the next
section); a short WAV is only copied, and is still ready in the frame that plays it.
Studio's Audio editor decodes everything at once. `GENESIS_AUDIO_BACKGROUND_DECODE=0` turns it off
in a game. Checked by `Build.bat --test runtime-resources`.

### Sounds ready before they play (10 October 2026)

**Why a first sound held a frame.** GenesisCraft's exported game had frames of 105 to 434 ms a few
seconds into a world, each `loading in that frame: 1 sound 420 ms`. Sounds under 1 MB were decoded
whole in the frame that first played them, and an Ogg file's size says little about its length: its
650 KB underwater loop is 27 s of stereo sound. Measured on this PC (i7-14700F) in a new process,
decoding it took 242 to 262 ms the first time and 50 to 110 ms once the decoder's code was
optimised; a 0.6 s creature sound took 30 to 95 ms the first time. Each sample was also added to a
list and copied out: 5 ms of a 7 ms decode in a game's first seconds. Now:

- A game decodes every Ogg sound on worker threads of the sound system's own (two to four), and
  any other sound file of 1 MB or more. The first play counts as playing at once and is heard when
  the samples are ready, normally the next frame. A play goes ahead of sounds only preloaded.
- The decoder writes the samples straight into the array the sound keeps.
- Every way a script names a sound is one sound, decoded once: the Audio resource's name
  (`"snd_hurt_1"`), its document from the project or from Assets (`"Assets/Audio/snd_hurt_1.audio.json"`,
  `"Audio/snd_hurt_1.audio.json"`, `"snd_hurt_1.audio.json"`), its sound file from the project or
  from Assets (`"Assets/Audio/snd_hurt_1.ogg"`, `"Audio/snd_hurt_1.ogg"`), or the sound file's own
  name when the resource of that name plays it (`"snd_hurt_1.ogg"`). `"Audio/..."` and the bare file
  name used to find nothing.
- An exported game no longer reads four files' times on every `PlaySound` to see whether the sound
  changed; Studio's play (with live reload) still does.

| Command | Meaning |
|---|---|
| `SoundPreload(sound)` | Start decoding a sound on a worker now, so its first `PlaySound` or `PlaySoundAt` starts at once. Never waits. `sound` as `PlaySound` takes it. False when there is no such sound. |
| `SoundsLoading()` | How many preloaded sounds are still being decoded; 0 once all are ready. |

A room's loading cover waits for the sounds preloaded while it is up, so preloading a world's
sounds in a Create event (`SoundPreload("snd_cow_say_1")`, or a loop over
`ResourceList("Audio", "Audio")`) has them ready when the room is shown. Sounds an Object plays on
arrival are preloaded behind the cover the same way. Only preloads are waited for, never a sound
that is only played, and the cover's time limit (12 s) still holds.

A project can have its sounds decoded while it loads without naming them, in its `.genesisproj`:

```json
"runtime": { "preloadAudioMegabytes": 200 }
```

Its Audio resources are decoded on the workers, smallest file first, until that many megabytes of
samples are held (decoded, an Ogg file takes 7 to 20 times its size: GenesisCraft's 2,212 sounds
under 1 MB, 88 MB of Ogg files, would take several hundred MB). The first room's cover waits for them, up to its
time limit; the rest go on decoding during play. The log says what was done, as
`sounds decoded ahead of time: <decoded> of the project's <sounds> (<MB> MB of samples, budget <N> MB) in <ms> ms`.

0, the default, preloads nothing. Checked by `Build.bat --test runtime-resources` (every name of a
sound, an Ogg decoded off the frame and a short WAV at once, a preloaded sound starting at once in a
game's and an editor's mixer and through the script commands, the budget smallest first and
stopping at its size) and `Build.bat --test room-change` (the cover waits for preloaded sounds).

The Audio editor's source field is labelled **Source** (it lists WAV and Ogg files), and its import
button **Import sound…**.

### Post effects for shader packs (10 October 2026)

Shader packs kept as Fullscreen Shader resources need nothing new: `ResourceList("Shaders/Packs",
"Fullscreen shader")` lists them and `PostEffectAdd` runs one. What was missing is now there (details
and an example in [Post effects](PostEffects.md#shader-packs)):

| Command | Meaning |
|---|---|
| `PostEffectAddFile(path)` | Run a plain `.hlsl` file of the game's folder (from the project folder or Assets) as a post effect, compiled on a worker and again when the file changes. Its name is the path given. False when there is no such file. |
| `PostEffectSetTexture(shader, slot, image)` | Give a running effect an Image resource (or a `TextureCreate` texture) at register 3 to 15, or by its name in the shader. |
| `PostEffectIsRunning(shader)` | True once the effect is compiled and drawn. |
| `PostEffectError(shader)` | Why it does not run, in one line (`Broken.hlsl:1:38: error: use of undeclared identifier 'x'`), or empty. |
| `PostEffectLastError()` | The newest such reason of any effect still asked for, as `effect: reason`. |

The textures a Fullscreen Shader resource binds at `t3` and above in the Shader editor now reach it as
a post effect too; a declared texture nothing binds reads white. Checked by
`Build.bat --test post-effects` on DX11, DX12, Vulkan and OpenGL: a pack file given a red Image reads
it through a repeating sampler (with an unbound texture reading white), turns green when its file is
rewritten, and a file that does not compile and one that is missing say why.

A slow-frame line (see [Finding what made a frame long](#finding-what-made-a-frame-long)) now counts
the scripts' Draw events in "gathering what to draw" and names them among the longest parts
(`scripts' Draw events`), with `waiting for the graphics card to begin` (beginning a frame waits
for the graphics card to give its buffers back) and `project post effects`. All three used to be
part of the time "outside the frame's own work", which was most of a game's long first frames.

## GPU culling of instanced batches (22 October 2026)

A batch of 64 or more instances of one mesh is checked against the camera on the GPU before it is
drawn. Batches like this come from scattered grass, trees and rocks, a crowd of one model, or
DrawMesh calls the renderer groups. A compute pass tests each instance's bounding sphere against
the sides of the view, writes the instances in view one after another into a second buffer, and
counts them into the batch's draw arguments. The batch is then drawn with those arguments, so
instances out of view cost the GPU nothing.

It runs on DX11, DX12, Vulkan and OpenGL. The software renderer draws every instance. Shadows and
reflections draw every instance too, since they look from elsewhere. `GENESIS_GPU_CULLING=0`
turns it off for a run. The renderer's statistics give the batches culled this way
(`GpuCulledBatches`) and, read back a few frames late, how many of their instances were in view
(`GpuCullVisibleInstances` of `GpuCullCandidateInstances`).
## DX12 pipeline states kept between runs (22 October 2026)

On Direct3D 12 a pipeline state, made the first time a draw needs it, is compiled by the driver,
and a first compile can hold up a frame. The pipeline states a game makes are now kept on disk in
a D3D12 pipeline library, `DX12Pipelines\pipelines-<build>.bin`, beside the shader cache
(`GENESIS_SHADER_CACHE`, otherwise `%LOCALAPPDATA%\GenesisRuntime\ShaderCache`). The next run
loads them instead of compiling them again.

Each pipeline is filed under a hash of what it is made from: shader code, blend, depth and raster
states, attachment formats, topology and vertex layout. A new engine build starts a new file and
deletes the old one. A file written by another driver or GPU is not accepted by D3D12, and a new
one is made in its place. The file is written when a viewport or the game closes, and every ten
seconds while new pipelines are being made. The render log gives each run's count ("Pipelines
this run: N loaded from the library, M compiled"). `GENESIS_DX12_PIPELINE_LIBRARY=0` turns the
library off. Vulkan's pipeline cache is not kept yet.
## Texture streaming by distance (22 October 2026)

Model textures are cooked at import to BC7 (normal maps to BC5) with their mips, and other
textures get their mips when they are first read. A texture with mips larger than 512 texels a
side now starts on the graphics card from the level that fits 512: a 4096 texture takes a
sixty-fourth of its memory until something needs it, so a world full of big textures loads
quickly. The texture's handle stays the same throughout.

Each frame, every mesh drawn with a streamed texture says how much detail it needs: as many texels
as the pixels it covers on screen. A texture asked for more is read again on a worker, from the
level wanted, and swapped in at the start of a frame. When the textures on the card take more than
the budget (`GENESIS_TEXTURE_BUDGET_MB`, 1 024 MB unless set), a texture whose detail has not
been asked for in five seconds goes back to the level it is wanted at, never below its starting
512. `GENESIS_TEXTURE_STREAMING=0` loads every texture whole, as before. The software renderer
and pictures without mips (GUI images) are never streamed.
## Surface shaders, a shader per material and each instance's own camo

Added 9 October 2026 (request 33 of the Aetherium port). [Mesh shaders](MeshShaders.md) has the
whole contract.

- **Surface shaders.** A mesh Shader resource that starts with `#include "GenesisSurface.hlsl"`
  writes one function, `void Surface(GenesisSurfaceInput i, inout GenesisSurface s)`, which is
  handed the material's own albedo, alpha, normal, roughness, metalness, occlusion and emission and
  may change any of them; the engine then lights the result exactly as it lights its own materials
  (sun and shadows, ambient, point and spot lights and their shadows, environment reflection, wet
  weather, fog, glow). The inputs are documented and versioned: world position, normals, view
  direction, camera, UVs, the draw's colour, the material's base texture, screen position, time,
  frame, resolution, the sun and the ambient light. `GenesisColor`, `GenesisFresnel`,
  `GenesisPerturbNormal`, `GenesisLuminance` and the sampler `GenesisLinearWrap` help. A camo
  pattern, lit windows at dusk and glass with a Fresnel edge no longer copy the engine's lighting or
  its constant buffers. Shaders without the include are full pixel shaders, as before.
- **A shader per material.** A model's material can have a mesh shader of its own, chosen in the
  Model editor (**Texture** page, **Material & PBR**, **Shader**) and kept in the `.model.json`'s
  `materialShaders`. It now wins over the Object's shader in a room too, as it already did in the
  Object editor's preview: a building's glass keeps its glass shader while the Object's shader
  covers the walls.
- **Each instance's values and images.** `ShaderSetParameter`, `ShaderSetVector` and their
  `Instance…` forms now reach the shaders of an instance's model materials as well as its Object
  shader. New: `ShaderSetTexture(name, image)` / `InstanceSetShaderTexture(id, name, image)` swap one
  of the instance's shader textures, and `ModelSetMaterialTexture(material, slot, image)` /
  `InstanceSetMaterialTexture(id, material, slot, image)` swap one material's image on one instance:
  `albedo`, `normal`, `orm`, `emission`, or a texture that material's shader declares. Draws with
  the same shader, values and images still share one instanced batch; an instance with values of
  its own takes a batch of its own and nothing else changes.

`Build.bat --test mesh-surface` checks all of it on DX11, DX12, Vulkan and OpenGL: an empty surface
and a surface painting its parameters' colour match the engine's material pixel for pixel at tile
resolution (difference 0.00 on all four), including sun shadows and a lamp; emission, see-through
glass, three guns each wearing its own camo through one material shader, an Object shader on the
rest of the model, a swapped pattern and a swapped albedo; and twelve boxes sharing values in one
batch, one more for an instance with values of its own. Not yet seen in a running game.

## Outlines and the object images

Added 9 October 2026 for readability in fast games: teammates in blue and enemies in red, seen
through walls or not, a pickup lit at its edge.

| Command | What it does |
|---|---|
| `OutlineSet(r, g, b, width, throughWalls)` | Outlines this instance's 3D draws (its model's meshes, a script's meshes, its image cube) in a colour (0 to 1 each), `width` pixels wide (up to 16). With `throughWalls` the outline shows where the instance is behind something; without, only around what the camera sees of it. Width 0 marks the instance for post effects without a line. |
| `OutlineClear()` | Takes it away. |
| `InstanceSetOutline(id, r, g, b, width, throughWalls)`, `InstanceClearOutline(id)` | The same for another instance. |
| `InstanceHasOutline(id) -> bool` | Whether an instance is outlined or marked. |

How it works: each marked draw is drawn a second time into two images the size of the screen,
**ObjectMarks** (its outline colour and width) and **ObjectIds** (its instance id, negative where
it is hidden behind something), keeping the nearest marked surface at each pixel. A full-screen
pass then draws each outline around its marks over the finished frame, before held items and the
project's post effects; a line runs between two marked instances that touch, never inside one.
Post effects read both images (`t3`, `t4`; see [Post effects](PostEffects.md)), so a project can
add a fill behind walls, a pulse or a highlight of its own on the same marks. Nothing of this
runs in a frame without marks. Outlines need the frame's post-processing (the normal case for a
game window); the Software renderer draws none. Model layers (a first-person weapon) and the sky
layer are not outlined.

C#: `MeshDrawCall.Outline` (colour and width), `OutlineId` and `OutlineThroughWalls` on any draw;
the commands above are static methods on `PgslCommands`.

`Build.bat --test outlines` checks on DX11, DX12, Vulkan and OpenGL that a box's outline is drawn
around it and not over it, that a box behind a wall is outlined through it only when asked, that a
post effect finds a box's id (and the hidden box's negative id), and that an outline set from a
script reaches its model's draws (and only its own) through the draw pass a game uses. It also
measures the cost at 1920 x 1080 on DX11.

## Big worlds at hundreds of frames a second (22-23 October 2026)

Commands for games with large worlds, many things in them and a lot of script work: data kept as
numbers, meshes and changes made on worker jobs, things placed once and drawn by the engine, and
measurements that show where a frame goes.

### Numbers kept as numbers

`NumListCreate()` makes a list that holds only numbers, unboxed: `NumListAdd`, `NumListAdd3`,
`NumListGet`, `NumListSet`, `NumListResize`, `NumListReserve`, and range commands (`NumListFill`,
`NumListCopy`, `NumListAddRange`, `NumListScale`, `NumListSort`, `NumListSum`, `NumListMin`,
`NumListMax`, `NumListIndexOf`, `NumListToList`). `DsGridToNumList` and `DsGridFromNumList` move a
grid's cells in one call, `DsListCopyRange` and `DsListAddRange` move ranges of ordinary lists.
Ordinary lists and maps still take any value; a whole number from -1024 to 65535 or true/false
stored in one shares a box with every other copy (nothing allocated), and a large list destroyed
gives its storage to the next `DsListReserve` on an empty list.

### Meshes built on jobs and things placed once

| Command | What it does |
|---|---|
| `JobScriptMesh(job, mesh)` | Lends a mesh to a job: the job builds it (`MeshAddQuad...`, `MeshAddCube`, `MeshAddQuadsFromNumList`) in pooled arrays, and `JobTake` hands the finished build over; it is uploaded within `MeshSetUploadBudget`. |
| `MeshStaticAdd(mesh, shader, x, y, z, sx, sy, sz, yaw, image)` | Places a script mesh once (the arguments of `DrawMeshShader3D`); the engine draws it in every view it is inside, with no Draw event. `MeshStaticMove`, `MeshStaticRemove`, `MeshStaticClear`, `MeshStaticCount`, `MeshStaticDrawnCount`. |
| `ModelStaticAdd(model, x, y, z, pitch, yaw, roll, scale, castShadows)` | Places a Model once with no Object: rocks, trees, props by the thousand. The copies of one model in view are drawn together (one instanced draw per part), at the level of detail their size on screen calls for. `ModelStaticAddFromNumList(model, list, castShadows)` places many (7 numbers each). `ModelStaticRemove`, `ModelStaticClear`, `ModelStaticCount`, `ModelStaticDrawnCount`. |
| `InstanceSetPositionsFromNumList(ids, xyz)`, `InstanceGetPositionsToNumList(ids, xyz)` | Moves or reads many instances in one call. |
| `InstanceSetTransformsFromNumList(ids, list)` | Places, turns and sizes many instances (9 numbers each: x, y, z, pitch, yaw, roll, sx, sy, sz). |
| `InstanceSetTintsFromNumList(ids, rgba)` | Tints many model instances (4 numbers each). |

Placed meshes and models are kept in 64 m cells: a view skips every cell out of sight and then
each placement by its bounds, so 20 000 placements over 2 km take under half a millisecond a view.
They end with the room.

### More on jobs

| Command | What it does |
|---|---|
| `JobScriptAfter(job, other)` | The job waits ("waiting") until the other has finished, then reads what it changed. |
| `JobScriptNumList(job, list, copyBack)` | Gives a job a number list. |
| `JobScriptListRegion(job, list, start, count, copyBack)`, `JobScriptNumListRegion(...)` | The job reads the whole list; `JobTake` writes back only its range. Several `JobScriptGridRegion` rectangles of one grid may be given to one job. |
| `JobCacheNumList(name, size)`, `JobCacheGet(name)`, `JobCacheSet(name, value)`, `JobCacheReset()` | A worker's own tables and numbers kept between its jobs (a lookup table filled once). |
| `JobScriptUrgent(job, true)` | The job is taken by the next free worker before normal jobs waiting. |
| `SceneQueuePosition(id, x, y, z)`, `SceneQueuePositionsFromNumList`, `SceneQueueVariable(id, name, value)`, `SceneQueueDestroy(id)`, `SceneQueueStaticMove(...)` | In a job: changes made in order when the game takes the job, so jobs can update their share of the instances side by side. Elsewhere: made after the frame's Step events (`SceneQueueApply()` makes them at once). `SceneQueueCount()`. |

A job uses its worker's own script engine, reused, and costs about 3 KB of garbage from
`JobScriptCreate` to `JobRelease`.

### Faster script functions

A script function called often whose work is numbers runs compiled: it may read and write its own
variables and the instance's number variables (and x, y and the other built-in fields), call
commands that take and give numbers, and call other such functions. Each call checks that the
instance's variables it reads hold numbers, and is interpreted otherwise, so results are always
those of the interpreter. Text given straight to a command (`VariableSet("gjStep", n)`,
`DsMapGet(map, "key")`, `DsListAddString(list, "x")`) does not stop it: the command is called the
interpreter's way. A function that works with text (keys made with `StringOf`, parts of a split
text, a value that may be text) is compiled with values instead: each variable holds what the
interpreter's would and each operation does what the interpreter's does, and it calls the
numbers-only form of the functions it calls whenever their arguments are numbers. A function
compiled with numbers only calls commands that cannot run a script (data, text, maths, variables,
drawing, or numbers-only commands). One compiled with values calls any command and any function,
interpreted ones too: before a command that may run a script (`CreateInstance`, a room change), a
call by a computed name, a variable read by a computed name, or a function that reads its caller's
variables, it puts its variables in a frame as the interpreter would have them, so the code called
sees what it would interpreted. Functions that call each other are compiled together, a variable
read before it is set gives what the interpreter's gives, and a function that still cannot be
compiled says why in the profile report. GenesisCraft flying at render distance 18: game thread
6.9 -> 5.4 ms a frame (scripts 6.2 -> 4.8 ms).
`GENESIS_PGSL_TIER2=0` turns compiling off, `GENESIS_PGSL_TIER2_VALUES=0` only compiling with values.

**Put heavy work in a function, not in an event's own code (measured 25 October 2026).** Functions
compile because their variables are the call's own, kept in slots. An event's own code (Step, Draw,
Create) is the instance's: every name it assigns, a `var` included, is one of the instance's
variables, kept by name, as other events and scripts may read them. Compiling event code with those
meanings kept was tried and measured: a Step event with a 400-step loop ran 500 times in 29 ms
compiled against 18 ms interpreted, the lookups costing more than the interpreting saved, so event
code stays interpreted. A loop or a calculation in a function the event calls runs compiled.

`ScriptProfileEnable(true)`, `ScriptProfileReport(count)` and `ScriptProfileReset()` time every
script function (`GENESIS_SCRIPT_PROFILE=1` writes `Debug/Logs/script_profile.txt` at the end).

### Where a frame goes

`FrameBudgetEnable(true)` counts each part of every frame (each subsystem's update and draws,
physics, the scripts' events, static meshes and models) against a budget: `FrameBudgetSetFrame(ms)`,
`FrameBudgetSet(part, ms)`, `FrameBudgetReport(count)`, `FrameBudgetPartMs(part)`,
`FrameBudgetReset()`; `GENESIS_FRAME_BUDGET=1` writes `Debug/Logs/frame_budget.txt`. A profile
recording (`GENESIS_PROFILE=1` or `--profile`) does both on its own: its report gains **Frame parts**
and **Hottest script functions**, the second saying why each interpreted function is not compiled.

### The engine's own benchmark

`BuildTools/Benchmarks/BenchmarkScenes.ps1` has a synthetic **Complex Scene**: chunk meshes built
on jobs and placed once, a 4096 x 256 world grid streamed by jobs, 1000 instances moved by jobs, 500
with their own Step events, 100 000 placed props, 200 script mesh draws a frame, sparks and the high
quality tier. Every publish that changes the engine measures it and adds it to
`TestResults/Benchmarks/history.jsonl` (23 October: about 250 frames a second on the DX11 test PC).

### Controllers

Silk.NET polled all sixteen gamepad and sixteen joystick slots on every window update; empty slots
are now looked at twice a second (connected controllers every frame, as before).
`GENESIS_SILK_INPUT=every` keeps the old polling.

### Large Scripts start in seconds

A game's Scripts are read and compiled on several threads while it starts, the largest first, and
each still sees the names of the Scripts before it (a call written without brackets) as when they
were compiled in turn. Reading and compiling cost a fraction of what they did: the shorthand
rewrite scans each `;` and `(` instead of matching every identifier, names and numbers are made
into strings once, tokens are values rather than objects, a Script's text is prepared once rather
than twice, and the compiler runs as optimised code from the start. A large function is decoded for
the interpreter on the thread that compiled it. Number literals are read the same in every
culture (a PC writing 0,5 read `0.0625` as 625).

GenesisCraft (195 Scripts, 44 MB, most of it generated data) took 4.7 s to compile its Scripts and
held its first room's Create event 4.7 s; now about 1.5 s for each, and its first room is ready
under 5 s after the game starts instead of over 8. The list of what the first room uses follows
quoted names only when they could be a file, and is half as long to make.

A Script of 64 K characters or more is also kept compiled on disk, in
`%LOCALAPPDATA%\GenesisRuntime\ScriptCache` (`GENESIS_SCRIPT_CACHE`: another folder, or 0 to turn it
off): while it, the names of the Scripts before it, the command table and the engine build are
unchanged, the next start reads it back instead of preparing and compiling it. Only the three
newest engine builds' entries are kept, and an entry that cannot be read is compiled again. With
it GenesisCraft's Scripts take 0.7 s and its first room is ready about 3.1 s after the game starts.
A large Script also keeps only a hash of its source and drops its prepared text once compiled (about
170 MB less memory for GenesisCraft). The load profile (`GENESIS_LOAD_PROFILE=1`) now says how much
of the start went to the JIT (GenesisCraft: 9,400 methods, about 1 s over all threads).

For data loaded from a Script, `DsListAddNumbers(list, "0.5, 1, -2.25")` and
`NumListAddNumbers(list, text)` add every number in a text in one call (commas, semicolons or spaces
between them), many times faster to compile and to run than a `DsListAdd` for each number.

## Smaller changes

- **Bushes and saplings.** The `Shrub` and `Sapling` foliage shapes are built from rounded solid
  clumps (40 triangles near the camera, 8 to 16 further away) instead of crossed flat cards.
- **Scripts could not start a network session.** See [Multiplayer](LargeWorlds.md#multiplayer).
- **Text over overlay sprites.** Text an Object draws in the overlay pass is drawn after that
  Object's overlay sprites, so a label is on top of its own panel.
- **Text in a size not drawn before.** The frame that first shows a line of text in a new size
  has to send its letters to the graphics card. They now go as one strip for the line instead of
  one update for each letter. It matters on Vulkan, where each update is waited for: a line of 17
  new letters took 7 to 11 ms singly and 1 to 4 ms as a strip on an idle graphics card, and a game
  on Vulkan had frames of 80 to 135 ms when a title or a line of speech first appeared. Measured
  in that game afterwards, with the same scripted run both ways: sent singly, the frame in which a
  card first showed three new sizes (about forty letters) took 134 ms, 116 ms of it here; as
  strips, no frame in the run spent more than 9 ms on its HUD. `GENESIS_GLYPH_STRIPS=0` sends them
  singly again, for comparison.
- **Exported games ship their model cache.** Export writes each large model's binary cache into the
  game. These caches are checked against the model's content instead of its modified time, so they
  survive being copied, zipped or installed. A player's first load is as fast as later ones.
- **Importing a model does its slow work once, off the editor.** The Model editor's Import / Replace
  imports the file once on its worker (it used to import a throwaway copy, save it as JSON, hash the
  source and delete it, then save again on the editor's thread). The worker also makes the cooked
  model's text, its binary cache and the source's hash; the editor only writes them. So the first
  open after an import reads the cache, and the source is not read again to check it. A model
  cooked when a Model resource is first opened writes its cache at once as well.

## Verification

`Build.bat --test large-world` covers these with harness cases: the weather subsystem (rain, snow,
flash, thunder delay, script strike), camera-following particles at altitude, a
terrain's own water look through save and reload, animation events (once per pass, none while time
is still), foliage shapes, sealed caches surviving a changed modified time, the controller
commands, panning and the sound commands. The sound case also plays a silent sound on the real
mixer and checks that it is panned, that a fade-out stops it, that it can change group and that a
muted device stays muted. Further cases cover a four-bone figure (sockets and bones at rest and in
motion, a clip started part-way, a strike on the spine while the leg walks, fading, a reversed
clip, marks passed in both directions), tint, glow and emission as they reach the renderer's draw
calls, the HUD shapes and text measuring, bursts removing themselves, save slots (including names
that would leave the save folder), the sun's shape across a 100-degree view, and a meadow with a
kind of plant refused.

Text in a new size being sent as one strip, and reaching the screen on four graphics backends,
is one more case in the same suite. Room changes have ten cases of their own: a change spread over frames with nothing stepping
meanwhile, Create events still running together once the room is whole, the one-step change and
the 2D room that appears without a cover, the first room of a game prepared behind the cover,
emitters and arriving sounds made ready behind it, the engine's and a game's own loading screen
(from an Object and from a painter), the cover
hiding a room and fading from it on four graphics backends, collision prepared on worker threads
and handed over, a texture read in the background, a mesh waiting for its textures, and the
slow-frame line. The weather case also checks that rain is carried the way the wind blows and
starts upwind of the camera, and the model case that a material's light takes a colour.

Seen in a running game on 2 October 2026 (the test island): rain leaning with a thunderstorm's
wind; a room change spread over frames, from the Player's log; and the loading screen itself, in
pictures the Player saved of its covered frames. A game's own loading screen, drawn from an Object
or a painter, has been checked by the harness and not looked at in a game by this work.

Not seen or heard in a running game:

- Lightning and thunder in play; rain was seen in the test island (captures, 2 October 2026).
- A terrain's own water colours on screen.
- Animation events driving gameplay.
- Text over overlay sprites.
- A sealed cache in a real exported game.
- A real controller. The commands were checked against the engine's input state, not hardware;
  vibration in particular has not been felt.
- Panning and fades by ear. The levels sent to the mixer were checked; nobody has listened.
- Part-body clips, tint, glow and emission on a real character. The poses and draw calls were
  checked, not the picture.
- The round sun and the unbanded sky on screen. The geometry was checked; the noise is in a
  shader that the harness does not read back.
- HUD circles and polygons on screen. The new grass and flowers were seen in the Terrain editor's
  view (a walkthrough capture), not in a game.
