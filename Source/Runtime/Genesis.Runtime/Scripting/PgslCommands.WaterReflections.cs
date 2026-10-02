using Genesis.Rendering.Core;
using Genesis.Shared.Scripting;
namespace Genesis.Runtime.Scripting;
public static partial class PgslCommands
{
    [PgslCommand("WaterReflections", "Engine.Rendering.WaterReflections", "Enable one half-resolution planar reflection for the dominant water plane; software uses sky fallback", "Rendering", Namespace = "Engine.Rendering")]
    public static bool WaterReflections
    {
        get => EngineRenderingDefaults.WaterReflections;
        set => EngineRenderingDefaults.WaterReflections = value;
    }

    [PgslCommand("ReversedDepth", "Engine.Rendering.ReversedDepth", "Store scene depth reversed in floating point, so a view of kilometres keeps a close near plane without distant surfaces flickering. On by default; the software renderer ignores it", "Rendering", Namespace = "Engine.Rendering")]
    public static bool ReversedDepth
    {
        get => EngineRenderingDefaults.ReversedDepth;
        set => EngineRenderingDefaults.ReversedDepth = value;
    }
}
