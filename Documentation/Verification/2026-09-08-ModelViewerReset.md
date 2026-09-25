# Model Viewer and Editor reset — 8 September 2026

## Delivered scope

The old Model Editor shell, production inspector, topology/UV controls and generator UI were
removed. The rig, pose, joint-drawing and animation implementation was retained in the separate
`ModelRigViewportControl` used by `ModelAnimationStudioDialog`. Its working-copy, Cancel, undo,
binding, saved poses, generated clips and save/reopen behaviour remain covered by regression tests.
Reusable model loaders, mesh data utilities and runtime code remain available.

`ModelViewerControl` is now the resource entry point. It has a permanent left hierarchy/material
panel, a central/right viewport, viewing controls above it, and animation transport/scrubbing below.
The top toolbar exposes Import Model, Frame, Spin, shading, projection, camera, lighting, ground,
and Edit Model. The View menu provides a second camera, orientation gizmo and grid settings.
Textured, untextured and wireframe modes, Studio/Soft/Unlit lighting, shadows, and grid cell size,
colour and XYZ rotation are preview settings. They do not alter the saved model.

The floor and grid use the model's minimum Y bound after its pivot offset. Geometry is not translated
to compensate, so opening or framing a model does not change its authored coordinates. This fixes
the half-buried fox and applies to the second camera and replacement Editor too.

The replacement `ModelEditorControl` builds on that viewing surface. Its left panel contains the
current brush settings, mesh selection, transform dialog, Draw/Carve/Smooth sculpting, vertex paint,
colour picker and explicit primitive creation. The right panel contains preview, onion skinning,
mesh groups with visibility and renaming, material assignment, a new-material action and colour
texture assignment. Models start empty. Paint/sculpt update during dragging, have a brush outline,
and record one undo operation per stroke. Mesh transforms provide XYZ offsets, rotations and scales.
The Animation menu opens the preserved Rigging, Posing and Animation windows.

Rig templates now stop at layout: adjust individual joints, then explicitly Bind mesh before posing.
Follow children is switched off when placing a template. This does not claim automatic fitting is
perfect for every character; manual weight painting and richer fitting controls remain outstanding.

## Importing models

Choose **Import Model…** in either toolbar, or **File → Import Model…**. Import creates a new,
project-owned model and opens it in the corresponding Viewer or Editor, preserving the current
document. glTF/GLB use the existing importer. FBX, OBJ and DAE are converted to owned GLB assets
using AssimpNetter/Assimp. The original converted source file is also retained in the model data
folder. Games consume the canonical asset and do not need Blender.

`.blend` import requires an installed Blender. Genesis finds standard Windows installations or
uses **File → Locate Blender…** / `GENESIS_BLENDER_PATH`. Conversion runs in a background process,
with source-script auto-execution disabled, captured errors and a timeout that closes its child
process. Geometry and animation import were verified with Blender-generated `.blend` and binary
FBX fixtures. Static FBX/OBJ/DAE and existing animated glTF/GLB fixtures were also tested.

