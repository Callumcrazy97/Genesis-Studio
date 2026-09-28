using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

internal static class RoomSpritePlacementSuite
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.SpriteOrigins.MatchGameplayPixelsPickingGhostAndReopen", () => Placement(ctx));
    }

    private static void Placement(HeadlessContext ctx)
    {
        ProjectSession project = new ProjectService().CreateProject(ctx.Workspace, "Sprite placement", TwoDShowcaseTemplate.TemplateId);
        string path = Path.Combine(project.RootPath, TwoDShowcaseTemplate.StartRoom);
        string authored = File.ReadAllText(path);
        using var editor = new RoomEditorControl(path, project.RootPath);
        RoomNode[] targets = editor.Room.Nodes.Where(node => node.Name is "Midpoint flag" or "Summit flag" or "Acorn 1").ToArray();
        Assert(targets.Length == 3, "The production Meadow placement examples are missing.");
        RoomNode[] scene = editor.Room.Nodes.ToArray();
        EcsWorld world = new();
        RoomBuildResult built = new RoomSceneBuilder(project.RootPath).Build(world, editor.Room);
        try
        {
            using var host = GateSuite.NewHost(1380, 900);
            host.Controls.Add(editor); ThemeService.Apply(host); GateSuite.ShowHost(host);
            editor.SetGridVisible(false);
            editor.Navigation.SetSection(RoomNavSection.Objects);
            editor.Placement.TargetLayerId = targets[0].LayerId;
            editor.Navigation.ObjectsPanel.RefreshLayers();
            editor.ActiveRoomEditContextChanged();
            GateSuite.Pump(4, 20);
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                editor.Viewport.BackendOverride = backend.Backend;
                GateSuite.Pump(3, 20);
                using (editor.Viewport.CaptureFrame(3)) { }
                IRenderController renderer = editor.Viewport.Host.Renderer ?? throw new InvalidOperationException("No Room renderer.");
                string expected = backend.Backend switch
                { RenderBackendOption.OpenGL => "OpenGL", RenderBackendOption.Software => "Software", _ => backend.DisplayName };
                Assert(renderer.BackendName == expected, "Room used " + renderer.BackendName + " instead of " + expected + ".");
                foreach (RoomNode target in targets)
                {
                    Entity entity = built.EntitiesByNodeId[target.Id];
                    ref TransformComponent runtime = ref world.GetRef<TransformComponent>(entity);
                    Assert(runtime.X == target.Transform.X && runtime.Y == target.Transform.Y && target.Transform.Y == 288,
                        "The authored floor anchor was moved to compensate for a display mismatch.");
                    ShowOnly(target);
                    editor.Viewport.Camera2DX = target.Transform.X;
                    editor.Viewport.Camera2DY = 252;
                    editor.Viewport.Zoom2D = 2;
                    string id = target.Name.Replace(' ', '-').ToLowerInvariant() + "-" + backend.ShortName.ToLowerInvariant();
                    using Bitmap room = Draw(renderer, target, entity, runtimeInstead: false);
                    using Bitmap game = Draw(renderer, target, entity, runtimeInstead: true);
                    Save(room, "room-origin-" + id); Save(game, "game-origin-" + id);
                    Match(room, game, target.Name + " on " + expected);
                    Vector2 inside = new(target.Transform.X, target.Transform.Y - 12);
                    Vector2 buried = new(target.Transform.X, target.Transform.Y + 8);
                    Assert(editor.HitTestNodes(editor.ClientFromWorld2D(inside)).Contains(target), "Picking missed the visible " + target.Name + ".");
                    Assert(!editor.HitTestNodes(editor.ClientFromWorld2D(buried)).Contains(target), "Picking still uses a centered box below " + target.Name + ".");
                }
                ColourBackground(renderer, backend.ShortName);
                Assert(editor.Viewport.Host.RenderFaultCount == 0, expected + " recorded a render fault: " + editor.Viewport.Host.LastRenderException);
            }

            HeadlessHarness.Step("rotated, mirrored and nonuniformly scaled sprites use gameplay bounds", () =>
            {
                RoomNode target = targets.Single(node => node.Name == "Midpoint flag");
                Entity entity = built.EntitiesByNodeId[target.Id];
                ShowOnly(target);
                target.Transform.ScaleX = -2; target.Transform.ScaleY = .75f; target.Transform.RotationZ = 33;
                ref TransformComponent runtime = ref world.GetRef<TransformComponent>(entity);
                runtime.ScaleX = -2; runtime.ScaleY = .75f; runtime.Rotation = 33;
                editor.Viewport.BackendOverride = RenderBackendOption.SilkNetDx11;
                GateSuite.Pump(3, 20); using (editor.Viewport.CaptureFrame(3)) { }
                editor.Viewport.Camera2DX = target.Transform.X; editor.Viewport.Camera2DY = target.Transform.Y - 12; editor.Viewport.Zoom2D = 2;
                IRenderController renderer = editor.Viewport.Host.Renderer!;
                using Bitmap room = Draw(renderer, target, entity, false), game = Draw(renderer, target, entity, true);
                Save(room, "room-origin-mirrored-rotated"); Save(game, "game-origin-mirrored-rotated");
                Match(room, game, "rotated and mirrored flag");
                Vector2 center = new Vector2(-8, -27);
                float angle = 33 * MathF.PI / 180;
                center = new Vector2(center.X * MathF.Cos(angle) - center.Y * MathF.Sin(angle), center.X * MathF.Sin(angle) + center.Y * MathF.Cos(angle));
                center += new Vector2(target.Transform.X, target.Transform.Y);
                Assert(editor.HitTestNodes(editor.ClientFromWorld2D(center)).Contains(target), "Picking lost the mirrored/rotated visible center.");
                Vector2[] corners = (Vector2[])typeof(RoomEditorControl).GetMethod("NodeCorners2D", Private)!.Invoke(editor, [target])!;
                Assert(Vector2.Distance((corners[0] + corners[2]) * .5f, center) < .01f, "Selection handles differ from the rendered pivot.");
                target.Transform.ScaleX = target.Transform.ScaleY = 1; target.Transform.RotationZ = 0;
                runtime.ScaleX = runtime.ScaleY = 1; runtime.Rotation = 0;
            });

            HeadlessHarness.Step("placement preview and clicking retain the authored bottom pivot", () =>
            {
                RoomNode flag = targets.Single(node => node.Name == "Midpoint flag");
                editor.Room.Nodes.Remove(flag);
                string objectPath = Path.Combine(project.AssetsPath, "Objects", "Checkpoint.object.json");
                editor.BeginPlacement(objectPath);
                Point click = editor.ClientFromWorld2D(new Vector2(flag.Transform.X, 288));
                editor.EditorPointerMove(click, MouseButtons.None, Keys.None);
                IRenderController renderer = editor.Viewport.Host.Renderer!;
                using Bitmap baseline = Draw(renderer, flag, built.EntitiesByNodeId[flag.Id], false);
                using Bitmap ghost = Draw(renderer, flag, built.EntitiesByNodeId[flag.Id], false, ghost: true);
                Save(ghost, "room-origin-bottom-pivot-ghost");
                int changedAbove = 0, changedBelow = 0;
                Vector2 anchor = editor.Viewport.World2DToSurface(new Vector2(flag.Transform.X, 288));
                for (int y = Math.Max(0, (int)anchor.Y - 139); y < Math.Min(ghost.Height, (int)anchor.Y + 60); y++)
                for (int x = Math.Max(0, (int)anchor.X - 19); x < Math.Min(ghost.Width, (int)anchor.X + 35); x++)
                {
                    // Exclude the blue outline and the crosshair at the placement anchor.
                    if (Math.Abs(x - anchor.X) < 5 || Math.Abs(y - anchor.Y) < 5) continue;
                    Color color = ghost.GetPixel(x, y), before = baseline.GetPixel(x, y);
                    if (Math.Abs(color.R - before.R) + Math.Abs(color.G - before.G) + Math.Abs(color.B - before.B) > 6)
                    { if (y < anchor.Y) changedAbove++; else changedBelow++; }
                }
                Assert(changedAbove > 60 && changedBelow == 0, $"The placement ghost is buried or invisible: changed above={changedAbove}, below={changedBelow}.");
                editor.EditorPointerDown(click, MouseButtons.Left, Keys.None); editor.EditorPointerUp(click, MouseButtons.Left, Keys.None);
                RoomNode placed = editor.SelectedNode!;
                Assert(Math.Abs(placed.Transform.Y - 288) < .6f, "Clicking changed the preview's authored ground anchor.");
                Assert(editor.HitTestNodes(editor.ClientFromWorld2D(new Vector2(placed.Transform.X, 276))).Contains(placed), "The placed object differs from the preview.");
                editor.Undo(); editor.CancelPlacement();
            });
            editor.Room.Nodes.Clear(); editor.Room.Nodes.AddRange(scene);
            Assert(File.ReadAllText(path) == authored, "Previewing placement changed the production template document.");
            editor.Save();
            using var reopened = new RoomEditorControl(path, project.RootPath);
            Assert(targets.All(target => reopened.Room.Nodes.Single(node => node.Id == target.Id).Transform.Y == 288), "Save/reopen moved a ground anchor.");
            HeadlessHarness.Step("room settings retain selection and hierarchy layer moves preserve world pose", () =>
            {
                RoomNode flag = targets.Single(node => node.Name == "Midpoint flag");
                editor.Select(flag);
                editor.Navigation.SetSection(RoomNavSection.Settings); GateSuite.Pump(2, 15);
                Assert(editor.SelectedNode == flag && !editor.CanEditNodeInActiveContext(flag),
                    "Room Settings discarded selection or allowed an inactive object gesture.");
                editor.Navigation.SetSection(RoomNavSection.Objects); GateSuite.Pump(2, 15);
                Assert(editor.SelectedNode == flag && editor.CanEditNodeInActiveContext(flag), "Returning to Objects lost selection.");
                RoomNode parent = new() { Name = "Placement test parent", Kind = RoomNodeKind.GameObject, LayerId = flag.LayerId,
                    GameObject = new(), Transform = new RoomTransform { X = flag.Transform.X - 10, Y = 280, ScaleX = 2, ScaleY = 2, ScaleZ = 2 } };
                editor.Room.Nodes.Add(parent);
                Assert(editor.SetNodeParent(flag, parent), "Parenting the placement fixture failed.");
                Vector3 before = editor.GetNodeWorldMatrix(flag).Translation;
                RoomLayer destination = editor.AddRoomLayer("Placement test foreground");
                editor.FlushPendingRoomUiRefresh();
                RoomObjectsPanel panel = editor.Navigation.ObjectsPanel;
                panel.SetInstancesMode(true); panel.RefreshRoomInstances(); GateSuite.Pump(2, 15);
                TreeView tree = panel.InstanceHierarchy;
                TreeNode layerRow = tree.Nodes.Cast<TreeNode>().SelectMany(root => root.Nodes.Cast<TreeNode>())
                    .Single(node => node.Tag == destination);
                layerRow.EnsureVisible(); GateSuite.Pump(2, 15);
                Point dropPoint = new(layerRow.Bounds.Left + 6, layerRow.Bounds.Top + layerRow.Bounds.Height / 2);
                Type dragType = typeof(RoomObjectsPanel).GetNestedType("InstanceDrag", BindingFlags.NonPublic)!;
                DataObject data = new(); data.SetData(dragType, Activator.CreateInstance(dragType, [flag.Id])!);
                Point screen = tree.PointToScreen(dropPoint);
                DragEventArgs drag = new(data, 0, screen.X, screen.Y, DragDropEffects.Move, DragDropEffects.None);
                typeof(TreeView).GetMethod("OnDragOver", Private)!.Invoke(tree, [drag]);
                Assert(drag.Effect == DragDropEffects.Move, "The visible layer row rejects a valid cross-layer drag.");
                Assert(panel.DropHierarchyAt(flag.Id, dropPoint)
                    && flag.ParentId.Length == 0 && flag.LayerId == destination.Id
                    && editor.SelectedNode == flag && panel.ActiveObjectLayer == destination
                    && Vector3.Distance(editor.GetNodeWorldMatrix(flag).Translation, before) < .01f,
                    "Dragging a parented object to another layer lost its world placement.");
                editor.Undo(); editor.FlushPendingRoomUiRefresh();
                Assert(flag.ParentId == parent.Id && flag.LayerId == parent.LayerId
                    && editor.SelectedNode == flag && panel.ActiveObjectLayer?.Id == parent.LayerId
                    && Vector3.Distance(editor.GetNodeWorldMatrix(flag).Translation, before) < .01f,
                    "Layer-move undo did not restore hierarchy and world placement in one operation.");
            });
            host.Close();
        }
        finally { foreach (Entity entity in built.SpawnedEntities) ObjectDrawAssetRegistry.Remove(entity); }

        void ShowOnly(RoomNode target)
        {
            editor.Room.Nodes.Clear();
            editor.Room.Nodes.AddRange(scene.Where(node => node.Kind != RoomNodeKind.GameObject || node == target));
        }
        void ColourBackground(IRenderController renderer, string backend)
        {
            RoomNode[] previous = editor.Room.Nodes.ToArray();
            RoomNode colour = new() { Kind = RoomNodeKind.Background, Background = new() { Asset = string.Empty,
                Layout = RoomBackgroundLayout.StretchView, TintArgb = Color.FromArgb(255, 36, 92, 164).ToArgb() } };
            RoomAsset colourRoom = RoomAsset.Create("Colour background proof", RoomDimension.TwoD);
            colourRoom.Nodes.Add(colour);
            using RuntimeScene runtime = new("Colour background proof");
            using RoomRenderSubsystem presentation = new(project.RootPath, colourRoom);
            try
            {
                editor.Room.Nodes.Clear(); editor.Room.Nodes.Add(colour);
                foreach (bool gameplay in new[] { false, true })
                {
                    renderer.BeginFrame(); renderer.Set3DFrameActive(false); renderer.SetRoomFog(RoomFogState.Disabled);
                    renderer.SetViewport(0, 0, renderer.PixelWidth, renderer.PixelHeight); renderer.Clear(.06f, .1f, .14f, 1);
                    renderer.SetCamera2D(editor.Viewport.Camera2DX, editor.Viewport.Camera2DY, editor.Viewport.Zoom2D, 0);
                    if (gameplay)
                    { FrameRenderQueue queue = new(); presentation.Render2D(runtime, renderer, queue); queue.Flush(renderer, includeMeshes: false); }
                    else typeof(RoomEditorControl).GetMethod("DrawBackgrounds2D", Private)!.Invoke(editor, [renderer]);
                    renderer.EndFrame();
                    Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] bytes), "Colour background had no physical readback.");
                    foreach (Point sample in new[] { new Point(8, 8), new Point(width / 2, height / 2), new Point(width - 8, height - 8) })
                    {
                        int index = (sample.Y * width + sample.X) * 4;
                        Assert(Math.Abs(bytes[index + 2] - 36) < 5 && Math.Abs(bytes[index + 1] - 92) < 5 && Math.Abs(bytes[index] - 164) < 5,
                            $"Colour fill did not cover {(gameplay ? "gameplay" : "Room")} on {backend} at {sample}: {bytes[index + 2]},{bytes[index + 1]},{bytes[index]}.");
                    }
                    using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    for (int y = 0; y < height; y++) Marshal.Copy(bytes, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
                    bitmap.UnlockBits(data); Save(bitmap, (gameplay ? "game" : "room") + "-colour-background-" + backend.ToLowerInvariant());
                }
            }
            finally { editor.Room.Nodes.Clear(); editor.Room.Nodes.AddRange(previous); }
        }

        Bitmap Draw(IRenderController renderer, RoomNode target, Entity entity, bool runtimeInstead, bool ghost = false)
        {
            renderer.BeginFrame(); renderer.Set3DFrameActive(false); renderer.SetRoomFog(RoomFogState.Disabled);
            renderer.SetViewport(0, 0, renderer.PixelWidth, renderer.PixelHeight); renderer.Clear(.06f, .1f, .14f, 1);
            renderer.SetCamera2D(editor.Viewport.Camera2DX, editor.Viewport.Camera2DY, editor.Viewport.Zoom2D, 0);
            if (runtimeInstead) editor.Room.Nodes.Remove(target);
            typeof(RoomEditorControl).GetMethod("DrawRoom2D", Private)!.Invoke(editor, [renderer]);
            if (runtimeInstead)
            {
                FrameRenderQueue queue = new();
                ObjectDrawPass.EnqueueEntity2D(world, entity, project.RootPath, renderer, 0, 0, 1, queue);
                queue.Flush(renderer, includeMeshes: false);
                editor.Room.Nodes.Add(target);
            }
            if (ghost) typeof(RoomEditorControl).GetMethod("DrawPlacementGhost", Private)!.Invoke(editor, [renderer]);
            renderer.EndFrame();
            Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] bytes), "Room/gameplay rendering has no physical readback.");
            Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < height; y++) Marshal.Copy(bytes, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
            bitmap.UnlockBits(data); return bitmap;
        }
        void Save(Bitmap bitmap, string name)
        {
            string file = name + ".png";
            bitmap.Save(Path.Combine(ctx.Captures, file), ImageFormat.Png);
            ctx.Report.Images.Add(ImageResult.From(name, file, VisualCapture.Measure(bitmap)));
        }
    }

    private static void Match(Bitmap room, Bitmap game, string label)
    {
        Assert(room.Size == game.Size, "Room/gameplay output dimensions differ.");
        int differences = 0, content = 0;
        for (int y = 0; y < room.Height; y++)
        for (int x = 0; x < room.Width; x++)
        {
            Color a = room.GetPixel(x, y), b = game.GetPixel(x, y);
            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 6) differences++;
            if (a.R > 130 && a.G > 80 && a.B < 120) content++;
        }
        Assert(content > 60, label + " contains no actual sprite/tile pixels.");
        Assert(differences <= 12, $"Room/gameplay sprite placement differs for {label}: {differences} physical pixels.");
    }

    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
