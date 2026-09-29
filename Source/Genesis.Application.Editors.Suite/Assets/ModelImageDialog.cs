using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Choose an Image, inspect its actual 3D mesh, then append it to the current Model.</summary>
public sealed class ModelImageDialog : DpiAwareForm
{
    private readonly string _projectRoot;
    private readonly RuntimeModelRenderSystem _renderer = new();
    private readonly TableLayoutPanel _body = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
    private readonly Label _imageName = new() { Name = "ModelImageSource", Text = "Choose a saved Image", AutoSize = true };
    private readonly Label _summary = new() { Name = "ModelImageSummary", AutoSize = true };
    private readonly NumericUpDown _width = Number("ModelImageWidth", .01m, 1000, 2, 2);
    private readonly NumericUpDown _depth = Number("ModelImageDepth", .01m, 1000, .25m, 2);
    private readonly NumericUpDown _detail = Number("ModelImageDetail", 2, 64, 32, 0);
    private readonly NumericUpDown _opacity = Number("ModelImageOpacity", 1, 255, 48, 0);
    private readonly Button _create = new() { Name = "CreateModelFromImage", Text = "Add to model", AutoSize = true };
    private readonly Panel _settings = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly List<Label> _labels = [];
    private ImageMaterialPixels? _pixels;
    private string _reference = "";
    private bool _dirty = true, _configuring;

    public ModelImageDialog(string projectRoot, string? imageReference = null)
    {
        _projectRoot = projectRoot;
        Text = "Create Model from Image"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1100, 740); MinimumSize = new Size(740, 500);
        ShowInTaskbar = false; MinimizeBox = false;
        BackColor = EditorChrome.Canvas; ForeColor = EditorChrome.Text; Font = EditorChrome.BaseFont;
        Viewport = new EditorViewport3D { Name = "ModelImagePreview", Dock = DockStyle.Fill, FloorStyle = EditorFloorStyle.GridOnly };
        Viewport.SceneStateFactory = () =>
        {
            var state = EditorSceneLighting.Create(false);
            state.BackgroundColor = new Vector3(EditorChrome.Canvas.R, EditorChrome.Canvas.G, EditorChrome.Canvas.B) / 255;
            state.FogEnabled = state.FogScreenSpace = false;
            state.LightingEnabled = false;
            return state;
        };
        Viewport.DrawScene += renderer =>
        {
            if (_dirty) { _renderer.InvalidateAssets(renderer); _dirty = false; }
            if (Result is not null) _renderer.DrawAsset(Result, _projectRoot, Matrix4x4.Identity, default, renderer);
        };
        Viewport.Camera.Yaw = MathF.PI - .45f; Viewport.Camera.Pitch = -.15f;
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _body.Controls.Add(_settings, 0, 0); _body.Controls.Add(Viewport, 1, 0);
        foreach (Control control in _body.Controls) control.Margin = Padding.Empty;
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(16) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _settings.Controls.Add(fields);
        Add("1. Choose your pixel art", Label("Saved layers and transparency are taken from the first animation frame."));
        Add("", _imageName);
        var choose = new Button { Name = "ChooseModelSourceImage", Text = "Choose Image…", AutoSize = true };
        choose.Click += (_, _) =>
        {
            ProjectAssetEntry? picked = AssetPickerService.PickAsset(new(_projectRoot, ResourceKind.Image, _reference, "Choose Image for Model"), this);
            if (picked is not null) SelectImage(picked.Reference);
        };
        Add("", choose);
        Add("2. Set the shape", Label("Width is in metres. Height follows the Image proportions. Transparent areas become holes."));
        Add("Width (metres)", _width); Add("Depth (metres)", _depth); Add("Detail (longest edge)", _detail);
        Add("Minimum opacity (1–255)", _opacity);
        Add("", Label("255 is fully solid. Raise minimum opacity to remove faint edges; lower it to keep them. Fully transparent pixels always stay empty."));
        Add("", Label("Higher detail keeps smaller pixels and uses more triangles. Right-drag to orbit, middle-drag to pan and scroll to zoom."));
        Add("", _summary);
        Label gameSteps = Label("The new part stays editable. Its colours remain linked to the Image. Save the Model and use its Use in game action to create an Object.");
        gameSteps.Name = "ModelImageGameSteps";
        Add("3. Add, then use in game", gameSteps);
        foreach (NumericUpDown value in new[] { _width, _depth, _detail, _opacity }) value.ValueChanged += (_, _) => { if (!_configuring) Rebuild(); };
        FlowLayoutPanel footer = new() { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = EditorChrome.Surface };
        Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _create.Click += (_, _) => { if (Result is not null) { DialogResult = DialogResult.OK; Close(); } };
        foreach (Button action in new[] { _create, cancel }) { EditorChrome.StyleField(action); footer.Controls.Add(action); }
        Controls.Add(_body); Controls.Add(footer); AcceptButton = _create; CancelButton = cancel;
        SizeChanged += (_, _) => ApplyInterfaceLayout();
        Shown += (_, _) => FramePreview();
        Disposed += (_, _) => { if (Viewport.Host.Renderer is { } renderer) _renderer.InvalidateAssets(renderer); };
        if (!string.IsNullOrWhiteSpace(imageReference)) SelectImage(imageReference); else Rebuild();
        ApplyInterfaceLayout();

