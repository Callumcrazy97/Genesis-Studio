# Genesis Studio

A Windows x64 C#/.NET 10 game-development application with integrated asset editors, a dockable
WinForms workspace and the Ember runtime/standalone Player.

The [master document](Documentation/README.md) contains current completion status, the 2D/3D
programme, editor workflows, application comparisons, build/export commands, source layout and
validation evidence. Historical ledgers there are labelled separately from current acceptance.
See [AGENTS.md](AGENTS.md) for repository contributor instructions.

From the repository root, run `DeveloperRequirementsInstaller.ps1 -CheckOnly` to audit prerequisites
and `Build.bat --quick --run` to build and open Studio. Quick Build alone is not a full regression
or editor acceptance pass.
