# Genesis Studio against Unity: to-do list, open requests and what fits before 30 October

Written 28 October 2026, 23:00, for Cal and for the AI Team run from 29 October 09:00. Sources:
`Documentation/Design/ProductionReadiness.md` (the engine-by-engine table, 24-25 Oct),
`Documentation/Design/UnityGapAudit.md` (19 Oct), `.claude/agent-tools/COMPLETION-PLAN.md` (22 Oct),
the engine requests in Golden Stag, Aetherium and GenesisCraft, the 28 Oct task ledger
(`.claude/task-ledger.json`) and the dated to-dos. Status was checked against git where a name could
be found; where it could not, the row says **verify** (check the code before starting).

Left out, as asked: anything that needs a user base (asset store, cloud services, analytics, crash
service, ratings). Also left out: things Cal decided against (plugins, starter screens, a two-level
settings picker, ReadyToRun), shown at the end so they can be revisited.

**Sizes:** S = under an hour; M = 1 to 4 hours; L = 4 to 12 hours, for a first version like the 22
October phases; XL = several days. **Checks** run one at a time on the one GPU: a tier run takes about
20 minutes, a Full Build about 30.

## 1. Where Genesis stands

For one developer making a 2D or 3D game on Windows, Genesis already has most of what Unity offers:
- **World and look:** scenes (rooms), prefab-like Objects and nesting, an Inspector, terrain with
  grass, scatter and water, PBR with shadows, automatic reflections and bounced light, a post stack
  with TAA, depth of field, render scale and dynamic resolution.
- **Characters and play:** physics with characters and joints, animation controllers with blend
  trees, sync groups and IK, navigation with agents, the UI toolkit, input actions with rebinding,
  audio buses with acoustics.
- **Systems:** LAN and relay multiplayer, saves, jobs, a profiler and debugger, C# and PGSL, export
  and an installer.

The big gaps are:
- **Platforms:** Genesis builds for Windows only.
- **Baked lighting:** no lightmaps, and no probes placed by hand.
- **Node-based tools:** no shader graph or VFX graph.
- **Timeline:** no cutscene timeline (declined).
- **Netcode prediction and server authority.**
- **A polygon navmesh:** navigation uses a grid.
- **Cloth and XR.**

## 2. Tomorrow's to-dos (dated 29 October)

