using System;
using System.Numerics;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// How a room change that takes more than a moment is spread over frames and what is shown
    /// meanwhile. A game sets these once, from a script; they hold for every later room change.
    /// </summary>
    public static class RoomChangeScreen
    {
        /// <summary>Overrides the frame budget for a run, in milliseconds; 0 changes room in one step, as engines before this did.</summary>
        public const string BudgetEnvironmentVariable = "GENESIS_ROOM_CHANGE_BUDGET_MS";

        /// <summary>How long a room change works in each frame before letting the frame be drawn, when the game has not said.</summary>
        public const double DefaultFrameBudgetMilliseconds = 8;

        /// <summary>
        /// The frame budget a script asked for, in milliseconds, or null to leave it to the host.
        /// 0 changes room in one step with no cover and no loading screen.
        /// </summary>
        public static double? FrameBudgetMilliseconds { get; set; }

        /// <summary>
        /// A 2D room that is ready within this many milliseconds simply appears, with no cover:
        /// a platformer stepping from screen to screen, or restarting its level, is not
        /// interrupted by a loading screen. A quarter of a second, so that a change which is
        /// usually quick does not show a cover on the day the machine is busy.
        /// </summary>
        public static double GraceMilliseconds { get; set; } = 250;

        /// <summary>Whether the engine draws its own progress bar and text on the cover. A game that draws its own turns this off, or draws from <see cref="EntityBehavior.OnDrawLoadingScreen"/>.</summary>
        public static bool ShowProgress { get; set; } = true;

        /// <summary>The words above the engine's progress bar; empty for none.</summary>
        public static string Text { get; set; } = "Loading";

        /// <summary>The colour of the cover.</summary>
        public static Vector4 Background { get; set; } = new(0.02f, 0.02f, 0.03f, 1f);

        /// <summary>The colour of the engine's progress bar and text.</summary>
        public static Vector4 Foreground { get; set; } = new(0.86f, 0.88f, 0.92f, 1f);

        /// <summary>How long the cover takes to fade once the room is ready, in seconds; 0 lifts it at once.</summary>
        public static float FadeSeconds { get; set; } = 0.2f;

        /// <summary>
        /// The least time a cover stays up once it is raised, in seconds, so a loading screen that
        /// carries a picture or a line of text can be read. 0 lifts it as soon as the room is ready.
        /// </summary>
        public static float MinimumSeconds { get; set; }

        /// <summary>Puts every setting back to what a new game starts with.</summary>
        public static void Reset()
        {
            FrameBudgetMilliseconds = null;
            GraceMilliseconds = 250;
            ShowProgress = true;
            Text = "Loading";
            Background = new Vector4(0.02f, 0.02f, 0.03f, 1f);
            Foreground = new Vector4(0.86f, 0.88f, 0.92f, 1f);
            FadeSeconds = 0.2f;
            MinimumSeconds = 0f;
        }

        /// <summary>
        /// Covers the frame that has just been drawn and puts the loading screen on the cover:
        /// the game's own if one of its behaviours draws one, otherwise the engine's bar and
        /// text. Below full strength it is only the cover, part faded from the room behind it.
        /// </summary>
        public static void Compose(IRenderController renderer, int width, int height, RoomChangeProgress change,
            float strength, ScriptHostSystem scripts = null)
        {
            if (renderer == null || change == null) return;
            Vector4 cover = Background;
            renderer.DrawRect(0, 0, width, height, new RenderColor(cover.X, cover.Y, cover.Z, Math.Clamp(strength, 0f, 1f)));
            renderer.FlushOverlaySprites();
            if (strength < 1f) return;
            renderer.ComposeOverlay(canvas =>
            {
                var hud = new OverlayHudCanvas(canvas, width, height);
                bool drawn = scripts?.DispatchDrawLoadingScreen(hud, change.Progress) == true;
                if (!drawn) Draw(hud, change);
            });
        }

        /// <summary>The engine's own loading screen: a few words and a thin bar, low on the cover.</summary>
        public static void Draw(IHudCanvas hud, RoomChangeProgress change)
        {
            if (hud == null || change == null || !ShowProgress) return;
            float width = MathF.Min(420f, hud.Width * 0.5f), height = MathF.Max(3f, hud.Height / 240f);
            float x = (hud.Width - width) * 0.5f, y = hud.Height * 0.84f;
            Vector4 colour = Foreground;
            if (!string.IsNullOrEmpty(Text))
                hud.TextCentered(Text, hud.Width * 0.5f, y - MathF.Max(26f, hud.Height / 30f), hud.Width * 0.9f,
                    MathF.Max(14f, hud.Height / 54f), colour);
            hud.Rect(x, y, width, height, new Vector4(colour.X, colour.Y, colour.Z, 0.18f));
            hud.Rect(x, y, width * Math.Clamp(change.Progress, 0f, 1f), height, colour);
        }
    }
}
