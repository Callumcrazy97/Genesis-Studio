using System;
using System.IO;
using System.Numerics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scene;
using Genesis.Shared.Audio;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scripting
{
    /// <summary>
    /// The engine services a gameplay <see cref="EntityBehavior"/> may use without ever
    /// owning the engine. The host (runtime player or editor sandbox) creates the window,
    /// renderer and main loop, then exposes this façade so project scripts can read input,
    /// drive the camera, upload meshes, load project assets and draw a HUD — all inside the
    /// engine's existing lifecycle.
    ///
    /// A behaviour never calls <c>new SilkGameWindow(...)</c>, never creates an
    /// <see cref="IRenderController"/>, and never runs its own loop. It reaches everything
    /// it needs through <see cref="EntityBehavior.Game"/>.
    /// </summary>
    public interface IGameContext
    {
        // ── Scene / world ──────────────────────────────────────────────────────────
        RuntimeScene Scene  { get; }
        EcsWorld     World  { get; }
        Camera3D     Camera { get; }
        InputState   Input  { get; }

        /// <summary>The configuration of the room currently being played.</summary>
        RoomAsset Room { get; }

        // ── Diagnostics ────────────────────────────────────────────────────────────
        /// <summary>Write a line to the engine log (the host's logger), for gameplay diagnostics.</summary>
        void Log(string message);

        // ── Timing ─────────────────────────────────────────────────────────────────
        float DeltaTime  { get; }
        float TotalTime  { get; }
        long  FrameCount { get; }

        // ── Window / cursor ────────────────────────────────────────────────────────
        int  ScreenWidth   { get; }
        int  ScreenHeight  { get; }
        /// <summary>Live OS window client width (may differ from the swap chain when stretched).</summary>
        int  ClientWidth   { get; }
        /// <summary>Live OS window client height.</summary>
        int  ClientHeight  { get; }
        /// <summary>Back-buffer / overlay layout width (swap-chain pixels).</summary>
        int  RenderWidth   { get; }
        /// <summary>Back-buffer / overlay layout height.</summary>
        int  RenderHeight  { get; }
        /// <summary>Scale from client mouse X to overlay/render X.</summary>
        float OverlayScaleX { get; }
        /// <summary>Scale from client mouse Y to overlay/render Y.</summary>
        float OverlayScaleY { get; }
        /// <summary>Map a client-space mouse position to overlay/render coordinates for HUD hit tests.</summary>
        Vector2 MapMouseToOverlay(Vector2 clientPosition);
        bool MouseCaptured { get; }
        void SetMouseCaptured(bool captured);
        /// <summary>Show, hide, or lock the OS cursor (Normal / Hidden / Locked).</summary>
        void SetCursorMode(CursorMode mode);

        /// <summary>Request a room change; honoured by the host at a frame boundary.</summary>
        void ChangeRoom(string roomName);

        // ── Presentation (host-owned; live settings menus drive these) ─────────────
        /// <summary>Current vsync state declared by the room / set live.</summary>
        bool       VSyncEnabled { get; }
        /// <summary>Current frame-rate cap (0 = uncapped).</summary>
        int        TargetFps    { get; }
        /// <summary>Frames shown per second, counted over the last half second; 0 where there is no window.</summary>
        double     MeasuredFps  => 0;
        /// <summary>Current window presentation mode.</summary>
        WindowMode WindowMode   { get; }
        /// <summary>Whether the game's window has the keyboard focus; true where there is no window.</summary>
        bool       WindowHasFocus => true;
        /// <summary>Whether the game's window is minimised (no frame is drawn); false where there is no window.</summary>
        bool       WindowIsMinimized => false;
        /// <summary>Enable/disable vertical sync at runtime (the host owns the swap chain).</summary>
        void SetVSync(bool enabled);
        /// <summary>Set the frame-rate cap at runtime (0 = uncapped).</summary>
        void SetTargetFps(int fps);
        /// <summary>Resize the window / back buffer at runtime.</summary>
        void SetResolution(int width, int height);
        /// <summary>Switch between windowed / borderless / fullscreen at runtime.</summary>
        void SetWindowMode(WindowMode mode);

        // ── Mesh management (GPU) ──────────────────────────────────────────────────
        MeshHandle RegisterMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<ushort> indices);
        void       ReleaseMesh(MeshHandle handle);
        MeshHandle GetBuiltinMesh(BuiltinMeshKind kind);

        // ── Project asset pipeline ─────────────────────────────────────────────────
        /// <summary>Resolve a project-relative asset path (e.g. "Textures/atlas.png") to an absolute path.</summary>
        string        ResolveAssetPath(string projectRelativePath);
        /// <summary>Load a texture from a project-relative path through the asset pipeline.</summary>
        TextureHandle LoadTexture(string projectRelativePath);
        /// <summary>Loads a texture for a specific use; lit 3D colour/data textures receive mipmaps.</summary>
        TextureHandle LoadTexture(string projectRelativePath, Genesis.Shared.Materials.TextureColorSpace colorSpace)
            => LoadTexture(projectRelativePath);
        TextureHandle CreateTexture(int width, int height, ReadOnlySpan<byte> rgba);
        void          UpdateTexture(TextureHandle handle, int width, int height, ReadOnlySpan<byte> rgba);
        void          ReleaseTexture(TextureHandle handle);

        // ── Per-frame 3D submission ─────────────────────────────────────────────────
        // A light added inside OnRenderFrame joins that frame; one added during an update is kept
        // for the next frame drawn. SetChunkBounds is valid only inside OnRenderFrame.
        void AddPointLight(Vector3 position, Vector3 color, float radius, float intensity = 1f, float falloff = 2f);
        void SetChunkBounds(int chunkId, Vector3 min, Vector3 max);

        // ── Audio ───────────────────────────────────────────────────────────────────
        /// <summary>
        /// Runtime audio service. Never null (defaults to a no-op), so behaviours can
        /// call <c>Game.Audio.Play(...)</c> freely. The host installs the real
        /// XAudio2-backed implementation; sandboxes/tests use the null one.
        /// </summary>
        IAudioSystem Audio { get; }

        /// <summary>Sample the terrain height at world XZ.</summary>
        float GetTerrainHeight(float worldX, float worldZ);

        /// <summary>
        /// Where a model's socket (or, failing that, its bone of that name) is in the world in the
        /// pose it is being drawn in. For more that a script may ask of a model (clips, a clip on
        /// part of the body, tint and glow) see <see cref="Genesis.Runtime.Modeling.ModelInstance"/>.
        /// </summary>
        bool TryGetSocketWorld(Genesis.Shared.ECS.Entity entity, string socketOrBone, out Matrix4x4 socketWorld) =>
            Genesis.Runtime.Modeling.ModelInstance.TryGetSocketWorld(World, entity, socketOrBone, out socketWorld);

        /// <summary>
        /// Plays a Particle resource once at a place; the emitter removes itself when the effect
        /// has played out. See <see cref="Genesis.Runtime.Particles.ParticleBursts"/>.
        /// </summary>
        Genesis.Shared.ECS.Entity PlayParticleBurst(string asset, Vector3 position, float scale = 1f, float emitSeconds = 0f) =>
            Genesis.Runtime.Particles.ParticleBursts.Play(World, asset, position, scale, emitSeconds);
    }

    /// <summary>
    /// 2D drawing surface handed to <see cref="EntityBehavior.OnDrawHud"/> each frame, in
    /// pixel coordinates (origin top-left). Backed by the host's overlay compositor.
    /// </summary>
    public interface IHudCanvas
    {
        int Width  { get; }
        int Height { get; }

        void Text(string text, float x, float y, float size, Vector4 color);
        /// <summary>Authored font family or project font payload; legacy canvases retain their default font.</summary>
        void Text(string text, float x, float y, float size, Vector4 color, string font)
            => Text(text, x, y, size, color);
        void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color);
        void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color, string font)
            => TextCentered(text, centerX, y, width, size, color);
        void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true);
        void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f);

        /// <summary>
        /// The width and line height, in pixels, that <see cref="Text(string, float, float, float, Vector4, string)"/>
        /// would draw this text at: for right-aligning, wrapping and fitting a panel to its label.
        /// </summary>
        Vector2 MeasureText(string text, float size, string font = null)
            => Genesis.Shared.Overlay.GlyphAtlas.Measure(text, font, size);

        /// <summary>Text with <paramref name="tracking"/> pixels of letter spacing between glyphs.</summary>
        void Text(string text, float x, float y, float size, Vector4 color, string font, float tracking)
            => Text(text, x, y, size, color, font);

        /// <summary>
        /// Limits later drawing to a rectangle until it is cleared with a zero width or height.
        /// Canvases that cannot clip draw everything.
        /// </summary>
        void SetClip(float x, float y, float width, float height) { }

        /// <summary>
        /// Whether later drawing blends in linear light rather than on the stored sRGB values, until
        /// it is set again. Canvases that cannot blend in linear light ignore it.
        /// </summary>
        void SetBlendLinear(bool linear) { }

        /// <summary>Whether <see cref="Sprite"/> draws: then GUI images are ordered with this canvas's shapes and text.</summary>
        bool SupportsSprites => false;

        /// <summary>A textured sprite in screen pixels, in order with this canvas's shapes and text.</summary>
        void Sprite(in SpriteDrawCall call) { }

        /// <summary>
        /// Many filled rectangles in order (PGSL DrawRectanglesFromList): one command on canvases
        /// that keep batches, otherwise a filled <see cref="Rect"/> for each.
        /// </summary>
        void Rects(ReadOnlySpan<GuiRectangle> rectangles)
        {
            foreach (GuiRectangle rectangle in rectangles)
                Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, rectangle.Color, filled: true);
        }

        /// <summary>A filled disc, or a ring of the given thickness.</summary>
        void Circle(float centerX, float centerY, float radius, Vector4 color, bool filled = true, float thickness = 1.5f)
            => HudShapes.Circle(this, centerX, centerY, radius, color, filled, thickness);

        /// <summary>Part of a ring: a cooldown sweep, a compass arc. Degrees run clockwise on screen from three o'clock.</summary>
        void Arc(float centerX, float centerY, float radius, float startDegrees, float sweepDegrees, Vector4 color, float thickness = 1.5f)
            => HudShapes.Arc(this, centerX, centerY, radius, startDegrees, sweepDegrees, color, thickness);

        /// <summary>A shape through these points, filled or as an outline joined back to the first point.</summary>
        void Polygon(ReadOnlySpan<Vector2> points, Vector4 color, bool filled = true, float thickness = 1.5f)
            => HudShapes.Polygon(this, points, color, filled, thickness);
    }

    /// <summary>
    /// Safe no-op context used when a behaviour runs without a host (e.g. unit tests or a
    /// preview that has not wired services yet). Never returns a usable scene; gameplay that
    /// needs the engine must run inside a real host.
    /// </summary>
    public sealed class NullGameContext : IGameContext
    {
        public static readonly NullGameContext Instance = new NullGameContext();

        /// <summary>
        /// Public so a caller can build an isolated instance with its own <see cref="Audio"/>
        /// rather than mutating the shared <see cref="Instance"/>.
        /// </summary>
        public NullGameContext() { }

        public RuntimeScene Scene  => null;
        public Camera3D     Camera => null;

        /// <summary>
        /// Settable for the same reason as <see cref="Audio"/>: a headless run that plays a real
        /// room needs a real ECS world, real input and the room asset behind them. Leaving these as
        /// hard nulls forced every such caller to reimplement the whole ~60-member interface just to
        /// supply three of them.
        /// </summary>
        public EcsWorld   World { get; set; }

        public InputState Input { get; set; }

        public RoomAsset  Room  { get; set; }

        /// <summary>Optional project root used by headless/editor-preview asset resolution.</summary>
        public string ProjectPath { get; set; }

        public void Log(string message) { }

        public float DeltaTime  => 0f;
        public float TotalTime  => 0f;
        public long  FrameCount => 0;

        public int  ScreenWidth   => 0;
        public int  ScreenHeight  => 0;
        public int  ClientWidth   => 0;
        public int  ClientHeight  => 0;
        public int  RenderWidth   => 0;
        public int  RenderHeight  => 0;
        public float OverlayScaleX => 1f;
        public float OverlayScaleY => 1f;
        public Vector2 MapMouseToOverlay(Vector2 clientPosition) => clientPosition;
        public bool MouseCaptured => false;
        public void SetMouseCaptured(bool captured) { }
        public void SetCursorMode(CursorMode mode) { }
        public void ChangeRoom(string roomName) { }

        public bool       VSyncEnabled => true;
        public int        TargetFps    => 0;
        public WindowMode WindowMode   => WindowMode.Windowed;
        public void SetVSync(bool enabled) { }
        public void SetTargetFps(int fps) { }
        public void SetResolution(int width, int height) { }
        public void SetWindowMode(WindowMode mode) { }

        public MeshHandle RegisterMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<ushort> indices) => MeshHandle.Invalid;
        public void       ReleaseMesh(MeshHandle handle) { }
        public MeshHandle GetBuiltinMesh(BuiltinMeshKind kind) => MeshHandle.Invalid;

        public string ResolveAssetPath(string projectRelativePath)
        {
            if (string.IsNullOrWhiteSpace(ProjectPath)) return projectRelativePath;
            if (string.IsNullOrWhiteSpace(projectRelativePath) || projectRelativePath == ".") return ProjectPath;
            return ResourceNames.ResolveFile(ProjectPath, projectRelativePath);
        }
        public TextureHandle LoadTexture(string projectRelativePath) => TextureHandle.Invalid;
        public TextureHandle CreateTexture(int width, int height, ReadOnlySpan<byte> rgba) => TextureHandle.Invalid;
        public void          UpdateTexture(TextureHandle handle, int width, int height, ReadOnlySpan<byte> rgba) { }
        public void          ReleaseTexture(TextureHandle handle) { }

        public void AddPointLight(Vector3 position, Vector3 color, float radius, float intensity = 1f, float falloff = 2f) { }
        public void SetChunkBounds(int chunkId, Vector3 min, Vector3 max) { }

        /// <summary>
        /// Settable so headless tests can drop in a recording audio system and assert what the
        /// PGSL Audio commands actually asked the mixer to do.
        /// </summary>
        public IAudioSystem Audio { get; set; } = NullAudioSystem.Instance;

        public float GetTerrainHeight(float worldX, float worldZ) => 0f;
    }
}
