# Guided rigging and animation — 9 September 2026

## Workflow

Open **Model Editor → Animation → Rigging → Rig Management → Templates…**.
Choose a body, orient it, detect and review joints, bind, and generate animations.
Humanoids support 1–8 arm pairs; quadrupeds, insects and arachnids use four, six and
eight legs. A tail can have up to eight segments.

Up, forward, ground and symmetry describe a fitting reference. Source vertices and
the resource transform are preserved. Front/side views help check depth. Exclude
accessory meshes from detection using the checklist; they remain in the asset.
Drag joints or edit XYZ to pin corrections. Each correction moves only that joint;
mirroring is off by default and can be enabled explicitly. Refit retains pinned joints
and bend directions. Amber entries are advisory: no orientation checkbox or per-joint
confirmation is required. Invalid mappings, hierarchy and zero-length segments still
produce actionable errors when binding or generating motion.

Existing compatible rigs default to reuse: map semantic roles to their existing
bones, retaining indices, rest axes, inverse binds, weights and clips. Fresh rigs use
the surface-connected binder. A gentle movement test can be previewed before
generation, and joint placement remains accessible after binding.

Idle, Walk, Run, Turn Left/Right, Crouch and Jump generate normal Genesis clips.
Tempo, duration, stride, step height, stance width, sway, intensity, crouch depth and
jump height are adjustable. The optional virtual-travel preview shows contact motion;
saved root transforms remain fixed. Frames are available in the existing dropdown
and timeline after applying.

Analysis, fitting, binding and baking operate on a cancellable working copy.
**Use Rig** accepts a fitted copy directly into the ordinary Rigging page, even before
binding. Use the same **Bind To Mesh**, **Unbind** and **Apply to model** controls as a
drawn rig. Binding and animation preview within Templates remain optional. Layout
dragging leaves unselected joints fixed; moving a bone translates its two endpoints.
Connected-chain propagation is available when posing, not when adjusting rig layout.
Apply to model creates one editor undo operation; File → Save persists it. The body
page describes what fresh fitting replaces without another confirmation dialog.
Cancelling either window leaves its parent unchanged.
Saved recipes own their generated clips. Regeneration preserves unrelated clips and
offers a new copy when generated frames have been edited manually.

## Implementation and boundaries

`ModelRigWizardGeometry` welds positions, samples a normalized interior volume,
detects distinct extremities, uses constrained interior paths, and refines depth
against mesh intersections. Semantic profiles describe named attachments and chains;
bone count is not used to infer anatomy. The template-fitting design reference is
[Baran and Popovic's Pinocchio paper](https://people.csail.mit.edu/ibaran/papers/2007-SIGGRAPH-Pinocchio.pdf).
This implementation uses repository-specific voxel and fitting heuristics.

The motion solver creates foot trajectories and stance phases first, then applies
FABRIK with fixed segment lengths and a stable bend plane. Biped alternating steps,
four-beat quadruped walking, diagonal-pair running, insect tripod and arachnid alternating
groups share this solver. Arms settle from a T-pose and counter-swing; segmented tails
have restrained phase-delayed motion. Invalid or unreachable planted targets are rejected
with correction guidance. Imported bone bases are retained when converting solved
world positions back to local animation frames.

Typed setup, landmark mapping and recipe data live beside the ordinary model data.
Wizard metadata bypasses the legacy template replacement path. Runtime playback
uses the baked clips; no runtime fitter, cloud, LLM/BYOK, physics or cross-editor
integration is required. Detection needs a roughly suitable symmetric body and review
of ambiguous geometry. Fingers, faces, wings, asymmetric hybrids, attacks and
interaction animation remain outside this version.

## Verification

The interaction correction passed **16 wizard checks / 12 captures** in
`TestResults/rig-wizard-editing-final` and **6 rig-editing checks / 3 captures** in
`TestResults/rig-editing-wizard-final`. New checks exercise independent numeric and
pointer edits, explicit mirroring, direct binding without acknowledgements, Use Rig
on an unbound replacement, normal Bind To Mesh/Apply buttons, corrected-landmark
persistence, undo and save/reopen. Captures were inspected.

Full Build **`20260909-170858-c43ad307`** published the interaction correction after
**455 checks / 164 captures**, all five renderer smokes and startup/package checks
passed in **483.74 seconds**. No renderer was skipped. This invocation did not enable
five optional Vulipet cases controlled by `GENESIS_MODEL_REVIEW_SOURCE`; Archer and
Vulipet wizard fitting/generation remained included. Source hashes for the correction
are recorded in `.validation/rig-wizard-editing/source-hashes.json`.
A subsequent `--test model-system` run against the same build with
`GENESIS_MODEL_REVIEW_SOURCE=F:\Development\Game Development\Shared\Models\Vulipet.glb`
passed **62 checks / 54 captures**, including all five optional cases, in
`TestResults/rig-wizard-editing-model-system`. All 11 changed source/test files still
match the recorded hashes; the published BuildSummary identifies the successful run above.

The initial focused run `TestResults/rig-wizard-release` passed **14 checks and 10 captures**.
It covers all four families, 2/4/8/16-arm humanoids, rotated and varied-proportion
fixtures, welded cube seams, separate parts, accessory exclusion, missing limbs,
mirrored pinned corrections, retained bend directions, segmented tails, rig reuse,
preserved imported weights/rest axes, neutral geometry, regeneration/manual edits,
save/reopen, the complete wizard apply/undo path and cancellation. Tail bend handles
affect generated motion, zero intensity preserves the rest pose, and reopening a
bound wizard model defaults to rig reuse.

All seven movements were generated for Archer and Vulipet. Sampled stance drift
under virtual travel must stay below 1% of leg length, and segment length error below
0.1%. These are joint/contact assertions, not a guarantee that every surface vertex
in an arbitrary mesh will deform perfectly. Captures use repository unattended
helpers; Computer Control was not used. Source model files are unchanged.

See the [inspected capture gallery](../EditorReview/2026-09-09/rig-wizard/index.html).
Initial publication Full Build **`20260909-155000-c399f687`** passed **458 checks, 173 captures and all
five renderer smokes** in **396.99 seconds**, with no skipped backend. Startup, package
and shader compiler checks passed; Studio and its matching Player were promoted to
`Genesis Application/`. All 21 changed source/test files match their recorded hashes.
This run includes the tail-bend, zero-intensity and rig-reuse assertions. The earlier
Full run `20260909-153759-bf71392b` also passed before those final refinements.

An additional measurement pass sampled **600 positions per clip** across **63
model/motion combinations**: four humanoid arm counts, quadruped, insect, arachnid,
Archer and Vulipet, each with all seven movements. It measures reconstructed FK foot
positions against the first contact in each uninterrupted stance, including the
virtual travel transform. Maximum whole-stance drift was **0.092493% of leg length**;
maximum segment-length error was **0.000183%**. The requested limits are 1% and 0.1%.
See the [numerical results](2026-09-09-GuidedRigWizard.metrics.json).

Initial publication evidence: `TestResults/Builds/20260909-155000-c399f687/BuildSummary.json`,
`Tests/results.json`, `Backends/`, and `.validation/rig-wizard/release-source-hashes.json`.
The additional measurement helper is `.validation/rig-wizard/metrics/metrics.csproj`.
These checks validate the implementation and supplied fixtures; user acceptance of
arbitrary models remains a separate review.
