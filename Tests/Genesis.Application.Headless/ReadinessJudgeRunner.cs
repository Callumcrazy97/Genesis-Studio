using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Headless.Suites;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Viewport;
using Genesis.Shared.Assets;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Headless;

internal sealed record JudgeSurface(string Name, ResourceKind? Kind, string Workflow, bool RequiredFor2D = true);
internal sealed record JudgeCapture(string Surface, string Variant, string File, string Sha256, int DeviceDpi,
    string ScaleEvidence, IReadOnlyList<string> Findings);
internal sealed record JudgeCaptureManifest(string ProductFingerprint, IReadOnlyList<JudgeCapture> Captures);
internal sealed record JudgeRating(string Surface, int Usability, int Aesthetics, string Notes,
    IReadOnlyList<string> CaptureHashes);
internal sealed record JudgeReview(string ProductFingerprint, string ReviewedBy, IReadOnlyList<JudgeRating> Ratings);
internal sealed record JudgeDecision(string Surface, bool RequiredFor2D, string Functionality, string Usability,
    string Aesthetics, int? UsabilityScore, int? AestheticsScore, IReadOnlyList<string> Findings);

/// <summary>Serial, evidence-based acceptance. Missing evidence can never manufacture a pass.</summary>
internal static class ReadinessJudgeRunner
{
    internal static readonly JudgeSurface[] Surfaces =
    [
        new("Shell", null, "Studio.Foundation.Shell"),
        new("Image", ResourceKind.Image, "Editor.Image"),
        new("Room", ResourceKind.Room, "Editor.Room"),
        new("Object", ResourceKind.GameObject, "Editor.Object"),
        new("Script", ResourceKind.PgslScript, "Editor.Script"),
        new("Audio", ResourceKind.Audio, "Editor.Audio"),
        new("Shader", ResourceKind.Shader, "Editor.Shader"),
        new("Particle", ResourceKind.Particle, "Editor.Particle"),
        new("Physics", ResourceKind.Physics, "Editor.Physics"),
        new("Pathing", ResourceKind.Pathing, "Editor.Pathing"),
        new("UI", ResourceKind.UserInterface, "Editor.UI"),
        new("Note", ResourceKind.Note, "Editor.Note"),
        new("Model", ResourceKind.Model, "Editor.Model", false),
        new("Terrain", ResourceKind.Terrain, "Editor.Terrain", false),
    ];

