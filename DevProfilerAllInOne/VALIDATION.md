# Validation

Revision: 1.1.1

Completed in the packaging environment:

- Project, solution, XAML, and manifest XML parsing
- Python helper and sample syntax compilation
- C# lexical delimiter validation across all source files
- PowerShell bootstrap and sample structural delimiter validation
- XAML event-handler reference validation
- Embedded PowerShell bootstrap resource validation
- Verification that PowerShell uses `-STA` and no longer uses `-NonInteractive`
- Verification that the PowerShell launch gate is configured by the adapter, consumed by the bootstrap, and signalled by the coordinator
- Verification that Job Object active-process and process-ID queries are present
- Verification that profiling waits for the owned process tree after the root PowerShell process exits
- Verification that Stop terminates the owned Job Object even when only child GUI processes remain
- Verification that Job Object PIDs are merged into live process-tree metrics after root-process exit
- PowerShell WinForms/STA validation target added at `SampleTargets/PowerShellGuiSample/main.ps1`
- ZIP integrity and SHA-256 generation

## Environment limitation

The packaging environment is Linux and does not contain the .NET SDK, PowerShell, or the Windows Desktop targeting pack. The WPF application and WinForms validation target could not be executed here. Run `build-release.bat` on Windows with the .NET 10 SDK, then profile `SampleTargets\PowerShellGuiSample\main.ps1` to perform the final Windows GUI runtime validation.
