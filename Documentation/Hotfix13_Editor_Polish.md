# Hotfix 13 — Editor polish, resource discovery and build identity

**Baseline:** apply over the complete Hotfix 12 source tree. Keep the existing order: Hotfix 10 → Hotfix 11 → Hotfix 12 → Hotfix 13. Earlier prerequisites of that tree remain required. This is not a full Studio distribution and not an alternate game-template branch.

**Delivery status:** the bounded implementations below are complete in source and accompanied by executable regression cases. Native .NET compilation, WinForms interaction, actual shortcuts, image decoding, publish smoke and performance acceptance have not been executed in the delivery environment. Source/structure and archive checks are separate evidence, not native tests.

## 1. Background image picker and shared resource pickers

The exception reported by the user came from `AssetPickerModal.BuildResultsPanel`: it selected `View.Tile` and then enabled `VirtualMode`. H13 uses `View.LargeIcon` for Grid and `View.Details` for List. Neither initial construction nor the Grid button sets Tile view. Virtualization remains enabled rather than loading every result into an ordinary ListView.

This is the shared typed picker, so the correction applies to all resource fields that call `AssetPickerService`, not only Room backgrounds. No per-template exception or guessed background filename is introduced. Current references are selected by their public names, and accepting a resource returns its canonical name, for example **Morning Sky**. Directory and source-format data remain internal bookkeeping; displayed reference labels, tooltips and returned values are names.

Filtering first invalidates native selected indices and sets the virtual list size to zero before replacing the backing result set. Selection callbacks are suppressed during that transition. A valid old selection is restored by name; a new nonempty search selects its first matching result. An empty result set disables Select/Favourite. Clicking empty space cannot accept an unrelated old row.

### Bounded, asynchronous previews

The modal no longer decodes the first 300 thumbnails synchronously. Its virtual item callback queues visible resources. One thumbnail worker decodes one item at a time; requests that have scrolled off screen are discarded before decode. The pending queue is capped at 256 and the per-dialog thumbnail cache at 512, with least-recently-used slots. There is no first-300-resource thumbnail eligibility limit.

A separate worker handles the selected large preview. Rapid selection replaces the pending request instead of creating an unbounded task for every keypress. Request generations prevent an old preview from replacing the current one. Controls and ImageList handles are touched only from the UI continuation; bitmap decode runs on a background worker. Closing cancels queued work and disposes late results rather than waiting for it on the UI thread.

These are two bounded workers per open picker, not the full roadmap Task Centre or Derived Data Cache. An already-running GDI decode is allowed to finish and its obsolete output is discarded; it is not forcibly aborted. First-time tag metadata reads and the initial project-index validation still use their existing paths. Do not claim that all picker I/O or all project import work is asynchronous.

Missing, damaged or undecodable media produces a fallback preview, not a failed picker. Background-panel previews also detach their bitmap from the source stream before closing it and release replaced/disposed previews.

## 2. Room hierarchy: disclosure, selection and refresh

The Room hierarchy retains TreeNode instances and native handles for unchanged entries. It reconciles actual child lists only when membership, parenting or ordering changes. Position/property updates and visibility/lock label updates do not clear the tree and recreate it. Root/layer/instance identities, expanded branches, selected item and top visible row are retained where they still exist.

Selection synchronization now updates the existing selected node rather than calling the full hierarchy population path. Structural refreshes remain necessary when content changes; this patch does not skip new/deleted/reparented items to appear fast.

The owner-drawn triangle, eye and lock controls are hit-tested before native TreeView selection processing. A **single click on the disclosure triangle** expands/collapses its branch without selecting it, rebuilding the hierarchy or replacing the Inspector target. The second click of a double-click sequence is consumed for the same adornment, so native double-click handling cannot reverse the first toggle. Ordinary row clicks continue through selection rules; right-click menus continue through applicability checks.

Search expands matching paths temporarily and restores the user's stored disclosure state when the query is cleared. Excluded rows can be recreated when they re-enter the filter, but unfiltered stable branches are not rebuilt on every click.

