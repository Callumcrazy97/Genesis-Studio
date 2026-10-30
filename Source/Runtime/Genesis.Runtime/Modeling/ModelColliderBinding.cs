using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Modeling;

internal static class ModelColliderBinding
{
    // A model's collision triangles, unscaled, built once per model (and winding): every instance
    // shares them and carries its own scale, so the physics world can share one mesh between them.
    private sealed class SharedGeometry
    {
        public MeshColliderComponent Plain, Mirrored;
        public bool HasPlain, HasMirrored;
        // The model has no triangle a collider may take (every part NOCOL_, say): known once, warned once.
        public bool Empty;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GModelAsset, SharedGeometry> Shared = new();

    /// <summary>
    /// <see cref="AttachGeometry"/> for a model that may have no collision triangles. A model whose
    /// parts are all left out of its collider gets none: false is returned (the caller leaves the
    /// body out too), and a warning naming the model is reported once per model rather than the
    /// game stopping when the model is placed or streamed in.
    /// </summary>
    public static bool TryAttachGeometry(EcsWorld world, Entity entity, GModelAsset asset, Vector3 scale, string modelName = null)
    {
        if (asset == null) return false;
        SharedGeometry shared = Shared.GetValue(asset, _ => new SharedGeometry());
        bool warn = false;
        lock (shared)
        {
            if (!shared.Empty && !shared.HasPlain && !shared.HasMirrored && !ModelCollisionNames.HasColliderTriangles(asset))
            {
                shared.Empty = true;
                warn = true;
            }
        }
        if (warn) ReportEmptyCollider(asset, modelName);
        if (shared.Empty) return false;
        AttachGeometry(world, entity, asset, scale);
        return true;
    }

    private static readonly object WarningGate = new();
    private static readonly List<string> Warnings = new();

    /// <summary>The empty-collider warnings given so far in this process, oldest first (at most 256 kept).</summary>
    public static IReadOnlyList<string> EmptyColliderWarnings
    {
        get { lock (WarningGate) return Warnings.ToArray(); }
    }

    private static void ReportEmptyCollider(GModelAsset asset, string modelName)
    {
        string name = !string.IsNullOrWhiteSpace(modelName) ? modelName : !string.IsNullOrWhiteSpace(asset.Name) ? asset.Name : "(unnamed)";
        string message = ModelCollisionNames.EmptyColliderMessage(name);
        lock (WarningGate)
        {
            if (Warnings.Count >= 256) Warnings.RemoveAt(0);
            Warnings.Add(message);
        }
        System.Console.WriteLine("[Physics] Warning: " + message);
        System.Diagnostics.Trace.TraceWarning(message);
    }

    private static readonly List<string> BodyWarningList = new();

    /// <summary>
    /// The warnings given so far in this process for a placed Object whose Physics could not be made
    /// as asked (a Static body its Model cannot give, a Physics preset that is not one), oldest first
    /// (at most 256 kept).
    /// </summary>
    public static IReadOnlyList<string> BodyWarnings
    {
        get { lock (WarningGate) return BodyWarningList.ToArray(); }
    }

    /// <summary>
    /// An Object's Physics is Static but its Model cannot make the fixed body (its file is missing, it has
    /// no geometry, or none of its parts collide): the Object is placed and drawn without a body, and this says why.
    /// </summary>
    internal static void ReportStaticBodyFailed(string modelName, string reason) =>
        ReportBody($"An Object's Physics is Static, but its Model '{modelName}' cannot make a fixed body: {reason}. The Object is drawn with no collider.");

    /// <summary>A Physics preset the engine does not know (and no 3D Physics resource has the name): the Object is a falling box, as before, but not silently.</summary>
    internal static void ReportUnknownPreset(string preset, string modelName) =>
        ReportBody($"Physics preset '{preset}'{(string.IsNullOrWhiteSpace(modelName) ? "" : $" on an Object with the Model '{modelName}'")} is not one of "
            + "Static, StaticSolid, RigidBody (or Dynamic), Trigger, Projectile, PlatformerCharacter, TopDownCharacter, or a 3D Physics resource: the Object is a dynamic box and falls.");

