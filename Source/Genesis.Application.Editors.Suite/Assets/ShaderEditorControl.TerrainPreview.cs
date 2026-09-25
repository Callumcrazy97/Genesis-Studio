using System.Numerics;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Shared.Interfaces;
using Genesis.World;
using Genesis.World.Foliage;
using Genesis.World.Terrain;
using Genesis.World.Water;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Geometry submitted by the current terrain preview, including material isolation.</summary>
public readonly record struct ShaderTerrainPreviewSubmission(
    string Component, bool ReceivesShader, MeshDrawFlags Flags, int Vertices, int Triangles,
    int Instances, Vector3 Minimum, Vector3 Maximum);

public sealed partial class ShaderEditorControl
{
    private sealed class TerrainPreviewBatch
    {
        public string Component = string.Empty;
        public MeshHandle Mesh;
        public MeshDrawFlags Flags;
        public int Vertices;
        public int Triangles;
        public Vector3 Minimum;
        public Vector3 Maximum;
        public Vector3 FrameMinimum;
        public MeshInstanceData[]? Instances;
    }

    private readonly List<TerrainPreviewBatch> _terrainPreviewBatches = [];
    private readonly List<ShaderTerrainPreviewSubmission> _terrainPreviewSubmissions = [];
    private string _terrainPreviewMessage = string.Empty;
    private Vector3 _terrainFrameMinimum;
    private Vector3 _terrainFrameMaximum;
    private int _terrainFrameWidth;
    private int _terrainFrameHeight;

    public IReadOnlyList<ShaderTerrainPreviewSubmission> TerrainPreviewSubmissions => _terrainPreviewSubmissions;
    public string TerrainPreviewMessage => _terrainPreviewMessage;

    private bool DrawTerrainPreview(IRenderController renderer)
    {
        _terrainPreviewSubmissions.Clear();
        string component = TerrainShaderTargetCatalog.Normalize(_document.TargetComponent);
        string? resource = ResolveTargetResourcePath();
        if (component == TerrainShaderTargetCatalog.None || string.IsNullOrWhiteSpace(resource)) return false;

        if (_loadedTerrainResource != resource || _loadedTerrainComponent != component)
        {
            ReleaseTerrainPreview(renderer);
            _loadedTerrainResource = resource;
            _loadedTerrainComponent = component;
            try
            {
                BuildTerrainPreview(renderer, resource, component);
                _terrainFrameWidth = _terrainFrameHeight = 0;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                or System.Text.Json.JsonException or Newtonsoft.Json.JsonException or InvalidOperationException
                or FormatException or OverflowException)
            {
                ReleaseTerrainPreview(renderer);
                _terrainPreviewMessage = "Terrain preview unavailable: " + exception.Message;
            }
        }

        if (_terrainPreviewMessage.Length > 0) _statusLabel.Text = _terrainPreviewMessage;
        if (_terrainPreviewBatches.Count == 0) return false;
        if (_terrainFrameWidth != renderer.PixelWidth || _terrainFrameHeight != renderer.PixelHeight)
            FrameTerrainPreview(renderer.PixelWidth, renderer.PixelHeight);

        AuthoredShaderTextures textures = BuildPreviewAuthoredTextures(renderer);
        foreach (TerrainPreviewBatch batch in _terrainPreviewBatches)
        {
            bool receivesShader = (batch.Flags & MeshDrawFlags.EditorReference) == 0;
            MeshDrawCall draw = new()
            {
                Mesh = batch.Mesh, World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = 1f,
                Flags = batch.Flags, AuthoredTextures = receivesShader ? textures : default,
            };
            if (batch.Instances is { Length: > 0 } instances)
                renderer.DrawMeshInstances(draw, instances);
            else
                renderer.DrawMesh(draw);
            _terrainPreviewSubmissions.Add(new ShaderTerrainPreviewSubmission(batch.Component,
                receivesShader, batch.Flags, batch.Vertices, batch.Triangles,
                batch.Instances?.Length ?? 1, batch.Minimum, batch.Maximum));
        }
        return true;
    }

