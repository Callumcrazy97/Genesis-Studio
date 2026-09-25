# Model tools and drawn rig workflow — 9 September 2026

## Changes

The Viewer and Editor now use the Image Editor's command-strip styling and the same
`CollapsibleSection` controls. Rigging, Posing and Animation use its `ImageRigPages` control.
The user-supplied Gemini mockups informed the stronger section boundaries, grouped buttons and
frame ruler; the requested Image Editor workflow remains the implementation reference.

**Import / Replace** fills the current model resource. It retains that resource's path and metadata
identity, imports its owned source/dependencies, validates geometry, then replaces its canonical
asset and refreshes the open document. It does not create a second project-tree model. Invalid
imports leave the previous descriptor and canonical model intact. Previous imported sources are
retained in the resource's data folder. Import conversion formats and Blender requirements are
unchanged from the [previous report](2026-09-08-ModelViewerReset.md).

Sculpting now moves vertices at identical positions together across UV/normal splits and 16-bit
mesh chunks. It uses radius-relative displacement, gathers smooth-brush neighbours before editing,
and updates normals. One stroke records all affected chunks for undo/cancel. Colouring changes
vertex colour without changing geometry. This directly addresses the torn fox in the user's report.

## Tool layout and behaviour

- **Current Tool:** radius, strength/opacity, drawing plane and colour.
- **Sculpting:** Brush, Line, Smooth; Cube, Sphere, Cylinder; Square face, Circle face, Triangle face.
- **Tools:** Push, Pull, Colouring Tool (renamed from Vertex Paint).
- **Selections:** Wand, Lasso, Region. Ctrl adds; Shift subtracts; Escape clears/cancels.

Brush and Line sculpt existing vertices. Push/ Pull displace along the shared surface normal.
Solids and faces are placed by dragging on XY, XZ or YZ; choosing a plane also selects a matching
camera view. A square face has two triangles. Placement previews its footprint and commits on
release; Escape cancels. Colouring and sculpting respect the active vertex selection. Wand selects
connected geometry, including chunk boundaries. Region/Lasso select projected vertices through
the model, so back-facing vertices can also be included. Transform selection applies XYZ edits
to the selected vertices, or the selected mesh when no vertex selection exists.

The right panel contains Preview, Layers / Mesh groups, Onion Skin and Material. Group visibility
is a preview setting, not a promise of complete layer compositing or hierarchy authoring.
The bottom timeline provides a frame ruler, pose-key diamonds, scrubbing, playback and Loop.

## Animation workflow

There are no template category, preset animal or automatic root controls in the Model workspace.
**New rig** starts empty. **Draw joints** places a centre and a radius by dragging outward;
**Draw bones** connects drawn endpoints, reusing joints under those endpoints. Joint radii are
saved with the model. Existing imported skeletons, skin weights, poses and clips remain readable.

Click **Rig mesh** when the skeleton is ready; the workspace opens Posing. In **Move / rotate**,
the bone's tips rotate around the opposite endpoint and its middle translates. Move, Rotate and
Resize are also explicit action choices. **Only this bone** and **Connected chain** control propagation;
shared endpoints still represent the same joint. Shift-drag in skeleton layout aligns a handle to
an existing joint. Undo and Escape restore the complete gesture.

Save poses, assign them to frame numbers, then **Generate frames**. **Apply to model** returns the
clip to the main Editor timeline; File → Save persists it for the Viewer and Runner. Cancel work
discards the entire dialog draft. Changing a bound skeleton requires binding again before posing.

## Verification

The focused `--test model-system` run passed **26 checks and 21 captures** in
`TestResults/ModelRefinement/Reviewed`. New cases cover current-resource import and failure recovery,
welded seams across chunks, drag-created faces, selection-limited colouring, direct bone endpoint
rotation/cancellation, persisted joint circles, and a disposable copy of the user's fox.
The fox's shared positions remain joined after sculpting; Colouring preserves all positions.
Captures were inspected using the repository's unattended rendering harness. No Computer Control
or interaction with the user's running apps was used.

Full Build `20260908-232336-d249ed87` passed **415 regression checks and 125 captures** in
**502.43 seconds**. DX11, DX12, Vulkan, OpenGL and Software smokes all passed, including their
winding and persistent mesh-update probes; no backend was skipped. Package consistency, published
startup and shader-compiler checks passed, and Studio with its matching Player was promoted to
`Genesis Application/`. The final application code is the code tested by that Full Build.

The earlier expanded focused run failed because its fox test aimed at empty space at the centre
of the side-view bounds. The fixture now finds the nearby visible surface before exercising the
pointer stroke. That failure did not publish a package. A temporary console-output gap during Full
was investigated with a managed process trace: visual tests were still progressing, and Full
completed successfully. No unrelated runtime change was needed.

## Remaining work

Model remains the active editor. Further geometry/UV editing, texture-pixel painting, manual skin
weights, retargeting, dependency-aware reimport and richer hierarchy controls remain separate work.
Sculpt brushes do not remesh or create volume from an empty canvas. Joint circles are placement and
snapping guides; this update does not add IK constraints or generate new geometry at joints.
Complete Model acceptance before Room. Cross-editor embedding/physics and AI/LLM/BYOK remain last.
