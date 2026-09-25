using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Theme;
using Genesis.Application.Headless.Suites;
using Genesis.Runtime.Modeling;
using WinFormsApplication = System.Windows.Forms.Application;

namespace Genesis.Application.Headless;

/// <summary>Captures complete, populated editor surfaces for the UX review documentation.</summary>
internal static class EditorReviewCaptureRunner
{
    private const int DefaultCaptureWidth = 1360;
    private const int DefaultCaptureHeight = 840;

    public static int Run(
        string? projectRoot,
        string outputDirectory,
        int captureWidth = DefaultCaptureWidth,
        int captureHeight = DefaultCaptureHeight)
    {
        try
        {
            if (captureWidth < 640 || captureHeight < 480)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(captureWidth),
                    $"Editor review captures must be at least 640 × 480; requested {captureWidth} × {captureHeight}.");
            }

            outputDirectory = Path.GetFullPath(outputDirectory);
            string portfolioRoot = ResolvePortfolioRoot(outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(portfolioRoot);

            bool previousVSync = Genesis.Rendering.Core.EditorPreviewSettings.VSync;
            Genesis.Rendering.Core.EditorPreviewSettings.Configure(vsync: false);
            try
            {
                SuiteChromeBridge.Push();
                WinFormsApplication.SetDefaultFont(ThemeService.InterfaceFont);

                if (!string.IsNullOrWhiteSpace(projectRoot)
                    && Directory.Exists(Path.Combine(projectRoot, "Assets")))
                {
                    CaptureFixtureEditors(Path.GetFullPath(projectRoot), outputDirectory, captureWidth, captureHeight);
                }
                else
                {
                    Console.WriteLine(
                        "Skipping the eleven-editor fixture set; no Assets directory was given.");
                }

                CaptureShared3DEditors(portfolioRoot);
            }
            finally
            {
                Genesis.Rendering.Core.EditorPreviewSettings.Configure(previousVSync);
            }

            Console.WriteLine(
                $"EDITOR REVIEW CAPTURES PASSED — 11-editor fixture plus shared 3D variants\nImages: {outputDirectory}\nShared 3D: {Path.Combine(portfolioRoot, "shared-3d")}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"EDITOR REVIEW CAPTURES FAILED: {ex}");
            return 1;
        }
    }

    private static string ResolvePortfolioRoot(string outputDirectory)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(outputDirectory);
        string leaf = Path.GetFileName(trimmed);
        if (string.Equals(leaf, "images", StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, "narrow", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(trimmed)?.FullName ?? trimmed;
        }

        return trimmed;
    }

