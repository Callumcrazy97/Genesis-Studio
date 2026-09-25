using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Primitives
{
    /// <summary>Result of compiling one HLSL stage for a concrete backend representation.</summary>
    public readonly struct ShaderCompileResult
    {
        public ShaderCompileResult(
            byte[] blob,
            string entryPoint,
            string profile,
            GpuShaderBinaryFormat binaryFormat,
            bool cacheHit)
        {
            Blob = blob;
            EntryPoint = entryPoint;
            Profile = profile;
            BinaryFormat = binaryFormat;
            CacheHit = cacheHit;
        }

        public byte[] Blob { get; }
        public string EntryPoint { get; }
        public string Profile { get; }
        public GpuShaderBinaryFormat BinaryFormat { get; }
        public bool CacheHit { get; }
    }
}
