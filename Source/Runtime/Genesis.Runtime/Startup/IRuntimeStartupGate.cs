using System;

namespace Genesis.Runtime.Startup
{
    /// <summary>Optional out-of-process launcher contract. No editor or GUI types cross this boundary.</summary>
    public interface IRuntimeStartupGate
    {
        bool IsActivated { get; }
        Exception Failure { get; }
        void Report(string phase, string detail, long completed, long total);
        void SignalReady();
        void SignalStarted();
        void SignalFailure(Exception error);
    }
}