    private static void CaptureFixtureEditors(
        string projectRoot,
        string outputDirectory,
        int captureWidth,
        int captureHeight)
    {
        string assets = Path.Combine(projectRoot, "Assets");
        string gateGroup = Directory.GetParent(projectRoot)?.FullName ?? projectRoot;
        string workspaceRoot = Directory.GetParent(gateGroup)?.FullName ?? gateGroup;
        string platformerRoot = Path.Combine(gateGroup, "Gate Platformer");
        string detailedRoot = Path.Combine(workspaceRoot, "Headless Visual Test");

        string viewerPath = RequireFile(assets, "Images", "Gate Sprite.image.json");
        ImageDocumentSession viewerSession = new(ImageDocumentSerializer.LoadAtomic(viewerPath).Document, viewerPath);
        CaptureControl(new ImageViewerControl(viewerSession), Path.Combine(outputDirectory, "12-image-viewer.png"), captureWidth, captureHeight, 8);

        CaptureProjectEntry(projectRoot, outputDirectory, captureWidth, captureHeight);

        CaptureImage(
            RequireFile(assets, "Images", "Gate Sprite.image.json"),
            Path.Combine(outputDirectory, "01-image-editor.png"),
            captureWidth,
            captureHeight);

        string roomPath = PreferExisting(
            Path.Combine(platformerRoot, "Assets", "Rooms", "Level 1.room.json"),
            RequireFile(assets, "Rooms", "Gate Arena.room.json"));
        string roomRoot = roomPath.StartsWith(platformerRoot, StringComparison.OrdinalIgnoreCase)
            ? platformerRoot
            : projectRoot;
        CaptureSuite(
            new RoomEditorControl(roomPath, roomRoot),
            Path.Combine(outputDirectory, "02-room-editor.png"),
            captureWidth,
            captureHeight);
        CaptureSuite(
            new TerrainEditorControl(
                RequireFile(assets, "Terrain", "Gate Terrain.terrain.json"), projectRoot),
            Path.Combine(outputDirectory, "03-terrain-editor.png"),
            captureWidth,
            captureHeight);
        string objectPath = RequireAnyFile(
            "Object editor fixture",
            Path.Combine(platformerRoot, "Assets", "Objects", "Coin.object.json"),
            Path.Combine(assets, "Objects", "Rainbow Logo.object.json"),
            Path.Combine(assets, "Gate", "Gate Object.object.json"));
        string objectRoot = objectPath.StartsWith(platformerRoot, StringComparison.OrdinalIgnoreCase)
            ? platformerRoot
            : projectRoot;
        CaptureSuite(
            new ObjectEditorControl(objectPath, objectRoot),
            Path.Combine(outputDirectory, "04-object-editor.png"),
            captureWidth,
            captureHeight);
        CaptureSuite(
            new PgslScriptEditorControl(
                RequireFile(assets, "Scripts", "Gate Script.pgsl"), projectRoot),
            Path.Combine(outputDirectory, "05-pgsl-editor.png"),
            captureWidth,
            captureHeight);
        string audioPath = PreferExisting(
            Path.Combine(detailedRoot, "Assets", "Audio", "SuiteSpatial.audio.json"),
            RequireFile(assets, "Audio", "Gate Sound.audio.json"));
        string audioRoot = audioPath.StartsWith(detailedRoot, StringComparison.OrdinalIgnoreCase)
            ? detailedRoot
            : projectRoot;
        CaptureSuite(
            new AudioEditorControl(audioPath, audioRoot),
            Path.Combine(outputDirectory, "06-audio-editor.png"),
            captureWidth,
            captureHeight);
        CaptureSuite(
            new ShaderEditorControl(
                RequireFile(assets, "Shaders", "Gate Shader.shader.json"), projectRoot),
            Path.Combine(outputDirectory, "07-shader-editor.png"),
            captureWidth,
            captureHeight);
        ModelEditorControl modelEditor = new(
            RequireFile(assets, "Models", "Gate Model.model.json"), projectRoot);
        CaptureSuite(
            modelEditor,
            Path.Combine(outputDirectory, "08-model-editor.png"),
            captureWidth,
            captureHeight,
            settleIterations: 16,
            afterShown: modelEditor.FrameModelForTest);
        ModelEditorControl modelViewer = new(
            RequireFile(assets, "Models", "Gate Model.model.json"), projectRoot, ModelEditorRole.Viewer);
        CaptureSuite(modelViewer, Path.Combine(outputDirectory, "13-model-viewer.png"),
            captureWidth, captureHeight, settleIterations: 16, afterShown: modelViewer.FrameModelForTest);
        ParticleEditorControl particleEditor = new(
            RequireFile(assets, "Particles", "Gate Particle.particle.json"), projectRoot);
        CaptureSuite(
            particleEditor,
            Path.Combine(outputDirectory, "09-particle-editor.png"),
            captureWidth,
            captureHeight,
            settleIterations: 20,
            afterShown: () => particleEditor.ActiveInspectorSection = 2);
        CaptureSuite(
            new PhysicsEditorControl(
                RequireFile(assets, "Physics", "Gate Physics.physics.json"), projectRoot),
            Path.Combine(outputDirectory, "10-physics-editor.png"),
            captureWidth,
            captureHeight);
        CaptureSuite(
            new NoteEditorControl(
                RequireFile(assets, "Notes", "Gate Note.md"), projectRoot),
            Path.Combine(outputDirectory, "11-note-editor.png"),
            captureWidth,
            captureHeight);
    }

