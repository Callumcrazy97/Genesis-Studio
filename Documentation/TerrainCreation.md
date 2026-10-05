# Terrain creation and materials

## Create terrain

Use **Create** in the left palette:

- **Landscape Wizard** provides preset controls, a top-down view and a live 3D preview, then replaces the base landscape through an undoable operation.
- **Region** creates a flat, untextured rectangular section. Click and drag to preview its footprint; release to create it.
- **Lasso** creates a flat section from a drawn outline. Release to close the outline; self-crossing outlines are rejected.
- **Create From Heightmap** and **Create From Code** open a source wizard with separate 3D terrain and grayscale heightmap / volume cross-section tabs. Placement controls stay beneath the preview: enter X/Y/Z or choose manual placement.

Region and Lasso use the height beneath the first click, or the zero-height plane outside the landscape. Escape cancels the drawing. New sections are separate terrain-owned mesh parts: they preserve the base landscape, appear in the Terrain tree, and support placement transforms and components. The existing sculpt and paint brushes (up to eight layers) continue to edit the base landscape; they do not sculpt these mesh sections.

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

A code heightfield applied with **Replace editable base terrain** is not held to the preview's limit: it is generated again at the spacing the code asks for, up to 8192 cells per side, and whole-terrain commands in the code (`Ocean`, `Erode`, `Rivers`, `PaintNatural`, `Layer`, `Scatter`, `ScatterCollision`, `Sites`, `SitePlace`, `SitePaint`) are applied to the result. The **Island** example uses them. [Large worlds](LargeWorlds.md) lists every command.

**Options › Forests and scatter…** edits the terrain's scatter layers (the forests, rocks and undergrowth placed by rule rather than one by one): which Model, how many per hectare, where it grows, how large, how far it is drawn and whether it is solid. The terrain view draws the layers as the game does. One change is one undo step.

Creation journals the mesh, recipe, definition and placement together. Undo/redo restores that operation. **File → Save** persists the terrain placement. Generated mesh sections live beneath the terrain resource's `.parts` directory.

## Components and PBR

Paint strokes update the affected material area while the mouse is held. They reuse the loaded PBR maps and GPU material handles without rebuilding the terrain mesh, library or inspector. Resource changes still prepare the full material in the background. Camera look, orbit and pan present frames during mouse dragging.

**Foliage** lists authored plants and trees with their Image icons (or a category icon). Click a definition in the library or the foliage panel to arm placement. **New foliage** and **New tree** open the entity wizard. Ecological scatter settings are available separately through **Ecological scatter…**. Empty library categories have no placeholder children or expand arrow.

Add/Edit Terrain Object follows **Identity → Category → Components → Review & Create**. Identity offers a project Image icon, thumbnail and New Icon action. Choose Terrain, Foliage, Tree, Water, Prop/Object or Environment, then compose a custom asset from expandable components. Cards can be disabled, reordered and removed. Model meshes, animation clips, Texture frame count/FPS and billboard mode, Shader overrides, Physics collider settings, Audio and PGSL hooks appear inline. Saving adds the definition to the category library and arms placement. The live preview stays alongside every step and plays visual animations continuously; the main Terrain window retains its separate Play/Pause/Stop controls.

The fixed **Current Tool** card identifies the tool and active asset. Brush radius, strength/opacity and Smooth/Linear/Hard falloff sit directly below it. The remaining cards scroll. Select uses surface raycasts; Place arms the chosen library definition. Select Region / Select Lasso restrict painting and Fill; Clear Selection returns to the entire heightfield.

The paint swatches only select a brush channel. **Fill** paints that channel over the heightfield or active selection, with Undo. To change the layer definition, select Terrain and use **Image / Colour / Mapping / PBR…** in the Inspector. Choose an Image or colour, Repeat/Clamp/Tile/Stretch mapping, tiling and map resolution. Repeat uses a repeat count; Tile uses metres per tile; Stretch covers the terrain once.

### Eight paint layers

A terrain holds up to **eight** paint layers (**Add layer** stops at eight). Layers 1-4 are stored in the first RGBA splat plane, as always; layers 5-8 in a second plane that is created the first time one of them is painted. Painting any layer lowers all the others so the eight weights still add up to one, and every paint, fill, path, river and regeneration step undoes both planes together.

The `.gterrain` file stays **version 1** (heights, then one splat plane) while layers 5-8 carry no paint, so terrains that use four layers save exactly the file they always did and older builds can still read them. Once layers 5-8 are painted the file is **version 2**: the same header and planes followed by the second splat plane. Version 1 files load unchanged.

