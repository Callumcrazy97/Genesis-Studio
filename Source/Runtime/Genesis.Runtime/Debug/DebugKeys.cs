using System;

namespace Genesis.Runtime.Debugger
{
    /// <summary>
    /// Whether the debug screen's keys act: F6 (the screen), F7 (its full panel), F8 (record a
    /// profile), P (pause) and N (step a frame while paused).
    /// </summary>
    /// <remarks>
    /// <para>
    /// They are for whoever is making the game, so they act only when the game was started to be
    /// worked on: Studio's Debug (<c>--debug</c>), Studio's Run (which passes
    /// <c>--live-reload</c> and has always offered the debug screen on F6), or any launch with
    /// <see cref="EnvironmentVariable"/> set to 1. An exported game started normally has them off,
    /// so a player typing "p" in a chat line does not pause the game. The same variable set to 0
    /// turns them off even in Studio.
    /// </para>
    /// <para>
    /// A game can turn them off for a while (<c>DebugKeysSetEnabled(false)</c>), for example while
    /// a chat line has the keyboard; it cannot turn them on where the launch does not allow them.
    /// While a game is reading typed text (it called <c>KeyboardTextTake</c> this frame or the
    /// last), P and N are letters, not debug keys; F6, F7 and F8 type nothing and still act.
    /// </para>
    /// <para>
    /// A profile recorded for the whole run (<c>GENESIS_PROFILE=1</c> or <c>--profile</c>) needs
    /// no key and is not affected.
    /// </para>
    /// </remarks>
    public static class DebugKeys
    {
        /// <summary>1 turns the debug keys on for any launch, 0 turns them off for any launch.</summary>
        public const string EnvironmentVariable = "GENESIS_DEBUG_KEYS";

        /// <summary>True when the way the game was started allows the debug keys. Off until the Player says.</summary>
        public static bool LaunchAllows { get; set; }

        /// <summary>False while the game has turned the debug keys off.</summary>
        public static bool GameAllows { get; set; } = true;

        /// <summary>True when the debug keys act this frame.</summary>
        public static bool Active => LaunchAllows && GameAllows;

        /// <summary>
        /// Whether a launch allows the debug keys: a debug run or Studio's Run does, unless
        /// <see cref="EnvironmentVariable"/> says otherwise; 1 there allows them anywhere.
        /// </summary>
        public static bool AllowedFor(bool debugRun, bool studioPlay, out string reason)
        {
            string setting = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();
            if (setting == "1") { reason = EnvironmentVariable + "=1"; return true; }
            if (setting == "0") { reason = EnvironmentVariable + "=0"; return false; }
            if (debugRun) { reason = "a debug run"; return true; }
            if (studioPlay) { reason = "Studio's Run"; return true; }
            reason = "not a debug run or Studio's Run; " + EnvironmentVariable + "=1 turns them on";
            return false;
        }

        /// <summary>A new game: the launch decides again and the game has not turned them off.</summary>
        public static void Reset()
        {
            LaunchAllows = false;
            GameAllows = true;
        }
    }
}
