# H17 — Resource Library and browser responsiveness

**Baseline: H16, after H10–H14 on the original Studio branch.** H16 already includes the H15 shortcut-router correction. Apply H17 directly over the currently working H16 tree; do not substitute the Midnight Restoration branch or reapply earlier game patches.

This is an editor-only delivery. It does not change game templates, runtime behaviour, PGSL semantics, gameplay assets, resource GUID formats, or protected resource-root rules. Public references remain globally unique resource names. It adds no new resource type and does not migrate your projects.

## Install and identify

Close Studio and its Player, extract the changed-files ZIP into the source root containing `Build.bat`, and run:

```powershell
.\Build.bat
```

The build must identify the source as `H17`. The published splash, Hub, title, status and About surfaces retain the existing assembly-embedded identity system and display H17 with that build's actual run ID. A source revision is not a claim that a native test suite has passed.

## Find, bookmark and reopen resources

The Assets dock now has a library row below its existing Finder controls. **All assets** shows the normal protected-root hierarchy. **Favourites** shows bookmarked resources in name order. **Recent** shows recently opened or picker-accepted resources, newest first, regardless of their original folders. The latter two views are flat lists, not a reorganisation of the project.

Select a resource and click **☆**, or right-click it and choose **Add to Favourites**. The star changes to **★**; the menu becomes **Remove from Favourites**. Folders and protected roots cannot be bookmarked. Double-click a resource or press Enter to open it through its normal editor. Highlighting a resource only for inspection does not add a recent entry. Activating an existing editor and successfully opening a separate Image Editor or Model Composer do count as opening.

The same favourites and recent history are shared with the existing asset picker. A favourite added while selecting a room background appears in the Assets dock on reactivation, refocusing the tree, or opening its scope menu. Rename/move operations retain GUID-backed bookmarks. Deleted resources do not appear merely because their old bookmark remains stored. Descriptor-less entries retain the existing name-fallback limitation.

**Ctrl+D remains Duplicate in the Assets browser.** H17 does not appropriate it for favourites; the picker retains its own existing context-specific Ctrl+D behaviour. This avoids turning an established resource-duplication shortcut into an unrelated command.

**All types** filters by resource type without requiring any search text. It intersects with the current library scope, local name filter and Finder results. Switching back to All assets retains those filters deliberately. Use **Reset** to clear the scope, type, local name and shell Finder filters together. The count shows matching resources versus total project resources, excluding organisational folders. Zero means no match, not a loading failure; the count tooltip explains how to recover. ToolStrip overflow retains access to commands in a narrow dock.

The shared command palette also exposes **Toggle Resource Favourite**, **Show All Assets**, **Show Favourite Assets**, **Show Recent Assets**, **Reset Asset Filters**, and **Clear Recent Assets**. View commands reveal and focus the Assets dock. Toggle Favourite requires the original focus to be the browser and the selection still to match; editing a text field cannot silently favourite the resource behind it. Clear Recent removes history only, not files or favourites.

## Refresh and selection behaviour

Ordinary refresh reconciles existing tree nodes by private identity instead of clearing and repopulating the native tree. An unchanged node keeps its native handle. Selection, scroll anchor and user folder expansion are preserved when their resources remain available. Structural changes create, move or remove only the necessary nodes. Filtered views can require different nodes/parents; H17 does not claim their native handles are retained across every possible reparenting.

The normal hierarchy's folder disclosure state is kept separately from temporary filtered expansion. Returning from a filtered view restores that normal state. Explicit operations that select a newly created or moved resource may intentionally reveal it; ordinary refresh does not automatically jump back to the selected row.

An unchanged identity no longer republishes an Inspector selection merely because another tree snapshot was built. Actual resource changes still flow through the existing asset-monitor/live-reload system. Filtering out the selected resource clears both the Inspector's display and its live setters, so a hidden resource cannot be edited by a stale field. Clearing controls disposes a snapshot of them rather than modifying a collection while enumerating it.

