# Genesis Studio

A Windows x64 C#/.NET 10 game-development application with integrated asset editors, a dockable
WinForms workspace and the Ember runtime/standalone Player.

The [master document](Documentation/README.md) contains current completion status, the 2D/3D
programme, editor workflows, application comparisons, build/export commands, source layout and
validation evidence. Historical ledgers there are labelled separately from current acceptance.
See [AGENTS.md](AGENTS.md) for repository contributor instructions.

[Large worlds](Documentation/LargeWorlds.md) covers kilometre-scale terrain, scattered forests,
automatic model detail, long views and reversed depth, loading (the model cache, reading ahead,
changing room in the background), sharing a world between players, and what is not done yet.
[Game features](Documentation/GameFeatures.md) covers weather the engine draws, a terrain's own
water colours, what a script may ask of a model (bones, clips on part of the body, tint and
light), animation events, controllers, positioned sound, HUD shapes, particle bursts, save
slots, and room changes that are spread over frames behind a loading screen.

Genesis Studio is free to use under its own [licence](LICENSE.md): the games people make with it
are theirs, and they may give away or sell them with the Genesis Player inside.

What a user receives is described in the [product guide](Documentation/ProductGuide.md), which is
installed as the product's own README beside the other guides; the libraries Genesis is built with
are listed in [ThirdPartyNotices.txt](Documentation/ThirdPartyNotices.txt). The master document's
"Release readiness" section says what has been checked for a release and what still stands in
the way of one.

From the repository root, run `DeveloperRequirementsInstaller.ps1 -CheckOnly` to audit prerequisites
and `Build.bat --quick --run` to build and open Studio. Quick Build alone is not a full regression
or editor acceptance pass.
