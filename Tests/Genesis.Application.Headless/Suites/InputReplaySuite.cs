using System.Globalization;
using System.Text;
using Genesis.Application.Core.Automation;
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
/// same place. Then the file format, Escape and the hand-back to the real devices, and an
/// automation run's scripted input, which is played by the clock at whatever frame rate.
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
            bool? ranToEnd = null;
            void OnEnded(bool toEnd) => ranToEnd = toEnd;
            InputReplay.ReplayEnded += OnEnded;
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
                HeadlessHarness.Assert(ranToEnd == false, "A replay stopped by Escape was reported as having run to its end.");

                // Stopped by a script: what it held is let go at the next frame, and then nothing is checked per frame.
                HeadlessHarness.Assert(InputReplay.StartReplay(path), "The replay could not start again: " + InputReplay.LastError);
                Step(input);
                InputReplay.StopReplay();
                Step(input);
                HeadlessHarness.Assert(!InputReplay.IsActive && !input.IsDown(Key.W) && !InputReplay.Finished,
                    "A replay stopped by a script kept its keys held, or kept the host checking it every frame.");

                // Played to its end: finished on the last frame, the devices back on the next.
                ranToEnd = null;
                HeadlessHarness.Assert(InputReplay.StartReplay(path), "The replay could not start again: " + InputReplay.LastError);
                for (int i = 0; i < 10; i++) Step(input);
                HeadlessHarness.Assert(InputReplay.Finished && InputReplay.Mode == InputReplayMode.Replaying && ranToEnd == null,
                    "The replay should be finished, and still in charge, on its last frame.");
                Step(input);
                HeadlessHarness.Assert(InputReplay.Mode == InputReplayMode.Off && ranToEnd == true && !input.IsDown(Key.W),
                    "The replay did not hand back to the devices, and let go of its keys, the frame after its last.");

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
                InputReplay.ReplayEnded -= OnEnded;
                InputReplay.Reset();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.InputReplay.ScriptedInputPlaysByTheClockKeepingPressesAndLook", () =>
        {
            // game.run's walk: stand, walk, jump while walking and turning, stand. Played uncapped,
            // slowly and unevenly it must last its seconds of play, keep the game's own time steps,
            // press and let go of each key once and turn exactly the written turn.
            var steps = new List<GameRunCommands.InputStep>
            {
                new() { Seconds = 0.5f },
                new() { Seconds = 1f, Hold = ["W"] },
                new() { Seconds = 0.5f, Hold = ["W"], Press = ["Space"], Look = [90f, -30f] },
                new() { Seconds = 0.25f },
            };
            InputFrame[] written = GameRunCommands.Frames(steps);
            string path = Path.Combine(ctx.Workspace, "InputReplayClock", "walk.ginput");
            GameRunCommands.WriteInput(path, 3, written);
            HeadlessHarness.Assert(InputReplay.TryRead(path, out _, out InputFrame[] read, out InputPlayback playback, out string error)
                && playback == InputPlayback.ByClock && read.Length == written.Length,
                "game.run's input was not saved to be played by the clock: " + error);
            double seconds = written.Sum(frame => (double)frame.Delta), heldW = 1.5;
            double lookX = written.Sum(frame => (double)frame.LookX), lookY = written.Sum(frame => (double)frame.LookY);
            HeadlessHarness.Assert(Math.Abs(lookX - 45) < 1e-3 && Math.Abs(lookY + 15) < 1e-3, $"The written turn was {lookX}, {lookY}; expected 45, -15.");

            var rates = new (string Name, Func<int, float> Step)[]
            {
                ("uncapped (900 frames a second)", _ => 1f / 900f),
                ("at the written rate (60 a second)", _ => 1f / 60f),
                ("slowly (10 a second)", _ => 0.1f),
                ("unevenly", frame => (frame % 4) switch { 0 => 0.004f, 1 => 0.05f, 2 => 1f / 60f, _ => 0.2f }),
            };
            var lines = new List<string>();
            foreach ((string name, Func<int, float> step) in rates)
            {
                ClockRun run = PlayByClock(path, step);
                lines.Add($"{name}: {run.Rendered} frames, finished at {run.PlayedWhenFinished:0.####} s, W held {run.HeldW:0.####} s, "
                    + $"W {run.WPressed}/{run.WReleased}, Space {run.SpacePressed}/{run.SpaceReleased}, look {run.LookX:0.####}, {run.LookY:0.####}");
                HeadlessHarness.Assert(run.DeltaKept && run.Started == 1,
                    $"Played {name}, the replay changed the game's time step, or did not restart the fixed-step clock once.");
                HeadlessHarness.Assert(run.PlayedWhenFinished >= seconds - 1e-4 && run.PlayedWhenFinished < seconds + run.Longest + 1e-4,
                    $"Played {name}, the {seconds:0.##} s of input finished after {run.PlayedWhenFinished:0.####} s of play.");
                HeadlessHarness.Assert(run.WPressed == 1 && run.WReleased == 1 && run.SpacePressed == 1 && run.SpaceReleased == 1,
                    $"Played {name}, a press or release did not come exactly once: {lines[^1]}");
                HeadlessHarness.Assert(Math.Abs(run.HeldW - heldW) <= run.Longest + 1e-4,
                    $"Played {name}, W was held for {run.HeldW:0.####} s of play; it was written for {heldW} s.");
                HeadlessHarness.Assert(Math.Abs(run.LookX - lookX) < 1e-3 && Math.Abs(run.LookY - lookY) < 1e-3,
                    $"Played {name}, the camera turned {run.LookX}, {run.LookY}; the input turns {lookX}, {lookY}.");
                HeadlessHarness.Assert(run.EndedToEnd && !run.HeldAfterEnd,
                    $"Played {name}, the replay did not hand back to the devices, letting go of W, the frame after its end.");
            }
            // At the rate it was written for it is played a recorded frame a frame, as a recording is.
            HeadlessHarness.Assert(PlayByClock(path, _ => 1f / 60f).Rendered == written.Length,
                "Played at its own rate, scripted input did not take one recorded frame a frame.");
            File.WriteAllLines(Path.Combine(ctx.Captures, "input-replay-clock.txt"), lines);
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.InputReplay.RecordingsStayFrameByFrameAndVersionOneFilesStillPlay", () =>
        {
            string folder = Path.Combine(ctx.Workspace, "InputReplayVersions");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "recorded.ginput");
            float[] recordedSteps = [0.033f, 0.0125f, 1f / 60f, 0.05f, 0.002f, 1f / 30f];
            try
            {
                InputReplay.Reset();
                var input = new InputState();
                HeadlessHarness.Assert(InputReplay.StartRecording(path, seed: 11), "Recording could not start: " + InputReplay.LastError);
                for (int i = 0; i < recordedSteps.Length; i++)
                {
                    if (i == 2) input.OnKeyDown(Key.W);
                    float delta = recordedSteps[i];
                    int width = 640, height = 480;
                    InputReplay.BeginFrame(input, ref delta, ref width, ref height, out _);
                    input.NextFrame();
                }
                HeadlessHarness.Assert(InputReplay.StopRecording() == recordedSteps.Length, "Every frame should have been recorded.");
                HeadlessHarness.Assert(InputReplay.TryRead(path, out int seed, out InputFrame[] frames, out InputPlayback playback, out string error)
                    && seed == 11 && frames.Length == recordedSteps.Length && playback == InputPlayback.FrameByFrame,
                    "A recording should be saved to be played frame by frame: " + error);

                // Played uncapped, a recording is still the recording: a frame each, with its time step.
                PlayFrameByFrame(input, path, recordedSteps, "A recording");

                // A version 1 file (no word saying how it is played, 16 bytes of header) is a recording.
                byte[] saved = File.ReadAllBytes(path);
                HeadlessHarness.Assert(BitConverter.ToInt32(saved, 8) == InputReplay.FormatVersion && InputReplay.FormatVersion == 2,
                    "Recordings should be saved as version 2 of the format.");
                string old = Path.Combine(folder, "version1.ginput");
                using (var writer = new BinaryWriter(File.Create(old)))
                {
                    writer.Write(saved, 0, 8);
                    writer.Write(1);
                    writer.Write(11);
                    writer.Write(saved, 20, saved.Length - 20);
                }
                HeadlessHarness.Assert(InputReplay.TryRead(old, out int oldSeed, out InputFrame[] oldFrames, out InputPlayback oldPlayback, out string oldError)
                    && oldSeed == 11 && oldFrames.Length == recordedSteps.Length && oldPlayback == InputPlayback.FrameByFrame,
                    "A version 1 recording was not read as a recording: " + oldError);
                PlayFrameByFrame(input, old, recordedSteps, "A version 1 recording");

                // A version this Player does not know is refused, saying which.
                byte[] future = (byte[])saved.Clone();
                BitConverter.GetBytes(InputReplay.FormatVersion + 1).CopyTo(future, 8);
                string futurePath = Path.Combine(folder, "future.ginput");
                File.WriteAllBytes(futurePath, future);
                HeadlessHarness.Assert(!InputReplay.StartReplay(futurePath) && InputReplay.LastError.Contains("version 3", StringComparison.Ordinal),
                    "A recording from a later format was played, or refused without saying why: " + InputReplay.LastError);

                // Asked to, a recording is played by the clock: the game's time step is kept.
                HeadlessHarness.Assert(InputReplay.StartReplay(path, InputPlayback.ByClock) && InputReplay.Playback == InputPlayback.ByClock,
                    "A recording could not be played by the clock when asked: " + InputReplay.LastError);
                float kept = 1f / 900f;
                int keptWidth = 640, keptHeight = 480;
                InputReplay.BeginFrame(input, ref kept, ref keptWidth, ref keptHeight, out _);
                HeadlessHarness.Assert(kept == 1f / 900f && InputReplay.Frame == 1,
                    "A recording played by the clock replaced the game's time step.");
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

    /// <summary>Plays a recording uncapped and checks it is played a frame a frame with its own time steps: W from its third frame.</summary>
    private static void PlayFrameByFrame(InputState input, string path, float[] recordedSteps, string what)
    {
        HeadlessHarness.Assert(InputReplay.StartReplay(path) && InputReplay.Playback == InputPlayback.FrameByFrame,
            $"{what} could not be replayed frame by frame: {InputReplay.LastError}");
        for (int i = 0; i < recordedSteps.Length; i++)
        {
            float delta = 1f / 900f;
            int width = 1920, height = 1080;
            InputReplay.BeginFrame(input, ref delta, ref width, ref height, out _);
            HeadlessHarness.Assert(delta == recordedSteps[i] && InputReplay.Frame == i + 1 && width == 640 && height == 480
                && input.IsDown(Key.W) == (i >= 2) && input.WasPressed(Key.W) == (i == 2),
                $"{what}, replayed, differed from what was recorded in frame {i + 1}: time step {delta}, frame {InputReplay.Frame}, window {width}x{height}.");
            input.NextFrame();
        }
        InputReplay.StopReplay();
        Step(input);
    }

    private sealed class ClockRun
    {
        public int Rendered, Started, WPressed, WReleased, SpacePressed, SpaceReleased;
        public double PlayedWhenFinished = -1, HeldW, Longest, LookX, LookY;
        public bool DeltaKept = true, EndedToEnd, HeldAfterEnd;
    }

    /// <summary>
    /// Plays a replay by the clock with the given time steps, as the host does (the replay takes the
    /// frame's input, the game reads it, the frame's presses end), until it hands back to the devices.
    /// </summary>
    private static ClockRun PlayByClock(string path, Func<int, float> step)
    {
        var run = new ClockRun();
        bool? ranToEnd = null;
        void OnEnded(bool toEnd) => ranToEnd = toEnd;
        InputReplay.ReplayEnded += OnEnded;
        try
        {
            InputReplay.Reset();
            HeadlessHarness.Assert(InputReplay.StartReplay(path) && InputReplay.Playback == InputPlayback.ByClock,
                "Scripted input did not start playing by the clock: " + InputReplay.LastError);
            var input = new InputState();
            double played = 0;
            for (int i = 0; i < 100_000 && InputReplay.IsActive; i++)
            {
                float delta = step(i), given = delta;
                int width = 640, height = 480;
                InputReplay.BeginFrame(input, ref delta, ref width, ref height, out bool started);
                if (InputReplay.Mode != InputReplayMode.Replaying) break;
                run.Rendered++;
                if (started) run.Started++;
                played += given;
                run.Longest = Math.Max(run.Longest, given);
                run.DeltaKept &= delta == given;
                if (input.WasPressed(Key.W)) run.WPressed++;
                if (input.WasReleased(Key.W)) run.WReleased++;
                if (input.WasPressed(Key.Space)) run.SpacePressed++;
                if (input.WasReleased(Key.Space)) run.SpaceReleased++;
                if (input.IsDown(Key.W)) run.HeldW += given;
                run.LookX += input.LookDelta.X;
                run.LookY += input.LookDelta.Y;
                if (InputReplay.Finished && run.PlayedWhenFinished < 0) run.PlayedWhenFinished = played;
                input.NextFrame();
            }
            run.EndedToEnd = ranToEnd == true && !InputReplay.IsActive;
            run.HeldAfterEnd = input.IsDown(Key.W);
        }
        finally
        {
            InputReplay.ReplayEnded -= OnEnded;
            InputReplay.Reset();
        }
        return run;
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
