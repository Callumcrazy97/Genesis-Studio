# H20 — Resource library tags and shared search

## Purpose and scope

Library tags classify resources for authoring and discovery. They are not Image Editor animation tags, Object gameplay tags, material parameters, or resource names. Assigning `forest` and `night sky` to **Morning Sky** does not change its name, animation clips, pixel data, Background flag, or runtime behaviour.

H20 is an editor-only overlay for H19. No existing template assets or runtime files are replaced. Existing projects need no migration: resources without library tags remain untagged. Tags live in the resource's existing identity sidecar, alongside its stable GUID, and are shared with the project rather than stored only in one person's favourite preferences. The sidecar is private storage, not a reference to type into PGSL or a picker.

## Author a tagged resource without editing files

1. In Assets, right-click a resource and choose **Edit Library Tags…**. Alternatively, select it, open the command palette, and search **Edit Library Tags**. Folders and protected resource roots are not taggable resources.
2. Enter tags on separate lines or separate them with commas/semicolons. The dialog displays the normalized count and explains invalid input. Existing project tags appear as searchable suggestions on the right; select one and choose **Add selected tag** or double-click it.
3. Choose **Apply tags**. Cancel or Escape discards the draft. Leaving the input blank removes all library tags. Applying an unchanged, case-equivalent tag set does not rewrite metadata or add a history entry.

Each resource supports up to 32 tags, each at most 48 characters. Tags are case-insensitive, Unicode-normalized, trimmed, deduplicated and sorted. Spaces within a tag are supported; repeated spaces normalize to one. Quotes, backslashes, commas, semicolons and control characters are not part of tag names. Commas/semicolons/newlines delimit tags in the input field. The draft is capped at 4,096 input characters. Suggestions display up to 256 matches after filtering, rather than making later alphabetic tags impossible to find.

Only one resource is edited at a time. Bulk-tag editing is not part of H20.

## Search consistently

The Assets name filter, project Finder and shared resource pickers use the same library query parser.

| Query | Meaning |
|---|---|
| `forest` | Find that text in a resource name or library tag. Finder/pickers also inspect the existing folder grouping. |
| `tag:forest` | Require the exact library tag `forest`. |
| `tag:"night sky"` | Require a tag containing spaces. |
| `tag:forest tag:night` | Require both tags. |
| `tag:forest -tag:ui` | Require `forest`, exclude `ui`. |
| `tag:forest Tree` | Require `forest` and find the text `Tree` in the eligible name/tag/folder fields. |
| `-tag:ui` | Show resources that do not have `ui`, including untagged resources. |

Whitespace-separated terms combine with AND; quotes preserve a phrase. Explicit tag predicates are exact and case-insensitive, never fuzzy. Ordinary picker name/folder discovery retains its fuzzy subsequence fallback. The Assets filter and Finder do not add fuzzy name matching.

A query is limited to 512 characters and 32 terms. An incomplete `tag:`, a missing closing quote, or an empty quoted search produces no matches and a visible validation message. It does not silently show every asset or retain an unrelated old picker selection. In the shell Finder, malformed input is handled before dispatching a search, rather than generating an exception in the Console.

With Finder's content search enabled, explicit tag predicates first restrict the candidates. Remaining ordinary text is then used by the existing bounded content scanner when metadata does not match. Tag-only searches do not open source files for content matching. Private storage suffixes do not become public name references.

## Browser tag filters

The Assets toolbar includes **All tags**. Open it to select a tag, choose **Untagged resources**, or remove the tag constraint. The dropdown shows counts from the complete in-memory resource snapshot, not counts restricted to the current filter combination; its first 100 alphabetical tags are listed, and **Search tags…** focuses the query field for any other tag.

Tag filters intersect All/Favourites/Recent, resource type, the browser query and Finder results. A tag never overrides a type restriction. **Reset** clears all of these filters together. Filter changes project the current snapshot without rescanning the filesystem or opening sprite descriptors; existing tree reconciliation continues to preserve compatible node identity and folder state.

On a narrow Assets dock, normal ToolStrip overflow can put later controls under the overflow arrow. The same tag predicates are available in the search field regardless of toolbar width.

## Shared picker integration

Pickers display the selected resource's library tags and a tooltip explaining where to edit them. Result tooltips expose the same classification. The picker no longer opens full image/resource descriptors merely to read their unrelated `tags` fields while matching search terms.

H19 constraints remain mandatory. A Room Background picker still only includes Background-enabled Images; a Tileset picker still requires the appropriate usage. Tag queries, Favourites and Recent operate inside that eligible set. Selecting a result still returns the globally unique public resource name only.

Library tags are edited in the Assets browser, not from within a picker modal. Close and reopen a picker after changing classification elsewhere to get a fresh project snapshot.

## Undo, redo and document safety

