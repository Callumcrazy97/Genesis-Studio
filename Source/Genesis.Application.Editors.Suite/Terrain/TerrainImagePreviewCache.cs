using System.Numerics;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>Frame-specific Image materials and geometry shared by the Terrain canvas and wizards.</summary>
internal sealed class TerrainImagePreviewCache : IDisposable
{
    private sealed class Entry
    {
        public DateTime NextCheck, Stamp;
        public Task<(DateTime Stamp, TerrainImageMaterial Pixels)>? Pending;
        public TerrainImageMaterial? Pixels;
        public readonly Dictionary<IRenderController, MeshDrawCall> Materials = [];
        public readonly Dictionary<(IRenderController Renderer, bool Extruded), MeshHandle> Geometry = [];
        public readonly Dictionary<bool, (MeshVertex[] Vertices, ushort[] Indices)> CpuGeometry = [];

        public void Release()
        {
            foreach (var pair in Materials)
                foreach (TextureHandle handle in new[] { pair.Value.Texture, pair.Value.NormalMap, pair.Value.OrmMap })
                    if (handle.IsValid) pair.Key.ReleaseTexture(handle);
            foreach (var pair in Geometry)
                if (pair.Value.IsValid) pair.Key.Renderer.ReleaseMesh(pair.Value);
            Materials.Clear(); Geometry.Clear(); CpuGeometry.Clear();
        }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    public string Error { get; private set; } = "";

    private static string Key(string root, string reference, int frame) => ResourceNames.Resolve(root, reference, ResourceType.Image) + "|" + frame;

    public MeshDrawCall? Material(IRenderController renderer, string root, string reference, int frame)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        string path = ResourceNames.Resolve(root, reference, ResourceType.Image);
        string key = Key(root, reference, frame);
        if (!_entries.TryGetValue(key, out Entry? entry)) _entries[key] = entry = new();
        if (entry.Pending is { IsCompleted: true } pending)
        {
            if (pending.IsCompletedSuccessfully)
            {
                entry.Release(); entry.Pixels = pending.Result.Pixels; entry.Stamp = pending.Result.Stamp; Error = "";
            }
            else Error = pending.Exception?.GetBaseException().Message ?? "Image material unavailable";
            entry.Pending = null;
        }
        if (entry.Pending is null && DateTime.UtcNow > entry.NextCheck)
        {
            entry.NextCheck = DateTime.UtcNow.AddMilliseconds(500);
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (entry.Pixels is null || entry.Stamp != stamp)
                entry.Pending = Task.Run(() => (stamp, TerrainImageMaterial.Load(root, reference, frame)));
        }
        if (entry.Pixels is not { } pixels) return null;
        if (!entry.Materials.TryGetValue(renderer, out MeshDrawCall material))
        {
            material = new MeshDrawCall
            {
                Texture = renderer.CreateTexture(pixels.Width, pixels.Height, pixels.Albedo, Genesis.Shared.Materials.TextureColorSpace.Srgb),
                NormalMap = renderer.CreateTexture(pixels.Width, pixels.Height, pixels.Normal, Genesis.Shared.Materials.TextureColorSpace.Linear),
                OrmMap = renderer.CreateTexture(pixels.Width, pixels.Height, pixels.Orm, Genesis.Shared.Materials.TextureColorSpace.Linear),
                SurfaceParams = new Vector4(1, 0, 0, 0), DetailParams = new Vector4(0, 0, 0, 1),
            };
            entry.Materials[renderer] = material;
        }
        return material;
    }

    public MeshHandle Geometry(IRenderController renderer, string root, string reference, int frame, string mode)
    {
        if (!_entries.TryGetValue(Key(root, reference, frame), out Entry? entry) || entry.Pixels is not { } pixels)
            return MeshHandle.Invalid;
        var key = (renderer, TerrainTextureGeometry.IsExtruded(mode));
        if (!entry.Geometry.TryGetValue(key, out MeshHandle mesh))
        {
            var geometry = CpuGeometry(root, reference, frame, mode)!.Value;
            mesh = geometry.Indices.Length == 0 ? MeshHandle.Invalid : renderer.RegisterMesh(geometry.Vertices, geometry.Indices);
            entry.Geometry[key] = mesh;
        }
        return mesh;
    }

    public (MeshVertex[] Vertices, ushort[] Indices)? CpuGeometry(string root, string reference, int frame, string mode)
    {
        if (!_entries.TryGetValue(Key(root, reference, frame), out Entry? entry) || entry.Pixels is not { } pixels) return null;
        bool extruded = TerrainTextureGeometry.IsExtruded(mode);
        if (!entry.CpuGeometry.TryGetValue(extruded, out var geometry))
            entry.CpuGeometry[extruded] = geometry = extruded ? TerrainTextureGeometry.Extruded(pixels.Width, pixels.Height, pixels.Albedo)
                : TerrainTextureGeometry.Plane(pixels.Width, pixels.Height);
        return geometry;
    }

    public bool OpaqueAt(string root, string reference, int frame, Vector2 uv)
    {
        if (!_entries.TryGetValue(Key(root, reference, frame), out Entry? entry) || entry.Pixels is not { } pixels) return false;
        int x = Math.Clamp((int)(uv.X * pixels.Width), 0, pixels.Width - 1);
        int y = Math.Clamp((int)(uv.Y * pixels.Height), 0, pixels.Height - 1);
        return pixels.Albedo[(y * pixels.Width + x) * 4 + 3] > 16;
    }

    public void Dispose()
    {
        foreach (Entry entry in _entries.Values) entry.Release();
        _entries.Clear(); Error = "";
    }
}
