using Genesis.Runtime.Input;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Input recording and replay for tests a person does not have to sit through: a run is recorded
/// once with the real keyboard, mouse and controller, then played back in the real Player with
/// the same input, time steps and random numbers on the same frames. Pictures for an acceptance
/// run come from <c>ScreenshotSave</c>.
/// </summary>
public static partial class PgslCommands
{
    private static System.Random _seededRandom;
    private static readonly object SeededRandomGate = new();

    /// <summary>
    /// Makes Random, RandomRange, Choose and DsListShuffle draw from a generator started from
    /// <paramref name="seed"/>, or from the shared unseeded one again when it is null.
    /// </summary>
    internal static void SeedRandom(int? seed)
    {
        lock (SeededRandomGate) _seededRandom = seed.HasValue ? new System.Random(seed.Value) : null;
    }

    internal static double NextRandomDouble()
    {
        if (_seededRandom == null) return System.Random.Shared.NextDouble();
        lock (SeededRandomGate) return (_seededRandom ?? System.Random.Shared).NextDouble();
    }

    internal static int NextRandomInt(int maxExclusive)
    {
        if (maxExclusive <= 1) return 0;
        if (_seededRandom == null) return System.Random.Shared.Next(maxExclusive);
        lock (SeededRandomGate) return (_seededRandom ?? System.Random.Shared).Next(maxExclusive);
    }

    [PgslCommand("RandomSeed", "RandomSeed(seed)",
        "Make Random, RandomRange, Choose and DsListShuffle give the same numbers every run from this seed", "Math")]
    public static void RandomSeed(double seed) => SeedRandom(unchecked((int)(long)seed));

    [PgslCommand("InputRecordStart", "InputRecordStart(name) -> bool",
        "Record the keyboard, mouse, controller, time step and window size of every frame from the next one, as name.ginput in the project's debug Replays folder (or at a full path); false when the file cannot be written",
        "Debug")]
    public static bool InputRecordStart(string name) =>
        InputReplay.StartRecording(InputReplay.ResolvePath(ProjectPath, name));

    [PgslCommand("InputRecordStop", "InputRecordStop() -> number",
        "Stop recording and save the file; the number of frames recorded", "Debug")]
    public static double InputRecordStop() => InputReplay.StopRecording();

    [PgslCommand("InputReplayStart", "InputReplayStart(name) -> bool",
        "Play a recording from the next frame in place of the real keyboard, mouse and controller, with its time steps and random numbers; false when there is no such recording. Escape stops it",
        "Debug")]
    public static bool InputReplayStart(string name) =>
        InputReplay.StartReplay(InputReplay.ResolvePath(ProjectPath, name));

    [PgslCommand("InputReplayStop", "InputReplayStop()", "Stop a replay; the real devices are read again from the next frame", "Debug")]
    public static void InputReplayStop() => InputReplay.StopReplay();

    [PgslCommand("InputReplayActive", "InputReplayActive() -> bool", "True while a recording is being played", "Debug")]
    public static bool InputReplayActive() => InputReplay.Mode == InputReplayMode.Replaying;

    [PgslCommand("InputRecordActive", "InputRecordActive() -> bool", "True while input is being recorded", "Debug")]
    public static bool InputRecordActive() => InputReplay.Mode == InputReplayMode.Recording;

    [PgslCommand("InputReplayFrame", "InputReplayFrame() -> number",
        "Frames recorded or played so far: 1 in the first frame of a recording or replay, and the same frame number in both", "Debug")]
    public static double InputReplayFrame() => InputReplay.Frame;

    [PgslCommand("InputReplayFinished", "InputReplayFinished() -> bool",
        "True from the frame the last recorded frame is played; the real devices are back from the frame after", "Debug")]
    public static bool InputReplayFinished() => InputReplay.Finished;
}
