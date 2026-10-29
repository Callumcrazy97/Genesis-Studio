# PGSL and Engine API test coverage (29 October 2026)

What the headless suites cover of PGSL and of `Engine.<Area>.<Part>.<Value>`, what had no case before this
pass and has one now, and what no suite can see without a window or a renderer. The final pass runs
`Build.bat --quick --test pgsl` and `Build.bat --quick --test engine-api` (targets in `HeadlessTestRunner.Tiers`,
described in `Documentation/BuildProfiles.md`).

## How the new cases are made

- **Engine API: from the live registry, not from a list.** `EngineApiModel` splits every name `PgslApi.Reference()`
  returns into area, part and value. `EngineApiSuite` runs one case per area and per part that has a value
  (`Engine.Api.Areas.<Area>.<RoundTrip|RoundTripAndEnabled|ReadsWithoutError>`, `Engine.Api.Parts.<Area>.<Part>...`), so a part
  added later is covered by its first run. In a probe room without a window (`EngineApiGame`: scene, physics, climate,
  input, a sound system that remembers bus volumes, a terrain with a lake, grass and stones, a probe instance with a body,
  model, sprite, particle effect and drawing surface) each case, for every settable value:
  reads the default in a script, sets up to four values (inside the range its description gives, then beside the default;
  a flag's opposite; each of a text value's choices), reads each back, sets it back and checks it reads its default again,
  then does the same through the Inspector's path (`EngineSettings.Apply` and `TryRead`).
  Where the area or part has an `Enabled`: it is switched false and true, a part turned on must turn its parent on,
  and where the engine's own state can be seen without a renderer (lighting, shadows, fog, AO, bloom, depth of field,
  free fly camera, dynamic resolution, automatic detail) that state must follow.
- **PGSL: the same script by the interpreter alone and with the compiled tier.** `PgslTierSuite` runs 15 scripts twice
  (`PgslCompiledTier.Enabled` off, then on) with every function called well past the 40 calls that make it hot, and compares
  every result variable. `PgslFeaturesSuite` asserts the semantics of the features themselves (the interpreter's answer
  that the tier is compared to).

## PGSL features

| Feature | Semantics asserted (interpreter) | Interpreter against compiled tier |
|---|---|---|
| Compiled tier (`PgslCompiledTier`) | existing: `Engine.Pgsl.Logic.HotNumberFunctionsRunCompiled`, `FunctionsReadingInstanceNumbersRunCompiled`, `FunctionsGivingTextToCommandsRunCompiled` | `Engine.Pgsl.Tier.SameResultInterpretedAndCompiled.*` (15 scripts; 25 of their 29 functions compile, all 15 give identical results), `TheTierCanBeTurnedOffAndOnAgain` |
| Operators, `i++`, `i--`, `+=`, `*=` in `for`, `break`, `continue`, `repeat`, recursion | existing `Engine.Pgsl.Logic.Operators/ControlFlow`, `TheCheckerTakesIncrementsInForClauses...`; new `Engine.Pgsl.ForLoops.StepsWithIncrementsDecrementsAndCompoundAssignments` | `...Operators`, `...Loops`, `...Recursion`, `...DivisionAndModulo` |
| Cached scripts on disk (`ScriptCompileCache`) | existing `Engine.Pgsl.ALargeScriptIsReadBackCompiledOnTheNextLoad`, `Runtime.PGSL.Cache.*` | `Engine.Pgsl.Tier.ALargeScriptReadBackFromTheCacheRunsTheSameBothWays` (fresh, read back; interpreted, compiled) |
| Typed grids (`DsGridCreate(w, h, kind)`), `DsGridCopyRegion` | existing `Engine.Pgsl.Logic.Compact grids`, `Engine.Pgsl.VmSpeed.GridKinds`; new `Engine.Pgsl.TypedGrids.EachKindKeepsItsRangeAndCopiesBetweenKinds` | `...TypedGrids` |
| `DsListInsert` with text (2a26c75) | new `Engine.Pgsl.Lists.InsertTakesNumbersAndTextAndKeepsTheOrder` | `...ListsWithText` |
| Numbers from one text (`DsListAddNumbers`, `NumListAddNumbers`) | existing `Engine.Pgsl.ManyNumbersAreAddedFromOneText`; new `Engine.Pgsl.Lists.NumbersFromOneTextMatchNumbersAddedOneByOne` | `...ListsWithText` |
| Strings counted from 1 | new `Engine.Pgsl.Strings.PositionsAndIndexesCountFromOne` (was only reported by `Engine.Pgsl.Logic.Strings`) | `...StringsFromOne` |
| `Choose` | existing `ChooseAndListCreateShareOneArrayAndStillWork`; new `Engine.Pgsl.Choose.DrawsEveryOptionAndNothingElseAndRepeatsWithASeed` | `...RandomAndChooseWithASeed` |
| Event variables (a top-level `var` belongs to the instance until Cal decides ST3) | new `Engine.Pgsl.EventLocals.AVarInAnEventBelongsToTheInstanceTodayAndAFunctionsOwnDoesNot` | `...InstanceVariables` |
| `Engine.*` in a function, short circuits, text keys, limits | existing `EngineApi` group | `...EngineApiInAFunction`, `...ShortCircuits`, `...TextKeysInAMap`, `...TheInstructionLimit`, `...TheRecursionLimit` |
| Command auto-testers | `Runtime.Pgsl.CommandAutoTest`, `Engine.Pgsl.Logic.EveryCommandIsCallable` (PGSL commands only) | new `Engine.Api.Commands.*`: every PGSL and every Engine command has a name, signature (naming the command), description and category; both sweeps report each one with its signature; the Engine sweep had no case at all |

