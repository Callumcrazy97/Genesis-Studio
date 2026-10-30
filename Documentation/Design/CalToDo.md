# Cal's to-do after the 29 October run

One line per step: open X, look at Y, if Z, screenshot. Written 29 October 22:10, refreshed 30 October
06:45 after the Full Build passed; the "Script-tested" table is being filled by the 30 October runs,
so a line there that still says *pending* has not run yet. What was built is in
`Handover-2026-10-29.md` and `Handover-2026-10-30.md` (which also lists what changed in your games);
every item's status is in `UnityParityPlan.md`.

## Decisions only you can make

1. **ST3, PGSL event locals.** Today a top-level `var` in an event belongs to the instance (it keeps its
   value between events and other events see it). Decide: keep that, or make a top-level `var` local
   to the event body (a warning for a year for scripts that rely on it). Reply with one word, "keep"
   or "local"; the change itself is a day's work plus a migration note in `PgslLanguage.md`.
2. **T6, first-person right stick.** Open any 3D template (Tumble Isles), start it with the pad, push
   the right stick *up*. Does the camera look up or down? Say which way it went and which way you
   want; the FirstPerson recipe's sign flips in one place and the Golden Stag note gets corrected.
3. **Kinfield's re-baked terrain.** A terrain file re-baked with the 29 October path-edge paint fix is
   in `C:\gs\worktree-agent-a1d9d881754dcd424\keep` (not applied to the game). Open Kinfield 3D
   Painted, look at the dotted path edges from Pennywick's square; if the brown band at the path's
   edge bothers you, say so and the file goes in, otherwise it is deleted.
4. **Golden Stag's hearth.** The black band inside the hearth is the game's own `GS_Hearth` faces at
   about 3% ambient light with an "East window light" placed inside the hearth, not the engine. Open
   the inn room in Studio, select the hearth, raise the room's ambient or move the window light out
   of the hearth, F5, screenshot before and after. The Golden Stag session can do this on request.

## Checks only a person can make

5. **T9, time the Nature Walk.** With a stopwatch, make the Nature Walk from `NatureWalk.batch.json`'s
   description in Studio by hand (terrain, grass, water, sky, a walk). Note the minutes; the bar is
   15 and the blind AI trial took 29 (28 Oct) before the fixes. Screenshot the finished room.
6. **T5, Nuvema.** Open Nuvema Town, F5 on D3D11, then on Vulkan (Settings > Display). Look for black
   blocks on the water and the building fronts; if any, F6 > View > Albedo and screenshot both views.
   Then at the name entry, type a name with WASD and with the pad; resize the window while playing.
7. **Genesiscraft export: done for you, one look left.** `C:\Users\Cal\Desktop\Genesiscraft-2026-10-30`
   is a fresh export with the current engine (2be990b), beside the old `Desktop\Genesiscraft`, which
   is untouched. Its exe played w20 and w21 (the lower one, through `DsListInsert`) with no script
   error; w21 came to the top of the list with w20 and w22 kept; w20's ground is at 63 and the ocean
   floor at 52 under water at 62. Kelp was seen only as faint streaks under the surface, so look at
   it yourself (pictures `C:\gs\p3-genesiscraft\d1-w20`); if it is right, delete the old copy.
8. **The F6 debugger in your own games.** F5 a game, press F6: colliders, GUI layers, the object
   report and the view modes (Albedo, Ambient, Direct, LOD). Screenshot anything that looks wrong.
9. **The new editors, five minutes each**, so you know they exist before someone asks: the MIDI
   piano-roll studio (Assets > New > Music), the audio mixer (open an Audio resource, its
   Options menu, "Mixer and snapshots"), the node shader editor (open a Shader, its Nodes mode), the effects module stack (open a
   Particle), the Model editor's texture painting (Surface step, paint on the model), Terrain
   templates with built-in grass (New > Terrain), the input rebinding screen (F5, Escape, Controls).
