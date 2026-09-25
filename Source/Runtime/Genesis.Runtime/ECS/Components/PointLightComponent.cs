using System.Numerics;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    public enum LightEmitterAction
    {
        Steady,
        Flicker,
        Pulse,
        Glow,
        ColourCycle,
    }

    /// <summary>Reusable point-light settings authored as part of an Object prefab.</summary>
    public struct PointLightComponent : IComponent
    {
        public Vector3 Color;
        public Vector3 SecondaryColor;
        public Vector3 TertiaryColor;
        public Vector3 Offset;
        public float Radius;
        public float Intensity;
        public float Falloff;
        public LightEmitterAction Action;
        public float ActionSpeed;
        public float ActionAmount;
        public float Phase;
        public int ColorCount;
        public bool Enabled;
    }
}
