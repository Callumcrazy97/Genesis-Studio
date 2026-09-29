using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Headless.Suites;

internal static class ModelTubeWorkflowSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "DrawTubes"), "Draw tubes");
        ResourceService resources = new(project);
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Tube.ThreeDGeometryTaperSeamsRigUndoAndSave", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Spatial tube");
            using ModelEditorControl editor = new(path, project.RootPath);
            GModelAsset rigged = GModelPrimitiveFactory.CreateCube("Existing rig", .3f);
            rigged.Rig = new() { Bones = [new() { Name = "Root" }], InverseBindMatrices = [Matrix4x4.Identity] };
            rigged.Animations.Add(new() { Name = "Existing clip", Frames = [new() { LocalBoneTransforms = [Matrix4x4.Identity] }] });
            editor.ApplyAnimationWorkspace(rigged, "Existing clip"); editor.Save();
            Vector3[] points = [new(0, 0, 0), new(0, 1, .5f), new(1, 2, 1), new(2, 2, .5f)];
            editor.CreateTube(points, .4f, .9f);
            Check(editor.CanonicalMeshCount == 2 && editor.PreviewAsset.Rig.Bones.Count == 1 && editor.ActiveClip == "Existing clip", "Drawing damaged existing rig or animation.");
            GModelMesh mesh = editor.PreviewAsset.Meshes[^1];
            Check(mesh.Indices.Length / 3 == 64 && mesh.Indices.All(i => i < mesh.Vertices.Length), "Tube topology or caps are invalid.");
            Check(Math.Abs(Vector3.Distance(mesh.Vertices[0].Position, points[0]) - .2f) < .001
                && Math.Abs(Vector3.Distance(mesh.Vertices[27].Position, points[^1]) - .02f) < .001, "Width or taper was ignored.");
            for (int ring = 0; ring < points.Length; ring++)
                Check(mesh.Vertices[ring * 9].Position == mesh.Vertices[ring * 9 + 8].Position
                    && mesh.Vertices[ring * 9].UV.X == 0 && mesh.Vertices[ring * 9 + 8].UV.X == 1, "The Image UV seam is open or interpolates across the whole texture.");
            for (int i = 0; i < mesh.Indices.Length; i += 3)
            {
                var a = mesh.Vertices[mesh.Indices[i]]; var b = mesh.Vertices[mesh.Indices[i + 1]]; var c = mesh.Vertices[mesh.Indices[i + 2]];
                Check(Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal + b.Normal + c.Normal) > 0,
                    "A tube face winds inward or has zero area.");
            }
            editor.Undo(); Check(editor.CanonicalMeshCount == 1 && editor.ActiveClip == "Existing clip", "Undo did not remove only the tube.");
            editor.Redo(); editor.Save(); using ModelEditorControl reopened = new(path, project.RootPath);
            Check(reopened.PreviewAsset.Meshes[^1].Vertices.SequenceEqual(mesh.Vertices) && reopened.PreviewAsset.Meshes[^1].Indices.SequenceEqual(mesh.Indices), "Saving changed the tube geometry.");
            string objectPath = reopened.CreateModelObject("Tube Object");
            Check(ObjectEventStore.Load(objectPath)["Create"].Contains("ModelSet", StringComparison.Ordinal), "The drawn tube has no saved gameplay link.");
            byte[] before = File.ReadAllBytes(reopened.CanonicalModelPath); bool rejected = false;
            try { reopened.CreateTube([Vector3.Zero, Vector3.Zero], .4f, .5f); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && reopened.CanonicalMeshCount == 2 && !reopened.IsDirty && File.ReadAllBytes(reopened.CanonicalModelPath).SequenceEqual(before), "A zero-length tube left a partial edit.");
        });
        foreach ((int width, float scale) in new[] { (1440, 1f), (1000, 1f), (1440, 2f) })
            HeadlessHarness.RunCase(ctx.Report, $"Editor.Model.Tube.PointerLayout.{width}.Scale{scale}", () =>
            {
                GenesisSettings settings = new(); settings.Appearance.InterfaceScale = scale;
                try
                {
                    ThemeService.ApplySettings(settings);
                    string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, $"Tube {width} {scale}");
                    using ModelEditorControl editor = new(path, project.RootPath);
                    using Form host = UnattendedWindowing.NewHost(width, 900); host.Controls.Add(editor);
                    ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host); System.Windows.Forms.Application.DoEvents();
                    editor.Viewport.Camera.Target = Vector3.Zero; editor.Viewport.Camera.Distance = 7;
                    editor.Viewport.Camera.Pitch = 0; editor.Viewport.Camera.Yaw = MathF.PI;
                    editor.SelectTool(ModelAuthoringTool.Tube); editor.ConfigureTube(.22f, .75f);
                    Control guide = editor.Controls.Find("ModelTubeGuide", true).Single();
                    ((ScrollableControl)guide.Parent!.Parent!.Parent!).ScrollControlIntoView(guide.Parent.Parent);
                    using (var frame = editor.Viewport.CaptureFrame(3)) { }
                    Point[] stroke = Enumerable.Range(0, 40).Select(i => At(new(-1.8f + i * .09f, MathF.Sin(i * .13f) * .8f, 0))).ToArray();
                    editor.PointerDown(stroke[0], MouseButtons.Left);
                    foreach (Point point in stroke.Skip(1)) editor.PointerMove(point, MouseButtons.Left);
                    Check(editor.CanonicalMeshCount == 0 && editor.PendingTubeTriangleCount > 100, "Freehand creation committed before release or collapsed to a snapped line.");
                    editor.SelectTool(ModelAuthoringTool.Select);
                    Check(editor.CanonicalMeshCount == 0 && editor.PendingTubeTriangleCount == 0 && editor.Viewport.NavigationEnabled, "Cancelling a drawing left geometry or blocked navigation.");
                    editor.SelectTool(ModelAuthoringTool.Tube); editor.PointerDown(stroke[0], MouseButtons.Left);
                    foreach (Point point in stroke.Skip(1)) editor.PointerMove(point, MouseButtons.Left);
                    int expectedTriangles = editor.PendingTubeTriangleCount;
                    GateSuite.Pump(3, 20);
                    string drawingName = $"model-tube-stroke-{width}-scale{scale}";
                    var drawingMetrics = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, drawingName + ".png"), true);
                    ctx.Report.Images.Add(ImageResult.From(drawingName, drawingName + ".png", drawingMetrics));
                    editor.PointerUp(stroke[^1], MouseButtons.Left);
                    Check(editor.CanonicalMeshCount == 1 && editor.BakedTriangleCount == expectedTriangles, "Release did not create the drawn tube and reported triangle cost.");
                    editor.FrameModel(); using (var frame = editor.Viewport.CaptureFrame(6)) Check(frame is not null, "Tube native preview is absent.");
                    Check(editor.Controls.Find("ModelTubeWidth", true).Single().Visible && guide.Visible && editor.Viewport.Width > 400,
                        "The drawing controls or useful viewport are hidden.");
                    string name = $"model-drawn-tube-{width}-scale{scale}";
                    var metrics = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, name + ".png"), true);
                    ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
                    editor.PointerDown(stroke[0], MouseButtons.Left); editor.PointerMove(stroke[20], MouseButtons.Left);
                    Check(editor.PendingTubeTriangleCount > 0, "The selected mesh gizmo intercepted drawing another tube.");
                    editor.SelectTool(ModelAuthoringTool.Select); editor.SelectTool(ModelAuthoringTool.Tube);
                    Descendants(editor).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>())
                        .Single(button => button.Name == "ModelGizmoMove").PerformClick();
                    Check(editor.ActiveTool == ModelAuthoringTool.Select, "Move did not switch from drawing to transforming the finished part.");
                    Point At(Vector3 p) { var q = editor.Viewport.WorldToSurface(p); return new((int)q.X, (int)q.Y); }
                }
                finally { settings.Appearance.InterfaceScale = 1; ThemeService.ApplySettings(settings); }
            });
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls) { yield return control; foreach (Control child in Descendants(control)) yield return child; }
    }
}
