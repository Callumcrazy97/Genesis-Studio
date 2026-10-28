using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Runtime;
using Genesis.Runtime.Input;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;
using Genesis.World.Terrain;
using Genesis.World.Water;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Designer walkthrough the Terrain Editor, Model Editor, and Shader Editor actually have to
/// survive: a grassy field with Play-mode sway, two authored trees planted as a forest, then a
/// carved pond that fills a volume and carries physics plus an assigned shader.
/// </summary>
internal static class TerrainWalkthroughSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Walkthrough");
        ProjectSession project = HeadlessHarness.Require(ctx.Project, "Project fixture");
        ResourceService resources = HeadlessHarness.Require(ctx.Resources, "Resource service");

        HeadlessHarness.RunCase(ctx.Report, "Walkthrough.Terrain.GrassForestPond", () =>
        {
            string terrainPath = resources.CreateResource(
                Path.Combine(resources.AssetsRoot, "Terrain"), ResourceKind.Terrain, "Walkthrough Meadow");
            Directory.CreateDirectory(Path.Combine(resources.AssetsRoot, "Models"));
            Directory.CreateDirectory(Path.Combine(resources.AssetsRoot, "TerrainEntities"));
            Directory.CreateDirectory(Path.Combine(resources.AssetsRoot, "Shaders"));

            using Form host = GateSuite.NewHost(1120, 800);
            TerrainEditorControl editor = new(terrainPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(8, 25);

            HeadlessHarness.Step("author a grassy field and scatter swaying meadow grass", () =>
            {
                editor.ApplyGeneration(new TerrainGenParams
                {
                    Preset = TerrainPreset.Flatlands,
                    ResolutionX = 129,
                    ResolutionZ = 129,
                    CellSize = 1f,
                    MinHeight = -8f,
                    MaxHeight = 16f,
                    Seed = 2401,
                });
                editor.ScatterFoliage(new FoliageScatterSettings
                {
                    Seed = 2402,
                    Preset = FoliagePreset.Meadow,
                    MaximumInstances = 9000,
                    Density = 1f,
                    MinimumSpacing = 0.55f,
                    NearDistance = 28f,
                    FarDistance = 80f,
                    StreamingCellSize = 16f,
                    VisibleInstanceBudget = 6000,
                    TriangleBudget = 160000,
                });
                HeadlessHarness.Assert(
                    editor.Foliage.Instances.Count > 800,
                    "Meadow scatter produced too little grass to test Play sway.");
            });

            HeadlessHarness.Step("Play advances shader time and the grass actually sways", () =>
            {
                FoliageInstance blade = editor.Foliage.Instances
                    .OrderBy(instance => instance.Position.X * instance.Position.X
                        + instance.Position.Z * instance.Position.Z)
                    .First();
                editor.Viewport.Camera.Target = blade.Position + Vector3.UnitY * 0.35f;
                editor.Viewport.Camera.Distance = 6.5f;
                editor.Viewport.Camera.Pitch = -0.28f;
                editor.Viewport.Camera.Yaw = 0.62f;
                GateSuite.Pump(4, 20);

                using Bitmap rest = RequireCapture(editor, "Grass rest pose");
                SaveCapture(ctx, (Bitmap)rest.Clone(), "31-walkthrough-grass.png", "Grassy field", 24);

                editor.PlayPreview();
                HeadlessHarness.Assert(editor.IsPreviewPlaying, "Terrain Play did not start the preview clock.");
                float started = editor.PreviewTimeSeconds;
                editor.StepPreview(1f / 60f, 80);
                HeadlessHarness.Assert(
                    editor.PreviewTimeSeconds > started + 1.2f,
                    "StepPreview did not advance Play time far enough for blade sway.");
                GateSuite.Pump(3, 20);

                using Bitmap playing = RequireCapture(editor, "Grass after Play");
                SaveCapture(ctx, (Bitmap)playing.Clone(), "32-walkthrough-grass-play.png", "Swaying grass", 24);
                int swayed = CountDifferingPixels(rest, playing, step: 2, threshold: 8);
                HeadlessHarness.Assert(
                    editor.LastFoliagePerformance.SubmittedInstances > 0,
                    "Play captured no foliage instances.");
                HeadlessHarness.Assert(
                    swayed >= 5,
                    $"Play did not move swaying grass on screen (pixel delta={swayed}).");
                editor.PausePreview();
            });

            string oakPath = string.Empty;
            string pinePath = string.Empty;
            string pondId = string.Empty;
            string pondShaderRelative = string.Empty;
            HeadlessHarness.Step("prepare two canonical tree fixtures for the terrain walkthrough", () =>
            {
                oakPath = resources.CreateResource(
                    Path.Combine(resources.AssetsRoot, "Models"), ResourceKind.Model, "Walkthrough Oak");
                pinePath = resources.CreateResource(
                    Path.Combine(resources.AssetsRoot, "Models"), ResourceKind.Model, "Walkthrough Pine");

                using (Form oakHost = GateSuite.NewHost(960, 720))
                {
                    ModelEditorControl oak = new(oakPath, project.RootPath);
                    oakHost.Controls.Add(oak);
                    GateSuite.ShowHost(oakHost);
                    GateSuite.Pump(4, 20);
                    ProceduralMeshResult generated = oak.ApplyFixtureTree(new ProceduralTreeOptions
                    {
                        Preset = ProceduralTreePreset.WideOldOak,
                        Quality = ProceduralModelQuality.Draft,
                        Seed = 8101,
                    });
                    oak.Save();
                    HeadlessHarness.Assert(
                        generated.Vertices.Length > 0
                        
                        && File.Exists(oak.CanonicalModelPath),
                        "The oak tree was not generated and saved from the Model Editor.");
                }

                using (Form pineHost = GateSuite.NewHost(960, 720))
                {
                    ModelEditorControl pine = new(pinePath, project.RootPath);
                    pineHost.Controls.Add(pine);
                    GateSuite.ShowHost(pineHost);
                    GateSuite.Pump(4, 20);
                    ProceduralMeshResult generated = pine.ApplyFixtureTree(new ProceduralTreeOptions
                    {
                        Preset = ProceduralTreePreset.StylisedConifer,
                        Quality = ProceduralModelQuality.Draft,
                        Seed = 8102,
                    });
                    pine.Save();
                    HeadlessHarness.Assert(
                        generated.Vertices.Length > 0
                        
                        && File.Exists(pine.CanonicalModelPath),
                        "The pine tree was not generated and saved from the Model Editor.");
                }
            });

            HeadlessHarness.Step("plant those trees as a forest on the same meadow", () =>
            {
                string oakEntity = CreateTreeEntity(resources, project, "Walkthrough Oak Entity", oakPath);
                string pineEntity = CreateTreeEntity(resources, project, "Walkthrough Pine Entity", pinePath);
                float forestX = editor.Terrain.OriginX + 28f;
                float forestZ = editor.Terrain.OriginZ + 36f;
                int planted = 0;
                for (int row = 0; row < 3; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        string entity = ((row + column) & 1) == 0 ? oakEntity : pineEntity;
                        editor.PlaceTerrainEntity(entity, forestX + column * 6.5f, forestZ + row * 6.5f);
                        planted++;
                    }
                }

                HeadlessHarness.Assert(
                    editor.PlacedEntityCount == planted,
                    $"Forest placement wrote {editor.PlacedEntityCount} trees, expected {planted}.");
                editor.Viewport.Camera.Target = new Vector3(
                    forestX + 10f,
                    editor.Terrain.SampleHeight(forestX + 10f, forestZ + 6f) + 2.5f,
                    forestZ + 6f);
                editor.Viewport.Camera.Distance = 28f;
                editor.Viewport.Camera.Pitch = -0.38f;
                editor.Viewport.Camera.Yaw = 0.85f;
                GateSuite.Pump(5, 25);
                Bitmap forest = RequireCapture(editor, "Forest on the meadow");
                SaveCapture(ctx, forest, "33-walkthrough-forest.png", "Forest on meadow", 32);
            });

            HeadlessHarness.Step("carve a filled pond with physics and an assigned shader", () =>
            {
                string shaderPath = resources.CreateResource(
                    Path.Combine(resources.AssetsRoot, "Shaders"), ResourceKind.Shader, "Walkthrough Pond Shader");
                using (Form shaderHost = GateSuite.NewHost(960, 720))
                {
                    ShaderEditorControl shader = new(shaderPath, project.RootPath);
                    shaderHost.Controls.Add(shader);
                    GateSuite.ShowHost(shaderHost);
                    GateSuite.Pump(4, 20);
                    HeadlessHarness.Assert(
                        shader.SelectPreset("Pond Water"),
                        "The pond shader could not take the Pond Water preset.");
                    shader.Save();
                    HeadlessHarness.Assert(
                        shader.TargetType == ShaderTargetType.Terrain,
                        "The pond shader was not targeted at Terrain.");
                }

                float pondX = editor.Terrain.OriginX + (editor.Terrain.ResolutionX - 1) * editor.Terrain.CellSize - 28f;
                float pondZ = editor.Terrain.OriginZ + (editor.Terrain.ResolutionZ - 1) * editor.Terrain.CellSize * 0.5f;
                float surfaceBefore = editor.Terrain.SampleHeight(pondX, pondZ);
                TerrainWaterDefinition pond = editor.FillBasinPond(pondX, pondZ, 16f, 12f, physicsDepth: 3.6f);
                HeadlessHarness.Assert(
                    (pond.Kind == TerrainWaterKind.Water || pond.Kind == TerrainWaterKind.Pond)
                    && pond.HasFootprint
                    && pond.PhysicsDepth >= 3.5f
                    && pond.PhysicsMode == WaterPhysicsMode.None && !pond.Swimmable
                    && pond.FluidDensity >= 1000f,
                    "The pond should retain its footprint and depth while defaulting to decorative water.");
                TerrainWaterDefinition? fromPond = JsonSerializer.Deserialize<TerrainWaterDefinition>(
                    "{\"kind\":\"Pond\"}",
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                TerrainWaterDefinition? fromLake = JsonSerializer.Deserialize<TerrainWaterDefinition>(
                    "{\"kind\":\"Lake\"}",
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                HeadlessHarness.Assert(
                    fromPond is not null && fromPond.Kind == TerrainWaterKind.Water
                    && fromLake is not null && fromLake.Kind == TerrainWaterKind.Water,
                    "Existing Pond/Lake nature JSON did not load as Water.");
                HeadlessHarness.Assert(
                    editor.Terrain.SampleHeight(pondX, pondZ) < surfaceBefore - 0.4f,
                    "The pond basin was not carved before fill.");

                WaterWorkflowAssertions.SurfaceAndVolume(pond);

                string shaderRelative = ResourceNames.Name(project.RootPath, shaderPath, ResourceType.Shader);
                editor.SelectTerrainComponent(TerrainComponentsPanel.ComponentKind.Water, pond.Id);
                HeadlessHarness.Assert(editor.SelectedShaderTargetId == "water:" + pond.Id,
                    "Selecting water did not update the active component while its inventory panel was hidden.");
                string targetId = editor.SelectedShaderTargetId ?? string.Empty;
                editor.SetComponentShader(targetId, shaderRelative);
                pondId = pond.Id;
                pondShaderRelative = shaderRelative;
                HeadlessHarness.Assert(
                    targetId == "water:" + pond.Id
                    && editor.GetComponentShader(targetId) == shaderRelative
                    && editor.GetLiveInspectorValues().Any(value =>
                        value.PropertyPath == "Component.Shader" && Equals(value.Value, shaderRelative)),
                    "The pond shader was not assigned on the water body.");
                HeadlessHarness.Assert(
                    editor.Foliage.Instances.Count(instance =>
                        pond.ContainsHorizontal(
                            instance.Position.X,
                            instance.Position.Z,
                            TerrainWaterDefinition.FoliageExclusionPadding)) == 0,
                    "Meadow grass is still growing inside the pond footprint.");

                editor.SelectTerrainComponent(TerrainComponentsPanel.ComponentKind.Water, pond.Id);
                HeadlessHarness.Assert(editor.GetLiveInspectorValues().Any(value =>
                    value.PropertyPath == "Water.PhysicsMode" && Equals(value.Value, "None"))
                    && editor.TryApplyLiveInspectorValue("Water.PhysicsMode", "SwimmableVolume")
                    && !editor.TryApplyLiveInspectorValue("Water.PhysicsMode", "999")
                    && !editor.TryApplyLiveInspectorValue("Water.PhysicsDepth", double.NaN),
                    "Selected-water Inspector did not expose an explicit valid physics-mode edit.");
                editor.Undo();
                HeadlessHarness.Assert(editor.Nature.WaterBodies.Single(body => body.Id == pond.Id).PhysicsMode == WaterPhysicsMode.None,
                    "Undo did not restore decorative water.");
                editor.Redo();
                pond = editor.Nature.WaterBodies.Single(body => body.Id == pond.Id);
                HeadlessHarness.Assert(pond.PhysicsMode == WaterPhysicsMode.SwimmableVolume && pond.Swimmable,
                    "Redo did not restore the explicit swimming opt-in.");
                editor.RebuildPhysicsPreview();
                editor.DropPlayableIntoWater(pond.Id);
                editor.StepPhysicsPreview(1f / 60f, 180);
                HeadlessHarness.Assert(
                    editor.PlayableIsActive
                    && editor.PlayableSubmergedFraction > 0.15f
                    && (editor.PlayableEnteredWater || editor.PlayableMotorState == Genesis.Shared.ECS.Components.CharacterMotorState.Swimming),
                    $"Pond physics never held the playable (state={editor.PlayableMotorState}, "
                    + $"submerged={editor.PlayableSubmergedFraction:0.00}).");

                editor.ResetPlayablePreview();
                editor.SelectTerrainComponent(TerrainComponentsPanel.ComponentKind.Foliage, "foliage");
                editor.Viewport.Camera.Target = new Vector3(
                    pondX, pond.SurfaceHeight - pond.PhysicsDepth * 0.15f, pondZ);
                editor.Viewport.Camera.Distance = 18f;
                editor.Viewport.Camera.Pitch = -0.72f;
                editor.Viewport.Camera.Yaw = 0.95f;
                GateSuite.Pump(5, 25);
                Bitmap pondFrame = RequireCapture(editor, "Filled pond");
                int waterPixels = CountTealPixels(pondFrame, step: 2);
                SaveCapture(ctx, pondFrame, "34-walkthrough-pond.png", "Filled pond", 24);
                HeadlessHarness.Assert(
                    waterPixels >= 40,
                    $"Pond fill did not draw teal water (teal pixels={waterPixels}).");
            });

            editor.Save();
            HeadlessHarness.Assert(File.Exists(terrainPath + ".gterrain"), "The walkthrough meadow was not saved.");
            string natureJson = File.ReadAllText(TerrainNatureSerializer.SidecarPath(terrainPath));
            HeadlessHarness.Assert(
                natureJson.Contains("\"kind\": \"Water\"", StringComparison.Ordinal)
                && !natureJson.Contains("\"kind\": \"Pond\"", StringComparison.Ordinal),
                "Standing water was not saved as canonical kind Water.");
            HeadlessHarness.Assert(
                TerrainNatureSerializer.LoadComponentShaders(terrainPath).TryGetValue("water:" + pondId, out string? storedShader)
                && storedShader == pondShaderRelative,
                "The pond shader was not persisted on the terrain asset.");

            RoomAsset room = RoomAsset.Create("Walkthrough Play", RoomDimension.ThreeD);
            room.Nodes.Add(new RoomNode
            {
                Kind = RoomNodeKind.Terrain,
                Name = "Meadow",
                Enabled = true,
                Terrain = new RoomTerrainData
                {
                    Asset = Path.GetRelativePath(project.RootPath, terrainPath).Replace('\\', '/'),
                },
            });
            room.Normalize();
            using RuntimeScene scene = new("Walkthrough F5") { Input = new InputState() };
            ProjectGameContext game = new(project.RootPath, scene, editor.Viewport.Host.Renderer, null, room, null);
            using RoomTerrainSubsystem runtimeTerrain = new(project.RootPath, room, game);
            HeadlessHarness.Assert(
                runtimeTerrain.GetComponentShader("water:" + pondId) == pondShaderRelative,
                "F5 did not load the pond's assigned shader from the terrain asset.");
            MeshDrawCall[] buffer = new MeshDrawCall[256];
            int submitted = 0;
            runtimeTerrain.SubmitMeshes(scene, buffer, ref submitted, editor.Viewport.Host.Renderer);
            HeadlessHarness.Assert(
                submitted > 0
                && buffer.Take(submitted).Any(call =>
                    call.Shader.IsValid && (call.Flags & MeshDrawFlags.Water) == 0),
                "F5 still drew the pond through the engine water pass instead of the assigned shader.");
        });

        // The Decorate step dresses a terrain like the Nature Walk in a few clicks.
        HeadlessHarness.RunCase(ctx.Report, "Editor.Terrain.Decorate.ButtonsOpenTheirTools", () => DecorateButtons(project, resources));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Terrain.Decorate.NewPineOakAndBoulderMakeModelsAndSolidLayersThatReopen",
            () => DecorateNewModels(project, resources));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Terrain.Decorate.MeadowSetsThePresetAndGrassAroundTheCamera", () => DecorateMeadow(project, resources));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Terrain.Decorate.ModelEditorGenerateStillAddsTreesAndRocks", () => DecorateModelEditorGenerate(project, resources));
    }

    private static void DecorateButtons(ProjectSession project, ResourceService resources)
    {
        string terrainPath = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Terrain"), ResourceKind.Terrain, "Decorate Buttons");
        using Form host = GateSuite.NewHost(1360, 860);
        TerrainEditorControl editor = new(terrainPath, project.RootPath) { Dock = DockStyle.Fill };
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(8, 25);

        Genesis.Application.Core.UI.WorkflowBar workflow = HeadlessHarness.Require(editor.TerrainWorkflow, "Terrain workflow bar");
        HeadlessHarness.Assert(workflow.Steps.Single(step => step.Id == "Decorate").Instruction.Contains("meadow flowers", StringComparison.Ordinal),
            "The Decorate step does not say where forests, flowers, water and paths are.");
        HeadlessHarness.Assert(workflow.Activate("Decorate") && editor.ActiveMode == TerrainEditorControl.TerrainEditorMode.Foliage,
            "The Decorate step did not open the Decorate page.");
        GateSuite.Pump(3, 20);
        Button Decorate(string name) => editor.Controls.Find(name, true).OfType<Button>().SingleOrDefault()
            ?? throw new InvalidOperationException($"The Decorate page has no {name} button.");
        Button forests = Decorate("TerrainDecorateForests"), meadow = Decorate("TerrainDecorateMeadow");
        Button water = Decorate("TerrainDecorateWater"), paths = Decorate("TerrainDecoratePaths");
        HeadlessHarness.Assert(forests.Text == "Forests and scatter…" && meadow.Text == "Meadow flowers and grass"
            && water.Text == "Water: fill a basin" && paths.Text == "Paths",
            $"The Decorate buttons read '{forests.Text}', '{meadow.Text}', '{water.Text}', '{paths.Text}'.");
        HeadlessHarness.Assert(new[] { forests, meadow, water, paths }.All(button => button.Visible && button.Enabled),
            "The Decorate buttons are not shown on the Decorate page.");

        meadow.PerformClick();
        HeadlessHarness.Assert(editor.Nature.FoliageSettings.Preset == FoliagePreset.Meadow && editor.GrassRule.Enabled,
            "Meadow flowers and grass did not set the Meadow scatter and switch grass around the camera on.");

        water.PerformClick();
        HeadlessHarness.Assert(editor.ActiveMode == TerrainEditorControl.TerrainEditorMode.Water
            && editor.ActiveWaterTool == TerrainEditorControl.WaterAuthoringTool.Basin,
            $"Water did not arm Fill Basin (mode {editor.ActiveMode}, tool {editor.ActiveWaterTool}).");
        HeadlessHarness.Assert(workflow.CurrentStepId == "Decorate", $"Water moved the guide to {workflow.CurrentStepId}.");

        workflow.Activate("Decorate");
        GateSuite.Pump(2, 20);
        paths.PerformClick();
        HeadlessHarness.Assert(editor.ActiveMode == TerrainEditorControl.TerrainEditorMode.Paths
            && editor.ActivePathTool == TerrainEditorControl.PathAuthoringTool.PaintPath,
            $"Paths did not open Paint Path (mode {editor.ActiveMode}).");

        // Forests opens this dialog modally (as Options › Forests and scatter does), with New model.
        using TerrainScatterDialog dialog = editor.CreateScatterDialog();
        HeadlessHarness.Assert(Enum.GetValues<TerrainScatterDialog.Starter>().All(starter =>
                dialog.Controls.Find("TerrainScatterNew" + starter, true).OfType<Button>().Any(button => button.Text == starter.ToString())),
            "Forests and scatter does not offer New model › Pine, Oak and Boulder.");
    }

    private static void DecorateNewModels(ProjectSession project, ResourceService resources)
    {
        string terrainPath = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Terrain"), ResourceKind.Terrain, "Decorate Forest");
        string pinePath, oakPath, boulderPath;
        List<TerrainScatterLayer> made;
        using (TerrainEditorControl editor = new(terrainPath, project.RootPath))
        {
            editor.ApplyGeneration(new TerrainGenParams
            {
                Preset = TerrainPreset.Flatlands, ResolutionX = 129, ResolutionZ = 129, CellSize = 1f,
                MinHeight = -8f, MaxHeight = 16f, Seed = 2404,
            });
            editor.SelectPaintLayer(0);
            editor.FillPaintSelection();

            using TerrainScatterDialog dialog = editor.CreateScatterDialog();
            GateSuite.ShowHost(dialog);
            GateSuite.Pump(2, 20);
            HeadlessHarness.Assert(dialog.Controls.Find("TerrainScatterNewModels", true).SingleOrDefault() is { Visible: true },
                "New model is hidden in a terrain that belongs to a project.");
            pinePath = dialog.AddStarterLayer(TerrainScatterDialog.Starter.Pine);
            oakPath = dialog.AddStarterLayer(TerrainScatterDialog.Starter.Oak);
            boulderPath = dialog.AddStarterLayer(TerrainScatterDialog.Starter.Boulder);
            made = dialog.Layers.ToList();

            foreach (string path in new[] { pinePath, oakPath, boulderPath })
            {
                GModelAsset model = StudioModelResourceLoader.LoadReadOnly(path);
                // A rounded boulder is a low-poly shape (85 vertices); a tree has thousands.
                HeadlessHarness.Assert(File.Exists(StudioModelResourceLoader.CanonicalPath(path)) && model.HasRenderableMeshes
                    && model.Meshes.Sum(mesh => mesh.Vertices?.Length ?? 0) > (path == boulderPath ? 40 : 1000),
                    $"New model did not make a Model with geometry at {path}.");
            }

            HeadlessHarness.Assert(made.Count == 3 && made.Select(layer => layer.Name).SequenceEqual(["Pines", "Oaks", "Boulders"]),
                $"New model made the layers {string.Join(", ", made.Select(layer => layer.Name))}.");
            TerrainScatterLayer pines = made[0], oaks = made[1], boulders = made[2];
            HeadlessHarness.Assert(string.Equals(StudioModelResourceLoader.Resolve(project.RootPath, pines.Model), Path.GetFullPath(pinePath), StringComparison.OrdinalIgnoreCase),
                $"The pine layer's model '{pines.Model}' is not the new Pine Model.");
            HeadlessHarness.Assert(pines.CollisionRadius > 0.2f && pines.CollisionHeight > 1f && oaks.CollisionRadius > 0.2f && boulders.CollisionRadius > 0.2f,
                "A new tree or boulder layer can be walked through (no solid width).");
            HeadlessHarness.Assert(pines.PaintLayerMask == 1 && oaks.PaintLayerMask == 1 && boulders.PaintLayerMask == 15
                && pines.DensityPerHectare > 0f && pines.MinimumSpacing >= 2f && pines.MaximumSlopeDegrees is > 0f and < 90f,
                $"The new layers' rules are not the Nature Walk's (paint {pines.PaintLayerMask}/{oaks.PaintLayerMask}/{boulders.PaintLayerMask}).");
            (int cellsX, int cellsZ) = TerrainScatterPlacement.CellCount(editor.Terrain);
            int placed = 0;
            for (int z = 0; z < cellsZ; z++)
                for (int x = 0; x < cellsX; x++)
                    placed += TerrainScatterPlacement.Generate(editor.Terrain, pines, x, z).Length;
            HeadlessHarness.Assert(placed > 5, $"The pine layer placed {placed} trees on a grassy field.");

            editor.SetScatterLayers(made);
            editor.Save();
        }

        TerrainScatterLayer savedPines = TerrainNatureSerializer.LoadOrDefault(terrainPath).ScatterLayers.First(layer => layer.Name == "Pines");
        HeadlessHarness.Assert(savedPines.CollisionRadius == made[0].CollisionRadius && savedPines.Model == made[0].Model,
            "Saving the terrain lost the pine layer's model or solid width.");
        using (TerrainEditorControl reopened = new(terrainPath, project.RootPath))
        {
            HeadlessHarness.Assert(reopened.ScatterLayers.Count == 3 && reopened.ScatterLayers[0].CollisionRadius == made[0].CollisionRadius,
                "Reopening the terrain lost the new forest layers.");
        }

        // The Model editor opens the generated pine as an ordinary Model.
        using ModelEditorControl pineEditor = new(pinePath, project.RootPath);
        HeadlessHarness.Assert(pineEditor.PreviewAsset.HasRenderableMeshes, "The Model editor opened the new Pine with no geometry.");
    }

    private static void DecorateMeadow(ProjectSession project, ResourceService resources)
    {
        string terrainPath = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Terrain"), ResourceKind.Terrain, "Decorate Meadow");
        using (TerrainEditorControl editor = new(terrainPath, project.RootPath))
        {
            editor.ApplyGeneration(new TerrainGenParams
            {
                Preset = TerrainPreset.Flatlands, ResolutionX = 97, ResolutionZ = 97, CellSize = 1f,
                MinHeight = -8f, MaxHeight = 16f, Seed = 2405,
            });
            editor.SelectPaintLayer(0);
            editor.FillPaintSelection();
            HeadlessHarness.Assert(!editor.GrassRule.Enabled, "A new terrain starts with grass around the camera on.");

            // The grass rule's densities are named after the painted layers.
            using (Form scatter = editor.CreateScatterSettingsDialog())
            {
                Control density = scatter.Controls.Find("InspectorDrawer_Layer1Density", true).Single();
                Control.ControlCollection rows = density.Parent!.Controls;
                string caption = rows[rows.IndexOf(density) - 1].Text;
                HeadlessHarness.Assert(caption == "Layer 1 (Grass) density", $"The first grass density reads '{caption}'.");
            }

            editor.ApplyMeadowFlowersAndGrass();
            HeadlessHarness.Assert(editor.Nature.FoliageSettings.Preset == FoliagePreset.Meadow, "Meadow did not choose the Meadow scatter.");
            HeadlessHarness.Assert(editor.Foliage.Instances.Count > 200 && editor.Foliage.Instances.Any(instance => instance.Species == FoliageSpecies.Wildflower),
                $"Meadow grew {editor.Foliage.Instances.Count} plants and no wildflowers.");
            HeadlessHarness.Assert(editor.GrassRule is { Enabled: true, Species: FoliageSpecies.MeadowGrass } && editor.GrassRule.Density(0) > 0f,
                "Meadow did not switch grass around the camera on over the grass layer.");
            using (Form scatter = editor.CreateScatterSettingsDialog())
            {
                HeadlessHarness.Assert(scatter.Controls.Find("InspectorDrawer_Enabled", true).OfType<CheckBox>().FirstOrDefault() is { Checked: true },
                    "The grass fields still show grass around the camera off after Meadow.");
            }

            editor.Undo();
            HeadlessHarness.Assert(!editor.GrassRule.Enabled && editor.Nature.FoliageSettings.Preset == FoliagePreset.Meadow,
                "The first undo after Meadow should switch the grass off and keep the flowers.");
            editor.Undo();
            HeadlessHarness.Assert(editor.Nature.FoliageSettings.Preset != FoliagePreset.Meadow, "The second undo did not take the Meadow scatter back.");
            editor.Redo();
            editor.Redo();
            editor.Save();
        }

        using TerrainEditorControl reopened = new(terrainPath, project.RootPath);
        HeadlessHarness.Assert(reopened.GrassRule.Enabled && reopened.Nature.FoliageSettings.Preset == FoliagePreset.Meadow,
            "Reopening the terrain lost the meadow or its grass around the camera.");
    }

    private static void DecorateModelEditorGenerate(ProjectSession project, ResourceService resources)
    {
        string modelPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Model), ResourceKind.Model, "Decorate Generated");
        var tree = new ProceduralTreeOptions { Preset = ProceduralTreePreset.StylisedConifer, Quality = ProceduralModelQuality.Draft, Seed = 8103 };
        int treeVertices = GeneratedModels.Tree(tree).Meshes.Single().Vertices.Length;
        using (ModelEditorControl model = new(modelPath, project.RootPath))
        {
            int before = model.PreviewAsset.Meshes.Count;
            model.AddGeneratedTree(tree);
            model.AddGeneratedRock(new ProceduralRockOptions { Quality = ProceduralModelQuality.Draft });
            HeadlessHarness.Assert(model.PreviewAsset.Meshes.Count == before + 2 && model.PreviewAsset.Meshes[before].Vertices.Length == treeVertices,
                $"Generate › Tree and Rock added {model.PreviewAsset.Meshes.Count - before} part(s).");
            model.Undo();
            HeadlessHarness.Assert(model.PreviewAsset.Meshes.Count == before + 1, "Undo did not take the generated rock back.");
            model.Redo();
            model.Save();
            HeadlessHarness.Assert(File.Exists(model.CanonicalModelPath) && !model.IsDirty, "The generated model was not saved.");
        }

        GModelAsset saved = StudioModelResourceLoader.LoadReadOnly(modelPath);
        HeadlessHarness.Assert(saved.Meshes.Sum(mesh => mesh.Vertices?.Length ?? 0) >= treeVertices, "The saved model lost the generated tree.");
    }

    private static string CreateTreeEntity(
        ResourceService resources,
        ProjectSession project,
        string name,
        string modelPath)
    {
        string folder = Path.Combine(resources.AssetsRoot, "TerrainEntities");
        Directory.CreateDirectory(folder);
        // Exercise loading an existing legacy part, not new standalone creation.
        string entityPath = Path.Combine(folder, name + ".terrainentity.json");
        File.WriteAllText(entityPath, ResourceDefinitions.Get(ResourceKind.TerrainEntity).DefaultContent);
        using TerrainEntityWizardDialog wizard = new(entityPath, project.RootPath, TerrainEntityType.Object);
        GateSuite.ShowHost(wizard);
        GateSuite.Pump(2, 20);
        wizard.SetName(name);
        wizard.GoToPage(1);
        GateSuite.Pump(2, 20);
        wizard.AddComponent(TerrainEntityComponentKinds.Model);
        wizard.SetComponentProperty(
            wizard.Components.Count - 1,
            "Model",
            Path.GetRelativePath(project.RootPath, modelPath).Replace('\\', '/'));
        HeadlessHarness.Assert(
            wizard.Components.Any(component =>
                component.Type == TerrainEntityComponentKinds.Model
                && component.Get("Model").Contains(Path.GetFileNameWithoutExtension(modelPath), StringComparison.OrdinalIgnoreCase)),
            $"Terrain Entity '{name}' did not keep its Model Editor tree.");
        wizard.SaveAndCloseForTest();
        return entityPath;
    }

    private static Bitmap RequireCapture(TerrainEditorControl editor, string label)
    {
        Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 5);
        HeadlessHarness.Assert(frame is not null, $"{label} viewport readback failed.");
        return frame!;
    }

    private static RuntimeImageMetrics SaveCapture(
        HeadlessContext ctx,
        Bitmap bitmap,
        string fileName,
        string label,
        int minColors)
    {
        string path = Path.Combine(ctx.Captures, fileName);
        bitmap.Save(path, ImageFormat.Png);
        RuntimeImageMetrics metrics = RuntimeImageMetrics.Measure(bitmap);
        bitmap.Dispose();
        ctx.Report.Images.Add(new ImageResult(
            label, fileName, metrics.Width, metrics.Height, metrics.UniqueSampledColors, metrics.AverageLuminance));
        HeadlessHarness.Assert(
            metrics.UniqueSampledColors >= minColors,
            $"'{fileName}' looks blank/flat ({metrics.UniqueSampledColors} colours < {minColors}).");
        return metrics;
    }

    private static int CountDifferingPixels(Bitmap a, Bitmap b, int step = 3, int threshold = 12)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return int.MaxValue;
        }

        int differing = 0;
        for (int y = 0; y < a.Height; y += step)
        {
            for (int x = 0; x < a.Width; x += step)
            {
                Color pa = a.GetPixel(x, y);
                Color pb = b.GetPixel(x, y);
                int delta = Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B);
                if (delta > threshold)
                {
                    differing++;
                }
            }
        }

        return differing;
    }

    private static int CountTealPixels(Bitmap bitmap, int step = 2)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y += step)
        {
            for (int x = 0; x < bitmap.Width; x += step)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.B >= 70 && pixel.B > pixel.R + 18 && pixel.B >= pixel.G - 8)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
