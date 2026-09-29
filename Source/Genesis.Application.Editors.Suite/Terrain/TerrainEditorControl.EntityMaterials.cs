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
    private readonly TerrainImagePreviewCache _entityImages = new();
    private readonly Dictionary<string, (DateTime Stamp, ShaderAssetDocument Document)> _entityShaders = new(StringComparer.OrdinalIgnoreCase);

    private MeshDrawCall? EntityImageMaterial(IRenderController renderer, string reference, int frameIndex = 0)
    {
        MeshDrawCall? material = _entityImages.Material(renderer, ProjectRoot, reference, frameIndex);
        if (!string.IsNullOrEmpty(_entityImages.Error)) MaterialPreviewStatus = _entityImages.Error;
        return material;
    }

    private void SubmitEntityDraw(IRenderController renderer, TerrainEntityDocument document, MeshDrawCall draw)
    {
        var texture = document.Components.FirstOrDefault(component => component.Enabled && component.Type == TerrainEntityComponentKinds.Texture);
        if (texture is not null && EntityImageMaterial(renderer, texture.Get("Texture"), EntityTextureFrame(texture)) is { } material)
        {
            draw.Texture = material.Texture; draw.NormalMap = material.NormalMap; draw.OrmMap = material.OrmMap;
            draw.SurfaceParams = material.SurfaceParams; draw.DetailParams = material.DetailParams;
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
        if (texture is null) return;
        int frame = EntityTextureFrame(texture);
        if (EntityImageMaterial(renderer, texture.Get("Texture"), frame) is not { } material) return;
        MeshHandle mesh = _entityImages.Geometry(renderer, ProjectRoot, texture.Get("Texture"), frame, texture.Get("Mode"));
        float scale = float.TryParse(texture.Get("Scale", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? Math.Clamp(value, .001f, 1000) : 1;
        Matrix4x4 facing = TerrainTextureGeometry.Facing(texture.Get("Mode"), world, _viewport.Camera.Eye);
        material.Mesh = mesh; material.World = Matrix4x4.CreateScale(scale) * facing * world;
        material.Tint = RenderColor.White; material.Alpha = 1;
        material.Flags = MeshRasterDefaults.ApplyOverride(MeshDrawFlags.Transparent, FaceCullingOverride.None, document.WindingOrder);
        SubmitEntityDraw(renderer, document, material);
    }

    private int EntityTextureFrame(TerrainEntityComponent texture)
    {
        if (!float.TryParse(texture.Get("AnimationFps"), NumberStyles.Float, CultureInfo.InvariantCulture, out float fps) || fps <= 0) return 0;
        var sprite = SpriteAssetLoader.Load(ResourceNames.Resolve(ProjectRoot, texture.Get("Texture"), ResourceType.Image));
        int count = Math.Max(1, sprite.Frames.Count);
        if (int.TryParse(texture.Get("FrameCount"), out int requested) && requested > 0) count = Math.Min(count, requested);
        return (int)(_viewportSession.Clock.Time * fps) % count;
    }

    private void ReleaseEntityMaterials()
    {
        _entityImages.Dispose(); _entityShaders.Clear();
    }
}
