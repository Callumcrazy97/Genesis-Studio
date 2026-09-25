using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private sealed record SurfacePixels(int Size, byte[] Color, byte[] Normal, byte[] Orm,
        List<TerrainLayerDocument> Layers, TerrainImageMaterial?[] Images, float WorldWidth, float WorldHeight);
    private sealed record SurfacePatch(SurfacePixels Surface, Rectangle Area, byte[] Color, byte[] Normal, byte[] Orm);
    private Rectangle _paintDirtyRegion;
    private Task<SurfacePatch>? _paintPreparation;
    private int _paintVersion, _materialPaintVersion, _paintUploadVersion;
    private readonly Dictionary<IRenderController, int> _surfaceUploadVersions = [];
    private readonly Dictionary<IRenderController, MeshDrawCall> _surfaceMaterials = [];
    private SurfacePixels? _surfacePixels;
    private Task<SurfacePixels?>? _materialPreparation;
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
        using var dialog = new DpiAwareForm { Text = _settings.Layers[index].Name + " · Material", ClientSize = new Size(600, 510),
            StartPosition = FormStartPosition.CenterParent, BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text };
        var column = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20) };
        var image = new Button { Text = "Choose Image…", Width = 540, Height = 36 };
        var name = new Label { Width = 540, Height = 32, Text = _settings.Layers[index].Image, AutoEllipsis = true };
        var status = new Label { Width = 540, Height = 48 };
        var generate = new Button { Text = "Generate missing PBR maps…", Width = 260, Height = 36 };
        var tiling = new NumericUpDown { Minimum = .1m, Maximum = 128, DecimalPlaces = 1, Value = (decimal)Math.Clamp(_settings.Layers[index].Tiling, .1f, 128), Width = 120 };
        var colour = new Button { Text = "Use colour instead of Image…", Width = 260, Height = 32 };
        var addressing = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        addressing.Items.AddRange(["Repeat", "Clamp", "Tile", "Stretch"]); addressing.SelectedItem = _settings.Layers[index].Addressing;
        var resolution = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
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
            name.Text = ResourceDisplayName.Format(reference);
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
        dialog.Controls.Add(column); Refresh(); dialog.ShowDialog(FindForm()); RebuildSelectionInspector();
    }

    private void RefreshMaterialTiles()
    {
        foreach (var (tile, index) in _referenceLayers)
        {
            if (index >= _settings.Layers.Count) continue;
            tile.Text = _settings.Layers[index].Name;
            string? raster = ResolveNamedImageRaster(_settings.Layers[index].Image);
            if (raster is null)
            {
                var bitmap = new Bitmap(96, 64); using var graphics = Graphics.FromImage(bitmap); var c = _settings.Layers[index].Color;
                graphics.Clear(Color.FromArgb((int)(c[0] * 255), (int)(c[1] * 255), (int)(c[2] * 255)));
                var old = tile.Image; tile.Image = bitmap; old?.Dispose(); tile.Invalidate(); continue;
            }
            try { using var image = new Bitmap(raster); var previous = tile.Image; tile.Image = new Bitmap(image, 96, 64); previous?.Dispose(); tile.Invalidate(); }
            catch (Exception exception) when (exception is IOException or ArgumentException) { }
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
        string signature = string.Join('|', layers.Select(layer => $"{layer.Image}:{layer.Tiling}:{layer.Addressing}:{layer.Resolution}:{string.Join(',', layer.Color)}:{File.GetLastWriteTimeUtc(ResourceNames.Resolve(ProjectRoot, layer.Image, ResourceType.Image)).Ticks}"))
            + $"|{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_terrain)}:{_terrain.ResolutionX}:{_terrain.ResolutionZ}:{_terrain.CellSize}";
        if (signature == _materialSignature) return;
        _pendingMaterialSignature = signature;
        byte[] splats = (byte[])_terrain.SplatmapData.Clone(); int width = _terrain.ResolutionX, height = _terrain.ResolutionZ;
        _materialPaintVersion = _paintVersion;
        MaterialPreviewStatus = "Preparing terrain material…";
        float worldWidth = (width - 1) * _terrain.CellSize, worldHeight = (height - 1) * _terrain.CellSize;
        _materialPreparation = Task.Run(() => BakeSurface(layers, splats, width, height, worldWidth, worldHeight));
    }

    private SurfacePixels? BakeSurface(List<TerrainLayerDocument> layers, byte[] splats, int width, int height, float worldWidth, float worldHeight)
    {
        var images = layers.Select(layer => string.IsNullOrWhiteSpace(layer.Image) ? null : ReduceMaterial(TerrainImageMaterial.Load(ProjectRoot, layer.Image), layer.Resolution)).ToArray();
        const int size = 2048;
        byte[] color = new byte[size * size * 4], normal = new byte[color.Length], orm = new byte[color.Length];
        var surface = new SurfacePixels(size, color, normal, orm, layers, images, worldWidth, worldHeight);
        BakeSurfaceArea(surface, new Rectangle(0, 0, size, size), splats, width, height, color, normal, orm);
        return surface;
    }

    private static void BakeSurfaceArea(SurfacePixels surface, Rectangle area, byte[] splats, int width, int height,
        byte[] color, byte[] normal, byte[] orm)
    {
        int size = surface.Size;
        var layers = surface.Layers; var images = surface.Images;
        float worldWidth = surface.WorldWidth, worldHeight = surface.WorldHeight;
        for (int y = area.Top; y < area.Bottom; y++)
        for (int x = area.Left; x < area.Right; x++)
        {
            float u = x / (float)(size - 1), v = y / (float)(size - 1);
            Vector4 weights = Sample(splats, width, height, u, v, false); float total = weights.X + weights.Y + weights.Z + weights.W;
            weights = total > .00001f ? weights / total : new Vector4(1, 0, 0, 0);
            Vector3 c = default, n = default, o = default;
            for (int layer = 0; layer < layers.Count; layer++)
            {
                float weight = weights[layer]; if (weight < .0001f) continue;
                var definition = layers[layer]; var image = images[layer];
                if (image is null)
                {
                    c += new Vector3(definition.Color[0], definition.Color[1], definition.Color[2]) * weight;
                    n += Vector3.UnitZ * weight; o += new Vector3(1, .72f, 0) * weight; continue;
                }
                float su = definition.Addressing == "Stretch" ? u : definition.Addressing == "Tile" ? u * worldWidth / definition.Tiling : u * definition.Tiling;
                float sv = definition.Addressing == "Stretch" ? v : definition.Addressing == "Tile" ? v * worldHeight / definition.Tiling : v * definition.Tiling;
                bool repeat = definition.Addressing is "Repeat" or "Tile";
                Vector4 a = Sample(image.Albedo, image.Width, image.Height, su, sv, repeat);
                Vector4 b = Sample(image.Normal, image.Width, image.Height, su, sv, repeat);
                Vector4 d = Sample(image.Orm, image.Width, image.Height, su, sv, repeat);
                c += new Vector3(a.X, a.Y, a.Z) * weight;
                n += (new Vector3(b.X, b.Y, b.Z) * 2 - Vector3.One) * weight;
                o += new Vector3(d.X, d.Y, d.Z) * weight;
            }
            int pixel = ((y - area.Top) * area.Width + x - area.Left) * 4;
            Write(color, pixel, c); Write(normal, pixel, (n.LengthSquared() > .00001f ? Vector3.Normalize(n) : Vector3.UnitZ) * .5f + new Vector3(.5f)); Write(orm, pixel, o);
        }
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
            BakeSurfaceArea(pixels, area, splats, width, height, patch.Color, patch.Normal, patch.Orm);
            return patch;
        });
    }

    private static TerrainImageMaterial ReduceMaterial(TerrainImageMaterial image, int resolution)
    {
        int width = Math.Min(image.Width, Math.Clamp(resolution, 256, 2048)), height = Math.Min(image.Height, Math.Clamp(resolution, 256, 2048));
        if (width == image.Width && height == image.Height) return image;
        byte[] Resize(byte[] source)
        {
            var result = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                var pixel = Sample(source, image.Width, image.Height, x / (float)(width - 1), y / (float)(height - 1), false);
                int at = (y * width + x) * 4; for (int c = 0; c < 4; c++) result[at + c] = (byte)(pixel[c] * 255);
            }
            return result;
        }
        return new(width, height, Resize(image.Albedo), Resize(image.Normal), Resize(image.Orm));
    }

    private static Vector4 Sample(byte[] pixels, int width, int height, float u, float v, bool repeat)
    {
        u = repeat ? u - MathF.Floor(u) : Math.Clamp(u, 0, 1); v = repeat ? v - MathF.Floor(v) : Math.Clamp(v, 0, 1);
        float x = u * (width - 1), y = v * (height - 1); int x0 = (int)x, y0 = (int)y;
        Vector4 At(int px, int py) { int i = (py * width + px) * 4; return new Vector4(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) / 255f; }
        return Vector4.Lerp(Vector4.Lerp(At(x0, y0), At(Math.Min(x0 + 1, width - 1), y0), x - x0),
            Vector4.Lerp(At(x0, Math.Min(y0 + 1, height - 1)), At(Math.Min(x0 + 1, width - 1), Math.Min(y0 + 1, height - 1)), x - x0), y - y0);
    }

    private static void Write(byte[] pixels, int i, Vector3 value)
    {
        pixels[i] = (byte)Math.Clamp(value.X * 255, 0, 255); pixels[i + 1] = (byte)Math.Clamp(value.Y * 255, 0, 255);
        pixels[i + 2] = (byte)Math.Clamp(value.Z * 255, 0, 255); pixels[i + 3] = 255;
    }

    private MeshDrawCall? EnsureSurfaceMaterial(IRenderController renderer)
    {
        if (_surfacePixels is not { } pixels) return null;
        if (_surfaceMaterials.TryGetValue(renderer, out var existing))
        {
            if (_surfaceUploadVersions.GetValueOrDefault(renderer, -1) != _paintUploadVersion)
            {
                renderer.UpdateTexture(existing.Texture, pixels.Size, pixels.Size, pixels.Color);
                renderer.UpdateTexture(existing.NormalMap, pixels.Size, pixels.Size, pixels.Normal);
                renderer.UpdateTexture(existing.OrmMap, pixels.Size, pixels.Size, pixels.Orm);
                _surfaceUploadVersions[renderer] = _paintUploadVersion;
            }
            return existing;
        }
        var material = new MeshDrawCall { Texture = renderer.CreateTexture(pixels.Size, pixels.Size, pixels.Color),
            NormalMap = renderer.CreateTexture(pixels.Size, pixels.Size, pixels.Normal), OrmMap = renderer.CreateTexture(pixels.Size, pixels.Size, pixels.Orm),
            World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = 1, SurfaceParams = new Vector4(1, 0, 0, 0), DetailParams = new Vector4(0, 0, 0, 1) };
        _surfaceMaterials[renderer] = material; _surfaceUploadVersions[renderer] = _paintUploadVersion; return material;
    }

    private void ReleaseSurfaceMaterials()
    {
        foreach (var (renderer, material) in _surfaceMaterials)
            foreach (var texture in new[] { material.Texture, material.NormalMap, material.OrmMap })
                if (texture.IsValid) renderer.ReleaseTexture(texture);
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
