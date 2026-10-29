# Model editor

The Model editor is the window **Edit Model** opens from the Model Viewer. It has a mode rail
(Create, Edit, Select, Texture, Rig, Outliner), the 3D view and one undo history for the model.
The Viewer inspects a model and has no authoring tools.

- Code: `Source/Genesis.Application.Editors.Suite/Assets/ModelEditorControl*.cs`, partial files by
  feature (`.Toolbox` builds the pages, `.Gestures` the pointer, `.TexturePaint` the Paint tool);
  the Viewer is `ModelViewerControl*.cs`. Texture painting's own logic is in `Assets/Model/Painting/`.
- Tests: `Tests/Genesis.Application.Headless/Suites/Model*Suite.cs`; `--test model-editor` runs the
  editor's suites, including Paint.

## Hidden materials

Texture page, **HIDDEN MATERIALS**: every material of the model with a tick box. A ticked material's
surfaces are not drawn, in the editor's preview at once and in games (an imported scene's ground
once a Terrain stands in for it); the geometry stays in the model. Each tick is one undo step, and
Save keeps the list in the Model resource's `hiddenMaterials` (the only place it is kept:
`GModelMaterial.Hidden` is set from it on every load; names of materials the model no longer has
are left in the list). The Viewer shows a hidden material as "*name* (hidden)" and cannot change it.

## Paint

Texture painting on the model, in the 3D view, live. Texture page, **PAINT ON THE MODEL**:

| Control | What it does |
|---|---|
| Paint / Erase | Choose the brush. Left-drag on the model. Esc during a drag takes the drag back. |
| Colour… | The paint colour. |
| Size (pixels) | Brush diameter in the Image's pixels (1-512). |
| Hardness (%), Opacity (%) | As in the Image editor's brush. |
| New Image size | 512 × 512 or 1024 × 1024: the size of the Image made for a material that has none. |

The section's message line says what happened: the Image made, or why a part cannot be painted.
The tool is in the Model editor only; the Viewer, and the editor opened in its Viewer role, have none.

### How a stroke works

1. **Picking.** A ray from the pointer (`EditorViewport3D.PickRay`) is tested against the triangles
   of every visible part on the CPU (`ModelPaintPicker`, with `ModelSurfaceBrush.RaycastUv`); the
   nearest hit wins, and its UV is interpolated there. The triangle's material is its per-face
   material when the part has them, otherwise the part's, exactly as the renderer batches them.
2. **Pixel.** The UV times the material's UV Scale, repeats wrapped, row 0 at the top: the pixel the
   renderer samples.
3. **Brush.** Paint is the Image editor's own `RasterOperations.StampBrush` (size, hardness,
   opacity), unchanged. Erase uses the same footprint to move pixels back toward the Image as it was
   when painting began in this editor, rather than to transparent. The pointer path is sampled every
   two screen pixels, and gaps in texture space are filled at a quarter of the brush size, but never
   across a UV seam (the next triangle's pixels lie elsewhere on the Image).
4. **Image.** Strokes go into the material's Albedo Image resource: into the top visible, unlocked
   colour layer of frame 1 (the frame a model draws). A material with no Image, or only a plain
   picture file, gets a new Image resource when painting starts (in the project's Images folder,
   named "*model* *material* paint"): a picture file is copied in as it is; otherwise the Image is
   filled with the material's colour, times the part's vertex colour when all its vertices share one,
   and those are made white. The model looks the same before the first stroke, and a stroke shows the
   colour picked. Making the Image is one undo step ("Make paint Image"); undoing it takes the Image
   off the material but leaves the Image resource in the project.
5. **Live.** Each renderer drawing the model gets its own texture of the painted pixels, uploaded
   again when they change (at most once a frame), and drawn instead of the material's saved image
   through `RuntimeModelRenderSystem.MaterialAlbedoOverride`. Only the editor's own preview sets that
   hook (default null): games, other editors and the Viewer draw the saved Image.
6. **Undo.** One entry per stroke ("Paint on model", "Erase on model") over every Image the stroke
   touched, kept as the changed 64-pixel tiles (the Image editor's `PixelStrokeRecorder`).
7. **Save.** The model's Save writes every painted Image that changed (as the Image editor saves
   them, `ImageWorkspaceStorage.Save`) and then the model, so a saved model never names an Image
   without its strokes. Closing without saving discards the strokes with the other model edits.
8. **No UVs.** A part whose triangles cover no texture area cannot be painted. Choosing Paint on
   such a model, or stroking such a part, says so and makes nothing; Quick UV Unwrap or Box UVs
   (Material & PBR) give it UVs.

### Limits of this first version

- Frame 1 and one layer per Image (the top colour layer); colour (Albedo) images only, not Normal,
  ORM or emission.
- Overlapping UVs paint every face that shares the spot (each face of the built-in cube uses the
  whole picture). A dab at an island's edge does not continue on the island across the seam.
- Brush size is in the Image's pixels, so it looks larger on a part with sparse UVs; the outline
  on the model shows its real size there.
- No mirror painting, stabiliser or texture brushes. The live picture has no mip levels until saved.
- If the same Image is also open and edited in the Image editor, the last save wins.
- Picking uses the bind pose; entering Paint stops clip playback.
- The older **Paint linked Image…** dialog (an Image canvas beside the model) remains for painting
  the 2D picture and the model together.