        void Add(string caption, Control field)
        {
            if (caption.Length > 0) fields.Controls.Add(Label(caption));
            field.Dock = DockStyle.Top; field.Margin = new Padding(0, 0, 0, 12);
            EditorChrome.StyleField(field); fields.Controls.Add(field);
            if (field is Label label) _labels.Add(label);
        }
        Label Label(string text)
        {
            Label label = new() { Text = text, AutoSize = true, ForeColor = EditorChrome.Muted, Margin = new Padding(0, 8, 0, 5) };
            _labels.Add(label); return label;
        }
    }

    public EditorViewport3D Viewport { get; }
    public GModelAsset? Result { get; private set; }
    public string ImageReference => _reference;
    public void ShowGameSteps() => _settings.ScrollControlIntoView(_labels[^1]);
    public bool SelectImage(string reference)
    {
        try
        {
            _reference = Path.IsPathRooted(reference) ? Path.GetRelativePath(_projectRoot, reference).Replace('\\', '/') : reference.Replace('\\', '/');
            _pixels = ImageMaterialAssetLoader.Load(_projectRoot, _reference);
            _imageName.Text = ResourceDisplayName.Format(_reference) + $" · {_pixels.Width} × {_pixels.Height}";
            Rebuild(); return Result is not null;
        }
        catch (Exception ex) { _pixels = null; Result = null; _dirty = true; _create.Enabled = false; _summary.Text = ex.Message; return false; }
    }
    public void Configure(float width, float depth, int detail, int opacityCutoff = 48)
    {
        _configuring = true;
        try { _width.Value = (decimal)width; _depth.Value = (decimal)depth; _detail.Value = detail; _opacity.Value = opacityCutoff; }
        finally { _configuring = false; }
        Rebuild();
    }
    private void Rebuild()
    {
        Result = null;
        try
        {
            if (_pixels is not null) Result = ModelImageGeometry.Build(_pixels, _reference, (float)_width.Value, (float)_depth.Value, (int)_detail.Value, (int)_opacity.Value);
            _summary.Text = Result is null ? "Choose an Image to see the 3D preview." : $"{Result.Meshes[0].Indices.Length / 3:N0} triangles · {Result.Bounds.Size.X:0.##} × {Result.Bounds.Size.Y:0.##} × {Result.Bounds.Size.Z:0.##} metres";
        }
        catch (Exception ex) { _summary.Text = ex.Message; }
        _create.Enabled = Result is not null; _dirty = true; FramePreview(); Viewport.Invalidate(true);
    }
    public void FramePreview()
    {
        if (Result is null) return;
        Vector3 size = Result.Bounds.Max - Result.Bounds.Min;
        Viewport.FloorHeight = Result.Bounds.Min.Y; Viewport.Camera.Target = (Result.Bounds.Min + Result.Bounds.Max) / 2;
        float aspect = Math.Max(.3f, Viewport.Width / (float)Math.Max(1, Viewport.Height));
        float angle = Viewport.FieldOfViewDegrees * MathF.PI / 360;
        Viewport.Camera.Distance = Math.Max(.1f, size.Length() * .6f / MathF.Sin(Math.Min(angle, MathF.Atan(MathF.Tan(angle) * aspect))));
    }
    public override void ApplyInterfaceLayout()
    {
        if (_body.ColumnStyles.Count == 0) return;
        float scale = Math.Max(1, EditorChrome.BaseFont.SizeInPoints / 9.5f);
        _body.ColumnStyles[0].Width = Math.Min(320 * scale, Math.Max(250, ClientSize.Width * .43f));
        int width = Math.Max(160, _settings.ClientSize.Width - 48 - SystemInformation.VerticalScrollBarWidth);
        foreach (Label label in _labels) label.MaximumSize = new Size(width, 0);
        foreach (Button button in Controls.OfType<FlowLayoutPanel>().SelectMany(panel => panel.Controls.OfType<Button>()))
            button.MinimumSize = new Size(0, button.Font.Height + 18);
    }
    private static NumericUpDown Number(string name, decimal min, decimal max, decimal value, int decimals) => new()
    { Name = name, Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : .05m };
}
