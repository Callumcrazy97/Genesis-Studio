# Menu and script features: text layout, smooth shapes, clipping, globals, quitting, input

Engine features added on 3 October 2026, asked for by a game porting a full menu to Genesis. None
is specific to that game. Existing commands keep their meaning except where this page says so.

[Game features](GameFeatures.md) covers the 2 October additions.

## Text: measuring, letter spacing, alignment

| Command | Meaning |
|---|---|
| `DrawTextWidth(text, size)` | Width in pixels of the text in the current font and letter spacing. Size 0 is the current font size. Works outside Draw events too. |
| `DrawTextHeight(size)` | The current font's line height at that size. |
| `DrawSetTextTracking(pixels)` | Pixels added between letters of later text, on top of the font's own spacing and kerning. 0 is none. |
| `DrawSetTextAlign(align)` | `"left"` (the default) starts text at its x, `"center"` centres it on x, `"right"` ends it at x. |

`DrawText`, `DrawTextScaled` and `DrawTextColoured` follow the alignment and the spacing. Text is
still placed on whole pixels, as before. A project font (`DrawSetFont("Assets/Fonts/X.ttf")`) is
looked up at most once a second; it used to be looked up at every draw (about 0.16 ms each).

## Shapes

| Command | Meaning |
|---|---|
| `DrawRoundRectEx(x1, y1, x2, y2, radius, border)` | Rounded rectangle with smooth edges: filled when border is 0, otherwise an outline that many pixels wide, with rounded corners inside and out. |
| `DrawCircleEx(x, y, radius, border)` | Smooth circle: filled when border is 0, otherwise a ring. |
| `DrawLineWidth(x1, y1, x2, y2, width)` | A line of any width. Its edges are not smoothed. |
| `DrawRectangleGradient(x1, y1, x2, y2, r1, g1, b1, a1, r2, g2, b2, a2, vertical)` | From the first colour to the second (channels 0 to 255, alpha 0 to 1), top to bottom when vertical, else left to right. Drawn as one band per pixel. |

The smooth shapes are drawn one pixel row at a time, so a translucent shape covers each pixel once:
`DrawRoundRect` used to overlap its corner discs with its middle, which made the corners of a
translucent one darker. Edge pixels get the share of the pixel the shape covers as their alpha.

**Changed:** `DrawRoundRect` is now drawn the same way: smooth edges, no dark corners, and its
outline has rounded corners (it used to be four straight lines with gaps at the corners).

## GUI drawing in the order drawn, and clipping

In a DrawGui event, shapes, text and images (`DrawSprite`, `DrawSpriteScaled`, `DrawSpritePart`)
reach the screen in the order the script drew them, so a dimming panel or a padlock plate drawn
after a label or an icon covers it. Before, every image was drawn first, then every shape, then all
text. A C# behaviour's `OnDrawHud` canvas carries images the same way (`IHudCanvas.Sprite`).

**Pixel-art sampling in 3D rooms.** A 3D room with "Pixel-art sampling" on now draws its sprites
and GUI images with nearest-texel sampling, as a 2D room does. It used to apply only to 2D rooms,
so a 3D room's pixel-art HUD and bitmap fonts were always smoothed. (An Image's own Nearest filter
is still not applied on its own: Images default to Nearest, and honouring it would change how
nearly every game draws its scaled sprites.)

| Command | Meaning |
|---|---|
| `DrawSetClip(x, y, width, height)` | Later GUI drawing (shapes, text and images) shows only inside this rectangle. |
| `DrawResetClip()` | Draw everywhere again. A clip also ends with the event that set it. |

## GUI blending in linear light

GUI drawing blends the stored sRGB values unless asked otherwise: a white panel at 8% over black
shows as 20/255. A UI designed in linear light (as many are) mixes the light itself, and the same
panel shows as about 80/255. A script cannot do that mix, because it needs the pixel underneath.

