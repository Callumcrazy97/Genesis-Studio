using System;
using System.Numerics;
using Genesis.Shared.Materials;

namespace Genesis.Shared.Interfaces
{
    // Abstract rendering API.  No Silk.NET / SharpDX types appear here.
    // Swap backends by changing which class implements this interface.
    public interface IRenderController : IDisposable, IRenderCommandSink
    {
        // ── Lifecycle ───────────────────────────────────────────────────────────
        void   Initialize(IntPtr windowHandle, int width, int height);
        void   Resize(int width, int height);
        bool   TryResize(int width, int height);
        void   Shutdown();
        bool   IsInitialized { get; }
        bool   IsFramebufferReady { get; }
        string BackendName   { get; }
        /// <summary>GPU / device adapter string reported by the active backend (for F6 / diagnostics).</summary>
        string AdapterName   { get; }
        /// <summary>
        /// True when the active rendering backend exposes hardware compute suitable for GPU particle simulation.
        /// Software/reference renderers leave this false.
        /// </summary>
        bool SupportsComputeShaders => false;
        /// <summary>
        /// True when the active backend supports indirect GPU draws. Phase 1 exposes this alongside compute
        /// so particle routing never guesses capability from a backend name.
        /// </summary>
        bool SupportsIndirectDraw => false;
        int    PixelWidth    { get; }
        int    PixelHeight   { get; }

        // ── Frame control ───────────────────────────────────────────────────────
        void BeginFrame();
        /// <summary>Starts another camera within the acquired frame, clearing per-view submits and lights.</summary>
        void BeginCameraPass(bool postProcessing) { }
        void Clear(float r, float g, float b, float a = 1f);
        void EndFrame();
        void Present();
        void SetVSync(bool vsync);

        /// <summary>When false, EndFrame skips the 3D forward pass (pure 2D demos).</summary>
        void Set3DFrameActive(bool active);

        // ── Viewport / state ────────────────────────────────────────────────────
        void SetViewport(int x, int y, int width, int height);
        void SetBlendMode(BlendMode mode);
        void SetSamplerState(SamplerFilter filter);
        /// <summary>Configure 2D room fog. HUD/GUI overlay flushes are always rendered with fog disabled.</summary>
        void SetRoomFog(RoomFogState state);

        // ── 2D drawing ──────────────────────────────────────────────────────────
        new void DrawSprite(in SpriteDrawCall call);
        new void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls);
        /// <summary>Flush sprites submitted after <see cref="EndFrame"/> (e.g. HUD icons over D2D).</summary>
        void FlushOverlaySprites();
        void DrawLine(float x1, float y1, float x2, float y2, RenderColor color, float thickness = 1f, int depth = -10000);
        void DrawRect(float x, float y, float w, float h, RenderColor color, bool filled = true, int depth = -10000);
        /// <summary>
        /// Screen-space text at pixel coordinates, drawn on the shared overlay and composited with
        /// the rest of it. Queued, not immediate: it appears when the overlay is composed, which is
        /// at <see cref="ComposeOverlay"/> or, failing that, before <see cref="Present"/>.
        /// </summary>
        void DrawText(string text, float x, float y, float size, RenderColor color);

        // ── Overlay ─────────────────────────────────────────────────────────────
        /// <summary>
        /// Draws HUD text and vector elements over the finished frame through a backend-neutral
        /// canvas, then composites them. Returns false when there is no presentable surface.
        /// </summary>
        /// <remarks>
        /// Callers receive <see cref="IOverlayCanvas"/> rather than any graphics-API type, which is
        /// what lets a boot splash, a debug banner or a scripted HUD be authored once and render
        /// identically on every backend — the commands are recorded and rasterised by one shared
        /// text stack, not issued against Direct2D.
        /// </remarks>
        bool ComposeOverlay(Action<IOverlayCanvas> draw);

        // ── Readback ────────────────────────────────────────────────────────────
        /// <summary>
        /// Copies the last rendered frame into tightly-packed BGRA bytes (byte-identical in memory
        /// to GDI+ <c>Format32bppArgb</c>).
        /// </summary>
        /// <remarks>
        /// Call after <see cref="EndFrame"/> and <b>before</b> <see cref="Present"/> — flip-model
        /// presentation recycles the back buffer. This is what unblocks headless screenshot
        /// verification, because <c>Control.DrawToBitmap</c> renders a hardware viewport black.
        /// </remarks>
        bool TryReadFramePixels(out int width, out int height, out byte[] bgra);

