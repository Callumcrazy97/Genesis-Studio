using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Runtime.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The beginner's path, end to end: an empty project, Objects whose behaviour comes only from the
/// one-click recipes, a Room built in the Room editor, and then the Room actually played in the
/// runtime with a key held down.
/// </summary>
/// <remarks>
/// This is the claim "you can make a game with the guided steps" as a test. Nothing here writes
/// PGSL by hand: the player walks because of the arrow-key recipe, the chaser follows because of
/// the chase recipe, and the coin disappears because of the collectible recipe.
/// </remarks>
internal static class BeginnerJourneySuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Beginner journey");
        HeadlessHarness.RunCase(context.Report, "Journey.TwoD.RecipeObjectsInARoomPlayInTheRuntime", () =>
        {
            string root = Path.Combine(context.Workspace, "Journey");
            Directory.CreateDirectory(root);
            ProjectSession project = new ProjectService().CreateProject(root, "Journey 2D", "Blank");
            ResourceService resources = new(project);

            // 1. Draw: a new Image resource is a valid sprite straight away.
            string sprite = resources.CreateResource(
                ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, "Journey Sprite");
            string spriteReference = Path.GetRelativePath(project.RootPath, sprite).Replace('\\', '/');

            // 2. Make Objects: behaviour comes from recipes only.
            string MakeObject(string name, params string[] recipes)
            {
                string path = resources.CreateResource(
                    ResourceFolderPolicy.RootFor(project, ResourceKind.GameObject), ResourceKind.GameObject, name);
                using ObjectEditorControl editor = new(path, project.RootPath);
                HeadlessHarness.Assert(editor.SetSpriteBinding(spriteReference), $"'{name}' could not use the new sprite.");
                foreach (string recipe in recipes)
                {
                    HeadlessHarness.Assert(editor.ApplyBehaviourRecipe(recipe).Count > 0, $"Recipe '{recipe}' added nothing to '{name}'.");
                }

                editor.Save();
                HeadlessHarness.Assert(!editor.IsDirty, $"'{name}' did not save.");
                return path;
            }

            string playerObject = MakeObject("Player", "MoveArrows");
            string chaserObject = MakeObject("Journey Chaser", "Chase");
            string coinObject = MakeObject("Journey Coin", "Collectible");

            // 3. Build a Room: place one of each through the Room editor.
            string roomPath = resources.CreateResource(
                ResourceFolderPolicy.RootFor(project, ResourceKind.Room), ResourceKind.Room, "Journey Room");
            string playerNode, chaserNode, coinNode;
            using (Form host = GateSuite.NewHost(1280, 820))
            using (RoomEditorControl editor = new(roomPath, project.RootPath))
            {
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                GateSuite.Pump(8, 20);
                HeadlessHarness.Assert(!editor.ViewMode3D, "A new Room in a blank project should be 2D.");
                Point centre = new(editor.Viewport.Width / 2, editor.Viewport.Height / 2);
                string Place(string prefab, float x, float y)
                {
                    HeadlessHarness.Assert(editor.DropObjectAt(prefab, centre), $"'{Path.GetFileName(prefab)}' could not be placed in the Room.");
                    RoomNode node = editor.Room.Nodes.Last();
                    node.Transform.X = x;
                    node.Transform.Y = y;
                    return node.Id;
                }

                playerNode = Place(playerObject, 200, 200);
                chaserNode = Place(chaserObject, 400, 200);
                coinNode = Place(coinObject, 212, 200);
                editor.Save();
                HeadlessHarness.Assert(!editor.IsDirty && editor.Room.Nodes.Count == 3, "The Room did not save its three instances.");
            }

            // 4. Press Run: build the saved Room into a live world and hold the Right arrow.
            IGameContext previousContext = PgslCommands.ActiveGameContext;
            string? previousProject = PgslCommands.ProjectPath;
            PgslCommands.ProjectPath = project.RootPath;
            try
            {
                Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);

                ScriptHostSystem scriptHost = new();
                EcsWorld world = new();
                RoomAsset asset = RoomAssetLoader.Parse(roomPath);
                InputState input = new();
                NullGameContext game = new() { World = world, Input = input, Room = asset, ProjectPath = project.RootPath };
                scriptHost.SetContext(game);
                PgslCommands.ActiveGameContext = game;
                RoomBuildResult built = new RoomSceneBuilder(project.RootPath, scriptHost).Build(world, asset);
                HeadlessHarness.Assert(built.EntitiesByNodeId.Count == 3, $"The Room spawned {built.EntitiesByNodeId.Count} instances instead of 3.");

                Entity player = built.EntitiesByNodeId[playerNode];
                Entity chaser = built.EntitiesByNodeId[chaserNode];
                Entity coin = built.EntitiesByNodeId[coinNode];
                float playerStart = world.GetRef<TransformComponent>(player).X;
                float chaserStart = world.GetRef<TransformComponent>(chaser).X;

                input.OnKeyDown(Key.Right);
                for (int frame = 0; frame < 10; frame++)
                {
                    scriptHost.Update(1f / 60f);
                    // The game loop applies queued destroys at the end of each frame
                    // (ScriptHostSubsystem); a hand-stepped world has to do the same.
                    world.FlushDeferred();
                    input.NextFrame();
                }

                float playerNow = world.GetRef<TransformComponent>(player).X;
                HeadlessHarness.Assert(Math.Abs(playerNow - (playerStart + 40)) < 0.5f,
                    $"Holding Right moved the Player from {playerStart} to {playerNow}; the arrow-key recipe should move it 4 pixels a frame.");
                float chaserNow = world.GetRef<TransformComponent>(chaser).X;
                HeadlessHarness.Assert(chaserNow < chaserStart - 10,
                    $"The chaser stayed at {chaserNow} (from {chaserStart}); the chase recipe should move it toward the Player.");
                HeadlessHarness.Assert(!world.IsAlive(coin),
                    "The coin is still in the Room after the Player touched it; the collectible recipe should remove it.");
            }
            finally
            {
                PgslCommands.ActiveGameContext = previousContext;
                PgslCommands.ProjectPath = previousProject;
            }
        });
    }
}
