using System.Numerics;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// The editor-suite lighting overhaul. Every specialised editor viewport shares this state
/// instead of the old flat "LightingEnabled + nothing else" look: warm sun with real shadows,
/// sky-model hemisphere ambient (never black undersides), GGX specular + fresnel rim from the
/// stylized pipeline, and the always-on ACES tonemap in the renderer. This is what kills the
/// "old-school flat Lambert" appearance in Room/Terrain/Model previews.
/// </summary>
public static class EditorSceneLighting
{
    /// <summary>Sky used by editor viewports — slightly brighter than the runtime default so
    /// authoring surfaces read clearly.</summary>
    public static SkyModel Sky { get; } = new(
        zenithColor: new Vector3(0.36f, 0.50f, 0.72f),
        horizonColor: new Vector3(0.58f, 0.66f, 0.78f),
        groundColor: new Vector3(0.24f, 0.22f, 0.19f),
        ambientIntensity: 0.42f);

    /// <summary>
    /// Full editor scene state. <paramref name="showFloor"/> draws the camera-following
    /// checkerboard reference floor (rooms/models); terrain replaces the floor with itself.
    /// </summary>
    public static Mesh3DState Create(bool showFloor, bool shadows = true, float farPlane = 900f)
    {
        Mesh3DState state = Mesh3DState.Default;

        // Warm key light, high enough for crisp shadow shapes without harsh contrast.
        state.LightDirection = Vector3.Normalize(new Vector3(0.55f, -0.72f, 0.42f));
        state.SunColor = new Vector3(1.0f, 0.956f, 0.87f);
        state.SunIntensity = 1.25f;
        state.LightingEnabled = true;
        state.LightingWeight = 1f;

        state.AmbientColor = Sky.AmbientSky;
        state.AmbientGroundColor = Sky.AmbientGround;
        state.BackgroundColor = Sky.HorizonColor;
        EngineFogDefaults fog = EngineRenderingDefaults.Fog;
        state.FogColor = fog.Color;
        state.FogEnabled = fog.Enabled;
        state.FogStart = fog.Start;
        state.FogEnd = fog.End;
        state.FogScreenSpace = fog.Enabled;

        state.ShadowsEnabled = shadows;
        state.ShadowHighQuality = true;
        state.ShadowBias = 0.0015f;
        state.ShadowOrthoSize = 90f;

        // Non-Lambertian response: wrapped diffuse, visible specular, gentle rim.
        state.StylizedLightingEnabled = true;
        state.StylizedToonSteps = 1f;          // smooth wrap, no cel banding by default
        state.StylizedDiffuseWrap = 0.42f;
        state.StylizedSpecularStrength = 0.85f;
        state.StylizedRimStrength = 0.30f;
        state.StylizedSaturation = 1.06f;

        state.ShowFloor = showFloor;
        state.FloorFollowsCamera = true;
        state.FloorColor = new Vector3(0.30f, 0.33f, 0.38f);
        state.ShowSunVisual = false;

        state.FrustumCullingEnabled = false;
        state.CameraFarPlane = farPlane;
        return state;
    }

    /// <summary>Distant aerial haze for large scenes (terrain). Reads as depth, not repaint.</summary>
    public static Mesh3DState WithAerialHaze(Mesh3DState state, float start, float end)
    {
        state.FogEnabled = true;
        state.FogScreenSpace = true;
        state.FogStart = start;
        state.FogEnd = end;
        state.FogDensity = 0.012f;
        state.FogHeightBase = 0f;
        state.FogHeightFalloff = 0.045f;
        state.FogAerialBlend = 0.7f;
        state.FogSunPreserve = 0.85f;
        state.FogNoiseStrength = 0.08f;
        state.FogColor = Sky.FogColor;
        return state;
    }
}
