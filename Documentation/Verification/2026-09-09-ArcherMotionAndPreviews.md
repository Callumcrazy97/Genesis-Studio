# Archer motion import and frame previews — 9 September 2026

## Correction

Reproduced the reported distortion using the supplied `Erika Archer.dae` T-pose and
`Standing To Crouched.dae` from `F:/Development/Game Development/Shared/Models/Mixamo/Characters/Archer/`.
The converted motion file stores its first standing pose as its default node transforms.
The previous importer treated those defaults as a rest pose and applied separate world-space
corrections to every joint. In the reproduction, the forearm's local translation changed by
about 30 source units, separating connected parts.

Motion import now transfers authored local transforms between matching joints. Extra hierarchy
nodes are handled relative to the nearest matched ancestor; unmatched target joints retain
their parent-relative rest transforms. The target's geometry, inverse bind matrices and weights
are retained. This follows Old Genesis's local-pose sampling/application in
`GltfAnimationImporter.cs` and `ModelViewportScene.cs` under its ModelViewer folder.
Name matching and the 80% animated-joint compatibility check remain in place. This supports
compatible skeletons; it does not retarget arbitrary proportions or bone coordinate systems.

Already imported clips contain baked transforms from the earlier importer. Reimport the
original animation file through **More Options → Import Model as animation** to obtain the
corrected clip. Existing clips are preserved and duplicate names receive a suffix.

## Timeline

Model Viewer and Model Editor now share a scrollable filmstrip with a front-view image for
each animation frame, frame numbers, pose-key markers and a highlighted current frame.
Click or drag the images to scrub; use Left/Right, Home/End to step, and the wheel or scrollbar
to browse. Playback keeps the selected frame visible. The wider animation dropdown exposes
longer clip names.

Preview rendering uses a separate software renderer and a private mesh snapshot, with CPU
skinning from the canonical palette. It does not rotate the main camera or modify editable
geometry. Only visible/nearby frames are requested; requests are bounded and up to 96 images
are retained. Clip changes and geometry edits invalidate previews; closing the control cancels
the worker. Framing and lighting stay consistent across the clip. The Rig/Poses/Animations
window retains its separate authoring ruler in this change.

## Validation

The focused Model-system run passed **43 checks and 39 captures** in
`TestResults/ModelArcher/ModelSystem`. The exact Archer reproduction preserves all matched
local transforms across all 20 frames. Checks cover save/reopen, standing/crouching pixel
differences, thumbnail clicks and keyboard stepping, clip changes, a 150-frame Editor clip,
cache eviction/regeneration and independence from the main camera and editable mesh.

Full Build **`20260909-124922-ec34a58f`** passed **441 checks, 160 captures and all five
renderer smokes** (DX11, DX12, Vulkan, OpenGL, Software), with **no skipped backend**, in
**304.96 seconds**. Package, startup and shader-compiler checks passed. Studio and its
matching Player were promoted to `Genesis Application/`. All eight changed source/test
files match the final build inputs. Captures use the unattended repository harness,
without Computer Control.

Evidence: `TestResults/Builds/20260909-124922-ec34a58f/BuildSummary.json` and `Tests/results.json`
under that run. The gallery's after captures come from this successful Full run.

Two earlier runs were not published. `20260909-123647-ba4db5e1` had one renderer-factory
architecture failure; previews now use `RenderControllerFactory` through `IRenderController`.
`20260909-124314-c32f5d43` had one Player icon probe failure: its 10-second deadline expired
during cold startup (13:45:06 launch, 13:45:16.428 shader-ready). The test now shares the
existing bounded startup allowance of up to 30 seconds and still requires correctly sized
small and large icons. Both unsuccessful runs skipped renderer smokes after the regression
gate failed; the successful final run repeated the full coverage.

See the [before/after capture gallery](../EditorReview/2026-09-09/archer-motion/index.html).
Model remains the active editor; this correction does not declare the complete Model system finished.