    private void BuildTerrainPreview(IRenderController renderer, string resource, string component)
    {
        ValidateTerrainPreviewBinary(resource + ".gterrain");
        TerrainAsset terrain = LoadPreviewTerrain(resource);
        if (!float.IsFinite(terrain.CellSize) || terrain.CellSize <= 0
            || !float.IsFinite(terrain.OriginX) || !float.IsFinite(terrain.OriginZ)
            || !float.IsFinite(terrain.MinHeight) || !float.IsFinite(terrain.MaxHeight) || terrain.MinHeight >= terrain.MaxHeight)
            throw new InvalidDataException("Terrain dimensions or height range are invalid.");
        TerrainNatureDocument nature = TerrainNatureSerializer.LoadOrDefault(resource);
        bool all = component == TerrainShaderTargetCatalog.All;
        bool groundSelected = all || component.StartsWith("layer:", StringComparison.OrdinalIgnoreCase);
        MeshData ground = BuildTerrainPreviewGround(terrain, ReadTerrainLayerColours(resource, component));
        AddTerrainPreviewMesh(renderer, "terrain:surface", ground, groundSelected);

        foreach (TerrainPathDefinition path in nature.Paths)
        {
            string id = "path:" + path.Id;
            AddTerrainPreviewMesh(renderer, id,
                TerrainPathGeometry.BuildRibbon(path, terrain.SampleHeight, Math.Max(terrain.CellSize, .5f)),
                all || string.Equals(component, id, StringComparison.OrdinalIgnoreCase));
        }
        foreach (TerrainWaterDefinition water in nature.WaterBodies)
        {
            string id = "water:" + water.Id;
            MeshData mesh = WaterSurfaceMesh.BuildVisual(water.ToWaterBody(), water.Center);
            // Use the ordinary mesh pipeline so the edited shader owns the selected surface.
            // The authored footprint, fill hull and waterfall sheet come from the runtime builder.
            foreach (ref MeshVertex vertex in mesh.Vertices.AsSpan())
                vertex.Color = new Vector4(.22f, .52f, .70f, 1f);
            // A standing-water mesh includes its below-ground fill volume. Fit the visible
            // surface and exposed sides, rather than centering the camera inside that volume.
            float? visibleFloor = water.Kind != TerrainWaterKind.Waterfall && mesh.Vertices is { Length: > 0 }
                ? mesh.Vertices.Min(vertex => terrain.SampleHeight(vertex.Position.X, vertex.Position.Z)) : null;
            AddTerrainPreviewMesh(renderer, id, mesh,
                all || string.Equals(component, id, StringComparison.OrdinalIgnoreCase), MeshDrawFlags.NoCull,
                frameFloorHeight: visibleFloor);
        }

        string? foliagePath = TerrainNatureSerializer.ResolveFoliageCache(resource, nature);
        if (foliagePath is not null && File.Exists(foliagePath))
        {
            FoliageField field = FoliageFieldCache.Load(foliagePath, nature.FoliageCacheSha256);
            foreach (IGrouping<FoliageSpecies, FoliageInstance> group in field.Instances.GroupBy(instance => instance.Species))
            {
                MeshData geometry = FoliageGeometry.Build(group.Key, nearLod: true);
                MeshInstanceData[] instances = group.Select(instance => new MeshInstanceData(
                    Matrix4x4.CreateScale(instance.Scale) * Matrix4x4.CreateRotationY(instance.Rotation)
                        * Matrix4x4.CreateTranslation(instance.Position),
                    new RenderColor(1f + instance.HueVariation * .06f,
                        1f - MathF.Abs(instance.HueVariation) * .03f, 1f - instance.HueVariation * .08f, 1f))).ToArray();
                AddTerrainPreviewMesh(renderer, TerrainShaderTargetCatalog.Vegetation, geometry,
                    all || component == TerrainShaderTargetCatalog.Vegetation,
                    MeshDrawFlags.Foliage | MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow, instances);
            }
        }
        else if (component == TerrainShaderTargetCatalog.Vegetation)
            _terrainPreviewMessage = "This terrain has no saved vegetation geometry. Save its vegetation before previewing a shader.";

        TerrainPointOfInterest? point = component.StartsWith("point:", StringComparison.OrdinalIgnoreCase)
            ? nature.PointsOfInterest.FirstOrDefault(value => value.Id.Equals(component[6..], StringComparison.OrdinalIgnoreCase)) : null;
        if (point is not null)
        {
            AddTerrainPreviewMesh(renderer, component, BuildPointGuide(point), false, MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow);
            _terrainPreviewMessage = $"{point.Name} is a point of interest, with no renderable surface. Its position and discovery radius are shown as guides.";
        }

        TerrainPreviewBatch[] selected = _terrainPreviewBatches.Where(batch =>
            (batch.Flags & MeshDrawFlags.EditorReference) == 0 || batch.Component == component).ToArray();
        if (selected.Length == 0)
        {
            selected = _terrainPreviewBatches.Take(1).ToArray();
            if (_terrainPreviewMessage.Length == 0)
                _terrainPreviewMessage = "The selected component has no saved surface geometry to shade.";
        }
        _terrainFrameMinimum = selected.Aggregate(new Vector3(float.MaxValue), (value, batch) => Vector3.Min(value, batch.FrameMinimum));
        _terrainFrameMaximum = selected.Aggregate(new Vector3(float.MinValue), (value, batch) => Vector3.Max(value, batch.Maximum));
    }