## Engine API

52 areas, 79 parts, 1757 names, 478 properties of which 406 can be set (registry of 29 Oct 2026). Before this pass,
cases touched the parts in `PgslEngineApiChecks` (Lighting Reflections/Bounce/Probe/Sun/Toon, Rendering Fog/Shadows/Detail,
Camera Orbit, Audio Acoustics, Game Paused/Threads, Weather, Tile layers, Water Surface/Swimming/Foam/Reflections,
Terrain Edit/Grass/Scatter/Collision, Particle Emitter/Bursts, Navigation Mesh/Avoidance/Agent, Instance Enabled, Physics
body values with `with` and `for`, Image/Instance values), `EngineSettingsChecks` (Inspector, rooms, F6) and the area
suites (`EngineSystemsSuite`, `EngineAdditionsSuite`): about 60 values by name. Every other area and part had no case.
Now every area and part with a value has one, and so do all 406 settable values:

| Area | Parts | Read back as set | Kept in range | Text left as it was | Read their default (named in the page) | Parts and area with an Enabled |
|---|---:|---:|---:|---:|---:|---:|
| Animation | 0 | 1 | 0 | 0 | 1 | 0 |
| Audio | 4 | 15 | 1 | 2 | 0 | 4 |
| Camera | 5 | 27 | 0 | 0 | 7 | 4 |
| Debug | 1 | 0 | 0 | 0 | 1 | 1 |
| Decal | 0 | 0 | 0 | 0 | 1 | 0 |
| Display | 5 | 6 | 0 | 1 | 13 | 2 |
| Draw | 0 | 4 | 0 | 0 | 2 (+3 known defects) | 0 |
| Game | 1 | 4 | 0 | 0 | 0 | 0 |
| Image | 0 | 11 | 0 | 1 | 1 | 0 |
| Input | 2 | 5 | 0 | 0 | 2 | 0 |
| Instance | 1 | 23 | 0 | 1 | 0 | 1 |
| Language | 0 | 0 | 0 | 1 | 0 | 0 |
| Lighting | 7 | 22 | 0 | 2 | 4 | 4 |
| Mesh | 1 | 1 | 0 | 0 | 1 | 0 |
| Model | 2 | 13 | 0 | 1 | 0 | 0 |
| Navigation | 3 | 11 | 0 | 0 | 0 | 2 |
| Network | 1 | 0 | 0 | 0 | 1 | 0 |
| Particle | 3 | 13 | 0 | 0 | 0 | 4 |
| Physics | 4 | 39 | 1 | 0 | 2 | 2 |
| Rendering | 12 | 57 | 2 | 2 | 1 | 5 |
| Room | 2 | 4 | 0 | 1 | 1 | 0 |
| Sky | 5 | 33 | 0 | 2 | 0 | 2 |
| Terrain | 5 | 15 | 1 | 0 | 0 | 3 |
| Tile | 1 | 2 | 0 | 0 | 1 | 2 |
| Ui | 3 | 4 | 0 | 0 | 0 | 3 |
| View | 1 | 2 | 0 | 0 | 0 | 0 |
| Water | 4 | 13 | 1 | 0 | 0 | 3 |
| Weather | 5 | 14 | 0 | 1 | 0 | 4 |
| Totals | 79 | 340 | 6 | 15 | 39 | |