    private static void ReportBody(string message)
    {
        lock (WarningGate)
        {
            // Each is said once: a room of fifty such Objects would otherwise say it fifty times.
            if (BodyWarningList.Contains(message)) return;
            if (BodyWarningList.Count >= 256) BodyWarningList.RemoveAt(0);
            BodyWarningList.Add(message);
        }
        System.Console.WriteLine("[Physics] Warning: " + message);
        System.Diagnostics.Trace.TraceWarning(message);
    }

    public static void AttachGeometry(EcsWorld world, Entity entity, GModelAsset asset, Vector3 scale)
    {
        using var timed = Genesis.Shared.Assets.LoadClock.Measure(Genesis.Shared.Assets.LoadWork.Collider);
        bool mirrored = scale.X * scale.Y * scale.Z < 0;
        SharedGeometry shared = Shared.GetValue(asset, _ => new SharedGeometry());
        MeshColliderComponent geometry;
        lock (shared)
        {
            if (mirrored ? !shared.HasMirrored : !shared.HasPlain)
            {
                MeshColliderComponent built = Build(asset, mirrored);
                if (mirrored) { shared.Mirrored = built; shared.HasMirrored = true; }
                else { shared.Plain = built; shared.HasPlain = true; }
            }
            geometry = mirrored ? shared.Mirrored : shared.Plain;
        }
        geometry.Scale = scale;
        world.Set(entity, geometry);
    }

    private static MeshColliderComponent Build(GModelAsset asset, bool mirrored)
    {
        List<Vector3> vertices = new(); List<int> indices = new();
        Vector3 pivot = asset.Pivot?.Position ?? Vector3.Zero;
        Matrix4x4[] palette = asset.Rig?.IsValid == true ? GModelPrimitiveFactory.EvaluateBindPosePalette(asset.Rig) : [];
        // COL_/UCX_ meshes are the whole collider when there are any; NOCOL_ meshes are left out.
        foreach (GModelMesh mesh in asset.Meshes.Where((mesh, index) => mesh.Lod == 0 && ModelCollisionNames.InCollider(asset, index)))
        {
            int start = vertices.Count;
            vertices.AddRange(mesh.IsSkinned && mesh.SkinnedVertices.Length > 0
                ? mesh.SkinnedVertices.Select(vertex => GModelAsset.SkinBindPosition(vertex, palette) - pivot)
                : mesh.Vertices.Select(vertex => vertex.Position - pivot));
            foreach (ushort index in mesh.Indices)
                if (index >= vertices.Count - start) throw new InvalidDataException("Model collider has an invalid triangle index.");
            for (int triangle = 0; triangle + 2 < mesh.Indices.Length; triangle += 3)
            {
                // Render meshes are counterclockwise; Bepu expects clockwise exterior faces.
                indices.Add(start + mesh.Indices[triangle]);
                indices.Add(start + mesh.Indices[triangle + (mirrored ? 1 : 2)]);
                indices.Add(start + mesh.Indices[triangle + (mirrored ? 2 : 1)]);
            }
        }
        if (indices.Count == 0) throw new InvalidDataException("A Mesh collider requires saved, renderable model geometry.");
        return new MeshColliderComponent { Vertices = vertices.ToArray(), Indices = indices.ToArray(), Scale = Vector3.One };
    }

    public static float ClosedVolume(MeshColliderComponent geometry)
    {
        double volume = 0;
        for (int triangle = 0; triangle < geometry.Indices.Length; triangle += 3)
            volume += Vector3.Dot(geometry.Vertices[geometry.Indices[triangle]],
                Vector3.Cross(geometry.Vertices[geometry.Indices[triangle + 1]], geometry.Vertices[geometry.Indices[triangle + 2]])) / 6d;
        Vector3 scale = geometry.Scale == Vector3.Zero ? Vector3.One : geometry.Scale;
        return (float)System.Math.Abs(volume * scale.X * scale.Y * scale.Z);
    }
}