**Per-layer detail maps.** With more than four layers, or with **Tile each layer's normal and ORM maps** ticked in the layer material dialog, every layer's albedo, normal and ORM (occlusion, roughness, metallic) are tiled in the shader at the layer's own repeat, instead of normal and ORM coming from one whole-terrain bake of at most 2048² (a metre per texel on a 2 km island). The layers' maps are packed into three atlas textures (albedo, normal, ORM), each layer in its own cell with a wrapped border a sixteenth of its size so mipmapped, tiled sampling never bleeds into the next layer. A cell is at most 1024² (a layer's map resolution above that is reduced), so eight layers cost three 4608 × 2304 textures. Atlases rather than texture arrays because the OpenGL backend has no array textures; with the two splat planes on the FlowMap and HeightMap slots, the shader uses the same three extra texture slots (t21-t23) as the four-layer surface on DX11, DX12, Vulkan and OpenGL. Terrains with four or fewer layers and the option off keep the original four-layer shader and look exactly as before.

**Height blend.** **Height blend sharpness** (0-1, saved as `HeightBlendSharpness` in the terrain document) replaces the linear cross-fade: where layers meet, the layer standing highest (its paint weight plus its albedo **alpha**, used as height) wins, and the others fade out within a band that narrows as sharpness rises (0.5 wide at 0, 0.02 at 1). Paint an alpha height channel into a layer's Image (stones high, mortar low) for natural edges; with opaque albedo the result is a sharpened paint boundary. 0 keeps linear blending. Height blending also uses the per-layer shader.

The Software renderer draws all eight layers from its whole-terrain bake.

Measured on the development PC (DX11, 1280 × 720, a 2 km terrain seen from walking height): eight 1024² layers pack in about 0.7 s on the background bake, upload with mipmaps in about 0.2 s when the material is first bound, and the frame cost was 0.36 ms against 0.33 ms for the four-layer shader on the same view. Layers with no paint at a pixel are skipped, so cost follows how many layers overlap rather than how many exist.

**Generate PBR** preserves existing channels and saves missing channels into the assigned Image. Albedo, Normal, Roughness and AO update the open terrain and entity previews. Normal relief does not displace geometry. Texture components expose the same generation action. Generation saves to the Image; terrain material assignment and Fill use terrain undo.

## Verification scope

`Genesis.Application.Headless.exe --test terrain-layers-grass` checks eight-layer paint, version 1/2 files and undo snapshots, atlas cells, compiles the per-layer shader to DXBC, DXIL, SPIR-V and GLSL, renders all eight layers on DX11, DX12, Vulkan and OpenGL, and captures height blending and tiled per-layer normals on DX11.

The focused standalone review includes inspected captures, menu reopen after selection, fixed-header spacing, six sculpt brushes and undo, selection-only swatches, masked Fill, surface picking, manual preview/placement, the actual Image Editor bridge window, heightfield save/reopen/undo, four wizard pages, component/icon persistence, visibly alternating sprite frames and live PBR. Evidence is in `.validation/terrain-authoring-04` and the Terrain capture gallery. Full regression, the five-renderer sweep, Room/F5 terrain-part parity and package publication remain deferred. Physics/PGSL hooks are authorable data; this preview does not execute player collision events or player-driven foliage bending.

## Grass around the camera

Ecological scatter stores every tuft for the whole terrain, at most 250,000 of them, which leaves a large island bare. **Grass around the camera** grows grass from a rule instead: square cells near the camera are filled with tufts as it moves and handed back for reuse once it has left them behind, so nothing is stored and the grass reaches as far as the rule says wherever the camera goes. It is off unless switched on, and terrains saved before it existed load with it off and look exactly as they did.

Set it on the **Foliage** page of the Terrain editor, under **Grass Around the Camera**, then select **Apply Grass Rule** (one undo step; **File → Save** writes it to the terrain's nature document as `grassRule`). The Terrain view, the room preview and the running game all grow the same grass from the same rule.

- **Enabled**: grow grass around the camera.
- **Radius** (100 m): how far from the camera grass is grown and drawn.
- **Spacing** (0.5 m): distance between neighbouring tufts where a layer's density is 1. A whole number of tufts fits along each side of a cell, so the spacing used may be a little larger.
- **Layer 1 to Layer 8 density** (1 for layer 1, 0 for the rest): the chance, 0 to 1, that a spot grows a tuft on each painted layer. A spot's chance blends the densities of the layers painted there, so a meadow at 1 is full, a dirt road at 0 stays bare and a half-painted verge is half full. They are saved as the `layerDensities` array.
- **Full density fraction** (0.45): the share of the radius at full density. Beyond it fewer and fewer of each cell's tufts are drawn, down to none at the radius, so the grass fades out instead of ending at a line.
- **Detail distance** (30 m): tufts nearer than this draw with the detailed blade mesh, further ones with the simple one.
- **Jitter**, **Minimum scale**, **Maximum scale**, **Pattern seed**: how far each tuft wanders from its lattice point, its size range and the pattern. Position, size, turn and tone come from the spot's lattice coordinates and the seed, so the same ground always grows the same grass however the camera arrived.
- **Steepest slope degrees** (40): steeper ground stays bare.
- **Species** (Meadow grass): the tuft drawn. Tufts use the same meshes and foliage shading as scattered grass, so the two look alike.
- **Cell size** (8 m), **Cells per frame** (8), **Generation budget milliseconds** (1.5), **Maximum drawn tufts** (16,000): growing is limited to that many cells and that much time a frame, nearest first, so walking or driving never holds a frame up; drawing culls whole cells against the view and draws the nearest first up to the tuft limit. In the game the limit also shrinks to what the renderer's 32,768-instance frame buffer has left after scattered foliage and the forests and rocks, less 4,096 kept for everything else, so switching grass on never drops a tree.

Tufts stand on the terrain's height and are not solid. Like scattered foliage they keep out of the terrain's water bodies and a 1.25 m bank around them. In the Terrain view, sculpting or painting regrows the grass in place, nearest cells first, so it follows the stroke without going bare. On a 960 × 540 DX11 test frame moving 1 m a frame across a 512 m meadow, growing cost 0.2 to 0.5 ms a frame on average, and grass added under 1 ms of CPU and about 0.2 ms of GPU time to a frame. The `terrain-layers-grass` focused target checks the rule, the file, the recycling and the frame budget, captures a meadow beside a dirt strip, and writes the measured frame cost to `Logs/terrain-grass-frame-cost.txt`.
