# The engine API: Engine.Area.Name

Every engine command and value by its standard name, made by `PgslApi` and checked by
`--test pgsl-logic` (which writes this page beside its captures). A **property** is read like a
variable and, when it can be set, set like one (`Engine.Physics.Mass = 4;`, `Engine.Image.Frame += 1;`);
an instance's property is the current instance's, each target's in turn inside `with`. The flat
names (`PhysicsApplyForce`) still work. See PgslLanguage.md, "The engine API".

1751 names: 476 properties (404 can be set), 1275 commands.

## Engine.Alarm

| Name | Kind | What it is |
|---|---|---|
| `ClearAlarm` | command | Disarm one alarm |
| `ClearAllAlarms` | command | Disarm every alarm |
| `CountActive` | command | How many alarms are currently armed |
| `Fired` | command | Test one alarm bit in a TickAlarms mask |
| `GetAlarm` | command | Frames remaining on alarm 0-11, or -1 when unset |
| `GetUserValue` | command | Read user slot 0-11 |
| `IsSet` | command | True while alarm 0-11 is counting down |
| `SetAlarm` | command | Arm alarm 0-11 to fire after a number of frames |
| `SetUserValue` | command | Write user slot 0-11 |
| `TickAlarms` | command | Count alarms down; returns a bitmask of those that fired |

## Engine.Animation

| Name | Kind | What it is |
|---|---|---|
| `BlendTreeAddClip` | command | Add a threshold clip to a 1D tree |
| `BlendTreeCreate1D` | command | Create an instance-local 1D blend tree |
| `BlendTreeCreateDirect` | command | Create an instance-local direct blend tree |
| `BlendTreeSetSync` | command | Keep a blend tree's clips in step (a sync group): they share one phase, so a walk and a run of different lengths put their feet down together |
| `BlendTreeSetWeight` | command | Set a direct blend clip weight; weights are normalized |
| `BoneClearLookAt` | command | Stop a bone looking at a point; it follows its animation again |
| `BoneClearReach` | command | Stop a bone reaching for a point; its limb follows its animation again |
| `BoneClearRotation` | command | Remove an instance-local bone rotation and return to the sampled animation pose |
| `BoneClearTranslation` | command | Remove an instance-local bone offset and return to the sampled animation pose |
| `BoneLookAt` | command | Turn a bone (a head, a neck) to face a point in the world after animation blending, as far as maxDegrees (default 70) and weight (0 to 1, default 1) allow; call it each frame as the point moves. The model is taken to face +Z in its own space unless AnimationSetLookForward says otherwise |
| `BoneReach` | command | Bend the two bones above a bone so it reaches a point in the world after animation blending: a hand to a door handle, a foot onto a step. Lengths stay as they are, the limb keeps the way it bends, and a point out of reach is reached towards. Weight 0 to 1 (default 1); call it each frame as the point moves |
| `BoneSetRotation` | command | Apply an instance-local bone rotation in degrees after animation blending; useful for gaze or aim |
| `BoneSetTranslation` | command | Move a bone by a local offset (in its parent's space, model units) after animation blending, as AnimationBoneSetRotation turns it |
| `ControllerError` | command | Why the last AnimationControllerUse failed; empty after one that worked |
| `ControllerUse` | command | Give this instance's model the states, blends and transitions of an Animation Controller resource (made in Studio's Animation Controller editor); set its parameters with AnimationStateSetFloat, AnimationStateSetBool and AnimationStateSetTrigger. False when there is no such controller (AnimationControllerError says why) |
| `GetBool` | command | Read a boolean state parameter |
| `GetParameter` | command | Read a numeric blend/state parameter |
| `GetSpeed` | command | Clip playback rate multiplier |
| `GetTag` | command | Name of the current clip |
| `IsPlaying` | command | True while a clip is running |
| `LayerCreate` | command | Create/select a layer; comma-separated exact bone names, empty mask affects all bones |
| `LayerSetAdditive` | command | Choose additive or override layer blending |
| `LayerSetWeight` | command | Set layer influence between zero and one |
| `Loop` | property | Sets (AnimationSetLoop): Whether the clip repeats |
| `ModelAnimationCrossed` | command | True in the one frame the active model clip passes this point (0 = its start, 1 = its end): the moment a blow lands or a foot comes down |
| `ModelAnimationGetClip` | command | Get the active model clip |
| `ModelAnimationGetLength` | command | Length of one of this model's clips in seconds at the rate it is played; 0 if the model has no such clip |
| `ModelAnimationGetProgress` | command | From 0 to 1: how far through the active model clip; a looping clip starts again at 0 |
| `ModelAnimationGetRootMotionDeltaX` | command | Last local root displacement X |
| `ModelAnimationGetRootMotionDeltaY` | command | Last local root displacement Y |
| `ModelAnimationGetRootMotionDeltaZ` | command | Last local root displacement Z |
| `ModelAnimationGetSpeed` | command | Get the model clip playback multiplier |
| `ModelAnimationGetTime` | command | Seconds the active model clip has been playing |
| `ModelAnimationIsPlaying` | command | True while a model clip is playing |
| `ModelAnimationPlay` | command | Play a skeletal clip and blend from the current pose |
| `ModelAnimationSetLoop` | command | Set whether the active model clip repeats |
| `ModelAnimationSetRootMotion` | command | Extract and apply base-layer graph root movement each simulation step |
| `ModelAnimationSetSpeed` | command | Set the model clip playback multiplier |
| `ModelAnimationSetTime` | command | Seek the active model clip |
| `ModelAnimationStop` | command | Pause the active model clip |
| `Play` | command | Start a named animation clip |
| `Playing` | read only | Reads (AnimationIsPlaying): True while a clip is running |
| `SetBool` | command | Set a boolean state parameter |
| `SetLookForward` | command | Which way the model faces in its own space (default 0, 0, 1), for a model built facing another way; AnimationBoneLookAt measures turns from it |
| `SetLoop` | command | Whether the clip repeats |
| `SetParameter` | command | Set a numeric blend/state parameter |
| `SetSpeed` | command | Clip playback rate multiplier |
| `Speed` | property | Reads (AnimationGetSpeed) and sets (AnimationSetSpeed): Clip playback rate multiplier |
| `StateAddTransition` | command | Transition on bool, numeric comparison or AnimationFinished; * means any state |
| `StateConsumeTrigger` | command | Read and clear a one-shot animation-controller trigger |
| `StateCreate` | command | Create a state from a clip name or numeric tree ID; first state is entry |
| `StateCreateClip` | command | Create a clip state, including non-looping attacks |
| `StateCurrent` | command | The state this instance's animation is in (of its base layer); empty when it has none |
| `StateGetBool` | command | Get a boolean animation-controller parameter |
| `StateGetFloat` | command | Get a numeric animation-controller parameter |
| `StateGetText` | command | Get a text animation-controller parameter |
| `StateHasFinished` | command | True when the current non-looping sprite or model state has completed |
| `StatePlay` | command | Play an animation state on the assigned sprite or model |
| `StateSetBool` | command | Set a boolean animation-controller parameter |
| `StateSetFloat` | command | Set a numeric animation-controller parameter |
| `StateSetSpeed` | command | Set playback speed for the assigned sprite or model animation |
| `StateSetText` | command | Set a text animation-controller parameter |
| `StateSetTrigger` | command | Raise a one-shot animation-controller trigger |
| `StateStop` | command | Pause the assigned sprite or model animation |
| `Stop` | command | Halt the current clip |
| `Tag` | read only | Reads (AnimationGetTag): Name of the current clip |

## Engine.Array

| Name | Kind | What it is |
|---|---|---|
| `Add` | command | Add a value to the end |
| `Clear` | command | Remove every entry |
| `Contains` | command | True when the array holds the value |
| `Copy` | command | A new list with the same entries (nested lists are shared) |
| `Create` | command | A new list holding the values given, in order; what an array literal such as [1, 2, 3] makes |
| `Dequeue` | command | Remove and return the first entry (0 when empty) |
| `Find` | command | The first index holding the value, or -1 |
| `Get` | command | Read as a number; 0 when unset |
| `GetString` | command | Read as text; empty when unset |
| `IndexOf` | command | 0-based position of a value, -1 when absent |
| `Insert` | command | Put a value at an index, moving the later entries up |
| `Length` | command | Entry count |
| `MaxValue` | command | Largest entry; 0 when empty |
| `Mean` | command | Average of the entries; 0 when empty |
| `MinValue` | command | Smallest entry; 0 when empty |
| `Pop` | command | Remove and return the last entry; 0 when empty |
| `Push` | command | Append a number |
| `Remove` | command | Remove the entry at an index, moving the later entries down |
| `Reverse` | command | Reverse the order |
| `Set` | command | Write a number, growing the array as needed |
| `SetString` | command | Write a string, growing the array as needed |
| `Shuffle` | command | Put the entries in a random order (RandomSeed repeats it) |
| `Size` | command | How many entries a list holds |
| `Sort` | command | Sort numerically |
| `Sum` | command | Sum of the entries |

## Engine.Audio

| Name | Kind | What it is |
|---|---|---|
| `Acoustics` | property | How the place sounds for sound effects: "outdoors" (no echo), "room", "hall" or "cave". Each room starts outdoors unless its settings choose another |
| `AcousticsAmount` | property | How much of the acoustics is heard, 0 (none) to 1 (0.5 unless set) |
| `BusDuck` | command | Ducking: while any sound on byBus plays, bus drops by amount (0.6 leaves 40%), down over attack seconds (default 0.15) and back over release (default 0.6): BusDuck("music", "voice", 0.6). Amount 0 turns it off |
| `BusDuckLevel` | command | How far a ducked bus is down now: 1 when not ducked, 0.4 when ducked by 0.6 |
| `Enabled` | property | All of the game's sound (true unless set); off keeps every volume as it was for when it comes back |
| `GetBusVolume` | command | The volume of a group of sounds: "music", "sfx" or "master" |
| `GetMasterVolume` | command | Current overall output volume |
| `IsSoundPlaying` | command | True while a channel is still audible |
| `ListenerFollowCamera` | command | Give the listener back to the 3D camera after SetAudioListener |
| `MasterVolume` | property | Reads (GetMasterVolume) and sets (SetMasterVolume): Current overall output volume |
| `Music` | property | The music track playing ("" for none): setting it cross-fades to that track over a second, "" fades it out |
| `MusicCurrent` | command | The track MusicPlay is playing, or empty |
| `MusicMuffle` | property | How muffled the music is, 0 (clear) to 1 (heard through a wall): for a pause menu, underwater or a dream (0 unless set) |
| `MusicPlay` | command | Play a looping music track on the music bus, cross-fading from the one playing over fadeSeconds (default 1); the same track already playing only moves to the new volume |
| `MusicStop` | command | Fade the music MusicPlay started to silence (default 1 second) and stop it |
| `MusicVolume` | property | The volume of the music bus (0-1) |
| `Play` | command | Play a project audio asset. Returns a channel id (0 = failed). |
| `PlaySound` | command | Play a sound; returns a channel handle |
| `PlaySoundAt` | command | Play a sound at a place in the world: quieter with distance and heard to one side. Returns a channel handle |
| `SetBusVolume` | command | Set the volume of a group of sounds: "music", "sfx" or "master" (0..1) |
| `SetListener` | command | Put the listener for positioned sounds somewhere of your choosing. In a 3D room it otherwise follows the camera |
| `SetMasterVolume` | command | Set the overall output volume (0..1) |
| `SfxVolume` | property | The volume of the sound-effects bus (0-1) |
| `SoundFade` | command | Move a playing channel's volume to a new level over a time |
| `SoundPreload` | command | Start decoding a sound on a worker thread now, so that its first PlaySound or PlaySoundAt starts at once. Never waits. sound as PlaySound takes it: an Audio resource's name, its .audio.json or its sound file. A room's loading cover waits for the sounds preloaded while it is up (in Create events). False when there is no such sound |
| `SoundSetBus` | command | Put a playing channel in a group, "music" or "sfx". A sound longer than ten seconds is taken for music unless told otherwise |
| `SoundSetPitch` | command | Change the pitch of a playing channel (1 is as recorded) |
| `SoundSetPosition` | command | Move a playing channel to a place in the world |
| `SoundSetVolume` | command | Change the volume of a playing channel (0..1) |
| `SoundsLoading` | command | How many sounds asked for with SoundPreload (or by the project's preload setting) are still being decoded; 0 once all are ready |
| `Stop` | command | Stop a playing audio channel. |
| `StopAllSounds` | command | Stop every playing sound |
| `StopSound` | command | Stop one playing channel |
| `StopSoundFaded` | command | Fade a playing channel to silence over a time, then stop it |

## Engine.Audio.Acoustics

| Name | Kind | What it is |
|---|---|---|
| `Amount` | property | How much of the echo is heard, 0 to 1 (0.5 unless set) |
| `Enabled` | property | Echo for sound effects: off is outdoors, on brings back the last place chosen ("room" unless one was) |
| `Place` | property | How the place sounds: "outdoors" (no echo, unless the room chooses another), "room", "hall" or "cave" |

## Engine.Audio.Effects

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | The sound-effects bus (true unless set); off keeps its volume for when it comes back |
| `Volume` | property | The sound-effects bus's volume, 0 to 1 (1 unless set) |

## Engine.Audio.Listener

| Name | Kind | What it is |
|---|---|---|
| `FollowCamera` | property | Positioned sounds are heard from the 3D camera (true unless set); false keeps the listener where SetAudioListener put it |

## Engine.Audio.Music

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | The music bus (true unless set); off keeps its volume for when it comes back |
| `Muffle` | property | How muffled the music is, 0 (clear) to 1 (heard through a wall); (0 unless set) |
| `Track` | property | The music playing ("" for none): setting it cross-fades to that track over a second, "" fades it out |
| `Volume` | property | The music bus's volume, 0 to 1 (1 unless set) |

## Engine.Camera.Collision

| Name | Kind | What it is |
|---|---|---|
| `Clearance` | property | How far the orbit camera keeps from a wall it moves in front of, 0 to 5 (0.25 unless set) |
| `Enabled` | property | Whether the orbit camera moves in front of walls between it and this instance (true unless set) |
| `RecoverySpeed` | property | How quickly the orbit camera moves back out once the wall is gone, 0.1 to 100 (10 unless set) |

## Engine.Camera

| Name | Kind | What it is |
|---|---|---|
| `CollisionEnabled` | property | Sets (CameraSetCollisionEnabled): Enable camera occlusion avoidance |
| `Far` | property | How far from the 3D camera things are still drawn (the room's distance unless set) |
| `ForwardX` | read only | Reads (GetCameraForwardX): Get camera forward look vector X |
| `ForwardY` | read only | Reads (GetCameraForwardY): Get camera forward look vector Y |
| `ForwardZ` | read only | Reads (GetCameraForwardZ): Get camera forward look vector Z |
| `Fov` | property | The 3D camera's vertical field of view, 1 to 179 degrees (60 unless the room sets another) |
| `FreeFly` | property | Sets (CameraSetFreeFly): The engine's own fly camera (WASD, Space, Ctrl and mouse look in a room with no physics player): false turns it off, true keeps it on. By default it runs until a script moves the camera |
| `GetForwardX` | command | Get camera forward look vector X |
| `GetForwardY` | command | Get camera forward look vector Y |
| `GetForwardZ` | command | Get camera forward look vector Z |
| `GetPitch` | command | Get play camera Pitch |
| `GetRightX` | command | Get camera right strafe vector X |
| `GetRightY` | command | Get camera right strafe vector Y |
| `GetRightZ` | command | Get camera right strafe vector Z |
| `GetRoll` | command | The 3D camera's own roll, in degrees |
| `GetX` | command | Get play camera X position |
| `GetY` | command | Get play camera Y position |
| `GetYaw` | command | Get play camera Yaw |
| `GetZ` | command | Get play camera Z position |
| `ModelLayerSetFov` | command | A model layer's vertical field of view (0 = the camera's), so zooming the world does not zoom the layer |
| `ModelLayerSetNear` | command | A model layer's near plane in world units (default 0.01) |
| `ModelLayerSetVisible` | command | Hide or show a whole model layer |
| `MoveForward` | command | Move the active view forward |
| `MoveRight` | command | Move the active view right |
| `MoveUp` | command | Move the active view up |
| `Near` | property | How close to the 3D camera things are still drawn (0.1 unless the room sets another) |
| `OrbitDistance` | property | Sets (CameraSetOrbitDistance): Attach/configure this instance's third-person spring arm |
| `OrbitEnabled` | property | Sets (CameraSetOrbitEnabled): Enable/disable this instance's orbit camera |
| `OrbitMouseLook` | property | Sets (CameraSetOrbitMouseLook): Enable built-in mouse orbit; disable when scripting yaw/pitch from input |
| `OrbitPitch` | property | Sets (CameraSetOrbitPitch): Set orbit pitch in degrees, clamped -25 to 75 |
| `OrbitYaw` | property | Sets (CameraSetOrbitYaw): Set orbit yaw in degrees |
| `Pitch` | property | Reads (GetCameraPitch) and sets (SetCameraPitch): Get play camera Pitch |
| `RightX` | read only | Reads (GetCameraRightX): Get camera right strafe vector X |
| `RightY` | read only | Reads (GetCameraRightY): Get camera right strafe vector Y |
| `RightZ` | read only | Reads (GetCameraRightZ): Get camera right strafe vector Z |
| `Roll` | property | Reads (GetCameraRoll) and sets (SetCameraRoll): The 3D camera's own roll, in degrees |
| `SetCollisionEnabled` | command | Enable camera occlusion avoidance |
| `SetFarPlane` | command | Override play camera far clipping plane |
| `SetFov` | command | Override play camera field of view |
| `SetFreeFly` | command | The engine's own fly camera (WASD, Space, Ctrl and mouse look in a room with no physics player): false turns it off, true keeps it on. By default it runs until a script moves the camera |
| `SetNearPlane` | command | Override play camera near clipping plane |
| `SetOrbitDistance` | command | Attach/configure this instance's third-person spring arm |
| `SetOrbitEnabled` | command | Enable/disable this instance's orbit camera |
| `SetOrbitMouseLook` | command | Enable built-in mouse orbit; disable when scripting yaw/pitch from input |
| `SetOrbitPitch` | command | Set orbit pitch in degrees, clamped -25 to 75 |
| `SetOrbitYaw` | command | Set orbit yaw in degrees |
| `SetPitch` | command | Set play camera Pitch |
| `SetPosition` | command | Set the play camera position |
| `SetRoll` | command | Roll the 3D camera about its direction of view: positive leans it to the right. It stays until set again; a shake's tilt adds to it |
| `SetShoulderOffset` | command | Set horizontal shoulder, height and forward offsets |
| `SetTarget` | command | Set play camera look-at target |
| `SetYaw` | command | Set play camera Yaw |
| `Shake3D` | command | Shake the 3D view by up to this many metres, dying away over the seconds given; the camera's own position is not changed |
| `ShakeRoll3D` | command | Shake the 3D view and tilt it by up to this many degrees, dying away over the seconds given |
| `ViewLayerSetFov` | command | The same as ModelLayerSetFov(1, degrees) |
| `ViewLayerSetNear` | command | The same as ModelLayerSetNear(1, distance) |
| `X` | property | The play camera's position across |
| `Y` | property | The play camera's height |
| `Yaw` | property | Reads (GetCameraYaw) and sets (SetCameraYaw): Get play camera Yaw |
| `Z` | property | The play camera's position along Z |

## Engine.Camera.Focus

| Name | Kind | What it is |
|---|---|---|
| `Auto` | property | Focus on whatever is in the middle of the screen instead of the focus distance (false unless set) |
| `Blur` | property | The strongest blur, in pixels of a 1080-line picture, 0 to 32 (8 unless set) |
| `Distance` | property | How far from the camera things are sharpest, 0.1 to 100000 (10 unless set) |
| `Enabled` | property | Depth of field: what is near the focus distance stays sharp, nearer and farther things blur (false unless set) |
| `Range` | property | How far either side of the focus distance stays sharp, 0 to 100000 (4 unless set) |

## Engine.Camera.FreeFly

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Fly the 3D camera with the mouse and WASD, for looking round a level (false unless set) |

## Engine.Camera.Orbit

| Name | Kind | What it is |
|---|---|---|
| `Distance` | property | How far behind this instance the orbit camera sits, 0.05 to 1000 (7.5 unless set) |
| `Enabled` | property | This instance's third-person camera, following it on a spring arm (true once any Orbit value is set) |
| `MouseLook` | property | Whether moving the mouse turns the orbit camera (true unless set); off when a script turns it |
| `Pitch` | property | How far the orbit camera looks down on this instance, -25 to 75 degrees (15 unless set) |
| `Sensitivity` | property | Degrees the orbit camera turns for each pixel the mouse moves, 0.01 to 2 (0.15 unless set) |
| `Yaw` | property | Which way round this instance the orbit camera looks (0 unless set) |

## Engine.Camera.Shake

| Name | Kind | What it is |
|---|---|---|
| `Roll` | command | Shake the 3D view and tilt it by up to this many degrees, dying away over the seconds given |
| `Start` | command | Shake the 3D view, dying away over the seconds given |

## Engine.Camera.Shoulder

| Name | Kind | What it is |
|---|---|---|
| `X` | property | How far to the side of this instance the orbit camera looks over (1.2 unless set; negative for the left shoulder) |
| `Y` | property | How high above this instance the orbit camera looks from (2.4 unless set) |
| `Z` | property | How far forward of this instance the orbit camera's focus is (0 unless set) |

## Engine.Debug

| Name | Kind | What it is |
|---|---|---|
| `DisableGPUFunction` | command | Disable a named GPU-side pass (e.g. "Shadows") |
| `EnableGPUFunction` | command | Re-enable a named GPU-side pass (e.g. "Shadows") |
| `FrameBudgetEnable` | command | Start or stop counting each part of the frame (every subsystem's update and draws, physics, scripts' events) against the frame's budget; GENESIS_FRAME_BUDGET=1 starts it with the game and writes Debug/Logs/frame_budget.txt at the end |
| `FrameBudgetPartMs` | command | A part's average milliseconds a frame so far (0 when not seen) |
| `FrameBudgetReport` | command | The frame's average and longest time, then the parts that took longest: part \| average ms \| longest ms \| share of budget % \| budget ms \| frames over |
| `FrameBudgetReset` | command | Forget the frame budget's counts |
| `FrameBudgetSet` | command | A budget for one part of the frame, named as the report names it (0 for none); frames where it takes longer are counted |
| `FrameBudgetSetFrame` | command | The whole frame's budget (default 16.67, a 60 fps frame) |
| `KeysEnabled` | command | True when the debug screen's keys act: the game was started to be worked on and has not turned them off |
| `KeysSetEnabled` | command | Turn the debug screen's keys (F6, F7, F8, P pauses, N steps) off while the game has the keyboard, as a chat line does, and on again. They act only in Studio's Debug and Run, or with GENESIS_DEBUG_KEYS=1; this cannot turn them on in an exported game |
| `Log` | command | Write a line to the in-game debugger console. |
| `ScreenshotLastPath` | command | The file the last picture was saved to; empty before the first or when it could not be saved |
| `ScreenshotPending` | command | Pictures asked for that are not taken yet |
| `ScreenshotSave` | command | Save a picture of this frame once it is drawn, as name.png in the project's debug images folder; one run can take many |
| `TestCheck` | command | Report a self-test result (pass or fail with a detail); Studio's game.run collects them from the project's Logs/checks.log |
| `Watch` | command | Publish a named value to the in-game debugger's Watch window. |

## Engine.Debug.Keys

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | The engine's debug keys (F6 debug screen, F12 screenshot...) in this run (true in Studio's Run, false in an exported game, unless set) |

