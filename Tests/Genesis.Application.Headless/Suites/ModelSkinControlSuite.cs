using System.Drawing;
using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

internal static class ModelSkinControlSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.SkinControl.MidpointTipAndObliqueCamera", () =>
        {
            string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Drag controls");
            var asset = ModelPoseWorkflowSuite.Fixture();
            // A large joint circle crosses the midpoint handle.
            asset.Rig.Bones[1].JointRadius = .8f;
            asset.Rig.Bones.Add(new() { Name = "Finger", ParentIndex = 2, BindLocal = Matrix4x4.CreateTranslation(.2f, 0, 0) });
            GModelPrimitiveFactory.RebuildInverseBindMatrices(asset.Rig);
            StudioModelResourceLoader.SaveCanonical(path, asset);
            using var dialog = new ModelAnimationStudioDialog(path, ctx.Project!.RootPath, asset, 1); Show(dialog);
            var view = dialog.Preview; view.FrameModelForTest();
            var rest = view.CaptureWorkingPose();
            var world = Worlds(asset, rest);
            foreach (float yaw in new[] { MathF.PI, 1.55f, .7f })
            {
                view.Viewport.Camera.Yaw = yaw; view.Viewport.Camera.Pitch = -.25f; Render(dialog);
                // Use a bone which remains visible from this camera (the vertical one for side view).
                int bone = yaw == 1.55f ? 1 : 2; int parent = asset.Rig.Bones[bone].ParentIndex;
                view.SelectAnimationNode(asset.Rig.Bones[bone].Name);
                var midpoint = (world[bone].Translation + world[parent].Translation) * .5f;
                var start = At(dialog, midpoint); var end = new Point(start.X + 18, start.Y + 12);
                Assert(view.DirectPointerDown(start), "Midpoint missed."); view.DirectPointerMove(end);
                var moved = Worlds(asset, view.CaptureWorkingPose());
                Vector3 delta = moved[bone].Translation - world[bone].Translation;
                Near(moved[parent].Translation - world[parent].Translation, delta, "Midpoint rotated instead of translating both endpoints.");
                Near(Vector3.TransformNormal(Vector3.UnitX, moved[bone]), Vector3.TransformNormal(Vector3.UnitX, world[bone]), "Move changed bone rotation.");
                var actual = At(dialog, midpoint + delta);
                Assert(Math.Abs(actual.X - end.X) <= 2 && Math.Abs(actual.Y - end.Y) <= 2 && delta.Length() < .5f, "Oblique camera amplified the mouse movement.");
                view.DirectPointerUp(end); view.Undo(); Assert(view.CaptureWorkingPose().SequenceEqual(rest), "Move undo failed.");
            }
            view.Viewport.Camera.Yaw = MathF.PI; view.Viewport.Camera.Pitch = 0; Render(dialog);
            foreach (bool follow in new[] { false, true })
            {
                view.FollowAnimationChildren = follow; view.SelectAnimationNode("Hand");
                var start = At(dialog, world[2].Translation); var end = At(dialog, world[1].Translation + new Vector3(1.38564f, .8f, 0));
                Assert(view.DirectPointerDown(start), "Tip missed."); view.DirectPointerMove(end); view.DirectPointerUp(end);
                var moved = Worlds(asset, view.CaptureWorkingPose());
                Near(moved[1].Translation, world[1].Translation, "Rotation moved the pivot.");
                Near(Vector3.TransformNormal(Vector3.UnitX, moved[1]), Vector3.UnitX, "Rotating a limb rotated its parent's skin transform.");
                Assert(Math.Abs(Vector3.Distance(moved[2].Translation, moved[1].Translation) - 1.6f) < .001f, "Rotation changed bone length.");
                Assert((Vector3.Distance(moved[3].Translation, world[3].Translation) > .1f) == follow, "Connected-chain switch was ignored.");
                view.Undo(); Assert(view.CaptureWorkingPose().SequenceEqual(rest), "Rotation undo failed.");
            }
            ((System.Windows.Forms.ComboBox)dialog.Controls.Find("RigTransformMode", true).Single()).SelectedIndex = 1;
            var tip = At(dialog, world[2].Translation);
            view.DirectPointerDown(tip); view.DirectPointerMove(new(tip.X + 12, tip.Y - 8));
            var translated = Worlds(asset, view.CaptureWorkingPose());
            Near(translated[2].Translation - world[2].Translation, translated[1].Translation - world[1].Translation, "Explicit Move rotated an endpoint.");
            Assert(view.CancelAnimationGesture() && view.CaptureWorkingPose().SequenceEqual(rest), "Escape did not restore the moved pose.");
            ((System.Windows.Forms.ComboBox)dialog.Controls.Find("RigTransformMode", true).Single()).SelectedIndex = 0;
            Editor3DInspectionSuite.Capture(ctx, dialog, "rig-stable-drag-controls");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.SkinControl.SeparateLimbsWeldsAndRestPose", () =>
        {
            var asset = Strips(); GModelPrimitiveFactory.BindSkinToMesh(asset);
            Assert(asset.LastSkinDiagnostics.Passed, asset.LastSkinDiagnostics.Summary);
            var all = asset.Meshes.SelectMany(m => m.SkinnedVertices).ToArray();
            foreach (var group in all.GroupBy(v => v.Position))
                Assert(group.All(v => v.JointWeights == group.First().JointWeights && v.JointIndices == group.First().JointIndices), "UV/chunk seam has different skin weights.");
            foreach (var vertex in all.Where(v => v.Position.Y < .7f))
            {
                int opposite = vertex.Position.X < 0 ? 4 : 2;
                Assert(Weight(vertex, opposite) == 0, "A nearby disconnected limb influenced the other leg.");
            }
            var locals = ModelPoseWorkflow.BindPose(asset);
            var palette = GModelPrimitiveFactory.EvaluateSkinPalette(asset.Rig, locals);
            foreach (var v in all) Near(Skin(v, palette), v.Position, "Rest pose changed geometry.");
            var worlds = Worlds(asset, locals);
            worlds[2] *= Matrix4x4.CreateTranslation(-worlds[1].Translation) * Matrix4x4.CreateRotationZ(.65f) * Matrix4x4.CreateTranslation(worlds[1].Translation);
            locals = Locals(asset, worlds); palette = GModelPrimitiveFactory.EvaluateSkinPalette(asset.Rig, locals);
            foreach (var v in all.Where(v => v.Position.X > 0)) Near(Skin(v, palette), v.Position, "Left leg pose pulled the right leg.");
            Assert(all.Where(v => v.Position.X < 0 && v.Position.Y < .3f).All(v => Vector3.Distance(Skin(v, palette), v.Position) > .3f), "Selected leg did not deform.");
        });
        string? fox = Environment.GetEnvironmentVariable("GENESIS_MODEL_REVIEW_SOURCE");
        if (!string.IsNullOrWhiteSpace(fox) && File.Exists(fox))
            HeadlessHarness.RunCase(ctx.Report, "Editor.Model.SkinControl.FoxHeadPoseAndPersistence", () => Fox(ctx, fox));
    }

    private static void Fox(HeadlessContext ctx, string source)
    {
        string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Fox skin controls");
        using var editor = new ModelEditorControl(path, ctx.Project!.RootPath); editor.ImportExternalModel(source);
        using var dialog = editor.CreateAnimationStudio(); Show(dialog); dialog.NewSkeleton();
        var bounds = dialog.Result.Bounds; var size = bounds.Size; var min = bounds.Min;
        Vector3 P(float x, float y, float z) => min + new Vector3(x * size.X, y * size.Y, z * size.Z);
        // Authored anatomical layout: hips, chest, neck/head, tail and four independent legs.
        Vector3[] points = [P(.5f,.42f,.38f), P(.5f,.47f,.68f), P(.5f,.62f,.73f), P(.5f,.87f,.73f),
            P(.5f,.5f,.22f), P(.5f,.65f,.05f), P(.28f,.3f,.36f), P(.28f,.02f,.4f),
            P(.72f,.3f,.36f), P(.72f,.02f,.4f), P(.28f,.3f,.69f), P(.28f,.02f,.73f), P(.72f,.3f,.69f), P(.72f,.02f,.73f)];
        int[] parents = [-1,0,1,2,0,4,0,6,0,8,1,10,1,12];
        for (int i = 0; i < points.Length; i++) dialog.AddJointAt(parents[i], points[i]);
        var watch = System.Diagnostics.Stopwatch.StartNew(); dialog.BindMesh(); watch.Stop();
        Console.WriteLine($"Fox surface binding: {watch.ElapsedMilliseconds} ms, {dialog.Result.Meshes.Sum(m => m.Vertices.Length)} vertices.");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(10), "Surface binding stalled.");
        var view = dialog.Preview; view.Viewport.Camera.Yaw = MathF.PI; view.Viewport.Camera.Pitch = -.12f; view.FrameModelForTest(); Render(dialog);
        var asset = view.RiggedAsset!; var rest = view.CaptureWorkingPose();
        var restPalette = GModelPrimitiveFactory.EvaluateSkinPalette(asset.Rig, rest);
        foreach (var v in asset.Meshes.SelectMany(m => m.SkinnedVertices)) Near(Skin(v, restPalette), v.Position, "Fox rest mesh collapsed after binding.");
        dialog.SaveCurrentPose("Rest"); Editor3DInspectionSuite.Capture(ctx, dialog, "rig-fox-surface-rest");
        view.SelectAnimationNode(asset.Rig.Bones[3].Name);
        ((System.Windows.Forms.ComboBox)dialog.Controls.Find("RigPropagation", true).Single()).SelectedIndex = 1;
        var start = At(dialog, points[3]); var rotatedTip = Vector3.Transform(points[3] - points[2], Matrix4x4.CreateRotationZ(.35f)) + points[2];
        Assert(view.DirectPointerDown(start), "Fox head handle missed."); view.DirectPointerMove(At(dialog, rotatedTip)); view.DirectPointerUp(At(dialog, rotatedTip));
        var posed = view.CaptureWorkingPose(); var palette = GModelPrimitiveFactory.EvaluateSkinPalette(asset.Rig, posed);
        Assert(!posed.SequenceEqual(rest), "Fox head did not rotate.");
        var vertices = asset.Meshes.SelectMany(m => m.SkinnedVertices).ToArray();
        foreach (var group in vertices.GroupBy(v => v.Position))
            Assert(group.Select(v => Skin(v, palette)).Distinct().Count() == 1, "Fox split along a UV or mesh-chunk seam.");
        float lowerMovement = vertices.Where(v => v.Position.Y < min.Y + size.Y * .3f).Max(v => Vector3.Distance(Skin(v, palette), v.Position));
        Assert(lowerMovement < size.Length() * .002f, $"Head rotation dragged the lower body ({lowerMovement}).");
        Render(dialog); Editor3DInspectionSuite.Capture(ctx, dialog, "rig-fox-surface-head-pose");
        dialog.SaveCurrentPose("Head tilt"); dialog.ApplyToModel(); editor.ApplyAnimationWorkspace(dialog.Result, dialog.SelectedClip); editor.Save();
        using var reopened = new ModelViewerControl(path, ctx.Project.RootPath);
        Assert(reopened.PreviewAsset.Metadata["genesis.skin.binding"] == "surface-geodesic-1", "Surface binding marker was lost.");
        Assert(reopened.PreviewAsset.Meshes.SelectMany(m => m.SkinnedVertices).SequenceEqual(vertices), "Saved skin weights changed on reopen.");
    }

    private static GModelAsset Strips()
    {
        var asset = new GModelAsset { SkinBindingMode = GModelSkinBindingMode.Smooth4, Bounds = new() { Min = new(-.13f,0,0), Max = new(.13f,1,0) } };
        Vector3[] points = [new(0,1.2f,0),new(-.1f,1,0),new(-.1f,0,0),new(.1f,1,0),new(.1f,0,0)];
        int[] parents = [-1,0,1,0,3];
        for (int i = 0; i < points.Length; i++) asset.Rig.Bones.Add(new() { Name = "Joint " + i, ParentIndex = parents[i], BindLocal = Matrix4x4.CreateTranslation(points[i] - (parents[i] < 0 ? Vector3.Zero : points[parents[i]])) });
        foreach (float x in new[] { -.1f, .1f })
        {
            var vertices = new List<MeshVertex>(); var indices = new List<ushort>();
            for (int row = 0; row < 40; row++)
            {
                int offset = vertices.Count;
                foreach (var p in new[] { new Vector3(x-.03f,row/40f,0), new(x+.03f,row/40f,0),new(x-.03f,(row+1)/40f,0),new(x+.03f,(row+1)/40f,0) })
                    vertices.Add(new() { Position = p, Normal = Vector3.UnitZ, Color = Vector4.One });
                foreach (int i in new[] {0,1,2,1,3,2}) indices.Add((ushort)(offset+i));
            }
            asset.Meshes.Add(new() { Vertices = vertices.ToArray(), Indices = indices.ToArray() });
        }
        GModelPrimitiveFactory.RebuildInverseBindMatrices(asset.Rig); return asset;
    }
    private static float Weight(SkinnedMeshVertex v, int joint) => Enumerable.Range(0,4).Where(i => (int)v.JointIndices[i] == joint).Sum(i => v.JointWeights[i]);
    private static Vector3 Skin(SkinnedMeshVertex v, Matrix4x4[] palette) { var p = Vector3.Zero; for(int i=0;i<4;i++) if(v.JointWeights[i]>0) p += Vector3.Transform(v.Position,palette[(int)v.JointIndices[i]])*v.JointWeights[i]; return p; }
    private static Matrix4x4[] Worlds(GModelAsset asset, Matrix4x4[] locals) => GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, locals);
    private static Matrix4x4[] Locals(GModelAsset asset, Matrix4x4[] worlds) => worlds.Select((w,i) => { int p = asset.Rig.Bones[i].ParentIndex; var inverse = Matrix4x4.Identity; if(p>=0)Matrix4x4.Invert(worlds[p],out inverse); return w*inverse; }).ToArray();
    private static Point At(ModelAnimationStudioDialog d, Vector3 p) { var v=d.Preview.Viewport; var s=v.WorldToSurface(p-d.Preview.RiggedAsset!.Pivot.Position); return new((int)Math.Round(s.X*v.Host.Width/v.SurfaceWidth),(int)Math.Round(s.Y*v.Host.Height/v.SurfaceHeight)); }
    private static void Show(ModelAnimationStudioDialog d) { ThemeService.Apply(d); UnattendedWindowing.Configure(d); UnattendedWindowing.ShowWithoutFocus(d); Editor3DInspectionSuite.Pump(); }
    private static void Render(ModelAnimationStudioDialog d) { Editor3DInspectionSuite.Pump(); using var f=d.Preview.Viewport.CaptureFrame(4); Assert(f is not null,"Viewport failed."); }
    private static void Near(Vector3 a, Vector3 b, string message) => Assert(Vector3.Distance(a,b)<.0001f, message+ $" ({a} vs {b})");
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value,message);
}
