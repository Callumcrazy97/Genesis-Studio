using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Application.Headless.Suites;

internal static class ModelIntakeSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var project = HeadlessHarness.Require(ctx.Project, "Project");
        var resources = HeadlessHarness.Require(ctx.Resources, "Resources");
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.Intake");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Intake.EmptySaveReopenAndImportControls", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Empty model");
            foreach (var role in new[] { ModelEditorRole.Viewer, ModelEditorRole.Composer })
            {
                using var editor = new ModelEditorControl(path, project.RootPath, role);
                Assert(editor.Parts.Count == 0 && editor.BakedVertexCount == 0, "New model created placeholder geometry.");
                using var host = Host(editor);
                var import = Descendants(editor).OfType<ToolStrip>().SelectMany(t => t.Items.Cast<ToolStripItem>())
                    .Single(i => i.Name == "ImportModelFiles");
                Assert(import.Available && import.Owner!.Visible && import.Overflow == ToolStripItemOverflow.Never,
                    "Import Model is hidden or relegated to overflow.");
                Capture(host, ctx, "empty-model-" + role);
                editor.Save();
            }
            using var reopened = new ModelEditorControl(path, project.RootPath);
            Assert(reopened.Parts.Count == 0 && reopened.BakedVertexCount == 0, "Saving/reopening an empty model recreated a cube.");
            Assert(ModelAssetLoader.LoadParts(path).Count == 0 && !StudioModelResourceLoader.Load(path).HasRenderableMeshes,
                "Room or runtime fabricated geometry for an empty model.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Intake.LegacyExplicitPrimitivePreserved", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Legacy primitive");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"primitive\":\"Cube\",\"scale\":2}");
            using var editor = new ModelEditorControl(path, project.RootPath);
            Assert(editor.Parts.Count == 1, "An explicitly authored legacy primitive disappeared.");
            Assert(ModelAssetLoader.LoadParts(path).Count == 1 && StudioModelResourceLoader.Load(path).HasRenderableMeshes,
                "Legacy loading differs between editors and runtime.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Intake.ImportReplacesCurrentResource", () =>
        {
            string source = AnimatedGlbFixture.Write(Path.Combine(ctx.Workspace, "Model intake source"));
            foreach (var role in new[] { ModelEditorRole.Viewer, ModelEditorRole.Composer })
            {
                string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Import host " + role);
                using var editor = new ModelEditorControl(path, project.RootPath, role);
                string original = File.ReadAllText(path), opened = "";
                editor.OpenLinkedResourceRequested += (_, resource) => opened = resource;
                string imported = editor.ImportAndOpenModel(source);
                Assert(opened == "" && imported == path && File.Exists(imported), "Import must fill the current model instead of opening a second resource.");
                Assert(File.ReadAllText(path) != original && !editor.IsDirty, "The imported source was not saved in the current resource.");
                var asset = StudioModelResourceLoader.Load(imported);
                Assert(asset.HasRenderableMeshes && asset.Animations.Count > 0, "Imported geometry or animation was lost.");
                using var reopened = new ModelEditorControl(imported, project.RootPath, role);
                using var host = Host(reopened); reopened.SetSpin(false); reopened.FrameModelForTest();
                // Inspect the flat cloth fixture from +Z for a consistent capture.
                reopened.Viewport.Camera.Yaw = MathF.PI;
                Capture(host, ctx, "imported-model-" + role);
            }
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Intake.ViewerAndEditorRenderExterior", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Closed cube probe");
            var (vertices, indices) = MeshGeometry.BuildCube(RenderColor.White, 2);
            for (int i = 0; i < vertices.Length; i++)
                vertices[i].Color = vertices[i].Normal.Z > .5f ? new Vector4(0, 1, 0, 1)
                    : vertices[i].Normal.Z < -.5f ? new Vector4(1, 0, 0, 1) : new Vector4(.2f, .3f, 1, 1);
            var asset = new GModelAsset { Name = "Closed cube", Meshes = [new() { Vertices = vertices, Indices = indices }],
                Materials = [new() { EmissiveFactor = Vector3.One }] };
            asset.RecalculateBounds();
            StudioModelResourceLoader.SaveCanonical(path, asset);
            foreach (var role in new[] { ModelEditorRole.Viewer, ModelEditorRole.Composer })
            {
                using var editor = new ModelEditorControl(path, project.RootPath, role);
                editor.SetSpin(false); editor.Save();
                using var host = Host(editor);
                editor.Viewport.CameraOverrideFactory = () => new EditorCameraOverride(
                    Matrix4x4.CreateLookAt(new Vector3(0, 0, 4), Vector3.Zero, Vector3.UnitY),
                    Matrix4x4.CreateOrthographic(4, 4, .1f, 20), new Vector3(0, 0, 4), -Vector3.UnitZ);
                foreach (var shading in new[] { ModelPreviewShading.Textured, ModelPreviewShading.Untextured })
                {
                    editor.SetShading(shading);
                    using var frame = editor.Viewport.CaptureFrame(8);
                    Assert(frame is not null, "Model viewport did not render.");
                    Color center = frame!.GetPixel(frame.Width / 2, frame.Height / 2);
                    Assert(center.G > 80 && center.R < 50, $"{role}/{shading} rendered the cube interior: {center}.");
                }
                editor.SetShading(ModelPreviewShading.Textured);
                editor.Viewport.CameraOverrideFactory = null; editor.Viewport.Camera.Yaw = MathF.PI - .55f;
                Capture(host, ctx, "closed-cube-" + role);
            }
            var runtime = StudioModelResourceLoader.Load(path);
            Assert(runtime.Meshes[0].Indices.SequenceEqual(indices) && runtime.WindingOrder == FrontFaceWindingOverride.Default,
                "Saving changed geometry winding or silently forced a model override for the runtime.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Intake.WindingPreferenceMigration", () =>
        {
            string settingsPath = Path.Combine(ctx.Workspace, "winding-settings.json");
            File.WriteAllText(settingsPath, "{\"schemaVersion\":2,\"rendering\":{\"frontFaceWinding\":\"Clockwise\"}}");
            var migrated = new SettingsService(settingsPath);
            Assert(migrated.Current.Rendering.FrontFaceWinding == "CounterClockwise" && migrated.MigrationNotice is not null,
                "Old installation default was not migrated.");
            migrated.Update(s => s.Rendering.FrontFaceWinding = "Clockwise");
            Assert(new SettingsService(settingsPath).Current.Rendering.FrontFaceWinding == "Clockwise", "Current explicit preference was overwritten.");
            Assert(new GenesisSettings().Rendering.FrontFaceWinding == "CounterClockwise" && Mesh3DState.Default.FrontCounterClockwise
                && Genesis.Shared.Rendering.Conventions.FrontCounterClockwise,
                "New installation and renderer defaults disagree.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Intake.MirroredPartNormalsAndWinding", () =>
        {
            foreach (float x in new[] { 1f, -1f })
            {
                var (vertices, indices) = ModelPartBuilder.Bake([new ModelPart { Scale = [x, 2, .5f] }]);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    var a = vertices[indices[i]]; var b = vertices[indices[i + 1]]; var c = vertices[indices[i + 2]];
                    Assert(Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal + b.Normal + c.Normal) > 0,
                        "Mirroring inverted a primitive's surface relative to its normals.");
                }
            }
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Intake.UpwardSurfaceWinding", () =>
        {
            var terrain = new TerrainAsset(3, 3, 1, 0, 0, -1, 1);
            var vertices = new MeshVertex[9];
            TerrainMeshBuilder.BuildVertices(terrain, TerrainMeshBuilder.DefaultLayerColors, vertices);
            AssertUpward(vertices, TerrainMeshBuilder.BuildIndices(3, 3));
            var (floorVertices, floorIndices) = MeshGeometry.BuildFloor(RenderColor.White);
            AssertUpward(floorVertices, floorIndices);
            var (checkerVertices, checkerIndices) = MeshGeometry.BuildCheckerFloor(RenderColor.Black, RenderColor.White, tiles: 3);
            AssertUpward(checkerVertices, checkerIndices);

            static void AssertUpward(MeshVertex[] vertices, ushort[] indices)
            {
                for (int i = 0; i < indices.Length; i += 3)
                {
                    Vector3 a = vertices[indices[i]].Position;
                    Vector3 b = vertices[indices[i + 1]].Position;
                    Vector3 c = vertices[indices[i + 2]].Position;
                    Assert(Vector3.Cross(b - a, c - a).Y > 0, "Terrain or floor triangles face below their upward normals.");
                }
            }
        });
    }

    private static Form Host(ModelEditorControl editor)
    {
        var host = UnattendedWindowing.NewHost(1280, 820);
        host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
        System.Windows.Forms.Application.DoEvents(); return host;
    }
    private static void Capture(Form host, HeadlessContext ctx, string name)
    {
        var editor = host.Controls.OfType<ModelEditorControl>().Single();
        using (var settle = editor.Viewport.CaptureFrame(8)) { }
        editor.FrameModelForTest();
        using (var settled = editor.Viewport.CaptureFrame(4)) { }
        var metrics = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, name + ".png"), includeViewports: true);
        ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
