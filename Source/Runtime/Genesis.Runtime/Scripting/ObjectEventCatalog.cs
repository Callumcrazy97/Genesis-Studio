using System;
using System.Collections.Generic;
using System.Linq;

namespace Genesis.Runtime.Scripting;

/// <summary>When during a frame (or a lifetime) an event fires.</summary>
public enum ObjectEventPhase
{
    /// <summary>Once, when the instance or room or game begins.</summary>
    Begin,

    /// <summary>Every frame, before drawing.</summary>
    Step,

    /// <summary>Every frame, during the draw pass. World space.</summary>
    Draw,

    /// <summary>Every frame, during the draw pass. Screen space, on top of everything.</summary>
    DrawGui,

    /// <summary>In response to input.</summary>
    Input,

    /// <summary>When two instances touch.</summary>
    Collision,

    /// <summary>When a countdown reaches zero.</summary>
    Alarm,

    /// <summary>Only when the script asks for it.</summary>
    UserDefined,

    /// <summary>Once, when the instance or room or game ends.</summary>
    End,
}

/// <summary>One event an object can handle.</summary>
public sealed record ObjectEventDefinition(
    string Id,
    string Label,
    string Category,
    ObjectEventPhase Phase,
    string Description)
{
    /// <summary>Filename this event's script occupies inside the object's folder.</summary>
    public string FileName => Id + ".pgsl";
}

/// <summary>
/// The object event model, shared by the editor and the runtime dispatcher.
/// </summary>
/// <remarks>
/// GameMaker semantics, per the T2 decision: <b>objects own their events</b> (code stored per-event
/// inside the object's own folder) and <b>PGSL Scripts are a separate library of reusable functions</b>
/// called ad-hoc by name. Scripts carry no event tag and no ordering declaration; ordering within an
/// event is simply the order you call things.
///
/// This catalogue is the single source of truth. The Object Editor builds its category tree from it
/// and the runtime resolves handlers from it, so an event cannot exist in the UI but not in dispatch —
/// which is precisely the class of drift behind NEXT-044 and NEXT-046, where the editor's naming
/// convention and the runtime's lookup disagreed and every HUD silently stopped rendering.
///
/// The set is taken from the previous Python-era editor (8 categories, ~35 events) rather than
/// invented, because it is what designers coming from GameMaker expect to find.
/// </remarks>
public static class ObjectEventCatalog
{
    /// <summary>Alarm slots, matching <see cref="PgslCommands.AlarmSlotCount"/>.</summary>
    public const int AlarmCount = 12;

    /// <summary>User-defined event slots.</summary>
    public const int UserEventCount = 8;

    private static readonly ObjectEventDefinition[] Definitions = Build();

    /// <summary>Every event, in category order.</summary>
    public static IReadOnlyList<ObjectEventDefinition> All => Definitions;

    /// <summary>Category names in display order.</summary>
    public static IReadOnlyList<string> Categories { get; } =
        ["Create", "Step", "Draw", "Input", "Collision", "Alarm", "User Defined", "End"];

    /// <summary>Events in one category, or empty when the category is unknown.</summary>
    public static IReadOnlyList<ObjectEventDefinition> InCategory(string category) =>
        [.. Definitions.Where(d => string.Equals(d.Category, category, StringComparison.Ordinal))];

    /// <summary>Look an event up by its id (which is also its filename stem).</summary>
    public static ObjectEventDefinition Find(string id) =>
        Definitions.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the id names a real event.</summary>
    public static bool Exists(string id) => Find(id) is not null;

    /// <summary>Alarm event id for a slot, or null when the slot is out of range.</summary>
    public static string AlarmEventId(int slot) => slot is >= 0 and < AlarmCount ? $"Alarm{slot}" : null;

    /// <summary>Every event that fires during the draw pass, world or screen space.</summary>
    public static IReadOnlyList<ObjectEventDefinition> DrawEvents =>
        [.. Definitions.Where(d => d.Phase is ObjectEventPhase.Draw or ObjectEventPhase.DrawGui)];

    private static ObjectEventDefinition[] Build()
    {
        List<ObjectEventDefinition> events =
        [
            new("Create", "Create", "Create", ObjectEventPhase.Begin,
                "Once, when the instance is created and positioned."),
            new("RoomStart", "Room Start", "Create", ObjectEventPhase.Begin,
                "When the room containing this instance begins."),
            new("GameStart", "Game Start", "Create", ObjectEventPhase.Begin,
                "Once, when the game starts."),

            new("Step", "Step", "Step", ObjectEventPhase.Step,
                "Every frame. The main place gameplay logic lives."),
            new("StepBegin", "Step Begin", "Step", ObjectEventPhase.Step,
                "Every frame, before all Step events."),
            new("StepEnd", "Step End", "Step", ObjectEventPhase.Step,
                "Every frame, after all Step events."),

            new("Draw", "Draw", "Draw", ObjectEventPhase.Draw,
                "Every frame, in world space. Replaces the default sprite draw."),
            new("DrawGui", "Draw GUI", "Draw", ObjectEventPhase.DrawGui,
                "Every frame, in screen space on top of the world. Where a HUD belongs."),

            new("KeyPressed", "Key — Pressed", "Input", ObjectEventPhase.Input,
                "The frame a key goes down."),
            new("KeyHeld", "Key — Held", "Input", ObjectEventPhase.Input,
                "Every frame a key stays down."),
            new("KeyReleased", "Key — Released", "Input", ObjectEventPhase.Input,
                "The frame a key comes up."),
            new("MouseLeftPressed", "Mouse Left — Pressed", "Input", ObjectEventPhase.Input,
                "The frame the left button goes down."),
            new("MouseRightPressed", "Mouse Right — Pressed", "Input", ObjectEventPhase.Input,
                "The frame the right button goes down."),
            new("MouseEnter", "Mouse Enter", "Input", ObjectEventPhase.Input,
                "When the cursor moves onto this instance."),
            new("MouseLeave", "Mouse Leave", "Input", ObjectEventPhase.Input,
                "When the cursor moves off this instance."),

            new("Collision", "Collision — Any", "Collision", ObjectEventPhase.Collision,
                "When this instance touches any other. The other instance is available as Other."),

            new("Destroy", "Destroy", "End", ObjectEventPhase.End,
                "Once, when the instance is destroyed."),
            new("RoomEnd", "Room End", "End", ObjectEventPhase.End,
                "When the room containing this instance ends."),
            new("GameEnd", "Game End", "End", ObjectEventPhase.End,
                "Once, when the game shuts down."),
        ];

        for (int slot = 0; slot < AlarmCount; slot++)
        {
            events.Add(new ObjectEventDefinition(
                $"Alarm{slot}",
                $"Alarm {slot}",
                "Alarm",
                ObjectEventPhase.Alarm,
                $"When alarm {slot} counts down to zero. Arm it with SetAlarm({slot}, frames)."));
        }

        for (int slot = 0; slot < UserEventCount; slot++)
        {
            events.Add(new ObjectEventDefinition(
                $"UserEvent{slot}",
                $"User Event {slot}",
                "User Defined",
                ObjectEventPhase.UserDefined,
                $"Runs after script calls EventUser({slot})."));
        }

        return [.. events];
    }
}