**H10 context boundaries remain:** Objects edits belong to Objects and its selected object layer; tiles belong to the selected Tilesets layer; backgrounds belong to the selected Backgrounds slot. The all-instance navigation view can disclose the tree without silently authorizing another resource type. Visibility/locking of organizational room layers remains a layer-level navigation action. Native tree selection, label editing and drag initiation cannot bypass the active resource context.

`HierarchyRefreshCount` and `HierarchyStructureUpdateCount` expose diagnostic counters used by regression cases. No native millisecond, FPS or DPI acceptance result is claimed here.

## 3. Reserved command-palette shortcut

**Ctrl+Shift+P** is reserved for the current project's command palette and is intercepted by a project-scoped Windows message filter before a child/native editor can consume the chord. The filter accepts only this chord, leaves all other editor shortcuts on their existing local-first route, and unregisters when its shell is disposed.

It rejects unrelated project windows and modal dialogs. It covers the shell, docked/floating content and registered separate Image Editor/Model Composer windows through the shell's existing ownership checks. Holding the chord does not queue repeated dialogs. The first press captures the original editing control and queues opening on the shell, rather than entering a modal message loop from inside the message filter.

Palette commands still act on that original context and recheck it before executing. Cancel does not apply an edit. A closed context or a newly active modal dialog cancels the queued opening.

The same palette is visible through **View → Command Palette**, **Help → Command Palette**, **Help → Keyboard Shortcuts**, and the **Commands…** toolbar button. The separate help entry is now clearly labelled **PGSL Command Reference**. That reference window documents scripting APIs; it is not the command palette.

This correction is a bounded refinement of ST-CMD-02/03. It does not implement universal shortcut rebinding or migrate every specialist modelling/painting operation into the command catalog. There are 36 shell command identities after adding Copy Build Information.

## 4. Visible, assembly-owned build identity

Studio now displays a source revision and exact Build.bat run identity, for example:

```text
Genesis Studio — H13 — Build 20260923-210000-a1b2c3d4
```

The example run identifier is illustrative, not a claim about the user's build. The actual identifier is the staged build's existing timestamp/GUID run ID, embedded in the Studio assembly alongside H13 and the build configuration. The public product AssemblyVersion is not repurposed as an invented semantic release number.

Identity appears on the splash, Project Hub title and bottom card, workspace title, status badge tooltip, About dialog and startup log. **Help → Copy Build Information** copies the loaded assembly path, process path, runtime/configuration, module ID and expected build-report location. It reads the loaded assembly, not a neighbouring JSON/version file or file modification time.

`BuildTools/Build.ps1` passes that run ID through publishing and native test-harness compilation. The existing startup smoke now requires the exact revision/run identity in the Studio log in addition to its existing shader/package evidence. `BuildSummary.json` records the source revision as well. Thus a stale executable cannot pass that smoke merely by sitting next to a newer report.

A direct IDE or `dotnet` build that does not supply the run property is explicitly labelled **local**. Its diagnostics still expose the real module ID and assembly location; it does not pretend to have a Build.bat timestamp. The standalone Engine distribution and the in-game HUD are not altered by this patch.

The user's screenshots have fewer menu items than the H12 source defines, but the screenshots alone do not establish which executable was launched. H13 adds observable evidence instead of assuming a missed patch or blaming a workspace layout.

## 5. Additional completed roadmap slices

### Persistent favourites and recent resources in the shared picker

Select a resource and use **☆ Favourite** or **Ctrl+D**. The **Favourites** scope lists bookmarks of the requested resource type. **Recent Assets** records successfully accepted resource selections, not every row merely highlighted while searching. Recent history is bounded to 24 entries; favourites to 512. Ctrl+F focuses search.

Preferences are project-local, persisted outside authored Assets under `.genesis/User`. Stable resource GUIDs already present in private metadata are carried through the catalog into picker entries. A logical rename or move therefore does not lose a GUID-backed bookmark. Descriptor-less resources without metadata use a name fallback; there is no invented filename-based public reference.

