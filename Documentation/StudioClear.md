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
| `WorkflowBar` / `WorkflowStep` | The guided-steps bar. Buttons are named `WorkflowStep_<Id>`, Next is `WorkflowNext`. `SetCurrent`, `Activate`, `GoNext`, `SetInstruction` and `RefreshProgress` (check marks from `IsDone`). Narrow bars drop the instruction, then show numbers only; editors shorter than 560 logical px fold the bar to zero height (`AutoHideBelowHeight`) so short windows keep their viewport. It folds by height, never by `Visible`: re-showing a docked control lets Windows move it in the sibling z-order. Keyboard and screen-reader accessible. |
| `StarterGallery` / `StarterItem` | Card grid grouped by category. Cards are named `Starter_<Id>`, chosen with one click, Enter or Space, and can carry a badge (*Next*, *✓ Done*, *Preview only*). `Compact` gives two narrow columns for side panels; `FitsContent` sizes the gallery to its cards inside a page that already scrolls. |
| `ResourceKindVisuals` | Icon, colour, category and one-line purpose for each resource kind. |
| `WindowChrome` | Dark Windows title bars (DWM attribute 20, falling back to 19) for `DpiAwareForm` windows and every themed dialog, and theme-matched scroll bars on scrolling panels, lists, list views, multi-line text boxes and the Assets tree (`ApplyScrollTheme`; other tree views are left alone because the Explorer theme repaints their selected row). Switching theme updates every control that opted in. |
| `EditorWorkflow` (Editors.Suite) | Docks a workflow bar directly under an editor's command bar. |

## Shell

- **One app bar.** File, Edit, View, Tools and Help on the left; *▶ Run* (filled accent), Debug,
  Validate (with its issue count), Undo, Redo and *Save project* on the right of the same row. The
  second 44 px command row is gone.
- **Home.** *Your game in 4 steps* (draw a sprite, make an Object, build a Room, press Run) is ticked
  off from what is on disk, with the next step marked. *Create something new* shows every resource
  kind with its icon and purpose; a card asks for a name, creates the resource and opens it.
  *Continue where you left off* and *Project at a glance* follow.
- **Straight to the canvas.** *Draw a sprite* (or creating an Image from Home) asks for the canvas
  size and opens the Image editor, instead of stopping in the viewer; a new Model opens in the
  Model editor on its *Start* step.
- **Getting started.** *Help › Getting started* (F1) and the *Getting started* button on Home open
  [GettingStarted.md](GettingStarted.md) in a themed window inside Studio (`GuideViewerForm`), so
  the guide does not depend on the machine having a Markdown viewer. The guide ships beside the
  EXE and in the installer. *Help › Genesis Documentation* still reveals the engineering README.
- **Assets.** *＋ New* is the panel's primary action; resource names use the full text colour.
  The Finder stays in the Assets dock beside the tree it drives. Folders that contain something
  show an open/closed chevron (the row fill used to paint over the tree's own expand button, so
  nothing showed that a folder opened), the selected row is an opaque accent tint, and the tree's
  scroll bar follows the theme.

## Editors

| Editor | Steps | One-click starters and changes |
| --- | --- | --- |
| Terrain | Shape › Sculpt › Paint › Decorate › Use in game | The ten landforms as cards with a shaded relief of each, directly on the Create page (the wizard stays for size, seed and processes). Each mode's tip becomes the bar's instruction. |
| Model viewer | Import › Edit › Animate › Use in game | The bar takes the old hint's row, so the view keeps its height. |
| Model editor | Start › Shape › Surface › Rig & animate › Use in game | *Generate › Tree… / Rock…* adds seeded procedural parts. The Rig page offers *Fit a ready-made rig (recommended)* first. |
| Image | Draw › Animate › Rig › Use in game | The rig studio has its own Bones › Bind › Pose › Animate bar with done ticks. Hidden when the Image editor paints model materials. |
| Shader | Look › Preview on › Tune › Use in game | Every look says what it applies to (Model, Image, Terrain, Particle, Full screen). *Use in game* creates a 2D sprite Object for image looks and a 3D Object that draws the chosen Model for model looks. |
| Object | Look › Behaviour › Test › Use in game | Behaviour recipes (arrow-key movement, platformer controls, patrol, chase the player, projectile, wrap around, collectible, spin, float, disappear after 3 seconds) append readable PGSL to the right events, marked so a recipe is never added twice. A new Object's Behaviour step opens them; Options has *Behaviour recipes…*. |
| Particle | Effect › Tune › Use in game | The Effect step opens the preset tiles (previously two menus deep); choosing one moves on to Tune. *Use in game* can create a new effect Object or attach the effect to one of the project's existing Objects. |
| Physics | What is it? › Set up › Tune › Test › Use in game | Presets grouped as Materials, Worlds and gravity, and Test playgrounds (*Preview only*). Plain labels: Grip (friction), Bounciness, Weight (density). |
| Room | 3D: Ground › Place › Sky › Camera › Use in game; 2D: Tiles › Place › Background › Camera › Use in game | Scene presets (Sunny day, Golden hour, Overcast, Stormy, Snowy, Night, Alien world, Indoor) set the sky, clock, weather and clouds as one undoable edit. A visible *2D | 3D* switch sits on the command bar. |

