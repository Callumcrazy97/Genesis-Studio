using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Studio.Theme;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Headless.Suites;

internal static class Editor3DInspectionSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.View3D");
        foreach (string name in new[] { "ModelViewer", "ModelEditor", "Room", "Terrain", "Shader", "Particle", "Physics" })
            HeadlessHarness.RunCase(ctx.Report, "Editor.View3D." + name + ".CommandsAndRendering", () => Check(ctx, name));
    }

    private static void Check(HeadlessContext ctx, string name)
    {
        var kind = name.StartsWith("Model", StringComparison.Ordinal) ? ResourceKind.Model : Enum.Parse<ResourceKind>(name);
        string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, kind, "Inspection " + name);
        string root = ctx.Project!.RootPath;
        if (kind == ResourceKind.Model) StudioModelResourceLoader.SaveCanonical(path, ModelPoseWorkflowSuite.Fixture());
        using Control control = name switch
        {
            "ModelViewer" => new ModelViewerControl(path, root), "ModelEditor" => new ModelEditorControl(path, root),
            "Room" => new RoomEditorControl(path, root), "Terrain" => new TerrainEditorControl(path, root),
            "Shader" => new ShaderEditorControl(path, root), "Particle" => new ParticleEditorControl(path, root),
            _ => new PhysicsEditorControl(path, root),
        };
        using var host = UnattendedWindowing.NewHost(1400, 900);
        control.Dock = DockStyle.Fill; host.Controls.Add(control); ThemeService.Apply(host);
        UnattendedWindowing.ShowWithoutFocus(host); Pump();
        var viewport = control switch
        {
            ModelViewerControl model => model.Viewport, RoomEditorControl room => room.Viewport,
            TerrainEditorControl terrain => terrain.Viewport, ShaderEditorControl shader => shader.Viewport,
            ParticleEditorControl particle => particle.Viewport, PhysicsEditorControl physics => physics.Viewport,
            _ => throw new InvalidOperationException(),
        };
        var view = Descendants(control).OfType<ToolStrip>().SelectMany(s => s.Items.OfType<ToolStripDropDownItem>())
            .First(m => m.Text == "View" && m.DropDownItems.ContainsKey("View3DNormals"));
        // Open the menu through its control API so its real opening handlers refresh enabled/check states.
        void Open() { view.ShowDropDown(); view.HideDropDown(); Pump(); }
        ToolStripMenuItem Item(string label) => (ToolStripMenuItem)view.DropDownItems["View3D" + label]!;
        if (viewport.Mode2D)
        {
            Open();
            Assert(new[] { "Normals", "Wireframe", "Shadows", "Lighting", "Depth" }.All(s => !Item(s).Enabled), "3D inspection remained enabled in 2D.");
        }
        if (control is ShaderEditorControl shaderControl) shaderControl.SetPipeline(ShaderAssetPipeline.Mesh);
        if (control is ParticleEditorControl particleControl) { particleControl.SetPreview2D(false); particleControl.SetTimelinePosition(.5f); }
        if (control is PhysicsEditorControl physicsControl) physicsControl.SetPreview2D(false);
        if (control is RoomEditorControl)
            Descendants(control).OfType<ToolStrip>().SelectMany(s => s.Items.OfType<ToolStripButton>()).First(b => b.Text == "3D").PerformClick();
        Pump(); Open();
        Assert(!viewport.Mode2D, name + " did not enter 3D.");
        string original = File.ReadAllText(path);
        foreach (string label in new[] { "Normals", "Wireframe", "Shadows", "Lighting", "Depth" })
            Assert(Item(label).Enabled && view.DropDownItems.Cast<ToolStripItem>().Count(i => i.Text == label) == 1, label + " missing, disabled or duplicated.");
        using var baseline = viewport.CaptureFrame(8);
        Item("Normals").PerformClick();
        Assert(viewport.InspectionState.DebugView == RenderDebugView.Normals && Item("Normals").Checked && !Item("Depth").Checked, "Normals did not become active.");
        using var normals = viewport.CaptureFrame(6);
        // Rooms may be empty. Other default previews contain geometry and must change pixels.
        if (name != "Room") Assert(Difference(baseline!, normals!) > 20, name + " Normals did not change rendered geometry.");
        Capture(ctx, host, name + "-normals");
        Item("Depth").PerformClick();
        Assert(!Item("Normals").Checked && Item("Depth").Checked, "Normals and Depth were active together.");
        using (var depth = viewport.CaptureFrame(4)) { }
        Capture(ctx, host, name + "-depth");
        Item("Depth").PerformClick(); Assert(viewport.InspectionState.DebugView == RenderDebugView.Shaded, "Depth did not turn off.");
        bool wire = viewport.InspectionState.Wireframe;
        Item("Wireframe").PerformClick(); Assert(viewport.InspectionState.Wireframe != wire, "Wireframe did not reach scene state.");
        Item("Wireframe").PerformClick(); Assert(viewport.InspectionState.Wireframe == wire, "Wireframe did not restore.");
        bool shadows = viewport.InspectionState.ShadowsEnabled;
        Item("Shadows").PerformClick(); Assert(viewport.InspectionState.ShadowsEnabled != shadows, "Shadows did not toggle.");
        Item("Shadows").PerformClick();
        bool lit = viewport.InspectionState.LightingEnabled;
        Item("Lighting").PerformClick(); Assert(viewport.InspectionState.LightingEnabled != lit, "Lighting did not toggle.");
        if (!viewport.InspectionState.LightingEnabled) Assert(viewport.InspectionState.LightingWeight == 0, "Unlit retained a nonzero lighting weight.");
        Item("Lighting").PerformClick();
        if (name == "ModelViewer")
        {
            Item("Normals").PerformClick(); viewport.PinSecondaryFromCurrentView();
            Assert(viewport.SecondaryViewport!.InspectionState.DebugView == RenderDebugView.Normals, "Second camera did not share inspection mode.");
            viewport.ClearSecondaryCamera(); Item("Normals").PerformClick();
        }
        Assert(File.ReadAllText(path) == original, "View options wrote the resource.");
    }

    internal static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    internal static int Difference(Bitmap a, Bitmap b)
    {
        int count = 0;
        for (int y = 0; y < Math.Min(a.Height, b.Height); y += 4) for (int x = 0; x < Math.Min(a.Width, b.Width); x += 4)
        {
            var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
            if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 30) count++;
        }
        return count;
    }
    internal static void Capture(HeadlessContext ctx, Form form, string name)
    {
        Pump(); var metrics = VisualCapture.CaptureOpenForm(form, Path.Combine(ctx.Captures, name + ".png"), true);
        ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
    }
    internal static void Pump() => System.Windows.Forms.Application.DoEvents();
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
