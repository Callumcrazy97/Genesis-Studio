# Source boundary

Authored application code and the integrated runtime belong here.

- `Genesis.Application.Core` remains UI-independent.
- `Genesis.Application.Studio` is the Windows editor process.
- `Runtime/` is the integrated Ember (Genesis) game runtime source. It is
  consumed by editors and the runtime harness through public contracts; app
  projects reference it via `../Runtime/...` project paths, never the reverse.
- PGSL compiler/VM and command tooling currently live under `Runtime/Genesis.Runtime/Scripting/`
  and shared command contracts under `Runtime/Genesis.Shared/Commands/`; a separate
  `Genesis.Scripting` project is not the current layout. Full AOT tooling remains a roadmap item.
- Editor projects use Core/runtime contracts; shared Image-document tooling for Model painting
  remains a follow-on integration rather than an existing shared editor session.

`Build.bat` publishes to the sibling `Genesis Application/` folder. Never add
published files, caches, test captures, or imported game projects under
`Source/`. The Ember runtime source under `Source/Runtime/` is the one
deliberate exception: it is first-class source, not generated output.
