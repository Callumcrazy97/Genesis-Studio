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

- **Names are case-sensitive**: `mbx` and `mbX` are two variables, in an event, in a function and
  on the instance alike, so a function's `var mbx` never stands in for the instance's `mbX`. Only
  the built-in instance variables (below) are the same whatever their case.
- In an event, a plain assignment (`hp = 5`) sets the instance's variable, and so does `var hp`.
- **Inside a function**, a plain assignment or `var` makes a variable of that call, gone when it
  returns. To set the instance's own variable from a function write **`self.hp = 5`** (read it with
  `self.hp`). `VariableSet("hp", 5)` and `InstanceVariableSet(id, "hp", 5)` do the same.
- A function's parameters and locals are its own: a function never changes the variables of the
  function that called it.
- A function that reads a name it has not set itself finds the variable of the function calling it,
  if that one has it, before the instance's. Give such values as parameters rather than rely on it.
- **Inside `with (target)`** a name not declared with `var` is the target instance's: in
  `var n = 0; with ("Child") { if (n < 5) { InstanceDestroy(id); n += 1; } }` the counter is the
  caller's because of `var`; without it every child would have its own and all would go.
- A function defined in a project **Script** (a library) can be called from any object directly.
  Give each library function its own name: a Script run as a whole still uses its own, but a call
  by name reaches only one of two that share a name (in any case). Run (F5) warns about every such
  name in the Console, listing each Script, line and parameter count, and still runs the game.
- `GlobalSet` / `GlobalGet` (and `GlobalSetString`, `GlobalExists`, `GlobalDelete`) hold values
  every instance and script sees.
- Reading a name nothing has set gives 0 (and a runtime note), not an error. Run (F5) checks every
  script first and stops on a name that nothing in the project sets or names: any script's
  assignment (top level or in a function, `self.x` included) or any quoted name, such as
  `VariableSet("plx", ...)` or `GlobalSet("score", ...)`, counts. A name set only under a computed
  name (`VariableSet("slot" + i, ...)`) is not known: name it once in quotes. The Console lists every
  error with the total.

## Names the engine owns

