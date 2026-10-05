using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Image Editor's "Convert to 3D Model": voxel settings on the left, a live 3D preview with the
/// shared transform gizmo on the right. The move, rotation and resize are baked into the Model.
/// </summary>
public sealed class PixelModelDialog : DpiAwareForm
{
    private readonly PixelModelSource _source;
    private readonly RuntimeModelRenderSystem _renderer = new();
    private readonly PixelModelSettings _settings = new();
    private readonly TableLayoutPanel _body = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
    private readonly Panel _side = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TextBox _name = new() { Name = "PixelModelName" };
    private readonly NumericUpDown _pixelSize = Number("PixelModelPixelSize", .001m, 16m, (decimal)PixelModelSettings.DefaultPixelSize, 4, .0625m);
    private readonly NumericUpDown _depth = Number("PixelModelDepth", 1, 256, 1, 0, 1);
    private readonly NumericUpDown _alpha = Number("PixelModelAlpha", 1, 255, PixelModelSettings.DefaultAlphaThreshold, 0, 1);
    private readonly ComboBox _frames = new() { Name = "PixelModelFrames", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _summary = new() { Name = "PixelModelSummary", AutoSize = true };
    private readonly Label _transform = new() { Name = "PixelModelTransform", AutoSize = true };
    private readonly Button _create = new() { Name = "PixelModelCreate", Text = "Create Model", AutoSize = true };
    private readonly Dictionary<EditorGizmoMode, Button> _modes = [];
    private readonly List<Label> _labels = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private EditorGizmoMode _mode = EditorGizmoMode.Move;
    private bool _dirty = true, _configuring;
    private int? _dragAxis;
    private PointF _dragStart;
    private PixelModelSettings? _dragFrom;

    public PixelModelDialog(PixelModelSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Text = "Convert to 3D Model"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1100, 740); MinimumSize = new Size(760, 520);
        ShowInTaskbar = false; MinimizeBox = false;
        BackColor = EditorChrome.Canvas; ForeColor = EditorChrome.Text; Font = EditorChrome.BaseFont;

        Viewport = new EditorViewport3D { Name = "PixelModelPreview", Dock = DockStyle.Fill, FloorStyle = EditorFloorStyle.GridOnly, MiddleButtonPans = true };
        Viewport.SceneStateFactory = () =>
        {
            var state = EditorSceneLighting.Create(false);
            state.BackgroundColor = new Vector3(EditorChrome.Canvas.R, EditorChrome.Canvas.G, EditorChrome.Canvas.B) / 255;
            state.FogEnabled = state.FogScreenSpace = false;
            return state;
        };
        Viewport.DrawScene += DrawPreview;
        Viewport.DrawOverlay += DrawGizmo;
        Viewport.Host.MouseDown += (_, e) => BeginGizmoDrag(e);
        Viewport.Host.MouseMove += (_, e) => MoveGizmoDrag(e);
        Viewport.Host.MouseUp += (_, _) => EndGizmoDrag();
        Viewport.Camera.Yaw = MathF.PI - .55f; Viewport.Camera.Pitch = -.3f;

        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _body.Controls.Add(_side, 0, 0); _body.Controls.Add(Viewport, 1, 0);
        foreach (Control control in _body.Controls) control.Margin = Padding.Empty;
        TableLayoutPanel fields = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(16) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _side.Controls.Add(fields);

        int frameCount = source.Frames.Count;
        _name.Text = source.SuggestedName;
        _frames.Items.Add($"Current frame ({Math.Clamp(source.CurrentFrameIndex, 0, Math.Max(0, frameCount - 1)) + 1} of {frameCount})");
        if (frameCount > 1) _frames.Items.Add($"All {frameCount} frames, as a \"Frames\" animation");
        _frames.SelectedIndex = 0;

        Add("1. Name the Model", _name);
        Add("2. Size per pixel (units; 0.0625 = 16 pixels a unit)", _pixelSize);
        Add("Depth (pixels)", _depth);
        Add("Alpha threshold (this opaque or more is solid, 1–255)", _alpha);
        Add("Frames", _frames);
        Add("", _summary);
        FlowLayoutPanel modes = new() { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        foreach ((EditorGizmoMode mode, string text) in new[] { (EditorGizmoMode.Move, "Move"), (EditorGizmoMode.Rotate, "Rotate"), (EditorGizmoMode.Scale, "Resize") })
        {
            Button button = new() { Name = "PixelModelGizmo" + mode, Text = text, AutoSize = true, Margin = new Padding(0, 0, 6, 6) };
            button.Click += (_, _) => SetGizmoMode(mode);
            _modes[mode] = button; modes.Controls.Add(button);
        }
        Button reset = new() { Name = "PixelModelResetTransform", Text = "Reset", AutoSize = true, Margin = new Padding(0, 0, 6, 6) };
        reset.Click += (_, _) => SetTransform(Vector3.Zero, Vector3.Zero, Vector3.One);
        modes.Controls.Add(reset);
        Add("3. Place it", Caption("Drag a coloured gizmo handle in the preview. Resize stretches each axis on its own. Right-drag orbits, middle-drag pans, scroll zooms."));
        Add("", modes);
        Add("", _transform);

        foreach (NumericUpDown value in new[] { _pixelSize, _depth, _alpha }) value.ValueChanged += (_, _) => { if (!_configuring) Rebuild(); };
        _frames.SelectedIndexChanged += (_, _) => { if (!_configuring) Rebuild(); };
        _name.TextChanged += (_, _) => _settings.Name = _name.Text;

        FlowLayoutPanel footer = new() { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = EditorChrome.Surface };
        Button cancel = new() { Name = "PixelModelCancel", Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _create.Click += (_, _) => CreateFromDialog();
        foreach (Button action in new[] { _create, cancel }) { EditorChrome.StyleField(action); footer.Controls.Add(action); }
        Controls.Add(_body); Controls.Add(footer); AcceptButton = _create; CancelButton = cancel;
        SizeChanged += (_, _) => ApplyInterfaceLayout();
        // A theme applied after construction repaints every button; the active gizmo mode is restored.
        Shown += (_, _) => { SetGizmoMode(_mode); FramePreview(); };
        Disposed += (_, _) => { if (Viewport.Host.Renderer is { } renderer) _renderer.InvalidateAssets(renderer); };
        SetGizmoMode(EditorGizmoMode.Move);
        Rebuild();
        ApplyInterfaceLayout();

        void Add(string caption, Control field)
        {
            if (caption.Length > 0) fields.Controls.Add(Caption(caption));
            field.Dock = DockStyle.Top; field.Margin = new Padding(0, 0, 0, 10);
            if (field is not FlowLayoutPanel and not Label) EditorChrome.StyleField(field);
            fields.Controls.Add(field);
            if (field is Label label) _labels.Add(label);
        }
        Label Caption(string text)
        {
            Label label = new() { Text = text, AutoSize = true, ForeColor = EditorChrome.Muted, Margin = new Padding(0, 8, 0, 4) };
            _labels.Add(label); return label;
        }
    }

    /// <summary>The Image Editor hook: shows the dialog and returns the created Model's path.</summary>
    public static string? Show(IWin32Window? owner, PixelModelSource source, Action<Control>? theme = null)
    {
        using PixelModelDialog dialog = new(source);
        theme?.Invoke(dialog);
        dialog.SetGizmoMode(dialog.GizmoMode);
        DialogResult result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return result == DialogResult.OK ? dialog.CreatedModelPath : null;
    }

    public EditorViewport3D Viewport { get; }
    /// <summary>The preview model, untransformed; the transform is drawn as its world matrix.</summary>
    public GModelAsset? Preview { get; private set; }
    public PixelModelSettings Settings => _settings.Clone();
    public EditorGizmoMode GizmoMode => _mode;
    public string? CreatedModelPath { get; private set; }
    /// <summary>A fixed preview time in seconds; null plays the animation in real time.</summary>
    public float? PreviewTime { get; set; }

    public void Configure(float pixelSize, int depthPixels, int alphaThreshold, bool allFrames, string? name = null)
    {
        _configuring = true;
        try
        {
            _pixelSize.Value = Math.Clamp((decimal)pixelSize, _pixelSize.Minimum, _pixelSize.Maximum);
            _depth.Value = Math.Clamp(depthPixels, (int)_depth.Minimum, (int)_depth.Maximum);
            _alpha.Value = Math.Clamp(alphaThreshold, 1, 255);
            _frames.SelectedIndex = allFrames && _frames.Items.Count > 1 ? 1 : 0;
            if (name is not null) _name.Text = name;
        }
        finally { _configuring = false; }
        Rebuild();
    }

    public void SetGizmoMode(EditorGizmoMode mode)
    {
        _mode = mode;
        foreach ((EditorGizmoMode key, Button button) in _modes)
        {
            bool active = key == mode;
            button.BackColor = active ? EditorChrome.Accent : EditorChrome.Raised;
            button.ForeColor = active ? Color.White : EditorChrome.Text;
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = active ? EditorChrome.Accent : EditorChrome.Border;
        }
        Viewport.Invalidate(true);
    }

    public void SetTransform(Vector3 position, Vector3 rotationDegrees, Vector3 scale)
    {
        _settings.Position = position;
        _settings.RotationDegrees = rotationDegrees;
        _settings.Scale = Vector3.Max(scale, new Vector3(.01f));
        RefreshTransform();
    }

    /// <summary>
    /// One gizmo drag along an axis: <paramref name="amount"/> is world units along the handle for
    /// Move, the handle lengths stretched for Resize, and half-turns for Rotate.
    /// </summary>
    public void ApplyGizmoDrag(EditorGizmoMode mode, int axis, float amount) => ApplyGizmoDrag(_settings.Clone(), mode, axis, amount);

    /// <summary>The model with its move, rotation and resize baked in.</summary>
    public GModelAsset BuildModel() => PixelModelBuilder.Build(_source, _settings.Clone());

    /// <summary>Builds and saves the Model resource beside the Image; returns its path.</summary>
    public string CreateModel()
    {
        if (string.IsNullOrWhiteSpace(_source.ImagePath))
            throw new InvalidOperationException("Save the Image in a Genesis project before converting it.");
        GModelAsset asset = BuildModel();
        CreatedModelPath = PixelModelResource.Create(_source.ImagePath, asset, _name.Text);
        return CreatedModelPath;
    }

    public void FramePreview()
    {
        if (Preview is null) return;
        (Vector3 min, Vector3 max) = TransformedBounds();
        Viewport.Camera.Target = (min + max) / 2;
        float aspect = Math.Max(.3f, Viewport.Width / (float)Math.Max(1, Viewport.Height));
        float angle = Viewport.FieldOfViewDegrees * MathF.PI / 360;
        Viewport.Camera.Distance = Math.Max(.1f, (max - min).Length() * .65f / MathF.Sin(Math.Min(angle, MathF.Atan(MathF.Tan(angle) * aspect))));
        Viewport.NearPlane = Math.Clamp(Viewport.Camera.Distance / 200f, .001f, .1f);
    }

    public override void ApplyInterfaceLayout()
    {
        if (_body.ColumnStyles.Count == 0) return;
        float scale = Math.Max(1, EditorChrome.BaseFont.SizeInPoints / 9.5f);
        _body.ColumnStyles[0].Width = Math.Min(330 * scale, Math.Max(260, ClientSize.Width * .42f));
        int width = Math.Max(160, _side.ClientSize.Width - 48 - SystemInformation.VerticalScrollBarWidth);
        foreach (Label label in _labels) label.MaximumSize = new Size(width, 0);
        foreach (Button button in Controls.OfType<FlowLayoutPanel>().SelectMany(panel => panel.Controls.OfType<Button>()))
            button.MinimumSize = new Size(0, button.Font.Height + 18);
    }

    private void Rebuild()
    {
        _settings.PixelSize = (float)_pixelSize.Value;
        _settings.DepthPixels = (int)_depth.Value;
        _settings.AlphaThreshold = (int)_alpha.Value;
        _settings.AllFrames = _frames.SelectedIndex == 1;
        _settings.Name = _name.Text;
        Preview = null;
        try
        {
            Preview = PixelModelBuilder.Build(_source, _settings.Clone(), bakeTransform: false);
            IReadOnlyList<int> frames = PixelModelBuilder.FrameIndices(_source, _settings);
            int naive = frames.Sum(index => PixelModelBuilder.NaiveCubeTriangles(_source.Frames[index], _source.Width, _source.Height, _settings));
            int triangles = PixelModelBuilder.TriangleCount(Preview);
            _summary.Text = $"{triangles:N0} triangles (one cube per pixel would be {naive:N0})"
                + (frames.Count > 1 ? $" · {frames.Count} frames, each on its own bone" : string.Empty);
            _summary.ForeColor = EditorChrome.Muted;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _summary.Text = exception.Message; _summary.ForeColor = EditorChrome.Warning;
        }
        _create.Enabled = Preview is not null;
        _dirty = true;
        RefreshTransform();
        FramePreview();
    }

    private void RefreshTransform()
    {
        string Format(Vector3 value, string format) => string.Join(", ",
            new[] { value.X, value.Y, value.Z }.Select(component => component.ToString(format, CultureInfo.InvariantCulture)));
        string size = string.Empty;
        if (Preview is not null)
        {
            (Vector3 min, Vector3 max) = TransformedBounds();
            Vector3 extent = max - min;
            size = $"Size {extent.X:0.###} × {extent.Y:0.###} × {extent.Z:0.###} units\n";
        }
        _transform.Text = size + $"Position {Format(_settings.Position, "0.###")}\nRotation {Format(_settings.RotationDegrees, "0.#")}°\nScale {Format(_settings.Scale, "0.###")}";
        Viewport.Invalidate(true);
    }

    private (Vector3 Min, Vector3 Max) TransformedBounds()
    {
        GModelBounds bounds = Preview?.Bounds ?? new GModelBounds();
        Matrix4x4 transform = _settings.Transform;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = new((corner & 1) == 0 ? bounds.Min.X : bounds.Max.X, (corner & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (corner & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            point = Vector3.Transform(point, transform);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        return (min, max);
    }

    private float GizmoLength()
    {
        (Vector3 min, Vector3 max) = TransformedBounds();
        return Math.Max(.05f, (max - min).Length() * .45f);
    }

    private EditorGizmoSpace GizmoSpace => _mode == EditorGizmoMode.Move ? EditorGizmoSpace.World : EditorGizmoSpace.Local;

    private void DrawPreview(IRenderController renderer)
    {
        if (_dirty) { _renderer.InvalidateAssets(renderer); _dirty = false; }
        if (Preview is null) return;
        RuntimeModelAnimationState animation = Preview.Animations.Count > 0
            ? new RuntimeModelAnimationState(PixelModelBuilder.ClipName, PreviewTime ?? (float)_clock.Elapsed.TotalSeconds, PixelModelBuilder.ClipFps, true)
            : default;
        _renderer.DrawAsset(Preview, string.Empty, _settings.Transform, animation, renderer);
    }

    private void DrawGizmo(IRenderController renderer)
    {
        if (Preview is null) return;
        EditorTransformGizmo.Draw3D(Viewport, renderer, _settings.Position, GizmoLength(), _mode, GizmoSpace,
            _settings.RotationDegrees, includeZ: true, activeAxis: _dragAxis);
    }

    private void BeginGizmoDrag(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || Preview is null) return;
        PointF surface = Viewport.ControlToSurface(e.Location);
        EditorGizmoHit? hit = EditorTransformGizmo.HitTest3D(Viewport, surface, _settings.Position, GizmoLength(), _mode, GizmoSpace,
            _settings.RotationDegrees, includeZ: true, threshold: 12f);
        if (hit is null) return;
        _dragAxis = hit.Value.AxisIndex; _dragStart = surface; _dragFrom = _settings.Clone();
        Viewport.Host.Capture = true;
    }

    private void MoveGizmoDrag(MouseEventArgs e)
    {
        if (_dragAxis is not int axis || _dragFrom is null) return;
        float length = GizmoLength();
        Vector3 direction = EditorTransformGizmo.Axes(GizmoSpace, _dragFrom.RotationDegrees)[axis];
        if (!EditorTransformGizmo.TryProjectAxisToSurface(Viewport, _dragFrom.Position, direction, length, out Vector2 onScreen, out float screenLength))
            return;
        PointF surface = Viewport.ControlToSurface(e.Location);
        Vector2 delta = new(surface.X - _dragStart.X, surface.Y - _dragStart.Y);
        float along = Vector2.Dot(delta, onScreen) / screenLength;
        // Rotation follows the drag across the screen, since dragging along the axis itself turns nothing.
        if (_mode == EditorGizmoMode.Rotate) along = (delta.X - delta.Y) / Math.Max(1f, screenLength);
        ApplyGizmoDrag(_dragFrom, _mode, axis, _mode == EditorGizmoMode.Move ? along * length : along);
    }

    private void EndGizmoDrag()
    {
        if (_dragAxis is null) return;
        _dragAxis = null; _dragFrom = null; Viewport.Host.Capture = false;
        Viewport.Invalidate(true);
    }

    private void ApplyGizmoDrag(PixelModelSettings from, EditorGizmoMode mode, int axis, float amount)
    {
        if ((uint)axis > 2 || !float.IsFinite(amount)) return;
        Vector3 unit = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        switch (mode)
        {
            case EditorGizmoMode.Move:
                SetTransform(from.Position + unit * amount, from.RotationDegrees, from.Scale);
                break;
            case EditorGizmoMode.Rotate:
                SetTransform(from.Position, from.RotationDegrees + unit * amount * 180f, from.Scale);
                break;
            default:
                float factor = Math.Max(.05f, 1f + amount);
                SetTransform(from.Position, from.RotationDegrees, from.Scale * (Vector3.One + unit * (factor - 1f)));
                break;
        }
    }

    private void CreateFromDialog()
    {
        try
        {
            CreateModel();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _summary.Text = exception.Message; _summary.ForeColor = EditorChrome.Warning;
        }
    }

    private static NumericUpDown Number(string name, decimal min, decimal max, decimal value, int decimals, decimal increment) => new()
    { Name = name, Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Increment = increment };
}