Support depends on the source format and converter. Arbitrary Blender procedural shaders,
constraints, drivers, external dependencies, morph animation and every exporter extension are not
promised to round-trip. OBJ has no skeletal animation contract. Reimport with dependency tracking
and preservation of local edits remains future work. See the primary
[AssimpNetter documentation](https://github.com/Saalvage/AssimpNetter) and
[Blender pipeline documentation](https://www.blender.org/features/pipeline/).

## Comparison and inspection

Original Genesis reference: `Old Genesis/GMS Style/GameManager/Editors/ModelEditorControl.cs`
puts Load and Edit in its left properties group, while `ModelViewer/ModelViewportScene.cs` uses
`ComputeAutoGroundOffsetY()` to rest the bounds on the floor. The reset restores a stable left
inspection panel and an equally discoverable import/edit path, with viewing commands in the top
toolbar as requested. Its floor alignment is a preview-ground change rather than an asset transform.

| Area | Original Genesis | Reset Genesis | Third-party reference / remaining difference |
|---|---|---|---|
| Layout | Dense properties groups, Load/Edit left, transport below | Left inventory, large viewport, top viewing controls, bottom animation | Blender uses context-sensitive viewport controls and inspectors; Genesis retains the Image Editor's simpler panel order |
| View display | Existing shared grid, framing and floor correction | Bound-aligned ground, shading, lighting, camera controls and visible orientation axes | Blender has separate solid/material/rendered inspection modes; Genesis has three simpler preview modes |
| Brushes | Legacy drawing/painting tools | Live sculpt and vertex paint, colour/radius/strength, material texture assignment | Blender offers much deeper sculpting, masks, remeshing and texture painting; those are not equivalence claims |
| Animation | Separate rig/pose libraries and animation builder | Preserved guided Rig → Pose → Animate workflow; explicit fitting/binding boundary | Curves, constraints, retargeting, weight painting and pose thumbnails remain gaps |

This comparison uses inspected Genesis captures, reference source and Blender's official
[shading guide](https://docs.blender.org/manual/id/5.0/editors/3dview/display/shading.html),
[sculpting guide](https://docs.blender.org/manual/en/latest/sculpt_paint/sculpting/index.html) and
[brush guide](https://docs.blender.org/manual/en/latest/sculpt_paint/brush/introduction.html).
Blender's UI was not operated for this review. Existing Original Genesis captures are historical
references, not new interactive captures. No Computer Control was used.

**Inspection decision:** the Viewer layout and core inspection workflow are ready for user review.
The replacement Editor's exposed tools are implemented and tested, but the entire Model system has
not reached Blender/Blockbench feature parity. Keep Model active; do not advance to Room yet.
The [capture gallery](../EditorReview/2026-09-08/model-reset/index.html) records the visual evidence.

## Verification and retained coverage

Focused Editor/animation: **9 checks passed**, including pointer edits before release, whole-stroke
undo, undo after a mesh-list edit, transform/material/group persistence, generated clips, explicit
template binding and cancellation. Focused Viewer: **5 checks passed**, including the optional copy
of the user's fox, ground alignment, secondary rendering, non-looping playback, empty/import error
states and Blender/FBX/OBJ/DAE imports.

Full Build `20260908-222754-35fc56d5` passed **409 regression checks, 121 recorded images**, all
five renderer smokes (DX11, DX12, Vulkan, OpenGL, Software), and package/startup/compiler checks in
**336.98 seconds**. Each backend passed its 24 winding probes; none was skipped. Solid untextured
inspection now has its own texture-suppression option and keeps normal culling/depth, independent
of translucent onion-skin rendering.

The subsequent axis-button/material-selection polish passed **21 focused Model-system checks and
19 images**, including a disposable copy of the user's fox. Positive-X axis clicks and whole-mesh
material replacement are included. Final Quick Build `20260908-223634-d1a4fe09`, using
`--quick --test model-system`, passed those **21 checks and 19 images**, package consistency and
startup/compiler checks in **106.83 seconds**, then promoted Studio and its matching Player to
`Genesis Application/`. The published package includes Assimp's native library, managed wrapper
and licence. This focused publication did not repeat unrelated regressions or the five backend
smokes; the Full run above records that coverage.

The first Full attempt (`20260908-221758-fbf2c9ac`) failed two obsolete assertions about six old
production dropdowns and primitive-list persistence. They now check the replacement themed material
picker/group list and exact canonical geometry persistence. That failed run was not promoted and
skipped backend smokes; the successful Full run above supersedes it.

The three old shell-specific kitbash, preset humanoid/paint and flowing-material cases were retired.
The old Model gate's topology/UV/LOD/collider and generator UI steps were retired with those controls.
Source copies of the previous editor and affected tests are in `.build/model-editor-before-reset/`
and `.build/model-tests-before-reset/`. Import-to-Object/Room/runtime, winding, renderer and saved
animation checks remain; the cart animation check now targets the retained animation canvas.
Unrelated room/terrain checks construct procedural fixtures through a test-only data helper.
New Viewer and Editor suites run in Full Regression. A green build does not claim the removed
features still exist.

## Remaining order

1. Review the Viewer and replacement Editor with user models. Refine selection/transform ergonomics,
   brush performance, material workflows and layout based on that review.
2. Restore needed advanced geometry/UV tools deliberately, then complete weight editing, hierarchy
   reparenting, reimport and animation-only import. No inactive placeholder buttons are presented.
3. Finish Model acceptance before Room, then follow the standalone editor order in README.
4. Cross-editor embedding, physics inside Model and shader effects inside other editors stay last.
   AI/LLM/BYOK integration follows that integration phase.
