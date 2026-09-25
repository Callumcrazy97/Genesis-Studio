using System;
using System.IO;
using System.Numerics;
using Genesis.Rendering.Meshes;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project
{
    /// <summary>Work-driven in-process loader. There is no timer, fake percentage or forced delay.</summary>
    public sealed class ProjectBootSplash : IDisposable
    {
        private readonly string _projectPath;
        private readonly ProjectLogger _logger;
        private readonly string _startRoom;
        private readonly AssetWarmCache _warm = new();
        private readonly MeshDrawCall[] _draw = new MeshDrawCall[1];
        private IRenderController _renderer;
        private TextureHandle _logo;
        private MeshHandle _cube;
        private bool _submitted, _presented, _initialized;
        public bool IsComplete => _initialized && _presented && _warm.JobsDone == _warm.JobsTotal;
        public int JobsDone => _warm.JobsDone + (_presented ? 1 : 0);
        public int JobsTotal => _warm.JobsTotal + 1;
        public float Progress => JobsDone / (float)JobsTotal;
        public string Status => IsComplete ? "Ready" : _warm.JobsDone < _warm.JobsTotal
            ? $"Preparing assets {_warm.JobsDone}/{_warm.JobsTotal}: {Path.GetFileName(_warm.CurrentPath)}"
            : "Presenting the prepared graphics frame";

        public ProjectBootSplash(string projectPath, ProjectLogger logger, string startRoom = null)
            => (_projectPath, _logger, _startRoom) = (projectPath, logger, startRoom);

        public void Initialize(IRenderController renderer)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            _renderer = renderer;
            string logo = GenesisBranding.ResolveSplashPath();
            if (!string.IsNullOrEmpty(logo) && File.Exists(logo)) _logo = renderer.LoadTexture(logo);
            _warm.BuildCriticalList(_projectPath, _startRoom);
            _initialized = true;
            _logger?.Line($"Measured runtime preload: {_warm.JobsTotal} asset jobs + one presented graphics warmup.");
        }
        public void Update(float dt, IRenderController renderer)
        {
            if (!_initialized || IsComplete) return;
            _warm.WarmStep(renderer, maxItems: 4);
        }
        public void DrawLogo(IRenderController renderer)
        {
            if (!_initialized || IsComplete) return;
            if (!_submitted)
            {
                _cube = MeshGeometry.RegisterCube(renderer, RenderColor.White, 0.01f);
                renderer.SetCamera3D(Matrix4x4.Identity, Matrix4x4.Identity);
                renderer.SetMesh3DState(Mesh3DState.Default);
                _draw[0] = new MeshDrawCall { Mesh = _cube, World = Matrix4x4.CreateTranslation(0, -1000, 0), Tint = RenderColor.White, Alpha = 1f };
                renderer.DrawMeshBatch(_draw);
                _submitted = true;
            }
            if (!_logo.IsValid) return;
            float size = MathF.Min(renderer.PixelWidth, renderer.PixelHeight) * 0.45f;
            renderer.SetCamera2D(renderer.PixelWidth / 2f, renderer.PixelHeight / 2f, 1f, 0f);
            var sprite = new SpriteDrawCall { Texture = _logo, X = renderer.PixelWidth / 2f,
                Y = renderer.PixelHeight * 0.43f, Width = size, Height = size,
                OriginX = size / 2, OriginY = size / 2, Tint = RenderColor.White, Alpha = 1 };
            renderer.DrawSprite(sprite);
        }
        /// <summary>Called only after Present returned successfully, never after a swallowed exception.</summary>
        public void NotifyPresented() { if (_submitted) _presented = true; }
        public void DrawOverlay(IOverlayCanvas canvas, int width, int height)
        {
            if (IsComplete || canvas == null) return;
            float bw = MathF.Min(480, width * .72f), x = (width - bw) / 2, y = height * .77f;
            canvas.DrawTextCentered(GenesisBranding.EngineName, width / 2f, height * .68f, width, 24,
                new Vector4(.92f, .96f, 1, 1), bold: true);
            canvas.DrawRect(x, y, bw, 8, new Vector4(.1f, .14f, .2f, 1), filled: true);
            canvas.DrawRect(x, y, bw * Progress, 8, new Vector4(.35f, .8f, 1, 1), filled: true);
            canvas.DrawTextCentered(Status, width / 2f, y + 22, width * .9f, 15, new Vector4(.65f, .74f, .84f, 1));
        }
        public void Dispose()
        {
            if (_renderer != null)
            {
                if (_cube.IsValid) _renderer.ReleaseMesh(_cube);
                // LoadTexture is renderer-cached and may be shared by gameplay. The renderer owns it.
            }
            _cube = MeshHandle.Invalid; _logo = TextureHandle.Invalid; _renderer = null;
        }
    }
}
