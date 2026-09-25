# Model winding, empty resources and import — 8 September 2026

## Changes

New model resources start empty. Opening, saving or reopening one no longer creates a cube.
Legacy documents that explicitly name a primitive still load that geometry. Room and runtime
loaders agree with the editor about empty resources.

Both **Model Viewer** and **Model Editor** have an always-visible **Import Model…** command on the
primary toolbar beside the workspace title, as well as **File → Import Model…**. The previous layout
rebuild discarded the toolbar import button. Original Genesis placed **Load…** alongside **Edit**
in its model properties group; the current version keeps importing and opening the Editor on the
primary model command row. Reference: `Old Genesis/GMS Style/GameManager/Editors/ModelEditorControl.cs`.

Choose a **`.glb`** or **`.gltf`** file. Import creates a new project model with its own source and
dependencies, validates conversion, then opens it. Importing from the Viewer opens a Viewer;
importing from the separate Editor opens another Editor. The original document and its unsaved
work remain intact. The separate Editor previously had no handler for this navigation request.
Malformed/unsupported imports report an in-app error. OBJ/FBX conversion remains a separate Model
backlog item; the dialog does not advertise unsupported formats.

## Winding diagnosis and repair

Genesis's primitive triangles and glTF data wind counter-clockwise from outside, with geometric
cross products agreeing with outward normals. The installation and renderer defaults were still
clockwise. A closed cube with a green near face and red far face demonstrated that DX11, DX12,
OpenGL and Vulkan all rendered the red interior under that default. Their counter-clockwise
setting rendered the green exterior. This was a shared convention problem, not four independent
backend index-buffer defects.

A second probe using the actual runtime `Camera3D` exposed the other half of the inconsistency:
its left-handed projection reverses screen winding relative to the editors' right-handed cameras.
The renderer now translates world-space winding to the active projection when choosing raster state.
Both perspective and orthographic paths are covered; LH light-camera shadow passes use the same
conversion. Runtime navigation and camera matrices are unchanged. Before this conversion, the
runtime-camera probe still produced the red interior even though the editor-camera probe passed.

The shared mesh defaults, fresh settings, Preferences fallbacks, Studio/Player preference bridge,
and backend-parity harness now use counter-clockwise. Settings schema 3 migrates the previous
installation-wide clockwise default once and records a migration notice. Current-schema explicit
preferences and per-model/object winding overrides remain available and are preserved. No existing
mesh files are blindly rewritten.

The shared editor checker/plain floor and terrain preview had retained downward-facing triangles
to compensate for the previous default. They now face upward, matching their normals and runtime
terrain meshes. Physics collision meshes retain their physics API's separate winding convention.

Software also inferred GUI rendering from every orthographic projection, disabling culling and
depth even for 3D models. It now respects the draw's raster/depth state for orthographic and
perspective cameras. GUI passes provide their own states.

Mirrored baked parts reverse triangle indices and use inverse-transpose normals. Mirrored imported
node bind transforms preserve outward winding. Reflected draw transforms select the opposite raster
winding; mixed reflected instance batches take the per-instance submission path so their states
cannot be combined incorrectly. Explicit clockwise/counter-clockwise overrides still behave distinctly.

OpenGL's enum mapping remains direct: `GL_UPPER_LEFT` already adjusts the signed-area calculation.
The misleading comment implying another inversion was corrected. See the official
[ARB_clip_control specification](https://github.com/KhronosGroup/OpenGL-Registry/blob/main/extensions/ARB/ARB_clip_control.txt)
and [Vulkan front-face definition](https://docs.vulkan.org/refpages/latest/refpages/source/VkFrontFace.html).

## Verification

- `--test model-intake`: seven cases exercise empty Viewer/Editor creation and save/reopen, visible
  import controls, preservation of explicit legacy geometry, GLB import/navigation/ownership,
  actual Viewer/Editor exterior pixels, settings migration, mirrored baked-part normals and upward
  terrain/floor surfaces.
- Every backend smoke now checks 24 face-colour probes: default/CW/CCW × normal/reflected ×
  editor/runtime camera × orthographic/perspective. Default and CCW must show green exteriors; explicit CW must show red.
  A nonempty image alone can no longer pass an inside-out model.
- Focused intake checks and all five enhanced backend smokes passed before Full Build.
- Captures use the repository's off-screen WinForms/GPU harness; no Computer Control was used.
  The animated GLB fixture is a flat cloth sheet; its materials now explicitly specify double-sided
  rendering so Object-preview animation visibility does not depend on viewing a sheet from its front.
  Production imports continue respecting the source material's own double-sided setting.
- The five renderer golden images were inspected and updated for the corrected exterior faces.
  The final shadow-camera correction also changed Software's recorded shadows. Face-colour assertions
  independently verify winding; visual thresholds were not relaxed. These checks establish consistent
  face visibility, not complete visual feature parity between Software and the hardware backends.

Inspected captures: [empty Viewer](../EditorReview/2026-09-08/after/model-winding/empty-model-Viewer.png),
[imported model in Editor](../EditorReview/2026-09-08/after/model-winding/imported-model-Composer.png),
[closed cube in Viewer](../EditorReview/2026-09-08/after/model-winding/closed-cube-Viewer.png),
and [capture manifest](../EditorReview/2026-09-08/after/model-winding/capture-manifest.json).

Full Build **`20260908-205640-7f232e09` passed and published** Studio and its matching Player to
`Genesis Application/` in **327.95 seconds**: **405 regression checks, 131 recorded images**, all
five renderer smokes (24 face probes each), startup, bundled shader compiler and package consistency
checks. No renderer backends were skipped. See the
[build summary](../../TestResults/Builds/20260908-205640-7f232e09/BuildSummary.json).

The initial Full Build exposed the old floor winding, one-sided test fixture and obsolete image
baselines. A subsequent run was stopped before publication to add and fix the failing actual-runtime
camera probe. The published build includes all of those corrections and reruns the complete gate.

The overall Model system remains active; remaining work includes weight painting, hierarchy
reparenting, additional import formats and animation acceptance. Cross-editor embedding and
AI/LLM/BYOK remain in their deferred phases.
