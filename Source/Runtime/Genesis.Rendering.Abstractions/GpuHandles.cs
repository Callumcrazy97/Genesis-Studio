namespace Genesis.Rendering.Abstractions
{
    // Opaque resource handles. Deliberately int-id structs rather than interfaces or wrapped
    // pointers, mirroring the MeshHandle/TextureHandle idiom the render contract already uses:
    // they cost nothing to pass, cannot be confused with each other, and keep every native type
    // behind the backend that owns it. Id 0 is always "invalid", so `default` is a safe empty value.

    public readonly struct GpuBufferHandle
    {
        public readonly int Id;
        public GpuBufferHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static GpuBufferHandle Invalid => new GpuBufferHandle(0);
    }

    public readonly struct GpuTextureHandle
    {
        public readonly int Id;
        public GpuTextureHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static GpuTextureHandle Invalid => new GpuTextureHandle(0);
    }

    public readonly struct GpuSamplerHandle
    {
        public readonly int Id;
        public GpuSamplerHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static GpuSamplerHandle Invalid => new GpuSamplerHandle(0);
    }

    public readonly struct GpuShaderProgramHandle
    {
        public readonly int Id;
        public GpuShaderProgramHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static GpuShaderProgramHandle Invalid => new GpuShaderProgramHandle(0);
    }

    public readonly struct GpuVertexLayoutHandle
    {
        public readonly int Id;
        public GpuVertexLayoutHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static GpuVertexLayoutHandle Invalid => new GpuVertexLayoutHandle(0);
    }

    public readonly struct GpuRenderTargetHandle
    {
        public readonly int Id;
        public GpuRenderTargetHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static GpuRenderTargetHandle Invalid => new GpuRenderTargetHandle(0);
    }

    /// <summary>A timestamp scope in flight. Results arrive a few frames later, never immediately.</summary>
    public readonly struct GpuQueryHandle
    {
        public readonly int Id;
        public GpuQueryHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static GpuQueryHandle Invalid => new GpuQueryHandle(0);
    }

    /// <summary>Resolved GPU textures for extra authored shader resource slots.</summary>
    public struct AuthoredGpuTextures
    {
        public int Count;
        public int Slot0, Slot1, Slot2, Slot3;
        public GpuTextureHandle Tex0, Tex1, Tex2, Tex3;
        public int TexId0, TexId1, TexId2, TexId3;

        public void Bind(IGpuDevice gpu)
        {
            if (gpu == null) return;
            BindSlot(gpu, 0, Slot0, Tex0);
            BindSlot(gpu, 1, Slot1, Tex1);
            BindSlot(gpu, 2, Slot2, Tex2);
            BindSlot(gpu, 3, Slot3, Tex3);
        }

        public bool SameBindings(in AuthoredGpuTextures other) =>
            Count == other.Count
            && Slot0 == other.Slot0 && TexId0 == other.TexId0
            && Slot1 == other.Slot1 && TexId1 == other.TexId1
            && Slot2 == other.Slot2 && TexId2 == other.TexId2
            && Slot3 == other.Slot3 && TexId3 == other.TexId3;

        private void BindSlot(IGpuDevice gpu, int index, int slot, GpuTextureHandle texture)
        {
            if (index >= Count || !texture.IsValid) return;
            gpu.SetTexture(GpuShaderStage.Pixel, slot, texture);
        }
    }
}
