using System.Numerics;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>The primitive shapes a kitbash part can be.</summary>
public enum ModelPrimitiveKind
{
    Cube,
    Sphere,
    Cylinder,
    Capsule,
    Cone,
    Quad,
}

/// <summary>
/// One kitbash part: a primitive with its own transform and colour. A model is a list of these,
/// baked into a single mesh by <see cref="ModelPartBuilder"/> — which is what makes the result a
/// real editable/sculptable mesh rather than a single hardcoded primitive.
/// </summary>
public sealed class ModelPart
{
    public string Name { get; set; } = "Part";
    public ModelPrimitiveKind Primitive { get; set; } = ModelPrimitiveKind.Cube;
    public float[] Position { get; set; } = [0f, 0f, 0f];
    public float[] Rotation { get; set; } = [0f, 0f, 0f];   // degrees (yaw/pitch/roll applied Y,X,Z)
    public float[] Scale { get; set; } = [1f, 1f, 1f];
    public float[] Color { get; set; } = [0.62f, 0.68f, 0.85f];

    public Vector3 PositionVector => Vec(Position, 0f);
    public Vector3 RotationVector => Vec(Rotation, 0f);
    public Vector3 ScaleVector
    {
        get
        {
            Vector3 s = Vec(Scale, 1f);
            return new Vector3(
                MathF.Abs(s.X) < 0.001f ? 0.001f : s.X,
                MathF.Abs(s.Y) < 0.001f ? 0.001f : s.Y,
                MathF.Abs(s.Z) < 0.001f ? 0.001f : s.Z);
        }
    }

    public RenderColor RenderTint => new(
        Math.Clamp(Color.ElementAtOrDefault(0), 0f, 1f),
        Math.Clamp(Color.ElementAtOrDefault(1), 0f, 1f),
        Math.Clamp(Color.ElementAtOrDefault(2), 0f, 1f));

    public Matrix4x4 LocalTransform()
    {
        Vector3 r = RotationVector * (MathF.PI / 180f);
        return Matrix4x4.CreateScale(ScaleVector)
            * Matrix4x4.CreateFromYawPitchRoll(r.Y, r.X, r.Z)
            * Matrix4x4.CreateTranslation(PositionVector);
    }

    public ModelPart Clone() => new()
    {
        Name = Name,
        Primitive = Primitive,
        Position = [.. Position],
        Rotation = [.. Rotation],
        Scale = [.. Scale],
        Color = [.. Color],
    };

    private static Vector3 Vec(float[] a, float fallback) => new(
        a is { Length: > 0 } ? a[0] : fallback,
        a is { Length: > 1 } ? a[1] : fallback,
        a is { Length: > 2 } ? a[2] : fallback);
}

/// <summary>
/// Bakes a list of <see cref="ModelPart"/>s into one mesh (positions/normals/colours baked per
/// vertex) via the shared <see cref="MeshCombiner"/>. The baked arrays are what the Model Editor
/// previews, what sculpting will displace, and what gets saved as the model's geometry.
/// </summary>
public static class ModelPartBuilder
{
    private readonly record struct PositionKey(int X, int Y, int Z)
    {
        public static PositionKey From(Vector3 value) => new(
            (int)MathF.Round(value.X * 100000f),
            (int)MathF.Round(value.Y * 100000f),
            (int)MathF.Round(value.Z * 100000f));
    }

    private sealed class EdgeInfo(int a, int b)
    {
        public int A { get; } = Math.Min(a, b);
        public int B { get; } = Math.Max(a, b);
        public List<int> Faces { get; } = [];
    }

    private sealed record SubdivisionFace(int[] Vertices, int Material);

    /// <summary>Unit-sized source geometry for a primitive kind (part transform scales it).</summary>
    public static (MeshVertex[] Vertices, ushort[] Indices) BuildPrimitive(ModelPrimitiveKind kind, RenderColor color) => kind switch
    {
        ModelPrimitiveKind.Sphere => MeshGeometry.BuildSphere(color, 0.5f),
        ModelPrimitiveKind.Cylinder => MeshGeometry.BuildCylinder(color, 0.5f, 1f),
        ModelPrimitiveKind.Capsule => BuildCapsule(color),
        ModelPrimitiveKind.Cone => MeshGeometry.BuildCone(color, 0.5f, 1f),
        ModelPrimitiveKind.Quad => MeshGeometry.BuildQuad(color),
        _ => MeshGeometry.BuildCube(color, 1f),
    };

    private static (MeshVertex[] Vertices, ushort[] Indices) BuildCapsule(RenderColor color)
    {
        (MeshVertex[] cylinder, ushort[] cylinderIndices) = MeshGeometry.BuildCylinder(color, 0.5f, 0.5f);
        (MeshVertex[] sphere, ushort[] sphereIndices) = MeshGeometry.BuildSphere(color, 0.5f);
        MeshCombinePart[] parts =
        [
            new(cylinder, cylinderIndices, Matrix4x4.Identity),
            new(sphere, sphereIndices, Matrix4x4.CreateScale(1f, .5f, 1f) * Matrix4x4.CreateTranslation(0f, .5f, 0f)),
            new(sphere, sphereIndices, Matrix4x4.CreateScale(1f, .5f, 1f) * Matrix4x4.CreateTranslation(0f, -.5f, 0f)),
        ];
        return MeshCombiner.Combine(parts);
    }

