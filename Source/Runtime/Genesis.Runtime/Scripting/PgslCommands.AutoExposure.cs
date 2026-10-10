using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Request 64b: eye adaptation. The exposure follows the brightness of the 3D scene (the GUI is
/// never metered). A room's <c>autoExposure</c> settings start it; these change it for the room
/// being played. With no game running they do nothing.
/// </summary>
public static partial class PgslCommands
{
    [PgslCommand("RenderSetAutoExposure", "RenderSetAutoExposure(on, key, adaptSeconds, minEv, maxEv)",
        "Eye adaptation: the exposure follows the scene's brightness so an average scene shows at key (0.18 = mid grey), taking about adaptSeconds to adjust (0 = at once), never darker than minEv or brighter than maxEv stops (e.g. -4, 4)",
        "Lighting")]
    public static void RenderSetAutoExposure(bool on, double key, double adaptSeconds, double minEv, double maxEv)
    {
        SceneEnvironment environment = ActiveEnvironment;
        if (environment == null) return;
        bool wasOn = environment.AutoExposureEnabled;
        environment.AutoExposureEnabled = on;
        if (double.IsFinite(key)) environment.AutoExposureKey = AutoExposureDefaults.ClampKey((float)key);
        if (double.IsFinite(adaptSeconds))
        {
            float seconds = AutoExposureDefaults.ClampSeconds((float)adaptSeconds);
            environment.AutoExposureDarkenSeconds = seconds;
            environment.AutoExposureBrightenSeconds = seconds;
        }
        if (double.IsFinite(minEv)) environment.AutoExposureMinEv = AutoExposureDefaults.ClampEv((float)minEv);
        if (double.IsFinite(maxEv)) environment.AutoExposureMaxEv = AutoExposureDefaults.ClampEv((float)maxEv);
        // Turned on, it starts from the scene as it is rather than fading in from the last exposure.
        if (on && !wasOn) environment.ResetAutoExposure();
    }

    [PgslCommand("RenderSetAutoExposureSpeed", "RenderSetAutoExposureSpeed(darkenSeconds, brightenSeconds)",
        "How long eye adaptation takes: to darken when the scene gets brighter (stepping into daylight, 0.8 by default) and to brighten when it gets darker (2.5 by default); 0 = at once",
        "Lighting")]
    public static void RenderSetAutoExposureSpeed(double darkenSeconds, double brightenSeconds)
    {
        SceneEnvironment environment = ActiveEnvironment;
        if (environment == null) return;
        if (double.IsFinite(darkenSeconds)) environment.AutoExposureDarkenSeconds = AutoExposureDefaults.ClampSeconds((float)darkenSeconds);
        if (double.IsFinite(brightenSeconds)) environment.AutoExposureBrightenSeconds = AutoExposureDefaults.ClampSeconds((float)brightenSeconds);
    }

    [PgslCommand("RenderSetAutoExposureMetering", "RenderSetAutoExposureMetering(centerWeight)",
        "How eye adaptation measures the picture: 0 counts it all evenly, 1 counts its centre most (0.5 by default)", "Lighting")]
    public static void RenderSetAutoExposureMetering(double centerWeight)
    {
        SceneEnvironment environment = ActiveEnvironment;
        if (environment != null && double.IsFinite(centerWeight))
            environment.AutoExposureCenterWeight = AutoExposureDefaults.ClampCenterWeight((float)centerWeight);
    }

    [PgslCommand("RenderResetAutoExposure", "RenderResetAutoExposure()",
        "Eye adaptation jumps to the scene at once instead of adjusting over time (after a camera cut or a teleport)", "Lighting")]
    public static void RenderResetAutoExposure()
    {
        ActiveEnvironment?.ResetAutoExposure();
    }

    [PgslCommand("RenderGetAutoExposure", "RenderGetAutoExposure() -> bool", "Whether eye adaptation is on in this room", "Lighting")]
    public static bool RenderGetAutoExposure() => ActiveEnvironment?.AutoExposureEnabled ?? false;

    [PgslCommand("AutoExposure", "Engine.Rendering.AutoExposure",
        "Turn on eye adaptation with a key (0.18 = mid grey) and an adaptation time in seconds (as RenderSetAutoExposure)",
        "Engine · Rendering", Namespace = "Engine.Rendering")]
    public static void RenderingAutoExposure(float key, float adaptSeconds)
    {
        SceneEnvironment environment = ActiveEnvironment;
        if (environment == null) return;
        RenderSetAutoExposure(true, key, adaptSeconds, environment.AutoExposureMinEv, environment.AutoExposureMaxEv);
    }

    [PgslCommand("AutoExposureEnabled", "Engine.Rendering.AutoExposureEnabled",
        "Eye adaptation on or off for this room", "Engine · Rendering", Namespace = "Engine.Rendering")]
    public static bool RenderingAutoExposureEnabled
    {
        get => RenderGetAutoExposure();
        set
        {
            SceneEnvironment environment = ActiveEnvironment;
            if (environment == null) return;
            RenderSetAutoExposure(value, environment.AutoExposureKey, double.NaN,
                environment.AutoExposureMinEv, environment.AutoExposureMaxEv);
        }
    }
}
