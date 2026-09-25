namespace Genesis.Shared.Interfaces
{
    /// <summary>Renderer behavior switches, tweakable at runtime.</summary>
    public sealed class RendererOptions
    {
        public bool FaceCulling { get; set; } = true;
        public bool CullFrontFaces { get; set; }
        public bool FrontCounterClockwise { get; set; } = true;
        public bool FrustumCulling { get; set; } = true;
        public bool Wireframe { get; set; }
    }
}
