using System.Drawing;
using System.Numerics;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Physics;
using Genesis.Rendering.Core;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    private static void CheckSpritePhysicsGameplay(HeadlessContext ctx, GateSuite.GateFixture fixture, PhysicsEditorControl editor)
    {
        var project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string Image(string name, bool tiles)
        {
            string path = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, name);
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(path).Document, path, ImageDocumentAccess.Editor);
            session.Document.Origin.Y = 1;
            if (tiles)
            {
                session.Document.Usage.Allowed = ImageUsage.Tileset;
                session.Document.Usage.Tileset.TileWidth = session.Document.Usage.Tileset.TileHeight = 32;
                session.Document.Usage.Tileset.Collision.Add(0);
            }
            ImageWorkspaceStorage.Save(session, ImageWorkspace.CreateBlank(32, 32, tiles ? Color.FromArgb(255, 100, 110, 120) : Color.FromArgb(255, 40, 180, 220)));
            return ResourceNames.Name(project.RootPath, path);
        }
        string sprite = Image("Physics sprite proof", false), floor = Image("Physics floor proof", true);
        HeadlessHarness.Assert(editor.ChooseSpriteImage(sprite), "Physics Quick setup did not choose the saved sprite for its preview.");
        using (Bitmap? preview = editor.Viewport.CaptureFrame(3))
        {
            HeadlessHarness.Assert(preview is not null, "Physics sprite preview did not produce a frame.");
            int imagePixels = 0;
            for (int y = 0; y < preview!.Height; y++) for (int x = 0; x < preview.Width; x++)
            {
                Color pixel = preview.GetPixel(x, y);
                if (pixel.R < 70 && pixel.G > 150 && pixel.B > 190) imagePixels++;
            }
            HeadlessHarness.Assert(imagePixels > 200, "Choosing an Image retained generic collider shapes instead of the saved sprite.");
            string file = "physics-saved-sprite-preview.png"; preview.Save(Path.Combine(ctx.Captures, file));
            ctx.Report.Images.Add(ImageResult.From("Physics saved sprite preview", file, VisualCapture.Measure(preview)));
        }
        string physics = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Physics), ResourceKind.Physics, "Saved sprite physics");
        PhysicsSceneConfig asset = PhysicsScenePresets.Create("2D Platformer");
        asset.Shape = PhysicsBodyShape.Box; asset.BodyType = PhysicsBodyKind.Dynamic;
        asset.LockRotation = true; asset.Restitution = 0; asset.Friction = .35; asset.Density = 2;
        string objectPath;
        using (PhysicsEditorControl author = new(physics, project.RootPath))
        {
            HeadlessHarness.Assert(author.SetDefinition(PhysicsCodeCodec.Serialize(asset)), "The Physics editor rejected the 2D body definition.");
            HeadlessHarness.Assert(author.ChooseSpriteImage(sprite), "Quick setup did not accept the actual sprite Image.");
            objectPath = author.CreatePhysicsObject("Physics sprite Object");
        }
        RoomAsset room = RoomAsset.Create("Physics Room proof", RoomDimension.TwoD);
        RoomNode placed = new() { Name = "Falling sprite", Kind = RoomNodeKind.GameObject,
            GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, objectPath) },
            Transform = new() { X = 128, Y = 64, Z = 12 } };
        room.Nodes.Add(placed);
        RoomTileLayerData tiles = new() { Tileset = floor, CellWidth = 32, CellHeight = 32, CollisionEnabled = true };
        for (int x = 0; x < 24; x++) tiles.Cells.Add(new RoomTileCell { X = x, Y = 6 });
        room.Nodes.Add(new RoomNode { Name = "Solid floor", Kind = RoomNodeKind.TileLayer, TileLayer = tiles });
        using RuntimeScene scene = new("Saved Physics gameplay");
        ProjectGameContext game = new(project.RootPath, scene, null, null, room, null);
        var oldGame = PgslCommands.ActiveGameContext; string oldProject = PgslCommands.ProjectPath;
        PgslContext context = new(); var oldContext = PgslCommands.BindContext(context);
        PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = project.RootPath;
        Entity moving = Entity.Null;
        void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
        try
        {
            RoomBuildResult built = new RoomSceneBuilder(project.RootPath).Build(scene, room);
            moving = built.EntitiesByNodeId[placed.Id];
            Check(scene.Physics is not null && scene.IsSpritePhysicsScene && scene.World.Has<SpritePhysicsBindingComponent>(moving), "Saved 2D Physics did not create a sprite simulation.");
            scene.Physics!.EnableThreadDispatcher = false;
            RigidBodyComponent body = scene.World.GetRef<RigidBodyComponent>(moving);
            Check(body.PlanarTwoD && body.LockRotation && Math.Abs(body.Friction - .35f) < .001f && Math.Abs(body.Mass - 2) < .001f && body.Size == new Vector3(.5f),
                $"Gameplay ignored the authored material or sprite bounds: friction={body.Friction}, mass={body.Mass}, size={body.Size}.");
            PgslCommands.PhysicsSetVelocity(moving.Id, 32, 0, 9999);
            Check(Math.Abs(PgslCommands.PhysicsGetVelocityX(moving.Id) - 32) < .01 && Math.Abs(PgslCommands.PhysicsGetVelocityZ(moving.Id)) < .01, "2D velocity did not use pixels/second or reject depth velocity.");
            for (int i = 0; i < 30; i++) scene.UpdateFixed(1f / 60);
            TransformComponent t = scene.World.GetRef<TransformComponent>(moving);
            Check(t.X > 135 && t.Y > 70 && t.Z == 12, $"The actual Room sprite did not fall and move while retaining draw depth: {t.X},{t.Y},{t.Z}.");
            for (int i = 0; i < 150; i++) scene.UpdateFixed(1f / 60);
            t = scene.World.GetRef<TransformComponent>(moving);
            Check(Math.Abs(t.Y - 192) < 2 && t.Z == 12 && PgslCommands.PhysicsIsGrounded(moving.Id, 8), $"The bottom-origin sprite did not land flush on the tile floor: pivot={t.X},{t.Y}, grounded={PgslCommands.PhysicsIsGrounded(moving.Id, 8)}.");
            Check(Math.Abs(PgslCommands.PhysicsRaycast(512, 96, 999, 0, 1, 0, 200) - 96) < .1 && Math.Abs(PgslCommands.PhysicsRaycastHitY() - 192) < .1 && PgslCommands.PhysicsRaycastHitNormalY() < -.99, "Physics raycasts did not return the tile surface in sprite pixels.");
            CheckSpritePhysicsFrames(ctx, project.RootPath, scene, room, editor, t);
            PgslCommands.PhysicsSetVelocity(moving.Id, 0, -128, 555);
            for (int i = 0; i < 10; i++) scene.UpdateFixed(1f / 60);
            Check(scene.World.GetRef<TransformComponent>(moving).Y < 181 && PgslCommands.PhysicsGetVelocityY(moving.Id) < -60, "Negative sprite Y velocity did not produce a real jump.");
            VMEngine.Initialize(); ScriptHostSystem scriptHost = new(); scriptHost.SetContext(game);
            using (scriptHost.UseEventSources(new Dictionary<string, string> { ["Create"] = "x = 256; y = 96; image_angle = 30; image_xscale = 2; PhysicsSetVelocity(InstanceSelf(), 48, -32, 0);" }))
                scriptHost.Attach(scene.World, moving, "SpritePhysicsTeleport");
            t = scene.World.GetRef<TransformComponent>(moving);
            Check(scriptHost.RecentDiagnostics.Count == 0 && t.X == 256 && t.Y == 96 && Math.Abs(t.Rotation - 30) < .01 && t.ScaleX == 2,
                $"PGSL sprite edits were not retained: {t.X},{t.Y},{t.Rotation},{t.ScaleX}; {string.Join(';', scriptHost.RecentDiagnostics)}.");
            Vector3 expected = SpritePhysicsBinding.Pose(scene.World.GetRef<SpritePhysicsBindingComponent>(moving), t).Position;
            Check(Vector3.Distance(scene.Physics.GetBodyPosition(scene.World.GetRef<RigidBodyComponent>(moving).RegistrationId), expected) < .001f && Math.Abs(PgslCommands.PhysicsGetVelocityX(moving.Id) - 48) < .1,
                $"PGSL sprite editing left the solver body or velocity behind: actual={scene.Physics.GetBodyPosition(scene.World.GetRef<RigidBodyComponent>(moving).RegistrationId)}, expected={expected}, velocity={PgslCommands.PhysicsGetVelocityX(moving.Id)}.");
            scene.UpdateFixed(1f / 60); t = scene.World.GetRef<TransformComponent>(moving);
            Check(t.X > 256 && t.Y < 96 && t.ScaleX == 2 && t.Z == 12 && Math.Abs(t.Rotation - 30) < .1, "The next solver step overwrote the pivot, rotation, scale or draw depth.");
            foreach (PhysicsBodyKind behaviour in new[] { PhysicsBodyKind.Static, PhysicsBodyKind.Kinematic })
            {
                asset.BodyType = behaviour; asset.Friction = .62;
                using (PhysicsEditorControl author = new(physics, project.RootPath))
                {
                    Check(author.SetDefinition(PhysicsCodeCodec.Serialize(asset)), "Body behaviour edit was rejected."); author.Save();
                }
                using RuntimeScene reloaded = new("Edited Physics body");
                ProjectGameContext editedGame = new(project.RootPath, reloaded, null, null, room, null);
                PgslCommands.ActiveGameContext = editedGame;
                Entity placedBody = new RoomSceneBuilder(project.RootPath).Build(reloaded, room).EntitiesByNodeId[placed.Id];
                try
                {
                    Check(Math.Abs(reloaded.World.GetRef<RigidBodyComponent>(placedBody).Friction - .62) < .001, "A saved material edit did not reach Room loading.");
                    PgslCommands.PhysicsSetVelocity(placedBody.Id, 64, 0, 400);
                    for (int i = 0; i < 60; i++) reloaded.UpdateFixed(1f / 60);
                    TransformComponent result = reloaded.World.GetRef<TransformComponent>(placedBody);
                    Check(Math.Abs(result.Y - 64) < .01 && result.Z == 12
                        && Math.Abs(result.X - (behaviour == PhysicsBodyKind.Static ? 128 : 192)) < 1,
                        $"{behaviour} ignored its authored behaviour: {result.X},{result.Y},{result.Z}.");
                    if (behaviour == PhysicsBodyKind.Kinematic)
                    {
                        PgslCommands.PhysicsApplyImpulse(placedBody.Id, 1000, 1000, 1000);
                        Check(Math.Abs(PgslCommands.PhysicsGetVelocityX(placedBody.Id) - 64) < .01, "An impulse moved a Kinematic body.");
                    }
                }
                finally { ObjectDrawAssetRegistry.Remove(placedBody); }
            }
            JObject disabled = JObject.Parse(File.ReadAllText(objectPath));
            disabled["components"]!.OfType<JObject>().Single(component => (string?)component["type"] == "PhysicsComponent")["enabled"] = false;
            File.WriteAllText(objectPath, disabled.ToString()); ResourceNames.Invalidate(project.RootPath);
            using RuntimeScene disabledScene = new("Disabled Physics resource");
            Entity disabledEntity = new RoomSceneBuilder(project.RootPath).Build(disabledScene, room).EntitiesByNodeId[placed.Id];
            try { Check(disabledScene.Physics is null && !disabledScene.World.Has<RigidBodyComponent>(disabledEntity), "A disabled Physics component still enabled the solver."); }
            finally { ObjectDrawAssetRegistry.Remove(disabledEntity); }
        }
        finally
        {
            if (!moving.IsNull) ObjectDrawAssetRegistry.Remove(moving);
            PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldProject;
        }
    }

    private static void CheckSpritePhysicsFrames(HeadlessContext ctx, string project, RuntimeScene scene, RoomAsset room, PhysicsEditorControl editor, TransformComponent landed)
    {
        foreach (RenderBackendOption backend in new[] { RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12, RenderBackendOption.Vulkan, RenderBackendOption.OpenGL, RenderBackendOption.Software })
        {
            editor.Viewport.BackendOverride = backend; GateSuite.Pump(3, 20);
            using (Bitmap? warm = editor.Viewport.CaptureFrame(3)) { }
            IRenderController renderer = editor.Viewport.Host.Renderer ?? throw new InvalidOperationException("No physical Physics renderer for " + backend);
            string name = backend switch { RenderBackendOption.SilkNetDx11 => "Direct3D 11", RenderBackendOption.Direct3D12 => "Direct3D 12", RenderBackendOption.Vulkan => "Vulkan", RenderBackendOption.OpenGL => "OpenGL", _ => "Software" };
            HeadlessHarness.Assert(renderer.BackendName == name, "A different backend rendered the Physics Room: " + renderer.BackendName);
            room.Settings.Width = renderer.PixelWidth; room.Settings.Height = renderer.PixelHeight;
            renderer.ClearPreviewShaderOverride(); renderer.BeginFrame(); renderer.Clear(.02f, .03f, .05f, 1);
            FrameRenderQueue queue = new();
            using (RoomRenderSubsystem presentation = new(project, room))
            {
                presentation.Render2D(scene, renderer, queue); queue.Flush(renderer, includeMeshes: false); renderer.EndFrame();
                HeadlessHarness.Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] bytes), "Physics gameplay has no actual frame readback.");
                using Bitmap bitmap = new(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                for (int row = 0; row < height; row++) System.Runtime.InteropServices.Marshal.Copy(bytes, row * width * 4, data.Scan0 + row * data.Stride, width * 4);
                bitmap.UnlockBits(data);
                string file = "sprite-physics-room-" + backend + ".png"; bitmap.Save(Path.Combine(ctx.Captures, file));
                ctx.Report.Images.Add(ImageResult.From("Saved Physics Object at tile floor · " + name, file, VisualCapture.Measure(bitmap)));
                int top = height, bottom = -1, left = width, right = -1;
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    if (pixel.R < 70 && pixel.G > 150 && pixel.B > 190) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); left = Math.Min(left, x); right = Math.Max(right, x); }
                }
                HeadlessHarness.Assert(Math.Abs(top - (landed.Y - 32)) < 3 && Math.Abs(bottom + 1 - 192) < 3
                    && Math.Abs(left - (landed.X - 16)) < 3 && Math.Abs(right - left + 1 - 32) < 3,
                    $"{name}: the physical sprite pixels disagree with its pivot or tile contact: bounds={left},{top}..{right},{bottom}, pivot={landed.X},{landed.Y}.");
                Color tile = bitmap.GetPixel((int)landed.X, 208);
                HeadlessHarness.Assert(Math.Abs(tile.R - 100) < 3 && Math.Abs(tile.G - 110) < 3 && Math.Abs(tile.B - 120) < 3, name + ": the authored solid floor was not drawn at its collider.");
            }
        }
        editor.Viewport.BackendOverride = RenderBackendOption.SilkNetDx11;
    }
}
