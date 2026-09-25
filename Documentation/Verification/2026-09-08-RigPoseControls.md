# Rigging controls and joint infill — 8 September 2026

The rig workspace now gives explicit control over moving, rotating and resizing attached bones,
which connected bones follow, and where joints stay fixed. Joint infill repairs transparent gaps
inside the joint circles using the original bound artwork. Preview and generated frames share the
same rasterizer and posed joint settings.

## Workflow

1. Open **Animation → Rigging / Posing / Animation**. The existing workspace has numbered
   **1 · Rig**, **2 · Pose** and **3 · Animate** pages, with common controls above the canvas.
2. Select a bone on the canvas or from the bone list. The selected bone is yellow. The list also
   makes overlapping bone endpoints easier to select.
3. Choose an **Action**. **Move / rotate** rotates from either tip around the opposite tip and
   translates from the middle. **Move**, **Rotate** and **Resize** select a single operation.
   Resize extends or shrinks the bone; the bound artwork stretches along its length without
   increasing limb thickness. The **Length** and **Angle** fields provide exact values.
4. Choose **Affect** before dragging:
   - **Only this bone** is the default. Other bones' far tips stay in place; shared endpoints
     follow the joint so the connection stays joined. Adjacent bones can therefore change length.
   - **Connected chain** transforms descendants and bones connected through moving joints.
     The fixed pivot does not carry the adjoining branch with it. Parent ancestors are excluded.
     The canvas highlights the affected bones and the selection hint gives the affected count.
5. Enable **Edit joints** to edit an existing joint after binding. Drag its centre to move the
   shared anchor, or drag its circumference/change **Radius** to change the joint's infill area.
   **Pin joint** fixes the centre during bone manipulation; pinned joints have an orange marker.
   Unpin it to move it again. Pins hold positions; they do not run an inverse-kinematics solver.
6. Leave **Fill joint gaps** enabled to close gaps such as the torn neck. Increase the neck
   joint's radius if the gap extends beyond the circle, while checking the preview. Source
   transparency remains transparent and already rendered pixels are not overwritten.
7. Use **Undo pose / Redo pose** (Ctrl+Z / Ctrl+Y outside text fields) to reverse a completed
   drag or numeric edit. **Reset pose** returns to the bound pose and is undoable.
   **Detach bone** removes its current connections without changing its position; Shift-drag
   can attach it again. Esc cancels a drag before stopping a drawing tool or closing an idle window.
8. **Save pose**, assign saved poses to animation frames, then **Generate frames**. The generated
   timeline uses the same joint infill, radius and interpolated pose as the workspace preview.
   Save the image to persist the document. **Save rig** also retains joint radius/pin preferences
   without replacing the original bind geometry.

Pins used in both bracketing poses constrain intermediate frames. Connections deliberately removed
in saved poses interpolate in world space instead of silently reattaching to the original rig.

## What changed technically

- Added a shared pose editing solver for explicit propagation, shared anchors and pinned centres.
- Added controls for transformations, propagation, exact values, joint editing and pose history.
- Changed rasterized bone stretching to scale along the bone axis while preserving thickness.
- Added optional joint patches from original RGBA pixels, limited to empty destination pixels
  within each attached joint circle. Changing circle radius changes coverage, not source scale.
- Aligned interpolated joint centres with the sampled bone endpoints. Preview, Apply pose and
  Generate frames all supply posed joint data to the rasterizer.
- Added persisted joint pins and an infill setting. Older rigs default to infill enabled.
- Kept creation/binding guards independent of asynchronous preview cache readiness.
- Fixed native viewport docking so the new controls remain visible above the canvas.

Infill is limited to available original artwork. It cannot invent a hidden shoulder or guarantee
arbitrary large deformations without seams. Circles must be connected to bones to supply a patch;
a nearby unconnected circle does not establish an attachment. Existing opaque white pixels in
the bound artwork remain opaque. This update does not add weighted meshes or inverse kinematics.

## Verification

Published build: [`20260908-155625-78831fac`](../../TestResults/Builds/20260908-155625-78831fac/BuildSummary.json).

Command: `Build.bat --quick --test image-features --backend dx11`.

- Release publication and warnings-as-errors compilation passed.
- **120 focused image checks passed**, including nine rig-control regression cases covering
  infill/alpha preservation, axial stretch, propagation, reversed bone orientation, dialog
  movement/resize/undo/pins, small-window layout, persistence, pinned/detached interpolation,
  and generated-frame agreement with the preview.
- DX11 smoke, published Studio startup/shader compiler, and package consistency checks passed.
- Quick Build completed in **79.26 seconds**. DX12, Vulkan, OpenGL and Software smokes and the
  complete regression suite were not rerun for this focused update.
- Read-only review of the saved 696 × 1104 mannequin rig used copies and no-op save callbacks.
  The default pose stayed unchanged. Saved poses 1 and 2 filled 486 and 252 transparent gap pixels
  respectively. The neck seam was visually checked in the resulting PNGs.
- The actual rig workspace was captured and inspected at 1280 × 850 and 900 × 660. Controls fit
  above the viewport without being hidden by the native rendering surface.

Evidence:

- [Workspace at 1280 × 850](../../TestResults/RigPoseControls/MannequinRelease/rig-workspace-1280.png)
- [Workspace at 900 × 660](../../TestResults/RigPoseControls/MannequinRelease/rig-workspace-900.png)
- [Saved pose 1 before infill](../../TestResults/RigPoseControls/MannequinRelease/pose-1-before.png)
- [Saved pose 1 with infill](../../TestResults/RigPoseControls/MannequinRelease/pose-1-infill.png)
- [Saved pose 2 with infill](../../TestResults/RigPoseControls/MannequinRelease/pose-2-infill.png)

## Nearest-app comparison and scope

For this skeletal-animation subsystem, Spine is the closer reference than a pixel drawing editor.
Its official tools guide describes dedicated transforms, numeric entry and compensation to control
how children follow. Genesis now exposes corresponding everyday choices through Action, Length,
Angle and Affect, although its shared-joint constraint behavior is different.
Source: [Spine tools and compensation](https://esotericsoftware.com/spine-tools).

Spine also provides mesh vertex weights and manual weight adjustment. Genesis currently uses
rigid pixel ownership plus joint patches; this is not equivalent deformation quality.
Source: [Spine weights](https://esotericsoftware.com/spine-weights).

| Review area | Result for this update |
| --- | --- |
| Move, rotate and resize attached bones | Passed focused interaction checks. |
| Choose local or connected motion | Passed solver and dialog checks; scope is visible. |
| Repair the supplied neck seam | Passed visual inspection of saved mannequin poses. |
| Discoverability and layout | Numbered stages, common controls, explicit scope and canvas hints; inspected at two sizes. |
| Preview-to-generated-animation consistency | Passed frame-byte comparison and persistence checks. |
| Professional skeletal-editor parity | Not claimed: no weighted mesh editor, full IK or animation curve editor in this update. |

This completes the reported control/seam repair pass. Keep the image animation system under
review while the user retests the real posing workflow; do not advance to the next editor on a
claim of full third-party parity.

## User retest

- Load the mannequin's saved tilted-head pose with **Fill joint gaps** on and off.
- Move the neck/head from the middle, rotate from each tip, and extend/shrink using Resize and Length.
- Compare **Only this bone** with **Connected chain** on an arm or leg; watch the highlighted scope.
- Move a joint, change its radius, pin it, then manipulate a connected bone and undo/redo.
- Save two poses, generate an animation, play it from the main image timeline, save and reopen.