With the **Assets browser** focused, **Ctrl+Z** undoes the most recent library-tag edit in that browser session. **Ctrl+Y** or **Ctrl+Shift+Z** redoes it. Context-menu and command-palette actions expose the same operations. Text fields retain native text undo; other editor documents retain their own command routing. An empty browser tag history does not fall through to undo a Room or Image edit in an unrelated tab.

The history holds the last 50 tag operations for the open browser session. It is not persisted across closing the project. The saved tags are persisted. A new non-empty edit after undo replaces the redo branch; an unchanged Apply does not.

History uses the resource's GUID. Renaming or moving the resource within the same project therefore does not invalidate the tag edit. Undo resolves the current location before writing. Deletion, replacement with another GUID, or externally changed tags produces a visible refusal rather than altering another resource. Unrelated metadata fields changed after the tag operation are preserved during undo.

This is a bounded metadata journal, not universal undo for resource deletion, move, rename or every editor operation. Those broader roadmap items remain open.

## Identity, import and persistence

The following paths preserve valid library classification:

- Normal project rename and move keep the original resource GUID and tags.
- Duplicate/copy preserves the tag set while assigning the copy a fresh GUID and unique public name.
- Import of a complete supported resource with a readable identity sidecar copies valid library tags into the new resource's fresh metadata. Importing bare media without such metadata starts untagged.
- Ordinary document saving does not rewrite the classification field; the shared metadata writer preserves unrelated keys.

The tag writer edits only `libraryTags` and the metadata modification timestamp. It preserves GUID, resource name, creation timestamp and unrecognized fields. It captures the full metadata SHA-256 when opening the dialog, rechecks identity and revision before saving, writes/flushed a short-named temporary file in the same folder, rechecks the source, and atomically replaces the sidecar. The temporary file is removed on failure. A cancelled/invalid/stale edit does not intentionally replace the original metadata.

Writes are restricted to existing resources under this project's Assets root and reject reparse-point files/folders. Metadata reads are limited to 256 KiB. Unsupported newer schema versions and invalid tag lists are not overwritten by the tag editor. A malformed optional tag list does not cause ordinary resource enumeration to regenerate an otherwise valid GUID. The read-only browser path shows an empty tag set for invalid/oversized metadata; opening the tag editor surfaces the problem rather than silently repairing it.

This is optimistic stale-edit detection, not a cross-process filesystem lock or multi-file project transaction. Simultaneous editing of the same project by multiple Studio processes is not certified. The general resource service's pre-existing behaviour for completely corrupt/missing identity metadata is not replaced by this tag feature.

Read caching is bounded to 4,096 resource entries and keyed by metadata timestamp/length. Ordinary writes and metadata watcher events invalidate entries. Once full, the cache clears before admitting another resource; this is bounded caching, not a persistent asset database or LRU guarantee. A tool that deliberately changes bytes while preserving metadata timestamps and bypassing file notifications may require refreshing/reopening the project.

## Build and acceptance

Apply H20 directly over H19 and run:

```powershell
.\Build.bat
```

The printed source revision and rebuilt Studio must identify **H20** with the build run ID. Existing projects do not need recreation.

The focused native suite includes 32 core cases and 16 Windows integration cases:

```powershell
.\Build.bat --test resource-tags
```

The framework-independent runner links the actual production core implementations and includes 94 cases across the H12/H13/H17/H20 core suites:

```powershell
dotnet run --project Tests\Genesis.Foundation.Checks --configuration Release
```

Native compilation, those C# test cases, keyboard delivery, Windows atomic-file replacement behaviour, DPI, and measured UI responsiveness were not executed in the delivery environment. They remain acceptance gates, not conclusions from source checks.

### Human acceptance pass

Use a disposable project copy for the first persistence/undo checks. Tag **Morning Sky** with `forest` and `night sky`, and tag a non-background sprite with `forest`. Confirm browser exact-tag and untagged filters, Finder name/tag matching, and the Background picker: the sprite must still be excluded. Apply/cancel invalid drafts, clear tags, undo/redo, then close/reopen Studio and check persistence. Rename and move a tagged resource, duplicate it, and verify the original/copy identities remain independent. Confirm that a tag edit never changes an animation clip, gameplay tag, Background/Tileset flag, source pixels, or a PGSL resource-name reference.

For conflict testing, open the tag dialog, change the metadata externally in a disposable project, and try Apply. The editor should refuse the stale write and retain the external change. Missing/corrupt/future metadata tests are also included in the authored suites; do not test destructive corruption in the only copy of a production project.

## Deliberate limits

H20 completes library classification and its editor discovery round trip. It does not deliver automatic tagging, bulk editing, saved search collections, tag taxonomies, gameplay tag queries, animation-tag renaming, a new resource type, a full asset database/dependency cache, multi-user locking, the Task Centre, crash recovery or universal document undo. No new stub editor or unfinished roadmap feature is included.
