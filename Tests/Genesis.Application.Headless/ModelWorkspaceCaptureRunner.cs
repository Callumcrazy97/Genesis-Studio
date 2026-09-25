using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Headless;

internal static class ModelWorkspaceCaptureRunner
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        Genesis.Rendering.Core.EditorPreviewSettings.Configure(vsync: false);
        var project = new ProjectService().CreateProject(Path.Combine(output, "Workspace"), "Model review");
        var resources = new ResourceService(project);
        string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Campfire");
        using (var seed = new ModelEditorControl(path, project.RootPath))
        {
            while (seed.Parts.Count > 0) { seed.SelectPart(0); seed.DeleteSelectedPartForTest(); }
            for (int i = 0; i < 8; i++)
            {
                var rock = seed.AddPart(ModelPrimitiveKind.Sphere);
                float angle = i * MathF.Tau / 8;
                rock.Name = "Rock " + (i + 1);
                rock.Position = [MathF.Cos(angle) * 1.4f, .18f, MathF.Sin(angle) * 1.4f];
                rock.Scale = [.65f, .42f, .55f]; rock.Color = [.38f, .42f, .5f];
            }
            for (int i = 0; i < 4; i++)
            {
                var log = seed.AddPart(ModelPrimitiveKind.Cylinder);
                log.Name = "Log " + (i + 1); log.Position = [0, .4f + i * .12f, 0];
                log.Scale = [.38f, 2.2f, .38f]; log.Rotation = [90, i * 45, 0]; log.Color = [.42f, .2f, .07f];
            }
            seed.ApplyFixtureParts(); seed.Save();
        }
        foreach (var role in new[] { ModelEditorRole.Composer, ModelEditorRole.Viewer })
        foreach (var size in new[] { new Size(1440, 940), new Size(1024, 740) })
        {
            using var editor = new ModelEditorControl(path, project.RootPath, role);
            editor.SetSpin(false); editor.SelectPart(8);
            using var host = new Form { Text = "Genesis Studio · Model " + role, ClientSize = size };
            host.Controls.Add(editor); ThemeService.Apply(host);
            UnattendedWindowing.Configure(host); UnattendedWindowing.ShowWithoutFocus(host);
            editor.FrameModelForTest();
            System.Windows.Forms.Application.DoEvents();
            using (var warmup = editor.Viewport.CaptureFrame(12)) { }
            using var screenshot = VisualCapture.CaptureWindowPixels(host);
            screenshot.Save(Path.Combine(output, $"{role.ToString().ToLowerInvariant()}-{size.Width}.png"));
            Validate(editor);
            if (role == ModelEditorRole.Composer && size.Width == 1440)
            {
                foreach (var mode in new[] { ModelEditorMode.Compose, ModelEditorMode.Sculpt, ModelEditorMode.Paint })
                {
                    editor.SetMode(mode);
                    System.Windows.Forms.Application.DoEvents();
                    using (var frame = editor.Viewport.CaptureFrame(4)) { }
                    using var modeImage = VisualCapture.CaptureWindowPixels(host);
                    modeImage.Save(Path.Combine(output, $"editor-{mode.ToString().ToLowerInvariant()}.png"));
                    Validate(editor);
                }
            }
        }
        Console.WriteLine("Model workspace captures: " + output);
        return 0;
    }

    private static void Validate(ModelEditorControl editor)
    {
        var menus = editor.Controls.Find("ModelViewerMenus", true).Single();
        var commands = editor.Controls.Find("ModelViewerCommands", true).Single();
        var timeline = editor.Controls.Find("ModelViewerPlayback", true).Single();
        Rectangle menuBounds = menus.RectangleToScreen(menus.ClientRectangle);
        Rectangle commandBounds = commands.RectangleToScreen(commands.ClientRectangle);
        Rectangle viewportBounds = editor.Viewport.RectangleToScreen(editor.Viewport.ClientRectangle);
        Rectangle timelineBounds = timeline.RectangleToScreen(timeline.ClientRectangle);
        HeadlessHarness.Assert(menuBounds.Bottom <= commandBounds.Top && commandBounds.Bottom <= viewportBounds.Top,
            "Model menus or commands overlap the viewport.");
        HeadlessHarness.Assert(timeline.Visible && timelineBounds.Top >= viewportBounds.Bottom && timeline.Height >= 25,
            "Model timeline is missing, clipped, or covers the viewport.");
        HeadlessHarness.Assert(editor.Viewport.Width >= 350 && editor.Viewport.Height >= 200,
            "Model panels leave insufficient room for the viewport.");
        Console.WriteLine($"PASS {editor.Role} {editor.Mode} layout at {editor.Width}px; viewport {editor.Viewport.Width}x{editor.Viewport.Height}");
    }
}
