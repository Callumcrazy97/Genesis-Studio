using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Genesis.Runtime.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>Live, unsaved asset preview shared by terrain source and entity wizards.</summary>
public sealed class TerrainAssetPreview : UserControl
{
    private sealed class DrawList : IMeshDrawList
    {
        public readonly List<MeshDrawCall> Items = [];
        public int Count => Items.Count;
        public void Clear() => Items.Clear();
        public void Add(in MeshDrawCall call) => Items.Add(call);
        public int CopyTo(MeshDrawCall[] buffer, int startIndex) { foreach (var draw in Items) { if (startIndex == buffer.Length) break; buffer[startIndex++] = draw; } return startIndex; }
    }
    private readonly string _project;
    private readonly Func<TerrainEntityDocument>? _getDocument;
    private readonly RuntimeModelRenderSystem _models = new(
        assetFreshnessIntervalMilliseconds: 500,
        textureFreshnessIntervalMilliseconds: 1000);
    private readonly RuntimeModelAssetRegistry _assets = new();
    private readonly DrawList _draws = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 42, ForeColor = EditorChrome.Muted, Padding = new Padding(10) };
    private GModelAsset? _mesh;
    private MeshHandle _quad;
    private string _framed = "", _particlePath = "";
    private readonly List<ParticleSimulation> _particles = [];
    private ParticleConfig? _particleEffect;
    private readonly Dictionary<string, (DateTime Stamp, ShaderAssetDocument Asset)> _shaders = [];
    private readonly TerrainImagePreviewCache _images = new();
    private float _lastTime;
    private bool _disposed;
    public EditorViewport3D Viewport { get; } = new() { Dock = DockStyle.Fill, ControlMethod = EditorCameraControlMethod.Orbit };
    public float TimeSeconds => (float)_clock.Elapsed.TotalSeconds;
    public string PreviewStatus => _status.Text;

    public TerrainAssetPreview(string projectRoot, Func<TerrainEntityDocument>? document = null)
    {
        _project = projectRoot; _getDocument = document; Dock = DockStyle.Fill; BackColor = EditorChrome.Canvas;
        var title = new Label { Text = "LIVE PREVIEW · Playing", Dock = DockStyle.Top, Height = 32, Padding = new Padding(10, 6, 0, 0), ForeColor = EditorChrome.Text };
        Controls.Add(Viewport); Controls.Add(_status); Controls.Add(title);
        void FitLabels()
        {
            title.Height = title.Font.Height + title.Padding.Vertical + 8;
            _status.Height = TextRenderer.MeasureText(_status.Text, _status.Font,
                new Size(Math.Max(120, ClientSize.Width - _status.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak).Height + _status.Padding.Vertical;
        }
        title.FontChanged += (_, _) => FitLabels();
        _status.FontChanged += (_, _) => FitLabels();
        _status.TextChanged += (_, _) => FitLabels();
        SizeChanged += (_, _) => FitLabels();
        Viewport.Camera.Target = new Vector3(0, .5f, 0); Viewport.Camera.Distance = 3;
        Viewport.DrawScene += Draw;
        _timer.Tick += (_, _) => { if (Visible) { Viewport.Host.AdvanceSceneTime(.033f); Viewport.Invalidate(); } };
        HandleCreated += (_, _) => _timer.Start();
    }
    public void SetMesh(GModelAsset model)
    {
        _models.InvalidateAssets(Viewport.Host.Renderer); _mesh = model; Frame(model, 1);
    }
    private void Frame(GModelAsset model, float scale)
    {
        Viewport.Camera.Target = (model.Bounds.Min + model.Bounds.Max) * (.5f * scale);
        Viewport.FloorHeight = model.Bounds.Min.Y * scale - .02f;
        float distance = Math.Max(2, Vector3.Distance(model.Bounds.Min, model.Bounds.Max) * scale * 1.3f);
        Viewport.Camera.MaximumDistance = Math.Max(1600, distance * 3); Viewport.Camera.Distance = distance; Viewport.FarPlane = Math.Max(900, distance * 4);
    }
    private static float Number(TerrainEntityComponent? component, string key, float fallback) => float.TryParse(component?.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private string PathOf(string reference) => ResourceNames.Resolve(_project, reference);
    private void EnsureQuad(IRenderController renderer)
    {
        if (_quad.IsValid) return;
        MeshVertex V(float x, float y, float u, float v) => new() { Position = new(x, y, 0), Normal = Vector3.UnitZ, UV = new(u, v), Color = Vector4.One };
        _quad = renderer.RegisterMesh(new[] { V(-.5f, 0, 0, 1), V(.5f, 0, 1, 1), V(.5f, 1, 1, 0), V(-.5f, 1, 0, 0) }, new ushort[] { 0, 1, 2, 0, 2, 3 });
    }
    private void Draw(IRenderController renderer)
    {
        try
        {
            float time = TimeSeconds, dt = Math.Clamp(time - _lastTime, 0, .1f); _lastTime = time;
            if (_mesh is not null)
            {
                _models.DrawAsset(_mesh, _project, Matrix4x4.Identity, default, renderer);
                _status.Text = "Terrain preview · RMB orbit · wheel zoom"; return;
            }
            if (_getDocument is null) { _status.Text = "Generate a preview to see the terrain."; return; }
            var document = _getDocument();
            var model = document.Components.FirstOrDefault(c => c.Enabled && c.Type == TerrainEntityComponentKinds.Model);
            var texture = document.Components.FirstOrDefault(c => c.Enabled && c.Type == TerrainEntityComponentKinds.Texture);
            var shader = document.Components.FirstOrDefault(c => c.Enabled && c.Type == TerrainEntityComponentKinds.Shader);
            _draws.Items.Clear();
            string modelPath = model?.Get("Model") ?? "";
            int frame = 0;
            if (texture is not null && !string.IsNullOrWhiteSpace(texture.Get("Texture")))
            {
                var sprite = SpriteAssetLoader.Load(PathOf(texture.Get("Texture")));
                int available = Math.Max(1, sprite.Frames.Count), requested = (int)Number(texture, "FrameCount", 0);
                int count = requested <= 0 ? available : Math.Min(available, requested);
                frame = (int)(time * Math.Max(0, Number(texture, "AnimationFps", 0))) % Math.Max(1, count);
            }
            MeshDrawCall? image = texture is null ? null : _images.Material(renderer, _project, texture.Get("Texture"), frame);
            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                if (modelPath.EndsWith(".gmodel", StringComparison.OrdinalIgnoreCase)) modelPath = PathOf(modelPath);
                float scale = Math.Max(.001f, Number(model, "Scale", 1));
                var asset = _assets.Load(_project, modelPath);
                if (_framed != modelPath) { Frame(asset, scale); _framed = modelPath; }
                _models.Enqueue(_draws, _project, modelPath, "", Matrix4x4.CreateScale(scale),
                    new Draw3DComponent { Visible = true, CastShadows = true }, new ModelRendererComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 },
                    new RuntimeModelAnimationState(model!.Get("AnimationClip"), time, Number(model, "AnimationFps", 60), true), renderer);
            }
            else if (texture is not null || shader is not null || document.Type is TerrainEntityType.Fluid or TerrainEntityType.Terrain)
            {
                float scale = Math.Max(.001f, Number(texture, "Scale", 1));
                string mode = texture?.Get("Mode") ?? nameof(TerrainEntityTextureMode.Plane3D);
                MeshHandle geometry;
                if (image.HasValue)
                    geometry = _images.Geometry(renderer, _project, texture!.Get("Texture"), frame, mode);
                else
                {
                    EnsureQuad(renderer); geometry = _quad;
                }
                Matrix4x4 rotation = TerrainTextureGeometry.Facing(mode, Matrix4x4.Identity, Viewport.Camera.Eye);
                _draws.Items.Add(new MeshDrawCall { Mesh = geometry, World = Matrix4x4.CreateScale(scale) * rotation, Tint = RenderColor.White, Alpha = 1,
                    Flags = MeshRasterDefaults.ApplyOverride(MeshDrawFlags.Transparent, FaceCullingOverride.None, document.WindingOrder) });
            }
            for (int drawIndex = 0; drawIndex < _draws.Items.Count; drawIndex++)
            {
                var draw = _draws.Items[drawIndex];
                if (image is { } material)
                {
                    draw.Texture = material.Texture; draw.NormalMap = material.NormalMap; draw.OrmMap = material.OrmMap;
                    draw.SurfaceParams = material.SurfaceParams; draw.DetailParams = material.DetailParams;
                }
                if (shader is not null && !string.IsNullOrWhiteSpace(shader.Get("Shader")) && ObjectDrawPass.TryApplyMeshShader(renderer, _project, shader.Get("Shader"), ref draw))
                {
                    string path = PathOf(shader.Get("Shader")); DateTime stamp = File.GetLastWriteTimeUtc(path);
                    if (!_shaders.TryGetValue(path, out var cached) || cached.Stamp != stamp)
                    {
                        var loaded = ShaderAssetDocument.Load(path); ShaderParameterReflection.Synchronize(loaded); _shaders[path] = cached = (stamp, loaded);
                    }
                    var asset = cached.Asset;
                    var overrides = new Dictionary<string, float[]>();
                    foreach (var parameter in asset.Parameters)
                    {
                        var values = (float[])parameter.Value.Clone(); for (int i = 0; i < values.Length; i++) values[i] = Number(shader, $"Parameter.{parameter.Name}.{i}", values[i]);
                        overrides[parameter.Name] = values;
                    }
                    ShaderParameterReflection.Pack(asset, overrides, out draw.ShaderParams0, out draw.ShaderParams1, out draw.ShaderParams2, out draw.ShaderParams3);
                }
                _draws.Items[drawIndex] = draw;
                renderer.DrawMesh(draw);
            }
            var particle = document.Components.FirstOrDefault(c => c.Enabled && c.Type == TerrainEntityComponentKinds.ParticleEmitter);
            string particlePath = particle?.Get("Particle") ?? "";
            if (!string.IsNullOrWhiteSpace(particlePath))
            {
                if (_particlePath != particlePath)
                {
                    _particleEffect = ParticleAssetLoader.Load(_project, particlePath);
                    _particles.Clear();
                    foreach ((string _, string _, ParticleConfig config) in ParticleAssetLoader.EnumerateEnabledEmitters(_particleEffect))
                    {
                        config.MaxParticles = Math.Min(5000, config.MaxParticles);
                        config.Loop = true;
                        ParticleSimulation simulation = new();
                        simulation.LoadConfig(config);
                        _particles.Add(simulation);
                    }
                    _particlePath = particlePath;
                }
                EnsureQuad(renderer);
                foreach (ParticleSimulation simulation in _particles)
                {
                    simulation.Step(dt);
                    simulation.DrawInstances3D(renderer, new[] { _quad }, default, Viewport.Camera.Eye, Viewport.Camera.Forward);
                }
                if (_particleEffect?.Light is { Enabled: true } light && light.Intensity > 0 && light.Radius > 0)
                {
                    float amount = (float)Math.Clamp(light.FlickerAmount, 0d, 1d);
                    float wave = MathF.Sin(time * (float)Math.Max(.1, light.FlickerFrequency) * MathF.Tau);
                    ParticleColor color = light.Color ?? new ParticleColor(1f, .45f, .1f, 1f);
                    renderer.AddPointLight(new Vector3((float)light.OffsetX, (float)light.OffsetY, (float)light.OffsetZ),
                        new Vector3(color.R, color.G, color.B), (float)light.Radius,
                        (float)light.Intensity * (1f - amount * .5f + wave * amount * .5f), (float)Math.Max(.1, light.Falloff));
                }
            }
            _status.Text = _draws.Count == 0 && string.IsNullOrEmpty(particlePath) ? "Add a Model, Texture or Particle System to preview your asset."
                : $"Playing · frame {frame + 1} · {_particles.Sum(simulation => simulation.ActiveCount)} particles · RMB orbit";
            if (!string.IsNullOrEmpty(_images.Error)) _status.Text = "Preview: " + _images.Error;
        }
        catch (Exception exception) { _status.Text = "Preview: " + exception.Message; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _timer.Stop(); _timer.Dispose();
            var renderer = Viewport.Host.Renderer; _models.InvalidateAssets(renderer);
            if (_quad.IsValid && renderer is not null) renderer.ReleaseMesh(_quad);
            _images.Dispose();
        }
        base.Dispose(disposing);
    }
}
