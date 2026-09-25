# Shared Image Editor / Engine pixel-rig API

## What is shared

`Genesis.Runtime.Imaging` contains the pixel deformation and joint-gap infill algorithm previously implemented in the Image Editor, the connected/pinned-joint pose solver, saved-keyframe interpolation and the eight-mode RGBA layer compositor. The Image Editor now calls that Engine implementation through document adapters. The live runtime does not reference editor assemblies.

This is source-art deformation and source-alpha-aware infill around authored joints. It is not AI image generation, a missing-art generator, or arbitrary whole-image rotation. An image must contain a saved, bound pixel rig with named bones/joints before the live API can control it.

Bind once in an Object's Create event. Change the pose, aim or controls in Step. The component lifecycle advances playback, and the normal object draw pass submits the dynamic result. Do not call `Advance` again from gameplay when using a bound live component.

## Author an image

Open a sprite in the Image Editor, select the raster layer and source frame, open its existing Rig Studio, place bones/joints, rig the pixels, and save the image. Give bones practical names such as `Head`, `Arm` or `Torch`. Saved poses and pose animations are optional. The runtime reads the existing `pixelRigs` JSON schema, including authored keyframes/FPS, joint radii, pinned joints and gap-infill preference.

The initial implementation supports one bound rig per live object. Use canvas-local rig coordinates; keep the bound source cel at zero offset. Other visible colour layers from that source frame are composited in their authored order, while hidden/material-only layers are excluded. Other layers do not independently advance their own frame timeline during rig playback. Their static cel offsets, visibility, opacity and blend modes are respected.

After resizing the image canvas, rebind/save the rig before using it at runtime. A missing rig, mismatched canvas, invalid coordinates, missing joint/parent, parent cycle or invalid keyframe reference is rejected before binding. A failed replacement does not discard an already valid live rig.

## PGSL object events

Example Create event, after authoring an image containing a rig named `Upper Body` and a bone named `Head`:

```pgsl
rig_ready = SpriteRigBind("Hero", "Upper Body");
if (rig_ready) {
    SpriteRigInfill(true);
}
```

A simple procedural control, usable from Step:

```pgsl
if (rig_ready) {
    SpriteRigBoneRotate("Head", -20, false);
}
```

Rotation is an **absolute offset from the sampled pose**, not an additional 20-degree rotation every update. Replace the fixed control with `SpriteRigAim("Head", targetWorldX, targetWorldY, 35, false)` when the game supplies a target in room coordinates. `SpriteRigAimLocal` instead accepts image-local pixels. World aiming accounts for the calling object's origin, rotation and signed/mirrored scale. The local coordinate convention is the same as the Image Editor (X right, Y down).

The image path, rig name, bone names and target variables in these examples are authored by your project. They are not new dependencies installed into the Luigi or Mushroom Meadow templates.

| Command | Behaviour |
| --- | --- |
| `SpriteRigBind(image, rig = "")` | Returns true on success. Empty rig selects the first saved rig. Rebinding the same image/rig is idempotent. |
| `SpriteRigIsBound()` | Returns whether this object has a live rig. |
| `SpriteRigError()` | Returns the most recent rig-operation error recorded for this object. |
| `SpriteRigClear()` | Disposes this object's rig and resumes ordinary sprite-frame drawing. |
| `SpriteRigPose(pose)` | Selects a saved pose by name or ID. Returns success. |
| `SpriteRigBoneRotate(bone, degrees, followChildren = true)` | Sets a rotation offset using the shared connected-joint solver. Returns success. |
| `SpriteRigAim(bone, worldX, worldY, limit = 35, followChildren = true)` | Clamps aim around the sampled authored direction, using a room-space target. Returns success. |
| `SpriteRigAimLocal(bone, x, y, limit = 35, followChildren = true)` | Equivalent control for an image-local target. Returns success. |
| `SpriteRigJointMove(joint, x, y)` | Moves an unpinned joint in image-local coordinates, preserving the solver's connected constraints. Returns success. |
| `SpriteRigInfill(enabled)` | Enables/disables the real source-art joint-gap infill. Returns success. |
| `SpriteRigPlay(animation, speed = 1, loop = -1, restart = false)` | Plays authored keys/FPS. Loop -1 uses the saved setting, 0 disables it, 1 enables it. Speed must be above 0 and at most 16. Repeated calls do not restart unless requested. |
| `SpriteRigStop()` | Pauses at the current pose. |
| `SpriteRigFrame()` | Returns the current 1-based animation frame, or 0 when unbound. |
| `SpriteRigSeek(frame)` | Seeks in the current animation, clamped to its last key. Returns success. |
| `SpriteRigReset()` | Restores the saved bind pose and clears procedural controls. |
| `SpriteRigClearControls()` | Clears procedural overrides, retaining the selected pose/animation. |

