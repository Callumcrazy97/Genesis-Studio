namespace Genesis.Application.Core.Diagnostics;

/// <summary>
/// True while the application is being driven by something with nobody sitting in front of it.
/// </summary>
/// <remarks>
/// A modal dialog in an unattended run is not a prompt, it is a hang: the headless suite, the build
/// gate and the published-app smoke test all drive real windows, and any one of them can reach a
/// "Save changes?" box that will wait forever for a click nobody is there to make. It blocked a
/// build for exactly that reason.
///
/// Code that would ask a question checks this first and takes the conservative answer instead —
/// which for unsaved work means <i>saving it</i>, never discarding it — and logs what it decided so
/// the run's output still says what happened. This is deliberately not a "suppress all dialogs"
/// switch: an error someone needs to see is still shown, because the alternative is a run that
/// passes while hiding the thing that went wrong.
/// </remarks>
public static class UnattendedSession
{
    /// <summary>Whether prompts must answer themselves rather than wait for a person.</summary>
    public static bool IsActive { get; private set; }

    /// <summary>
    /// Declares this process automated. Called by the headless runner at startup; nothing in the
    /// interactive application calls it.
    /// </summary>
    public static void Enable() => IsActive = true;
}