Opening preferences does not create a file. Writes re-read the last saved state under a project-file gate, merge the requested change, write a unique pending file and atomically replace the preference file. Two pickers cannot blindly overwrite one another's saved choices. Corrupt, oversized or unsupported future-version preferences are not silently replaced. A save failure leaves the previously committed in-memory state and exposes a preferences-unavailable explanation while leaving the resource picker usable.

This completes **picker** favourites/recent persistence and GUID identity, a bounded AS-BROWSE-01/AS-DB-01 slice. It does not add a new global Asset Browser favourites panel, a new dependency database, cloud preference sync or the entire resource-browser roadmap.

### Undoable background authoring

The Backgrounds panel now records image assignment, colour, visibility, layout, scroll speeds, depth and source-mode changes in the existing room undo journal. Picking an image in an empty slot creates and assigns that slot as **one** undoable operation. Undo removes that new node; Redo restores it. Existing background settings are restored without overwriting unrelated transform data or other nodes.

Repeatedly choosing the same image does not add a no-op undo entry. Numeric edits remain individually undoable; their existing controls are retained. Image picking preselects the current name and checks the tab/slot/lock context again after the modal dialog closes. A new slot cannot fall back into a locked layer when all layers are locked. Save/reopen uses the existing room format and name references.

This is the completed Backgrounds-panel slice of ST-UNDO-01 and ST-ROOM-01, not a claim that every destructive operation in all editors is now on a unified transaction system.

## 6. Install and acceptance

Close Studio and its Player. Apply this changed-files-only patch over H12 and run:

```powershell
.\Build.bat
```

Run the published Studio from that successful build. Confirm the splash/title/startup log show **H13** and the same run ID printed by Build.bat. Old shortcuts can be diagnosed using the loaded executable path in Copy Build Information. No reset of the user's workspace or saved projects is required for this patch.

The focused native suite is:

```powershell
.\Build.bat --test studio-polish
```

It contains **40 new cases: 14 core preference cases plus 26 native editor/shell cases**. The full harness also includes the suite. Core-only validation compiles the exact production command/save/preference implementations without a Windows GUI:

```powershell
dotnet run --project Tests\Genesis.Foundation.Checks --configuration Release
```

That portable runner now has **38 cases: 24 existing H12 cases plus 14 H13 cases**. Neither runner was executed in the delivery environment. The native suite includes picker construction/view switching/filtering/selection/close, GUID preferences, native tree-message disclosure, stable handles, context restrictions, background undo/redo/save, reserved-key filtering/actual palette opening/modal isolation and assembly/menu identity.

Human acceptance remains required: assign/replace an image from a background; toggle grid/list, search to zero results and return; favourite and reopen; single-click hierarchy triangles and rows; scroll/edit without losing state; undo/redo background values; invoke the palette from Room numeric fields, text, floating and separate editors; inspect build identity at normal and high DPI. Native timings, complete multi-monitor/DPI coverage, actual keyboard delivery and large-project responsiveness are **pending evidence**, not inferred from source tests.

The reported `Information` messages about Save availability, already-saved documents, successful room saves and live reload are not the picker exceptions. The two `Error Unhandled UI exception` entries are the actual failure path corrected here. H13 retains meaningful logging rather than suppressing exceptions globally.

## 7. Scope exclusions

No game templates, PGSL gameplay, imagery, sounds, existing project assets or gameplay systems are changed. The shared resource catalog only exposes the already-authored private GUID alongside each existing public name; names, formats and runtime resource lookup rules are not migrated. No new game resource type, global fog, renderer overhaul, world-partition stub, AI placeholder or unfinished editor is added. The complete master roadmap remains in `Documentation/README.md`, with this delivery's statuses above the unchanged H12/history text.

## 8. Delivery source audit

105 source/structure assertions passed, including all 21 changed/new C# files' lexical delimiters and all 18 project XML parses. This validates source shape and explicit wiring, not C# type checking, .NET execution or live UI behaviour. The 26-file patch has 17 modifications and nine additions. All 1,622 baseline template files are unchanged, and the complete prior master remains byte-identical beneath the new ledger. ZIP integrity, exact changed-file membership, entry hashes and overlay reconstruction are separately verified before delivery.
