using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>A problem the sandbox hit while running an object's events.</summary>
public sealed record ObjectSandboxError(string EventId, int Frame, string Message);

/// <summary>
/// Something the run absorbed silently: a value the VM substituted, or a command that could not
/// mean anything without a room. Not an error — but not nothing, either.
/// </summary>
public sealed record ObjectSandboxWarning(string Subject, string Message);

/// <summary>
/// Retained VM and instance context from an Object Sandbox run. Inspector writes go through this
/// binding, so changing a value mutates the existing instance instead of recompiling or replaying
/// Create/Step events.
/// </summary>
public sealed class ObjectSandboxLiveInstance
{
    private static readonly object ContextGate = new();
    private readonly PgslVm _vm;
    private readonly PgslContext _context;
    private readonly IReadOnlyDictionary<string, string> _variableOwners;

    internal ObjectSandboxLiveInstance(
        PgslVm vm,
        PgslContext context,
        IReadOnlyDictionary<string, string> variableOwners)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _variableOwners = new Dictionary<string, string>(
            variableOwners ?? throw new ArgumentNullException(nameof(variableOwners)),
            StringComparer.OrdinalIgnoreCase);
    }

    public int InstanceId => _context.InstanceId;

    public IReadOnlyDictionary<string, object> InstanceValues => WithContext(() =>
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = _context.X,
            ["y"] = _context.Y,
            ["z"] = _context.Z,
            ["hspeed"] = _context.HSpeed,
            ["vspeed"] = _context.VSpeed,
            ["speed"] = _context.Speed,
            ["direction"] = _context.Direction,
            ["friction"] = _context.Friction,
            ["gravity"] = _context.Gravity,
            ["gravity_direction"] = _context.GravityDirection,
            ["sprite_index"] = _context.SpriteIndex ?? string.Empty,
            ["image_index"] = _context.ImageIndex,
            ["image_speed"] = _context.ImageSpeed,
            ["image_alpha"] = _context.ImageAlpha,
            ["image_angle"] = _context.ImageAngle,
            ["image_xscale"] = _context.ImageXScale,
            ["image_yscale"] = _context.ImageYScale,
            ["visible"] = _context.Visible,
            ["depth"] = _context.Depth,
            ["solid"] = _context.Solid,
        });

    public IReadOnlyDictionary<string, object> ScriptVariables => WithContext(() =>
        _vm.GetVariables()
            .Where(pair => IsInspectable(pair.Key, pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Event that first introduced each persistent variable. A variable subsequently changed by
    /// Step remains owned by Create, matching where an author would go to change its declaration.
    /// </summary>
    public IReadOnlyDictionary<string, string> ScriptVariableOwners => _variableOwners;

    public bool TrySetValue(string name, object value) =>
        WithContext(() => _vm.TrySetLiveVariable(name, value));

    public bool TryGetValue(string name, out object value)
    {
        object found = null;
        bool success = WithContext(() =>
        {
            if (_vm.TryGetLiveVariable(name, out object current))
            {
                found = current;
                return true;
            }
            return false;
        });
        value = found!;
        return success;
    }

    private T WithContext<T>(Func<T> action)
    {
        lock (ContextGate)
        {
            PgslContext previousCommands = PgslCommands.BindContext(_context);
            PgslContext previousBridge = VMEngine.Bridge?.GetContext();
            VMEngine.Bridge?.SetContext(_context);
            try
            {
                return action();
            }
            finally
            {
                VMEngine.Bridge?.SetContext(previousBridge);
                PgslCommands.BindContext(previousCommands);
            }
        }
    }

    internal static bool IsInspectable(string name, object value) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.StartsWith("__", StringComparison.Ordinal)
        && !name.StartsWith("@", StringComparison.Ordinal)
        && !name.StartsWith("argument", StringComparison.OrdinalIgnoreCase)
        && value is bool or string or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal;
}

/// <summary>What one sandbox run produced.</summary>
public sealed record ObjectSandboxResult(
    int FramesRun,
    double X,
    double Y,
    double Speed,
    double Direction,
    IReadOnlyDictionary<string, double> Numbers,
    IReadOnlyDictionary<string, string> Strings,
    IReadOnlyList<ObjectSandboxError> Errors,
    IReadOnlyList<string> EventsFired,
    IReadOnlyList<double> AlarmsRemaining,
    double ElapsedMilliseconds,
    IReadOnlyList<ObjectSandboxWarning> Warnings = null,
    ObjectSandboxLiveInstance LiveInstance = null)
{
    /// <summary>Everything the run swallowed. Never null.</summary>
    public IReadOnlyList<ObjectSandboxWarning> Warnings { get; init; } = Warnings ?? [];

    public ObjectSandboxLiveInstance LiveInstance { get; init; } = LiveInstance;

    public bool Ok => Errors.Count == 0;

    /// <summary>True when the run neither failed nor quietly did nothing.</summary>
    public bool Clean => Errors.Count == 0 && Warnings.Count == 0;
}

/// <summary>
/// Runs one object's PGSL events in isolation, without a game, a room, or a renderer.
/// </summary>
/// <remarks>
/// This is what makes the Object Editor's built-in sandbox possible: press play and the object's
/// Create/Step/Draw/Alarm events actually execute, against a real VM and a real instance context,
/// with every drawing call recorded and every script variable readable afterwards.
///
/// Deliberately *not* a miniature game. There is no world, no other instances and no physics — a
/// sandbox that needed those would be as hard to set up as the thing it is meant to shortcut. What it
/// does give is the fast loop that was missing: edit an event, run 60 frames, see whether x actually
/// moved and whether the HUD actually drew, without launching the player.
///
/// Alarms are ticked between Step calls, so an object that arms an alarm in Create and handles it in
/// Alarm0 exercises the whole path here rather than only under F5.
/// </remarks>
public static class ObjectSandbox
{
    /// <summary>Upper bound on frames, so a UI slider cannot ask for a multi-minute run.</summary>
    public const int MaxFrames = 10_000;

    /// <summary>
    /// Run an object's events.
    /// </summary>
    /// <param name="events">Event id → PGSL source, as <see cref="ObjectEventStore.Load"/> returns.</param>
    /// <param name="frames">How many Step frames to run after the begin events.</param>
    /// <param name="surface">Recorder for draw calls; a fresh one is used when null.</param>
    /// <param name="roomWidth">Room width the script sees.</param>
    /// <param name="roomHeight">Room height the script sees.</param>
    public static ObjectSandboxResult Run(
        IReadOnlyDictionary<string, string> events,
        int frames = 60,
        PgslRecordingDrawSurface surface = null,
        double roomWidth = 1280,
        double roomHeight = 720)
    {
        int frameCount = Math.Clamp(frames, 0, MaxFrames);
        List<ObjectSandboxError> errors = [];
        List<string> fired = [];
        List<PgslRuntimeNote> notes = [];
        surface ??= new PgslRecordingDrawSurface();

        if (events is null || events.Count == 0)
        {
            return new ObjectSandboxResult(
                0, 0, 0, 0, 0, new Dictionary<string, double>(), new Dictionary<string, string>(),
                errors, fired, [], 0, []);
        }

        VMEngine.Initialize();
        VMEngine.ClearCompileCache();

        PgslContext ctx = new()
        {
            InstanceId = 1,
            RoomWidth = roomWidth,
            RoomHeight = roomHeight,
            DrawSurface = surface,
            DrawColor = Color.White,
            DrawAlpha = 1.0,
        };

        PgslContext previous = PgslCommands.BindContext(ctx);

        // BindContext alone is NOT enough. Instance variables (x, y, speed, …) are resolved by the
        // VM's engine bridge, which holds its own context reference — so a script assigning x wrote
        // nowhere and the sandbox reported x = 0 for code that had plainly set it. Setting the bridge
        // is what makes instance variables real here.
        VMEngine.Bridge?.SetContext(ctx);

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        // Everything the VM would otherwise absorb — an unset name read as 0, a command that needs
        // a world — is recorded for the duration of the run and reported as warnings. A sandbox
        // that says "OK" for a script whose every meaningful line was silently neutralised is
        // worse than no sandbox: it is a wrong answer delivered confidently.
        using IDisposable collecting = PgslRuntimeDiagnostics.Collect(notes);

        try
        {
            // Compile every event once up front so a syntax error is reported against its own event
            // rather than surfacing mid-run as a mystery.
            Dictionary<string, CompileResult> compiled = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string id, string source) in events)
            {
                if (string.IsNullOrWhiteSpace(source)) continue;

                try
                {
                    CompileResult result = VMEngine.Compile(source);
                    if (result is null)
                    {
                        errors.Add(new ObjectSandboxError(id, -1, "failed to compile"));
                        continue;
                    }

                    compiled[id] = result;
                }
                catch (Exception exception)
                {
                    errors.Add(new ObjectSandboxError(id, -1, exception.Message));
                }
            }

            // One VM for the whole run, so variables persist across events and frames exactly as they
            // do for a real instance.
            PgslVm vm = VMEngine.CreateVm(debug: false);
            Dictionary<string, string> variableOwners = new(StringComparer.OrdinalIgnoreCase);
            bool cleared = false;

            void RunEvent(string id, int frame)
            {
                if (!compiled.TryGetValue(id, out CompileResult result)) return;

                try
                {
                    HashSet<string> variablesBefore = vm.GetVariables().Keys
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    vm.Execute(result.Instructions, result.Constants, clearVariables: !cleared);
                    cleared = true;
                    fired.Add(id);
                    foreach ((string name, object value) in vm.GetVariables())
                    {
                        if (!variablesBefore.Contains(name)
                            && ObjectSandboxLiveInstance.IsInspectable(name, value))
                        {
                            variableOwners.TryAdd(name, id);
                        }
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(new ObjectSandboxError(id, frame, exception.Message));
                }
            }

            // Begin events, in the order a real game fires them.
            RunEvent("GameStart", -1);
            RunEvent("RoomStart", -1);
            RunEvent("Create", -1);

            for (int frame = 0; frame < frameCount; frame++)
            {
                RunEvent("StepBegin", frame);
                RunEvent("Step", frame);
                RunEvent("StepEnd", frame);

                // Alarms count down between frames, then their handlers run — so arming in Create and
                // handling in Alarm0 is exercised here, not only under F5.
                double mask = PgslCommands.TickAlarms(1);
                if (mask > 0)
                {
                    for (int slot = 0; slot < ObjectEventCatalog.AlarmCount; slot++)
                    {
                        if (PgslCommands.AlarmFired(mask, slot)) RunEvent($"Alarm{slot}", frame);
                    }
                }

                RunEvent("Draw", frame);
                RunEvent("DrawGui", frame);
            }

            clock.Stop();

            Dictionary<string, object> vars = vm.GetVariables();
            Dictionary<string, double> numbers = new(StringComparer.Ordinal);
            Dictionary<string, string> strings = new(StringComparer.Ordinal);
            foreach ((string name, object value) in vars)
            {
                if (name.StartsWith("__", StringComparison.Ordinal)) continue;   // internal ds_* storage

                switch (value)
                {
                    case double number: numbers[name] = number; break;
                    case int integer: numbers[name] = integer; break;
                    case bool flag: numbers[name] = flag ? 1 : 0; break;
                    case string text: strings[name] = text; break;
                    default: break;   // handles and objects are not useful to display
                }
            }

            List<double> alarms = [];
            for (int slot = 0; slot < ObjectEventCatalog.AlarmCount; slot++)
            {
                alarms.Add(PgslCommands.GetAlarm(slot));
            }

            // Read the movement variables from the VM table first, falling back to the instance
            // context. A bare `x = 64` in script lands in the VM's own variable table; only the
            // full behaviour host bridges those onto the context each frame, and the sandbox
            // deliberately runs without one. Reading the context alone reported x = 0 for a script
            // that had plainly assigned it.
            double Read(string name, double fallback) =>
                vars.TryGetValue(name, out object value) && value is not null && value is not string
                    ? Convert.ToDouble(value)
                    : fallback;

            return new ObjectSandboxResult(
                frameCount,
                Read("x", ctx.X),
                Read("y", ctx.Y),
                Read("speed", ctx.Speed),
                Read("direction", ctx.Direction),
                numbers,
                strings,
                errors,
                fired,
                alarms,
                clock.Elapsed.TotalMilliseconds,
                Summarise(notes),
                new ObjectSandboxLiveInstance(vm, ctx, variableOwners));
        }
        finally
        {
            VMEngine.Bridge?.SetContext(previous);
            PgslCommands.BindContext(previous);
        }
    }

    /// <summary>
    /// Command categories that cannot do anything without a live game — no world, no instances, no
    /// input device, no audio engine. They do not throw when those are missing; they return a
    /// neutral value, which is why a script built out of them runs "successfully" here and does
    /// nothing. Anything driven by <see cref="PgslContext"/> alone (drawing, alarms, room size,
    /// sprite state, maths, strings) genuinely works in the sandbox and is not listed.
    /// </summary>
    private static readonly HashSet<string> LiveGameCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Collision", "Instances", "Input", "Audio", "Networking", "Particles", "Physics",
    };

    /// <summary>
    /// Folds the VM's raw notes into one line per distinct problem, with a count.
    /// </summary>
    /// <remarks>
    /// A 60-frame run of a Step event produces sixty identical notes; reporting them sixty times
    /// would bury the second problem under the first. The count is kept because "×60" is the
    /// difference between a one-off and something happening every frame.
    /// </remarks>
    private static List<ObjectSandboxWarning> Summarise(List<PgslRuntimeNote> notes)
    {
        Dictionary<string, (string Message, int Count)> unresolved = new(StringComparer.Ordinal);
        Dictionary<string, int> unavailable = new(StringComparer.OrdinalIgnoreCase);

        foreach (PgslRuntimeNote note in notes)
        {
            switch (note.Kind)
            {
                case PgslNoteKind.UnresolvedRead:
                case PgslNoteKind.ReadFailed:
                {
                    string message = note.Kind == PgslNoteKind.UnresolvedRead
                        ? $"'{note.Subject}' was read before anything set it, so it counted as 0."
                        : $"reading '{note.Subject}' failed and counted as 0 — {note.Detail}";
                    unresolved.TryGetValue(note.Subject, out (string Message, int Count) seen);
                    unresolved[note.Subject] = (message, seen.Count + 1);
                    break;
                }

                case PgslNoteKind.CommandCalled:
                {
                    PgslCommandInfo info = PgslCommandRegistry.TryGet(note.Subject);
                    if (info is null || !LiveGameCategories.Contains(info.Category ?? string.Empty))
                    {
                        break;
                    }

                    unavailable.TryGetValue(note.Subject, out int count);
                    unavailable[note.Subject] = count + 1;
                    break;
                }
            }
        }

        List<ObjectSandboxWarning> warnings = [];
        foreach ((string subject, (string message, int count)) in unresolved.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            warnings.Add(new ObjectSandboxWarning(subject, Count(message, count)));
        }

        foreach ((string subject, int count) in unavailable.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            warnings.Add(new ObjectSandboxWarning(
                subject,
                Count(
                    $"{subject} needs a running room, so the sandbox returned its neutral value.",
                    count)));
        }

        return warnings;

        static string Count(string message, int count) =>
            count > 1 ? $"{message} (×{count})" : message;
    }

    /// <summary>Distinct events that fired, with how many times each did.</summary>
    public static IReadOnlyList<(string EventId, int Count)> FireCounts(ObjectSandboxResult result) =>
        result is null
            ? []
            : [.. result.EventsFired
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Select(group => (group.Key, group.Count()))
                .OrderByDescending(entry => entry.Item2)];
}
