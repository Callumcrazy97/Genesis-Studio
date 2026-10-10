using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// A game's graphics options: anti-aliasing and quality tiers, beside the window and frame-rate
// options in PgslCommands.Display. They change engine-wide settings, which take effect from the
// next frame.
public static partial class PgslCommands
{
    [PgslCommand("RenderSetAntiAliasing", "RenderSetAntiAliasing(mode) -> bool",
        "Smooth the jagged edges of the 3D picture: \"off\", \"fxaa\" (one pass, softest) or \"smaa\" (sharper, three passes); the GUI is never softened. False for any other name", "Display")]
    public static bool RenderSetAntiAliasing(string mode)
    {
        if (!AntiAliasingModes.TryParse(mode, out AntiAliasingMode parsed)) return false;
        MeshLightingDefaults.AntiAliasing = parsed;
        return true;
    }

    [PgslCommand("RenderGetAntiAliasing", "RenderGetAntiAliasing() -> string",
        "The anti-aliasing asked for: \"off\", \"fxaa\" or \"smaa\"", "Display")]
    public static string RenderGetAntiAliasing() => AntiAliasingModes.Name(MeshLightingDefaults.AntiAliasing);

    [PgslCommand("RenderSetQuality", "RenderSetQuality(tier) -> bool",
        "Set the graphics quality: \"low\", \"medium\", \"high\" or \"ultra\" sets ambient occlusion, contact shadows, sun shadow cascades and resolution, point-light shadows, local volumetric light, bloom, fog and cloud quality and anti-aliasing together; each can be changed afterwards (Engine.Rendering). False for any other name", "Display")]
    public static bool RenderSetQuality(string tier)
    {
        if (!RenderQuality.TryParse(tier, out RenderQualityTier parsed)) return false;
        RenderQuality.Apply(parsed);
        return true;
    }

    [PgslCommand("RenderGetQuality", "RenderGetQuality() -> string",
        "The quality tier last set (\"low\", \"medium\", \"high\" or \"ultra\"), \"custom\" once one of its settings has been changed since, or \"\" before any tier was set", "Display")]
    public static string RenderGetQuality() => RenderQuality.Current;

    [PgslCommand("SetQuality", "Engine.Rendering.SetQuality(tier) -> bool",
        "Set the graphics quality tier: low, medium, high or ultra (as RenderSetQuality). False for any other name", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static bool SetQuality(string tier) => RenderSetQuality(tier);

    [PgslCommand("QualityTier", "Engine.Rendering.QualityTier",
        "The quality tier last set: low, medium, high or ultra; custom once one of its settings has been changed; empty before any. Setting it sets the tier", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static string QualityTier
    {
        get => RenderGetQuality();
        set
        {
            // Reading it back and writing it again (custom, or empty) changes nothing.
            if (RenderQuality.TryParse(value, out RenderQualityTier parsed) && RenderQuality.Current != RenderQuality.Name(parsed))
                RenderQuality.Apply(parsed);
        }
    }

    [PgslCommand("AntiAliasing", "Engine.Rendering.AntiAliasing",
        "Anti-aliasing of the 3D picture: off, fxaa or smaa (as RenderSetAntiAliasing)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static string AntiAliasing
    {
        get => RenderGetAntiAliasing();
        set => RenderSetAntiAliasing(value);
    }

    [PgslCommand("ShadowResolution", "Engine.Rendering.ShadowResolution",
        "Sun shadow map size in texels per side, for every cascade: 512, 1024 (the default), 2048 or 4096; 0 goes back to the default", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static int ShadowResolution
    {
        get => MeshLightingDefaults.ShadowResolution > 0 ? MeshLightingDefaults.ShadowResolution : MeshLightingDefaults.DefaultShadowResolution;
        set => MeshLightingDefaults.ShadowResolution = value;
    }

    [PgslCommand("VolumetricFogQuality", "Engine.Rendering.VolumetricFogQuality",
        "Volumetric fog quality for every room: 0 low, 1 medium, 2 high; -1 leaves each room's own", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static int VolumetricFogQuality
    {
        get => MeshLightingDefaults.VolumetricFogQuality;
        set => MeshLightingDefaults.VolumetricFogQuality = value;
    }
}
