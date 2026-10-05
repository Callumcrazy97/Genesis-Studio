# Convert a model to 2D sprites

Pre-rendered 3D, the way a fishing game draws its boat: the model is rendered from every
direction into the frames of an ordinary Image, and the game shows the frame for the way the
object faces.

## In Studio

Open a Model (viewer or Model editor) and press **2D sprites…** on the command bar. The panel has:

| Setting | Default | Notes |
|---|---|---|
| Directions | 16 | 8, 16 or 32 facing angles, evenly spaced. |
| Camera elevation | 30° | 0 looks from the side, 89 almost straight down. |
| Frame size | 128 px | Square frames. |
| Projection | Orthographic | Perspective is offered; orthographic keeps every direction the same size. |
| Animation | None | A clip renders *Frames per direction* frames at *Frames per second* in every direction. Starts on the viewer's selected clip. |
| Engine lighting | On | The viewer's key light, ambient and light direction through the engine's stylized lighting; *Cel bands* above 1 gives stepped shading. Off renders flat colour. |
| Outline | 0 px | A silhouette outline in a chosen colour, drawn around the model. |
| Transparent background | On | Off fills the background with a dark grey. |
| Front offset | 0° | For a model whose front is not +Z: turns it so direction 0 still faces right. |

The live preview shows 8 of the directions, starting at 0° (facing right) and going
counter-clockwise. **Create Image** renders every direction and frame, adds an Image resource
(under Sprites) and opens it.

## How it renders

Frames are drawn by the engine's renderer (the frame orchestration every backend uses, on the
software device, the same path as the Model timeline and Room object previews), so materials,
textures and lighting match the viewer. The renderer's readback is opaque, so every frame is drawn
twice, over black and over white; the difference between the two is exactly how see-through each
pixel is. Soft edges and translucent materials keep their alpha and no colour key can collide with
the model's own colours. Skinned models are posed on the CPU for each frame. One framing (the
sphere the model sweeps while turning about its centre) is used for every frame, so the sprite does
not jump or change size as it turns; the Image origin is the frame centre, which is the turning
axis.

## The Image

- Frames are **direction-major**: every animation frame of direction 0, then direction 1, and so on.
  Frame names give the angle ("45°", or "45° frame 2"); with a clip, each direction also gets an
  animation tag ("Facing 45") for previewing in the Image editor.
- `usage.directions` records the layout: `count`, `framesPerDirection`, `startAngleDegrees` (0),
  plus `elevationDegrees`, `orthographic`, `sourceModel`, `clip` and `framesPerSecond` for reference.
  The runtime reads it (`SpriteRuntimeUsage.Directions`).

## Angles

Direction `d` faces `d × 360 / count` degrees. **0 faces right (+X), angles grow
counter-clockwise, 90 faces up the screen**: the same convention as `PointDirection`, so
`PointDirection(x, y, targetX, targetY)` can be passed straight in. A frame covers half a step
either side of its angle (with 8 directions, 0–22 picks the right-facing frame and 23–67 the
up-right one); negative angles and angles past 360 wrap. In 3D terms the camera looks from the
bottom of the screen (+Z) and the model's front is +Z.

## PGSL

| Command | Result |
|---|---|
| `SpriteDirectionFrame(image, angleDegrees, animationFrame?)` | Frame index for an angle and an animation frame (which wraps, negative too); -1 when the image is missing. |
| `SpriteSetDirection(angleDegrees, animationFrame?)` | Sets the calling instance's frame from its own sprite. Without an animation frame it keeps the current one, so playback (`SpriteSetSpeed`) animates within the direction; false when the sprite is missing. |
| `SpriteDirectionCount(image)` | Number of directions; 0 when missing. |
| `SpriteDirectionFrames(image)` | Animation frames per direction; 0 when missing. |

An Image without direction data counts as one direction per frame, so a hand-drawn sheet ordered
by angle (counter-clockwise from right) works too.

```
// Create
SpriteSet("Boat Sprites");
SpriteSetSpeed(0); // or animate: SpriteSetDirection keeps playback within the direction

// Step: face the way the boat is moving
if (Speed > 0) SpriteSetDirection(Direction);
```

## Verification

`Genesis.Application.Headless.exe --test model-sprites` converts a cube with a different colour on
each face to 8 directions and checks that facing right, away, left and toward the camera show the
right, back, left and front faces (opposite directions differ), that the top shows and the bottom
never does, that the image is not mirrored or upside down, that the background is transparent, the
Image has 8 frames and its metadata, that an animated bake is direction-major with a rising clip and
an outline, and that PGSL picks frames by angle (0, 22, 23, 44, 46, 359, negative angles, wrapped
animation frames), directly and from a running script. Captures: `model-sprites-8-directions.png`,
`model-sprites-animated.png`, `model-sprites-panel.png`.
