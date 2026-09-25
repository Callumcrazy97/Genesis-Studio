using System.Globalization;
using System.Numerics;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Assets;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private sealed class EntityDrawList : IMeshDrawList
    {
        public readonly List<MeshDrawCall> Items = [];
        public int Count => Items.Count;
        public void Clear() => Items.Clear();
        public void Add(in MeshDrawCall call) => Items.Add(call);
        public int CopyTo(MeshDrawCall[] buffer, int startIndex)
        { foreach (var item in Items) { if (startIndex >= buffer.Length) break; buffer[startIndex++] = item; } return startIndex; }
    }
    private readonly EntityDrawList _entityDrawList = new();
    private sealed class ImagePreviewEntry
    {
        public DateTime CheckedAt, Stamp;
        public Task<TerrainImageMaterial>? Pending;
        public TerrainImageMaterial? Pixels;
        public readonly Dictionary<IRenderController, MeshDrawCall> Materials = [];
    }
    private readonly Dictionary<string, ImagePreviewEntry> _entityImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IRenderController, MeshHandle> _entityPlanes = [];
    private readonly Dictionary<string, (DateTime Stamp, ShaderAssetDocument Document)> _entityShaders = new(StringComparer.OrdinalIgnoreCase);

    private MeshDrawCall? EntityImageMaterial(IRenderController renderer, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        string path = ResourceNames.Resolve(ProjectRoot, reference);
        if (!_entityImages.TryGetValue(path, out var entry)) _entityImages[path] = entry = new();
        if (entry.Pending is { IsCompleted: true } pending)
        {
            if (pending.IsCompletedSuccessfully)
            {
                foreach (var pair in entry.Materials) ReleaseImageMaterial(pair.Key, pair.Value);
                entry.Materials.Clear(); entry.Pixels = pending.Result;
            }
            else MaterialPreviewStatus = pending.Exception?.GetBaseException().Message ?? "Image material unavailable";
            entry.Pending = null;
        }
        if (entry.Pending is null && DateTime.UtcNow > entry.CheckedAt)
        {
            entry.CheckedAt = DateTime.UtcNow.AddSeconds(1);
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (entry.Stamp != stamp) { entry.Stamp = stamp; entry.Pending = Task.Run(() => TerrainImageMaterial.Load(ProjectRoot, reference)); }
        }
        if (entry.Pixels is not { } pixels) return null;
        if (!entry.Materials.TryGetValue(renderer, out var material))
        {
            material = new MeshDrawCall { Texture = renderer.CreateTexture(pixels.Width, pixels.Height, pixels.Albedo),
                NormalMap = renderer.CreateTexture(pixels.Width, pixels.Height, pixels.Normal), OrmMap = renderer.CreateTexture(pixels.Width, pixels.Height, pixels.Orm),
                SurfaceParams = new Vector4(1, 0, 0, 0), DetailParams = new Vector4(0, 0, 0, 1) };
            entry.Materials[renderer] = material;
        }
        return material;
    }

    private void SubmitEntityDraw(IRenderController renderer, TerrainEntityDocument document, MeshDrawCall draw)
    {
        var texture = document.Components.FirstOrDefault(component => component.Enabled && component.Type == TerrainEntityComponentKinds.Texture);
        if (texture is not null && EntityImageMaterial(renderer, texture.Get("Texture")) is { } material)
        {
            draw.Texture = material.Texture; draw.NormalMap = material.NormalMap; draw.OrmMap = material.OrmMap;
            draw.SurfaceParams = material.SurfaceParams; draw.DetailParams = material.DetailParams;
            if (float.TryParse(texture.Get("AnimationFps"), NumberStyles.Float, CultureInfo.InvariantCulture, out float fps) && fps > 0)
            {
                var sprite = SpriteAssetLoader.Load(ResourceNames.Resolve(ProjectRoot, texture.Get("Texture"), ResourceType.Image));
                int frames = sprite.Frames.Count;
                if (int.TryParse(texture.Get("FrameCount"), out int count) && count > 0) frames = Math.Min(frames, count);
                if (frames > 1) draw.Texture = renderer.LoadTexture(SpriteAssetLoader.ResolveFrameTexturePath(ProjectRoot, texture.Get("Texture"), (int)(_viewportSession.Clock.Time * fps) % frames));
            }
        }
        var shader = document.Components.FirstOrDefault(component => component.Enabled && component.Type == TerrainEntityComponentKinds.Shader);
        if (shader is not null && !string.IsNullOrWhiteSpace(shader.Get("Shader")) && ObjectDrawPass.TryApplyMeshShader(renderer, ProjectRoot, shader.Get("Shader"), ref draw))
        {
            try
            {
                string path = ResourceNames.Resolve(ProjectRoot, shader.Get("Shader"), ResourceType.Shader);
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (!_entityShaders.TryGetValue(path, out var cached) || cached.Stamp != stamp)
                {
                    var asset = ShaderAssetDocument.Load(path); ShaderParameterReflection.Synchronize(asset);
                    _entityShaders[path] = cached = (stamp, asset);
                }
                var overrides = new Dictionary<string, float[]>();
                foreach (var parameter in cached.Document.Parameters)
                {
                    float[] values = (float[])parameter.Value.Clone();
                    for (int i = 0; i < values.Length; i++)
                        if (float.TryParse(shader.Get($"Parameter.{parameter.Name}.{i}"), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) values[i] = value;
                    overrides[parameter.Name] = values;
                }
                ShaderParameterReflection.Pack(cached.Document, overrides, out draw.ShaderParams0, out draw.ShaderParams1, out draw.ShaderParams2, out draw.ShaderParams3);
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidOperationException)
            { MaterialPreviewStatus = exception.Message; }
        }
        renderer.DrawMesh(draw);
    }

    private void DrawEntitySprite(IRenderController renderer, TerrainEntityDocument document, Matrix4x4 world)
    {
        var texture = document.Components.FirstOrDefault(component => component.Enabled && component.Type == TerrainEntityComponentKinds.Texture);
        if (texture is null || EntityImageMaterial(renderer, texture.Get("Texture")) is not { } material) return;
        if (!_entityPlanes.TryGetValue(renderer, out var mesh))
        {
            MeshVertex Vertex(float x, float y, float u, float v) => new() { Position = new Vector3(x, y, 0), Normal = Vector3.UnitZ, UV = new Vector2(u, v), Color = Vector4.One };
            mesh = renderer.RegisterMesh(new[] { Vertex(-.5f, 0, 0, 1), Vertex(.5f, 0, 1, 1), Vertex(.5f, 1, 1, 0), Vertex(-.5f, 1, 0, 0) }, new ushort[] { 0, 1, 2, 0, 2, 3 });
            _entityPlanes[renderer] = mesh;
        }
        float scale = float.TryParse(texture.Get("Scale", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? Math.Clamp(value, .001f, 1000) : 1;
        Matrix4x4 facing = Matrix4x4.Identity;
        if (texture.Get("Mode") == nameof(TerrainEntityTextureMode.Billboard2D))
        {
            Vector3 toward = _viewport.Camera.Eye - world.Translation;
            facing = Matrix4x4.CreateRotationY(MathF.Atan2(toward.X, toward.Z));
        }
        material.Mesh = mesh; material.World = Matrix4x4.CreateScale(scale) * facing * world;
        material.Tint = RenderColor.White; material.Alpha = 1;
        material.Flags = MeshRasterDefaults.ApplyOverride(MeshDrawFlags.Transparent, FaceCullingOverride.None, document.WindingOrder);
        SubmitEntityDraw(renderer, document, material);
    }

    private static void ReleaseImageMaterial(IRenderController renderer, MeshDrawCall material)
    { foreach (var handle in new[] { material.Texture, material.NormalMap, material.OrmMap }) if (handle.IsValid) renderer.ReleaseTexture(handle); }

    private void ReleaseEntityMaterials()
    {
        foreach (var entry in _entityImages.Values) foreach (var pair in entry.Materials) ReleaseImageMaterial(pair.Key, pair.Value);
        _entityImages.Clear(); _entityShaders.Clear();
        foreach (var pair in _entityPlanes) if (pair.Value.IsValid) pair.Key.ReleaseMesh(pair.Value);
        _entityPlanes.Clear();
    }
}
