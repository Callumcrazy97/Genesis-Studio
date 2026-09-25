namespace Genesis.Shared.Interfaces
{
    /// <summary>Which GPU pipeline consumes a shader-editor preview pixel shader.</summary>
    public enum ShaderPreviewProfile
    {
        /// <summary>Sprite batch — <c>MainPS(VSOut)</c> with UV/COLOR.</summary>
        SpritePipeline,

        /// <summary>Fullscreen triangle — <c>MainPS(PreviewPSIn)</c> with interpolated UV.</summary>
        FullscreenEffect,

        /// <summary>Forward mesh pass — <c>MainPS(VSOut)</c> matching <see cref="Genesis.Rendering.Primitives.ForwardShaders"/>.</summary>
        MeshPipeline,
    }
}
