# DevProfiler 1.0.1 build correction

This revision addresses the Windows build failure reported on 29 July 2026.

## Corrections

- Added explicit `System.IO` imports for all files using `Path`, `File`, `Directory`, streams, and file-system exceptions.
- Added a project-wide `GlobalUsings.cs` fallback for `System.IO`.
- Removed the unsupported attempt to call `TraceEvent.CallStack()` directly on a live `EventPipeEventSource`.
- The .NET adapter now records a `.nettrace` file, converts it to ETLX with `TraceLog.CreateFromEventPipeDataFile`, then resolves sampled managed call stacks from `TraceLog`.
- Ensured EventPipe is stopped cleanly before ETLX conversion and does not attempt to reattach after a normal stream shutdown.

Run `build-release.bat` again from this corrected package.
