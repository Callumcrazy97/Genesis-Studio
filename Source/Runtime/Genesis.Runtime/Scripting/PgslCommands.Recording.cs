using System;
using System.Drawing;
using Genesis.Runtime.Recording;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Gameplay recording and replays (killcams): a game records to a video or pictures, keeps the last
// few seconds in a replay buffer, saves them, or plays them back in game. Keys are the game's own
// choice; nothing is bound by default and nothing runs until one of these is called.
public static partial class PgslCommands
{
    [PgslCommand("RecordStart", "RecordStart(name, format) -> bool",
        "Start recording the game: format \"mp4\" (the default) makes Recordings/name.mp4 when RecordStop is called; \"png\" saves every frame as a picture in Recordings/name. Up to 30 frames a second; false when already recording",
        "Recording")]
    public static bool RecordStart(string name = "", string format = "mp4") =>
        GameRecorder.StartRecording(name, string.Equals(format?.Trim(), "png", StringComparison.OrdinalIgnoreCase));

    [PgslCommand("RecordStop", "RecordStop() -> path",
        "Stop recording and return where the video (or folder of pictures) goes; the video is finished in the background a moment later. \"\" when nothing was recording",
        "Recording")]
    public static string RecordStop() => GameRecorder.StopRecording();

    [PgslCommand("RecordIsRecording", "RecordIsRecording() -> bool", "True while the game is being recorded", "Recording")]
    public static bool RecordIsRecording() => GameRecorder.IsRecording;

    [PgslCommand("ReplayBufferEnable", "ReplayBufferEnable(seconds, fps, scale)",
        "Keep the last seconds of play (1 to 60) in memory, at fps frames a second (5 to 60) and scale of the screen size (0.1 to 1; 0.5 is half): what ReplayPlay and ReplayBufferSave use. Costs a frame readback at that rate",
        "Recording")]
    public static void ReplayBufferEnable(double seconds = 10, double fps = 30, double scale = 0.5) =>
        GameRecorder.EnableBuffer(Finite(seconds, 10), Finite(fps, 30), Finite(scale, 0.5));

    [PgslCommand("ReplayBufferDisable", "ReplayBufferDisable()", "Stop keeping a replay buffer and free it", "Recording")]
    public static void ReplayBufferDisable() => GameRecorder.DisableBuffer();

    [PgslCommand("ReplayBufferSeconds", "ReplayBufferSeconds() -> number", "How many seconds the replay buffer holds right now", "Recording")]
    public static double ReplayBufferSeconds() => GameRecorder.BufferedSeconds();

    [PgslCommand("ReplayBufferSave", "ReplayBufferSave(name, seconds) -> path",
        "Save the last seconds of the replay buffer as Recordings/name.mp4 (made in the background); \"\" when the buffer is empty",
        "Recording")]
    public static string ReplayBufferSave(string name = "", double seconds = 10) => GameRecorder.SaveBuffer(name, Finite(seconds, 10));

    [PgslCommand("ReplayPlay", "ReplayPlay(secondsBack, speed) -> bool",
        "Play back the last secondsBack of the replay buffer at speed (1 is real time, 0.5 slow motion); draw it with ReplayDraw. The buffer pauses while it plays. False when the buffer is empty",
        "Recording")]
    public static bool ReplayPlay(double secondsBack = 3, double speed = 1) => GameRecorder.Play(Finite(secondsBack, 3), Finite(speed, 1));

    [PgslCommand("ReplayStop", "ReplayStop()", "Stop a replay before its end", "Recording")]
    public static void ReplayStop() => GameRecorder.StopPlayback();

    [PgslCommand("ReplayIsPlaying", "ReplayIsPlaying() -> bool", "True while a replay is playing", "Recording")]
    public static bool ReplayIsPlaying() => GameRecorder.IsPlaying;

    [PgslCommand("ReplayProgress", "ReplayProgress() -> number", "How far the replay has played, 0 to 1 (1 when none is playing)", "Recording")]
    public static double ReplayProgress() => GameRecorder.PlaybackProgress();

    [PgslCommand("ReplayDraw", "ReplayDraw(x, y, width, height) -> bool",
        "Draw the replay's current frame into a rectangle, in Draw GUI: full screen for a killcam, or small for picture-in-picture. False when no replay is playing",
        "Recording")]
    public static bool ReplayDraw(double x, double y, double width, double height)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || !Finite3(x, y, width) || !double.IsFinite(height) || width <= 0 || height <= 0) return false;
        int texture = GameRecorder.CurrentTexture();
        if (texture == 0) return false;
        surface.DrawScriptTexture(texture, new RectangleF(0f, 0f, 1f, 1f), new RectangleF((float)x, (float)y, (float)width, (float)height),
            0f, Color.White, 1f);
        return true;
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