    private void AddTerrainPreviewMesh(IRenderController renderer, string component, MeshData geometry,
        bool selected, MeshDrawFlags flags = MeshDrawFlags.None, MeshInstanceData[]? instances = null,
        float? frameFloorHeight = null)
    {
        if (geometry.Vertices is not { Length: > 0 } || geometry.Indices is not { Length: > 0 }) return;
        if (instances is { Length: 0 }) return;
        Vector3 localMin = new(float.MaxValue), localMax = new(float.MinValue);
        foreach (MeshVertex vertex in geometry.Vertices)
        {
            localMin = Vector3.Min(localMin, vertex.Position);
            localMax = Vector3.Max(localMax, vertex.Position);
        }
        Vector3 min = localMin, max = localMax;
        if (instances is not null)
        {
            min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
            foreach (MeshInstanceData instance in instances)
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 position = Vector3.Transform(new Vector3(
                    (corner & 1) == 0 ? localMin.X : localMax.X,
                    (corner & 2) == 0 ? localMin.Y : localMax.Y,
                    (corner & 4) == 0 ? localMin.Z : localMax.Z), instance.World);
                min = Vector3.Min(min, position); max = Vector3.Max(max, position);
            }
        }
        _terrainPreviewBatches.Add(new TerrainPreviewBatch
        {
            Component = component, Mesh = renderer.RegisterMesh(geometry.Vertices, geometry.Indices),
            Flags = flags | (selected ? MeshDrawFlags.None : MeshDrawFlags.EditorReference),
            Vertices = geometry.Vertices.Length, Triangles = geometry.Indices.Length / 3,
            Minimum = min, Maximum = max, Instances = instances,
            FrameMinimum = frameFloorHeight.HasValue
                ? new Vector3(min.X, Math.Clamp(frameFloorHeight.Value, min.Y, max.Y), min.Z) : min,
        });
    }

    private static MeshData BuildTerrainPreviewGround(TerrainAsset terrain, Vector4[] colours)
    {
        // A bounded preview LOD retains the authored extents/heights and splat colours without
        // allocating a full high-resolution mesh just to inspect a shader.
        int width = Math.Min(129, terrain.ResolutionX), depth = Math.Min(129, terrain.ResolutionZ);
        TerrainAsset sampled = new(width, depth, 1f, 0f, 0f, terrain.MinHeight, terrain.MaxHeight);
        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
        {
            int sx = x * (terrain.ResolutionX - 1) / (width - 1), sz = z * (terrain.ResolutionZ - 1) / (depth - 1);
            sampled.SetHeight(x, z, terrain.GetHeight(sx, sz));
            (byte r, byte g, byte b, byte a) = terrain.GetSplat(sx, sz);
            sampled.SetSplat(x, z, r, g, b, a);
        }
        MeshVertex[] vertices = new MeshVertex[width * depth];
        TerrainMeshBuilder.BuildVertices(sampled, colours, vertices);
        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
        {
            int sx = x * (terrain.ResolutionX - 1) / (width - 1), sz = z * (terrain.ResolutionZ - 1) / (depth - 1);
            ref MeshVertex vertex = ref vertices[z * width + x];
            vertex.Position.X = terrain.OriginX + sx * terrain.CellSize;
            vertex.Position.Z = terrain.OriginZ + sz * terrain.CellSize;
            vertex.Normal = Vector3.Normalize(new Vector3(
                terrain.GetHeight(Math.Max(0, sx - 1), sz) - terrain.GetHeight(Math.Min(terrain.ResolutionX - 1, sx + 1), sz),
                2f * terrain.CellSize,
                terrain.GetHeight(sx, Math.Max(0, sz - 1)) - terrain.GetHeight(sx, Math.Min(terrain.ResolutionZ - 1, sz + 1))));
        }
        return new MeshData { Vertices = vertices, Indices = TerrainMeshBuilder.BuildIndices(width, depth) };
    }

    private static void ValidateTerrainPreviewBinary(string path)
    {
        if (!File.Exists(path)) return;
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        if (stream.Length < 36 || reader.ReadInt32() != 0x4E525447 || reader.ReadInt32() != 1)
            throw new InvalidDataException("The terrain data header is invalid or unsupported.");
        int width = reader.ReadInt32(), depth = reader.ReadInt32();
        // Check before TerrainAsset.Load allocates from the header. The shader preview is
        // bounded to 4097 samples per axis; malformed/truncated files never reach allocation.
        if (width < 2 || depth < 2 || width > 4097 || depth > 4097)
            throw new InvalidDataException("Terrain shader previews support 2–4097 samples per axis.");
        long expected = 36L + (long)width * depth * 6L;
        if (stream.Length != expected)
            throw new InvalidDataException("Terrain data dimensions do not match its height and material samples.");
    }

    private static MeshData BuildPointGuide(TerrainPointOfInterest point)
    {
        const int segments = 64;
        float radius = Math.Max(.1f, point.DiscoveryRadius), thickness = Math.Max(.015f, radius * .008f);
        MeshVertex[] vertices = new MeshVertex[segments * 2 + 6];
        List<ushort> indices = [];
        for (int i = 0; i < segments; i++)
        {
            float angle = i * MathF.Tau / segments;
            Vector3 radial = new(MathF.Cos(angle), 0f, MathF.Sin(angle));
            for (int side = 0; side < 2; side++)
                vertices[i * 2 + side] = new MeshVertex { Position = point.Position + radial * (radius + (side == 0 ? -thickness : thickness)),
                    Normal = Vector3.UnitY, Color = new Vector4(1f, .76f, .25f, 1f) };
            ushort a = (ushort)(i * 2), b = (ushort)(((i + 1) % segments) * 2);
            indices.AddRange([a, b, (ushort)(a + 1), (ushort)(a + 1), b, (ushort)(b + 1)]);
        }
        float marker = Math.Max(.15f, radius * .05f);
        Vector3[] axis = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        for (int i = 0; i < 6; i++) vertices[segments * 2 + i] = new MeshVertex
            { Position = point.Position + axis[i] * marker, Normal = axis[i], Color = new Vector4(1f, .76f, .25f, 1f) };
        foreach (int i in new[] { 2, 0, 4, 2, 4, 1, 2, 1, 5, 2, 5, 0, 3, 4, 0, 3, 1, 4, 3, 5, 1, 3, 0, 5 })
            indices.Add((ushort)(segments * 2 + i));
        return new MeshData { Vertices = vertices, Indices = indices.ToArray() };
    }

    private void FrameTerrainPreview(int width, int height)
    {
        _terrainFrameWidth = width; _terrainFrameHeight = height;
        Vector3 center = (_terrainFrameMinimum + _terrainFrameMaximum) * .5f;
        float radius = Math.Max(.5f, (_terrainFrameMaximum - _terrainFrameMinimum).Length() * .5f);
        float vertical = _viewport.FieldOfViewDegrees * MathF.PI / 360f;
        float horizontal = MathF.Atan(MathF.Tan(vertical) * Math.Max(.1f, width / (float)Math.Max(1, height)));
        _viewport.Camera.Target = center;
        _viewport.Camera.Distance = radius / MathF.Sin(Math.Min(vertical, horizontal)) * 1.1f;
        _viewport.FarPlane = Math.Max(900f, _viewport.Camera.Distance + radius * 2f);
        _viewport.FloorHeight = 0;
    }

    private void ReleaseTerrainPreview(IRenderController renderer)
    {
        foreach (TerrainPreviewBatch batch in _terrainPreviewBatches)
            if (batch.Mesh.IsValid) renderer.ReleaseMesh(batch.Mesh);
        _terrainPreviewBatches.Clear();
        _terrainPreviewSubmissions.Clear();
        _terrainPreviewMessage = string.Empty;
    }
}