    private static void CaptureShared3DEditors(string outputDirectory)
    {
        string sharedDir = Path.Combine(outputDirectory, "shared-3d");
        string imagesDir = Path.Combine(outputDirectory, "images");
        string narrowDir = Path.Combine(outputDirectory, "narrow");
        Directory.CreateDirectory(sharedDir);
        Directory.CreateDirectory(imagesDir);
        Directory.CreateDirectory(narrowDir);

        string workspace = Path.Combine(AppContext.BaseDirectory, "TestResults", "EditorReviewShared3DWorkspace");
        Directory.CreateDirectory(workspace);
        ProjectService projects = new();
        ProjectSession blank = projects.CreateProject(workspace, "Shared3D Blank", "Blank", overwrite: true);
        ProjectSession platformer = projects.CreateProject(workspace, "Shared3D Platformer", "2D", overwrite: true);
        ResourceService resources = new(blank);

        string modelPath = resources.CreateResource(
            Path.Combine(blank.AssetsPath, "Models"), ResourceKind.Model, "Shared3D Model");
        string terrainPath = resources.CreateResource(
            Path.Combine(blank.AssetsPath, "Terrain"), ResourceKind.Terrain, "Shared3D Terrain");
        string roomPath = PreferExisting(
            Path.Combine(platformer.AssetsPath, "Rooms", "Level 1.room.json"),
            resources.CreateResource(
                Path.Combine(blank.AssetsPath, "Rooms"), ResourceKind.Room, "Shared3D Room"));
        string roomRoot = roomPath.StartsWith(platformer.RootPath, StringComparison.OrdinalIgnoreCase)
            ? platformer.RootPath
            : blank.RootPath;

        using (ModelEditorControl seed = new(modelPath, blank.RootPath))
        {
            seed.ApplyFixtureTree(new ProceduralTreeOptions { Quality = ProceduralModelQuality.Draft });
            seed.Save();
        }

        Capture3DVariants(
            sharedDir,
            "room",
            () => new RoomEditorControl(roomPath, roomRoot),
            (control, variant) =>
            {
                RoomEditorControl room = (RoomEditorControl)control;
                room.ViewMode3D = true;
                if (variant is "inset" or "floor-on" or "floor-off" or "normal" or "fullscreen" or "narrow")
                {
                    if (variant == "floor-off")
                    {
                        room.SetFloorStyle(EditorFloorStyle.None);
                    }
                    else
                    {
                        room.SetFloorStyle(EditorFloorStyle.Checkerboard);
                    }

                    if (variant == "inset")
                    {
                        room.ShowGameCameraInInset();
                        if (!room.Viewport.HasSecondaryCamera)
                        {
                            room.Viewport.PinSecondaryFromCurrentView("Pinned view");
                        }
                    }
                }
            });

        Capture3DVariants(
            sharedDir,
            "terrain",
            () => new TerrainEditorControl(terrainPath, blank.RootPath),
            (control, variant) =>
            {
                TerrainEditorControl terrain = (TerrainEditorControl)control;
                if (variant == "floor-on")
                {
                    terrain.SetFloorStyle(EditorFloorStyle.Checkerboard);
                }
                else if (variant == "floor-off")
                {
                    terrain.SetFloorStyle(EditorFloorStyle.None);
                }

                if (variant == "inset")
                {
                    terrain.Viewport.PinSecondaryFromCurrentView("Pinned view");
                }
            });

        Capture3DVariants(
            sharedDir,
            "model",
            () => new ModelEditorControl(modelPath, blank.RootPath),
            (control, variant) =>
            {
                ModelEditorControl model = (ModelEditorControl)control;
                model.FrameModelForTest();
                if (variant == "floor-on")
                {
                    model.SetGround(EditorFloorStyle.Checkerboard);
                }
                else if (variant == "floor-off")
                {
                    model.SetGround(EditorFloorStyle.None);
                }

                if (variant == "inset")
                {
                    model.Viewport.PinSecondaryFromCurrentView("Pinned view");
                }
            },
            settleIterations: 16);

        // Keep shared 3D variants separate. They use different assets, dimensions and modes
        // from the fixture captures; replacing those files hides the actual editor state.
    }

