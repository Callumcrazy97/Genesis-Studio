# Convert an Image to a 3D Model

The Image Editor can turn pixel art into a voxel Model: every solid pixel becomes a block, and the
result is saved as an ordinary Model resource that Rooms, Objects and the Model Editor use like any
other.

**Where:** Image Editor → **Options → File → Convert to 3D Model…**, or the **Convert to 3D Model…**
button at the bottom of **Use in game**. The Image must be saved in a project first.

## The dialog

| Setting | Meaning |
|---|---|
| Name | The new Model's resource name (must not already be used by a Model). |
| Size per pixel (units) | World units per pixel. The default 0.0625 makes 16 pixels one unit. |
| Depth (pixels) | How many pixels deep the art is extruded (default 1). |
| Alpha threshold (1–255) | Pixels at least this opaque become solid (default 128); fainter ones are left out. |
| Frames | The current frame only, or all frames as an animation. |

The preview on the right is the shared 3D viewport: right-drag orbits, middle-drag pans and the
wheel zooms. Choose **Move**, **Rotate** or **Resize** and drag a coloured gizmo handle; Resize
stretches one axis at a time, so the art can be reshaped as well as scaled. **Reset** puts it back.
The move, rotation and resize are baked into the Model's vertices when you press **Create Model**.

The Model's origin is the Image's origin (Image properties), so a character drawn with a bottom
origin stands on the floor.

## What is made

- **Merged faces.** Faces of the same colour are merged into large rectangles on the front and back,
  and into runs along the edges; faces between two solid pixels are never made. A 4 × 4 two-colour
  tile is 20 triangles rather than the 192 of one cube per pixel. The dialog shows both counts.
- **Colours** are vertex colours, so no texture is created and the Model needs no Image at runtime.
  Lighting still shades the sides.
- **Big images** are split into several meshes (indices are 16-bit); nothing else changes.

## All frames: a flipbook animation

With **all frames**, each frame is its own mesh, fully skinned to its own bone (`Frame 1`,
`Frame 2`, … under a `Root` bone), and the Model gets one looping clip named **`Frames`** at 60 fps.
Its keys scale the current frame's bone to 1 and every other frame's bone to 0, holding each frame
for its duration from the Image's timeline (rounded to 1/60 s). It is an ordinary skeletal clip, so
the Animator component, `AnimationController` state machines and scripts play it like any other:
`ModelAnimationPlay("Frames", true, 0)`. Without an animator the Model shows its first frame.

Each of the Image's animation tags (Animate → Tag range) also becomes a clip of the same name over
its frames, honouring its direction (forward, reverse or ping-pong) and its loop setting, so a
sprite sheet tagged `Idle` and `Walk` gives a Model with `Idle` and `Walk` clips. A tag named
`Frames` is left out, since that name is the whole flipbook.

The new Model is created beside the Image when that folder may hold Models, otherwise in the
project's Models folder, and Studio offers to open it in the Model Editor.
