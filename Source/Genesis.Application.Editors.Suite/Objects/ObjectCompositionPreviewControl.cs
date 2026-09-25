using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Audio;
using Genesis.Runtime;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Project;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Input;
using Genesis.Physics;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Audio;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>
/// Live Object preview that instantiates the saved prefab through <see cref="PrefabSpawner"/> and
/// renders it through the same Object draw/composition services used by F5.
/// </summary>
public sealed class ObjectCompositionPreviewControl : UserControl
{
    private readonly string _projectRoot;
    private readonly EditorViewport3D _viewport = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly MeshDrawCall[] _meshBuffer = new MeshDrawCall[8192];
    private readonly FrameRenderQueue _scriptDrawQueue = new();
    private readonly ObjectPreviewDrawRecording _worldDrawRecording = new(), _guiDrawRecording = new();
    private readonly ObjectPreviewHud _hud = new();
    private readonly InputState _input = new();
    private readonly IAudioSystem _audio;
    private readonly IDisposable? _audioOwner;
    private RuntimeScene? _scene;
    private ObjectCompositionSubsystem? _composition;
    private Entity _entity = Entity.Null;
    private long _lastTicks;
    private string _audioStatus = "Runtime audio preview active";
    private ScriptHostSystem? _scripts;
    private readonly string _scriptName = "ObjectPreview_" + Guid.NewGuid().ToString("N");
    private double _accumulator;
    private string? _framedAsset;
    public bool Playing { get; set; } = true;
    public IReadOnlyDictionary<string, string>? EventSources { get; set; }
    public PgslBehavior? LiveBehavior => _scripts?.Instances.OfType<PgslBehavior>().FirstOrDefault();
    public string? ScriptError => _scripts?.LastError;
    public long SimulationFrames { get; private set; }
    public Vector3? ModelDimensions { get; private set; }
    public EditorViewport3D Viewport => _viewport;

