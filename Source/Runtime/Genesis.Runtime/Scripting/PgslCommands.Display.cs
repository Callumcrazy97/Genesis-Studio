using System;
using System.Globalization;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// A game's video options (window mode, size, vsync, frame cap) and the wall clock (dates for save
// lists). The host owns the window; these ask it, as a settings menu does.
public static partial class PgslCommands
{
    [PgslCommand("WindowSetFullscreen", "WindowSetFullscreen(enabled)",
        "Fill the screen (a borderless window the size of the display) or go back to a window", "Display")]
    public static void WindowSetFullscreen(bool enabled) =>
        ActiveGameContext?.SetWindowMode(enabled ? WindowMode.Borderless : WindowMode.Windowed);

    [PgslCommand("WindowIsFullscreen", "WindowIsFullscreen() -> bool", "Whether the game fills the screen (borderless or exclusive)", "Display")]
    public static bool WindowIsFullscreen() =>
        ActiveGameContext is { } game && game.WindowMode != WindowMode.Windowed;

    [PgslCommand("WindowSetMode", "WindowSetMode(mode)",
        "\"windowed\", \"borderless\" (fills the screen) or \"fullscreen\" (exclusive); false for any other name", "Display")]
    public static bool WindowSetMode(string mode)
    {
        if (!TryWindowMode(mode, out WindowMode parsed)) return false;
        ActiveGameContext?.SetWindowMode(parsed);
        return true;
    }

    [PgslCommand("WindowGetMode", "WindowGetMode() -> string", "\"windowed\", \"borderless\" or \"fullscreen\"", "Display")]
    public static string WindowGetMode() => (ActiveGameContext?.WindowMode ?? WindowMode.Windowed) switch
    {
        WindowMode.Borderless => "borderless",
        WindowMode.Fullscreen => "fullscreen",
        _ => "windowed",
    };

    [PgslCommand("WindowSetSize", "WindowSetSize(width, height)", "Resize a windowed game's window, in pixels", "Display")]
    public static void WindowSetSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 160 || height < 120) return;
        ActiveGameContext?.SetResolution((int)Math.Min(width, 16384), (int)Math.Min(height, 16384));
    }

    [PgslCommand("WindowSetVSync", "WindowSetVSync(enabled)", "Wait for the display's refresh before each frame (no tearing)", "Display")]
    public static void WindowSetVSync(bool enabled) => ActiveGameContext?.SetVSync(enabled);

    [PgslCommand("WindowGetVSync", "WindowGetVSync() -> bool", "Whether vsync is on", "Display")]
    public static bool WindowGetVSync() => ActiveGameContext?.VSyncEnabled ?? true;

    [PgslCommand("GameSetMaxFps", "GameSetMaxFps(fps)", "Cap the frame rate (0 = no cap; vsync still applies)", "Display")]
    public static void GameSetMaxFps(double fps)
    {
        if (double.IsFinite(fps)) ActiveGameContext?.SetTargetFps((int)Math.Clamp(Math.Round(fps), 0, 1000));
    }

    [PgslCommand("GameGetMaxFps", "GameGetMaxFps() -> number", "The frame-rate cap (0 = none)", "Display")]
    public static double GameGetMaxFps() => ActiveGameContext?.TargetFps ?? 0;

    private static bool TryWindowMode(string mode, out WindowMode parsed)
    {
        switch (mode?.Trim().ToLowerInvariant())
        {
            case "windowed": case "window": parsed = WindowMode.Windowed; return true;
            case "borderless": parsed = WindowMode.Borderless; return true;
            case "fullscreen": case "exclusive": parsed = WindowMode.Fullscreen; return true;
            default: parsed = WindowMode.Windowed; return false;
        }
    }

    // ── The wall clock ───────────────────────────────────────────────────────

    [PgslCommand("DateNow", "DateNow() -> number",
        "Now, as seconds since 1 January 1970 (UTC): store it in a save, then read its parts with the Date commands", "Time")]
    public static double DateNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    [PgslCommand("DateYear", "DateYear(time) -> number", "The year of a DateNow time, in local time", "Time")]
    public static double DateYear(double time) => LocalTime(time).Year;

    [PgslCommand("DateMonth", "DateMonth(time) -> number", "The month of a DateNow time, 1 to 12, in local time", "Time")]
    public static double DateMonth(double time) => LocalTime(time).Month;

    [PgslCommand("DateDay", "DateDay(time) -> number", "The day of the month of a DateNow time, 1 to 31, in local time", "Time")]
    public static double DateDay(double time) => LocalTime(time).Day;

    [PgslCommand("DateHour", "DateHour(time) -> number", "The hour of a DateNow time, 0 to 23, in local time", "Time")]
    public static double DateHour(double time) => LocalTime(time).Hour;

    [PgslCommand("DateMinute", "DateMinute(time) -> number", "The minute of a DateNow time, 0 to 59", "Time")]
    public static double DateMinute(double time) => LocalTime(time).Minute;

    [PgslCommand("DateSecond", "DateSecond(time) -> number", "The second of a DateNow time, 0 to 59", "Time")]
    public static double DateSecond(double time) => LocalTime(time).Second;

    [PgslCommand("DateWeekday", "DateWeekday(time) -> number", "The day of the week of a DateNow time, 0 Sunday to 6 Saturday, in local time", "Time")]
    public static double DateWeekday(double time) => (int)LocalTime(time).DayOfWeek;

    [PgslCommand("DateText", "DateText(time, format) -> string",
        "A DateNow time as text in local time: format such as \"yyyy-MM-dd HH:mm\" or \"d MMM yyyy\" (empty for a short date and time)", "Time")]
    public static string DateText(double time, string format)
    {
        DateTime local = LocalTime(time);
        try
        {
            return string.IsNullOrWhiteSpace(format)
                ? local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : local.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }

    private static DateTime LocalTime(double seconds)
    {
        if (!double.IsFinite(seconds)) seconds = 0;
        double clamped = Math.Clamp(seconds, -62135596800d, 253402300799d);
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(clamped * 1000)).LocalDateTime;
    }
}
