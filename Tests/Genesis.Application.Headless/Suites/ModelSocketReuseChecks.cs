using System.Numerics;
using Genesis.Runtime;
using Genesis.Runtime.ECS;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless.Suites;

internal static class ModelSocketReuseChecks
{
    public static void Run(string workspace, Action<bool, string> check)
    {
        string root = Path.Combine(workspace, "SocketReuse");
        string otherProject = Path.Combine(workspace, "SocketReuseOther");
        const string first = "Assets/Models/First.model.json";
        const string second = "Assets/Models/Second.model.json";
        static void Save(string project, string reference, float gripX)
        {
            string file = Path.Combine(project, reference);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{}");
            var asset = GModelPrimitiveFactory.CreateCube("Socket model", 1);
            asset.Sockets.Add(new GModelSocket
            {
                Name = "Grip", BoneIndex = -1, NodeIndex = -1,
                LocalTransform = Matrix4x4.CreateTranslation(gripX, 0, 0),
            });
            StudioModelResourceLoader.SaveCanonical(file, asset);
            ResourceCatalog.Invalidate(project);
        }
        Save(root, first, 1);
        Save(root, second, 3);
        Save(otherProject, first, 7);
        using var scene = new RuntimeScene("Socket model reuse");
        var world = scene.World;
        var parent = world.CreateEntity();
        var child = world.CreateEntity();
        world.Set(parent, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        world.Set(parent, new ModelRendererComponent { ModelAsset = first, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        world.Set(parent, new ModelAnimatorComponent { Controller = new AnimationController(), Playing = true, PlaybackSpeed = 1 });
        world.Set(child, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        world.Set(child, new ModelSocketAttachmentComponent
        {
            Enabled = true, ParentEntityId = parent.Id, SocketName = "Grip", LocalOffset = Matrix4x4.Identity,
        });
        string previousProject = PgslCommands.ProjectPath;
        try
        {
            PgslCommands.ProjectPath = root;
            ComponentLifecycle.OnUpdate(world, parent, .016f);
            // The socket registry is deliberately cold. A held item's first activation must
            // succeed even while the already evaluated canonical model cannot be read again.
            using (File.Open(StudioModelResourceLoader.CanonicalPath(Path.Combine(root, first)), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                ModelSocketRuntime.Update(world, root, new RuntimeModelAssetRegistry());
                check(MathF.Abs(world.GetRef<TransformComponent>(child).X - 1) < .001f,
                    "A first attachment did not reuse the model already evaluated for animation.");
            }
            world.GetRef<ModelRendererComponent>(parent).ModelAsset = second;
            ModelSocketRuntime.Update(world, root, new RuntimeModelAssetRegistry());
            check(MathF.Abs(world.GetRef<TransformComponent>(child).X - 3) < .001f,
                "Changing the parent model reused an unrelated cached socket.");
            world.GetRef<ModelRendererComponent>(parent).ModelAsset = first;
            ModelSocketRuntime.Update(world, otherProject, new RuntimeModelAssetRegistry());
            check(MathF.Abs(world.GetRef<TransformComponent>(child).X - 7) < .001f,
                "Socket reuse crossed project boundaries.");
            world.GetRef<ModelAnimatorComponent>(parent).Playing = false;
            Save(root, first, 9);
            ModelSocketRuntime.Update(world, root, new RuntimeModelAssetRegistry());
            check(MathF.Abs(world.GetRef<TransformComponent>(child).X - 9) < .001f,
                "A paused animator prevented an authored socket edit from taking effect.");
        }
        finally { PgslCommands.ProjectPath = previousProject; }
    }
}
