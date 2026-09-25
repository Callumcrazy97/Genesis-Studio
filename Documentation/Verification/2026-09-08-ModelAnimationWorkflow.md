# Model animation workflow and comparison — 8 September 2026

## Outcome and scope

The Model Editor now uses **Animation → Rigging / Posing / Animation** to open one 3D workspace with
three steps, following the accepted Image Editor. The old preset-only wizard is removed. The
separate Model Viewer remains the entry point for inspection and playback; authoring stays in the
Model Editor. This delivers the pose-based animation workflow, **not a full Model-system parity
sign-off**. Room remains next after the remaining Model work.

## How to use it

1. Open a Model and choose **Edit Model → Animation → Rigging**. An imported skeleton and its existing
   weights are retained. Alternatively, use a humanoid/quadruped template or New skeleton. Replacing
   a skeleton clears its poses and clips in the working copy; Cancel work discards that replacement.
2. Select a joint in the hierarchy. **Add child joint**, or enable **Draw bones** and drag to place
   its child on the **XY, XZ or YZ** plane through the selected joint. Each release selects the new
   child, allowing a chain to be drawn. Use Move and local XYZ values for depth/layout corrections.
   Delete joint removes a leaf; delete its children first. Saved rigs expands the model-local library.
3. **Bind mesh** applies the edited rest skeleton using one, two or four skin influences. The Rig
   step keeps the source mesh still while laying out joints. A changed layout must be bound before
   switching to Pose or applying. Rebinding is explicit; merely opening an imported rig preserves
   its weights. Review existing motion after changing a rest skeleton or rebinding.
4. In **Pose**, select a joint and use Move / Rotate / Scale or local XYZ fields. **Follow children**
   moves its branch; switching it off compensates direct children so descendants keep their world
   transforms. Save as new, Update pose, Load pose, Rename and Delete manage named poses. References
   use stable IDs. Deleting a pose used by an animation explains which assignments need changing.
5. In **Animate**, name the animation, set length/FPS/Loop, and assign poses to frame numbers. For
   example, frame 1 = Default, 30 = Arm raised, 60 = Default. **Linear**, **Smooth**, and **Hold** govern
   the transition to the next assignment. Quaternion slerp handles 3D rotations; XYZ translation
   and scale interpolate separately. Before/after the assigned keys, the first/last pose is held.
6. **Generate frames** creates a normal runtime clip and selects it in the workspace timeline.
   Regeneration replaces that recipe's generated clip, retaining unrelated clips. **Apply to model**
   transfers the working copy and selects the generated clip in the main timeline. **File → Save**
   persists the rig, poses, assignments, and generated frames in the canonical `.gmodel` asset.
   The Viewer can then play the same saved clip. Pose-assignment diamonds appear on its frame ruler.

Undo/redo supports joint pose changes and applying the whole workspace. Esc cancels an active
gizmo/drawing gesture, then exits drawing mode; Esc while idle and Cancel work discard the workspace.
The dialog cannot write the model through a hidden Save shortcut. Validation failures appear in
its status area, and failed generation leaves the previous playable clip intact. Generation is
bounded to 10,000 frames and two million joint/frame matrices; it currently runs synchronously.

## Original Genesis versus current Genesis

Reviewed reference source under `Old Genesis/GMS Style/GameManager/Editors/`, particularly
`ModelRigging/ModelRigEditorWindow.cs`, `ModelRigging/AnimationBuilderDialog.cs`,
`ModelViewer/ModelImporter.cs`, and `ModelEditWindow.cs`. Legacy code was inspected, not copied into
the active application. Its rig-window capture uses an empty fixture and demonstrates layout only.

| Area | Original Genesis | Current Genesis after this change |
|---|---|---|
| Entry points | Model properties/view window, Edit, separate rig and animation surfaces | Separate Viewer/Editor, one Animation menu and three workflow steps |
| Skeleton authoring | Draw / Move / Rotate / Navigate; templates; bone hierarchy | Connected drawing on explicit 3D planes, joint addition/leaf deletion/rename, XYZ gizmos, templates and model-local rig library |
| Rest pose and binding | Bind Skin, explicit bind reference | Separate rest-layout preview and posing; explicit rebind; retained imported weights until rebound |
| Skin editing | Paint Weights and geodesic binding controls in legacy rig UI | Automatic one/two/four-influence binding; **manual weight painting still missing** |
| Hierarchy control | Drag-reparent with cycle prevention | Joint parenting on creation; Follow children or preserve descendants; **reparenting and constraints still missing** |
| Pose library | Create/load/rename/delete; thumbnail gallery | Save/load/update/rename/delete; stable references and text list; **thumbnail gallery still missing** |
| Animation creation | Ordered saved poses, one shared “frames per pose” stride, quaternion interpolation | Arbitrary one-based frame assignments, per-transition Linear/Smooth/Hold, FPS, Loop, retained editable recipes |
| Timeline | Clip selection, transport, scrubber, capture pose at current frame | Persistent Viewer/Editor timeline, transport, frame edits, pose markers and generated clip selection |
| Animation import | Separate Import GLB animation action | Existing glTF/GLB model animation import; **animation-only import/retargeting still missing** |
| Source formats | Importer dispatches glTF/GLB, OBJ and FBX | glTF/GLB only; **OBJ/FBX and reimport parity still pending** |
| Current additional authoring | Not exhaustively verified for parity in this pass | Existing Mesh mode includes topology, UV projection, material assignment, LOD and pivot controls; these still need the wider Model acceptance audit |

