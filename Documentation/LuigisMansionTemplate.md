# Luigi's Mansion template patch — delivery and integration

## Hotfix 6 — Patch 5 path-safe template packaging

This hotfix applies **on top of the original Patch 5: Luigi's Mansion: A Light in the Dark**. It does not install Midnight Restoration and does not replace any Mushroom Meadow or Luigi gameplay, room, artwork or audio file.

Close Studio and Player, apply the changed files at the source root, then run `Build.bat` as normal. No source-folder relocation, asset renaming, registry modification, project recreation or removal of original media is required for the reported build-copy failure. The build creates a fresh staging directory; it does not rely on deleting the user's existing projects or published installation.

### Cause and change

Patch 5 marked every loose template file for transitive build/publish copying. Deep sprite sidecars then acquired each consumer project's `bin/Release/net10.0-windows` prefix. The reported Image Editor MSB3021 occurred after Core compiled, during that content copy.

Core now generates **`Templates/LuigisMansion.zip`** under its normal per-configuration/TFM/RID intermediate directory and propagates only that short content path. The built-in MSBuild `ZipDirectory` task reads the unchanged source `LuigisMansion/Assets` directory; no external archiver or extra package is required. The file-set input tracks added, removed and renamed resources as well as timestamp changes, and unchanged builds reuse the previous bundle. A temporary archive is promoted only after successful compression.

Studio extracts the archive directly into a newly created project's `Assets` directory. All original resource names, relative paths and bytes remain unchanged. The newly generated project manifest/ID is not in the bundle. Archive paths, duplicate destinations, parent/file conflicts and an absent start room are checked before extraction; existing resources are never overwritten. The current archive takes precedence over loose content left by an older build, while the old loose-directory loader remains available for older deployments. Gameplay runs from the resulting ordinary editable resources, not from the archive.

The source-tree template remains in its original location and can still be authored there. Normal operating-system restrictions still apply to source/project paths in general; this change removes the long nested **build-output** paths reported here rather than claiming unlimited path support throughout Windows.

### Validation for this hotfix

Performed in the delivery environment: XML parsing and build-item/source inspection, archive/extraction round-trip and SHA-256 comparison for all 1,445 template asset files, revalidation of the 617 imported-media provenance hashes, Windows-path budget calculations using the supplied checkout location, and patch-overlay/ZIP-integrity checks. These are portable checks, not an execution of MSBuild or the C# extractor.

`Build.bat --test luigis-mansion` now includes the short-path package presence/content test and additional project-installation assertions, alongside the existing native media-hash, PGSL and gameplay cases. **No .NET SDK or Windows runtime is available in the delivery environment; native compilation, MSBuild incremental execution and the native regression suite have not been run here.**

## Apply original Patch 5

This is a changed/new-files-only source overlay for the supplied **Genesis Studio** after the Mushroom Meadow patch and Hotfixes 2, 3 and 4. Extract at the Studio root containing `Source`, `Tests` and `Build.bat`, retaining paths. It is not an overlay for the GameMaker archive or the standalone Genesis Engine folder. Existing user-created Mushroom Meadow projects are not replaced.

Run `Build.bat`; this republishes Studio and its matching player. Create a new project from **Luigi's Mansion: A Light in the Dark**, then F5. The bundled starter can alternatively be opened at `Source/Genesis.Application.Core/Projects/Templates/Assets/LuigisMansion/A Light in the Dark.genesisproj`. New projects are preferable to editing the installed template directly.

The detailed controls, chapter rules, authoring guide and provenance are under the new project's `Assets/Notes` folder.

## Shared runtime changes

- `RoomEffects2D`: opt-in room-owned 2D lights, rectangular shadow rays, contact shadows, bounded CPU lightmap and pooled canonical particles. Independent per-viewport mask textures; resources released at room disposal.
- `PgslCommands.Effects2D`: light/particle commands, shared visibility tests, own-instance ID, and viewport-correct pointer coordinates.
- `ProjectNumberSave` / `PgslCommands.SaveNumbers`: small project-ID-isolated numeric checkpoints, bounded finite values, format checks and atomic write/replace.
- `RoomRenderSubsystem`: update, render and dispose room effects within existing 2D viewport composition.
- `SpriteDrawCall` / `SpriteRenderer`: optional smooth sampling per sprite/material batch so effect textures do not force pixel art to become blurry.
- `PgslRenderDrawSurface` / `ScriptHostSystem`: GUI sprites share the primitive panel depth so title art, gallery images and HUD icons are not sorted behind their own backgrounds. World-draw sprite depth is unchanged.
- `PgslCommands`: correct swapped right/middle mouse constants using the actual runtime enum.

Existing Hotfix 4 input-edge timing, sprite animation, collision geometry, PGSL return handling and VSync wiring are retained. Effects remain disabled until a room opts in, so ordinary rooms do not receive a darkness overlay or particles.

## Native regression targets

`Build.bat --test luigis-mansion` runs eleven authored cases covering short-path template packaging, template/root installation, original media hashes, all PGSL events through the actual VM/compiler, six chapter floors, frame-stable Luigi masks, actual Create/Step movement/jump/pause, light visibility rays, particle pool/pause/expiry/isolation, numeric saves, and mouse/sampling contracts.

`Build.bat --test 2d-showcase` retains the prior Mushroom Meadow regression target. Template gallery assertions were updated to retain both native showcases alongside the four original available templates.

These native tests were added but **not executed in the delivery environment**, which has no .NET SDK or Windows graphics runtime. The included validation notes distinguish executed source/media/reference checks from native tests and manual GPU/audio acceptance. No native executable or screenshot is being passed off as tested output.

## Manual acceptance on Windows

1. Build without errors, create the new template, launch the title, enter chapter one and confirm no PGSL diagnostic banner. Test with the existing engine HUD closed as well as open.
2. Hold D: briefly slow translation with very fast feet, then normal speed. Reverse to A; test Shift, short/full jumps, W, landing near platform edges, pause/resume and resizes.
3. Point at a ghost, right-click to stun, then hold left-click. Keep it in the cone and move away to increase pull; release or aim away to test escape. Verify no capture through a platform blocker. Overheat and cool the tool.
4. Relight all wards and capture all ghosts; neither goal alone should unlock the room. Collect the revealed key and press E at the door. Verify each of six chapters loads once and awards its checkpoint once.
5. Search a chest and vase, collapse/vacuum Dry Bones, and heal with Toad. Complete the Portrait Keeper's three phases and reach the ending.
6. Restart the player and verify unlocked chapters/banked treasure. Die/retry mid-chapter and verify unfinished loot was not banked. Test new-game confirmation and gentler flash.
7. Browse V on the title: animated source frames, original credits/sheets, Tab to every supplied sound/music item, preview stop on selection/exit.
8. Check audible loop cleanup, pointer alignment after resizing, actual shadow occlusion, crisp source pixels versus smooth particles, and performance/VSync on the target GPU. This delivery does not claim these observations were made remotely.
