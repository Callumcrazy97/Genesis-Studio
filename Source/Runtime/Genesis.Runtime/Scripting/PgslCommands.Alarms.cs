using System;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Alarms and user-value slots.
//
// PgslContext already carried Alarm0..Alarm11 and UserDefined0..UserDefined11 as plain properties;
// what was missing was any way to reach them from script, and anything to count them down. There is
// no indexer on the context, hence the switch-based accessors.
//
// Convention: an alarm holds a countdown in FRAMES. -1 means "not set". TickAlarms decrements every
// armed alarm and returns a bitmask of the ones that reached zero this tick, so the host dispatches
// the matching Alarm event and the alarm disarms itself — the GameMaker behaviour designers expect.
public static partial class PgslCommands
{
    #region Alarms

    /// <summary>How many alarm slots every instance has.</summary>
    public const int AlarmSlotCount = 12;

    /// <summary>How many user-value slots every instance has.</summary>
    public const int UserValueSlotCount = 12;

    [PgslCommand("SetAlarm", "SetAlarm(index, frames)", "Arm alarm 0-11 to fire after a number of frames", "Alarms")]
    public static void SetAlarm(double index, double frames)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        WriteAlarm(ctx, (int)index, frames);
    }

    [PgslCommand("GetAlarm", "GetAlarm(index) -> number", "Frames remaining on alarm 0-11, or -1 when unset", "Alarms")]
    public static double GetAlarm(double index)
    {
        PgslContext ctx = GetContext();
        return ctx is null ? -1 : ReadAlarm(ctx, (int)index);
    }

    [PgslCommand("AlarmIsSet", "AlarmIsSet(index) -> bool", "True while alarm 0-11 is counting down", "Alarms")]
    public static bool AlarmIsSet(double index) => GetAlarm(index) >= 0;

    [PgslCommand("ClearAlarm", "ClearAlarm(index)", "Disarm one alarm", "Alarms")]
    public static void ClearAlarm(double index)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        WriteAlarm(ctx, (int)index, -1);
    }

    [PgslCommand("ClearAllAlarms", "ClearAllAlarms()", "Disarm every alarm", "Alarms")]
    public static void ClearAllAlarms()
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        for (int index = 0; index < AlarmSlotCount; index++)
        {
            WriteAlarm(ctx, index, -1);
        }
    }

    [PgslCommand("AlarmCountActive", "AlarmCountActive() -> number", "How many alarms are currently armed", "Alarms")]
    public static double AlarmCountActive()
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return 0;

        int active = 0;
        for (int index = 0; index < AlarmSlotCount; index++)
        {
            if (ReadAlarm(ctx, index) >= 0) active++;
        }

        return active;
    }

    /// <summary>
    /// Advance every armed alarm by <paramref name="frames"/> and disarm the ones that reached zero.
    /// </summary>
    /// <returns>
    /// A bitmask (bit N = alarm N) of the alarms that fired, as a double so PGSL can hold it. The
    /// host turns those bits into Alarm event dispatches; the alarm is already disarmed by then, so
    /// an event handler is free to re-arm it without the tick immediately clearing it again.
    /// </returns>
    [PgslCommand("TickAlarms", "TickAlarms(frames) -> number", "Count alarms down; returns a bitmask of those that fired", "Alarms")]
    public static double TickAlarms(double frames)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return 0;

        double step = frames <= 0 ? 1 : frames;
        int fired = 0;
        for (int index = 0; index < AlarmSlotCount; index++)
        {
            double remaining = ReadAlarm(ctx, index);
            if (remaining < 0) continue;

            remaining -= step;
            if (remaining <= 0)
            {
                fired |= 1 << index;
                WriteAlarm(ctx, index, -1);
            }
            else
            {
                WriteAlarm(ctx, index, remaining);
            }
        }

        return fired;
    }

    [PgslCommand("AlarmFired", "AlarmFired(mask, index) -> bool", "Test one alarm bit in a TickAlarms mask", "Alarms")]
    public static bool AlarmFired(double mask, double index)
    {
        int slot = (int)index;
        if (slot is < 0 or >= AlarmSlotCount) return false;
        return ((long)mask & (1L << slot)) != 0;
    }

    [PgslCommand("SetUserValue", "SetUserValue(index, value)", "Write user slot 0-11", "Alarms")]
    public static void SetUserValue(double index, double value)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        WriteUserValue(ctx, (int)index, value);
    }

    [PgslCommand("GetUserValue", "GetUserValue(index) -> number", "Read user slot 0-11", "Alarms")]
    public static double GetUserValue(double index)
    {
        PgslContext ctx = GetContext();
        return ctx is null ? 0 : ReadUserValue(ctx, (int)index);
    }

    [PgslCommand("EventUser", "EventUser(index)", "Run this object's User Event 0-7 after the current handler returns", "Events")]
    public static void EventUser(double index)
    {
        int slot = (int)index;
        if (slot is < 0 or >= ObjectEventCatalog.UserEventCount) return;
        GetContext()?.UserEventCallback?.Invoke(slot);
    }

    // ── Slot access ─────────────────────────────────────────────────────────────
    // PgslContext exposes Alarm0..Alarm11 as separate properties rather than an array, so these
    // switches are the only way to index them. Out-of-range slots are ignored rather than throwing:
    // a script typo must not kill the frame.

    private static double ReadAlarm(PgslContext ctx, int index) => index switch
    {
        0 => ctx.Alarm0,
        1 => ctx.Alarm1,
        2 => ctx.Alarm2,
        3 => ctx.Alarm3,
        4 => ctx.Alarm4,
        5 => ctx.Alarm5,
        6 => ctx.Alarm6,
        7 => ctx.Alarm7,
        8 => ctx.Alarm8,
        9 => ctx.Alarm9,
        10 => ctx.Alarm10,
        11 => ctx.Alarm11,
        _ => -1,
    };

    private static void WriteAlarm(PgslContext ctx, int index, double value)
    {
        switch (index)
        {
            case 0: ctx.Alarm0 = value; break;
            case 1: ctx.Alarm1 = value; break;
            case 2: ctx.Alarm2 = value; break;
            case 3: ctx.Alarm3 = value; break;
            case 4: ctx.Alarm4 = value; break;
            case 5: ctx.Alarm5 = value; break;
            case 6: ctx.Alarm6 = value; break;
            case 7: ctx.Alarm7 = value; break;
            case 8: ctx.Alarm8 = value; break;
            case 9: ctx.Alarm9 = value; break;
            case 10: ctx.Alarm10 = value; break;
            case 11: ctx.Alarm11 = value; break;
            default: break;   // unknown slot: ignore rather than fault the script
        }
    }

    private static double ReadUserValue(PgslContext ctx, int index) => index switch
    {
        0 => ctx.UserDefined0,
        1 => ctx.UserDefined1,
        2 => ctx.UserDefined2,
        3 => ctx.UserDefined3,
        4 => ctx.UserDefined4,
        5 => ctx.UserDefined5,
        6 => ctx.UserDefined6,
        7 => ctx.UserDefined7,
        8 => ctx.UserDefined8,
        9 => ctx.UserDefined9,
        10 => ctx.UserDefined10,
        11 => ctx.UserDefined11,
        _ => 0,
    };

    private static void WriteUserValue(PgslContext ctx, int index, double value)
    {
        switch (index)
        {
            case 0: ctx.UserDefined0 = value; break;
            case 1: ctx.UserDefined1 = value; break;
            case 2: ctx.UserDefined2 = value; break;
            case 3: ctx.UserDefined3 = value; break;
            case 4: ctx.UserDefined4 = value; break;
            case 5: ctx.UserDefined5 = value; break;
            case 6: ctx.UserDefined6 = value; break;
            case 7: ctx.UserDefined7 = value; break;
            case 8: ctx.UserDefined8 = value; break;
            case 9: ctx.UserDefined9 = value; break;
            case 10: ctx.UserDefined10 = value; break;
            case 11: ctx.UserDefined11 = value; break;
            default: break;   // unknown slot: ignore rather than fault the script
        }
    }

    #endregion
}
