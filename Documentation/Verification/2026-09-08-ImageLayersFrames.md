# Image layers, onion skin and frame targets — 8 September 2026

## Changes

The editor now previews all visible layers by default, including generated Normal, Roughness,
Metallic and other material maps. The Preview dropdown can isolate a material channel or return
to All visible layers. The main canvas and composite preview use the same layer compositing.
Colour exports and runtime colour textures retain the colour-channel composite; material maps
are not accidentally baked into game sprites.

Layer selection no longer clears and repopulates the list. Stable rows preserve the selection
and scroll position while visibility, opacity and other properties change. The eye has a larger
click area and indicates hidden layers with a slash. Name can be edited inline (Enter or leave
the field to commit); Depth reorders the layer, with 1 at the back. Up/Down and layer opacity
remain available. These properties are shared across the sprite's frames and survive saving.

Onion skin supports Same layer or Specified other layers, previous/next frame counts, opacity,
current-frame references from other layers, and above/below-artwork placement. The default draws
ghosts above the current composite so opaque artwork cannot conceal them. Reference layers may
be hidden from the normal composite. Black pixels still produce visible coloured ghosts, and
straight-alpha compositing avoids darkening transparent previews twice. Previous ghosts are blue,
next ghosts red and current-frame reference ghosts gold. Empty directions and missing reference
choices show an explanation. Onion preferences are saved with the image; guides are never exported.

## Frame targets

Image-operation windows share the following selector:

| Target | Frames used |
| --- | --- |
| This Frame | The frame selected when the dialog opened. |
| Frame Number / Range | Inclusive one-based From and To values; equal values select one numbered frame. |
| All Frames | Every frame in the image. |
| Previous Frames | Frames before the current frame, excluding it. |
| Next Frames | Frames after the current frame, excluding it. |

The selector displays the affected count. An empty direction cannot be applied. Operations
preserve the selected frame and compute results independently from each target cel. Pixel effects
use the matching active layer, skip locked cels, and retain the selection mask. Results are prepared
before mutation and committed as one undoable edit; a failure does not partially edit the range.

Coverage:

- All colour/stylise/legacy filters, noise, dither and normal generation previews.
- PBR material generation: maps use each target's colour pixels; untargeted frames receive empty
  cels with the same layer identities so saving/reopening keeps a consistent layer structure.
- Text, stamp, scale artwork, mirror, crop-to-selection and remove blank space.
- Rotate, image/canvas resizing and nine-slice pixel resizing.
- Rig workspace Apply pose. Generate frames still uses its explicit pose/frame assignments.
- Palette extraction reads the selected range; saved swatches are shared sprite metadata.
- Animation tag creation/editing uses the shared range selector.
- Oversized paste dialog targets the pasted pixels; shared canvas growth pads the other frames.

Frames share one canvas. All Frames permits changing its dimensions. A partial range transforms
its artwork within the existing canvas and clips pixels outside that canvas, leaving untargeted
frames unchanged. This rule is displayed in canvas-operation dialogs. Nine-slice guides themselves
remain shared sprite metadata. File pickers, colour pickers, help and view-only settings do not
apply pixel effects and therefore do not show an unrelated frame target.

## Verification

Published Full Build [`20260908-165529-588472f0`](../../TestResults/Builds/20260908-165529-588472f0/BuildSummary.json)
passed in **310.99 seconds**: **392 regression checks, 120 test images**, DX11, DX12, Vulkan,
OpenGL and Software smoke tests, and published startup/shader compiler/package checks.
The successful build was promoted to `Genesis Application/`.

The focused image suite includes 131 checks. New regression cases cover actual onion controls,
reference-layer persistence, opaque artwork, material visibility/opacity, stable layer selection
and eye clicks, all range boundaries, per-frame effect results, atomic failure, single-step undo/
redo, locked cels, scoped material save/reopen, partial canvas edits and target-selector layout.

Visual evidence from the running WinForms editor:

- [Layers and composite preview](../../TestResults/LayerFrames/Review/layers.png)
- [Specified onion reference layers](../../TestResults/LayerFrames/Review/onion-layers.png)
- [Effect frame-range selector](../../TestResults/LayerFrames/Review/effect-frame-range.png)

## Original Genesis and nearest-app review

Original Genesis was inspected at
`Old Genesis/GMS Style/GameManager/PixelArtEditor/CanvasPanel.cs` and
`Panels/ProjectPanel.cs`. Its onion settings distinguished adjacent frames and current-frame
references on other layers. The new controls restore those choices and add an explicit reference
layer checklist and previous/next counts.

Aseprite documents previous/next onion references and tint configuration. Its layer controls
provide the visibility, naming and ordering baseline used for this review.
Sources: [Aseprite onion skinning](https://www.aseprite.org/docs/onion-skinning/),
[Aseprite layers](https://www.aseprite.org/docs/layers/).

The reported layer/onion/frame-target issues have implementation and regression coverage. The
reviewed layout has visible eye controls, inline layer properties and a consistent target header.
This is not a claim of complete Aseprite feature parity. Image Editor acceptance still requires
the user's real-project retest, especially generated animation and save/reopen; keep the next
editor queued until that review is complete.

## Retest

1. Enable onion skin on an animation and change Previous, Next and Opacity. Toggle above-artwork
   placement and compare Same layer with Specified other layers, including a hidden reference.
2. Click several layer eyes repeatedly; adjust opacity, name and depth. Verify the selected layer
   stays selected and the canvas/top-right preview agree.
3. Apply a visible effect to This Frame, an explicit range, All, Previous and Next. Verify untouched
   frames, then Undo/Redo the whole edit.
4. Generate PBR maps for a numbered subset. Save/reopen and check both generated and empty cels.
5. Generate a rig animation, test onion skin and effects on it, then save and reopen.
