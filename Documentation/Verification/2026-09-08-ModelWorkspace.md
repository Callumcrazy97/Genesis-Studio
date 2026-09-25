# Model Viewer and Editor — visual checkpoint, 8 September 2026

## Scope and decision

The user redirected the Model work to **layout/aesthetics and Viewer/Editor separation first**.
This checkpoint delivers that presentation pass using existing model commands. It does not claim
that the broader Model technical backlog is complete, and it does not advance work to Room.

Opening a model already used a docked Viewer and a separate Composer. The visible authoring window
is now called **Model Editor**, and the Viewer has a prominent **Edit Model** action. The internal
Composer role and saved model contract remain unchanged.

## Delivered presentation

- Full-width document menus and a separate mode/action row; camera/grid/gizmo controls belong to
  the viewport toolbar. Removed the unused preview-target picker from the Model surface.
- Viewer: hierarchy at left, inspection properties at right, preview and persistent playback below.
- Editor: Hierarchy, Materials and Meshes sections; primitive creation; contextual Compose, Sculpt,
  Mesh and Animate inspectors. Materials/Meshes currently show inventory, with existing editing
  commands accessible through Mesh mode.
- Compact XYZ part-transform groups, explanatory empty states and a themed asset section strip.
- A persistent frame ruler connected to the existing clip/transport state. Controls wrap at narrow
  widths; Stop returns to the start. Playing from an authoring mode switches into Animate.
- Dark preview background, subtle ground grid, brighter inspection lighting, closer initial framing
  and a stationary default Editor camera. Viewer auto-spin remains independently controllable.
- Working panel toggles at all widths and Window → Reset layout. The 1024px layout initially hides
  side panels; opening both leaves a usable 444px viewport with wrapped playback controls.

This is a presentation change. It does not add OBJ/FBX import, new rig algorithms, a keyframe curve
editor, embedded physics authoring, shared Image painting, AI or BYOK.

## Evidence and validation

Fresh baseline workflow: `TestResults/ModelSystem-20260908/Before/` — existing Model workflow passed
with three captures. Populated pre-redesign windows are in `BeforeWorkspace/`.

Latest inspected captures: [`PublishedReview/`](../../TestResults/ModelSystem-20260908/PublishedReview/),
generated from the same build's test executable after successful publication.
These include Viewer/Editor at 1440×940 and 1024×740, narrow panels expanded, and Mesh/Sculpt/Animate.
The fixture is a disposable primitive campfire blockout; its generic auto-rig only exercises the
animation layout, not production animation quality. Source/reference assets were not modified.

Nine layout checks passed for menu/command/viewport separation, visible unclipped timeline and
minimum useful viewport dimensions. The existing focused `--test model` workflow also passed,
including canonical model save/reopen, imported animation, mesh tooling and runtime checks.
Release compilation passed with zero warnings/errors. Actual capture DPI is the local 96-DPI
environment; these are not 125%/150% DPI acceptance results.

Full regression `20260908-180215-cd337f10`: **391/392 passed**. The sole failure was the existing
`Runtime.PGSL.TwoD` requirement for a `room loaded entities=` startup-log line. That run rendered and
saved the Player screenshot, and logged partial-save rejection/recovery, but omitted the required
startup line. It remains a failed Full Build and did not promote. The unchanged focused 2D workflow
subsequently passed in [`TwoDRetry/`](../../TestResults/ModelSystem-20260908/TwoDRetry/) (16.79 seconds).
No runtime or log-assertion code was changed to suppress the failure; track the intermittent startup
diagnostic in the later technical pass.

Publication through `Build.bat --quick --check --full-smoke`: **passed**, run
[`20260908-180752-24a11d42`](../../TestResults/Builds/20260908-180752-24a11d42/BuildSummary.json),
in **156.5 seconds**. All **13 workflow checks**, explicit **DX11/DX12/Vulkan/OpenGL/Software**
smokes, package checks and published startup/shader compiler checks passed. The staged package
was promoted to `Genesis Application/`. The 2D Player check passed again in this gate.
This was Quick Build with explicit workflow/backend coverage, not a passing Full regression.

The first full run (`20260908-175546-e43e2cd9`) failed two checks and did not promote. The chrome
test was updated to distinguish Model's nested layout from other editors' embedded toolbars. The
model lighting fixture now includes a curved, coloured surface: its unchanged colour-count threshold
previously depended on the old checkerboard floor, while a correctly rendered plain cube naturally
uses few colours. Review of that capture also led to fitting the model against the actual field of
view and aspect ratio, preserving a margin around its bounding sphere.

## Comparison and remaining acceptance

The user's Model reference remains the visual target: dominant central preview, organised resource
panel, right inspector and bottom transport. The revised layout follows that structure. It does
not claim the illustrated material/rig/hierarchy functionality solely because it resembles the image.

[Blockbench](https://blockbench.net/) is the proposed nearest comparator for the later low-poly
authoring/animation inspection; its [workspace overview](https://blockbench.net/wiki/guides/blockbench-overview-tips/)
provides the outliner and animation workflow baseline. No installed Blockbench session was tested.
The full functional comparison is deferred until after the user's visual review, as requested.

Next technical audit: part-transform undo, gizmo drag start offsets, surface-brush behavior,
OBJ/FBX and reimport handling, hierarchy/material organisation, and animation editing/persistence.
Keep current save/load and Runner behavior working. Cross-editor features stay in the final
integration phase, followed by AI/LLM and BYOK.

## User review

1. Open a Model from Assets; inspect the Viewer and use **Edit Model** to open its separate window.
2. Compare the Editor layout with the reference; switch Compose/Sculpt/Paint/Mesh/Animate.
3. Toggle side panels, resize the window and use Window → Reset layout.
4. Open an animated model; check the clip picker, frame ruler and transport remain accessible.
5. Save, close and reopen using disposable testing assets.