    internal static string ProductFingerprint(string? directory = null)
    {
        string root = Path.GetFullPath(directory ?? AppContext.BaseDirectory);
        List<string> files = [];
        AddBinaries(root);
        string player = Path.Combine(root, "Player");
        if (Directory.Exists(player)) AddBinaries(player);
        foreach (string content in new[] { "Assets", "Projects/Templates", "Templates", "Themes", "Shaders", "runtimes/win-x64/native", "Player/Shaders", "Player/runtimes/win-x64/native" })
        {
            string folder = Path.Combine(root, content.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(folder)) files.AddRange(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories));
        }
        StringBuilder hashes = new();
        foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(file => Path.GetRelativePath(root, file), StringComparer.Ordinal))
        {
            using FileStream source = File.OpenRead(file);
            hashes.Append(Path.GetRelativePath(root, file).Replace('\\', '/')).Append(':')
                .Append(Convert.ToHexString(SHA256.HashData(source))).AppendLine();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashes.ToString())));

        void AddBinaries(string folder)
        {
            files.AddRange(Directory.EnumerateFiles(folder, "*.dll"));
            files.AddRange(Directory.EnumerateFiles(folder, "*.exe"));
        }
    }

    public static int Evaluate(string root, string? reviewFile)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        string fingerprint = ProductFingerprint();
        List<TestCaseResult> tests = [];
        List<string> rejectedEvidence = [];
        foreach (string file in Directory.EnumerateFiles(root, "results.json", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc))
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(file));
            if (!json.RootElement.TryGetProperty("ProductFingerprint", out JsonElement identity)
                || identity.GetString() != fingerprint)
            {
                rejectedEvidence.Add(Path.GetRelativePath(root, file) + ": stale or unidentified build");
                continue;
            }
            tests.AddRange(JsonSerializer.Deserialize<List<TestCaseResult>>(json.RootElement.GetProperty("Tests").GetRawText()) ?? []);
        }
        tests = tests.GroupBy(test => test.Name, StringComparer.Ordinal).Select(group => group.Last()).ToList();
        string captureFile = Path.Combine(root, "Captures", "capture-manifest.json");
        JudgeCaptureManifest? manifest = File.Exists(captureFile)
            ? JsonSerializer.Deserialize<JudgeCaptureManifest>(File.ReadAllText(captureFile)) : null;
        JudgeReview? review = reviewFile is not null
            ? JsonSerializer.Deserialize<JudgeReview>(File.ReadAllText(Path.GetFullPath(reviewFile))) : null;
        List<JudgeDecision> decisions = [];
        foreach (JudgeSurface surface in Surfaces)
        {
            TestCaseResult[] evidence = tests.Where(test => test.Name == surface.Workflow
                || test.Name.StartsWith(surface.Workflow + ".", StringComparison.Ordinal)).ToArray();
            JudgeCapture[] captures = manifest?.ProductFingerprint == fingerprint
                ? manifest.Captures.Where(capture => capture.Surface == surface.Name).ToArray() : [];
            JudgeRating? rating = review?.ProductFingerprint == fingerprint && !string.IsNullOrWhiteSpace(review.ReviewedBy)
                ? review.Ratings.FirstOrDefault(item => item.Surface == surface.Name) : null;
            decisions.Add(Decide(surface, evidence, captures, rating, root));
        }
        string[] mandatory = ["Editor.ResourceInspector", "Editor.Model", "Runtime.PGSL.TwoD",
            "Runtime.Pgsl.ScriptDiscoveryExcludesOutputsAndCachesEmptyProjects", "Runtime.Pgsl.CommandAutoTest",
            "Acceptance.CodeAssistance.AssetDefinitionsKeepEditingSpaceAndSave",
            "Acceptance.CodeAssistance.ScriptCodeKeepsEditingSpace", "Acceptance.CodeAssistance.PersistentPositionAndTypedArguments",
            "Acceptance.CodeAssistance.DeclarationsCompleteFieldsAndTypedValues",
            "Acceptance.CodeAssistance.PgslCommandsAndLocalFunctionHints", "Showcase.RealRoomCreateTitleStartMovementAndPause",
            "Editor.Room.SpriteOrigins.MatchGameplayPixelsPickingGhostAndReopen",
            "Editor.ParticleWorkbench.TwoD.FireRainAndPortalPlayOnAllBackends",
            "Editor.ParticleWorkbench.TwoD.PixelSizeDirectionPlacementAndLiveReloadOnAllBackends",
            "Editor.ParticleWorkbench.TwoD.SoftwareRingLivesInXYAndLocalPlacementMovesItsParticles",
            "Editor.ParticleWorkbench.TwoD.CollisionModesUsePlacedPlaneInBothSimulationSpaces",
            "Editor.ParticleWorkbench.TwoD.TrailRibbonAndBeamChangeTheRenderedShapeOnAllBackends",
            "Editor.ParticleWorkbench.TwoD.AnimatedTextureChangesPixelsOnAllBackends",
            "Editor.ParticleWorkbench.TwoD.LinkedEmittersSpawnAtSourcePositionOnAllBackends",
            "Editor.ParticleWorkbench.TwoD.SoftwareEditorPreviewFollowsSavedEmitterLinks",
            "Editor.ParticleWorkbench.TwoD.SoftwareTrailRetainsAndFadesItsTailAfterParticleDeath",
            "Editor.ParticleWorkbench.QuickSetup.PreviewResizesWithinWorkspace",
            "Editor.Interaction.Rig.PgslBindingControlsErrorsAndClear",
            "Acceptance.MushroomMeadow.CompleteGame", "Acceptance.MushroomMeadow.ImageRigLiveAuthoring", "Acceptance.MushroomMeadow.Export.DX11",
            "Acceptance.MushroomMeadow.Export.DX12", "Acceptance.MushroomMeadow.Export.Vulkan",
            "Acceptance.MushroomMeadow.Export.OpenGL", "Acceptance.MushroomMeadow.Export.Software",
            "Acceptance.Text.AuthoredFont.DX11", "Acceptance.Text.AuthoredFont.DX12",
            "Acceptance.Text.AuthoredFont.Vulkan", "Acceptance.Text.AuthoredFont.OpenGL", "Acceptance.Text.AuthoredFont.Software"];
        var requirements = mandatory.Select(name => new
        {
            Name = name,
            State = tests.Any(test => test.Name == name && !test.Passed) ? "Failed"
                : tests.Any(test => test.Name == name && test.Passed) ? "Passed" : "Unverified",
        }).ToArray();
        bool accepted = decisions.Where(item => item.RequiredFor2D).All(item => item.Functionality == "Passed"
            && item.Usability == "Passed" && item.Aesthetics == "Passed") && requirements.All(item => item.State == "Passed");
        var report = new { CreatedUtc = DateTime.UtcNow, ProductFingerprint = fingerprint, Accepted2D = accepted,
            Surfaces = decisions, Requirements = requirements, RejectedEvidence = rejectedEvidence,
            ScaleLimit = "125/150/200 percent captures exercise the application InterfaceScale preference at the recorded device DPI; native monitor DPI acceptance remains separate." };
        File.WriteAllText(Path.Combine(root, "judge.json"), JsonSerializer.Serialize(report, HeadlessHarness.JsonOptions));
        Console.WriteLine(accepted ? "JUDGES PASSED: 2D acceptance evidence complete." : "JUDGES NOT ACCEPTED: inspect judge.json for failed and unverified requirements.");
        return accepted ? 0 : decisions.Any(item => item.Functionality == "Failed" || item.Usability == "Failed"
            || item.Aesthetics == "Failed") || requirements.Any(item => item.State == "Failed") ? 1 : 2;
    }

    internal static JudgeDecision Decide(JudgeSurface surface, IReadOnlyList<TestCaseResult> evidence,
        IReadOnlyList<JudgeCapture> captures, JudgeRating? rating, string root)
    {
        List<string> findings = evidence.Where(test => !test.Passed).Select(test => test.Name + ": " + test.Error).ToList();
        findings.AddRange(captures.SelectMany(capture => capture.Findings.Select(finding => capture.Variant + ": " + finding)));
        bool completeWorkflow = evidence.Any(test => test.Name == surface.Workflow && test.Passed);
        string functional = evidence.Any(test => !test.Passed) ? "Failed" : completeWorkflow ? "Passed" : "Unverified";
        if (!completeWorkflow) findings.Add("Complete workflow missing: " + surface.Workflow + ". Child checks cannot replace it.");
        bool blocking = captures.Any(capture => capture.Findings.Any(finding =>
            finding.StartsWith("Could not inspect", StringComparison.Ordinal)
            || finding.StartsWith("Render fault", StringComparison.Ordinal)
            || finding.StartsWith("Preview is wider", StringComparison.Ordinal)
            || finding.StartsWith("Code surface lacks", StringComparison.Ordinal)));
        if (blocking) functional = "Failed";
        string[] variants = ["normal", "narrow", "scale125", "scale150", "scale200"];
        bool complete = variants.All(variant => captures.Any(capture => capture.Variant == variant))
            && captures.All(capture => File.Exists(Path.Combine(root, capture.File))
                && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, capture.File)))) == capture.Sha256);
        bool reviewed = complete && rating is not null && !string.IsNullOrWhiteSpace(rating.Notes)
            && rating.Usability is >= 1 and <= 5 && rating.Aesthetics is >= 1 and <= 5
            && captures.All(capture => rating.CaptureHashes.Contains(capture.Sha256, StringComparer.Ordinal));
        if (!complete) findings.Add("Missing or altered captures; visual evidence cannot pass.");
        if (!reviewed) findings.Add("Usability and aesthetics require a review tied to all current capture hashes.");
        return new(surface.Name, surface.RequiredFor2D, functional,
            blocking ? "Failed" : reviewed ? rating!.Usability >= 4 ? "Passed" : "Failed" : "Unverified",
            reviewed ? rating!.Aesthetics >= 4 ? "Passed" : "Failed" : "Unverified",
            reviewed ? rating!.Usability : null, reviewed ? rating!.Aesthetics : null, findings);
    }

    public static int Capture(string output, string? surfaceFilter = null, string? variantFilter = null)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        string root = Directory.GetParent(output)!.FullName;
        string workspace = Path.Combine(root, "InspectionWorkspace", Guid.NewGuid().ToString("N"));
        ProjectSession project = new ProjectService().CreateProject(workspace, "Judge Meadow", TwoDShowcaseTemplate.TemplateId);
        ResourceService resources = new(project);
        SettingsService settings = new(Path.Combine(workspace, "judge-settings.json"));
        settings.Current.Editing.AutoSave = false;
        List<JudgeCapture> captures = [];
        foreach (JudgeSurface surface in Surfaces.Where(surface => surfaceFilter is null
            || surface.Name.Equals(surfaceFilter, StringComparison.OrdinalIgnoreCase)))
        {
            ResourceItem? resource = null;
            if (surface.Kind is ResourceKind kind)
            {
                resource = Flatten(resources.BuildTree()).FirstOrDefault(item => item.Kind == kind);
                if (resource is null)
                {
                    string file = resources.CreateResource(ResourceFolderPolicy.RootFor(project, kind), kind, "Judge " + surface.Name);
                    resource = Flatten(resources.BuildTree()).Single(item => item.FullPath == file);
                }
            }
            if (surface.Kind == ResourceKind.UserInterface)
            {
                ResourceItem sprite = Flatten(resources.BuildTree()).First(item => item.Kind == ResourceKind.Image
                    && Path.GetFileName(item.FullPath) == "Sun Coin.image.json");
                UiAssetDocument.Save(resource!.FullPath, new UiAssetDocument
                {
                    Elements =
                    [
                        new() { Id = "Hud", Type = UiElementType.Panel, X = 24, Y = 24, Width = 420, Height = 140 },
                        new() { Id = "Title", ParentId = "Hud", Type = UiElementType.Text, X = 20, Y = 12, Width = 380, Height = 36, Text = "Mushroom Meadow", FontSize = 26 },
                        new() { Id = "Coin", ParentId = "Hud", Type = UiElementType.Image, X = 20, Y = 62, Width = 48, Height = 48, Image = Path.GetRelativePath(project.RootPath, sprite.FullPath).Replace('\\', '/') },
                        new() { Id = "Health", ParentId = "Hud", Type = UiElementType.ProgressBar, X = 90, Y = 74, Width = 280, Height = 24, Value = 75, Maximum = 100 },
                        new() { Id = "Start", Type = UiElementType.Button, Anchor = UiAnchor.Center, X = -120, Y = -24, Width = 240, Height = 48, Text = "Start adventure" },
                    ],
                });
            }
            if (surface.Kind == ResourceKind.Note)
                File.WriteAllText(resource!.FullPath, "<!-- genesis-note-tags: level, gameplay, playtest -->\n# Mushroom Meadow playtest\n\nBuild a readable first level with a safe starting area.\n\n## Gameplay checklist\n\n- [x] Start, jump and run\n- [x] Coins and single-use blocks\n- [x] Checkpoint, win and restart\n- [ ] Compare exported backend captures\n\n**Controls:** A/D to move, Space to jump, Shift to run.\n\nKeep the checkpoint flag rig editable in the Image Editor.\n");
            if (surface.Kind == ResourceKind.Model)
            {
                using ModelViewerControl importer = new(resource!.FullPath, project.RootPath);
                importer.ImportExternalModel(AnimatedGlbFixture.Write(Path.Combine(workspace, "Model review source")));
            }
            if (surface.Kind == ResourceKind.PgslScript)
            {
                using PgslScriptEditorControl authoring = new(resource!.FullPath, project.RootPath);
                authoring.ScriptText = "// @function Main inputs= return=Void\nfunction Main() { }\n"
                    + "// @function TimeBonus inputs=seconds:Float return=Float\nfunction TimeBonus(seconds) { return Floor(seconds) * 10; }\n";
                authoring.Builder.InsertBlueprintAction("Set Variable");
                var set = authoring.Builder.Blocks.Last();
                authoring.Builder.SetArgument(set.Id, "Variable", "score");
                authoring.Builder.SetArgument(set.Id, "Value", "10");
                authoring.Builder.InsertCommand("Print");
                var print = authoring.Builder.Blocks.Last();
                authoring.Builder.SetArgument(print.Id, print.Parameters[0].Name, "score");
                authoring.Save();
            }
            List<(string Variant, Size Size, float Scale)> variants = new()
                { ("normal", new Size(1480, 900), 1f), ("narrow", new Size(1080, 700), 1f),
                  ("scale125", new Size(1480, 900), 1.25f), ("scale150", new Size(1480, 900), 1.5f),
                  ("scale200", new Size(1480, 900), 2f) };
            if (surface.Name is "Script" or "Object" or "Shader" or "Physics" or "Particle" or "Pathing")
            {
                variants.Add(("code", new Size(1480, 900), 1f));
                variants.Add(("code-narrow", new Size(1080, 700), 1f));
            }
            if (surface.Name == "Script")
                foreach (string state in new[] { "code-scale200", "use-in-game", "use-in-game-scale200", "game-steps-scale200",
                    "functions-scale200", "commands-scale200", "routines-scale200", "options-scale200",
                    "function-dialog", "function-dialog-scale200", "function-error-scale200" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
            if (surface.Name == "Room")
                foreach (string state in new[] { "objects", "objects-scale200", "instances", "instances-scale200",
                    "tilesets", "tilesets-scale200", "backgrounds", "backgrounds-scale200", "background-fields-scale200", "background-color-scale200", "views", "views-scale200", "view-fields-scale200",
                    "follow-scale200", "output-scale200", "camera-guides-scale200",
                    "settings", "settings-scale200", "physics-scale200", "inspector-scale200", "use-in-game", "use-in-game-scale200", "game-steps-scale200",
                    "options-scale200", "view-options-scale200", "camera-options-scale200", "edit-options-scale200",
                    "rename-layer-dialog", "rename-layer-dialog-scale200", "starting", "starting-scale200" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
            if (surface.Name == "Object")
                foreach (string state in new[] { "code-scale200", "use-in-game", "use-in-game-scale200", "game-steps-scale200",
                    "properties-scale200", "preview-scale200", "split", "split-scale200", "options-scale200",
                    "add-event-dialog", "add-event-dialog-scale200", "add-action-dialog", "add-action-dialog-scale200",
                    "components-dialog", "components-dialog-scale200", "starting", "starting-scale200" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
            if (surface.Name == "Image")
            {
                variants.Add(("tools-scale200", new Size(1480, 900), 2f));
                variants.Add(("properties-scale200", new Size(1480, 900), 2f));
                foreach (string page in new[] { "rig", "pose", "animate" })
                {
                    variants.Add((page + "-dialog", new Size(1480, 900), 1f));
                    variants.Add((page + "-dialog-scale200", new Size(1480, 900), 2f));
                }
                foreach (string state in new[] { "animate", "animate-scale200", "use-in-game", "use-in-game-scale200", "game-steps-scale200",
                    "playback-frames-scale200", "playback-clip-scale200", "playback-rig-scale200", "options-scale200",
                    "options-panels-scale200", "options-file-scale200", "options-edit-scale200", "options-view-scale200",
                    "options-select-scale200", "options-layer-scale200", "options-frames-scale200", "options-tools-scale200", "options-effects-scale200" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
            }
            if (surface.Name == "Shader")
            {
                variants.Add(("code-scale200", new Size(1480, 900), 2f));
                foreach (string state in new[] { "quick-lower-scale200", "use-in-game", "use-in-game-scale200",
                    "game-steps-scale200", "preview-settings-scale200", "options-scale200", "compile-error-scale200" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
                foreach (string page in new[] { "Presets", "Parameters", "Buffers" })
                {
                    variants.Add((page.ToLowerInvariant(), new Size(1480, 900), 1f));
                    variants.Add((page.ToLowerInvariant() + "-scale200", new Size(1480, 900), 2f));
                }
            }
            if (surface.Name == "Particle")
                foreach (string state in new[] { "properties-scale200", "curves-scale200", "emitters-scale200", "2d", "2d-scale200",
                    "library-presets-scale200", "library-preview-scale200", "forces-scale200", "renderer-scale200", "events-scale200",
                    "use-in-game", "use-in-game-scale200", "quick-lower-scale200" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
            if (surface.Name == "Physics")
            {
                variants.Add(("code-scale200", new Size(1480, 900), 2f));
                foreach (string state in new[] { "presets", "properties", "colliders", "joints", "settings", "2d" })
                {
                    variants.Add((state, new Size(1480, 900), 1f));
                    variants.Add((state + "-scale200", new Size(1480, 900), 2f));
                }
                foreach (string state in new[] { "use-in-game", "use-in-game-scale200", "game-steps-scale200", "quick-fields-scale200",
                    "preview-resource-scale200", "options-scale200", "code-error-scale200", "body-static", "body-kinematic", "sprite-image", "sprite-image-scale200" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
            }
            if (surface.Name == "Pathing")
                foreach (string state in new[] { "code-scale200", "2d", "2d-scale200", "quick-points-scale200", "advanced", "advanced-scale200",
                    "mode-search-scale200", "mode-wander-scale200", "mode-follow-scale200", "telemetry-scale200", "timeline-scale200",
                    "use-in-game", "use-in-game-scale200", "game-steps-scale200", "options-scale200", "code-error-scale200" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
            if (surface.Name == "UI")
                foreach (string state in new[] { "elements-scale200", "inspector-panel-scale200", "inspector-text", "inspector-text-scale200",
                    "inspector-image", "inspector-image-scale200", "inspector-button", "inspector-button-scale200",
                    "inspector-progress", "inspector-progress-scale200", "inspector-stretch-scale200", "zoomed",
                    "inspector-text-details-scale200", "inspector-image-details-scale200", "inspector-button-details-scale200",
                    "inspector-progress-details-scale200", "inspector-stretch-details-scale200",
                    "starting", "starting-scale200", "starting-steps-scale200", "starter-hud", "starter-menu", "starter-pause",
                    "use-in-game", "use-in-game-scale200", "game-steps-scale200", "options-scale200", "add-element-scale200" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
            if (surface.Name == "Audio")
                foreach (string state in new[] { "quick-fields-scale200", "music", "music-scale200", "spatial", "spatial-scale200",
                    "spatial-fields-scale200", "advanced", "advanced-scale200", "advanced-fields-scale200",
                    "use-in-game", "use-in-game-scale200", "game-steps-scale200", "options-scale200", "no-source" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
            if (surface.Name == "Note")
                foreach (string state in new[] { "source", "source-scale200", "preview", "preview-scale200", "library-scale200",
                    "details-scale200", "library-scrolled-scale200", "details-scrolled-scale200", "format-scale200", "options-scale200" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
            if (surface.Name == "Shell")
            {
                foreach (string state in new[] { "menu-File", "menu-Edit", "menu-View", "menu-Tools", "menu-Help",
                    "renderer", "inspector", "commands-pgsl", "commands-engine", "commands-editor", "commands-shell",
                    "palette", "project-hub", "project-hub-templates", "project-hub-recent", "new-project", "preferences", "export",
                    "preferences-Appearance", "preferences-Editing", "preferences-Runtime", "preferences-Rendering", "preferences-Project", "preferences-Shortcuts" })
                {
                    variants.Add((state, new Size(1480, 900), 1f));
                    variants.Add((state + "-scale200", new Size(1480, 900), 2f));
                }
                foreach (ResourceKind inspectorKind in new[] { ResourceKind.Image, ResourceKind.Audio, ResourceKind.Shader,
                    ResourceKind.PgslScript, ResourceKind.GameObject, ResourceKind.Room, ResourceKind.Particle,
                    ResourceKind.Physics, ResourceKind.Pathing, ResourceKind.UserInterface, ResourceKind.Note })
                {
                    variants.Add(("inspector-" + inspectorKind, new Size(1480, 900), 1f));
                    variants.Add(("inspector-" + inspectorKind + "-scale200", new Size(1480, 900), 2f));
                }
                foreach (string state in new[] { "General", "Appearance", "Editing", "Runtime", "Rendering", "Project", "Shortcuts" })
                    variants.Add(("preferences-" + state + "-bottom-scale200", new Size(1480, 900), 2f));
                foreach (string state in new[] { "preferences-Appearance-image", "export-bottom", "project-hub-templates-bottom", "new-project-bottom" })
                    variants.Add((state + "-scale200", new Size(1480, 900), 2f));
            }
            if (surface.Name == "Model")
                foreach (string state in new[] { "viewer", "viewer-narrow", "viewer-scale200", "use-in-game", "use-in-game-scale200",
                    "game-steps-scale200", "options-scale200", "details-scale200", "texture-scale200", "rig-scale200", "outliner-scale200", "empty" })
                    variants.Add((state, new Size(state.EndsWith("narrow", StringComparison.Ordinal) ? 1080 : 1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
            foreach ((string variant, Size size, float scale) in variants)
            {
                if (variantFilter is not null && !variant.Equals(variantFilter, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine("Inspect " + surface.Name + " / " + variant);
                try
                {
                settings.Current.Appearance.InterfaceScale = scale;
                ThemeService.ApplySettings(settings.Current);
                StudioServices services = new(settings, new ProjectService(), new ProjectValidator(), new StudioLog(Path.Combine(workspace, "judge.log")));
                using StudioShellForm shell = new(services, project, persistLayout: false);
                shell.ClientSize = size;
                GateSuite.ShowHost(shell);
                Form host = shell;
                Form? editorHost = null;
                Control inspected = shell;
                ToolStripDropDownItem? openMenu = null;
                bool dialog = false;
                if (surface.Kind == ResourceKind.Image)
                {
                    ResourceItem image = variant.Contains("dialog", StringComparison.Ordinal) || variant.StartsWith("use-in-game", StringComparison.Ordinal)
                        || variant.StartsWith("playback-", StringComparison.Ordinal) || variant == "game-steps-scale200"
                        ? Flatten(resources.BuildTree()).First(item => item.Kind == ResourceKind.Image
                            && Path.GetFileName(item.FullPath) == "Checkpoint.image.json") : resource!;
                    ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(image.FullPath).Document,
                        image.FullPath, ImageDocumentAccess.Editor);
                    ImageEditorControl editor = new(session, ImageWorkspaceStorage.Load(session));
                    host = GateSuite.NewHost(size.Width, size.Height);
                    host.Controls.Add(editor);
                    inspected = editor;
                    if (variant.Contains("dialog", StringComparison.Ordinal))
                    {
                        editorHost = host;
                        int page = variant.StartsWith("animate", StringComparison.Ordinal) ? 2 : variant.StartsWith("pose", StringComparison.Ordinal) ? 1 : 0;
                        host = editor.CreateRigStudioDialog(page);
                        inspected = host;
                    }
                }
                else if (surface.Kind == ResourceKind.Model)
                {
                    host = GateSuite.NewHost(size.Width, size.Height);
                    inspected = variant.StartsWith("viewer", StringComparison.Ordinal)
                        ? new ModelViewerControl(resource!.FullPath, project.RootPath)
                        : new ModelEditorControl(variant == "empty" ? resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Model), ResourceKind.Model, "Empty Model Review") : resource!.FullPath, project.RootPath);
                    host.Controls.Add(inspected);
                }
                else if (resource is not null)
                {
                    IStudioDocument opened = shell.OpenStudioResource(resource);
                    inspected = opened is Genesis.Application.Studio.Docking.SuiteEditorDocument suite
                        ? suite.Surface.AsControl : (Control)opened;
                    shell.Inspector.Inspect(resource);
                }
                if (variant.StartsWith("code", StringComparison.Ordinal))
                {
                    switch (inspected)
                    {
                        case PgslScriptEditorControl script:
                            script.ScriptText = "function DrawHero(sprite, x, y)\n{\n    DrawSprite(sprite, 0, x, y);\n}";
                            script.SetAuthoringMode(PgslScriptAuthoringMode.Code);
                            script.Code.MoveCaret(script.ScriptText.LastIndexOf(", x, y", StringComparison.Ordinal) + 2);
                            break;
                        case ObjectEditorControl objects:
                            objects.SelectEvent("Step");
                            objects.VisibleEventCode = "// Play the saved Image Editor rig from gameplay code.\nSpriteRigPlay(\"Flutter\", 1, -1, false);";
                            objects.ShowCodeEditor(objects.VisibleEventCode.IndexOf(", -1", StringComparison.Ordinal) + 2);
                            break;
                        case ShaderEditorControl shader:
                            shader.SetAuthoringMode(ShaderAuthoringMode.Code);
                            shader.SetSource("float4 MainPS(float2 uv : TEXCOORD0) : SV_Target\n{\n    return lerp(float4(0.15, 0.45, 0.8, 1), float4(1, 0.7, 0.2, 1), 0.5);\n}");
                            Descendants(shader).OfType<CodeEditor>().Single().MoveCaret(shader.SourceText.LastIndexOf(", 0.5", StringComparison.Ordinal) + 2);
                            break;
                        case PhysicsEditorControl physics: physics.SetAuthoringMode(PhysicsAuthoringMode.Code); break;
                        case ParticleEditorControl particle: particle.SetAuthoringMode(ParticleAuthoringMode.Code); break;
                        case PathingEditorControl pathing:
                            pathing.CommandBar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Code").PerformClick();
                            int point = pathing.Code.CodeText.IndexOf("waypoint ", StringComparison.Ordinal);
                            int open = pathing.Code.CodeText.IndexOf('(', Math.Max(0, point));
                            if (open >= 0) pathing.Code.MoveCaret(pathing.Code.CodeText.IndexOf(',', open) + 2);
                            break;
                        default:
                            Button? mode = Descendants(inspected).OfType<Button>().FirstOrDefault(button => button.Tag as string == "Code");
                            if (mode is not null) mode.PerformClick();
                            else Descendants(inspected).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>())
                                .FirstOrDefault(button => button.Text == "Code")?.PerformClick();
                            break;
                    }
                }
                if (inspected is RoomEditorControl roomWorkflow)
                {
                    string page = variant.Split('-')[0];
                    RoomNavSection section = page switch { "instances" => RoomNavSection.Instances, "tilesets" or "rename" => RoomNavSection.Tilesets,
                        "backgrounds" or "background" => RoomNavSection.Backgrounds, "views" or "view" or "follow" or "output" or "camera" => RoomNavSection.Views,
                        "settings" or "physics" => RoomNavSection.Settings, _ => RoomNavSection.Objects };
                    roomWorkflow.Navigation.SetSection(section);
                    if (variant == "background-color-scale200")
                        Descendants(roomWorkflow.Navigation.BackgroundsPanel).OfType<ComboBox>().Single(combo => combo.Items.Cast<object>().Any(item => item.ToString() == "Colour")).SelectedItem = "Colour";
                    if (variant is "follow-scale200" or "output-scale200" or "camera-guides-scale200")
                    {
                        string title = page == "follow" ? "Follow and dead zone" : page == "output" ? "Output viewport" : "Editor guides";
                        foreach (InspectorSection group in Descendants(roomWorkflow.Navigation).OfType<InspectorSection>()) group.Expanded = group.Title == title;
                    }
                    if (section == RoomNavSection.Instances) roomWorkflow.Select(roomWorkflow.Room.Nodes.First(node => node.GameObject is not null));
                    if (variant == "inspector-scale200")
                    {
                        roomWorkflow.Navigation.SetSection(RoomNavSection.Instances);
                        roomWorkflow.Select(roomWorkflow.Room.Nodes.First(node => node.GameObject is not null));
                        roomWorkflow.SetInspectorVisible(true);
                        roomWorkflow.EditorToolbar.OptionsDropdown.DropDownItems.OfType<ToolStripDropDownItem>().Single(item => item.Text == "View")
                            .DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Objects and navigation").PerformClick();
                    }
                    if (section == RoomNavSection.Tilesets)
                    {
                        string sheet = ProjectAssetIndex.Enumerate(project.RootPath, ResourceKind.Image).First(asset => TileSetInfo.Load(asset.FullPath) is not null).FullPath;
                        if (!roomWorkflow.Navigation.TilesetsPanel.SelectTileset(sheet)) throw new InvalidOperationException("Room tile selector did not arm painting.");
                    }
                    if (variant.StartsWith("starting", StringComparison.Ordinal))
                    { roomWorkflow.Room.Nodes.Clear(); roomWorkflow.Select(null); roomWorkflow.RefreshPalette(); }
                    ToolStrip commands = roomWorkflow.Controls.OfType<EditorCommandBar>().Single();
                    if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant == "game-steps-scale200")
                        commands.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                    if (variant == "options-scale200") openMenu = roomWorkflow.EditorToolbar.OptionsDropdown;
                    else if (variant.Contains("-options-", StringComparison.Ordinal))
                        openMenu = roomWorkflow.EditorToolbar.OptionsDropdown.DropDownItems.OfType<ToolStripDropDownItem>()
                            .Single(item => item.Text == (page == "view" ? "View" : page == "edit" ? "Edit" : "Camera"));
                    if (variant.StartsWith("rename-layer-dialog", StringComparison.Ordinal))
                    {
                        host = roomWorkflow.Navigation.TilesetsPanel.CreateRenameLayerDialog(roomWorkflow.Navigation.TilesetsPanel.ActiveTileLayer!);
                        inspected = host; dialog = true;
                    }
                }
                if (inspected is ShaderEditorControl && variant.StartsWith("presets", StringComparison.Ordinal)
                    || inspected is ShaderEditorControl && variant.StartsWith("parameters", StringComparison.Ordinal)
                    || inspected is ShaderEditorControl && variant.StartsWith("buffers", StringComparison.Ordinal))
                {
                    string page = variant.Split('-')[0];
                    ((ShaderEditorControl)inspected).CommandBar.Items.OfType<ToolStripDropDownButton>()
                        .Single(item => item.Text == "Options").DropDownItems.OfType<ToolStripMenuItem>()
                        .Single(item => string.Equals(item.Tag as string, page, StringComparison.OrdinalIgnoreCase)).PerformClick();
                }
                if (inspected is ShaderEditorControl shaderWorkflow)
                {
                    if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant.StartsWith("game-steps", StringComparison.Ordinal))
                        shaderWorkflow.CommandBar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                    else if (variant == "preview-settings-scale200")
                        shaderWorkflow.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options")
                            .DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Tag as string == "Preview settings").PerformClick();
                    else if (variant == "options-scale200")
                        openMenu = shaderWorkflow.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                    else if (variant == "compile-error-scale200")
                    { shaderWorkflow.SetSource("float4 MainPS( { broken"); shaderWorkflow.CompileNow(); }
                }
                if (inspected is ObjectEditorControl objectWorkflow)
                {
                    ToolStrip commands = objectWorkflow.Controls.OfType<EditorCommandBar>().Single();
                    ToolStripDropDownButton options = commands.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                    if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant == "game-steps-scale200")
                        commands.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                    if (variant.StartsWith("starting", StringComparison.Ordinal))
                    {
                        foreach (string id in objectWorkflow.PgslEvents.Keys.ToArray()) objectWorkflow.RemoveEvent(id);
                    }
                    if (variant.StartsWith("properties-", StringComparison.Ordinal) && !Descendants(inspected).Single(control => control.Name == "ObjectIdentityScroll").Visible)
                        options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Properties and events").PerformClick();
                    if (variant.StartsWith("preview-", StringComparison.Ordinal)) options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Preview and live variables").PerformClick();
                    if (variant.StartsWith("split", StringComparison.Ordinal)) objectWorkflow.SetWorkspaceMode(ObjectWorkspaceMode.Split);
                    if (variant == "options-scale200") openMenu = options;
                    if (variant.Contains("-dialog", StringComparison.Ordinal))
                    {
                        Form modal = variant.StartsWith("add-event", StringComparison.Ordinal) ? objectWorkflow.CreateAddEventDialog()
                            : variant.StartsWith("add-action", StringComparison.Ordinal) ? objectWorkflow.VisualActions.CreateWizardDialog("SpriteRigPlay")
                            : objectWorkflow.CreateCompositionDialog();
                        if (modal is ObjectCompositionDialog composition) composition.SelectComponent("SpriteComponent");
                        editorHost = host = modal; inspected = modal; dialog = true;
                    }
                }
                if (inspected is ImageEditorControl imageWorkflow)
                {
                    ToolStripDropDownButton options = imageWorkflow.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                    if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant.StartsWith("playback-", StringComparison.Ordinal) || variant == "game-steps-scale200")
                        imageWorkflow.CommandBar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                    if (variant.StartsWith("playback-", StringComparison.Ordinal))
                    {
                        ComboBox playback = Descendants(imageWorkflow).OfType<ComboBox>().Single(control => control.Name == "ImageGameplayPlayback");
                        playback.SelectedItem = variant.Split('-')[1] switch
                        { "frames" => "All frames", "clip" => playback.Items.Cast<string>().First(item => item.StartsWith("Clip: ", StringComparison.Ordinal)),
                            _ => playback.Items.Cast<string>().First(item => item.StartsWith("Rig: ", StringComparison.Ordinal)) };
                    }
                    if (variant == "options-scale200") openMenu = options;
                    else if (variant.StartsWith("options-", StringComparison.Ordinal))
                    {
                        string name = variant.Split('-')[1] switch { "panels" => "Panels", "frames" => "Frames and tags", string part => char.ToUpperInvariant(part[0]) + part[1..] };
                        openMenu = options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == name);
                    }
                    if (variant == "animate" || variant == "animate-scale200")
                        imageWorkflow.CommandBar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Animate").PerformClick();
                }
                if (surface.Name == "Shell")
                {
                    string shellState = variant.Replace("-scale200", string.Empty, StringComparison.Ordinal)
                        .Replace("-bottom", string.Empty, StringComparison.Ordinal).Replace("-image", string.Empty, StringComparison.Ordinal);
                    if (variant.StartsWith("inspector-", StringComparison.Ordinal) && shellState != "inspector")
                    {
                        string kindName = variant[10..].Replace("-scale200", string.Empty, StringComparison.Ordinal);
                        ResourceKind inspectorKind = Enum.Parse<ResourceKind>(kindName);
                        ResourceItem? selected = Flatten(resources.BuildTree()).FirstOrDefault(item => item.Kind == inspectorKind);
                        if (selected is null)
                        {
                            string file = resources.CreateResource(ResourceFolderPolicy.RootFor(project, inspectorKind), inspectorKind, "Judge Inspector " + inspectorKind);
                            selected = Flatten(resources.BuildTree()).Single(item => item.FullPath == file);
                        }
                        string sprite = ProjectAssetIndex.Enumerate(project.RootPath, ResourceKind.Image).First().Reference;
                        if (inspectorKind == ResourceKind.PgslScript)
                            File.WriteAllText(selected.FullPath, "var moveSpeed = 3.5;\nvar hero = " + JsonSerializer.Serialize(sprite) + "; // @resource Image\n");
                        IStudioDocument opened = shell.OpenStudioResource(selected);
                        if (opened is SuiteEditorDocument { Surface: UiEditorControl ui } && ui.Document.Elements.Count == 0)
                        {
                            ui.AddElement(UiElementType.Image); ui.TryApplyInspectorValue("Ui.Image", sprite); ui.Save();
                        }
                        shell.Inspector.Inspect(selected);
                        shell.Inspector.Show(shell.Inspector.DockPanel, DockState.DockRight);
                        shell.Inspector.DockPanel.DockRightPortion = scale > 1 ? 560 : 440;
                    }
                    if (variant.StartsWith("menu-", StringComparison.Ordinal))
                        openMenu = shell.MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
                            .Single(menu => menu.Text!.Replace("&", string.Empty) == shellState[5..]);
                    else if (shellState == "renderer")
                        openMenu = shell.Controls.OfType<StatusStrip>().Single().Items.OfType<ToolStripDropDownButton>().Single();
                    else if (shellState == "inspector" || shellState == "commands-editor")
                    {
                        ResourceItem selected = Flatten(resources.BuildTree()).First(item => item.Kind == ResourceKind.GameObject);
                        shell.OpenStudioResource(selected);
                        shell.Inspector.Inspect(selected);
                        if (shellState == "inspector") shell.Inspector.Show(shell.Inspector.DockPanel, DockState.DockRight);
                    }
                    if (shellState == "project-hub-recent") settings.AddRecentProject(project.Manifest.Name, project.ProjectFile);
                    if (variant.StartsWith("commands-", StringComparison.Ordinal))
                    {
                        PgslCommandReferenceForm help = shell.CreateCommandReference();
                        if (shellState == "commands-engine") help.SelectEngineTab();
                        else if (shellState == "commands-editor") help.SelectEditorTab();
                        else if (shellState == "commands-shell") help.SelectShellTab();
                        host = help;
                    }
                    else host = shellState switch
                    {
                        "palette" => new CommandPaletteForm(shell.CommandCatalog, shell.CaptureCommandContext()),
                        "project-hub" or "project-hub-templates" or "project-hub-recent" => new ProjectHubForm(services),
                        "new-project" => new NewProjectDialog(TwoDShowcaseTemplate.TemplateId),
                        "preferences" => new PreferencesForm(settings, project),
                        _ when shellState.StartsWith("preferences-", StringComparison.Ordinal) => new PreferencesForm(settings, project),
                        "export" => new ExportGameDialog(project),
                        _ => shell,
                    };
                    if (host is ProjectHubForm hub && shellState == "project-hub-templates") hub.ShowSection(HubSection.Templates);
                    if (host is PreferencesForm && shellState.StartsWith("preferences-", StringComparison.Ordinal))
                        Descendants(host).OfType<ListBox>().Single(list => list.Items.Contains("Appearance")).SelectedItem = shellState[12..];
                    if (variant == "preferences-Appearance-image-scale200")
                        Descendants(host).OfType<RadioButton>().Single(button => button.Name == "ImageThemeMode").Checked = true;
                    dialog = host != shell;
                    if (dialog) inspected = host;
                }
                if (inspected is PgslScriptEditorControl scriptSurface)
                {
                    EditorCommandBar commands = scriptSurface.Controls.OfType<EditorCommandBar>().Single();
                    ToolStripDropDownButton options = commands.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                    if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant.StartsWith("game-steps", StringComparison.Ordinal))
                        commands.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                    else if (variant.StartsWith("functions-", StringComparison.Ordinal) || variant.StartsWith("commands-", StringComparison.Ordinal))
                        options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text ==
                            (variant.StartsWith("functions-", StringComparison.Ordinal) ? "Function outline" : "Command reference")).PerformClick();
                    else if (variant.StartsWith("routines-", StringComparison.Ordinal))
                        Descendants(scriptSurface).OfType<ComboBox>().Single(control => control.Name == "ScriptRoutinePicker").SelectedIndex = 1;
                    else if (variant == "options-scale200") openMenu = options;
                    else if (variant.StartsWith("function-", StringComparison.Ordinal))
                    {
                        host = (Form)typeof(PgslScriptEditorControl).GetMethod("CreateFunctionDialog",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(scriptSurface, [null])!;
                        inspected = host; dialog = true;
                    }
                }
                if (inspected is PhysicsEditorControl physicsSurface)
                {
                    ToolStripDropDownButton options = physicsSurface.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                    if (variant.StartsWith("2d", StringComparison.Ordinal))
                    {
                        physicsSurface.SetPreview2D(true);
                        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        typeof(PhysicsEditorControl).GetMethod("SetPhysicsPaused", flags)!.Invoke(physicsSurface, [true]);
                        for (int step = 0; step < 120; step++)
                            typeof(PhysicsEditorControl).GetMethod("StepSandbox", flags)!.Invoke(physicsSurface, [1f / 60]);
                    }
                    else if (variant.Split('-')[0] is "presets" or "properties" or "colliders" or "joints" or "settings")
                    {
                        string page = variant.Split('-')[0];
                        options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => string.Equals(item.Tag as string, page, StringComparison.OrdinalIgnoreCase)).PerformClick();
                    }
                    else if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant.StartsWith("game-steps", StringComparison.Ordinal))
                        physicsSurface.CommandBar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                    else if (variant == "preview-resource-scale200") options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Tag as string == "Preview resource").PerformClick();
                    else if (variant == "options-scale200") openMenu = options;
                    else if (variant == "code-error-scale200") physicsSurface.SetDefinition("physics_material \"Draft\" {\n friction: NaN\n}");
                    else if (variant.StartsWith("sprite-image", StringComparison.Ordinal))
                    {
                        ProjectAssetEntry image = ProjectAssetIndex.EnumerateImagesForUsage(project.RootPath, Genesis.Application.Core.Images.ImageUsage.Sprite).First();
                        if (!physicsSurface.ChooseSpriteImage(image.Reference)) throw new InvalidOperationException("Physics could not preview the actual project sprite.");
                    }
                    else if (variant.StartsWith("body-", StringComparison.Ordinal))
                        Descendants(physicsSurface).OfType<ComboBox>().Single(control => control.Name == "PhysicsBodyChoice").SelectedItem =
                            variant == "body-static" ? Genesis.Physics.PhysicsBodyKind.Static : Genesis.Physics.PhysicsBodyKind.Kinematic;
                }
                if (inspected is AudioEditorControl audioSurface)
                {
                    ToolStripDropDownButton options = audioSurface.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                    if (variant.StartsWith("music", StringComparison.Ordinal))
                        Descendants(audioSurface).OfType<ComboBox>().Single(control => control.Name == "AudioStartingPreset").SelectedIndex = 1;
                    if (variant.StartsWith("spatial", StringComparison.Ordinal)) audioSurface.SetSpatial(true);
                    if (variant.StartsWith("advanced", StringComparison.Ordinal))
                    {
                        options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Advanced ambience").PerformClick();
                        audioSurface.ApplyEnvironmentPreset(Genesis.Runtime.Climate.EnvironmentAudioRole.Water);
                    }
                    if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant == "game-steps-scale200")
                        audioSurface.CommandBar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                    if (variant == "options-scale200") openMenu = options;
                    if (variant == "no-source" && !audioSurface.TryApplyInspectorValue("source", null))
                        throw new InvalidOperationException("Audio source could not be cleared through its actual Inspector binding.");
                }
                if (inspected is PathingEditorControl pathingSurface)
                {
                    if (!variant.StartsWith("code", StringComparison.Ordinal))
                    {
                        ResourceItem previewObject = Flatten(resources.BuildTree()).First(item => item.Kind == ResourceKind.GameObject
                            && Path.GetFileName(item.FullPath) == "Explorer.object.json");
                        string reference = Path.GetRelativePath(project.RootPath, previewObject.FullPath).Replace('\\', '/');
                        pathingSurface.Code.CodeText = "pathing \"Sprite patrol\" {\n dimension: TwoD\n object: \"" + reference + "\"\n preview_agents: 1\n mode: WaypointPatrol\n loop: PingPong\n speed: 80\n animation: \"Run\"\n waypoint \"Start\" (64, 64, 0) wait 0 curve false\n waypoint \"Bend\" (160, 192, 0) wait 0 curve true\n waypoint \"End\" (320, 96, 0) wait 0 curve false\n}\n";
                        pathingSurface.Save();
                        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        typeof(PathingEditorControl).GetMethod("ScrubTo", flags)!.Invoke(pathingSurface, [1.5f]);
                    }
                    ToolStripDropDownButton options = pathingSurface.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                    if (variant.StartsWith("advanced", StringComparison.Ordinal) || variant.StartsWith("mode-", StringComparison.Ordinal))
                        options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Advanced route settings").PerformClick();
                    if (variant.StartsWith("mode-", StringComparison.Ordinal))
                    {
                        Genesis.Runtime.Navigation.PathingRouteMode mode = variant.Split('-')[1] switch
                        { "search" => Genesis.Runtime.Navigation.PathingRouteMode.NavMeshSearch, "wander" => Genesis.Runtime.Navigation.PathingRouteMode.WanderRadius, _ => Genesis.Runtime.Navigation.PathingRouteMode.FollowLeader };
                        Descendants(pathingSurface).OfType<ComboBox>().Single(control => control.Name == "PathingRouteChoice").SelectedItem = mode;
                    }
                    if (variant == "telemetry-scale200") options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Agent telemetry").PerformClick();
                    if (variant == "timeline-scale200") options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Speed timeline").PerformClick();
                    if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant == "game-steps-scale200")
                        pathingSurface.CommandBar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                    if (variant == "options-scale200") openMenu = options;
                    if (variant == "code-error-scale200") { pathingSurface.Code.CodeText += "\nunknown: broken"; try { pathingSurface.Save(); } catch (InvalidDataException) { } }
                }
                if (inspected is ParticleEditorControl particleSurface)
                {
                    if (variant.StartsWith("2d", StringComparison.Ordinal)) particleSurface.SetPreview2D(true);
                    string? panel = variant.Split('-')[0] switch
                    { "properties" or "forces" or "renderer" or "events" => "Properties", "curves" => "Curves / timeline",
                        "emitters" or "library" => "Emitters / Presets", _ => null };
                    if (panel is not null)
                        Descendants(inspected).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripDropDownButton>())
                            .Single(button => button.Text == "Options").DropDownItems.OfType<ToolStripMenuItem>()
                            .Single(item => item.Text == (panel == "Properties" ? "Advanced properties" : panel)).PerformClick();
                    if (variant.StartsWith("use-in-game", StringComparison.Ordinal))
                        Descendants(inspected).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>())
                            .Single(button => button.Text == "Use in game").PerformClick();
                    if (variant.StartsWith("quick-lower", StringComparison.Ordinal))
                    {
                        FlowLayoutPanel quick = Descendants(inspected).OfType<FlowLayoutPanel>().Single(page => page.Name == "ParticleQuickSetup");
                        quick.ScrollControlIntoView(quick.Controls.Cast<Control>().Last());
                    }
                    if (variant.StartsWith("library", StringComparison.Ordinal))
                    {
                        string page = variant.Split('-')[1];
                        TabControl tabs = Descendants(inspected).OfType<TabControl>().Single(tab => tab.TabPages.Cast<TabPage>().Any(item => item.Text == "Emitters"));
                        tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(item => item.Text.Equals(page, StringComparison.OrdinalIgnoreCase));
                    }
                    else if (variant.Split('-')[0] is "forces" or "renderer" or "events")
                    {
                        string page = variant.Split('-')[0];
                        TabControl tabs = Descendants(inspected).OfType<TabControl>().Single(tab => tab.TabPages.Cast<TabPage>().Any(item => item.Text == "Emission"));
                        tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(item => item.Text.Equals(page, StringComparison.OrdinalIgnoreCase));
                        if (page == "events")
                        {
                            Descendants(inspected).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripDropDownButton>())
                                .Single(button => button.Text == "Add").DropDownItems.OfType<ToolStripMenuItem>()
                                .Single(item => item.Text == "Fire").PerformClick();
                            Descendants(inspected).OfType<Button>().Single(button => button.Text == "Add link").PerformClick();
                        }
                    }
                }
                if (inspected is UiEditorControl uiSurface)
                {
                    string id = variant.Split('-').ElementAtOrDefault(1) switch
                    { "text" => "Title", "image" => "Coin", "button" => "Start", "progress" => "Health", _ => "Hud" };
                    uiSurface.SelectElement(id);
                    if (variant.StartsWith("inspector-stretch", StringComparison.Ordinal))
                    {
                        uiSurface.TryApplyInspectorValue("Ui.Anchor", "Stretch");
                        uiSurface.TryApplyInspectorValue("Ui.Width", 24); uiSurface.TryApplyInspectorValue("Ui.Height", 24);
                    }
                    if (variant.StartsWith("elements", StringComparison.Ordinal) || variant.StartsWith("inspector", StringComparison.Ordinal) && scale == 2)
                    {
                        string toggle = variant.StartsWith("elements", StringComparison.Ordinal) ? "Elements" : "Inspector";
                        uiSurface.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(menu => menu.Text == "Options")
                            .DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == toggle).PerformClick();
                    }
                    if (variant.StartsWith("starting", StringComparison.Ordinal) || variant.StartsWith("starter-", StringComparison.Ordinal))
                    {
                        uiSurface.Document.Elements.Clear();
                        if (variant.StartsWith("starter-", StringComparison.Ordinal))
                            uiSurface.ApplyStartingLayout(variant.Split('-')[1] switch { "hud" => "HUD", "menu" => "Menu", _ => "Pause" });
                        else uiSurface.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(menu => menu.Text == "Options")
                            .DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Starting layouts").PerformClick();
                    }
                    if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant == "game-steps-scale200")
                        uiSurface.CommandBar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Use in game").PerformClick();
                    if (variant is "options-scale200" or "add-element-scale200")
                        openMenu = uiSurface.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(menu => menu.Text == (variant == "options-scale200" ? "Options" : "Add element"));
                }
                if (inspected is NoteEditorControl noteSurface)
                {
                    if (variant.StartsWith("source", StringComparison.Ordinal)) noteSurface.SetViewMode(NoteEditorViewMode.Source);
                    if (variant.StartsWith("preview", StringComparison.Ordinal)) noteSurface.SetViewMode(NoteEditorViewMode.Preview);
                    if (variant.StartsWith("library", StringComparison.Ordinal) || variant.StartsWith("details", StringComparison.Ordinal))
                    {
                        string toggle = variant.StartsWith("library", StringComparison.Ordinal) ? "Library" : "Details";
                        Descendants(inspected).OfType<EditorCommandBar>().Single().Items.OfType<ToolStripDropDownButton>()
                            .Single(menu => menu.Text == "Options").DropDownItems.OfType<ToolStripMenuItem>()
                            .Single(item => item.Text == toggle).PerformClick();
                    }
                    if (variant is "format-scale200" or "options-scale200")
                        openMenu = Descendants(inspected).OfType<EditorCommandBar>().Single().Items.OfType<ToolStripDropDownButton>()
                            .Single(menu => menu.Text == (variant.StartsWith("format", StringComparison.Ordinal) ? "Format" : "Options"));
                }
                try
                {
                    if (!dialog) host.ClientSize = size;
                    if (host != shell) ThemeService.Apply(host);
                    GateSuite.ShowHost(host);
                    if (inspected is ModelViewerControl modelView)
                    {
                        modelView.SelectClip("Wave"); modelView.SetFrame(0);
                        if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant == "game-steps-scale200")
                            Descendants(modelView).OfType<EditorCommandBar>().Single().Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                        if (variant == "options-scale200") openMenu = Descendants(modelView).OfType<EditorCommandBar>().Single().Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                        if (variant == "details-scale200")
                            Descendants(modelView).OfType<EditorCommandBar>().Single().Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options")
                                .DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Model details panel").PerformClick();
                        if (modelView is ModelEditorControl modelTools && variant is "texture-scale200" or "rig-scale200" or "outliner-scale200")
                        {
                            string target = variant == "texture-scale200" ? "Texture" : variant == "rig-scale200" ? "Rig" : "Outliner";
                            Descendants(modelTools).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>()).Single(button => button.Text!.Contains(target, StringComparison.Ordinal)).PerformClick();
                            modelView.SelectClip("Wave"); modelView.SetFrame(0);
                        }
                        if (variant == "game-steps-scale200")
                            Descendants(modelView).OfType<FlowLayoutPanel>().Single(page => page.Name == "ModelUseInGame").AutoScrollPosition = new Point(0, 10000);
                        using Bitmap? modelWarm = modelView.Viewport.CaptureFrame(3);
                        ModelFrameRuler modelFrames = Descendants(modelView).OfType<ModelFrameRuler>().Single();
                        long modelDeadline = Environment.TickCount64 + 5000;
                        while (modelFrames.Enabled && modelFrames.CachedPreviewCount < Math.Min(6, modelFrames.Maximum + 1)
                            && Environment.TickCount64 < modelDeadline) GateSuite.Pump(1, 30);
                    }
                    if (host is PgslCommandReferenceForm commandReference)
                    {
                        if (variant.StartsWith("commands-engine", StringComparison.Ordinal)) commandReference.SelectEngineTab();
                        else if (variant.StartsWith("commands-editor", StringComparison.Ordinal)) commandReference.SelectEditorTab();
                        else if (variant.StartsWith("commands-shell", StringComparison.Ordinal)) commandReference.SelectShellTab();
                    }
                    if (surface.Name == "Image" && variant.StartsWith("animate-dialog", StringComparison.Ordinal))
                        Descendants(host).OfType<ListBox>().Single(list => list.Name == "RigAnimations").SelectedIndex = 0;
                    if (surface.Name == "Image" && variant is "tools-scale200" or "properties-scale200")
                    {
                        string toggle = variant == "tools-scale200" ? "Drawing tools" : "Properties";
                        ((ImageEditorControl)inspected).CommandBar.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options")
                            .DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Panels").DropDownItems.OfType<ToolStripMenuItem>()
                            .Single(item => item.Text == toggle).PerformClick();
                    }
                    if (openMenu?.OwnerItem is ToolStripDropDownItem parentMenu) parentMenu.ShowDropDown();
                    openMenu?.ShowDropDown();
                    GateSuite.Pump(6, 20);
                    if (surface.Name == "Script" && variant == "function-error-scale200")
                    {
                        Descendants(host).OfType<TextBox>().Single(control => control.Name == "ScriptFunctionName").Text = "Bad name";
                        Descendants(host).OfType<Button>().Single(control => control.Name == "ScriptFunctionConfirm").PerformClick();
                        GateSuite.Pump(2, 10);
                    }
                    if (host == shell && resource is not null)
                        inspected = shell.OpenStudioDocuments.OfType<SuiteEditorDocument>()
                            .FirstOrDefault(document => document.Resource.FullPath == resource.FullPath)?.Surface.AsControl ?? inspected;
                    if (inspected is UiEditorControl && variant == "zoomed")
                    {
                        Control canvas = Descendants(inspected).Single(control => control.GetType().Name == "UiDesignCanvas");
                        canvas.GetType().GetMethod("OnMouseWheel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(canvas, [new MouseEventArgs(MouseButtons.None, 0, canvas.Width / 3, canvas.Height / 3, 480)]);
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is RoomEditorControl roomLayout && variant is "game-steps-scale200" or "physics-scale200")
                    {
                        if (variant == "game-steps-scale200")
                        {
                            FlowLayoutPanel guide = Descendants(roomLayout).OfType<FlowLayoutPanel>().Single(control => control.Name == "RoomUseInGame");
                            guide.AutoScrollPosition = new Point(0, Math.Max(0, guide.DisplayRectangle.Height - guide.ClientSize.Height));
                        }
                        else
                        {
                            RoomInspectorPanel settingsPanel = Descendants(roomLayout.Navigation).OfType<RoomInspectorPanel>().Single(panel => panel.Visible);
                            Descendants(settingsPanel).OfType<Genesis.Application.Editors.Suite.Inspector.ResourceInspectorPropertySurface>().Single().SetGroupExpanded("Physics", true);
                            GateSuite.Pump(2, 10);
                            FlatScrollPanel scroll = Descendants(settingsPanel).OfType<FlatScrollPanel>().Single(control => control.Name == "RoomInspectorScroll");
                            scroll.ScrollOffset = int.MaxValue;
                        }
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is RoomEditorControl roomFields && variant is "background-fields-scale200" or "view-fields-scale200" or "follow-scale200" or "output-scale200" or "camera-guides-scale200")
                    {
                        ScrollableControl scroll = variant == "background-fields-scale200" ? roomFields.Navigation.BackgroundsPanel
                            : Descendants(roomFields.Navigation).OfType<Panel>().First(panel => panel.Visible && panel.AutoScroll);
                        scroll.AutoScrollPosition = new Point(0, Math.Max(0, scroll.DisplayRectangle.Height - scroll.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is PgslScriptEditorControl && variant == "game-steps-scale200")
                    {
                        FlowLayoutPanel steps = Descendants(inspected).OfType<FlowLayoutPanel>().Single(control => control.Name == "ScriptGameSteps");
                        steps.AutoScrollPosition = new Point(0, Math.Max(0, steps.DisplayRectangle.Height - steps.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is ShaderEditorControl && variant is "game-steps-scale200" or "quick-lower-scale200")
                    {
                        string name = variant == "game-steps-scale200" ? "ShaderUseInGame" : "ShaderControls";
                        FlowLayoutPanel scroll = Descendants(inspected).OfType<FlowLayoutPanel>().Single(control => control.Name == name);
                        scroll.AutoScrollPosition = new Point(0, Math.Max(0, scroll.DisplayRectangle.Height - scroll.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is PhysicsEditorControl && variant is "game-steps-scale200" or "quick-fields-scale200")
                    {
                        string name = variant == "game-steps-scale200" ? "PhysicsUseInGame" : "PhysicsQuickSetup";
                        FlowLayoutPanel scroll = Descendants(inspected).OfType<FlowLayoutPanel>().Single(control => control.Name == name);
                        scroll.AutoScrollPosition = new Point(0, Math.Max(0, scroll.DisplayRectangle.Height - scroll.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is PathingEditorControl && variant is "game-steps-scale200" or "quick-points-scale200" or "advanced" or "advanced-scale200" or "mode-search-scale200" or "mode-wander-scale200" or "mode-follow-scale200")
                    {
                        if (variant == "game-steps-scale200")
                        {
                            ScrollableControl guide = Descendants(inspected).OfType<ScrollableControl>().Single(control => control.Name == "PathingUseInGame");
                            guide.AutoScrollPosition = new Point(0, Math.Max(0, guide.DisplayRectangle.Height - guide.ClientSize.Height));
                        }
                        else
                        {
                            ScrollableControl quick = Descendants(inspected).OfType<ScrollableControl>().Single(control => control.Name == "PathingQuickScroll");
                            if (variant == "quick-points-scale200") quick.AutoScrollPosition = new Point(0, Math.Max(0, quick.DisplayRectangle.Height - quick.ClientSize.Height));
                            else if (variant.StartsWith("mode-", StringComparison.Ordinal)) quick.AutoScrollPosition = new Point(0, Math.Max(0, quick.DisplayRectangle.Height - quick.ClientSize.Height));
                            else quick.ScrollControlIntoView(Descendants(inspected).OfType<ComboBox>().Single(control => control.Name == "PathingRouteChoice").Parent!);
                        }
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is UiEditorControl && variant.Contains("-details-", StringComparison.Ordinal))
                    {
                        Panel scroll = Descendants(inspected).OfType<Panel>().Single(panel => panel.GetType().Name == "EditorScrollHost");
                        scroll.AutoScrollPosition = new Point(0, Math.Max(0, scroll.DisplayRectangle.Height - scroll.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is UiEditorControl && variant is "starting-steps-scale200" or "game-steps-scale200")
                    {
                        FlowLayoutPanel guide = Descendants(inspected).OfType<FlowLayoutPanel>().Single(panel => panel.Name is "UiStartingGuide" or "UiUseInGame");
                        guide.AutoScrollPosition = new Point(0, Math.Max(0, guide.DisplayRectangle.Height - guide.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is AudioEditorControl && variant is "game-steps-scale200" or "quick-fields-scale200" or "spatial-fields-scale200" or "advanced-fields-scale200")
                    {
                        string name = variant == "game-steps-scale200" ? "AudioUseInGame" : "AudioQuickFields";
                        FlowLayoutPanel scroll = Descendants(inspected).OfType<FlowLayoutPanel>().Single(control => control.Name == name);
                        scroll.AutoScrollPosition = new Point(0, Math.Max(0, scroll.DisplayRectangle.Height - scroll.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is ImageEditorControl && variant == "game-steps-scale200")
                    {
                        FlowLayoutPanel guide = Descendants(inspected).OfType<FlowLayoutPanel>().Single(control => control.Name == "ImageUseInGame");
                        guide.AutoScrollPosition = new Point(0, Math.Max(0, guide.DisplayRectangle.Height - guide.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is ObjectEditorControl && variant == "game-steps-scale200")
                    {
                        FlowLayoutPanel guide = Descendants(inspected).OfType<FlowLayoutPanel>().Single(control => control.Name == "ObjectUseInGame");
                        guide.AutoScrollPosition = new Point(0, Math.Max(0, guide.DisplayRectangle.Height - guide.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is ObjectEditorControl && variant == "properties-scale200")
                    {
                        ScrollableControl fields = (ScrollableControl)Descendants(inspected).Single(control => control.Name == "ObjectIdentityScroll");
                        fields.AutoScrollPosition = new Point(0, Math.Max(0, fields.DisplayRectangle.Height - fields.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is NoteEditorControl && variant.Contains("-scrolled-", StringComparison.Ordinal))
                    {
                        Panel scroll = Descendants(inspected).OfType<Panel>().First(panel => panel.GetType().Name == "EditorScrollHost" && panel.Visible && panel.AutoScroll);
                        scroll.AutoScrollPosition = new Point(0, Math.Max(0, scroll.DisplayRectangle.Height - scroll.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is ParticleEditorControl particlePreview)
                    {
                        // Inspect an established effect, after real simulation steps and GPU
                        // submission, rather than a newly born particle on the first frame.
                        particlePreview.StepForTest(.75f);
                    }
                    if (surface.Name == "Shell" && variant.Contains("-bottom-", StringComparison.Ordinal))
                    {
                        foreach (ScrollableControl scroll in Descendants(host).OfType<ScrollableControl>().Where(control => control.Visible && control.AutoScroll))
                            scroll.AutoScrollPosition = new Point(0, Math.Max(0, scroll.DisplayRectangle.Height - scroll.ClientSize.Height));
                        GateSuite.Pump(2, 30);
                    }
                    List<string> findings = InspectControls(inspected);
                    if (inspected is NoteEditorControl noteState)
                        Console.WriteLine("Note state: dirty=" + noteState.IsDirty + "; sameText="
                            + (noteState.NoteText == File.ReadAllText(noteState.ResourcePath).Replace("\r\n", "\n")));
                    if (inspected is AudioEditorControl)
                        foreach (FlowLayoutPanel rows in Descendants(inspected).OfType<FlowLayoutPanel>())
                            Console.WriteLine($"Audio layout: rows={rows.Bounds}, client={rows.ClientRectangle}, dock={rows.Dock}, parent={rows.Parent?.ClientRectangle}, display={rows.DisplayRectangle}");
                    if (inspected is ShaderEditorControl && variant.StartsWith("presets", StringComparison.Ordinal))
                        foreach (Control panel in Descendants(inspected).Where(control => control.Name is "ShaderPresetWorkspace" or "ShaderPassStack"))
                            Console.WriteLine($"Shader presets: {panel.Name} bounds={panel.Bounds} visible={panel.Visible}, children="
                                + string.Join("; ", panel.Controls.Cast<Control>().Select(child => $"{child.GetType().Name}: {child.Bounds}, {child.Visible}")));
                    if (surface.Name == "Shell" && variant == "scale200")
                    {
                        WelcomeDocument start = Descendants(shell).OfType<WelcomeDocument>().Single();
                        Panel scroll = start.Controls.OfType<Panel>().Single();
                        TableLayoutPanel layout = scroll.Controls.OfType<TableLayoutPanel>().Single();
                        Console.WriteLine($"Start layout: scroll={scroll.AutoScrollPosition}, display={scroll.DisplayRectangle}, client={scroll.ClientRectangle}, hero={layout.GetControlFromPosition(0, 0)?.Bounds}, active={host.ActiveControl?.GetType().Name}");
                    }
                    if (surface.Name == "Object" && variant == "scale200")
                    {
                        EventListPanel events = Descendants(inspected).OfType<EventListPanel>().Single();
                        Console.WriteLine($"Object events: bounds={events.Bounds}, client={events.ClientRectangle}, scroll={events.AutoScrollPosition}, count={events.EventIds.Count}");
                        Control graph = ((ObjectEditorControl)inspected).VisualActions.Graph;
                        for (Control? ancestor = graph; ancestor is not null && ancestor != inspected; ancestor = ancestor.Parent)
                            Console.WriteLine($"Object graph {ancestor.GetType().Name}: bounds={ancestor.Bounds}, client={ancestor.ClientSize}, dock={ancestor.Dock}");
                    }
                    if (host is PgslCommandReferenceForm referenceState)
                        Console.WriteLine("Command capture state: " + variant + " / " + referenceState.ActiveCommandPathCaption + " / " + referenceState.VisibleCommandCount);
                    // Settle GPU initialization/resizing before printing the surrounding WinForms
                    // controls, so their geometry and the composited frame belong to one layout.
                    foreach (D3DViewportControl viewport in Descendants(surface.Name == "Shell" ? host : inspected)
                        .OfType<D3DViewportControl>().Where(viewport => viewport.Visible))
                        using (viewport.ReadbackFrameToBitmap(3)) { }
                    if (inspected is ParticleEditorControl particleLayout)
                    {
                        if (particleLayout.Viewport.Visible && particleLayout.Viewport.Parent?.Parent is SplitContainer previewSplit
                            && previewSplit.Parent is Control middle && previewSplit.Right > middle.ClientSize.Width)
                            findings.Add("Preview is wider than its visible workspace; the effect is clipped.");
                    }
                    using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
                    if (host is PgslCommandReferenceForm renderedState)
                        Console.WriteLine("Command rendered state: " + renderedState.ActiveCommandPathCaption + " / " + renderedState.VisibleCommandCount);
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                    foreach (D3DViewportControl viewport in Descendants(surface.Name == "Shell" ? host : inspected)
                        .OfType<D3DViewportControl>().Where(viewport => viewport.Visible))
                    {
                        using Bitmap? frame = viewport.ReadbackFrameToBitmap(0);
                        if (frame is null) { findings.Add("Viewport readback unavailable."); continue; }
                        if (viewport.LastRenderException is Exception fault) findings.Add("Render fault: " + fault.Message);
                        Point position = host.PointToClient(viewport.PointToScreen(Point.Empty));
                        Rectangle visible = new(position, viewport.ClientSize);
                        for (Control? ancestor = viewport.Parent; ancestor is not null; ancestor = ancestor.Parent)
                            visible.Intersect(new Rectangle(host.PointToClient(ancestor.PointToScreen(Point.Empty)), ancestor.ClientSize));
                        System.Drawing.Drawing2D.GraphicsState state = graphics.Save();
                        graphics.SetClip(visible);
                        graphics.DrawImage(frame, new Rectangle(position, viewport.ClientSize));
                        graphics.Restore(state);
                    }
                    if (openMenu is not null)
                    {
                        // Offscreen native windows clamp popup coordinates to the physical
                        // monitor. Composite each real popup next to its logical owner instead
                        // of placing nested menus at a fabricated corner of the captured form.
                        Point DrawPopup(ToolStripDropDownItem item)
                        {
                            ToolStripDropDown popup = item.DropDown;
                            Point anchor;
                            if (item.OwnerItem is ToolStripDropDownItem parent)
                            {
                                Point parentAnchor = DrawPopup(parent);
                                anchor = new Point(parentAnchor.X + parent.DropDown.Width - 2, parentAnchor.Y + item.Bounds.Top);
                                if (anchor.X + popup.Width > bitmap.Width) anchor.X = parentAnchor.X - popup.Width + 2;
                            }
                            else
                            {
                                ToolStrip owner = item.Owner!;
                                anchor = host.PointToClient(owner.PointToScreen(item.Bounds.Location));
                                anchor.Y += owner is StatusStrip ? -popup.Height : item.Height;
                            }
                            anchor.X = Math.Clamp(anchor.X, 0, Math.Max(0, bitmap.Width - popup.Width));
                            anchor.Y = Math.Clamp(anchor.Y, 0, Math.Max(0, bitmap.Height - popup.Height));
                            using Bitmap menuPixels = new(Math.Max(1, popup.Width), Math.Max(1, popup.Height));
                            popup.DrawToBitmap(menuPixels, new Rectangle(Point.Empty, popup.Size));
                            graphics.DrawImageUnscaled(menuPixels, anchor); return anchor;
                        }
                        DrawPopup(openMenu);
                        openMenu.HideDropDown();
                    }
                    }
                    string file = Path.Combine(output, surface.Name + "-" + variant + ".png");
                    bitmap.Save(file, ImageFormat.Png);
                    captures.Add(new(surface.Name, variant, Path.GetRelativePath(root, file),
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), host.DeviceDpi,
                        "application InterfaceScale " + (scale * 100) + "% at device DPI " + host.DeviceDpi, findings));
                }
                finally { if (host != shell) host.Dispose(); editorHost?.Dispose(); }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Console.WriteLine("INSPECTION FAILED " + surface.Name + ": " + error);
                    captures.Add(new(surface.Name, variant, "", "", 0, "unavailable",
                        ["Could not inspect: " + error]));
                }
            }
        }
        File.WriteAllText(Path.Combine(output, "capture-manifest.json"), JsonSerializer.Serialize(
            new JudgeCaptureManifest(ProductFingerprint(), captures), HeadlessHarness.JsonOptions));
        int failed = captures.Count(capture => string.IsNullOrEmpty(capture.File));
        Console.WriteLine($"Captured {captures.Count - failed} populated layouts; {failed} inspection failures. Subjective ratings remain unverified.");
        return failed == 0 ? 0 : 1;
    }

    private static List<string> InspectControls(Control root)
    {
        List<string> findings = [];
        foreach (CodeEditor editor in Descendants(root).OfType<CodeEditor>())
            if (!editor.HasIntelligenceProvider) findings.Add("Code surface lacks a completion/signature provider.");
        foreach (ToolStrip strip in Descendants(root).OfType<ToolStrip>())
        foreach (IGrouping<string, ToolStripItem> group in strip.Items.Cast<ToolStripItem>()
                     .Where(item => item.Available && item is ToolStripButton && !string.IsNullOrWhiteSpace(item.Text))
                     .GroupBy(item => item.Text!.Replace("&", string.Empty), StringComparer.OrdinalIgnoreCase))
            if (group.Count() > 1) findings.Add("Repeated toolbar action: " + group.Key);
        foreach (Button button in Descendants(root).OfType<Button>().Where(button => button.Visible))
        {
            if (button.Height < 20) findings.Add("Small button: " + button.Text + " (" + button.Height + "px)");
            if (button.Parent is { } parent && parent is not ScrollableControl { AutoScroll: true }
                && !parent.ClientRectangle.Contains(button.Bounds)) findings.Add("Clipped button: " + button.Text);
        }
        return findings.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        foreach (Control nested in Descendants(child)) yield return nested;
    }

    private static IEnumerable<ResourceItem> Flatten(ResourceItem root)
    {
        yield return root;
        foreach (ResourceItem child in root.Children)
        foreach (ResourceItem nested in Flatten(child)) yield return nested;
    }
}
