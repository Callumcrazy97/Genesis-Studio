# Animation, project stability and runner verification

## Changes

- Generate frames validates saved rig data independently of the asynchronous preview renderer,
  publishes generated cels and an animation tag into the Image Editor timeline, and reports failures
  in a dialog as well as the status line. The generated layer-pixel budget is 512 MiB, with a maximum
  of 600 frames. Exceeding either limit gives an actionable message. Generated frame identities remain
  available when switching between saved rigs in the same dialog.
- Embedded `bindPixels` values and overlong JSON strings are excluded from filesystem reference
  resolution. The original Studio log identifies `PathTooLongException` in
  `AssetDependencyGraph.ResolveAll`, called by `ProjectAssetMonitor`, at the time of the reported save
  crash and again while opening the project. This establishes a scanner failure; it does not establish
  that the saved project was corrupt. The user deleted that test project, so verification uses fresh
  fixtures instead of attempting recovery.
- Asset watcher publication is serialized. Watcher and subscriber failures report through Studio's
  Console; logging failures cannot themselves crash the application. Failed project-window construction
  leaves the previous window visible. UI/startup/fatal managed-error handlers provide in-app diagnostics.
- A full-image UV sentinel now maps to that image's packed atlas region. Previously the empty mapped
  rectangle triggered the shader's whole-texture fallback, drawing the entire atlas on every object.
- Editor-launched players return startup/runtime stderr and exit codes to Studio. Each launch is owned
  by its editor window; close/re-run stops it. Players watch the original parent PID and creation time
  and exit when that process disappears. Audio and networking dispose in the runner's `finally` block.
- Room Play launches actual gameplay in Genesis Player. Pause, Resume and Stop send commands to that
  player. The former visual-preview timer is no longer presented as live gameplay.

## Verification

- Focused Image Editor/Viewer suite: 111 checks passed, including the real Generate frames button,
  timeline/tag persistence, trim/save/watcher/reopen, selection, drawing, rig joints and cancellation.
- Runner lifecycle harness: start/pause/resume/stop; primary Room toolbar controls; managed startup
  error delivery; and termination after a parent exits without disposing the player all passed.
- Platformer gameplay and command-scene captures passed on DX11, DX12, Vulkan, OpenGL and Software.
  Visual inspection confirms a visible player and distinct coin, ground and platform sprites. These
  captures verify the reported atlas defect; they are not a claim of identical rendering features or
  pixels across every backend. Software's existing rendering limits remain.
- Full Build `20260908-144918-e9cc67da` passed: 372 regression checks, 120 images, all five renderer
  smokes, package consistency and published startup checks. The matching package was promoted to
  `Genesis Application/` in 384.03 seconds.
- Evidence: `TestResults/AnimationRunnerFix/`, including `lifecycle.log`, `ImageVerified/`, and one
  capture directory per backend; `TestResults/Builds/20260908-144918-e9cc67da/BuildSummary.json` records
  the release gate. No Studio, Headless or Player processes remained after verification.

## User retest

1. On a new test image, remove its background, make a rig, save poses, and assign them to frames.
   Click Generate frames and close the dialog. The selected animation tag and its generated frames
   should appear in the bottom timeline and in the Image Viewer.
2. Remove Blank Space, save, close the project and reopen it. Check pixels, bones, poses, tags and
   playback. A generation limit or invalid rig should show an in-app explanation.
3. Run the template with F5 using each backend. Check the player and several different object sprites.
4. Open a room and test Play, Pause, Resume and Stop. Gameplay opens in a separate Player window.
5. Close the room/project or Studio while its player is running. The owned player should exit too.

These fixes cover the reproduced managed failures and process ownership paths. Native device/driver
failures cannot be guaranteed recoverable; an editor-owned player's nonzero exit is still reported
back in Studio when its process terminates.
