using System.Globalization;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// The room's sky light and eye adaptation (request 64) in the Inspector and in the editor's
/// preview of the room, which shows them as a game in that room would.
/// </summary>
public sealed partial class RoomEditorControl
{
    private const string SkyLightGroup = "Sky light & exposure";

    private static void AddSkyLightAndExposureValues(List<ResourceInspectorLiveValue> values, RoomEnvironment environment)
    {
        bool hemisphere = SkyLightModes.TryParse(environment.SkyLight, out int mode) && mode == SkyLightModes.Hemisphere;
        values.Add(new(SkyLightGroup, "environment.skyLight", "Sky light", hemisphere ? "Hemisphere" : "Zenith",
            Choices: ["Zenith", "Hemisphere"]));
        values.Add(Number(SkyLightGroup, "environment.skyLightStrength", "Sky light strength (1 = the sky as drawn)",
            environment.SkyLightStrength, 0, 16, 0.05m, 2));
        values.Add(Number(SkyLightGroup, "environment.skyLightSaturation", "Sky light colour (1 = the sky's own, 0 = grey)",
            environment.SkyLightSaturation, 0, 2, 0.05m, 2));
        values.Add(new(SkyLightGroup, "environment.autoExposure", "Auto exposure", environment.AutoExposure));
        values.Add(Number(SkyLightGroup, "environment.autoExposureKey", "Exposure key (0.18 = mid grey)",
            environment.AutoExposureKey, 0.01m, 4, 0.01m, 2));
        values.Add(Number(SkyLightGroup, "environment.autoExposureDarkenSeconds", "Darken over (s)",
            environment.AutoExposureDarkenSeconds, 0, 600, 0.1m, 2));
        values.Add(Number(SkyLightGroup, "environment.autoExposureBrightenSeconds", "Brighten over (s)",
            environment.AutoExposureBrightenSeconds, 0, 600, 0.1m, 2));
        values.Add(Number(SkyLightGroup, "environment.autoExposureMinEv", "Darkest (stops)",
            environment.AutoExposureMinEv, -16, 16, 0.5m, 1));
        values.Add(Number(SkyLightGroup, "environment.autoExposureMaxEv", "Brightest (stops)",
            environment.AutoExposureMaxEv, -16, 16, 0.5m, 1));
    }

    private static bool TryApplySkyLightAndExposureValue(RoomEnvironment next, string name, object? value)
    {
        switch (name)
        {
            case "skylight":
                next.SkyLight = SkyLightModes.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, out int mode)
                    ? SkyLightModes.Name(mode)
                    : SkyLightModes.Name(SkyLightModes.Zenith);
                return true;
            case "skylightstrength": next.SkyLightStrength = Convert.ToSingle(value, CultureInfo.InvariantCulture); return true;
            case "skylightsaturation": next.SkyLightSaturation = Convert.ToSingle(value, CultureInfo.InvariantCulture); return true;
            case "autoexposure": next.AutoExposure = Convert.ToBoolean(value, CultureInfo.InvariantCulture); return true;
            case "autoexposurekey": next.AutoExposureKey = Convert.ToSingle(value, CultureInfo.InvariantCulture); return true;
            case "autoexposuredarkenseconds": next.AutoExposureDarkenSeconds = Convert.ToSingle(value, CultureInfo.InvariantCulture); return true;
            case "autoexposurebrightenseconds": next.AutoExposureBrightenSeconds = Convert.ToSingle(value, CultureInfo.InvariantCulture); return true;
            case "autoexposureminev": next.AutoExposureMinEv = Convert.ToSingle(value, CultureInfo.InvariantCulture); return true;
            case "autoexposuremaxev": next.AutoExposureMaxEv = Convert.ToSingle(value, CultureInfo.InvariantCulture); return true;
            default: return false;
        }
    }

    /// <summary>
    /// The room's sky light and auto exposure on the editor's preview. The preview is drawn when
    /// something changes rather than every frame, so its exposure adapts at once.
    /// </summary>
    private void ApplyRoomSkyLightPreview(ref Mesh3DState state)
    {
        if (_room.Environment is not { } environment) return;
        SceneEnvironment preview = new();
        RoomSceneBuilder.ApplySkyLightAndExposure(preview, environment);
        preview.AutoExposureDarkenSeconds = 0f;
        preview.AutoExposureBrightenSeconds = 0f;
        preview.AutoExposureResetId = 0;
        EnvironmentMapper.ApplySkyLightAndExposure(ref state, preview);
    }
}
