using System;

namespace Genesis.Physics;

internal static class PhysicsDamping
{
    internal static float Clamp(float rate) => float.IsFinite(rate) ? Math.Clamp(rate, 0f, 100f) : 0f;
    internal static float Factor(float rate, float seconds) => MathF.Exp(-Clamp(rate) * MathF.Max(0f, seconds));
}
