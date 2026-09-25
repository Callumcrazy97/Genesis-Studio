#nullable disable
using System;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites
{
    internal sealed class EditorInteractionRenderProbe : IRenderController
    {
        public readonly System.Collections.Generic.Dictionary<int, byte[]> Textures = new();
        public readonly System.Collections.Generic.List<SpriteDrawCall> Sprites = new();
        public int Created, Updated, Released;
        private int _nextId;
        public void Initialize(IntPtr windowHandle, int width, int height) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void Resize(int width, int height) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public bool TryResize(int width, int height) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void Shutdown() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public bool IsInitialized => throw new NotSupportedException("Unexpected test-renderer operation.");
        public bool IsFramebufferReady => throw new NotSupportedException("Unexpected test-renderer operation.");
        public string BackendName => throw new NotSupportedException("Unexpected test-renderer operation.");
        public string AdapterName => throw new NotSupportedException("Unexpected test-renderer operation.");
        public int PixelWidth => throw new NotSupportedException("Unexpected test-renderer operation.");
        public int PixelHeight => throw new NotSupportedException("Unexpected test-renderer operation.");

        public void BeginFrame() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void Clear(float r, float g, float b, float a = 1f) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void EndFrame() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void Present() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetVSync(bool vsync) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void Set3DFrameActive(bool active) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetViewport(int x, int y, int width, int height) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetBlendMode(BlendMode mode) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetSamplerState(SamplerFilter filter) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetRoomFog(RoomFogState state) => throw new NotSupportedException("Unexpected test-renderer operation.");

        // Immediate-mode HUD (hotbar, menus) draws textured/colored quads during OnRenderFrame.
        public void DrawSprite(in SpriteDrawCall call) { Sprites.Add(call); }
        public void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls) { foreach (var call in calls) Sprites.Add(call); }
        public void FlushOverlaySprites() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void DrawLine(float x1, float y1, float x2, float y2, RenderColor color, float thickness = 1f, int depth = -10000)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void DrawRect(float x, float y, float w, float h, RenderColor color, bool filled = true, int depth = -10000)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        // Text is a HUD element, so it follows DrawLine/DrawRect rather than the blocked mesh path.
        public void DrawText(string text, float x, float y, float size, RenderColor color)
            => throw new NotSupportedException("Unexpected test-renderer operation.");

        public bool ComposeOverlay(Action<IOverlayCanvas> draw) => throw new NotSupportedException("Unexpected test-renderer operation.");

        public bool TryReadFramePixels(out int width, out int height, out byte[] bgra)
            => throw new NotSupportedException("Unexpected test-renderer operation.");

        public bool TryReadSubmittedFramePixels(out int width, out int height, out byte[] bgra)
            => throw new NotSupportedException("Unexpected test-renderer operation.");

        public void DrawMesh(in MeshDrawCall call) { /* blocked for scripts */ }
        public void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls) { /* blocked for scripts */ }
        public void DrawMeshInstances(in MeshDrawCall template, ReadOnlySpan<MeshInstanceData> instances) { /* blocked for scripts */ }

        public MeshHandle RegisterMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<ushort> indices)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public MeshHandle RegisterSkinnedMesh(ReadOnlySpan<SkinnedMeshVertex> vertices, ReadOnlySpan<ushort> indices)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public SkinPaletteHandle CreateSkinPalette(int matrixCount)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void UpdateSkinPalette(SkinPaletteHandle handle, ReadOnlySpan<Matrix4x4> matrices)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void ReleaseSkinPalette(SkinPaletteHandle handle)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public MeshHandle RegisterCombinedMesh(ReadOnlySpan<MeshCombinePart> parts)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void UpdateMesh(MeshHandle handle, ReadOnlySpan<MeshVertex> vertices) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void ReleaseMesh(MeshHandle handle) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetMesh3DState(Mesh3DState state) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public MeshHandle GetBuiltinMesh(BuiltinMeshKind kind) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void Advance3DTime(float deltaSeconds) => throw new NotSupportedException("Unexpected test-renderer operation.");

        public void SetCamera2D(float x, float y, float zoom, float rotation) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetCamera3D(Matrix4x4 view, Matrix4x4 projection) => throw new NotSupportedException("Unexpected test-renderer operation.");

        public TextureHandle LoadTexture(string path) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public TextureHandle CreateTexture(int width, int height, ReadOnlySpan<byte> rgba)
        { if (rgba.Length != width*height*4) throw new ArgumentException("Texture size");
          var handle = new TextureHandle(++_nextId); Textures.Add(handle.Id,rgba.ToArray()); Created++; return handle; }
        public void UpdateTexture(TextureHandle handle, int width, int height, ReadOnlySpan<byte> rgba)
        { if (!Textures.ContainsKey(handle.Id) || rgba.Length != width*height*4) throw new ArgumentException("Texture update");
          Textures[handle.Id]=rgba.ToArray(); Updated++; }
        public void ReleaseTexture(TextureHandle handle)
        { if (!Textures.Remove(handle.Id)) throw new InvalidOperationException("Texture released twice"); Released++; }

        public RenderTargetHandle CreateRenderTarget(int width, int height) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public TextureHandle GetRenderTargetTexture(RenderTargetHandle handle) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetRenderTarget(RenderTargetHandle handle) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetRenderTargetDefault() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void ReleaseRenderTarget(RenderTargetHandle handle) => throw new NotSupportedException("Unexpected test-renderer operation.");

        public void AddPointLight(Vector3 position, Vector3 color, float radius, float intensity = 1f, float falloff = 2f)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void ClearPointLights() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void AddFogVolume(FogVolume volume) => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void ClearFogVolumes() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void AddSmokeVolume(Vector3 center, float radius, float density)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void ClearSmokeVolumes() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetWeatherMapRgba8(
            ReadOnlySpan<byte> rgba, int width, int height, float worldHalfExtent, Vector2 worldCenter = default)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetChunkBounds(int chunkId, Vector3 boundsMin, Vector3 boundsMax)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void ClearChunkBounds() => throw new NotSupportedException("Unexpected test-renderer operation.");

        public void SetPreviewShaderOverride(
            string hlslSourceCode,
            string sourcePath = null,
            ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline,
            string projectPath = null,
            string entryPoint = null,
            string compilerProfile = null)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void ApplyPreviewShaderBytecode(byte[] pixelShaderBytecode, ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetPreviewShaderProgramOverride(
            string hlslSourceCode,
            string vertexEntryPoint,
            string pixelEntryPoint,
            string sourcePath = null,
            ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline,
            string projectPath = null)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void ClearPreviewShaderOverride() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public RuntimeShaderHandle RegisterRuntimeShader(string hlslSourceCode, string entryPoint, ShaderPreviewProfile profile, string sourcePath = null, string projectPath = null)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public RuntimeShaderHandle RegisterRuntimeShaderProgram(
            string hlslSourceCode,
            string vertexEntryPoint,
            string pixelEntryPoint,
            ShaderPreviewProfile profile,
            string sourcePath = null,
            string projectPath = null)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void ReleaseRuntimeShader(RuntimeShaderHandle handle, ShaderPreviewProfile profile)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetShaderPreviewTextures(in AuthoredShaderTextures textures)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void DrawShaderPreviewFullscreen(int x = 0, int y = 0, int width = 0, int height = 0)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void ResetShaderPreviewTime() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void SetShaderPreviewParameters(Vector4 row0, Vector4 row1, Vector4 row2, Vector4 row3)
            => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void AdvanceShaderPreviewTime(float deltaSeconds) => throw new NotSupportedException("Unexpected test-renderer operation.");

        public RenderStats GetStats() => throw new NotSupportedException("Unexpected test-renderer operation.");
        public double LastGpuMilliseconds => throw new NotSupportedException("Unexpected test-renderer operation.");
        public void Dispose() { Textures.Clear(); }
    }
}
