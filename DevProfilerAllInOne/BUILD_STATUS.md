# Build and validation status

## Completed in this environment

- Source updated to version 1.1.1.
- PowerShell GUI launch changed to STA and interactive-capable mode.
- Root/child process lifetime handling corrected through Windows Job Object accounting.
- Job Object process IDs are retained in metrics after the PowerShell root exits.
- Stop handles child-only GUI sessions.
- PowerShell launch race protected by a startup gate.
- WinForms GUI validation sample added.
- XML, Python syntax, PowerShell structure, C# lexical structure, required-fix assertions, ZIP integrity, and checksums validated.

## Environment limitation

This Linux packaging environment does not contain the .NET SDK, PowerShell, or Windows Desktop runtime. A Windows WPF compile and GUI execution test could not be performed here. Use `build-release.bat` on Windows with the .NET 10 SDK to restore, compile, publish, and package the self-contained application.