| Setting or command | Meaning |
|---|---|
| Preferences › Project › **Blend GUI in linear light** (`rendering.blendGuiInLinearLight` in the project file) | Every DrawGui event starts blending in linear light. Off by default. |
| `DrawSetBlendLinear(enabled)` | Later GUI shapes, text and images blend in linear light (true) or as stored (false), until set again; each event starts from the project setting. Draws keep their order across the switch. |
| `DrawGetBlendLinear()` | Whether GUI drawing blends in linear light now. |

In linear light a draw's colour and its image are decoded from sRGB, mixed with the decoded screen,
and the result encoded again, so over black 8% white is 79/255 and half white over grey 128 is
204/255 (191 as stored). Smooth shape edges, gradients and text edges also come out as they do in
a linear-light UI. Every renderer does it: DX11 and DX12 draw those quads through an sRGB view of
the back buffer, OpenGL through an sRGB texture view of its surface, Vulkan through sRGB views of
the swap-chain images (where the driver has `VK_KHR_swapchain_mutable_format`; without it the
draws blend as stored), and the software renderer in its own blend. Only the GUI overlay (DrawGui
and the UI resources it draws) takes part; world drawing and the debug overlay blend as before,
and a game that never asks for it renders exactly as it did.

## Images

| Command | Meaning |
|---|---|
| `DrawSpritePart(name, frame, u0, v0, u1, v1, x, y, width, height, alpha)` | Part of an image frame into a rectangle. u0, v0 to u1, v1 are fractions of the frame (0 to 1): (0.25, 0, 0.75, 1) is the middle half. Tinted by the image blend. For crops, "cover" fills and zooms. |
| `SpriteWidth(name)`, `SpriteHeight(name)` | An Image's frame size in pixels; 0 when there is no such Image. |

## State that outlives a room, time, quitting

| Command | Meaning |
|---|---|
| `GlobalSet(name, value)`, `GlobalSetString(name, text)` | Store a number or text that every room's scripts can read until the game ends. Nothing is written to disk. |
| `GlobalGet(name)`, `GlobalGetString(name)` | Read one back: 0 or empty when unset. |
| `GlobalExists(name)`, `GlobalDelete(name)` | |
| `TimeMs()` | Milliseconds of real time since the game started, unaffected by pause and time scale. |
| `GameEnd()`, `GameQuit()` | End the game and close its window. `GameEnd` was listed before but did nothing. |

A C# game reaches the same through `PgslCommands.GlobalSet(...)` and the others.

## One instance driving another

`with (target) { ... }` runs its block once as each instance the target names, and did nothing
before (no instance was ever found). The target is an instance id, `all`, or an Object's name:
`with (Weapon) { revision = 1; }`. A bare name is taken as an Object's name unless a variable of
that name holds an instance id, so `var w = InstanceFind("Weapon", 0); with (w) { ... }` works too.

Inside the block:

- `x`, `y` and the other built-in variables, and commands that act on "this instance"
  (`ModelSet`, `ModelAnimationPlay`, the light commands, ...), are the target's.
- Other variables are the target's own, as its scripts see them: `revision = 1` sets the target's
  `revision`. Names declared with `var`, function parameters and `argument0`... stay the caller's,
  so `var mode = 2; with (Weapon) { weaponMode = mode; }` passes the caller's value across.
- A script resource called in the block runs as the target too. A function defined in the caller's
  own file runs as the caller.
- `return` leaves the block and the event as usual; the caller is restored.

| Command | Meaning |
|---|---|
| `InstanceVariableSet(id, name, value)` | Set a variable of another instance. |
| `InstanceVariableGet(id, name)` | Read one; 0 when the instance or the variable does not exist. |
| `InstanceVariableExists(id, name)` | |
| `InstanceNumber(objectName)` | How many live instances of an Object there are. |
| `InstanceFind(objectName, n)` | The id of the n-th of them, counting from 0; 0 when there is none. |

## Input for menus

