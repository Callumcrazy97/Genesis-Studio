using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite.Assets.Painting;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Texture painting on the model, in the Model editor only (the Viewer has no tools): a ray from the
/// pointer to the mesh's triangles, the texture coordinate there, the Image editor's brush into the
/// Image of that triangle's material, and the preview showing the stroke at once through a live
/// texture. One undo entry per stroke; Save writes the painted Images with the model.
/// </summary>
public sealed partial class ModelEditorControl
{
    private const int MaximumPaintTextureSize = 4096;
    private readonly Dictionary<string, ModelPaintTexture> _paintTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelPaintTexture> _paintByReference = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ModelPaintTexture, PixelStrokeRecorder> _paintStroke = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GModelMesh, (MeshVertex[] Source, bool HasUvs)> _paintUvChecks = new(ReferenceEqualityComparer.Instance);
    private readonly ImageBrushSettings _paintBrush = new() { Size = 16, Hardness = .8f, Opacity = 1f };
    private Color _paintBrushColour = Color.FromArgb(214, 48, 64);
    private bool _paintErase, _paintStrokeActive;
    private int _paintNewTextureSize = 1024;
    private (int Mesh, int Triangle, int Material, Vector2 Uv)? _lastPaintDab;
    private Point? _lastPaintPointer;
    private ModelPaintHit? _paintHover;
    private string _paintMessageText = "";
    private Label? _paintMessage;
    private Button? _paintToolButton, _paintEraseButton, _paintColourButton;
    private NumericUpDown? _paintSizeInput, _paintHardnessInput, _paintOpacityInput;
    private ThemedComboBox? _paintNewSizeInput;
    private bool _syncingPaint;

    private const string PaintGuide = "Left-drag on the model to paint its material's Image; each drag is one undo. "
        + "Erase takes strokes back off. A material with no Image gets one at the size below. Save keeps the painted Images.";

    public string TexturePaintMessage => _paintMessageText;
    public bool TexturePaintEraser => _paintErase;
    public Color TexturePaintColour => _paintBrushColour;
    public int TexturePaintSize => _paintBrush.Size;
    public float TexturePaintHardness => _paintBrush.Hardness;
    public float TexturePaintOpacity => _paintBrush.Opacity;
    public IReadOnlyCollection<ModelPaintTexture> PaintTextures => _paintTextures.Values;

    /// <summary>The size of the Image made for a material that has none when painting starts: 512 or 1024 pixels square.</summary>
    public int TexturePaintNewTextureSize
    {
        get => _paintNewTextureSize;
        set { _paintNewTextureSize = value <= 512 ? 512 : 1024; RefreshTexturePaintControls(); }
    }

    /// <summary>The Image being painted for a material, if painting has opened one for it.</summary>
    public ModelPaintTexture? PaintTextureForMaterial(int material) =>
        (uint)material < (uint)Asset.Materials.Count
        && _paintByReference.TryGetValue(Asset.Materials[material].AlbedoTexture ?? "", out ModelPaintTexture? texture) ? texture : null;

    public void SetTexturePaintBrush(Color colour, int size, float hardness = 1f, float opacity = 1f)
    {
        _paintBrushColour = Color.FromArgb(255, colour.R, colour.G, colour.B);
        _paintBrush.Size = Math.Clamp(size, 1, 512);
        _paintBrush.Hardness = Math.Clamp(hardness, 0f, 1f);
        _paintBrush.Opacity = Math.Clamp(opacity, .01f, 1f);
        RefreshTexturePaintControls();
    }

    public void SetTexturePaintEraser(bool erase) { FinishStroke(); _paintErase = erase; RefreshTexturePaintControls(); UpdateToolHeader(); }

    private void SelectTexturePaint(bool erase)
    {
        _paintErase = erase;
        SelectTool(ModelAuthoringTool.TexturePaint);
        RefreshTexturePaintControls();
        // Say at once when nothing here can be painted, rather than on the first stroke.
        if (Asset.Meshes.Count > 0 && Asset.Meshes.All(mesh => !HasPaintableUvs(mesh))) ShowPaintMessage(NoUvMessage(null));
        else ShowPaintMessage(PaintGuide);
    }

