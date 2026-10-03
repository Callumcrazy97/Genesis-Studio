using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// What belongs to a play session rather than to a room or an instance: named values that every
/// room's scripts can read, a clock that runs in real time, and a way to end the game.
/// </summary>
public static partial class PgslCommands
{
    private static readonly Dictionary<string, object> GlobalValues = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Stopwatch SessionClock = Stopwatch.StartNew();
    private const int MaxGlobals = 4096;

    /// <summary>Set by the Player: ends the game cleanly. Null where there is no game to end (editor previews).</summary>
    public static Action GameQuitHandler { get; set; }

    /// <summary>True once a script asked for the game to end, for hosts and tests to observe.</summary>
    public static bool GameQuitRequested { get; private set; }

    /// <summary>Starts a new play session: no global values, the clock at zero, no quit pending.</summary>
    public static void ResetSession()
    {
        lock (GlobalValues) GlobalValues.Clear();
        SessionClock.Restart();
        GameQuitRequested = false;
    }

    [PgslCommand("GlobalSet", "GlobalSet(name, value)", "Store a number every room's scripts can read for the rest of the game", "Game")]
    public static void GlobalSet(string name, double value) => SetGlobal(name, value);

    [PgslCommand("GlobalSetString", "GlobalSetString(name, text)", "Store text every room's scripts can read for the rest of the game", "Game")]
    public static void GlobalSetString(string name, string text) => SetGlobal(name, text ?? string.Empty);

    [PgslCommand("GlobalGet", "GlobalGet(name) -> number", "A global number; 0 when unset", "Game")]
    public static double GlobalGet(string name)
    {
        object value = ReadGlobal(name);
        return value switch
        {
            double number => number,
            string text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0,
            _ => 0,
        };
    }

    [PgslCommand("GlobalGetString", "GlobalGetString(name) -> string", "A global as text; empty when unset", "Game")]
    public static string GlobalGetString(string name)
    {
        object value = ReadGlobal(name);
        return value switch
        {
            string text => text,
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    [PgslCommand("GlobalExists", "GlobalExists(name) -> bool", "Whether a global has been set", "Game")]
    public static bool GlobalExists(string name) => ReadGlobal(name) is not null;

    [PgslCommand("GlobalDelete", "GlobalDelete(name)", "Forget a global", "Game")]
    public static void GlobalDelete(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (GlobalValues) GlobalValues.Remove(name.Trim());
    }

    [PgslCommand("TimeMs", "TimeMs() -> number", "Milliseconds of real time since the game started, unaffected by pause or time scale", "Game")]
    public static double TimeMs() => SessionClock.Elapsed.TotalMilliseconds;

    [PgslCommand("GameEnd", "GameEnd()", "End the game and close its window", "Game")]
    public static void GameEnd()
    {
        GameQuitRequested = true;
        GameQuitHandler?.Invoke();
    }

    [PgslCommand("GameQuit", "GameQuit()", "End the game and close its window (the same as GameEnd)", "Game")]
    public static void GameQuit() => GameEnd();

    private static void SetGlobal(string name, object value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string key = name.Trim();
        lock (GlobalValues)
        {
            if (!GlobalValues.ContainsKey(key) && GlobalValues.Count >= MaxGlobals) return;
            GlobalValues[key] = value;
        }
    }

    private static object ReadGlobal(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (GlobalValues) return GlobalValues.TryGetValue(name.Trim(), out object value) ? value : null;
    }
}