| Audio | Sound › Tune › Listen › Use in game | |
| Pathing | Route › Preview › Use in game | |
| UI | Start › Design › Use in game | Start opens the ready-made HUD and menu layouts. |
| Script | Write › Check › Use in game | |

Unchanged on purpose: command bar item lists, mode rails, control names and page names that the
headless suites pin, and every existing route to a feature. The old "1. 2. 3." hint labels remain
in the tree, hidden, where layout code measures them.

## Tests

`ClearWorkflowSuite` (`--test clear`, 13 cases; `--test clear-core` runs the six window-free ones):
`Editor.Workflow.EveryEditorShowsItsStepsUnderTheCommandBar` opens all thirteen editors in the
table above and checks each has one bar with the documented steps, every step button visible and
the bar directly beneath the command bar. The rest cover
tokens and headings; workflow bar behaviour; gallery choice; every behaviour recipe (alone and all
2D recipes combined) through `PgslScriptValidator`; every recipe executed for 30 frames in
`ObjectSandbox` with patrol, projectile, float and timer effects checked; Terrain steps, bar fold
and unfold, and landforms with undo; Object recipes appending once; Room scene presets with undo
and the 2D/3D step switch; attaching a particle effect to an existing Object; and a model look
creating a shaded 3D Object.

`BeginnerJourneySuite` (`--test journey`) is the claim "you can make a game with the guided steps"
as two tests, each starting from an empty project and ending in the live runtime:

- **2D:** three Objects get their behaviour only from recipes, the Room editor places one of each,
  and the saved Room is stepped with the Right arrow held. The Player walks, the chaser follows and
  the coin is collected.
- **3D:** a landform card shapes a terrain, *Use in game* makes its 3D Room, *Generate › Rock* and
  *Use in game* make a Model Object, the float recipe gives it behaviour, the Room editor applies
  the Golden hour scene preset and places the rock, and the saved Room is played: the sky is the
  preset's and the rock bobs.

Both are part of the full regression run.

The beginner guide is held to the product: `Clear.Guide.GettingStartedNamesRealRecipesAndSteps`
fails if the guide names a recipe the Object editor does not offer (or omits one), or if its first
game stops being the four steps Home shows, and `Shell.Guide.ViewerShowsTheGuideWithoutMarkdownMarks`
checks what the in-app viewer displays. `Shell.PaletteMenusAndBuildDiagnosticsAreDiscoverable` pins
the Help menu entry and F1.

## Captions

Section headings, field labels and panel titles are sentence case everywhere. Factories apply
`UiTokens.DisplayHeading` (`EditorChrome`, `CollapsibleSection`, `ImageEditorChrome.MakeSectionTitle`,
the Inspector's section headings, the rig and animation studios), so a caption written in capitals
by older code still displays correctly. Three kinds of capitals remain on purpose: the small
eyebrow above a page title (*CURRENT PROJECT*, *NEW PROJECT*, *PROJECT TEMPLATES*, *PREFERENCES*),
small badges (*2D GAME*, and *PGSL GAME CODE* / *ENGINE API* in the command reference), and the
action palette's category rows
and inspector group keys that suites look up by name.

## Known limits

- Trees other than Assets (Room hierarchy, action palette) keep the standard Windows scroll bar:
  the dark Explorer theme also repaints a tree's selected row, which only the Assets tree's own
  row drawing is written to cover.
- The guide viewer renders headings, paragraphs, lists and inline bold, italic and code. Tables,
  links and images in a guide would be shown as plain text.

## Review method

Every surface is captured through the judge-capture mode
(`--judge-captures <dir> --variant normal|narrow|scale150|scale200`) and inspected. The first pass
found the workflow bar docked above the command bar in Studio (fixed, with a regression check), an
ampersand drawn as a mnemonic underscore, two bars starting on the wrong step, unthemed scroll bars
and headings that ignored the interface text size.
