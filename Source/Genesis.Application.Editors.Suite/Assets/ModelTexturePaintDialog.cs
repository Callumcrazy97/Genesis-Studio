using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>One Image draft and undo history, painted from its 2D canvas or the Model's UVs.</summary>
public sealed class ModelTexturePaintDialog : DpiAwareForm
{
    private readonly GModelAsset _asset;
    private readonly int _material;
    private readonly ModelGpuCache _gpu = new();
    private readonly Label _status = new() { Name = "ModelTexturePaintStatus", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 6, 12, 6) };
    private readonly Label _guide = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 6, 12, 6),
        Text = "Paint the Image on the left or left-drag on the Model. Both edit the same Image layer and share Undo. Models use frame 1. Right-drag orbits; middle-drag pans. Save Image updates other editors and gameplay. Painting changes colours; geometry stays as authored." };
    private readonly Button _colour = new() { Name = "ModelTextureBrushColour", Text = "Brush colour…", AutoSize = true };
    private readonly NumericUpDown _size = new() { Name = "ModelTextureBrushSize", Minimum = 1, Maximum = 512, Value = 4, Width = 86 };
    private readonly FlowLayoutPanel _tools = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10), WrapContents = true };
    private TextureHandle _texture;
    private IRenderController? _textureOwner;
    private long _textureVersion = -1;
    private int _textureWidth, _textureHeight;
    private PixelStrokeRecorder? _stroke;
    private ImageLayerBuffer? _strokeLayer;
    private Point? _lastPointer;
    private Point? _hover;
    private bool _refreshing;

    public ModelTexturePaintDialog(string projectRoot, GModelAsset asset, int materialIndex)
    {
        _asset = ModelPoseWorkflow.Copy(asset);
        _material = materialIndex;
        if ((uint)materialIndex >= _asset.Materials.Count) throw new InvalidOperationException("Choose a mesh material first.");
        string reference = _asset.Materials[materialIndex].AlbedoTexture;
        string image = SpriteAssetLoader.ResolveDescriptorPath(projectRoot, reference);
        if (string.IsNullOrWhiteSpace(reference) || !SpriteAssetLoader.IsSpriteDescriptorPath(image) || !File.Exists(image))
            throw new InvalidOperationException("Assign a saved Image to Albedo before painting. Raw texture files can be imported in the Image Editor.");
        ImagePath = image;
        ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(image).Document, image, ImageDocumentAccess.Editor);
        Image = new ImageEditorControl(session, ImageWorkspaceStorage.Load(session)) { Dock = DockStyle.Fill };
        Image.ConfigureMaterialPainting(); Image.SetForegroundColor(Color.Coral); Image.SetBrushSize(4);
        Viewport = new EditorViewport3D { Name = "ModelTexturePaintingViewport", Dock = DockStyle.Fill, FloorStyle = EditorFloorStyle.None };
        Viewport.Camera.Yaw = MathF.PI - .45f; Viewport.Camera.Pitch = -.15f;
        Viewport.SceneStateFactory = () =>
        {
            var state = EditorSceneLighting.Create(false); state.LightingEnabled = false; state.FogEnabled = state.FogScreenSpace = false;
            state.BackgroundColor = new Vector3(EditorChrome.Canvas.R, EditorChrome.Canvas.G, EditorChrome.Canvas.B) / 255;
            return state;
        };
        Viewport.DrawScene += DrawModel;
        Viewport.DrawOverlay += renderer =>
        {
            if (_hover is not { } point) return;
            renderer.DrawRect(point.X - 6, point.Y - 6, 12, 12, RenderColor.White, filled: false, depth: -9000);
            Color color = Image.ForegroundColor;
            renderer.DrawRect(point.X - 2, point.Y - 2, 4, 4, new RenderColor(color.R / 255f, color.G / 255f, color.B / 255f), filled: true, depth: -9001);
        };
        Viewport.Host.MouseLeave += (_, _) => _hover = null;
        Viewport.Host.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            BeginStroke(); PaintAt(e.Location); _lastPointer = e.Location;
            Viewport.NavigationEnabled = false; Viewport.Host.Capture = true;
        };
        Viewport.Host.MouseMove += (_, e) =>
        {
            _hover = e.Location;
            if (_stroke is null) return;
            Point from = _lastPointer ?? e.Location;
            int samples = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Pow(e.X - from.X, 2) + Math.Pow(e.Y - from.Y, 2)) / 3));
            for (int index = 1; index <= samples; index++)
                PaintAt(new Point(from.X + (e.X - from.X) * index / samples, from.Y + (e.Y - from.Y) * index / samples));
            _lastPointer = e.Location;
        };
        Viewport.Host.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) FinishStroke(); };
        Text = "Paint Model Image"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1440, 900); MinimumSize = new Size(860, 600); ShowInTaskbar = false; MinimizeBox = false;
        BackColor = EditorChrome.Canvas; ForeColor = EditorChrome.Text; Font = EditorChrome.BaseFont;
        TableLayoutPanel body = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); body.Controls.Add(Image, 0, 0); body.Controls.Add(Viewport, 1, 0);
        foreach (Control control in body.Controls) control.Margin = Padding.Empty;
        _colour.Click += (_, _) =>
        {
            using ColorDialog color = new() { Color = Image.ForegroundColor, FullOpen = true };
            if (color.ShowDialog(this) == DialogResult.OK) SetBrush(color.Color, Image.BrushSize);
        };
        _size.ValueChanged += (_, _) => Image.SetBrushSize((int)_size.Value);
        Add(_colour); Add(new Label { Text = "Size (pixels)", AutoSize = true, Padding = new Padding(6, 6, 0, 0) }); Add(_size);
        Button undo = new() { Text = "Undo", AutoSize = true }; undo.Click += (_, _) => { FinishStroke(); Image.Undo(); };
        Button redo = new() { Text = "Redo", AutoSize = true }; redo.Click += (_, _) => { FinishStroke(); Image.Redo(); };
        Add(undo); Add(redo);
        FlowLayoutPanel footer = new() { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        Button close = new() { Text = "Close", AutoSize = true }; close.Click += (_, _) => Close();
        Button save = new() { Name = "SaveModelImage", Text = "Save Image", AutoSize = true }; save.Click += (_, _) => SaveImage();
        EditorChrome.StyleField(close); EditorChrome.StyleField(save); footer.Controls.Add(close); footer.Controls.Add(save);
        Controls.Add(body); Controls.Add(_status); Controls.Add(_tools); Controls.Add(_guide); Controls.Add(footer);
        Image.DirtyChanged += (_, _) => RefreshStatus();
        Shown += (_, _) => { Image.Canvas.FitToView(); FrameModel(); };
        SizeChanged += (_, _) => ApplyInterfaceLayout();
        FormClosing += (_, e) =>
        {
            FinishStroke();
            if (!Image.IsDirty) return;
            DialogResult result = MessageBox.Show(this, "Save the Image changes before closing?", "Model Image", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (result == DialogResult.Cancel) e.Cancel = true;
            else if (result == DialogResult.Yes) { try { SaveImage(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save Image"); e.Cancel = true; } }
        };
        Disposed += (_, _) =>
        {
            if (_textureOwner is not null) { if (_texture.IsValid) _textureOwner.ReleaseTexture(_texture); _gpu.Clear(_textureOwner); }
        };
        RefreshStatus();
        void Add(Control field) { EditorChrome.StyleField(field); field.Margin = new Padding(0, 0, 10, 0); _tools.Controls.Add(field); }
    }

    public string ImagePath { get; }
    public ImageEditorControl Image { get; }
    public EditorViewport3D Viewport { get; }
    public void SetBrush(Color color, int size) { Image.SetForegroundColor(color); Image.SetBrushSize(size); _size.Value = size; RefreshStatus(); }
    public void SaveImage() { FinishStroke(); Image.Save(); RefreshStatus(); }
    public void BeginStroke()
    {
        FinishStroke();
        if (Image.Workspace.SelectedFrameIndex != 0) { Image.Workspace.SelectedFrameIndex = 0; Image.RefreshFromDocument(); }
        _strokeLayer = Image.Workspace.CurrentLayer;
        if (_strokeLayer is null || _strokeLayer.Locked || !_strokeLayer.Visible || _strokeLayer.Channel != ImageMaterialChannel.Color)
        { _status.Text = "Choose an unlocked, visible colour layer before painting on the Model."; _strokeLayer = null; return; }
        _stroke = new(Image.Workspace, _strokeLayer);
    }
    public bool PaintRay(Vector3 origin, Vector3 direction)
    {
        if (_stroke is null || _strokeLayer is null) return false;
        float best = float.MaxValue; Vector2 uv = default; bool found = false;
        foreach (GModelMesh mesh in _asset.Meshes)
        {
            MeshVertex[] vertices = mesh.IsSkinned ? mesh.SkinnedVertices.Select(v => new MeshVertex { Position = v.Position, UV = v.UV }).ToArray() : mesh.Vertices;
            if (!ModelSurfaceBrush.RaycastUv(vertices, mesh.Indices, origin + _asset.Pivot.Position, direction, out Vector3 hit, out Vector2 candidate, out int triangle)) continue;
            float distance = Vector3.DistanceSquared(origin + _asset.Pivot.Position, hit);
            if (distance >= best) continue;
            best = distance; uv = candidate;
            // Occluding meshes of another material must block painting through them.
            int material = mesh.TriangleMaterialIndices.Length == mesh.Indices.Length / 3 ? mesh.TriangleMaterialIndices[triangle] : mesh.MaterialIndex;
            found = material == _material;
        }
        if (!found || !float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) return false;
        uv *= RuntimeModelRenderSystem.MaterialUvScale(_asset.Materials[_material]);
        int x = Math.Clamp((int)MathF.Floor((uv.X - MathF.Floor(uv.X)) * Image.Workspace.Width), 0, Image.Workspace.Width - 1);
        int y = Math.Clamp((int)MathF.Floor((uv.Y - MathF.Floor(uv.Y)) * Image.Workspace.Height), 0, Image.Workspace.Height - 1);
        int radius = Image.BrushSize / 2 + 2;
        _stroke.Capture(new Rectangle(x - radius, y - radius, radius * 2 + 1, radius * 2 + 1));
        RasterOperations.StampBrush(_strokeLayer.Pixels, Image.Workspace.Width, Image.Workspace.Height, x, y, Image.ForegroundColor,
            new ImageBrushSettings { Size = Image.BrushSize }, erase: false, alphaLock: _strokeLayer.AlphaLocked, Image.Workspace.Selection);
        Image.Workspace.InvalidateComposite(); Image.RefreshFromDocument();
        return true;
    }
    public bool PaintAt(Point point) { _hover = point; var ray = Viewport.PickRay(point); return PaintRay(ray.Origin, ray.Direction); }
    public void FinishStroke(bool cancel = false)
    {
        if (_stroke is not null)
        {
            var command = _stroke.Complete("Paint Model Image");
            if (cancel) { command.Undo(Image.Session); Image.RefreshFromDocument(); }
            else Image.CommitPixelEdit(command);
        }
        _stroke = null; _strokeLayer = null; _lastPointer = null; Viewport.NavigationEnabled = true; Viewport.Host.Capture = false;
    }
    private void DrawModel(IRenderController renderer)
    {
        if (_textureOwner != renderer)
        {
            _textureOwner = renderer; _texture = TextureHandle.Invalid; _textureVersion = -1;
        }
        ImageWorkspace workspace = Image.Workspace;
        if (_textureVersion != workspace.Version || !_texture.IsValid)
        {
            byte[] rgba = workspace.CompositeCurrentFrameFor(0).Pixels;
            if (_texture.IsValid && (_textureWidth != workspace.Width || _textureHeight != workspace.Height))
            { renderer.ReleaseTexture(_texture); _texture = TextureHandle.Invalid; }
            if (_texture.IsValid) renderer.UpdateTexture(_texture, workspace.Width, workspace.Height, rgba);
            else _texture = renderer.CreateTexture(workspace.Width, workspace.Height, rgba);
            _textureVersion = workspace.Version; _textureWidth = workspace.Width; _textureHeight = workspace.Height;
        }
        var cache = _gpu.GetOrCreate(renderer, _asset);
        var palette = _gpu.UpdatePalette(renderer, _asset, cache, default);
        foreach (var mesh in cache.Meshes)
        {
            bool painted = mesh.MaterialIndex == _material;
            Vector4 baseColor = painted ? _asset.Materials[_material].BaseColor : new(.4f, .4f, .4f, 1);
            renderer.DrawMesh(new MeshDrawCall { Mesh = mesh.Mesh, SkinPalette = mesh.IsSkinned ? palette : SkinPaletteHandle.Invalid,
                World = Matrix4x4.CreateTranslation(-_asset.Pivot.Position), Texture = painted ? _texture : TextureHandle.Invalid,
                DetailParams = new(0, 0, 0, RuntimeModelRenderSystem.MaterialUvScale(_asset.Materials[_material])),
                Tint = new(baseColor.X, baseColor.Y, baseColor.Z, baseColor.W), Alpha = baseColor.W,
                Flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog });
        }
    }
    public void FrameModel()
    {
        var meshes = _asset.Meshes.Where(mesh => mesh.MaterialIndex == _material).ToArray();
        Vector3[] positions = meshes.SelectMany(mesh => mesh.IsSkinned ? mesh.SkinnedVertices.Select(v => v.Position) : mesh.Vertices.Select(v => v.Position)).ToArray();
        if (positions.Length == 0) return;
        Vector3 min = positions.Aggregate(Vector3.Min), max = positions.Aggregate(Vector3.Max);
        Viewport.Camera.Target = (min + max) / 2 - _asset.Pivot.Position;
        float aspect = Math.Max(.3f, Viewport.Width / (float)Math.Max(1, Viewport.Height));
        float angle = Viewport.FieldOfViewDegrees * MathF.PI / 360;
        Viewport.Camera.Distance = Math.Max(.1f, (max - min).Length() * .65f / MathF.Sin(Math.Min(angle, MathF.Atan(MathF.Tan(angle) * aspect))));
    }
    private void RefreshStatus()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { _status.Text = Path.GetFileNameWithoutExtension(ImagePath) + " · Frame 1 · " + (Image.IsDirty || _stroke is not null ? "Unsaved Image changes" : "Saved Image");
            _colour.BackColor = Image.ForegroundColor;
            _colour.ForeColor = Image.ForegroundColor.GetBrightness() > .45f ? Color.Black : Color.White;
            _size.Value = Image.BrushSize; }
        finally { _refreshing = false; }
    }
    public override void ApplyInterfaceLayout()
    {
        _guide.MaximumSize = _status.MaximumSize = new Size(Math.Max(200, ClientSize.Width), 0);
        foreach (Button button in _tools.Controls.OfType<Button>()) button.MinimumSize = new Size(0, button.Font.Height + 18);
        RefreshStatus();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _stroke is not null) { FinishStroke(cancel: true); return true; }
        if (keyData == (Keys.Control | Keys.S)) { SaveImage(); return true; }
        if (keyData == (Keys.Control | Keys.Z)) { FinishStroke(); Image.Undo(); return true; }
        if (keyData == (Keys.Control | Keys.Y)) { FinishStroke(); Image.Redo(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
