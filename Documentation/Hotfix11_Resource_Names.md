# Hotfix 11: one global resource-name namespace

Apply over Hotfix 10. This patch changes Studio's authoring identity and the shared Engine/runtime used by Studio and its matching Player. It is not an overlay for a separately laid-out standalone Engine distribution.

## Public contract

A resource is referenced by its name: `Hero`. The namespace is case-insensitively unique across every resource kind. A sprite and an object cannot both be named `Hero`; folder placement does not create another namespace. Spaces and meaningful dots (for example `Hero.Run`) are supported. A resource name cannot be a path, a known resource filename, an operating-system-reserved name, or end with a resource extension.

The UI uses the same name in the resource browser, Finder, pickers, Inspector, editor tabs, Object references, room placement, scripts and generated completion text. Room backgrounds, tilesets, object prefabs, model assets, particle assets, shader previews, texture references, physics assets, paths, terrain references, audio and UI resources resolve through the same cached catalog.

Private storage still has filenames and formats. Texture pixels, sound payloads, object event programs, sidecar data, import files, project manifests and cache files remain implementation details. An OS file picker used to import external artwork still selects an actual source file. No mass renaming of those private files is required to reference `Hero`.

## Authoring and gameplay

PGSL Object events and Script resources use normal string arguments containing the name only:

```pgsl
SpriteSet("Hero");
rig_ready = SpriteRigBind("Hero", "Upper Body");
```

`Upper Body` is a rig name inside the image, not another project resource. The existing deformation/infill implementation is unchanged.

```pgsl
if (ResourceExists("Hero")) {
    resource_kind = ResourceTypeOf("Hero");
}
```

`ResourceName` returns the canonical name. Type filtering validates a globally resolved resource; it never selects a different same-named asset in another type folder.

```pgsl
result = ScriptExecute("Movement Rules", 10, 20);
```

`ScriptExecute` forwards arguments to the same PGSL module execution path used by direct script calls. An identifier-compatible Script name can still be called directly. A renamed direct call is rewritten to a quoted `ScriptExecute` call when its new name contains spaces. Local function declarations and their calls are not renamed as resources.

C# runtime loaders and the existing `SpriteRigRuntime.Bind` API use the same name resolution. A compiled C# Script resource stores its public-name-to-CLR-class binding in its assembly. Renaming the resource does not require renaming its class or keeping its source file in an exported game solely to attach that behavior. A library-only C# resource has no attachable behavior. A behavior resource should define one primary concrete EntityBehavior, or uniquely match its private source-file stem when it contains multiple behavior classes.

## Create, import, copy, move and rename

Create/import/copy choose an unused project-wide name. Rename rejects an existing name even when the other resource is of a different kind. Existing protected-root and per-type folder restrictions remain in place.

F2 changes public identity, not the physical descriptor/sidecar names. Static typed references in descriptors, room data, PGSL/Object events, normal C# string bindings and Notes resource links/code examples are updated in one prepared transaction. GUIDs are preserved. Clone/copy creates fresh identity and rebinds copied internal links to the copied resources; links to assets outside the copied group continue to use the originals. Moving a renamed resource preserves its logical name.

Save open work before renaming. Studio blocks a project-wide reference rename while any docked document, Image Editor window or Model Composer window has unsaved changes. Clean affected documents refresh from disk through the existing asset monitor.

Name controls in the shared Inspector are not an alternate way to mutate identity independently of the resource service. Use the resource browser's Rename action.

## Existing projects

The next project open performs a reference/identity migration. New template instances are normalized immediately after their original template files are created. Source template files in the Studio repository are not edited by this patch.

Many older projects legally had same-stem assets of different kinds. Migration makes those names unique deterministically rather than choosing the first matching file at runtime. Objects keep their common gameplay name; an image conflicting with an object receives `Sprite` at the end. Other kinds use their type and a numeric suffix when needed. For example an object `Coin` and image `Coin` become `Coin` and `CoinSprite`. Typed source references are updated to the intended resource. Ambiguous references within the same type stop migration rather than guess.

The migration version is persisted in the project manifest. A migrated project without further name conflicts is not migrated again on every open.

Changes are prepared and parsed before any project file is replaced. Each migration or reference rename records its backup here:

```text
.genesis/Backups/ResourceNames/<timestamp-id>/
```

The backup has a report and an index mapping original project-relative paths to short, hashed backup files. This avoids duplicating long asset directory trees under the backup directory. Entries also identify newly created metadata that did not exist beforehand. The transaction rolls back writes if a write fails; the backup remains available. To restore an earlier version, close Studio/Player, restore files using that index, and remove only files marked as newly created by that operation. Restore the migration backup before reverting the runtime to a version that cannot resolve the new identities.

Original image/audio payload bytes and source template files are unchanged. Metadata, resource-reference text and project settings in opened/created projects may change as part of the migration. No project recreation is necessary.

## Boundaries and validation

Legacy storage references are accepted at loading/normalization boundaries to keep older projects readable. Newly emitted authoring references use names. Public lookups never silently select one of several same-named resources.

Computed references, reflection strings, interpolated/raw C# strings and external code are not arbitrarily rewritten. Change computed resource references to name-valued expressions rather than constructing filenames. Dialogue and comments are not treated as resource bindings. Raw payload sources remain private file references. This is resource-aware static rewriting, not a general-purpose semantic refactoring compiler for every possible C# program.

Native checks are registered under:

```powershell
.\Build.bat --test resource-names
```

The suite covers global identity, uniqueness, type safety, cache invalidation, pickers, create/copy/move/rename, migration backups/idempotency, static references, private event exclusion, room/sprite/rig resolution, named C# behavior exports, and PGSL module calls. The environment used to prepare this patch has no .NET SDK or Windows runtime: native compilation and this suite have not been executed here.

An independent portable data audit checked both supplied showcase resource trees, including duplicate-name planning and resolvable static descriptor links. Lexical C# checks, project XML parsing, baseline template byte comparisons and exact changed-files ZIP checks were also performed. Those are not C# compilation or an interactive editor/GPU pass.
