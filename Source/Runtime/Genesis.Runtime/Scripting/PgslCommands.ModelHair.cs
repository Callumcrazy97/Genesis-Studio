#nullable enable
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Shared.ECS;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    private static RuntimeModelAssetRegistry HairModelAssets => RuntimeModelAssetRegistry.Shared;
    private static string _modelHairError = "";

    [PgslCommand("ModelHairSetStyles", "ModelHairSetStyles(scalpStyle, facialStyle) -> bool",
        "Choose this model's authored scalp and beard styles; None hides that category. Model Editor > Edit > Hair styles authors the choices.", "Models")]
    public static bool ModelHairSetStyles(string scalpStyle, string facialStyle)
    {
        if (!TryHairInstance(out var world, out Entity entity)) return false;
        ref ModelRendererComponent renderer = ref world!.GetRef<ModelRendererComponent>(entity);
        return ModelHairRuntime.TrySetStyles(ref renderer, HairModelAssets.Load(ProjectPath, renderer.ModelAsset),
            scalpStyle, facialStyle, out _modelHairError);
    }

    [PgslCommand("ModelHairSetColor", "ModelHairSetColor(red, green, blue) -> bool",
        "Set scalp and eyebrow base colour (0..1 RGB); use a neutral hair albedo for predictable recolouring.", "Models")]
    public static bool ModelHairSetColor(double red, double green, double blue) => SetModelHairColor(false, red, green, blue);

    [PgslCommand("ModelBeardSetColor", "ModelBeardSetColor(red, green, blue) -> bool",
        "Set facial-hair base colour independently of scalp hair (0..1 RGB).", "Models")]
    public static bool ModelBeardSetColor(double red, double green, double blue) => SetModelHairColor(true, red, green, blue);

    [PgslCommand("ModelHairReset", "ModelHairReset() -> bool",
        "Restore model-authored hair defaults and material colours, preserving other mesh masks and material tints.", "Models")]
    public static bool ModelHairReset()
    {
        if (!TryHairInstance(out var world, out Entity entity)) return false;
        world!.GetRef<ModelRendererComponent>(entity).Hair = null;
        _modelHairError = "";
        return true;
    }

    [PgslCommand("ModelHairLastError", "ModelHairLastError() -> string",
        "Explain the most recent failed hair command; empty after success.", "Models")]
    public static string ModelHairLastError() => _modelHairError;

    private static bool SetModelHairColor(bool facial, double red, double green, double blue)
    {
        if (!TryHairInstance(out var world, out Entity entity)) return false;
        ref ModelRendererComponent renderer = ref world!.GetRef<ModelRendererComponent>(entity);
        return ModelHairRuntime.TrySetColor(ref renderer, HairModelAssets.Load(ProjectPath, renderer.ModelAsset), facial,
            new Vector3((float)red, (float)green, (float)blue), out _modelHairError);
    }

    private static bool TryHairInstance(out Genesis.Runtime.ECS.World? world, out Entity entity)
    {
        world = ActiveGameContext?.World;
        entity = Entity.Null;
        PgslContext? context = GetContext();
        if (context is not null && world is not null)
        {
            entity = world.GetEntity(context.InstanceId);
            if (world.IsAlive(entity) && world.Has<ModelRendererComponent>(entity)
                && !string.IsNullOrWhiteSpace(world.GetRef<ModelRendererComponent>(entity).ModelAsset)) return true;
        }
        _modelHairError = "This instance needs a model renderer.";
        return false;
    }
}
