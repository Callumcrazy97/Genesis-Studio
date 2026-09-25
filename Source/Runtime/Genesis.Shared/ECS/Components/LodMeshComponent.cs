using Genesis.Shared.Interfaces;

namespace Genesis.Shared.ECS.Components
{
    public struct LodMeshComponent : IComponent
    {
        public MeshHandle Mesh0;
        public MeshHandle Mesh1;
        public MeshHandle Mesh2;
        public MeshHandle Mesh3;

        public float MediumDistance;
        public float FarDistance;
        public float VeryFarDistance;

        /// <summary>Shadow LOD switches at cameraDistance × this (≥1 = coarser shadow meshes sooner).</summary>
        public float ShadowLodBias;
    }
}
