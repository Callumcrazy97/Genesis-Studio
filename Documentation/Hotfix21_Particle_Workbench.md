# H21 — Particle Editor workbench

## Scope

H21 applies over H20. It completes a focused Particle Editor authoring pass, not the whole particle/rendering programme. Game templates and project content are not patched. The underlying `ParticleConfig` schema and ordinary Engine particle execution remain compatible. Public references are resource names only.

The normal workflow is **open/create a Particle resource → choose a preset → edit/add emitters → inspect the live preview → undo or redo → save → reopen**. Advanced text is optional. No JSON editing is required for this workflow.

Native compilation, the C# suites, visual appearance, GPU output, timings and interactive DPI testing have not been executed in the delivery environment. Source and archive checks are recorded separately in the master ledger. The authored acceptance cases below are not claimed as test results.

## Workspace

The left side has **Emitters**, **Presets** and **Preview** tabs. The central area is the existing shared 2D/3D viewport. Properties occupy the right side; the curve/colour editor and transport timeline sit below the preview. The **Panels** menu can hide the left, right or lower panel to give the preview more room, and Restore panels brings them back. These panel visibility choices are session-only, not a new persisted workspace system.

The top command bar keeps shared File/Edit, Save, dirty state, 2D/3D, View and Camera commands. Lower-priority commands use the existing More overflow at narrow sizes. The formerly non-functional particle transform-gizmo menu is not exposed. This patch does not implement spatial emitter gizmos.

Properties are grouped as **Emission / Forces / Material / Curves / Collision / Renderer / Effect Light**. Fields relevant only to box emission, a mesh source, mesh particles, flipbooks, active collision response or an enabled light are hidden when not applicable. The selected emitter is named above the Inspector. Light belongs to the whole effect, not the selected emitter.

## Emitters and presets

Select an emitter row to edit it. Tick its checkbox to enable or disable it. **F2** renames, **Ctrl+D** duplicates, **Delete** removes, and **Alt+Up / Alt+Down** reorder while the emitter list is focused. Native label editing retains ownership of its text keys. Add, Duplicate, Remove and reorder buttons provide the same operations with the mouse.

At least one emitter remains in the effect. Untick it for an empty preview; Remove is disabled for the final emitter. The stack supports up to 64 emitters, and names are locally unique, case-insensitively, within that particle effect. Emitter names are not separate project resources. The Particle resource itself still uses Genesis's single project-wide name namespace.

Add uses the emitter parameters of the chosen built-in preset. Selecting a card in **Presets** instead replaces the **whole effect**, including that preset's authored emitter stack. This is one reversible edit. The card search filters the existing built-in preset library; H21 does not add generated textures or new game assets.

Duplicate creates a detached emitter with a fresh local ID and an unused name. Reordering or removing the first emitter preserves the effect's light, preview settings, duration/range, notes, script field and other emitters' identities. This matters because the legacy runtime schema stores the first emitter in the root object; it must not accidentally promote a child's unrelated effect settings.

Selection-only changes do not dirty the document. The list reuses native rows for ordinary value updates instead of destroying the selected name editor or rebuilding the stack on each numeric input.

## Live preview and transport

**Pause/Play** pauses or resumes simulation. **Stop** rewinds and pauses. **Restart** rewinds using the same preview seed and retains the play/pause state. **Step** pauses and advances exactly one 1/60-second simulation step. **Burst** previews the chosen count in every enabled emitter without changing the authored burst count. Non-looping emitters retain their normal start burst on restart.

**Emit continuously** is different: it changes the selected emitter's authored Loop setting and is saved/undoable. Playback speed and random seed are preview-only preferences for the current editor session.

The preview uses fixed 60 Hz integration regardless of display update frequency. Speed choices are 0.25×, 0.5×, 1× and 2×. The viewport label says **60 Hz simulation**, not an invented measured FPS. No real-time performance guarantee follows from that label.

The **Preview** tab provides 2D, reference floor, seed and named model/terrain/object target selection. Restarting with the same seed, same configuration and same external preview conditions reproduces the random particle stream. Each emitter uses its stable local identity as a salt, so merely reordering it does not change its random stream. A new seed deliberately changes it. This is not a guarantee of bit-identical GPU pixels across backends or hardware.

Ordinary rate, force, colour, curve, size and similar scalar edits update the current simulation rather than clearing it. Existing particles therefore remain in flight; changes to velocity-affecting settings act on subsequent simulation steps, and spawn parameters affect new particles. Reducing capacity can legitimately discard excess particles. Structural edits, enable/disable, preset changes and undo/redo restart the preview. Changes to texture, mesh, flipbook geometry or blend can recreate the corresponding render resources, rather than doing so on every wheel tick.

### Timeline seeking

**Preview range** sets the saved timeline range; it is not a new gameplay emitter-stop timer. Scrubbing pauses and requests a replay from the deterministic start. Replay is pumped by a UI timer in slices of at most eight simulation steps, with a four-millisecond budget checked *between* steps. A single expensive simulation step can exceed that budget: this is a scheduling bound, not a guaranteed four-millisecond frame.