(The commands-only areas, Alarm, Array, File, Global, Grid, Job, Json, List, Map, Math, Noise, NumList, PostEffect,
Priority, Queue, Recording, Resource, Save, Stack, Shader, Text, Texture, Time and Variable, have no values to sweep; their
commands are covered by the PGSL suites and the command sweeps. Totals are 406 settable values: 340 + 6 + 15 + 39 + 3 known defects + 3 skipped.)

**What the sweep reported without failing on the first pass** (the numbers below are that pass's; "Found by the first pass,
closed on 29 October" further down says what changed since, and the page the sweep writes says what stands now):

- **No Enabled yet:** 58 of the 131 areas and parts that have values (17 areas, 41 parts; the list is `ScopesWithoutEnabled` in
  `EngineApiSuite.cs`). The standard gives every area and every part an `Enabled`, so a new one without it fails
  `Engine.Api.Registry.EveryAreaAndPartHasAnEnabledOrIsAKnownGap`; one that gains it is noted so it can leave the list.
- **Values that read their default whatever is set** (39, `KnownSilent`): 20 need a window, the monitor or the mouse
  (Display.Width, Height, Fullscreen, VSync, CursorVisible, MaxFps, Window.*, Monitor.Index, Input.Mouse.Captured,
  Input.MouseCaptured), a character controller (Physics.Character.*), a network (Network.Replication.Rate), a 2D room's
  tile layers (Tile.Enabled) or a game started to be worked on (Debug.Keys.Enabled); 19 are old `SetX` commands that became
  properties with a setter and no getter, so they read 0 or their default whatever was set: Animation.Loop,
  Camera.CollisionEnabled, FreeFly, OrbitDistance, OrbitEnabled, OrbitMouseLook, OrbitPitch, OrbitYaw, Decal.Limit,
  Draw.FontSize, Draw.TextTracking, Image.Direction, Lighting.ColorCount, Falloff, Intensity, Radius, Mesh.UploadBudget,
  Rendering.AutoExposureMetering, Room.Speed. Any other value that stops reading back what is set fails its case.
- **Engine defects** (`KnownDefects`): Engine.Draw.Font, Engine.Draw.TextAlign and Engine.Shader.Name are set with text and
  read as numbers.
- **Enabled that does not switch here:** Debug.Keys.Enabled (the debug keys act only in a game started to be worked on) and
  Tile.Enabled (a 2D room's tile layers); the other 45 areas and parts with an Enabled read false after false and true after
  true, and the page says so per part (`engine-api-parts.md`, written beside the captures).
- **Enabled with an effect seen in the engine's own state:** Lighting, Rendering.Shadows/Fog/Ao/Bloom/Detail,
  Camera.Focus/FreeFly, Display.Resolution.Dynamic.

**Still no case that can see an effect:** anything drawn (the effect of Fog, Bloom, Grading on a picture is in the renderer
suites), the window and monitor, sound output, networking, 2D tile layers and views (`Engine.Tile.Enabled`, `Engine.View.*`
need a 2D room: `Engine.Pgsl.Logic.EngineApiTileLayersUpdateDistanceAndFrameCosts` covers tile layers by name).

## Counts

Run on 29 Oct 2026 through `agent-test.ps1` (`Build.bat --quick --test <target>`) in a worktree:

| Target | Cases before | Added | Now | Passed | Time of the cases |
|---|---:|---:|---:|---:|---|
| `engine-api` | 29 (`pgsl-engine-api` 16, `engine-settings` 3, `debug-screen` 10) | 118 (`Engine.Api.*`: 108 areas and parts, 3 registry rules, 4 command lists, the probe room, the page, F6) | 147 | 147 | about 20 s |
| `pgsl` | 181 | 24 (17 `Engine.Pgsl.Tier.*`, 7 `Pgsl.*` feature cases) | 205 | 203 | 9 min under load: the 11 export cases took 471 s of it, the other 194 cases about 72 s |

The 16 EngineApi cases are in both targets (they are in `pgsl-logic` too), so the two targets hold 336 different cases,
142 of them new. The two `pgsl` failures are older cases that fail in a fresh worktree, not new ones: 
`Engine.Pgsl.PreparingAScriptTwiceChangesNothing` needs the 181 starter-template Scripts that the worktree does not have
(they are not in git; it found 8 sources, not over 100), and `Export.PgslOnly.FreshCopiesStartEveryTime` ran its exported game
65.7 s to start on a machine every session was using (the start-up trace shows 15 s in "HUD and hooks").

New cases alone: `--test engine-api-parts` (118 cases, 1.8 s of case time), `--test pgsl-tier` (17, 0.14 s),
`--test pgsl-features` (7, 0.03 s), `--test pgsl-engine-api` (16 existing, 1.6 s).

## Found by the first pass, closed on 29 October (second pass: the engine API gaps)

- Engine.Draw.Font, Engine.Draw.TextAlign, Engine.Shader.Name read back the text they were set with
  (`DrawGetFont`, `DrawGetTextAlign`, `ShaderGetName`); `Engine.Api.Getters.DrawFontTextAlignAndShaderNameReadBackTheTextSet` keeps it so.
- The values with a setter and no getter have getters: Animation.Loop, Camera.CollisionEnabled, FreeFly, OrbitDistance,
  OrbitEnabled, OrbitMouseLook, OrbitPitch, OrbitYaw, Draw.FontSize, Draw.TextTracking, Lighting.ColorCount, Falloff,
  Intensity, Radius, Mesh.UploadBudget, Rendering.AutoExposureMetering, Image.Direction (and Display.CursorVisible,
  Input.MouseCaptured, which the coverage page had listed among the window's). Decal.Limit and Room.Speed had one already: the
  first reads back (it only sat in the list), the second needs a window. Still read as their default in the probe room, each
  with its reason in `KnownSilent` (22 values in all): the window and monitor values, the mouse capture, a character
  controller's step height and slope limit, the network's replication rate, the debug keys, a 2D room's tile layers, Room.Speed,
  Image.Direction (needs a directional Image; `Engine.Pgsl.SpriteDirections` checks `SpriteGetDirection`) and
  Audio.Snapshot.Enabled (true only while a blend moves; only false can be set).
- The values merged since (the mixer's effects on Engine.Audio.Music and Effects, Engine.Camera.Rig and Rig.Path, Engine.Debug.Overlay.Wireframe):
  the probe room's sound system remembers bus effects, a script makes a camera rig in it first (the rig's values are not
  Inspector settings: `EngineSettings` leaves them out, and the registry rule and the Inspector round trip say so), and
  Wireframe keeps what is asked before the debug screen exists, as the screen does.
- Enabled on the areas and parts that had none: 28 got one, 17 are listed as not being switches with the reason
  (`ScopesWithoutEnabledByDesign`), 14 are left (`ScopesWithoutEnabled`, with the reason for each). The list is in
  `Documentation/Design/EngineApiAreas.md`.
- The ten PGSL commands whose signature showed no argument list (Engine.Rendering.AutoExposure, Engine.Sky.SetAmbientTint,
  SetAmbientGroundTint, SetAtmospherePreset, SetCloudCoverage, SetHaze, SetLatitude, SetTimeOfDay,
  Engine.Terrain.Water.ClearHoles, PunchHole) show them, and the suite asserts it.

## Found, not fixed (the tests own no engine code)

- Interpreter and compiled tier word the same fault differently: the recursion limit ("Too much recursion: 'F' is 200 calls
  deep" against "Function 'F' called itself too deeply (more than 200 calls)") and the line the instruction-limit error names
  (the interpreter says line 0, the compiled one the caller's line). Their results are identical in all 15 scripts.
- The compiled tier leaves a function that writes `Engine.*` properties interpreted ("writes 'Engine.Image.Frame', not its own
  variable"); the results agree, it only costs speed.
- `Engine.Instance.SpriteIndex` read on an instance with no sprite gives nothing rather than empty text.

