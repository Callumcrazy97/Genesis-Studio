# DevProfiler 1.1.1 — PowerShell GUI / Child Process Fix

## Fixed

- PowerShell now starts in an STA apartment so WinForms, WPF, COM-backed dialogs, clipboard operations, and other desktop UI code can initialise correctly.
- Removed `-NonInteractive` from the PowerShell host because GUI-capable and prompt-capable scripts must not be forced into non-interactive mode.
- Explicitly launches the target host with a normal visible window (`CreateNoWindow = false`, `WindowStyle = Normal`).
- PowerShell target execution is held behind a short startup gate until DevProfiler has assigned the host to its Job Object, preventing fast child launches from escaping process-tree ownership.
- A profiling session no longer finishes merely because the root `powershell.exe` / `pwsh.exe` process exits. DevProfiler now waits until the complete owned Windows Job Object is empty, preserving and profiling GUI applications launched by the script.
- Stop now terminates the complete owned Job Object even when the PowerShell root has already exited and only child GUI processes remain.
- Stop first requests a normal close from all observed process windows before force-terminating the remaining process tree.
- Job Object assignment failure is now reported accurately rather than silently pretending full child-lifetime ownership succeeded.

## Validation target

`SampleTargets/PowerShellGuiSample/main.ps1` opens a WinForms window and remains profiled until the window is closed.
