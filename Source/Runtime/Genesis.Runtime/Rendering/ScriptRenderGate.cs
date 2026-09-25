using System;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Wraps <see cref="IRenderController"/> during script <c>OnRenderFrame</c> dispatch.
    /// Blocks direct object drawing; engine hooks (lights, fog, chunk bounds) still work.
    /// </summary>
    public sealed class ScriptRenderGate : IRenderController
    {
        private IRenderController _inner;
        private IRenderCommandSink _commands;

        public void Bind(IRenderController inner, IRenderCommandSink commands = null)
        {
            _inner = inner;
            _commands = commands ?? inner;
        }

        public void Initialize(IntPtr windowHandle, int width, int height) => _inner?.Initialize(windowHandle, width, height);
        public void Resize(int width, int height) => _inner.Resize(width, height);
        public bool TryResize(int width, int height) => _inner.TryResize(width, height);
        public void Shutdown() => _inner.Shutdown();
        public bool IsInitialized => _inner.IsInitialized;
        public bool IsFramebufferReady => _inner.IsFramebufferReady;
        public string BackendName => _inner.BackendName;
        public string AdapterName => _inner?.AdapterName ?? string.Empty;
        public int PixelWidth => _inner.PixelWidth;
        public int PixelHeight => _inner.PixelHeight;

        public void BeginFrame() => _inner.BeginFrame();
        public void Clear(float r, float g, float b, float a = 1f) => _inner.Clear(r, g, b, a);
        public void EndFrame() => _inner.EndFrame();
        public void Present() => _inner.Present();
        public void SetVSync(bool vsync) => _inner.SetVSync(vsync);
        public void Set3DFrameActive(bool active) => _inner.Set3DFrameActive(active);
        public void SetViewport(int x, int y, int width, int height) => _inner.SetViewport(x, y, width, height);
        public void SetBlendMode(BlendMode mode) => _inner.SetBlendMode(mode);
        public void SetSamplerState(SamplerFilter filter) => _inner.SetSamplerState(filter);
        public void SetRoomFog(RoomFogState state) => _inner.SetRoomFog(state);

        // Immediate-mode HUD (hotbar, menus) draws textured/colored quads during OnRenderFrame.
        public void DrawSprite(in SpriteDrawCall call)
        {
            if (!Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit)
            {
                Genesis.Shared.Rendering.RenderAutoState.DrawSubmitRejected++;
                return;
            }
            _commands.DrawSprite(call);
        }

        public void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls)
        {
            if (!Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit)
            {
                Genesis.Shared.Rendering.RenderAutoState.DrawSubmitRejected += calls.Length;
                return;
            }
            _commands.DrawSpriteBatch(calls);
        }
        public void FlushOverlaySprites() => _inner.FlushOverlaySprites();
        public void DrawLine(float x1, float y1, float x2, float y2, RenderColor color, float thickness = 1f, int depth = -10000)
            => _inner.DrawLine(x1, y1, x2, y2, color, thickness, depth);
        public void DrawRect(float x, float y, float w, float h, RenderColor color, bool filled = true, int depth = -10000)
            => _inner.DrawRect(x, y, w, h, color, filled, depth);
        // Text is a HUD element, so it follows DrawLine/DrawRect rather than the blocked mesh path.
        public void DrawText(string text, float x, float y, float size, RenderColor color)
            => _inner.DrawText(text, x, y, size, color);

        public bool ComposeOverlay(Action<IOverlayCanvas> draw) => _inner.ComposeOverlay(draw);

        public bool TryReadFramePixels(out int width, out int height, out byte[] bgra)
            => _inner.TryReadFramePixels(out width, out height, out bgra);

        public bool TryReadSubmittedFramePixels(out int width, out int height, out byte[] bgra)
            => _inner.TryReadSubmittedFramePixels(out width, out height, out bgra);

        public void DrawMesh(in MeshDrawCall call) { /* blocked for scripts */ }
        public void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls) { /* blocked for scripts */ }
        public void DrawMeshInstances(in MeshDrawCall template, ReadOnlySpan<MeshInstanceData> instances) { /* blocked for scripts */ }

        public MeshHandle RegisterMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<ushort> indices)
            => _inner.RegisterMesh(vertices, indices);
        public MeshHandle RegisterSkinnedMesh(ReadOnlySpan<SkinnedMeshVertex> vertices, ReadOnlySpan<ushort> indices)
            => _inner.RegisterSkinnedMesh(vertices, indices);
        public SkinPaletteHandle CreateSkinPalette(int matrixCount)
            => _inner.CreateSkinPalette(matrixCount);
        public void UpdateSkinPalette(SkinPaletteHandle handle, ReadOnlySpan<Matrix4x4> matrices)
            => _inner.UpdateSkinPalette(handle, matrices);
        public void ReleaseSkinPalette(SkinPaletteHandle handle)
            => _inner.ReleaseSkinPalette(handle);
        public MeshHandle RegisterCombinedMesh(ReadOnlySpan<MeshCombinePart> parts)
            => _inner.RegisterCombinedMesh(parts);
        public void UpdateMesh(MeshHandle handle, ReadOnlySpan<MeshVertex> vertices) => _inner.UpdateMesh(handle, vertices);
        public void ReleaseMesh(MeshHandle handle) => _inner.ReleaseMesh(handle);
        public void SetMesh3DState(Mesh3DState state) => _inner.SetMesh3DState(state);
        public MeshHandle GetBuiltinMesh(BuiltinMeshKind kind) => _inner.GetBuiltinMesh(kind);
        public void Advance3DTime(float deltaSeconds) => _inner.Advance3DTime(deltaSeconds);

        public void SetCamera2D(float x, float y, float zoom, float rotation) => _inner.SetCamera2D(x, y, zoom, rotation);
        public void SetCamera3D(Matrix4x4 view, Matrix4x4 projection) => _inner.SetCamera3D(view, projection);

        public TextureHandle LoadTexture(string path) => _inner.LoadTexture(path);
        public TextureHandle CreateTexture(int width, int height, ReadOnlySpan<byte> rgba) => _inner.CreateTexture(width, height, rgba);
        public void UpdateTexture(TextureHandle handle, int width, int height, ReadOnlySpan<byte> rgba)
            => _inner.UpdateTexture(handle, width, height, rgba);
        public void ReleaseTexture(TextureHandle handle) => _inner.ReleaseTexture(handle);

        public RenderTargetHandle CreateRenderTarget(int width, int height) => _inner.CreateRenderTarget(width, height);
        public TextureHandle GetRenderTargetTexture(RenderTargetHandle handle) => _inner.GetRenderTargetTexture(handle);
        public void SetRenderTarget(RenderTargetHandle handle) => _inner.SetRenderTarget(handle);
        public void SetRenderTargetDefault() => _inner.SetRenderTargetDefault();
        public void ReleaseRenderTarget(RenderTargetHandle handle) => _inner.ReleaseRenderTarget(handle);

        public void AddPointLight(Vector3 position, Vector3 color, float radius, float intensity = 1f, float falloff = 2f)
            => _inner.AddPointLight(position, color, radius, intensity, falloff);
        public void ClearPointLights() => _inner.ClearPointLights();
        public void AddFogVolume(FogVolume volume) => _inner.AddFogVolume(volume);
        public void ClearFogVolumes() => _inner.ClearFogVolumes();
        public void AddSmokeVolume(Vector3 center, float radius, float density)
            => _inner.AddSmokeVolume(center, radius, density);
        public void ClearSmokeVolumes() => _inner.ClearSmokeVolumes();
        public void SetWeatherMapRgba8(
            ReadOnlySpan<byte> rgba, int width, int height, float worldHalfExtent, Vector2 worldCenter = default)
            => _inner.SetWeatherMapRgba8(rgba, width, height, worldHalfExtent, worldCenter);
        public void SetChunkBounds(int chunkId, Vector3 boundsMin, Vector3 boundsMax)
            => _inner.SetChunkBounds(chunkId, boundsMin, boundsMax);
        public void ClearChunkBounds() => _inner.ClearChunkBounds();

        public void SetPreviewShaderOverride(
            string hlslSourceCode,
            string sourcePath = null,
            ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline,
            string projectPath = null,
            string entryPoint = null,
            string compilerProfile = null)
            => _inner.SetPreviewShaderOverride(
                hlslSourceCode,
                sourcePath,
                profile,
                projectPath,
                entryPoint,
                compilerProfile);
        public void ApplyPreviewShaderBytecode(byte[] pixelShaderBytecode, ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline)
            => _inner.ApplyPreviewShaderBytecode(pixelShaderBytecode, profile);
        public void SetPreviewShaderProgramOverride(
            string hlslSourceCode,
            string vertexEntryPoint,
            string pixelEntryPoint,
            string sourcePath = null,
            ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline,
            string projectPath = null)
            => _inner.SetPreviewShaderProgramOverride(
                hlslSourceCode,
                vertexEntryPoint,
                pixelEntryPoint,
                sourcePath,
                profile,
                projectPath);
        public void ClearPreviewShaderOverride() => _inner.ClearPreviewShaderOverride();
        public RuntimeShaderHandle RegisterRuntimeShader(string hlslSourceCode, string entryPoint, ShaderPreviewProfile profile, string sourcePath = null, string projectPath = null)
            => _inner.RegisterRuntimeShader(hlslSourceCode, entryPoint, profile, sourcePath, projectPath);
        public RuntimeShaderHandle RegisterRuntimeShaderProgram(
            string hlslSourceCode,
            string vertexEntryPoint,
            string pixelEntryPoint,
            ShaderPreviewProfile profile,
            string sourcePath = null,
            string projectPath = null)
            => _inner.RegisterRuntimeShaderProgram(
                hlslSourceCode,
                vertexEntryPoint,
                pixelEntryPoint,
                profile,
                sourcePath,
                projectPath);
        public void ReleaseRuntimeShader(RuntimeShaderHandle handle, ShaderPreviewProfile profile)
            => _inner.ReleaseRuntimeShader(handle, profile);
        public void SetShaderPreviewTextures(in AuthoredShaderTextures textures)
            => _inner.SetShaderPreviewTextures(textures);
        public void DrawShaderPreviewFullscreen(int x = 0, int y = 0, int width = 0, int height = 0)
            => _inner.DrawShaderPreviewFullscreen(x, y, width, height);
        public void ResetShaderPreviewTime() => _inner.ResetShaderPreviewTime();
        public void SetShaderPreviewParameters(Vector4 row0, Vector4 row1, Vector4 row2, Vector4 row3)
            => _inner.SetShaderPreviewParameters(row0, row1, row2, row3);
        public void AdvanceShaderPreviewTime(float deltaSeconds) => _inner.AdvanceShaderPreviewTime(deltaSeconds);

        public RenderStats GetStats() => _inner.GetStats();
        public double LastGpuMilliseconds => _inner.LastGpuMilliseconds;
        public void Dispose() => _inner.Dispose();
    }
}
