# Game features: weather, models, animation, controllers, sound, HUD, saves

Engine features added on 2 October 2026 for games in general. None of them is specific to one
project, and each is off or unchanged until a room, an Object or a script asks for it, except where
this page says a default changed.

[Large worlds](LargeWorlds.md) covers the other half of the same work: streaming, the model cache
and multiplayer.

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

## Weather the engine draws

A 3D room with a dynamic sky can draw its own rain, snow and lightning from the weather it already
has. Turn on **Draw rain, snow and lightning** in the Room editor under **Lighting & atmosphere**
(`weatherEffects` in the room file's `environment`). It is off by default.

| Part | Behaviour |
|---|---|
| Rain and snow | One particle emitter follows the camera and uses the built-in `builtin://Rain` or `builtin://Snow` effect. How much falls follows the local rain, snow or hail the climate reports, so it thickens and thins with the weather. |
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
it. The built-in rain is also brighter and denser than it was.

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
| `SetBusVolume(bus, volume)`, `GetBusVolume(bus)` | Volume of a group: `"music"`, `"sfx"` or `"master"`. A sound longer than ten seconds is music unless its Audio resource says otherwise. |
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

## Smaller changes

- **Bushes and saplings.** The `Shrub` and `Sapling` foliage shapes are built from rounded solid
  clumps (40 triangles near the camera, 8 to 16 further away) instead of crossed flat cards.
- **Scripts could not start a network session.** See [Multiplayer](LargeWorlds.md#multiplayer).
- **Text over overlay sprites.** Text an Object draws in the overlay pass is drawn after that
  Object's overlay sprites, so a label is on top of its own panel.
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