| Command | Meaning |
|---|---|
| `WindowSetCursorVisible(visible)` | Show or hide the system pointer over the game window, for a menu that draws its own. While the mouse is captured for mouse look (`SetMouseCaptured(true)`, which already hides the pointer) it stays captured; the choice applies when it is released. |
| `CameraSetFreeFly(enabled)` | The engine's own fly camera (W/A/S/D, Space, Ctrl, Shift and mouse look) in a room with no physics player. By default it stops as soon as a script moves the camera and starts again in the next room; `false` turns it off, `true` keeps it on alongside a script. |
| `GamepadAxisRaw(axis)` | A stick or trigger as the controller reports it, with no dead zone. Same names and directions as `GamepadAxis`. |
| `GamepadSetDeadZone(stick, amount)` | `"Left"`, `"Right"`, `"Sticks"` (both) or `"Triggers"`, 0 to 0.95: how far it must move before `GamepadAxis` reads it. The sticks start at 0.18, as before, and the triggers at 0. |
| `GamepadGetDeadZone(stick)` | |
| `KeyboardTextTake()` | The characters typed since the last call, in order, as the keyboard layout and shift make them. For name boxes. |

## Language and maths

| Command | Meaning |
|---|---|
| `ArcSin(x)`, `ArcCos(x)` | The angle in degrees, as `Sin` and `Cos` take. |
| `ArcTan2(y, x)` | The angle in degrees of the direction (x, y), from -180 to 180. |

Fixed in the language:

- A script or function that called a command returning nothing, as a statement, took a value off
  its caller's stack: `var r = 10 + scr_note();` failed with "Stack underflow" when `scr_note` called
  `VariableSet`.
- A script called from another overwrote its caller's `argument0` and onwards; they are now put
  back when it returns.
- Functions of different scripts shared one namespace: two scripts that each defined `Helper()`
  called each other's. Each script now sees its own.
- `ArrayGetString` on a slot below the array's length that was never written returned `"0"`; it
  returns empty text, as documented.
- A function parameter or a `var` inside a function named like a built-in (`x`, `y`, `id`,
  `speed`...) read and wrote the instance's own value instead of the argument. Inside a function
  they are now the function's own. (`var x` in an event, outside a function, still means the
  instance's `x`, and the strict check still warns about it.)
- The project check reported a function defined in one of an Object's events (usually Create) as
  unknown in its other events, though the game runs it; an Object's events now share their
  functions in the check as in the game.
- `KeyCheck("5")` and the other key commands read a single digit as the enum's fifth key, not the
  5 key.

## Assets

