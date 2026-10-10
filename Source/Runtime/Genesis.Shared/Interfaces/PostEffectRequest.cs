using System.Numerics;

namespace Genesis.Shared.Interfaces
{
    /// <summary>
    /// One project post effect for the renderer to run after the final composite: a Fullscreen
    /// Shader resource's code and entry point, and its parameter values packed as the Shader editor
    /// packs them (GenesisParameters, four rows of four).
    /// </summary>
    public readonly record struct PostEffectRequest(
        string Name,
        string Source,
        string Entry,
        string SourcePath,
        string ProjectPath,
        Vector4 Row0,
        Vector4 Row1,
        Vector4 Row2,
        Vector4 Row3)
    {
        /// <summary>The pictures the effect reads beyond the frame, its depth and its flags (t3 and up).</summary>
        public AuthoredShaderTextures Textures { get; init; }

        /// <summary>Bit n: the shader declares a texture at register tn (n from 3). One given no picture reads white.</summary>
        public int TextureSlots { get; init; }

        /// <summary>
        /// Bit 3: the shader declares <c>ObjectMarks</c> at t3; bit 4: <c>ObjectIds</c> at t4 (the names
        /// in any case). Those read the engine's object images rather than white when given no picture.
        /// </summary>
        public int ObjectImageSlots { get; init; }

        /// <summary>Bit n: the shader declares a sampler at register sn (n from 1).</summary>
        public int SamplerSlots { get; init; }
    }
}