        /// <summary>
        /// Reads the current back buffer after <see cref="IRenderBackend.EndFrame"/> has already
        /// submitted it, without ending the frame a second time.
        /// </summary>
        bool TryReadSubmittedFramePixels(out int width, out int height, out byte[] bgra);

        // ── 3D drawing ──────────────────────────────────────────────────────────
        new void DrawMesh(in MeshDrawCall call);
        new void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls);
        /// <summary>
        /// Submit one material/mesh template with many transforms. Backends retain a single
        /// instanced draw batch rather than expanding this into individual draw commands.
        /// </summary>
        void DrawMeshInstances(in MeshDrawCall template, ReadOnlySpan<MeshInstanceData> instances);

        // ── 3D mesh management ───────────────────────────────────────────────────
        MeshHandle RegisterMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<ushort> indices);
        MeshHandle RegisterSkinnedMesh(ReadOnlySpan<SkinnedMeshVertex> vertices, ReadOnlySpan<ushort> indices);
        SkinPaletteHandle CreateSkinPalette(int matrixCount);
        void       UpdateSkinPalette(SkinPaletteHandle handle, ReadOnlySpan<Matrix4x4> matrices);
        void       ReleaseSkinPalette(SkinPaletteHandle handle);
        /// <summary>
        /// Merge several mesh parts (each with its own transform) into one static GPU mesh.
        /// Use for region batching in any game type — voxels, terrain tiles, static props.
        /// </summary>
        MeshHandle RegisterCombinedMesh(ReadOnlySpan<MeshCombinePart> parts);
        /// <summary>Upload new vertex data into an existing mesh handle (same vertex/index count). Used for CPU-skinned animation.</summary>
        void       UpdateMesh(MeshHandle handle, ReadOnlySpan<MeshVertex> vertices);
        void       ReleaseMesh(MeshHandle handle);
        void       SetMesh3DState(Mesh3DState state);
        MeshHandle GetBuiltinMesh(BuiltinMeshKind kind);
        void       Advance3DTime(float deltaSeconds);

        // ── Camera ──────────────────────────────────────────────────────────────
        void SetCamera2D(float x, float y, float zoom, float rotation);
        void SetCamera3D(Matrix4x4 view, Matrix4x4 projection);

        // ── Texture management ──────────────────────────────────────────────────
        TextureHandle LoadTexture(string path);
        TextureHandle LoadTexture(string path, TextureColorSpace colorSpace) => LoadTexture(path);
        TextureHandle CreateTexture(int width, int height, ReadOnlySpan<byte> rgba);
        void UpdateTexture(TextureHandle handle, int width, int height, ReadOnlySpan<byte> rgba);
        void ReleaseTexture(TextureHandle handle);

        // ── Render targets ──────────────────────────────────────────────────────
        RenderTargetHandle CreateRenderTarget(int width, int height);
        /// <summary>
        /// The target's colour attachment as a sampleable texture, so a later pass can read what
        /// was rendered into it. Invalid if the handle is unknown or already released.
        /// </summary>
        TextureHandle GetRenderTargetTexture(RenderTargetHandle handle);
        void SetRenderTarget(RenderTargetHandle handle);
        void SetRenderTargetDefault();
        void ReleaseRenderTarget(RenderTargetHandle handle);

        // ── Lighting ─────────────────────────────────────────────────────────────
        /// <summary>
        /// Add a dynamic point light for this frame.  Call before <see cref="EndFrame"/>;
        /// cleared automatically at <see cref="BeginFrame"/>.  Max 8 lights.
        /// </summary>
        void AddPointLight(
            Vector3 position,
            Vector3 color,
            float radius,
            float intensity = 1f,
            float falloff = 2f);

        /// <summary>Remove all point lights added since the last BeginFrame.</summary>
        void ClearPointLights();

        /// <summary>
        /// Add a placeable analytic fog volume for this frame (terrain-and-rendering-fix-plan.md
        /// Issue 6, Stage 1). Evaluated in the screen-space fog post pass only — does not affect
        /// the forward pass. Call before <see cref="EndFrame"/>; cleared automatically at
        /// <see cref="BeginFrame"/>. Max 8 volumes, same submit-per-frame contract as
        /// <see cref="AddPointLight"/>.
        /// </summary>
        void AddFogVolume(FogVolume volume);

        /// <summary>Remove all fog volumes added since the last BeginFrame.</summary>
        void ClearFogVolumes();

        /// <summary>
        /// Add an analytic smoke sphere for this frame (AF1.6). Consumed as a single Beer-Lambert
        /// transmittance term in the fog composite — it darkens what is seen through it rather than
        /// adding a second fog. Ignored entirely unless smoke extinction is enabled. Same
        /// submit-per-frame contract and 8-volume cap as <see cref="AddFogVolume"/>.
        /// </summary>
        void AddSmokeVolume(Vector3 center, float radius, float density);

        /// <summary>Remove all smoke volumes added since the last BeginFrame.</summary>
        void ClearSmokeVolumes();

        /// <summary>
        /// AF2.3: upload the AF2.2 weather-map RGBA8 grid for this frame's raymarched cloud pass
        /// (R=Coverage, G=Type, B=Erosion, A=Vertical). Cleared at BeginSubmitFrame; must be set
        /// again each frame when raymarched clouds are enabled.
        /// </summary>
        void SetWeatherMapRgba8(ReadOnlySpan<byte> rgba, int width, int height, float worldHalfExtent, Vector2 worldCenter = default);

        // ── Large-world chunk culling ────────────────────────────────────────────
        /// <summary>
        /// Register (or update) an axis-aligned bounding box for a chunk ID.  Meshes
        /// submitted with <see cref="MeshDrawCall.ChunkId"/> matching this ID are
        /// frustum-rejected at the chunk level before per-instance tests.
        /// Pass <paramref name="chunkId"/> = 0 to clear a specific chunk.
        /// </summary>
        void SetChunkBounds(int chunkId, Vector3 boundsMin, Vector3 boundsMax);

        /// <summary>Remove all registered chunk bounds.</summary>
        void ClearChunkBounds();

        // ── Editor Shader Overrides ─────────────────────────────────────────────
        /// <summary>
        /// Compiles and sets an override Pixel Shader for live preview in the editor.
        /// If compilation fails, it throws; if successful, subsequent draws will use it.
        /// </summary>
        void SetPreviewShaderOverride(
            string hlslSourceCode,
            string sourcePath = null,
            ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline,
            string projectPath = null,
            string entryPoint = null,
            string compilerProfile = null);

        /// <summary>
        /// Compiles and installs an authored vertex/pixel program for editor preview. An empty
        /// vertex entry keeps the engine vertex stage; this preserves skinning and legacy shaders.
        /// </summary>
        void SetPreviewShaderProgramOverride(
            string hlslSourceCode,
            string vertexEntryPoint,
            string pixelEntryPoint,
            string sourcePath = null,
            ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline,
            string projectPath = null);

        /// <summary>Installs precompiled MainPS bytecode (compile may run off the UI thread).</summary>
        void ApplyPreviewShaderBytecode(byte[] pixelShaderBytecode, ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline);

        /// <summary>Removes any active preview shader override.</summary>
        void ClearPreviewShaderOverride();

        /// <summary>Compile and retain an authored runtime pixel shader for this active backend.</summary>
        RuntimeShaderHandle RegisterRuntimeShader(string hlslSourceCode, string entryPoint, ShaderPreviewProfile profile, string sourcePath = null, string projectPath = null);

        /// <summary>Compile and retain an authored runtime vertex/pixel program.</summary>
        RuntimeShaderHandle RegisterRuntimeShaderProgram(
            string hlslSourceCode,
            string vertexEntryPoint,
            string pixelEntryPoint,
            ShaderPreviewProfile profile,
            string sourcePath = null,
            string projectPath = null);

        /// <summary>Release a handle returned by <see cref="RegisterRuntimeShader"/>.</summary>
        void ReleaseRuntimeShader(RuntimeShaderHandle handle, ShaderPreviewProfile profile);

        /// <summary>Set extra Texture2D resources reflected from the authored preview shader.</summary>
        void SetShaderPreviewTextures(in AuthoredShaderTextures textures);

        /// <summary>Draw the active fullscreen-effect preview shader into the given pixel region.</summary>
        void DrawShaderPreviewFullscreen(int x = 0, int y = 0, int width = 0, int height = 0);

        /// <summary>Reset animated shader-editor time/frame counters.</summary>
        void ResetShaderPreviewTime();

        /// <summary>Set the reflected <c>GenesisParameters</c> values bound at pixel slot b5.</summary>
        void SetShaderPreviewParameters(Vector4 row0, Vector4 row1, Vector4 row2, Vector4 row3);

        /// <summary>Advance shader-editor Time/Frame constants (play/pause in the editor).</summary>
        void AdvanceShaderPreviewTime(float deltaSeconds);

        // ── Debug / metrics ─────────────────────────────────────────────────────
        RenderStats GetStats();

        /// <summary>
        /// GPU milliseconds for the last resolved frame. Ring-buffered and non-stalling, so the
        /// value lags the current frame by a few frames and reads 0 until the first resolves.
        /// </summary>
        double LastGpuMilliseconds { get; }
    }
}
