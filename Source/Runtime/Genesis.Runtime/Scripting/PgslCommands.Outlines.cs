using System;
using System.Numerics;
using Genesis.Runtime.Rendering;
using Genesis.Shared.ECS;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Outlines for readability (a teammate in blue, an enemy in red, a pickup through a wall). The
// outline is drawn by the engine; the same marks, with each instance's id, are images a project's
// post effects can read (ObjectMarks and ObjectIds, Documentation/PostEffects.md).
public static partial class PgslCommands
{
    /// <summary>The widest outline, in pixels.</summary>
    private const double OutlineMaxWidth = 16;

    [PgslCommand("OutlineSet", "OutlineSet(r, g, b, width, throughWalls)",
        "Outline this instance's 3D draws in a colour (0 to 1 each), width in pixels up to 16 (0 marks it for post effects without a line); throughWalls shows it behind what stands in front",
        "Shaders")]
    public static void OutlineSet(double r, double g, double b, double width, bool throughWalls) =>
        InstanceSetOutline(GetContext()?.InstanceId ?? 0, r, g, b, width, throughWalls);

    [PgslCommand("OutlineClear", "OutlineClear()", "Take this instance's outline away", "Shaders")]
    public static void OutlineClear() => InstanceClearOutline(GetContext()?.InstanceId ?? 0);

    [PgslCommand("InstanceSetOutline", "InstanceSetOutline(id, r, g, b, width, throughWalls)",
        "Outline another instance's 3D draws in a colour (0 to 1 each), width in pixels up to 16; throughWalls shows it behind walls",
        "Shaders")]
    public static void InstanceSetOutline(double id, double r, double g, double b, double width, bool throughWalls)
    {
        if (!double.IsFinite(r) || !double.IsFinite(g) || !double.IsFinite(b) || !double.IsFinite(width)) return;
        if (!TryDrawAssets(id, out ObjectDrawAssetEntry assets)) return;
        assets.Outline = new Vector4(
            (float)Math.Clamp(r, 0, 1), (float)Math.Clamp(g, 0, 1), (float)Math.Clamp(b, 0, 1),
            (float)Math.Clamp(width, 0, OutlineMaxWidth));
        assets.OutlineThroughWalls = throughWalls;
    }

    [PgslCommand("InstanceClearOutline", "InstanceClearOutline(id)", "Take another instance's outline away", "Shaders")]
    public static void InstanceClearOutline(double id)
    {
        if (ExistingDrawAssets(id, out ObjectDrawAssetEntry assets)) assets.Outline = null;
    }

    [PgslCommand("InstanceHasOutline", "InstanceHasOutline(id) -> bool", "Whether an instance is outlined (or marked)", "Shaders")]
    public static bool InstanceHasOutline(double id) => ExistingDrawAssets(id, out ObjectDrawAssetEntry assets) && assets.Outline.HasValue;

    /// <summary>An instance's draw entry when it has one; never makes one.</summary>
    private static bool ExistingDrawAssets(double id, out ObjectDrawAssetEntry assets)
    {
        assets = null;
        var world = World;
        if (world is null || !double.IsFinite(id) || id < 1 || id > int.MaxValue) return false;
        Entity entity = world.GetEntity((int)id);
        return world.IsAlive(entity) && ObjectDrawAssetRegistry.TryGet(entity, out assets);
    }
}
