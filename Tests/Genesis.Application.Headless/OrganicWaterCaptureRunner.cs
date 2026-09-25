using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Studio.Theme;
using Genesis.World.Terrain;
using Genesis.World.Water;

namespace Genesis.Application.Headless;

/// <summary>Capture-only review of the organic mesh and terrain water authoring workspaces.</summary>
internal static class OrganicWaterCaptureRunner
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        Genesis.Rendering.Core.EditorPreviewSettings.Configure(vsync: false);
        ProjectSession project = new ProjectService().CreateProject(
            Path.Combine(output, "Workspace"), "Organic and water review", "Blank", overwrite: true);
        ResourceService resources = new(project);

        string modelPath = resources.CreateResource(
            Path.Combine(resources.AssetsRoot, "Models"), ResourceKind.Model, "Organic Surface Review");
        using (ModelEditorControl seed = new(modelPath, project.RootPath))
        {
            while (seed.Parts.Count > 0)
            {
                seed.SelectPart(0);
                seed.DeleteSelectedPartForTest();
            }
            ModelPart body = seed.AddPart(ModelPrimitiveKind.Cube);
            body.Name = "Control Cage";
            body.Scale = [2.4f, 1.5f, 1.8f];
            body.Color = [.19f, .58f, .78f];
            ModelPart detail = seed.AddPart(ModelPrimitiveKind.Sphere);
            detail.Name = "Organic Detail";
            detail.Position = [0f, 1.55f, 0f];
            detail.Scale = [.9f, .9f, .9f];
            detail.Color = [.82f, .46f, .2f];
            seed.Save();
        }

        string terrainPath = resources.CreateResource(
            Path.Combine(resources.AssetsRoot, "Terrain"), ResourceKind.Terrain, "River and Basin Review");
        TerrainAsset terrain = TerrainAsset.CreateDefaultForestGlade();
        List<WaterSplinePoint> points =
        [
            new() { Position = new(-20f, 0f, 56f), Width = 4.2f, Depth = .8f },
            new() { Position = new(-10f, 0f, 48f), Width = 5.2f, Depth = 1.1f },
            new() { Position = new(2f, 0f, 38f), Width = 6.1f, Depth = 1.35f },
            new() { Position = new(12f, 0f, 24f), Width = 7.4f, Depth = 1.7f },
        ];
        TerrainWaterDefinition river = TerrainRiverSystem.CreateRiver(terrain, points, true, 1.1f);
        river.Name = "Review River";
        TerrainRiverSystem.CarveRiverbed(terrain, river.RiverPoints);
        terrain.Save(terrainPath + ".gterrain");
        TerrainNatureSerializer.Save(terrainPath, new TerrainNatureDocument { WaterBodies = [river] });

        CaptureModel(modelPath, project.RootPath, Path.Combine(output, "model-mesh.png"));
        CaptureTerrain(terrainPath, project.RootPath, Path.Combine(output, "terrain-water.png"));
        Console.WriteLine("Focused organic/water editor captures: " + output);
        return 0;
    }

    private static void CaptureModel(string path, string projectRoot, string destination)
    {
        using ModelEditorControl editor = new(path, projectRoot);
        editor.SetSpin(false);
        editor.SetMode(ModelEditorMode.Mesh);
        editor.SelectPart(0);
        Capture(editor, destination, () =>
        {
            TrackBar? subdivision = Descendants(editor).OfType<TrackBar>()
                .FirstOrDefault(slider => slider.Minimum == 0 && slider.Maximum == 2);
            if (subdivision is not null) subdivision.Value = 2;
            editor.FrameModelForTest();
        });
    }

    private static void CaptureTerrain(string path, string projectRoot, string destination)
    {
        using TerrainEditorControl editor = new(path, projectRoot);
        editor.SetMode(TerrainEditorControl.TerrainEditorMode.Water);
        Capture(editor, destination, () =>
        {
            Button? river = Descendants(editor).OfType<Button>()
                .FirstOrDefault(button => button.Text == "River Tool");
            river?.PerformClick();
            Label? spline = Descendants(editor).OfType<Label>()
                .FirstOrDefault(label => label.Text == "Spline River");
            if (spline?.Parent is ScrollableControl scroll) scroll.ScrollControlIntoView(spline);
        });
    }

    private static void Capture(Control control, string destination, Action afterShown)
    {
        using Form host = new()
        {
            Text = control.GetType().Name,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(20, 20),
            ClientSize = new Size(1440, 940),
            ShowInTaskbar = false,
        };
        control.Dock = DockStyle.Fill;
        host.Controls.Add(control);
        ThemeService.Apply(host);
        UnattendedWindowing.Configure(host);
        UnattendedWindowing.ShowWithoutFocus(host);
        afterShown();
        for (int i = 0; i < 14; i++)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(35);
        }
        using Bitmap? warmup = (control as ModelEditorControl)?.Viewport.CaptureFrame(5)
            ?? (control as TerrainEditorControl)!.Viewport.CaptureFrame(5);
        using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
        bitmap.Save(destination, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }
}
