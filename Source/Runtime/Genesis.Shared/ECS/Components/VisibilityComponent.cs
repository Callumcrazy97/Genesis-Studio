using System.Numerics;

namespace Genesis.Shared.ECS.Components
{
    /// <summary>Last coarse visibility result written by the runtime visibility phase.</summary>
    public enum VisibilityResult : byte
    {
        Unknown = 0,
        Visible = 1,
        Disabled = 2,
        FrustumCulled = 3,
        Occluded = 4,
        DistanceCulled = 5,
        /// <summary>Projected bounds smaller than the sub-pixel cull threshold (R7.10).</summary>
        SubPixelCulled = 6,
    }

    /// <summary>
    /// Backend-neutral authored visibility data for drawable entities. The runtime visibility phase
    /// writes <see cref="LastResult"/>; render backends remain free to perform a final conservative
    /// occlusion test immediately before submission.
    /// </summary>
    public struct VisibilityComponent : IComponent
    {
        /// <summary>Local-space sphere centre relative to the entity transform.</summary>
        public Vector3 BoundsOffset;

        /// <summary>Local-space sphere radius. Values &lt;= 0 use the engine's default 0.5-unit radius.</summary>
        public float BoundsRadius;

        /// <summary>
        /// Optional max camera distance before <see cref="VisibilityResult.DistanceCulled"/>.
        /// Values &lt;= 0 use the active camera far plane (R7.10).
        /// </summary>
        public float MaxDrawDistance;

        /// <summary>Authored pass/layer mask. Zero means all layers until layer filtering is wired.</summary>
        public ushort LayerMask;

        /// <summary>Authored master switch for this drawable.</summary>
        public bool Enabled;

        /// <summary>Result written by <c>VisibilitySystem</c> for the current variable update.</summary>
        public VisibilityResult LastResult;

        public static VisibilityComponent Default(float boundsRadius = 0.5f) => new VisibilityComponent
        {
            BoundsOffset = Vector3.Zero,
            BoundsRadius = boundsRadius,
            MaxDrawDistance = 0f,
            LayerMask = 0,
            Enabled = true,
            LastResult = VisibilityResult.Unknown,
        };
    }
}
