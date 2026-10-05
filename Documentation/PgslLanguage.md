# PGSL language notes

How PGSL scripts behave: operators, loops, variables and scope, the names the engine owns, the
limits, and what happens on a mistake. Every statement below is checked by the headless
`pgsl-logic` target, which also writes a table of every check and of every registered command
(`pgsl-logic.md` beside its captures).

## Operators

| Kind | Operators | Notes |
|---|---|---|
| Arithmetic | `+ - * / %` | `/` is real division (7 / 2 = 3.5). `%` is floored: -7 % 3 = 2. |
| Comparison | `== != < <= > >=` | |
| Logical | `&& \|\| !` | **Short-circuit**: the right side of `&&` is not evaluated when the left is false, nor the right side of `\|\|` when the left is true, so `if (i < n && DsListGet(l, i) > 0)` is safe. The result is 1 or 0. `and`, `or` and `not` are not keywords. |
| Bitwise | `& \| ^ ~ << >>` | On 32-bit integers. `+` binds tighter than `<<` (1 + 2 << 1 = 6). |
| Conditional | `c ? a : b` | Only the chosen side is evaluated. |
| Assignment | `= += -= *= /=`, `x++`, `x--` | `++`/`--` only as a statement of their own. There is no `%=`. |
| Text | `+` | Joins text; a number joined to text becomes text ("n" + 5 = "n5"). |

## Control flow

`if` / `else if` / `else`, `while`, `for (init; condition; step)`, `repeat (n)`, `with (target)`,
`function Name(a, b) { ... return value; }`.

- **`break`** leaves the innermost `while`, `for` or `repeat`; **`continue`** goes on to its next
  pass (in a `for`, the step runs first). Neither may jump out of a `with` block, and both are
  errors outside a loop.
- There is no `switch` and no `do ... until`; use `if` / `else if` and `while`.

## Variables and scope

- In an event, a plain assignment (`hp = 5`) sets the instance's variable, and so does `var hp`.
- **Inside a function**, a plain assignment or `var` makes a variable of that call, gone when it
  returns. To set the instance's own variable from a function write **`self.hp = 5`** (read it with
  `self.hp`). `VariableSet("hp", 5)` and `InstanceVariableSet(id, "hp", 5)` do the same.
- A function's parameters and locals are its own: a function never changes the variables of the
  function that called it.
- A function that reads a name it has not set itself finds the variable of the function calling it,
  if that one has it, before the instance's. Give such values as parameters rather than rely on it.
- `GlobalSet` / `GlobalGet` (and `GlobalSetString`, `GlobalExists`, `GlobalDelete`) hold values
  every instance and script sees.
- Reading a name nothing has set gives 0 (and a runtime note), not an error.

## Names the engine owns

- **Keywords**: `if else while for repeat with function return var true false from break continue`.
  `from` starts a namespace block (`from Engine.Sky: ...`), so it cannot be a variable.
- **Built-in instance variables**, the same whatever their case: `id instance_id self x y z hspeed
  vspeed speed direction friction gravity gravity_direction sprite_index image_index image_speed
  image_alpha image_angle image_xscale image_yscale visible depth solid room_width room_height
  draw_alpha draw_font_size alarm0`…`alarm11 user_var0`…`user_var11`. Assigning `speed = 3` sets the
  instance's speed. Engine properties such as `Fps`, `MouseX` and `DeltaTime` also read as names.
- A **function declared in a script** is called even when an engine command has the same name: a
  script's `function Depth(n)` is the script's, not the built-in `Depth`.

## Limits

| Limit | Value | When it is reached |
|---|---|---|
| Instructions | 100 000 per event body and per function call (each call has its own count) | "Infinite loop detected: Maximum instruction limit (100000) exceeded in function 'Name'". Split long work over several frames. |
| Recursion | 200 calls deep | "Too much recursion: 'Name' is 200 calls deep". Use a loop with a list or stack (`DsStack*`) for deep work. |
| Script calls | 64 Script resources deep | |

## Mistakes

| Mistake | What happens |
|---|---|
| A syntax error in an object's event | The event does not run; the error names the line. |
| A syntax error in a library Script | It is written to the log when the project loads ("PGSL script failed to compile: Assets\Scripts\Lib.pgsl, line 2: ..."), and a call to one of its functions says so instead of only "Unknown command". F5 also reports it. |
| A command given the wrong kind of value | An error naming the command, the argument and what it was given, and the text variant when there is one: "DsListAdd: argument 2 (value) must be a number, but was given the text "x". Use DsListAddString for text." Text holding a number ("12.5") is read the same on every machine. |
| A command given too few arguments | The missing ones are 0 or "" (and a runtime note says so). |
| A list, map or grid id that does not exist; a missing map key | 0 or "". |
| `Real("abc")`, `Sqrt(-1)` | 0. |
| `1 / 0` | Infinity. |
| An unknown command | "Unknown command: Name". |

## Text and lists

String positions count from 1: `StringPos("l", "hello")` is 3 (0 when absent),
`StringCopy("hello", 1, 3)` is "hel", `StringSplitPart("a,b,c", ",", 1)` is "a". `StringReplace`
replaces the first match, `StringReplaceAll` every one. Lists, grids and arrays count from 0.
`Round` rounds halves away from zero (2.5 is 3, -2.5 is -3). `Sin`, `Cos` and `PointDirection` work
in degrees; `PointDirection` is in screen space (straight up, towards -y, is 90).

## Events

In one frame: Step Begin, Step, Step End, then alarms, Draw and Draw GUI. Create runs once before
the first frame (after Game Start and Room Start).

## Particles

`ParticleBurstAt(particle, x, y, z, scale, seconds)` plays a Particle resource once and removes
itself: an effect that does not loop emits its burst count; a looping one emits for `seconds`
(0.25 s if 0); then the emitter goes once its last particle has lived. A burst whose resource
cannot be loaded is removed after 5 seconds. `SpawnParticleEmitter` keeps emitting until destroyed.
A particle's gravity is positive upwards (world units per second squared): use a negative value for
falling sparks.

## Esc

By default Esc closes a game. A game that wants Esc for itself (a pause menu) turns off **Allow Esc
to close the game** in the project's settings (`allowEscapeToClose` in an exported game's
`GenesisGame.json`) and handles the Escape key in its own events.

## Tests and pictures

`ScreenshotSave(name)` saves a picture of the drawn frame during a run, so a scripted test can take
as many as it needs without `--autoshot`, which ends the run after its picture.
