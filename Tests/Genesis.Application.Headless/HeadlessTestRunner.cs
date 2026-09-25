using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Runtime;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio.Theme;
using Genesis.Application.Headless.Suites;
using Genesis.Shared.Interfaces;
using WinFormsApplication = System.Windows.Forms.Application;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;
using SkiaSharp;

namespace Genesis.Application.Headless;

internal static class HeadlessTestRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        string outputRoot = ResolveOutput(args);
        PrepareOutput(outputRoot);
        WinFormsApplication.SetDefaultFont(ThemeService.InterfaceFont);

        string? focusedTarget = ReadOption(args, "--test");
        bool fastBuildGate = focusedTarget is not null || args.Any(argument =>
            string.Equals(argument, "--fast-tests", StringComparison.OrdinalIgnoreCase));
        string profile = focusedTarget is not null
            ? $"Focused Test ({focusedTarget})"
            : fastBuildGate
                ? "Build Gate (11 editor workflows + 2 PGSL runtime workflows)"
                : "Full Regression (build gate, then every detailed regression)";

        TestReport report = new()
        {
            StartedUtc = DateTime.UtcNow,
            MachineName = Environment.MachineName,
            RuntimeVersion = Environment.Version.ToString(),
            OutputDirectory = outputRoot,
            FastBuildGate = fastBuildGate,
            Profile = profile,
        };

        Console.WriteLine($"Headless profile: {profile.ToUpperInvariant()}");

        string workspace = Path.Combine(outputRoot, "Workspace");
        string captures = Path.Combine(outputRoot, "Images");
        string logs = Path.Combine(outputRoot, "Logs");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(captures);
        Directory.CreateDirectory(logs);

        HeadlessContext ctx = new()
        {
            Report = report,
            OutputRoot = outputRoot,
            Workspace = workspace,
            Captures = captures,
            Logs = logs,
            UpdateBaselines = args.Any(a =>
                string.Equals(a, "--update-baselines", StringComparison.OrdinalIgnoreCase)),
        };

        if (focusedTarget is not null)
        {
            RunFocusedTarget(ctx, focusedTarget);
            return Finish(report, outputRoot, fastBuildGate: true);
        }

        // The build gate: one consolidated test per editor and one PGSL runtime workflow per
        // dimension. It is self-contained and reports exactly 13 outcomes.
        // It is self-contained — it builds its own projects — so it is also what runs alone under
        // --fast-tests, and it runs first in a full pass so a broken fundamental fails in seconds
        // rather than three minutes into the detailed regressions.
        Suites.GateSuite.Run(ctx);

        if (fastBuildGate)
        {
            return Finish(report, outputRoot, fastBuildGate: true);
        }

        // CPU-only rendering contract checks run first: a constant-buffer layout drift would
        // otherwise surface as an inexplicably wrong pixel in a later visual capture.
        Suites.RenderBackendSuite.Run(ctx);

        Suites.RuntimeSuite.Run(ctx);
        Suites.EcsFoundationSuite.Run(ctx);
        Suites.ThemeImageSuite.Run(ctx);

        ProjectSession? project = null;
        ResourceService? resources = null;
        StudioServices? studioServices = null;
        ResourceFixture? fixture = null;

        HeadlessHarness.BeginMajor(report, "Shell");

        RunCase(report, "Shell.Project.CreateAndOpen", () =>
        {
            ProjectService projectService = new();
            project = projectService.CreateProject(workspace, "Headless Visual Test");
            ProjectSession reopened = projectService.OpenProject(project.ProjectFile);
            Assert(reopened.Manifest.ProjectId == project.Manifest.ProjectId, "Project ID changed after reopen.");
            Assert(Directory.Exists(reopened.AssetsPath), "Assets directory was not created.");

            bool threw = false;
            try
            {
                projectService.CreateProject(workspace, "Headless Visual Test");
            }
            catch (IOException)
            {
                threw = true;
            }
            Assert(threw, "CreateProject did not throw IOException when directory already exists without overwrite.");

            ProjectSession overwritten = projectService.CreateProject(workspace, "Headless Visual Test", overwrite: true);
            Assert(overwritten.Manifest.ProjectId != project.Manifest.ProjectId, "Overwritten project did not generate a fresh manifest.");
            project = overwritten;
            ctx.Project = project;
        });

        RunCase(report, "Shell.Resources.CreateRenameMoveCopyCutPaste", () =>
        {
            ProjectSession session = Require(project, "Project fixture");
            resources = new ResourceService(session);
            fixture = BuildResourceFixture(resources);
            VerifyResourceFixture(resources, fixture);
            ctx.Resources = resources;
            ctx.Fixture = fixture;
        });

        RunCase(report, "Shell.Resources.GuidDatabaseAndValidation", () =>
        {
            ProjectSession session = Require(project, "Project fixture");
            ResourceService resourceService = Require(resources, "Resource service");
            _ = resourceService.BuildTree();
            ProjectValidator validator = new();
            IReadOnlyList<ProjectValidationIssue> issues = validator.Validate(session);
            ProjectValidationIssue[] errors = issues
                .Where(issue => issue.Severity == ProjectValidationSeverity.Error)
                .ToArray();
            Assert(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.Message)));

            ResourceFixture resourceFixture = Require(fixture, "Resource fixture");
            Guid original = ReadAssetGuid(resourceFixture.HeroObject);
            Guid copied = ReadAssetGuid(resourceFixture.HeroCopy);
            Assert(original != Guid.Empty, "Original resource GUID was not created.");
            Assert(copied != Guid.Empty, "Copied resource GUID was not created.");
            Assert(original != copied, "Copied resources must receive a new GUID.");
        });

        RunCase(report, "Shell.Resources.PathTraversalBlocked", () =>
        {
            ResourceService resourceService = Require(resources, "Resource service");
            bool blocked = false;
            try
            {
                resourceService.CreateFolder(
                    Path.GetDirectoryName(resourceService.AssetsRoot)
                    ?? resourceService.AssetsRoot,
                    "Escaped");
            }
            catch (UnauthorizedAccessException)
            {
                blocked = true;
            }

            Assert(blocked, "A resource operation escaped the project Assets root.");
        });

        RunCase(report, "Shell.Services.CentralPreferences", () =>
        {
            string settingsFile = Path.Combine(outputRoot, "UserData", "preferences.json");
            SettingsService settings = new(settingsFile);
            settings.Update(current =>
            {
                current.Appearance.Theme = "Light";
                current.Appearance.CodeFontSize = 12;
                current.Editing.AutoSaveMinutes = 3;
                current.Editing.CreateBackups = true;
                current.Editing.BackupRetentionDays = 21;
                current.Runtime.VSyncInPreview = false;
                current.General.ConfirmDestructiveActions = true;
            });

            SettingsService reloaded = new(settingsFile);
            Assert(reloaded.Current.Appearance.Theme == "Light", "Theme preference did not persist.");
            Assert(reloaded.Current.Editing.AutoSaveMinutes == 3, "Editing preference did not persist.");
            Assert(
                reloaded.Current.Editing.BackupRetentionDays == 21,
                "Backup retention preference did not persist.");
            Assert(!reloaded.Current.Runtime.VSyncInPreview, "Runtime VSync preference did not persist.");

            ThemeService.ApplySettings(reloaded.Current);
            Assert(!ThemeService.Palette.IsDark, "Light theme did not apply a light palette.");
            Assert(
                ThemeService.Palette.Accent.ToArgb() == ThemeCatalog.Get("Light").Accent.ToArgb(),
                "Light theme accent did not match the theme catalog.");
            Assert(ThemeService.CodeFont.SizeInPoints == 12f, "Code font size was not applied live.");

            reloaded.Update(current => current.Appearance.Theme = "Blue");
            ThemeService.ApplySettings(reloaded.Current);
            Assert(ThemeService.Palette.IsDark, "Blue theme should be a dark palette.");
            Assert(
                ThemeService.Palette.Accent.ToArgb() == ThemeCatalog.Get("Blue").Accent.ToArgb(),
                "Blue theme accent did not match the theme catalog.");

            ProjectSession session = Require(project, "Project fixture");
            reloaded.AddRecentProject(session.Manifest.Name, session.ProjectFile);
            StudioLog studioLog = new(Path.Combine(logs, "studio.log"));
            studioServices = new StudioServices(
                reloaded,
                new ProjectService(),
                new ProjectValidator(),
                studioLog);
            ctx.StudioServices = studioServices;
        });

        Suites.ShellLayoutSuite.Run(ctx);
        Suites.ResourceSearchSuite.Run(ctx);

        RunCase(report, "Shell.Resources.ImportRenameAssociates", () =>
        {
            ResourceService resourceService = Require(resources, "Resource service");
            string png = Path.Combine(outputRoot, "import-source.png");
            using (Bitmap bitmap = new(12, 12))
            {
                using Graphics graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.FromArgb(220, 40, 40));
                bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            }

            string imported = resourceService.ImportFiles(resourceService.AssetsRoot, [png]).Single();
            Assert(
                imported.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase),
                "Image import did not create a Sprite resource.");
            string spriteData = ResourceAssociates.GetSpriteDataDirectory(imported);
            string media = Path.Combine(spriteData, "frame-0000.png");
            Assert(Directory.Exists(spriteData), "Imported sprite data directory was not created.");
            Assert(File.Exists(media), "Imported image associate was not created.");

            ResourceItem tree = resourceService.BuildTree();
            Assert(FindResource(tree, imported) is not null, "Imported sprite missing from asset tree.");
            Assert(FindResource(tree, spriteData) is null, "Sprite data directory must stay hidden in the asset tree.");

            string renamed = resourceService.Rename(imported, "Hero Sprite");
            string renamedData = ResourceAssociates.GetSpriteDataDirectory(renamed);
            string renamedMedia = Path.Combine(renamedData, "frame-0000.png");
            Assert(!Directory.Exists(spriteData), "Old sprite data directory was not renamed.");
            Assert(File.Exists(renamedMedia), "Associate was not renamed with the resource.");

            string folder = resourceService.CreateFolder(Path.Combine(resourceService.AssetsRoot, "Sprites"), "Imported");
            string moved = resourceService.Move(renamed, folder);
            string movedData = ResourceAssociates.GetSpriteDataDirectory(moved);
            string movedMedia = Path.Combine(movedData, "frame-0000.png");
            Assert(File.Exists(moved), "Moved sprite missing.");
            Assert(File.Exists(movedMedia), "Associate was not moved with the resource.");

            resourceService.MoveToTrash(moved);
            Assert(!File.Exists(moved), "Trashed sprite still exists.");
            Assert(!Directory.Exists(movedData), "Sprite data directory was not trashed with the resource.");
        });

        HeadlessHarness.BeginMajor(report, "Editor.Image");

        RunCase(report, "Editor.Image.Schema.CommandsAndAssociates", () =>
        {
            ResourceService resourceService = Require(resources, "Resource service");
            string spritePath = resourceService.CreateResource(
                resourceService.AssetsRoot,
                ResourceKind.Image,
                "Image Schema Test");
            ImageDocument document = ImageDocument.CreateDefault(32, 24);
            ImageFrame frame = new()
            {
                Name = "Idle 1",
                DurationMilliseconds = 125,
                SourceRectangle = new ImageRectangle { Width = 32, Height = 24 },
            };
            document.Frames.Add(frame);
            document.Tags.Add(new ImageAnimationTag
            {
                Name = "Idle",
                StartFrameId = frame.Id,
                EndFrameId = frame.Id,
            });
            document.Usage.Allowed |= ImageUsage.Tileset;
            document.Usage.Tileset.TileWidth = 8;
            document.Usage.Tileset.TileHeight = 8;
            document.CollisionShapes.Add(new ImageCollisionShape
            {
                Kind = ImageCollisionShapeKind.Capsule,
                Size = new ImageVector2 { X = 12, Y = 20 },
            });
            document.Armature = new ImageArmature
            {
                Bones = [new ImageBone { Name = "Root", Length = 10 }],
            };
            ImageDocumentSerializer.SaveAtomic(spritePath, document);
            ImageDocumentLoadResult loaded = ImageDocumentSerializer.LoadAtomic(spritePath);
            Assert(loaded.Document.SchemaVersion == 2, "Sprite schema did not round-trip at v2.");
            Assert(loaded.Document.Frames[0].DurationMilliseconds == 125, "Frame timing did not round-trip.");
            Assert(loaded.Document.Usage.Supports(ImageUsage.Tileset), "Tileset profile did not round-trip.");
            Assert(loaded.Document.Armature?.Bones.Count == 1, "Armature did not round-trip.");

            ImageDocumentSession imageSession = new(loaded.Document, spritePath);
            int marker = 0;
            imageSession.Execute(new StructuralImageCommand(
                "Marker",
                _ => marker = 1,
                _ => marker = 0));
            Assert(marker == 1 && imageSession.IsDirty, "Structural command did not execute or mark dirty.");
            Assert(imageSession.Undo() && marker == 0, "Structural command undo failed.");
            Assert(imageSession.Redo() && marker == 1, "Structural command redo failed.");

            ImageWorkspace workspace = ImageWorkspace.CreateBlank(32, 24, Color.Transparent);
            ImageLayerBuffer layer = workspace.CurrentLayer!;
            PixelStrokeRecorder recorder = new(workspace, layer);
            recorder.Capture(new Rectangle(0, 0, 8, 8));
            RasterOperations.SetPixel(layer.Pixels, 32, 24, 3, 4, Color.Red);
            IImageDocumentCommand pixelCommand = recorder.Complete("Pixel");
            imageSession.Execute(pixelCommand);
            Assert(RasterOperations.GetPixel(layer.Pixels, 32, 24, 3, 4).R == 255, "Pixel command failed.");
            imageSession.Undo();
            Assert(RasterOperations.GetPixel(layer.Pixels, 32, 24, 3, 4).A == 0, "Pixel tile undo failed.");
            imageSession.Redo();
            Assert(RasterOperations.GetPixel(layer.Pixels, 32, 24, 3, 4).R == 255, "Pixel tile redo failed.");

            string dataDirectory = ResourceAssociates.GetSpriteDataDirectory(spritePath);
            Directory.CreateDirectory(Path.Combine(dataDirectory, "frames"));
            ImageWorkspaceStorage.WritePng(
                Path.Combine(dataDirectory, "frames", frame.Id + ".png"),
                32,
                24,
                workspace.CompositeCurrentFrame());
            string renamed = resourceService.Rename(spritePath, "Image Schema Renamed");
            string renamedData = ResourceAssociates.GetSpriteDataDirectory(renamed);
            Assert(Directory.Exists(renamedData), "Sprite data directory did not follow rename.");
            string destination = resourceService.CreateFolder(Path.Combine(resourceService.AssetsRoot, "Sprites"), "Image Copies");
            string copied = resourceService.Copy(renamed, destination);
            Assert(Directory.Exists(ResourceAssociates.GetSpriteDataDirectory(copied)), "Sprite data directory did not copy.");
            Assert(ReadAssetGuid(renamed) != ReadAssetGuid(copied), "Copied sprite retained the source GUID.");
        });

        RunCase(report, "Editor.Image.Schema.V1MigratesOnLoad", () =>
        {
            ResourceService resourceService = Require(resources, "Resource service");
            string spritePath = resourceService.CreateResource(
                resourceService.AssetsRoot,
                ResourceKind.Image,
                "Legacy V1 Sprite");
            File.WriteAllText(
                spritePath,
                """{"schemaVersion":1,"canvas":{"width":48,"height":32},"frames":[],"layers":[]}""");

            ImageDocumentLoadResult loaded = ImageDocumentSerializer.LoadAtomic(spritePath);
            Assert(loaded.WasMigrated, "V1 sprite should report migration.");
            Assert(loaded.Document.SchemaVersion == 2, "V1 sprite should migrate to schema v2.");
            Assert(loaded.Document.Canvas.Width == 48 && loaded.Document.Canvas.Height == 32, "Canvas size was not preserved.");

            using StudioShellForm studio = new(Require(studioServices, "Studio services"), Require(project, "Project fixture"), persistLayout: false);
            ResourceItem sprite = FindResource(resourceService.BuildTree(), spritePath)
                ?? throw new InvalidOperationException("Migrated sprite missing from resource tree.");
            IStudioDocument document = studio.OpenStudioResource(sprite);
            ImageViewerDocument? viewer = document as ImageViewerDocument;
            Assert(viewer is not null && !viewer.IsDisposed, "V1 sprite should open in a live image viewer.");
        });

        RunCase(report, "Editor.Image.Schema.ImportSaveRelativePaths", () =>
        {
            ResourceService resourceService = Require(resources, "Resource service");
            string spritePath = resourceService.CreateResource(
                resourceService.AssetsRoot,
                ResourceKind.Image,
                "Import Save Test");
            ImageDocumentLoadResult initial = ImageDocumentSerializer.LoadAtomic(spritePath);
            ImageDocumentSession session = new(initial.Document, spritePath);
            string externalPng = Path.Combine(outputRoot, "external-import.png");
            using (Bitmap bitmap = new(1312, 907))
            {
                using Graphics graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.FromArgb(255, 64, 128, 192));
                graphics.FillRectangle(Brushes.White, 100, 80, 400, 300);
                bitmap.Save(externalPng, System.Drawing.Imaging.ImageFormat.Png);
            }

            ImageWorkspace workspace = ImageWorkspaceStorage.Import(session, externalPng);
            Assert(workspace.Width == 1312 && workspace.Height == 907, "Import did not adopt source dimensions.");
            Assert(
                session.Document.Import.Source is { } importSource
                && !Path.IsPathRooted(importSource)
                && importSource.Contains("import/external-import.png", StringComparison.Ordinal),
                "Import source must be resource-relative.");

            ImageWorkspaceStorage.Save(session, workspace);
            ImageDocumentLoadResult reloaded = ImageDocumentSerializer.LoadAtomic(spritePath);
            Assert(reloaded.Document.Frames.Count == 1, "Saved sprite should contain one frame.");
            Assert(
                reloaded.Document.Frames[0].Source is { } frameSource
                && !Path.IsPathRooted(frameSource),
                "Frame source must be resource-relative after save.");
            Assert(
                reloaded.Document.Import.Source is { } savedImport
                && !Path.IsPathRooted(savedImport),
                "Import source must remain resource-relative after save.");

            ImageWorkspace roundTrip = ImageWorkspaceStorage.Load(new ImageDocumentSession(reloaded.Document, spritePath));
            Assert(roundTrip.Width == 1312 && roundTrip.Height == 907, "Reloaded workspace dimensions mismatch.");
            (int width, int height, byte[] pixels) = roundTrip.CompositeCurrentFrameFor(0);
            Assert(width == 1312 && height == 907 && pixels.Length == width * height * 4, "Reloaded pixels missing.");
            Assert(pixels[0] == 64 && pixels[1] == 128 && pixels[2] == 192, "Reloaded pixel data did not round-trip.");
        });

        RunCase(report, "Editor.Image.Viewer.UntitledJpgReadback", () =>
        {
            string untitledJpg = ResolveUntitledJpgFixture();
            Assert(File.Exists(untitledJpg), $"Fixture image is missing: {untitledJpg}");

            ResourceService resourceService = Require(resources, "Resource service");
            string spritePath = resourceService.CreateResource(
                resourceService.AssetsRoot,
                ResourceKind.Image,
                "Untitled Jpg Viewer");
            ImageDocumentSession session = new(
                ImageDocumentSerializer.LoadAtomic(spritePath).Document,
                spritePath);

            ImageWorkspace workspace = ImageWorkspaceStorage.Import(session, untitledJpg);
            byte[] memoryPixels = workspace.CompositeCurrentFrame();
            Assert(memoryPixels.Length == workspace.Width * workspace.Height * 4, "Imported workspace pixels missing.");
            Assert(memoryPixels[0] > 200 && memoryPixels[1] > 200, "Imported Untitled.jpg pixels are not yellow in memory.");
            string? resolvedPath = ImageWorkspaceStorage.ResolveSessionImagePath(session);
            Assert(!string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath!), "Imported associate path was not created.");
            ImageViewerControl viewer = new(session, workspace);
            Form host = Host(viewer, "Image Viewer — Untitled.jpg");
            GateSuite.ShowHost(host);
            
            viewer.RefreshFromDocument();
            viewer.Preview.SyncClientSize();
            viewer.Preview.FitToView();
            PumpMessages(8, 35);
            using Bitmap? readback = viewer.Preview.ReadbackFrameToBitmap(settleFrames: 6);
            Assert(readback is not null, "GPU readback returned null for Untitled.jpg preview.");
            RenderStats stats = viewer.Preview.Renderer!.GetStats();
            Assert(stats.InstancesDrawn > 0, "No sprite instances were submitted for Untitled.jpg preview.");
            string capturePath = Path.Combine(captures, "13-image-viewer-untitled-jpg-readback.png");
            readback!.Save(capturePath, System.Drawing.Imaging.ImageFormat.Png);
            AssertYellowSquareReadback(readback);
            report.Images.Add(ImageResult.From(
                "Image Viewer Untitled.jpg Readback",
                "13-image-viewer-untitled-jpg-readback.png",
                MeasureBitmap(readback)));
            host.Close();
            host.Dispose();
        });

        RunCase(report, "Editor.Image.Viewer.GridOverlayReadback", () =>
        {
            ResourceService resourceService = Require(resources, "Resource service");
            string spritePath = resourceService.CreateResource(
                resourceService.AssetsRoot,
                ResourceKind.Image,
                "Grid Overlay Viewer");
            ImageDocumentSession session = new(
                ImageDocumentSerializer.LoadAtomic(spritePath).Document,
                spritePath);
            ImageWorkspace workspace = ImageWorkspace.CreateBlank(64, 64, Color.FromArgb(255, 220, 180, 60));
            ImageViewerControl viewer = new(session, workspace);
            Form host = Host(viewer, "Image Viewer — Grid Overlay");
            GateSuite.ShowHost(host);
            
            viewer.RefreshFromDocument();
            viewer.Preview.Overlay.ShowGrid = true;
            viewer.Preview.Overlay.GridWidth = 8;
            viewer.Preview.Overlay.GridHeight = 8;
            viewer.Preview.Overlay.GridColor = new RenderColor(0.1f, 1f, 0.2f, 1f);
            viewer.Preview.SyncClientSize();
            viewer.Preview.ActualPixels();
            PumpMessages(8, 35);
            using Bitmap? readback = viewer.Preview.ReadbackFrameToBitmap(settleFrames: 4);
            Assert(readback is not null, "Grid overlay readback returned null.");
            string capturePath = Path.Combine(captures, "14-image-viewer-grid-overlay.png");
            readback!.Save(capturePath, System.Drawing.Imaging.ImageFormat.Png);
            AssertGridLineColorPresent(readback, minGreen: 150, maxRed: 120);
            report.Images.Add(ImageResult.From(
                "Image Viewer Grid Overlay",
                "14-image-viewer-grid-overlay.png",
                MeasureBitmap(readback)));
            host.Close();
            host.Dispose();
        });

        RunCase(report, "Editor.Image.Interaction.PointerAccuracy", () =>
        {
            ResourceService resourceService = Require(resources, "Resource service");
            string spritePath = resourceService.CreateResource(
                resourceService.AssetsRoot,
                ResourceKind.Image,
                "Pointer Accuracy");
            ImageDocumentSession session = new(
                ImageDocumentSerializer.LoadAtomic(spritePath).Document,
                spritePath);
            ImageWorkspace imageWorkspace = ImageWorkspace.CreateBlank(64, 64, Color.Transparent);
            ImageEditorControl editor = new(session, imageWorkspace);
            Form host = Host(editor, "Image Editor — Pointer Accuracy");
            GateSuite.ShowHost(host);
            
            editor.Canvas.SyncClientSize();
            editor.Canvas.FitToView();
            PumpMessages(6, 30);
            editor.SetActiveTool(ImageToolKind.Brush);
            editor.SetBrushSize(8);
            editor.SetForegroundColor(Color.Black);
            Point targetPixel = new(63, 63);
            Point clientPoint = editor.Canvas.ImageToClient(targetPixel);
            editor.SimulateCanvasPointer(clientPoint, down: true, up: true);
            PumpMessages(4, 20);
            Color painted = RasterOperations.GetPixel(
                imageWorkspace.CurrentLayer!.Pixels, 64, 64, targetPixel.X, targetPixel.Y);
            Assert(painted.A > 0, "Brush did not paint at the requested pixel.");
            Assert(painted.R < 40 && painted.G < 40 && painted.B < 40,
                $"Brush painted at ({targetPixel.X},{targetPixel.Y}) but pixel is {painted}.");
            host.Close();
            host.Dispose();
        });

        RunCase(report, "Editor.Image.Viewer.OpenFromStudioShell", () =>
        {
            StudioServices services = Require(studioServices, "Studio services");
            ProjectSession session = Require(project, "Project fixture");
            ResourceFixture resourceFixture = Require(fixture, "Resource fixture");
            ResourceService resourceService = Require(resources, "Resource service");
            string spritePath = Path.Combine(resourceFixture.DestinationFolder, "Sample Image.image.json");
            Assert(File.Exists(spritePath), "Sample image fixture is missing.");

            using StudioShellForm studio = new(services, session, persistLayout: false);
            ResourceItem sprite = FindResource(resourceService.BuildTree(), spritePath)
                ?? throw new InvalidOperationException("Sample sprite missing from resource tree.");
            IStudioDocument document = studio.OpenStudioResource(sprite);
            ImageViewerDocument? viewer = document as ImageViewerDocument;
            Assert(viewer is not null && !viewer.IsDisposed, "Sprite open did not create a live image viewer document.");
            Assert(studio.OpenStudioDocuments.Count >= 1, "Studio shell did not register the opened image viewer.");

            GateSuite.ShowHost(studio);
            
            WinFormsApplication.DoEvents();
            ImageMetrics metrics = VisualCapture.Capture(
                studio,
                Path.Combine(captures, "12-image-viewer-from-shell.png"),
                captureFromScreen: true);
            report.Images.Add(ImageResult.From("Image Viewer From Shell", "12-image-viewer-from-shell.png", metrics));
        });

        RunCase(report, "Editor.Image.Viewer.DecoderSupportedFormats", () =>
        {
            string formatRoot = Path.Combine(outputRoot, "ImageFormats");
            Directory.CreateDirectory(formatRoot);
            string[] extensions = [".png", ".bmp", ".jpg", ".gif"];
            foreach (string extension in extensions)
            {
                string path = Path.Combine(formatRoot, "sample" + extension);
                using Bitmap bitmap = new(4, 3);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                    graphics.Clear(Color.FromArgb(255, 32, 160, 224));
                bitmap.Save(path, extension switch
                {
                    ".bmp" => System.Drawing.Imaging.ImageFormat.Bmp,
                    ".jpg" => System.Drawing.Imaging.ImageFormat.Jpeg,
                    ".gif" => System.Drawing.Imaging.ImageFormat.Gif,
                    _ => System.Drawing.Imaging.ImageFormat.Png,
                });
                Assert(
                    Genesis.Shared.Assets.ImageAssetDecoder.TryDecodeFrames(path, out var decoded, out string diagnostic)
                    && decoded.Width == 4
                    && decoded.Height == 3
                    && decoded.Frames.Count >= 1,
                    $"Failed to decode {extension}: {diagnostic}");
            }

            string webp = Path.Combine(formatRoot, "sample.webp");
            using (SKBitmap bitmap = new(new SKImageInfo(4, 3, SKColorType.Rgba8888, SKAlphaType.Opaque)))
            {
                bitmap.Erase(new SKColor(32, 160, 224));
                using SKImage image = SKImage.FromBitmap(bitmap);
                using SKData data = image.Encode(SKEncodedImageFormat.Webp, 100);
                using FileStream output = File.Create(webp);
                data.SaveTo(output);
            }
            Assert(
                Genesis.Shared.Assets.ImageAssetDecoder.TryDecodeFrames(webp, out var webpDecoded, out string webpError)
                && webpDecoded.Frames.Count == 1,
                $"Failed to decode WebP: {webpError}");

            string tga = Path.Combine(formatRoot, "sample.tga");
            WriteTinyTga(tga, 4, 3);
            Assert(
                Genesis.Shared.Assets.ImageAssetDecoder.TryDecodeFrames(tga, out var tgaDecoded, out string tgaError)
                && tgaDecoded.Width == 4
                && tgaDecoded.Height == 3,
                $"Failed to decode TGA: {tgaError}");
            string[] expected = [".png", ".bmp", ".jpg", ".jpeg", ".gif", ".webp", ".tga"];
            Assert(
                expected.All(extension => Genesis.Shared.Assets.ImageAssetDecoder.Extensions.Contains(extension)),
                "The shared image decoder does not advertise every required format.");
        });

        RunCase(report, "Editor.Image.Viewer.VisualAndInteraction", () =>
        {
            ResourceService resourceService = Require(resources, "Resource service");
            string emptyPath = resourceService.CreateResource(
                resourceService.AssetsRoot,
                ResourceKind.Image,
                "Empty Viewer State");
            ImageDocumentSession emptySession = new(
                ImageDocumentSerializer.LoadAtomic(emptyPath).Document,
                emptyPath);
            ImageWorkspace emptyWorkspace = ImageWorkspace.CreateBlank(64, 64, Color.Transparent);
            Form emptyHost = Host(new ImageViewerControl(emptySession, emptyWorkspace), "Image Viewer — Empty");
            ImageMetrics emptyMetrics = VisualCapture.Capture(
                emptyHost,
                Path.Combine(captures, "09-image-viewer-empty.png"),
                captureFromScreen: true);
            report.Images.Add(ImageResult.From("Image Viewer Empty", "09-image-viewer-empty.png", emptyMetrics));

            string animatedPath = resourceService.CreateResource(
                resourceService.AssetsRoot,
                ResourceKind.Image,
                "Animated Tileset Viewer");
            ImageDocument animatedDocument = ImageDocument.CreateDefault(64, 64);
            animatedDocument.Usage.Allowed |= ImageUsage.Tileset;
            animatedDocument.Usage.Tileset.TileWidth = 16;
            animatedDocument.Usage.Tileset.TileHeight = 16;
            ImageWorkspace animatedWorkspace = ImageWorkspace.CreateBlank(64, 64, Color.Transparent);
            animatedWorkspace.AddFrame(duplicateCurrent: true);
            for (int index = 0; index < animatedWorkspace.Frames.Count; index++)
            {
                animatedDocument.Frames.Add(new ImageFrame
                {
                    Id = animatedWorkspace.Frames[index].Id.ToString("N"),
                    Name = $"Frame {index + 1}",
                    DurationMilliseconds = index == 0 ? 80 : 140,
                    SourceRectangle = new ImageRectangle { Width = 64, Height = 64 },
                });
            }
            animatedDocument.Layers.Add(new ImageLayer { Name = "Base" });
            animatedDocument.Armature = new ImageArmature
            {
                Bones =
                [
                    new ImageBone { Name = "Root", Length = 24 },
                    new ImageBone { Name = "Child", Length = 16 },
                ],
            };
            ImageDocumentSerializer.SaveAtomic(animatedPath, animatedDocument);
            ImageDocumentSession animatedSession = new(animatedDocument, animatedPath);
            ImageViewerControl animatedViewer = new(animatedSession, animatedWorkspace);
            Form animatedHost = Host(animatedViewer, "Image Viewer — Animated Tileset");

            // Tile collision is authored in the VIEWER, against the grid drawn over the sheet.
            // 64x64 sheet with 16px tiles = a 4x4 grid, so index 5 is the tile at (16,16).
            animatedViewer.MarkSolidTilesMode = true;
            Assert(animatedViewer.SolidTileCount == 0, "A fresh tile set should have no solid tiles.");
            Assert(animatedViewer.ToggleSolidTileAt(20f, 20f), "Clicking a tile did not resolve a tile index.");
            Assert(
                animatedViewer.SolidTileCount == 1 && animatedDocument.Usage.Tileset.Collision.Contains(5),
                $"Expected tile 5 flagged solid, got [{string.Join(",", animatedDocument.Usage.Tileset.Collision)}].");
            Assert(animatedViewer.Undo(), "Tile collision toggle did not enter the undo history.");
            Assert(animatedViewer.SolidTileCount == 0, "Undo did not clear the solid tile.");
            Assert(animatedViewer.Redo() && animatedViewer.SolidTileCount == 1, "Redo did not restore the solid tile.");
            Assert(
                !animatedViewer.ToggleSolidTileAt(4000f, 4000f),
                "A click outside the sheet must not flag a tile.");
            animatedViewer.ToggleSolidTile(6);
            animatedViewer.ToggleSolidTile(9);
            ImageMetrics animatedMetrics = VisualCapture.Capture(
                animatedHost,
                Path.Combine(captures, "10-image-viewer-animated-tileset.png"),
                captureFromScreen: true);
            report.Images.Add(ImageResult.From(
                "Image Viewer Animated Tileset",
                "10-image-viewer-animated-tileset.png",
                animatedMetrics));

            ImageEditorControl editor = new(animatedSession, animatedWorkspace);
            int activeFrameIndex = animatedWorkspace.SelectedFrameIndex;
            editor.DrawStroke(new Point(6, 8), new Point(54, 50), Color.DeepSkyBlue, 5);
            Assert(animatedSession.History.CanUndo, "Editor stroke did not enter command history.");
            Color painted = RasterOperations.GetPixel(
                animatedWorkspace.Frames[activeFrameIndex].Layers[0].Pixels,
                64,
                64,
                24,
                25);
            Assert(painted.A > 0, "Scripted editor stroke did not modify pixels.");
            editor.Undo();
            editor.Redo();
            editor.AddRasterLayer("Highlights");
            editor.SetOnionSkin(true, 1, 1);
            Form editorHost = Host(editor, "Image Editor — Drawing, Layers, Onion, Rig");
            ImageMetrics editorMetrics = VisualCapture.Capture(
                editorHost,
                Path.Combine(captures, "11-image-editor-drawing-onion-rig.png"),
                captureFromScreen: true);
            report.Images.Add(ImageResult.From(
                "Image Editor Drawing Onion Rig",
                "11-image-editor-drawing-onion-rig.png",
                editorMetrics));
        });

        RunCase(report, "Editor.Image.Animation.Playback", () =>
        {
            ImageDocument document = ImageDocument.CreateDefault(32, 32);
            ImageWorkspace workspace = ImageWorkspace.CreateBlank(32, 32, Color.Transparent);
            workspace.AddFrame(duplicateCurrent: true);
            workspace.AddFrame(duplicateCurrent: true);
            ImageWorkspaceStorage.SynchronizeDocument(document, workspace);

            IReadOnlyList<int> all = ImageAnimationPlayback.GetPlayableFrameIndices(document, 0, workspace.Frames.Count);
            Assert(all.Count == 3 && all[0] == 0 && all[2] == 2, "All-frames clip should include every frame.");

            document.Tags.Add(new ImageAnimationTag
            {
                Name = "Middle",
                StartFrameId = document.Frames[1].Id,
                EndFrameId = document.Frames[1].Id,
                Direction = ImagePlaybackDirection.Forward,
                Loop = false,
            });
            IReadOnlyList<int> middle = ImageAnimationPlayback.GetPlayableFrameIndices(document, 1, workspace.Frames.Count);
            Assert(middle.Count == 1 && middle[0] == 1, "Tagged clip should resolve to its frame range.");

            document.Tags.Add(new ImageAnimationTag
            {
                Name = "Ping",
                StartFrameId = document.Frames[0].Id,
                EndFrameId = document.Frames[2].Id,
                Direction = ImagePlaybackDirection.PingPong,
                Loop = true,
            });
            (int next, int direction, bool stop) = ImageAnimationPlayback.AdvancePlayback(
                0, ImageAnimationPlayback.GetPlayableFrameIndices(document, 2, workspace.Frames.Count), 1,
                ImagePlaybackDirection.PingPong, true);
            Assert(next == 1 && direction == 1 && !stop, "Ping-pong playback should advance within the clip.");
        });

        RunCase(report, "Editor.Image.Animation.ExportSpriteSheet", () =>
        {
            ImageWorkspace workspace = ImageWorkspace.CreateBlank(4, 4, Color.Red);
            workspace.AddFrame(duplicateCurrent: true);
            byte[] sheet = new byte[workspace.Width * workspace.Frames.Count * workspace.Height * 4];
            for (int i = 0; i < workspace.Frames.Count; i++)
            {
                (_, _, byte[] pixels) = workspace.CompositeCurrentFrameFor(i);
                for (int y = 0; y < workspace.Height; y++)
                for (int x = 0; x < workspace.Width; x++)
                {
                    int sourceIndex = (y * workspace.Width + x) * 4;
                    int destIndex = (y * (workspace.Width * workspace.Frames.Count) + i * workspace.Width + x) * 4;
                    sheet[destIndex] = pixels[sourceIndex];
                    sheet[destIndex + 1] = pixels[sourceIndex + 1];
                    sheet[destIndex + 2] = pixels[sourceIndex + 2];
                    sheet[destIndex + 3] = pixels[sourceIndex + 3];
                }
            }
            Assert(sheet.Length == 4 * 4 * 2 * 4, "Sprite sheet buffer has unexpected size.");
            Assert(sheet[3] > 0, "Sprite sheet should contain opaque pixels.");
        });

        HeadlessHarness.BeginMajor(report, "Shell");

        RunCase(report, "Shell.UI.Splash.Visual", () =>
        {
            using SplashForm splash = new();
            ImageMetrics metrics = VisualCapture.Capture(
                splash,
                Path.Combine(captures, "01-launch-splash.png"));
            report.Images.Add(ImageResult.From("Launch splash", "01-launch-splash.png", metrics));
        });

        RunCase(report, "Shell.UI.ProjectHub.Visual", () =>
        {
            StudioServices services = Require(studioServices, "Studio services");
            using ProjectHubForm hub = new(services);
            ImageMetrics metrics = VisualCapture.Capture(
                hub,
                Path.Combine(captures, "02-project-hub.png"));
            report.Images.Add(ImageResult.From("Project Hub", "02-project-hub.png", metrics));
        });

        RunCase(report, "Shell.UI.ProjectHub.TemplatesNavigation", () =>
        {
            // The nav items were decorative labels with no handlers, so clicking Templates did
            // nothing. Drive the real click path rather than calling ShowSection directly.
            StudioServices services = Require(studioServices, "Studio services");
            using ProjectHubForm hub = new(services);
            GateSuite.ShowHost(hub);
            PumpMessages(4, 20);

            Assert(hub.CurrentSection == HubSection.Projects, "The hub should open on Projects.");

            Assert(
                hub.ClickNavigation(HubSection.Templates),
                "The hub has no Templates navigation item.");
            PumpMessages(4, 20);

            Assert(
                hub.CurrentSection == HubSection.Templates,
                "Clicking Templates did not switch the hub to the Templates page.");

            Assert(hub.ClickNavigation(HubSection.Projects), "The Projects navigation item disappeared.");
            PumpMessages(2, 20);
            Assert(hub.CurrentSection == HubSection.Projects, "Clicking Projects did not switch back.");
            hub.ClickNavigation(HubSection.Templates);
            PumpMessages(2, 20);

            // Every offered template must be one the service can actually build, and the 2D
            // platformer must be among them — the gallery cannot advertise what does not exist.
            Assert(
                ProjectTemplateCatalog.Available.Any(template => template.Id == "2D"),
                "The 2D Platformer template is not available in the catalogue.");
            Assert(
                ProjectTemplateCatalog.Available.Any(template => template.Id == "2DDungeonCrawler"),
                "The Crypts of Genesis template is not available in the catalogue.");
            Assert(
                ProjectTemplateCatalog.All.All(template => !template.Available || template.Contents.Count > 0),
                "An available template lists no contents, so its card would be blank.");

            ImageMetrics metrics = VisualCapture.Capture(
                hub,
                Path.Combine(captures, "02b-project-hub-templates.png"),
                captureFromScreen: true);
            report.Images.Add(ImageResult.From("Project Hub — Templates", "02b-project-hub-templates.png", metrics));

            hub.ShowSection(HubSection.Projects);
            Assert(hub.CurrentSection == HubSection.Projects, "Switching back to Projects failed.");
        });

        RunCase(report, "Shell.UI.NewProject.Visual", () =>
        {
            // New Project makes a BLANK project — the template cards moved to the hub's Templates
            // page, because choosing a starting point with content in it is not the same decision
            // as naming a new empty one.
            using NewProjectDialog dialog = new();
            Assert(
                dialog.SelectedTemplate == "Blank",
                $"New Project should default to Blank, got '{dialog.SelectedTemplate}'.");

            using NewProjectDialog fromTemplate = new("2D");
            Assert(
                fromTemplate.SelectedTemplate == "2D",
                "The dialog did not carry the template it was opened with.");

            ImageMetrics metrics = VisualCapture.Capture(
                dialog,
                Path.Combine(captures, "03-new-project.png"));
            report.Images.Add(ImageResult.From("New Project", "03-new-project.png", metrics));
        });

        RunCase(report, "Shell.UI.Preferences.Visual", () =>
        {
            StudioServices services = Require(studioServices, "Studio services");
            using PreferencesForm preferences = new(services.Settings);
            ImageMetrics metrics = VisualCapture.Capture(
                preferences,
                Path.Combine(captures, "04-preferences.png"));
            report.Images.Add(ImageResult.From("Preferences", "04-preferences.png", metrics));
        });

        RunCase(report, "Shell.UI.WindowCapture.RendersWithoutDesktopReadback", () =>
        {
            Color background = Color.FromArgb(31, 73, 127);
            Color foreground = Color.FromArgb(211, 61, 47);
            using Form probe = new()
            {
                AutoScaleMode = AutoScaleMode.None,
                BackColor = background,
                ClientSize = new Size(320, 220),
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10_000, -10_000),
                Text = "Window capture probe",
            };
            using Panel marker = new()
            {
                BackColor = foreground,
                Bounds = new Rectangle(200, 30, 90, 150),
            };
            probe.Controls.Add(marker);
            GateSuite.ShowHost(probe);
            PumpMessages(4, 20);

            using Bitmap pixels = VisualCapture.CaptureWindowPixels(probe);
            pixels.Save(Path.Combine(captures, "window-client-capture-probe.png"));
            Assert(
                pixels.GetPixel(24, 24).ToArgb() == background.ToArgb(),
                $"Window capture did not render the form's client background: {pixels.GetPixel(24, 24)}; expected {background}.");
            Assert(
                pixels.GetPixel(marker.Left + marker.Width / 2, marker.Top + marker.Height / 2).ToArgb()
                    == foreground.ToArgb(),
                "Window capture did not render child controls without desktop pixels.");
            probe.BackColor = foreground;
            marker.BackColor = background;
            // No message pump here: CaptureWindowPixels owns flushing pending paints.
            using Bitmap repainted = VisualCapture.CaptureWindowPixels(probe);
            Assert(repainted.GetPixel(24, 24).ToArgb() == foreground.ToArgb()
                && repainted.GetPixel(marker.Left + marker.Width / 2, marker.Top + marker.Height / 2).ToArgb() == background.ToArgb(),
                "Window capture returned stale pixels after controls changed colour.");
        });

        RunCase(report, "Shell.UI.StudioShell.MenuAndShortcuts", () =>
        {
            StudioServices services = Require(studioServices, "Studio services");
            ProjectSession session = Require(project, "Project fixture");
            using StudioShellForm studio = new(services, session, persistLayout: false);
            MenuStrip menu = studio.MainMenuStrip
                ?? throw new InvalidOperationException("The main menu is missing.");
            string[] expected = ["File", "Edit", "View", "Tools", "Help"];
            string[] actual = menu.Items
                .OfType<ToolStripMenuItem>()
                .Select(item => (item.Text ?? string.Empty).Replace("&", string.Empty, StringComparison.Ordinal))
                .ToArray();
            Assert(actual.SequenceEqual(expected), $"Unexpected menu map: {string.Join(", ", actual)}");

            ToolStripMenuItem edit = menu.Items
                .OfType<ToolStripMenuItem>()
                .Single(item => (item.Text ?? string.Empty).Contains("Edit", StringComparison.Ordinal));
            // These items advertise their shortcut without registering it: a registered menu
            // accelerator is consumed before the focused control is offered the key, which is what
            // made Ctrl+C copy a resource while the caret sat in a code editor. Focus-aware routing
            // is asserted by Editor.QoL.Shortcuts.*; here we only guard the ownership.
            AssertShortcutShownNotClaimed(edit, "Cut", "Ctrl+X");
            AssertShortcutShownNotClaimed(edit, "Copy", "Ctrl+C");
            AssertShortcutShownNotClaimed(edit, "Paste", "Ctrl+V");
            AssertShortcutShownNotClaimed(edit, "Rename", "F2");
            AssertShortcutShownNotClaimed(edit, "Delete", "Del");
            AssertShortcutShownNotClaimed(edit, "Duplicate", "Ctrl+D");
            Assert(studio.CommandCatalog.FindShortcut((int)(Keys.Control | Keys.D)) == "resource.duplicate",
                "Duplicate is not routed through the shared command catalog.");

            ResourceFixture resourceFixture = Require(fixture, "Resource fixture");
            Assert(
                studio.AssetBrowser.SelectPath(resourceFixture.Note),
                "The resource tree could not select a resource by path.");
            Assert(
                studio.AssetBrowser.ExecuteShortcut(Keys.Control | Keys.C),
                "Ctrl+C was not handled by the resource tree.");
            Assert(
                studio.AssetBrowser.SelectPath(resourceFixture.DestinationFolder),
                "The resource tree could not select the paste destination.");
            Assert(
                studio.AssetBrowser.ExecuteShortcut(Keys.Control | Keys.V),
                "Ctrl+V was not handled by the resource tree.");
            string expectedCopy = Path.Combine(
                resourceFixture.DestinationFolder,
                Path.GetFileName(resourceFixture.Note));
            Assert(File.Exists(expectedCopy), "Keyboard copy/paste did not create the resource.");

            ImageMetrics metrics = VisualCapture.Capture(
                studio,
                Path.Combine(captures, "05-studio-shell.png"),
                captureFromScreen: true);
            report.Images.Add(ImageResult.From("Studio Shell", "05-studio-shell.png", metrics));
        });

        RunCase(report, "Shell.Editor.DocumentRouting.OpenAndCapture", () =>
        {
            StudioServices services = Require(studioServices, "Studio services");
            ProjectSession session = Require(project, "Project fixture");
            ResourceFixture resourceFixture = Require(fixture, "Resource fixture");
            ResourceService resourceService = Require(resources, "Resource service");

            using StudioShellForm studio = new(services, session, persistLayout: false);
            ResourceItem note = FindResource(resourceService.BuildTree(), resourceFixture.Note)
                ?? throw new InvalidOperationException("Note resource missing from tree.");

            // Notes now route to the specialised suite editor through the registry
            // (the generic foundation surface remains only for unregistered kinds).
            IStudioDocument document = studio.OpenStudioResource(note);
            Assert(document is SuiteEditorDocument, "Note did not route to its specialised suite editor.");
            Assert(
                string.Equals(document.Resource.FullPath, note.FullPath, StringComparison.OrdinalIgnoreCase),
                "Opened document pointed at the wrong resource.");
            document.Save();
            Assert(!document.IsDirty, "Document remained dirty after save.");
            Assert(studio.OpenStudioDocuments.Count == 1, "Expected exactly one open document.");

            // Capture before VisualCapture disposes the shell.
            GateSuite.ShowHost(studio);
            
            WinFormsApplication.DoEvents();
            ImageMetrics metrics = VisualCapture.Capture(
                studio,
                Path.Combine(captures, "08-foundation-editor.png"),
                captureFromScreen: true);
            report.Images.Add(ImageResult.From("Document Routing", "08-foundation-editor.png", metrics));
        });

        RunCase(report, "Shell.Assets.FolderExpandDoesNotDoubleToggle", () =>
        {
            StudioServices services = Require(studioServices, "Studio services");
            ProjectSession session = Require(project, "Project fixture");
            ResourceFixture resourceFixture = Require(fixture, "Resource fixture");
            using StudioShellForm studio = new(services, session, persistLayout: false);
            Assert(
                studio.AssetBrowser.SelectPath(resourceFixture.DestinationFolder),
                "Could not select Destination folder.");
            TreeNode? folder = studio.AssetBrowser.SelectedNode;
            Assert(folder is not null, "Selected folder node was null.");
            Assert(folder!.Nodes.Count > 0, "Destination folder has no children to expand.");

            folder.Collapse();
            Assert(!folder.IsExpanded, "Folder should start collapsed for the toggle check.");
            folder.Expand();
            Assert(folder.IsExpanded, "Folder Expand did not expand the node.");
            folder.Collapse();
            Assert(!folder.IsExpanded, "Folder Collapse did not collapse the node.");
        });

        ctx.Resources = resources;
        ctx.Project = project;
        ctx.StudioServices = studioServices;
        ctx.Fixture = fixture;
        Suites.ImageEditorSuite.RunFeatureSections(ctx);
        Suites.QualityOfLifeSuite.Run(ctx);
        Suites.EditorVisualCompatSuite.Run(ctx);
        Suites.ShaderWorkspaceSuite.Run(ctx);
        Suites.ShaderTerrainPreviewSuite.Run(ctx);
        Suites.RoomSurfacePlacementSuite.Run(ctx);
        Suites.RoomSurfaceAlignmentSuite.Run(ctx);
        Suites.RoomTerrainPreviewSuite.Run(ctx);
        Suites.RoomWorkspaceSuite.Run(ctx);
        Suites.RoomHierarchyTransformsSuite.Run(ctx);
        Suites.RoomMetricGridToolsSuite.Run(ctx);
        Suites.RoomContextInspectorSuite.Run(ctx);
        Suites.EditorInteractionSuite.Run(ctx);
        Suites.ResourceNamesSuite.Run(ctx);
        Suites.StudioFoundationSuite.Run(ctx);
        Suites.StudioPolishSuite.Run(ctx);
        Suites.ResourceLibrarySuite.Run(ctx);
        Suites.ResourceTagsSuite.Run(ctx);
        Suites.ParticleWorkbenchSuite.Run(ctx);
        Suites.PgslPreprocessingSuite.Run(ctx);
        Suites.RoomPalettePlacementSuite.Run(ctx);
        Suites.RoomObjectsPanelSuite.Run(ctx);
        Suites.RoomCompletionSuite.Run(ctx);
        Suites.RoomFeedbackSuite.Run(ctx);
        Suites.RoomNavigationSuite.Run(ctx);
        Suites.RoomEnvironmentPreviewSuite.Run(ctx);
        Suites.SuiteEditorSuite.Run(ctx);
        Suites.TwoDPipelineSuite.Run(ctx);
        Suites.TwoDShowcaseSuite.Run(ctx);
        Suites.LuigisMansionSuite.Run(ctx);

        return Finish(report, outputRoot, fastBuildGate: false);
    }

    private static void RunFocusedTarget(HeadlessContext ctx, string target)
    {
        string normalized = target.Trim().TrimStart('-').Replace('_', '-').ToLowerInvariant();
        switch (normalized)
        {
            case "particle-workbench":
            case "editor-h21":
                Suites.ParticleWorkbenchSuite.Run(ctx);
                break;
            case "resource-tags":
            case "editor-h20":
                Suites.ResourceTagsSuite.Run(ctx);
                break;
            case "resource-library":
            case "editor-h17":
                Suites.ResourceLibrarySuite.Run(ctx);
                break;
            case "studio-polish":
            case "editor-h13":
                Suites.StudioPolishSuite.Run(ctx);
                break;
            case "studio-foundation":
            case "editor-h12":
                Suites.StudioFoundationSuite.Run(ctx);
                break;
            case "resource-names":
            case "editor-h11":
                Suites.ResourceNamesSuite.Run(ctx);
                break;
            case "editor-interaction":
            case "editor-h10":
                PrepareFocusedProject(ctx, requireStudioServices: false);
                Suites.EditorInteractionSuite.Run(ctx);
                Suites.RigPoseControlSuite.Run(ctx);
                break;
            case "render":
            case "rendering":
                Suites.RenderBackendSuite.Run(ctx);
                break;
            case "runtime":
                Suites.RuntimeSuite.Run(ctx);
                break;
            case "engine-systems":
                Suites.EngineSystemsSuite.Run(ctx);
                break;
            case "ecs":
                Suites.EcsFoundationSuite.Run(ctx);
                break;
            case "theme":
                Suites.ThemeImageSuite.Run(ctx);
                break;
            case "qol":
            case "quality-of-life":
                ctx.StudioServices = new StudioServices(
                    new SettingsService(),
                    new ProjectService(),
                    new ProjectValidator(),
                    new StudioLog(Path.Combine(ctx.Logs, "focused-qol-studio.log")));
                Suites.QualityOfLifeSuite.Run(ctx);
                break;
            case "editor-compat":
            case "editor-visual-compat":
                PrepareFocusedProject(ctx, requireStudioServices: false);
                Suites.EditorVisualCompatSuite.Run(ctx);
                break;
            case "editor-suite":
            case "suite-editors":
                PrepareFocusedProject(ctx, requireStudioServices: true);
                Suites.SuiteEditorSuite.Run(ctx);
                break;
            case "walkthrough":
            case "terrain-walkthrough":
                PrepareFocusedProject(ctx, requireStudioServices: true);
                Suites.TerrainWalkthroughSuite.Run(ctx);
                break;
            case "image-features":
                PrepareFocusedProject(ctx, requireStudioServices: false);
                Suites.ImageEditorSuite.RunFeatureSections(ctx);
                break;
            case "model-viewer":
                PrepareFocusedProject(ctx, requireStudioServices: true);
                Suites.ModelViewerSuite.Run(ctx);
                break;
            case "shader-workspace":
                Suites.ShaderWorkspaceSuite.Run(ctx);
                Suites.ShaderTerrainPreviewSuite.Run(ctx);
                break;
            case "shader-terrain-preview":
                Suites.ShaderTerrainPreviewSuite.Run(ctx);
                break;
            case "room-workspace":
                Suites.RoomCompletionSuite.Run(ctx);
                Suites.RoomSurfacePlacementSuite.Run(ctx);
                Suites.RoomSurfaceAlignmentSuite.Run(ctx);
                Suites.RoomTerrainPreviewSuite.Run(ctx);
                Suites.RoomWorkspaceSuite.Run(ctx);
                Suites.RoomHierarchyTransformsSuite.Run(ctx);
                Suites.RoomMetricGridToolsSuite.Run(ctx);
                Suites.RoomContextInspectorSuite.Run(ctx);
                Suites.PgslPreprocessingSuite.Run(ctx);
                Suites.RoomPalettePlacementSuite.Run(ctx);
                Suites.RoomObjectsPanelSuite.Run(ctx);
                Suites.RoomEnvironmentPreviewSuite.Run(ctx);
                break;
            case "room-shell":
                Suites.RoomContextInspectorSuite.Run(ctx);
                Suites.PgslPreprocessingSuite.Run(ctx);
                Suites.RoomSurfaceAlignmentSuite.Run(ctx);
                Suites.RoomWorkspaceSuite.Run(ctx);
                Suites.RoomObjectsPanelSuite.Run(ctx);
                Suites.RoomPalettePlacementSuite.Run(ctx);
                Suites.RoomMetricGridToolsSuite.Run(ctx);
                break;
            case "room-completion":
                Suites.RoomCompletionSuite.Run(ctx);
                break;
            case "room-feedback":
                Suites.RoomFeedbackSuite.Run(ctx);
                break;
            case "room-navigation":
                Suites.RoomNavigationSuite.Run(ctx);
                break;
            case "room-profile":
                Suites.RoomFeedbackSuite.Profile(ctx);
                break;
            case "room-environment-preview":
                Suites.RoomEnvironmentPreviewSuite.Run(ctx);
                break;
            case "room-surface-placement":
                Suites.RoomSurfacePlacementSuite.Run(ctx);
                break;
            case "room-terrain-preview":
                Suites.RoomTerrainPreviewSuite.Run(ctx);
                break;
            case "view-options":
                PrepareFocusedProject(ctx, requireStudioServices: true);
                Suites.Editor3DInspectionSuite.Run(ctx);
                Suites.ModelBranchSidebarSuite.Run(ctx);
                break;
            case "model-editor":
                PrepareFocusedProject(ctx, requireStudioServices: false);
                Suites.ModelEditorResetSuite.Run(ctx);
                Suites.ModelPoseWorkflowSuite.Run(ctx);
                break;
            case "model-system":
                PrepareFocusedProject(ctx, requireStudioServices: true);
                Suites.ModelRigWizardSuite.Run(ctx);
                Suites.ModelArcherAnimationSuite.Run(ctx);
                Suites.ModelMotionImportSuite.Run(ctx);
                Suites.ModelRigEditingSuite.Run(ctx);
                Suites.ModelRefinementSuite.Run(ctx);
                Suites.ModelRigPlacementSuite.Run(ctx);
                Suites.ModelViewerSuite.Run(ctx);
                Suites.ModelEditorResetSuite.Run(ctx);
                Suites.ModelPoseWorkflowSuite.Run(ctx);
                Suites.ModelIntakeSuite.Run(ctx);
                break;
            case "model-motion-import":
                PrepareFocusedProject(ctx, requireStudioServices: true);
                Suites.ModelMotionImportSuite.Run(ctx);
                break;
            case "model-archer":
                PrepareFocusedProject(ctx, requireStudioServices: true);
                Suites.ModelArcherAnimationSuite.Run(ctx);
                break;
            case "rig-wizard":
                PrepareFocusedProject(ctx, requireStudioServices: true);
                Suites.ModelRigWizardSuite.Run(ctx);
                break;
            case "rig-editing":
                PrepareFocusedProject(ctx, requireStudioServices: true);
                Suites.ModelRigEditingSuite.Run(ctx);
                break;
            case "model-animation":
                PrepareFocusedProject(ctx, requireStudioServices: false);
                Suites.ModelPoseWorkflowSuite.Run(ctx);
                break;
            case "model-intake":
                PrepareFocusedProject(ctx, requireStudioServices: false);
                Suites.ModelIntakeSuite.Run(ctx);
                break;
            case "luigis-mansion":
            case "mansion":
                Suites.LuigisMansionSuite.Run(ctx);
                break;
            case "2d-showcase":
            case "resource-folders":
                Suites.TwoDShowcaseSuite.Run(ctx);
                break;
            case "2d-pipeline":
                PrepareFocusedProject(ctx, requireStudioServices: false);
                Suites.TwoDPipelineSuite.Run(ctx);
                break;
            case "shell-layout":
                Suites.ShellLayoutSuite.Run(ctx);
                break;
            case "resource-search":
                Suites.ResourceSearchSuite.Run(ctx);
                break;
            default:
                Suites.GateSuite.RunFocused(ctx, normalized);
                break;
        }
    }

    private static void PrepareFocusedProject(HeadlessContext ctx, bool requireStudioServices)
    {
        string fixtureRoot = Path.Combine(ctx.Workspace, "FocusedFixture");
        Directory.CreateDirectory(fixtureRoot);
        ProjectService projects = new();
        ProjectSession project = projects.CreateProject(fixtureRoot, "Focused Feature");
        ctx.Project = project;
        ctx.Resources = new ResourceService(project);

        if (requireStudioServices)
        {
            ctx.StudioServices = new StudioServices(
                new SettingsService(),
                projects,
                new ProjectValidator(),
                new StudioLog(Path.Combine(ctx.Logs, "focused-studio.log")));
        }
    }

    /// <summary>Writes the artefacts and reports the outcome. Shared by both profiles.</summary>
    private static int Finish(TestReport report, string outputRoot, bool fastBuildGate)
    {
        report.CompletedUtc = DateTime.UtcNow;
        report.Passed = report.Tests.All(test => test.Passed);
        File.WriteAllText(
            Path.Combine(outputRoot, "results.json"),
            JsonSerializer.Serialize(report, JsonOptions));
        WriteSummary(report, Path.Combine(outputRoot, "summary.txt"));

        Console.WriteLine();
        string profileLabel = report.Profile.StartsWith("Focused Test", StringComparison.Ordinal)
            ? "FOCUSED TEST"
            : fastBuildGate ? "BUILD GATE" : "FULL REGRESSION";
        Console.WriteLine(
            report.Passed
                ? $"{profileLabel} PASSED — {report.Tests.Count} checks, {report.Images.Count} images"
                : $"{profileLabel} FAILED — {report.Tests.Count(test => !test.Passed)} failing checks");
        Console.WriteLine($"Artifacts: {outputRoot}");
        return report.Passed ? 0 : 1;
    }

    private static ResourceFixture BuildResourceFixture(ResourceService resources)
    {
        string characters = resources.CreateFolder(Path.Combine(resources.AssetsRoot, "Objects"), "Characters");
        string gameplay = resources.CreateFolder(Path.Combine(resources.AssetsRoot, "Scripts"), "Gameplay");
        string destination = resources.CreateFolder(Path.Combine(resources.AssetsRoot, "Objects"), "Destination");

        foreach (ResourceDefinition definition in ResourceDefinitions.All)
        {
            resources.CreateResource(
                resources.AssetsRoot,
                definition.Kind,
                $"Sample {definition.DisplayName}");
        }

        string playerObject = resources.CreateResource(characters, ResourceKind.GameObject, "Player");
        string heroObject = resources.Rename(playerObject, "Hero");
        string objectsFolder = Path.Combine(resources.AssetsRoot, "Objects");
        string movedHero = resources.Move(heroObject, objectsFolder);
        string heroCopy = resources.Copy(movedHero, destination);

        string script = resources.CreateResource(gameplay, ResourceKind.PgslScript, "Player Controller");
        resources.SetClipboard(ResourceClipboardOperation.Cut, [script]);
        string scriptsFolder = Path.Combine(resources.AssetsRoot, "Scripts");
        IReadOnlyList<string> cutResults = resources.Paste(scriptsFolder);
        Assert(cutResults.Count == 1, "Cut/paste did not move exactly one resource.");

        string note = resources.CreateResource(resources.AssetsRoot, ResourceKind.Note, "Design Notes");
        string noteDuplicate = resources.Duplicate(note);
        Assert(File.Exists(noteDuplicate), "Duplicate did not create a file.");

        return new ResourceFixture(
            characters,
            gameplay,
            destination,
            movedHero,
            heroCopy,
            cutResults[0],
            note);
    }

    private static void VerifyResourceFixture(
        ResourceService resources,
        ResourceFixture fixture)
    {
        Assert(Directory.Exists(fixture.CharactersFolder), "Characters folder is missing.");
        Assert(Directory.Exists(fixture.GameplayFolder), "Gameplay folder is missing.");
        Assert(File.Exists(fixture.HeroObject), "Rename/move failed for the object resource.");
        Assert(File.Exists(fixture.HeroCopy), "Copy failed for the object resource.");
        Assert(File.Exists(fixture.Script), "Cut/paste failed for the PGSL resource.");
        Assert(File.Exists(fixture.Note), "Note resource was not created.");

        ResourceItem tree = resources.BuildTree();
        int resourcesInTree = CountResources(tree);
        Assert(
            resourcesInTree >= ResourceDefinitions.All.Count + 5,
            $"Resource tree contains only {resourcesInTree} resources.");
    }

    private static ResourceItem? FindResource(ResourceItem root, string fullPath)
    {
        if (string.Equals(root.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        foreach (ResourceItem child in root.Children)
        {
            ResourceItem? match = FindResource(child, fullPath);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static int CountResources(ResourceItem item) =>
        (item.IsFolder ? 0 : 1) + item.Children.Sum(CountResources);

    private static Guid ReadAssetGuid(string resourcePath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resourcePath + ".meta"));
        string? text = document.RootElement.GetProperty("guid").GetString();
        return Guid.TryParseExact(text, "N", out Guid id) ? id : Guid.Empty;
    }

    private static void AssertShortcut(ToolStripMenuItem menu, string text, Keys expected)
    {
        ToolStripMenuItem item = menu.DropDownItems
            .OfType<ToolStripMenuItem>()
            .Single(candidate =>
                (candidate.Text ?? string.Empty).Contains(text, StringComparison.Ordinal));
        Assert(item.ShortcutKeys == expected, $"{text} shortcut is {item.ShortcutKeys}, expected {expected}.");
    }

    /// <summary>Asserts a menu item shows a shortcut it deliberately does not register.</summary>
    private static void AssertShortcutShownNotClaimed(ToolStripMenuItem menu, string text, string display)
    {
        ToolStripMenuItem item = menu.DropDownItems
            .OfType<ToolStripMenuItem>()
            .Single(candidate =>
                (candidate.Text ?? string.Empty).Contains(text, StringComparison.Ordinal));
        Assert(
            item.ShortcutKeys == Keys.None,
            $"{text} registers {item.ShortcutKeys}; the menu would consume it before the focused editor.");
        Assert(
            item.ShortcutKeyDisplayString == display,
            $"{text} shows '{item.ShortcutKeyDisplayString}' instead of '{display}'.");
    }

    private static Form Host(Control control, string title)
    {
        Form form = new()
        {
            Text = title,
            ClientSize = new Size(1280, 760),
            StartPosition = FormStartPosition.Manual,
            BackColor = Color.FromArgb(20, 22, 28),
        };
        control.Dock = DockStyle.Fill;
        form.Controls.Add(control);
        return form;
    }

    private static void PumpMessages(int iterations, int delayMilliseconds)
    {
        for (int index = 0; index < iterations; index++)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(delayMilliseconds);
        }
    }

    private static ImageMetrics MeasureBitmap(Bitmap bitmap)
    {
        HashSet<int> colors = [];
        int samples = 0;
        int opaque = 0;
        long luminance = 0;
        int stepX = Math.Max(1, bitmap.Width / 120);
        int stepY = Math.Max(1, bitmap.Height / 80);

        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                Color color = bitmap.GetPixel(x, y);
                colors.Add(color.ToArgb());
                samples++;
                if (color.A > 245)
                    opaque++;
                luminance += (color.R * 299L + color.G * 587L + color.B * 114L) / 1000L;
            }
        }

        return new ImageMetrics(
            bitmap.Width,
            bitmap.Height,
            colors.Count,
            samples == 0 ? 0 : opaque / (double)samples,
            samples == 0 ? 0 : luminance / (double)samples);
    }

    private static string ResolveUntitledJpgFixture()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Untitled.jpg"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Untitled.jpg")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Untitled.jpg")),
        ];
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static void AssertYellowSquareReadback(Bitmap bitmap)
    {
        int marginX = Math.Max(1, bitmap.Width / 5);
        int marginY = Math.Max(1, bitmap.Height / 5);
        int stepX = Math.Max(1, (bitmap.Width - marginX * 2) / 40);
        int stepY = Math.Max(1, (bitmap.Height - marginY * 2) / 40);
        long red = 0;
        long green = 0;
        long blue = 0;
        int samples = 0;

        for (int y = marginY; y < bitmap.Height - marginY; y += stepY)
        {
            for (int x = marginX; x < bitmap.Width - marginX; x += stepX)
            {
                Color color = bitmap.GetPixel(x, y);
                if (color.A < 200)
                    continue;
                red += color.R;
                green += color.G;
                blue += color.B;
                samples++;
            }
        }

        Assert(samples > 0, "Untitled.jpg readback did not contain any opaque samples.");
        double avgRed = red / (double)samples;
        double avgGreen = green / (double)samples;
        double avgBlue = blue / (double)samples;
        bool isGray = avgRed < 90
            && avgGreen < 90
            && avgBlue < 90
            && Math.Abs(avgRed - avgGreen) < 20
            && Math.Abs(avgGreen - avgBlue) < 20;
        Assert(!isGray,
            $"Preview readback is gray ({avgRed:F0}, {avgGreen:F0}, {avgBlue:F0}) — Untitled.jpg did not render.");
        Assert(
            avgRed > 150 && avgGreen > 150 && avgBlue < 140,
            $"Expected a yellow square preview, got average RGB ({avgRed:F0}, {avgGreen:F0}, {avgBlue:F0}).");
    }

    private static void AssertGridLineColorPresent(Bitmap bitmap, int minGreen, int maxRed)
    {
        int hits = 0;
        int cx = bitmap.Width / 2;
        int cy = bitmap.Height / 2;
        for (int y = cy - 34; y <= cy + 34; y++)
        {
            for (int x = cx - 34; x <= cx + 34; x++)
            {
                if ((uint)x >= (uint)bitmap.Width || (uint)y >= (uint)bitmap.Height) continue;
                Color color = bitmap.GetPixel(x, y);
                if (color.G > color.R + 40 && color.G > color.B + 40 && color.G > minGreen)
                    hits++;
            }
        }
        Assert(hits >= 2, $"Grid overlay colour was not visible in readback (hits={hits}).");
    }

    private static void AssertPinkAndWhiteReadback(Bitmap bitmap)
    {
        int pink = 0;
        int white = 0;
        int margin = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 8);
        int step = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 24);
        for (int y = margin; y < bitmap.Height - margin; y += step)
        {
            for (int x = margin; x < bitmap.Width - margin; x += step)
            {
                Color color = bitmap.GetPixel(x, y);
                if (color.A < 200) continue;
                if (color.R > 180 && color.G < 120 && color.B > 120) pink++;
                if (color.R > 200 && color.G > 200 && color.B > 200) white++;
            }
        }
        Assert(pink >= 3, $"Expected pink square pixels in editor readback (samples={pink}).");
        Assert(white >= 3, $"Expected white fill pixels in editor readback (samples={white}).");
    }

    private static void WriteTinyTga(string path, int width, int height)
    {
        byte[] header = new byte[18];
        header[2] = 2;
        header[12] = (byte)(width & 0xff);
        header[13] = (byte)(width >> 8);
        header[14] = (byte)(height & 0xff);
        header[15] = (byte)(height >> 8);
        header[16] = 32;
        header[17] = 0x28;
        using FileStream output = File.Create(path);
        output.Write(header);
        for (int i = 0; i < width * height; i++)
        {
            output.WriteByte(224);
            output.WriteByte(160);
            output.WriteByte(32);
            output.WriteByte(255);
        }
    }

    private static void RunCase(TestReport report, string name, Action action) =>
        HeadlessHarness.RunCase(report, name, action);

    private static void Assert(bool condition, string message) =>
        HeadlessHarness.Assert(condition, message);

    private static T Require<T>(T? value, string description) where T : class =>
        HeadlessHarness.Require(value, description);

    private static string ResolveOutput(string[] args)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--output", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestResults", "latest"));
    }

    private static string? ReadOption(string[] args, string option)
    {
        int index = Array.FindIndex(args, argument =>
            string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a test target.");
        return args[index + 1];
    }

    /// <summary>
    /// Empty the capture/log directory before a run, tolerating a held handle.
    /// </summary>
    /// <remarks>
    /// This used to be a bare <c>Directory.Delete(recursive: true)</c>, which throws
    /// <see cref="IOException"/> whenever anything holds the directory — a shell sitting in it, an
    /// image viewer with a capture open, an antivirus scan mid-pass. That unhandled exception took
    /// the entire suite down before a single test ran, and because a .NET crash exits with a code
    /// cmd reads as *negative*, Build.bat's old <c>if errorlevel 1</c> printed BUILD SUCCESSFUL over
    /// the top of it (NEXT-043, now also fixed).
    ///
    /// Clearing the *contents* rather than the directory itself survives the common case, because a
    /// held directory handle blocks removing the directory but not the files inside it. Anything
    /// genuinely locked is reported and skipped: a stale leftover PNG is harmless (the summary is
    /// regenerated from what this run actually wrote, so it cannot claim an un-run test), whereas
    /// refusing to run at all is not.
    /// </remarks>
    private static void PrepareOutput(string outputRoot)
    {
        if (Directory.Exists(outputRoot))
        {
            List<string> blocked = [];
            ClearDirectoryContents(outputRoot, blocked);

            if (blocked.Count > 0)
            {
                Console.Error.WriteLine(
                    $"[warn] {blocked.Count} item(s) under '{outputRoot}' are locked and were left in place:");
                foreach (string item in blocked.Take(5))
                {
                    Console.Error.WriteLine($"[warn]   {item}");
                }

                if (blocked.Count > 5)
                {
                    Console.Error.WriteLine($"[warn]   … and {blocked.Count - 5} more");
                }
            }
        }

        Directory.CreateDirectory(outputRoot);
    }

    private static void ClearDirectoryContents(string directory, List<string> blocked)
    {
        foreach (string file in SafeEnumerate(() => Directory.GetFiles(directory), blocked, directory))
        {
            if (!TryDelete(() => File.Delete(file)))
            {
                blocked.Add(file);
            }
        }

        foreach (string child in SafeEnumerate(() => Directory.GetDirectories(directory), blocked, directory))
        {
            ClearDirectoryContents(child, blocked);
            if (!TryDelete(() => Directory.Delete(child, recursive: false)))
            {
                blocked.Add(child);
            }
        }
    }

    private static string[] SafeEnumerate(Func<string[]> enumerate, List<string> blocked, string directory)
    {
        try
        {
            return enumerate();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            blocked.Add($"{directory} (could not enumerate: {exception.Message})");
            return [];
        }
    }

    /// <summary>
    /// Delete with a short backoff. Most locks here are transient — an antivirus pass or a handle
    /// the OS has not released yet — so a couple of retries clears them; a genuinely held handle
    /// never will, and the caller records it instead of failing the run.
    /// </summary>
    private static bool TryDelete(Action delete)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                delete();
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2)
                {
                    return false;
                }

                Thread.Sleep(60 * (attempt + 1));
            }
        }

        return false;
    }

    private static void WriteSummary(TestReport report, string file) =>
        HeadlessHarness.WriteSummary(report, file);
}