| # | Item | From | Size | Notes |
|---|---|---|---|---|
| T1 | **Built-in meadow grass** as the Terrain editor's default grass, **a grass shader** (base-to-tip shading and colour; today grass colour comes only from the species material, no tint), and **template options** in the Terrain editor: Terrain, Foliage (Grass now; flowers, bushes later), Water (later) | Cal, 28 Oct | L | Reference picture and exact settings: `.claude/agent-tools/grass-reference-2026-10-28.png`, memory `genesis-todos`. Overlaps GS4. Then flowers (today "floating cubes"), terrain material (bleak) and trees ("three.js placeholders") to the grass's standard |
| T2 | **Black hard-edged areas in lit interiors**: probe-captured bounced light replacing ambient with black (Nuvema, fixed game-side 28 Oct night), Golden Stag's hall (black cross over the hearth, black back walls) | Golden Stag, Nuvema | M-L | Engine fix: a floor or fallback when the probe is dark; check light culling by cluster (no light dropped while its radius covers visible pixels); soft PCF point/spot shadows; a project ShadowFloor; a culled-lights stat. **Engine side merged 29 Oct (4f11cec):** the light-cluster list has room for every cluster to be full (a hall of lamps no longer loses every lamp past some depth); a lamp that cannot reach the view takes no shadow slot; the probe is recaptured when the lamps around the camera change; bounced light and probe reflections never drop below 35% of the room's ambient (`Engine.Lighting.Bounce.Floor`); F6 shows lights culled, lights out of view and the shadow distance. **Still open:** the pure-black band by the Golden Stag hearth (3,3,4) is unchanged with lamp shadows and probes off, so it is neither; next is an F6 pick and the hall's post effects. **C2 lead:** in Nuvema the sea and pond are dark flat navy on D3D11 but bright on Vulkan in the same build, being followed up |
| T3 | **Pennywick at ~8 FPS from Studio's Play** (21:43-21:48, 28 Oct) | 2D Adventure session | M | Most likely the engine session's own publish compiling at the same time. Re-measure on a quiet machine: Studio Play (supervised) vs --benchmark; Vulkan 15-30% slower than D3D11 on this scene (Cal's Studio preference is Vulkan); the 350-700 ms first-frame hitch; the 3 GB footprint |
| T4 | **Publish the 3D-view scaling fix** (59bf7c0: 3D views now grow with the window and F11) | Nuvema, 28 Oct night | S | Built and validated; not promoted because Studio was open. `.\Build.bat --quick --no-track` with Studio and games closed |
| T5 | Nuvema fixes to confirm by Cal: black blocks (bounce/reflections off), name entry by WASD/pad, resizing | 28 Oct night | S | Cal tests |
| T6 | **First-person right-stick sign**: changed 28 Oct to "away is +", but the Golden Stag note says Cal's pad reports right stick up as -Y | 28 Oct | S | Ask Cal which way it looks, then fix the FirstPerson recipe |
| T7 | Look gaps the blind trial found: pale turquoise water, flat sky with no sun glow, one looping birdsong (want random panned chirps) | 28 Oct | M each | Judge side by side with `BuildTools/Benchmarks/NatureWalk/reference-threejs.html` |
| T8 | Second blind trial on a *different* small scene, with the three.js version written on the same clock | Task Checker | M | The Nature Walk batch now ships, so repeating that one would measure copying |
| T9 | Cal times making the Nature Walk in Studio | Task Checker | Cal | The one measurement only a person can make |
| T10 | Automation while Studio has the project open: a room order (or start room) saved by `room.create start:true` is overwritten by the window's next save | 28 Oct review | S-M | `ProjectRoomOrder.OrderSaved` is in-process only; reload the manifest on file change |

### Added by Cal, 28 October 23:30

| # | Item | Size | Notes |
|---|---|---|---|
| C1 | **Resource tree: opening (expanding) a folder opens a resource Cal did not ask to open** | S | **Done 28 Oct night (2a26c75), Cal to confirm:** a double-click opens only the resource its first click pressed |
| C2 | **Nuvema's black blocks are still there** after Bounce and Reflections were turned off every frame (Hud/Step.pgsl, 28 Oct night), so that diagnosis was wrong or incomplete | M | Cal's Studio renders on **Vulkan** (`preferences.json` backend Vulkan, reversed depth on); the earlier Nuvema checks probably ran on D3D11. Compare the bedroom on D3D11 and Vulkan with `game.run` pictures first, then follow the difference (depth, a texture or descriptor not bound, the decal pass, the probe capture's state). Shares a look with T2/GS6 |
| C3 | **Live texture painting in the Model editor's edit mode**: paint and edit a model's textures on the model, live | L | First version: brush on the model in the viewport (ray to triangle, UV, write the texture, re-upload), undo, save to the material's image; reuse the Image editor's brushes. **First version merged 29 Oct (1357915):** Texture page → Paint / Erase, live in the 3D view, one undo per stroke, Save writes the Images, `Documentation/Design/ModelEditor.md`. Open: the Inspector's Albedo thumbnail stays blank right after a new Image is made; the built-in cube's faces share one picture; frame 1, one layer and Albedo only |
| C4 | **MIDI studio in the Audio editor** (GarageBand-like): import and edit MIDI files, or make one from nothing; tracks with instruments; notes placed and sized on a key grid (piano roll); a volume (velocity) per note; play and render to an Audio resource | L | PyGenesis had it: `F:\Development\System Development\Python\PyPlatform\New PyGenesis\Editors\Sound\core\PyGroove.py` (the editor), `Runtime\Audio\MidiUtils.py`, `MidiSynth.py`, `MidiPlayer.py`, `meltysynth.py`, `MuseScore_General.sf2`. Cal prefers no third-party tools unless they can legally be shipped. A MIDI reader and writer is ours to write (a simple format). For instruments, either Genesis's own synthesiser presets, or a SoundFont player: MeltySynth is a C# library and MuseScore General a SoundFont, both believed MIT; **verify both licences**, add them to `ThirdPartyNotices.txt`. **First version merged 29 Oct (4264de4):** a Music resource (`.music.json`, Assets\Music) opens in the Audio editor's Music mode: tracks with instruments (Genesis synth presets, General MIDI mapping, channel 10 = drum kit), piano roll with velocity lane, snap, loop range, one undo per edit, play, and render to `<piece> Audio`; our own MIDI format 0/1 reader and writer; `Documentation/Design/AudioEditor.md`. Licences verified (MeltySynth MIT, MuseScore General MIT) but not added: the SoundFont is 208 MB, so instruments are the synthesiser's; ThirdPartyNotices unchanged. Open: pan, per-track effects, pitch bend, editing tempo and time-signature changes, live edits while playing, an `audio.music` automation command |
| C5 | **Node-based shader editor and effects (VFX) editor**: Cal says they fit in the window | L each, first versions | Shader: a node graph in the Shader editor that writes the same HLSL the code view uses (inputs, maths, textures, time, output to colour, normal, emission, alpha), for model, terrain, image and full-screen shaders. Effects: a node or stack editor over the existing GPU particles (spawn, initialise, update, render modules). **Shader node editor, first version merged 29 Oct (2a1d948):** a Nodes page in the Shader editor (canvas with pan, zoom, a searchable add menu, wires with type and loop checks, one undo per edit); the graph is saved in the shader document (`graph`, null for code shaders, ignored by old players) and writes the HLSL the code view uses: Model and Terrain get a surface shader, Image, Particle and Full screen a pixel shader; `shader.graph` and `shader.nodes` automation commands; `shader-nodes` test target; README "Node view", Cookbook "Make a shader from nodes". Also fixed: a Model shader sampling its own texture previewed black on a model. Open: no vertex stage (next for the same engineer), groups, comments, sub-graphs, copy and paste; object position falls back to world position in instanced batches (engine request: `ObjectPosition` in `GenesisSurfaceInput`). **Effects editor, first version merged 29 Oct (bed268d):** a module stack over the existing particle settings (27 modules in Spawn, Initialise, Update and Render groups; cards with on/off, constant or random-range fields, a shared point-curve editor, a colour-over-life gradient, Reset, Add menus; Restart, Pause and a scrub bar; an emitter chooser for layered effects); point curves (`keys`, straight or smooth, never overshooting) replace the Bezier wherever sampled (GPU table, CPU simulation, 2D room effects); `modules` saved per emitter, both omitted when null; `particle.module` automation command; `particle-modules` target, 7 cases, particle-workbench 83/83; docs in `Hotfix21_Particle_Workbench.md`, Automation, Cookbook. No drag handles because the GPU simulation applies forces in a fixed order; hidden for lack of a runtime setting: soft particles, sorting, attract to a point, rate over time, random start size; Duration is "Preview only" |
| C6 | **Lightmaps baked when the game is built** | XL for the baker | Yes, in Unity's way: baked in Studio (on demand, or before Export or Run when static geometry or lights changed, cached by a hash of what affects them) and shipped with the game as images; a game never bakes. The baker itself is the large part: a second UV set (unwrap), direct light plus bounces on the GPU, denoising, mixed lights. Placed light and reflection probes come first (section 4) |

| C7 | **Shadows and other effects only appear close up** (Cathedral Test, Cal 28 Oct 23:45; "affects every game"). From the pews the candle stand casts no shadow on the wall and its reflection in the marble is missing; close to it both appear (the wall shadow, the mirrored stand). Pictures: `.claude/agent-tools/cathedral-far-no-shadows-2026-10-28.webp` and `cathedral-near-shadows-2026-10-28.webp` | M-L | Likely suspects: lamp (point/spot) shadows given only to lights near the camera, or a short lamp-shadow distance; shadow casters culled by distance or size; screen-space or planar reflections with a short range; contact shadows standing in for real ones. Probably the same family as T2/GS6 (Golden Stag's black hall) and C2. Fix so shadows and reflections fade out smoothly with distance and quality settings, never pop in at a few metres. **Cal (28 Oct 23:55): this is old and basic to the engine**: his Ember-based Minecraft game about four months ago, before all of this work, had shadows and lighting only within about 8 blocks. So start from the oldest defaults, not recent features: `RoomEnvironment.ShadowDistance` defaults to 0, documented as "the close-range shadows suited to small scenes" (find what that range is and make the default follow the view distance, with cascades); the default range and culling of lamps and point lights; whether the forward pass lights a surface only within a fixed distance. Block 1. **Merged 29 Oct (4f11cec):** each sun cascade is fitted to a sphere round its slice of the view and snapped to shadow-map texels (also C10-3), the bias is the same few texels in every cascade (the old one put far shadows tens of metres off); shadow distance follows the camera's far plane up to 300 m over 3 cascades, fading over the last 15% (room fields `shadowCascades`, `shadowFade`; `Engine.Rendering.Shadows.Fade`; `ShadowDistance` 0 = follow the view); casters outside the view still cast (Objects, static model and mesh layers, voxel chunks); lamp shadows 4 by default, up to 8 (quality tiers 0/2/4/8, saved 1 migrated to 4), bias in metres, 12-tap edges, fade to 60 m; contact shadows skip pixels a project shader lit itself. Pictures: Cathedral pews now shadowed at mid-distance, the corridor's far floor no longer black. Not pictured yet: GenesisCraft after the change. Expect goldens to move at the final pass and a little more CPU from casters kept outside the view |

| C8 | **Z-fighting in every game** (Cal 28 Oct 23:50). Example: the Cathedral's stained-glass window, where the wall's grey stones break through the bottom of the glass. Picture: `.claude/agent-tools/cathedral-window-zfighting-2026-10-28.png` | M | Check the engine side first, since Cal sees it everywhere: the depth format and reversed depth on each backend (Cal's Studio uses **Vulkan with reversed depth on**: confirm a 32-bit float depth buffer, a 1-to-0 clear, a greater-or-equal compare and the projection's depth range on Vulkan as on D3D11), the default near plane, and depth precision in the shadow and reflection passes. Then give coplanar layers (decals, windows set into walls, see-through materials) a depth bias or draw-order option. Compare the same view on D3D11 and Vulkan. **Cal: it predates the recent work too** (seen months ago), so look at the original depth set-up (depth format, near plane default, projection) as well as reversed depth. Block 1. **Merged 29 Oct (4f11cec):** the scene depth set-up is one shared description (reversed depth, 32-bit float, greater-than test, cleared to 0) on every backend; new opt-in `MeshDrawFlags.InFront` / `Engine.Model.InFront` / `"InFront"` on an Object's ModelRendererComponent draws a coplanar layer a hair nearer the camera (the draw buffer's spare float is now `DepthNudge`). Not yet checked in a picture: the Cathedral glass is game content and does not set InFront; a capture in a test room of ours is next |

| C7b | Another "only so far" example (Cathedral corridor): past a certain distance the marble floor has no reflection and its checks blur together into black. Picture: `.claude/agent-tools/cathedral-corridor-floor-fades-2026-10-28.png` | with C7 | Cal suspects LOD. Also check: the reflection's range or fade (probe, screen-space or planar), texture filtering at grazing angles (anisotropic filtering off, mip bias, mips made with the wrong colour space, which darkens them), and fog or haze tinting to black. Fix it with C7. **Merged 29 Oct (4f11cec):** cooked BC7 colour mips are now averaged in linear light (the old mips darkened with distance); textures already cooked keep their old mips until cooked again. Picture: the corridor's far floor at 15:00 is no longer black and the columns' shadows reach down the hall |
| C9 | **F6 debug screen needs much more** (Cal 28 Oct): today there is no way to see why something looks wrong | L | Wanted: view modes for LOD levels (colour by level), shadow coverage and cascades, reflection source and range, light count and culled lights (overlaps GS6's stat), overdraw, depth and z-fighting; colliders and hitboxes drawn for 2D and 3D objects (bodies, characters, triggers, tile solids); the HUD and GUI layers listed (which UI, which draw calls, which views); and wider reporting: what each object is, its model, material, shader, LOD and why it is or is not drawn. Build it early in block 1, so it helps find C2, C7, C8 and T2 |

| C10 | **Minecraft Test (GenesisCraft) is broken in several ways** (Cal 29 Oct 00:10; he suspects today's work, possibly the game session's own Sonnet 5.5 changes). Pictures in `.claude/agent-tools/minecraft-2026-10-28/` (1-8) | M-L | **Triage first:** engine commits since 26 Oct touched only input replay, the model cache's test counters, the FirstPerson recipe and the 3D-view scaling (not yet published), so start with what the GenesisCraft session changed in the game today (its git log and Documents), then which Player it runs. Then each symptom: (1) **main menu world lighting** very dark (picture 1; **29 Oct: does not reproduce**, the title is bright on D3D11 and Vulkan with and without the shader pack; the picture shows a translucent rectangle drawn over the panorama, source unknown); (2) **script error** `Game.Step line 58: DsListInsert: argument 3 (value) must be a number, but was given the text "w20"` (picture 2; **done 28 Oct night, 2a26c75: DsListInsert takes text or numbers**): an engine bug, since `DsListInsert(id, position, double value)` takes only numbers while lists hold text too, so make it take any value as `DsListAdd` does, and check the other Ds* commands for the same mistake from the typed command calls; (3) **shadows that change with the camera's yaw, pitch and position** (pictures 3-6): stabilise the sun's shadow cascades (fit to a bounding sphere, snap to shadow-map texels) so a shadow never moves when only the camera turns; old engine behaviour, the same family as C7; (4) **water stopped working** (picture 7: water and the trees round it drawn as tall dark-blue columns); (5) **the house is up in the air**, the ground far below it (picture 8): chunk or mesh placement, or the camera's height. **29 Oct, (4) and (5) are one cause, fixed in the game repo (8681bb9b, master):** playing a world below the top of the list, `McWorldToTop` deleted its entry and then hit the DsListInsert text error, so the world restarted with every setting 0 (generation 0, sea at 40 instead of 62); unedited land regenerated 22 blocks lower and 11,012 kelp cells stood above the lower sea. `w20.save` is the 15 Oct save and intact; its entry is back at the top of Cal's list (the file as found is kept beside it). Not 28 Oct's work: the script dates from 11 Oct and the strict argument check from 5-6 Oct. **Cal must re-export Genesiscraft** (the Desktop export carries engine 2a741ab, before the DsListInsert fix). Pictures 3 and 6 were taken in the same misloaded world; GenesisCraft draws blocks with engine shadows off, so (3) is the general cascade work of C7, verified elsewhere | Block 1 |

## 3. Open engine requests from other sessions

| # | Request | Project | Size | Status |
|---|---|---|---|---|
| GS1 | **`resource.delete` deletes other resources whose names start with the same text** (deleting `music_village` removed `music_village_night.wav`) | Golden Stag | S | **Done 28 Oct night (2a26c75):** files named after a longer resource stay with it; add a regression case |
| GS2 | Synthesiser: exponential envelope; high-pass and band-pass with Q; continuous and pink/brown noise; band-limited saw/square; no DC from square duty; stereo and `pan`; a loop render mode; more than 512 layers | Golden Stag | M-L | **Done 29 Oct (b5f8a0f):** exponential envelope; high-pass and band-pass with Q; white, pink and brown noise; band-limited saw and square (no aliasing, no DC from duty); `pan` per layer (stereo output); `LoopSeconds` seamless loop; layer cap 512 → 16,384; `audio.synth` gains `loopSeconds` and writes stereo WAVs; all 30 old presets render bit-identical (fingerprinted) |
| GS3 | Grass: a texture and tint ramp on the blades; wind settings (or a vertex stage in Mesh shaders); grass parting round pushers (`GrassSetPusher`); grass honouring the nature document's `paths` | Golden Stag | L | Open; do with T1 |
| GS4 | Head look-at and hand reach for any entity from C# (`ModelInstance.SetBoneLookAt(world, entity, ...)`) | Golden Stag | S-M | In progress 29 Oct (the camera-rigs engineer, after acce433) |
| GS5 | Sun, sky and shade inputs for post effects (sun direction and colour, horizon/zenith colours in a cbuffer); separate up/down tints for the hemisphere sky light; per-hour sun and fog colours that the climate does not overwrite | Golden Stag | M | Open |
| GS6 | Lighting artefacts in the stone hall | Golden Stag | M-L | = T2 |
| GS7 | Golden Stag's engine snapshot is from 19 Oct (`Tools\update_engine.ps1`) | Golden Stag | S | Game side, when the sessions agree |
| GC1 | **The compiled script tier counts instructions differently from the interpreter** (a loop is charged its whole span at each jump back), so a function hits the 400,000 cap only when compiled | GenesisCraft | S-M | **Bug: correctness** |
| GC2 | Compile the script tier on a worker and switch when ready (two start frames of 6.9 s and 5.3 s spent compiling on the game thread) | GenesisCraft | M | Open |
| GC3 | One 2.9 s frame while flying, cause unknown | GenesisCraft | M | Investigate with GENESIS_PROFILE |
| GC4 | Cold first launch of an export (~30 s, ~10 s after) | GenesisCraft | M | **Verify**: scripts are now cached on disk |
| GC5 | An opt-in way for load-time work to run past the per-call instruction cap | GenesisCraft | S | Open |
| GC6 | Live reload that keeps scripts resolvable; `PlaySound` by resource name | GenesisCraft | S-M | **Verify** |
| AE15 | `DrawText` with a project font costs ~0.16 ms a call (resolves the font path each time) | Aetherium | S | **Verify** |
| AE22 | An unattended run should not read or vibrate the controller | Aetherium | S | **Verify** |
| AE54 | A script's ds_lists and ds_maps belong to the instance that made them | Aetherium | S-M | **Verify** |
| AE55 | A one-off particle burst with a direction | Aetherium | S | **Verify** (`ParticleBurstAt` exists) |
| AE56 | A thin glowing line in 3D (tracers, lightning) | Aetherium | S | **Verify** (`DrawLine3D` exists; glow?) |
| KF1 | Kinfield grass no longer leans away from the hero; dotted path edges at 0.25 m | Kinfield | S-M | Open (22 Oct) |
| KF2 | Terrain paint material: ~2.5 s bake with a stand-in ground, then a ~180 ms / 179 MB upload on the game thread | Kinfield (any terrain with image layers) | M | Cache the bake on disk, make mips off the game thread |
| NV1 | Unlit model materials (glTF `KHR_materials_unlit`) on static meshes; only skinned ones honour it | Nuvema | M | **Closed 29 Oct (e6ddeab):** already fixed since 22 Oct (8c65f1e static, 3d18f28 skinned): the importer reads the flag, every static or skinned mesh with it draws its own colour with no light and no received shadow (fog by the room's rules, still casts); the Model editor has the per-material "Unlit" tick box. Added the missing `unlit-models` regression case (our own glTF quad on D3D11, single and instanced draws, plus a lit control). Note for Nuvema: unlit colours reach the screen exactly only with the room's `toneMapping` off. Found while testing: instanced draws (`SubmitInstances`) drop metallic and roughness factors, ORM and emission maps, emission colour and UV scale/scroll, so a model in a room's static layer can shade differently from a single draw (fix list, renderer) |
| ST1 | Studio's UI editor Inspector lacks Layout, Spacing, Padding, Style, Placeholder, MaxLength | UI toolkit, 22 Oct | S-M | In progress 29 Oct (the test-suite engineer, after the suites merged in 9a9c695) |
| ST2 | Model editor has no control for `hiddenMaterials` | 22 Oct | S | **Done 29 Oct (5431c37):** a Hidden tick box per material in the Model editor, saved to `hiddenMaterials`; editor edits no longer drop the hidden state |
| ST3 | PGSL event locals: what a top-level `var` is (today it belongs to the instance) | 25 Oct | Decision | **Needs Cal's call** |
| ST4 | Vulkan: distant things black in GenesisCraft; vsync fixed at swap-chain creation | GenesisCraft, 12-15 Oct | M | Not reproduced; waiting for Cal's screenshot and `GENESIS_VULKAN_DEBUG=1` log |
| — | Already done, can be closed: Aetherium 72-78 and 80 (sync groups dda015d, characters on the ground 2bc1be5, Create runs first a0e8419); `i++` in for-loops (a0e8419); the 2D GUI letterbox rectangle (7f25187); `SoundPreload`, `DsGridCreate` typed grids, `DsGridCopyRegion` | | | |

## 4. Unity parity: what is left

| Area | Unity has | Genesis has | Gap to close | Size | Before 30 Oct? |
|---|---|---|---|---|---|
| **Platforms** | Windows, Mac, Linux, Android, iOS, Web, consoles | Windows | Linux Player first (it already renders on Vulkan and OpenGL); then Mac (Metal/MoltenVK); mobile and web are a different engine shape | Linux L, Mac XL, mobile/web XL+ | Linux only as a stretch (Cal said "later") |
| **Baked lighting** | Lightmaps, baked GI, mixed lights, probes placed by hand, reflection probe volumes | One automatic probe at the camera (reflections, bounced light); real-time lights | (a) probes and reflection volumes placed in the Room editor, blended by position; (b) a lightmap baker (second UV set, GPU bake, denoise) | (a) L, (b) XL | (a) yes, (b) no |
| **Lamp shadows and light culling** | Soft shadows for many point/spot lights, robust light culling | Lamp shadows; artefacts in Golden Stag | = T2 | M-L | Yes |
| **Shader Graph** | Node graph for surface shaders | Shader editor with presets and code; Mesh shaders pixel-only | Node view generating the same HLSL; a vertex stage for Mesh shaders | Node view XL; vertex stage M | Vertex stage yes; node view no ("later, if wanted"). **Both done 29 Oct:** node view in 2a1d948 (see C5); vertex stage in f871c3a: a surface shader may define `void Vertex(GenesisVertexInput i, inout GenesisVertex v)` (model space, after skinning and the material's displacement, before the world transform; time, world positions, camera, wind and the shader's parameters as inputs), compiled for DXBC and SPIR-V, Wind Sway and Wave presets, the Code page's Vertex button, the node view's Vertex offset output, `MeshShaders.md` "Vertex stage", Cookbook "Things that move". Open: the shadow pass still uses its own programs, so a displaced mesh casts the undisplaced shape's shadow (per-shader shadow programs would key shadow batches by shader across cascades, lamps and skinning); outline marks likewise |
| **VFX Graph** | GPU node particles | Particle editor, GPU particles, presets, bursts | A node or stack editor for GPU particles | XL | No |
| **Timeline / Cinemachine** | Timeline tracks; virtual cameras with blends, dolly and framing | PGSL camera moves and animation events; orbit, shoulder and collision cameras | Camera rigs with blends and dolly paths (M-L); a timeline (L-XL) | M-L / L-XL | Camera blends yes; timeline was declined. **Camera rigs merged 29 Oct (acce433):** named rigs (fixed, follow, orbit, shoulder, lookat, dolly) a script makes per room, switched with cut, linear or ease-in-out blends of position, rotation and FOV; dolly paths from script points or a Pathing resource (straight or smooth, open or looped, 0..1 by distance or at a speed); flat `CameraRig*` commands and `Engine.Camera.Rigs` / `Rig` / `Rig.Path`; `camera-rigs` target (5/5) and pgsl-logic (76/76); Cookbook, GameFeatures, EngineApiAreas. Defaults: Rigs.Enabled true, BlendTime 1, BlendCurve easeinout; rigs reset with the room; rig Pitch is degrees looking down (unlike `Engine.Camera.Pitch`). Open: no placed rig Objects or Room-editor gizmo, no framing/dead zone, no Dutch roll, leaving a rig hands the camera back without a blend, main camera only. Bug found, not fixed: `ThirdPersonCamera` documents Pitch as looking down but a positive pitch looks up |
| **Navigation** | Recast navmesh, links and areas in the editor | Grid with floors, agent sizes, links, area costs, obstacles, crowds | Polygon navmesh (Recast-style); links and costs placed in the Room editor | XL / M | Editor links and costs yes; polygons as a stretch |
| **Animation** | Mecanim with humanoid retargeting, avatar masks, Animation Rigging | Clips, controllers, blend trees, layers, sync groups, look and reach IK, root motion, 2D rig | Retargeting between rigs; two-bone and FABRIK IK; constraints | L / M | IK yes; retargeting as a stretch |
| **Physics extras** | Cloth, wheel colliders, ragdoll wizard | Bodies, characters, joints with limits; cloth and vehicles are Objects plus PGSL by Cal's rule | A cloth solver on a model part; wheel ray and suspension helpers for Objects | XL / M | Wheel helpers yes, as an example |
| **Netcode** | Prediction, reconciliation, server authority, NAT punch-through | LAN, relay by code, replication with interest distance | Fixed-tick prediction and reconciliation, authority mode, NAT punch-through | L-XL | NAT punch-through (M) yes; prediction no |
| **Input** | Input System: analogue actions, per-device bindings, composites, rebinding UI | Named actions, rebinding by next press, saved bindings | Analogue actions with dead zones, bindings per controller, combinations, a ready-made rebinding screen | M | **First version merged 29 Oct (5431c37):** stick and trigger axes read as action values with per-action dead zone and sensitivity, bindings per controller index with shared defaults, chords (every key held, fires once), and a rebinding screen a game opens with one call (PGSL and Engine API; `input-actions` target, 17/17 with model-editor). Behaviour change: with keyboard emulation on, rebinding takes the controller button, not the key it emulates. Not yet pictured: the screen is checked through recorded draw calls only |
| **UI** | UI Toolkit with UI Builder, data binding, world-space UI | UI toolkit (lists, focus, typing, themes), UI editor | ST1; data binding; UI in the 3D world | S-M / M / M | Yes |
| **Audio** | Mixer with snapshots and effects (EQ, compressor, reverb), spatialiser | Buses, ducking, 3D sound, acoustics, synthesiser | Per-bus effects and snapshots; GS2 synth | M / M-L | **First version merged 29 Oct (b5f8a0f):** each bus with an effect gets its own XAudio2 submix (filter, system EQ, reverb, a level meter; a frame-rate compressor from the meter); snapshots of bus mixes and effects with a timed blend; `AudioMixer.json` beside the `.genesisproj` (ProjectSettings is left out of exports); flat `Bus*` and `AudioSnapshot*` commands, `Engine.Audio.Music` / `Engine.Audio.Effects` (Filter, Eq, Reverb, Compressor each with Enabled; Level), `Engine.Audio.Snapshot`; the Audio editor's Options › "Mixer and snapshots…" page; `--test mixer` 6/6 inside the Audio target. Open: no effects on the master bus, fixed effect order, the compressor misses sharp transients and does not run in the editor's own preview, no delay or chorus, mixer edits outside the editor's undo |
| **Decals** | URP/HDRP decals with normal and mask maps, layers | Script decals, follow, see-through | Normal and roughness on decals; layers; a Room-editor decal tool; decals that bend with skinned models | S-M each | Yes (not skinned) |
| **Terrain** | Holes, detail meshes, tools package | Sculpt, paint (8 layers), scatter, grass, water, tiles | Holes; tessellation (marked "–"); T1 templates; GS3 grass | M / L | Holes and T1 yes |
| **Level of detail** | LOD groups, impostors | Automatic LODs | Distant stand-ins (impostors), LOD fades | L | Stretch |
| **Scenes** | Additive multi-scene editing | One room open and played at a time; persistent Objects | Additive rooms (load several, edit side by side) | L | Stretch |
| **Profiling** | Profiler timeline, memory profiler | F6 profiler, GENESIS_PROFILE reports, frame budget parts | A frame timeline window with history; memory snapshot view | M | Yes |
| **Version control** | Plastic/Git integration | None in Studio | Git status, diff and commit in Studio (Genesis already writes text resources) | M | Optional |
| **XR** | OpenXR (VR/AR) | None | OpenXR on D3D11/Vulkan | XL | No |
| **Testing games** | Unity Test Framework | `game.run` with scripted input, `TestCheck`, the headless harness | Mostly there; a Studio "Tests" panel | S-M | Optional |

## 5. What fits between 29 October 09:00 and the plan's end (30 October)

**What limits it.** The usage budget limits the window more than the clock does:
- **Usage:** 60% of a weekly limit went in 13 hours of one heavy session (15 Oct), and an AI Team spends faster.
- **Clock:** about 36 hours by the clock (09:00 on the 29th to the end of the 30th; the OPEN budget
  window in CLAUDE.md runs to 30 Oct 23:59). The weekly limit reset given in the 22 Oct plan (Thursday 08:00 UTC) falls on the
  29th; check it.
- **Machine:** one PC and one GPU. Checks run one at a time, so engineers can write code in
  parallel (in worktrees; F: has 510 GB free) but each tier or Full Build run is a queue of one.

**Past pace, for a yardstick.**
- 22 Oct: one session, no team, did ten scoped first versions in a day.
- 23 Oct: the eight-item speed list.
- 28 Oct: the AI Team did a blind trial, three UI packages, a replay rewrite and about ten fixes in
  about 5 hours.

**Realistic for the window:** every S item, about 10 to 15 M items and 4 to 6 L first versions, all
built and published, if the budget holds out. Cal leaves testing to the end (section 7), which
saves about 20 minutes a block. Recommended order, with Cal's 28 Oct additions:

| Block | Items | Estimate |
|---|---|---|
| 1. Bugs and the morning list | C1 (resource tree opens things), GS1 (delete bug), GC1 (instruction count), C2 and T2 (black areas: Nuvema, Golden Stag), T4 publish, T6 stick, T10, T3 Pennywick re-measure | 6-8 h |
| 2. Cal's grass | T1 (default meadow grass, grass shader, Terrain template options) with GS3 (texture, wind settings, pushers, paths) | 6-8 h |
| 3. Cal's editors | C5 node shader editor, C5 effects editor, C3 live texture painting, C4 MIDI studio (first versions) | 4 x 6-10 h |
| 4. Quick wins for parity | Input (analogue actions, per-controller, combos, rebinding screen); ST1 UI fields; ST2; decal normals and layers; terrain holes; camera blends and dolly; NAT punch-through; GS4; GS5; NV1 unlit | 8-12 h |
| 5. Verify-first items | AE15, AE22, AE54-56, GC4, GC6: check each in code, close or fix | 2-3 h |
| 6. If anything remains | Placed light and reflection probes (lightmap step 1), T7 look gaps, T8 second blind trial | 6-10 h |
| 7. The final test pass (section 7) | All backends, then every PGSL and Engine API test, then a Full Build | 2-3 h |

With block 3 added, the list is about 60-90 hours of work; a team spending a week's budget in a day will
not reach the end of it. **Blocks 1-3 and the final pass are the realistic core**; 4-6 go as far as
the budget allows, in that order.

**Will not fit:** Mac, mobile, web or consoles; a lightmap baker; a shader graph or VFX graph node
editor; a timeline; netcode prediction; cloth; XR. These are XL: each needs several days of work and
checking.

**After the window (for planning):** Genesis would still be Windows-only and without baked lighting
or node tools, but it would have no known open bugs from the game sessions, and better input, UI,
cameras, decals and grass.

## 6. Decided against or deferred by Cal (revisit if parity matters more now)

| Item | Decision |
|---|---|
| Plugins, editor extensions, asset store | Not wanted (24 Oct) |
| A cutscene timeline | Declined (24 Oct); cameras and events from PGSL instead |
| Linux | "Later" (the Player already renders on Vulkan and OpenGL) |
| Node view in the Shader editor | "Later, if wanted" |
| Crash reports sent to a service | When Genesis has users |
| ReadyToRun precompiling of the Player | Declined (about 1 s, not worth it) |
| Systems for one kind of game (vehicle system, cloth editor, ragdoll system) | Not wanted: these are Objects with physics and PGSL |

## 7. For the AI Team run

**How Cal wants it tested (28 Oct):** testing is left to the end.
- **During:** add several features, then one small focused test (at most 5 minutes), then build and
  publish; repeat until everything is done.
- **At the end:**
  1. Every backend through the built-in backend rendering check (the five renderer smokes, e.g.
     `Build.bat --quick --full-smoke`).
  2. Every PGSL and Engine-level API test through the built-in suites. **First bring those suites up
     to date with the PGSL improvements** (the compiled tier, cached scripts, typed lists and grids)
     **and the Engine API work** (`Engine.<Area>.<Part>.<Value>`, Enabled on every area and part,
     `Documentation/Design/EngineApiAreas.md`), so they cover what exists now.
  3. A Full Build last.

**Running at full machine use with parallel agents (Cal, 28 Oct):** the PC has 16 GB RAM and one GPU.
- **Compiles:** at most 2 at once (`agent-compile.ps1`'s machine-wide limit). Two at once already
  left about 1 GB free on 28 Oct; each takes 1-1.5 GB plus its helper processes.
- **Worktrees:** one per engineer. On 28 Oct a shared tree gave locked DLLs (CS2012) and compile
  errors from other agents' half-done edits. One integrator merges.
- **GPU:** game runs and test runs one at a time through `Global\GenesisAgentTestRun`. Parallel GPU
  runs give false failures and meaningless timings.
- **Weekly limit:** commit, push and update the ledger after every verified block, so the limit
  cutting in loses nothing. Stop new features at about 80% of the weekly limit, and keep the rest
  for the final test pass.

**Sub-chats per task (Cal, 28 Oct): writing and research are the slow part, so run them in
parallel.**
- **Fresh sub-chats, not forks:** a fork carries the parent's whole conversation, so it spends
  budget before it starts. Brief each sub-chat with its plan item (T1, C4, C7...), the files it
  owns and its acceptance criteria.
- **Structure:** one sub-chat per main task, with sub-chats under it for research or design and
  for separate parts (the MIDI studio: file read and write, piano roll, instruments).
- **Rendering together:** C2, C7, C7b, C8, C10(3) and T2 all touch the renderer (ForwardRenderer,
  shadows, depth, light probes). Give them one sub-chat, or run them one after another.
- **In parallel:** the editors (C3 painting, C4 MIDI studio, C5 shader and effects editors), T1/GS3
  grass, and the bugs GS1, GC1 and C1 are well apart.
- **Reporting:** each sub-chat runs only its own 5-minute check and reports back briefly: changes,
  what passed, what it could not check. The main session merges, builds and publishes.

**Process (Cal, 28 Oct):**

```
Main Task: the planner conversation (this one, or "Active Claude sessions resource planning") tracks it
--> Sub Task: the AI Team in its own sub-chat; its Planner tracks, the team works
----> Minor Task: a junior engineer works, with the team where required
----> Minor Task: ...
--> Sub Task: ...
```

**A command structure: each level briefs the level below it.**
- **The Main Task** (a planner conversation: the engine session or "Active Claude sessions
  resource planning"):
  - Splits the plan into Sub Tasks.
  - Writes each Sub conversation's brief: the plan item, the files it owns, its acceptance
    criteria, the rules in this section, what to report back.
  - Creates the conversations and tracks them in the ledger.
- **A Sub conversation** (the AI Team for one Sub Task):
  - Its Planner splits the Sub Task into Minor Tasks and writes each Minor Task's brief.
  - It runs them, integrates the results, runs the 5-minute check and reports to the Main Task.
- **A Minor Task:** a junior engineer, with other team members where needed, works from its brief
  and reports what changed, what passed and what it could not do.

**Start the run with `/ai-team UnityParityPlan.md`** (the skill is in `C:\Users\Cal\.claude\skills\ai-team\`).
Typing it is Cal's one opt-in to the Workflow tool and parallel subagents for the whole run, and in
Bypass mode nothing else asks for approval.

**How a conversation is created without anyone clicking:**
- **Sub conversations:** the Main Task calls `create_scheduled_task` (the scheduled-tasks tools)
  with `fireAt` about a minute ahead, `notifyOnCompletion` true and its brief as the prompt. Each
  run opens as its own fresh conversation, and the Main Task is told when it finishes. The prompt
  must stand alone (a run remembers nothing), so it names the plan file, the ledger and its item.
  Runs happen only while the Claude app is open.
- **Minor Tasks:** run inside the Sub conversation as subagents (the Agent tool, each in its own
  worktree), briefed by the Sub conversation's Planner.
- **Spawn-task cards** need a click, so they are not automatic.
- **Not checked:** whether a scheduled run may itself create scheduled tasks. Only the Main Task
  needs to, so it does not matter for this structure.

- **Publish in batches:** merge several finished Minor and Sub Tasks, then one build and publish,
  then a backup. Not a publish per change, and not one only at the end.
- **An idle session goes further:** a sub-chat or session that finishes, or waits on nothing, takes
  the next unclaimed item in the plan's order and claims it in the ledger first. It never waits.

- Start from this file, the task ledger (`.claude/task-ledger.json`) and memory `genesis-todos`.
- Keep the standing rules:
  - Back up only with `Backup.ps1 -KeepLast 3`, in the cycle backup, changes, publish, backup.
  - Delete your own `.build\tests` and `TestResults\Builds` output once read.
  - Coordinate the GPU lock with the other sessions.
  - Content made with Genesis's own tools; engine features general and opt-in.
- Engineers write in their own worktrees so the shared tree never half-compiles. Run headless
  tests with short output paths (GDI+ fails on long ones), and run the export tests through
  `Build.bat`: the test folder's Player copy is stale.
- A blind check is worth more than a self-check: the 28 Oct Task Checker rejected a scorecard the
  team had written about its own work.
