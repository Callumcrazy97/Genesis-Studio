using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Headless.Suites;

internal static class ModelProfileWorkflowSuite
{
    private static readonly Vector2[] Outline = [new(0, 0), new(4, 0), new(4, 4), new(3, 4), new(3, 1), new(1, 1), new(1, 4), new(0, 4)];

    public static void Run(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ModelProfiles"), "Model profiles");
        ResourceService resources = new(project);
        foreach (string plane in new[] { "XY", "XZ", "YZ" }) foreach (bool reverse in new[] { false, true })
        {
            HeadlessHarness.RunCase(ctx.Report, $"Editor.Model.Profile.Concave.{plane}." + (reverse ? "Reverse" : "Forward"), () =>
            {
                string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, plane + (reverse ? " reverse" : " forward"));
                using ModelEditorControl editor = new(path, project.RootPath); editor.DrawingPlane = plane;
                Vector3[] points = (reverse ? Outline.Reverse() : Outline).Select(p => OnPlane(p, plane)).ToArray();
                for (int i = 0; i < points.Length; i++) Check(editor.CreateConnectedEdge(points[i], points[(i + 1) % points.Length]), "A valid edge was rejected.");
                Check(editor.CanonicalMeshCount == 9, "Closing an outline did not add one face.");
                GModelMesh face = editor.PreviewAsset.Meshes[^1];
                CheckFace(face, plane, 10);
                editor.Undo(); Check(editor.CanonicalMeshCount == 7, "Undo did not remove the closing edge and face together.");
                editor.Redo(); CheckFace(editor.PreviewAsset.Meshes[^1], plane, 10);
                editor.Save(); using ModelEditorControl reopened = new(path, project.RootPath);
                CheckFace(reopened.PreviewAsset.Meshes[^1], plane, 10);
                string gameObject = reopened.CreateModelObject("Drawn " + plane + (reverse ? " reverse" : " forward"));
                Check(ObjectEventStore.Load(gameObject)["Create"].Contains("ModelSet", StringComparison.Ordinal), "Use in game did not write the saved Model command.");
            });
        }
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Profile.InvalidClosureIsAtomicAndUndoRestoresDrawing", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Correcting an outline");
            using ModelEditorControl editor = new(path, project.RootPath);
            Vector3 a = new(0, 0, 0), b = new(4, 4, 0), c = new(0, 4, 0), d = new(4, 0, 0);
            Check(editor.CreateConnectedEdge(a, b) && editor.CreateConnectedEdge(b, c) && editor.CreateConnectedEdge(c, d), "Open edges failed.");
            int materialCount = editor.PreviewAsset.Materials.Count, triangles = editor.BakedTriangleCount;
            Check(!editor.CreateConnectedEdge(d, a) && editor.CanonicalMeshCount == 3
                && editor.PreviewAsset.Materials.Count == materialCount && editor.BakedTriangleCount == triangles, "Crossing closure left a partial edit.");
            editor.Undo(); // Restore the chain ending at c, then close the valid triangle.
            Check(editor.CreateConnectedEdge(c, a) && editor.CanonicalMeshCount == 4, "Undo did not restore the connected drawing chain.");
            Check(Math.Abs(Area(editor.PreviewAsset.Meshes[^1]) - 8) < .001, "The corrected outline produced the wrong face.");
            editor.Undo(); editor.Undo(); editor.Undo();
            Check(editor.CanonicalMeshCount == 0, "The drawing history did not return to the original asset.");
            Vector3[] overlap = [new(0, 0, 0), new(4, 0, 0), new(2, 0, 0), new(2, 2, 0), new(0, 2, 0)];
            for (int i = 0; i < overlap.Length - 1; i++) Check(editor.CreateConnectedEdge(overlap[i], overlap[i + 1]), "Open overlap edge failed.");
            Check(!editor.CreateConnectedEdge(overlap[^1], overlap[0]) && editor.CanonicalMeshCount == 4, "Overlapping profile silently filled a face.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Profile.CollinearCornersRetainValidTopology", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Straight corners");
            using ModelEditorControl editor = new(path, project.RootPath);
            Vector3[] points = [new(0, 0, 0), new(2, 0, 0), new(4, 0, 0), new(4, 4, 0), new(0, 4, 0)];
            for (int i = 0; i < points.Length; i++) Check(editor.CreateConnectedEdge(points[i], points[(i + 1) % points.Length]), "A straight corner prevented a valid face.");
            GModelMesh face = editor.PreviewAsset.Meshes[^1];
            Check(Math.Abs(Area(face) - 16) < .001 && face.Indices.All(i => i < face.Vertices.Length), "Collinear triangulation lost area or wrote invalid indices.");
            for (int i = 0; i < face.Indices.Length; i += 3)
                Check(TriangleNormal(face, i).LengthSquared() > 1e-8f, "A zero-area triangle was emitted.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Profile.PointerDrawAndVisibleTriangleCost", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Pointer outline");
            using ModelEditorControl editor = new(path, project.RootPath);
            using Form host = UnattendedWindowing.NewHost(1440, 900); host.Controls.Add(editor);
            ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host); System.Windows.Forms.Application.DoEvents();
            editor.Viewport.Camera.Target = new(2, 2, 0); editor.Viewport.Camera.Distance = 9;
            editor.Viewport.Camera.Pitch = 0; editor.Viewport.Camera.Yaw = MathF.PI;
            editor.SelectTool(ModelAuthoringTool.Line);
            using (var frame = editor.Viewport.CaptureFrame(3)) { }
            for (int i = 0; i < Outline.Length; i++)
            {
                Point start = At(Outline[i]), end = At(Outline[(i + 1) % Outline.Length]);
                editor.PointerDown(start, MouseButtons.Left); editor.PointerMove(end, MouseButtons.Left); editor.PointerUp(end, MouseButtons.Left);
            }
            Check(editor.CanonicalMeshCount == 9, "The actual pointer path did not close a concave face.");
            CheckFace(editor.PreviewAsset.Meshes[^1], "XY", 10, .12f);
            editor.FrameModel(); using (var frame = editor.Viewport.CaptureFrame(6)) { }
            // Preview ticks update the status as they do during ordinary interactive use. The status
            // line refreshes four times a second, so a fixed 80 ms wait passed only when the drawing
            // above happened to take long enough; wait for the refresh itself, within a second.
            bool StatusShowsCost() => Descendants(editor).OfType<Label>()
                .Any(label => label.Text.StartsWith("Unsaved · 22 triangles", StringComparison.Ordinal));
            DateTime endTime = DateTime.UtcNow.AddMilliseconds(1000);
            while (DateTime.UtcNow < endTime && !StatusShowsCost()) System.Windows.Forms.Application.DoEvents();
            Check(StatusShowsCost(), "The triangle cost is absent from the persistent status bar.");
            string name = "model-drawn-concave-outline";
            var metrics = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, name + ".png"), true);
            ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
            Point At(Vector2 p) { Vector3 projected = editor.Viewport.WorldToSurface(new(p.X, p.Y, 0)); return new((int)MathF.Round(projected.X), (int)MathF.Round(projected.Y)); }
        });
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls) { yield return control; foreach (Control child in Descendants(control)) yield return child; }
    }
    private static Vector3 OnPlane(Vector2 p, string plane) => plane switch { "YZ" => new(0, p.X, p.Y), "XZ" => new(p.X, 0, p.Y), _ => new(p.X, p.Y, 0) };
    private static Vector3 TriangleNormal(GModelMesh mesh, int index) => Vector3.Cross(mesh.Vertices[mesh.Indices[index + 1]].Position - mesh.Vertices[mesh.Indices[index]].Position,
        mesh.Vertices[mesh.Indices[index + 2]].Position - mesh.Vertices[mesh.Indices[index]].Position);
    private static float Area(GModelMesh mesh) => Enumerable.Range(0, mesh.Indices.Length / 3).Sum(i => TriangleNormal(mesh, i * 3).Length() * .5f);
    private static void CheckFace(GModelMesh mesh, string plane, float expectedArea, float tolerance = .001f)
    {
        Vector3 normal = plane switch { "YZ" => Vector3.UnitX, "XZ" => Vector3.UnitY, _ => Vector3.UnitZ };
        Check(Math.Abs(Area(mesh) - expectedArea) < tolerance, "Concave face coverage differs from the drawn outline.");
        Check(!ModelSurfaceBrush.Raycast(mesh.Vertices, mesh.Indices, OnPlane(new(2, 3), plane) + normal * 5, -normal, out _)
            && ModelSurfaceBrush.Raycast(mesh.Vertices, mesh.Indices, OnPlane(new(.5f, 3), plane) + normal * 5, -normal, out _), "The open notch was filled or the solid arm was lost.");
        for (int i = 0; i < mesh.Indices.Length; i += 3) Check(Vector3.Dot(TriangleNormal(mesh, i), normal) > 0, "Triangle winding disagrees with the drawing plane normal.");
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
