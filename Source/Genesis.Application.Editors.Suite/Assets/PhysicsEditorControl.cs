using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Physics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Properties versus raw JSON authoring for the Physics Editor.</summary>
public enum PhysicsAuthoringMode
{
    Properties,
    Code,
}

/// <summary>
/// Physics scene/material editor against <see cref="PhysicsSceneConfig"/> with a live Bepu sandbox
/// on the shared <see cref="EditorViewport3D"/> stack (lighting, floor, Camera inset, gizmos).
/// </summary>
public sealed partial class PhysicsEditorControl : EditorSurfaceControl, IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    private readonly EditorViewport3D _viewport;
    private readonly Label _statusLabel;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Panel _authoringHost;
    private readonly Panel _propertiesSurface;
    private readonly Panel _codeSurface;
    private readonly CodeEditor _code;
    private readonly System.Windows.Forms.Timer _codeApplyTimer;
    private readonly ToolStrip _modeRail;
    private readonly Dictionary<PhysicsAuthoringMode, ToolStripButton> _modeButtons = [];
    private readonly ThemedComboBox _presetCombo = new() { Width = 140 };
    private readonly ThemedComboBox _shapeCombo = new() { Width = 120 };
    private readonly ThemedComboBox _spawnLayoutCombo = new() { Width = 120 };
    private readonly EditorDimensionChrome.DimensionToggle _dimensionToggle;
    private readonly EditorPreviewTargetChrome.PreviewTargetControls _previewTargetControls;
    private PhysicsSceneConfig _document;
    private PhysicsInteractionSession? _session;
    private TrackBar? _frictionSlider;
    private TrackBar? _restitutionSlider;
    private NumericUpDown? _densityInput;
    private NumericUpDown? _gravityInput;
    private NumericUpDown? _spawnCountInput;
    private Button? _playPauseButton;
    private TrackBar? _speedSlider;
    private bool _paused;
    private float _simulationSpeed = 1f;
    private PhysicsAuthoringMode _authoringMode = PhysicsAuthoringMode.Properties;
    private bool _syncing;
    private bool _syncingCode;
    private string _activePreset = "Custom";
    private EditorFloorStyle _floorStyle = EditorFloorStyle.Checkerboard;
    private EditorGizmoMode _gizmoMode = EditorGizmoMode.Move;
    private EditorGizmoSpace _gizmoSpace = EditorGizmoSpace.World;
    private int _selectedPropIndex = -1;
    private bool _gizmoDragging;
    private int _gizmoAxis = -1;
    private Vector3 _gizmoDragStartPose;
    private Vector3 _gizmoDragAxisWorld;
    private Vector2 _gizmoDragAxisScreenDir;
    private float _gizmoDragWorldPerPixel;

    public PhysicsEditorControl(string resourcePath, string projectRoot)
        : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        _document = LoadDocument(resourcePath);
        _activePreset = ResolvePresetName(_document);

        EditorCommandBar toolbar = EditorChrome.MakeToolbar();
        toolbar.Items.Add(EditorDocumentMenuChrome.BuildFileMenu(this));
        toolbar.Items.Add(EditorDocumentMenuChrome.BuildEditMenu(this));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("Preset")
        {
            ForeColor = EditorChrome.Muted,
            Margin = new Padding(4, 0, 4, 0),
        });
        foreach (string name in PhysicsScenePresets.Names)
            _presetCombo.Items.Add(name);
        if (!_presetCombo.Items.Contains(_activePreset) && _activePreset != "Custom")
            _presetCombo.Items.Add(_activePreset);
        _presetCombo.Items.Add("Custom");
        _presetCombo.SelectedItem = _presetCombo.Items.Contains(_activePreset) ? _activePreset : "Custom";
        EditorChrome.StyleField(_presetCombo);
        _presetCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || _presetCombo.SelectedItem is not string name || name == "Custom") return;
            ApplyPreset(name);
        };
        toolbar.Items.Add(new ToolStripControlHost(_presetCombo)
        {
            AutoSize = false,
            Margin = new Padding(0, 4, 6, 0),
            Size = new Size(148, 28),
        });
        toolbar.Items.Add(EditorChrome.ToolButton("Play/Pause", "Pause or resume the physics sandbox", TogglePlayback));
        toolbar.Items.Add(EditorChrome.ToolButton("Step", "Advance the sandbox by one frame", () => StepSandbox(1f / 60f)));
        toolbar.Items.Add(EditorChrome.ToolButton("Reset", "Rebuild the sandbox from the current scene config", RebuildSandbox));

        _viewport = CreateViewport();
        EditorViewportChrome.Attached chrome = EditorViewportChrome.Attach(
            toolbar,
            new EditorViewportChrome.Options
            {
                Viewport = _viewport,
                GetIs2D = () => _document.Dimension == PhysicsDimension.TwoD,
                SetIs2D = SetPreview2D,
                PreviewTarget = new EditorPreviewTargetChrome.PreviewTargetBinding
                {
                    ProjectRoot = ProjectRoot,
                    Owner = FindForm(),
                    ReadKind = () => EditorPreviewTargetChrome.ParseKind(_document.PreviewAssetKind),
                    ReadPath = () => _document.PreviewAssetPath ?? string.Empty,
                    Write = SetPreviewTarget,
                },
                ViewTooltip = "Grid and reference-floor options for the physics sandbox",
                FloorStyle = new EditorViewMenuChrome.FloorStyleBinding
                {
                    Read = () => _floorStyle,
                    Write = SetFloorStyle,
                    Invalidate = () => _viewport.Host.Invalidate(),
                },
                GizmoTooltip = "Move or rotate the selected sandbox body",
                ReadGizmoMode = () => _gizmoMode,
                WriteGizmoMode = mode => { _gizmoMode = mode; _viewport.Invalidate(true); },
                ReadGizmoSpace = () => _gizmoSpace,
                WriteGizmoSpace = space => { _gizmoSpace = space; _viewport.Invalidate(true); },
                Invalidate = () => _viewport.Invalidate(true),
                IncludeScaleGizmo = false,
            });
        _dimensionToggle = chrome.Dimension
            ?? throw new InvalidOperationException("Physics Editor requires the shared dimension chrome.");
        _previewTargetControls = chrome.PreviewTarget
            ?? throw new InvalidOperationException("Physics Editor requires the shared preview-target chrome.");

        FlowLayoutPanel properties = new()
        {
            AutoScroll = true,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(12),
            WrapContents = false,
        };
        properties.Controls.Add(EditorChrome.SectionLabel("Material"));
        AddInspectorSlider(properties, "Friction", (float)_document.Friction, SetFriction, out _frictionSlider);
        AddInspectorSlider(properties, "Restitution", (float)_document.Restitution, SetRestitution, out _restitutionSlider);
        AddInspectorNumeric(properties, "Density", (float)_document.Density, 0.01f, 100f, SetDensity, out _densityInput);
        properties.Controls.Add(EditorChrome.SectionLabel("World"));
        AddInspectorNumeric(properties, "Gravity ×", _document.GravityStrength, 0f, 10f, SetGravityStrength, out _gravityInput);
        foreach (PhysicsBodyShape shape in Enum.GetValues<PhysicsBodyShape>())
        {
            if (shape == PhysicsBodyShape.Mesh || shape == PhysicsBodyShape.Cylinder) continue;
            _shapeCombo.Items.Add(shape);
        }
        _shapeCombo.SelectedItem = _document.SpawnShape is PhysicsBodyShape.Mesh or PhysicsBodyShape.Cylinder
            ? PhysicsBodyShape.Box
            : _document.SpawnShape;
        EditorChrome.StyleField(_shapeCombo);
        _shapeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || _shapeCombo.SelectedItem is not PhysicsBodyShape shape) return;
            _document.Shape = shape;
            _document.SpawnShape = shape;
            RefreshComputedMass();
            MarkCustom();
            SyncCodeFromProperties();
            MarkDirty();
        };
        AddInspectorRow(properties, "Spawn shape", _shapeCombo);
        foreach (PhysicsSpawnLayout layout in Enum.GetValues<PhysicsSpawnLayout>())
            _spawnLayoutCombo.Items.Add(layout);
        _spawnLayoutCombo.SelectedItem = _document.SpawnLayout;
        EditorChrome.StyleField(_spawnLayoutCombo);
        _spawnLayoutCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || _spawnLayoutCombo.SelectedItem is not PhysicsSpawnLayout layout) return;
            _document.SpawnLayout = layout;
            MarkCustom();
            MarkDirty();
        };
        AddInspectorRow(properties, "Spawn layout", _spawnLayoutCombo);
        AddInspectorNumeric(properties, "Spawn count", _document.SpawnCount, 0f, 128f, SetSpawnCount, out _spawnCountInput, decimals: 0);
        properties.Controls.Add(EditorChrome.SectionLabel("Simulation"));
        properties.Controls.Add(BuildSimulationControls());
        if (!string.IsNullOrWhiteSpace(_document.Notes))
        {
            properties.Controls.Add(EditorChrome.DividerLabel("Preset notes"));
            properties.Controls.Add(InfoCard("Notes", _document.Notes));
        }

        _propertiesSurface = new Panel
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
        };
        _propertiesSurface.Controls.Add(properties);
        _propertiesSurface.Controls.Add(EditorChrome.SectionLabel("Physics Scene"));

        _code = new CodeEditor { Dock = DockStyle.Fill };
        _code.SetRules(BuildPhysicsRules());
        _codeSurface = new Panel
        {
            BackColor = EditorChrome.Canvas,
            Dock = DockStyle.Fill,
            Visible = false,
        };
        _codeSurface.Controls.Add(_code);
        _codeApplyTimer = new System.Windows.Forms.Timer { Interval = 450 };
        _codeApplyTimer.Tick += (_, _) =>
        {
            _codeApplyTimer.Stop();
            ApplyCodeFromEditor();
        };
        _code.TextChangedByUser += (_, _) =>
        {
            if (_syncingCode) return;
            _codeApplyTimer.Stop();
            _codeApplyTimer.Start();
        };

        _authoringHost = EditorChrome.SidePanel(EditorChrome.LeftPanelWidth, DockStyle.Left);
        _authoringHost.Controls.Add(_codeSurface);
        _authoringHost.Controls.Add(_propertiesSurface);

        _modeRail = EditorChrome.MakeModeRail(EditorChrome.ModeRailWidth);
        foreach (PhysicsAuthoringMode mode in Enum.GetValues<PhysicsAuthoringMode>())
        {
            PhysicsAuthoringMode captured = mode;
            ToolStripButton button = EditorChrome.ModeButton(
                mode.ToString(),
                mode == PhysicsAuthoringMode.Properties
                    ? "Edit material and world fields with the property inspector"
                    : "Edit the canonical PhysicsSceneConfig JSON",
                () => SetAuthoringMode(captured));
            _modeButtons[mode] = button;
            _modeRail.Items.Add(button);
        }

        _statusLabel = EditorChrome.MakeStatusBar();

        BuildReferenceWorkspace(toolbar, properties);

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) =>
        {
            if (!_paused)
                StepSandbox(0.016f * _simulationSpeed);
            else
                _viewport.Invalidate(true);
        };
        _timer.Start();
        Disposed += (_, _) =>
        {
            _timer.Dispose();
            _codeApplyTimer.Dispose();
            _session?.Dispose();
            InvalidatePhysicsTargetRenderer();
        };

        RebuildSandbox();
        PushCodeFromConfig();
        SetAuthoringMode(PhysicsAuthoringMode.Properties);
        SyncDimensionUi();
        UpdateStatus();
    }

    public EditorViewport3D Viewport => _viewport;
    public PhysicsAuthoringMode AuthoringMode => _authoringMode;

    private EditorViewport3D CreateViewport()
    {
        EditorViewport3D viewport = new()
        {
            Mode2D = _document.Dimension == PhysicsDimension.TwoD,
            Background2D = () => (0.05f, 0.06f, 0.10f),
            SceneStateFactory = () => EditorSceneLighting.Create(
                showFloor: _floorStyle.DrawsPlate() && _document.Dimension != PhysicsDimension.TwoD),
            FloorStyle = _floorStyle,
        };
        viewport.Camera.Target = new Vector3(0f, 1.2f, 0f);
        viewport.Camera.Distance = 14f;
        viewport.DrawScene += DrawSandbox3D;
        viewport.DrawScene2D += DrawSandbox2D;
        viewport.DrawOverlay += DrawOverlay;
        viewport.SelectionWorldPoint = () =>
        {
            if (_session is null || _selectedPropIndex < 0 || !_session.IsPropActive(_selectedPropIndex))
                return null;
            _session.GetPropPose(_selectedPropIndex, out Vector3 position, out _);
            return position;
        };
        viewport.Host.MouseDown += OnViewportMouseDown;
        viewport.Host.MouseMove += OnViewportMouseMove;
        viewport.Host.MouseUp += OnViewportMouseUp;
        return viewport;
    }

    public void SetAuthoringMode(PhysicsAuthoringMode mode)
    {
        if (mode == PhysicsAuthoringMode.Code && _authoringMode == PhysicsAuthoringMode.Properties)
            PushCodeFromConfig();
        if (mode == PhysicsAuthoringMode.Properties && _authoringMode == PhysicsAuthoringMode.Code)
            ApplyCodeFromEditor(force: true);

        _authoringMode = mode;
        bool code = mode == PhysicsAuthoringMode.Code;
        if (_referencePhysicsLayout)
        {
            _propertiesSurface.Visible = false;
            _codeSurface.Visible = code;
            if (_physicsAuthoringSplit is not null)
                _physicsAuthoringSplit.Panel1Collapsed = !code;
        }
        else
        {
            _propertiesSurface.Visible = !code;
            _codeSurface.Visible = code;
            _authoringHost.Width = code ? 420 : EditorChrome.LeftPanelWidth;
        }
        foreach ((PhysicsAuthoringMode key, ToolStripButton button) in _modeButtons)
            button.Checked = key == mode;
        UpdateStatus();
    }

    public void SetPreview2D(bool enabled)
    {
        _document.Dimension = enabled ? PhysicsDimension.TwoD : PhysicsDimension.ThreeD;
        _viewport.Mode2D = enabled;
        SyncDimensionUi();
        MarkCustom();
        MarkDirty();
        UpdateStatus();
        _viewport.Invalidate(true);
    }

    private void SetPreviewTarget(EditorPreviewTargetChrome.PreviewTargetKind kind, string path)
    {
        _document.PreviewAssetKind = EditorPreviewTargetChrome.FormatKind(kind);
        _document.PreviewAssetPath = path ?? string.Empty;
        if (kind != EditorPreviewTargetChrome.PreviewTargetKind.None && !string.IsNullOrWhiteSpace(path))
            _document.BackdropSprite = ResourceDisplayName.Format(path);
        MarkCustom();
        MarkDirty();
        InvalidatePhysicsTarget();
        RebuildSandbox();
        UpdateStatus();
        _viewport.Invalidate(true);
    }

    private void ApplyPreset(string name)
    {
        PhysicsSceneConfig next = PhysicsScenePresets.Create(name);
        next.PreviewAssetPath = _document.PreviewAssetPath;
        next.PreviewAssetKind = _document.PreviewAssetKind;
        if (!string.IsNullOrWhiteSpace(_document.BackdropSprite) &&
            string.IsNullOrWhiteSpace(next.BackdropSprite))
        {
            next.BackdropSprite = _document.BackdropSprite;
        }

        _document = next;
        _activePreset = name;
        _syncing = true;
        try { SyncPropertyControls(); }
        finally { _syncing = false; }
        SetPreview2D(_document.Dimension == PhysicsDimension.TwoD);
        RebuildSandbox();
        PushCodeFromConfig();
        MarkDirty();
        UpdateStatus();
    }

    private void RebuildSandbox()
    {
        _session?.Dispose();
        int slots = Math.Max(24, _document.SpawnCount + 24);
        _session = new PhysicsInteractionSession(slots);
        _session.ApplyPhysicsSceneConfig(_document);
        bool hasTarget = ConfigureTargetSandbox(out Func<float, float, float> sampleGround, out Vector3 spawnOrigin);
        if (!hasTarget)
        {
            _session.BuildStressPlayground();
            sampleGround = (_, _) => 0f;
            spawnOrigin = Vector3.Zero;
        }
        SandboxPropShape shape = MapSpawnShape(_document.SpawnShape);
        if (_document.SpawnCount > 0)
        {
            _session.SpawnBulkProps(
                shape,
                _document.SpawnCount,
                sampleGround,
                origin: spawnOrigin,
                spreadRadius: _document.SpawnLayout == PhysicsSpawnLayout.Orbit ? 6f : 4f,
                authoredMass: _document.SpawnMass);
        }

        _selectedPropIndex = -1;
        ResetPhysicsTelemetry();
        _viewport.Invalidate(true);
    }

    private static SandboxPropShape MapSpawnShape(PhysicsBodyShape shape) => shape switch
    {
        PhysicsBodyShape.Sphere => SandboxPropShape.Sphere,
        PhysicsBodyShape.Capsule => SandboxPropShape.Capsule,
        _ => SandboxPropShape.Box,
    };

    private Control BuildSimulationControls()
    {
        Panel host = new()
        {
            BackColor = EditorChrome.Surface,
            Height = 84,
            Margin = new Padding(0, 8, 0, 4),
            Width = 252,
        };
        _playPauseButton = new Button
        {
            Location = new Point(0, 4),
            Size = new Size(92, 28),
            Text = "Pause",
        };
        EditorChrome.StyleField(_playPauseButton);
        _playPauseButton.Click += (_, _) => TogglePlayback();
        Button step = new()
        {
            Location = new Point(98, 4),
            Size = new Size(70, 28),
            Text = "Step",
        };
        EditorChrome.StyleField(step);
        step.Click += (_, _) => StepSandbox(1f / 60f);
        Button reset = new()
        {
            Location = new Point(174, 4),
            Size = new Size(72, 28),
            Text = "Reset",
        };
        EditorChrome.StyleField(reset);
        reset.Click += (_, _) => RebuildSandbox();
        Label speed = new()
        {
            ForeColor = EditorChrome.Muted,
            Location = new Point(0, 42),
            Size = new Size(52, 24),
            Text = "Speed",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _speedSlider = new TrackBar
        {
            AutoSize = false,
            BackColor = EditorChrome.Surface,
            Location = new Point(58, 40),
            Maximum = 60,
            Minimum = 0,
            Size = new Size(188, 30),
            TickStyle = TickStyle.None,
            Value = 60,
        };
        _speedSlider.ValueChanged += (_, _) =>
        {
            _simulationSpeed = _speedSlider.Value / 60f;
            UpdateStatus();
        };
        host.Controls.Add(_speedSlider);
        host.Controls.Add(speed);
        host.Controls.Add(reset);
        host.Controls.Add(step);
        host.Controls.Add(_playPauseButton);
        return host;
    }

    private void SetFloorStyle(EditorFloorStyle style)
    {
        _floorStyle = style;
        _viewport.FloorStyle = style;
        _viewport.Host.Invalidate();
        UpdateStatus();
    }

    private void TogglePlayback()
    {
        SetPhysicsPaused(!_paused);
    }

    private void StepSandbox(float dt)
    {
        if (_session is null) return;
        _session.ApplyPhysicsSceneConfig(_document);
        PhysicsInteractionInput input = new()
        {
            CameraPosition = _viewport.Camera.Eye,
            CameraForward = _viewport.Camera.Forward,
        };
        _session.Step(input, dt);
        RecordPhysicsTelemetry(dt);
        _viewport.Invalidate(true);
        UpdateStatus();
    }

    private void DrawSandbox3D(IRenderController renderer)
    {
        if (_session is null) return;
        DrawPhysicsPreviewTarget(renderer);
        for (int i = 0; i < _session.PropCount; i++)
        {
            if (!_session.IsPropActive(i)) continue;
            _session.GetPropPose(i, out Vector3 position, out Quaternion rotation);
            PhysicsBody body = _session.GetPropBody(i);
            SandboxPropShape shape = _session.GetPropShape(i);
            BuiltinMeshKind meshKind = shape switch
            {
                SandboxPropShape.Sphere => BuiltinMeshKind.Sphere,
                _ => BuiltinMeshKind.Cube,
            };
            Vector3 scale = shape switch
            {
                SandboxPropShape.Sphere => new Vector3(body.HalfExtents.X * 2f),
                SandboxPropShape.Capsule => new Vector3(body.HalfExtents.X * 2f, body.HalfExtents.Y * 2f, body.HalfExtents.X * 2f),
                _ => body.HalfExtents * 2f,
            };
            bool selected = i == _selectedPropIndex;
            Matrix4x4 world =
                Matrix4x4.CreateScale(scale) *
                Matrix4x4.CreateFromQuaternion(rotation) *
                Matrix4x4.CreateTranslation(position);
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = renderer.GetBuiltinMesh(meshKind),
                World = world,
                Tint = selected
                    ? new RenderColor(0.45f, 0.72f, 1f)
                    : new RenderColor(0.78f, 0.62f, 0.32f),
                Alpha = 1f,
            });
        }

        if (_session.PlayerBody.IsValid)
        {
            _session.GetPlayerPose(out Vector3 playerPos, out Quaternion playerRot);
            Matrix4x4 playerWorld =
                Matrix4x4.CreateScale(0.7f, 2f, 0.7f) *
                Matrix4x4.CreateFromQuaternion(playerRot) *
                Matrix4x4.CreateTranslation(playerPos);
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = renderer.GetBuiltinMesh(BuiltinMeshKind.Cube),
                World = playerWorld,
                Tint = new RenderColor(0.35f, 0.85f, 0.55f),
                Alpha = 0.85f,
            });
        }
        DrawPhysicsTelemetryOverlay(renderer);
    }

    private void DrawSandbox2D(IRenderController renderer)
    {
        if (_session is null) return;
        // Ortho 2D preview: project XZ onto the 2D plane as XY sprites via unit cubes flattened.
        for (int i = 0; i < _session.PropCount; i++)
        {
            if (!_session.IsPropActive(i)) continue;
            _session.GetPropPose(i, out Vector3 position, out _);
            PhysicsBody body = _session.GetPropBody(i);
            float size = MathF.Max(body.HalfExtents.X, body.HalfExtents.Z) * 2f;
            Matrix4x4 world =
                Matrix4x4.CreateScale(size, size, 1f) *
                Matrix4x4.CreateTranslation(position.X, position.Y, 0f);
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = renderer.GetBuiltinMesh(BuiltinMeshKind.Cube),
                World = world,
                Tint = i == _selectedPropIndex
                    ? new RenderColor(0.45f, 0.72f, 1f)
                    : new RenderColor(0.78f, 0.62f, 0.32f),
                Alpha = 1f,
            });
        }
    }

    private void DrawOverlay(IRenderController renderer)
    {
        if (_floorStyle == EditorFloorStyle.GridOnly && _document.Dimension != PhysicsDimension.TwoD)
        {
            EditorViewportGridHelper.DrawGrid3D(
                _viewport,
                renderer,
                cellSize: 1f,
                rgba: [1f, 1f, 1f, 0.08f],
                horizontalExtent: 12f,
                verticalExtent: 4f);
        }

        if (_session is null || _selectedPropIndex < 0 || !_session.IsPropActive(_selectedPropIndex))
            return;
        if (_document.Dimension == PhysicsDimension.TwoD) return;

        _session.GetPropPose(_selectedPropIndex, out Vector3 origin, out _);
        EditorTransformGizmo.Draw3D(
            _viewport,
            renderer,
            origin,
            axisLength: 1.6f,
            _gizmoMode,
            _gizmoSpace,
            activeAxis: _gizmoDragging && _gizmoAxis >= 0 ? _gizmoAxis : null);
    }

    private void OnViewportMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _session is null) return;
        if (_document.Dimension != PhysicsDimension.TwoD &&
            _selectedPropIndex >= 0 &&
            _session.IsPropActive(_selectedPropIndex))
        {
            _session.GetPropPose(_selectedPropIndex, out Vector3 origin, out _);
            PointF surface = _viewport.ControlToSurface(e.Location);
            const float axisLength = 1.6f;
            EditorGizmoHit? hit = EditorTransformGizmo.HitTest3D(
                _viewport,
                surface,
                origin,
                axisLength,
                _gizmoMode,
                _gizmoSpace);
            if (hit is { } gizmoHit &&
                EditorTransformGizmo.TryProjectAxisToSurface(
                    _viewport,
                    origin,
                    EditorTransformGizmo.Axes(_gizmoSpace, default)[gizmoHit.AxisIndex],
                    axisLength,
                    out Vector2 screenDir,
                    out float screenLen))
            {
                _gizmoDragging = true;
                _gizmoAxis = gizmoHit.AxisIndex;
                _gizmoDragStartPose = origin;
                _gizmoDragAxisWorld = EditorTransformGizmo.Axes(_gizmoSpace, default)[gizmoHit.AxisIndex];
                _gizmoDragAxisScreenDir = screenDir;
                _gizmoDragWorldPerPixel = axisLength / MathF.Max(1f, screenLen);
                return;
            }
        }

        (Vector3 rayOrigin, Vector3 rayDir) = _viewport.PickRay(e.Location);
        if (_session.PhysicsWorld.Raycast(rayOrigin, rayDir, 200f, out PhysicsBody hitBody) &&
            !hitBody.IsStatic)
        {
            for (int i = 0; i < _session.PropCount; i++)
            {
                if (_session.IsPropActive(i) &&
                    _session.GetPropBody(i).Handle.Value == hitBody.Handle.Value)
                {
                    _selectedPropIndex = i;
                    UpdateStatus();
                    _viewport.Invalidate(true);
                    return;
                }
            }
        }

        _selectedPropIndex = -1;
        UpdateStatus();
        _viewport.Invalidate(true);
    }

    private void OnViewportMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_gizmoDragging || _gizmoAxis < 0 || _session is null || _selectedPropIndex < 0) return;
        PointF surface = _viewport.ControlToSurface(e.Location);
        Vector3 originSurface = _viewport.WorldToSurface(_gizmoDragStartPose);
        Vector2 along = new(surface.X - originSurface.X, surface.Y - originSurface.Y);
        float worldDelta = Vector2.Dot(along, _gizmoDragAxisScreenDir) * _gizmoDragWorldPerPixel;
        Vector3 next = _gizmoDragStartPose + _gizmoDragAxisWorld * worldDelta;
        PhysicsBody body = _session.GetPropBody(_selectedPropIndex);
        _session.GetPropPose(_selectedPropIndex, out _, out Quaternion rotation);
        _session.PhysicsWorld.SetBodyPose(body, next, rotation);
        _session.PhysicsWorld.SetLinearVelocity(body, Vector3.Zero);
        _viewport.Invalidate(true);
    }

    private void OnViewportMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _gizmoDragging = false;
        _gizmoAxis = -1;
    }

    public override void Save()
    {
        if (_authoringMode == PhysicsAuthoringMode.Code)
            ApplyCodeFromEditor(force: true);
        _document.SaveToFile(ResourcePath);
        AcceptSave();
    }

    public event EventHandler? InspectorStateChanged;

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues() =>
    [
        new(
            "Physics material",
            "friction",
            "Friction",
            (decimal)_document.Friction,
            Minimum: 0,
            Maximum: 1,
            Increment: 0.01m,
            DecimalPlaces: 2),
        new(
            "Physics material",
            "restitution",
            "Restitution",
            (decimal)_document.Restitution,
            Minimum: 0,
            Maximum: 1,
            Increment: 0.01m,
            DecimalPlaces: 2),
        new(
            "Physics material",
            "density",
            "Density",
            (decimal)_document.Density,
            Minimum: 0.01m,
            Maximum: 100m,
            Increment: 0.1m,
            DecimalPlaces: 2),
    ];

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value) =>
        TryApplyInspectorValue(propertyPath, value);

    public bool TryApplyInspectorValue(string propertyPath, object? value)
    {
        try
        {
            return propertyPath.ToLowerInvariant() switch
            {
                "friction" => ApplyInspectorFloat(value, SetFriction),
                "restitution" => ApplyInspectorFloat(value, SetRestitution),
                "density" => ApplyInspectorFloat(value, SetDensity),
                _ => false,
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException
                                           or OverflowException)
        {
            return false;
        }
    }

    private bool ApplyInspectorFloat(object? value, Action<float> apply)
    {
        apply(Convert.ToSingle(value, CultureInfo.InvariantCulture));
        return true;
    }

    private void SetFriction(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        if (MathF.Abs((float)_document.Friction - value) < 0.0001f) return;
        _document.Friction = value;
        if (_frictionSlider is not null)
            _frictionSlider.Value = (int)Math.Clamp(value * 100f, 0f, 100f);
        MarkCustom();
        SyncCodeFromProperties();
        UpdateStatus();
        MarkDirty();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetRestitution(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        if (MathF.Abs((float)_document.Restitution - value) < 0.0001f) return;
        _document.Restitution = value;
        if (_restitutionSlider is not null)
            _restitutionSlider.Value = (int)Math.Clamp(value * 100f, 0f, 100f);
        MarkCustom();
        SyncCodeFromProperties();
        UpdateStatus();
        MarkDirty();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetDensity(float value)
    {
        value = Math.Clamp(value, 0.01f, 100f);
        if (MathF.Abs((float)_document.Density - value) < 0.0001f) return;
        _document.Density = value;
        if (_densityInput is not null)
            _densityInput.Value = Math.Clamp((decimal)value, _densityInput.Minimum, _densityInput.Maximum);
        RefreshComputedMass();
        MarkCustom();
        SyncCodeFromProperties();
        UpdateStatus();
        MarkDirty();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetGravityStrength(float value)
    {
        value = Math.Clamp(value, 0f, 10f);
        if (MathF.Abs(_document.GravityStrength - value) < 0.0001f) return;
        _document.GravityStrength = value;
        _session?.ApplyPhysicsSceneConfig(_document);
        MarkCustom();
        SyncCodeFromProperties();
        UpdateStatus();
        MarkDirty();
    }

    private void SetSpawnCount(float value)
    {
        int count = (int)Math.Clamp(value, 0f, 128f);
        if (_document.SpawnCount == count) return;
        _document.SpawnCount = count;
        MarkCustom();
        SyncCodeFromProperties();
        MarkDirty();
    }

    private void MarkCustom()
    {
        _activePreset = "Custom";
        if (_presetCombo.Items.Contains("Custom"))
        {
            _syncing = true;
            try { _presetCombo.SelectedItem = "Custom"; }
            finally { _syncing = false; }
        }
    }

    private void SyncCodeFromProperties()
    {
        if (_authoringMode == PhysicsAuthoringMode.Properties && !_syncingCode)
            PushCodeFromConfig();
    }

    private void PushCodeFromConfig()
    {
        _syncingCode = true;
        try
        {
            _code.CodeText = PhysicsCodeCodec.Serialize(_document);
        }
        finally
        {
            _syncingCode = false;
        }
    }

    private void ApplyCodeFromEditor(bool force = false)
    {
        if (_syncingCode && !force) return;
        try
        {
            PhysicsSceneConfig? parsed = _code.CodeText.TrimStart().StartsWith('{')
                ? JsonSerializer.Deserialize<PhysicsSceneConfig>(_code.CodeText, JsonOptions)
                : PhysicsCodeCodec.Parse(_code.CodeText, _document);
            if (parsed is null) return;
            _document = parsed;
            _activePreset = ResolvePresetName(_document);
            _syncing = true;
            try { SyncPropertyControls(); }
            finally { _syncing = false; }
            SetPreview2D(_document.Dimension == PhysicsDimension.TwoD);
            _previewTargetControls.Sync(
                EditorPreviewTargetChrome.ParseKind(_document.PreviewAssetKind),
                _document.PreviewAssetPath ?? string.Empty);
            _session?.ApplyPhysicsSceneConfig(_document);
            MarkDirty();
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
            UpdateStatus();
            _viewport.Invalidate(true);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            // Leave previous live document while typing.
        }
    }

    private void SyncPropertyControls()
    {
        if (_frictionSlider is { IsDisposed: false })
            _frictionSlider.Value = (int)Math.Clamp(_document.Friction * 100.0, 0, 100);
        if (_restitutionSlider is { IsDisposed: false })
            _restitutionSlider.Value = (int)Math.Clamp(_document.Restitution * 100.0, 0, 100);
        if (_densityInput is { IsDisposed: false })
            _densityInput.Value = Math.Clamp((decimal)_document.Density, _densityInput.Minimum, _densityInput.Maximum);
        if (_gravityInput is { IsDisposed: false })
            _gravityInput.Value = Math.Clamp((decimal)_document.GravityStrength, _gravityInput.Minimum, _gravityInput.Maximum);
        if (_spawnCountInput is { IsDisposed: false })
            _spawnCountInput.Value = Math.Clamp(_document.SpawnCount, _spawnCountInput.Minimum, _spawnCountInput.Maximum);
        if (!_shapeCombo.IsDisposed && _shapeCombo.Items.Contains(_document.SpawnShape))
            _shapeCombo.SelectedItem = _document.SpawnShape;
        if (!_spawnLayoutCombo.IsDisposed && _spawnLayoutCombo.Items.Contains(_document.SpawnLayout))
            _spawnLayoutCombo.SelectedItem = _document.SpawnLayout;
        if (!_presetCombo.IsDisposed && _presetCombo.Items.Contains(_activePreset))
            _presetCombo.SelectedItem = _activePreset;
        else if (!_presetCombo.IsDisposed)
            _presetCombo.SelectedItem = "Custom";
    }

    private void SyncDimensionUi() =>
        _dimensionToggle.Sync(_document.Dimension == PhysicsDimension.TwoD);

    private static PhysicsSceneConfig LoadDocument(string path)
    {
        PhysicsSceneConfig loaded = PhysicsSceneConfig.LoadFromFile(path);
        // Migrate tiny legacy editor docs that only carried friction/restitution/density.
        try
        {
            if (File.Exists(path))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("schemaVersion", out _) ||
                    doc.RootElement.TryGetProperty("SchemaVersion", out _))
                {
                    // Old PhysicsDocument — keep material floats, keep scene defaults otherwise.
                    if (doc.RootElement.TryGetProperty("friction", out JsonElement friction) ||
                        doc.RootElement.TryGetProperty("Friction", out friction))
                        loaded.Friction = friction.GetDouble();
                    if (doc.RootElement.TryGetProperty("restitution", out JsonElement restitution) ||
                        doc.RootElement.TryGetProperty("Restitution", out restitution))
                        loaded.Restitution = restitution.GetDouble();
                    if (doc.RootElement.TryGetProperty("density", out JsonElement density) ||
                        doc.RootElement.TryGetProperty("Density", out density))
                        loaded.Density = density.GetDouble();
                }
            }
        }
        catch
        {
            // Keep LoadFromFile result.
        }

        return loaded;
    }

    private static string ResolvePresetName(PhysicsSceneConfig config)
    {
        foreach (string name in PhysicsScenePresets.Names)
        {
            PhysicsSceneConfig preset = PhysicsScenePresets.Create(name);
            if (string.Equals(preset.Name, config.Name, StringComparison.OrdinalIgnoreCase) &&
                preset.GravityModel == config.GravityModel &&
                MathF.Abs(preset.GravityStrength - config.GravityStrength) < 0.001f)
            {
                return name;
            }
        }

        return "Custom";
    }

    private static List<HighlightRule> BuildPhysicsRules()
    {
        Color key = EditorChrome.FromHex("#9CDCFE");
        Color value = EditorChrome.FromHex("#CE9178");
        Color number = EditorChrome.FromHex("#B5CEA8");
        Color keyword = EditorChrome.FromHex("#C586C0");
        Color comment = EditorChrome.FromHex("#6A9955");
        return
        [
            new HighlightRule(new Regex(@"""[^""\\]*(?:\\.[^""\\]*)*""", RegexOptions.Compiled), value),
            new HighlightRule(new Regex(@"\b-?\d+(?:\.\d+)?\b", RegexOptions.Compiled), number),
            new HighlightRule(new Regex(@"\b(physics_material|true|false|Dynamic|Static|Kinematic|Box|Sphere|Capsule|Mesh)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), keyword),
            new HighlightRule(new Regex(@"^[ \t]*[A-Za-z_][A-Za-z0-9_.]*(?=\s*:)", RegexOptions.Compiled | RegexOptions.Multiline), key),
            new HighlightRule(new Regex(@"//[^\r\n]*", RegexOptions.Compiled), comment),
        ];
    }

    private static void AddInspectorRow(Control parent, string caption, Control field)
    {
        Label label = new()
        {
            AutoSize = true,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            Margin = new Padding(0, 8, 0, 2),
            Text = caption.ToUpperInvariant(),
        };
        parent.Controls.Add(label);
        parent.Controls.Add(field);
    }

    private void AddInspectorSlider(Control parent, string caption, float value, Action<float> apply, out TrackBar slider)
    {
        Label label = new()
        {
            AutoSize = true,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            Margin = new Padding(0, 8, 0, 2),
            Text = caption.ToUpperInvariant(),
        };
        TrackBar trackBar = new()
        {
            AutoSize = false,
            BackColor = EditorChrome.Surface,
            Height = 28,
            Maximum = 100,
            Minimum = 0,
            TickStyle = TickStyle.None,
            Value = (int)Math.Clamp(value * 100f, 0f, 100f),
            Width = 244,
        };
        trackBar.ValueChanged += (_, _) =>
        {
            if (!_syncing) apply(trackBar.Value / 100f);
        };
        parent.Controls.Add(label);
        parent.Controls.Add(trackBar);
        slider = trackBar;
    }

    private void AddInspectorNumeric(
        Control parent,
        string caption,
        float value,
        float min,
        float max,
        Action<float> apply,
        out NumericUpDown numeric,
        int decimals = 2)
    {
        Label label = new()
        {
            AutoSize = true,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            Margin = new Padding(0, 8, 0, 2),
            Text = caption.ToUpperInvariant(),
        };
        NumericUpDown input = new()
        {
            DecimalPlaces = decimals,
            Increment = decimals > 0 ? 0.1m : 1m,
            Maximum = (decimal)max,
            Minimum = (decimal)min,
            Value = (decimal)Math.Clamp(value, min, max),
            Width = 244,
        };
        EditorChrome.StyleField(input);
        input.ValueChanged += (_, _) =>
        {
            if (!_syncing) apply((float)input.Value);
        };
        parent.Controls.Add(label);
        parent.Controls.Add(input);
        numeric = input;
    }

    private static Control InfoCard(string title, string body)
    {
        Panel card = new()
        {
            BackColor = EditorChrome.Raised,
            Height = 70,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(10, 8, 10, 8),
            Width = 244,
        };
        card.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = EditorChrome.Muted,
            Text = body,
        });
        card.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Font = new Font(EditorChrome.BaseFont, FontStyle.Bold),
            ForeColor = EditorChrome.Text,
            Height = 22,
            Text = title,
        });
        return card;
    }

    private void UpdateStatus()
    {
        string mode = _authoringMode == PhysicsAuthoringMode.Code ? "Code" : "Properties";
        string dim = _document.Dimension == PhysicsDimension.TwoD ? "2D" : "3D";
        string selection = _selectedPropIndex >= 0 ? $"prop {_selectedPropIndex}" : "no selection";
        string target = string.IsNullOrWhiteSpace(_document.PreviewAssetPath)
            ? "no target"
            : ResourceDisplayName.Format(_document.PreviewAssetPath);
        string gizmo = EditorTransformGizmo.StatusHint(_gizmoMode, _gizmoSpace, snap: false);
        _statusLabel.Text =
            $"{mode} · {dim} · {_activePreset} · {selection} · {target} · {gizmo} · " +
            $"μ {_document.Friction:0.00}  e {_document.Restitution:0.00}  ρ {_document.Density:0.00} · " +
            $"{(_paused ? "paused" : "playing")} {_simulationSpeed:0.00}×";
    }
}