Favourite membership and recent rank use derived in-memory indexes, not a scan of every bookmark for each asset. The preference-file format is unchanged.

Local/type/scope filtering projects the current in-memory snapshot; it does not rescan project directories or decode images. Explicit refresh, resource mutations and filesystem notifications still rebuild the ResourceService snapshot. Synchronous mutation notifications are coalesced within the browser's own resource-operation boundary. A later filesystem notification may still cause a separate refresh.

This is not a fully virtual resource tree, incremental disk asset database, or the removal of all O(n) metadata filtering. Those larger roadmap tasks remain open.

## Thumbnail ownership and work limits

Initial construction no longer warms every image and object in the project. The browser shows placeholders immediately and schedules first-frame thumbnails for visible rows as needed. Closed folders do not create preview work. Drawing tree rows only reads the cache; file reads, descriptor resolution and image decoding occur off the UI thread.

Each browser has a single preview worker and no project-sized pending queue. After each completion it checks the current visible rows again. A 60 ms timer detects new viewport demand; it is not a viewport/render FPS limiter. Each scan considers at most 256 rows. The LRU cache holds at most 512 resource results, including missing/corrupt-image results so these do not retry on every paint.

Prepared images transfer to the UI thread only while the browser and source generation are valid. Superseded results are disposed; closing the dock cancels pending scheduling and rejects late UI delivery. A decoder already executing cannot be forcibly interrupted; it finishes and its result is discarded/disposed. No worker waits synchronously on the UI thread. Separate picker jobs retain their existing budgets; this is not a process-wide job scheduler.

The decoder rejects encoded files larger than 32 MiB and sources reporting more than 32 million pixels. Those are defensive input limits, not a guarantee that an operating-system image codec cannot allocate memory while reading its header. Unsupported or unreadable media uses a placeholder. Explicit refresh clears cached negative results after source repair. Cache eviction/invalidation and shutdown dispose owned bitmaps; source streams are closed before caching the scaled result and opened to permit source replacement/deletion.

Image and Object thumbnails use the existing name-resolved first-frame loader, including shared/sibling frame sources. The older associated-pixel resolver is retained as a compatibility fallback. H17 does not generate art or change any runtime atlas policy.

## Tests and acceptance

Run the focused Windows integration target:

```powershell
.\Build.bat --test resource-library
```

`editor-h17` is an equivalent focused target. It registers 42 cases: 24 exact-production core projection/preference cases plus 18 Windows browser/cache/command/Inspector integration cases. Core cases include a 20,000-resource synthetic snapshot, filter intersections, GUID rename/deletion behaviour, corrupted preference protection and source immutability. Windows cases cover retained nodes/disclosure, hidden-selection clearing, successful-open history, protected roots, command focus, image ownership, shared frames, bounded cache and filter/close lifetime.

The framework-independent runner directly compiles production core source and now contains 62 cases (24 foundation + 14 picker/preferences + 24 resource-library):

```powershell
dotnet run --project Tests\Genesis.Foundation.Checks --configuration Release
```

Two older H12 tests now inspect the H16 shortcut-router field instead of referring to the property H16 intentionally removed. No designer-visible router property is reintroduced.

**Execution status:** native compilation, both C# test runners, actual key/mouse delivery, high-DPI rendering, large-project interactive performance and codec/GDI behaviour have not been run in the delivery environment. There is no .NET SDK or Windows runtime available there. Static lexical/XML/source checks and archive hashes do not establish those runtime results.

For human acceptance, open an existing project, favourite a resource through the background-image picker and verify it in Assets; rename that resource and verify the bookmark; open two different resource types and inspect their order in Recent; combine type/name/Finder filters then Reset; expand/collapse folders and refresh without losing the view; hide the selected resource with a type filter and check the Inspector clears; scroll a large folder then close its dock; and run the existing Room/Inspector workflows at your normal DPI. Record measured timings rather than treating these structural improvements as a quantified speed claim.

The master ledger identifies which bounded H17 implementations are complete and which programme gates remain pending.
