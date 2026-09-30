# Studio "Clear" redesign

Goal: every editor should be as simple as 1-2-3 — obvious steps, one starting point you can click,
one primary action — without removing the depth that is already there.

Restore point: tag `backup/pre-shell-redesign-2026-09-30` (and a local zip taken the same evening).

## Principles

1. **One frame for every editor.** Command bar → workflow bar (numbered, clickable steps, one plain
   instruction, *Next ›*) → tools for the current step | view | context panel → status.
2. **Start from something.** Step 1 of each editor is a visual starter gallery; one click applies.
3. **Show the current step.** Advanced controls stay in Options / "More settings". One route per
   function where possible.
4. **Honest UI.** Features that do not work in games are hidden or labelled *Preview only*.
5. **Readable.** Larger secondary text, sentence-case headings (no brackets, ALL CAPS or `-- x --`),
   Fluent icons, rounded pills, an accent primary action and dark title bars.

## Shared components (`Source/Genesis.Application.Core/UI`)

| Component | Purpose |
| --- | --- |
| `UiTokens` | Live palette and fonts shared by Studio, the editor suite and the Image editor. Studio pushes its theme here through `SuiteChromeBridge`. `DisplayHeading` turns ALL-CAPS captions into sentence case and keeps acronyms. |
| `UiGlyphs` | Segoe Fluent Icons / Segoe MDL2 Assets code points, each checked against a rendered sheet. Captions always remain, so an icon is never the only label. |
| `PillToolStripRenderer` | Rounded hover/pressed pills, an accent-tinted checked mode and an accent outline for the primary command (`PrimaryTag`). `GenesisToolStripRenderer` and the editor chrome derive from it. |
| `WorkflowBar` / `WorkflowStep` | The guided-steps bar. Buttons are named `WorkflowStep_<Id>`, Next is `WorkflowNext`. `SetCurrent`, `Activate`, `GoNext`, `SetInstruction` and `RefreshProgress` (check marks from `IsDone`). Narrow bars drop the instruction, then show numbers only. Keyboard and screen-reader accessible. |
| `StarterGallery` / `StarterItem` | Responsive card grid grouped by category. Cards are named `Starter_<Id>`, chosen with one click, Enter or Space, and can carry a badge such as *Recommended* or *Preview only*. |
| `WindowChrome` | Dark Windows title bars (DWM attribute 20, falling back to 19) for `DpiAwareForm` windows and every themed dialog. |

## Status

- **Phase 1 — foundation: in progress.** The components above; editor chrome restyle (headings,
  fonts, pill tool strips, *Use in game* as the primary command); inspector group captions without
  brackets; dark title bars. Compile-verified; the headless regression has not been re-run yet.
- Phase 2 — shell: one-row app bar with central search, Assets dock tidy-up, Home page with a
  "your game in 4 steps" checklist and a create gallery.
- Phase 3 — workflow bars in Terrain, Model, 2D rigging, Shader, Object, Particle, Physics and
  Room (3D), plus the viewers.
- Phase 4 — per-editor workflow features (landform gallery, auto-rig, behaviour recipes, effect
  tiles, scene presets and so on).
- Phase 5 — tests, 100/150/200% captures, Full Build and renderer smokes.
