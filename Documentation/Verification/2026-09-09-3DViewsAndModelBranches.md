# 3D View options, shared joints and Model sidebar — 9 September 2026

## View controls

The View menu now provides **Normals, Wireframe, Shadows, Lighting and Depth** in
Model Viewer/Editor, Room, Terrain, Shader, Particle and Physics. The shared rig
viewport receives the same controls. They are enabled for 3D previews and disabled
in 2D. These are preview settings; toggling them does not save changes to resources.

- Normals displays surface normal directions as RGB colours.
- Depth displays camera depth in grayscale: nearer surfaces are lighter. Previously
  the debug shader used the light's depth. The grayscale uses a contrast remap.
- Normals and Depth are mutually exclusive; clicking the active mode restores shading.
- Wireframe displays mesh edges. Shadows and Lighting are independent toggles;
  visible shadows require lighting and suitable receiving geometry.
- Existing lighting presets/settings remain available. Second-camera viewports share
  the primary viewport's inspection settings.

Diagnostic shading temporarily overrides authored material/preview shaders. Returning
to shaded mode restores them. Software rendering now implements normals, camera depth
and triangle-edge wireframe, alongside the four hardware backends. Turning lighting
off clears the preview's lighting weight as well as its enable flag.

## Shared joints

Draw bones toward or away from a joint to attach multiple limbs. Existing connections
are retained. Joining two disconnected skeleton branches reroots the smaller branch
at the attachment point, preserving its edges and the larger skeleton's root.
Current transforms, bind transforms, saved poses and generated animation frames are
converted to the new hierarchy without changing their world positions/orientations.

Redrawing an existing bone is a no-op. A connection that would create a loop is
rejected with an in-app message before changing the skeleton. Click **Rig mesh** after
editing the skeleton, then continue with the existing Posing/Animation workflow.

## Viewer sidebar

Source and Model properties measure their text instead of using fixed clipping
heights. The hierarchy takes the remaining height. Sections stack without overlap,
and shorter windows scroll. Drag the divider at the sidebar's right edge to change
its width. Collapse/expand and larger text retain a stable layout.

The checks also exposed repeated disposal of a Physics preview's simulation. Cleanup
is now idempotent, avoiding a second Bepu disposal error when its host closes.

## Verification

- Debug harness build: zero warnings and errors.
- `--test view-options`: **9 checks, 17 captures**, all passed in
  `TestResults/ViewOptions/Focused2`.
- `--backend-smoke all`: all five passed in `TestResults/ViewOptions/Renderers`.
  New pixel probes verify normal colours, depth changing with camera distance, and
  wireframe edge coverage on DX11, DX12, Vulkan, OpenGL and Software.
- Full Build **`20260909-101249-29de9d0a`**: **428 checks, 149 captures, no failures**
  in **455.26 seconds**. All five renderer smokes passed; none was skipped. Package,
  startup and bundled shader-compiler checks passed. Studio and its matching Player
  were promoted to `Genesis Application/`.

The focused checks exercise the real menu actions in each editor, 2D enablement,
rendered pixels, second-camera state, unchanged resource files, shared-joint branches,
saved pose/frame preservation, binding and save/reopen, sidebar resizing, larger text
and repeated collapse/expand. Captures use the unattended repository harness; no
Computer Control is used. The gallery uses the final Full run's captures; its sidebar,
diagnostic and branch images were inspected. See the [capture gallery](../EditorReview/2026-09-09/view-options/index.html).

Build evidence is in `TestResults/Builds/20260909-101249-29de9d0a/BuildSummary.json`
and `Tests/results.json` beneath the same directory. The 21-file source hash snapshot
in `.validation/view-options/source-sha256.json` remained unchanged during Full verification.

This is a bounded correction. Model remains the active system; the other editors'
overall completion and cross-editor embedding/physics/AI integration remain deferred.