    private void BuildTexturePaintSection(FlowLayoutPanel page)
    {
        CollapsibleSection paint = Section(page, "PAINT ON THE MODEL", 318);
        paint.Name = "ModelTexturePaintSection";
        _paintToolButton = Tool("Paint", () => SelectTexturePaint(erase: false));
        _paintToolButton.Name = "ModelTexturePaintTool"; _paintToolButton.SetBounds(8, 6, 122, 31);
        _paintEraseButton = Tool("Erase", () => SelectTexturePaint(erase: true));
        _paintEraseButton.Name = "ModelTexturePaintErase"; _paintEraseButton.SetBounds(136, 6, 122, 31);
        _paintColourButton = Tool("Colour…", ChooseTexturePaintColour);
        _paintColourButton.Name = "ModelTexturePaintColour"; _paintColourButton.SetBounds(8, 44, 250, 31);
        paint.Content.Controls.Add(_paintToolButton); paint.Content.Controls.Add(_paintEraseButton); paint.Content.Controls.Add(_paintColourButton);
        _paintSizeInput = Whole(1, 512, _paintBrush.Size, value => _paintBrush.Size = value);
        _paintSizeInput.Name = "ModelTexturePaintSize";
        Field(paint.Content, "Size (pixels)", _paintSizeInput, 82);
        _paintHardnessInput = Whole(0, 100, (int)MathF.Round(_paintBrush.Hardness * 100), value => _paintBrush.Hardness = value / 100f);
        _paintHardnessInput.Name = "ModelTexturePaintHardness";
        Field(paint.Content, "Hardness (%)", _paintHardnessInput, 114);
        _paintOpacityInput = Whole(1, 100, (int)MathF.Round(_paintBrush.Opacity * 100), value => _paintBrush.Opacity = value / 100f);
        _paintOpacityInput.Name = "ModelTexturePaintOpacity";
        Field(paint.Content, "Opacity (%)", _paintOpacityInput, 146);
        _paintNewSizeInput = new ThemedComboBox { Name = "ModelTexturePaintNewSize", DropDownStyle = ComboBoxStyle.DropDownList };
        _paintNewSizeInput.Items.AddRange(["512 × 512", "1024 × 1024"]);
        _paintNewSizeInput.SelectedIndexChanged += (_, _) => { if (!_syncingPaint) _paintNewTextureSize = _paintNewSizeInput.SelectedIndex == 0 ? 512 : 1024; };
        Field(paint.Content, "New Image size", _paintNewSizeInput, 178);
        _paintMessage = new Label
        {
            Name = "ModelTexturePaintMessage", Text = PaintGuide, Location = new Point(8, 212), Size = new Size(250, 100), ForeColor = EditorChrome.Muted,
        };
        _paintMessageText = PaintGuide;
        paint.Content.Controls.Add(_paintMessage);
        RefreshTexturePaintControls();

        NumericUpDown Whole(int min, int max, int value, Action<int> changed)
        {
            NumericUpDown number = Number(min, max, Math.Clamp(value, min, max), entered => { if (!_syncingPaint) changed((int)entered); });
            number.DecimalPlaces = 0; number.Increment = 1;
            return number;
        }
    }

    private void ChooseTexturePaintColour()
    {
        using ColorDialog picker = new() { Color = _paintBrushColour, FullOpen = true };
        if (picker.ShowDialog(this) == DialogResult.OK) SetTexturePaintBrush(picker.Color, _paintBrush.Size, _paintBrush.Hardness, _paintBrush.Opacity);
    }