    public ObjectCompositionPreviewControl(string projectRoot, bool compact = false)
    {
        _projectRoot = projectRoot;
        try
        {
            XAudioSystem audio = new(projectRoot);
            _audio = audio;
            _audioOwner = audio;
        }
        catch (Exception exception)
        {
            // Preview remains usable on machines/headless sessions with no XAudio device.
            _audio = NullAudioSystem.Instance;
            _audioStatus = "Audio preview unavailable: " + exception.Message;
        }
        Dock = DockStyle.Fill;
        EditorCommandBar toolbar = EditorChrome.MakeToolbar();
        _viewport.Dock = DockStyle.Fill;
        _viewport.Camera.Target = Vector3.Zero;
        _viewport.Camera.Distance = 8f;
        _viewport.Zoom2D = 4f;
        _viewport.DrawScene += Draw3D;
        _viewport.DrawScene2D += Draw2D;
        _viewport.DrawOverlay += DrawGui;
        _viewport.Host.KeyDown += (_, e) => { if (Playing) _input.OnKeyDown(PreviewKey(e.KeyCode)); };
        _viewport.Host.KeyUp += (_, e) => _input.OnKeyUp(PreviewKey(e.KeyCode));
        _viewport.Host.PreviewKeyDown += (_, e) => e.IsInputKey = true;
        _viewport.Host.MouseDown += (_, e) =>
        {
            _viewport.Host.Focus();
            if (Playing && PreviewButton(e.Button) is { } button) _input.OnMouseDown(button);
        };
        _viewport.Host.MouseUp += (_, e) => { if (PreviewButton(e.Button) is { } button) _input.OnMouseUp(button); };
        _viewport.Host.MouseMove += (_, e) => _input.OnMouseMove(e.X, e.Y);
        _viewport.Host.MouseWheel += (_, e) => { if (Playing) _input.OnWheel(e.Delta / 120f); };
        _viewport.Host.LostFocus += (_, _) => { _input.ClearHeld(); _input.NextFrame(); };
        _viewport.SceneStateFactory = () =>
        {
            var state = EditorSceneLighting.Create(showFloor: true);
            state.BackgroundColor = new Vector3(.075f, .095f, .13f); state.FogEnabled = false;
            return state;
        };
        _viewport.SelectionWorldPoint = () => Vector3.Zero;
        EditorViewportChrome.Attach(
            toolbar,
            new EditorViewportChrome.Options
            {
                Viewport = _viewport,
                GetIs2D = () => _viewport.Mode2D,
                SetIs2D = is2D =>
                {
                    _viewport.Mode2D = is2D;
                    _viewport.Invalidate(true);
                },
                ViewTooltip = "Grid and reference-floor options for the Object preview",
                FloorStyle = new EditorViewMenuChrome.FloorStyleBinding
                {
                    Read = () => EditorFloorStyle.Checkerboard,
                    Write = _ => { },
                    Invalidate = () => _viewport.Invalidate(true),
                },
                GizmoTooltip = "Transform gizmo for the composed Object preview",
                ReadGizmoMode = () => EditorGizmoMode.Move,
                WriteGizmoMode = _ => { },
                ReadGizmoSpace = () => EditorGizmoSpace.World,
                WriteGizmoSpace = _ => { },
                Invalidate = () => _viewport.Invalidate(true),
                IncludeRotateGizmo = false,
                IncludeScaleGizmo = false,
            });
        Controls.Add(_viewport);
        Controls.Add(toolbar);
        toolbar.Visible = !compact;
        if (_audioOwner == null)
        {
            Controls.Add(new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                BackColor = EditorChrome.Raised,
                ForeColor = EditorChrome.Warning,
                Font = EditorChrome.SmallFont,
                Padding = new Padding(6, 3, 3, 0),
                Text = _audioStatus,
            });
        }
    }

    public Entity PreviewEntity => _entity;
    public int ParticleEmitterCount => _composition?.ParticleEmitterCount ?? 0;
    public int ActiveParticleCount => _composition?.ActiveParticleCount ?? 0;
    public int ActiveAudioCount => _composition?.ActiveAudioCount ?? 0;
    public int PointLightCount => _composition?.PointLightCount ?? 0;
    public string AudioStatus => _audioStatus;
    public float AnimatorTimeSeconds => _scene != null && !_entity.IsNull
        && _scene.World.IsAlive(_entity) && _scene.World.Has<ModelAnimatorComponent>(_entity)
            ? _scene.World.GetRef<ModelAnimatorComponent>(_entity).TimeSeconds
            : 0f;

    public void Reload(JObject prefab)
    {
        ReleaseScene();
        JObject clone = (JObject)(prefab ?? new JObject()).DeepClone();
        _input.ClearHeld(); _input.NextFrame();
        _scene = new RuntimeScene("Object Composition Preview") { Input = _input };
        _composition = _scene.AddSubsystem(new ObjectCompositionSubsystem(_projectRoot, _audio));
        _entity = PrefabSpawner.Spawn(_scene.World, clone);
        if (string.Equals((string?)clone["dimension"], "ThreeD", StringComparison.OrdinalIgnoreCase))
        {
            _scene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset());
            var transform = _scene.World.GetRef<TransformComponent>(_entity);
            _scene.World.Set(_entity, new Transform3DComponent { Position = new(transform.X, transform.Y, transform.Z),
                Rotation = Quaternion.CreateFromYawPitchRoll(transform.RotationY * MathF.PI / 180, transform.RotationX * MathF.PI / 180, transform.RotationZ * MathF.PI / 180),
                Scale = new(transform.ScaleX, transform.ScaleY, transform.ScaleZ) });
            new RoomSceneBuilder(_projectRoot).AttachAuthoredPhysics(_scene.World, _entity, clone, transform);
            var floor = _scene.World.CreateEntity();
            _scene.World.Set(floor, new Transform3DComponent { Position = new(0, -.5f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One });
            _scene.World.Set(floor, RigidBodyComponent.InfiniteFloor());
            _scene.UpdateFixed(1f / 60f);
        }
        _scripts = new ScriptHostSystem();
        _scripts.SetContext(new ProjectGameContext(_projectRoot, _scene, _viewport.Host.Renderer, null,
            new RoomAsset { Dimension = (string?)clone["dimension"] == "ThreeD" ? RoomDimension.ThreeD : RoomDimension.TwoD }, null, _audio));
        if (EventSources is not null)
        {
            using (_scripts.UseEventSources(EventSources))
                _scripts.Attach(_scene.World, _entity, _scriptName);
            _scripts.BeginRoom(beginGame: true);
        }
        _accumulator = 0; SimulationFrames = 0;
        _viewport.Mode2D = !string.Equals((string?)clone["dimension"], "ThreeD", StringComparison.OrdinalIgnoreCase);
        _viewport.Background2D = () => (0.055f, 0.07f, 0.11f);
        _lastTicks = _clock.ElapsedTicks;
        _scene.UpdateVariable(1f / 60f);
        FrameAsset(clone);
        _viewport.Invalidate(true);
    }

    public void FrameAsset(JObject prefab, bool force = false)
    {
        if (_scene is null || !_scene.World.Has<TransformComponent>(_entity)) return;
        var transform = _scene.World.GetRef<TransformComponent>(_entity);
        string assetKey = (_viewport.Mode2D ? (string?)prefab["sprite"] : (string?)prefab["model"]) ?? "";
        if (!_viewport.Mode2D && _scene.World.Has<ModelRendererComponent>(_entity))
            assetKey = _scene.World.GetRef<ModelRendererComponent>(_entity).ModelAsset ?? "";
        string frameKey = $"{_viewport.Mode2D}:{assetKey}:{transform.ScaleX}:{transform.ScaleY}:{transform.ScaleZ}:{transform.X}:{transform.Y}:{transform.Z}";
        if (!force && _framedAsset == frameKey) return;
        _framedAsset = frameKey;
        var position = new Vector3(transform.X, transform.Y, transform.Z);
        _viewport.Camera.Target = position;
        _viewport.Camera2DX = transform.X; _viewport.Camera2DY = transform.Y;
        if (!_viewport.Mode2D && string.IsNullOrWhiteSpace(assetKey))
        {
            var size = new Vector3(Math.Abs(transform.ScaleX), Math.Abs(transform.ScaleY), Math.Abs(transform.ScaleZ));
            ModelDimensions = size; _viewport.Camera.Target = position;
            _viewport.Camera.Distance = Math.Max(2, size.Length() * 1.4f);
            _viewport.NearPlane = .01f; _viewport.FarPlane = Math.Max(100, size.Length() * 50);
            _viewport.FloorHeight = position.Y - size.Y * .5f;
        }
        if (_viewport.Mode2D && !string.IsNullOrWhiteSpace(assetKey))
        {
            try
            {
                var sprite = SpriteAssetLoader.Load(_projectRoot, assetKey);
                if (sprite.Frames.Count > 0)
                {
                    // Rendering uses the source image's dimensions; legacy Canvas metadata can
                    // describe an older import size. Fit the same pixels the user actually sees.
                    using var frameImage = System.Drawing.Image.FromFile(SpriteAssetLoader.ResolveFrameTexturePath(_projectRoot, assetKey, 0));
                    _viewport.Zoom2D = Math.Clamp(Math.Min(_viewport.Width / Math.Max(1f, frameImage.Width * Math.Abs(transform.ScaleX)),
                        _viewport.Height / Math.Max(1f, frameImage.Height * Math.Abs(transform.ScaleY))) * .7f, .05f, 16f);
                }
            }
            catch (IOException exception) { _audioStatus = exception.Message; }
        }
        if (!_viewport.Mode2D && !string.IsNullOrWhiteSpace(assetKey))
        {
            try
            {
                var asset = StudioModelResourceLoader.LoadReadOnly(StudioModelResourceLoader.Resolve(_projectRoot, assetKey));
                ModelDimensions = asset.Bounds.Max - asset.Bounds.Min;
                var scale = new Vector3(transform.ScaleX, transform.ScaleY, transform.ScaleZ);
                if (_scene.World.Has<ModelRendererComponent>(_entity))
                {
                    var model = _scene.World.GetRef<ModelRendererComponent>(_entity);
                    scale *= new Vector3(model.ScaleX, model.ScaleY, model.ScaleZ);
                }
                var rotation = Quaternion.CreateFromYawPitchRoll(transform.RotationY * MathF.PI / 180,
                    transform.RotationX * MathF.PI / 180, transform.RotationZ * MathF.PI / 180);
                var matrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
                var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = new Vector3((corner & 1) == 0 ? asset.Bounds.Min.X : asset.Bounds.Max.X,
                        (corner & 2) == 0 ? asset.Bounds.Min.Y : asset.Bounds.Max.Y,
                        (corner & 4) == 0 ? asset.Bounds.Min.Z : asset.Bounds.Max.Z) - asset.Pivot.Position;
                    point = Vector3.Transform(point, matrix); min = Vector3.Min(min, point); max = Vector3.Max(max, point);
                }
                float radius = Math.Max(.1f, Vector3.Distance(min, max) * .5f);
                float aspect = Math.Max(.3f, _viewport.Width / (float)Math.Max(1, _viewport.Height));
                float angle = Math.Min(.5f, MathF.Atan(MathF.Tan(.5f) * aspect));
                _viewport.Camera.Target = (min + max) * .5f;
                _viewport.Camera.Distance = radius * 1.12f / MathF.Sin(angle);
                _viewport.NearPlane = Math.Max(.0001f, radius / 1000);
                _viewport.FarPlane = Math.Max(100, radius * 50);
                _viewport.FloorHeight = min.Y;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or Newtonsoft.Json.JsonException)
            {
                _audioStatus = "Preview asset could not be framed: " + exception.Message;
            }
        }
    }

    public Bitmap? CaptureFrame(int settleFrames = 4) => _viewport.CaptureFrame(settleFrames);

    private static Key PreviewKey(Keys key) => key switch
    {
        Keys.ShiftKey => Key.Shift, Keys.ControlKey => Key.Control, Keys.Menu => Key.Alt,
        Keys.Return => Key.Enter, Keys.Back => Key.Backspace,
        Keys.Oemplus => Key.Plus, Keys.OemMinus => Key.Minus, Keys.Oemcomma => Key.Comma,
        Keys.OemPeriod => Key.Period,
        _ => Enum.TryParse<Key>(key.ToString(), out var result) ? result : Key.Unknown,
    };

    private static MouseButton? PreviewButton(MouseButtons button) => button switch
    {
        MouseButtons.Left => MouseButton.Left, MouseButtons.Right => MouseButton.Right,
        MouseButtons.Middle => MouseButton.Middle, _ => null,
    };

    private void TickScene()
    {
        if (_scene == null) return;
        long now = _clock.ElapsedTicks;
        float dt = _lastTicks == 0 ? 1f / 60f : (float)((now - _lastTicks) / (double)Stopwatch.Frequency);
        _lastTicks = now;
        if (!Playing) return;
        _accumulator += Math.Clamp(dt, 0, .1f);
        while (_accumulator >= 1d / 60)
        {
            _scene.UpdateVariable(1f / 60f);
            _scripts?.Update(1f / 60f);
            if (_scene.Physics is not null) _scene.UpdateFixed(1f / 60f);
            _scene.World.FlushDeferred();
            _input.NextFrame();
            SimulationFrames++; _accumulator -= 1d / 60;
            if (!string.IsNullOrWhiteSpace(_scripts?.LastError)) { Playing = false; break; }
        }
        _audio.SetListener(_scene.Camera3D.Position, _scene.Camera3D.Forward);
        _audio.Update();
    }

    private void Draw3D(IRenderController renderer)
    {
        if (_scene == null || _composition == null) return;
        TickScene();
        Vector3 eye = _viewport.Camera.Eye;
        Vector3 forward = _viewport.Camera.Forward;
        _scene.Camera3D.Position = eye;
        _scene.Camera3D.Yaw = MathF.Atan2(forward.X, forward.Z);
        _scene.Camera3D.Pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        _scene.Camera3D.AspectRatio = renderer.PixelWidth / (float)Math.Max(1, renderer.PixelHeight);

        int count = 0;
        // Lights must be submitted before the model draw calls are flushed.
        _composition.SubmitMeshes(_scene, _meshBuffer, ref count, renderer);
        ObjectDrawPass.SubmitMeshes3D(
            _scene.World, _projectRoot, _meshBuffer, ref count, renderer,
            eye, forward, _viewport.ViewMatrix * _viewport.ProjectionMatrix);
        if (count > 0) renderer.DrawMeshBatch(_meshBuffer.AsSpan(0, count));
        DrawEvent(renderer);
    }

    private void Draw2D(IRenderController renderer)
    {
        if (_scene == null || _composition == null) return;
        TickScene();
        FrameRenderQueue queue = new();
        // EditorViewport3D has already installed a world-centred 2D camera. Unlike RoomRenderSubsystem
        // (which installs an identity screen camera), these calls must stay in world coordinates;
        // adding half the surface here would apply the centre twice and place the object off-screen.
        const float offsetX = 0f;
        const float offsetY = 0f;
        ObjectDrawPass.Render2D(_scene.World, _projectRoot, queue, renderer, offsetX, offsetY, 1f);
        _composition.Render2D(queue, offsetX, offsetY, 1f);
        queue.Flush(renderer, includeMeshes: false, includeSprites: true);
        DrawEvent(renderer);
    }

    private void DrawEvent(IRenderController renderer)
    {
        _hud.Reset(renderer.PixelWidth, renderer.PixelHeight); _scriptDrawQueue.Reset();
        var surface = new PgslRenderDrawSurface(renderer, _hud, renderer.PixelWidth, renderer.PixelHeight,
            _scriptDrawQueue, _projectRoot, is3DActive: !_viewport.Mode2D);
        if (Playing) { _worldDrawRecording.Begin(surface); _scripts?.DispatchPgslWorldDraw(renderer, _scriptDrawQueue, _worldDrawRecording); }
        else _worldDrawRecording.Replay(surface);
        _scriptDrawQueue.Flush(renderer, includeMeshes: !_viewport.Mode2D, includeSprites: true);
    }

    private void DrawGui(IRenderController renderer)
    {
        var surface = new PgslRenderDrawSurface(renderer, _hud, renderer.PixelWidth, renderer.PixelHeight, projectPath: _projectRoot);
        if (Playing) { _guiDrawRecording.Begin(surface); _scripts?.DispatchPgslGuiDraw(renderer, _hud, _guiDrawRecording); }
        else _guiDrawRecording.Replay(surface);
        renderer.FlushOverlaySprites();
        renderer.ComposeOverlay(canvas => _hud.Replay(new OverlayHudCanvas(canvas, renderer.PixelWidth, renderer.PixelHeight)));
    }

    private void ReleaseScene()
    {
        _scriptDrawQueue.Reset();
        _worldDrawRecording.Reset(); _guiDrawRecording.Reset();
        _scripts?.Shutdown(); _scripts = null;
        if (!_entity.IsNull)
        {
            ObjectDrawAssetRegistry.Remove(_entity);
            ProceduralMeshDrawRegistry.Remove(_entity);
        }
        _scene?.Dispose();
        _scene = null;
        _composition = null;
        _entity = Entity.Null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _viewport.DrawScene -= Draw3D;
            _viewport.DrawScene2D -= Draw2D;
            _viewport.DrawOverlay -= DrawGui;
            ReleaseScene();
            _audioOwner?.Dispose();
        }
        base.Dispose(disposing);
    }
}
