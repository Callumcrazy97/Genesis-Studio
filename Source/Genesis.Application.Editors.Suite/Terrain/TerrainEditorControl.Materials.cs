using System.Numerics;
using Genesis.Runtime.Assets;
using SurfacePixels = Genesis.Runtime.Assets.TerrainSurfaceMaterialPixels;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private sealed record SurfacePatch(SurfacePixels Surface, Rectangle Area, byte[] Color, byte[] Normal, byte[] Orm);
    private Rectangle _paintDirtyRegion;
    private Task<SurfacePatch>? _paintPreparation;
    private int _paintVersion, _materialPaintVersion, _paintUploadVersion;
    private readonly Dictionary<IRenderController, int> _surfaceUploadVersions = [];
    private readonly Dictionary<IRenderController, MeshDrawCall> _surfaceMaterials = [];
    private SurfacePixels? _surfacePixels;
    private Task<SurfacePixels>? _materialPreparation;
    private DateTime _nextMaterialCheck;
    private string _materialSignature = "";
    private string _pendingMaterialSignature = "";
    public string MaterialPreviewStatus { get; private set; } = "No image material assigned";
    public int MaterialPreviewRevision { get; private set; }

    public async Task GenerateLayerPbrAsync(int index, Genesis.Application.Editors.Image.Imaging.PbrMaterialSettings settings)
    {
        if ((uint)index >= (uint)_settings.Layers.Count) throw new ArgumentOutOfRangeException(nameof(index));
        string reference = _settings.Layers[index].Image;
        await Task.Run(() => TerrainImageMaterial.GenerateMissing(ProjectRoot, reference, settings));
        if (IsDisposed) return;
        _nextMaterialCheck = DateTime.MinValue; _viewport.Invalidate();
    }

    public void AssignLayerImage(int index, string reference, float tiling = 8f)
    {
        if ((uint)index >= (uint)_settings.Layers.Count) return;
        var before = CloneLayers(_settings.Layers); var after = CloneLayers(before);
        after[index].Image = reference; after[index].Tiling = Math.Clamp(tiling, .1f, 128f);
        ApplyLayers(before, after, "Change terrain material");
        _nextMaterialCheck = DateTime.MinValue;
        RefreshMaterialTiles();
        _viewport.Invalidate();
    }

    public void OpenLayerMaterial(int index)
    {
        if ((uint)index >= (uint)_settings.Layers.Count) return;
        using DpiAwareForm dialog = CreateLayerMaterialDialog(index);
        dialog.ShowDialog(FindForm());
        RebuildSelectionInspector();
    }

    public DpiAwareForm CreateLayerMaterialDialog(int index)
    {
        if ((uint)index >= (uint)_settings.Layers.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var dialog = new DpiAwareForm { Text = _settings.Layers[index].Name + " · Material", ClientSize = new Size(680, 720),
            MinimumSize = new Size(540, 560), Tag = "font-measured-layout", FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterParent, BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text };
        var column = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20) };
        var image = new Button { Text = "Choose Image…", Width = 540, Height = 36 };
        var name = new Label { Width = 540, Height = 32, Text = _settings.Layers[index].Image, AutoEllipsis = true };
        var status = new Label { Width = 540, Height = 48 };
        var generate = new Button { Text = "Generate missing PBR maps…", Width = 260, Height = 36 };
        var tiling = new NumericUpDown { Minimum = .1m, Maximum = 128, DecimalPlaces = 1, Value = (decimal)Math.Clamp(_settings.Layers[index].Tiling, .1f, 128), Width = 120 };
        var colour = new Button { Text = "Use colour instead of Image…", Width = 260, Height = 32 };
        var addressing = new ThemedComboBox { Name = "TerrainLayerAddressing", Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        addressing.Items.AddRange(["Repeat", "Clamp", "Tile", "Stretch"]); addressing.SelectedItem = _settings.Layers[index].Addressing;
        var resolution = new ThemedComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
        resolution.Items.AddRange([256, 512, 1024, 2048]); resolution.SelectedItem = _settings.Layers[index].Resolution;
        void MappingChanged()
        {
            var before = CloneLayers(_settings.Layers); var after = CloneLayers(before);
            after[index].Addressing = addressing.SelectedItem?.ToString() ?? "Repeat";
            after[index].Resolution = resolution.SelectedItem is int size ? size : 2048;
            ApplyLayers(before, after, "Change terrain texture mapping"); _nextMaterialCheck = DateTime.MinValue;
        }
        addressing.SelectedIndexChanged += (_, _) => MappingChanged(); resolution.SelectedIndexChanged += (_, _) => MappingChanged();
        colour.Click += (_, _) =>
        {
            using var picker = new ColorDialog { FullOpen = true };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            var before = CloneLayers(_settings.Layers); var after = CloneLayers(before);
            after[index].Image = ""; after[index].Color = [picker.Color.R / 255f, picker.Color.G / 255f, picker.Color.B / 255f];
            ApplyLayers(before, after, "Change terrain layer colour"); _nextMaterialCheck = DateTime.MinValue; RefreshMaterialTiles(); Refresh();
        };
        foreach (var control in new Control[] { colour, addressing, resolution }) EditorChrome.StyleField(control);
        foreach (Control control in new Control[] { image, generate, tiling }) EditorChrome.StyleField(control);
        void Refresh()
        {
            string reference = _settings.Layers[index].Image;
            name.Text = string.IsNullOrWhiteSpace(reference) ? "Colour material · no Image assigned" : ResourceDisplayName.Format(reference);
            status.Text = TerrainImageMaterial.Describe(ProjectRoot, reference); generate.Enabled = !string.IsNullOrWhiteSpace(reference);
        }
        image.Click += (_, _) =>
        {
            var asset = AssetPickerService.PickAsset(new AssetPickerRequest(ProjectRoot, ResourceKind.Image, _settings.Layers[index].Image), dialog);
            if (asset is null) return;
            AssignLayerImage(index, asset.Reference, (float)tiling.Value); Refresh();
        };
        tiling.ValueChanged += (_, _) => AssignLayerImage(index, _settings.Layers[index].Image, (float)tiling.Value);
        generate.Click += async (_, _) =>
        {
            using var settings = new Genesis.Application.Editors.Image.Dialogs.PbrMaterialDialog();
            if (settings.ShowDialog(dialog) != DialogResult.OK) return;
            string reference = _settings.Layers[index].Image;
            generate.Enabled = false; status.Text = "Generating material channels…";
            try { await Task.Run(() => TerrainImageMaterial.GenerateMissing(ProjectRoot, reference, settings.Settings)); }
            catch (Exception exception) { if (!dialog.IsDisposed) status.Text = exception.Message; return; }
            finally { if (!dialog.IsDisposed) generate.Enabled = true; }
            _nextMaterialCheck = DateTime.MinValue; _viewport.Invalidate();
            if (!dialog.IsDisposed) Refresh();
        };
        column.Controls.AddRange([image, colour, name, status, generate,
            new Label { Text = "Addressing (Tile uses metres per tile)", AutoSize = true }, addressing,
            new Label { Text = "Repeat count / tile size", AutoSize = true }, tiling,
            new Label { Text = "Material map resolution", AutoSize = true }, resolution]);
        column.Controls.Add(new Label { Text = "Changes apply live. Ctrl+Z in Terrain undoes them.", AutoSize = true, ForeColor = EditorChrome.Muted });
        dialog.Controls.Add(column);
        Button done = new() { Text = "Done", AutoSize = true, DialogResult = DialogResult.OK };
        EditorChrome.StyleField(done);
        FlowLayoutPanel footer = new() { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        footer.Controls.Add(done); dialog.Controls.Add(footer); dialog.AcceptButton = dialog.CancelButton = done;
        void Fit()
        {
            if (dialog.IsDisposed) return;
            foreach (Control field in column.Controls)
            {
                int width = Math.Max(180, column.ClientSize.Width - column.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - field.Margin.Horizontal - 8);
                field.Width = width;
                if (field is Button button) button.Height = button.Font.Height + 18;
                if (field is ComboBox combo) combo.ItemHeight = combo.Font.Height + 8;
                if (field is Label label)
                {
                    label.AutoSize = true;
                    label.MinimumSize = label.MaximumSize = new Size(width, 0);
                }
            }
            done.MinimumSize = new Size(0, done.Font.Height + 18);
        }
        dialog.SizeChanged += (_, _) => Fit();
        dialog.FontChanged += (_, _) => Fit();
        dialog.Shown += (_, _) => { Fit(); dialog.BeginInvoke((Action)Fit); };
        Refresh();
        return dialog;
    }

    private void RefreshMaterialTiles()
    {
        if (_paintLayerGrid is null) return;
        foreach (Button tile in _paintLayerGrid.Controls.OfType<Button>())
        {
            int index = (int)tile.Tag!;
            tile.Text = _settings.Layers[index].Name;
            bool selected = index == SelectedLayer;
            tile.BackColor = selected ? EditorChrome.Hover : EditorChrome.Raised;
            tile.FlatAppearance.BorderColor = selected ? EditorChrome.Accent : EditorChrome.Border;
            tile.FlatAppearance.BorderSize = selected ? 2 : 1;
            tile.Invalidate();
        }
    }

    private void UpdateMaterialPreview()
    {
        if (_materialPreparation is { IsCompleted: true } completed)
        {
            _materialPreparation = null;
            if (completed.IsCompletedSuccessfully)
            {
                ReleaseSurfaceMaterials(); _surfacePixels = completed.Result;
                _materialSignature = _pendingMaterialSignature;
                if (_materialPaintVersion != _paintVersion) InvalidatePaint();
                _terrainGroundRevision.Clear(); _meshDirty = true;
                MaterialPreviewRevision++;
                MaterialPreviewStatus = _surfacePixels is null ? "No image material assigned" : "Live PBR · Albedo / Normal / ORM";
                RefreshMaterialTiles();
                RebuildSelectionInspector();
            }
            else { MaterialPreviewStatus = completed.Exception?.GetBaseException().Message ?? "Material preparation cancelled"; _materialSignature = _pendingMaterialSignature; }
        }
        UpdatePaintPreview();
        if (_materialPreparation is not null || DateTime.UtcNow < _nextMaterialCheck) return;
        _nextMaterialCheck = DateTime.UtcNow.AddSeconds(1);
        var layers = CloneLayers(_settings.Layers.Take(4));
        string signature = string.Join('|', layers.Select(layer => $"{layer.Image}:{layer.Tiling}:{layer.Addressing}:{layer.Resolution}:{string.Join(',', layer.Color)}:{MaterialSourceTimestamp(layer.Image)}"))
            + $"|{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_terrain)}:{_terrain.ResolutionX}:{_terrain.ResolutionZ}:{_terrain.CellSize}";
        if (signature == _materialSignature) return;
        _pendingMaterialSignature = signature;
        byte[] splats = (byte[])_terrain.SplatmapData.Clone(); int width = _terrain.ResolutionX, height = _terrain.ResolutionZ;
        _materialPaintVersion = _paintVersion;
        MaterialPreviewStatus = "Preparing terrain material…";
        float worldWidth = (width - 1) * _terrain.CellSize, worldHeight = (height - 1) * _terrain.CellSize;
        _materialPreparation = Task.Run(() => BakeSurface(layers, splats, width, height, worldWidth, worldHeight));
    }

    private long MaterialSourceTimestamp(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return 0;
        string path = ResourceNames.Resolve(ProjectRoot, image, ResourceType.Image);
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? 0 : File.GetLastWriteTimeUtc(path).Ticks;
    }

    private SurfacePixels BakeSurface(List<TerrainLayerDocument> layers, byte[] splats, int width, int height, float worldWidth, float worldHeight)
    {
        var authored = layers.Select(layer => new TerrainMaterialLayer { Name = layer.Name, Color = (float[])layer.Color.Clone(),
            Image = layer.Image, Tiling = layer.Tiling, Addressing = layer.Addressing, Resolution = layer.Resolution }).ToList();
        return TerrainSurfaceMaterialBaker.Bake(ProjectRoot, authored, splats, width, height, worldWidth, worldHeight);
    }
    // Brush input invalidates only the affected atlas area. Image loading and full material
    // preparation are independent; neither the inspector nor terrain meshes change for paint.
    private void InvalidatePaint(Rectangle? cells = null)
    {
        _paintVersion++;
        var area = cells ?? new Rectangle(0, 0, _terrain.ResolutionX, _terrain.ResolutionZ);
        _paintDirtyRegion = _paintDirtyRegion.IsEmpty ? area : Rectangle.Union(_paintDirtyRegion, area);
    }

    private void UpdatePaintPreview()
    {
        if (_paintPreparation is { IsCompleted: true } completed)
        {
            _paintPreparation = null;
            if (completed.IsCompletedSuccessfully && ReferenceEquals(completed.Result.Surface, _surfacePixels))
            {
                var patch = completed.Result; var surface = patch.Surface;
                void Copy(byte[] source, byte[] target)
                {
                    for (int row = 0; row < patch.Area.Height; row++)
                        Buffer.BlockCopy(source, row * patch.Area.Width * 4, target,
                            ((row + patch.Area.Y) * surface.Size + patch.Area.X) * 4, patch.Area.Width * 4);
                }
                Copy(patch.Color, surface.Color); Copy(patch.Normal, surface.Normal); Copy(patch.Orm, surface.Orm);
                _paintUploadVersion++;
                MaterialPreviewRevision++;
            }
            else if (!completed.IsCompletedSuccessfully)
                MaterialPreviewStatus = completed.Exception?.GetBaseException().Message ?? "Paint preview cancelled";
        }
        if (_paintPreparation is not null || _paintDirtyRegion.IsEmpty || _surfacePixels is not { } pixels) return;
        Rectangle cells = _paintDirtyRegion; _paintDirtyRegion = Rectangle.Empty;
        int width = _terrain.ResolutionX, height = _terrain.ResolutionZ, size = pixels.Size;
        // Include neighbouring cells because the splat field is bilinearly interpolated.
        var area = Rectangle.FromLTRB(Math.Max(0, (int)((cells.Left - 1f) / (width - 1) * (size - 1))),
            Math.Max(0, (int)((cells.Top - 1f) / (height - 1) * (size - 1))),
            Math.Min(size, (int)Math.Ceiling((cells.Right + 1f) / (width - 1) * (size - 1)) + 1),
            Math.Min(size, (int)Math.Ceiling((cells.Bottom + 1f) / (height - 1) * (size - 1)) + 1));
        if (area.Width <= 0 || area.Height <= 0) return;
        byte[] splats = (byte[])_terrain.SplatmapData.Clone();
        _paintPreparation = Task.Run(() =>
        {
            int length = area.Width * area.Height * 4;
            var patch = new SurfacePatch(pixels, area, new byte[length], new byte[length], new byte[length]);
            TerrainSurfaceMaterialBaker.BakeArea(pixels, area.X, area.Y, area.Width, area.Height, splats, width, height, patch.Color, patch.Normal, patch.Orm);
            return patch;
        });
    }

    private MeshDrawCall? EnsureSurfaceMaterial(IRenderController renderer)
    {
        if (_surfacePixels is not { } pixels) return null;
        if (_surfaceMaterials.TryGetValue(renderer, out var existing))
        {
            if (_surfaceUploadVersions.GetValueOrDefault(renderer, -1) != _paintUploadVersion)
            {
                TerrainSurfaceMaterialBinding.UpdatePaint(renderer, existing, pixels, _terrain.SplatmapData, _terrain.ResolutionX, _terrain.ResolutionZ);
                _surfaceUploadVersions[renderer] = _paintUploadVersion;
            }
            return existing;
        }
        var material = TerrainSurfaceMaterialBinding.Create(renderer, pixels, _terrain.SplatmapData, _terrain.ResolutionX, _terrain.ResolutionZ);
        _surfaceMaterials[renderer] = material; _surfaceUploadVersions[renderer] = _paintUploadVersion; return material;
    }

    private void ReleaseSurfaceMaterials()
    {
        foreach (var (renderer, material) in _surfaceMaterials)
            TerrainSurfaceMaterialBinding.Release(renderer, material);
        _surfaceMaterials.Clear();
        _surfaceUploadVersions.Clear();
    }

    private void RestoreTerrainBaseline(float worldX, float worldZ, float radius, float strength)
    {
        if (_eraseBaseline.Length != _terrain.HeightsData.Length) return;
        var bounds = BrushCellBounds(worldX, worldZ, radius);
        for (int z = bounds.MinZ; z <= bounds.MaxZ; z++)
        for (int x = bounds.MinX; x <= bounds.MaxX; x++)
        {
            float dx = _terrain.OriginX + x * _terrain.CellSize - worldX, dz = _terrain.OriginZ + z * _terrain.CellSize - worldZ;
            float distance = MathF.Sqrt(dx * dx + dz * dz); if (distance > radius) continue;
            int index = z * _terrain.ResolutionX + x;
            _terrain.HeightsData[index] = (ushort)Math.Clamp(_terrain.HeightsData[index] + (_eraseBaseline[index] - _terrain.HeightsData[index]) * strength * (1 - distance / radius), 0, ushort.MaxValue);
        }
    }

    private void FillTerrainDepression(float worldX, float worldZ, float radius, float strength, float rimHeight)
    {
        var bounds = BrushCellBounds(worldX, worldZ, radius);
        for (int z = bounds.MinZ; z <= bounds.MaxZ; z++)
        for (int x = bounds.MinX; x <= bounds.MaxX; x++)
        {
            float dx = _terrain.OriginX + x * _terrain.CellSize - worldX, dz = _terrain.OriginZ + z * _terrain.CellSize - worldZ;
            float distance = MathF.Sqrt(dx * dx + dz * dz); if (distance > radius) continue;
            float current = _terrain.GetHeight(x, z);
            if (current < rimHeight) _terrain.SetHeight(x, z, current + (rimHeight - current) * strength * (1 - distance / radius));
        }
    }

    private (int MinX, int MaxX, int MinZ, int MaxZ) BrushCellBounds(float x, float z, float radius) => (
        Math.Max(0, (int)MathF.Floor((x - radius - _terrain.OriginX) / _terrain.CellSize)),
        Math.Min(_terrain.ResolutionX - 1, (int)MathF.Ceiling((x + radius - _terrain.OriginX) / _terrain.CellSize)),
        Math.Max(0, (int)MathF.Floor((z - radius - _terrain.OriginZ) / _terrain.CellSize)),
        Math.Min(_terrain.ResolutionZ - 1, (int)MathF.Ceiling((z + radius - _terrain.OriginZ) / _terrain.CellSize)));
}
