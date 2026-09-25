#nullable enable annotations
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Genesis.Rendering.Meshes;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Modeling;

/// <summary>
/// Runtime reader for the Studio <c>.model.json</c> resource and its authoritative baked
/// <c>.mesh</c> sidecar. This closes the resource flow between Model Editor output and F5.
/// </summary>
public static class StudioModelResourceLoader
{
    private const int MeshMagic = 0x4853454D; // 'MESH'
    private const int MeshVersion = 1;
    private const int VertexBytes = 48;

    private sealed class Document
    {
        public string? Source { get; set; }
        public string? Primitive { get; set; }
        public float[] Tint { get; set; } = [0.62f, 0.68f, 0.85f];
        public float Scale { get; set; } = 2f;
        public float Emissive { get; set; }
        public float[] EmissiveColor { get; set; } = [1f, 1f, 1f];
        public bool DoubleSided { get; set; }
        public FaceCullingOverride Culling { get; set; } = FaceCullingOverride.Default;
        public FrontFaceWindingOverride WindingOrder { get; set; } = FrontFaceWindingOverride.Default;
        public List<Part> Parts { get; set; } = [];
        public List<string> Materials { get; set; } = [];
    }

    private sealed class Part
    {
        public string Name { get; set; } = "Part";
        public string Primitive { get; set; } = "Cube";
        public float[] Position { get; set; } = [0f, 0f, 0f];
        public float[] Rotation { get; set; } = [0f, 0f, 0f];
        public float[] Scale { get; set; } = [1f, 1f, 1f];
        public float[] Color { get; set; } = [0.62f, 0.68f, 0.85f];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Resolve(string projectPath, string modelName)
    {
        return ResourceNames.Resolve(projectPath, modelName, ResourceType.Model);
    }

    public static long Stamp(string path)
    {
        long document = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;
        string sidecar = path + ".mesh";
        long mesh = File.Exists(sidecar) ? File.GetLastWriteTimeUtc(sidecar).Ticks : 0L;
        string canonical = CanonicalPath(path);
        long canonicalStamp = File.Exists(canonical) ? File.GetLastWriteTimeUtc(canonical).Ticks : 0L;
        string source = ResolveSource(path);
        long sourceStamp = File.Exists(source) ? File.GetLastWriteTimeUtc(source).Ticks : 0L;
        return Math.Max(Math.Max(document, mesh), Math.Max(canonicalStamp, sourceStamp));
    }

    public static GModelAsset Load(string path) => LoadCore(path, allowReimport: true);

    /// <summary>Reads the last saved model for background previews without importing or writing assets.</summary>
    public static GModelAsset LoadReadOnly(string path) => LoadCore(path, allowReimport: false);

    private static GModelAsset LoadCore(string path, bool allowReimport)
    {
        Document document = ReadDocument(path);
        string canonical = CanonicalPath(path);
        string source = ResolveSource(path, document);
        if (File.Exists(canonical)
            && (!allowReimport || string.IsNullOrWhiteSpace(source)
                || !File.Exists(source)
                || File.GetLastWriteTimeUtc(canonical) >= File.GetLastWriteTimeUtc(source)))
        {
            GModelAsset canonicalAsset = RuntimeModelStore.Load(canonical);
            canonicalAsset.Culling = document.Culling;
            canonicalAsset.WindingOrder = document.WindingOrder;
            return canonicalAsset;
        }

        if (!allowReimport && !string.IsNullOrWhiteSpace(source) && !File.Exists(path + ".mesh"))
            return RuntimeModelStore.CreateEmpty(Path.GetFileNameWithoutExtension(path), "Import this model before requesting a preview.");

        if (allowReimport && File.Exists(source) && ExternalModelImporter.CanImport(source))
        {
            try
            {
                string projectRoot = FindProjectRoot(path);
                GModelAsset imported = ExternalModelImporter.Import(source, projectRoot, path);
                imported.Culling = document.Culling;
                imported.WindingOrder = document.WindingOrder;
                RuntimeModelStore.Save(canonical, imported);
                return imported;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or JsonException or NotSupportedException or OverflowException)
            {
                if (File.Exists(canonical))
                {
                    GModelAsset fallback = RuntimeModelStore.Load(canonical);
                    fallback.Culling = document.Culling;
                    fallback.WindingOrder = document.WindingOrder;
                    fallback.ImportMessage = "Reimport failed; using the last canonical asset: " + exception.Message;
                    return fallback;
                }
                GModelAsset failed = RuntimeModelStore.CreateEmpty(
                    Path.GetFileName(path).Replace(".model.json", string.Empty, StringComparison.OrdinalIgnoreCase),
                    "External model import failed: " + exception.Message);
                failed.SourceFile = source;
                return failed;
            }
        }

        (MeshVertex[] vertices, ushort[] indices) = ReadSidecar(path + ".mesh");
        if (vertices.Length == 0 || indices.Length == 0)
            (vertices, indices) = Bake(document);

        GModelAsset asset = new()
        {
            Name = Path.GetFileName(path).Replace(".model.json", string.Empty, StringComparison.OrdinalIgnoreCase),
            SourceFile = path,
            ImportRequired = vertices.Length == 0 || indices.Length == 0,
            ImportMessage = vertices.Length == 0 ? "Studio model contains no renderable geometry." : string.Empty,
            Culling = document.Culling,
            WindingOrder = document.WindingOrder,
        };
        string material = document.Materials.FirstOrDefault() ?? string.Empty;
        asset.Materials.Add(new GModelMaterial
        {
            Name = "Studio Material",
            AlbedoTexture = material,
            EmissiveFactor = Vec(document.EmissiveColor, 1f) * MathF.Max(0f, document.Emissive),
            DoubleSided = document.DoubleSided,
        });
        if (!asset.ImportRequired)
        {
            asset.Meshes.Add(new GModelMesh
            {
                Name = "Baked Mesh",
                MaterialIndex = 0,
                Vertices = vertices,
                Indices = indices,
                IsSkinned = false,
            });
        }
        asset.RecalculateBounds();
        return asset;
    }

