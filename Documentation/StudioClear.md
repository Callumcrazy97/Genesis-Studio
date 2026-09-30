# Studio "Clear" redesign

Goal: every editor should be as simple as 1-2-3 — obvious steps, one starting point you can click,
one primary action — without removing the depth that is already there.

Restore point: tag `backup/pre-shell-redesign-2026-09-30` (and a local zip taken the same evening).

## Principles

1. **One frame for every editor.** Command bar → workflow bar (numbered, clickable steps, one plain
   instruction, *Next ›*) → tools for the current step | view | context panel → status.
2. **Start from something.** Step 1 of each editor offers ready-made starting points; one click
   applies them and every one is undoable.
3. **Show the current step.** Advanced controls stay in Options / "More settings". One route per
   function where possible.
4. **Honest UI.** Features that only demonstrate the simulator are labelled *Preview only*.
5. **Readable.** Larger secondary text, sentence-case headings (no brackets, ALL CAPS or `-- x --`),
   Fluent icons, rounded pills, an accent primary action and dark title bars.

## Shared components (`Source/Genesis.Application.Core/UI`)

| Component | Purpose |
| --- | --- |
| `UiTokens` | Live palette and fonts shared by Studio, the editor suite and the Image editor. Studio pushes its theme here through `SuiteChromeBridge`. `DisplayHeading` turns ALL-CAPS captions into sentence case and keeps acronyms. |
| `UiGlyphs` | Segoe Fluent Icons / Segoe MDL2 Assets code points, each checked against a rendered sheet. Captions always remain, so an icon is never the only label. |
| `PillToolStripRenderer` | Rounded hover/pressed pills, an accent-tinted checked mode, an accent outline for an editor's primary command (`PrimaryTag`, used for *Use in game* and *＋ New*) and a filled accent button (`AccentTag`, used for *Run*). `GenesisToolStripRenderer` and the editor chrome derive from it. |
| `WorkflowBar` / `WorkflowStep` | The guided-steps bar. Buttons are named `WorkflowStep_<Id>`, Next is `WorkflowNext`. `SetCurrent`, `Activate`, `GoNext`, `SetInstruction` and `RefreshProgress` (check marks from `IsDone`). Narrow bars drop the instruction, then show numbers only; editors shorter than 620 logical px fold the bar away (`AutoHideBelowHeight`) so short windows keep their viewport. Keyboard and screen-reader accessible. |
| `StarterGallery` / `StarterItem` | Card grid grouped by category. Cards are named `Starter_<Id>`, chosen with one click, Enter or Space, and can carry a badge (*Next*, *✓ Done*, *Preview only*). `Compact` gives two narrow columns for side panels; `FitsContent` sizes the gallery to its cards inside a page that already scrolls. |
| `ResourceKindVisuals` | Icon, colour, category and one-line purpose for each resource kind. |
| `WindowChrome` | Dark Windows title bars (DWM attribute 20, falling back to 19) for `DpiAwareForm` windows and every themed dialog. |
| `EditorWorkflow` (Editors.Suite) | Docks a workflow bar directly under an editor's command bar. |

## Shell

- **One app bar.** File, Edit, View, Tools and Help on the left; *▶ Run* (filled accent), Debug,
  Validate (with its issue count), Undo, Redo and *Save project* on the right of the same row. The
  second 44 px command row is gone.
- **Home.** *Your game in 4 steps* (draw a sprite, make an Object, build a Room, press Run) is ticked
  off from what is on disk, with the next step marked. *Create something new* shows every resource
  kind with its icon and purpose; a card asks for a name, creates the resource and opens it.
  *Continue where you left off* and *Project at a glance* follow.
- **Assets.** *＋ New* is the panel's primary action; resource names use the full text colour.
  The Finder stays in the Assets dock beside the tree it drives.

## Editors

| Editor | Steps | One-click starters and changes |
| --- | --- | --- |
| Terrain | Shape › Sculpt › Paint › Decorate › Use in game | The ten landforms as cards with a shaded relief of each, directly on the Create page (the wizard stays for size, seed and processes). Each mode's tip becomes the bar's instruction. |
| Model viewer | Import › Edit › Animate › Use in game | The bar takes the old hint's row, so the view keeps its height. |
| Model editor | Start › Shape › Surface › Rig & animate › Use in game | *Generate › Tree… / Rock…* adds seeded procedural parts. The Rig page offers *Fit a ready-made rig (recommended)* first. |
| Image | Draw › Animate › Rig › Use in game | The rig studio has its own Bones › Bind › Pose › Animate bar with done ticks. Hidden when the Image editor paints model materials. |
| Shader | Look › Preview on › Tune › Use in game | Every look says what it applies to (Model, Image, Terrain, Particle, Full screen). |
| Object | Look › Behaviour › Test › Use in game | Behaviour recipes (arrow-key movement, platformer controls, patrol, chase the player, projectile, wrap around, collectible, spin, float, disappear after 3 seconds) append readable PGSL to the right events, marked so a recipe is never added twice. A new Object's Behaviour step opens them; Options has *Behaviour recipes…*. |
| Particle | Effect › Tune › Use in game | The Effect step opens the preset tiles (previously two menus deep); choosing one moves on to Tune. |
| Physics | What is it? › Set up › Tune › Test › Use in game | Presets grouped as Materials, Worlds and gravity, and Test playgrounds (*Preview only*). Plain labels: Grip (friction), Bounciness, Weight (density). |
| Room | 3D: Ground › Place › Sky › Camera › Use in game; 2D: Tiles › Place › Background › Camera › Use in game | Scene presets (Sunny day, Golden hour, Overcast, Stormy, Snowy, Night, Alien world, Indoor) set the sky, clock, weather and clouds as one undoable edit. A visible *2D | 3D* switch sits on the command bar. |

Unchanged on purpose: command bar item lists, mode rails, control names and page names that the
headless suites pin, and every existing route to a feature. The old "1. 2. 3." hint labels remain
in the tree, hidden, where layout code measures them.

## Tests

`ClearWorkflowSuite` (`--test clear`; `--test clear-core` runs the four window-free cases):
tokens and headings, workflow bar behaviour, gallery choice, every behaviour recipe (alone and all
2D recipes combined) through `PgslScriptValidator`, Terrain steps and landforms with undo, Object
recipes appending once, and Room scene presets with undo and the 2D/3D step switch. It is part of
the full regression run.

## Follow-ups

- Particle *Use in game*: attach the effect to an existing Object as well as creating a new one.
- Shader *Use in game*: create a Model Object for model-target looks.
- Captures of every editor and the shell at 100/150/200 % for the design record.
