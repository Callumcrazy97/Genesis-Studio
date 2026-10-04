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
        Vector4 Row3);
}
