using System;

namespace Genesis.Runtime.Diagnostics;

/// <summary>
/// The two kinds of figures the debug screen and its recordings show. Game figures (PGSL objects
/// and events, instances, script errors, frame time and memory) are always available. Engine
/// figures (render passes, draw calls, subsystem times) are for whoever develops the engine: they
/// appear only when Studio's developer setting, or <see cref="EngineEnvironmentVariable"/>, asks.
/// An exported game never shows them.
/// </summary>
public static class DebugCategories
{
    /// <summary>Set to 1 to show and record the Engine category.</summary>
    public const string EngineEnvironmentVariable = "GENESIS_ENGINE_DEBUG";

    /// <summary>
    /// Set to 0 to stop a debug run from recording a profile as soon as it starts. Studio passes
    /// its "Start recording when debugging starts" preference here; absent means record.
    /// </summary>
    public const string RecordOnStartEnvironmentVariable = "GENESIS_DEBUG_RECORD";

    /// <summary>True when this process was asked for the Engine category.</summary>
    public static bool EngineRequested =>
        string.Equals(Environment.GetEnvironmentVariable(EngineEnvironmentVariable)?.Trim(), "1", StringComparison.Ordinal);

    /// <summary>True unless this process was told not to record when debugging starts.</summary>
    public static bool RecordOnStartRequested =>
        !string.Equals(Environment.GetEnvironmentVariable(RecordOnStartEnvironmentVariable)?.Trim(), "0", StringComparison.Ordinal);
}
