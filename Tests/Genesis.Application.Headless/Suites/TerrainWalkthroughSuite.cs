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

                WaterBody visual = pond.ToWaterBody();
                float span = WaterSurfaceMesh.VerticalSpan(WaterSurfaceMesh.BuildVisual(visual, Vector3.Zero));
                HeadlessHarness.Assert(
                    span >= pond.PhysicsDepth * 0.85f,
                    $"Pond visual is a flat sheet (vertical span {span:0.00} vs physics depth {pond.PhysicsDepth:0.00}).");

                string shaderRelative = Path.GetRelativePath(project.RootPath, shaderPath).Replace('\\', '/');
                editor.SelectTerrainComponent(TerrainComponentsPanel.ComponentKind.Water, pond.Id);
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
