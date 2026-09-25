# Target Launch Probe

This is a Windows integration probe for compiled GUI/game targets. It launches
the target with the same output-redirection and hidden-console settings used by
DevProfiler, then records:

- stdout and stderr;
- process exit and responsiveness;
- visible top-level windows owned by the target;
- full-desktop PNG screenshots at 0.25, 0.5, 0.75 and 1.0 seconds;
- a 3.0-second confirmation screenshot for slower single-file game startup.

Run it with:

```powershell
dotnet run --project Tools\TargetLaunchProbe\TargetLaunchProbe.csproj -- `
  "C:\path\to\target.exe" `
  "C:\path\to\results" `
  "C:\optional\path\containing\native-libraries"
```

If the optional native-library directory is omitted, the probe uses the same
Silk.NET/GLFW companion discovery as DevProfiler.

The probe requests a normal window close after four seconds, then terminates
only the process tree that it launched if necessary.
