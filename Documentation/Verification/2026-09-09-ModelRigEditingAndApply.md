# Model rig deletion, binding and Apply — 9 September 2026

## Controls

The right-hand Rigging page now contains only these groups:

| Group | Actions |
|---|---|
| Rig Management | New, Load, Save Rig, Delete; named rig list |
| Drawing | Draw Bone, Draw Joint, Select |
| Binding | Bind To Mesh, Unbind |

Rigs are retained in the model's library during drawing, binding and Apply; Save Rig
also saves the current layout explicitly. New and Load preserve the previous working rig in that
library. The active rig name survives save/reopen; older models reuse a matching library
rig, so another Apply does not create a duplicate. Loading a rig leaves binding as a
separate action. Unbind keeps the skeleton,
poses and clips while clearing mesh skin weights. Apply can retain an unbound skeleton
and does not start binding implicitly. Project persistence remains File → Save.

Select a bone body and press **Delete** to remove that connection while keeping its
endpoint joints and other branches. Select a joint centre or circumference and press
Delete to remove the joint and its incident connections. Neighbouring joints and their
branches stay in place. Saved poses and animation frames are converted to the remaining
hierarchy, and stale skin weights are cleared before the next bind. Delete in a text
field retains normal text-editing behaviour. A click without a drag no longer records
an unnecessary pose edit.

## Stall correction

Two expensive paths were identified:

1. Binding classified every rig with nine or more joints as a quadruped. Its per-vertex
   weight calculation repeatedly scanned the entire mesh to infer body orientation.
   A 22-joint dense regression stalled and was stopped. Drawn/custom rigs now bind to
   their actual parent-to-joint segments, with no bone-count template classification.
   Explicit legacy templates retain their own mapping, with orientation computed once
   per binding operation.
2. Working copies, Apply and undo snapshots repeatedly serialized complete meshes to
   JSON. Geometry and dense frame matrices now copy their value-type arrays directly.
   Metadata remains independently copied. Opening the workspace and binding also avoid
   redundant whole-mesh copies.

The user's fox canonical file was about 69 MB. The final Release regression run measured:

| Scenario | Apply duration | Allocated managed memory |
|---|---:|---:|
| Synthetic 129,120 vertices, 22 joints, 60 frames | 5 ms | 34.1 MiB |
| Imported fox, 120,272 vertices, 22 joints; button and transfer | 22 ms | 54.5 MiB |

These are local measurements, not guaranteed timings on other hardware. The fox editor
continued rendering after Apply. The binding stall was reproduced; the original OS
black-screen event was not independently reproduced.

## Verification

The focused `--test model-system` run passed **35 checks and 32 captures** in
`TestResults/RigEditing/ModelSystem`. It covers typed-copy fidelity/isolation, real
Delete shortcut dispatch, text focus, bone/joint deletion, exact option groups,
bind/unbind, unbound Apply, automatic rig management, save/reopen, dense Apply allocation
limits, undo/redo and the production fox through Apply and rendering.

The final `Build.bat --full` run **20260909-112821-1bf300fa** passed **433 checks,
153 captures and all five renderer smokes** (DX11, DX12, Vulkan, OpenGL, Software),
with no skipped backend, in **360.87 seconds**. This includes active-rig-name
persistence and compatibility with older saved libraries, saved pose/frame preservation
after deletion, and binding custom rigs with more than nine joints. Package,
startup and shader-compiler checks passed. Studio and its matching Player were promoted
to `Genesis Application/`; all 13 changed source/test files match those used by the build.

Evidence: `TestResults/Builds/20260909-112821-1bf300fa/BuildSummary.json` and
`Tests/results.json` under that run. Captures use the unattended repository harness;
no Computer Control is used, and the user's source test project is not modified.
See the [capture gallery](../EditorReview/2026-09-09/rig-editing/index.html).

Model remains the active editor. This correction does not mark the complete Model
system finished or start cross-editor/physics/AI integration.

## Save Rig button restoration

Save Rig is restored beside New, Load and Delete. It saves the current layout into
the working model's rig library without closing the window; Apply to model followed
by the model's Save command persists it. Cancel work retains its existing discard
behaviour. Saving an empty rig displays an in-app message.

Quick run `20260909-133935-ef36194c` compiled without warnings or errors and passed
all **4 focused rig-editing checks with 2 captures**, including the actual Save Rig
button, library loading and save/reopen. The captured Rig Management row was inspected
and all four buttons fit. Evidence is in that run's `Tests/` directory.

Publication did **not** complete: startup smoke exceeded 60 seconds in that run and
in Quick retry `20260909-134603-86cd82b0`, before producing a Studio log. Both package
audits passed; neither run was promoted. The retry skipped regression, and neither
run repeated Full or renderer smokes. Those attempts left the installed build unchanged; this
button correction was then verified in source and the test build only.

Subsequent Full build `20260909-140428-670876a6` passed 444 checks, 163 captures,
startup and all five renderer smokes and published Save Rig with the
[bone control and skinning correction](2026-09-09-ModelSkinControls.md).