Object event context is required for these per-instance commands. Failures from boolean commands can be inspected with `SpriteRigError()` instead of assuming a control was applied. Bind and mutation operations are intended for the game's normal simulation/render thread, not concurrent worker-thread calls.

Saved animation sampling and procedural controls are combined before rendering. Call `SpriteRigClearControls` when a control should no longer persist. `SpriteRigPose` changes the underlying pose but does not implicitly delete the procedural controls layered over it.

## C# Engine API

```csharp
using Genesis.Runtime.Imaging;
using Genesis.Shared.ECS;
using EcsWorld = Genesis.Runtime.ECS.World;

public static class HeroRigSetup
{
    public static bool Bind(EcsWorld world, Entity entity,
        string projectRoot, out string error)
    {
        if (!SpriteRigRuntime.Bind(world, entity, projectRoot,
                "Hero", "Upper Body", out error))
            return false;

        PixelRigPlayer player = SpriteRigRuntime.Get(world, entity)!;
        player.FillJointGaps = true;
        if (!player.RotateBone("Head", -20, followChildren: false))
        {
            error = "The saved rig has no Head bone.";
            SpriteRigRuntime.Clear(world, entity);
            return false;
        }
        return true;
    }
}
```

The entity needs its ordinary 2D object transform. Binding adds a visible Draw2D component if absent, but does not invent the object's placement/transform. `SpriteRigRuntime.BindFile` accepts a resolved descriptor filename; `Get` returns the per-instance player. C# `EntityBehavior` subclasses also have `BindSpriteRig(image, rig)`, `SpriteRig` and `ClearSpriteRig()` helpers.

`PixelRigPlayer` exposes `SetPose`, `Play`, `Stop`, `Seek`, `RotateBone`, `AimBone`, `MoveJoint`, `Reset`, `ClearControls`, `FillJointGaps` and `GetPixels`. The lower-level `PixelRigPoseEditor`, `PixelRigRasterizer` and `PixelLayerCompositor` are usable without binding a live entity. Standalone players, unlike bound components, must be advanced by their owner.

## Rendering, ownership and limits

Each instance owns mutable pose state and output pixels. Source descriptors, source artwork and other instances are not modified. The regular 2D object draw route honors origin, mirrored scale, rotation, depth, alpha, authored sprite shader passes and the existing queue's viewport clipping. A bound rig is a draw override until cleared: `SpriteSet` alone does not replace its image/rig binding.

The image collision mask remains authored/static; this is visual deformation, not a per-pixel animated physics collider. Procedural joint controls do not automatically attach separate game entities to those joints.

Rendering reuses a per-instance pixel buffer and a per-renderer dynamic texture. Unchanged revisions do not rerasterize or upload. Authored animation is sampled at its saved frame rate. Source decoding is at bind time, not per draw. Texture handles are released on unbind, entity destruction, world disposal and renderer asset invalidation. Static source-frame colour layers are cached until rebinding; to reload a changed rig, clear and bind it again.

The CPU rasterizer is intended for character-sized 2D rigs. Allocation guards are not throughput guarantees: the loader caps a rig at 4,194,304 pixels, 128 bones, 256 joints, 512 poses and 128 animations; static layer buffers are capped at 128 MiB per instance. Practical scene budgets should be much smaller and profiled on the target machine. This patch does not establish GPU deformation, crowd-scale throughput, or full performance parity among render backends.

## Verification

Run `.\Build.bat --test editor-interaction` on Windows after the normal build. New cases exercise production Inspector controls, Room editing routes, engine/editor raster parity, pinned-joint validation, revision caches, animation sampling, actual PGSL-to-live-entity binding, object draw submission, texture ownership and hidden layer composition. The render controller used by the tests records submission rather than rendering an image.

Native compilation and these tests were not executed in the patching environment because no .NET/Windows runtime was available. Human Image Editor interaction and GPU output still require local acceptance.