The requested position is displayed immediately. While work remains, the status shows Seeking. New scrub requests replace old ones. Stop, Play, Step or changing emitter properties cancels obsolete replay. At most the first 60 seconds of a long authored range are scrubbed, preventing minutes of synchronous work on the UI thread. No `Task.Run` simulation thread races the renderer or accesses WinForms controls.

## Curves, colour and alpha

Choose Size, Speed, Alpha or Velocity over lifetime, then drag a Bezier control point. The corresponding custom-curve switch turns on. In the Curves properties section, turn that switch off to return to the standard interpolation without deleting your custom curve.

A continuous drag is one document undo entry. **Escape** restores its starting value without adding a history entry. Gradient dragging follows the stop object rather than its old list index, so dragging a key past another key does not switch which key is being moved.

Double-click inside the gradient bar to add a colour key. Double-click an existing key marker to edit its colour. Right-click a key to remove it, retaining at least two keys. The wheel over a key adjusts its alpha. A checkerboard makes transparency visible. The authoring limit is 128 keys. Editing colour/alpha keys does not accidentally enable the currently selected size/speed curve.

Gradient painting no longer sorts all stops and constructs a colour object for every pixel; it samples the maintained order with value-type channel data and reuses a drawing pen. This is a source-level allocation reduction, not a measured latency claim.

## Image/model references

Texture, mesh-particle and mesh-surface fields use the shared type-filtered pickers. The selected value is a resource name such as **Soft Glow**, not a source filename. The × button clears an assigned reference and returns to the default/fallback presentation; clearing participates in undo.

Changing or clearing a mesh-surface source also clears previous sampled points before loading its replacement, so a missing new model cannot keep emitting from the old model silently. Asset dependency refresh continues to invalidate the existing preview caches.

## Save, undo and Advanced drafts

The Particle Editor now uses the shared document journal with a 100-operation cap. Numeric edits are individual undo steps; structural operations and curve drags are single steps. Snapshots contain authored data, not particle simulation arrays or textures. Undoing back to the last saved content clears the dirty marker; redoing away from it marks the document dirty. History is session-only.

**Save copy…** creates a new resource under the protected Particles root using a unique public name. It does not change the original document's identity or silently mark its unsaved edits as saved. Resource-name rules, project save coordination and backups remain the existing shared systems.

**Advanced** opens the selected emitter's optional declarative definition. Accepted changes are applied after the existing short edit debounce or explicitly with Apply draft. Revert draft discards uncommitted text. Unknown keys, duplicate properties, incomplete braces, malformed numbers, undefined enum values, invalid colours, unsafe counts and filename/path references are reported inline. Numeric/colour serialization retains round-trip precision rather than rounding unrelated data during a text edit.

An invalid pending draft blocks emitter switching, conflicting property edits, mode changes and Save. The previous valid document and file remain intact. This also lets Studio's existing Run/Debug/Export save boundary stop rather than knowingly launching stale particle content. Corrupt JSON source fails before the control subscribes to document/theme events; it is not silently replaced by a Fire preset.

This Advanced pane edits emitter configuration, not a new PGSL runtime and not the root effect envelope. Existing particle script fields are preserved, but H21 does not implement missing particle-specific script events.

## Deliberate remaining limits

This delivery does not claim GPU compute particles, trails/ribbons, subemitters, particle event scripting, universal emitter transform gizmos, arbitrary triangle collision, cross-backend visual parity or AAA particle budgets. The inherited model preview is a visual target; height-plane/terrain collision does not become full model-surface physics. Effect-light preview is a 3D facility. The preview's spatial settings and seed must be held constant for a meaningful deterministic comparison.

The complete shared viewport, undo, document/recovery, asset database and particle runtime programmes remain open. H21 finishes the authoring/transport/editor-safety slice described here without starting those larger features.

## Native acceptance

Build and focused suite:

```powershell
.\Build.bat
.\Build.bat --test particle-workbench
```

The build identity should be **H21**. The focused suite contains 57 authored cases: 20 exact-production clock cases, 18 document/codec/simulator cases and 19 control-level cases. It is not a substitute for a visual playthrough. The framework-independent runner adds those 20 clock cases to its 94 existing cases:

```powershell
dotnet run --project Tests\Genesis.Foundation.Checks --configuration Release
```

Human check: create a Particle resource, apply Fire, add Smoke, and change rate/force/colour while watching the preview. Select and rename emitters, duplicate/reorder/remove, then undo back. Scrub a long preview, cancel it, single-step, and restart with the same seed. Drag a curve and gradient key and undo each in one action. Save/reopen the layered effect. Finally enter an invalid Advanced property, confirm Save is refused without altering the file, and revert the draft. Check panel overflow/visibility and keyboard access at 100%, 125%, 150% and 200% DPI.
