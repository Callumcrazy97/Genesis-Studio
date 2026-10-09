using System;
using System.Numerics;
using Genesis.Rendering.Lights;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// Flash lights: short point lights a game fires for a muzzle flash, an explosion or a spark. They go
/// into the same clustered light list as every other point light (each pixel still evaluates only the
/// lights whose reach covers its cluster, so dozens of small flashes cost little), are lit with the
/// same response and scatter in the froxel fog, but they never take a local shadow slot: a shot must
/// not take a lamp's shadow away, nor have six shadow faces drawn for a light that lasts 50 ms.
/// </summary>
internal sealed partial class ForwardRenderer
{
    /// <summary>Adds a flash light for this frame (see IRenderController.AddFlashLight).</summary>
    public void AddFlashLight(
        Vector3 position,
        Vector3 color,
        float radius,
        float intensity = 1f,
        float falloff = 2f)
    {
        if (_pointLightCount >= PointLightCapacity) return;
        if (!float.IsFinite(radius) || !float.IsFinite(intensity) || radius <= 0f || intensity <= 0f) return;
        _pointLights[_pointLightCount++] = new ClusterPointLightGpu
        {
            PosRadius = new Vector4(position, radius),
            ColorIntensity = new Vector4(ToLinearColor(color), intensity),
            FalloffPad = new Vector4(Math.Clamp(falloff, 0.05f, 16f), 0f, 0f, 0f),
            // A point light's cone fields are ignored by the shaders; W marks it as a flash.
            SpotDirCos = new Vector4(0f, 0f, 0f, OmniShadowMath.FlashLightMarker),
        };
    }

    /// <summary>Flash lights among the lights submitted for the frame being made (for checks).</summary>
    public int FlashLightsSubmitted
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _pointLightCount; i++)
                if (OmniShadowMath.IsFlashLight(_pointLights[i])) count++;
            return count;
        }
    }
}