- **Ogg Vorbis sounds play.** An imported `.ogg` Audio resource was silent in games; Ogg files are
  now decoded like WAV (by the file's content, not its name), in games and in the Audio editor.
- **An imported picture keeps its size.** Its Image gets the picture's own size as its canvas
  instead of 64 x 64.
- **Right-handed models can come in the right way round.** glTF (and Blender's exports) are
  right-handed and Genesis's world is left-handed, so an imported model is drawn as its mirror
  image: text reads backwards, a right-handed figure holds things in its left hand. With
  `"convertRightHandedModels": true` in the project file (`.genesisproj`), or
  `"convertRightHanded": true` in one Model's `.model.json`, the model is mirrored along Z when it
  is imported: points, normals, tangents, nodes, bones, skins, clips, sockets and colliders, with
  triangles turned so their fronts still face out. A model facing +Z in its source faces -Z,
  Genesis's forward. A Model's own setting wins over the project's. Clips taken from a glTF file
  into a converted Model are converted the same way. **Off by default**: models already in a
  project, and projects that compensate in their own pipeline, are unchanged. Changing the setting
  applies the next time a model is imported (re-import it, or replace its source file).
- **glTF material colours and factors are drawn as in a glTF viewer.** glTF's `baseColorFactor` is
  linear and was stored as if it were an sRGB colour, so a factor of 0.5 drew as 0.21; it is now
  converted on import. Models imported from now on (schema `genesis.gmodel/3`) also draw their
  materials' `metallicFactor` and `roughnessFactor` (scaling an ORM map, or on their own) and, for
  `MASK` materials, their `alphaCutoff` instead of the fixed 0.35. Models imported before keep how
  they look until they are imported again.
- **A model is cooked again only when its source changes.** A Model's `.model.json` keeps a hash
  of its source file (`sourceHash`). A source with a newer time but the same content (after a copy,
  a git checkout or an archive tool) no longer replaces the cooked `.gmodel` and the edits made to
  it (materials, sockets, merged clips). A model cooked before this records its hash the first time
  Studio finds its times in agreement. **The Player never writes into the project**: a changed
  source is used for that run only, and a model without a hash plays as cooked.
- **Collision parts by name.** A mesh, or the node holding it, named `COL_...` or `UCX_...` is a
  collision part: it is never drawn, and when a model has any they are its whole Mesh collider. A
  mesh named `NOCOL_...` is drawn but left out of the collider (glass, trims, small detail).
  Models without such names collide with every triangle, as before.

## Post effects

A project's Fullscreen Shader resources can now run over the finished frame, listed in a room's
`postEffects` or added by scripts (`PostEffectAdd`). See [Post effects](PostEffects.md), which
also has a complete ink outline as a project shader.

## Models reflect their surroundings

A room's environment has a new `environmentReflection` strength (0 to 4, **0 by default**, so
existing rooms look exactly as before). Above 0, models reflect the sky and the ground (the room's
two hemisphere ambient colours) along the mirror direction, blurring towards the plain ambient as
the surface roughens, weighted by the usual split-sum approximation of a physically based renderer.
Metals take their colour from it instead of going nearly black wherever no light's highlight falls,
and their diffuse ambient gives way to it. 1 is physically balanced. Scripts can change it while the
room runs: `EnvironmentSetReflection(strength)`, `EnvironmentGetReflection()`.

It is not a captured sky or a reflection probe: a metal mirrors the room's ambient colours, not the
actual scene around it. Terrain and water keep their own shading. With the room's hemisphere sky
light (`"skyLight": "hemisphere"`, see [Sky light and eye adaptation](GameFeatures.md#sky-light-and-eye-adaptation))
a smooth surface mirrors the sky as the engine draws it, horizon to zenith, and the lit ground
below the horizon.

## User Interface resources that are game menus

A User Interface resource (the UI Editor, drawn in game by `DrawUi`) can now express a styled menu
without script drawing. Each element gains, in the UI Editor's Inspector:

| Property | Elements | Meaning |
|---|---|---|
| Corner radius | Panel, Button, Progress bar, Slider, Toggle | Rounded corners in design pixels, smooth-edged. 0 is square. |
| Border width, Border colour | the same | An outline of any width inside the box, rounded with it. Unset keeps the old look: buttons and progress bars outline one pixel (accent, text colour); a width of 0 removes it. An empty colour uses the accent (a progress bar's text colour). |
| Fill, Gradient end colour | the same | `Solid`, `VerticalGradient` (Background at the top to Gradient end at the bottom) or `HorizontalGradient` (left to right). |
| Text alignment (Across, Down), Letter spacing | Text, Button, Toggle | `Left`/`Center`/`Right` and `Top`/`Middle`/`Bottom`; `Auto` keeps the old placement (text left, buttons centred, both in the middle). Spacing adds design pixels between letters. |
| Range (Minimum, Step) with Value and Maximum | Slider | The slider's value runs from Minimum to Maximum, snapped to Step (0 is continuous). |
| Image fit, Crop | Image | `Stretch` (as before, times the image scale), `Contain` (whole picture, letterboxed, centred), `Cover` (fills the box, centred, overflow cut off) or `Crop` (the Crop X/Y/W/H part of the picture, in fractions 0 to 1, into the box). |
| Enabled | all | A disabled element has its Disabled look and ignores the pointer. |
| Look (state) and State colours | all | Pick Hover, Pressed, Selected or Disabled, then set its Background, Text and Border colours (and, through the live Inspector, gradient end and accent). Empty colours keep the normal ones. The canvas shows the selected element in the chosen look; **Clear look** removes it. A disabled element with no Disabled look of its own is drawn at half opacity. |

Two element types are new: a **Slider** (track in the background colour, the filled part in the
accent, a round knob in the text colour) and a **Toggle** (an on/off switch, accent when on, with
its text to the right). Their Value is what the player sets: dragging the slider anywhere along it,
or clicking the toggle, which flips its Value between 0 and 1.

In game, `DrawUi` follows the pointer: an enabled Button, Slider or Toggle under it is hovered; it
is pressed while the left button is held after pressing on it (a slider stays pressed while dragged
off it). These commands read and change that state on the calling instance:

| Command | Meaning |
|---|---|
| `UiUpdate(ui)` | Follow the pointer now (hover, press, clicks, slider drags, toggle flips). `DrawUi` does this too; call it in Step to read this frame's clicks before drawing. A press or release is seen once however often either is called. |
| `UiGetValue(ui, id)` | A slider's, toggle's (0 or 1) or progress bar's value, as the player or `UiSetValue` left it. |
| `UiValueChanged(ui, id)` | True once after the player moves a slider or flips a toggle; reading it clears it. |
| `UiClicked(ui, id)` | True once after a press and release on the same button, slider or toggle; reading it clears it. |
| `UiGetHovered(ui)` | The enabled button, slider or toggle under the pointer, or `""`. |
| `UiSetSelected(ui, id, selected)` | Give an element its Selected look, for menus moved with keys or a controller. |
| `UiSetEnabled(ui, id, enabled)` | Enable or disable one element on this instance. |

The look order is Disabled, then Pressed, then Hover, then Selected. **Older UI files load and draw
exactly as before:** every new property defaults to the old drawing (a square solid box is still one
rectangle, an unaligned unspaced text is still drawn by the UI text call), and a file only gains the
new keys when it is saved again. The UI Editor's Inspector now also stays beside the canvas instead
of lying over its right side, and its property rows keep their order when an element type hides some.
Regression: `--test menu-clips` (pixels on all five renderers: transparent rounded corners, gradient
directions, the border, centred and right/bottom text, the hover look under the pointer, the slider
and toggle; a slider and toggle driven by simulated pointer input and read from a PGSL script; image
contain/cover/crop rectangles; an old file's draw calls unchanged; Inspector save/reopen).

## Models that share their clips

A Model can play the clips of other Models as its own: list them in its `.model.json` as
`"animationLibraries": ["Operator Moves", "Hollow Moves"]` (Model resource names). Characters on
one skeleton then keep one set of clips between them instead of a copy each.

- **Same skeleton** (the same bone names, in the same order, posed the same at rest): the clips
  are shared, so they are held once however many models use them.
- **Same bone names, other proportions or order**: each clip is retargeted when the model loads.
  Every bone keeps its own rest pose plus the library's movement from rest, and takes the library's
  rotation, so a clip made on one body plays on a taller or shorter one.
- A clip the model has itself wins over a library clip of the same name. Borrowed clips are never
  written into the model's own file, so saving the model in the Model editor keeps it small.
- **Clips play at their own frame rate.** An animator's clip rate (`ClipFps`, the Animator
  component's "Clip FPS") now defaults to 0, meaning each clip plays at the rate it was authored
  at. It used to default to 60, which overrode the clip's rate and played imported clips (baked at
  30 fps) at double speed. A rate you set still overrides the clip's (an Object or Room that saved
  60 explicitly keeps 60); scripts can set it too.
- **A GLB of clips with no meshes** (an animation library exported on its own) now imports as a
  clips-only Model to use as a library; it used to be refused, and stopped the whole import.
- **Clips-only Models open as an animation library.** The Model Viewer and the Model editor used
  to show such a Model as empty ("No model loaded", mesh tools with nothing to work on). They now
  show an **Animation library** panel instead of the mesh tools: the clips with their lengths, Play
  and Stop, and the usual timeline to scrub the selected clip. The clip plays on the library's own
  skeleton, drawn as bone lines and joints. **Choose body Model…** lists the project's Models and
  plays the clip on the one you pick (the library's clips are added to it exactly as
  `animationLibraries` would, retargeted to its proportions), with its frames in the timeline;
  **Skeleton only** returns to the bone lines. The clips are read-only there: Save leaves the
  library's files unchanged, and Import / Replace is off, so a library is never given a mesh by
  accident. Regression: `--test menu-clips`.
- **One file that cannot be imported no longer stops the others.** The rest of the batch is
  imported, then the failures are reported together ("Imported 9 of 10 file(s). Not imported: ...").

## Models as 2D sprites

The Model viewer and Model editor have **2D sprites…** on the command bar: it renders the model
with the engine renderer from 8, 16 or 32 facing angles (camera elevation, frame size,
orthographic or perspective, an optional animation clip, engine lighting, outline, transparent
background, with a live preview) into a new Image, frames ordered direction by direction, with the
layout saved in `usage.directions`. PGSL picks the frame for an angle (0 = right,
counter-clockwise, like `PointDirection`): `SpriteDirectionFrame(image, angle, frame?)`,
`SpriteSetDirection(angle, frame?)` for the calling instance, `SpriteDirectionCount(image)` and
`SpriteDirectionFrames(image)`. See [Convert a model to 2D sprites](ModelToSprites.md).

## Images to 3D Models

The Image Editor's **Options → File → Convert to 3D Model…** (also at the bottom of **Use in game**)
turns pixel art into a voxel Model resource: merged faces, vertex colours, and with all frames a
looping `Frames` flipbook clip (plus one clip per animation tag) that the Animator plays. A 3D preview with the shared gizmo moves,
rotates and resizes it before it is saved. See [Convert an Image to a 3D Model](ImageToModel.md).

## Start-up

- **Only the first room is prepared before it starts.** The Player used to prepare every texture,
  sound and model in the project before the first room (14 s of "runtime preload" in a project with
  3,749 assets). It now prepares the files the first room refers to: its room file and, through
  it, the Objects, Images, Models, Scripts and sounds it names, the ones their descriptors and event
  code name, and so on. Everything else is prepared when a room needs it; a room change already
  prepares its room behind its cover. The log line says what was covered:
  `Measured runtime preload: N asset jobs (referenced by the first room 'Menu')`.
  `GENESIS_PRELOAD_ALL=1` prepares everything first, as before. A first room that names an asset
  only in a way the walk cannot see (a name built from pieces at run time) loads it when it is
  first used.
- **Model textures are not packed into sprite sheets.** An Image whose allowed use is only
  "texture" is never drawn as a sprite, so the start no longer spends time packing it into the
  texture groups' sheets (10.5 s in the same project, most of it 398 model textures).

## Test runs

A Player opened by a test or tool (`GENESIS_UNATTENDED_WINDOW=1`) no longer takes the foreground:
its window is shown without being activated, and it neither reads nor vibrates the controller.

## Verification

`Build.bat --test pgsl-scripts` (the language fixes, globals, quitting, `TimeMs`, inverse
trigonometry, typed text), `--test effects-2d` (measuring, spacing, alignment, smooth shapes,
gradients, clipping, GUI order, project fonts), `--test asset-import` (picture size, Ogg decoding,
`SpriteWidth`), `--test release` (an unattended Player never holds the foreground),
`--test model-sprites` (a model converted to directional sprites, and frame selection by angle) and
`--test gui-linear` (GUI blending in linear light on all five renderers: 8% white, half white over
grey, an image, a switch mid-frame and the order across it, and the same scene blended as stored).
