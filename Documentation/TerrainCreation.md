# Terrain creation and materials

## Create terrain

Use **Create** in the left palette:

- **Landscape Wizard** provides preset controls, a top-down view and a live 3D preview, then replaces the base landscape through an undoable operation.
- **Region** creates a flat, untextured rectangular section. Click and drag to preview its footprint; release to create it.
- **Lasso** creates a flat section from a drawn outline. Release to close the outline; self-crossing outlines are rejected.
- **Create From Heightmap** and **Create From Code** open a source wizard with separate 3D terrain and grayscale heightmap / volume cross-section tabs. Placement controls stay beneath the preview: enter X/Y/Z or choose manual placement.

Region and Lasso use the height beneath the first click, or the zero-height plane outside the landscape. Escape cancels the drawing. New sections are separate terrain-owned mesh parts: they preserve the base landscape, appear in the Terrain tree, and support placement transforms and components. The existing sculpt and four-channel paint brushes continue to edit the base landscape; they do not sculpt these mesh sections.

## Heightmap and code wizard

Set width, length and sample spacing in **metres**, then select **Generate preview**. Heightmap luminance maps dark pixels to minimum height and light pixels to maximum height. The current importer samples 8-bit luminance.

**Create / edit heightmap in Image Editor** opens the existing Image Editor beside another live 3D preview. Paint, generate or edit its pixels; changes stream into the terrain preview. Use **Use heightmap and return to terrain preview** to finish. The source Image is not overwritten. Cancelling the terrain wizard discards its working terrain.

Choose **Replace editable base terrain** to apply a heightfield that the Sculpt and Paint tools can edit. The operation preserves the previous terrain for Undo and saves into the terrain resource on File → Save. Its regular square grid may round rectangular dimensions to the nearest cell. Leave this option off to create a separate terrain-owned mesh section. Volumes always create mesh sections. Manual placement shows the generated mesh beneath the pointer; click to apply, or Escape to cancel.

Code uses numerical PGSL. `x`, `y`, `z` are coordinates in metres; `u`, `v` range from zero to one. Output `height` for a heightfield or `density` for a volume; negative density denotes solid terrain. Volume mode supports cave interiors and overhangs. Hills, Ravine and Cave examples are provided.

```pgsl
TerrainSize(4000, 2000);
TerrainSpacing(16);
height = 60 * noise(x * 0.008, z * 0.008);
```

`TerrainSize` and `TerrainSpacing` override the GUI fields. Generation runs in a cancellable background task. **Create section** becomes available after a successful preview; changing an input invalidates that preview. Cancel closes without adding a section. Heightfields are limited to 384 × 384 cells and volumes to 64³ cells; larger dimensions remain intact while effective spacing increases. The preview reports the actual dimensions and spacing.

Creation journals the mesh, recipe, definition and placement together. Undo/redo restores that operation. **File → Save** persists the terrain placement. Generated mesh sections live beneath the terrain resource's `.parts` directory.

## Components and PBR

Paint strokes update the affected material area while the mouse is held. They reuse the loaded PBR maps and GPU material handles without rebuilding the terrain mesh, library or inspector. Resource changes still prepare the full material in the background. Camera look, orbit and pan present frames during mouse dragging.

**Foliage** lists authored plants and trees with their Image icons (or a category icon). Click a definition in the library or the foliage panel to arm placement. **New foliage** and **New tree** open the entity wizard. Ecological scatter settings are available separately through **Ecological scatter…**. Empty library categories have no placeholder children or expand arrow.

Add/Edit Terrain Object follows **Identity → Category → Components → Review & Create**. Identity offers a project Image icon, thumbnail and New Icon action. Choose Terrain, Foliage, Tree, Water, Prop/Object or Environment, then compose a custom asset from expandable components. Cards can be disabled, reordered and removed. Model meshes, animation clips, Texture frame count/FPS and billboard mode, Shader overrides, Physics collider settings, Audio and PGSL hooks appear inline. Saving adds the definition to the category library and arms placement. The live preview stays alongside every step and plays visual animations continuously; the main Terrain window retains its separate Play/Pause/Stop controls.

The fixed **Current Tool** card identifies the tool and active asset. Brush radius, strength/opacity and Smooth/Linear/Hard falloff sit directly below it. The remaining cards scroll. Select uses surface raycasts; Place arms the chosen library definition. Select Region / Select Lasso restrict painting and Fill; Clear Selection returns to the entire heightfield.

The four paint swatches only select a brush channel. **Fill** paints that channel over the heightfield or active selection, with Undo. To change the layer definition, select Terrain and use **Image / Colour / Mapping / PBR…** in the Inspector. Choose an Image or colour, Repeat/Clamp/Tile/Stretch mapping, tiling and map resolution. Repeat uses a repeat count; Tile uses metres per tile; Stretch covers the terrain once.

**Generate PBR** preserves existing channels and saves missing channels into the assigned Image. Albedo, Normal, Roughness and AO update the open terrain and entity previews. Normal relief does not displace geometry. Texture components expose the same generation action. Generation saves to the Image; terrain material assignment and Fill use terrain undo.

## Verification scope

The focused standalone review includes inspected captures, menu reopen after selection, fixed-header spacing, six sculpt brushes and undo, selection-only swatches, masked Fill, surface picking, manual preview/placement, the actual Image Editor bridge window, heightfield save/reopen/undo, four wizard pages, component/icon persistence, visibly alternating sprite frames and live PBR. Evidence is in `.validation/terrain-authoring-04` and the Terrain capture gallery. Full regression, the five-renderer sweep, Room/F5 terrain-part parity and package publication remain deferred. Physics/PGSL hooks are authorable data; this preview does not execute player collision events or player-driven foliage bending.
