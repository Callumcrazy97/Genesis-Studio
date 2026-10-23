using Genesis.Runtime.Diagnostics;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Where the frames' time goes, part by part (FrameBudget).
public static partial class PgslCommands
{
    [PgslCommand("FrameBudgetEnable", "FrameBudgetEnable(enabled)",
        "Start or stop counting each part of the frame (every subsystem's update and draws, physics, scripts' events) against the frame's budget; GENESIS_FRAME_BUDGET=1 starts it with the game and writes Debug/Logs/frame_budget.txt at the end",
        "Debug")]
    public static void FrameBudgetEnable(bool enabled) => FrameBudget.Enabled = enabled;

    [PgslCommand("FrameBudgetSetFrame", "FrameBudgetSetFrame(milliseconds)", "The whole frame's budget (default 16.67, a 60 fps frame)", "Debug")]
    public static void FrameBudgetSetFrame(double milliseconds)
    {
        if (double.IsFinite(milliseconds) && milliseconds > 0) FrameBudget.FrameMilliseconds = milliseconds;
    }

    [PgslCommand("FrameBudgetSet", "FrameBudgetSet(part, milliseconds)",
        "A budget for one part of the frame, named as the report names it (0 for none); frames where it takes longer are counted", "Debug")]
    public static void FrameBudgetSet(string part, double milliseconds) => FrameBudget.SetBudget(part, milliseconds);

    [PgslCommand("FrameBudgetReport", "FrameBudgetReport(count) -> string",
        "The frame's average and longest time, then the parts that took longest: part | average ms | longest ms | share of budget % | budget ms | frames over", "Debug")]
    public static string FrameBudgetReport(double count) => FrameBudget.Report(double.IsFinite(count) ? (int)System.Math.Clamp(count, 1, 200) : 20);

    [PgslCommand("FrameBudgetPartMs", "FrameBudgetPartMs(part) -> number", "A part's average milliseconds a frame so far (0 when not seen)", "Debug")]
    public static double FrameBudgetPartMs(string part) => FrameBudget.AverageMilliseconds(part);

    [PgslCommand("FrameBudgetReset", "FrameBudgetReset()", "Forget the frame budget's counts", "Debug")]
    public static void FrameBudgetReset() => FrameBudget.Reset();
}
