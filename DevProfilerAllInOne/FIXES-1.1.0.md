# DevProfiler 1.1.0 — PowerShell Support

## Added

- PowerShell as a third language in the target selector.
- Automatic runtime detection, preferring PowerShell 7 (`pwsh.exe`) and falling back to Windows PowerShell (`powershell.exe`).
- Manual runtime selection for portable or non-standard PowerShell installations.
- `.ps1` target selection.
- Safe argument transport from the UI to the target script using JSON encoded as Base64.
- Exact total script wall-clock timing in the merged and per-process tables.
- Structured capture for Output, Information, Verbose, Debug, Warning, and Error records.
- Error source file, line, column, and fully-qualified error ID capture when PowerShell supplies them.
- PowerShell runtime/version, exit code, success state, and stream counts in raw output and exported reports.
- Included PowerShell sample target.

## Scope

PowerShell 1.1.0 profiles the complete script invocation and its process tree. It does not yet rewrite individual PowerShell functions for per-function timings. Structured stream records and exact script-level duration are labelled accordingly in the UI.
