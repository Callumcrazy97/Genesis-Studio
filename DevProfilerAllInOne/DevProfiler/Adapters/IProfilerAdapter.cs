using System.Diagnostics;
using DevProfiler.Models;

namespace DevProfiler.Adapters;

public interface IProfilerAdapter : IAsyncDisposable
{
    string DisplayName { get; }
    TargetLanguage Language { get; }
    string CaptureDescription { get; }

    Task<LaunchPlan> PrepareAsync(ProfileSession session, CancellationToken cancellationToken);
    Task OnProcessStartedAsync(ProfileSession session, Process process, CancellationToken cancellationToken);
    Task StopAsync(ProfileSession session, CancellationToken cancellationToken);
    Task FinalizeAsync(ProfileSession session, CancellationToken cancellationToken);
    double ReadLiveFps(ProfileSession session);
}