    private void RefreshTexturePaintControls()
    {
        if (_paintToolButton is null) return;
        _syncingPaint = true;
        try
        {
            bool active = _tool == ModelAuthoringTool.TexturePaint && _mode == ModelEditorMode.TexturePaint;
            _paintToolButton.BackColor = active && !_paintErase ? ImageEditorChrome.Hover : ImageEditorChrome.Raised;
            _paintEraseButton!.BackColor = active && _paintErase ? ImageEditorChrome.Hover : ImageEditorChrome.Raised;
            _paintColourButton!.BackColor = _paintBrushColour;
            _paintColourButton.ForeColor = _paintBrushColour.GetBrightness() > .45f ? Color.Black : Color.White;
            _paintSizeInput!.Value = Math.Clamp(_paintBrush.Size, (int)_paintSizeInput.Minimum, (int)_paintSizeInput.Maximum);
            _paintHardnessInput!.Value = Math.Clamp((int)MathF.Round(_paintBrush.Hardness * 100), 0, 100);
            _paintOpacityInput!.Value = Math.Clamp((int)MathF.Round(_paintBrush.Opacity * 100), 1, 100);
            _paintNewSizeInput!.SelectedIndex = _paintNewTextureSize <= 512 ? 0 : 1;
        }
        finally { _syncingPaint = false; }
    }

    private void ShowPaintMessage(string text)
    {
        _paintMessageText = text;
        if (_paintMessage is not null) _paintMessage.Text = text;
        Status.Text = text;
    }

    private static string NoUvMessage(GModelMesh? mesh) => (mesh is null ? "This model has" : $"“{mesh.Name}” has")
        + " no UVs, so there is nowhere on an Image for paint to go. Give it UVs first (Quick UV Unwrap, or Box UVs, under Material & PBR), then paint.";

    private bool HasPaintableUvs(GModelMesh mesh)
    {
        MeshVertex[] vertices = PickVertices(mesh);
        if (_paintUvChecks.TryGetValue(mesh, out var cached) && ReferenceEquals(cached.Source, vertices)) return cached.HasUvs;
        if (_paintUvChecks.Count > 64) _paintUvChecks.Clear();
        bool has = ModelPaintPicker.HasUvs(vertices, mesh.Indices);
        _paintUvChecks[mesh] = (vertices, has);
        return has;
    }

    private MeshVertex[]? PaintVertices(int mesh) =>
        _hiddenGroups.Contains(mesh) || (uint)mesh >= (uint)Asset.Meshes.Count ? null : PickVertices(Asset.Meshes[mesh]);

    private bool TryPickPaint(Vector3 origin, Vector3 direction, out ModelPaintHit hit) =>
        ModelPaintPicker.TryPick(Asset, PaintVertices, origin + Asset.Pivot.Position, direction, out hit);

    private void BeginTexturePaint(Point point)
    {
        BeginTexturePaintStroke();
        Surface.NavigationEnabled = false; Surface.Host.Capture = true;
        var ray = Surface.PickRay(point);
        if (TryPickPaint(ray.Origin, ray.Direction, out ModelPaintHit hit) && hit.Mesh != _groups.SelectedIndex) SelectPart(hit.Mesh);
        PaintTextureAt(point);
        _lastPaintPointer = point;
    }

    private void BeginTexturePaintStroke()
    {
        if (_paintStrokeActive) return;
        _paintStrokeActive = true; _lastPaintDab = null; _lastPaintPointer = null;
    }

