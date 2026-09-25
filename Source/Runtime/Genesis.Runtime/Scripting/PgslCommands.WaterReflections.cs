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
}
