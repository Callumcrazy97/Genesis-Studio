using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Commands;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// A room's Frame rate and VSync are defaults: they reach the window until the game chooses its
/// own (a settings menu, a saved option read in a Create event, which runs before the room's
/// settings are applied), and from then on no room change resets the game's choice.
/// GameUseRoomFps hands both back. Part of tier-engine (frame-rate-choice).
/// </summary>
internal static class FrameRateChoiceChecks
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "FrameRateChoice");
        IGameContext? previous = PgslCommands.ActiveGameContext;
        RoomAsset menu = Room(60, vsync: true);
        RoomAsset level = Room(30, vsync: false);
        try
        {
            HeadlessHarness.RunCase(ctx.Report, "Engine.Display.FrameRate.RoomSettingsApplyUntilTheGameChooses", () =>
            {
                ProjectRoomPresentation.ResetAppliedFrameRate();
                var window = new PacingWindow { TargetFps = 200, VSync = false };
                ProjectRoomPresentation.Apply(window, null, menu);
                Expect(window, 60, true, "the first room");
                ProjectRoomPresentation.Apply(window, null, level);
                Expect(window, 30, false, "a room with other settings");
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Display.FrameRate.GameChoiceOutranksEveryRoom", () =>
            {
                // The game reads its options in a Create event, before the first room's settings are applied.
                ProjectRoomPresentation.ResetAppliedFrameRate();
                var window = new PacingWindow { TargetFps = 200, VSync = true };
                PgslCommands.ActiveGameContext = new ProjectGameContext(ctx.Workspace, null, null, null, menu, null);
                PgslCommands.GameSetMaxFps(0);
                PgslCommands.WindowSetVSync(false);
                HeadlessHarness.Assert(ProjectRoomPresentation.GameChoseFrameRate && ProjectRoomPresentation.GameChoseVSync,
                    "GameSetMaxFps and WindowSetVSync were not recorded as the game's own choice");
                // The context has no window here; the window gets what the game asked for.
                window.TargetFps = 0;
                window.VSync = false;

                ProjectRoomPresentation.Apply(window, null, menu);
                Expect(window, 0, false, "the first room, after the game chose uncapped without vsync");
                ProjectRoomPresentation.Apply(window, null, level);
                Expect(window, 0, false, "a room change to a room at 30 with vsync off");
                ProjectRoomPresentation.Apply(window, null, menu);
                Expect(window, 0, false, "a room change back to a room at 60 with vsync on");

                PgslCommands.GameUseRoomFps();
                Expect(window, 60, true, "GameUseRoomFps in a room at 60 with vsync on");
                ProjectRoomPresentation.Apply(window, null, level);
                Expect(window, 30, false, "a room change after GameUseRoomFps");
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Display.FrameRate.EngineCommandsCountAsTheGamesChoice", () =>
            {
                ProjectRoomPresentation.ResetAppliedFrameRate();
                var window = new PacingWindow { TargetFps = 200, VSync = false };
                ProjectRoomPresentation.Apply(window, null, menu);
                using var scene = new RuntimeScene();
                using (new EngineRuntimeCommandBinding(scene, window, new RendererOptions()))
                {
                    Engine.FpsTarget(144);
                    Engine.SetVsync(false);
                }
                Expect(window, 144, false, "Engine.FpsTarget(144) and Engine.SetVsync(false)");
                ProjectRoomPresentation.Apply(window, null, level);
                ProjectRoomPresentation.Apply(window, null, menu);
                Expect(window, 144, false, "room changes after Engine.FpsTarget and Engine.SetVsync");
            });
        }
        finally
        {
            PgslCommands.ActiveGameContext = previous;
            ProjectRoomPresentation.ResetAppliedFrameRate();
            Engine.FpsTarget(-1);
            Engine.SetVsync(true);
        }
    }

    private static RoomAsset Room(int fps, bool vsync) => new()
    {
        Dimension = RoomDimension.TwoD,
        Settings = new RoomSettings { TargetFps = fps, VSync = vsync, CaptureMouse = false },
    };

    private static void Expect(PacingWindow window, int fps, bool vsync, string after) =>
        HeadlessHarness.Assert(window.TargetFps == fps && window.VSync == vsync,
            $"After {after} the window runs at {window.TargetFps} fps with vsync {(window.VSync ? "on" : "off")}; "
            + $"expected {fps} fps with vsync {(vsync ? "on" : "off")}.");

    /// <summary>The pacing half of a game window, with no surface.</summary>
    private sealed class PacingWindow : IGameWindow
    {
        public string Title { get; set; } = "";
        public int Width => 640;
        public int Height => 360;
        public int TargetFps { get; set; }
        public double CurrentFps => 0;
        public bool VSync { get; set; }
        public WindowMode Mode { get; set; }
        public bool IsRunning => true;
        public IntPtr NativeHandle => IntPtr.Zero;
        public bool MouseCaptured { get; private set; }

        public event Action Load { add { } remove { } }
        public event Action<double> Update { add { } remove { } }
        public event Action<double> Render { add { } remove { } }
        public event Action<int, int> Resize { add { } remove { } }
        public event Action Closing { add { } remove { } }

        public void Run() { }
        public void Close() { }
        public void SetSize(int width, int height) { }
        public void SetMouseCaptured(bool captured) => MouseCaptured = captured;
        public void SetCursorMode(CursorMode mode) => MouseCaptured = mode == CursorMode.Locked;
        public void Dispose() { }
    }
}
