using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Core.Resources;

/// <summary>
/// Seeded trees and rocks as Models, for any editor: the Model editor's Generate adds one as a part,
/// and the Terrain editor's forests make a Model resource of one without the Model editor open.
/// </summary>
public static class GeneratedModels
{
    /// <summary>A generated tree as model geometry: one mesh and the material that shows its colours.</summary>
    public static GModelAsset Tree(ProceduralTreeOptions options) =>
        Geometry(ProceduralModelGenerator.GenerateTree(options), "Tree");

    /// <summary>A generated rock as model geometry: one mesh and the material that shows its colours.</summary>
    public static GModelAsset Rock(ProceduralRockOptions options) =>
        Geometry(ProceduralModelGenerator.GenerateRock(options), "Rock");

    private static GModelAsset Geometry(ProceduralMeshResult result, string kind)
    {
        GModelAsset geometry = new() { Name = kind };
        // The generators write their bark, leaf and stone colours into the vertices; a white,
        // untextured material shows them as they are.
        geometry.Materials.Add(new GModelMaterial { Name = kind, BaseColor = Vector4.One, RoughnessFactor = 0.9f });
        geometry.Meshes.Add(new GModelMesh
        {
            Name = kind,
            MaterialIndex = 0,
            Vertices = result.Vertices,
            Indices = result.Indices,
            IsSkinned = false,
        });
        geometry.RecalculateBounds();
        return geometry;
    }

    /// <summary>
    /// Makes a Model resource in the project's Models folder holding <paramref name="geometry"/>,
    /// saved as the Model editor saves one: the baked model beside a descriptor with no parts.
    /// The name is made unique if it is taken. Returns the new resource's path.
    /// </summary>
    public static string CreateResource(ResourceService resources, string name, GModelAsset geometry)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(geometry);
        if (!geometry.HasRenderableMeshes) throw new ArgumentException("The model has no geometry.", nameof(geometry));
        string path = resources.CreateResource(
            ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Model), ResourceKind.Model, name);
        StudioModelResourceLoader.SaveCanonical(path, geometry);
        JsonObject document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        document["schemaVersion"] = 5;
        document["parts"] = new JsonArray();
        document.Remove("primitive");
        document["rig"] = null;
        document["animations"] = new JsonArray();
        File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
