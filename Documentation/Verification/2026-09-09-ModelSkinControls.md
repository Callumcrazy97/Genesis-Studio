# Model bone controls and surface binding — 9 September 2026

## Behaviour

In Move / rotate mode, the visible midpoint handle translates the bone; an endpoint
rotates it about the opposite endpoint. Move explicitly translates even when grabbing
a tip. Rotate preserves bone length, and Resize remains an explicit action. Midpoint
handles take precedence over overlapping joint circles. Affect retains the choice of
one bone or its descendants; Escape and Undo restore the original pose.

The default drag plane is View, fixed at the picked depth for the gesture. XY/XZ/YZ
remain available. An edge-on constrained plane rejects the drag with an in-app message
instead of magnifying a small mouse motion. Rotating a bone no longer rotates the
parent's skin basis: the shared endpoint can move, while the upstream orientation stays
intact. This addresses torso deformation caused by a limb rotation.

Bind To Mesh uses four local influences for drawn rigs. Weights propagate over welded
triangle edges using edge-length geodesic distances and local falloff. Nearby disconnected
limbs cannot exchange weights across empty space, and coincident positions across UV
seams/material chunks share weights. Storage retains one distance field at a time.

Old Genesis's `ModelSkinBinder` also used mesh adjacency, with hop counts and influence
pruning. Its bounding-box joint clamp was a separate import cleanup operation. This
change fixes selection, transforms and weights without imposing a movement-distance cap.

## Existing models

For a previously hand-bound model, open Rigging and click **Bind To Mesh**, then
**Apply to model** and **File → Save** to persist the replacement weights. Check your
joint placement before rebinding. Imported artist-authored weights are preserved unless
you explicitly bind again. Existing poses/clips are retained. Save Rig is also present.

Automatic binding is affected by skeleton placement and mesh topology; it does not add
inverse kinematics, anatomical joint limits or volume-preserving dual-quaternion skinning.
Extreme rotations and deliberate bone translation can still stretch an adjoining surface.

## Verification

Focused run `TestResults/ModelSkinControl2` passed 8 checks and 7 captures, including
oblique midpoint drags, isolated/chain rotation, parent orientation, separate limbs,
rest-pose identity, welded seams, the production fox, saving/reopening, Delete and Save Rig.
The fox bound 120,272 vertices in 74 ms locally. The captured head tilt was inspected;
these timings are measurements, not guarantees.

Full build **20260909-140428-670876a6** passed **444 checks, 163 captures and all five
renderer smokes**, with none skipped, in 524.02 seconds. It includes the additional
explicit Move/Escape assertion, the existing Archer motion-import test, Delete,
Save Rig and save/reopen. The fox bound in 53 ms in that run. Package, startup and
bundled shader compiler checks passed; Studio and matching Player were promoted to
`Genesis Application/`. All seven changed source/test files match their recorded hashes.

The Full run spent 160.7 seconds in an unrelated runtime animation-state test scanning
project scripts; a captured managed stack identified that operation, and it completed
successfully without changes to the scripting code. The first focused run exposed a
misplaced binding dispatch; correcting it made both the cross-limb and persistence
checks pass. Failed intermediate evidence remains separate from the final results.

Evidence: `TestResults/Builds/20260909-140428-670876a6/BuildSummary.json`, `Tests/`,
and the [rest/posed capture gallery](../EditorReview/2026-09-09/model-skin-controls/index.html).
This correction does not mark every Model workflow accepted. Tests use unattended
repository capture helpers; no Computer Control or changes to the user's source
project are involved.
