# Model Viewer rig and animation import — 9 September 2026

## Workflow

Open a model, then use **More Options** beside **Import / Replace**:

- **Import Rig From Model** copies the donor skeleton, binds it to the current mesh,
  and adds it to the model's rig library. Compatible existing weights, poses and clips
  are retained. The donor's geometry and materials do not replace the current model.
- **Import Model as animation** imports all clips from the selected file onto the
  current rig. The first imported clip is selected in the animation dropdown immediately;
  use Play or scrub the timeline. Existing clips remain; duplicate names receive a suffix
  such as `Wave (2)`.

Import saves into the current resource. Undo/Redo is supported; use File → Save or Ctrl+S
to persist an undo. These commands also work in Model Editor and retain its working
geometry. View-only settings still do not dirty or save the model.

## Compatibility and reference

The reference is Old Genesis's `GMS Style/GameManager/Editors/ModelViewer/AnimationClipImporter.cs`
and `BoneNameNormalizer.cs`, with the matching implementation also present in Unity Style.
Its workflow matches names case-insensitively, strips namespaces such as `mixamorig:`,
and rejects low matches. The current implementation adapts that workflow to canonical
`GModelAsset` rigs and sampled matrices, rather than treating the old `ModelData` storage
format as interchangeable.

Matching uses names, not joint array order. The original rest-transform compensation was
corrected after the Archer reproduction; see [local motion and frame previews](2026-09-09-ArcherMotionAndPreviews.md).
Unmapped target joints retain their rest relationship to their parent. At least
80% of animated source joints must match; partial matches report the omitted count.
Ambiguous names, invalid hierarchies, missing clips and incompatible rigs produce an
in-app error before the current model is changed. This is compatible-skeleton import,
not automatic retargeting between unrelated characters; an imported rig may need fitting
in Rigging before use with a differently proportioned mesh.

Inputs use the existing GLB/glTF, FBX, OBJ, DAE and Blender conversion path, plus current
`.model.json` and `.gmodel` files. The source must actually contain rig/animation data;
OBJ is normally static. Blender is required for `.blend`, as with ordinary model import.
Animation-only glTF and FBX files without mesh geometry are supported. Donors are read
without creating another resource or extracting their textures into the project.

## Persistence and validation

Conversion, binding, clip mapping and import saving run in the background. UI publication
explicitly returns to the window's thread. Import guards concurrent edits and restores
the controls afterward. Canonical data is staged in the model's associated data folder;
a descriptor-write failure rolls back the canonical file. Source files, materials and
unrelated model edits are preserved.

The focused `--test model-system` run passed **41 checks and 35 captures** in
`TestResults/ModelMotionImport/ModelSystemFinal`. The six new cases cover background UI
completion, exact menu actions, appending/renaming clips, rendered animation changes,
playback, rig binding and authored weights, joint index/namespace mapping, animation-only
glTF/FBX, rejected imports, locked-file rollback, undo/redo and save/reopen.

Full Build **`20260909-115903-526e5b3c`** passed **439 checks, 156 captures and all five
renderer smokes** (DX11, DX12, Vulkan, OpenGL, Software), with no skipped backend, in
**351.64 seconds**. Package, startup and shader-compiler checks passed; Studio and its
matching Player were promoted to `Genesis Application/`. All 11 changed source/test
files match those used by the build. Captures are produced by the unattended repository
harness; no Computer Control is used.

Evidence: `TestResults/Builds/20260909-115903-526e5b3c/BuildSummary.json` and
`Tests/results.json` under that run.

See the [capture gallery](../EditorReview/2026-09-09/model-motion-import/index.html).
Model remains the active editor; this does not declare the complete Model system finished.