    private static void Capture3DVariants(
        string outputDirectory,
        string prefix,
        Func<Control> factory,
        Action<Control, string> prepare,
        int settleIterations = 12)
    {
        (string Variant, int Width, int Height)[] variants =
        [
            ("normal", DefaultCaptureWidth, DefaultCaptureHeight),
            ("fullscreen", 1920, 1080),
            ("narrow", 760, 620),
            ("inset", DefaultCaptureWidth, DefaultCaptureHeight),
            ("floor-on", DefaultCaptureWidth, DefaultCaptureHeight),
            ("floor-off", DefaultCaptureWidth, DefaultCaptureHeight),
        ];

        foreach ((string variant, int width, int height) in variants)
        {
            Control editor = factory();
            CaptureControl(
                editor,
                Path.Combine(outputDirectory, $"{prefix}-{variant}.png"),
                width,
                height,
                settleIterations,
                () => prepare(editor, variant));
        }
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static void CaptureImage(
        string documentPath,
        string outputPath,
        int captureWidth,
        int captureHeight)
    {
        ImageDocumentSession session = new(
            ImageDocumentSerializer.LoadAtomic(documentPath).Document,
            documentPath);
        ImageWorkspace workspace = ImageWorkspaceStorage.Load(session);
        CaptureControl(
            new ImageEditorControl(session, workspace),
            outputPath,
            captureWidth,
            captureHeight,
            settleIterations: 8);
    }

    private static void CaptureSuite(
        IEditorSurface surface,
        string outputPath,
        int captureWidth,
        int captureHeight,
        int settleIterations = 8,
        Action? afterShown = null)
    {
        if (surface is not Control control)
        {
            throw new InvalidOperationException(
                $"Editor surface '{surface.GetType().Name}' is not a WinForms control.");
        }

        CaptureControl(control, outputPath, captureWidth, captureHeight, settleIterations, afterShown);
    }

    private static void CaptureProjectEntry(string projectRoot, string outputDirectory, int width, int height)
    {
        string userData = Path.Combine(Path.GetFullPath(outputDirectory), "capture-userdata");
        Directory.CreateDirectory(userData);
        var settings = new Genesis.Application.Core.Settings.SettingsService(Path.Combine(userData, "settings.json"));
        var projects = new ProjectService();
        ProjectSession project = projects.OpenProject(projectRoot);
        settings.AddRecentProject(project.Manifest.Name, project.ProjectFile);
        var services = new StudioServices(settings, projects, new ProjectValidator(),
            new Genesis.Application.Core.Diagnostics.StudioLog(Path.Combine(userData, "studio.log")));
        CaptureControl(new Genesis.Application.Studio.Forms.ProjectHubForm(services), Path.Combine(outputDirectory, "14-project-hub.png"), width, height, 8);
        var templates = new Genesis.Application.Studio.Forms.ProjectHubForm(services);
        CaptureControl(templates, Path.Combine(outputDirectory, "15-project-templates.png"), width, height, 8,
            () => templates.ShowSection(Genesis.Application.Studio.Forms.HubSection.Templates));
        CaptureControl(new Genesis.Application.Studio.Forms.NewProjectDialog(), Path.Combine(outputDirectory, "16-new-project.png"), 820, 650, 8);
        CaptureControl(new WelcomeDocument(project), Path.Combine(outputDirectory, "17-welcome.png"), width, height, 8);
        CaptureControl(new Genesis.Application.Studio.Forms.SplashForm(), Path.Combine(outputDirectory, "18-splash.png"), 940, 540, 4);
    }

    private static void CaptureControl(
        Control control,
        string outputPath,
        int captureWidth,
        int captureHeight,
        int settleIterations,
        Action? afterShown = null)
    {
        Form host = control as Form ?? new()
        {
            Text = control.GetType().Name,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(20, 20),
            ClientSize = new System.Drawing.Size(captureWidth, captureHeight),
            ShowInTaskbar = false,
        };
        host.ClientSize = new System.Drawing.Size(captureWidth, captureHeight);
        if (control != host)
        {
            control.Dock = DockStyle.Fill;
            host.Controls.Add(control);
        }
        GateSuite.ShowHost(host);
        
        host.BringToFront();
        WinFormsApplication.DoEvents();
        afterShown?.Invoke();

        for (int i = 0; i < settleIterations; i++)
        {
            WinFormsApplication.DoEvents();
            Thread.Sleep(35);
        }

        using System.Drawing.Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"Captured {Path.GetFileName(outputPath)} ({bitmap.Width}x{bitmap.Height}, DPI={host.DeviceDpi}, renderer={Genesis.Rendering.Core.RenderBackendSelection.EffectiveBackend})");

        host.Close();
        host.Dispose();
        WinFormsApplication.DoEvents();
    }

    private static string RequireFile(string assetsRoot, string folder, string fileName)
    {
        string path = Path.Combine(assetsRoot, folder, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Editor review fixture is missing '{fileName}'.", path);
        }

        return path;
    }

    private static string PreferExisting(string preferred, string fallback) =>
        File.Exists(preferred) ? preferred : fallback;

    private static string RequireAnyFile(string description, params string[] candidates)
    {
        string? existing = candidates.FirstOrDefault(File.Exists);
        if (existing is not null) return existing;
        throw new FileNotFoundException(
            $"{description} is missing. Checked: {string.Join(", ", candidates.Select(Path.GetFileName))}.");
    }
}
