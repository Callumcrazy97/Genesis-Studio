using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Core.Projects.Templates;

/// <summary>
/// Builds "Verdant Hollow", the production 3D Nature Walk project template. The composition ports
/// the Nature1 reference into ordinary authored Genesis resources: a painted/collidable kilometre
/// terrain, four lakes and four trails, dense streamed undergrowth, editable procedural trees and
/// landmarks, atmospheric weather, water, particles, spatial audio, lights, physics and PGSL play.
/// </summary>
public static class NatureWalkTemplate
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public const string RoomName = "VerdantHollow";
    public const string PlayerName = "NatureExplorer";
    public const string TerrainName = "VerdantHollowTerrain";
    public const string PineTreeName = "VerdantPine";
    public const string OakTreeName = "AncientOak";
    public const string BirchTreeName = "SilverBirch";
    public const string WindsweptTreeName = "GroveWillow";
    public const string BoulderName = "MossyBoulder";
    public const string CliffShardName = "CliffRock";
    public const string WatchtowerName = "BrokenWatchtower";
    public const string StoneArchName = "WeatheredStoneArch";
    public const string FallenLogName = "FallenLog";
    public const string LanternName = "TrailLantern";
    public const string CampfireName = "Campfire";
    public const string WindManagerName = "WindManager";
    public const string InteractiveGrassName = "InteractiveGrass";
    public const string NatureAudioName = "NatureAmbience";
    public const string CampfireAudioName = "CampfireLoop";

    public const int AuthoredFoliageSeed = 0x7E3E_2026;
    public const int AuthoredFoliageMaximum = 72000;
    public const string AuthoredFoliageCacheName = "VerdantHollowTerrain.terrain.gfoliage";
    public const string WaterName = "HollowLake";

    private const int TerrainResolution = 513;
    private const float TerrainCellSize = 2.0f;
    private const float TerrainMinHeight = -36.0f;
    private const float TerrainMaxHeight = 96.0f;

    private readonly record struct LakeSpec(string Name, float X, float Z, float Radius, float Surface, float Depth);
    private readonly record struct TrailSpec(string Name, float Width, (float X, float Z)[] Points);

    private static readonly LakeSpec[] Lakes =
    [
        new("Willowmere", -92f, 82f, 66f, -4.5f, 8.5f),
        new("Fern Tarn", 146f, -156f, 38f, 0.5f, 5.8f),
        new("Mirror Lake", 282f, -98f, 48f, 4.0f, 9.5f),
        new("Grove Pool", 322f, -18f, 54f, 3.0f, 8.2f),
    ];

    private static readonly TrailSpec[] Trails =
    [
        new("Verdant Loop", 5.2f,
        [(-18f, -245f), (-10f, -190f), (22f, -132f), (8f, -70f), (-42f, -5f), (-72f, 54f), (-52f, 128f), (8f, 186f), (70f, 150f), (94f, 78f), (58f, 12f), (25f, -58f), (-18f, -128f), (-18f, -245f)]),
        new("Willowmere Shore", 3.8f,
        [(-72f, 40f), (-36f, 68f), (-46f, 112f), (-94f, 146f), (-142f, 122f), (-154f, 74f), (-126f, 35f), (-72f, 40f)]),
        new("Watchtower Spur", 3.6f,
        [(18f, -126f), (62f, -160f), (114f, -194f), (168f, -224f), (214f, -260f)]),
        new("The Grove Route", 4.2f,
        [(72f, 145f), (126f, 118f), (180f, 75f), (232f, 28f), (278f, 12f), (318f, 34f), (354f, 82f)]),
    ];

    public static void Apply(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        ResourceService resources = new(session);
        string images = Path.Combine(session.AssetsPath, "Sprites");
        string objects = Path.Combine(session.AssetsPath, "Objects");
        string rooms = Path.Combine(session.AssetsPath, "Rooms");
        string terrains = Path.Combine(session.AssetsPath, "Terrain");
        string models = Path.Combine(session.AssetsPath, "Models");
        string particles = Path.Combine(session.AssetsPath, "Particles");
        string audio = Path.Combine(session.AssetsPath, "Audio");
        string shaders = Path.Combine(session.AssetsPath, "Shaders");
        string physics = Path.Combine(session.AssetsPath, "Physics");


        Directory.CreateDirectory(images);
        Directory.CreateDirectory(objects);
        Directory.CreateDirectory(rooms);
        Directory.CreateDirectory(terrains);
        Directory.CreateDirectory(models);

        Directory.CreateDirectory(particles);
        Directory.CreateDirectory(audio);
        Directory.CreateDirectory(shaders);
        Directory.CreateDirectory(physics);

        // 1. Textures & Materials
        string grassTex = ImportImageResource(resources, images, "GrassDiffuse", "lush_alpine_grass_1787671629947.jpg");
        string soilTex = ImportImageResource(resources, images, "SoilDiffuse", "forest_dirt_pebbles_1787671723859.jpg");
        string rockTex = ImportImageResource(resources, images, "RockDiffuse", "mountain_granite_rock_1787671740897.jpg");
        string barkTex = ImportImageResource(resources, images, "BarkDiffuse", "rugged_tree_bark_1787671664050.jpg");
        string foliageTex = ImportImageResource(resources, images, "FoliageDiffuse", "tree_canopy_leaves_1787671689525.jpg");
        string pineFoliageTex = ImportImageResource(resources, images, "PineDiffuse", "pine_needle_canopy_1787671707943.jpg");

        // 2. Procedural Nature 3D Models
        string pineModel = CreateProceduralTreeModel(
            resources, models, session.RootPath, PineTreeName,
            new ProceduralTreeOptions
            {
                Preset = ProceduralTreePreset.StylisedConifer,
                Quality = ProceduralModelQuality.High,
                Seed = 0x5A172026UL,
                HeightScale = 1.35f,
                CanopyScale = 1.1f,
                BranchDensity = 1.2f,
                LeafDensity = 1.25f,
            }, pineFoliageTex);

        string oakModel = CreateProceduralTreeModel(
            resources, models, session.RootPath, OakTreeName,
            new ProceduralTreeOptions
            {
                Preset = ProceduralTreePreset.WideOldOak,
                Quality = ProceduralModelQuality.High,
                Seed = 0x00A13031UL,
                HeightScale = 1.2f,
                CanopyScale = 1.4f,
                BranchDensity = 1.3f,
                LeafDensity = 1.3f,
            }, foliageTex);

        string birchModel = CreateProceduralTreeModel(
            resources, models, session.RootPath, BirchTreeName,
            new ProceduralTreeOptions
            {
                Preset = ProceduralTreePreset.TallWoodland,
                Quality = ProceduralModelQuality.High,
                Seed = 0x00B12021UL,
                HeightScale = 1.5f,
                CanopyScale = 0.95f,
                BranchDensity = 1.1f,
                LeafDensity = 1.15f,
            }, foliageTex);

        string windsweptModel = CreateProceduralTreeModel(
            resources, models, session.RootPath, WindsweptTreeName,
            new ProceduralTreeOptions
            {
                Preset = ProceduralTreePreset.WindsweptHillside,
                Quality = ProceduralModelQuality.High,
                Seed = 0x00D04041UL,
                HeightScale = 1.1f,
                CanopyScale = 1.2f,
                BranchDensity = 1.2f,
                LeafDensity = 1.1f,
                WindBias = 0.65f,
            }, foliageTex);

        string boulderModel = CreateProceduralRockModel(
            resources, models, session.RootPath, BoulderName,
            new ProceduralRockOptions
            {
                Preset = ProceduralRockPreset.MossyStone,
                Quality = ProceduralModelQuality.High,
                Seed = 0xB01DE22026UL,
                Width = 3.2f,
                Height = 2.2f,
                Depth = 2.8f,
                Roughness = 0.42f,
                Asymmetry = 0.35f,
            }, rockTex);

        string cliffModel = CreateProceduralRockModel(
            resources, models, session.RootPath, CliffShardName,
            new ProceduralRockOptions
            {
                Preset = ProceduralRockPreset.JaggedBoulder,
                Quality = ProceduralModelQuality.High,
                Seed = 0xC11FE33036UL,
                Width = 4.5f,
                Height = 6.2f,
                Depth = 3.8f,
                Roughness = 0.65f,
                Asymmetry = 0.48f,
            }, rockTex);

        string watchtowerModel = CreateWatchtowerModel(resources, models, session.RootPath, rockTex);
        string stoneArchModel = CreateStoneArchModel(resources, models, session.RootPath, rockTex);
        string fallenLogModel = CreateFallenLogModel(resources, models, session.RootPath, barkTex);
        string lanternModel = CreateLanternModel(resources, models, session.RootPath, barkTex);
        string campfireModel = CreateCampfireModel(resources, models, session.RootPath, barkTex);

        // 3. Shaders
        string foliageShader = CreateFoliageWindShader(resources, shaders, session.RootPath, pineModel);
        string campfireShader = CreateCampfireShader(resources, shaders, session.RootPath, campfireModel);

        // 4. Particles
        string pollenParticle = CreatePollenParticles(resources, particles);
        string campfireParticle = CreateCampfireParticles(resources, particles);
        string fireflyParticle = CreateFireflyParticles(resources, particles);
        string fallingLeavesParticle = CreateFallingLeavesParticles(resources, particles);

        // 5. Audio
        string natureAudio = CreateNatureAmbienceAudio(resources, audio, session.RootPath);
        string campfireAudio = CreateCampfireAudio(resources, audio, session.RootPath);

        // Authorable Physics Editor resources accompany the consumed terrain/water/object settings.
        CreatePhysicsMaterial(resources, physics, "ForestGround", 0.88f, 0.02f, 1.35f);
        CreatePhysicsMaterial(resources, physics, "WeatheredStone", 0.74f, 0.08f, 2.4f);
        CreatePhysicsMaterial(resources, physics, "ForestWood", 0.62f, 0.12f, 0.82f);

        // 6. Terrain
        string terrain = CreateTerrain(resources, terrains, grassTex);

        // 7. Objects (Prefabs)
        string player = CreatePlayer(resources, objects);
        string pineObject = CreateModelObject(resources, objects, session.RootPath, PineTreeName, pineModel, foliageTex, foliageShader);
        string oakObject = CreateModelObject(resources, objects, session.RootPath, OakTreeName, oakModel, foliageTex, foliageShader);
        string birchObject = CreateModelObject(resources, objects, session.RootPath, BirchTreeName, birchModel, foliageTex, foliageShader);
        string windsweptObject = CreateModelObject(resources, objects, session.RootPath, WindsweptTreeName, windsweptModel, foliageTex, foliageShader);
        string boulderObject = CreateModelObject(resources, objects, session.RootPath, BoulderName, boulderModel, rockTex, null);
        string cliffObject = CreateModelObject(resources, objects, session.RootPath, CliffShardName, cliffModel, rockTex, null);
        string watchtowerObject = CreateModelObject(resources, objects, session.RootPath, WatchtowerName, watchtowerModel, rockTex, null);
        string stoneArchObject = CreateModelObject(resources, objects, session.RootPath, StoneArchName, stoneArchModel, rockTex, null);
        string fallenLogObject = CreateModelObject(resources, objects, session.RootPath, FallenLogName, fallenLogModel, barkTex, null);
        string lanternObject = CreateLanternObject(resources, objects, session.RootPath, lanternModel, barkTex);
        string campfireObject = CreateCampfireObject(
            resources, objects, session.RootPath, campfireModel, barkTex,
            campfireShader, campfireParticle, campfireAudio);
        string pollenObject = CreateParticleFieldObject(resources, objects, session.RootPath,
            "SunlitPollenField", pollenParticle, 1.0f);
        string fireflyObject = CreateParticleFieldObject(resources, objects, session.RootPath,
            "LakeFireflies", fireflyParticle, 1.0f);
        string fallingLeavesObject = CreateParticleFieldObject(resources, objects, session.RootPath,
            "FallingLeaves", fallingLeavesParticle, 0.8f);
        string natureAudioObject = CreateAmbientAudioObject(
            resources, objects, session.RootPath, natureAudio);
        string windManager = CreateWindManager(resources, objects);
        string interactiveGrass = CreateInteractiveGrass(resources, objects);

        // 8. Room
        CreateRoom(
            resources,
            rooms,
            session.RootPath,
            player,
            terrain,
            grassTex,
            pineObject,
            oakObject,
            birchObject,
            windsweptObject,
            boulderObject,
            cliffObject,
            watchtowerObject,
            stoneArchObject,
            fallenLogObject,
            lanternObject,
            campfireObject,
            pollenObject,
            fireflyObject,
            fallingLeavesObject,
            windManager,
            interactiveGrass,
            natureAudioObject);

        session.Manifest.StartRoom = $"Assets/Rooms/{RoomName}.room.json";
    }

    // ── Textures ────────────────────────────────────────────────────────────────

    private static string CreateGrassTexture(ResourceService resources, string folder)
    {
        const int size = 256;
        string path = resources.CreateResource(folder, ResourceKind.Image, "ForestGrass");
        ImageDocument document = new();
        document.Canvas.Width = size;
        document.Canvas.Height = size;
        document.Usage.Allowed = ImageUsage.Texture;

        WritePixels(path, document, size, size, rgba =>
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float noise1 = MathF.Sin(x * 0.12f) * MathF.Cos(y * 0.14f);
                    float noise2 = MathF.Sin((x + y) * 0.06f) * 0.5f;
                    int blade = ((x * 17) + (y * 23)) % 19 - 9;
                    int r = (int)(42 + (noise1 + noise2) * 16f) + blade;
                    int g = (int)(118 + (noise1 + noise2) * 28f) + (blade * 2);
                    int b = (int)(46 + (noise1 + noise2) * 14f) + blade;
                    int offset = ((y * size) + x) * 4;
                    rgba[offset] = Clamp255(r);
                    rgba[offset + 1] = Clamp255(g);
                    rgba[offset + 2] = Clamp255(b);
                    rgba[offset + 3] = 255;
                }
            }
        });
        return path;
    }

    private static string CreateSoilTexture(ResourceService resources, string folder)
    {
        const int size = 256;
        string path = resources.CreateResource(folder, ResourceKind.Image, "ForestSoil");
        ImageDocument document = new();
        document.Canvas.Width = size;
        document.Canvas.Height = size;
        document.Usage.Allowed = ImageUsage.Texture;

        WritePixels(path, document, size, size, rgba =>
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int grain = ((x * 19) + (y * 31)) % 23 - 11;
                    int r = 84 + grain;
                    int g = 56 + (grain / 2);
                    int b = 38 + (grain / 3);
                    int offset = ((y * size) + x) * 4;
                    rgba[offset] = Clamp255(r);
                    rgba[offset + 1] = Clamp255(g);
                    rgba[offset + 2] = Clamp255(b);
                    rgba[offset + 3] = 255;
                }
            }
        });
        return path;
    }

    private static string CreateRockTexture(ResourceService resources, string folder)
    {
        const int size = 256;
        string path = resources.CreateResource(folder, ResourceKind.Image, "MountainRock");
        ImageDocument document = new();
        document.Canvas.Width = size;
        document.Canvas.Height = size;
        document.Usage.Allowed = ImageUsage.Texture;

        WritePixels(path, document, size, size, rgba =>
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int grain = ((x * 29) + (y * 37)) % 31 - 15;
                    float strata = MathF.Sin(y * 0.18f + x * 0.04f) * 12f;
                    int r = (int)(95 + strata) + grain;
                    int g = (int)(102 + strata) + grain;
                    int b = (int)(112 + strata) + grain;
                    int offset = ((y * size) + x) * 4;
                    rgba[offset] = Clamp255(r);
                    rgba[offset + 1] = Clamp255(g);
                    rgba[offset + 2] = Clamp255(b);
                    rgba[offset + 3] = 255;
                }
            }
        });
        return path;
    }

    private static string CreateBarkTexture(ResourceService resources, string folder)
    {
        const int size = 128;
        string path = resources.CreateResource(folder, ResourceKind.Image, "BarkTexture");
        ImageDocument document = new();
        document.Canvas.Width = size;
        document.Canvas.Height = size;
        document.Usage.Allowed = ImageUsage.Texture;

        WritePixels(path, document, size, size, rgba =>
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int furrow = (x % 16 < 3) ? -28 : (x % 8 == 0) ? 18 : 0;
                    int r = 94 + furrow;
                    int g = 62 + (furrow / 2);
                    int b = 38 + (furrow / 3);
                    int offset = ((y * size) + x) * 4;
                    rgba[offset] = Clamp255(r);
                    rgba[offset + 1] = Clamp255(g);
                    rgba[offset + 2] = Clamp255(b);
                    rgba[offset + 3] = 255;
                }
            }
        });
        return path;
    }

    private static string ImportImageResource(ResourceService resources, string folder, string resourceName, string sourceFileName)
    {
        string path = resources.CreateResource(folder, ResourceKind.Image, resourceName);
        ImageDocument document = new();
        
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "Projects", "Templates", "Assets", "NatureWalk", sourceFileName);
        
        if (!File.Exists(sourcePath))
        {
            // Fallback to a procedural colored square if the asset is missing
            document.Canvas.Width = 128;
            document.Canvas.Height = 128;
            document.Usage.Allowed = ImageUsage.Texture;
            WritePixels(path, document, 128, 128, rgba =>
            {
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    rgba[i] = 100;
                    rgba[i+1] = 150;
                    rgba[i+2] = 100;
                    rgba[i+3] = 255;
                }
            });
            return path;
        }

        document.Canvas.Width = 512;
        document.Canvas.Height = 512;
        document.Usage.Allowed = ImageUsage.Texture;

        string dataDirectory = Path.ChangeExtension(path, null);
        if (dataDirectory.EndsWith(".image", StringComparison.OrdinalIgnoreCase))
        {
            dataDirectory = dataDirectory[..^".image".Length];
        }
        dataDirectory += ".spritedata";
        string frameDirectory = Path.Combine(dataDirectory, "frames");
        Directory.CreateDirectory(frameDirectory);

        string frameId = Guid.NewGuid().ToString("N");
        string frameExt = Path.GetExtension(sourceFileName);
        string frameFileName = frameId + frameExt;
        string framePath = Path.Combine(frameDirectory, frameFileName);
        
        File.Copy(sourcePath, framePath, overwrite: true);

        string relative = Path.Combine(Path.GetFileName(dataDirectory), "frames", frameFileName).Replace('\\', '/');

        document.Frames.Add(new ImageFrame
        {
            Id = frameId,
            Source = relative
        });
        document.Layers.Add(new ImageLayer { Name = "Layer 1" });
        document.Layers[0].Cels.Add(new ImageCel { FrameId = frameId });

        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    // ── Procedural Models ───────────────────────────────────────────────────────

    private static string CreateProceduralTreeModel(
        ResourceService resources,
        string folder,
        string projectRoot,
        string modelName,
        ProceduralTreeOptions options,
        string texturePath)
    {
        string path = resources.CreateResource(folder, ResourceKind.Model, modelName);
        ProceduralMeshResult mesh = ProceduralModelGenerator.GenerateTree(options);

        JsonObject document = new()
        {
            ["schemaVersion"] = 3,
            ["source"] = "Generated",
            ["generatorKind"] = "Tree",
            ["hasBakedEdits"] = true,
            ["treeGenerator"] = new JsonObject
            {
                ["preset"] = options.Preset.ToString(),
                ["quality"] = options.Quality.ToString(),
                ["seed"] = options.Seed,
                ["heightScale"] = options.HeightScale,
                ["canopyScale"] = options.CanopyScale,
                ["branchDensity"] = options.BranchDensity,
                ["leafDensity"] = options.LeafDensity,
                ["windBias"] = options.WindBias,
            },
            ["materials"] = new JsonArray(Relative(projectRoot, texturePath)),
            ["parts"] = new JsonArray(),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        WriteMeshSidecar(path + ".mesh", mesh.Vertices, mesh.Indices);

        GModelAsset gmodel = new()
        {
            Name = modelName,
            SourceFile = path,
        };
        gmodel.Materials.Add(new GModelMaterial { Name = "TreeMaterial", AlbedoTexture = Relative(projectRoot, texturePath) });
        gmodel.Meshes.Add(new GModelMesh
        {
            Name = "TreeMesh",
            MaterialIndex = 0,
            Vertices = mesh.Vertices,
            Indices = mesh.Indices,
            IsSkinned = false,
        });
        gmodel.RecalculateBounds();
        StudioModelResourceLoader.SaveCanonical(path, gmodel);

        return path;
    }

    private static string CreateProceduralRockModel(
        ResourceService resources,
        string folder,
        string projectRoot,
        string modelName,
        ProceduralRockOptions options,
        string texturePath)
    {
        string path = resources.CreateResource(folder, ResourceKind.Model, modelName);
        ProceduralMeshResult mesh = ProceduralModelGenerator.GenerateRock(options);

        JsonObject document = new()
        {
            ["schemaVersion"] = 3,
            ["source"] = "Generated",
            ["generatorKind"] = "Rock",
            ["hasBakedEdits"] = true,
            ["rockGenerator"] = new JsonObject
            {
                ["preset"] = options.Preset.ToString(),
                ["quality"] = options.Quality.ToString(),
                ["seed"] = options.Seed,
                ["width"] = options.Width,
                ["height"] = options.Height,
                ["depth"] = options.Depth,
                ["roughness"] = options.Roughness,
                ["asymmetry"] = options.Asymmetry,
            },
            ["materials"] = new JsonArray(Relative(projectRoot, texturePath)),
            ["parts"] = new JsonArray(),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        WriteMeshSidecar(path + ".mesh", mesh.Vertices, mesh.Indices);

        GModelAsset gmodel = new()
        {
            Name = modelName,
            SourceFile = path,
        };
        gmodel.Materials.Add(new GModelMaterial { Name = "RockMaterial", AlbedoTexture = Relative(projectRoot, texturePath) });
        gmodel.Meshes.Add(new GModelMesh
        {
            Name = "RockMesh",
            MaterialIndex = 0,
            Vertices = mesh.Vertices,
            Indices = mesh.Indices,
            IsSkinned = false,
        });
        gmodel.RecalculateBounds();
        StudioModelResourceLoader.SaveCanonical(path, gmodel);

        return path;
    }

    private static string CreateWatchtowerModel(
        ResourceService resources, string folder, string projectRoot, string texturePath) =>
        CreateComposedModel(resources, folder, projectRoot, WatchtowerName, texturePath,
            ModelPart("Stone Base", "Cylinder", 0, 0.7f, 0, 0, 0, 0, 5.8f, 1.4f, 5.8f, 0.28f, 0.31f, 0.27f),
            ModelPart("Tower", "Cylinder", 0, 5.3f, 0, 0, 0, 0, 4.2f, 8.2f, 4.2f, 0.42f, 0.45f, 0.38f),
            ModelPart("Lookout", "Cube", 0, 9.6f, 0, 0, 0, 0, 6.4f, 1.0f, 6.4f, 0.34f, 0.28f, 0.20f),
            ModelPart("Roof", "Cylinder", 0, 11.1f, 0, 0, 0, 0, 7.5f, 2.2f, 7.5f, 0.23f, 0.17f, 0.12f),
            ModelPart("Broken Beam", "Cylinder", 2.3f, 12.8f, 0.7f, 0, 0, 18, 0.38f, 5.4f, 0.38f, 0.24f, 0.14f, 0.07f));

    private static string CreateStoneArchModel(
        ResourceService resources, string folder, string projectRoot, string texturePath) =>
        CreateComposedModel(resources, folder, projectRoot, StoneArchName, texturePath,
            ModelPart("Left Pier", "Cube", -2.5f, 2.8f, 0, 0, 0, -3, 2.2f, 5.6f, 2.1f, 0.38f, 0.42f, 0.35f),
            ModelPart("Right Pier", "Cube", 2.5f, 2.8f, 0, 0, 0, 4, 2.2f, 5.6f, 2.1f, 0.38f, 0.42f, 0.35f),
            ModelPart("Lintel", "Cube", 0, 6.0f, 0, 0, 0, 2, 7.0f, 1.5f, 2.2f, 0.34f, 0.38f, 0.32f),
            ModelPart("Capstone", "Cube", -1.2f, 7.0f, 0.1f, 0, 8, -5, 3.0f, 0.8f, 2.4f, 0.29f, 0.34f, 0.27f));

    private static string CreateFallenLogModel(
        ResourceService resources, string folder, string projectRoot, string texturePath) =>
        CreateComposedModel(resources, folder, projectRoot, FallenLogName, texturePath,
            ModelPart("Trunk", "Cylinder", 0, 0.72f, 0, 0, 0, 90, 1.35f, 8.5f, 1.35f, 0.26f, 0.12f, 0.045f),
            ModelPart("Moss", "Sphere", -1.4f, 1.26f, 0.25f, 0, 0, 0, 2.6f, 0.32f, 0.9f, 0.20f, 0.42f, 0.12f),
            ModelPart("Branch", "Cylinder", 1.7f, 1.1f, 0, 0, 22, 42, 0.24f, 2.6f, 0.24f, 0.28f, 0.14f, 0.05f));

    private static string CreateLanternModel(
        ResourceService resources, string folder, string projectRoot, string texturePath) =>
        CreateComposedModel(resources, folder, projectRoot, LanternName, texturePath,
            ModelPart("Post", "Cylinder", 0, 1.7f, 0, 0, 0, 0, 0.24f, 3.4f, 0.24f, 0.20f, 0.11f, 0.045f),
            ModelPart("Crossbar", "Cube", 0.48f, 3.2f, 0, 0, 0, 0, 1.2f, 0.18f, 0.18f, 0.23f, 0.13f, 0.055f),
            ModelPart("Lamp", "Cube", 0.82f, 2.75f, 0, 0, 0, 0, 0.55f, 0.8f, 0.55f, 1.0f, 0.62f, 0.13f),
            ModelPart("Cap", "Sphere", 0.82f, 3.2f, 0, 0, 0, 0, 0.72f, 0.25f, 0.72f, 0.18f, 0.12f, 0.06f));

    private static string CreateCampfireModel(
        ResourceService resources, string folder, string projectRoot, string texturePath) =>
        CreateComposedModel(resources, folder, projectRoot, CampfireName, texturePath,
            ModelPart("Log A", "Cylinder", 0, 0.28f, 0, 0, 0, 48, 0.34f, 2.8f, 0.34f, 0.30f, 0.12f, 0.035f),
            ModelPart("Log B", "Cylinder", 0, 0.28f, 0, 0, 0, -48, 0.34f, 2.8f, 0.34f, 0.38f, 0.16f, 0.045f),
            ModelPart("Coal Bed", "Sphere", 0, 0.26f, 0, 0, 0, 0, 1.2f, 0.38f, 1.2f, 0.34f, 0.08f, 0.025f),
            ModelPart("Flame Core", "Sphere", 0, 0.92f, 0, 0, 0, 0, 0.62f, 1.45f, 0.62f, 1f, 0.30f, 0.025f),
            ModelPart("Flame Tip", "Sphere", 0.12f, 1.68f, 0, 0, 0, 0, 0.36f, 0.82f, 0.36f, 1f, 0.72f, 0.08f));

    private static string CreateComposedModel(
        ResourceService resources,
        string folder,
        string projectRoot,
        string name,
        string texturePath,
        params JsonObject[] parts)
    {
        string path = resources.CreateResource(folder, ResourceKind.Model, name);
        JsonObject document = new()
        {
            ["schemaVersion"] = 3,
            ["source"] = "Generated",
            ["generatorKind"] = "Kitbash",
            ["hasBakedEdits"] = true,
            ["materials"] = new JsonArray(Relative(projectRoot, texturePath)),
            ["parts"] = new JsonArray(parts),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static JsonObject ModelPart(
        string name, string primitive,
        float x, float y, float z,
        float rotationX, float rotationY, float rotationZ,
        float scaleX, float scaleY, float scaleZ,
        float r, float g, float b) => new()
    {
        ["name"] = name,
        ["primitive"] = primitive,
        ["position"] = new JsonArray(x, y, z),
        ["rotation"] = new JsonArray(rotationX, rotationY, rotationZ),
        ["scale"] = new JsonArray(scaleX, scaleY, scaleZ),
        ["color"] = new JsonArray(r, g, b),
    };

    private static void WriteMeshSidecar(string path, MeshVertex[] vertices, ushort[] indices)
    {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write(0x4853454D); // 'MESH'
        writer.Write(1);          // Version 1
        writer.Write(vertices.Length);
        writer.Write(indices.Length);
        for (int i = 0; i < vertices.Length; i++)
        {
            writer.Write(vertices[i].Position.X);
            writer.Write(vertices[i].Position.Y);
            writer.Write(vertices[i].Position.Z);
            writer.Write(vertices[i].Normal.X);
            writer.Write(vertices[i].Normal.Y);
            writer.Write(vertices[i].Normal.Z);
            writer.Write(vertices[i].Color.X);
            writer.Write(vertices[i].Color.Y);
            writer.Write(vertices[i].Color.Z);
            writer.Write(vertices[i].Color.W);
            writer.Write(vertices[i].UV.X);
            writer.Write(vertices[i].UV.Y);
        }
        for (int i = 0; i < indices.Length; i++)
        {
            writer.Write(indices[i]);
        }
    }

    // ── Shaders ─────────────────────────────────────────────────────────────────

    private static string CreateFoliageWindShader(
        ResourceService resources,
        string folder,
        string projectRoot,
        string previewModel)
    {
        string path = resources.CreateResource(folder, ResourceKind.Shader, "FoliageWind");
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["pipeline"] = "Mesh",
            ["entry"] = "MainPS",
            ["profile"] = "ps_5_0",
            ["source"] = FoliageShaderSource,
            ["previewAsset"] = Relative(projectRoot, previewModel),
            ["parameters"] = new JsonArray(
                ShaderParameter("WindStrength", 0.65f),
                ShaderParameter("WindSpeed", 1.8f),
                ShaderParameter("SunTransmission", 1.25f)),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static JsonObject ShaderParameter(string name, float value) => new()
    {
        ["name"] = name,
        ["type"] = "float",
        ["value"] = new JsonArray(value),
    };

    private const string FoliageShaderSource = """
        Texture2D AlbedoTex : register(t1);
        SamplerState AlbedoSamp : register(s0);
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        cbuffer GenesisParameters : register(b5) { float WindStrength; float WindSpeed; float SunTransmission; };
        struct VSOut
        {
            float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2;
            float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; float4 ShadowPos : TEXCOORD5;
            float4 ShadowPosNr : TEXCOORD6; float AtlasLayer : TEXCOORD7;
        };
        float4 MainPS(VSOut IN) : SV_Target
        {
            float4 tex = AlbedoTex.Sample(AlbedoSamp, IN.UV) * IN.Color;
            float sway = sin(Time * max(WindSpeed, 0.1) + (IN.WorldPos.x + IN.WorldPos.z) * 0.4) * 0.08 * WindStrength;
            float3 sunGlow = float3(1.05, 1.12, 0.95) * (1.0 + sway) * max(SunTransmission, 0.5);
            return float4(saturate(tex.rgb * sunGlow), tex.a);
        }
        """;

    private static string CreateCampfireShader(
        ResourceService resources, string folder, string projectRoot, string previewModel)
    {
        string path = resources.CreateResource(folder, ResourceKind.Shader, "CampfireGlow");
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["pipeline"] = "Mesh",
            ["entry"] = "MainPS",
            ["profile"] = "ps_5_0",
            ["source"] = CampfireShaderSource,
            ["previewAsset"] = Relative(projectRoot, previewModel),
            ["parameters"] = new JsonArray(
                ShaderParameter("PulseSpeed", 2.4f),
                ShaderParameter("Glow", 1.35f)),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private const string CampfireShaderSource = """
        Texture2D AlbedoTex : register(t1);
        SamplerState AlbedoSamp : register(s0);
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        cbuffer GenesisParameters : register(b5) { float PulseSpeed; float Glow; };
        struct VSOut
        {
            float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2;
            float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; float4 ShadowPos : TEXCOORD5;
            float4 ShadowPosNr : TEXCOORD6; float AtlasLayer : TEXCOORD7;
        };
        float4 MainPS(VSOut IN) : SV_Target
        {
            float4 tex = AlbedoTex.Sample(AlbedoSamp, IN.UV) * IN.Color;
            float pulse = 0.78 + sin(Time * max(PulseSpeed, 0.1) + IN.WorldPos.y * 3.0) * 0.22;
            float heat = saturate(IN.Color.r - max(IN.Color.g, IN.Color.b));
            float3 warm = float3(1.0, 0.27, 0.035) * heat * Glow * pulse;
            return float4(saturate(tex.rgb * (0.86 + pulse * 0.22) + warm), tex.a);
        }
        """;

    // ── Terrain ─────────────────────────────────────────────────────────────────

    public static float SampleAlpineHeight(float wx, float wz)
    {
        const float halfSize = TerrainResolution * TerrainCellSize * 0.5f;

        // Multi-octave rolling hills (broad continental shape)
        float hills = Fbm(wx * 0.0034f, wz * 0.0034f, 5, 2.03f, 0.5f, 42) * 46f;

        // Ridge spines for directional valley walls
        float ridge = (Ridged(wx * 0.0021f, wz * 0.0021f, 4, 2.05f, 0.5f, 83) - 0.5f) * 30f;

        // Medium undulation
        float med = Fbm(wx * 0.011f, wz * 0.011f, 3, 2.01f, 0.5f, 139) * 5.2f;

        // Fine surface bumps
        float fine = Fbm(wx * 0.052f, wz * 0.052f, 3, 2.07f, 0.5f, 275) * 1.1f;

        float h = hills + 0.55f * ridge + med + fine;

        // Broad central hollow and a gentler south-to-north walking valley.
        float valleyAxis = (0.30f * wx + 0.52f * wz) * 0.010f;
        h -= 13f * MathF.Exp(-(valleyAxis * valleyAxis));

        // World rim enclosure lift - mountains at edges
        float edge = MathF.Max(MathF.Abs(wx), MathF.Abs(wz)) / halfSize;
        float rimFactor = MathF.Max(0f, edge - 0.55f) / 0.45f;
        h += 38f * MathF.Pow(rimFactor, 2.1f);

        // The reference scene's lakes are actual sculpted basins, not water planes floating
        // through hills. Blend into a flat shore shelf and deepen smoothly at each centre.
        foreach (LakeSpec lake in Lakes)
        {
            float dx = wx - lake.X;
            float dz = wz - lake.Z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            float basin = 1f - Smoothstep(lake.Radius * 0.72f, lake.Radius, distance);
            if (basin <= 0f) continue;
            float centre = 1f - Smoothstep(0f, lake.Radius * 0.72f, distance);
            float target = lake.Surface - 1.15f - lake.Depth * centre;
            h += (target - h) * basin;
        }

        return Math.Clamp(h, TerrainMinHeight + 1f, TerrainMaxHeight - 2f);
    }

    private static float TrailDistance(float x, float z)
    {
        float best = float.MaxValue;
        foreach (TrailSpec trail in Trails)
        {
            for (int i = 1; i < trail.Points.Length; i++)
            {
                best = MathF.Min(best, DistanceToSegment(x, z, trail.Points[i - 1], trail.Points[i]));
            }
        }
        return best;
    }

    private static float DistanceToSegment(
        float x, float z, (float X, float Z) a, (float X, float Z) b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        float lengthSquared = dx * dx + dz * dz;
        float t = lengthSquared <= 0.0001f ? 0f : Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / lengthSquared, 0f, 1f);
        float px = a.X + dx * t;
        float pz = a.Z + dz * t;
        float rx = x - px;
        float rz = z - pz;
        return MathF.Sqrt(rx * rx + rz * rz);
    }

    private static bool TryLakeDistance(float x, float z, out LakeSpec nearest, out float distance)
    {
        nearest = Lakes[0];
        distance = float.MaxValue;
        foreach (LakeSpec lake in Lakes)
        {
            float dx = x - lake.X;
            float dz = z - lake.Z;
            float candidate = MathF.Sqrt(dx * dx + dz * dz);
            if (candidate >= distance) continue;
            nearest = lake;
            distance = candidate;
        }
        return distance <= nearest.Radius + 12f;
    }

    // Perlin-like noise using deterministic hashing (no external deps)
    private static float Noise2D(float x, float y, int seed)
    {
        int ix = (int)MathF.Floor(x);
        int iy = (int)MathF.Floor(y);
        float fx = x - ix;
        float fy = y - iy;
        float u = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
        float v = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);

        float g(int cx, int cy, float dx, float dy)
        {
            uint h = StableHash(unchecked((uint)(cx * 374761393 + cy * 668265263 + seed * 1013904223)));
            float angle = h * (MathF.Tau / uint.MaxValue);
            return MathF.Cos(angle) * dx + MathF.Sin(angle) * dy;
        }

        float d00 = g(ix, iy, fx, fy);
        float d10 = g(ix + 1, iy, fx - 1f, fy);
        float d01 = g(ix, iy + 1, fx, fy - 1f);
        float d11 = g(ix + 1, iy + 1, fx - 1f, fy - 1f);

        float a = d00 + (d10 - d00) * u;
        float b = d01 + (d11 - d01) * u;
        return 1.414f * (a + (b - a) * v);
    }

    private static float Fbm(float x, float y, int octaves, float lacunarity, float gain, int seed)
    {
        float sum = 0f, amp = 1f, totalAmp = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += amp * Noise2D(x, y, seed + 131 * i);
            totalAmp += amp;
            amp *= gain;
            x *= lacunarity;
            y *= lacunarity;
        }
        return sum / totalAmp;
    }

    private static float Ridged(float x, float y, int octaves, float lacunarity, float gain, int seed)
    {
        float sum = 0f, amp = 1f, totalAmp = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float n = 1f - MathF.Abs(Noise2D(x, y, seed + 917 * i));
            sum += amp * n * n;
            totalAmp += amp;
            amp *= gain;
            x *= lacunarity;
            y *= lacunarity;
        }
        return sum / totalAmp;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(0.0001f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static string CreateTerrain(ResourceService resources, string folder, string grassAlbedo)
    {
        string path = resources.CreateResource(folder, ResourceKind.Terrain, TerrainName);

        JsonObject settings = new()
        {
            ["schemaVersion"] = 1,
            ["resolution"] = new JsonArray(TerrainResolution, TerrainResolution),
            ["cellSize"] = TerrainCellSize,
            ["minHeight"] = TerrainMinHeight,
            ["maxHeight"] = TerrainMaxHeight,
            ["seedProfile"] = "ErodedMountains",
            ["preset"] = "ErodedMountains",
            ["seed"] = 202608,
            ["fogEnabled"] = true,
            ["fogDensity"] = 0.0012,
            ["layers"] = new JsonArray(
                Layer("ForestGrass", 0.18, 0.48, 0.19),
                Layer("ForestSoil", 0.38, 0.26, 0.16),
                Layer("MountainRock", 0.45, 0.48, 0.52),
                Layer("AlpineMoss", 0.28, 0.42, 0.24)),
            ["entities"] = new JsonArray(),
        };
        File.WriteAllText(path, settings.ToJsonString(JsonOptions));

        WriteHeightfield(path + ".gterrain");
        WriteNaturalWorld(path);
        return path;
    }

    private static JsonObject Layer(string name, double r, double g, double b) => new()
    {
        ["name"] = name,
        ["color"] = new JsonArray(r, g, b),
    };

    private static void WriteHeightfield(string path)
    {
        int count = TerrainResolution * TerrainResolution;
        ushort[] heights = new ushort[count];
        byte[] splat = new byte[count * 4];

        float origin = -TerrainResolution * TerrainCellSize * 0.5f;

        for (int z = 0; z < TerrainResolution; z++)
        {
            for (int x = 0; x < TerrainResolution; x++)
            {
                float wx = origin + (x * TerrainCellSize);
                float wz = origin + (z * TerrainCellSize);

                float h = SampleAlpineHeight(wx, wz);

                float normalised = (h - TerrainMinHeight) / (TerrainMaxHeight - TerrainMinHeight);
                int index = (z * TerrainResolution) + x;
                heights[index] = (ushort)(Math.Clamp(normalised, 0f, 1f) * ushort.MaxValue);

                // Compute slope via central differences
                float e = TerrainCellSize;
                float hl = SampleAlpineHeight(wx - e, wz);
                float hr = SampleAlpineHeight(wx + e, wz);
                float hd = SampleAlpineHeight(wx, wz - e);
                float hu = SampleAlpineHeight(wx, wz + e);
                float nx = hl - hr;
                float ny = 2f * e;
                float nz = hd - hu;
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                float normalY = ny / MathF.Max(len, 0.001f);
                float slope = 1f - normalY;

                // Four authored routes are baked into splat channel 1, matching the editable
                // path records used by the Terrain Editor and runtime ribbon geometry.
                float trailDist = TrailDistance(wx, wz);
                float trailPath = 1f - Smoothstep(1.6f, 5.2f, trailDist);

                // Rock: steep slopes + noise
                float rockBase = Smoothstep(0.34f, 0.62f, slope);
                float rockNoise = Fbm(wx * 0.06f, wz * 0.06f, 3, 2.0f, 0.5f, 613) * 0.22f;
                float rock = Math.Clamp(rockBase + rockNoise, 0f, 1f);

                // Moss gathers at altitude and around damp lake shelves.
                float moss = Math.Clamp((h - 32f) / 28f, 0f, 1f) * (1f - rock);
                if (TryLakeDistance(wx, wz, out LakeSpec lake, out float lakeDistance))
                {
                    float shore = 1f - Smoothstep(lake.Radius - 5f, lake.Radius + 10f, lakeDistance);
                    moss = MathF.Max(moss, shore * 0.68f);
                }

                // Grass: everything else
                float grass = MathF.Max(0f, 1f - rock - trailPath - moss);

                // Normalize
                float total = grass + trailPath + rock + moss;
                if (total < 0.0001f) { grass = 1f; total = 1f; }
                float inv = 1f / total;

                int channel = index * 4;
                splat[channel] = (byte)(Math.Clamp(grass * inv, 0f, 1f) * 255f);
                splat[channel + 1] = (byte)(Math.Clamp(trailPath * inv, 0f, 1f) * 255f);
                splat[channel + 2] = (byte)(Math.Clamp(rock * inv, 0f, 1f) * 255f);
                splat[channel + 3] = (byte)(Math.Clamp(moss * inv, 0f, 1f) * 255f);
            }
        }

        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write(0x4E525447); // 'GTRN'
        writer.Write(1);
        writer.Write(TerrainResolution);
        writer.Write(TerrainResolution);
        writer.Write(TerrainCellSize);
        writer.Write(origin);
        writer.Write(origin);
        writer.Write(TerrainMinHeight);
        writer.Write(TerrainMaxHeight);
        for (int i = 0; i < count; i++)
        {
            writer.Write(heights[i]);
        }
        writer.Write(splat);
    }

    private static void WriteNaturalWorld(string terrainResourcePath)
    {
        List<FoliageRecord> foliage = BuildAlpineFoliage();
        string cachePath = Path.Combine(Path.GetDirectoryName(terrainResourcePath)!, AuthoredFoliageCacheName);
        string digest = WriteFoliageCache(cachePath, foliage);

        JsonArray paths = [];
        foreach (TrailSpec trail in Trails) paths.Add(BuildTrailDocument(trail));
        JsonArray waterBodies = [];
        foreach (LakeSpec lake in Lakes) waterBodies.Add(BuildWaterDocument(lake));

        JsonObject nature = new()
        {
            ["schema"] = "genesis.terrain-nature",
            ["version"] = 1,
            ["pathSettings"] = new JsonObject
            {
                ["seed"] = 202608,
                ["pathCount"] = Trails.Length,
                ["width"] = 4.6,
                ["gradeStrength"] = 0.85,
                ["splatChannel"] = 1,
                ["kind"] = "Trail",
            },
            ["paths"] = paths,
            ["foliageSettings"] = new JsonObject
            {
                ["seed"] = AuthoredFoliageSeed,
                ["preset"] = "TemperateForest",
                ["maximumInstances"] = AuthoredFoliageMaximum,
                ["density"] = 0.98,
                ["minimumSpacing"] = 0.58,
                ["pathExclusion"] = 2.2,
                ["maximumSlopeDegrees"] = 42.0,
                ["nearDistance"] = 55.0,
                ["farDistance"] = 260.0,
                ["streamingCellSize"] = 24.0,
                ["visibleInstanceBudget"] = 9000,
                ["triangleBudget"] = 420000,
                ["residentMemoryBudgetMegabytes"] = 64.0,
                ["gpuUploadBudgetMegabytes"] = 4.0,
                ["targetGpuMilliseconds"] = 5.5,
            },
            ["foliageCacheFile"] = AuthoredFoliageCacheName,
            ["foliageCacheSha256"] = digest,
            ["foliageInstanceCount"] = foliage.Count,
            ["waterBodies"] = waterBodies,
            ["pointsOfInterest"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = NewId(),
                    ["name"] = "South Trail Camp",
                    ["category"] = "Camp",
                    ["position"] = Vector(-18f, SampleAlpineHeight(-18f, -214f), -214f),
                    ["discoveryRadius"] = 24.0,
                },
                new JsonObject
                {
                    ["id"] = NewId(),
                    ["name"] = "Willowmere Shore",
                    ["category"] = "Scenic",
                    ["position"] = Vector(Lakes[0].X, Lakes[0].Surface, Lakes[0].Z),
                    ["discoveryRadius"] = 50.0,
                },
                new JsonObject
                {
                    ["id"] = NewId(),
                    ["name"] = "Broken Watchtower",
                    ["category"] = "Landmark",
                    ["position"] = Vector(214f, SampleAlpineHeight(214f, -260f), -260f),
                    ["discoveryRadius"] = 42.0,
                },
                new JsonObject
                {
                    ["id"] = NewId(),
                    ["name"] = "Weathered Arch",
                    ["category"] = "Landmark",
                    ["position"] = Vector(126f, SampleAlpineHeight(126f, 118f), 118f),
                    ["discoveryRadius"] = 36.0,
                },
                new JsonObject
                {
                    ["id"] = NewId(),
                    ["name"] = "The Grove",
                    ["category"] = "AncientWoodland",
                    ["position"] = Vector(318f, SampleAlpineHeight(318f, 34f), 34f),
                    ["discoveryRadius"] = 55.0,
                }),
        };

        File.WriteAllText(terrainResourcePath + ".nature.json", nature.ToJsonString(JsonOptions));
    }

    private static JsonObject BuildTrailDocument(TrailSpec trail)
    {
        JsonArray points = [];
        float length = 0f;
        for (int i = 0; i < trail.Points.Length; i++)
        {
            (float x, float z) = trail.Points[i];
            points.Add(Point(x, z));
            if (i > 0)
            {
                float dx = x - trail.Points[i - 1].X;
                float dz = z - trail.Points[i - 1].Z;
                length += MathF.Sqrt(dx * dx + dz * dz);
            }
        }
        return new JsonObject
        {
            ["id"] = NewId(), ["name"] = trail.Name, ["kind"] = "Trail",
            ["width"] = trail.Width, ["points"] = points, ["length"] = length,
        };
    }

    private static JsonObject BuildWaterDocument(LakeSpec lake) => new()
    {
        ["id"] = NewId(), ["name"] = lake.Name, ["kind"] = "Water",
        ["center"] = Vector(lake.X, lake.Surface, lake.Z),
        ["sizeX"] = lake.Radius * 2f, ["sizeZ"] = lake.Radius * 2f,
        ["surfaceHeight"] = lake.Surface,
        ["simulationEnabled"] = true, ["simulationResolution"] = 64,
        ["simulationDepth"] = 3.2, ["simulationDamping"] = 0.988,
        ["temperatureCelsius"] = 12.0, ["rainCoupling"] = 0.82,
        ["waveAmplitude"] = 0.16, ["flowSpeed"] = 0.12,
        ["physicsDepth"] = lake.Depth + 2f, ["fluidDensity"] = 1000.0,
        ["buoyancyStrength"] = 1.2, ["linearDrag"] = 3.2, ["angularDrag"] = 1.5,
        ["swimmable"] = true, ["damaging"] = false,
    };

    private static List<FoliageRecord> BuildAlpineFoliage()
    {
        const float halfWorld = TerrainResolution * TerrainCellSize * 0.5f;
        const float spacing = 1.35f;
        var result = new List<FoliageRecord>(AuthoredFoliageMaximum);
        int cells = (int)MathF.Ceiling(halfWorld * 2f / spacing);

        for (int zCell = 0; zCell <= cells && result.Count < AuthoredFoliageMaximum; zCell++)
        {
            for (int xCell = 0; xCell <= cells && result.Count < AuthoredFoliageMaximum; xCell++)
            {
                uint hash = StableHash(unchecked((uint)(AuthoredFoliageSeed ^ xCell * 73856093 ^ zCell * 19349663)));
                float x = -halfWorld + (xCell + 0.12f + Random01(hash + 1u) * 0.76f) * spacing;
                float z = -halfWorld + (zCell + 0.12f + Random01(hash + 2u) * 0.76f) * spacing;

                // Stay inside world bounds with margin
                if (x < -halfWorld + 20f || x > halfWorld - 20f || z < -halfWorld + 20f || z > halfWorld - 20f) continue;

                float h = SampleAlpineHeight(x, z);

                // Reject steep slopes
                float e = TerrainCellSize;
                float nx = SampleAlpineHeight(x - e, z) - SampleAlpineHeight(x + e, z);
                float ny = 2f * e;
                float nz2 = SampleAlpineHeight(x, z - e) - SampleAlpineHeight(x, z + e);
                float len = MathF.Sqrt(nx * nx + ny * ny + nz2 * nz2);
                float normalY = ny / MathF.Max(len, 0.001f);
                float slope = 1f - normalY;
                if (slope > 0.55f) continue;

                // Reject high altitude (above tree line)
                if (h > 55f) continue;

                // Keep every authored route readable. Lake interiors stay clear, while their
                // damp banks deliberately receive the dedicated instanced reed species.
                if (TrailDistance(x, z) < 5.8f) continue;
                bool shore = TryLakeDistance(x, z, out LakeSpec lake, out float lakeDistance);
                if (shore && lakeDistance < lake.Radius - 5f) continue;

                // Noise-driven forest density
                float density = Fbm(x * 0.0072f, z * 0.0072f, 4, 2.03f, 0.5f, 900) * 0.5f + 0.5f;
                float clearing = Fbm(x * 0.0035f, z * 0.0035f, 3, 2f, 0.5f, 1200) * 0.5f + 0.5f;
                density *= Smoothstep(0.30f, 0.62f, clearing);
                density *= 1f - Smoothstep(0.34f, 0.66f, slope);

                if (Random01(hash + 3u) > density * 1.12f) continue;

                // Species selection based on altitude and noise
                byte species = shore && lakeDistance < lake.Radius + 8f
                    ? (byte)6 // Reed
                    : (byte)(Random01(hash + 4u) switch
                {
                    < 0.50f => 0, // MeadowGrass
                    < 0.68f => 1, // TallGrass
                    < 0.80f => 5, // Wildflower
                    < 0.90f => 2, // Fern
                    < 0.96f => 3, // Shrub
                    _ => 4,       // Sapling
                });

                float baseScale = species switch
                {
                    0 => 0.68f,
                    1 => 1.05f,
                    2 => 0.88f,
                    3 => 1.15f,
                    4 => 0.95f,
                    5 => 0.75f,
                    6 => 1.15f,
                    _ => 0.68f,
                };

                result.Add(new FoliageRecord(
                    x, h, z,
                    baseScale * (0.85f + Random01(hash + 5u) * 0.35f),
                    Random01(hash + 6u) * MathF.Tau,
                    species,
                    0.55f + Random01(hash + 7u) * 0.45f,
                    Random01(hash + 8u) * 2f - 1f));
            }
        }

        return result;
    }

    private static string WriteFoliageCache(string path, IReadOnlyList<FoliageRecord> foliage)
    {
        const float origin = -TerrainResolution * TerrainCellSize * 0.5f;
        using (FileStream stream = File.Create(path))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(0x4C4F4647); // 'GFOL'
            writer.Write(1);
            writer.Write(AuthoredFoliageSeed);
            writer.Write(0);
            writer.Write(origin);
            writer.Write(origin);
            writer.Write(origin + ((TerrainResolution - 1) * TerrainCellSize));
            writer.Write(origin + ((TerrainResolution - 1) * TerrainCellSize));
            writer.Write(foliage.Count);
            foreach (FoliageRecord instance in foliage)
            {
                writer.Write(instance.X); writer.Write(instance.Y); writer.Write(instance.Z);
                writer.Write(instance.Scale); writer.Write(instance.Rotation); writer.Write(instance.Species);
                writer.Write(instance.WindExposure); writer.Write(instance.HueVariation);
            }
        }

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static JsonObject Point(float x, float z) => Vector(x, SampleAlpineHeight(x, z) + 0.05f, z);

    private static JsonObject Vector(float x, float y, float z) => new()
    {
        ["x"] = x,
        ["y"] = y,
        ["z"] = z,
    };

    private static uint StableHash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }

    private static float Random01(uint value) => StableHash(value) * (1f / uint.MaxValue);

    private readonly record struct FoliageRecord(
        float X,
        float Y,
        float Z,
        float Scale,
        float Rotation,
        byte Species,
        float WindExposure,
        float HueVariation);

    // ── Prefabs & GameObjects ───────────────────────────────────────────────────

    private static string CreateModelObject(
        ResourceService resources,
        string folder,
        string projectRoot,
        string objectName,
        string modelPath,
        string texturePath,
        string? shaderPath)
    {
        string modelAsset = Relative(projectRoot, modelPath);
        string textureAsset = Relative(projectRoot, texturePath);
        string path = resources.CreateResource(folder, ResourceKind.GameObject, objectName);

        JsonArray components =
        [
            Component("ModelRendererComponent", new JsonObject
            {
                ["ModelAsset"] = modelAsset,
                ["ScaleX"] = 1.0,
                ["ScaleY"] = 1.0,
                ["ScaleZ"] = 1.0,
                ["CastShadows"] = true,
                ["ReceiveShadows"] = true,
            }),
            Component("MaterialComponent", new JsonObject { ["Asset"] = textureAsset }),
            Component("PhysicsComponent", new JsonObject
            {
                ["Preset"] = "StaticSolid",
                ["Gravity"] = 0.0,
                ["GravityDirection"] = 270.0,
                ["Friction"] = 0.8,
                ["Solid"] = true,
            }),
        ];

        if (!string.IsNullOrWhiteSpace(shaderPath))
        {
            components.Add(Component("ShaderComponent", new JsonObject { ["Asset"] = Relative(projectRoot, shaderPath) }));
        }

        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["sprite"] = string.Empty,
            ["model"] = modelAsset,
            ["material"] = textureAsset,
            ["shader"] = shaderPath != null ? Relative(projectRoot, shaderPath) : string.Empty,
            ["physics"] = "StaticSolid",
            ["components"] = components,
            ["events"] = new JsonArray(),
        };

        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static string CreateWindManager(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.GameObject, WindManagerName);
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["sprite"] = string.Empty,
            ["model"] = string.Empty,
            ["components"] = new JsonArray(
                Component("ScriptComponent", new JsonObject { ["ScriptClass"] = WindManagerName })),
            ["events"] = new JsonArray("Create", "Step"),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, WindManagerName);
        Directory.CreateDirectory(eventFolder);
        File.WriteAllText(Path.Combine(eventFolder, "Create.pgsl"), WindManagerCreateEvent);
        File.WriteAllText(Path.Combine(eventFolder, "Step.pgsl"), WindManagerStepEvent);
        return path;
    }

    private static string CreateInteractiveGrass(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.GameObject, InteractiveGrassName);
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["sprite"] = string.Empty,
            ["model"] = string.Empty,
            ["components"] = new JsonArray(
                Component("ScriptComponent", new JsonObject { ["ScriptClass"] = InteractiveGrassName })),
            ["events"] = new JsonArray("Create", "Step", "Draw"),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, InteractiveGrassName);
        Directory.CreateDirectory(eventFolder);
        File.WriteAllText(Path.Combine(eventFolder, "Create.pgsl"), GrassCreateEvent);
        File.WriteAllText(Path.Combine(eventFolder, "Step.pgsl"), GrassStepEvent);
        File.WriteAllText(Path.Combine(eventFolder, "Draw.pgsl"), GrassDrawEvent);
        return path;
    }

    private static string CreatePlayer(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.GameObject, PlayerName);
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["sprite"] = string.Empty,
            ["model"] = string.Empty,
            ["physics"] = "PlatformerCharacter",
            ["characterMotor"] = new JsonObject
            {
                ["walkSpeed"] = 6.2,
                ["sprintMultiplier"] = 1.85,
                ["groundAcceleration"] = 48.0,
                ["airAcceleration"] = 16.0,
                ["jumpSpeed"] = 6.8,
                ["stepHeight"] = 0.55,
                ["maximumSlopeDegrees"] = 52.0,
                ["swimSpeed"] = 4.8,
                ["swimVerticalSpeed"] = 4.0,
                ["swimAcceleration"] = 20.0,
                ["swimDrag"] = 3.2,
            },
            ["components"] = new JsonArray(
                Component("PhysicsComponent", new JsonObject
                {
                    ["Preset"] = "PlatformerCharacter",
                    ["Gravity"] = 0.0,
                    ["GravityDirection"] = 270.0,
                    ["Friction"] = 0.15,
                    ["Solid"] = true,
                }),
                Component("ScriptComponent", new JsonObject { ["ScriptClass"] = PlayerName })),
            ["events"] = new JsonArray("Create", "Step", "Draw", "DrawGui"),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, PlayerName);
        Directory.CreateDirectory(eventFolder);
        File.WriteAllText(Path.Combine(eventFolder, "Create.pgsl"), PlayerCreateEvent);
        File.WriteAllText(Path.Combine(eventFolder, "Step.pgsl"), PlayerStepEvent);
        File.WriteAllText(Path.Combine(eventFolder, "Draw.pgsl"), PlayerDrawEvent);
        File.WriteAllText(Path.Combine(eventFolder, "DrawGui.pgsl"), PlayerDrawGuiEvent);
        return path;
    }

    // ── Particle & Audio ────────────────────────────────────────────────────────

    private static string CreatePollenParticles(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.Particle, "SunlightPollen");
        JsonObject document = new()
        {
            ["maxParticles"] = 360,
            ["emitRate"] = 28.0,
            ["burstCount"] = 0,
            ["loop"] = true,
            ["shape"] = "Sphere",
            ["spreadDegrees"] = 45.0,
            ["emitRadius"] = 18.0,
            ["speed"] = 0.35,
            ["speedVariance"] = 0.25,
            ["gravity"] = -0.04,
            ["drag"] = 0.65,
            ["lifetime"] = 4.5,
            ["lifetimeVariance"] = 1.2,
            ["startSize"] = 0.12,
            ["endSize"] = 0.04,
            ["emissive"] = 1.8,
            ["blendMode"] = "Additive",
            ["startColor"] = Color(1.0, 0.92, 0.58, 0.8),
            ["endColor"] = Color(0.95, 0.78, 0.32, 0.0),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static string CreateCampfireParticles(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.Particle, "CampfireSparks");
        JsonObject document = new()
        {
            ["maxParticles"] = 240,
            ["emitRate"] = 48.0,
            ["burstCount"] = 6,
            ["loop"] = true,
            ["shape"] = "Cone",
            ["spreadDegrees"] = 18.0,
            ["emitRadius"] = 0.35,
            ["speed"] = 2.4,
            ["speedVariance"] = 0.5,
            ["gravity"] = -0.3,
            ["drag"] = 0.25,
            ["lifetime"] = 1.8,
            ["lifetimeVariance"] = 0.4,
            ["startSize"] = 0.18,
            ["endSize"] = 0.02,
            ["emissive"] = 3.0,
            ["blendMode"] = "Additive",
            ["startColor"] = Color(1.0, 0.75, 0.2, 1.0),
            ["endColor"] = Color(0.85, 0.1, 0.02, 0.0),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static string CreateLanternObject(
        ResourceService resources, string folder, string projectRoot, string modelPath, string texturePath)
    {
        string model = Relative(projectRoot, modelPath);
        string texture = Relative(projectRoot, texturePath);
        string path = resources.CreateResource(folder, ResourceKind.GameObject, LanternName);
        File.WriteAllText(path, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["model"] = model,
            ["material"] = texture,
            ["physics"] = "StaticSolid",
            ["components"] = new JsonArray(
                Component("ModelRendererComponent", new JsonObject
                {
                    ["ModelAsset"] = model, ["ScaleX"] = 1.0, ["ScaleY"] = 1.0, ["ScaleZ"] = 1.0,
                    ["CastShadows"] = true, ["ReceiveShadows"] = true,
                }),
                Component("MaterialComponent", new JsonObject { ["Asset"] = texture }),
                Component("PointLightComponent", new JsonObject
                {
                    ["Enabled"] = true,
                    ["Color"] = new JsonArray(1.0, 0.58, 0.16),
                    ["Offset"] = new JsonArray(0.82, 2.75, 0.0),
                    ["Radius"] = 9.0,
                    ["Intensity"] = 1.8,
                }),
                Component("PhysicsComponent", new JsonObject
                {
                    ["Preset"] = "StaticSolid", ["Gravity"] = 0.0,
                    ["GravityDirection"] = 270.0, ["Friction"] = 0.62, ["Solid"] = true,
                })),
            ["events"] = new JsonArray(),
        }.ToJsonString(JsonOptions));
        return path;
    }

    private static string CreateCampfireObject(
        ResourceService resources,
        string folder,
        string projectRoot,
        string modelPath,
        string texturePath,
        string shaderPath,
        string particlePath,
        string audioPath)
    {
        string model = Relative(projectRoot, modelPath);
        string texture = Relative(projectRoot, texturePath);
        string shader = Relative(projectRoot, shaderPath);
        string particle = Relative(projectRoot, particlePath);
        string audio = Relative(projectRoot, audioPath);
        string path = resources.CreateResource(folder, ResourceKind.GameObject, CampfireName);
        File.WriteAllText(path, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["model"] = model,
            ["material"] = texture,
            ["shader"] = shader,
            ["physics"] = "StaticSolid",
            ["shaderParameters"] = new JsonObject
            {
                ["PulseSpeed"] = new JsonArray(2.4),
                ["Glow"] = new JsonArray(1.35),
            },
            ["components"] = new JsonArray(
                Component("ModelRendererComponent", new JsonObject
                {
                    ["ModelAsset"] = model, ["ScaleX"] = 1.0, ["ScaleY"] = 1.0, ["ScaleZ"] = 1.0,
                    ["CastShadows"] = true, ["ReceiveShadows"] = true,
                }),
                Component("MaterialComponent", new JsonObject { ["Asset"] = texture }),
                Component("ShaderComponent", new JsonObject { ["Asset"] = shader }),
                Component("ParticleComponent", new JsonObject
                {
                    ["Asset"] = particle, ["Emitting"] = true, ["FollowEntity"] = true,
                    ["RateScale"] = 1.0, ["EmitRate"] = 0.0,
                }),
                Component("AudioComponent", new JsonObject
                {
                    ["Asset"] = audio, ["AutoPlay"] = true, ["Spatial"] = true,
                    ["Loop"] = true, ["Volume"] = 0.42, ["Pitch"] = 1.0,
                }),
                Component("PointLightComponent", new JsonObject
                {
                    ["Enabled"] = true, ["Color"] = new JsonArray(1.0, 0.42, 0.08),
                    ["Offset"] = new JsonArray(0.0, 1.15, 0.0), ["Radius"] = 13.0, ["Intensity"] = 3.4,
                }),
                Component("PhysicsComponent", new JsonObject
                {
                    ["Preset"] = "StaticSolid", ["Gravity"] = 0.0,
                    ["GravityDirection"] = 270.0, ["Friction"] = 0.8, ["Solid"] = true,
                })),
            ["events"] = new JsonArray(),
        }.ToJsonString(JsonOptions));
        return path;
    }

    private static string CreateParticleFieldObject(
        ResourceService resources,
        string folder,
        string projectRoot,
        string name,
        string particlePath,
        float rateScale)
    {
        string particle = Relative(projectRoot, particlePath);
        string path = resources.CreateResource(folder, ResourceKind.GameObject, name);
        File.WriteAllText(path, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["components"] = new JsonArray(Component("ParticleComponent", new JsonObject
            {
                ["Asset"] = particle,
                ["Emitting"] = true,
                ["FollowEntity"] = true,
                ["RateScale"] = rateScale,
                ["EmitRate"] = 0.0,
            })),
            ["events"] = new JsonArray(),
        }.ToJsonString(JsonOptions));
        return path;
    }

    private static string CreateAmbientAudioObject(
        ResourceService resources, string folder, string projectRoot, string audioPath)
    {
        string audio = Relative(projectRoot, audioPath);
        string path = resources.CreateResource(folder, ResourceKind.GameObject, NatureAudioName);
        File.WriteAllText(path, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["components"] = new JsonArray(Component("AudioComponent", new JsonObject
            {
                ["Asset"] = audio,
                ["AutoPlay"] = true,
                ["Spatial"] = false,
                ["Loop"] = true,
                ["Volume"] = 0.55,
                ["Pitch"] = 1.0,
            })),
            ["events"] = new JsonArray(),
        }.ToJsonString(JsonOptions));
        return path;
    }

    private static string CreateFireflyParticles(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.Particle, "TwilightFireflies");
        JsonObject document = new()
        {
            ["maxParticles"] = 420,
            ["emitRate"] = 22.0,
            ["burstCount"] = 16,
            ["loop"] = true,
            ["shape"] = "Sphere",
            ["spreadDegrees"] = 180.0,
            ["emitRadius"] = 22.0,
            ["speed"] = 0.32,
            ["speedVariance"] = 0.28,
            ["gravity"] = -0.015,
            ["drag"] = 0.82,
            ["lifetime"] = 7.5,
            ["lifetimeVariance"] = 2.2,
            ["startSize"] = 0.095,
            ["endSize"] = 0.035,
            ["emissive"] = 3.8,
            ["blendMode"] = "Additive",
            ["startColor"] = Color(0.78, 1.0, 0.24, 0.95),
            ["endColor"] = Color(0.34, 0.72, 0.08, 0.0),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static string CreateFallingLeavesParticles(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.Particle, "FallingCanopyLeaves");
        JsonObject document = new()
        {
            ["maxParticles"] = 520,
            ["emitRate"] = 18.0,
            ["burstCount"] = 8,
            ["loop"] = true,
            ["shape"] = "Sphere",
            ["spreadDegrees"] = 110.0,
            ["emitRadius"] = 24.0,
            ["speed"] = 0.7,
            ["speedVariance"] = 0.5,
            ["gravity"] = 0.22,
            ["drag"] = 0.74,
            ["lifetime"] = 8.0,
            ["lifetimeVariance"] = 2.0,
            ["startSize"] = 0.20,
            ["endSize"] = 0.12,
            ["emissive"] = 0.15,
            ["blendMode"] = "Alpha",
            ["startColor"] = Color(0.34, 0.62, 0.12, 0.82),
            ["endColor"] = Color(0.55, 0.24, 0.04, 0.0),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static string CreateNatureAmbienceAudio(ResourceService resources, string folder, string projectRoot)
    {
        string path = resources.CreateResource(folder, ResourceKind.Audio, NatureAudioName);
        string wavePath = Path.Combine(folder, NatureAudioName + ".wav");
        WriteNatureAmbienceWave(wavePath);
        JsonObject document = new()
        {
            ["schemaVersion"] = 1,
            ["source"] = Relative(projectRoot, wavePath),
            ["volume"] = 0.55,
            ["loop"] = true,
            ["spatial"] = false,
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static string CreateCampfireAudio(ResourceService resources, string folder, string projectRoot)
    {
        string path = resources.CreateResource(folder, ResourceKind.Audio, CampfireAudioName);
        string wavePath = Path.Combine(folder, CampfireAudioName + ".wav");
        WriteCampfireWave(wavePath);
        JsonObject document = new()
        {
            ["schemaVersion"] = 1,
            ["source"] = Relative(projectRoot, wavePath),
            ["volume"] = 0.42,
            ["loop"] = true,
            ["spatial"] = true,
            ["minDistance"] = 1.5,
            ["maxDistance"] = 34.0,
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static void WriteCampfireWave(string path)
    {
        const int sampleRate = 22050;
        const int seconds = 2;
        int sampleCount = sampleRate * seconds;
        int dataBytes = sampleCount * sizeof(short);
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8.ToArray()); writer.Write(36 + dataBytes); writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray()); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8.ToArray()); writer.Write(dataBytes);
        uint noise = 0xA341316Cu;
        for (int i = 0; i < sampleCount; i++)
        {
            float time = i / (float)sampleRate;
            noise = StableHash(noise + (uint)i + 1u);
            float crackle = ((noise >> 8) * (1f / 16777216f) * 2f - 1f);
            float bed = MathF.Sin(time * MathF.Tau * 72f) * 0.13f + MathF.Sin(time * MathF.Tau * 119f) * 0.07f;
            float envelope = 0.72f + 0.18f * MathF.Sin(time * MathF.Tau * 1.7f);
            writer.Write((short)(Math.Clamp((bed + crackle * 0.12f) * envelope, -1f, 1f) * short.MaxValue));
        }
    }

    private static string CreatePhysicsMaterial(
        ResourceService resources, string folder, string name, float friction, float restitution, float density)
    {
        string path = resources.CreateResource(folder, ResourceKind.Physics, name);
        File.WriteAllText(path, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["friction"] = friction,
            ["restitution"] = restitution,
            ["density"] = density,
        }.ToJsonString(JsonOptions));
        return path;
    }

    private static void WriteNatureAmbienceWave(string path)
    {
        const int sampleRate = 22050;
        const int seconds = 3;
        const short channels = 1;
        const short bitsPerSample = 16;
        int sampleCount = sampleRate * seconds;
        int dataBytes = sampleCount * sizeof(short);
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        uint noise = 0x541278ABu;
        for (int i = 0; i < sampleCount; i++)
        {
            float time = i / (float)sampleRate;
            noise = StableHash(noise + (uint)i + 1u);
            float gust = MathF.Sin(time * MathF.Tau * 0.35f) * 0.4f + MathF.Sin(time * MathF.Tau * 0.82f) * 0.25f;
            float windHiss = ((noise >> 8) * (1f / 16777216f) * 2f - 1f) * (0.12f + gust * 0.08f);
            float breezeTone = MathF.Sin(time * MathF.Tau * 94f) * 0.06f + MathF.Sin(time * MathF.Tau * 148f) * 0.035f;
            short sample = (short)(Math.Clamp((windHiss + breezeTone), -1f, 1f) * short.MaxValue);
            writer.Write(sample);
        }
    }

    // ── Room Setup ──────────────────────────────────────────────────────────────

    private static void CreateRoom(
        ResourceService resources,
        string folder,
        string projectRoot,
        string player,
        string terrain,
        string grassAlbedo,
        string pineTree,
        string oakTree,
        string birchTree,
        string windsweptTree,
        string boulder,
        string cliff,
        string watchtower,
        string stoneArch,
        string fallenLog,
        string lantern,
        string campfire,
        string pollenField,
        string fireflyField,
        string fallingLeaves,
        string windManager,
        string interactiveGrass,
        string natureAudio)
    {
        string path = resources.CreateResource(folder, ResourceKind.Room, RoomName);

        JsonArray nodes =
        [
            new JsonObject
            {
                ["id"] = NewId(),
                ["kind"] = "Terrain",
                ["name"] = "Verdant Hollow Terrain",
                ["enabled"] = true,
                ["enabledIn2D"] = false,
                ["enabledIn3D"] = true,
                ["layerId"] = "terrain",
                ["transform"] = Transform(0, 0, 0),
                ["terrain"] = new JsonObject
                {
                    ["asset"] = Relative(projectRoot, terrain),
                    ["albedo"] = Relative(projectRoot, grassAlbedo),
                    ["uvScale"] = 0.06,
                },
            },
            MakeNode(projectRoot, player, "Nature Explorer", -18, SampleAlpineHeight(-18, -238), -238, "gameplay"),
            MakeNode(projectRoot, campfire, "South Trail Campfire", -18, SampleAlpineHeight(-18, -214), -214, "landmarks", yaw: 18),
            MakeNode(projectRoot, watchtower, "Broken Watchtower", 214, SampleAlpineHeight(214, -260), -260, "landmarks", yaw: -24, scale: 1.2f),
            MakeNode(projectRoot, stoneArch, "Weathered Arch", 126, SampleAlpineHeight(126, 118), 118, "landmarks", yaw: 38, scale: 1.15f),
            MakeNode(projectRoot, fallenLog, "Mossed Fallen Log", -50, SampleAlpineHeight(-50, -72), -72, "landmarks", yaw: 34, scale: 1.25f),
            MakeNode(projectRoot, fallenLog, "Grove Fallen Log", 288, SampleAlpineHeight(288, 46), 46, "landmarks", yaw: -51, scale: 1.4f),
            MakeNode(projectRoot, pollenField, "Sunlit Camp Pollen", -14, SampleAlpineHeight(-14, -198) + 4, -198, "effects"),
            MakeNode(projectRoot, fireflyField, "Willowmere Fireflies", Lakes[0].X, Lakes[0].Surface + 1.5f, Lakes[0].Z, "effects"),
            MakeNode(projectRoot, fireflyField, "Grove Fireflies", Lakes[3].X, Lakes[3].Surface + 1.5f, Lakes[3].Z, "effects"),
            MakeNode(projectRoot, fallingLeaves, "Grove Falling Leaves", 318, SampleAlpineHeight(318, 34) + 10, 34, "effects"),
            MakeNode(projectRoot, windManager, "Wind & Climate", 0, 0, 0, "effects"),
            MakeNode(projectRoot, natureAudio, "Forest Ambience", 0, 0, 0, "audio"),
        ];

        // Warm authored lanterns make the main path legible through dusk, rain and fog.
        for (int i = 1; i < Trails[0].Points.Length - 1; i += 2)
        {
            (float lx, float lz) = Trails[0].Points[i];
            nodes.Add(MakeNode(projectRoot, lantern, $"Trail Lantern {i:00}", lx + 3.4f,
                SampleAlpineHeight(lx + 3.4f, lz), lz, "landmarks", yaw: i * 31f));
        }

        // Procedurally scatter trees across the 1km world using density noise
        const float halfW = TerrainResolution * TerrainCellSize * 0.5f;
        const float treeSpacing = 32f;
        int treeGridSize = (int)(halfW * 2f / treeSpacing);
        int treeCount = 0;
        for (int tz = 0; tz < treeGridSize; tz++)
        {
            for (int tx = 0; tx < treeGridSize; tx++)
            {
                uint th = StableHash(unchecked((uint)(0xABCD1234 ^ tx * 198491317 ^ tz * 6542989)));
                float treePosX = -halfW + 30f + (tx + Random01(th + 1u) * 0.8f) * treeSpacing;
                float treePosZ = -halfW + 30f + (tz + Random01(th + 2u) * 0.8f) * treeSpacing;

                if (treePosX < -halfW + 25f || treePosX > halfW - 25f ||
                    treePosZ < -halfW + 25f || treePosZ > halfW - 25f) continue;

                float th2 = SampleAlpineHeight(treePosX, treePosZ);
                if (th2 > 45f || th2 < -4f) continue;

                // Slope check
                float e2 = TerrainCellSize;
                float tnx = SampleAlpineHeight(treePosX - e2, treePosZ) - SampleAlpineHeight(treePosX + e2, treePosZ);
                float tny = 2f * e2;
                float tnz = SampleAlpineHeight(treePosX, treePosZ - e2) - SampleAlpineHeight(treePosX, treePosZ + e2);
                float tlen = MathF.Sqrt(tnx * tnx + tny * tny + tnz * tnz);
                float tnY = tny / MathF.Max(tlen, 0.001f);
                if (1f - tnY > 0.45f) continue;

                if (TrailDistance(treePosX, treePosZ) < 11f) continue;
                if (TryLakeDistance(treePosX, treePosZ, out LakeSpec treeLake, out float treeLakeDistance)
                    && treeLakeDistance < treeLake.Radius + 5f) continue;

                // Noise-driven density
                float treeDensity = Fbm(treePosX * 0.0072f, treePosZ * 0.0072f, 4, 2.03f, 0.5f, 900) * 0.5f + 0.5f;
                float treeClearing = Fbm(treePosX * 0.0035f, treePosZ * 0.0035f, 3, 2f, 0.5f, 1200) * 0.5f + 0.5f;
                treeDensity *= Smoothstep(0.30f, 0.62f, treeClearing);
                if (Random01(th + 3u) > treeDensity * 0.65f) continue;

                // Species selection based on altitude
                float treeAlt = Smoothstep(18f, 46f, th2);
                string treeObj;
                string treeName;
                if (Random01(th + 4u) < treeAlt * 0.85f)
                {
                    treeObj = th2 > 35f ? windsweptTree : pineTree;
                    treeName = th2 > 35f ? $"Ridge Pine {treeCount}" : $"Pine {treeCount}";
                }
                else if (Random01(th + 5u) > 0.65f)
                {
                    treeObj = birchTree;
                    treeName = $"Birch {treeCount}";
                }
                else
                {
                    treeObj = oakTree;
                    treeName = $"Oak {treeCount}";
                }

                float treeScale = 0.82f + Random01(th + 6u) * 0.62f;
                nodes.Add(MakeNode(projectRoot, treeObj, treeName, treePosX, th2, treePosZ,
                    "vegetation", yaw: Random01(th + 7u) * 360f, scale: treeScale));
                treeCount++;
            }
        }

        // Hand-compose a close forest corridor around the opening walk. The broader forest above
        // is stochastic; this authored ring guarantees the first F5 view has the trunks, layered
        // canopy and depth cues that define the Nature1 reference.
        for (int row = 0; row < 17; row++)
        {
            float z = -258f + row * 13.5f;
            float centre = -18f + (z + 245f) * 0.14f;
            for (int side = -1; side <= 1; side += 2)
            {
                for (int rank = 0; rank < 2; rank++)
                {
                    uint h = StableHash(unchecked((uint)(0xC0FFEE ^ row * 92821 ^ side * 31337 ^ rank * 719)));
                    float distance = 14f + rank * 13f + Random01(h + 1u) * 4f;
                    float x = centre + side * distance;
                    float jitterZ = z + (Random01(h + 2u) - 0.5f) * 8f;
                    string tree = row % 5 == 0 ? birchTree : row % 3 == 0 ? oakTree : pineTree;
                    nodes.Add(MakeNode(projectRoot, tree, $"South Trail Canopy {row:00}-{side}-{rank}",
                        x, SampleAlpineHeight(x, jitterZ), jitterZ, "vegetation",
                        yaw: Random01(h + 3u) * 360f, scale: 0.88f + Random01(h + 4u) * 0.27f));
                }
            }
        }

        // Scatter rocks on steeper terrain
        int rockCount = 0;
        for (int rz = 0; rz < treeGridSize && rockCount < 40; rz += 2)
        {
            for (int rx = 0; rx < treeGridSize && rockCount < 40; rx += 2)
            {
                uint rh = StableHash(unchecked((uint)(0xDEAD_BEEF ^ rx * 512927357 ^ rz * 981726491)));
                float rockX = -halfW + 40f + (rx + Random01(rh + 1u)) * treeSpacing * 2f;
                float rockZ = -halfW + 40f + (rz + Random01(rh + 2u)) * treeSpacing * 2f;
                if (rockX < -halfW + 30f || rockX > halfW - 30f ||
                    rockZ < -halfW + 30f || rockZ > halfW - 30f) continue;

                float rHeight = SampleAlpineHeight(rockX, rockZ);
                float re = TerrainCellSize;
                float rnx = SampleAlpineHeight(rockX - re, rockZ) - SampleAlpineHeight(rockX + re, rockZ);
                float rny2 = 2f * re;
                float rnz = SampleAlpineHeight(rockX, rockZ - re) - SampleAlpineHeight(rockX, rockZ + re);
                float rlen = MathF.Sqrt(rnx * rnx + rny2 * rny2 + rnz * rnz);
                float rnY = rny2 / MathF.Max(rlen, 0.001f);
                float rslope = 1f - rnY;

                if (Random01(rh + 3u) > 0.25f + 1.5f * rslope) continue;

                string rockObj = Random01(rh + 4u) > 0.5f ? boulder : cliff;
                nodes.Add(MakeNode(projectRoot, rockObj, $"Rock {rockCount}", rockX, rHeight, rockZ,
                    "landmarks", yaw: Random01(rh + 5u) * 360f, scale: 0.7f + Random01(rh + 6u) * 0.8f));
                rockCount++;
            }
        }

        // A small set of script-driven grass clumps demonstrates live PGSL interaction; the
        // surrounding tens of thousands of blades use streamed instancing.
        int grassIndex = 0;
        foreach (TrailSpec trail in Trails)
        {
            foreach ((float gx, float gz) in trail.Points.Skip(1).Take(Math.Max(0, trail.Points.Length - 2)))
            {
                float offset = grassIndex % 2 == 0 ? 3.8f : -3.8f;
                nodes.Add(MakeNode(projectRoot, interactiveGrass, $"Interactive Trail Grass {grassIndex:00}",
                    gx + offset, SampleAlpineHeight(gx + offset, gz), gz, "vegetation"));
                grassIndex++;
            }
        }

        JsonObject room = new()
        {
            ["$schema"] = "genesis.room",
            ["version"] = 1,
            ["id"] = NewId(),
            ["name"] = RoomName,
            ["dimension"] = 1,
            ["settings"] = new JsonObject
            {
                ["width"] = 1920,
                ["height"] = 1080,
                ["depth"] = 1080,
                ["targetFps"] = 60,
                ["fixedFps"] = 60,
                ["captureMouse"] = true,
                ["gridSize"] = 1,
                ["snapEnabled"] = true,
            },
            ["environment"] = new JsonObject
            {
                ["backgroundColor"] = new JsonArray(0.30, 0.47, 0.68, 1.0),
                ["gravity"] = new JsonArray(0, -9.81, 0),
                ["ambientIntensity"] = 1.02,
                ["fogDensity"] = 0.0032,
                ["dynamicSky"] = true,
                ["weather"] = "Clear",
                ["timeOfDayHours"] = 7.75,
                ["timeScale"] = 8.0,
                ["automaticWeather"] = false,
                ["atmospherePreset"] = "Natural",
                ["volumetricClouds"] = true,
                ["cloudQuality"] = 3,
            },
            ["layers"] = new JsonArray(
                RoomLayer("terrain", "Terrain & Water", 0),
                RoomLayer("vegetation", "Vegetation", 20),
                RoomLayer("landmarks", "Landmarks & Props", 40),
                RoomLayer("effects", "Weather & Effects", 60),
                RoomLayer("audio", "Audio", 80),
                new JsonObject
                {
                    ["id"] = "gameplay",
                    ["name"] = "Gameplay",
                    ["order"] = 100,
                    ["enabled"] = true,
                }),
            ["nodes"] = nodes,
        };

        File.WriteAllText(path, room.ToJsonString(JsonOptions));
    }

    private static JsonObject RoomLayer(string id, string name, int order) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["order"] = order,
        ["enabled"] = true,
    };

    private static JsonObject MakeNode(
        string projectRoot,
        string prefabPath,
        string name,
        float x,
        float y,
        float z,
        string layerId,
        float yaw = 0f,
        float scale = 1f) => new()
    {
        ["id"] = NewId(),
        ["kind"] = "GameObject",
        ["name"] = name,
        ["enabled"] = true,
        ["enabledIn2D"] = false,
        ["enabledIn3D"] = true,
        ["layerId"] = layerId,
        ["transform"] = Transform(x, y, z, yaw, scale),
        ["gameObject"] = new JsonObject
        {
            ["prefab"] = Relative(projectRoot, prefabPath),
        },
    };

    // ── PGSL Gameplay Scripts ───────────────────────────────────────────────────

    private const string WindManagerCreateEvent = """
        // ── Wind & Climate Simulation ──────────────────────────────────────────────
        windClock = 0;
        windGust = 1.0;
        """;

    private const string WindManagerStepEvent = """
        windClock = windClock + DeltaTime;
        windGust = 0.85 + Sin(windClock * 0.4) * 0.35 + Sin(windClock * 1.1) * 0.18;
        """;

    private const string GrassCreateEvent = """
        swayPhase = (x * 19 + z * 31) % 100 * 0.0628;
        swayAngle = 0;
        bendAmount = 0;
        y = GetTerrainHeight(x, z);
        """;

    private const string GrassStepEvent = """
        swayPhase = swayPhase + DeltaTime * 2.2;
        bendAmount = Sin(swayPhase) * 0.14;
        swayAngle = swayPhase * 0.3;
        """;

    private const string GrassDrawEvent = """
        DrawSetColorRgb(48, 128, 56);
        DrawBox3D(x + bendAmount * 0.3, y + 0.35, z + bendAmount * 0.15, 0.22, 0.7, 0.22);
        DrawSetColorRgb(64, 156, 72);
        DrawBox3D(x + bendAmount * 0.6, y + 0.68, z + bendAmount * 0.3, 0.16, 0.45, 0.16);
        """;

    private const string PlayerCreateEvent = """
        // ── 1st Person 3D Nature Walker ────────────────────────────────────────────
        eyeHeight = 1.72;
        walkSpeed = 0.16;
        sprintSpeed = 0.28;
        jumpPower = 0.34;
        gravityStep = 0.016;
        verticalSpeed = 0;
        onGround = 1;

        bobTime = 0;
        bobAmount = 0;

        lookYaw = 0;
        lookPitch = -4;
        lookSensitivity = 0.16;

        x = -18;
        z = -238;
        y = GetTerrainHeight(x, z);

        SetMouseCaptured(true);
        Engine.SetCameraFov(72);
        Engine.SetCameraNearPlane(0.12);
        Engine.SetCameraFarPlane(2000);
        dust = ParticleCreate(180, 20, 1.2);

        showHelp = 0;
        helpTimer = 0;
        inWater = 0;
        travelState = "SOUTH TRAIL CAMP";

        SetCameraPosition(x, y + eyeHeight, z);
        SetCameraTarget(
            x + ForwardX(lookYaw, lookPitch),
            y + eyeHeight + ForwardY(lookYaw, lookPitch),
            z + ForwardZ(lookYaw, lookPitch));
        """;

    private const string PlayerStepEvent = """
        // ── Mouse Look ───────────────────────────────────────────────────────────
        lookYaw = lookYaw + (GetMouseLookDeltaX() * lookSensitivity);
        lookPitch = Clamp(lookPitch - (GetMouseLookDeltaY() * lookSensitivity), -85, 85);
        lookYaw = AngleNormalise(lookYaw);

        var forwardX = ForwardX(lookYaw, 0);
        var forwardZ = ForwardZ(lookYaw, 0);
        var rightX = RightX(lookYaw);
        var rightZ = RightZ(lookYaw);

        // ── Movement ─────────────────────────────────────────────────────────────
        var moveF = 0;
        var moveR = 0;
        if (KeyCheck("W") || KeyCheck("Up")) { moveF = moveF + 1; }
        if (KeyCheck("S") || KeyCheck("Down")) { moveF = moveF - 1; }
        if (KeyCheck("D") || KeyCheck("Right")) { moveR = moveR + 1; }
        if (KeyCheck("A") || KeyCheck("Left")) { moveR = moveR - 1; }

        var currentSpeed = walkSpeed;
        if (KeyCheck("Shift")) { currentSpeed = sprintSpeed; }

        var diagonal = 1;
        if (moveF != 0 && moveR != 0) { diagonal = 0.70710678; }

        var moving = 0;
        if (moveF != 0 || moveR != 0) { moving = 1; }

        var deltaX = ((forwardX * moveF) + (rightX * moveR)) * currentSpeed * diagonal;
        var deltaZ = ((forwardZ * moveF) + (rightZ * moveR)) * currentSpeed * diagonal;

        var nextX = Clamp(x + deltaX, -495, 495);
        var nextZ = Clamp(z + deltaZ, -495, 495);

        if (PlaceFree3D(nextX, z, 0.8)) { x = nextX; }
        if (PlaceFree3D(x, nextZ, 0.8)) { z = nextZ; }

        // ── Terrain Height & Water Snapping ──────────────────────────────────────
        var ground = GetTerrainHeight(x, z);
        inWater = WaterIsSwimmable(x, y + 0.5, z);

        travelState = "VERDANT LOOP";
        if (inWater) { travelState = "SWIMMING"; }
        if (x > 175 && z < -200) { travelState = "BROKEN WATCHTOWER"; }
        if (x > 250 && z > -20) { travelState = "THE GROVE"; }

        if (inWater == 1) {
            onGround = 0;
            verticalSpeed = verticalSpeed * 0.82;
            if (KeyCheck("Space")) { verticalSpeed = verticalSpeed + 0.025; }
            if (KeyCheck("Control")) { verticalSpeed = verticalSpeed - 0.02; }
            y = y + verticalSpeed;
            if (y < ground + 0.4) { y = ground + 0.4; verticalSpeed = 0; }
        } else if (onGround == 1) {
            y = ground;
            verticalSpeed = 0;
            if (KeyPressed("Space")) {
                verticalSpeed = jumpPower;
                onGround = 0;
            }
        } else {
            verticalSpeed = verticalSpeed - gravityStep;
            y = y + verticalSpeed;
            if (y <= ground) {
                y = ground;
                verticalSpeed = 0;
                onGround = 1;
            }
        }

        // ── Head Bobbing ─────────────────────────────────────────────────────────
        if (moving == 1 && onGround == 1) {
            bobTime = bobTime + DeltaTime * (KeyCheck("Shift") ? 13 : 9);
            bobAmount = Sin(bobTime) * (KeyCheck("Shift") ? 0.055 : 0.032);
        } else {
            bobAmount = bobAmount * 0.85;
        }

        // ── Dust Particles ───────────────────────────────────────────────────────
        ParticleSetCameraPosition(x, y + eyeHeight, z);
        if (moving == 1 && onGround == 1 && KeyCheck("Shift")) {
            ParticleSetRate(dust, 24);
        } else {
            ParticleSetRate(dust, 0);
        }
        ParticleUpdate(dust, DeltaTime);

        // ── 1st Person Camera ────────────────────────────────────────────────────
        var eyeX = x;
        var eyeY = y + eyeHeight + bobAmount;
        var eyeZ = z;
        SetCameraPosition(eyeX, eyeY, eyeZ);
        SetCameraTarget(
            eyeX + ForwardX(lookYaw, lookPitch),
            eyeY + ForwardY(lookYaw, lookPitch),
            eyeZ + ForwardZ(lookYaw, lookPitch));
        SetAudioListener(eyeX, eyeY, eyeZ);

        if (helpTimer < 120) { helpTimer = helpTimer + 1; }
        if (helpTimer >= 120) { showHelp = 0; }
        if (KeyPressed("H")) { showHelp = 1 - showHelp; helpTimer = 0; }
        """;

    private const string PlayerDrawEvent = """
        // Soft 1st Person shadow
        DrawSetColorRgb(15, 20, 15);
        DrawSetAlpha(0.28);
        DrawBox3D(x, y + 0.02, z, 0.7, 0.02, 0.7);
        DrawSetAlpha(1.0);
        """;

    private const string PlayerDrawGuiEvent = """
        // ── Minimalist Nature Explorer Reticle ───────────────────────────────────
        var cx = RoomGetWidth() / 2;
        var cy = RoomGetHeight() / 2;
        DrawSetAlpha(0.65);
        DrawSetColorRgb(255, 255, 255);
        DrawRectangle(cx - 4, cy - 1, cx + 4, cy + 1);
        DrawRectangle(cx - 1, cy - 4, cx + 1, cy + 4);

        // ── Top Left Scenic Card ─────────────────────────────────────────────────
        DrawSetAlpha(0.84);
        DrawSetColorRgb(10, 16, 26);
        DrawRectangle(16, 16, 360, 78);
        DrawSetAlpha(1);
        DrawSetColorRgb(236, 243, 255);
        DrawTextScaled(28, 26, "VERDANT HOLLOW", 20);

        DrawSetColorRgb(130, 195, 235);
        DrawTextScaled(28, 52, StringJoin2("ELEVATION  ", StringJoin2(StringOf(Floor(y)), " m")), 13);
        DrawSetColorRgb(170, 220, 140);
        DrawTextScaled(260, 52, StringJoin2("FPS  ", StringOf(Floor(Fps))), 13);

        DrawSetColorRgb(255, 178, 72);
        DrawTextScaled(390, 26, travelState, 16);

        // ── Controls Guide ───────────────────────────────────────────────────────
        if (showHelp == 1) {
            DrawSetAlpha(0.84);
            DrawSetColorRgb(10, 16, 26);
            DrawRectangle(16, 96, 360, 224);
            DrawSetAlpha(1);
            DrawSetColorRgb(214, 228, 246);
            DrawTextScaled(28, 106, "WASD / arrows   move", 15);
            DrawTextScaled(28, 128, "Mouse           look around", 15);
            DrawTextScaled(28, 150, "Shift           sprint", 15);
            DrawTextScaled(28, 172, "Space           jump / swim up", 15);
            DrawTextScaled(28, 194, "H               toggle guide", 15);
        }
        """;

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static JsonObject Color(double r, double g, double b, double a) => new()
    {
        ["r"] = r,
        ["g"] = g,
        ["b"] = b,
        ["a"] = a,
    };

    private static JsonObject Component(string type, JsonObject props) => new()
    {
        ["id"] = $"cmp-{Guid.NewGuid():N}",
        ["type"] = type,
        ["enabled"] = true,
        ["props"] = props,
    };

    private static void WritePixels(
        string documentPath, ImageDocument document, int width, int height, Action<byte[]> paint)
    {
        byte[] rgba = new byte[width * height * 4];
        paint(rgba);

        string dataDirectory = Path.ChangeExtension(documentPath, null);
        if (dataDirectory.EndsWith(".image", StringComparison.OrdinalIgnoreCase))
        {
            dataDirectory = dataDirectory[..^".image".Length];
        }

        dataDirectory += ".spritedata";
        string frameDirectory = Path.Combine(dataDirectory, "frames");
        Directory.CreateDirectory(frameDirectory);

        string frameId = Guid.NewGuid().ToString("N");
        string framePath = Path.Combine(frameDirectory, frameId + ".png");
        PngWriter.Write(framePath, rgba, width, height);

        string relative = Path.Combine(Path.GetFileName(dataDirectory), "frames", frameId + ".png")
            .Replace(Path.DirectorySeparatorChar, '/');

        document.Frames.Clear();
        document.Frames.Add(new ImageFrame
        {
            Id = frameId,
            Name = "Frame 1",
            DurationMilliseconds = 100,
            Source = relative,
        });

        ImageDocumentSerializer.SaveAtomic(documentPath, document);
    }

    private static byte Clamp255(int value) => (byte)Math.Clamp(value, 0, 255);

    private static JsonObject Transform(float x, float y, float z, float yaw = 0f, float scale = 1f) => new()
    {
        ["position"] = new JsonArray(x, y, z),
        ["rotation"] = new JsonArray(0, yaw, 0),
        ["scale"] = new JsonArray(scale, scale, scale),
    };

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string Relative(string projectRoot, string fullPath) =>
        Path.GetRelativePath(projectRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}

