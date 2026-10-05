using System.Globalization;
using System.Text;
using Genesis.Runtime;
using Genesis.Runtime.Input;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Input recording and replay: a script records a run driven by simulated devices with uneven
/// time steps, a second run replays it while the devices do something else, and the script must
/// see the same input, time steps, window size and random numbers on every frame and end in the
/// same place. Then the file format, Escape and the hand-back to the real devices.
/// </summary>
internal static class InputReplaySuite
{
    private const int RecordedFrames = 40;

    private const string CreateScript = """
        posX = 0;
        posY = 0;
        if (!InputReplayStart("probe")) { recordStarted = InputRecordStart("probe") ? 1 : 0; }
        """;

    // Everything the script reads is folded into numbers the test compares frame by frame.
    private const string StepScript = """
        frameNo = InputReplayFrame();
        recording = InputRecordActive() ? 1 : 0;
        replaying = InputReplayActive() ? 1 : 0;
        finished = InputReplayFinished() ? 1 : 0;
        sig = (KeyCheck("D") ? 1 : 0) + (KeyPressed("Space") ? 2 : 0) + (KeyReleased("Space") ? 4 : 0)
            + (MouseCheck(MbLeft) ? 8 : 0) + (MousePressed(MbLeft) ? 16 : 0) + (MouseReleased(MbLeft) ? 32 : 0)
            + (GamepadCheck("A") ? 64 : 0) + (GamepadPressed("A") ? 128 : 0) + (GamepadCheck("RT") ? 256 : 0)
            + (GamepadConnected() ? 512 : 0) + (KeyCheck("A") ? 1024 : 0);
        axis = GamepadAxis("LeftX") + GamepadAxis("RightTrigger") * 10 + GamepadAxis("RightY") * 100;
        mx = MouseX;
        my = MouseY;
        wheel = MouseWheel();
        dt = DeltaTime;
        rnd = Random(1000);
        pick = Choose(1, 2, 3, 4, 5);
        if (KeyCheck("D")) { posX = posX + 120 * DeltaTime; }
        posX = posX + GamepadAxis("LeftX") * 60 * DeltaTime;
        if (KeyPressed("Space")) { posY = posY + 1; }
        if (MouseCheck(MbLeft)) { posY = posY + MouseX * 0.001; }
        posY = posY + wheel;
        if (recording == 1 && frameNo >= 40) { savedFrames = InputRecordStop(); }
        """;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.InputReplay.ReplaySeesTheRecordedInputTimeAndRandomNumbers", () =>
        {
            string project = Path.Combine(ctx.Workspace, "InputReplay");
            Directory.CreateDirectory(project);
            string recording = InputReplay.ResolvePath(project, "probe");
            if (File.Exists(recording)) File.Delete(recording);
            string previousProject = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ProjectPath = project;
                InputReplay.Reset();

                // First run: no recording yet, so the script records while simulated devices play.
                List<string> recorded = Play(project, RecordedFrames + 4, recordRun: true, out _);
                HeadlessHarness.Assert(File.Exists(recording), "The recording was not saved at " + recording);
                HeadlessHarness.Assert(InputReplay.TryRead(recording, out int seed, out InputFrame[] frames, out string readError)
                    && frames.Length == RecordedFrames && seed == InputReplay.Seed,
                    $"The recording should hold {RecordedFrames} frames and its seed: {frames.Length} frames, {readError}");

                // Second run: the recording exists, so the script replays it; the devices do something else.
                List<string> replayed = Play(project, RecordedFrames + 4, recordRun: false, out PlayEnd end);
                HeadlessHarness.Assert(recorded.Count == RecordedFrames && replayed.Count == RecordedFrames,
                    $"The script saw {recorded.Count} recorded and {replayed.Count} replayed frames; expected {RecordedFrames} of each.");
                for (int i = 0; i < RecordedFrames; i++)
                {
                    HeadlessHarness.Assert(recorded[i] == replayed[i],
                        $"Frame {i + 1} differed between the recording and the replay:\n recorded {recorded[i]}\n replayed {replayed[i]}");
                }
                HeadlessHarness.Assert(end.FinishedSeen && end.ActiveAfterEnd == false,
                    "The replay did not report its end, or stayed active after its last frame.");
                HeadlessHarness.Assert(end.RealKeySeenAfterEnd,
                    "A real key pressed after the replay ended was not seen: the devices were not handed back.");
                HeadlessHarness.Assert(end.StartedFrames == 1,
                    $"The fixed-step clock should be restarted once, at the replay's first frame; it was {end.StartedFrames} times.");

                // The replay is not just equal, it is the recorded run: different time steps and input than the devices gave.
                string unevenStep = "dt=" + ((double)0.033f).ToString("R", CultureInfo.InvariantCulture) + " ";
                HeadlessHarness.Assert(replayed.Any(line => line.Contains(unevenStep, StringComparison.Ordinal))
                    && replayed.Any(line => line.Contains("window=1024x600", StringComparison.Ordinal))
                    && replayed.Any(line => line.Contains("typed=h", StringComparison.Ordinal))
                    && !replayed.Any(line => line.Contains("typed=z", StringComparison.Ordinal)),
                    "The replay did not carry the recorded time steps, window size and typing:\n" + string.Join("\n", replayed));
                File.WriteAllLines(Path.Combine(ctx.Captures, "input-replay-frames.txt"), replayed);
            }
            finally
            {
                InputReplay.Reset();
                PgslCommands.ProjectPath = previousProject;
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.InputReplay.EscapeStopsAReplayAndBadFilesAreRefused", () =>
        {
            string project = Path.Combine(ctx.Workspace, "InputReplayEscape");
            Directory.CreateDirectory(project);
            string path = InputReplay.ResolvePath(project, "held");
            try
            {
                InputReplay.Reset();
                var input = new InputState();
                HeadlessHarness.Assert(InputReplay.StartRecording(path, seed: 7), "Recording could not start: " + InputReplay.LastError);
                for (int i = 0; i < 10; i++)
                {
                    input.OnKeyDown(Key.W);
                    input.SetGamepadButton(GamepadButton.RightTrigger, true);
                    input.RightTrigger = 1f;
                    Step(input);
                }
                HeadlessHarness.Assert(InputReplay.StopRecording() == 10, "Ten frames should have been recorded.");
                input.ReleaseAll();
                input.NextFrame();

                HeadlessHarness.Assert(InputReplay.StartReplay(path), "The replay could not start: " + InputReplay.LastError);
                Step(input);
                Step(input);
                HeadlessHarness.Assert(input.IsDown(Key.W) && input.IsDown(GamepadButton.RightTrigger) && input.RightTrigger == 1f,
                    "A held key and a held trigger were not replayed.");
                input.OnKeyDown(Key.Escape);
                Step(input);
                HeadlessHarness.Assert(InputReplay.Mode == InputReplayMode.Off && !InputReplay.Finished,
                    "Escape did not stop the replay, or the stopped replay claimed to have finished.");
                HeadlessHarness.Assert(!input.IsDown(Key.W) && !input.IsDown(GamepadButton.RightTrigger) && input.RightTrigger == 0f
                    && !input.WasPressed(Key.Escape),
                    "Stopping the replay left its keys held, or passed the Escape press to the game.");

                // A file that is not a recording, and one that is not there, are refused with a reason.
                string bogus = Path.Combine(project, "bogus.ginput");
                File.WriteAllText(bogus, "not a recording");
                HeadlessHarness.Assert(!InputReplay.StartReplay(bogus) && InputReplay.LastError.Length > 0 && !InputReplay.IsActive,
                    "A file that is not a recording was played.");
                HeadlessHarness.Assert(!InputReplay.StartReplay(Path.Combine(project, "missing.ginput")) && !InputReplay.IsActive,
                    "A missing recording was played.");
                HeadlessHarness.Assert(InputReplay.ResolvePath(project, "a/b:c").EndsWith("a_b_c.ginput", StringComparison.Ordinal)
                    && InputReplay.ResolvePath(project, "").Length == 0,
                    "Recording names were not made into safe file names.");
            }
            finally
            {
                InputReplay.Reset();
            }
        });
    }

    private static void Step(InputState input)
    {
        float delta = 1f / 60f;
        int width = 640, height = 480;
        if (InputReplay.IsActive) InputReplay.BeginFrame(input, ref delta, ref width, ref height, out _);
        input.NextFrame();
    }

    private sealed class PlayEnd
    {
        public bool FinishedSeen;
        public bool? ActiveAfterEnd;
        public bool RealKeySeenAfterEnd;
        public int StartedFrames;
    }

    /// <summary>
    /// One run of a room with one scripted object, stepped the way the Player steps it: the
    /// devices report, the replay takes the frame's input, scripts run, then the frame's edges end.
    /// </summary>
    private static List<string> Play(string project, int frames, bool recordRun, out PlayEnd end)
    {
        end = new PlayEnd();
        using var scene = new RuntimeScene("Replay") { Input = new InputState() };
        InputState input = scene.Input;
        var game = new ProjectGameContext(project, scene, null, null, RoomAsset.Create("Replay", RoomDimension.TwoD), null);
        var host = new ScriptHostSystem();
        host.SetContext(game);
        Entity entity = scene.CreateEntity(System.Numerics.Vector3.Zero);
        var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Create"] = CreateScript,
            ["Step"] = StepScript,
        };
        PgslBehavior behavior;
        using (host.UseEventSources(events))
            behavior = (PgslBehavior)host.Attach(scene.World, entity, "ReplayProbe");

        IGameContext previousGame = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = game;
        var observed = new List<string>();
        try
        {
            host.BeginRoom(beginGame: true);
            for (int frame = 0; frame < frames; frame++)
            {
                if (recordRun) SimulateDevices(input, frame);
                else SimulateOtherDevices(input, frame);

                // The record run steps unevenly; the replay run's own clock is steady and must be overridden.
                float delta = recordRun ? (frame % 3 == 2 ? 0.033f : frame % 3 == 1 ? 0.0125f : 1f / 60f) : 1f / 60f;
                int width = recordRun ? (frame < 20 ? 1280 : 1024) : 800;
                int height = recordRun ? (frame < 20 ? 720 : 600) : 600;
                bool wasReplaying = InputReplay.Mode == InputReplayMode.Replaying;
                if (InputReplay.IsActive)
                {
                    InputReplay.BeginFrame(input, ref delta, ref width, ref height, out bool started);
                    if (started) end.StartedFrames++;
                }
                string typed = input.TakeTypedText();

                scene.GameTime.Advance(delta);
                host.Update(delta);

                IReadOnlyDictionary<string, object> v = behavior.GetVariablesSnapshot();
                // Only the frames the script saw being recorded, or replayed, are compared.
                if (Text(v, recordRun ? "recording" : "replaying") == "1")
                    observed.Add(string.Join(" ",
                    "frame=" + Text(v, "frameNo"), "sig=" + Text(v, "sig"), "axis=" + Text(v, "axis"),
                    "mouse=" + Text(v, "mx") + "," + Text(v, "my"), "wheel=" + Text(v, "wheel"),
                    "dt=" + Text(v, "dt"), "rnd=" + Text(v, "rnd"), "pick=" + Text(v, "pick"),
                    "pos=" + Text(v, "posX") + "," + Text(v, "posY"), $"window={width}x{height}", "typed=" + typed));

                if (!recordRun)
                {
                    if (Text(v, "finished") == "1") end.FinishedSeen = true;
                    if (wasReplaying && InputReplay.Mode == InputReplayMode.Off && end.ActiveAfterEnd == null)
                        end.ActiveAfterEnd = Text(v, "replaying") == "1";
                    if (frame == frames - 1)
                        end.RealKeySeenAfterEnd = Text(v, "replaying") == "0" && (Convert.ToInt32(v["sig"], CultureInfo.InvariantCulture) & 1024) != 0;
                }

                input.NextFrame();
            }
            end.ActiveAfterEnd ??= InputReplay.IsActive;
        }
        finally
        {
            host.EndRoom(endGame: true);
            host.Detach(entity);
            PgslCommands.ActiveGameContext = previousGame;
        }
        return observed;
    }

    /// <summary>A short session: walking, a jump tap, a click-and-drag, a wheel turn, a controller and typing.</summary>
    private static void SimulateDevices(InputState input, int frame)
    {
        if (frame == 2) input.OnKeyDown(Key.D);
        if (frame == 14) input.OnKeyUp(Key.D);
        if (frame == 6) input.OnKeyDown(Key.Space);
        if (frame == 7) input.OnKeyUp(Key.Space);
        // A tap that goes down and up between two frames is still a press.
        if (frame == 9) { input.OnKeyDown(Key.Space); input.OnKeyUp(Key.Space); }
        input.OnMouseMove(100 + frame * 7, 50 + frame * 3);
        if (frame == 10) input.OnMouseDown(MouseButton.Left);
        if (frame == 18) input.OnMouseUp(MouseButton.Left);
        if (frame == 12) input.OnWheel(2f);
        if (frame == 5) input.OnChar('h');
        input.GamepadConnected = frame >= 3;
        if (frame >= 3)
        {
            input.SetGamepadButton(GamepadButton.A, frame >= 20 && frame < 24);
            input.SetGamepadButton(GamepadButton.RightTrigger, frame >= 25);
            input.RightTrigger = frame >= 25 ? 0.75f : 0f;
            input.LeftStick = new System.Numerics.Vector2(frame >= 26 && frame < 34 ? 0.5f : 0f, 0f);
            input.RightStick = new System.Numerics.Vector2(0f, frame % 5 == 0 ? 0.25f : 0f);
        }
    }

    /// <summary>What the devices do during the replay: different keys, buttons and pointer, which must not be seen.</summary>
    private static void SimulateOtherDevices(InputState input, int frame)
    {
        if (frame % 2 == 0) input.OnKeyDown(Key.A); else input.OnKeyUp(Key.A);
        input.OnMouseMove(900 - frame, 10);
        if (frame == 4) input.OnMouseDown(MouseButton.Left);
        input.OnWheel(-1f);
        input.GamepadConnected = true;
        input.SetGamepadButton(GamepadButton.A, frame % 3 == 0);
        input.LeftStick = new System.Numerics.Vector2(-1f, 0f);
        if (frame == 8) input.OnChar('z');
        // After the replay hands back, a fresh press of A must reach the game.
        if (frame == RecordedFrames + 3) input.OnKeyDown(Key.A);
    }

    private static string Text(IReadOnlyDictionary<string, object> variables, string name)
    {
        if (!variables.TryGetValue(name, out object? value) || value == null) return "-";
        return value is double number
            ? number.ToString("R", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "-";
    }
}