    /// <summary>Bakes every part into a single vertex/index buffer in part order.</summary>
    public static (MeshVertex[] Vertices, ushort[] Indices) Bake(IReadOnlyList<ModelPart> parts)
    {
        if (parts.Count == 0)
        {
            return ([], []);
        }

        List<MeshCombinePart> combine = new(parts.Count);
        foreach (ModelPart part in parts)
        {
            (MeshVertex[] verts, ushort[] indices) = BuildPrimitive(part.Primitive, part.RenderTint);
            combine.Add(new MeshCombinePart(verts, indices, part.LocalTransform()));
        }

        return MeshCombiner.Combine(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(combine));
    }

    /// <summary>Axis-aligned bounds of the baked geometry (for framing the camera and for tests).</summary>
    public static (Vector3 Min, Vector3 Max) Bounds(IReadOnlyList<MeshVertex> vertices)
    {
        if (vertices.Count == 0)
        {
            return (Vector3.Zero, Vector3.Zero);
        }

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (MeshVertex v in vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }

        return (min, max);
    }

    /// <summary>Recomputes smooth vertex normals by area-weighted face accumulation. Called after
    /// sculpting displaces positions so lighting stays correct.</summary>
    public static void RecomputeNormals(MeshVertex[] vertices, ushort[] indices)
    {
        Vector3[] acc = new Vector3[vertices.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= vertices.Length || b >= vertices.Length || c >= vertices.Length)
            {
                continue;
            }

            Vector3 face = Vector3.Cross(
                vertices[b].Position - vertices[a].Position,
                vertices[c].Position - vertices[a].Position);
            acc[a] += face;
            acc[b] += face;
            acc[c] += face;
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 n = acc[i];
            vertices[i].Normal = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : vertices[i].Normal;
        }
    }

    /// <summary>Evaluates a non-destructive Catmull-Clark modifier from the retained control cage.</summary>
    public static void SetSubdivisionLevel(GModelMesh mesh, int requestedLevel)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.IsSkinned || (mesh.MorphTargets?.Count ?? 0) > 0)
            throw new InvalidOperationException("Subdivision is available for unskinned meshes without morph targets.");

        int level = Math.Clamp(requestedLevel, 0, 2);
        if (mesh.SubdivisionCageVertices is not { Length: > 0 }
            || mesh.SubdivisionCageIndices is not { Length: >= 3 })
        {
            mesh.SubdivisionCageVertices = (MeshVertex[])mesh.Vertices.Clone();
            mesh.SubdivisionCageIndices = (ushort[])mesh.Indices.Clone();
            mesh.SubdivisionCageMaterials = Materials(mesh.TriangleMaterialIndices, mesh.Indices.Length / 3, mesh.MaterialIndex);
        }

        MeshVertex[] vertices = (MeshVertex[])mesh.SubdivisionCageVertices.Clone();
        ushort[] indices = (ushort[])mesh.SubdivisionCageIndices.Clone();
        int[] materials = Materials(mesh.SubdivisionCageMaterials, indices.Length / 3, mesh.MaterialIndex);
        List<SubdivisionFace>? faces = null;
        for (int pass = 0; pass < level; pass++)
            (vertices, indices, materials, faces) = CatmullClarkOnce(vertices, indices, materials, faces);

        mesh.Vertices = vertices;
        mesh.Indices = indices;
        mesh.TriangleMaterialIndices = materials;
        mesh.SubdivisionLevel = level;
        mesh.FaceGroups.Clear();
        if (mesh.SmoothShading)
        {
            ApplySmoothShading(mesh.Vertices, mesh.Indices);
        }
        else
        {
            (mesh.Vertices, mesh.Indices) = FlatGeometry(mesh.Vertices, mesh.Indices);
            mesh.TriangleMaterialIndices = Materials(materials, mesh.Indices.Length / 3, mesh.MaterialIndex);
        }
        mesh.Tangents = [];
    }

    /// <summary>Makes the evaluated surface the new editable cage before destructive topology work.</summary>
    public static void ApplySubdivision(GModelMesh mesh)
    {
        if (mesh is null || mesh.SubdivisionLevel == 0) return;
        mesh.SubdivisionLevel = 0;
        mesh.SubdivisionCageVertices = [];
        mesh.SubdivisionCageIndices = [];
        mesh.SubdivisionCageMaterials = [];
    }

    public static void SetSmoothShading(GModelMesh mesh, bool smooth)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.SubdivisionCageVertices is { Length: > 0 } && mesh.SubdivisionCageIndices is { Length: >= 3 })
        {
            int level = mesh.SubdivisionLevel;
            mesh.SmoothShading = smooth;
            SetSubdivisionLevel(mesh, level);
            return;
        }
        ApplySubdivision(mesh);
        if (smooth)
        {
            ApplySmoothShading(mesh.Vertices, mesh.Indices);
        }
        else
        {
            (MeshVertex[] vertices, ushort[] indices) = FlatGeometry(mesh.Vertices, mesh.Indices);
            mesh.Vertices = vertices;
            mesh.Indices = indices;
            mesh.TriangleMaterialIndices = Materials(mesh.TriangleMaterialIndices, indices.Length / 3, mesh.MaterialIndex);
            mesh.FaceGroups.Clear();
        }
        mesh.SmoothShading = smooth;
        mesh.Tangents = [];
    }

    public static IReadOnlyList<int> ExtrudeFaces(GModelMesh mesh, IEnumerable<int> faces, float distance)
    {
        PrepareTopology(mesh);
        HashSet<int> selected = new HashSet<int>(faces ?? Enumerable.Empty<int>())
            .Where(face => (uint)face < (uint)(mesh.Indices.Length / 3)).ToHashSet();
        if (selected.Count == 0) return [];
        int[] sourceMaterials = Materials(mesh.TriangleMaterialIndices, mesh.Indices.Length / 3, mesh.MaterialIndex);
        List<MeshVertex> vertices = new(mesh.Vertices);
        List<ushort> indices = [];
        List<int> materials = [];
        Dictionary<ushort, Vector3> normals = [];
        Dictionary<ushort, ushort> lifted = [];
        Dictionary<(ushort A, ushort B), (int Count, ushort A, ushort B, int Material)> edges = [];

        foreach (int face in selected)
        {
            ushort a = mesh.Indices[face * 3], b = mesh.Indices[face * 3 + 1], c = mesh.Indices[face * 3 + 2];
            Vector3 normal = SafeNormal(Vector3.Cross(mesh.Vertices[b].Position - mesh.Vertices[a].Position,
                mesh.Vertices[c].Position - mesh.Vertices[a].Position), Vector3.UnitY);
            normals[a] = normals.GetValueOrDefault(a) + normal;
            normals[b] = normals.GetValueOrDefault(b) + normal;
            normals[c] = normals.GetValueOrDefault(c) + normal;
            CountEdge(a, b, sourceMaterials[face]); CountEdge(b, c, sourceMaterials[face]); CountEdge(c, a, sourceMaterials[face]);
        }

        foreach ((ushort old, Vector3 normal) in normals)
        {
            EnsureCapacity(vertices, 1);
            MeshVertex vertex = mesh.Vertices[old];
            vertex.Position += SafeNormal(normal, vertex.Normal) * distance;
            lifted[old] = (ushort)vertices.Count;
            vertices.Add(vertex);
        }

        List<int> top = [];
        for (int face = 0; face < mesh.Indices.Length / 3; face++)
        {
            ushort a = mesh.Indices[face * 3], b = mesh.Indices[face * 3 + 1], c = mesh.Indices[face * 3 + 2];
            if (selected.Contains(face))
            {
                top.Add(materials.Count);
                AddTriangle(indices, materials, lifted[a], lifted[b], lifted[c], sourceMaterials[face]);
            }
            else AddTriangle(indices, materials, a, b, c, sourceMaterials[face]);
        }

        foreach ((int count, ushort a, ushort b, int material) in edges.Values)
            if (count == 1) AddQuad(indices, materials, a, b, lifted[b], lifted[a], material);

        Apply(mesh, vertices, indices, materials);
        return top;

        void CountEdge(ushort a, ushort b, int material)
        {
            (ushort A, ushort B) key = a < b ? (a, b) : (b, a);
            if (edges.TryGetValue(key, out var edge)) edges[key] = (edge.Count + 1, edge.A, edge.B, edge.Material);
            else edges[key] = (1, a, b, material);
        }
    }

    public static IReadOnlyList<int> InsetFaces(GModelMesh mesh, IEnumerable<int> faces, float amount)
    {
        PrepareTopology(mesh);
        HashSet<int> selected = new HashSet<int>(faces ?? Enumerable.Empty<int>())
            .Where(face => (uint)face < (uint)(mesh.Indices.Length / 3)).ToHashSet();
        if (selected.Count == 0) return [];
        float inset = Math.Clamp(amount, 0.01f, 0.92f);
        int[] sourceMaterials = Materials(mesh.TriangleMaterialIndices, mesh.Indices.Length / 3, mesh.MaterialIndex);
        List<MeshVertex> vertices = new(mesh.Vertices);
        List<ushort> indices = [];
        List<int> materials = [];
        List<int> innerFaces = [];
        for (int face = 0; face < mesh.Indices.Length / 3; face++)
        {
            ushort a = mesh.Indices[face * 3], b = mesh.Indices[face * 3 + 1], c = mesh.Indices[face * 3 + 2];
            int material = sourceMaterials[face];
            if (!selected.Contains(face)) { AddTriangle(indices, materials, a, b, c, material); continue; }
            EnsureCapacity(vertices, 3);
            MeshVertex center = Average(mesh.Vertices[a], mesh.Vertices[b], mesh.Vertices[c]);
            ushort ia = AppendLerp(vertices, mesh.Vertices[a], center, inset);
            ushort ib = AppendLerp(vertices, mesh.Vertices[b], center, inset);
            ushort ic = AppendLerp(vertices, mesh.Vertices[c], center, inset);
            innerFaces.Add(materials.Count);
            AddTriangle(indices, materials, ia, ib, ic, material);
            AddQuad(indices, materials, a, b, ib, ia, material);
            AddQuad(indices, materials, b, c, ic, ib, material);
            AddQuad(indices, materials, c, a, ia, ic, material);
        }
        Apply(mesh, vertices, indices, materials);
        return innerFaces;
    }

    public static int BevelEdges(GModelMesh mesh, IEnumerable<(ushort A, ushort B)> edges, float width)
    {
        PrepareTopology(mesh);
        int changed = 0;
        foreach ((ushort a, ushort b) in (edges ?? []).Select(edge => Normalize(edge.A, edge.B)).Distinct().ToArray())
            if (BevelOne(mesh, a, b, MathF.Max(0.0001f, width))) changed++;
        return changed;
    }

    public static int LoopCut(GModelMesh mesh, ushort edgeA, ushort edgeB)
    {
        PrepareTopology(mesh);
        if ((uint)edgeA >= mesh.Vertices.Length || (uint)edgeB >= mesh.Vertices.Length) return 0;
        Vector3 axis = mesh.Vertices[edgeB].Position - mesh.Vertices[edgeA].Position;
        if (axis.LengthSquared() < 1e-10f) return 0;
        axis = Vector3.Normalize(axis);
        HashSet<(ushort A, ushort B)> parallel = [];
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        for (int corner = 0; corner < 3; corner++)
        {
            ushort a = mesh.Indices[i + corner], b = mesh.Indices[i + (corner + 1) % 3];
            Vector3 direction = mesh.Vertices[b].Position - mesh.Vertices[a].Position;
            if (direction.LengthSquared() < 1e-10f) continue;
            if (MathF.Abs(Vector3.Dot(axis, Vector3.Normalize(direction))) >= 0.985f) parallel.Add(Normalize(a, b));
        }
        SplitEdges(mesh, parallel);
        return parallel.Count;
    }

    /// <summary>Refines triangles beneath a sculpt dab until their longest edge meets the requested detail size.</summary>
    public static int RefineUnderBrush(GModelMesh mesh, Vector3 center, float radius, float detailSize)
    {
        PrepareTopology(mesh);
        float radiusSq = radius * radius;
        float thresholdSq = MathF.Max(0.0001f, detailSize * detailSize * 1.25f);
        HashSet<(ushort A, ushort B)> edges = [];
        for (int face = 0; face < mesh.Indices.Length / 3 && edges.Count < 4096; face++)
        {
            ushort a = mesh.Indices[face * 3], b = mesh.Indices[face * 3 + 1], c = mesh.Indices[face * 3 + 2];
            Vector3 centroid = (mesh.Vertices[a].Position + mesh.Vertices[b].Position + mesh.Vertices[c].Position) / 3f;
            if (Vector3.DistanceSquared(centroid, center) > radiusSq) continue;
            (ushort A, ushort B, float Length) longest = Longest(a, b, c);
            if (longest.Length > thresholdSq) edges.Add(Normalize(longest.A, longest.B));
        }
        if (edges.Count == 0) return 0;
        SplitEdges(mesh, edges);
        return edges.Count;

        (ushort A, ushort B, float Length) Longest(ushort a, ushort b, ushort c)
        {
            float ab = Vector3.DistanceSquared(mesh.Vertices[a].Position, mesh.Vertices[b].Position);
            float bc = Vector3.DistanceSquared(mesh.Vertices[b].Position, mesh.Vertices[c].Position);
            float ca = Vector3.DistanceSquared(mesh.Vertices[c].Position, mesh.Vertices[a].Position);
            return ab >= bc && ab >= ca ? (a, b, ab) : bc >= ca ? (b, c, bc) : (c, a, ca);
        }
    }

    /// <summary>Deletes selected topology and compacts every unreferenced vertex.</summary>
    public static int DeleteElements(
        GModelMesh mesh,
        IReadOnlyCollection<int>? selectedVertices,
        IReadOnlyCollection<(ushort A, ushort B)>? selectedEdges,
        IReadOnlyCollection<int>? selectedFaces)
    {
        PrepareTopology(mesh);
        HashSet<int> verticesToDelete = selectedVertices is null ? [] : new HashSet<int>(selectedVertices);
        HashSet<(ushort A, ushort B)> edgesToDelete = selectedEdges is null
            ? [] : selectedEdges.Select(edge => Normalize(edge.A, edge.B)).ToHashSet();
        HashSet<int> facesToDelete = selectedFaces is null ? [] : new HashSet<int>(selectedFaces);
        HashSet<(ushort A, ushort B, ushort C)> faceGeometryToDelete = facesToDelete
            .Where(face => face >= 0 && face * 3 + 2 < mesh.Indices.Length)
            .Select(face => NormalizeTriangle(mesh.Indices[face * 3], mesh.Indices[face * 3 + 1], mesh.Indices[face * 3 + 2]))
            .ToHashSet();
        int[] sourceMaterials = Materials(mesh.TriangleMaterialIndices, mesh.Indices.Length / 3, mesh.MaterialIndex);
        List<(ushort A, ushort B, ushort C, int Material)> kept = [];
        int removed = 0;
        for (int face = 0; face < mesh.Indices.Length / 3; face++)
        {
            ushort a = mesh.Indices[face * 3], b = mesh.Indices[face * 3 + 1], c = mesh.Indices[face * 3 + 2];
            bool remove = facesToDelete.Contains(face) || faceGeometryToDelete.Contains(NormalizeTriangle(a, b, c))
                || verticesToDelete.Contains(a) || verticesToDelete.Contains(b) || verticesToDelete.Contains(c)
                || edgesToDelete.Contains(Normalize(a, b)) || edgesToDelete.Contains(Normalize(b, c)) || edgesToDelete.Contains(Normalize(c, a));
            if (remove) { removed++; continue; }
            kept.Add((a, b, c, sourceMaterials[face]));
        }
        if (removed == 0) return 0;

        HashSet<ushort> used = kept.SelectMany(face => new[] { face.A, face.B, face.C }).ToHashSet();
        Dictionary<ushort, ushort> remap = [];
        List<MeshVertex> vertices = [];
        foreach (ushort source in used.Order())
        {
            if (vertices.Count >= ushort.MaxValue) throw new InvalidOperationException("The compacted mesh exceeds the 65,535 vertex limit.");
            remap[source] = (ushort)vertices.Count;
            vertices.Add(mesh.Vertices[source]);
        }
        List<ushort> indices = [];
        List<int> materials = [];
        foreach ((ushort a, ushort b, ushort c, int material) in kept)
            AddTriangle(indices, materials, remap[a], remap[b], remap[c], material);
        Apply(mesh, vertices, indices, materials);
        return removed;

        static (ushort A, ushort B, ushort C) NormalizeTriangle(ushort a, ushort b, ushort c)
        {
            Span<ushort> values = stackalloc ushort[3] { a, b, c };
            values.Sort();
            return (values[0], values[1], values[2]);
        }
    }

    public static void ApplySmoothShading(MeshVertex[] vertices, ushort[] indices)
    {
        Dictionary<PositionKey, Vector3> accumulated = [];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            ushort a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= vertices.Length || b >= vertices.Length || c >= vertices.Length) continue;
            Vector3 normal = Vector3.Cross(vertices[b].Position - vertices[a].Position, vertices[c].Position - vertices[a].Position);
            Add(a, normal); Add(b, normal); Add(c, normal);
        }
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 normal = accumulated.GetValueOrDefault(PositionKey.From(vertices[i].Position));
            if (normal.LengthSquared() > 1e-12f) vertices[i].Normal = Vector3.Normalize(normal);
        }
        void Add(ushort index, Vector3 normal)
        {
            PositionKey key = PositionKey.From(vertices[index].Position);
            accumulated[key] = accumulated.GetValueOrDefault(key) + normal;
        }
    }

    private static (MeshVertex[] Vertices, ushort[] Indices, int[] Materials, List<SubdivisionFace> Faces) CatmullClarkOnce(
        MeshVertex[] source, ushort[] sourceIndices, int[] sourceMaterials, List<SubdivisionFace>? suppliedFaces)
    {
        Dictionary<PositionKey, int> groupByPosition = [];
        List<List<int>> members = [];
        int[] groupOfVertex = new int[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            PositionKey key = PositionKey.From(source[i].Position);
            if (!groupByPosition.TryGetValue(key, out int group))
            {
                group = members.Count;
                groupByPosition[key] = group;
                members.Add([]);
            }
            members[group].Add(i);
            groupOfVertex[i] = group;
        }

        List<SubdivisionFace> faces = suppliedFaces is null
            ? BuildSubdivisionFaces(source, sourceIndices, sourceMaterials, groupOfVertex, members)
            : suppliedFaces.Select(face => new SubdivisionFace(
                face.Vertices.Select(vertex => groupOfVertex[vertex]).ToArray(), face.Material)).ToList();
        int faceCount = faces.Count;
        MeshVertex[] facePoints = new MeshVertex[faceCount];
        Dictionary<(int A, int B), EdgeInfo> edges = [];
        List<int>[] incidentFaces = Enumerable.Range(0, members.Count).Select(_ => new List<int>()).ToArray();
        for (int face = 0; face < faceCount; face++)
        {
            SubdivisionFace polygon = faces[face];
            facePoints[face] = Average(polygon.Vertices.Select(group => Average(source, members[group])).ToArray());
            for (int corner = 0; corner < polygon.Vertices.Length; corner++)
            {
                int group = polygon.Vertices[corner];
                if (!incidentFaces[group].Contains(face)) incidentFaces[group].Add(face);
                Edge(group, polygon.Vertices[(corner + 1) % polygon.Vertices.Length], face);
            }
        }

        if (members.Count + edges.Count + faceCount > ushort.MaxValue)
            throw new InvalidOperationException("Subdivision would exceed the 65,535 vertex mesh limit. Split the mesh into parts or use a lower level.");

        List<MeshVertex> output = new(members.Count + edges.Count + faceCount);
        for (int group = 0; group < members.Count; group++)
        {
            MeshVertex basis = Average(source, members[group]);
            List<int> adjacentFaces = incidentFaces[group];
            List<EdgeInfo> adjacentEdges = edges.Values.Where(edge => edge.A == group || edge.B == group).ToList();
            List<int> boundary = adjacentEdges.Where(edge => edge.Faces.Count == 1)
                .Select(edge => edge.A == group ? edge.B : edge.A).Distinct().ToList();
            Vector3 point;
            if (boundary.Count >= 2)
            {
                Vector3 n0 = AveragePosition(source, members[boundary[0]]);
                Vector3 n1 = AveragePosition(source, members[boundary[1]]);
                point = (basis.Position * 6f + n0 + n1) / 8f;
            }
            else
            {
                int n = Math.Max(1, adjacentFaces.Count);
                Vector3 f = adjacentFaces.Aggregate(Vector3.Zero, (sum, face) => sum + facePoints[face].Position) / n;
                Vector3 r = adjacentEdges.Count == 0 ? basis.Position : adjacentEdges.Aggregate(Vector3.Zero,
                    (sum, edge) => sum + (AveragePosition(source, members[edge.A]) + AveragePosition(source, members[edge.B])) * 0.5f) / adjacentEdges.Count;
                point = (f + r * 2f + basis.Position * (n - 3f)) / n;
            }
            basis.Position = point;
            output.Add(basis);
        }

        Dictionary<(int A, int B), ushort> edgeIndices = [];
        foreach (KeyValuePair<(int A, int B), EdgeInfo> pair in edges)
        {
            (int A, int B) key = pair.Key;
            EdgeInfo edge = pair.Value;
            MeshVertex a = Average(source, members[edge.A]);
            MeshVertex b = Average(source, members[edge.B]);
            MeshVertex value = Average(a, b);
            if (edge.Faces.Count >= 2)
                value = Average(a, b, facePoints[edge.Faces[0]], facePoints[edge.Faces[1]]);
            edgeIndices[key] = (ushort)output.Count;
            output.Add(value);
        }

        ushort[] faceIndices = new ushort[faceCount];
        for (int face = 0; face < faceCount; face++)
        {
            faceIndices[face] = (ushort)output.Count;
            output.Add(facePoints[face]);
        }

        int cornerCount = faces.Sum(face => face.Vertices.Length);
        List<ushort> indices = new(cornerCount * 6);
        List<int> materials = new(cornerCount * 2);
        List<SubdivisionFace> outputFaces = new(cornerCount);
        for (int face = 0; face < faceCount; face++)
        for (int corner = 0; corner < faces[face].Vertices.Length; corner++)
        {
            SubdivisionFace polygon = faces[face];
            int current = polygon.Vertices[corner];
            int next = polygon.Vertices[(corner + 1) % polygon.Vertices.Length];
            int previous = polygon.Vertices[(corner + polygon.Vertices.Length - 1) % polygon.Vertices.Length];
            ushort edgeNext = edgeIndices[Normalize(current, next)];
            ushort edgePrevious = edgeIndices[Normalize(previous, current)];
            ushort vertex = (ushort)current;
            AddQuad(indices, materials, vertex, edgeNext, faceIndices[face], edgePrevious, polygon.Material);
            outputFaces.Add(new SubdivisionFace([vertex, edgeNext, faceIndices[face], edgePrevious], polygon.Material));
        }
        MeshVertex[] result = output.ToArray();
        ApplySmoothShading(result, indices.ToArray());
        return (result, indices.ToArray(), materials.ToArray(), outputFaces);

        void Edge(int a, int b, int face)
        {
            (int A, int B) key = Normalize(a, b);
            if (!edges.TryGetValue(key, out EdgeInfo? edge)) edges[key] = edge = new EdgeInfo(a, b);
            edge.Faces.Add(face);
        }
    }

    /// <summary>
    /// Recovers quad control-cage faces from the common two-triangle representation. This keeps
    /// an implementation detail (the diagonal used by the renderer) from denting a subdivided
    /// box. Non-coplanar and unmatched triangles remain valid three-sided Catmull-Clark faces.
    /// </summary>
    private static List<SubdivisionFace> BuildSubdivisionFaces(MeshVertex[] source, ushort[] indices,
        int[] materials, int[] groupOfVertex, IReadOnlyList<List<int>> members)
    {
        int triangleCount = indices.Length / 3;
        int[][] triangles = new int[triangleCount][];
        Vector3[] normals = new Vector3[triangleCount];
        Dictionary<(int A, int B), List<int>> edgeFaces = [];
        for (int face = 0; face < triangleCount; face++)
        {
            int[] triangle =
            [
                groupOfVertex[indices[face * 3]],
                groupOfVertex[indices[face * 3 + 1]],
                groupOfVertex[indices[face * 3 + 2]],
            ];
            triangles[face] = triangle;
            Vector3 a = AveragePosition(source, members[triangle[0]]);
            Vector3 b = AveragePosition(source, members[triangle[1]]);
            Vector3 c = AveragePosition(source, members[triangle[2]]);
            normals[face] = SafeNormal(Vector3.Cross(b - a, c - a), Vector3.UnitY);
            AddEdge(triangle[0], triangle[1], face);
            AddEdge(triangle[1], triangle[2], face);
            AddEdge(triangle[2], triangle[0], face);
        }

        bool[] used = new bool[triangleCount];
        List<SubdivisionFace> result = [];
        for (int face = 0; face < triangleCount; face++)
        {
            if (used[face]) continue;
            int partner = -1;
            foreach ((int a, int b) in TriangleEdges(triangles[face]))
            {
                if (!edgeFaces.TryGetValue(Normalize(a, b), out List<int>? adjacent)) continue;
                int candidate = adjacent.FirstOrDefault(index => index != face && !used[index], -1);
                if (candidate < 0 || materials[candidate] != materials[face]
                    || Vector3.Dot(normals[face], normals[candidate]) < .9995f) continue;
                if (triangles[face].Concat(triangles[candidate]).Distinct().Count() != 4) continue;
                partner = candidate;
                break;
            }

            if (partner >= 0 && TryQuadBoundary(triangles[face], triangles[partner], out int[] quad))
            {
                used[face] = used[partner] = true;
                result.Add(new SubdivisionFace(quad, materials[face]));
            }
            else
            {
                used[face] = true;
                result.Add(new SubdivisionFace(triangles[face], materials[face]));
            }
        }
        return result;

        void AddEdge(int a, int b, int face)
        {
            (int A, int B) key = Normalize(a, b);
            if (!edgeFaces.TryGetValue(key, out List<int>? adjacent)) edgeFaces[key] = adjacent = [];
            adjacent.Add(face);
        }
    }

    private static IEnumerable<(int A, int B)> TriangleEdges(int[] triangle)
    {
        yield return (triangle[0], triangle[1]);
        yield return (triangle[1], triangle[2]);
        yield return (triangle[2], triangle[0]);
    }

    private static bool TryQuadBoundary(int[] first, int[] second, out int[] quad)
    {
        Dictionary<(int A, int B), (int Count, int From, int To)> edges = [];
        Add(first); Add(second);
        List<(int From, int To)> boundary = edges.Values.Where(edge => edge.Count == 1)
            .Select(edge => (edge.From, edge.To)).ToList();
        if (boundary.Count != 4) { quad = []; return false; }

        List<int> ordered = [boundary[0].From, boundary[0].To];
        boundary.RemoveAt(0);
        while (boundary.Count > 0)
        {
            int current = ordered[^1];
            int index = boundary.FindIndex(edge => edge.From == current);
            bool reverse = false;
            if (index < 0) { index = boundary.FindIndex(edge => edge.To == current); reverse = true; }
            if (index < 0) { quad = []; return false; }
            (int from, int to) = boundary[index];
            boundary.RemoveAt(index);
            ordered.Add(reverse ? from : to);
        }
        if (ordered.Count != 5 || ordered[^1] != ordered[0]) { quad = []; return false; }
        ordered.RemoveAt(ordered.Count - 1);
        quad = ordered.ToArray();
        return quad.Distinct().Count() == 4;

        void Add(int[] triangle)
        {
            foreach ((int from, int to) in TriangleEdges(triangle))
            {
                (int A, int B) key = Normalize(from, to);
                if (edges.TryGetValue(key, out var edge)) edges[key] = (edge.Count + 1, edge.From, edge.To);
                else edges[key] = (1, from, to);
            }
        }
    }

    private static void SplitEdges(GModelMesh mesh, HashSet<(ushort A, ushort B)> selected)
    {
        if (selected.Count == 0) return;
        int[] sourceMaterials = Materials(mesh.TriangleMaterialIndices, mesh.Indices.Length / 3, mesh.MaterialIndex);
        List<MeshVertex> vertices = new(mesh.Vertices);
        List<ushort> indices = [];
        List<int> materials = [];
        Dictionary<(ushort A, ushort B), ushort> midpoint = [];
        for (int face = 0; face < mesh.Indices.Length / 3; face++)
        {
            ushort a = mesh.Indices[face * 3], b = mesh.Indices[face * 3 + 1], c = mesh.Indices[face * 3 + 2];
            List<ushort> polygon = [a];
            AddMid(a, b); polygon.Add(b); AddMid(b, c); polygon.Add(c); AddMid(c, a);
            if (polygon.Count == 3) { AddTriangle(indices, materials, a, b, c, sourceMaterials[face]); continue; }
            EnsureCapacity(vertices, 1);
            MeshVertex center = Average(polygon.Select(index => vertices[index]).ToArray());
            ushort centerIndex = (ushort)vertices.Count; vertices.Add(center);
            for (int i = 0; i < polygon.Count; i++)
                AddTriangle(indices, materials, centerIndex, polygon[i], polygon[(i + 1) % polygon.Count], sourceMaterials[face]);

            void AddMid(ushort from, ushort to)
            {
                (ushort A, ushort B) key = Normalize(from, to);
                if (!selected.Contains(key)) return;
                if (!midpoint.TryGetValue(key, out ushort value))
                {
                    EnsureCapacity(vertices, 1);
                    value = (ushort)vertices.Count;
                    vertices.Add(Average(vertices[from], vertices[to]));
                    midpoint[key] = value;
                }
                polygon.Add(value);
            }
        }
        Apply(mesh, vertices, indices, materials);
    }

    private static bool BevelOne(GModelMesh mesh, ushort a, ushort b, float width)
    {
        List<int> adjacent = [];
        for (int face = 0; face < mesh.Indices.Length / 3; face++)
        {
            ushort x = mesh.Indices[face * 3], y = mesh.Indices[face * 3 + 1], z = mesh.Indices[face * 3 + 2];
            if ((x == a || y == a || z == a) && (x == b || y == b || z == b)) adjacent.Add(face);
        }
        if (adjacent.Count == 0) return false;
        int[] sourceMaterials = Materials(mesh.TriangleMaterialIndices, mesh.Indices.Length / 3, mesh.MaterialIndex);
        List<MeshVertex> vertices = new(mesh.Vertices);
        Dictionary<int, ushort> insetByFace = [];
        Vector3 midpoint = (mesh.Vertices[a].Position + mesh.Vertices[b].Position) * 0.5f;
        foreach (int face in adjacent.Take(2))
        {
            ushort c = Other(face);
            Vector3 inward = mesh.Vertices[c].Position - midpoint;
            Vector3 edge = mesh.Vertices[b].Position - mesh.Vertices[a].Position;
            inward -= SafeNormal(edge, Vector3.UnitX) * Vector3.Dot(inward, SafeNormal(edge, Vector3.UnitX));
            float max = MathF.Max(0.0001f, edge.Length() * 0.45f);
            MeshVertex value = Average(mesh.Vertices[a], mesh.Vertices[b]);
            value.Position = midpoint + SafeNormal(inward, value.Normal) * MathF.Min(width, max);
            EnsureCapacity(vertices, 1); insetByFace[face] = (ushort)vertices.Count; vertices.Add(value);
        }
        List<ushort> indices = [];
        List<int> materials = [];
        for (int face = 0; face < mesh.Indices.Length / 3; face++)
        {
            ushort x = mesh.Indices[face * 3], y = mesh.Indices[face * 3 + 1], z = mesh.Indices[face * 3 + 2];
            if (!insetByFace.TryGetValue(face, out ushort m)) { AddTriangle(indices, materials, x, y, z, sourceMaterials[face]); continue; }
            ushort c = Other(face);
            bool forward = (x == a && y == b) || (y == a && z == b) || (z == a && x == b);
            if (forward) { AddTriangle(indices, materials, a, m, c, sourceMaterials[face]); AddTriangle(indices, materials, m, b, c, sourceMaterials[face]); }
            else { AddTriangle(indices, materials, b, m, c, sourceMaterials[face]); AddTriangle(indices, materials, m, a, c, sourceMaterials[face]); }
        }
        if (insetByFace.Count == 2)
        {
            ushort[] mids = insetByFace.Values.ToArray();
            AddQuad(indices, materials, a, mids[0], b, mids[1], sourceMaterials[adjacent[0]]);
        }
        Apply(mesh, vertices, indices, materials);
        return true;

        ushort Other(int face)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                ushort value = mesh.Indices[face * 3 + corner];
                if (value != a && value != b) return value;
            }
            return a;
        }
    }

    private static void PrepareTopology(GModelMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.IsSkinned || (mesh.MorphTargets?.Count ?? 0) > 0)
            throw new InvalidOperationException("Topology tools require an unskinned mesh without morph targets.");
        ApplySubdivision(mesh);
    }

    private static void Apply(GModelMesh mesh, List<MeshVertex> vertices, List<ushort> indices, List<int> materials)
    {
        mesh.Vertices = vertices.ToArray();
        mesh.Indices = indices.ToArray();
        mesh.TriangleMaterialIndices = materials.ToArray();
        mesh.FaceGroups.Clear();
        mesh.Tangents = [];
        if (mesh.SmoothShading) ApplySmoothShading(mesh.Vertices, mesh.Indices);
        else RecomputeNormals(mesh.Vertices, mesh.Indices);
    }

    private static (MeshVertex[] Vertices, ushort[] Indices) FlatGeometry(MeshVertex[] source, ushort[] sourceIndices)
    {
        if (sourceIndices.Length > ushort.MaxValue)
            throw new InvalidOperationException("Flat shading would exceed the 65,535 vertex mesh limit.");
        MeshVertex[] vertices = new MeshVertex[sourceIndices.Length];
        ushort[] indices = new ushort[sourceIndices.Length];
        for (int i = 0; i + 2 < sourceIndices.Length; i += 3)
        {
            MeshVertex a = source[sourceIndices[i]], b = source[sourceIndices[i + 1]], c = source[sourceIndices[i + 2]];
            Vector3 normal = SafeNormal(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), Vector3.UnitY);
            a.Normal = b.Normal = c.Normal = normal;
            vertices[i] = a; vertices[i + 1] = b; vertices[i + 2] = c;
            indices[i] = (ushort)i; indices[i + 1] = (ushort)(i + 1); indices[i + 2] = (ushort)(i + 2);
        }
        return (vertices, indices);
    }

    private static int[] Materials(int[]? source, int triangleCount, int fallback)
    {
        if (source is { Length: var count } && count == triangleCount) return (int[])source.Clone();
        int[] result = new int[triangleCount];
        Array.Fill(result, Math.Max(0, fallback));
        return result;
    }

    private static MeshVertex Average(params MeshVertex[] values) => Average((IReadOnlyList<MeshVertex>)values);
    private static MeshVertex Average(IReadOnlyList<MeshVertex> values)
    {
        Vector3 position = default, normal = default;
        Vector4 color = default;
        Vector2 uv = default;
        foreach (MeshVertex value in values) { position += value.Position; normal += value.Normal; color += value.Color; uv += value.UV; }
        float scale = 1f / Math.Max(1, values.Count);
        return new MeshVertex { Position = position * scale, Normal = SafeNormal(normal, Vector3.UnitY), Color = color * scale, UV = uv * scale };
    }

    private static MeshVertex Average(MeshVertex[] source, IReadOnlyList<int> members) => Average(members.Select(index => source[index]).ToArray());
    private static Vector3 AveragePosition(MeshVertex[] source, IReadOnlyList<int> members) => members.Aggregate(Vector3.Zero, (sum, index) => sum + source[index].Position) / Math.Max(1, members.Count);
    private static ushort AppendLerp(List<MeshVertex> vertices, MeshVertex from, MeshVertex to, float amount)
    {
        MeshVertex value = from;
        value.Position = Vector3.Lerp(from.Position, to.Position, amount);
        value.Normal = SafeNormal(Vector3.Lerp(from.Normal, to.Normal, amount), from.Normal);
        value.Color = Vector4.Lerp(from.Color, to.Color, amount);
        value.UV = Vector2.Lerp(from.UV, to.UV, amount);
        ushort index = (ushort)vertices.Count; vertices.Add(value); return index;
    }
    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback) => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : fallback;
    private static (ushort A, ushort B) Normalize(ushort a, ushort b) => a < b ? (a, b) : (b, a);
    private static (int A, int B) Normalize(int a, int b) => a < b ? (a, b) : (b, a);
    private static void EnsureCapacity(List<MeshVertex> vertices, int additional)
    {
        if (vertices.Count + additional > ushort.MaxValue)
            throw new InvalidOperationException("The operation would exceed the 65,535 vertex mesh limit. Split the mesh into parts first.");
    }
    private static void AddTriangle(List<ushort> indices, List<int> materials, ushort a, ushort b, ushort c, int material)
    {
        indices.Add(a); indices.Add(b); indices.Add(c); materials.Add(Math.Max(0, material));
    }
    private static void AddQuad(List<ushort> indices, List<int> materials, ushort a, ushort b, ushort c, ushort d, int material)
    {
        AddTriangle(indices, materials, a, b, c, material);
        AddTriangle(indices, materials, a, c, d, material);
    }
}
