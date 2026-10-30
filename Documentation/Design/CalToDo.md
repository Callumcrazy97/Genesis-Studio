# Cal's to-do after the 29 October run

One line per step: open X, look at Y, if Z, screenshot. Written 29 October 22:10 during the quiet
window; the "Script-tested" section is filled in once the 00:31 test runs finish, so a line there
that still says *pending* has not run yet. What was built is in `Handover-2026-10-29.md`; every
item's status is in `UnityParityPlan.md`.

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
7. **Genesiscraft export.** The Desktop copy carries an engine from before the `DsListInsert` fix, so a
   world below the top of your list fails with a script error. Open My Genesis Game in Studio,
   Project > Export, export *beside* the Desktop copy (not over it), play world w20 and one lower.
   If the lower world loads and the ground is at 63 with kelp under water, delete the old copy.
8. **The F6 debugger in your own games.** F5 a game, press F6: colliders, GUI layers, the object
   report and the view modes (Albedo, Ambient, Direct, LOD). Screenshot anything that looks wrong.
9. **The new editors, five minutes each**, so you know they exist before someone asks: the MIDI
   piano-roll studio (Assets > New > Music), the audio mixer (Project > Audio Mixer: effects and
   snapshots), the node shader editor (New > Shader > Graph), the effects module stack (open a
   Particle), the Model editor's texture painting (Surface step, paint on the model), Terrain
   templates with built-in grass (New > Terrain), the input rebinding screen (F5, Escape, Controls).

## Script-tested for you (the runs after 00:31 fill this in)

| Check | How it was scripted | Result |
|---|---|---|
| Nuvema black blocks on all five backends | `game.benchmark` captures on dx11, dx12, vulkan, opengl, software; black fraction measured | pending |
| Nuvema name entry by scripted keys and pad | automation key and pad feed into the name screen, text read back | pending |
| Nuvema window resize while playing | resize then capture; no black or stretched frame | pending |
| Resource tree: expand a folder, double-click one resource, only that one opens | Studio automation, if it can drive the tree; otherwise says so here | pending |
| Genesiscraft distant terrain black on Vulkan | bot flight on Vulkan and D3D11, black fraction per frame, `GENESIS_VULKAN_DEBUG=1` log kept | pending (a clean result is evidence, not proof: it never reproduced before) |
| First-person right stick direction | a test feeds stick values and reads the camera direction; a tiny tool prints your pad's axis live | pending |
| Acceptance: a small 2D game and a small 3D game from scratch with built-in tools, on all five renderers | authored by a fresh agent with automation only: Coin Hop (2D) in 5.3 min, Lake Quest (3D) in about 3 min, about 20 min each with the fixes after the first plays (the bar is 15) | **Done 30 Oct 04:12:** both won on D3D11, D3D12, Vulkan and OpenGL; Software won the 2D game and draws the 3D one cleanly but at about 8 fps the walker did not finish in time. Projects and pictures in `C:\gs\worktree-agent-a7fa0e8ea93c56166\samples` and `...\out\runs` |

## Not for you

The subscription question the planning session listed on 29 October has no detail recorded here;
ask that session. Everything else from the run (the open test failures, the Full Build, the second
blind trial, `EngineApi.md`) is the integrator's and runs on from 00:31.
