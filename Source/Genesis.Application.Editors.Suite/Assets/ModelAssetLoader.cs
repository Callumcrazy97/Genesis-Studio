using System.Text.Json;
using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Loads a <c>.model.json</c> resource's kitbash parts and bakes them into renderable geometry,
/// so editors other than the Model Editor (the Room Editor's 3D preview, in particular) can draw a
/// placed model's <b>real</b> shape instead of a placeholder box.
/// </summary>
public static class ModelAssetLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private sealed class PartsDocument
    {
        public List<ModelPart> Parts { get; set; } = [];
        /// <summary>Schema v1 fallback: a single primitive with a uniform scale and tint.</summary>
        public string? Primitive { get; set; }
        public float[]? Tint { get; set; }
        public float Scale { get; set; } = 2f;
        public bool FlowEnabled { get; set; }
        public float FlowSpeedX { get; set; }
        public float FlowSpeedY { get; set; } = -0.6f;
    }

    /// <summary>A model's animated-UV material, so any host can reproduce the authored flow.</summary>
    public readonly record struct ModelFlowSettings(bool Enabled, System.Numerics.Vector2 Speed);

    /// <summary>Reads the model's flow (animated-UV) material settings.</summary>
    public static ModelFlowSettings LoadFlow(string modelPath)
    {
        try
        {
            if (!File.Exists(modelPath))
            {
                return new ModelFlowSettings(false, default);
            }

            PartsDocument? document = JsonSerializer.Deserialize<PartsDocument>(
                File.ReadAllText(modelPath), JsonOptions);
            return document is null
                ? new ModelFlowSettings(false, default)
                : new ModelFlowSettings(
                    document.FlowEnabled,
                    new System.Numerics.Vector2(document.FlowSpeedX, document.FlowSpeedY));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new ModelFlowSettings(false, default);
        }
    }

    /// <summary>
    /// Reads a model resource's parts. Schema v1 documents (one primitive + scale) are migrated to
    /// a single part so older models still render correctly.
    /// </summary>
    public static IReadOnlyList<ModelPart> LoadParts(string modelPath)
    {
        try
        {
            if (!File.Exists(modelPath))
            {
                return [];
            }

            PartsDocument? document = JsonSerializer.Deserialize<PartsDocument>(
                File.ReadAllText(modelPath), JsonOptions);
            if (document is null)
            {
                return [];
            }

            if (document.Parts.Count > 0)
            {
                return document.Parts;
            }
            if (string.IsNullOrWhiteSpace(document.Primitive)) return [];

            ModelPrimitiveKind kind = Enum.TryParse(document.Primitive, true, out ModelPrimitiveKind parsed)
                ? parsed
                : ModelPrimitiveKind.Cube;
            float scale = MathF.Max(0.1f, document.Scale);
            float[] tint = document.Tint is { Length: >= 3 } ? document.Tint : [0.62f, 0.68f, 0.85f];
            return
            [
                new ModelPart
                {
                    Name = kind.ToString(),
                    Primitive = kind,
                    Position = [0f, scale * 0.55f, 0f],
                    Scale = [scale, scale, scale],
                    Color = tint,
                },
            ];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return [];   // unreadable model: caller falls back to its placeholder
        }
    }

    /// <summary>
    /// Loads a model resource straight to vertex/index buffers. The baked sidecar is authoritative
    /// when present because it contains sculpting, vertex paint, and procedural geometry that the
    /// primitive-part document cannot represent.
    /// </summary>
    public static (MeshVertex[] Vertices, ushort[] Indices) LoadBaked(string modelPath)
    {
        string canonical = StudioModelResourceLoader.CanonicalPath(modelPath);
        if (File.Exists(canonical))
        {
            GModelAsset asset = RuntimeModelStore.Load(canonical);
            if (asset.HasRenderableMeshes && !asset.ImportRequired)
                return FlattenBindPose(asset);
        }
        return ModelMeshSidecar.TryLoad(modelPath, out MeshVertex[] vertices, out ushort[] indices)
            ? (vertices, indices)
            : ModelPartBuilder.Bake(LoadParts(modelPath));
    }

    private static (MeshVertex[] Vertices, ushort[] Indices) FlattenBindPose(GModelAsset asset)
    {
        List<MeshVertex> vertices = [];
        List<ushort> indices = [];
        Matrix4x4[] palette = asset.Rig.IsValid
            ? GModelPrimitiveFactory.EvaluateBindPosePalette(asset.Rig)
            : [];
        foreach (GModelMesh mesh in asset.Meshes)
        {
            int count = mesh.IsSkinned ? mesh.SkinnedVertices.Length : mesh.Vertices.Length;
            if (count == 0 || vertices.Count + count > ushort.MaxValue) continue;
            int offset = vertices.Count;
            Vector4 tint = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < asset.Materials.Count
                ? asset.Materials[mesh.MaterialIndex].BaseColor
                : Vector4.One;
            if (mesh.IsSkinned)
            {
                foreach (SkinnedMeshVertex source in mesh.SkinnedVertices)
                {
                    (Vector3 position, Vector3 normal) = Skin(source, palette);
                    vertices.Add(new MeshVertex
                    {
                        Position = position,
                        Normal = normal,
                        Color = source.Color * tint,
                        UV = source.UV,
                    });
                }
            }
            else
            {
                foreach (MeshVertex source in mesh.Vertices)
                {
                    MeshVertex copy = source;
                    copy.Color *= tint;
                    vertices.Add(copy);
                }
            }
            foreach (ushort index in mesh.Indices)
                indices.Add(checked((ushort)(offset + index)));
        }
        return (vertices.ToArray(), indices.ToArray());
    }

    private static (Vector3 Position, Vector3 Normal) Skin(SkinnedMeshVertex vertex, Matrix4x4[] palette)
    {
        Vector3 position = Vector3.Zero;
        Vector3 normal = Vector3.Zero;
        float total = 0f;
        for (int i = 0; i < 4; i++)
        {
            float weight = i switch { 0 => vertex.JointWeights.X, 1 => vertex.JointWeights.Y, 2 => vertex.JointWeights.Z, _ => vertex.JointWeights.W };
            int joint = (int)(i switch { 0 => vertex.JointIndices.X, 1 => vertex.JointIndices.Y, 2 => vertex.JointIndices.Z, _ => vertex.JointIndices.W });
            if (weight <= 0f || joint < 0 || joint >= palette.Length) continue;
            position += Vector3.Transform(vertex.Position, palette[joint]) * weight;
            normal += Vector3.TransformNormal(vertex.Normal, palette[joint]) * weight;
            total += weight;
        }
        if (total <= 1e-6f) return (vertex.Position, vertex.Normal);
        position /= total;
        normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : vertex.Normal;
        return (position, normal);
    }
}
