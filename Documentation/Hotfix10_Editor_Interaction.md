# Hotfix 10 — Editor interaction and shared runtime pixel rigs

## Apply to the current source tree

Baseline: the original Luigi's Mansion Template 5 branch, followed by Hotfixes 6, 7, 8 and 9. Extract this changed-files overlay into the Studio source directory containing `Source`, `Tests` and `Build.bat`. Do not reapply Hotfix 4 or the alternate Midnight Restoration branch.

Close Studio and its Player before building:

```powershell
.\Build.bat
```

No project template, game script, game artwork, sound, project manifest or saved room is changed by this patch. Existing projects do not need recreation or migration. This updates Studio and the matching shared Engine/runtime source built with it; it is not a replacement BIOS/launcher distribution for a separately installed Engine.

## Inspector response

The common property surface now distinguishes a property **value** change from a property **schema** change. Matching schemas retain the existing numeric, vector, check, colour, asset and text controls. Model-to-control refresh is suppressed from publishing another edit, so it does not create recursive changes or extra undo entries. Vector edits route only genuinely changed axes.

Room Inspector values, selection cards and the docked Inspector use this value-only path. Room navigation lists rebuild only for topology changes such as adding/removing a node, changing its layer/name, or changing its referenced resource. Scalar transform and tile-cell changes are excluded from that topology key. Own JSON scalar writes retain the control surface; real external file changes or component/schema changes still invalidate it.

The Room Editor updates the document and invalidates the viewport on each pointer event. A 33 ms UI timer coalesces Inspector values and navigation checks; this is **not** a 30 FPS cap on input, rendering or the simulation. Numeric property edits are applied immediately. Focus, in-progress text entry, group expansion, selected tile layer and Inspector scroll are retained for value-only changes.

## Editing permissions

Resource visibility and resource editability are separate. All visible layers still draw, but the current tab and its exact selected layer define what can be inspected or edited:

| Current Room navigation tab | Editable resources |
| --- | --- |
| Objects | Game Objects on the active object layer, chosen under Placement options. |
| Tilesets, in 2D | Individual painted tiles on the explicitly selected tile layer only. |
| Backgrounds | The background in the explicitly selected background slot only. |
| Tilesets, in 3D | The selected terrain entry only. |
| Other tabs, including Instances/Views | No object, tile or background editing through the viewport. |

Selecting an object in the room no longer silently switches the navigation to Objects. A pointer hit cannot choose a different tile layer for you. Objects above a selected tile layer do not intercept that layer's tile editing.

The same capability check is used for selection, drag and transform routes, keyboard deletion/nudging/duplication/paste, asset placement, relevant hierarchy operations, and Inspector callbacks that were opened before a tab change. Locked nodes/layers remain protected.

Changing tabs or layers during a drag cancels the unfinished gesture and restores its starting state. It also clears incompatible selections and armed placement. Previously completed edits remain in the undo journal. A completed continuous drag contributes one transform undo operation; separate numeric detents remain separate edits.

## Drag and preview work removed from the hot path

Image dimensions/resolved paths, tileset metadata, object Inspector JSON and exposed PGSL field metadata are cached until a real asset-dependency change. Background preview images reload for an asset/tint change, not for scrolling a numeric position/speed/depth field. The selected tile layer and its grid size/margin are not rewritten by passive navigation refresh.

Drag snapshots copy transform scalars directly rather than serializing JSON. A tile drag retains the inverse layer matrix for the gesture. Offscreen tiles and repeated backgrounds are culled against the viewport, and grid drawing is limited to the visible region. The full room hierarchy is not rebuilt for every pointer move.

These are implementation changes, not measured native FPS or interaction-latency results. No timing claim is made without a Windows run.

## Shared Image Editor/Engine functions

See `Runtime_Pixel_Rigs.md`. The existing Image Editor's deformation, connected-joint/pinned-joint solver, source-art gap infill and layer blend implementation are shared with `Genesis.Runtime.Imaging`. The Image Editor now delegates to that implementation, rather than maintaining a second copy.

The runtime includes per-entity rig state, authored pose/keyframe playback, procedural bone/joint control, cached pixels and dynamic texture submission through the ordinary 2D object draw path. C# and PGSL object events can use it. No Luigi/Mushroom Meadow code or templates were changed to demonstrate it.

## Validation status and checks to run

No .NET SDK, C# compiler, Windows UI runtime or native graphics runtime was available in the patching environment. **The patch has not been compiled there, and the native tests below have not been executed.** Source inspection, lexical delimiter checks, project XML parsing, command/call-site wiring checks and changed-files archive verification do not replace those tests.

After building successfully:

```powershell
.\Build.bat --test editor-interaction
```

This target runs 17 new Inspector/Room/runtime integration cases and the 9 existing Image Editor rig-control cases. The runtime render probe records actual production texture/submission calls; it does not emulate a GPU or prove final visual output. The new suite is also included in the full headless regression route.

Suggested human acceptance sequence:

1. Select a tile on a non-first tile layer. Scroll Offset X, Width and Rotation fields repeatedly. Verify the field remains focused, the layer/grid settings stay unchanged, and Undo/Redo and save/reopen preserve the values.
2. Drag a tile, object and selected background separately. Verify immediate movement, stable Inspector controls and one undo per completed drag. Switch tab/layer during a drag and verify it restores the starting transform.
3. Put objects, backgrounds and two tile layers over the same screen region. Verify only the active tab's exact layer can be selected or modified, including Delete, paste, asset drops and hierarchy actions.
4. Verify external image/tileset edits refresh previews, while scalar movements do not reload those previews.
5. Save a pixel rig in the Image Editor, bind it from a disposable project's Object Create event, and test local/world aiming, mirrored instances, infill, saved animation playback and `SpriteRigClear`. No existing template needs to be changed.

Production diagnostic counters available to native tests are `RoomInspectorValueRefreshCount`, `RoomStructureRefreshCount`, `RoomMetadataReadCount`, and the common property surface's `LayoutBuildCount`/`ValueRefreshCount`. They measure the relevant paths without relying on an artificial delay or a visual impression of speed.
