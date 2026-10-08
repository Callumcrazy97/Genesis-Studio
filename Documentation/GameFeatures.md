# Game features: weather, models, animation, controllers, sound, HUD, saves, room changes

Engine features added on 2 October 2026 for games in general. None of them is specific to one
project, and each is off or unchanged until a room, an Object or a script asks for it, except where
this page says a default changed.

[Large worlds](LargeWorlds.md) covers the other half of the same work: streaming, the model cache
and multiplayer. [Menu features](MenuFeatures.md) covers the 3 October additions: text layout,
smooth shapes, clipping, globals, quitting and menu input.

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
`GamepadKeyboardEmulation(false)` so one press is not two actions. The first controller is the one
read; a second is ignored.

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

## Particles: a burst that cleans up after itself

`Game.PlayParticleBurst(asset, position, scale, emitSeconds)` (events: `ParticleBurstAt`) plays a
Particle resource once and removes the emitter when the last particle has lived its life. An effect
that emits continuously is given `emitSeconds` (a quarter of a second if 0), stopped, and removed
when it has died away.

An emitter that is kept can fire its burst again: set `Restart` on its `ParticleComponent`.

An effect that follows the camera (`FollowCameraXZ`, as the built-in rain and snow do) ignores its
entity's position; that is what makes it weather. Any other effect is drawn where its entity's
`TransformComponent` is, read every update.

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
- **Engine** appears only when Studio's developer setting *Show Engine debug category* is on.