- **Keywords**: `if else while for repeat with function return var true false from break continue`.
  `from` starts a namespace block, so it cannot be a variable. The block is the one statement
  after the colon: `from Engine.Sky: { AmbientScale = 1.2; Visibility = 600; }` needs the braces to
  cover both. A setting can also be written in full: `Engine.Sky.AmbientScale = 1.2;`.
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
| Instructions | 100 000 per event body and per function call (each call has its own count), unless the game raises it with `ScriptInstructionLimit(instructions)` (up to 1 000 000 000, for the rest of the game; a lower number restores 100 000; it returns the limit in force) | "Infinite loop detected: Maximum instruction limit (100000) exceeded in function 'Name'". Split long work over several frames, raise the limit (a world generator's Create event: `ScriptInstructionLimit(50000000);`), or run it as a worker job. |
| Recursion | 200 calls deep | "Too much recursion: 'Name' is 200 calls deep". Use a loop with a list or stack (`DsStack*`) for deep work. |
| Script calls | 64 Script resources deep | |
| A worker job's instructions | 100 000 000 for the whole job, every call counted, unless `JobScriptBudget` sets another; no per-call limit | "The job ran more than its budget of N instructions". See [script functions on worker threads](GameFeatures.md#script-functions-on-worker-threads). |

## Speed

The VM decodes each event body and function once (operands unboxed, constants converted, every
name resolved) and reuses that, keeping the operand stack in locals while it runs. A function's
parameters and `var` names live in numbered slots of its frame; a name it has not set is still
looked for in each caller's frame and then the instance, exactly as before. A command whose
parameters are all numbers, bools or text (`DsGridGet`, `DsListGet`, `DsMapGet`, `Floor`, `Clamp`,
`MeshAddVertex`, `VariableSet`...) is called through a compiled call that takes numbers unboxed and
text as it is; any other value (text for a number, a list, an object, `params`) goes the general
way with the same conversions and errors. Nothing about the language changed: scoping, `self.`,
the instruction budget and count, and every message are the same, and `--test vm-speed` checks that
a voxel project's light spread and chunk mesher produce the same light, faces and vertices as
before.

`--test vm-speed` times the VM as the Player runs it (one instance's VM, no diagnostics collector)
and writes `vm-speed.md` beside its captures and `Logs\vm-speed.json`. Before and after, run in
turn three times each on the development PC, 6 Oct 2026; fastest runs (the medians give the same
ratios):

| Workload | Before | After |
|---|---:|---:|
| Arithmetic loop on locals (per instruction) | 10.8 ns | 2.6 ns |
| Small user-function call, overhead per call (median) | 100 to 180 ns | 53 ns |
| `DsGridSet` / `DsGridGet` / `DsListGet` in a loop (per command) | 1 230 ns | 59 ns |
| `Clamp`, `Floor`, `Max`, a 13-argument `MeshAddVertex` in a loop (per command) | 382 ns | 45 ns |
| Voxel light spread, 10 920 cells (`McLightSpreadSome`, about 640 instructions a cell) | 189 ms | 37 ms |
| Voxel chunk mesh, 1 740 smooth-lit faces (`McBuildColumn` / `McCubeFaces` / `McFaceLit`) | 84 ms | 17 ms |

What things cost now, roughly (one loop body, the loop itself subtracted): an instruction 2 to 3 ns;
`t = i` on locals 5 ns; reading an instance variable inside a function 14 ns, plus about 2 ns for
each caller in between; `Floor(i)` 14 ns; `Clamp(i, 0, 9)` 25 ns; `DsListGet`, `DsGridGet`,
`DsMapGet`, `VariableSet` 35 to 50 ns; a call of a small function 60 to 75 ns. So keep hot values in
`var` locals (copy an instance variable into one before a long loop), write a two-line helper inline
in a loop of thousands, and prefer one command over a loop of small ones where the engine has it
(`DsGridSetRegion`, `MeshAddCube`, the [whole-region grid commands and noise](GameFeatures.md#noise)),
or run the work as a [job on a worker thread](GameFeatures.md#script-functions-on-worker-threads).

Since 8 Oct 2026 a command taking up to four numbers and returning a number or nothing (`DsGridGet`,
`DsGridSet`, `DsListGet`, `Floor`, `Min`...) is called straight from the VM's stack through a
delegate bound to it, and a call finds its function by the name it carries without hashing the
text. Measured in turn with both turned off (`GENESIS_VM_FAST_CALLS=0`), on the performance cores:
the grid and list loop 0.48 ms against 0.57 to 0.60 ms (medians), `DsGridGet` 38 to 40 ns against
42 to 44 ns, `Floor` 12 to 15 ns against 15 to 16, a call with three arguments 61 to 69 ns against
70. The voxel light spread (37 ms) and mesher (17 ms) did not change: their time goes to many small
calls and instance-variable reads. A lattice hash written in script (`Sin`, multiplies, `%`) costs
about 160 to 180 ns a call, value noise made of four of them about 0.9 microseconds; `ValueNoise2D`
does the same natively.

On 9 Oct 2026 `vm-speed` also generates a chunk with a voxel game's own terrain generator, copied
unchanged (GenesisCraft's `McNoise2`, `McColumnCalc`, `McClimateAt`, `McLatMix`, `McGenColumnsRow`,
`McVein`...: 469 000 instructions for one 16 x 16 chunk's columns and ores). It takes 2.6 to 2.9 ms,
about 6 ns an instruction, so the VM runs some 170 million of that game's instructions a second
(the light spread and mesher above run at about 5 ns). Debug mode does not slow the VM: the
Player's VMs run without a debugger in either mode, and profiling times whole events. What a debug
recording showed as `Dictionary<String,Object>`, `Entry[String,Object][]` and most of `Int32[]` was
the debug inspector copying every Object's storage (each list, grid and map) every frame; it now
skips the engine's own `__` entries. The VM's own allocations, measured per run by `vm-speed`
(bytes this thread allocated): none per instruction or call. Storing a number in a list, stack,
queue, map or named array, or with `VariableSet`, used to allocate a box (24 bytes) each time;
whole numbers from -1 024 to 65 535 now share one box each, so `DsListSet`, `DsListAdd`,
`DsMapSet` and `VariableSet` of such numbers allocate nothing (`GENESIS_VM_SHARED_BOXES=0` turns
it off, for comparison: then each `DsListSet`, `DsMapSet` and `VariableSet` allocates 24 bytes and
takes 14 to 15 ns longer, and the light spread allocates 524 KB a run instead of 262 KB). Other
numbers still take a box: the light spread's queue of
packed cells allocates 262 KB for its 10 920 cells. Reading an instance variable from inside a
function skips callers that cannot hold it (11 to 16 ns where it was 14 to 21), and a call no
longer keeps a second stack of names. Measured on the performance cores (`GENESIS_TIMING_AFFINITY=0xFFFF`
pins `vm-speed`): the mesher 16.5 to 16.8 ms (was 17.2 to 17.8), the light spread unchanged at
37 to 38 ms, `DsListSet` 36 ns, `VariableSet` 39 ns (was 44 to 53).

Two things make timings misleading on the development PC. A process that keeps compiling new code
(a test run of many suites) postpones the runtime's optimised recompile of the VM, which then runs
about three times slower until it settles: `vm-speed` and the timings in `pgsl-logic` warm up
first. And it has performance and efficiency cores: a run on an efficiency core takes about twice
as long, so pin a timing run to the performance cores (processor affinity 0xFFFF there).

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

### List values

`var a = [1, 2, 3];` makes a list; `a[1]` reads it and `a[1] = 9` writes it. Lists can hold numbers,
text and other lists (`grid[1][0]`). A list is held by reference: `var b = a;` names the same list,
and `PgListCopy(a)` makes a separate one. Reading past the end is an error that says how long the
list is; writing past the end grows the list, filling the gap with 0. `String(a)` writes it as
`[1, 9, 3]`.

| Command | What it does |
|---|---|
| `PgListCreate(values...)` | A list of the values (what `[...]` makes). |
| `PgListSize(list)`, `IsList(value)` | Its length; whether a value is a list. |
| `PgListGet(list, i)`, `PgListSet(list, i, v)` | The same as `list[i]` and `list[i] = v`. |
| `PgListAdd`, `PgListInsert(list, i, v)`, `PgListRemove(list, i)`, `PgListClear` | Change the entries. |
| `PgListPop`, `PgListDequeue` | Remove and return the last or first entry (0 when empty). |
| `PgListFind(list, v)`, `PgListSort(list, ascending)`, `PgListShuffle`, `PgListCopy` | Search, order and copy. |

Like the other collection commands they ignore a value that is not a list.

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