10. **Your games after the default changes.** Open Golden Stag's hall, Kinfield's square and the
    Cathedral Test and look: three sun cascades to 300 m and four lamp shadows by default, glTF
    models in instanced layers now darker (their default metallic 1), grass parted by characters,
    standard water teal and lake mist lighter, sun glow off in rooms saved before 29 October (set the
    room's `sunGlow` to 1 for the new look). Screenshot anything that looks worse than before.

## Script-tested for you (the 30 October runs fill this in)

| Check | How it was scripted | Result |
|---|---|---|
| Nuvema black blocks on all five backends | the same town view run on dx11, dx12, vulkan, opengl and software (`GENESIS_RENDER_BACKEND` per run, a copy of the project with a seeded save), near-black pixels counted | **Clean (30 Oct 08:00):** 0.000% near-black in the lower two thirds of the frame on all five; the top 3D screen's 0.22% is the same 288 pixels everywhere (sprite outlines and hair), so no black blocks on the water or the house fronts; Vulkan within a mean of 1.1 of D3D11; Software flatter but nothing black. `C:\gs\p2-nuvema\a1-*.png` |
| Nuvema name entry by scripted keys and pad | recorded inputs played through the Player's own replay: keys (D, S, Space, Enter) and the pad (left stick, D-pad, A, Start) on the name screen, the name read back | **Works:** keys typed "Cal" and the screen says "Cal! What a lovely name."; the pad typed "Pad" the same way. `a2-keys-*.png`, `a2-pad-*.png` |
| Nuvema window resize while playing | 512x768, 1280x720, 1920x1000, 640x960 and back, on dx11 and vulkan, a picture at each | **Clean:** no black or stretched frames; the DS layout keeps its 2:3 shape, so the side bars at 16:9 sizes are by design (62.5% of the frame at 1280x720); dx11 and vulkan match. `a3-resize-*.png` |
| Resource tree: expand a folder, double-click one resource, only that one opens | not scripted: no automation drives the resource tree | **For you, thirty seconds:** expand a folder, double-click one resource, one editor opens and no other |
| Genesiscraft distant terrain black on Vulkan | a creative flight at y 130 for about 47 s with 11 pictures on vulkan (`GENESIS_VULKAN_DEBUG=1`) and on dx11, render distance 32, w20's seed in the test slot, near-black counted per picture | **Clean on both (evidence, not proof; it never reproduced before):** near-black 0.4-6.3% on Vulkan and 0.4-6.1% on D3D11 (the floor is the HUD, the high end tree canopies), no cell 95% black. The Vulkan log repeats a depth-image layout validation error (plan W7), not visible in the pictures. `C:\gs\p3-genesiscraft\c-vulkan`, `c-dx11`, `montage-c-*.png`, `render.log` |
| First-person right stick direction | a headless probe fed `GamepadAxis("RightY")` +1 and -1 to the FirstPerson recipe and read the camera pitch; a live readout project prints your pad's axis | **In the engine, stick up (+1) looks up** (pitch 0 to 55 degrees) and -1 looks down, with or without the pad's look emulation. Whether *your* pad reports up as +1 only your pad can say: plug it in and run `powershell -ExecutionPolicy Bypass -File C:\gs\p2-stick\play-live.ps1` (or open `C:\gs\p2-stick\Stick` in Studio and F5), hold the stick up and read `RightY` and the camera line |
| Acceptance: a small 2D game and a small 3D game from scratch with built-in tools, on all five renderers | authored by a fresh agent with automation only: Coin Hop (2D) in 5.3 min, Lake Quest (3D) in about 3 min, about 20 min each with the fixes after the first plays (the bar is 15) | **Done 30 Oct 04:12:** both won on D3D11, D3D12, Vulkan and OpenGL; Software won the 2D game and draws the 3D one cleanly but at about 8 fps the walker did not finish in time. Projects and pictures in `C:\gs\worktree-agent-a7fa0e8ea93c56166\samples` and `...\out\runs` |

## Not for you

The subscription question the planning session listed on 29 October has no detail recorded here;
ask that session. The run's own remaining work (plan section 8, the look bar the blind trial did not
meet, the tiers run on the final main) is the integrator's; the Full Build passed at 05:56 on 30
October and the second blind trial is recorded in the plan's T8 row.
