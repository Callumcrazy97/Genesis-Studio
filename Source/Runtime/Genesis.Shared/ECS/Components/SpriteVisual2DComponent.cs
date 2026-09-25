using Genesis.Shared.ECS;

namespace Genesis.Shared.ECS.Components
{
    public struct SpriteVisual2DComponent : IComponent
    {
        public ushort CatalogIndex;
        public float  ImageXScale;
        public float  ImageYScale;
        public float  ImageAngle;
        public float  ImageAlpha;
        public float  TintR;
        public float  TintG;
        public float  TintB;
        /// <summary>Degrees added per frame (creature step spin).</summary>
        public float  SpinSpeed;
    }
}
