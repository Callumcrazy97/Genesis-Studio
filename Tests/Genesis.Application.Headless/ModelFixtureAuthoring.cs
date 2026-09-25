using System.Numerics;
using System.Text.Json.Nodes;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Headless;

/// <summary>Builds data fixtures for unrelated room/terrain checks without restoring removed editor UI.</summary>
internal static class ModelFixtureAuthoring
{
    public static void ApplyFixtureParts(this ModelEditorControl editor)
    {
        var mesh = ModelPartBuilder.Bake(editor.Parts);
        editor.ApplyAnimationWorkspace(ModelRigBridge.BuildAsset("Fixture", mesh.Vertices, mesh.Indices), "");
    }
    public static ProceduralMeshResult ApplyFixtureTree(this ModelEditorControl editor, ProceduralTreeOptions options)
    {
        var mesh = ProceduralModelGenerator.GenerateTree(options);
        editor.ApplyAnimationWorkspace(ModelRigBridge.BuildAsset("Tree fixture", mesh.Vertices, mesh.Indices), "");
        return mesh;
    }
    public static void ApplyFixtureFlow(this ModelEditorControl editor, Vector2 speed)
    {
        var document = JsonNode.Parse(File.ReadAllText(editor.ResourcePath))!.AsObject();
        document["flowEnabled"] = true; document["flowSpeedX"] = speed.X; document["flowSpeedY"] = speed.Y;
        File.WriteAllText(editor.ResourcePath, document.ToJsonString());
    }
}