**F8** (or the Record button) records a profile. A debug run (Studio's Debug button) records from
the start unless *Start recording when debugging starts* is turned off in Preferences → Runtime.
Each recording is a folder in `Debug/Profiles/` holding `frames.csv` (one row per frame),
`summary.json` and `report.md`: the slowest frames, the hottest PGSL objects and events, and
warnings about allocation, garbage collection, long frames and script errors. The recording
finishes when F8 is pressed again or the game closes.

### Finding what a game allocates every frame

With `GENESIS_ALLOCATION_LOG=1` (or when the game runs with the debugger) the Player writes, every
five seconds of play, how much managed memory the game allocated and how many collections that
cost; `GENESIS_ALLOCATION_LOG=types` also names the types allocated most:

```
Managed allocations: 14.5 MB in 5.0 s over 300 frames (49.5 KB a frame, 2.9 MB/s), 1 gen0 collections
Most allocated types: System.Double 5.1 MB, System.Threading.ExecutionContext 1.3 MB, ...
```

Frames behind the start-up screen or a room change's cover are not counted. An idle Verdant
Hollow allocated 3.9 MB a frame (a simulated lake rebuilt its whole surface, the foliage planner
regrew its lists and every particle layer rebuilt its colour table, each frame); it now allocates
about 50 KB. The `speed` headless target fails when an idle game allocates 1 MB a frame or more.

### What a game shows while it starts

The Player draws a loading screen (the engine's name, a bar and what it is doing) as soon as its
graphics device is ready, and again before it reads the textures and builds the first room; the
start-up screen then takes over. Its built-in DX11 shaders compile on worker threads from the
moment it starts (`GENESIS_SHADER_WARMUP=0` turns that off), which matters after an engine update,
when nothing is in the shader cache yet.

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
| `CharacterCreate(radius, height, stepHeight, maxSlopeDegrees)` | Give this instance a character at its own position. |
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
| `MeshVertexCount(mesh)`, `MeshTriangleCount(mesh)` | Its size. |
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

## Noise

Noise for generated terrain, caves, clouds, textures and wobble, worked out natively. The same
arguments give the same number on every PC, graphics backend and run: only additions,
multiplications, `Floor` and integer hashing are used (no sine), so a world generated from a seed
is the same world everywhere. Any number is a seed, fractions included (`0.5` and `0.7` differ).

| Command | What it does |
|---|---|
| `Noise2D(x, y, seed)`, `Noise3D(x, y, z, seed)` | Smooth gradient (Perlin) noise, -1 to 1. Hills and hollows are about one unit apart, so scale positions down (`Noise2D(x / 32, z / 32, seed)`). Each seed shifts the lattice, so whole-number positions still vary. |
| `ValueNoise2D(x, y, seed)`, `ValueNoise3D(x, y, z, seed)` | Smooth value noise, 0 to 1. At whole-number positions it is a repeatable random number per point (where a tree goes, which ore). |
| `FractalNoise2D(x, y, seed, octaves, lacunarity, gain)`, `FractalNoise3D(x, y, z, seed, octaves, lacunarity, gain)` | Several octaves of gradient noise summed (fBm), -1 to 1: each octave `lacunarity` times finer (2 is usual) and `gain` times weaker (0.5 is usual); 1 to 16 octaves. One octave is exactly `Noise2D` / `Noise3D`. |
| `NoiseFillGrid(grid, x0, y0, step, seed, octaves, lacunarity, gain, scale, offset)` | A whole DsGrid in one call: cell (i, j) becomes `offset + scale * FractalNoise2D(x0 + i * step, y0 + j * step, seed, octaves, lacunarity, gain)`, exactly what that call gives. Returns the cells filled (0 for a bad grid). |
| `NoiseFillGrid3D(grid, x0, y0, z0, step, plane, seed, octaves, lacunarity, gain, scale, offset)` | A flat slice of `FractalNoise3D`: `plane` `"xy"`, `"xz"` or `"yz"` names the axes `i` and `j` step along from `(x0, y0, z0)`; the third stays put (one layer of a cave field). |

Measured on the development PC's performance cores (8 Oct 2026, `--test pgsl-logic`, called from
C#): `Noise2D` 13 ns, `Noise3D` 20 ns, `ValueNoise2D` 14 ns; `NoiseFillGrid` 15 ns a cell with one
octave and 53 ns with four, `NoiseFillGrid3D` 61 ns a cell with three. From a script loop, value
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

Already there before: `DsGridSetRegion` (fill a region with one value), `DsGridClear` (fill the
grid), `DsGridCopy` (a whole grid), `DsGridGetSum` / `GetMax` / `GetMin` and `DsGridValueExists`.

## Script functions on worker threads

A function of the project's Scripts can run as a job on a worker thread, so generating a chunk, a
map or a path does not hold up the frame. The job runs on a VM of its own with a context of its
own, and is given **copies** of the grids and lists it needs, taken when it starts: the game may
change its own meanwhile, and nothing the job does reaches the game until the script takes the
result with `JobTake`. Inside the job each grid or list keeps its handle, so the same function also
runs directly on the game's thread and gives the same result; the same arguments and data always
give the same result.

A job may use maths, text, noise, grids, lists, maps, stacks, queues, named arrays, JSON and its
own variables (`VariableSet`). Anything that reaches the game or shared state (instances, `with`,
drawing, meshes, sound, files, input, the clock, `Random`, `Choose`, `Print`, global variables,
running a Script by name) stops the job with "*X* is not available in a worker job". A name the
job never set is an error too ("'*name*' has no value in this job"), not 0 as in an event: an
instance's variables are not there, so pass them as arguments or give them by name with
`JobScriptVariable`. A grid or list that was not given
to the job reads as one that does not exist (0, as a destroyed one does). Functions written in an
Object's events are not available to jobs; put the function in a Script.

| Command | What it does |
|---|---|
| `JobScriptCreate(function)` | A prepared job for a function of the project's Scripts (as they are now); 0 when there is no such function (`JobLastError` says why). |
| `JobScriptGrid(job, grid, copyBack)`, `JobScriptList(job, list, copyBack)` | Give the job its own copy of a grid or list; with `copyBack` true, `JobTake` copies the job's version back into it (size included). |
| `JobScriptVariable(job, name, value)`, `JobScriptVariableText(job, name, text)` | Give the job a variable of its own by name (a seed, a sea level, a grid's handle), so a function that reads that instance variable runs unchanged in the job. Built-in instance variables (`x`, `speed`...) cannot be given; pass those as arguments. |
| `JobScriptBudget(job, instructions)` | Instructions the job may run in all, every call counted (100 000 000 unless set, 1 000 to 2 000 000 000). The per-call limit of events does not apply. |
| `JobScriptStart(job, arguments...)` | Start it with the function's arguments (numbers, true/false or text). Jobs run in turn on worker threads of their own, as many as all but two of the processors, below the game's own threads in priority. |
| `JobRunScript(function, arguments...)` | Create and start in one, for a job that needs no grids or lists; its result is read with `JobResultNumber` / `JobResultString` / `JobResultBool`. |
| `JobStatus(job)` | `prepared`, `queued`, `running`, `succeeded`, `failed` (`JobError` says why, with the line) or `cancelled`. |
| `JobTake(job)` | Once it has succeeded: copy the grids and lists marked `copyBack` into the Object's own (once). |
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
refused when cancelled before it starts, and stopped by its budget. It also checks noise against recorded values, its range and
smoothness, the grid fills against single calls, and each whole-region grid command. Not yet seen:
a game streaming its world through jobs.

## Video options and the clock

| Command | What it does |
|---|---|
| `WindowSetFullscreen(enabled)`, `WindowIsFullscreen()` | Fill the screen (a borderless window the size of the display) or go back to a window. |
| `WindowSetMode(mode)`, `WindowGetMode()` | `"windowed"`, `"borderless"` or `"fullscreen"` (exclusive); false for any other name. |
| `WindowSetSize(width, height)`, `WindowGetWidth()`, `WindowGetHeight()` | A windowed game's size in pixels. |
| `WindowSetVSync(enabled)`, `WindowGetVSync()` | Wait for the display's refresh (no tearing). |
| `GameSetMaxFps(fps)`, `GameGetMaxFps()` | A frame-rate cap; 0 for none. |
| `DateNow()` | Now, as seconds since 1970 (UTC): what to store in a save for "last played". |
| `DateYear(t)`, `DateMonth(t)`, `DateDay(t)`, `DateHour(t)`, `DateMinute(t)`, `DateSecond(t)`, `DateWeekday(t)` | The parts of a `DateNow` time in local time (weekday 0 is Sunday). |
| `DateText(t, format)` | A `DateNow` time as text, such as `DateText(saved, "d MMM yyyy HH:mm")`. |

`TimeMs()` is still the millisecond clock for timing within a run.

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

## Pictures from a script

`ScreenshotSave(name)` saves a picture of the frame once it is drawn, as `name.png` in the
project's debug images folder (the autoshot's folder), so one run can step a camera through many
views. `ScreenshotPending()` counts pictures not yet taken and `ScreenshotLastPath()` gives the file
the last one went to. A picture asked for during a room change's cover waits for the first frame
without it.

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