    private void MoveTexturePaint(Point point, MouseButtons buttons)
    {
        var ray = Surface.PickRay(point);
        _paintHover = TryPickPaint(ray.Origin, ray.Direction, out ModelPaintHit hover) ? hover : null;
        if (_paintStrokeActive && buttons == MouseButtons.Left)
        {
            // The pointer path, every two pixels, so a quick drag leaves a line rather than dots.
            Point from = _lastPaintPointer ?? point;
            int samples = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt((point.X - from.X) * (point.X - from.X) + (point.Y - from.Y) * (point.Y - from.Y)) / 2));
            for (int index = 1; index <= samples; index++)
                PaintTextureAt(new Point(from.X + (point.X - from.X) * index / samples, from.Y + (point.Y - from.Y) * index / samples));
            _lastPaintPointer = point;
        }
        Surface.Invalidate(true);
    }

    /// <summary>Paints (or erases) where the viewport point meets the model, starting a stroke if none is open.</summary>
    public bool PaintTextureAt(Point point)
    {
        var ray = Surface.PickRay(point);
        return PaintTextureRay(ray.Origin, ray.Direction);
    }

    /// <summary>
    /// Paints (or erases) where a ray in the viewport's space meets the model, into the Image of the
    /// material of the triangle it meets, making that Image first when the material has none.
    /// Starts a stroke if none is open; <see cref="FinishStroke"/> makes it one undo entry.
    /// </summary>
    public bool PaintTextureRay(Vector3 origin, Vector3 direction)
    {
        BeginTexturePaintStroke();
        if (!TryPickPaint(origin, direction, out ModelPaintHit hit)) { _lastPaintDab = null; return false; }
        GModelMesh mesh = Asset.Meshes[hit.Mesh];
        if (!HasPaintableUvs(mesh)) { _lastPaintDab = null; ShowPaintMessage(NoUvMessage(mesh)); return false; }
        if ((uint)hit.Material >= (uint)Asset.Materials.Count)
        {
            _lastPaintDab = null;
            ShowPaintMessage($"“{mesh.Name}” has no material to paint. Choose or make one under Material & PBR first.");
            return false;
        }
        ModelPaintTexture? texture = OpenPaintTexture(hit.Material, create: true);
        if (texture is null) { _lastPaintDab = null; return false; }
        if (!_paintStroke.TryGetValue(texture, out PixelStrokeRecorder? recorder))
            _paintStroke[texture] = recorder = new PixelStrokeRecorder(texture.Workspace, texture.Layer);

        Vector2 uv = hit.Uv * RuntimeModelRenderSystem.MaterialUvScale(Asset.Materials[hit.Material]);
        List<Vector2> dabs = [];
        // Fill the gap since the last dab in texture space, so a stroke is solid at any zoom; but not
        // across a seam, where the next triangle's pixels lie somewhere else on the Image.
        if (_lastPaintDab is { } last && last.Mesh == hit.Mesh && last.Material == hit.Material)
        {
            Vector2 step = (uv - last.Uv) * new Vector2(texture.Width, texture.Height);
            float distance = step.Length(), spacing = Math.Max(1f, _paintBrush.Size * .25f);
            if (distance > spacing && (last.Triangle == hit.Triangle || distance <= Math.Max(_paintBrush.Size * 3f, 24f)))
            {
                int count = (int)MathF.Ceiling(distance / spacing);
                for (int index = 1; index < count; index++) dabs.Add(Vector2.Lerp(last.Uv, uv, index / (float)count));
            }
        }
        dabs.Add(uv);
        foreach (Vector2 dab in dabs)
        {
            Point pixel = ModelPaintPicker.Pixel(dab, texture.Width, texture.Height);
            recorder.Capture(ModelPaintBrush.Bounds(pixel.X, pixel.Y, _paintBrush));
            if (_paintErase) ModelPaintBrush.Restore(texture.Layer.Pixels, texture.Original, texture.Width, texture.Height, pixel.X, pixel.Y, _paintBrush);
            else ModelPaintBrush.Paint(texture.Layer.Pixels, texture.Width, texture.Height, pixel.X, pixel.Y, _paintBrushColour, _paintBrush);
        }
        texture.Workspace.Touch();
        _lastPaintDab = (hit.Mesh, hit.Triangle, hit.Material, uv);
        Surface.Invalidate(true);
        return true;
    }

    /// <summary>Esc during a stroke takes the stroke back and keeps the brush.</summary>
    private bool CancelTexturePaintStroke()
    {
        if (!_paintStrokeActive) return false;
        FinishTexturePaintStroke(cancel: true);
        return true;
    }

    /// <summary>Closes the open stroke as one undo entry over every Image it touched; cancelling puts the pixels back.</summary>
    private void FinishTexturePaintStroke(bool cancel = false)
    {
        if (!_paintStrokeActive) return;
        _paintStrokeActive = false; _lastPaintDab = null; _lastPaintPointer = null;
        Surface.NavigationEnabled = true;
        if (Surface.Host.Capture) Surface.Host.Capture = false;
        string label = _paintErase ? "Erase on model" : "Paint on model";
        (ModelPaintTexture Texture, Genesis.Application.Core.Images.IImageDocumentCommand Command)[] commands =
            [.. _paintStroke.Select(pair => (pair.Key, pair.Value.Complete(label))).Where(pair => pair.Item2.EstimatedByteSize > 0)];
        _paintStroke.Clear();
        if (commands.Length == 0) return;
        if (cancel)
        {
            foreach ((ModelPaintTexture texture, var command) in commands) command.Undo(texture.Session);
            Surface.Invalidate(true);
            return;
        }
        PushEdit(label,
            () => { foreach ((ModelPaintTexture texture, var command) in commands) command.Execute(texture.Session); Surface.Invalidate(true); },
            () => { for (int index = commands.Length - 1; index >= 0; index--) commands[index].Command.Undo(commands[index].Texture.Session); Surface.Invalidate(true); });
    }

    /// <summary>
    /// The Image a material's strokes go into: its own Image resource, or a new one when it has none
    /// (or only a plain picture file, which the new Image starts from).
    /// </summary>
    private ModelPaintTexture? OpenPaintTexture(int materialIndex, bool create)
    {
        GModelMaterial material = Asset.Materials[materialIndex];
        string reference = material.AlbedoTexture ?? "";
        if (_paintByReference.TryGetValue(reference, out ModelPaintTexture? known)) return known;
        string? descriptor = reference.Length == 0 ? null : SpriteAssetLoader.ResolveDescriptorPath(ProjectRoot, reference);
        if (!string.IsNullOrWhiteSpace(descriptor) && SpriteAssetLoader.IsSpriteDescriptorPath(descriptor) && File.Exists(descriptor))
        {
            string full = Path.GetFullPath(descriptor);
            if (!_paintTextures.TryGetValue(full, out ModelPaintTexture? opened))
            {
                try { opened = ModelPaintTexture.Open(full, out string problem); if (opened is null) { ShowPaintMessage(problem); return null; } }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                    or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
                { ShowPaintMessage("The Image could not be opened for painting: " + exception.Message); return null; }
                _paintTextures[full] = opened;
            }
            _paintByReference[reference] = opened;
            return opened;
        }
        return create ? CreatePaintTexture(materialIndex) : null;
    }

    /// <summary>
    /// Makes an Image resource for a material and gives it to the material, as one undoable change.
    /// A material with no picture gets its own colour baked in (its colour, and the part's vertex
    /// colour when every vertex shares one), with those made white, so it looks the same before the
    /// first stroke and a stroke shows the colour picked. A plain picture file is copied in as it is.
    /// </summary>
    private ModelPaintTexture? CreatePaintTexture(int materialIndex)
    {
        GModelMaterial material = Asset.Materials[materialIndex];
        int width = _paintNewTextureSize, height = _paintNewTextureSize;
        byte[]? pixels = null;
        string? picture = string.IsNullOrWhiteSpace(material.AlbedoTexture) ? null : ProjectAssetIndex.ResolveSpriteImage(ProjectRoot, material.AlbedoTexture);
        if (!string.IsNullOrWhiteSpace(picture) && File.Exists(picture)
            && Genesis.Shared.Assets.ImageAssetDecoder.TryDecodeFrames(picture, out var decoded, out _) && decoded.Frames.Count > 0)
        {
            if (decoded.Width > MaximumPaintTextureSize || decoded.Height > MaximumPaintTextureSize)
            {
                ShowPaintMessage($"{Path.GetFileName(picture)} is {decoded.Width} × {decoded.Height}; painting takes pictures up to {MaximumPaintTextureSize} pixels each way.");
                return null;
            }
            (width, height, pixels) = (decoded.Width, decoded.Height, decoded.Frames[0].Rgba);
        }
        bool bake = pixels is null, exclusive = false;
        Vector4? vertexColour = bake ? SharedVertexColour(materialIndex, out exclusive) : null;
        bool whiten = bake && exclusive && vertexColour is not null;
        Vector3 colour = new Vector3(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z)
            * (whiten && vertexColour is { } shared ? new Vector3(shared.X, shared.Y, shared.Z) : Vector3.One);
        Color fill = Color.FromArgb(255, Channel(colour.X), Channel(colour.Y), Channel(colour.Z));

        ModelPaintTexture texture;
        try
        {
            ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
            try { texture = ModelPaintTexture.Create(resources, PaintTextureName(material), width, height, fill, pixels); }
            catch (ArgumentException) { texture = ModelPaintTexture.Create(resources, "Model paint", width, height, fill, pixels); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or InvalidDataException)
        {
            ShowPaintMessage("An Image to paint could not be made: " + exception.Message);
            return null;
        }
        string reference = Path.GetRelativePath(ProjectRoot, texture.ImagePath).Replace('\\', '/');
        _paintTextures[Path.GetFullPath(texture.ImagePath)] = texture;
        _paintByReference[reference] = texture;
        ChangeAsset("Make paint Image", () =>
        {
            GModelMaterial target = Asset.Materials[materialIndex];
            target.AlbedoTexture = reference;
            if (bake) target.BaseColor = new Vector4(1f, 1f, 1f, target.BaseColor.W);
            if (whiten) WhitenMaterialVertices(materialIndex);
        });
        RefreshMaterialInspector();
        ShowPaintMessage($"Made the Image {ResourceDisplayName.Format(texture.ImagePath)} ({width} × {height}) for {material.Name}. Save keeps it and the painting.");
        return texture;

        static int Channel(float value) => (int)MathF.Round(Math.Clamp(float.IsFinite(value) ? value : 1f, 0f, 1f) * 255f);
    }

    private string PaintTextureName(GModelMaterial material)
    {
        string text = $"{ResourceDisplayName.Format(ResourcePath)} {material.Name} paint";
        string clean = string.Join(' ', new string(text.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 60) clean = clean[..60].Trim();
        return clean.Length == 0 ? "Model paint" : clean;
    }

    /// <summary>
    /// The vertex colour every triangle of a material shares, or null when they differ; and whether
    /// those vertices are the material's alone (so making them white leaves other materials as they are).
    /// </summary>
    private Vector4? SharedVertexColour(int material, out bool exclusive)
    {
        exclusive = true;
        Vector4? shared = null;
        foreach (GModelMesh mesh in Asset.Meshes)
        {
            MeshVertex[] vertices = PickVertices(mesh);
            HashSet<int>? ours = null, theirs = null;
            for (int triangle = 0; triangle < mesh.Indices.Length / 3; triangle++)
            {
                bool mine = ModelPaintPicker.MaterialOf(mesh, triangle) == material;
                for (int corner = 0; corner < 3; corner++)
                {
                    int vertex = mesh.Indices[triangle * 3 + corner];
                    if (vertex >= vertices.Length) continue;
                    if (!mine) { (theirs ??= []).Add(vertex); continue; }
                    (ours ??= []).Add(vertex);
                    Vector4 colour = vertices[vertex].Color;
                    if (shared is not { } first) shared = colour;
                    else if (MathF.Max(MathF.Max(MathF.Abs(first.X - colour.X), MathF.Abs(first.Y - colour.Y)), MathF.Abs(first.Z - colour.Z)) > 1.5f / 255f)
                        return null;
                }
            }
            if (ours is not null && theirs is not null && ours.Overlaps(theirs)) exclusive = false;
        }
        return shared;
    }

    private void WhitenMaterialVertices(int material)
    {
        foreach (GModelMesh mesh in Asset.Meshes)
        {
            MeshVertex[] vertices = Vertices(mesh);
            bool changed = false;
            for (int triangle = 0; triangle < mesh.Indices.Length / 3; triangle++)
            {
                if (ModelPaintPicker.MaterialOf(mesh, triangle) != material) continue;
                for (int corner = 0; corner < 3; corner++)
                {
                    int vertex = mesh.Indices[triangle * 3 + corner];
                    if (vertex >= vertices.Length) continue;
                    Vector4 colour = vertices[vertex].Color;
                    if (colour.X == 1f && colour.Y == 1f && colour.Z == 1f) continue;
                    vertices[vertex].Color = new Vector4(1f, 1f, 1f, colour.W);
                    changed = true;
                }
            }
            if (changed) SetVertices(mesh, vertices);
        }
    }

    /// <summary>The preview's live picture for a material being painted (<see cref="RuntimeModelRenderSystem.MaterialAlbedoOverride"/>).</summary>
    private TextureHandle LivePaintTexture(IRenderController renderer, int material) =>
        _paintByReference.Count > 0 && (uint)material < (uint)Asset.Materials.Count
        && _paintByReference.TryGetValue(Asset.Materials[material].AlbedoTexture ?? "", out ModelPaintTexture? texture)
            ? texture.Texture(renderer)
            : TextureHandle.Invalid;

    /// <summary>Writes every painted Image that changed. Called by Save before the model itself.</summary>
    private void SavePaintTextures()
    {
        foreach (ModelPaintTexture texture in _paintTextures.Values) texture.Save();
    }

    private void ReleasePaintTextures()
    {
        foreach (ModelPaintTexture texture in _paintTextures.Values) texture.Release();
    }

    /// <summary>The brush outline on the surface, sized to the pixels a dab covers there.</summary>
    private void DrawTexturePaintCursor(IRenderController renderer)
    {
        if (_mode != ModelEditorMode.TexturePaint || _paintHover is not { } hit || (uint)hit.Mesh >= (uint)Asset.Meshes.Count) return;
        GModelMesh mesh = Asset.Meshes[hit.Mesh];
        MeshVertex[]? vertices = PaintVertices(hit.Mesh);
        int first = hit.Triangle * 3;
        if (vertices is null || first + 2 >= mesh.Indices.Length
            || mesh.Indices[first] >= vertices.Length || mesh.Indices[first + 1] >= vertices.Length || mesh.Indices[first + 2] >= vertices.Length) return;
        Vector3 center = Surface.WorldToSurface(hit.Position - Asset.Pivot.Position);
        if (center.Z < 0 || center.Z > 1) return;
        // Screen pixels for one texture pixel on this triangle: the ratio of its two areas.
        int width = _paintNewTextureSize, height = _paintNewTextureSize;
        float uvScale = 1f;
        if ((uint)hit.Material < (uint)Asset.Materials.Count)
        {
            uvScale = RuntimeModelRenderSystem.MaterialUvScale(Asset.Materials[hit.Material]);
            if (PaintTextureForMaterial(hit.Material) is { } texture) (width, height) = (texture.Width, texture.Height);
        }
        Vector2 Screen(int corner) { Vector3 p = Surface.WorldToSurface(vertices[mesh.Indices[first + corner]].Position - Asset.Pivot.Position); return new(p.X, p.Y); }
        Vector2 Texel(int corner) => vertices[mesh.Indices[first + corner]].UV * uvScale * new Vector2(width, height);
        float screenArea = Area(Screen(0), Screen(1), Screen(2)), texelArea = Area(Texel(0), Texel(1), Texel(2));
        float perTexel = texelArea > 1e-6f && float.IsFinite(screenArea) ? MathF.Sqrt(screenArea / texelArea) : 1f;
        float radius = Math.Clamp(_paintBrush.Size * .5f * perTexel, 2f, Surface.SurfaceWidth);
        RenderColor ring = _paintErase ? new RenderColor(1f, 1f, 1f) : new RenderColor(_paintBrushColour.R / 255f, _paintBrushColour.G / 255f, _paintBrushColour.B / 255f);
        EditorTransformGizmo.DrawCircleOutline(renderer, new(center.X, center.Y), radius, RenderColor.Black, 3);
        EditorTransformGizmo.DrawCircleOutline(renderer, new(center.X, center.Y), radius, ring, 1);

        static float Area(Vector2 a, Vector2 b, Vector2 c) => MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) * .5f;
    }
}
