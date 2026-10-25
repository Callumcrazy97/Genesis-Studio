using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
namespace Genesis.Application.Editors.Suite.Assets;

// The view shows the model as an instance showing it is drawn: the model's engine settings
// (Engine.Model.Glow.Strength, Engine.Model.Shadows.Cast, Engine.Model.ScaleY...) are given to a
// stand-in instance by the game's own code, as a room gives them, and the model is drawn with that
// instance's values. Made again only when the settings change.
public partial class ModelViewerControl
{
    private string? _instanceLookKey;
    private (Draw3DComponent Draw, ModelRendererComponent Look) _instanceLook;

    /// <summary>How an instance showing this model is drawn once it has the model's engine settings.</summary>
    internal (Draw3DComponent Draw, ModelRendererComponent Look) InstanceLook()
    {
        IReadOnlyDictionary<string, string>? settings = Asset.EngineSettings;
        string key = settings is { Count: > 0 }
            ? string.Join("\n", settings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value))
            : "";
        if (key == _instanceLookKey) return _instanceLook;
        _instanceLookKey = key;
        var draw = new Draw3DComponent { Visible = true, CastShadows = true };
        var look = new ModelRendererComponent { CastShadows = true, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f };
        if (settings is { Count: > 0 })
        {
            using var scene = new RuntimeScene("Model settings preview");
            Entity entity = scene.World.CreateEntity();
            scene.World.Set(entity, new TransformComponent { ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f });
            scene.World.Set(entity, draw);
            scene.World.Set(entity, look);
            // This view's own game in front while the settings are given, so no other view's is touched.
            var game = new ProjectGameContext(ProjectRoot, scene, null, null, RoomAsset.Create("Model preview", RoomDimension.ThreeD), null);
            var previousGame = PgslCommands.ActiveGameContext;
            string previousPath = PgslCommands.ProjectPath;
            PgslCommands.ActiveGameContext = game;
            PgslCommands.ProjectPath = ProjectRoot;
            try
            {
                PrefabSpawner.ApplyEngineSettings(scene.World, entity, settings, null);
                draw = scene.World.GetRef<Draw3DComponent>(entity);
                look = scene.World.GetRef<ModelRendererComponent>(entity);
            }
            finally
            {
                PgslCommands.ActiveGameContext = previousGame;
                PgslCommands.ProjectPath = previousPath;
            }
        }
        return _instanceLook = (draw, look);
    }
}