The 2D pixel-infill operation is not copied into 3D. A 3D mesh deforms through joint skin weights;
good weighting and sufficient geometry remain necessary to keep a surface convincing. Manual
weight editing and broader deformation-quality checks remain Model tasks.

## Visual comparison

The [comparison gallery](../EditorReview/2026-09-08/model-animation-comparison.html) links actual
Genesis captures and the official Blockbench interface reference. Original Genesis uses dense grey
group boxes, large fixed action buttons, and two always-visible pose/animation galleries. Several
legacy labels clip in the captured fixture. Current Genesis groups actions by stage, uses the same
dark surfaces as the accepted Image Editor, preserves a dominant viewport, and puts transport below
it. The new compact XYZ inspector avoids covering controls beneath the libraries. Populated
captures were inspected at 1440 and 1080 pixels wide.

[Blockbench](https://blockbench.net/) is the nearest low-poly authoring comparator. Its
[official interface guide](https://blockbench.net/wiki/guides/blockbench-overview-tips/) documents
mode-dependent panels, a compact toolbar/outliner, and both a channel timeline and graph editor.
Genesis now has a clearer guided pose workflow, but remains more text-heavy and lacks that mature
per-channel curve presentation. This is an assessment of our captures against published reference
material, not a claim that an installed Blockbench build was functionally tested. A Windows firewall
prompt obscured the native browser capture; it was not approved or dismissed. Starting a separate
headless browser capture was rejected by automatic approval review (“blocked by policy”). The
gallery retains the official reference URL without downloading it.

**Inspection decision:** the new pose workflow is ready for user testing. Do not mark the entire
Model Editor complete or advance to Room based on this change. Finish weight editing, hierarchy
control, import/reimport, transform/brush reliability, and the broader Model visual/functional gate.
Cross-editor shader/physics embedding remains in the final integration phase, then AI/LLM/BYOK.

## Verification

Focused command: `Genesis.Application.Headless.exe --test model-animation --output <directory>`.
The custom suite is also included in Full Regression. Six cases cover mixed-axis rotation inspector
round trips, quaternion/hold/smooth generation and endpoints, descendant preservation,
invalid-assignment safety, pose→clip→save→Viewer reopen, pause/resume and non-looping restart,
undo/redo, 3D joint placement/binding, and cancellation with unchanged project files.
Five populated workflow captures are written into the report.

The visual check also found and repaired an existing 60-FPS constant in skeleton-overlay/CPU-skin
evaluation, so those now use the clip's FPS and Loop settings. Playback advances using elapsed time
instead of assuming one render equals 1/60 second. The numeric inspector uses the same Y/X/Z rotation
order as its gizmos. Framing includes the current posed mesh, and skeleton picking/gizmos respect an
authored model pivot. Workflow captures use the harness's GPU readback alongside rendered WinForms
controls; DrawToBitmap alone omitted the viewport and was unsuitable for this comparison.

Full Build **`20260908-195847-0313fd0e` passed in 337.48 seconds**, with **398 regression checks**,
package/startup validation and all five explicit renderer smokes: DX11, DX12, Vulkan, OpenGL and
Software. No backends were skipped. The build was promoted to `Genesis Application/`.
See [BuildSummary.json](../../TestResults/Builds/20260908-195847-0313fd0e/BuildSummary.json).

Subsequent visual inspection repaired the capture helper, shortened clipped workflow hints, and
removed authoring gizmos/onion-skin ghosts and pose manipulation from the Viewer. Final publication
**`20260908-200947-44335401` passed Quick Build in 90.46 seconds**, including all **six focused
workflow checks**, **five GPU-inclusive captures**, all five renderer smokes and package/startup
validation. The complete Full result above precedes these narrow final corrections; Full regression
was not repeated after them. See the [final BuildSummary.json](../../TestResults/Builds/20260908-200947-44335401/BuildSummary.json).

An intermediate publication was correctly blocked by a visual assertion that required saturated
gold mesh pixels. Inspection of the saved diagnostic showed intact, desaturated geometry under
Viewer lighting. The check now distinguishes warm/cool mesh colours from the neutral background
without relying on orange authoring overlays. The final focused rerun passes that check.

The [capture manifest](../EditorReview/2026-09-08/after/model-animation/capture-manifest.json) records
the five final images and hashes. Their gallery copies are stored with the documentation rather
than depending on a disposable test-output directory. Current captures cover populated Rig, Pose,
Animate, narrow Animate and reopened Viewer states; real high-DPI and complex-character deformation
acceptance remain unverified. No Computer Control was used after the user's instruction to stop it.
