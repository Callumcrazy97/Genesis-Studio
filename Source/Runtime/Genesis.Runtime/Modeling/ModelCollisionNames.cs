using System;
using System.Runtime.CompilerServices;

namespace Genesis.Runtime.Modeling;

/// <summary>
/// The usual naming for a model's collision parts, as most modelling tools and engines read it.
/// A mesh (or the node holding it) named <c>COL_…</c> or <c>UCX_…</c> is collision only: it makes
/// the Mesh collider and is never drawn; when a model has any, they are its whole collider. A mesh
/// named <c>NOCOL_…</c> is drawn but left out of the collider (glass, trims, small detail).
/// </summary>
public static class ModelCollisionNames
{
    private sealed class Roles
    {
        public bool[] CollisionOnly = [];
        public bool[] NoCollision = [];
        public bool HasCollisionOnly;
    }

    private static readonly ConditionalWeakTable<GModelAsset, Roles> Cache = new();

    /// <summary>True for a mesh that only makes the collider and is not drawn.</summary>
    public static bool IsCollisionOnly(GModelAsset asset, int meshIndex)
    {
        Roles roles = For(asset);
        return (uint)meshIndex < (uint)roles.CollisionOnly.Length && roles.CollisionOnly[meshIndex];
    }

    /// <summary>Whether a mesh belongs in the model's Mesh collider.</summary>
    public static bool InCollider(GModelAsset asset, int meshIndex)
    {
        Roles roles = For(asset);
        if ((uint)meshIndex >= (uint)roles.CollisionOnly.Length) return false;
        return roles.HasCollisionOnly ? roles.CollisionOnly[meshIndex] : !roles.NoCollision[meshIndex];
    }

    /// <summary>
    /// Whether a Mesh or Convex Hull collider built from the model would have any triangles: false
    /// for a model with no geometry, or whose parts are all left out of the collider (every mesh
    /// named <c>NOCOL_</c>, or only empty <c>COL_</c> meshes).
    /// </summary>
    public static bool HasColliderTriangles(GModelAsset asset)
    {
        if (asset?.Meshes == null) return false;
        for (int index = 0; index < asset.Meshes.Count; index++)
        {
            GModelMesh mesh = asset.Meshes[index];
            if (mesh.Lod == 0 && mesh.Indices is { Length: >= 3 } && InCollider(asset, index)) return true;
        }
        return false;
    }

    /// <summary>Whether the model asks for a Mesh or Convex Hull collider (made from its triangles).</summary>
    public static bool HasTriangleCollider(GModelAsset asset) =>
        asset?.Colliders != null && asset.Colliders.Exists(collider =>
            collider.Shape is GModelColliderShape.Mesh or GModelColliderShape.ConvexHull);

    /// <summary>What the engine and Studio's validation say about a Mesh collider with no triangles.</summary>
    public static string EmptyColliderMessage(string modelName) =>
        $"Model '{modelName}' has a Mesh or Convex Hull collider, but no part of it collides (every part is named NOCOL_, or its COL_ parts have no triangles): the collider is left out.";

    private static Roles For(GModelAsset asset)
    {
        if (asset == null) return new Roles();
        Roles roles = Cache.GetValue(asset, Build);
        // A model edited in place (meshes added or removed) is read again.
        if (roles.CollisionOnly.Length != asset.Meshes.Count)
        {
            Cache.Remove(asset);
            roles = Cache.GetValue(asset, Build);
        }
        return roles;
    }

    private static Roles Build(GModelAsset asset)
    {
        int count = asset.Meshes.Count;
        var roles = new Roles { CollisionOnly = new bool[count], NoCollision = new bool[count] };
        for (int i = 0; i < count; i++)
        {
            GModelMesh mesh = asset.Meshes[i];
            string node = mesh.SourceNodeIndex >= 0 && mesh.SourceNodeIndex < asset.Nodes.Count
                ? asset.Nodes[mesh.SourceNodeIndex].Name
                : null;
            roles.CollisionOnly[i] = IsCollisionName(mesh.Name) || IsCollisionName(node);
            roles.NoCollision[i] = Starts(mesh.Name, "NOCOL_") || Starts(node, "NOCOL_");
            roles.HasCollisionOnly |= roles.CollisionOnly[i];
        }
        return roles;
    }

    private static bool IsCollisionName(string name) => Starts(name, "COL_") || Starts(name, "UCX_");

    private static bool Starts(string name, string prefix) =>
        name != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