## Engine.Decal

| Name | Kind | What it is |
|---|---|---|
| `Add` | command | Put a picture on the world at x, y, z facing along the surface normal nx, ny, nz (a bullet hole where a ray hit, a scorch mark under a blast): size across, projected onto whatever opaque surface is there, walls, models and terrain alike, and not onto surfaces turned away. It lasts life seconds, fading out over the end (0: until removed). image is an Image resource or a texture from TextureCreate. Multiplied over the surface by default, so it keeps the surface's light. 0 when it could not be placed |
| `Clear` | command | Remove every decal (a room change does this too) |
| `Count` | command | How many decals are in the world now |
| `Exists` | command | Whether a decal is still in the world |
| `Follow` | command | Keep a decal on an object that moves (a door, a cart, a crate): it moves and turns with it from now on and goes when the object does. False when there is no such decal or object |
| `Limit` | property | Sets (DecalSetLimit): How many decals may be in the world at once (256 at first, at most 4096): adding past it removes the oldest, as lowering it does |
| `Move` | command | Move a decal and turn it to a new normal. Decals stay where they were put in the world: one on a moving object follows it only when moved like this each frame |
| `Remove` | command | Remove a decal at once; false when it is gone already |
| `SetBlend` | command | How a decal goes over the surface: "multiply" (the default: darkens and tints and keeps the surface's light, for holes, scorch, blood, dirt), "alpha" (paints its own colours, unlit, for signs and paint) or "add" (adds light, for embers and glowing marks) |
| `SetColor` | command | Tint a decal (0-255 each; white leaves the picture as it is) and set its opacity (0-1) |
| `SetFade` | command | How many seconds at the end of its life a decal takes to fade out (by default the last quarter of its life, at most 2 seconds; less than 0 goes back to that) |
| `SetLife` | command | Give a decal a new life from now (0: until removed) |
| `SetLimit` | command | How many decals may be in the world at once (256 at first, at most 4096): adding past it removes the oldest, as lowering it does |
| `SetRotation` | command | Turn a decal's picture about its normal, anticlockwise as seen from the front (a random turn makes bullet holes look less alike) |
| `SetSeeThrough` | command | Put a decal on a see-through surface (a bullet hole in a window pane): drawn as a flat picture in its own plane after glass and other see-through draws, seen from both sides and hidden only by opaque things in front, instead of projected onto the opaque world. Off by default |
| `SetSize` | command | A decal's width and height, and how deep through the surface it reaches (half its size at first: deeper reaches bumpier ground, but also anything standing on it) |
| `Unfollow` | command | Stop a decal following its object; it stays where it is |

## Engine.Display.Cursor

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Whether the system pointer shows over the game (true unless set); a captured mouse stays hidden and the choice applies when it is released |

## Engine.Display

| Name | Kind | What it is |
|---|---|---|
| `CursorVisible` | property | Sets (WindowSetCursorVisible): Show or hide the system pointer over the game window, for a menu that draws its own. A captured mouse (SetMouseCaptured) is already hidden and stays captured; the choice applies when it is released |
| `Fps` | read only | Reads (GameGetFps): Frames shown per second, counted over the last half second (what an FPS counter shows; Fps is 1 / this frame's time, which jitters); 0 before the first count |
| `FpsTarget` | command | -1 for uncapped; positive integer for strict frame pacing |
| `FpsVsync` | command | Explicit hardware VSync toggle |
| `Fullscreen` | property | Reads (WindowIsFullscreen) and sets (WindowSetFullscreen): Whether the game fills the screen (borderless or exclusive) |
| `GetFps` | command | Frames shown per second, counted over the last half second (what an FPS counter shows; Fps is 1 / this frame's time, which jitters); 0 before the first count |
| `GetGuiHeight` | command | Height of the GUI area, the letterboxed room window (the whole window when not letterboxed) |
| `GetGuiScale` | command | GUI area width over the room's authored window width: multiply design-size GUI by this to match the world |
| `GetGuiWidth` | command | Width of the GUI area, the letterboxed room window (the whole window when not letterboxed) |
| `GetGuiX` | command | Left of the GUI area: where the room's window lands when it is letterboxed (scaleViewportsWithWindow), else 0 |
| `GetGuiY` | command | Top of the GUI area: where the room's window lands when it is letterboxed, else 0 |
| `GetHeight` | command | Current render/window height for responsive DrawGui layouts |
| `GetMaxFps` | command | The frame-rate cap (0 = none) |
| `GetMode` | command | "windowed", "borderless" or "fullscreen" |
| `GetVSync` | command | Whether vsync is on |
| `GetWidth` | command | Current render/window width for responsive DrawGui layouts |
| `GuiHeight` | read only | Reads (WindowGetGuiHeight): Height of the GUI area, the letterboxed room window (the whole window when not letterboxed) |
| `GuiScale` | read only | Reads (WindowGetGuiScale): GUI area width over the room's authored window width: multiply design-size GUI by this to match the world |
| `GuiWidth` | read only | Reads (WindowGetGuiWidth): Width of the GUI area, the letterboxed room window (the whole window when not letterboxed) |
| `GuiX` | read only | Reads (WindowGetGuiX): Left of the GUI area: where the room's window lands when it is letterboxed (scaleViewportsWithWindow), else 0 |
| `GuiY` | read only | Reads (WindowGetGuiY): Top of the GUI area: where the room's window lands when it is letterboxed, else 0 |
| `HasFocus` | command | Whether the game's window has the keyboard focus: false once the player has switched to another window; true where there is no window |
| `Height` | property | The window's height (setting it resizes the window) |
| `IsFullscreen` | command | Whether the game fills the screen (borderless or exclusive) |
| `IsMinimized` | command | Whether the game's window is minimised: no frame is drawn and Draw events do not run until it is restored, though Step events do; false where there is no window |
| `MaxFps` | property | Reads (GameGetMaxFps) and sets (GameSetMaxFps): The frame-rate cap (0 = none) |
| `Minimized` | read only | Reads (WindowIsMinimized): Whether the game's window is minimised: no frame is drawn and Draw events do not run until it is restored, though Step events do; false where there is no window |
| `Mode` | property | Reads (WindowGetMode) and sets (WindowSetMode): "windowed", "borderless" or "fullscreen" |
| `SetCursorVisible` | command | Show or hide the system pointer over the game window, for a menu that draws its own. A captured mouse (SetMouseCaptured) is already hidden and stays captured; the choice applies when it is released |
| `SetFullscreen` | command | Fill the screen (a borderless window the size of the display) or go back to a window |
| `SetMaxFps` | command | Cap the frame rate (0 = no cap; vsync still applies). The game's choice outranks the rooms' Frame rate settings for the rest of the run |
| `SetMode` | command | "windowed", "borderless" (fills the screen) or "fullscreen" (exclusive); false for any other name |
| `SetSize` | command | Resize a windowed game's window, in pixels |
| `SetVSync` | command | Wait for the display's refresh before each frame (no tearing). The game's choice outranks the rooms' VSync settings for the rest of the run |
| `Size` | command | Resizes the game window's client area |
| `Title` | command | Sets the game window's title bar text |
| `UseRoomFps` | command | Follow the rooms' Frame rate and VSync settings again, after GameSetMaxFps or WindowSetVSync chose them: the current room's apply at once |
| `VSync` | property | Reads (WindowGetVSync) and sets (WindowSetVSync): Whether vsync is on |
| `Width` | property | The window's width (setting it resizes the window, as WindowSetSize does) |

## Engine.Display.Monitor

| Name | Kind | What it is |
|---|---|---|
| `Count` | read only | How many monitors there are (1 where it cannot tell) |
| `Height` | read only | The game's monitor's height in pixels (0 where there is no window) |
| `Index` | property | Which monitor the game is on, 0 for the first; setting it moves the window there (centred, or filling it in full screen) |
| `RefreshRate` | read only | How many times a second the game's monitor refreshes (0 where it cannot tell) |
| `Width` | read only | The game's monitor's width in pixels (0 where there is no window) |

## Engine.Display.Resolution

| Name | Kind | What it is |
|---|---|---|
| `Current` | read only | The share of the window's size the 3D scene was last drawn at: the Scale, or lower while dynamic resolution holds the frame rate (read) |
| `Scale` | property | The share of the window's size the 3D scene is drawn at, then stretched to the window with a light sharpening, 0.25 to 2 (1 unless set): 0.75 shades about 44% fewer pixels for a faster frame on a small machine, above 1 draws more for a sharper picture; the GUI and text always keep every pixel |
| `Sharpness` | property | How much a scene drawn smaller than the window is sharpened as it is stretched, 0 to 1 (0.5 unless set) |

## Engine.Display.Resolution.Dynamic

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Dynamic resolution (false unless set): the scene is drawn smaller while its frames take too long for TargetFps, and larger again when there is room, between MinimumScale and Engine.Display.Resolution.Scale |
| `MinimumScale` | property | The smallest share of the window's size dynamic resolution draws the scene at, 0.25 to 1 (0.5 unless set) |
| `TargetFps` | property | The frame rate dynamic resolution holds, 10 to 500 (60 unless set) |

## Engine.Display.Window

| Name | Kind | What it is |
|---|---|---|
| `AlwaysOnTop` | property | Whether the window stays above other windows (false unless set) |
| `Centre` | command | Put a windowed game's window in the middle of its monitor |
| `Focus` | command | Bring the game's window to the front and give it the keyboard |
| `Maximized` | property | Whether a windowed game's window fills its monitor's working area, border and title kept (false unless set); false restores it |
| `Minimized` | property | Whether the window is minimised to the taskbar; false restores it |
| `Resizable` | property | Whether the player can resize a windowed game's window by its edges (true unless set) |
| `X` | property | Where a windowed game's window is across the desktop (its left edge); 0 where there is no window |
| `Y` | property | Where a windowed game's window is down the desktop (its top edge); 0 where there is no window |

## Engine.Draw

| Name | Kind | What it is |
|---|---|---|
| `Alpha` | property | Reads (DrawGetAlpha) and sets (DrawSetAlpha): Current draw alpha, 0-1 |
| `Arrow` | command | Line with an arrowhead at the far end |
| `BlendLinear` | property | Reads (DrawGetBlendLinear) and sets (DrawSetBlendLinear): Whether GUI drawing blends in linear light now |
| `Box3D` | command | Axis-aligned box with independent extents |
| `Circle` | command | Circle — filled, or outline when outline is true |
| `CircleEx` | command | Smooth-edged circle: filled when border is 0, otherwise a ring that many pixels wide |
| `Clear` | command | Clear viewport |
| `ClearScreen` | command | Fill the whole surface with a colour |
| `Color` | property | Sets (DrawSetColor): Set draw color |
| `ColorB` | read only | Reads (DrawGetColorB): Blue channel of the current draw colour, 0-255 |
| `ColorG` | read only | Reads (DrawGetColorG): Green channel of the current draw colour, 0-255 |
| `ColorR` | read only | Reads (DrawGetColorR): Red channel of the current draw colour, 0-255 |
| `Cross` | command | Crosshair centred on a point |
| `Cube3D` | command | Axis-aligned cube at a point |
| `CubeColoured3D` | command | Cube in an explicit colour |
| `Draw3DIsActive` | command | True when the surface is in a 3D pass |
| `Ellipse` | command | Ellipse in a bounding box; filled by scanlines |
| `Floor3D` | command | Flat slab — a box with negligible height |
| `Font` | property | Sets (DrawSetFont): Font family or project-relative TTF/OTF payload for later text |
| `FontSize` | property | Sets (DrawSetFontSize): Pixel size for later text |
| `GetAlpha` | command | Current draw alpha, 0-1 |
| `GetBlendLinear` | command | Whether GUI drawing blends in linear light now |
| `GetColorB` | command | Blue channel of the current draw colour, 0-255 |
| `GetColorG` | command | Green channel of the current draw colour, 0-255 |
| `GetColorR` | command | Red channel of the current draw colour, 0-255 |
| `GetOffsetX` | command | The x DrawSetOffset last set |
| `GetOffsetY` | command | The y DrawSetOffset last set |
| `Grid` | command | Lattice of lines from a top-left origin |
| `Grid3D` | command | Lattice of thin boxes on the XZ plane |
| `HealthBar` | command | Two-tone progress bar; amount is 0-100 |
| `Line` | command | Draw line |
| `Line3D` | command | Chain of small cubes — the surface has no 3D line primitive |
| `LineWidth` | command | Line of a given width in pixels |
| `Model3D` | command | Draw a model asset |
| `ModelGui` | command | Draw a Model into a GUI rectangle (an inventory portrait), turned by yaw and pitch in degrees and framed to fit (zoom 1); layered with the other GUI drawing in call order. Draw GUI only; it shows from the next frame |
| `ModelGuiPose` | command | Draw a Model into a GUI rectangle posed at an animation clip's time in seconds |
| `ModelShader3D` | command | Draw a real model resource through an authored mesh shader |
| `ModelTransform3D` | command | Draw a model resource with independent scale and rotation |
| `OffsetX` | property | How far across what is drawn next is moved (as DrawSetOffset) |
| `OffsetY` | property | How far down what is drawn next is moved (as DrawSetOffset) |
| `Pillar3D` | command | Square pillar; the cube queue has no cylinder |
| `Point3D` | command | Small cube marking a point |
| `PointLight3D` | command | Add an omnidirectional light to the frame; from a Step event it lights the next frame drawn |
| `PointLightFalloff3D` | command | Add an omnidirectional light with an explicit falloff exponent; from a Step event it lights the next frame drawn |
| `Rect` | command | Draw a rectangle outline (filled=false) or filled (filled=true) |
| `Rectangle` | command | Filled rectangle |
| `RectangleGradient` | command | Rectangle shading from the first colour to the second (channels 0-255, alpha 0-1): top to bottom when vertical, else left to right |
| `RectanglesFromList` | command | Draw many filled rectangles in one call from a list holding 8 numbers for each: x, y, width, height, then red, green and blue (0-255) and alpha (0-1) as DrawSetColorRgb and DrawSetAlpha take them. Each is placed at x + its x * xscale, y + its y * yscale and sized by the scales (x, y, xscale and yscale may be left out: 0, 0, 1, 1), so a map kept in cells is drawn anywhere at any zoom. Drawn in list order, exactly as DrawRectangle draws each; entries with no width, height or alpha are skipped |
| `ResetClip` | command | Draw everywhere again after DrawSetClip |
| `RoundRect` | command | Smooth-edged rectangle with rounded corners; outline draws a one-pixel border |
| `RoundRectEx` | command | Smooth-edged rounded rectangle: filled when border is 0, otherwise an outline that many pixels wide |
| `Self` | command | Draw instance using its own sprite properties |
| `SetAlpha` | command | Set draw alpha |
| `SetBlendLinear` | command | Blend later GUI drawing (shapes, text and images) in linear light, as a linear-light UI does, until it is set again or the event ends; the project's Blend GUI in linear light setting is where each event starts |
| `SetClip` | command | Limit later GUI drawing (shapes, text and images) to a rectangle until DrawResetClip or the end of the event |
| `SetColor` | command | Set draw color |
| `SetColorRgb` | command | Set the draw colour from 0-255 channels |
| `SetFont` | command | Font family or project-relative TTF/OTF payload for later text |
| `SetFontSize` | command | Pixel size for later text |
| `SetOffset` | command | Move later GUI drawing (shapes, text and images) by x, y pixels until it is set again, so a panel laid out from 0, 0 can be drawn anywhere (a second screen, a window); 0, 0 puts it back |
| `SetTextAlign` | command | Where later text sits on its x: "left" starts there, "center" is centred on it, "right" ends there |
| `SetTextTracking` | command | Letter spacing for later text: pixels added between letters, on top of the font's own spacing and kerning (0 is none) |
| `Sphere3D` | command | Draw a sphere |
| `Sprite` | command | Draw sprite |
| `SpritePart` | command | Part of an image frame into a rectangle: u0, v0 to u1, v1 are fractions of the frame (0 to 1), so a crop or a zoom; tinted by the image blend |
| `SpritePartsFromList` | command | Draw many parts of one Image in one call from a list holding 10 numbers for each, in DrawSpritePart's order: frame, u0, v0, u1, v1 (fractions of the frame), x, y, width, height, alpha. Placed and sized by x, y, xscale and yscale as DrawRectanglesFromList (may be left out: 0, 0, 1, 1) and tinted by the image blend; a tile map or an icon strip in one call and one batch |
| `SpriteScaled` | command | Sprite with explicit transform |
| `Text` | command | Draw text |
| `Text3D` | command | World-space label projected through the active camera |
| `TextAlign` | property | Sets (DrawSetTextAlign): Where later text sits on its x: "left" starts there, "center" is centred on it, "right" ends there |
| `TextColoured` | command | Text in an explicit colour |
| `TextHeight` | command | Line height in pixels of the current font at a size; size 0 uses the current font size |
| `TextScaled` | command | Text at an explicit pixel size |
| `TextTracking` | property | Sets (DrawSetTextTracking): Letter spacing for later text: pixels added between letters, on top of the font's own spacing and kerning (0 is none) |
| `Texture` | command | Draw a texture like an image, its top-left corner at x, y, scaled and turned (degrees) about that corner and tinted by the image blend; in Draw GUI (ordered with the GUI's other drawing) or a 2D Draw event. Changed pixels go to the graphics card once, when it is next drawn |
| `TexturePart` | command | Part of a texture into a rectangle, as DrawSpritePart: u0, v0 to u1, v1 are fractions of the texture (0 to 1), so a window onto a big map, or a zoom; tinted by the image blend |
| `TextWidth` | command | Width in pixels that text takes in the current font and letter spacing; size 0 uses the current font size |
| `Triangle` | command | Triangle; filled by horizontal scanlines |
| `Wall3D` | command | Axis-aligned wall between two points; the dominant axis wins |

## Engine.File

| Name | Kind | What it is |
|---|---|---|
| `Exists` | command | Check a game-relative or explicit absolute path; directories are not files |
| `LastError` | command | Last text-file error for this Object; empty after a successful file operation |
| `ReadText` | command | Read strict UTF-8, up to 4 MiB; check FileLastError to distinguish errors from empty files |
| `ReadTextAsync` | command | Queue a bounded UTF-8 read; poll JobStatus, read JobResultString, then JobRelease; 0 on enqueue failure |
| `WriteText` | command | Atomically replace a UTF-8 file, up to 4 MiB, creating parent folders; relative to stable game saves |
| `WriteTextAsync` | command | Queue atomic UTF-8 replacement; poll JobStatus, read JobResultBool, then JobRelease; 0 on enqueue failure |

## Engine.Game

| Name | Kind | What it is |
|---|---|---|
| `DeltaTime` | read only | Time since last frame |
| `End` | command | End the game and close its window |
| `EventUser` | command | Run this object's User Event 0-7 after the current handler returns |
| `Fps` | read only | Current frames per second |
| `GetSpeed` | command | How fast game time is running; 1 is normal |
| `Name` | read only | The game's name, from its project |
| `Paused` | property | Stop the game's time (Step events and physics see no time passing; Draw events still run) and start it again at the speed it had (false unless set) |
| `Print` | command | Print to output |
| `Quit` | command | End the game and close its window (the same as GameEnd) |
| `Real` | command | To number |
| `ScriptInstructionLimit` | command | Instructions each event body and function call of this game may run before it is stopped as a hang (100 000 unless raised, up to 1 000 000 000; a lower number restores 100 000); returns the limit now in force. Lasts until the game ends; worker jobs keep their own budget |
| `ScriptProfileEnable` | command | Start or stop timing every script function call (calls, time including what it calls, how many ran compiled); GENESIS_SCRIPT_PROFILE=1 starts it with the game and writes Debug/Logs/script_profile.txt at the end |
| `ScriptProfileReport` | command | The script functions that took longest, most first, one line each: name \| calls \| total ms \| average us \| compiled calls |
| `ScriptProfileReset` | command | Forget the script profile's counts |
| `SetSpeed` | command | How fast game time runs: 1 is normal, 0.25 is slow motion, 0 holds physics, scripts' delta time and animation still (0 to 8) |
| `Speed` | property | Reads (GameGetSpeed) and sets (GameSetSpeed): How fast game time is running; 1 is normal |
| `String` | command | To string |
| `Threads` | property | How many of the computer's threads the engine shares its frame work between (culling, terrain, generation): reads the number in use; 0 chooses automatically (every core but two, the default), 1 keeps it all on the game's thread. Lasts until the game ends |
| `TimeMs` | command | Milliseconds of real time since the game started, unaffected by pause or time scale |
| `Version` | read only | The game's version, from its project ("1.0" unless the project sets one): for a title or about screen; it is in crash reports too |

## Engine.Game.Scripts

| Name | Kind | What it is |
|---|---|---|
| `InstructionLimit` | property | Instructions each event body and function call may run before it is stopped as a hang (100000 unless set; up to 1000000000) |
| `Profile` | property | Time every event and function, for Engine.Game.Scripts.Report() (false unless set) |

## Engine.Global

| Name | Kind | What it is |
|---|---|---|
| `Delete` | command | Forget a global |
| `Exists` | command | Whether a global has been set |
| `Get` | command | A global number; 0 when unset |
| `GetString` | command | A global as text; empty when unset |
| `Set` | command | Store a number every room's scripts can read for the rest of the game |
| `SetString` | command | Store text every room's scripts can read for the rest of the game |

## Engine.Grid

| Name | Kind | What it is |
|---|---|---|
| `Add` | command | Add to one cell |
| `AddGrid` | command | Add source * factor to destination, cell by cell, where both grids have the cell (layering noise fields, masks); returns the cells changed |
| `AddRegion` | command | Add a value to every cell of a region |
| `Bytes` | command | How many bytes a grid's cells take (width x height x 8 for numbers, 1 for u8, 2 for u16, 4 for i32 and f32); 0 when there is no such grid |
| `ClampRegion` | command | Clamp every cell of a region between min and max |
| `Clear` | command | Fill every cell |
| `Copy` | command | Make one grid an exact copy of another: its size, its kind (DsGridCreate) and its cells |
| `CopyRegion` | command | Copy a region of one grid into another (or the same) grid with its top-left at (dx, dy); returns the cells copied |
| `Count` | command | How many cells of a region hold the value |
| `Create` | command | Create a 2D grid of numbers; kind "u8", "u16", "i32" or "f32" makes a compact grid of small whole numbers (1, 2 or 4 bytes a cell, values clamped to the kind) or single-precision numbers. Up to 4096 a side and 8 MB of cells; 0 when too big or the kind is unknown |
| `Destroy` | command | Release a grid |
| `Find` | command | The first cell holding the value, as x + y * width, searching rows from y1 towards y2 and each row from x1 towards x2 (so a top-down search gives y1 > y2); -1 when none |
| `FindOther` | command | The first cell NOT holding the value, as x + y * width, searching as DsGridFind does (the highest solid block of a column: from the top, past the air); -1 when none |
| `FloorRegion` | command | Round every cell of a region down to a whole number (Floor) |
| `FromList` | command | Write a list's entries into a region, row by row, until the list runs out (cells outside the grid are skipped); returns the entries read |
| `FromNumList` | command | Write a number list into a region, row by row, until the list runs out (cells outside the grid are skipped); returns the entries read |
| `Get` | command | Read one cell; 0 when out of range |
| `GetColumnToList` | command | Replace a list's entries with column x's cells from row y0 to row y1, in rising row order (rows outside the grid read 0); DsGridToList(grid, x, y0, x, y1, list); returns the entries written |
| `GetMax` | command | Largest value in a region |
| `GetMin` | command | Smallest value in a region |
| `GetSum` | command | Sum over a region |
| `Height` | command | Rows |
| `Kind` | command | What a grid's cells hold: "f64" (numbers), "u8", "u16", "i32" or "f32" (see DsGridCreate); empty when there is no such grid |
| `Multiply` | command | Multiply one cell |
| `MultiplyRegion` | command | Multiply every cell of a region by a value |
| `Resize` | command | Resize, preserving overlapping cells |
| `Set` | command | Write one cell |
| `SetColumnFromList` | command | Write a list's entries into column x from row y0 down the rows (y0, y0 + 1, ...), one row each, until the list runs out (rows outside the grid are skipped); returns the entries read |
| `SetRegion` | command | Fill a rectangular region |
| `ToList` | command | Replace a list's entries with a region's cells, row by row (cells outside the grid read 0); returns the entries written |
| `ToNumList` | command | Replace a number list's entries with a region's cells, row by row (cells outside the grid read 0); returns the entries written |
| `ValueExists` | command | True when a region holds the value |
| `Width` | command | Columns |

## Engine.Image

| Name | Kind | What it is |
|---|---|---|
| `Alpha` | property | How opaque this instance's image is (0 invisible to 1) |
| `Angle` | property | Reads (SpriteGetAngle) and sets (SpriteSetAngle): Sprite rotation in degrees |
| `BlendB` | property | The blue of this instance's image tint (0-255) |
| `BlendG` | property | The green of this instance's image tint (0-255) |
| `BlendR` | property | The red of this instance's image tint (0-255; 255 leaves it as drawn) |
| `Depth` | property | Reads (SpriteGetDepth) and sets (SpriteSetDepth): Draw order |
| `Direction` | property | Sets (SpriteSetDirection): Show this instance's directional sprite facing an angle (0 = right, counter-clockwise); without an animation frame the current one is kept, so sprite playback animates within the direction |
| `DirectionCount` | command | How many facing angles a directional Image holds (one per frame for an Image without direction data); 0 when missing |
| `DirectionFrame` | command | Frame index of a directional Image for a facing angle (0 = right, counter-clockwise) and animation frame; -1 when the image is missing |
| `DirectionFrames` | command | Animation frames stored for each direction of a directional Image; 0 when missing |
| `Frame` | property | Reads (SpriteGetFrame) and sets (SpriteSetFrame): Current animation frame |
| `Get` | command | The instance's current sprite name |
| `GetAngle` | command | Sprite rotation in degrees |
| `GetDepth` | command | Draw order |
| `GetFrame` | command | Current animation frame |
| `GetSpeed` | command | Frames advanced per game frame |
| `GetVisible` | command | Whether the instance is drawn |
| `Height` | read only | Height in pixels of a frame of this instance's image (0 for none) |
| `Name` | property | Reads (SpriteGet) and sets (SpriteSet): The instance's current sprite name |
| `RigAim` | command | Aim in room coordinates, respecting this object's origin, rotation and mirrored scale |
| `RigAimLocal` | command | Aim a bone at image-local pixels, clamped around its authored direction |
| `RigBind` | command | Bind an Image Editor pixel rig to the calling live object; preserves other instances and source artwork |
| `RigBoneRotate` | command | Set an absolute local bone rotation offset from the sampled pose; shared joints remain connected |
| `RigClear` | command | Release this object's rig and resume normal sprite-frame drawing |
| `RigClearControls` | command | Clear aim/joint overrides while retaining the authored animation or selected pose |
| `RigError` | command | Last rig API error for this object; empty after a successful operation |
| `RigFrame` | command | Current authored rig animation frame (1-based) |
| `RigInfill` | command | Enable the editor's source-alpha-aware joint-gap infill; never invents new artwork |
| `RigIsBound` | command | Whether this object has a live pixel-rig player |
| `RigJointMove` | command | Move an unpinned joint in image-local pixels using the editor's connected-joint solver |
| `RigPlay` | command | Play saved rig keyframes at authored FPS; repeat calls do not restart unless requested; loop -1 uses authored setting |
| `RigPose` | command | Select a saved Image Editor pose by name or id, without baking or editing the source image |
| `RigReset` | command | Return to the saved bind pose and clear procedural controls |
| `RigSeek` | command | Seek in the current rig animation (1-based) |
| `RigStop` | command | Pause rig playback at its current pose |
| `ScaleX` | property | This instance's image scale across (negative flips it) |
| `ScaleY` | property | This instance's image scale down (negative flips it) |
| `Set` | command | Bind the instance's sprite by resource name |
| `SetAlpha` | command | Sprite opacity, 0-1 |
| `SetAngle` | command | Rotate the sprite |
| `SetBlend` | command | Tint the sprite from 0-255 channels |
| `SetDepth` | command | Draw order; lower draws in front |
| `SetDirection` | command | Show this instance's directional sprite facing an angle (0 = right, counter-clockwise); without an animation frame the current one is kept, so sprite playback animates within the direction |
| `SetFrame` | command | Jump to an animation frame |
| `SetScale` | command | Scale the sprite |
| `SetSpeed` | command | Frames advanced per game frame |
| `SetVisible` | command | Show or hide the instance |
| `Speed` | property | Reads (SpriteGetSpeed) and sets (SpriteSetSpeed): Frames advanced per game frame |
| `Visible` | property | Reads (SpriteGetVisible) and sets (SpriteSetVisible): Whether the instance is drawn |
| `Width` | read only | Width in pixels of a frame of this instance's image (0 for none) |

## Engine.Input

| Name | Kind | What it is |
|---|---|---|
| `ActionAxis` | command | From -1 to 1 between two actions, for moving: InputActionAxis("Left", "Right"); smooth with a stick, -1, 0 or 1 with keys |
| `ActionBind` | command | Bind an action to a key ("Space"), mouse button ("Mouse:Left"), controller button ("Pad:A") or stick direction ("Pad:LeftStickLeft", "Pad:RightStickUp", "Pad:LeftTrigger"); an action can have several. False when the binding is not understood |
| `ActionBindingCount` | command | How many bindings the action has |
| `ActionBindingName` | command | A binding's name for a settings screen ("W", "Mouse Left", "Pad A"); empty when there is none |
| `ActionCheck` | command | True while anything bound to the action is held |
| `ActionClear` | command | Remove every binding of an action |
| `ActionListen` | command | Rebind: the next key, mouse button or controller button pressed binds the action (replacing its bindings unless replace is false). Escape gives up. Poll InputActionListening each frame |
| `ActionListening` | command | True while InputActionListen still waits for a press; call each frame while a rebinding prompt shows |
| `ActionPressed` | command | True in the frame anything bound to the action goes down |
| `ActionReleased` | command | True in the frame anything bound to the action comes up |
| `ActionsLoad` | command | Read bindings kept by InputActionsSave: actions it names are rebound, others keep theirs. Returns how many it read (0 when there is no file yet) |
| `ActionsSave` | command | Keep every action's bindings in the game's saves (default "input-bindings.txt"); see FileLastError when false |
| `ActionValue` | command | How strongly an action is held, 0 to 1: 1 for a key or button, how far a stick is pushed or a trigger pulled for those |
| `GamepadAxis` | command | A stick or trigger: "LeftX", "LeftY", "RightX", "RightY" from -1 to 1 (right and away from you are positive), "LeftTrigger", "RightTrigger" from 0 to 1 |
| `GamepadAxisAt` | command | A stick or trigger of controller 0 to 3, with the names, directions and dead zones of GamepadAxis |
| `GamepadAxisRaw` | command | A stick or trigger as the controller reports it, with no dead zone; the same names and directions as GamepadAxis |
| `GamepadAxisRawAt` | command | A stick or trigger of controller 0 to 3 as it reports it, with no dead zone |
| `GamepadCheck` | command | True while a controller button is held: "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "L3", "R3", "Start", "Back", "DPadUp", "DPadDown", "DPadLeft", "DPadRight" |
| `GamepadCheckAt` | command | True while a button on controller 0 to 3 is held; the button names are GamepadCheck's |
| `GamepadConnected` | command | True while a controller is plugged in |
| `GamepadConnectedAt` | command | True while controller 0 to 3 is plugged in. Controller 0 is the one GamepadConnected reads; the next ones plugged in are 1, 2 and 3 |
| `GamepadCount` | command | How many controllers are plugged in; up to four are read |
| `GamepadGetDeadZone` | command | The dead zone of "Left", "Right" or "Triggers" |
| `GamepadKeyboardEmulation` | command | Whether the controller also presses keys (A is Space, the triggers are the mouse buttons, the right stick looks). On by default; turn it off when your scripts read the controller |
| `GamepadPressed` | command | True in the frame a controller button goes down |
| `GamepadPressedAt` | command | True in the frame a button on controller 0 to 3 goes down |
| `GamepadReleased` | command | True in the frame a controller button comes up |
| `GamepadReleasedAt` | command | True in the frame a button on controller 0 to 3 comes up |
| `GamepadSetDeadZone` | command | How far "Left", "Right" (the sticks), "Sticks" (both) or "Triggers" must move, 0 to 0.95, before GamepadAxis reads them; the sticks start at 0.18 and the triggers at 0 |
| `GamepadVibrate` | command | Run the controller's motors: the heavy one and the light one, each 0 to 1, for a time |
| `GamepadVibrateAt` | command | Run the motors of controller 0 to 3: the heavy one and the light one, each 0 to 1, for a time |
| `GetMouseLookDeltaX` | command | Get mouse look horizontal delta |
| `GetMouseLookDeltaY` | command | Get mouse look vertical delta |
| `KeyboardTextTake` | command | The characters typed since the last call, in order, as the keyboard layout and shift make them; for name boxes |
| `KeyCheck` | command | Check if key is held |
| `KeyPressed` | command | Check if key was pressed this frame |
| `KeyReleased` | command | Check if key was released this frame |
| `MbLeft` | read only | Mouse left button constant |
| `MbMiddle` | read only | Mouse middle button constant |
| `MbRight` | read only | Mouse right button constant |
| `MouseCaptured` | property | Sets (SetMouseCaptured): Capture the mouse for mouse look (the pointer is hidden and locked), or release it |
| `MouseCheck` | command | Check if mouse button is held |
| `MouseLookDeltaX` | read only | Reads (GetMouseLookDeltaX): Get mouse look horizontal delta |
| `MouseLookDeltaY` | read only | Reads (GetMouseLookDeltaY): Get mouse look vertical delta |
| `MousePressed` | command | Check if mouse button was pressed this frame |
| `MouseRawX` | read only | Raw mouse X |
| `MouseRawY` | read only | Raw mouse Y |
| `MouseReleased` | command | Check if mouse button was released this frame |
| `MouseWheel` | command | How far the mouse wheel turned this frame; positive is away from you |
| `MouseX` | read only | Mouse X |
| `MouseY` | read only | Mouse Y |
| `PointerWorldX` | command | Mouse X mapped through viewport 0, including window fitting |
| `PointerWorldY` | command | Mouse Y mapped through viewport 0, including window fitting |
| `RecordActive` | command | True while input is being recorded |
| `RecordStart` | command | Record the keyboard, mouse, controller, time step and window size of every frame from the next one, as name.ginput in the project's debug Replays folder (or at a full path); false when the file cannot be written |
| `RecordStop` | command | Stop recording and save the file; the number of frames recorded |
| `ReplayActive` | command | True while a recording is being played |
| `ReplayFinished` | command | True from the frame the last recorded frame is played; the real devices are back from the frame after |
| `ReplayFrame` | command | Frames recorded or played so far: 1 in the first frame of a recording or replay, and the same frame number in both |
| `ReplayStart` | command | Play a recording from the next frame in place of the real keyboard, mouse and controller, with its time steps and random numbers; false when there is no such recording. Escape stops it |
| `ReplayStop` | command | Stop a replay; the real devices are read again from the next frame |
| `SetMouseCaptured` | command | Capture the mouse for mouse look (the pointer is hidden and locked), or release it |

## Engine.Input.Gamepad

| Name | Kind | What it is |
|---|---|---|
| `DeadZone` | property | How far both sticks must move before they count, 0 to 0.9 (the engine's unless set) |
| `KeyboardEmulation` | property | A controller also presses the keys a keyboard game listens for (arrows, Space, Enter, Escape) (true unless set) |
| `VibrationStrength` | property | How strongly controllers vibrate when the game runs their motors, 0 (never) to 1 (as asked) (1 unless set) |

## Engine.Input.Mouse

| Name | Kind | What it is |
|---|---|---|
| `Captured` | property | The mouse is locked to the window and hidden, for mouse look (false unless set) |
| `InvertY` | property | Moving the mouse up looks down, for players who prefer it (false unless set) |
| `Sensitivity` | property | How far the view turns for a mouse movement, 0.05 to 10 (1 unless set); the engine's cameras and Engine.Input.Mouse.LookDelta both follow it |

## Engine.Instance

| Name | Kind | What it is |
|---|---|---|
| `Alive` | command | True when an instance still exists |
| `Attach` | command | Keep another instance on this one, x, y and z from it, turning and moving with it |
| `Child` | command | The id of one of the instances kept on this one, from 0 in the order they were made; 0 past the end |
| `ChildCount` | read only | How many instances are kept on this one (its parts, and what was attached to it) |
| `Count` | command | How many instances are alive |
| `Create` | command | Create object instance |
| `Depth` | property | Draw depth |
| `Destroy` | command | Destroy instance |
| `Detach` | command | Let this instance go from the one it is kept on (it stays where it is); false when it was on none |
| `Direction` | property | Direction in degrees |
| `DistanceTo` | command | Distance from an instance to a point; -1 when it does not exist |
| `Exists` | command | Instance exists |
| `Find` | command | The id of the n-th live instance of an Object, counting from 0; 0 when there is none |
| `FirstInRadius` | command | An instance within a radius, excluding the caller; 0 when none |
| `Furthest` | command | Furthest instance from a point, optionally of one Object; 0 when none |
| `GetPositionsToNumList` | command | Read many instances' positions in one call: positions becomes x, y, z for each id in the instances number list (0, 0, 0 for one that does not exist). Returns how many exist |
| `GetRotationPitch` | command | Read persistent pitch in degrees |
| `GetRotationRoll` | command | Read persistent roll in degrees |
| `GetRotationYaw` | command | Read persistent yaw in degrees |
| `GetScaleX` | command | Read persistent X scale |
| `GetScaleY` | command | Read persistent Y scale |
| `GetScaleZ` | command | Read persistent Z scale |
| `GetX` | command | X of an instance; 0 when it does not exist |
| `GetY` | command | Y of an instance; 0 when it does not exist |
| `GetZ` | command | Z of an instance; 0 when it does not exist |
| `HSpeed` | property | Horizontal speed |
| `Id` | read only | The calling object's live instance ID; also readable as id, instance_id or self |
| `ImageAlpha` | property | Alpha 0-1 |
| `ImageAngle` | property | Rotation degrees |
| `ImageIndex` | property | Animation frame |
| `ImageXScale` | property | Horizontal scale |
| `ImageYScale` | property | Vertical scale |
| `InRadius` | command | How many instances lie within a radius |
| `Nearest` | command | Closest instance to a point, optionally of one Object; 0 when none |
| `NearestOther` | command | Closest instance excluding the caller |
| `Number` | command | How many live instances of an Object there are ("all" counts every scripted instance) |
| `Parent` | property | The instance this one is kept on (an Object's part, or attached by Attach); 0 for none. Setting it keeps it on that instance where it is now, 0 lets it go |
| `Pitch` | property | This instance's rotation about X, in degrees (its body turns with it) |
| `Roll` | property | This instance's rotation about Z, in degrees (image_angle in 2D) |
| `ScaleX` | property | This instance's scale along X (its body is resized with it) |
| `ScaleY` | property | This instance's scale along Y |
| `ScaleZ` | property | This instance's scale along Z |
| `SceneQueueApply` | command | Make the game's queued changes now instead of after the Step events; returns how many were made |
| `SceneQueueCount` | command | Changes queued and not yet made (in a job, the job's own) |
| `SceneQueueDestroy` | command | Queue destroying an instance, made later in order (as SceneQueuePosition) |
| `SceneQueuePosition` | command | Queue an instance's move, made later in order: in a worker job when the game takes the job (JobTake), elsewhere after this frame's Step events. Lets jobs update instances side by side |
| `SceneQueuePositionsFromNumList` | command | Queue many moves at once: instances is a number list of ids, positions x, y, z for each in turn. Returns how many were queued |
| `SceneQueueVariable` | command | Queue setting a variable of an instance to a number, text or true/false, made later in order (as SceneQueuePosition) |
| `Self` | command | The calling object's live instance ID |
| `SetPositionsFromNumList` | command | Move many instances in one call: instances is a number list of ids, positions a number list of x, y, z for each in turn (as InstanceSetX/Y/Z: the 3D pose and physics follow). Returns how many moved; ids that do not exist are skipped |
| `SetRotation3D` | command | Set persistent Euler rotation in degrees and synchronize the physics pose |
| `SetScale3D` | command | Set nonzero persistent XYZ scale and resize registered physics colliders |
| `SetTintsFromNumList` | command | Tint many model instances in one call: colours holds r, g, b, a (0-1) for each id in the instances number list, as ModelSetTint. Returns how many changed; ids that are not model instances are skipped |
| `SetTransformsFromNumList` | command | Place, turn and size many instances in one call: transforms holds 9 numbers for each id in the instances number list (x, y, z, pitch, yaw, roll in degrees, sx, sy, sz), as InstanceSetX/Y/Z, InstanceSetRotation3D and InstanceSetScale3D (the 3D pose and physics follow). Returns how many changed; ids that do not exist, and scales of 0, are skipped |
| `SetX` | command | Move an instance on X |
| `SetY` | command | Move an instance on Y |
| `SetZ` | command | Move an instance on Z |
| `Solid` | property | Solid flag |
| `Speed` | property | Movement speed |
| `SpriteIndex` | property | Sprite name |
| `VariableExists` | command | Whether another instance has set a variable of that name |
| `VariableGet` | command | A variable of another instance; 0 when it or the variable does not exist |
| `VariableSet` | command | Set a variable of another instance, as its own scripts see it |
| `Visible` | property | Visible flag |
| `VSpeed` | property | Vertical speed |
| `X` | property | Position X |
| `Y` | property | Position Y |
| `Yaw` | property | This instance's rotation about Y (its heading), in degrees |
| `Z` | property | Position Z |

## Engine.Instance.Update

| Name | Kind | What it is |
|---|---|---|
| `Distance` | property | Instances farther than this from the camera skip their Step events until it comes near, 0 to 100000 metres; 0 updates every instance however far (the room's simulation distance unless set) |

## Engine.Job

| Name | Kind | What it is |
|---|---|---|
| `CacheGet` | command | A number this worker keeps from job to job (0 when not set) |
| `CacheNumList` | command | A number list this worker keeps from job to job under a name (made with size zeros the first time; check its contents to know whether it was filled): give the result to the NumList commands. Each worker has its own |
| `CacheReset` | command | Empty every worker's cache before its next job reads it (a new game, changed settings) |
| `CacheSet` | command | Keep a number on this worker for later jobs |
| `Cancel` | command | Request cancellation of queued/running work; an already committed atomic write remains successful |
| `Error` | command | Operation failure, or an invalid-handle error; empty while pending or after success |
| `LastError` | command | Last job API misuse, such as reading a pending/wrong-type result |
| `Release` | command | Release the handle/result; cancel pending work; capacity is returned when running work stops |
| `ResultBool` | command | Read a successful boolean result (a script job's return value: true when not 0); false on API failure |
| `ResultKind` | command | Declared result type; empty for an invalid handle |
| `ResultNumber` | command | Read a successful numeric result (a script job's return value); 0 on API failure |
| `ResultString` | command | Read a successful string result (a script job's return value as text); check JobLastError for pending/wrong-type reads |
| `RunScript` | command | Run a function of the project's Scripts on a worker thread with these arguments (no grids or lists; see JobScriptCreate); 0 on failure |
| `ScriptAfter` | command | Make a prepared job wait for another job of this Object: started, it reads "waiting" until the other has finished, then runs seeing what the other changed and marked to come back (grids, lists, maps under the same handles). A job waits for any number of others; if one fails, this one fails too |
| `ScriptBudget` | command | Instructions a prepared job may run in all (100 000 000 unless set; 1 000 to 2 000 000 000) |
| `ScriptCreate` | command | Prepare a worker job that runs a function of the project's Scripts; give it data with JobScriptGrid / JobScriptList, then JobScriptStart; 0 on failure (JobLastError says why) |
| `ScriptGrid` | command | Give a prepared job a grid as it is when the job starts (same handle inside the job; shared, not copied); copyBack true puts the job's grid into this one at JobTake if the job changed it |
| `ScriptGridRegion` | command | Give a prepared job a grid it may fill part of (x1, y1 to x2, y2, corners included): the job reads the whole grid as it is when the job starts, and with copyBack JobTake writes only that rectangle into this grid, keeping every other cell as the game has it. The game goes on changing its grid while the job runs, and jobs given parts that do not overlap all come back. The job's copy is made when it starts, in memory kept for the next job |
| `ScriptList` | command | Give a prepared job a list as it is when the job starts (same handle inside the job; shared, not copied); copyBack true puts the job's list into this one at JobTake if the job changed it |
| `ScriptListRegion` | command | Give a prepared job a list it may fill part of (count entries from start): the job reads the whole list as it is when the job starts (shared, copied only if the job writes it), and with copyBack JobTake writes only that range into this list, keeping every other entry as the game has it; jobs given ranges that do not overlap all come back. The range cannot grow or shrink the list |
| `ScriptMap` | command | Give a prepared job a map as it is when the job starts (same handle inside the job; shared, not copied); copyBack true puts the job's map into this one at JobTake if the job changed it |
| `ScriptMesh` | command | Let a prepared job build a mesh (MeshCreate's handle): in the job the mesh starts empty and the MeshAdd commands build it in arrays of its own; JobTake hands the finished mesh over (no copy), and it is uploaded within the frame's mesh budget |
| `ScriptNumList` | command | Give a prepared job a number list as it is when the job starts (same handle inside the job; shared, not copied); copyBack true puts the job's list into this one at JobTake if the job changed it |
| `ScriptNumListRegion` | command | The same for a number list (NumListCreate): the job reads all of it, JobTake writes back only its range |
| `ScriptPriority` | command | Give a prepared job a priority queue (DsPriorityCreate) as it is when the job starts (same handle inside the job; shared, not copied); copyBack true puts the job's queue into this one at JobTake if the job changed it |
| `ScriptShareAll` | command | Let a prepared job read every grid, list, map, stack, queue and priority queue of this Object and its instance variables (numbers, text, true/false), as they are when it starts; shared, not copied, so the cost does not grow with their size |
| `ScriptStart` | command | Start a prepared job on a worker thread with the function's arguments (numbers, true/false or text); poll JobStatus, then JobTake and JobRelease |
| `ScriptUrgent` | command | Have a prepared job taken by the next free worker before the normal jobs waiting (the chunk beside the player before far ones); jobs of one kind still go in the order they start |
| `ScriptVariable` | command | Give a prepared job a variable of its own with this number (a seed, a setting, a grid's handle), so a function that reads that instance variable runs unchanged in the job |
| `ScriptVariableText` | command | Give a prepared job a variable of its own holding text |
| `Status` | command | prepared (a script job not started), queued, running, succeeded, failed, cancelled or invalid; never blocks the game loop |
| `Take` | command | When a script job has succeeded, put the grids, lists and maps it was given with copyBack, and changed, into this Object's own (once); its result stays readable until JobRelease |

## Engine.Json

| Name | Kind | What it is |
|---|---|---|
| `Decode` | command | Decode an explicit map/list root; nested types are preserved; 0 on failure; release the owned tree with JsonFree |
| `Encode` | command | Encode a map/list tree with explicit nested references, up to 4 MiB and depth 64; cycles fail |
| `Free` | command | Release all decoder-owned containers, including removed children; never frees manually attached collections |
| `LastError` | command | Last JSON or typed-collection error for this Object |

## Engine.Language

| Name | Kind | What it is |
|---|---|---|
| `At` | command | One of the game's languages by number (0 first, sorted), for a language menu; empty past the end |
| `Count` | command | How many languages the game has text for |
| `Get` | command | The language the game's text is read in: the player's own when the game has it, else English, else the first there is |
| `Name` | property | Reads (LanguageGet) and sets (LanguageSet): The language the game's text is read in: the player's own when the game has it, else English, else the first there is |
| `Set` | command | Read the game's text in another language ("fr" for Assets/Text/fr.txt); false, keeping the current one, when there is no such file |

## Engine.Lighting.Ambient

| Name | Kind | What it is |
|---|---|---|
| `Ground` | property | The light from below where no sun reaches, as #rrggbb (the sky's unless set) |
| `Mode` | property | "zenith" (the two colours above) or "hemisphere" (the drawn sky's own light) (the room's unless set) |
| `Sky` | property | The light from above where no sun reaches, as #rrggbb (the sky's unless set) |
| `Strength` | property | How strong the hemisphere sky light is (1 unless set) |

## Engine.Lighting.Bounce

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Bounced light: surfaces are lit by the light around them (the sky where nothing is in the way, the lit walls and ground where something is) instead of the room's two flat ambient colours. On in each room unless turned off |
| `Strength` | property | How much the bounced light takes the place of the room's flat ambient colours, 0 to 1 (1 unless set) |

## Engine.Lighting

| Name | Kind | What it is |
|---|---|---|
| `ColorCount` | property | Sets (LightEmitterSetColorCount): Use one, two, or three authored light colours |
| `Enable` | command | Enable or disable this object's persistent Light Emitter |
| `Enabled` | property | The room's 3D lighting (the sun, lamps, shadows and every part of Engine.Lighting): off draws everything in its own colours. Turning a part on (Engine.Lighting.Bounce.Enabled = true) turns this on too |
| `Falloff` | property | Sets (LightEmitterSetFalloff): Set light falloff (1 linear, 2 quadratic, larger values concentrate light near the emitter) |
| `Flash` | command | A short point light at x, y, z: full brightness at once, fading to nothing over life seconds, then gone (a muzzle flash, an explosion, a spark). Colour 0-255; intensity and radius as DrawPointLight3D. It lights surfaces and fog but is never shadowed and never takes a lamp's shadow. Up to 256 at once (a new one then replaces the one nearest its end). 0 when nothing would be lit |
| `FlashClear` | command | Put every flash out (a room change does this too) |
| `FlashCount` | command | How many flashes are lit now |
| `FlashExists` | command | Whether a flash is still lit |
| `FlashMove` | command | Move a flash (one that follows a moving muzzle or a rolling fireball); false when it has burnt out |
| `FlashRemove` | command | Put a flash out at once; false when it has burnt out already |
| `GetReflection` | command | The room's sky and ground reflection strength |
| `Intensity` | property | Sets (LightEmitterSetIntensity): Set this object's light intensity |
| `Light2DAmbient` | command | Darken the current room for 2D lights: amount 0.05..1 is the light left where no light reaches (1 = not darkened); shadow colour channels are 0..1. Rooms start undarkened; other rooms are unaffected. |
| `Light2DBlocker` | command | Register/update an axis-aligned light blocker; nonpositive size removes it |
| `Light2DCone` | command | Shadow-tested torch cone. Direction is 0=right, 90=up; spread is the full angle. Lights darkness set with Light2DAmbient. |
| `Light2DLineClear` | command | Visibility segment against the same authored blockers used by 2D shadows |
| `Light2DPoint` | command | Create/update a bounded, shadow-tested 2D point light; zero radius removes it. A light glows; it lights up darkness only once Light2DAmbient darkens the room. |
| `Light2DRemove` | command | Remove a room-local 2D light |
| `Radius` | property | Sets (LightEmitterSetRadius): Set this object's light radius |
| `Reflection` | property | Reads (EnvironmentGetReflection) and sets (EnvironmentSetReflection): The room's sky and ground reflection strength |
| `SetAction` | command | Set Steady, Flicker, Pulse, Glow, or ColourCycle behaviour |
| `SetColor` | command | Set the primary light colour |
| `SetColorCount` | command | Use one, two, or three authored light colours |
| `SetFalloff` | command | Set light falloff (1 linear, 2 quadratic, larger values concentrate light near the emitter) |
| `SetIntensity` | command | Set this object's light intensity |
| `SetOffset` | command | Set the emitter offset relative to this object |
| `SetRadius` | command | Set this object's light radius |
| `SetReflection` | command | How strongly models reflect the sky and ground: 0 off, 1 physically balanced, up to 4. Gives metals their colour away from lights |
| `SetSecondaryColor` | command | Set the second light colour used by colour cycling |
| `SetTertiaryColor` | command | Set the third light colour used by colour cycling |
| `Shadow2DContact` | command | Soft elliptical ground/contact shadow; zero opacity removes it |

## Engine.Lighting.Glow

| Name | Kind | What it is |
|---|---|---|
| `Strength` | property | How much every surface glows in its own colours, 0 to 4 (0 unless the room sets another) |

## Engine.Lighting.Probe

| Name | Kind | What it is |
|---|---|---|
| `Distance` | property | How far the camera travels from the last capture before the light probe is captured again, 5 to 1000 metres (40 unless set) |
| `Quality` | property | The detail of the light probe reflections and bounced light come from, 1 (32 pixels a side) to 3 (128); 2 unless set |
| `Radius` | property | What lies within this distance of a capture is drawn into it wherever the camera looks, 10 to 2000 metres (80 unless set) |
| `Recapture` | command | Captures what is around the camera again at the next frame, for reflections and bounced light: after a big change to the scene, such as a door opening or a building placed |

## Engine.Lighting.Reflections

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Reflections: smooth and metal surfaces mirror what is around them, rough ones show it blurred. On in each room unless its settings or a script turn it off |
| `Strength` | property | How strongly surfaces reflect what is around them, 0 to 4: 1 is physically balanced (unless the room set its own environment reflection) |

## Engine.Lighting.Sun

| Name | Kind | What it is |
|---|---|---|
| `Color` | property | The sunlight's colour as #rrggbb (warm white unless the room sets another) |
| `Intensity` | property | How bright the sunlight is, 0 to 20 (1.1 unless the room sets another) |
| `Pitch` | property | How high the sun is, -90 to 90 degrees (42 unless the room sets another) |
| `Yaw` | property | Which way the sunlight comes from round the horizon (135 unless the room sets another) |

## Engine.Lighting.Toon

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Stylized (toon) lighting: stepped shading, a rim and its own highlight (off unless the room turns it on) |
| `Rim` | property | The strength of the light round silhouettes, 0 to 4 (0.18 unless set) |
| `Saturation` | property | How colourful toon-lit surfaces are, 0.5 to 2 (1 unless set) |
| `Specular` | property | The strength of the toon highlight, 0 to 4 (0.2 unless set) |
| `Steps` | property | How many bands the shading is cut into, 1 (smooth) to 8 (1 unless set) |
| `Wrap` | property | How far light wraps round the shaded side, 0 to 1 (0.5 unless set) |

## Engine.List

| Name | Kind | What it is |
|---|---|---|
| `Add` | command | Append a number |
| `AddBoolean` | command | Append a JSON boolean |
| `AddCollection` | command | Append an explicit map/list reference |
| `AddNull` | command | Append JSON null |
| `AddNumbers` | command | Append every number in a text ("0.5, 1, -2.25": commas, semicolons or spaces between them) in one call; returns how many were added. Generated data loads many times faster than with a DsListAdd for each number |
| `AddRange` | command | Add a range of another list at the end of this one; returns how many were added |
| `AddString` | command | Append a string |
| `Clear` | command | Remove every entry |
| `Copy` | command | Replace one list's entries with another's |
| `CopyRange` | command | Copy a range of one list into another at destStart (overwriting, and growing the destination when the range runs past its end); returns how many were copied |
| `Create` | command | Create a list; returns its handle |
| `Delete` | command | Remove the entry at a 0-based position |
| `Destroy` | command | Release a list |
| `Empty` | command | True when the list has no entries |
| `Fill` | command | Make a list hold count copies of a number (up to 1 000 000) |
| `Find` | command | 0-based position of a number, -1 when absent |
| `Get` | command | Read as a number; 0 when out of range |
| `GetString` | command | Read as text; empty when out of range |
| `Insert` | command | Insert a number at a 0-based position |
| `IsList` | command | Whether a value is a list |
| `Reserve` | command | Make room for this many entries (up to 1 000 000), so adding them grows nothing: a list a mesh is built from, filled again for every chunk |
| `Set` | command | Overwrite a number at a 0-based position |
| `SetBoolean` | command | Replace an entry with a JSON boolean |
| `SetCollection` | command | Replace a list entry with an explicit map/list reference |
| `SetNull` | command | Replace an entry with JSON null |
| `SetString` | command | Overwrite a string at a 0-based position |
| `Shuffle` | command | Randomise the order |
| `Size` | command | Entry count |
| `Sort` | command | Sort numerically |
| `Sum` | command | Sum of the numeric entries |
| `ValueKind` | command | Entry type; missing for invalid positions |

## Engine.Map

| Name | Kind | What it is |
|---|---|---|
| `Clear` | command | Remove every entry |
| `Create` | command | Create a key/value map |
| `Delete` | command | Remove one key |
| `Destroy` | command | Release a map |
| `Empty` | command | True when the map has no entries |
| `Exists` | command | True when the key is present |
| `Get` | command | Read as a number; 0 when absent |
| `GetString` | command | Read as text; empty when absent |
| `KeyAt` | command | Key at a 0-based insertion-order index |
| `Set` | command | Store a number under a key |
| `SetBoolean` | command | Store a JSON boolean; DsMapGet reads it as 1 or 0 |
| `SetCollection` | command | Store an explicit map/list reference; numeric handles alone remain numbers |
| `SetNull` | command | Store JSON null, preserving the key |
| `SetString` | command | Store a string under a key |
| `Size` | command | Entry count |
| `ValueKind` | command | Entry type: map, list, number, string, boolean, null or missing |

## Engine.Math

| Name | Kind | What it is |
|---|---|---|
| `Abs` | command | Absolute value |
| `AngleBetweenPoints3D` | command | Alias of PointDirection3D |
| `AngleBetweenVectors3D` | command | Angle in degrees between two directions |
| `AngleDifference` | command | Shortest signed difference between two angles, -180..180 |
| `AngleNormalise` | command | Wrap an angle into 0..360 |
| `Approach` | command | Move value toward target without overshooting |
| `ArcCos` | command | The angle in degrees whose cosine is x (-1 to 1) |
| `ArcSin` | command | The angle in degrees whose sine is x (-1 to 1) |
| `ArcTan2` | command | The angle in degrees of the direction (x, y), from -180 to 180 |
| `Ceil` | command | Round up |
| `Choose` | command | Pick random argument |
| `Clamp` | command | Constrain to a range |
| `Cos` | command | Cosine of an angle in degrees |
| `CrossProductX` | command | X of the cross product |
| `CrossProductY` | command | Y of the cross product |
| `CrossProductZ` | command | Z of the cross product |
| `DegToRad` | command | Degrees to radians |
| `DistanceBetweenPoints3D` | command | Straight-line distance in 3D |
| `DotProduct3D` | command | Dot product of two vectors |
| `Floor` | command | Round down |
| `ForwardX` | command | X of the forward unit vector; yaw 0 looks down +Z |
| `ForwardY` | command | Y of the forward unit vector |
| `ForwardZ` | command | Z of the forward unit vector; yaw 0 looks down +Z |
| `LengthDirX` | command | X component of a length at an angle |
| `LengthDirY` | command | Y component of a length at an angle |
| `Lerp` | command | Linear blend from a to b |
| `LerpVectorX` | command | X of a linear blend between two points |
| `LerpVectorY` | command | Y of a linear blend between two points |
| `LerpVectorZ` | command | Z of a linear blend between two points |
| `Max` | command | Larger of two values |
| `Min` | command | Smaller of two values |
| `MoveTowards3DX` | command | X after stepping toward a point |
| `MoveTowards3DY` | command | Y after stepping toward a point |
| `MoveTowards3DZ` | command | Z after stepping toward a point |
| `PointDirection` | command | Angle in degrees from point 1 to point 2 |
| `PointDirection3D` | command | Yaw in degrees toward a point, on the XZ plane |
| `PointDistance` | command | Distance between two points |
| `PointPitch3D` | command | Pitch in degrees toward a point; positive is upward |
| `Power` | command | x raised to n |
| `RadToDeg` | command | Radians to degrees |
| `Random` | command | Random 0..n |
| `RandomRange` | command | Random in range |
| `RandomSeed` | command | Make Random, RandomRange, Choose and DsListShuffle give the same numbers every run from this seed |
| `RightX` | command | X of the right-hand unit vector for a yaw |
| `RightZ` | command | Z of the right-hand unit vector for a yaw |
| `RotateAroundYX` | command | X after rotating a point about the Y axis |
| `RotateAroundYZ` | command | Z after rotating a point about the Y axis |
| `Round` | command | Round to nearest |
| `Sign` | command | -1, 0 or 1 |
| `Sin` | command | Sine of an angle in degrees |
| `Sqrt` | command | Square root (0 for negatives) |
| `Tan` | command | Tangent of an angle in degrees |
| `VectorLength3D` | command | Magnitude of a vector |
| `VectorNormaliseX` | command | X of the unit vector; 0 for a zero vector |
| `VectorNormaliseY` | command | Y of the unit vector; 0 for a zero vector |
| `VectorNormaliseZ` | command | Z of the unit vector; 0 for a zero vector |

## Engine.Mesh

| Name | Kind | What it is |
|---|---|---|
| `AddCube` | command | Add a cube's faces centred on x, y, z: faces is a sum of 1 +X, 2 -X, 4 +Y, 8 -Y, 16 +Z, 32 -Z (63 all; a voxel adds only faces touching air); u0, v0 to u1, v1 is its tile of the texture atlas |
| `AddCubeTiles` | command | MeshAddCube with a tile of its own for the top (+Y), the four sides and the bottom (-Y): a grass block, a log, a crate |
| `AddQuad` | command | Add a four-cornered face in one call: corners in order around it (turning like MeshAddTriangle), one normal, the texture rectangle u0, v0 (first corner) to u1, v1 (third), colour 0-255 and alpha 0-1. Returns the first corner's index, -1 when the mesh is full |
| `AddQuadColors` | command | MeshAddQuad with a colour (0-255) and alpha (0-1) for each corner, for baked light, shading or gradients; flip 1 splits the quad along the other diagonal. Returns the first corner's index, -1 when the mesh is full |
| `AddQuadsFromList` | command | Add many quads in one call from a list holding 36 numbers for each, in MeshAddQuadColors' order (four corners, normal, u0 v0 u1 v1, four colours with alpha, flip); stops when the mesh is full |
| `AddQuadsFromNumList` | command | Add quads from a number list (36 numbers each, MeshAddQuadColors' order), quadCount of them from firstQuad on; read without unboxing, so a chunk's faces go in at array speed. Stops when the mesh is full |
| `AddTriangle` | command | Add a triangle of three vertex indices (counter-clockwise from its front) |
| `AddVertex` | command | Add a vertex (position, normal, texture coordinate, colour 0-255 and alpha 0-1); -1 when the mesh is full (65535) |
| `AddVerticesFromList` | command | Add many vertices and triangles in one call: vertices holds 12 numbers for each (MeshAddVertex's order), triangles three indices for each, counted from the first vertex this call adds; stops when the mesh is full |
| `AddVerticesFromNumList` | command | MeshAddVerticesFromList from number lists: 12 numbers a vertex, three indices a triangle (counted from the first vertex this call adds); stops when the mesh is full |
| `Clear` | command | Empty a mesh to build it again |
| `Create` | command | A new empty mesh for the script to build (a voxel chunk, a procedural shape) |
| `Destroy` | command | Free a mesh |
| `DrawMesh3D` | command | Draw a script's mesh in a Draw event of a 3D room at a position, textured by an Image (empty for none) |
| `DrawMesh3DTransform` | command | Draw a script's mesh scaled and turned about the vertical axis (degrees) |
| `DrawMeshResetState` | command | Put the script-mesh draw options back to their defaults |
| `DrawMeshSetCull` | command | Whether later script-mesh draws hide faces seen from behind (on by default); off draws both sides (leaves, crossed plants) |
| `DrawMeshSetFog` | command | Whether later script-mesh draws are fogged (on by default) |
| `DrawMeshSetGlow` | command | Add the mesh's own colours over its lighting in later script-mesh draws: 0 none (default), 1 fully self-lit (lamps, lava, screens) |
| `DrawMeshSetShadows` | command | Whether this instance's later script-mesh draws cast shadows and are shadowed (both on by default) |
| `DrawMeshSetSky` | command | Draw this instance's later script-mesh draws in the sky layer, until set off or DrawMeshResetState: first, right after the frame is cleared to the sky, so the world covers them; centred on the camera (the draw's x, y, z are an offset from the eye) and at any size or distance, never cut by the far plane; unlit in their own colours, under the sky's haze and clouds. For a game's own sun, moon, stars or sky dome; off by default |
| `DrawMeshSetTransparent` | command | Blend later script-mesh draws as see-through even at full alpha (glass, water); off by default, when only alpha below 1 blends |
| `DrawMeshShader3D` | command | Draw a script's mesh through a mesh Shader resource (empty for the engine's own shading), with this instance's ShaderSetParameter / ShaderSetVector values |
| `InstanceSetMeshCollider` | command | Give an instance a fixed collider of a script's mesh (rebuilt by calling again after the mesh changes) |
| `IsUploaded` | command | Whether a mesh's latest build is on the GPU (false while it waits its turn) |
| `SceneQueueStaticMove` | command | Queue moving a placed mesh (MeshStaticAdd), made later in order (as SceneQueuePosition) |
| `SetUploadBudget` | command | How long a frame may spend sending new or changed meshes to the GPU (default 4; 0 = no limit). The rest go on later frames: a changed mesh draws its previous build meanwhile |
| `StaticAdd` | command | Place a script mesh in the 3D world once: the engine draws it every frame in every view it is inside (no Draw event call), with this instance's DrawMeshSet* options, colour and alpha as they are now; it stays until removed or the room ends. Rebuilding the mesh shows the new build. Returns its handle, 0 when the numbers are bad |
| `StaticClear` | command | Remove every placed mesh |
| `StaticCount` | command | Meshes placed with MeshStaticAdd |
| `StaticDrawnCount` | command | Placed meshes the last view drew (the rest were outside it) |
| `StaticMove` | command | Move, scale or turn a placed mesh; false when it is not placed |
| `StaticRemove` | command | Stop drawing a placed mesh (the mesh itself is kept) |
| `TriangleCount` | command | Triangles in a mesh |
| `UploadBudget` | property | Sets (MeshSetUploadBudget): How long a frame may spend sending new or changed meshes to the GPU (default 4; 0 = no limit). The rest go on later frames: a changed mesh draws its previous build meanwhile |
| `VertexCount` | command | Vertices in a mesh |

## Engine.Mesh.Upload

| Name | Kind | What it is |
|---|---|---|
| `Budget` | property | How long a frame may spend sending script meshes to the graphics card before the rest wait for the next (4 unless set) |

## Engine.Model

| Name | Kind | What it is |
|---|---|---|
| `BeardSetColor` | command | Set facial-hair base colour independently of scalp hair (0..1 RGB). |
| `CastShadows` | property | Whether this instance's model casts a shadow |
| `EmissionScale` | property | The light this model's materials give off: 0 puts its lamps out, 1 is as authored |
| `Get` | command | Get this instance's assigned 3D model |
| `Glow` | property | How brightly this instance's model glows (0 none to 32) |
| `HairLastError` | command | Explain the most recent failed hair command; empty after success. |
| `HairReset` | command | Restore model-authored hair defaults and material colours, preserving other mesh masks and material tints. |
| `HairSetColor` | command | Set scalp and eyebrow base colour (0..1 RGB); use a neutral hair albedo for predictable recolouring. |
| `HairSetStyles` | command | Choose this model's authored scalp and beard styles; None hides that category. Model Editor > Edit > Hair styles authors the choices. |
| `InstanceModelNodeClear` | command | Return one of another instance's model nodes to its authored pose |
| `InstanceModelNodeSetRotation` | command | Turn one of another instance's model nodes by degrees from its authored pose |
| `InstanceModelNodeSetTranslation` | command | Move one of another instance's model nodes by an offset in its parent's space |
| `InstanceSetCastShadows` | command | Whether another instance's model casts a shadow |
| `InstanceSetMaterialEmissionTint` | command | Colour the light one of another instance's model materials gives off (as ModelSetMaterialEmissionTint) |
| `InstanceSetMaterialTexture` | command | Give one of another instance's model materials another Image on that instance only |
| `InstanceSetModelLayer` | command | Draw another instance's model in a model layer (0 = the world, 1-8) |
| `InstanceSetViewLayer` | command | The same as InstanceSetModelLayer(id, 1) or (id, 0) |
| `Layer` | property | The model layer this instance's model is drawn in (0 = the world, 1-8 drawn over it) |
| `MorphGetWeight` | command | Read a named morph-target weight from this model instance |
| `MorphReset` | command | Remove this instance's override for one named morph target |
| `MorphResetAll` | command | Remove every per-instance morph-target override |
| `MorphSetEnabled` | command | Enable or disable all per-instance morph-target overrides |
| `MorphSetWeight` | command | Set a named morph-target weight on this model instance |
| `Name` | property | Reads (ModelGet) and sets (ModelSet): Get this instance's assigned 3D model |
| `NodeClear` | command | Return one of this instance's model nodes to its authored pose |
| `NodeSetRotation` | command | Turn one of this instance's model nodes (a propeller, a gun's bolt) by degrees from its authored pose; the nodes below it follow. False when the model has no node of that name |
| `NodeSetTranslation` | command | Move one of this instance's model nodes (a slide, a magazine) by an offset in its parent's space, model units |
| `ReceiveShadows` | property | Whether shadows fall on this instance's model |
| `ScaleX` | property | The model's own scale along X, on top of the instance's |
| `ScaleY` | property | The model's own scale along Y, on top of the instance's |
| `ScaleZ` | property | The model's own scale along Z, on top of the instance's |
| `Set` | command | Assign a 3D model to this instance |
| `SetCastShadows` | command | Whether this instance's model casts a shadow: a first-person weapon or arms should not |
| `SetEmissionScale` | command | Scale the light this model's materials give off: 0 puts its lamps out, 1 is as authored |
| `SetGlow` | command | Add the model's own colours on top of its lighting: 0 none, 1 fully self-lit. Raise it briefly for a hit flash |
| `SetLayer` | command | Draw this instance's model in a model layer: 0 is the world; 1-8 are drawn over the world in number order, each with its own field of view and near plane, never cut by a wall and casting no shadow (a held tool, a cockpit, a compass) |
| `SetMaterialEmission` | command | Make one of this model's materials give off light of this strength (window glass at night); a negative strength gives it back its authored light |
| `SetMaterialEmissionColor` | command | Make one of this model's materials give off light of this strength in this colour (window glass glowing warm at night); a negative strength gives it back its authored light and colour |
| `SetMaterialEmissionTint` | command | Colour the light one of this model's materials gives off, on this instance only: multiplies its emission image and its own glow (1, 1, 1 is as authored; above 1 brightens), leaving its surface colour alone, so one grey glow pattern serves every colour. A negative r gives back the material's own emission colour |
| `SetMaterialTexture` | command | Give one of this model's materials another Image on this instance only (each weapon its own camo). Slot: albedo, normal, orm, emission, or a texture the material's Shader declares. An empty image gives the slot back its own |
| `SetMaterialTint` | command | Set an instance-only multiplier on an authored model material; the shared resource is unchanged |
| `SetMeshVisible` | command | Show or hide a named mesh on this instance without changing the shared model |
| `SetTint` | command | Multiply the whole model's colour on this instance; an alpha below 1 fades it. 1, 1, 1, 1 is as authored |
| `SetViewLayer` | command | The same as ModelSetLayer(1) or ModelSetLayer(0) |
| `SocketAttach` | command | Attach another live instance to one of this model's named sockets |
| `SocketAttachOffset` | command | Attach another live instance with a local position, rotation and uniform scale offset |
| `SocketDetach` | command | Stop keeping another instance on this model's socket |
| `SocketExists` | command | True when this model has a socket, or a bone, of this name |
| `SocketX` | command | World X of one of this model's sockets (or bones) in the pose it is drawn in; 0 when it has none of that name |
| `SocketY` | command | World Y of one of this model's sockets or bones |
| `SocketZ` | command | World Z of one of this model's sockets or bones |
| `StaticAdd` | command | Place a Model once, with no Object (a rock, a tree, a prop): the engine draws it in every view it is inside, the copies of one model together in one instanced draw, so thousands cost about what is in view. Turned by degrees, sized by scale; it does not animate. Stays until removed or the room ends; 0 when the numbers are bad |
| `StaticAddFromNumList` | command | Place many copies of a Model in one call: transforms holds 7 numbers for each (x, y, z, pitch, yaw, roll, scale), as ModelStaticAdd. Returns how many were placed (bad entries are skipped) |
| `StaticClear` | command | Remove every placed model |
| `StaticCount` | command | Models placed with ModelStaticAdd |
| `StaticDrawnCount` | command | Placed models the last view drew (the rest were outside it) |
| `StaticRemove` | command | Stop drawing a placed model |
| `ViewLayer` | property | Whether this instance's model is drawn in the view layer (layer 1: a first-person weapon) |

## Engine.Model.Glow

| Name | Kind | What it is |
|---|---|---|
| `EmissionScale` | property | The light this model's materials give off: 0 puts its lamps out, 1 is as authored (1 unless set) |
| `Strength` | property | How brightly this instance's model glows in its own colours, 0 to 32 (0 unless set) |

## Engine.Model.Shadows

| Name | Kind | What it is |
|---|---|---|
| `Cast` | property | Whether this instance's model casts a shadow (true unless set) |
| `Receive` | property | Whether shadows fall on this instance's model (true unless set) |

## Engine.Navigation

| Name | Kind | What it is |
|---|---|---|
| `AddLink` | command | Let routes go straight between two points walking cannot join (a jump down, a gap, a ladder, a door): one way unless bothWays (the default), for cost (the link's length when left out; more makes routes walk round instead). In a 2D room x, y are the point and z is not used. Set after NavMeshBake or a room load. Returns the link's id, 0 when an end is off the grid or cannot be stood on |
| `AddObstacle` | command | Block the navigation floors an agent would touch a box on (a cart parked, a door shut, a tree fallen) until NavMeshRemoveObstacle: routes found from then on go round it. In a 2D room x, y are the corners and z is not used. Returns its id; 0 without a mesh |
| `AgentConfigureAvoidance` | command | Enable local avoidance and configure this agent's generic separation limits |
| `AgentConfigureUpdateRates` | command | Set distance-scaled local-avoidance update rates; movement itself remains continuous |
| `AgentGetDistanceRemaining` | command | Remaining route distance; -1 when no path exists |
| `AgentGetLink` | command | The link this agent is crossing (it goes straight to the far end; play a jump or climb), 0 while it walks |
| `AgentGetLinkProgress` | command | How far across its link this agent is, 0 to 1 (for an arc or an animation's time); 0 while it walks |
| `AgentHasArrived` | command | True only after reaching a valid destination |
| `AgentSetAvoidanceEnabled` | command | Enable or disable this agent's local avoidance |
| `AgentSetDestination` | command | Attach a navigation agent and request a destination on the current mesh |
| `AgentSetRadius` | command | How wide this agent is (its radius): its routes keep that far from walls and edges, so a cart or a giant takes the wide way round. 0 (the default) is the size the mesh was baked for, 0.35 |
| `AgentSetSpeed` | command | Set movement speed in world units per second |
| `Bake` | command | Bake a navigation grid from live terrain bounds and static colliders; save beside the room |
| `Baked` | read only | Whether the room has a navigation mesh to find routes on: baked this run, or loaded from beside the room (read) |
| `BakeGrid` | command | Bake explicit navigation bounds for Engine.Navigation.Mesh's agent (0.35 m radius, 1.8 m height, 45 degree slopes and 0.45 m steps unless set) |
| `Enabled` | property | Whether navigation agents move along their routes (true unless set); off, every agent holds where it is with its route kept, for a pause or a cutscene |
| `GetAreaCost` | command | The area cost of the navigation cell at a point (1 unless set); in a 2D room z is y |
| `PathFind` | command | Find a smoothed route (for an agent of radius, keeping that far from edges; the bake's 0.35 when left out); -1 means no route or mesh |
| `PathFollow` | command | Follow a reusable Pathing resource; loopMode is Loop, PingPong, Once, or an empty string to use the asset |
| `PathFree` | command | Release a path handle |
| `PathGetWaypointCount` | command | Read the waypoint count |
| `PathGetWaypointLink` | command | The link a route crosses from this waypoint to the next (its id from NavMeshAddLink), 0 where it is walked |
| `PathGetWaypointX` | command | Waypoint X |
| `PathGetWaypointY` | command | Waypoint Y |
| `PathGetWaypointZ` | command | Waypoint Z |
| `PathSeek` | command | Use NavMesh A* to seek a world-space destination |
| `PathStop` | command | Stop the current route or navigation request immediately |
| `PathWander` | command | Wander around the current position using the active NavMesh and local avoidance |
| `RemoveLink` | command | Take a link away (routes already found keep it); false when there is no such link |
| `RemoveObstacle` | command | Take away an obstacle NavMeshAddObstacle placed; false when there is no such obstacle |
| `SetAreaCost` | command | Make the navigation cells in a rectangle cheaper or dearer to cross (a road 0.5, mud 3; 1 is normal, 0.05 to 1000), so routes prefer or avoid them; in a 2D room z is y. Set after NavMeshBake or a room load. Returns how many cells changed |

## Engine.Navigation.Agent

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Whether this instance's navigation agent moves along its route (true unless set); off, it holds where it is with its route kept: stunned, talking, waiting at a door |
| `Speed` | property | How fast this instance's navigation agent moves, world units a second, 0 to 10000 (3.8 unless set) |
| `StoppingDistance` | property | How close to its destination this instance's agent counts as arrived, 0 to 100 (0.1 unless set) |

## Engine.Navigation.Avoidance

| Name | Kind | What it is |
|---|---|---|
| `Priority` | property | Who gives way, 0 to 100 (50 unless set): this instance's agent steers aside more for agents above it and less for those below, so a guard of 90 walks through a crowd of 50; setting it turns avoidance on |
| `Radius` | property | How much room this instance's agent keeps from other agents, 0.01 to 100 metres (0.35 unless set); setting it turns avoidance on |

## Engine.Navigation.Mesh

| Name | Kind | What it is |
|---|---|---|
| `AgentHeight` | property | How tall the agents the next bake is made for are, 0.1 to 50 metres (1.8 unless set): floors need this much headroom |
| `AgentRadius` | property | How wide the agents the next bake is made for are, 0 to 10 metres (0.35 unless set): walls and edges are kept this far away |
| `CellSize` | property | How fine a grid Engine.Navigation.Bake() lays over the room, 0 to 100 metres a cell (0 unless set: worked out from the room's size, 512 cells across); finer finds narrower ways at more memory |
| `MaxSlope` | property | The steepest ground the next bake lets agents walk, 0 to 89 degrees (45 unless set) |
| `StepHeight` | property | The highest step the next bake lets agents climb, 0 to 10 metres (0.45 unless set) |

## Engine.Network

| Name | Kind | What it is |
|---|---|---|
| `Available` | command | True when networking is wired up |
| `Broadcast` | command | Broadcast a tagged payload to all connected peers (host). |
| `Connect` | command | Join a host |
| `Connected` | read only | Reads (NetIsConnected): True when connected or hosting |
| `ConnectState` | command | "idle", "hosting", "connecting", "connected", "failed" (joining did not succeed) or "disconnected" (a joined game ended); NetLastError says why |
| `CopyCount` | command | How many copies of other machines' instances exist here |
| `CopyId` | command | The instance id of a copy, counting from 0 up to NetCopyCount(); 0 when there is none |
| `CopyObject` | command | The Object a shared instance or copy was made from |
| `CopyOwner` | command | The player a shared instance or copy belongs to, as the host numbers its players; 0 when it is the host's own |
| `Disconnect` | command | Leave and stop hosting |
| `Discover` | command | Look for games of this title hosted on this port on the local network and this computer, asking again every second until NetDiscover(0), NetHost or NetConnect. List them with NetFoundCount |
| `Forget` | command | Stop sharing an instance; its copies on other machines are removed |
| `FoundAddress` | command | The address of a game NetDiscover found, counting from 0, to pass to NetConnect; empty when there is none |
| `FoundCount` | command | How many games answered NetDiscover in the last few seconds; a game that stops answering drops off after about four |
| `FoundName` | command | The name a game NetDiscover found advertises: its title, or what its host set with NetHostName |
| `FoundPlayers` | command | How many players are in a game NetDiscover found, its host included; 0 when there is none |
| `FoundPort` | command | The port a game NetDiscover found is hosted on; 0 when there is none |
| `GetNumber` | command | Read a named number from a shared instance or a copy; the fallback when it has none |
| `GetText` | command | Read a named piece of text from a shared instance or a copy; the fallback when it has none |
| `Host` | command | Start hosting on a port |
| `HostName` | command | The name this machine advertises to players looking for games while it hosts; an empty name gives back the game's title |
| `HostOnline` | command | Host a game over the internet through a relay; NetSessionCode gives the code to share a moment later (NetConnectState says hosting then). False when this network cannot, or the relay's address is wrong |
| `InterestDistance` | command | How near a shared instance must be to a player for that player to be sent it |
| `IsConnected` | command | True when connected or hosting |
| `IsCopy` | command | True when this instance is a copy of something another machine controls |
| `IsHost` | command | True when this process is the host |
| `JoinOnline` | command | Join a game hosted through a relay by the code its host shares; connected once NetConnectState says connected (NetLastError says why not: no such game, full, wrong version) |
| `LastError` | command | Why hosting or joining failed or a joined game ended: "refused", "timed out", "wrong version", "full", "host closed", "unreachable", "bad address", "port in use"; empty while nothing has gone wrong |
| `LocalAddresses` | command | This computer's addresses on the local network (IPv4), separated by commas, the one most likely to work first: what other players type to join a game hosted here |
| `MessageFrom` | command | The id of the machine that sent the message NetReceive last took; pass it to NetSendText to reply |
| `MessageNumber` | command | The message NetReceive last took, as a number: what NetSendNumber sent, or text that reads as a number |
| `MessageTag` | command | The tag of the message NetReceive last took |
| `MessageText` | command | The message NetReceive last took, as text |
| `Own` | command | This machine controls this instance (a player's character); every other player sees a copy that follows it |
| `PeerCount` | command | How many peers are connected |
| `PeerId` | command | The id of a connected machine, counting from 0 up to NetPeerCount(); 0 when there is none |
| `Pending` | command | How many messages have arrived that no script has taken |
| `Port` | command | The port in use, 0 when inactive |
| `Receive` | command | Take the oldest message that arrived with this tag (0 takes any). True when there was one; read it with NetMessageText, NetMessageNumber, NetMessageFrom and NetMessageTag |
| `Replicate` | command | Host: show this instance to every player near it. Returns its network id, 0 if it cannot be shared |
| `Send` | command | Send a tagged payload to one peer (peerId 0 = broadcast when host). |
| `SendNumber` | command | Send a single number |
| `SendText` | command | Send a UTF-8 message; peer 0 broadcasts |
| `SessionCode` | command | The code players join this online game with (NetHostOnline); empty until the relay has given it |
| `SetNumber` | command | Give a shared instance a named number every machine can read from its copy. Only the machine that controls the instance can set it |
| `SetText` | command | Give a shared instance a named piece of text every machine can read from its copy |

## Engine.Network.Replication

| Name | Kind | What it is |
|---|---|---|
| `Rate` | property | How many times a second the host sends shared Objects to the players, 1 to 120 (20 unless set): more is smoother and costs more of the connection |

## Engine.Noise

| Name | Kind | What it is |
|---|---|---|
| `FillGrid` | command | Fill a whole DsGrid in one call: cell (i, j) = offset + scale * FractalNoise2D(x0 + i * step, y0 + j * step, ...); returns the cells filled |
| `FillGrid3D` | command | Fill a DsGrid with a flat slice of FractalNoise3D: plane "xy", "xz" or "yz" names the axes i and j step along from (x0, y0, z0); returns the cells filled |
| `FractalNoise2D` | command | Octaves of Noise2D summed (fBm), -1 to 1: each octave lacunarity times finer (2) and gain times weaker (0.5); 1 to 16 octaves |
| `FractalNoise3D` | command | Octaves of Noise3D summed (fBm), -1 to 1; 1 to 16 octaves |
| `Hash2` | command | A repeatable random number 0 to 1 (1 never) for a whole-number point (fractions are dropped down): the same on every machine; equals ValueNoise2D(Floor(x), Floor(y), salt) |
| `Hash3` | command | A repeatable random number 0 to 1 (1 never) for a whole-number 3D point: the same on every machine; equals ValueNoise3D(Floor(x), Floor(y), Floor(z), salt) |
| `Noise2D` | command | Smooth gradient (Perlin) noise, -1 to 1; the same on every machine. Features are about 1 unit across: scale positions (x / 32) |
| `Noise3D` | command | Smooth 3D gradient (Perlin) noise, -1 to 1; the same on every machine |
| `ValueNoise2D` | command | Smooth value noise, 0 to 1; at whole-number points a repeatable random value per point |
| `ValueNoise3D` | command | Smooth 3D value noise, 0 to 1; at whole-number points a repeatable random value per point |

## Engine.NumList

| Name | Kind | What it is |
|---|---|---|
| `Add` | command | Add a number at the end; returns its index, -1 when the list is full |
| `Add3` | command | Add three numbers at the end (a position, a normal, a colour); returns the first one's index, -1 when full |
| `AddNumbers` | command | Add every number in a text ("0.5, 1, -2.25": commas, semicolons or spaces between them) at the end in one call; returns how many were added. Generated data loads many times faster than with a NumListAdd for each number |
| `AddRange` | command | Add a range of another number list at the end of this one in one move; returns how many were added |
| `Clear` | command | Remove every entry (the list keeps its room) |
| `Copy` | command | Copy a range of one number list into another (or within one) at destStart, growing dest when needed; returns how many were copied |
| `Create` | command | Create a list of numbers kept unboxed (fast to fill, copy and give to jobs); returns its handle |
| `Destroy` | command | Release a number list |
| `Fill` | command | Set a range to one number, growing the list when the range runs past its end |
| `FromList` | command | Fill a number list from an ordinary list (entries that are not numbers become 0), replacing what it held |
| `Get` | command | The number at an index (0 outside the list) |
| `IndexOf` | command | The first index holding this number, -1 when none does |
| `Max` | command | The largest number in a range (0 for an empty range) |
| `Min` | command | The smallest number in a range (0 for an empty range) |
| `Reserve` | command | Make room for this many entries (up to 1 000 000) so adding them grows nothing |
| `Resize` | command | Make the list this long: entries past the end go, new ones are 0 |
| `Scale` | command | Change every number in a range to number * multiply + add in one move (move a chunk's vertices, convert units) |
| `Set` | command | Set the number at an index; an index past the end grows the list (new entries 0) |
| `Size` | command | Entry count |
| `Sort` | command | Sort a range of numbers |
| `Sum` | command | The sum of a range |
| `ToList` | command | Replace an ordinary list's entries with a number list's numbers |

## Engine.Particle

| Name | Kind | What it is |
|---|---|---|
| `AttachedGetEmitting` | command | Whether this object's attached particles are emitting |
| `AttachedGetRate` | command | This object's attached particle rate |
| `AttachedSetEmitting` | command | Start or stop this object's attached particle component |
| `AttachedSetRate` | command | Set this object's attached particle rate |
| `Burst` | command | Emit a one-off burst |
| `BurstAt` | command | Play a Particle resource once at a place; it removes itself when it has played out. Seconds is how long a continuous effect emits |
| `BurstBatched` | command | Play a Particle resource once at a place like ParticleBurstAt, but every batched burst of that resource shares one emitter: hundreds of them (falling leaves, sparks) cost one simulation and one draw a frame. No id is returned. An effect that loops, follows the camera, simulates in its own space, links emitters or lights the scene, and the Software renderer, play it as ParticleBurstAt |
| `BurstCount` | command | ParticleBurstAt bursts alive now |
| `BurstLimit` | property | Reads (ParticleBurstLimit) and sets (ParticleSetBurstLimit): The most ParticleBurstAt bursts alive at once (0 = no limit) |
| `Capacity` | command | Maximum particles an emitter can hold |
| `Count` | command | Live particles in an emitter |
| `Create` | command | Create a particle emitter |
| `Destroy` | command | Release an emitter |
| `DestroyAll` | command | Release every emitter |
| `Effects2DPause` | command | Freeze/unfreeze room particle time |
| `EmitterCount` | command | How many emitters exist |
| `Enabled` | property | Whether particle effects run and are drawn (true unless set); off, every effect stops where it is and carries on when back on: a photo mode, a pause screen |
| `Exists` | command | True while an emitter is alive |
| `GetRate` | command | Particles emitted per second |
| `Particle2DBurst` | command | Burst canonical Particle Editor emitters into a bounded room-owned 2D pool (never darkens the room) |
| `Particle2DCount` | command | Active particles in the current room's bounded pool |
| `Particle2DFlow` | command | Curved, converging particles from a source to a target without allocating entities |
| `Quality` | property | How many particles every effect makes, 0.1 to 1 of what it was authored with (1 unless set): fewer on a slow machine, each effect still recognisable; a one-off burst keeps at least one |
| `Reset` | command | Kill every live particle |
| `SetBurstLimit` | command | The most ParticleBurstAt bursts alive at once (default 256; 0 = no limit): one more removes the oldest. Emitters placed on Objects are never removed |
| `SetCameraPosition` | command | Tell an emitter set where the camera is, for depth sorting |
| `SetRate` | command | Particles emitted per second |
| `SpawnParticleEmitter` | command | Spawn a Particle resource through the runtime Object component system |
| `Update` | command | Advance an emitter's simulation |

## Engine.Particle.Bursts

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Whether new one-off bursts start, from ParticleBurstAt, batched bursts and the engine's own (true unless set); those playing finish |
| `Limit` | property | How many particle bursts may be alive at once, the oldest ending first (256 unless set) |

## Engine.Particle.Emitter

| Name | Kind | What it is |
|---|---|---|
| `Color` | property | A colour this instance's particles' colours are multiplied by, as "#rrggbb" (#ffffff unless set): a magic effect in the caster's colour |
| `Enabled` | property | Whether this instance's attached particles are emitting (as its Object has them unless set); those already out live their lives |
| `Gravity` | property | How strongly gravity pulls this instance's particles, -10 to 10 times the effect's own (1 unless set): 0 for sparks in space, below 0 to turn it around |
| `Life` | property | How long this instance's particles live, 0.05 to 10 times the effect's own (1 unless set): smoke that lingers in still air |
| `Rate` | property | Particles this instance's attached effect makes a second, 0 to 10000 (the effect's own unless set) |
| `Size` | property | How big this instance's particles are, 0 to 10 times the effect's own (1 unless set): a fire that grows as it catches |
| `Speed` | property | How fast this instance's particles leave, 0 to 10 times the effect's own (1 unless set): an engine's exhaust at full throttle |

## Engine.Particle.Gpu.Collision

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Whether effects' particles bounce off, stick to or die on the ground and geometry as each effect was made to (true unless set); off, they pass through, for a cheaper frame |

## Engine.Particle.Particles2D

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Whether a 2D room's particles (Particle2DBurst, Particle2DFlow) start and are drawn (true unless set); its lights and darkness stay |

## Engine.Physics

| Name | Kind | What it is |
|---|---|---|
| `AddCollisionFocus` | command | Keep the colliders a large world makes only near moving things (streamed scenery, terrain tiles, solid scatter) within radius metres of this instance, however far from the camera; false when it cannot |
| `AirDrag` | property | Reads (PhysicsGetAirDrag) and sets (PhysicsSetAirDrag): Global air resistance |
| `AllowSleep` | property | Reads (PhysicsGetAllowSleep) and sets (PhysicsSetAllowSleep): Whether idle bodies may sleep |
| `AngularDamping` | property | How quickly the body's spin dies away by itself (0 none) |
| `ApplyForce` | command | Apply a force for the current frame (given x, y and z only: to the current instance) |
| `ApplyImpulse` | command | Push one body (given x, y and z only: the current instance's) |
| `Available` | command | True when a physics world exists |
| `Awake` | property | Whether the body is being simulated: false once it has come to rest (setting false puts it to sleep until something touches it) |
| `BodyType` | property | How the body takes part: "dynamic" (moved by forces), "kinematic" (moved only by the game) or "static" (never moves); "" for no body |
| `Bounciness` | property | How much of a hit it bounces back (0 none to 1) |
| `CapsuleCast` | command | Distance an upright capsule (centred on x, y, z, height end to end) moves before it touches something, -1 for none |
| `CharacterCreate` | command | Give this instance a kinematic character capsule at its own position (feet): it slides, climbs steps and keeps to the ground |
| `CharacterDestroy` | command | Remove this instance's character |
| `CharacterEnteredWater` | command | True for the step this character enters swimmable water |
| `CharacterExitedWater` | command | True for the step this character exits swimmable water |
| `CharacterFits` | command | Whether the character would fit this tall where it stands |
| `CharacterGrounded` | command | Whether this instance's character stands on walkable ground |
| `CharacterGroundNormalY` | command | How upright the ground under the character is (1 flat); 1 in the air |
| `CharacterIsGrounded` | command | True when this character motor stands on a walkable physics surface |
| `CharacterIsSwimming` | command | True when this character motor is swimming |
| `CharacterMove` | command | Move this instance's character as far as the world allows; flags: 1 grounded, 2 ceiling, 4 wall, 8 climbed a step |
| `CharacterSetHeight` | command | Crouch or stand: change the character's height; false and unchanged when standing up would not fit |
| `CharacterSetPosition` | command | Put this instance's character's feet here (a teleport) |
| `CharacterSubmerged` | command | Submerged fraction of this character from 0 to 1 |
| `CharacterX` | command | This instance's character's feet, X |
| `CharacterY` | command | This instance's character's feet, Y |
| `CharacterZ` | command | This instance's character's feet, Z |
| `CirclesOverlap` | command | Circle-vs-circle overlap test |
| `CollisionCircle` | command | First matching instance whose position falls in a circle, excluding the caller |
| `CollisionCircle3D` | command | First matching instance whose position falls in an XZ circle, excluding the caller |
| `CollisionDistance3D` | command | Distance on XZ plane to nearest matching instance, or -1 if none found |
| `CollisionPoint` | command | First instance at a point within a tolerance |
| `CollisionRectangle` | command | Check rectangle area for object collision |
| `CreateJoint` | command | Create a ball joint between two dynamic bodies |
| `DestroyJoint` | command | Remove a script-created physics joint |
| `Enabled` | property | Whether the body takes part in the simulation (false leaves it where it is, touching nothing) |
| `Friction` | property | Grip against what it touches (0.01 ice to 4) |
| `GetAirDrag` | command | Global air resistance |
| `GetAllowSleep` | command | Whether idle bodies may sleep |
| `GetGravityStrength` | command | Scene gravity scale |
| `GetGravityX` | command | X of the gravity acceleration |
| `GetGravityY` | command | Y of the gravity acceleration |
| `GetGravityZ` | command | Z of the gravity acceleration |
| `GetMaxVelocity` | command | Velocity ceiling |
| `GetVelocityX` | command | Read linear velocity X |
| `GetVelocityY` | command | Read linear velocity Y |
| `GetVelocityZ` | command | Read linear velocity Z |
| `GravityScale` | property | How strongly gravity pulls this body (0 floats, 1 as the world, 2 twice as hard, negative rises) |
| `GravityStrength` | property | Reads (PhysicsGetGravityStrength) and sets (PhysicsSetGravityStrength): Scene gravity scale |
| `GravityX` | property | Gravity's pull along X (metres, or pixels in a 2D room, a second each second) |
| `GravityY` | property | Gravity's pull along Y (down is negative in 3D, positive in a 2D room) |
| `GravityZ` | property | Gravity's pull along Z |
| `Grounded` | read only | Whether the body stands on something |
| `HasBody` | read only | Whether this instance has a physics body |
| `IsGrounded` | command | Check for ground below a physics body |
| `JointHinge` | command | Let a joint turn only about one world axis, between minDegrees and maxDegrees from how the bodies are now: a knee, an elbow, a door on its hinges |
| `JointLimitSwing` | command | Keep a joint's second body within maxDegrees of the first along a world direction, taken as the bodies are now (a shoulder or hip: the direction along the limb). With PhysicsJointLimitTwist and PhysicsJointHinge, a ragdoll is limbs joined like a body's |
| `JointLimitTwist` | command | Let a joint's second body turn about a world direction, relative to the first, only between minDegrees and maxDegrees from how they are now (a forearm's twist) |
| `Layer` | property | The body's collision layer (0-31) |
| `LinearDamping` | property | How quickly the body's movement dies away by itself (0 none) |
| `LockRotation` | property | Whether the body is kept upright (it moves but never turns) |
| `Mask` | property | Which layers the body collides with: bit n for layer n (1 + 4 = layers 0 and 2) |
| `Mass` | property | The body's mass: a heavier body is pushed less by the same force and knocks lighter ones aside |
| `MaxVelocity` | property | Reads (PhysicsGetMaxVelocity) and sets (PhysicsSetMaxVelocity): Velocity ceiling |
| `OverlapCapsule` | command | Whether an upright capsule here overlaps or touches anything but the calling instance: does a crouched character fit standing up? |
| `PlaceFree` | command | Check if coordinate x, y is free from solid collisions |
| `PlaceFree3D` | command | True when no other instance occupies an XZ radius |
| `PlaceFreeRadius` | command | True when no other instance occupies a radius |
| `PlaceMeeting` | command | Check collision at coordinate x, y |
| `PointInCircle` | command | Point-in-circle test |
| `PointInRect` | command | Point-in-rectangle test |
| `PositionEmpty` | command | Check if coordinate x, y has no objects |
| `Raycast` | command | Distance to the first hit, -1 for none |
| `RaycastHitInstanceId` | command | Last hit entity ID; -1 for static geometry or a miss |
| `RaycastHitNormalX` | command | Last hit normal X; zero after a miss |
| `RaycastHitNormalY` | command | Last hit normal Y; zero after a miss |
| `RaycastHitNormalZ` | command | Last hit normal Z; zero after a miss |
| `RaycastHitX` | command | Last hit world X; zero after a miss |
| `RaycastHitY` | command | Last hit world Y; zero after a miss |
| `RaycastHitZ` | command | Last hit world Z; zero after a miss |
| `RecoveryVelocity` | property | How hard overlapping bodies are pushed apart |
| `RectContainsRect` | command | True when B is fully inside A |
| `RectsOverlap` | command | Axis-aligned rectangle overlap test |
| `Remove` | command | Take this instance's body out of the simulation (false when it had none) |
| `RemoveCollisionFocus` | command | Stop keeping colliders around this instance; false when it was not a collision focus |
| `Sensor` | property | Whether the body only notices what passes through it (a trigger) instead of blocking it |
| `SetAirDrag` | command | Global air resistance |
| `SetAllowSleep` | command | Whether idle bodies may sleep |
| `SetGravityDirection` | command | Point scene gravity somewhere else |
| `SetGravityStrength` | command | Scale scene gravity |
| `SetMaxVelocity` | command | Velocity ceiling for every body |
| `SetRecoveryVelocity` | command | How hard overlaps are pushed apart |
| `SetSleepThreshold` | command | Speed below which a body may sleep |
| `SetVelocity` | command | Set Dynamic or Kinematic velocity (given x, y and z only: the current instance's); 2D sprite bodies use pixels/second with Y down, 3D uses metres/second |
| `Shape` | property | The body's collider: "box", "sphere", "capsule" or "cylinder" (a model's "mesh" can be read, not chosen) |
| `SleepThreshold` | property | How slowly a body must move to be put to sleep (it wakes when touched) |
| `SolverIterations` | command | Solver iterations per step |
| `Speed` | read only | How fast the body moves, in any direction |
| `SphereCast` | command | Distance a sphere moves before it touches something (0 when it starts touching), -1 for none; the calling instance is never hit |
| `SphereRest` | command | Height of a sphere's centre where it comes to rest when lowered from maxCentreY (on a face, an edge or a corner); -1000000 when nothing is below |
| `SpinX` | property | How fast the body turns about X, degrees a second |
| `SpinY` | property | How fast the body turns about Y, degrees a second |
| `SpinZ` | property | How fast the body turns about Z, degrees a second (a 2D body's spin, clockwise as image_angle) |
| `Use` | command | Give this instance the body a Physics resource describes (made in the Physics editor), in place of any it has; false when there is no such resource |
| `UseGravity` | property | Whether gravity pulls this body at all |
| `VelocityX` | property | The body's speed along X (pixels or metres a second) |
| `VelocityY` | property | The body's speed along Y |
| `VelocityZ` | property | The body's speed along Z (always 0 for a 2D body) |
| `Weight` | property | How little air drag slows the body (heavier falls through air faster); 0 uses the world's default |

## Engine.Physics.Character

| Name | Kind | What it is |
|---|---|---|
| `Height` | read only | How tall this instance's character is now, in metres (read; CharacterSetHeight changes it where it fits) |
| `Radius` | read only | How wide this instance's character is, its capsule's radius in metres, as CharacterCreate made it (read) |
| `SlopeLimit` | property | The steepest slope this instance's character walks up, 0 to 89 degrees (as made by CharacterCreate, 45 unless set); steeper ground it slides from |
| `StepHeight` | property | How high a step this instance's character walks up without jumping, 0 to half its height (as made by CharacterCreate, 0.3 unless set); a crouch or a heavy load can lower it |

## Engine.Physics.Gravity

| Name | Kind | What it is |
|---|---|---|
| `Strength` | property | How strongly everything falls, metres a second squared (9.81 unless the room sets another) |
| `X` | property | Gravity along X (0 unless set) |
| `Y` | property | Gravity along Y (-9.81 unless set: down) |
| `Z` | property | Gravity along Z (0 unless set) |

## Engine.Physics.Sleep

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Bodies at rest stop being simulated until something touches them (true unless set) |
| `Threshold` | property | How slowly a body must move to fall asleep (the engine's unless set) |

## Engine.Physics.World

| Name | Kind | What it is |
|---|---|---|
| `AirDrag` | property | How much the air slows every body, 0 to 1 (the room's unless set) |
| `MaxVelocity` | property | The fastest any body may move, metres a second (the room's unless set) |
| `RecoveryVelocity` | property | How fast bodies pushed into each other are moved apart (the room's unless set) |

## Engine.PostEffect

| Name | Kind | What it is |
|---|---|---|
| `Add` | command | Run one of the project's Fullscreen Shader resources over the finished frame, after any already running |
| `AddFile` | command | Run a Full screen shader written as a plain .hlsl file of the game's folder (from the project folder or its Assets folder, "ShaderPacks/Sepia.hlsl") after the effects already running, as PostEffectAdd runs a Shader resource. Its name is the path given; it is compiled on a worker and again whenever the file changes. False when there is no such file (PostEffectLastError says why) |
| `Clear` | command | Stop every post effect |
| `Error` | command | Why one post effect does not run; empty when it runs or is still being compiled |
| `IsRunning` | command | True once a post effect is compiled and drawn over the frame; false while it is being compiled, when it failed, or when it was not added |
| `LastError` | command | Why the post effect that most recently failed does not run, as "effect: reason" (the compiler's message for a shader that does not compile, or a file or resource not found); empty once every effect asked for runs or is being compiled |
| `Remove` | command | Stop running a post effect |
| `SetParameter` | command | Set one of a running post effect's parameters, by the name it has in the shader's GenesisParameters |
| `SetTexture` | command | Give a running post effect a picture to read: image an Image resource's name or a texture made with TextureCreate ("" takes it away); slot the texture's register, 3 to 15 (Texture2D Noise : register(t3)), or its name in the shader ("Noise"). t0 to t2 are the frame, its depth and its flags. False when the effect is not running or the slot is not one it may be given |

## Engine.Priority

| Name | Kind | What it is |
|---|---|---|
| `Add` | command | Add a number with a priority (smaller comes out first; not a number counts as the largest); up to 1 000 000 entries |
| `AddString` | command | Add text with a priority |
| `Clear` | command | Remove every entry |
| `Create` | command | Create a priority queue: entries come out smallest priority first, equal priorities in the order added |
| `DeleteMin` | command | Take out the entry of smallest priority (the earliest added among equals) and return it as a number; 0 when empty |
| `DeleteMinString` | command | Take out the entry of smallest priority and return it as text; empty when the queue is empty |
| `Destroy` | command | Release a priority queue |
| `Empty` | command | True when the queue has no entries |
| `FindMin` | command | The entry DsPriorityDeleteMin would take out, as a number, left in the queue; 0 when empty |
| `FindMinString` | command | The entry DsPriorityDeleteMin would take out, as text; empty when the queue is empty |
| `MinPriority` | command | The priority of the entry that comes out next; 0 when empty |
| `Size` | command | Entry count |

## Engine.Queue

| Name | Kind | What it is |
|---|---|---|
| `Create` | command | Create a queue |
| `Dequeue` | command | Take from the head; 0 when empty |
| `Destroy` | command | Release a queue |
| `Enqueue` | command | Add to the tail |
| `Head` | command | Peek the head; 0 when empty |
| `Size` | command | Entry count |

## Engine.Recording

| Name | Kind | What it is |
|---|---|---|
| `IsRecording` | command | True while the game is being recorded |
| `Recording` | read only | Reads (RecordIsRecording): True while the game is being recorded |
| `ReplayBufferDisable` | command | Stop keeping a replay buffer and free it |
| `ReplayBufferEnable` | command | Keep the last seconds of play (1 to 60) in memory, at fps frames a second (5 to 60) and scale of the screen size (0.1 to 1; 0.5 is half): what ReplayPlay and ReplayBufferSave use. Costs a frame readback at that rate |
| `ReplayBufferSave` | command | Save the last seconds of the replay buffer as Recordings/name.mp4 (made in the background); "" when the buffer is empty |
| `ReplayBufferSeconds` | command | How many seconds the replay buffer holds right now |
| `ReplayDraw` | command | Draw the replay's current frame into a rectangle, in Draw GUI: full screen for a killcam, or small for picture-in-picture. False when no replay is playing |
| `ReplayIsPlaying` | command | True while a replay is playing |
| `ReplayPlay` | command | Play back the last secondsBack of the replay buffer at speed (1 is real time, 0.5 slow motion); draw it with ReplayDraw. The buffer pauses while it plays. False when the buffer is empty |
| `ReplayProgress` | command | How far the replay has played, 0 to 1 (1 when none is playing) |
| `ReplayStop` | command | Stop a replay before its end |
| `Start` | command | Start recording the game: format "mp4" (the default) makes Recordings/name.mp4 when RecordStop is called; "png" saves every frame as a picture in Recordings/name. Up to 30 frames a second; false when already recording |
| `Stop` | command | Stop recording and return where the video (or folder of pictures) goes; the video is finished in the background a moment later. "" when nothing was recording |

## Engine.Rendering

| Name | Kind | What it is |
|---|---|---|
| `AntiAliasing` | property | Anti-aliasing of the 3D picture: off, fxaa or smaa (as RenderSetAntiAliasing) |
| `AtmosphereLutEnabled` | property | Enable atmosphere LUT sky-view (AL) |
| `AutoExposure` | command | Turn on eye adaptation with a key (0.18 = mid grey) and an adaptation time in seconds (as RenderSetAutoExposure) |
| `AutoExposureEnabled` | property | Eye adaptation on or off for this room |
| `AutoExposureMetering` | property | Sets (RenderSetAutoExposureMetering): How eye adaptation measures the picture: 0 counts it all evenly, 1 counts its centre most (0.5 by default) |
| `Backend` | read only | The graphics the game draws with: "Direct3D 11", "Direct3D 12", "Vulkan", "OpenGL" or "Software" (read) |
| `BloomEnabled` | property | Enable HDR bloom (BL) |
| `CascadeCount` | property | Directional shadow cascade count (2 or 3) |
| `CelestialExtrasEnabled` | property | Enable celestial extras stars/MW/moon (CE) |
| `CloudQuality` | property | Cloud quality ladder 0..3 (P/B/H/C) |
| `CloudTemporalEnabled` | property | Enable cloud temporal reprojection (CT) |
| `ContactShadowsEnabled` | property | Enable contact shadows (CS — screen-space) |
| `Contrast` | property | Colour grading contrast (1 = identity) |
| `DrawCalls` | read only | Draw calls the last frame made, 2D and 3D (read) |
| `DrawModel3D` | command | Draw a model asset |
| `DrawModelShader3D` | command | Draw a real model resource through an authored mesh shader |
| `DrawModelTransform3D` | command | Draw a model resource with independent scale and rotation |
| `DrawPointLight3D` | command | Add an omnidirectional light to the frame; from a Step event it lights the next frame drawn |
| `DrawPointLightFalloff3D` | command | Add an omnidirectional light with an explicit falloff exponent; from a Step event it lights the next frame drawn |
| `Exposure` | property | HDR exposure scalar (1 = identity) |
| `FogShaftAnisotropy` | property | How strongly the haze scatters sunlight forward, 0 to 0.95 (0.25 as before). Above 0.25 shafts glow towards the sun and fade side-on, as in dusty air; about 0.7 for sunbeams indoors |
| `FogShaftFloor` | property | Brightness of fog in shadow against fog in sunlight, 0 to 1 (0.35 as before); higher gives subtler shafts |
| `FogShaftSoftness` | property | Width of a sun shaft's soft edge in metres, 0 to 4 (0 as before: one shadow sample, a cut edge) |
| `GetAntiAliasing` | command | The anti-aliasing asked for: "off", "fxaa", "smaa" or "taa" |
| `GetAutoExposure` | command | Whether eye adaptation is on in this room |
| `GetMotionBlur` | command | The camera motion blur amount, 0 (off) to 1 |
| `GetQuality` | command | The quality tier last set ("low", "medium", "high" or "ultra"), "custom" once one of its settings has been changed since, or "" before any tier was set |
| `GpuMs` | read only | Milliseconds the graphics card spent on the last 3D scene it has finished (read; 0 where the renderer cannot tell) |
| `GtaoEnabled` | property | Enable GTAO (AO — half-res ambient occlusion) |
| `LocalVolumetricLightBudget` | property | Local volumetric light budget (0–4, LV) |
| `LocalVolumetricsEnabled` | property | Enable local volumetric scatter (LV) |
| `MotionBlur` | property | Camera motion blur, 0 (off, the default) to 1 (as RenderSetMotionBlur) |
| `OmniShadowBudget` | property | Omni-shadow cubemap budget (0–4) |
| `Quality` | read only | Reads (RenderGetQuality): The quality tier last set ("low", "medium", "high" or "ultra"), "custom" once one of its settings has been changed since, or "" before any tier was set |
| `QualityTier` | property | The quality tier last set: low, medium, high or ultra; custom once one of its settings has been changed; empty before any. Setting it sets the tier |
| `RaymarchedCloudsEnabled` | property | Enable raymarched 3D clouds (CL) |
| `ResetAutoExposure` | command | Eye adaptation jumps to the scene at once instead of adjusting over time (after a camera cut or a teleport) |
| `ReversedDepth` | property | Store scene depth reversed in floating point, so a view of kilometres keeps a close near plane without distant surfaces flickering. On by default; the software renderer ignores it |
| `Saturation` | property | Colour grading saturation (1 = identity, 0 = grey) |
| `SetAntiAliasing` | command | Smooth the jagged edges of the 3D picture: "off", "fxaa" (one pass, softest) or "smaa" (sharper, three passes), or "taa" (frames blended over time: smoothest, best on fine detail); the GUI is never softened. False for any other name |
| `SetAutoExposure` | command | Eye adaptation: the exposure follows the scene's brightness so an average scene shows at key (0.18 = mid grey), taking about adaptSeconds to adjust (0 = at once), never darker than minEv or brighter than maxEv stops (e.g. -4, 4) |
| `SetAutoExposureMetering` | command | How eye adaptation measures the picture: 0 counts it all evenly, 1 counts its centre most (0.5 by default) |
| `SetAutoExposureSpeed` | command | How long eye adaptation takes: to darken when the scene gets brighter (stepping into daylight, 0.8 by default) and to brighten when it gets darker (2.5 by default); 0 = at once |
| `SetMotionBlur` | command | Camera motion blur: 0 off (the default) to 1, the share of a frame the virtual shutter is open (0.5 is a film camera's). Only the camera's own movement and turning blur; held items and the GUI stay sharp |
| `SetQuality` | command | Set the graphics quality tier: low, medium, high or ultra (as RenderSetQuality). False for any other name |
| `ShadowResolution` | property | Sun shadow map size in texels per side, for every cascade: 512, 1024 (the default), 2048 or 4096; 0 goes back to the default |
| `SmokeExtinctionEnabled` | property | Enable smoke extinction (SE) |
| `Triangles` | read only | Triangles the last frame drew (read) |
| `Vignette` | property | Strength of the darkening towards the picture's corners (0 = none) |
| `VolumetricFogQuality` | property | Volumetric fog quality for every room: 0 low, 1 medium, 2 high; -1 leaves each room's own |
| `WaterReflections` | property | Enable one half-resolution planar reflection for the dominant water plane; software uses sky fallback |

## Engine.Rendering.AntiAliasing

| Name | Kind | What it is |
|---|---|---|
| `Mode` | property | Smoothing of jagged edges: "off", "fxaa", "smaa" or "taa" (the quality tier's unless set) |

## Engine.Rendering.Ao

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Ambient occlusion: soft darkening in corners and creases (the quality tier's unless set) |

## Engine.Rendering.Bloom

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | A glow round bright light (the quality tier's unless set) |

## Engine.Rendering.Culling

| Name | Kind | What it is |
|---|---|---|
| `Frustum` | property | Skip drawing what is outside the view (true unless set) |
| `Occlusion` | property | Skip drawing what is hidden behind large things (false unless set) |

## Engine.Rendering.Decals

| Name | Kind | What it is |
|---|---|---|
| `DecalAdd` | command | Put a picture on the world at x, y, z facing along the surface normal nx, ny, nz (a bullet hole where a ray hit, a scorch mark under a blast): size across, projected onto whatever opaque surface is there, walls, models and terrain alike, and not onto surfaces turned away. It lasts life seconds, fading out over the end (0: until removed). image is an Image resource or a texture from TextureCreate. Multiplied over the surface by default, so it keeps the surface's light. 0 when it could not be placed |
| `DecalClear` | command | Remove every decal (a room change does this too) |
| `DecalCount` | command | How many decals are in the world now |
| `DecalExists` | command | Whether a decal is still in the world |
| `DecalFollow` | command | Keep a decal on an object that moves (a door, a cart, a crate): it moves and turns with it from now on and goes when the object does. False when there is no such decal or object |
| `DecalMove` | command | Move a decal and turn it to a new normal. Decals stay where they were put in the world: one on a moving object follows it only when moved like this each frame |
| `DecalRemove` | command | Remove a decal at once; false when it is gone already |
| `DecalSetBlend` | command | How a decal goes over the surface: "multiply" (the default: darkens and tints and keeps the surface's light, for holes, scorch, blood, dirt), "alpha" (paints its own colours, unlit, for signs and paint) or "add" (adds light, for embers and glowing marks) |
| `DecalSetColor` | command | Tint a decal (0-255 each; white leaves the picture as it is) and set its opacity (0-1) |
| `DecalSetFade` | command | How many seconds at the end of its life a decal takes to fade out (by default the last quarter of its life, at most 2 seconds; less than 0 goes back to that) |
| `DecalSetLife` | command | Give a decal a new life from now (0: until removed) |
| `DecalSetLimit` | command | How many decals may be in the world at once (256 at first, at most 4096): adding past it removes the oldest, as lowering it does |
| `DecalSetRotation` | command | Turn a decal's picture about its normal, anticlockwise as seen from the front (a random turn makes bullet holes look less alike) |
| `DecalSetSeeThrough` | command | Put a decal on a see-through surface (a bullet hole in a window pane): drawn as a flat picture in its own plane after glass and other see-through draws, seen from both sides and hidden only by opaque things in front, instead of projected onto the opaque world. Off by default |
| `DecalSetSize` | command | A decal's width and height, and how deep through the surface it reaches (half its size at first: deeper reaches bumpier ground, but also anything standing on it) |
| `DecalUnfollow` | command | Stop a decal following its object; it stays where it is |

## Engine.Rendering.Detail

| Name | Kind | What it is |
|---|---|---|
| `AnimationBySize` | property | Figures small on screen are posed less often (true unless set) |
| `CullSize` | property | Models smaller than this share of the screen's height are not drawn, 0 to 0.1 (0.0025 unless set) |
| `Enabled` | property | Automatic detail: models far away drawn simpler and tiny ones not at all (true unless set) |
| `Quality` | property | How long models keep full detail as they shrink on screen, 0.05 to 8: 2 keeps it to half the size (1 unless set) |

## Engine.Rendering.Exposure

| Name | Kind | What it is |
|---|---|---|
| `Auto` | property | Eye adaptation: the picture brightens in the dark and darkens in bright light (off unless the room turns it on) |
| `Value` | property | How bright the picture is made, 0.1 to 8 (1 unless set) |

## Engine.Rendering.Fog

| Name | Kind | What it is |
|---|---|---|
| `Color` | property | The fog's colour as #rrggbb (the room's unless set) |
| `Density` | property | How thick the fog is, 0 to 1 (0.012 unless the room sets another) |
| `Enabled` | property | Distance fog (off unless the room turns it on) |
| `End` | property | Where the fog hides everything (120 unless the room sets another) |
| `HeightBase` | property | The height fog thins out above (0 unless set) |
| `HeightFalloff` | property | How quickly fog thins with height, 0 (even) to 10 (0.35 unless set) |
| `Noise` | property | How patchy the fog is, 0 to 1 (0.04 unless set) |
| `Shafts` | property | Volumetric fog and its light shafts: 0 off, 1 or 2 for more detail (the quality tier's unless set) |
| `Start` | property | Where the fog begins (35 unless the room sets another) |

## Engine.Rendering.Grading

| Name | Kind | What it is |
|---|---|---|
| `Contrast` | property | The picture's contrast (1 unless set) |
| `Saturation` | property | How colourful the picture is: 0 grey, 1 as drawn (1 unless set) |
| `Vignette` | property | How much the picture darkens towards its corners, 0 to 1 (0 unless set) |

## Engine.Rendering.Instances

| Name | Kind | What it is |
|---|---|---|
| `Culled` | read only | Instances the last frame left out as unseen (read) |
| `Drawn` | read only | Instances the last frame drew (read) |

## Engine.Rendering.Lights

| Name | Kind | What it is |
|---|---|---|
| `LightEmitterEnable` | command | Enable or disable this object's persistent Light Emitter |
| `LightEmitterSetAction` | command | Set Steady, Flicker, Pulse, Glow, or ColourCycle behaviour |
| `LightEmitterSetColor` | command | Set the primary light colour |
| `LightEmitterSetColorCount` | command | Use one, two, or three authored light colours |
| `LightEmitterSetFalloff` | command | Set light falloff (1 linear, 2 quadratic, larger values concentrate light near the emitter) |
| `LightEmitterSetIntensity` | command | Set this object's light intensity |
| `LightEmitterSetOffset` | command | Set the emitter offset relative to this object |
| `LightEmitterSetRadius` | command | Set this object's light radius |
| `LightEmitterSetSecondaryColor` | command | Set the second light colour used by colour cycling |
| `LightEmitterSetTertiaryColor` | command | Set the third light colour used by colour cycling |
| `LightFlash` | command | A short point light at x, y, z: full brightness at once, fading to nothing over life seconds, then gone (a muzzle flash, an explosion, a spark). Colour 0-255; intensity and radius as DrawPointLight3D. It lights surfaces and fog but is never shadowed and never takes a lamp's shadow. Up to 256 at once (a new one then replaces the one nearest its end). 0 when nothing would be lit |
| `LightFlashClear` | command | Put every flash out (a room change does this too) |
| `LightFlashCount` | command | How many flashes are lit now |
| `LightFlashExists` | command | Whether a flash is still lit |
| `LightFlashMove` | command | Move a flash (one that follows a moving muzzle or a rolling fireball); false when it has burnt out |
| `LightFlashRemove` | command | Put a flash out at once; false when it has burnt out already |

## Engine.Rendering.Models

| Name | Kind | What it is |
|---|---|---|
| `KeepPreviousTransform` | property | Keep entity position, rotation and proportional model scale when a model or animation changes |

## Engine.Rendering.MotionBlur

| Name | Kind | What it is |
|---|---|---|
| `Strength` | property | Blur along movement, 0 (off) to 1 (0 unless set) |

## Engine.Rendering.Shadows

| Name | Kind | What it is |
|---|---|---|
| `Bias` | property | How far shadows are pushed off the surfaces casting them, against speckled self-shadowing, 0 to 0.01 (0.00015 unless set) |
| `Cascades` | property | How many shadow maps share the view, 2 or 3 (the quality tier's unless set) |
| `Contact` | property | Small screen-space shadows where things touch (the quality tier's unless set) |
| `Distance` | property | How far from the camera the sun's shadows reach, 0 for automatic (0 unless the room sets another) |
| `Enabled` | property | The sun's shadows (true unless the room turns them off) |
| `LampBudget` | property | How many point and spot lights may cast shadows at once (the quality tier's unless set) |
| `Resolution` | property | The sun's shadow map size in pixels (the quality tier's unless set) |
| `Strength` | property | How dark shadows are, 0 (none) to 1 (1 unless the room sets another) |

## Engine.Resource

| Name | Kind | What it is |
|---|---|---|
| `Exists` | command | Check a globally unique project resource name; no path or extension |
| `List` | command | A new list (DsList) of the names of the resources of one kind in a project folder, sorted, for a menu that offers whatever the folder holds. folder from Assets ("Shaders/Packs") or from the project ("Assets/Shaders/Packs"), "" for Assets; type as ResourceTypeOf names it ("Shader", "Image", "Audio", "Object", "Room", "Model"...), "Fullscreen shader", "Mesh shader" or "Sprite shader" for one kind of shader, or "" for every kind; subfolders true to include theirs. Free it with DsListDestroy |
| `LoadAsset` | command | Load a project-relative texture through the asset pipeline; returns a TextureHandle |
| `Name` | command | Return the canonical resource name, or an empty string when absent |
| `ScriptExecute` | command | Execute a named PGSL Script resource; supports spaces in resource names. Returns 0 when no Script VM is active |
| `TypeOf` | command | Return the resource type for a project name, or an empty string when absent |

## Engine.Room.Change

| Name | Kind | What it is |
|---|---|---|
| `Fade` | property | How long the cover takes to fade from a room just shown, 0 to 5 seconds (0.2 unless set; 0 lifts it at once) |
| `MinimumTime` | property | The least time a room change's cover stays up, so a loading screen can be read, 0 to 60 seconds (0 unless set) |
| `ProgressBar` | property | Whether the engine draws its progress bar and text on a room change's cover (true unless set) |
| `Text` | property | The words above the progress bar during a room change ("Loading" unless set; "" for none) |

## Engine.Room

| Name | Kind | What it is |
|---|---|---|
| `ChangeBudget` | command | How long a room change works in each frame before the frame is drawn and it carries on in the next (8 unless set). 0 changes room in one step, with no cover and no loading screen |
| `ChangeColors` | command | The colour of the cover drawn during a room change, and of the engine's progress bar and text on it; each part from 0 to 1 |
| `ChangeFade` | command | How long the cover takes to fade from a room that has just been shown (0.2 unless set; 0 lifts it at once) |
| `ChangeMinimumTime` | command | The least time the cover of a room change stays up once it is raised, so a loading screen can be read (0 unless set) |
| `ChangeProgressBar` | command | Whether the engine draws its own progress bar and text on the cover during a room change; turn it off to show only the cover, or to draw your own |
| `ChangeText` | command | The words above the engine's progress bar during a room change; an empty string for none |
| `Changing` | command | True while a room change is being spread over frames behind its loading screen |
| `GetGridSize` | command | The room's editing grid size |
| `GetHeight` | command | Room height in world units |
| `GetName` | command | Name of the current room |
| `GetWidth` | command | Room width in world units |
| `Goto` | command | Change to another room |
| `GotoWhenLoaded` | command | Read another room's models in the background and change to it when they are ready; the current room keeps running meanwhile |
| `GridSize` | read only | Reads (RoomGetGridSize): The room's editing grid size |
| `Height` | read only | Current room height in pixels |
| `IsThreeD` | command | True when the room is a 3D room |
| `LayerCount` | command | How many layers the room has |
| `LoadProgress` | command | From 0 to 1: how much of the room asked for with RoomGotoWhenLoaded has been read, or how far a room change has got; 1 when neither is under way |
| `Name` | read only | Reads (RoomGetName): Name of the current room |
| `NodeCount` | command | How many nodes the room defines |
| `Preload` | command | Start reading another room's models in the background so a later RoomGoto has less to load; returns how many models the room names |
| `PreloadPending` | command | How many models are still being read in the background; 0 means a RoomGoto will find the preloaded room's models ready |
| `Restart` | command | Re-enter the current room |
| `Speed` | property | Target frames per second |
| `ThreeD` | read only | Reads (RoomIsThreeD): True when the room is a 3D room |
| `ValueGet` | command | Read a room-scoped numeric script value |
| `ValueSet` | command | Set a numeric value shared by scripts in the current room; not a global or a save file |
| `Width` | read only | Current room width in pixels |

## Engine.Room.Streaming

| Name | Kind | What it is |
|---|---|---|
| `Forget` | command | Forgets what every streamed Object was like and which were destroyed: each comes back as its room first made it (a new day's foes, a reset dungeon) |
| `Load` | command | Puts back what Engine.Room.Streaming.Save gave (from SaveSlotRead): each room comes back as saved the next time the game enters it, so change to the saved room after loading; false when the text is not such a save |
| `Remember` | property | Streamed Objects ("streamable": true) come back where they were with their variables, and Objects or scenery a script destroyed stay gone, for the rest of the game, room by room (true unless set) |
| `Remembered` | read only | How many streamed Objects and destroyed things are remembered now, in every room (read) |
| `Save` | command | What every room remembers, as text for a save slot (SaveSlotWrite): each streamed Object and each Object of a persistent room where it was with its variables, what scripts destroyed, and what scripts made in persistent rooms; the room the game is in is taken as it is now |

## Engine.Save

| Name | Kind | What it is |
|---|---|---|
| `Clear` | command | Clear staged numeric save values; persisted only by SaveFlush |
| `Exists` | command | Whether this project has numeric save values |
| `Flush` | command | Atomically persist numeric values under LocalAppData/Genesis/GameSaves |
| `GetNumber` | command | Read a project-isolated numeric save value |
| `LastError` | command | Last numeric save error; empty after a successful checked setter or flush |
| `SetNumber` | command | Stage a numeric save value; call SaveFlush at a checkpoint |
| `SlotCount` | command | How many save slots exist |
| `SlotDelete` | command | Remove a named save slot |
| `SlotExists` | command | True when a named save slot exists |
| `SlotLastError` | command | Why the last SaveSlotWrite failed; empty after one that worked |
| `SlotName` | command | The name of a save slot, most recently written first, counting from 0; empty when there is none |
| `SlotRead` | command | Read a named save slot; empty when it does not exist |
| `SlotWrite` | command | Write a named save slot (JSON or any text up to 4 MiB), replacing what it held. Kept under the player's profile |
| `TrySetNumber` | command | Stage a finite number; report invalid keys, values and the 256-key limit through SaveLastError |

## Engine.Shader

| Name | Kind | What it is |
|---|---|---|
| `InstanceClearOutline` | command | Take another instance's outline away |
| `InstanceGetShaderParameter` | command | A shader parameter set on an instance (its first component); 0 when it has none of that name |
| `InstanceHasOutline` | command | Whether an instance is outlined (or marked) |
| `InstanceSetOutline` | command | Outline another instance's 3D draws in a colour (0 to 1 each), width in pixels up to 16; throughWalls shows it behind walls |
| `InstanceSetShader` | command | Draw another instance with a Shader resource (empty for none) |
| `InstanceSetShaderParameter` | command | Set one of another instance's shader parameters |
| `InstanceSetShaderTexture` | command | Give one of another instance's shader textures another Image; empty gives it back its own |
| `InstanceSetShaderVector` | command | Set a float2, float3 or float4 shader parameter of another instance |
| `Name` | property | Sets (ShaderSet): Draw this instance with a Shader resource (empty for none) |
| `OutlineClear` | command | Take this instance's outline away |
| `OutlineSet` | command | Outline this instance's 3D draws in a colour (0 to 1 each), width in pixels up to 16 (0 marks it for post effects without a line); throughWalls shows it behind what stands in front |
| `Set` | command | Draw this instance with a Shader resource (empty for none) |
| `SetParameter` | command | Set one of this instance's shader parameters, by its name in GenesisParameters |
| `SetTexture` | command | Give one of this instance's shader textures (a Texture2D the shader declares) another Image; empty gives it back its own |
| `SetVector` | command | Set a float2, float3 or float4 shader parameter of this instance (extra components are ignored) |

## Engine.Sky

| Name | Kind | What it is |
|---|---|---|
| `Altitude` | property | Cloud slab base height in metres |
| `AmbientMode` | property | How the sky lights shade: "hemisphere" or "zenith" (as SkySetAmbientMode) |
| `AmbientSaturation` | property | Colourfulness of the hemisphere sky light, 0 to 2 (1 = the sky's own colour) |
| `AmbientScale` | property | Scales the sky's ambient light, 0 to 8 (1 = as the preset has it) |
| `AmbientStrength` | property | Brightness of the hemisphere sky light, 0 to 16 (1 = the sky as drawn) |
| `AtmospherePreset` | property | The room's atmosphere preset by name (empty with no sky); setting it changes the preset, as SetAtmospherePreset does |
| `Coverage` | property | Cloud coverage scale (weather map × FogVolumes) |
| `DayOfYear` | property | Calendar day-of-year (celestial extras / moon phase) |
| `Density` | property | Cloud density scale (raymarch intensity multiplier) |
| `FogScale` | property | Scales the fog the weather brings, 0 to 4 (1 = as the weather has it) |
| `GetAmbientMode` | command | "hemisphere" or "zenith": how the sky lights shade in this room |
| `GetAmbientSaturation` | command | Colourfulness of the hemisphere sky light |
| `GetAmbientStrength` | command | Brightness of the hemisphere sky light |
| `Haze` | property | Atmospheric haze of the room's sky, 0 to 1, as the Room's Haze; changes with the hour for a hazy morning |
| `Latitude` | property | Observer latitude in degrees (celestial extras) |
| `MoonDirectionX` | read only | X of the unit direction towards the moon |
| `MoonDirectionY` | read only | Y of the unit direction towards the moon |
| `MoonDirectionZ` | read only | Z of the unit direction towards the moon |
| `MoonDiscVisible` | property | Whether the moon's disc is drawn; off keeps the moonlight (for a game drawing its own moon) |
| `NightFactor` | read only | 0 by day to 1 at night, as the engine blends sun and moon light |
| `Quality` | property | Cloud quality 0..3 (alias Engine.Rendering.CloudQuality) |
| `RaymarchedCloudsEnabled` | property | Enable raymarched clouds (alias Engine.Rendering) |
| `SetAmbientMode` | command | How the sky lights shade: "hemisphere" (the light of the whole sky dome and the ground, brighter and less blue) or "zenith" (the sky's zenith colour, as before). False for another name |
| `SetAmbientSaturation` | command | Colourfulness of the hemisphere sky light, 0 to 2: 1 the sky's own colour, 0 grey; 0.4 by default (a clear day's sunlit ground comes out near neutral) |
| `SetAmbientStrength` | command | Brightness of the hemisphere sky light, 0 to 16 (1 = the sky as drawn; 1.5 for brighter, game-like shade) |
| `SetAmbientTint` | command | Colour multiplier of the hemisphere sky light (as SkySetAmbientTint) |
| `SetAtmospherePreset` | command | Switch the room's atmosphere: Natural, ClearDay, GoldenHour, Overcast, Storm, Night or Alien. False for an unknown name |
| `SetCloudCoverage` | command | Set cloud coverage scale (alias Coverage) |
| `SetHaze` | command | Set the atmospheric haze, 0 to 1 (alias Haze) |
| `SetLatitude` | command | Set observer latitude in degrees (alias Latitude) |
| `SetTimeOfDay` | command | Set time of day in hours (alias TimeOfDay) |
| `SunDirectionX` | read only | X of the unit direction towards the sun |
| `SunDirectionY` | read only | Y of the unit direction towards the sun (below 0 when it has set) |
| `SunDirectionZ` | read only | Z of the unit direction towards the sun |
| `SunDiscVisible` | property | Whether the sun's disc is drawn; off keeps its light (for a game drawing its own sky) |
| `SunSize` | property | How large the sun's disc is drawn against its usual size (0.25 to 8) |
| `Thickness` | property | Cloud slab thickness in metres |
| `TimeOfDay` | property | Time of day in hours (0..24) |
| `Visibility` | property | How far one can see through the atmosphere's fog, in metres (0 = the preset's own) |

## Engine.Sky.Clouds

| Name | Kind | What it is |
|---|---|---|
| `Altitude` | property | How high the cloud layer starts (the room's unless set) |
| `Coverage` | property | How much of the sky is cloud, 0 to 1 (the room's unless set) |
| `Density` | property | How thick the clouds are (the room's unless set) |
| `Enabled` | property | Volumetric clouds (the room's unless set) |
| `Quality` | property | The clouds' detail (the quality tier's unless set) |
| `Thickness` | property | How deep the cloud layer is (the room's unless set) |

## Engine.Sky.Moon

| Name | Kind | What it is |
|---|---|---|
| `Visible` | property | Whether the moon's disc is drawn in the sky (true unless set) |

## Engine.Sky.Stars

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Stars and the moon's glow at night (the quality tier's unless set) |

## Engine.Sky.Sun

| Name | Kind | What it is |
|---|---|---|
| `Size` | property | How large the sun's disc is drawn (1 unless set) |
| `Visible` | property | Whether the sun's disc is drawn in the sky (true unless set) |

## Engine.Sky.Time

| Name | Kind | What it is |
|---|---|---|
| `DayOfYear` | property | The day of the year, 1 to 365: the sun's height through the seasons (the room's unless set) |
| `Latitude` | property | How far north (positive) or south the world is, for the sun's path (the room's unless set) |
| `OfDay` | property | The time of day, 0 to 24 hours: moves the sun and moon (the room's unless set) |
| `Paused` | property | Stop the time of day where it is (false unless set) |
| `Speed` | property | How much faster than real time the day goes, 0 to 100000 (24 unless the room sets another: a day an hour) |

## Engine.Stack

| Name | Kind | What it is |
|---|---|---|
| `Create` | command | Create a stack |
| `Destroy` | command | Release a stack |
| `Pop` | command | Pop the top value; 0 when empty |
| `Push` | command | Push a number |
| `Size` | command | Entry count |
| `Top` | command | Peek the top value; 0 when empty |

## Engine.Terrain.Collision

| Name | Kind | What it is |
|---|---|---|
| `Distance` | property | How far round each moving body a large terrain's collision is built, 4 to 500 metres (24 unless set): more for fast vehicles |

## Engine.Terrain

| Name | Kind | What it is |
|---|---|---|
| `Depth` | read only | How deep the room's loaded terrains are together, along z, in metres (read; 0 without a terrain) |
| `Enabled` | property | Whether the room's terrains' ground and paths are drawn (true unless set); what stands on it still stands, for an x-ray or a map view |
| `GetHeight` | command | Sample terrain elevation at world XZ |
| `MaterialAt` | command | The painted layer (1 to 8) most of the ground at world x, z is, 0 where no terrain is: footstep sounds, grip, where crops grow |
| `NormalX` | command | Which way the ground at world x, z faces, across x (with NormalY and NormalZ, a direction of length 1; straight up where no terrain is) |
| `NormalY` | command | Which way the ground at world x, z faces, upward: 1 on the flat, less the steeper it is |
| `NormalZ` | command | Which way the ground at world x, z faces, across z |
| `SlopeAt` | command | How steep the ground at world x, z is, 0 (flat) to 90 degrees: a vehicle that cannot climb, a skier that speeds up |
| `Width` | read only | How wide the room's loaded terrains are together, along x, in metres (read; 0 without a terrain) |

## Engine.Terrain.Detail

| Name | Kind | What it is |
|---|---|---|
| `Distance` | property | How near the camera a streamed terrain tile is read, 0 to 1000000 metres (the room's terrain distance unless set); only for a room that streams its terrains |
| `Quality` | property | How far large terrains keep their full detail from the camera, 0.25 to 4 (1 unless set): 2 keeps it twice as far for a sharper distance, 0.5 half as far for a faster frame; lasts across rooms |

## Engine.Terrain.Edit

| Name | Kind | What it is |
|---|---|---|
| `Flatten` | command | Levels the ground around world x, z to the world height given, blending out to the radius (a floor for a building a player puts down); false where no terrain is |
| `Lower` | command | Lowers the ground around world x, z by up to the metres given at the centre (a crater where a shell lands, a hole a player digs); false where no terrain is |
| `Paint` | command | Paints the terrain's layer 1 to 8 around world x, z at 0 to 1 strength (scorched earth, a path worn to dirt, snow settling); false where no terrain is |
| `Raise` | command | Raises the ground of the room's terrains around world x, z by up to the metres given at the centre, falling off smoothly to the radius (a mound, a bank of earth); its look, grass and collision follow at once and last while the room runs; false where no terrain is |
| `Smooth` | command | Softens the ground around world x, z toward its neighbours' heights, by 0 to 1 (an edge worn down); false where no terrain is |

## Engine.Terrain.Grass

| Name | Kind | What it is |
|---|---|---|
| `Density` | property | How thickly the grass grows, 0.05 to 4 times each terrain's own (1 unless set): thinner on a slow machine |
| `Distance` | property | How far from the camera grass grows, 0.1 to 10 times each terrain's own (1 unless set) |
| `Enabled` | property | Grass grown round the camera on every terrain, or none (each terrain's own rule unless set): off for a burnt field |
| `Height` | property | How tall the grass grows, 0.1 to 4 times each terrain's own (1 unless set): long grass after rain, cut grass in a garden |

## Engine.Terrain.Scatter

| Name | Kind | What it is |
|---|---|---|
| `Density` | property | How many trees and rocks the terrains scatter, 0 to 4 times each layer's own (1 unless set): the copies and their collision are placed again |
| `Distance` | property | How far the scattered trees and rocks are drawn, 0.1 to 10 times each terrain's own (1 unless set) |
| `Enabled` | property | Whether the terrains' scattered trees and rocks are drawn (true unless set); their collision stays |
| `Shadows` | property | Whether the scattered trees and rocks cast shadows (true unless set) |

## Engine.Terrain.Water

| Name | Kind | What it is |
|---|---|---|
| `ClearHoles` | command | Clear punched holes from the authoring scratchpad |
| `HoleAngleBias` | property | Jet direction bias in radians |
| `HoleCount` | read only | Number of punched holes in the authoring scratchpad |
| `HoleDischargeCoefficient` | property | Orifice discharge coefficient (Cd) |
| `HoleRadius` | property | Leak aperture radius in metres (punch tool) |
| `HoleShape` | property | Leak shape 0=Round 1=Slit 2=Crack |
| `PunchHole` | command | Punch a leak at position with outward surface normal |

## Engine.Text

| Name | Kind | What it is |
|---|---|---|
| `CharAt` | command | One character; index is 1-based |
| `Chr` | command | Character for a character code |
| `Contains` | command | True when the text contains the needle |
| `Copy` | command | Substring; index is 1-based |
| `Count` | command | How many times the needle occurs |
| `Delete` | command | Remove characters from a 1-based index |
| `Digits` | command | Only the digit characters |
| `EndsWith` | command | True when the text ends with the suffix |
| `Equals` | command | Exact comparison |
| `EqualsIgnoreCase` | command | Case-insensitive comparison |
| `FormatNumber` | command | Number as text with fixed decimals |
| `Insert` | command | Insert at a 1-based index |
| `IsEmpty` | command | True for empty or whitespace-only text |
| `Join2` | command | Concatenate two strings |
| `Join3` | command | Concatenate three strings |
| `LastPos` | command | 1-based position of the last match, 0 if absent |
| `Length` | command | Character count |
| `Letters` | command | Only the letter characters |
| `LettersDigits` | command | Only letters and digits |
| `LocalText` | command | A line of the game's text in the chosen language (Assets/Text/<language>.txt, key = text lines); English or the first language when it has no such key, else the key itself |
| `LocalTextHas` | command | True when the chosen language or the fallback has text for the key |
| `LocalTextWith` | command | LocalText with values put in for {0}, {1} and {2}: LocalTextWith("coins", n) for "coins = You have {0} coins" |
| `Lower` | command | Lower case |
| `Of` | command | Number as text; whole numbers lose the trailing .0 |
| `OrdAt` | command | Character code at a 1-based index |
| `PadLeft` | command | Right-align within a width |
| `PadRight` | command | Left-align within a width |
| `Pos` | command | 1-based position of the first match, 0 if absent |
| `RealOf` | command | Parse text as a number; 0 when it is not one |
| `Repeat` | command | Concatenate the text with itself |
| `Replace` | command | Replace the first occurrence |
| `ReplaceAll` | command | Replace every occurrence |
| `Reverse` | command | Characters in reverse order |
| `SplitCount` | command | How many delimited fields the text has |
| `SplitPart` | command | One 1-based field of delimited text |
| `StartsWith` | command | True when the text starts with the prefix |
| `Trim` | command | Remove leading and trailing whitespace |
| `Upper` | command | Upper case |

## Engine.Texture

| Name | Kind | What it is |
|---|---|---|
| `Create` | command | A picture a script paints, width x height pixels (1 to 4096 each), transparent at first; 0 when the size is not allowed. Paint it with TextureSetPixels, TextureSetRegion or TextureFillRectanglesFromList and draw it with DrawTexture |
| `Destroy` | command | Free a texture and its copy on the graphics card. Every texture is freed when the game ends |
| `Exists` | command | Whether a texture exists (made by TextureCreate and not destroyed) |
| `FillRectanglesFromList` | command | Paint rectangles into a texture from a list laid out as DrawRectanglesFromList's: x, y, width, height (pixels of the texture), red, green, blue (0-255), alpha (0-1). Each replaces the pixels it covers, alpha included, and is cut to the texture: a map's runs painted once, then drawn every frame with DrawTexture |
| `Height` | command | A texture's height in pixels; 0 when there is no such texture |
| `SetPixels` | command | Set a texture's pixels from a list holding 4 numbers for each, row by row from the top-left corner: red, green, blue (0-255) and alpha (0-1). A shorter list sets the pixels it holds |
| `SetRegion` | command | Set the pixels of a rectangle of a texture (x, y from its top-left corner) from a list holding 4 numbers for each, row by row: red, green, blue (0-255) and alpha (0-1). Pixels falling outside the texture are left out; a shorter list sets the pixels it holds |
| `SetSmooth` | command | Whether a texture is smoothed when drawn bigger or smaller than its pixels. Off at first: its pixels stay sharp, as a map's should |
| `Width` | command | A texture's width in pixels; 0 when there is no such texture |

## Engine.Tile

| Name | Kind | What it is |
|---|---|---|
| `Autotile` | property | Whether tiles painted or erased on a layer whose tile set autotiles take the tiles their neighbours call for (on unless set off) |
| `EraseAt` | command | Take away the tile at x, y (as TileRemoveAt): on an autotiled layer its neighbours take the tiles that now fit |
| `IndexAt` | command | Read a painted tile index, or -1 for empty; layer is the tile node name or id |
| `Meeting` | command | Test the caller's authored sprite mask against solid room tiles |
| `MoveX` | command | Sweep the sprite against tiles and return its allowed new X; assign x to the result |
| `MoveY` | command | Sweep the sprite against tiles and return its allowed new Y; assign y to the result |
| `PaintAt` | command | Put a tile in the cell at x, y of the front tile layer there (or the one named) whose tile set autotiles: it and its neighbours take the tiles that fit; false on a layer that does not autotile or a cell already painted |
| `RemoveAt` | command | Remove a live tile and its collision without changing the saved room (on an autotiled layer its neighbours take the tiles that now fit) |
| `SetAt` | command | Replace an existing live tile and invalidate collision; does not edit the saved room |

## Engine.Tile.Autotile

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Painting and erasing tiles from a script picks each tile from its neighbours on an autotiled tile set (true unless set) |

## Engine.Tile.Layer

| Name | Kind | What it is |
|---|---|---|
| `Alpha` | command | How opaque the tile layer is, 0 to 1 (1 unless set) |
| `OffsetX` | command | How far the tile layer is drawn moved across, in pixels (0 unless set) |
| `OffsetY` | command | How far the tile layer is drawn moved down, in pixels (0 unless set) |
| `SetAlpha` | command | Makes a tile layer see-through, 0 to 1 (a roof fading as the player walks under it); false for a name the room has no layer of |
| `SetOffset` | command | Draws a tile layer moved by x and y pixels (a background scrolled slower than the view for parallax); what is solid stays where it was painted; false for a name the room has no layer of |
| `SetVisible` | command | Shows or hides a tile layer of the room (a secret wall, a lit and an unlit version of a room); false for a name the room has no layer of |
| `Visible` | command | Whether the room's tile layer of this name is drawn (true unless set; false for a name the room has no layer of) |

## Engine.Time

| Name | Kind | What it is |
|---|---|---|
| `Day` | command | The day of the month of a DateNow time, 1 to 31, in local time |
| `Hour` | command | The hour of a DateNow time, 0 to 23, in local time |
| `Minute` | command | The minute of a DateNow time, 0 to 59 |
| `Month` | command | The month of a DateNow time, 1 to 12, in local time |
| `Now` | command | Now, as seconds since 1 January 1970 (UTC): store it in a save, then read its parts with the Date commands |
| `Second` | command | The second of a DateNow time, 0 to 59 |
| `Text` | command | A DateNow time as text in local time: format such as "yyyy-MM-dd HH:mm" or "d MMM yyyy" (empty for a short date and time) |
| `Weekday` | command | The day of the week of a DateNow time, 0 Sunday to 6 Saturday, in local time |
| `Year` | command | The year of a DateNow time, in local time |

## Engine.Ui

| Name | Kind | What it is |
|---|---|---|
| `Clicked` | command | True once after the player presses and releases the pointer on a button, slider or toggle; reading it clears it |
| `DrawUi` | command | Draw a User Interface resource in the GUI pass |
| `Enabled` | property | Whether User Interface resources are drawn and answer the pointer, keys and controllers (true unless set): off for a photo mode or a cutscene, everything in them kept as it was |
| `GetFocused` | command | The element the keyboard and controller act on (it has its Selected look), or an empty string |
| `GetHovered` | command | The enabled button, slider or toggle under the pointer, or an empty string |
| `GetScroll` | command | How far a Scroll List is scrolled, in design pixels |
| `GetText` | command | The text of an element on this instance: what the player typed into a Text Input, or what UiSetText or the resource gave it |
| `GetValue` | command | The value of a slider, toggle (0 or 1) or progress bar on this instance, as the player or UiSetValue left it |
| `HitTest` | command | Topmost visible UI element at a GUI point |
| `MouseX` | command | Mouse X in GUI pixels, including window scaling |
| `MouseY` | command | Mouse Y in GUI pixels, including window scaling |
| `ScrollTo` | command | Scroll a Scroll List to an offset in design pixels from its start (kept within its content) |
| `SetEnabled` | command | Enable or disable one element on this instance: a disabled one has its Disabled look and ignores the pointer |
| `SetFocus` | command | Give an element the focus (an empty id takes it away): a menu opens with its first button ready for the controller, a name box ready for typing |
| `SetSelected` | command | Give one element its Selected look on this instance, for menus moved with keys or a controller |
| `SetText` | command | Override text for one UI element on this instance |
| `SetTheme` | command | Draw this UI on this instance with the named styles of another User Interface resource (a light or dark theme, a team colour); an empty name goes back to its own |
| `SetValue` | command | Override a progress/value element on this instance |
| `SetVisible` | command | Show or hide one UI element on this instance |
| `Submitted` | command | True once after the player presses Enter in a Text Input; reading it clears it |
| `Update` | command | Track the pointer over a User Interface: hover, press, clicks, slider drags and toggle flips. DrawUi does this too; call it in Step to read clicks before drawing |
| `ValueChanged` | command | True once after the player moves a slider or flips a toggle; reading it clears it |

## Engine.Ui.Focus.Navigation

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Whether the arrow keys, a controller's d-pad and left stick move the focus between a User Interface's elements and Enter, Space or A press them (true unless set); off, a game moves the focus itself |

## Engine.Ui.Scale.Auto

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Whether User Interfaces stretch to fill the window (true unless set); off, they are drawn at their design pixels, times Engine.Ui.Scale.Value, anchored to the window's sides, crisp on a wide or large screen |

## Engine.Ui.Scale

| Name | Kind | What it is |
|---|---|---|
| `Value` | property | How large User Interfaces are drawn, 0.25 to 4 times (1 unless set): a player's text-size setting; what is anchored to a side of the window stays there |

## Engine.Variable

| Name | Kind | What it is |
|---|---|---|
| `Get` | command | Get a numeric script variable by name |
| `GetBool` | command | Get a boolean script variable by name |
| `GetText` | command | Get a text script variable by name |
| `Set` | command | Set a numeric script variable by name |
| `SetBool` | command | Set a boolean script variable by name |
| `SetText` | command | Set a text script variable by name |

## Engine.View

| Name | Kind | What it is |
|---|---|---|
| `Active` | property | Reads (ViewGetActive) and sets (ViewSetActive): Currently active viewport slot |
| `Count` | read only | Reads (ViewGetCount): Number of viewport slots in the current room |
| `Find` | command | The viewport slot with this name ("DS Top"), or -1 |
| `Get` | command | Get a numeric viewport property by name |
| `GetActive` | command | Currently active viewport slot |
| `GetBoundsH` | command | Visible world height |
| `GetBoundsW` | command | Visible world width |
| `GetCount` | command | Number of viewport slots in the current room |
| `GetEnabled` | command | Whether a viewport is rendered |
| `GetFollow` | command | Object name followed by a viewport |
| `GetFollowBorderH` | command | Horizontal follow dead-zone |
| `GetFollowBorderV` | command | Vertical follow dead-zone |
| `GetFollowSpeedH` | command | Horizontal follow speed |
| `GetFollowSpeedV` | command | Vertical follow speed |
| `GetFov` | command | 3D camera field of view in degrees |
| `GetFrustumFar` | command | 3D camera far clipping plane |
| `GetFrustumNear` | command | 3D camera near clipping plane |
| `GetPitch` | command | 3D camera pitch |
| `GetPosX` | command | Live viewport source X |
| `GetPosY` | command | Live viewport source Y |
| `GetPosZ` | command | Live viewport source Z |
| `GetViewportH` | command | Viewport height in pixels |
| `GetViewportW` | command | Viewport width in pixels |
| `GetViewportX` | command | Viewport left edge |
| `GetViewportY` | command | Viewport top edge |
| `GetYaw` | command | 3D camera yaw |
| `GetZoom` | command | Current 2D zoom |
| `MoveForward` | command | Move a 3D view along camera forward |
| `MoveRight` | command | Move a 3D view along camera right |
| `MoveUp` | command | Move a view along world up |
| `Reset` | command | Reset live position to the room's authored source |
| `Set` | command | Set a numeric viewport property by name |
| `SetActive` | command | Choose the viewport that drives 3D camera commands |
| `SetBounds` | command | Set visible world size |
| `SetBoundsH` | command | Set visible world height |
| `SetBoundsW` | command | Set visible world width |
| `SetEnabled` | command | Enable or disable a viewport |
| `SetFollow` | command | Set the object a viewport follows |
| `SetFollowBorder` | command | Set follow dead-zone |
| `SetFollowSpeed` | command | Set follow speed; -1 snaps |
| `SetFov` | command | Set 3D camera field of view |
| `SetFrustumFar` | command | Set 3D camera far clipping plane |
| `SetFrustumNear` | command | Set 3D camera near clipping plane |
| `SetPitch` | command | Set 3D camera pitch |
| `SetPosX` | command | Set live viewport source X |
| `SetPosY` | command | Set live viewport source Y |
| `SetPosZ` | command | Set live viewport source Z |
| `SetViewport` | command | Set viewport destination rectangle |
| `SetViewportH` | command | Set viewport height |
| `SetViewportW` | command | Set viewport width |
| `SetViewportX` | command | Set viewport left edge |
| `SetViewportY` | command | Set viewport top edge |
| `SetYaw` | command | Set 3D camera yaw |
| `SetZoom` | command | Set 2D zoom while preserving viewport centre |
| `Shake` | command | Apply a decaying camera shake |

## Engine.View.Camera

| Name | Kind | What it is |
|---|---|---|
| `Zoom` | property | How far the active view is zoomed in, 0.01 to 100 (1 unless set) |

## Engine.Water

| Name | Kind | What it is |
|---|---|---|
| `DepthAt` | command | Water depth above a world point, or 0 outside water |
| `Enabled` | property | Whether the room's water is there: drawn, and lifting and slowing bodies in it (true unless set); off for a drained lake or a dream |
| `FlowX` | command | Water current X velocity at a point |
| `FlowZ` | command | Water current Z velocity at a point |
| `IsSwimmable` | command | True when a point is inside a swimmable water volume |
| `Reflections` | property | Enable one half-resolution planar reflection for the dominant water plane; software uses sky fallback |
| `SurfaceAt` | command | Authored water surface height, or terrain height when no water exists |

## Engine.Water.Reflections

| Name | Kind | What it is |
|---|---|---|
| `Distortion` | property | How much the water's waves ripple its reflection, 0 to 5 times the usual (1 unless set): 0 for a still mirror |
| `Enabled` | property | Water mirrors the scene above it (off unless set; a half-resolution pass) |
| `Resolution` | property | The share of the view's size the water's reflection is drawn at, 0.25 to 1 (0.5 unless set): sharper at 1, cheaper at 0.25 |

## Engine.Water.Surface

| Name | Kind | What it is |
|---|---|---|
| `Clarity` | property | How far you see into the water, 0 (opaque) to 1 (clear) (each body's own unless set; 0.5 for new water): murky after a storm, clear in a spring |
| `DeepColor` | property | The water's colour where it is deep, as "#rrggbb" (each body's own unless set) |
| `FoamWidth` | property | How wide the foam is where the water meets the shore, 0 to 10 metres (each body's own unless set; 1.25 for new water) |
| `ShallowColor` | property | The water's colour where it is shallow, as "#rrggbb" (each body's own unless set) |
| `Waves` | property | How high the water's waves rise, 0 to 5 metres, on every water body of the room's terrains (each body's own unless set; 0.08 for new water) |
| `WaveSpeed` | property | How fast the water's surface moves and flows, 0 to 10 (each body's own unless set; 0.35 for new water) |

## Engine.Water.Surface.Foam

| Name | Kind | What it is |
|---|---|---|
| `Enabled` | property | Whether foam is drawn where the room's water meets the shore (true unless set); off keeps each body's foam width for when it comes back |

## Engine.Water.Swimming

| Name | Kind | What it is |
|---|---|---|
| `Buoyancy` | property | How strongly the room's water lifts bodies, 0 to 10 times each water's own (1 unless set): 0 and things sink like stones, 2 for a salt lake |
| `Drag` | property | How strongly the room's water slows bodies moving in it, 0 to 10 times each water's own (1 unless set): more for a swamp, less for a fast river |

## Engine.Weather

| Name | Kind | What it is |
|---|---|---|
| `Auto` | property | The weather changes by itself through the day (the room's choice unless set); setting a preset turns it off |
| `LightningFlash` | command | From 0 to 1: the light a lightning strike is adding to the scene right now |
| `LightningStrikes` | command | How many times lightning has struck since the room began; compare with the last value to react to a strike |
| `Preset` | property | The weather: "clear", "overcast", "wind", "rain", "thunderstorm", "snow", "hail", "fog" or "coldsnap"; setting it changes over Engine.Weather.Transition seconds and stops automatic weather ("clear" unless the room sets another) |
| `Release` | command | The wind, rain, snow, lightning and temperature a script set follow the weather again, and every weather part is on |
| `StrikeLightning` | command | Make lightning strike now at this distance, whatever the weather; thunder follows. The room must draw its own weather |
| `Transition` | property | How long a change of weather takes, 0 to 600 seconds (20 unless set) |

## Engine.Weather.Lightning

| Name | Kind | What it is |
|---|---|---|
| `Amount` | property | How often lightning strikes, 0 to 1 (the weather's unless set: 0.85 in a thunderstorm, 0 otherwise) |
| `Enabled` | property | Lightning in storms (true unless set); a script's own strikes still happen when off |

## Engine.Weather.Rain

| Name | Kind | What it is |
|---|---|---|
| `Amount` | property | How hard it rains, 0 to 1 (the weather's unless set: 0 in clear weather, 0.72 in rain) |
| `Enabled` | property | Rain (true unless set); off, no rain falls whatever the weather |
| `Wetness` | property | How wet the ground and surfaces are, 0 to 1 (0 unless set); rain wets them and warmth and wind dry them from there |

## Engine.Weather.Snow

| Name | Kind | What it is |
|---|---|---|
| `Amount` | property | How hard it snows, 0 to 1 (the weather's unless set: 0.78 in snow) |
| `Cover` | property | How much snow lies on the ground and surfaces, 0 to 1 (0 unless set); snow builds it and warmth melts it from there |
| `Enabled` | property | Snow (true unless set); off, no snow falls whatever the weather |

## Engine.Weather.Temperature

| Name | Kind | What it is |
|---|---|---|
| `Celsius` | property | The air's temperature at sea level, -60 to 60 degrees (the weather's unless set: 18 in clear weather); below 0 snow lies and above it melts |

## Engine.Weather.Wind

| Name | Kind | What it is |
|---|---|---|
| `Direction` | property | The way the wind blows towards, 0 to 360 degrees: 0 along +Z, 90 along +X (the weather's unless set) |
| `Enabled` | property | Wind (true unless set); off, the weather's wind is still whatever the weather |
| `Strength` | property | The wind's speed, 0 to 60 metres a second (the weather's unless set: 1.2 in clear weather, 11 in a thunderstorm) |
