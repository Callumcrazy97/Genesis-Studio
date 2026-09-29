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
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Headless.Suites;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Viewport;
using Genesis.Runtime.Modeling;
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
internal static partial class ReadinessJudgeRunner
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

    public static int Evaluate(string root, string? reviewFile, JudgeScope scope = JudgeScope.All)
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
        List<JudgeDecision> threeDDecisions = [];
        foreach (JudgeSurface surface in Surfaces)
        {
            TestCaseResult[] evidence = tests.Where(test => test.Name == surface.Workflow
                || test.Name.StartsWith(surface.Workflow + ".", StringComparison.Ordinal)).ToArray();
            JudgeCapture[] captures = manifest?.ProductFingerprint == fingerprint
                ? manifest.Captures.Where(capture => capture.Surface == surface.Name).ToArray() : [];
            JudgeRating? rating = review?.ProductFingerprint == fingerprint && !string.IsNullOrWhiteSpace(review.ReviewedBy)
                ? review.Ratings.FirstOrDefault(item => item.Surface == surface.Name) : null;
            decisions.Add(Decide(surface, evidence, captures, rating, root));
            threeDDecisions.Add(Decide(surface, evidence, captures, rating, root, ThreeDVariants(surface.Name)));
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
        JudgeRequirement[] requirements = Requirements(mandatory, tests);
        JudgeRequirement[] threeDRequirements = Requirements(ThreeDRequirements, tests);
        bool accepted = decisions.Where(item => item.RequiredFor2D).All(item => item.Functionality == "Passed"
            && item.Usability == "Passed" && item.Aesthetics == "Passed") && requirements.All(item => item.State == "Passed");
        JudgeBuildGate fullBuild = InspectFullBuild(root, fingerprint);
        bool accepted3D = Complete(threeDDecisions, threeDRequirements) && fullBuild.State == "Passed";
        bool requestedAccepted = (scope == JudgeScope.ThreeD || accepted) && (scope == JudgeScope.TwoD || accepted3D);
        var report = new { CreatedUtc = DateTime.UtcNow, ProductFingerprint = fingerprint, Scope = scope.ToString(),
            Accepted = requestedAccepted, Accepted2D = accepted, Accepted3D = accepted3D,
            Surfaces = decisions, Requirements = requirements, ThreeDSurfaces = threeDDecisions,
            ThreeDRequirements = threeDRequirements, FullBuild = fullBuild, RejectedEvidence = rejectedEvidence,
            ScaleLimit = "125/150/200 percent captures exercise the application InterfaceScale preference at the recorded device DPI; native monitor DPI acceptance remains separate." };
        File.WriteAllText(Path.Combine(root, "judge.json"), JsonSerializer.Serialize(report, HeadlessHarness.JsonOptions));
        Console.WriteLine(requestedAccepted ? "JUDGES PASSED: " + scope + " acceptance evidence complete."
            : "JUDGES NOT ACCEPTED: inspect judge.json for failed and unverified requirements.");
        IEnumerable<JudgeDecision> requestedDecisions = scope == JudgeScope.TwoD ? decisions.Where(item => item.RequiredFor2D)
            : scope == JudgeScope.ThreeD ? threeDDecisions : decisions.Concat(threeDDecisions);
        IEnumerable<JudgeRequirement> requestedRequirements = scope == JudgeScope.TwoD ? requirements
            : scope == JudgeScope.ThreeD ? threeDRequirements : requirements.Concat(threeDRequirements);
        return requestedAccepted ? 0 : requestedDecisions.Any(item => item.Functionality == "Failed" || item.Usability == "Failed"
            || item.Aesthetics == "Failed") || requestedRequirements.Any(item => item.State == "Failed")
            || (scope != JudgeScope.TwoD && fullBuild.State == "Failed") ? 1 : 2;
    }

    internal static JudgeDecision Decide(JudgeSurface surface, IReadOnlyList<TestCaseResult> evidence,
        IReadOnlyList<JudgeCapture> captures, JudgeRating? rating, string root, IReadOnlyList<string>? requiredVariants = null)
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
        bool visualBlocking = captures.Any(capture => capture.Findings.Count > 0);
        IReadOnlyList<string> variants = requiredVariants ?? ["normal", "narrow", "scale125", "scale150", "scale200"];
        bool complete = variants.All(variant => captures.Any(capture => capture.Variant == variant))
            && captures.All(capture => File.Exists(Path.Combine(root, capture.File))
                && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, capture.File)))) == capture.Sha256);
        bool reviewed = complete && rating is not null && !string.IsNullOrWhiteSpace(rating.Notes)
            && rating.Usability is >= 1 and <= 5 && rating.Aesthetics is >= 1 and <= 5
            && captures.All(capture => rating.CaptureHashes.Contains(capture.Sha256, StringComparer.Ordinal));
        if (!complete) findings.Add("Missing or altered captures; visual evidence cannot pass.");
        if (!reviewed) findings.Add("Usability and aesthetics require a review tied to all current capture hashes.");
        return new(surface.Name, surface.RequiredFor2D, functional,
            visualBlocking ? "Failed" : reviewed ? rating!.Usability >= 4 ? "Passed" : "Failed" : "Unverified",
            visualBlocking ? "Failed" : reviewed ? rating!.Aesthetics >= 4 ? "Passed" : "Failed" : "Unverified",
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
            ResourceItem? threeDRoom = null;
            string? threeDRouteObject = null;
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
            if(surface.Kind==ResourceKind.Terrain)
            {
                string terrainModel=resources.CreateResource(ResourceFolderPolicy.RootFor(project,ResourceKind.Model),ResourceKind.Model,"Judge terrain prop");
                using ModelViewerControl importer=new(terrainModel,project.RootPath);
                importer.ImportExternalModel(AnimatedGlbFixture.Write(Path.Combine(workspace,"Terrain object review source")));
                string terrainScript=resources.CreateResource(ResourceFolderPolicy.RootFor(project,ResourceKind.PgslScript),ResourceKind.PgslScript,"Judge terrain behavior");
                File.WriteAllText(terrainScript,"var speed = 2;\nvar active = true;\nvar label = \"Forest patrol\";\n");
                ResourceNames.Invalidate(project.RootPath);
                foreach (ResourceKind assetKind in new[] { ResourceKind.Physics, ResourceKind.Particle, ResourceKind.Shader })
                    if (!Flatten(resources.BuildTree()).Any(item => item.Kind == assetKind))
                        resources.CreateResource(ResourceFolderPolicy.RootFor(project, assetKind), assetKind, "Judge terrain " + assetKind);
                string pineModel = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Model), ResourceKind.Model, "Review conifer");
                using (ModelEditorControl pine = new(pineModel, project.RootPath))
                {
                    pine.ApplyFixtureTree(new ProceduralTreeOptions
                    {
                        Preset = ProceduralTreePreset.StylisedConifer, Quality = ProceduralModelQuality.Draft, Seed = 8102,
                    });
                    pine.Save();
                }
                string pinePart = Path.Combine(resource!.FullPath + ".parts", "Review pine.terrainpart.json");
                Directory.CreateDirectory(Path.GetDirectoryName(pinePart)!);
                using (TerrainEntityWizardDialog pine = new(pinePart, project.RootPath, TerrainEntityType.Tree))
                {
                    pine.SetName("Meadow pine");
                    pine.AddComponent(TerrainEntityComponentKinds.Model);
                    pine.SetComponentProperty(0, "Model", ResourceNames.Name(project.RootPath, pineModel));
                    pine.SaveAndCloseForTest();
                }
                using (TerrainEditorControl landscape = new(resource.FullPath, project.RootPath))
                {
                    landscape.ApplyGeneration(new TerrainGenParams
                    {
                        Preset = TerrainPreset.Flatlands, ResolutionX = 65, ResolutionZ = 65,
                        CellSize = 1, MinHeight = -8, MaxHeight = 16, Seed = 2401,
                    });
                    float centerX = landscape.Terrain.OriginX + 32, centerZ = landscape.Terrain.OriginZ + 32;
                    landscape.GeneratePaths(new Genesis.World.Terrain.TerrainPathSettings { Seed = 2403, PathCount = 2, Width = 3 });
                    landscape.FillBasinPond(centerX + 8, centerZ + 5, 18, 14, 4);
                    landscape.ScatterFoliage(new Genesis.World.Foliage.FoliageScatterSettings
                    {
                        Seed = 2402, Preset = Genesis.World.Foliage.FoliagePreset.Meadow,
                        MaximumInstances = 1200, Density = .65f, MinimumSpacing = .75f,
                    });
                    foreach ((float x, float z) in new[] { (-12f, -8f), (-7f, -13f), (-16f, 2f) })
                        landscape.PlaceTerrainEntity(pinePart, centerX + x, centerZ + z);
                    landscape.Nature.PointsOfInterest.Add(new() { Name = "Meadow overlook", Position = new(centerX, landscape.Terrain.SampleHeight(centerX, centerZ), centerZ) });
                    landscape.Save();
                }
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
            if (surface.Name is "Room" or "Pathing" or "Physics")
                (threeDRoom, threeDRouteObject) = CreateThreeDInspectionResources(surface, resources, project, workspace);
            List<(string Variant, Size Size, float Scale)> variants = new()
                { ("normal", new Size(1480, 900), 1f), ("narrow", new Size(1080, 700), 1f),
                  ("scale125", new Size(1480, 900), 1.25f), ("scale150", new Size(1480, 900), 1.5f),
                  ("scale200", new Size(1480, 900), 2f) };
            if (surface.Name is "Room" or "Pathing")
                variants.AddRange(variants.Select(item => ("3d-" + item.Variant, item.Size, item.Scale)).ToArray());
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
            {
                foreach (string state in new[] { "tube", "tube-narrow", "tube-scale200" })
                    variants.Add((state, new Size(state.EndsWith("narrow", StringComparison.Ordinal) ? 1080 : 1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
                foreach (string state in new[] { "from-image-dialog", "from-image-dialog-narrow", "from-image-dialog-scale200", "from-image-lower-dialog-scale200",
                    "texture-paint-dialog", "texture-paint-dialog-narrow", "texture-paint-dialog-scale200" })
                    variants.Add((state, new Size(state.EndsWith("narrow", StringComparison.Ordinal) ? 1080 : 1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
                foreach (string state in new[] { "viewer", "viewer-narrow", "viewer-scale200", "use-in-game", "use-in-game-scale200",
                    "game-steps-scale200", "options-scale200", "details-scale200", "details-lower-scale200", "texture-scale200", "rig-scale200", "outliner-scale200", "empty" })
                    variants.Add((state, new Size(state.EndsWith("narrow", StringComparison.Ordinal) ? 1080 : 1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
                foreach (string page in new[] { "create", "edit", "select", "texture", "rig", "outliner" })
                {
                    if (page is "edit" or "select") variants.Add((page + "-scale200", new Size(1480, 900), 2f));
                    variants.Add((page + "-lower-scale200", new Size(1480, 900), 2f));
                }
                foreach (string page in new[] { "rig", "pose", "animate" })
                {
                    variants.Add((page + "-dialog", new Size(1440, 900), 1f));
                    variants.Add((page + "-dialog-scale200", new Size(1480, 900), 2f));
                }
                variants.Add(("auto-rig-dialog", new Size(1480, 900), 1f));
                variants.Add(("auto-rig-dialog-scale200", new Size(1480, 900), 2f));
                foreach (string step in new[] { "orient", "review", "bind", "animate" })
                {
                    variants.Add(("auto-rig-" + step + "-dialog", new Size(1480, 900), 1f));
                    variants.Add(("auto-rig-" + step + "-dialog-scale200", new Size(1480, 900), 2f));
                    if (step is "orient" or "review" or "animate")
                        variants.Add(("auto-rig-" + step + "-lower-dialog-scale200", new Size(1480, 900), 2f));
                }
            }
            if (surface.Name == "Terrain")
            {
                foreach (string mode in new[] { "create", "sculpt", "paint", "paths", "foliage", "water", "water-river", "water-preview", "environment", "objects" })
                {
                    variants.Add((mode, new Size(1480, 900), 1f));
                    variants.Add((mode + "-scale200", new Size(1480, 900), 2f));
                    variants.Add((mode + "-lower-scale200", new Size(1480, 900), 2f));
                }
                foreach (string state in new[] { "use-in-game", "use-in-game-scale200", "game-steps-scale200", "inspector-scale200",
                    "options-scale200", "view-options-scale200", "camera-options-scale200", "more-tools-scale200", "panels-scale200",
                    "create-dialog", "create-dialog-scale200", "create-lower-dialog-scale200",
                    "scatter-dialog", "scatter-dialog-scale200", "scatter-lower-dialog-scale200" })
                    variants.Add((state, new Size(1480, 900), state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
                foreach (string state in new[] { "paths-edit", "foliage-generate", "material", "landmark",
                    "water-properties-surface", "water-properties-flow", "water-properties-gameplay" })
                {
                    variants.Add((state + "-dialog", new Size(1480, 900), 1f));
                    variants.Add((state + "-dialog-scale200", new Size(1480, 900), 2f));
                    if (state != "landmark") variants.Add((state + "-lower-dialog-scale200", new Size(1480, 900), 2f));
                }
                foreach (string state in new[] { "water-inspector-scale200", "objects-inspector-scale200" })
                    variants.Add((state, new Size(1480, 900), 2f));
                foreach(string state in new[]{"source-code","source-settings","source-heightmap","source-cave","source-error",
                    "part-identity","part-category","part-review","part-model","part-texture","part-physics","part-script","part-shader","part-particle","part-audio","part-condition"})
                {
                    variants.Add((state+"-dialog",new Size(1480,900),1f));
                    variants.Add((state+"-dialog-scale200",new Size(1480,900),2f));
                    if(state is "source-settings" or "part-model" or "part-texture" or "part-physics" or "part-condition")
                        variants.Add((state+"-lower-dialog-scale200",new Size(1480,900),2f));
                }
            }
            foreach (string state in ThreeDVariants(surface.Name))
                if (!variants.Any(item => item.Variant == state))
                    variants.Add((state, new Size(state.EndsWith("narrow", StringComparison.Ordinal) ? 1080 : 1480, 900),
                        state.EndsWith("scale200", StringComparison.Ordinal) ? 2f : 1f));
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
                    if (variant.Contains("-dialog", StringComparison.Ordinal) && inspected is ModelEditorControl modelEditor)
                    {
                        editorHost = host;
                        if (variant.StartsWith("texture-paint", StringComparison.Ordinal))
                        {
                            ResourceItem image = Flatten(resources.BuildTree()).First(item => item.Kind == ResourceKind.Image
                                && Path.GetFileName(item.FullPath) == "Checkpoint.image.json");
                            using ModelImageDialog creation = modelEditor.CreateImageDialog(image.FullPath);
                            modelEditor.AddImageGeometry(creation.Result!);
                            host = modelEditor.CreateTexturePaintDialog();
                        }
                        else if (variant.StartsWith("from-image", StringComparison.Ordinal))
                        {
                            ResourceItem image = Flatten(resources.BuildTree()).First(item => item.Kind == ResourceKind.Image
                                && Path.GetFileName(item.FullPath) == "Checkpoint.image.json");
                            host = modelEditor.CreateImageDialog(image.FullPath);
                            if (variant.Contains("lower", StringComparison.Ordinal))
                                host.Shown += (_, _) => ((ModelImageDialog)host).ShowGameSteps();
                        }
                        else if (variant.StartsWith("auto-rig", StringComparison.Ordinal))
                        {
                            ModelRigWizardDialog wizard = new(resource!.FullPath, project.RootPath,
                                ModelRigWizardSuite.Fixture(Genesis.Runtime.Modeling.GModelBodyPlan.Humanoid, 1));
                            int step = variant.Split('-')[2] switch { "orient" => 1, "review" => 2, "bind" => 3, "animate" => 4, _ => 0 };
                            wizard.Setup.Body = Genesis.Runtime.Modeling.GModelBodyPlan.Humanoid;
                            wizard.Setup.OrientationConfirmed = true; wizard.Setup.ReuseExistingRig = false;
                            if (step >= 2) WaitModelOperation(wizard.DetectAsync());
                            if (step >= 3)
                            {
                                foreach (var joint in wizard.Setup.Joints) joint.Reviewed = true;
                                WaitModelOperation(wizard.BindAsync());
                            }
                            if (step >= 4) WaitModelOperation(wizard.GenerateAsync([new() { Name = "Walk", Duration = 1.4f }]));
                            wizard.GoToStep(step); host = wizard;
                        }
                        else
                        {
                            modelEditor.ApplyAnimationWorkspace(ModelPoseWorkflowSuite.Fixture(), "");
                            ModelAnimationStudioDialog animation = modelEditor.CreateAnimationStudio(1);
                            var rest = animation.SaveCurrentPose("Rest");
                            animation.Preview.SelectAnimationNode("Shoulder");
                            animation.Preview.RotateSelectedAnimationNode(new System.Numerics.Vector3(0, 0, 55));
                            var raised = animation.SaveCurrentPose("Arm raised");
                            animation.GoToPage(2);
                            DataGridView keys = (DataGridView)animation.Controls.Find("ModelPoseAssignments", true).Single();
                            keys.Rows.Add(1, rest.Id, "Smooth"); keys.Rows.Add(30, raised.Id, "Smooth"); keys.Rows.Add(60, rest.Id, "Linear");
                            animation.GenerateFrames();
                            animation.GoToPage(variant.StartsWith("animate", StringComparison.Ordinal) ? 2 : variant.StartsWith("pose", StringComparison.Ordinal) ? 1 : 0);
                            host = animation;
                        }
                        inspected = host;
                    }
                }
                else if (resource is not null)
                {
                    ResourceItem openedResource = surface.Name == "Room" && variant.StartsWith("3d-", StringComparison.Ordinal)
                        ? threeDRoom! : resource;
                    IStudioDocument opened = shell.OpenStudioResource(openedResource);
                    inspected = opened is Genesis.Application.Studio.Docking.SuiteEditorDocument suite
                        ? suite.Surface.AsControl : (Control)opened;
                    shell.Inspector.Inspect(openedResource);
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
                    if (variant.StartsWith("3d-", StringComparison.Ordinal))
                    {
                        if (roomWorkflow.Room.Dimension != Genesis.Runtime.Scene.RoomDimension.ThreeD)
                            throw new InvalidOperationException("3D Room evidence opened a 2D document.");
                        roomWorkflow.FrameContentForTest();
                    }
                    string page = variant.Split('-')[0];
                    RoomNavSection section = page switch { "instances" => RoomNavSection.Instances, "tilesets" or "rename" => RoomNavSection.Tilesets,
                        "backgrounds" or "background" => RoomNavSection.Backgrounds, "views" or "view" or "follow" or "output" or "camera" => RoomNavSection.Views,
                        "settings" or "physics" => RoomNavSection.Settings, _ => RoomNavSection.Objects };
                    roomWorkflow.Navigation.SetSection(section);
                    if (variant.StartsWith("3d-camera-list", StringComparison.Ordinal))
                    { roomWorkflow.Navigation.SetSection(RoomNavSection.Views); roomWorkflow.RefreshSceneViews(); }
                    if (variant.StartsWith("3d-physics-overlay", StringComparison.Ordinal))
                    { roomWorkflow.SetPhysicsOverlayVisible(true); roomWorkflow.StepPhysicsPreview(); }
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
                    if (variant.StartsWith("3d-", StringComparison.Ordinal))
                    {
                        physicsSurface.SetPreview2D(false);
                        string objectFile = ResourceNames.Resolve(project.RootPath, threeDRouteObject, ResourceType.Object);
                        string modelReference = (string)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(objectFile))["components"]![0]!["props"]!["ModelAsset"]!;
                        if (!physicsSurface.ChooseModel(modelReference)) throw new InvalidOperationException("Physics could not preview the saved 3D Model.");
                        if (variant.StartsWith("3d-use-in-game", StringComparison.Ordinal) || variant == "3d-game-steps-scale200")
                            physicsSurface.CommandBar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                    }
                    else if (variant.StartsWith("2d", StringComparison.Ordinal))
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
                if (inspected is TerrainEditorControl terrainSurface)
                {
                    EditorCommandBar commands = Descendants(terrainSurface).OfType<EditorCommandBar>().Single(bar => bar.Name == "TerrainCommands");
                    ToolStripDropDownButton options = commands.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                    TerrainEditorControl.TerrainEditorMode? mode = variant.Split('-')[0] switch
                    {
                        "create" => TerrainEditorControl.TerrainEditorMode.Generate,
                        "sculpt" => TerrainEditorControl.TerrainEditorMode.Sculpt,
                        "paint" => TerrainEditorControl.TerrainEditorMode.Paint,
                        "paths" => TerrainEditorControl.TerrainEditorMode.Paths,
                        "foliage" => TerrainEditorControl.TerrainEditorMode.Foliage,
                        "water" => TerrainEditorControl.TerrainEditorMode.Water,
                        "environment" => TerrainEditorControl.TerrainEditorMode.Environment,
                        "objects" => TerrainEditorControl.TerrainEditorMode.Entities,
                        _ => null,
                    };
                    if (mode is { } active) terrainSurface.SetMode(active);
                    if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant == "game-steps-scale200")
                        commands.Items.OfType<ToolStripButton>().Single(button => button.Text == "Use in game").PerformClick();
                    if (variant.EndsWith("inspector-scale200", StringComparison.Ordinal))
                        options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Panels").DropDownItems.OfType<ToolStripMenuItem>()
                            .Single(item => item.Text == "Objects and Inspector").PerformClick();
                    if (variant.StartsWith("water-inspector", StringComparison.Ordinal))
                        Descendants(terrainSurface).OfType<TerrainComponentsPanel>().Single()
                            .Select(TerrainComponentsPanel.ComponentKind.Water, terrainSurface.Nature.WaterBodies[0].Id);
                    if (variant.StartsWith("objects-inspector", StringComparison.Ordinal))
                        Descendants(terrainSurface).OfType<TerrainComponentsPanel>().Single()
                            .Select(TerrainComponentsPanel.ComponentKind.Entity, terrainSurface.Nature.PlacedEntities[0].Id);
                    if (variant == "options-scale200") openMenu = options;
                    if (variant is "view-options-scale200" or "camera-options-scale200" or "more-tools-scale200" or "panels-scale200")
                        openMenu = options.DropDownItems.OfType<ToolStripDropDownItem>().Single(item => item.Text == (variant.Split('-')[0] switch
                            { "view" => "View", "camera" => "Camera", "more" => "More terrain tools", _ => "Panels" }));
                    if (variant.Contains("-dialog", StringComparison.Ordinal))
                    {
                        if (variant.StartsWith("water-properties-", StringComparison.Ordinal))
                            host = new TerrainWaterDialog(terrainSurface.Nature.WaterBodies, System.Numerics.Vector3.Zero);
                        else if (variant.StartsWith("paths-edit-", StringComparison.Ordinal)) host = new TerrainPathDialog(terrainSurface.Nature.PathSettings);
                        else if (variant.StartsWith("foliage-generate-", StringComparison.Ordinal)) host = new FoliageScatterDialog(terrainSurface.Nature.FoliageSettings);
                        else if (variant.StartsWith("material-", StringComparison.Ordinal)) host = terrainSurface.CreateLayerMaterialDialog(0);
                        else if (variant.StartsWith("landmark-", StringComparison.Ordinal)) host = terrainSurface.CreatePointOfInterestDialog(terrainSurface.Nature.PointsOfInterest[0].Id);
                        else if (variant.StartsWith("scatter-", StringComparison.Ordinal)) host = terrainSurface.CreateScatterSettingsDialog();
                        else if(variant.StartsWith("source-",StringComparison.Ordinal))
                            host=new TerrainSourceWizard(new TerrainCreationRecipe {Name="Review landscape",Source=TerrainCreationSource.Code,
                                Width=160,Length=160,Spacing=4,MinHeight=-8,MaxHeight=55},project.RootPath);
                        else if(variant.StartsWith("part-",StringComparison.Ordinal))
                        {
                            string partPath=Path.Combine(workspace,"Review.terrainpart.json");
                            if(File.Exists(partPath))File.Delete(partPath);
                            var part=new TerrainEntityWizardDialog(partPath,project.RootPath,TerrainEntityType.Object);
                            part.SetName("Forest prop");
                            string partKind=variant.Split('-')[1] switch
                            {
                                "model"=>TerrainEntityComponentKinds.Model,"texture"=>TerrainEntityComponentKinds.Texture,
                                "physics"=>TerrainEntityComponentKinds.Physics,"script"=>TerrainEntityComponentKinds.Script,
                                "shader"=>TerrainEntityComponentKinds.Shader,"particle"=>TerrainEntityComponentKinds.ParticleEmitter,
                                "audio"=>TerrainEntityComponentKinds.AudioEmitter,"condition"=>TerrainEntityComponentKinds.Condition,_=>""
                            };
                            if(partKind.Length>0)
                            {
                                part.AddComponent(partKind);
                                (ResourceKind Kind,string Property)? binding=partKind switch
                                {
                                    TerrainEntityComponentKinds.Model=>(ResourceKind.Model,"Model"),TerrainEntityComponentKinds.Texture=>(ResourceKind.Image,"Texture"),
                                    TerrainEntityComponentKinds.Physics=>(ResourceKind.Physics,"Physics"),TerrainEntityComponentKinds.Script=>(ResourceKind.PgslScript,"Script"),
                                    TerrainEntityComponentKinds.Shader=>(ResourceKind.Shader,"Shader"),TerrainEntityComponentKinds.ParticleEmitter=>(ResourceKind.Particle,"Particle"),
                                    TerrainEntityComponentKinds.AudioEmitter=>(ResourceKind.Audio,"Audio"),_=>null
                                };
                                if(binding is { } reference)
                                {
                                    ResourceItem[] matching=Flatten(resources.BuildTree()).Where(item=>item.Kind==reference.Kind).ToArray();
                                    ResourceItem? linked=matching.FirstOrDefault(item=>item.Name.StartsWith("Judge terrain",StringComparison.Ordinal))??matching.FirstOrDefault();
                                    if(linked is not null)part.SetComponentProperty(0,reference.Property,ResourceNames.Name(project.RootPath,linked.FullPath));
                                }
                                if(partKind==TerrainEntityComponentKinds.Model)part.SetComponentProperty(0,"AnimationClip","Wave");
                                if(partKind==TerrainEntityComponentKinds.Condition)part.SetComponentProperty(0,"If","PointDistance(0, 0, x, y) < 10");
                                part.GoToPage(2);
                            }
                            else part.GoToPage(variant.StartsWith("part-category",StringComparison.Ordinal)?1:variant.StartsWith("part-review",StringComparison.Ordinal)?3:0);
                            host=part;
                        }
                        else host = new TerrainCreationWizardDialog("Review landscape");
                        inspected = host; dialog = true;
                    }
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
                        pathingSurface.Code.CodeText = variant.StartsWith("3d-", StringComparison.Ordinal)
                            ? "pathing \"Model patrol\" {\n dimension: ThreeD\n object: \"" + threeDRouteObject + "\"\n preview_agents: 1\n mode: WaypointPatrol\n loop: PingPong\n speed: 2\n animation: \"Wave\"\n waypoint \"Start\" (-4, 0, -4) wait 0 curve false\n waypoint \"Bend\" (0, 0, 4) wait 0 curve true\n waypoint \"End\" (4, 0, -4) wait 0 curve false\n}\n"
                            : "pathing \"Sprite patrol\" {\n dimension: TwoD\n object: \"" + reference + "\"\n preview_agents: 1\n mode: WaypointPatrol\n loop: PingPong\n speed: 80\n animation: \"Run\"\n waypoint \"Start\" (64, 64, 0) wait 0 curve false\n waypoint \"Bend\" (160, 192, 0) wait 0 curve true\n waypoint \"End\" (320, 96, 0) wait 0 curve false\n}\n";
                        pathingSurface.Save();
                        if (variant.StartsWith("3d-", StringComparison.Ordinal)
                            && pathingSurface.Dimension != Genesis.Runtime.Navigation.PathingDimension.ThreeD)
                            throw new InvalidOperationException("3D Pathing evidence remained on the XY plane.");
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
                    if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant.StartsWith("3d-use-in-game", StringComparison.Ordinal)
                        || variant is "game-steps-scale200" or "3d-game-steps-scale200")
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
                    if(host is TerrainSourceWizard sourceWizard)
                    {
                        TabControl tabs=Descendants(sourceWizard).OfType<TabControl>().Single(tab=>tab.Name=="TerrainRecipeTabs");
                        if(variant.StartsWith("source-settings",StringComparison.Ordinal))tabs.SelectedIndex=1;
                        else if(variant.StartsWith("source-heightmap",StringComparison.Ordinal))
                        {
                            var heightmap=Genesis.Application.Editors.Image.Imaging.ImageWorkspace.CreateBlank(64,64,Color.FromArgb(128,128,128));
                            WaitModelOperation(sourceWizard.UpdateHeightmapAsync(heightmap));
                        }
                        else
                        {
                            if(variant.StartsWith("source-cave",StringComparison.Ordinal))
                                Descendants(sourceWizard).OfType<Button>().Single(button=>button.Text=="Cave").PerformClick();
                            if(variant.StartsWith("source-error",StringComparison.Ordinal))sourceWizard.Code.CodeText="height = missing_function(x);";
                            WaitModelOperation(sourceWizard.GeneratePreviewAsync());
                            if(variant.StartsWith("source-code",StringComparison.Ordinal))
                            {
                                sourceWizard.Code.CodeText += "\n// Continue editing the next sample here.\nheight = clamp(noise(x, z), ";
                                sourceWizard.Code.MoveCaret(sourceWizard.Code.CodeText.Length);
                            }
                        }
                        sourceWizard.ApplyInterfaceLayout();GateSuite.Pump(3,20);
                    }
                    if (host is TerrainWaterDialog waterDialog)
                        Descendants(waterDialog).OfType<TabControl>().Single(tabs => tabs.Name == "TerrainWaterSettingsTabs").SelectedIndex =
                            variant.StartsWith("water-properties-flow", StringComparison.Ordinal) ? 1 : variant.StartsWith("water-properties-gameplay", StringComparison.Ordinal) ? 2 : 0;
                    if(host is TerrainEntityWizardDialog||host is TerrainSourceWizard||host is TerrainCreationWizardDialog
                        ||host is TerrainWaterDialog||host is TerrainPathDialog||host is FoliageScatterDialog
                        ||variant.StartsWith("scatter-", StringComparison.Ordinal)||variant.StartsWith("material-", StringComparison.Ordinal))
                    {
                        if(variant.Contains("-lower-",StringComparison.Ordinal))
                        {
                            ScrollableControl scroll=Descendants(host).OfType<ScrollableControl>().First(control=>control.Visible&&control.AutoScroll);
                            scroll.AutoScrollPosition=new Point(0,int.MaxValue);GateSuite.Pump(3,20);
                        }
                        if(host is TerrainEntityWizardDialog&&variant.StartsWith("part-condition",StringComparison.Ordinal))
                        {
                            CodeEditor condition=Descendants(host).OfType<CodeEditor>().Single(code=>code.Name=="TerrainConditionCode");
                            condition.CodeText="PointDistance(0, ";condition.MoveCaret(condition.CodeText.Length);
                        }
                    }
                    if (host is ModelRigWizardDialog framedWizard) framedWizard.Preview.FrameModelForTest();
                    if (host is ModelAnimationStudioDialog framedAnimation) framedAnimation.Preview.FrameModelForTest();
                    if (inspected is ModelViewerControl modelView)
                    {
                        if (modelView is ModelEditorControl pullEditor && variant.StartsWith("push-pull", StringComparison.Ordinal))
                        {
                            GModelAsset pulled = GModelPrimitiveFactory.CreateCube("Face pull example", 1);
                            ModelPartBuilder.ExtrudeFaces(pulled.Meshes[0], [8], .35f);
                            pullEditor.ApplyAnimationWorkspace(pulled, "");
                            Descendants(pullEditor).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>())
                                .Single(button => button.Text?.EndsWith("Edit", StringComparison.Ordinal) == true).PerformClick();
                            pullEditor.SelectTool(ModelAuthoringTool.Push); pullEditor.FrameModel();
                            pullEditor.Viewport.Camera.Yaw = MathF.PI - .55f; pullEditor.Viewport.Camera.Pitch = -.2f;
                        }
                        if (modelView is ModelEditorControl tubeEditor && variant.StartsWith("tube", StringComparison.Ordinal))
                        {
                            tubeEditor.ApplyAnimationWorkspace(new Genesis.Runtime.Modeling.GModelAsset(), "");
                            tubeEditor.CreateTube(Enumerable.Range(0, 40).Select(i => new System.Numerics.Vector3(-1.8f + i * .09f, MathF.Sin(i * .13f) * .8f, 0)).ToArray(), .22f, .75f);
                            tubeEditor.SelectTool(ModelAuthoringTool.Tube); tubeEditor.ConfigureTube(.22f, .75f);
                            tubeEditor.SetCameraView("Front"); tubeEditor.FrameModel();
                            Control guide = Descendants(tubeEditor).Single(control => control.Name == "ModelTubeGuide");
                            ((ScrollableControl)guide.Parent!.Parent!.Parent!).ScrollControlIntoView(guide.Parent.Parent);
                        }
                        modelView.SelectClip("Wave"); modelView.SetFrame(0);
                        if (variant.StartsWith("use-in-game", StringComparison.Ordinal) || variant == "game-steps-scale200")
                            Descendants(modelView).OfType<EditorCommandBar>().Single().Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                        if (variant == "options-scale200") openMenu = Descendants(modelView).OfType<EditorCommandBar>().Single().Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                        if (variant.StartsWith("details-", StringComparison.Ordinal))
                        {
                            Descendants(modelView).OfType<EditorCommandBar>().Single().Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options")
                                .DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Model details panel").PerformClick();
                            if (variant.Contains("-lower-", StringComparison.Ordinal))
                            {
                                FlowLayoutPanel details = Descendants(modelView).OfType<FlowLayoutPanel>().Single(panel => panel.Name == "ModelEditorInspector");
                                details.AutoScrollPosition = new Point(0, Math.Max(0, details.DisplayRectangle.Height - details.ClientSize.Height));
                            }
                        }
                        if (modelView is ModelEditorControl modelTools && variant.EndsWith("scale200", StringComparison.Ordinal)
                            && variant.Split('-')[0] is "create" or "edit" or "select" or "texture" or "rig" or "outliner")
                        {
                            string target = variant.Split('-')[0];
                            Descendants(modelTools).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>())
                                .Single(button => button.Text!.Split('\n').Last().Equals(target, StringComparison.OrdinalIgnoreCase)).PerformClick();
                            modelView.SelectClip("Wave"); modelView.SetFrame(0);
                            if (variant.Contains("-lower-", StringComparison.Ordinal))
                            {
                                FlowLayoutPanel page = Descendants(modelTools).OfType<FlowLayoutPanel>().Single(panel => panel.Visible && panel.AutoScroll);
                                page.AutoScrollPosition = new Point(0, Math.Max(0, page.DisplayRectangle.Height - page.ClientSize.Height));
                                GateSuite.Pump(3, 20);
                            }
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
                    if (inspected is TerrainEditorControl terrainLayout)
                    {
                        if (variant.StartsWith("water-river", StringComparison.Ordinal) || variant.StartsWith("water-preview", StringComparison.Ordinal))
                            Descendants(terrainLayout).OfType<ComboBox>().Single(combo => combo.Name == "TerrainWaterWorkflow")
                                .SelectedIndex = variant.StartsWith("water-river", StringComparison.Ordinal) ? 1 : 2;
                        terrainLayout.ApplyInterfaceLayout(); GateSuite.Pump(3, 20);
                        if (variant.Contains("-lower-", StringComparison.Ordinal) || variant == "game-steps-scale200")
                        {
                            FlowLayoutPanel page = Descendants(terrainLayout).OfType<FlowLayoutPanel>().Single(panel => panel.Visible && panel.AutoScroll
                                && (panel.Name.StartsWith("TerrainMode", StringComparison.Ordinal) || panel.Name == "TerrainUseInGame"));
                            page.AutoScrollPosition = new Point(0, int.MaxValue); GateSuite.Pump(3, 20);
                        }
                    }
                    if (host is ModelRigWizardDialog && variant.Contains("-lower-", StringComparison.Ordinal))
                    {
                        FlowLayoutPanel fields = Descendants(host).OfType<FlowLayoutPanel>().Single(panel => panel.Name == "ModelRigWizardFields");
                        Control last = fields.Controls.Cast<Control>().Last();
                        fields.ScrollControlIntoView(last);
                        GateSuite.Pump(3, 20);
                        fields.AutoScrollPosition = new Point(0, int.MaxValue);
                        GateSuite.Pump(3, 20);
                        if (!fields.ClientRectangle.Contains(last.Bounds))
                            throw new InvalidOperationException($"The wizard's final settings cannot be scrolled into view: {last.Bounds} within {fields.ClientRectangle}; position {fields.AutoScrollPosition}, display {fields.DisplayRectangle}, range {fields.VerticalScroll.Maximum}, page {fields.VerticalScroll.LargeChange}.");
                    }
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
                    if (inspected is PhysicsEditorControl && variant is "game-steps-scale200" or "3d-game-steps-scale200" or "quick-fields-scale200")
                    {
                        string name = variant is "game-steps-scale200" or "3d-game-steps-scale200" ? "PhysicsUseInGame" : "PhysicsQuickSetup";
                        FlowLayoutPanel scroll = Descendants(inspected).OfType<FlowLayoutPanel>().Single(control => control.Name == name);
                        scroll.AutoScrollPosition = new Point(0, Math.Max(0, scroll.DisplayRectangle.Height - scroll.ClientSize.Height));
                        GateSuite.Pump(2, 10);
                    }
                    if (inspected is PathingEditorControl && variant is "game-steps-scale200" or "3d-game-steps-scale200" or "quick-points-scale200" or "advanced" or "advanced-scale200" or "mode-search-scale200" or "mode-wander-scale200" or "mode-follow-scale200")
                    {
                        if (variant is "game-steps-scale200" or "3d-game-steps-scale200")
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
        string manifestFile = Path.Combine(output, "capture-manifest.json");
        JudgeCaptureManifest? previous = File.Exists(manifestFile)
            ? JsonSerializer.Deserialize<JudgeCaptureManifest>(File.ReadAllText(manifestFile)) : null;
        File.WriteAllText(manifestFile, JsonSerializer.Serialize(
            MergeCaptures(ProductFingerprint(), previous, captures), HeadlessHarness.JsonOptions));
        int failed = captures.Count(capture => string.IsNullOrEmpty(capture.File));
        Console.WriteLine($"Captured {captures.Count - failed} populated layouts; {failed} inspection failures. Subjective ratings remain unverified.");
        return failed == 0 ? 0 : 1;
    }

    private static List<string> InspectControls(Control root)
    {
        List<string> findings = [];
        foreach (CodeEditor editor in Descendants(root).OfType<CodeEditor>())
        {
            if (!editor.HasIntelligenceProvider) findings.Add("Code surface lacks a completion/signature provider.");
            if (editor.Visible && editor.Parent is TableLayoutPanel parent
                && (editor.Left < parent.ClientRectangle.Left || editor.Right > parent.ClientRectangle.Right))
                findings.Add("Code area exceeds its field width.");
        }
        foreach (VisualActionBuilderControl builder in Descendants(root).OfType<VisualActionBuilderControl>())
        foreach (TreeView tree in Descendants(builder).OfType<TreeView>().Where(tree => tree.Visible))
            if (tree.ClientSize.Height < tree.Font.Height * 3)
                findings.Add("Action toolbox has insufficient space to choose actions.");
        foreach (RichTextBox help in Descendants(root).OfType<RichTextBox>()
                     .Where(help => help.Name == "CodeSignatureHelp" && help.Visible && help.IsHandleCreated && help.TextLength > 0))
        {
            for (int index = 0; index < help.TextLength; index++)
            {
                if (index + 1 < help.TextLength && help.Text[index + 1] != '\n') continue;
                int visibleEnd = index > 0 && help.Text[index] == '\r' ? index - 1 : index;
                Point end = help.GetPositionFromCharIndex(visibleEnd);
                Size glyph = TextRenderer.MeasureText(help.Text[visibleEnd].ToString(), EditorChrome.SmallFont,
                    Size.Empty, TextFormatFlags.NoPadding);
                if (end.X + glyph.Width <= help.ClientSize.Width && end.Y + glyph.Height <= help.ClientSize.Height + 4) continue;
                findings.Add("Call signature or description is clipped.");
                break;
            }
        }
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

    private static void WaitModelOperation(Task task)
    {
        long deadline = Environment.TickCount64 + 30000;
        while (!task.IsCompleted && Environment.TickCount64 < deadline) GateSuite.Pump(1, 5);
        if (!task.IsCompleted) throw new TimeoutException("Model wizard operation did not finish in 30 seconds.");
        task.GetAwaiter().GetResult();
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
