# DevProfiler 1.1.2 — Silk.NET Single-File Launch Fix

## Fixed

- Diagnosed a Silk.NET single-file publish that remained alive without ever
  creating a game window.
- DevProfiler now detects `glfw3.dll` in the selected target project's own
  unpacked `bin\Release` output when the DLL is absent beside the selected
  published executable.
- The detected companion directory is prepended only to the launched child
  process's `PATH`; no target files are copied or modified.
- Target stdout, stderr, launch diagnostics and exit codes remain visible in
  the live Raw Output tab and persistent session log.

## Validation

- Added `Tools/TargetLaunchProbe`, a reusable Windows integration probe.
- The probe captures stdout/stderr, process and visible-window state, and
  screenshots at 0.25, 0.5, 0.75, 1.0 and 3.0 seconds.
- Verified the existing Aetherium3D single-file Publish executable:
  no visible window during its first second, a responsive game window at
  three seconds, no stderr, and a clean exit code of `0`.
