using System;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>Native opt-in 2D effects. Coordinates and sizes are authored room pixels.</summary>
public static partial class PgslCommands
{
    private static RoomEffects2D Effects2D => RoomEffects2D.For(ActiveGameContext?.Room);

    [PgslCommand("InstanceSelf", "InstanceSelf() -> number", "The calling object's live instance ID", "Instances")]
    public static double InstanceSelf() => GetContext()?.InstanceId ?? 0;

    [PgslCommand("Light2DAmbient", "Light2DAmbient(amount, shadowR, shadowG, shadowB)",
        "Enable room-local 2D illumination; colour channels are 0..1. Other rooms are unaffected.", "Lighting 2D")]
    public static void Light2DAmbient(double amount, double r, double g, double b) =>
        Effects2D?.SetAmbient((float)amount, (float)r, (float)g, (float)b);

    [PgslCommand("Light2DPoint", "Light2DPoint(id, x, y, radius, intensity, r, g, b)",
        "Create/update a bounded, shadow-tested 2D point light; zero radius removes it", "Lighting 2D")]
    public static void Light2DPoint(int id, double x, double y, double radius, double intensity, double r, double g, double b) =>
        Effects2D?.SetLight(id, (float)x, (float)y, (float)radius, (float)intensity, (float)r, (float)g, (float)b);

    [PgslCommand("Light2DCone", "Light2DCone(id, x, y, direction, spread, radius, intensity, r, g, b)",
        "Shadow-tested torch cone. Direction is 0=right, 90=up; spread is the full angle.", "Lighting 2D")]
    public static void Light2DCone(int id, double x, double y, double direction, double spread, double radius,
        double intensity, double r, double g, double b) =>
        Effects2D?.SetLight(id, (float)x, (float)y, (float)radius, (float)intensity, (float)r, (float)g, (float)b, (float)direction, (float)spread);

    [PgslCommand("Light2DRemove", "Light2DRemove(id)", "Remove a room-local 2D light", "Lighting 2D")]
    public static void Light2DRemove(int id) => Effects2D?.RemoveLight(id);

    [PgslCommand("Light2DBlocker", "Light2DBlocker(id, x, y, width, height)",
        "Register/update an axis-aligned light blocker; nonpositive size removes it", "Lighting 2D")]
    public static void Light2DBlocker(int id, double x, double y, double width, double height) =>
        Effects2D?.SetObstacle(id, (float)x, (float)y, (float)width, (float)height);

    [PgslCommand("Light2DLineClear", "Light2DLineClear(x1, y1, x2, y2) -> bool",
        "Visibility segment against the same authored blockers used by 2D shadows", "Lighting 2D")]
    public static bool Light2DLineClear(double x1, double y1, double x2, double y2) =>
        Effects2D?.LineClear((float)x1, (float)y1, (float)x2, (float)y2) ?? true;

    [PgslCommand("Shadow2DContact", "Shadow2DContact(id, x, y, width, height, opacity)",
        "Soft elliptical ground/contact shadow; zero opacity removes it", "Lighting 2D")]
    public static void Shadow2DContact(int id, double x, double y, double width, double height, double opacity) =>
        Effects2D?.SetContact(id, (float)x, (float)y, (float)width, (float)height, (float)opacity);

    [PgslCommand("Particle2DBurst", "Particle2DBurst(asset, x, y, count, direction)",
        "Burst canonical Particle Editor emitters into a bounded room-owned 2D pool", "Particles 2D")]
    public static void Particle2DBurst(string asset, double x, double y, int count, double direction) =>
        Effects2D?.Emit(ProjectPath, asset, (float)x, (float)y, count, (float)direction);

    [PgslCommand("Particle2DFlow", "Particle2DFlow(asset, fromX, fromY, toX, toY, count)",
        "Curved, converging particles from a source to a target without allocating entities", "Particles 2D")]
    public static void Particle2DFlow(string asset, double fromX, double fromY, double toX, double toY, int count) =>
        Effects2D?.Emit(ProjectPath, asset, (float)fromX, (float)fromY, count, 0, true, (float)toX, (float)toY);

    [PgslCommand("Effects2DPause", "Effects2DPause(paused)", "Freeze/unfreeze room particle time", "Particles 2D")]
    public static void Effects2DPause(bool paused) { if (Effects2D is { } state) state.Paused = paused; }

    [PgslCommand("Particle2DCount", "Particle2DCount() -> number", "Active particles in the current room's bounded pool", "Particles 2D")]
    public static double Particle2DCount() => Effects2D?.ParticleCount ?? 0;

    [PgslCommand("PointerWorldX", "PointerWorldX() -> number", "Mouse X mapped through viewport 0, including window fitting", "Input")]
    public static double PointerWorldX() => PointerWorld(true);
    [PgslCommand("PointerWorldY", "PointerWorldY() -> number", "Mouse Y mapped through viewport 0, including window fitting", "Input")]
    public static double PointerWorldY() => PointerWorld(false);
    private static double PointerWorld(bool horizontal)
    {
        var room = ActiveGameContext?.Room;
        if (room == null || room.Viewports.Count == 0) return horizontal ? MouseRawX : MouseRawY;
        var view = room.Viewports[0];
        var port = RoomDisplayLayout.Port(room, view, (int)WindowGetWidth(), (int)WindowGetHeight());
        if (port.Width <= 0 || port.Height <= 0) return 0;
        return horizontal ? ViewGetPosX(0) + (MouseRawX - port.X) * view.SourceWidth / port.Width
            : ViewGetPosY(0) + (MouseRawY - port.Y) * view.SourceHeight / port.Height;
    }
}
