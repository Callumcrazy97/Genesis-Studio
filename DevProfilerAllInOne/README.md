# DevProfiler All In One

A Windows WPF profiler for testing Python, compiled .NET 10, and PowerShell targets from the same interface.

## Included capture modes

### Python

- Launches a selected `.py` entry point with a selected Python interpreter.
- Automatically detects `.venv`, `venv`, or `env` when the runtime field is blank.
- Uses exact `cProfile` function statistics for the root Python process.
- Injects `sitecustomize.py` through `PYTHONPATH` so normal Python child interpreters inherit profiling.
- Captures live stdout/stderr.
- Streams launch, stdout, stderr and process-exit diagnostics immediately into the **Raw Output (live)** tab and writes the same data to `target-output.log` in the session folder.
- Captures Pygame FPS through `pygame.display.flip()` and `pygame.display.update()` hooks.
- Merges results across captured Python processes while retaining per-process rows.

### .NET 10

- Launches a compiled `.exe` directly, or a `.dll` through a selected `dotnet.exe` runtime.
- Uses EventPipe and the .NET sample profiler for sampled managed call stacks.
- Captures runtime event counts for GC, exceptions, contention, loader, threading, and JIT activity.
- Captures live stdout/stderr.
- Process-tree CPU, RAM, thread, handle, and I/O monitoring includes child processes.


### PowerShell

- Launches a selected `.ps1` script with PowerShell 7 or Windows PowerShell in GUI-compatible STA mode.
- Automatically prefers `pwsh.exe` and falls back to `powershell.exe` when the runtime field is blank.
- Captures exact total script wall-clock time.
- Captures Output, Information, Verbose, Debug, Warning, and Error streams into structured diagnostic events.
- Retains source file, line, column, and fully-qualified error ID when supplied by PowerShell.
- Captures live stdout/stderr and shared process-tree CPU, RAM, thread, handle, and I/O metrics.
- Keeps profiling active when the PowerShell host exits but a launched WinForms/WPF/GUI child process is still running.
- Preserves script arguments through a JSON/Base64 transport rather than evaluating a generated command string.

## Shared interface

- Manual language selection.
- Browse for the target, runtime, and working directory.
- Optional raw command-line arguments.
- Windows Job Object ownership so Stop terminates the complete launched process tree.
- Target CPU rather than whole-computer CPU.
- Target private memory and working set rather than whole-computer RAM.
- Merged function table and per-process tables.
- Live CPU, private-memory, working-set, I/O, and FPS graphs.
- Warning analysis.
- Event log and raw output.
- Markdown report export.
- Fully templated dark WPF interface with readable dropdowns, tabs, grids, scrollbars, selection states, and raw output.

## Build requirements

- Windows 10 or Windows 11, x64.
- .NET 10 SDK with the Windows Desktop workload/targeting pack.
- Internet access on the first build so NuGet can restore the diagnostics packages.

## Build a self-contained release

Run:

```bat
build-release.bat
```

The published application will be placed in:

```text
artifacts\win-x64\DevProfiler.exe
```

The build script also creates:

```text
artifacts\DevProfiler-win-x64.zip
```

To run directly from source:

```bat
run-source.bat
```

## Testing the Python game

1. Select **Python**.
2. Select the game's one `.py` entry point.
3. Leave **Runtime** blank to detect a local virtual environment, or browse to its `python.exe`.
4. Confirm the working directory is the game folder.
5. Add any game arguments.
6. Select **Run**.
7. Close the game normally when practical. A normal shutdown gives Python enough time to write its exact `cProfile` results.

## Testing the .NET 10 game

1. Select **.NET 10**.
2. Select the compiled game `.exe`.
3. Leave **Runtime** blank for an `.exe`.
4. Confirm the working directory is the folder containing the game and its dependencies.
5. Select **Run**.
6. Exercise representative gameplay before closing the game.


## Testing a PowerShell script

1. Select **PowerShell**.
2. Select the `.ps1` target.
3. Leave **Runtime** blank to prefer PowerShell 7 and fall back to Windows PowerShell, or browse to a specific `pwsh.exe`/`powershell.exe`.
4. Confirm the working directory.
5. Enter arguments such as `-Iterations 10 -ShowWarning`.
6. Select **Run**.
7. GUI scripts run in STA mode; close the final GUI window to complete the session.
8. Review the exact script duration, structured stream events, process metrics, raw output, and warnings.

## Important limitations in this first release

- Python FPS is currently captured for Pygame presentation calls. Other Python graphics frameworks still receive CPU/RAM/I/O profiling but may show no FPS.
- .NET method timings are sampled estimates, not exact invocation counts. The interface labels these rows as `Sampled`.
- EventPipe method capture currently attaches to the root .NET process. Generic process-tree metrics still include child processes.
- A forcibly killed Python application may not execute its `atexit` profiler flush. Stop first requests a normal window close, then terminates the process tree if the game does not exit.
- .NET FPS requires a future PresentMon/ETW capture adapter or a small cooperative telemetry client. Process and runtime profiling works without changing the game.
- Native frames and unresolved runtime frames may appear without source filenames.
- Console targets run without a redundant visible terminal. If they fail before opening a game window, inspect **Raw Output (live)** for the exception and exit code; the persistent `target-output.log` is in the displayed session folder.
- For a single-file Silk.NET target whose custom loader cannot see its bundled `glfw3.dll`, DevProfiler can reuse the target project's own unpacked Release companion through the child-only `PATH`. The game files are not copied or modified.
- PowerShell capture currently reports exact whole-script duration and structured streams. Per-function PowerShell timing would require AST instrumentation or a dedicated PowerShell profiling engine and is not claimed in this release.

## Project layout

```text
DevProfilerAllInOne/
├── DevProfiler/                  WPF application
│   ├── Adapters/                 Python, .NET, and PowerShell capture adapters
│   ├── Controls/                 Lightweight live graph control
│   ├── Models/                   Normalised profile/session models
│   ├── Resources/                Embedded Python injection helpers
│   └── Services/                 Process tree, Job Object, analysis, export
├── SampleTargets/
│   ├── PythonSample/             Small profiler test target
│   ├── DotNetSample/             Small .NET 10 profiler test target
│   ├── PowerShellSample/          Small PowerShell profiler test target
│   └── PowerShellGuiSample/       WinForms/STA and child-lifetime validation target
├── build-release.bat
├── build-release.ps1
└── run-source.bat
```

## Session data

Raw session data is retained under:

```text
%LOCALAPPDATA%\DevProfiler\Sessions\<timestamp>
```

That folder can contain Python profile JSON, pstats text, FPS streams, .NET EventPipe traces, PowerShell structured event JSONL/summary JSON, and adapter diagnostics.
