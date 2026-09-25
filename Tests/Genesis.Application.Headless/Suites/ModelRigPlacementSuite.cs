using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Headless.Suites;

internal static class ModelRigPlacementSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.RigPlacement");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigPlacement.NearestPartAcrossSeamsAndChunks", () =>
        {
            var asset = Boxes(new(0, 0, 2), new(0, 0, -2));
            // Each triangle is a separate renderer chunk, with duplicated hits along diagonals.
            var chunks = new List<GModelMesh>();
            foreach (var mesh in asset.Meshes)
                for (int i = 0; i < mesh.Indices.Length; i += 3)
                    chunks.Add(new() { Vertices = mesh.Indices.Skip(i).Take(3).Select(j => mesh.Vertices[j]).ToArray(), Indices = [0, 1, 2, 0, 1, 2] });
            asset.Meshes = chunks;
            foreach (bool reverse in new[] { false, true })
            {
                if (reverse) foreach (var chunk in chunks) Array.Reverse(chunk.Indices);
                var surface = new ModelRigPlacementSurface(asset);
                Assert(surface.TryPick(new(0, 0, 6), -Vector3.UnitZ, Matrix4x4.Identity, out var front), "Front part was missed.");
                Near(front, new(0, 0, 2), "Placement used duplicate surface hits or the far body part.");
                Assert(surface.TryPick(new(0, 0, -6), Vector3.UnitZ, Matrix4x4.Identity, out var back), "Back part was missed.");
                Near(back, new(0, 0, -2), "Placement did not follow the clicked part after orbiting.");
                Assert(surface.TryPick(new(6, 0, 2), -Vector3.UnitX, Matrix4x4.Identity, out var side), "Side was missed.");
                Near(side, front, "Orthogonal rays disagree about the same part's centre.");
            }
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigPlacement.OpenSurfaceAndModelTransform", () =>
        {
            var asset = Boxes(Vector3.Zero);
            var transform = Matrix4x4.CreateTranslation(-1.2f, .4f, -.7f)
                * Matrix4x4.CreateScale(1.5f, .7f, 2) * Matrix4x4.CreateRotationY(.8f);
            var surface = new ModelRigPlacementSurface(asset);
            Assert(surface.TryPick(Vector3.Transform(new(0, 0, 6), transform),
                Vector3.TransformNormal(-Vector3.UnitZ, transform), transform, out var transformed), "Transformed mesh was missed.");
            Near(transformed, Vector3.Zero, "Placement failed to undo model pivot, rotation or scale.");
            // An open foreground face must not pair with the unrelated closed mesh behind it.
            var face = new GModelMesh
            {
                Vertices = [new() { Position = new(-1, -1, 2) }, new() { Position = new(1, -1, 2) }, new() { Position = new(0, 1, 2) }],
                Indices = [0, 1, 2],
            };
            asset.Meshes.Add(face); surface = new ModelRigPlacementSurface(asset);
            Assert(surface.TryPick(new(0, 0, 6), -Vector3.UnitZ, Matrix4x4.Identity, out var open), "Open face was missed.");
            Near(open, new(0, 0, 2), "Open face invented depth from another part.");
            Assert(!surface.TryPick(new(10, 10, 6), -Vector3.UnitZ, Matrix4x4.Identity, out _), "Empty space produced a joint position.");
            Assert(!surface.TryPick(new(0, 0, 6), Vector3.Zero, Matrix4x4.Identity, out _), "Invalid ray was accepted.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigPlacement.PointerDepthRadiusBindingAndReopen", () =>
        {
            string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Mesh depth rig");
            var asset = Boxes(new(-.7f, 0, .8f), new(.7f, 0, -.8f));
            asset.Pivot.Position = new(.3f, -.2f, .1f);
            StudioModelResourceLoader.SaveCanonical(path, asset);
            using var editor = new ModelEditorControl(path, ctx.Project!.RootPath);
            using var dialog = editor.CreateAnimationStudio();
            Show(dialog); dialog.NewSkeleton();
            var preview = dialog.Preview;
            preview.Viewport.Camera.Yaw = MathF.PI; preview.Viewport.Camera.Pitch = 0;
            preview.FrameModelForTest(); Render(dialog);
            Point start = PointAt(dialog, new(-.7f, 0, .8f)), end = PointAt(dialog, new(.7f, 0, -.8f)), miss = new(5, 5);
            Assert(preview.TryPickRigInterior(start, out var startDepth) && preview.TryPickRigInterior(end, out _), "Fixture parts are not pickable.");
            preview.TryPickRigInterior(end, out var endDepth);
            Assert(Math.Abs(startDepth.Z - .8f) < .03f && Math.Abs(endDepth.Z + .8f) < .03f, "Pointer ray did not reach the middle of each part.");
            dialog.BeginDrawingJoints();
            Assert(!preview.DrawingPointerDown(miss), "An empty-space press started a joint.");
            preview.DrawingPointerUp(miss); Assert(dialog.Result.Rig.Bones.Count == 0, "Empty-space click created a floating joint.");
            Assert(preview.DrawingPointerDown(start), "Joint drawing did not start on the mesh.");
            preview.DrawingPointerMove(miss);
            Assert(dialog.Result.Rig.Bones.Count == 0, "Joint committed before release.");
            Capture(dialog, ctx, "rig-interior-radius-preview");
            preview.DrawingPointerUp(miss);
            var placed = dialog.Result.Rig.Bones.Single();
            Near(placed.BindLocal.Translation, startDepth, "Sizing the circle changed its mesh depth.");
            Assert(placed.JointRadius > .05f && float.IsFinite(placed.JointRadius), "Joint radius was not measured at the picked depth.");

            dialog.NewSkeleton(); dialog.BeginDrawingBones();
            preview.DrawingPointerDown(start); preview.DrawingPointerMove(end); preview.DrawingPointerUp(miss);
            Assert(dialog.Result.Rig.Bones.Count == 0 && dialog.StatusText.Contains("not placed"), "Releasing a bone off the mesh committed its stale preview.");
            dialog.BeginDrawingJoints(); preview.DrawingPointerDown(start); preview.DrawingPointerUp(new(start.X + 12, start.Y));
            dialog.BeginDrawingBones(); preview.DrawingPointerDown(start); preview.DrawingPointerMove(end);
            Capture(dialog, ctx, "rig-interior-bone-preview");
            preview.DrawingPointerUp(end);
            Assert(dialog.Result.Rig.Bones.Count == 2, "Starting a bone on the joint did not reuse it.");
            var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(dialog.Result.Rig.Bones, preview.CaptureWorkingPose());
            Near(worlds[0].Translation, startDepth, "Bone start moved away from the mesh joint.");
            Near(worlds[1].Translation, endDepth, "Bone tip reused the start plane instead of its own mesh depth.");
            Capture(dialog, ctx, "rig-interior-front");
            preview.Viewport.Camera.Yaw = -MathF.PI / 2; Render(dialog);
            Capture(dialog, ctx, "rig-interior-side");
            dialog.BindMesh();
            Assert(dialog.SelectedPage == 1 && dialog.Result.Meshes.All(m => m.IsSkinned), "Drawn interior rig did not bind the mesh.");
            dialog.ApplyToModel(); editor.ApplyAnimationWorkspace(dialog.Result, dialog.SelectedClip); editor.Save();
            using var reopened = new ModelViewerControl(path, ctx.Project.RootPath);
            var saved = GModelPrimitiveFactory.ComputeWorldTransforms(reopened.PreviewAsset.Rig.Bones, ModelPoseWorkflow.BindPose(reopened.PreviewAsset));
            Near(saved[0].Translation, startDepth, "Save/reopen lost the first joint depth.");
            Near(saved[1].Translation, endDepth, "Save/reopen lost the bone tip depth.");
        });

        string? fox = Environment.GetEnvironmentVariable("GENESIS_MODEL_REVIEW_SOURCE");
        if (!string.IsNullOrWhiteSpace(fox) && File.Exists(fox))
            HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigPlacement.ProductionFoxOrbit", () =>
            {
                string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Fox mesh depth");
                using var editor = new ModelEditorControl(path, ctx.Project!.RootPath); editor.ImportExternalModel(fox);
                using var dialog = editor.CreateAnimationStudio(); Show(dialog); dialog.NewSkeleton(); dialog.BeginDrawingJoints();
                var preview = dialog.Preview; preview.Viewport.Camera.Yaw = -MathF.PI / 2; preview.Viewport.Camera.Pitch = 0;
                preview.FrameModelForTest(); Render(dialog);
                var bounds = dialog.Result.Bounds; var points = new List<Vector3>();
                // Sample several visible parts, including low limb locations, from the actual source.
                for (int y = preview.Viewport.Host.Height / 4; y < preview.Viewport.Host.Height * 4 / 5; y += 55)
                    for (int x = preview.Viewport.Host.Width / 3; x < preview.Viewport.Host.Width * 3 / 4; x += 65)
                    {
                        var point = new Point(x, y);
                        if (!preview.TryPickRigInterior(point, out var center) || points.Any(p => Vector3.Distance(p, center) < .18f)) continue;
                        Assert(preview.DrawingPointerDown(point), "Fox joint did not start on the hit mesh.");
                        preview.DrawingPointerUp(new(x + 10, y)); points.Add(center);
                    }
                Assert(points.Count >= 4, "Fox fixture did not cover enough visible parts.");
                Assert(points.Max(p => p.X) - points.Min(p => p.X) > bounds.Size.X * .1f, "Fox joints are still on a single side-view plane.");
                var before = preview.CaptureWorkingPose();
                Capture(dialog, ctx, "rig-fox-interior-side");
                preview.Viewport.Camera.Yaw = MathF.PI; preview.Viewport.Camera.Pitch = -.15f;
                Capture(dialog, ctx, "rig-fox-interior-front");
                preview.Viewport.Camera.Yaw = .7f; preview.Viewport.Camera.Pitch = -.25f;
                Capture(dialog, ctx, "rig-fox-interior-orbit");
                Assert(before.SequenceEqual(preview.CaptureWorkingPose()), "Orbiting changed authored joint coordinates.");
                dialog.CancelWork();
            });
    }

    private static GModelAsset Boxes(params Vector3[] centers)
    {
        var mesh = ModelPartBuilder.Bake(centers.Select(p => new ModelPart
            { Position = [p.X, p.Y, p.Z], Scale = [.6f, 1, .4f], Color = [.9f, .55f, .2f] }).ToArray());
        var asset = ModelRigBridge.BuildAsset("Depth fixture", mesh.Vertices, mesh.Indices);
        asset.Materials.Add(new() { Name = "Fixture", BaseColor = Vector4.One });
        return asset;
    }
    private static Point PointAt(ModelAnimationStudioDialog dialog, Vector3 model)
    {
        var viewport = dialog.Preview.Viewport;
        var p = viewport.WorldToSurface(model - dialog.Preview.RiggedAsset!.Pivot.Position);
        return new((int)Math.Round(p.X * viewport.Host.Width / viewport.SurfaceWidth),
            (int)Math.Round(p.Y * viewport.Host.Height / viewport.SurfaceHeight));
    }
    private static void Show(ModelAnimationStudioDialog dialog)
    {
        ThemeService.Apply(dialog); UnattendedWindowing.Configure(dialog); UnattendedWindowing.ShowWithoutFocus(dialog);
        System.Windows.Forms.Application.DoEvents();
    }
    private static void Render(ModelAnimationStudioDialog dialog)
    {
        System.Windows.Forms.Application.DoEvents();
        using var frame = dialog.Preview.Viewport.CaptureFrame(4);
        Assert(frame is not null, "Rig viewport failed to render.");
    }
    private static void Capture(ModelAnimationStudioDialog dialog, HeadlessContext ctx, string name)
    {
        Render(dialog);
        var metrics = VisualCapture.CaptureOpenForm(dialog, Path.Combine(ctx.Captures, name + ".png"), true);
        ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
    }
    private static void Near(Vector3 actual, Vector3 expected, string message) => Assert(Vector3.Distance(actual, expected) < .001f, $"{message} Expected {expected}, got {actual}.");
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
