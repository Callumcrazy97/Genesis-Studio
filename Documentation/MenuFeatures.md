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

In a DrawGui event, shapes and text reach the screen in the order the script drew them, so a panel
drawn after a label covers it. Before, all text was drawn after all shapes. Images (`DrawSprite`,
`DrawSpritePart`) are still drawn beneath the shapes and text of the same frame.

| Command | Meaning |
|---|---|
| `DrawSetClip(x, y, width, height)` | Later GUI drawing (shapes, text and images) shows only inside this rectangle. |
| `DrawResetClip()` | Draw everywhere again. A clip also ends with the event that set it. |

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
| `WindowSetCursorVisible(visible)` | Show or hide the system pointer over the game window, for a menu that draws its own. |
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
- The strict check rejects a function parameter named like an instance variable (`id`, `x`,
  `depth`...): it read the instance's value, not the argument.

## Assets

- **Ogg Vorbis sounds play.** An imported `.ogg` Audio resource was silent in games; Ogg files are
  now decoded like WAV (by the file's content, not its name), in games and in the Audio editor.
- **An imported picture keeps its size.** Its Image gets the picture's own size as its canvas
  instead of 64 x 64.

## Test runs

A Player opened by a test or tool (`GENESIS_UNATTENDED_WINDOW=1`) no longer takes the foreground:
its window is shown without being activated, and it neither reads nor vibrates the controller.

## Verification

`Build.bat --test pgsl-scripts` (the language fixes, globals, quitting, `TimeMs`, inverse
trigonometry, typed text), `--test effects-2d` (measuring, spacing, alignment, smooth shapes,
gradients, clipping, GUI order, project fonts), `--test asset-import` (picture size, Ogg decoding,
`SpriteWidth`) and `--test release` (an unattended Player never holds the foreground).
