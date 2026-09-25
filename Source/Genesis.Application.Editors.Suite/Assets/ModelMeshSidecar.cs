using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Reads and writes the editor's compact baked-mesh sidecar. Keeping this contract in one place
/// makes sculpted and procedurally generated models load identically in every editor.
/// </summary>
public static class ModelMeshSidecar
{
    private const int Magic = 0x4853454D; // 'MESH'
    private const int Version = 1;
    private const int VertexBytes = 48;
    private const int MaximumVertices = ushort.MaxValue;
    private const int MaximumIndices = 16 * 1024 * 1024;

    public static string PathFor(string modelPath) => modelPath + ".mesh";

    public static void Save(string modelPath, IReadOnlyList<MeshVertex> vertices, IReadOnlyList<ushort> indices)
    {
        string sidecarPath = PathFor(modelPath);
        string? directory = Path.GetDirectoryName(sidecarPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(sidecarPath);
        using BinaryWriter writer = new(stream);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(vertices.Count);
        writer.Write(indices.Count);
        foreach (MeshVertex vertex in vertices)
        {
            writer.Write(vertex.Position.X); writer.Write(vertex.Position.Y); writer.Write(vertex.Position.Z);
            writer.Write(vertex.Normal.X); writer.Write(vertex.Normal.Y); writer.Write(vertex.Normal.Z);
            writer.Write(vertex.Color.X); writer.Write(vertex.Color.Y); writer.Write(vertex.Color.Z); writer.Write(vertex.Color.W);
            writer.Write(vertex.UV.X); writer.Write(vertex.UV.Y);
        }

        foreach (ushort index in indices)
        {
            writer.Write(index);
        }
    }

    public static bool TryLoad(string modelPath, out MeshVertex[] vertices, out ushort[] indices)
    {
        vertices = [];
        indices = [];
        string sidecarPath = PathFor(modelPath);
        if (!File.Exists(sidecarPath))
        {
            return false;
        }

        try
        {
            using FileStream stream = File.OpenRead(sidecarPath);
            using BinaryReader reader = new(stream);
            if (stream.Length < 16 || reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
            {
                return false;
            }

            int vertexCount = reader.ReadInt32();
            int indexCount = reader.ReadInt32();
            if (vertexCount is < 0 or > MaximumVertices || indexCount is < 0 or > MaximumIndices)
            {
                return false;
            }

            long expectedBytes = 16L + (long)vertexCount * VertexBytes + (long)indexCount * sizeof(ushort);
            if (stream.Length != expectedBytes)
            {
                return false;
            }

            vertices = new MeshVertex[vertexCount];
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new MeshVertex
                {
                    Position = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Normal = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Color = new System.Numerics.Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    UV = new System.Numerics.Vector2(reader.ReadSingle(), reader.ReadSingle()),
                };
            }

            indices = new ushort[indexCount];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = reader.ReadUInt16();
                if (indices[i] >= vertexCount && vertexCount > 0)
                {
                    vertices = [];
                    indices = [];
                    return false;
                }
            }

            return vertexCount > 0 && indexCount >= 3;
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException)
        {
            vertices = [];
            indices = [];
            return false;
        }
    }
}
