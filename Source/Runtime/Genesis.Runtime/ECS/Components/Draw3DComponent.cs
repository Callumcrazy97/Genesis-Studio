using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    /// <summary>Marks an entity for 3D mesh rendering via the object draw pass.</summary>
    public struct Draw3DComponent : IComponent
    {
        public bool Visible;
        public bool CastShadows;
        public bool ReceiveShadows;
        /// <summary>True when a sibling script feeds mesh data each frame (no Model asset).</summary>
        public bool Procedural;
    }
}
