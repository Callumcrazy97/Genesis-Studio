using DevProfiler.Models;

namespace DevProfiler.Services;

public static class HealthAnalyzer
{
    public static IReadOnlyList<HealthWarning> Analyze(ProfileSession session)
    {
        var warnings = new List<HealthWarning>();
        List<MetricSample> metrics = session.Metrics.ToList();
        List<FunctionProfile> functions = session.Functions.ToList();

        if (session.Processes.Count == 0)
        {
            warnings.Add(new HealthWarning
            {
                Severity = DiagnosticSeverity.Critical,
                Title = "No processes captured",
                Body = "The target exited before its process tree could be sampled, or it could not be opened. Check the selected runtime, target path and working directory."
            });
        }
        else
        {
            warnings.Add(new HealthWarning
            {
                Severity = DiagnosticSeverity.Success,
                Title = "Process tree captured",
                Body = $"Observed {session.Processes.Select(x => x.ProcessId).Distinct().Count()} process(es) during the session."
            });
        }

        if (functions.Count == 0)
        {
            warnings.Add(new HealthWarning
            {
                Severity = DiagnosticSeverity.Critical,
                Title = "No function profile captured",
                Body = session.Target.Language switch
                {
                    TargetLanguage.Python => "No Python profile JSON was produced. The interpreter may have ignored PYTHONPATH, terminated forcefully, or used a frozen executable instead of the selected .py entry point.",
                    TargetLanguage.DotNet => "EventPipe did not return managed stack samples. Confirm the selected executable is a .NET process and DOTNET_EnableDiagnostics has not been disabled.",
                    TargetLanguage.PowerShell => "The PowerShell wrapper did not produce its script timing row. Confirm the target is a .ps1 script and the selected runtime can execute it.",
                    _ => "No profiler-specific function data was captured."
                }
            });
        }

        if (metrics.Count > 0)
        {
            double peakPrivate = metrics.Max(x => x.PrivateMemoryMb);
            double startPrivate = metrics.First().PrivateMemoryMb;
            double endPrivate = metrics.Last().PrivateMemoryMb;
            double peakCpu = metrics.Max(x => x.CpuPercent);

            if (peakPrivate > 8000)
            {
                warnings.Add(new HealthWarning
                {
                    Severity = DiagnosticSeverity.Critical,
                    Title = "Extreme private memory",
                    Body = $"The target process tree peaked at {peakPrivate:N0} MB of private memory. Check large textures, unbounded caches and retained world/chunk data."
                });
            }
            else if (peakPrivate > 2000)
            {
                warnings.Add(new HealthWarning
                {
                    Severity = DiagnosticSeverity.Warning,
                    Title = "High private memory",
                    Body = $"The target process tree peaked at {peakPrivate:N0} MB of private memory."
                });
            }

            if (metrics.Count >= 20 && endPrivate - startPrivate > 512)
            {
                warnings.Add(new HealthWarning
                {
                    Severity = DiagnosticSeverity.Warning,
                    Title = "Memory growth detected",
                    Body = $"Private memory increased by {endPrivate - startPrivate:N0} MB during the captured run. A longer repeat test can confirm whether this is expected loading or retained memory."
                });
            }

            if (peakCpu > 95)
            {
                warnings.Add(new HealthWarning
                {
                    Severity = DiagnosticSeverity.Info,
                    Title = "High CPU saturation",
                    Body = $"Normalised target CPU peaked at {peakCpu:N1}% across the machine. Review the highest self-time functions and frame spikes."
                });
            }

            List<double> fps = metrics.Where(x => x.Fps > 0).Select(x => x.Fps).ToList();
            if (fps.Count > 0)
            {
                double average = fps.Average();
                double minimum = fps.Min();
                if (minimum < 20)
                {
                    warnings.Add(new HealthWarning
                    {
                        Severity = DiagnosticSeverity.Critical,
                        Title = "Severe FPS drops",
                        Body = $"Captured Pygame FPS fell to {minimum:N0}; average FPS was {average:N0}."
                    });
                }
                else if (average < 45)
                {
                    warnings.Add(new HealthWarning
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Title = "Below 60 FPS",
                        Body = $"Average captured Pygame FPS was {average:N0}."
                    });
                }
                else
                {
                    warnings.Add(new HealthWarning
                    {
                        Severity = DiagnosticSeverity.Success,
                        Title = "FPS stable",
                        Body = $"Average captured Pygame FPS was {average:N0}; minimum was {minimum:N0}."
                    });
                }
            }
        }

        if (session.Target.Language == TargetLanguage.PowerShell)
        {
            int errors = session.Events.Count(x => x.Source.StartsWith("PowerShell", StringComparison.OrdinalIgnoreCase) && x.Category == "ERROR");
            int warningsCount = session.Events.Count(x => x.Source.StartsWith("PowerShell", StringComparison.OrdinalIgnoreCase) && x.Category == "WARNING");
            if (errors > 0)
            {
                warnings.Add(new HealthWarning
                {
                    Severity = DiagnosticSeverity.Warning,
                    Title = "PowerShell errors captured",
                    Body = $"The structured PowerShell streams contained {errors:N0} error record(s). Review the Event Log for source file and line details."
                });
            }
            else if (warningsCount > 0)
            {
                warnings.Add(new HealthWarning
                {
                    Severity = DiagnosticSeverity.Info,
                    Title = "PowerShell warnings captured",
                    Body = $"The structured PowerShell streams contained {warningsCount:N0} warning record(s)."
                });
            }
            else if (functions.Count > 0)
            {
                warnings.Add(new HealthWarning
                {
                    Severity = DiagnosticSeverity.Success,
                    Title = "PowerShell capture completed",
                    Body = "The script duration and structured PowerShell streams were captured without an Error or Warning record."
                });
            }
        }

        if (functions.Count > 0 && session.Target.Language != TargetLanguage.PowerShell)
        {
            double peak = functions.Max(x => x.InclusiveMs);
            List<FunctionProfile> hot = functions
                .Where(x => x.InclusiveMs >= peak * 0.5 && x.Category is not "BLOCKING" and not "IMPORT" and not "STDLIB")
                .Take(6)
                .ToList();

            if (hot.Count > 0)
            {
                warnings.Add(new HealthWarning
                {
                    Severity = DiagnosticSeverity.Warning,
                    Title = "Top cumulative hotspots",
                    Body = string.Join(Environment.NewLine, hot.Select(x => $"{x.Function} — {x.InclusiveMs:N2} ms — {x.CallsDisplay}"))
                });
            }

            List<FunctionProfile> highCalls = functions
                .Where(x => x.Calls > 10000 && x.SelfMs > 5 && x.Category is not "IMPORT" and not "STDLIB")
                .Take(6)
                .ToList();
            if (highCalls.Count > 0)
            {
                warnings.Add(new HealthWarning
                {
                    Severity = DiagnosticSeverity.Info,
                    Title = "High call count",
                    Body = string.Join(Environment.NewLine, highCalls.Select(x => $"{x.Function} — {x.Calls:N0} calls"))
                });
            }
        }

        return warnings
            .OrderBy(x => x.Severity switch
            {
                DiagnosticSeverity.Critical => 0,
                DiagnosticSeverity.Warning => 1,
                DiagnosticSeverity.Info => 2,
                _ => 3
            })
            .ToList();
    }
}
