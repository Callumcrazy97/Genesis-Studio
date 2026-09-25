# Quick Build and Full Build

Effective 8 September 2026. These profiles build **Genesis Studio and its matching Windows x64
Player**. They are separate from exporting a user's game. Run commands from the repository root.

| Contract | Quick (default) | Full |
|---|---|---|
| Command | `Build.bat` or `Build.bat --quick` | `Build.bat --full` |
| Configuration | Release; `--debug` available | Release only |
| Compilation | Incremental publish, warnings as errors | Explicit restore and clean, warnings as errors |
| Product | Self-contained Studio and `Player/GenesisEngine.exe` | Same |
| Package validation | Required files, DXC, VC++ runtime, matching shared DLL hashes, retired dependency rejection | Same |
| Regression | Skipped unless requested | Complete headless regression |
| Renderer tests | Skipped unless requested | DX11, DX12, Vulkan, OpenGL, Software, each explicitly requested |
| Startup | Staged Studio and bundled shader compiler smoke | Same |
| Promotion | Only after requested checks pass | Only after all checks pass |

No GPU test is silently counted as passed through fallback. A requested renderer failure fails the
build. Software smoke coverage does not imply hardware-feature parity. `BuildSummary.json` separates
requested checks, executed results and skipped renderer coverage.

## Additional commands

```bat
Build.bat --check
Build.bat --test Room
Build.bat --test 2d-pipeline
Build.bat --test model-system
Build.bat --backend vulkan
Build.bat --full-tests
Build.bat --full-smoke
Build.bat --full --installer
Build.bat --quick --run
Build.bat --help
```

`--check` adds the 13-workflow gate and DX11/DX12 smokes to Quick. `--full-tests` adds the entire
regression suite; `--full-smoke` adds all five renderer smokes; `--quick-smoke` adds DX11/DX12.
`--skip-tests` is a legacy Quick alias and still runs package/startup checks. Full rejects Quick,
skip-tests, Debug and focused-test combinations. The renderer selector accepts only `dx11`,
`dx12`, `vulkan`, `opengl`, `software`; removed aliases produce an actionable error.

## Output and recovery

Each run owns `.build/staging/<run>/`, `.build/tests/<run>/` and `TestResults/Builds/<run>/`.
Studio and Player are published into fresh staging even during Quick; this prevents deleted
backend DLLs from surviving in a previously published folder. Tests use the staged Player.

The driver audits the package and writes a SHA-256 `PackageManifest.json`. The manifest covers
the package payload before the manifest and final BuildSummary are written. It then moves the
existing `Genesis Application/` to `.build/previous/<run>/`, promotes staging and restores the
previous directory if that final move fails. These moves are restricted to the workspace.

A running Studio/Player under the published directory prevents promotion and leaves the validated
staging folder available. The build never force-stops those user processes. The isolated smoke
process has a 60-second timeout and may be stopped by its own PID if it hangs.

If a command fails, inspect the failed stage and log path in BuildSummary. Fix the cause and rerun.
Staging and previous packages are retained for diagnosis/rollback; they currently require manual
cleanup and can consume substantial disk space. Builds should be run one at a time in this workspace.

## Installer and scope

`--installer` compiles `Dist/GenesisStudio-Setup.exe` from the validated staging directory before
product promotion. Inno Setup and the existing redistribution scripts remain prerequisites.
Installer execution, locked-output and cancellation drills remain additional release checks.

The shell's game Export command is still a stub. This build driver does not claim to implement
game packaging or to add non-Windows Runner targets.