    /// <summary>The canonical model beside a Studio descriptor. It moves/renames with the resource.</summary>
    public static string CanonicalPath(string resourcePath)
    {
        string full = Path.GetFullPath(resourcePath ?? string.Empty);
        string directory = Path.GetDirectoryName(full) ?? ".";
        string name = Path.GetFileName(full).Replace(".model.json", string.Empty, StringComparison.OrdinalIgnoreCase);
        return Path.Combine(directory, name + ".gmodel");
    }

    public static void SaveCanonical(string resourcePath, GModelAsset asset)
        => RuntimeModelStore.Save(CanonicalPath(resourcePath), asset);

    public static string ResolveSource(string resourcePath)
        => ResolveSource(resourcePath, ReadDocument(resourcePath));

    private static string ResolveSource(string resourcePath, Document document)
    {
        string source = document.Source?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source)
            || source.Equals("Generated", StringComparison.OrdinalIgnoreCase)
            || source.Equals("Baked", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        string descriptorDirectory = Path.GetDirectoryName(Path.GetFullPath(resourcePath)) ?? ".";
        return Path.IsPathRooted(source)
            ? Path.GetFullPath(source)
            : Path.GetFullPath(Path.Combine(descriptorDirectory, source.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string FindProjectRoot(string resourcePath)
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(Path.GetFullPath(resourcePath)) ?? ".");
        while (directory != null)
        {
            if (directory.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase))
                return directory.Parent?.FullName ?? directory.FullName;
            directory = directory.Parent;
        }
        return Path.GetDirectoryName(Path.GetFullPath(resourcePath)) ?? ".";
    }

    private static Document ReadDocument(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions) ?? new Document();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Document();
        }
    }

    private static (MeshVertex[] Vertices, ushort[] Indices) ReadSidecar(string path)
    {
        if (!File.Exists(path)) return ([], []);
        try
        {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream);
            if (stream.Length < 16 || reader.ReadInt32() != MeshMagic || reader.ReadInt32() != MeshVersion) return ([], []);
            int vertexCount = reader.ReadInt32();
            int indexCount = reader.ReadInt32();
            if (vertexCount is <= 0 or > ushort.MaxValue || indexCount is < 3 or > 16 * 1024 * 1024) return ([], []);
            if (stream.Length != 16L + (long)vertexCount * VertexBytes + (long)indexCount * sizeof(ushort)) return ([], []);
            MeshVertex[] vertices = new MeshVertex[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                vertices[i] = new MeshVertex
                {
                    Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Color = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    UV = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                };
            }
            ushort[] indices = new ushort[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                indices[i] = reader.ReadUInt16();
                if (indices[i] >= vertexCount) return ([], []);
            }
            return (vertices, indices);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return ([], []);
        }
    }

    private static (MeshVertex[] Vertices, ushort[] Indices) Bake(Document document)
    {
        List<Part> parts = document.Parts;
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(document.Primitive))
        {
            float scale = MathF.Max(0.1f, document.Scale);
            parts = [new Part
            {
                Name = document.Primitive!, Primitive = document.Primitive!,
                Position = [0f, scale * 0.55f, 0f], Scale = [scale, scale, scale], Color = document.Tint,
            }];
        }
        if (parts.Count == 0) return ([], []);

        List<MeshCombinePart> combined = new(parts.Count);
        foreach (Part part in parts)
        {
            RenderColor color = Color(part.Color);
            (MeshVertex[] vertices, ushort[] indices) = part.Primitive.ToLowerInvariant() switch
            {
                "sphere" => MeshGeometry.BuildSphere(color, 0.5f),
                "cylinder" => MeshGeometry.BuildCylinder(color, 0.5f, 1f),
                "cone" => MeshGeometry.BuildCone(color, 0.5f, 1f),
                "pyramid" => MeshGeometry.BuildPyramid(color, 0.5f, 1f),
                "torus" or "ring" => MeshGeometry.BuildTorus(color, 0.5f, 0.035f),
                "checkerfloor" => MeshGeometry.BuildCheckerFloor(
                    new RenderColor(0.006f, 0.007f, 0.009f, 1f),
                    new RenderColor(0.82f, 0.84f, 0.88f, 1f)),
                "quad" => MeshGeometry.BuildQuad(color),
                _ => MeshGeometry.BuildCube(color, 1f),
            };
            Vector3 position = Vec(part.Position, 0f);
            Vector3 rotation = Vec(part.Rotation, 0f) * (MathF.PI / 180f);
            Vector3 scale = Vec(part.Scale, 1f);
            Matrix4x4 transform = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromYawPitchRoll(rotation.Y, rotation.X, rotation.Z)
                * Matrix4x4.CreateTranslation(position);
            combined.Add(new MeshCombinePart(vertices, indices, transform));
        }
        return MeshCombiner.Combine(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(combined));
    }

    private static Vector3 Vec(float[]? values, float fallback) => new(
        values is { Length: > 0 } ? values[0] : fallback,
        values is { Length: > 1 } ? values[1] : fallback,
        values is { Length: > 2 } ? values[2] : fallback);

    private static RenderColor Color(float[]? values) => new(
        Math.Clamp(values is { Length: > 0 } ? values[0] : 0.62f, 0f, 1f),
        Math.Clamp(values is { Length: > 1 } ? values[1] : 0.68f, 0f, 1f),
        Math.Clamp(values is { Length: > 2 } ? values[2] : 0.85f, 0f, 1f),
        1f);
}
