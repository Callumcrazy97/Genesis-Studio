# Model rig placement inside the mesh — 9 September 2026

## Correction

Joint and bone drawing previously intersected the selected XY/XZ/YZ plane through the
whole model's bounds centre. A joint could appear correct from one camera angle but sit
outside the limb in 3D.

The reference was Old Genesis's `Editors/ModelRigging/RiggingViewportControl.cs`,
`RayIntersectsModel`: sort mesh intersections and use the midpoint of the first two.
The current implementation follows that placement rule with three additions:

- Shared triangle edges and coincident faces count as one depth boundary.
- Exact shared vertex positions connect UV seams and renderer chunks for picking;
  separate mesh parts are not paired to invent thickness across empty space.
- Picking converts the camera ray into model coordinates, including the preview pivot
  and transform, before finding the interior position.

This uses the rest mesh displayed on the Rigging page. It does not alter geometry or
replace existing authored joint positions automatically.

## Controls

**Draw joints:** click a visible body part to place the centre inside it. Drag outward
to size the circle. Radius editing stays in a view plane through that centre, including
when the circumference extends beyond the silhouette.

**Draw bones:** drag between points on the mesh. Both endpoints receive independent
interior depths. Starting or finishing on an existing joint connects to that joint.
The drag preview and the committed bone use the same coordinates.

Empty-space presses do not create floating joints. Releasing a bone off the mesh cancels
that placement and displays an in-app explanation. Orbit to reach obscured body parts.
**Rig mesh**, poses, frame generation and **Apply to model** retain their existing workflow.
The Plane selector remains available for moving and rotating the authored skeleton.

For an open surface with only one crossing, placement uses the surface. Meshes with
holes, intersecting shells or inconsistent connectivity do not guarantee anatomical
centres; this is an interior ray midpoint, not an anatomical auto-rigger.

## Verification

The focused Model-system run passed **30 checks and 28 captures** in
`TestResults/RigPlacement/Focused`. New regressions cover separate foreground/background
parts, duplicate triangle hits, chunk and UV boundaries, reversed winding, transformed
models, open surfaces, empty-space rejection, actual pointer gestures, radius previews,
independent bone endpoint depths, skin binding and save/reopen.

A disposable copy of the user's fox was used to place joints from a side view, then
capture the same coordinates from the front and an angled view. Those captures and the
two-depth fixture were inspected with the repository's unattended capture harness.
No Computer Control was used and the source test project was not modified.
See the joint-depth and bone endpoint views in the
[capture gallery](../EditorReview/2026-09-09/model-tools/index.html).

Full Build **`20260909-084559-644ab0aa` passed 419 checks and 132 captures** in
**583.90 seconds**. All five renderer smokes passed: DX11, DX12, Vulkan, OpenGL and
Software, including their winding probes; none was skipped. Package, startup and bundled
shader-compiler checks passed. The build promoted Studio and its matching Player to
`Genesis Application/`. The gallery's placement images are from this final Full run.

Run with `Build.bat --full`. The optional production-fox cases use
`GENESIS_MODEL_REVIEW_SOURCE` pointing to the local GLB; regular analytic and pointer
placement regressions always run. The focused command is `--test model-system`.
The source-file hash snapshot in `.validation/rig-placement/source-sha256.json` remained
unchanged during verification.

An initial Full run, `20260909-084124-fa27ef9e`, was stopped when a separate Quick Build
started, to avoid concurrent builds against shared outputs. It is not a completed
verification result. The successful Full run began after the other builds finished.

## Scope

Model remains the active editor. This correction does not claim overall Model completion.
Cross-editor embedding, physics integration and AI/LLM/BYOK work remain deferred.
