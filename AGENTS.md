# Repository Guidelines

## Project Structure & Module Organization

Genesis Studio is a C#/.NET 10 Windows x64 game-development application using WinForms and DockPanelSuite.

- `Source/Genesis.Application.Core/`: projects, resources, validation, and diagnostics; starter assets live in `Projects/Templates/Assets/`.
- `Source/Genesis.Application.Studio/`: application shell, windows, and UI assets.
- `Source/Genesis.Application.Editors.Image/` and `Source/Genesis.Application.Editors.Suite/`: image tools and other editors, including shared viewport controls.
- `Source/Runtime/` and `Source/Genesis.Player/`: integrated Ember engine and standalone Game Runner.
- `Tests/Genesis.Application.Headless/`: regression suites, UI assertions, and capture helpers.
- `Themes/`, `Documentation/`, `BuildTools/`, and `Installer/`: themes, design/status documentation, build automation, and installer scripts.

Treat `Genesis Application/`, `Dist/`, `.build/`, and `TestResults/` as generated output. `Ignore/` contains legacy/reference material, not the active implementation.

## Build, Test, and Development Commands

Run PowerShell commands from the repository root:

```powershell
.\DeveloperRequirementsInstaller.ps1 -CheckOnly
.\Build.bat --quick
.\Build.bat --quick --run
.\Build.bat --test Image
.\Build.bat --test image-features
.\Build.bat --full
```

These audit prerequisites, publish incrementally, build and launch Studio, run the image workflow, run detailed image checks, and run complete regression plus all five renderer smokes, respectively. Quick Build includes package/startup checks but skips regression unless requested. Builds treat warnings as errors. Run one build at a time; close published Studio/Player instances before package promotion. Consult `Documentation/BuildProfiles.md` for additional switches and recovery.

## Coding Style & Naming Conventions

Follow adjacent C# code: four-space indentation, file-scoped namespaces, PascalCase types/methods/properties, camelCase locals/parameters, and `_camelCase` private fields. Preserve nullable annotations. Keep partial editor files organized by feature, such as `ImageEditorControl.FrameTargets.cs`. No active repository-wide formatter configuration was found; avoid unrelated formatting changes.

## Testing Guidelines

Use the custom `HeadlessHarness.RunCase` and assertion helpers; this is an executable harness, not an xUnit/NUnit suite. Add behavior-focused regressions to `Suites/*Suite.cs`, using descriptive dotted case names such as `Editor.Image.LayerFrames.OnionCheckboxAndOpacityRefreshPixels`. Verify persistence changes through save/reopen and UI changes with inspected captures. Run focused checks during development and Full Build before broad editor/runtime handoffs. Review `TestResults/Builds/<run>/BuildSummary.json`; report skipped coverage explicitly.

## Commit & Pull Request Guidelines

This checkout has no Git metadata, so historical commit conventions cannot be verified. Use concise imperative subjects, for example `Fix image timeline playback`. Keep changes focused. PR descriptions should explain behavior, reference relevant issues, record validation commands/results, and include before/after captures for visual changes. Update applicable documentation when workflows change.
