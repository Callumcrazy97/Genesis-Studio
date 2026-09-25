using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Core.Projects.Templates;

/// <summary>
/// Builds the primary 3D World template: a walkable, first-person 3D scene with textured heightfield terrain,
/// forest groves of pine and oak trees, boulders, mouse-look player controller, particles, and HUD.
/// All environment objects (trees, rocks, beacons) are authored GameObject prefabs placed directly in the room.
/// </summary>
public static class SandboxTemplate
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public const string RoomName = "World";
    public const string PlayerName = "Player";
    public const string TerrainName = "Ground";
    public const string PineTreeName = "PineTree";
    public const string OakTreeName = "OakTree";
    public const string BoulderName = "Boulder";
    public const string BeaconName = "Beacon";
    public const string CampfireName = "Campfire";
    public const string CampfireModelName = "CampfireModel";
    public const string CampfireMaterialName = "CampfireMaterial";
    public const string CampfireShaderName = "CampfireGlow";
    public const string CampfireParticleName = "CampfireEmbers";
    public const string CampfireAudioName = "CampfireLoop";

    public const int AuthoredFoliageSeed = 440902;
    public const int AuthoredFoliageMaximum = 6500;
    public const string AuthoredFoliageCacheName = "Ground.terrain.gfoliage";
    public const string WaterName = "Explorer Pond";

    /// <summary>Terrain grid resolution. 129 is the editor's own default.</summary>
    private const int TerrainResolution = 129;

    /// <summary>World units per terrain cell.</summary>
    private const float TerrainCellSize = 1f;

    private const float TerrainMinHeight = -8f;
    private const float TerrainMaxHeight = 24f;

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
        Directory.CreateDirectory(images);
        Directory.CreateDirectory(objects);
        Directory.CreateDirectory(rooms);
        Directory.CreateDirectory(terrains);
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(particles);
        Directory.CreateDirectory(audio);
        Directory.CreateDirectory(shaders);

        CreateGroundTexture(resources, images);
        CreateBarkTexture(resources, images);
        CreateFoliageTexture(resources, images);
        string campfireMaterial = CreateCampfireMaterial(resources, images);

        string terrain = CreateTerrain(resources, terrains);
        string campfireModel = CreateCampfireModel(resources, models, session.RootPath, campfireMaterial);
        string campfireShader = CreateCampfireShader(resources, shaders, session.RootPath, campfireModel);
        string campfireParticle = CreateCampfireParticles(resources, particles);
        string campfireAudio = CreateCampfireAudio(resources, audio, session.RootPath);
        string player = CreatePlayer(resources, objects);
        string pineTree = CreatePineTree(resources, objects);
        string oakTree = CreateOakTree(resources, objects);
        string boulder = CreateBoulder(resources, objects);
        string beacon = CreateBeacon(resources, objects);
        string campfire = CreateCampfire(
            resources,
            objects,
            session.RootPath,
            campfireModel,
            campfireMaterial,
            campfireShader,
            campfireParticle,
            campfireAudio);

        CreateRoom(
            resources,
            rooms,
            session.RootPath,
            player,
            terrain,
            pineTree,
            oakTree,
            boulder,
            beacon,
            campfire);

        session.Manifest.StartRoom = $"Assets/Rooms/{RoomName}.room.json";
    }

    // ── Textures ────────────────────────────────────────────────────────────────

    private static void CreateGroundTexture(ResourceService resources, string folder)
    {
        const int size = 128;
        string path = resources.CreateResource(folder, ResourceKind.Image, "GroundTexture");
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
                    bool dark = ((x / 16) + (y / 16)) % 2 == 0;
                    int noise = ((x * 7) + (y * 13)) % 11 - 5;
                    int r = (dark ? 74 : 96) + noise;
                    int g = (dark ? 118 : 142) + noise;
                    int b = (dark ? 52 : 68) + noise;
                    int offset = ((y * size) + x) * 4;
                    rgba[offset] = Clamp255(r);
                    rgba[offset + 1] = Clamp255(g);
                    rgba[offset + 2] = Clamp255(b);
                    rgba[offset + 3] = 255;
                }
            }
        });
    }

    private static void CreateBarkTexture(ResourceService resources, string folder)
    {
        const int size = 64;
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
                    int grain = (x % 8 < 2) ? -18 : (x % 4 == 0) ? 14 : 0;
                    int r = 108 + grain;
                    int g = 72 + (grain / 2);
                    int b = 44 + (grain / 3);
                    int offset = ((y * size) + x) * 4;
                    rgba[offset] = Clamp255(r);
                    rgba[offset + 1] = Clamp255(g);
                    rgba[offset + 2] = Clamp255(b);
                    rgba[offset + 3] = 255;
                }
            }
        });
    }

    private static void CreateFoliageTexture(ResourceService resources, string folder)
    {
        const int size = 64;
        string path = resources.CreateResource(folder, ResourceKind.Image, "FoliageTexture");
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
                    int leaf = ((x * 5) + (y * 9)) % 13 - 6;
                    int r = 48 + leaf;
                    int g = 126 + leaf * 2;
                    int b = 58 + leaf;
                    int offset = ((y * size) + x) * 4;
                    rgba[offset] = Clamp255(r);
                    rgba[offset + 1] = Clamp255(g);
                    rgba[offset + 2] = Clamp255(b);
                    rgba[offset + 3] = 255;
                }
            }
        });
    }

    private static string CreateCampfireMaterial(ResourceService resources, string folder)
    {
        const int size = 64;
        string path = resources.CreateResource(folder, ResourceKind.Image, CampfireMaterialName);
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
                    float radial = MathF.Sqrt(((x - 31.5f) * (x - 31.5f)) + ((y - 31.5f) * (y - 31.5f))) / 45f;
                    int grain = ((x * 13) + (y * 7)) % 19 - 9;
                    int r = (int)(194 - radial * 92f) + grain;
                    int g = (int)(78 - radial * 38f) + grain / 3;
                    int b = (int)(24 - radial * 14f) + grain / 5;
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

    // ── Terrain ─────────────────────────────────────────────────────────────────

    public static float SampleTemplateHeight(float wx, float wz)
    {
        float h = (MathF.Sin(wx * 0.055f) * MathF.Cos(wz * 0.045f) * 7.5f)
            + (MathF.Sin((wx + wz) * 0.021f) * 4.5f);

        float distance = MathF.Sqrt((wx * wx) + (wz * wz));
        const float clearingRadius = 14f;
        const float clearingFalloff = 10f;
        if (distance < clearingRadius + clearingFalloff)
        {
            float blend = Math.Clamp(
                (distance - clearingRadius) / clearingFalloff, 0f, 1f);
            h *= blend;
        }

        return h;
    }

    private static string CreateTerrain(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.Terrain, TerrainName);

        JsonObject settings = new()
        {
            ["schemaVersion"] = 1,
            ["resolution"] = new JsonArray(TerrainResolution, TerrainResolution),
            ["cellSize"] = TerrainCellSize,
            ["minHeight"] = TerrainMinHeight,
            ["maxHeight"] = TerrainMaxHeight,
            ["seedProfile"] = "RollingHills",
            ["preset"] = "RollingHills",
            ["seed"] = 2026,
            ["fogEnabled"] = false,
            ["layers"] = new JsonArray(
                Layer("Grass", 0.28, 0.52, 0.22),
                Layer("Dirt", 0.44, 0.34, 0.22),
                Layer("Rock", 0.48, 0.48, 0.52),
                Layer("Forest", 0.20, 0.38, 0.18)),
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

                float h = SampleTemplateHeight(wx, wz);

                float normalised = (h - TerrainMinHeight) / (TerrainMaxHeight - TerrainMinHeight);
                int index = (z * TerrainResolution) + x;
                heights[index] = (ushort)(Math.Clamp(normalised, 0f, 1f) * ushort.MaxValue);

                float rock = Math.Clamp((h - 5.5f) / 5f, 0f, 1f);
                float dirt = Math.Clamp((h - 1.5f) / 4f, 0f, 1f) * (1f - rock);
                float grass = 1f - rock - dirt;

                // The template's authored trail is visible in the terrain material as well as in
                // the nature sidecar. This is the same route the regional grass deliberately
                // avoids, so editing either asset in Studio immediately shows the relationship.
                if (wz is >= -18f and <= 38f && MathF.Abs(wx) <= 2.4f)
                {
                    float trail = 1f - Math.Clamp(MathF.Abs(wx) / 2.4f, 0f, 1f);
                    dirt = MathF.Max(dirt, 0.72f + trail * 0.28f);
                    grass *= 1f - trail * 0.88f;
                    rock *= 1f - trail * 0.6f;
                }
                int channel = index * 4;
                splat[channel] = (byte)(Math.Clamp(grass, 0f, 1f) * 255f);
                splat[channel + 1] = (byte)(Math.Clamp(dirt, 0f, 1f) * 255f);
                splat[channel + 2] = (byte)(Math.Clamp(rock, 0f, 1f) * 255f);
                splat[channel + 3] = 0;
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

    /// <summary>
    /// Writes the canonical natural-world sidecars consumed unchanged by Terrain Editor and F5.
    /// This intentionally uses the public on-disk format rather than a test fixture: every project
    /// created from the 3D template starts with authorable regional foliage, a route and swimmable
    /// water already connected to its terrain.
    /// </summary>
    private static void WriteNaturalWorld(string terrainResourcePath)
    {
        List<FoliageRecord> foliage = BuildRegionalFoliage();
        string cachePath = Path.Combine(Path.GetDirectoryName(terrainResourcePath)!, AuthoredFoliageCacheName);
        string digest = WriteFoliageCache(cachePath, foliage);

        float pondX = 22f;
        float pondZ = -15f;
        float pondSurface = SampleTemplateHeight(pondX, pondZ) + 1.15f;
        JsonObject nature = new()
        {
            ["schema"] = "genesis.terrain-nature",
            ["version"] = 1,
            ["pathSettings"] = new JsonObject
            {
                ["seed"] = 2026,
                ["pathCount"] = 1,
                ["width"] = 3.2,
                ["gradeStrength"] = 0.72,
                ["splatChannel"] = 1,
                ["kind"] = "Trail",
            },
            ["paths"] = new JsonArray(new JsonObject
            {
                ["id"] = NewId(),
                ["name"] = "Mountain Trail",
                ["kind"] = "Trail",
                ["width"] = 3.2,
                ["points"] = new JsonArray(
                    Point(0f, -18f),
                    Point(0f, -4f),
                    Point(0f, 12f),
                    Point(0f, 38f)),
                ["length"] = 56.0,
            }),
            ["foliageSettings"] = new JsonObject
            {
                ["seed"] = AuthoredFoliageSeed,
                ["preset"] = "Meadow",
                ["maximumInstances"] = AuthoredFoliageMaximum,
                ["density"] = 0.88,
                ["minimumSpacing"] = 0.72,
                ["pathExclusion"] = 1.35,
                ["maximumSlopeDegrees"] = 38.0,
                ["nearDistance"] = 34.0,
                ["farDistance"] = 112.0,
                ["streamingCellSize"] = 16.0,
                ["visibleInstanceBudget"] = 6000,
                ["triangleBudget"] = 180000,
                ["residentMemoryBudgetMegabytes"] = 24.0,
                ["gpuUploadBudgetMegabytes"] = 1.25,
                ["targetGpuMilliseconds"] = 5.0,
            },
            ["foliageCacheFile"] = AuthoredFoliageCacheName,
            ["foliageCacheSha256"] = digest,
            ["foliageInstanceCount"] = foliage.Count,
            ["waterBodies"] = new JsonArray(new JsonObject
            {
                ["id"] = NewId(),
                ["name"] = WaterName,
                ["kind"] = "Pond",
                ["center"] = Vector(pondX, pondSurface, pondZ),
                ["sizeX"] = 13.0,
                ["sizeZ"] = 10.0,
                ["surfaceHeight"] = pondSurface,
                ["simulationEnabled"] = true,
                ["simulationResolution"] = 40,
                ["simulationDepth"] = 1.4,
                ["simulationDamping"] = 0.985,
                ["temperatureCelsius"] = 14.0,
                ["rainCoupling"] = 0.65,
                ["waveAmplitude"] = 0.14,
                ["flowSpeed"] = 0.22,
                ["physicsDepth"] = 3.5,
                ["fluidDensity"] = 1000.0,
                ["buoyancyStrength"] = 1.1,
                ["linearDrag"] = 2.8,
                ["angularDrag"] = 1.2,
                ["swimmable"] = true,
                ["damaging"] = false,
            }),
            ["pointsOfInterest"] = new JsonArray(new JsonObject
            {
                ["id"] = NewId(),
                ["name"] = "Trail Camp",
                ["category"] = "Camp",
                ["position"] = Vector(0f, SampleTemplateHeight(0f, 6f), 6f),
                ["discoveryRadius"] = 18.0,
            }),
        };

        File.WriteAllText(terrainResourcePath + ".nature.json", nature.ToJsonString(JsonOptions));
    }

    private static List<FoliageRecord> BuildRegionalFoliage()
    {
        const float origin = -TerrainResolution * TerrainCellSize * 0.5f;
        const float spacing = 0.72f;
        var result = new List<FoliageRecord>(AuthoredFoliageMaximum);
        int cells = (int)MathF.Ceiling((TerrainResolution - 1) * TerrainCellSize / spacing);

        for (int zCell = 0; zCell <= cells && result.Count < AuthoredFoliageMaximum; zCell++)
        {
            for (int xCell = 0; xCell <= cells && result.Count < AuthoredFoliageMaximum; xCell++)
            {
                uint hash = StableHash(unchecked((uint)(AuthoredFoliageSeed ^ xCell * 73856093 ^ zCell * 19349663)));
                float x = origin + (xCell + 0.14f + Random01(hash + 1u) * 0.72f) * spacing;
                float z = origin + (zCell + 0.14f + Random01(hash + 2u) * 0.72f) * spacing;
                if (x is < -48f or > 48f || z is < -30f or > 42f) continue;

                // Two authored meadow regions flank the trail. The route, camp clearing and pond
                // stay clear, making the result useful rather than an unconstrained full-map fill.
                float leftRegion = ((x + 19f) * (x + 19f)) / (28f * 28f) + ((z - 7f) * (z - 7f)) / (36f * 36f);
                float rightRegion = ((x - 21f) * (x - 21f)) / (27f * 27f) + ((z - 8f) * (z - 8f)) / (35f * 35f);
                if (leftRegion > 1f && rightRegion > 1f) continue;
                if (MathF.Abs(x) < 4.4f && z is >= -20f and <= 40f) continue;
                if ((x * x) + ((z - 6f) * (z - 6f)) < 8f * 8f) continue;
                if (MathF.Abs(x - 22f) < 8.5f && MathF.Abs(z + 15f) < 7f) continue;
                if (Random01(hash + 3u) > 0.88f) continue;

                float delta = TerrainCellSize;
                float left = SampleTemplateHeight(x - delta, z);
                float right = SampleTemplateHeight(x + delta, z);
                float back = SampleTemplateHeight(x, z - delta);
                float front = SampleTemplateHeight(x, z + delta);
                float normalY = 2f * delta / MathF.Sqrt(
                    ((left - right) * (left - right)) + (4f * delta * delta) + ((back - front) * (back - front)));
                if (MathF.Acos(Math.Clamp(normalY, -1f, 1f)) > 38f * MathF.PI / 180f) continue;

                byte species = Random01(hash + 4u) switch
                {
                    < 0.68f => 0, // MeadowGrass
                    < 0.88f => 1, // TallGrass
                    _ => 5,       // Wildflower
                };
                float baseScale = species == 1 ? 0.95f : species == 5 ? 0.62f : 0.55f;
                result.Add(new FoliageRecord(
                    x,
                    SampleTemplateHeight(x, z),
                    z,
                    baseScale * (0.8f + Random01(hash + 5u) * 0.4f),
                    Random01(hash + 6u) * MathF.Tau,
                    species,
                    0.45f + Random01(hash + 7u) * 0.5f,
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
            writer.Write(0); // FoliagePreset.Meadow
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

    private static JsonObject Point(float x, float z) => Vector(x, SampleTemplateHeight(x, z) + 0.03f, z);

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

    // ── Composed campfire assets ────────────────────────────────────────────────

    private static string CreateCampfireModel(
        ResourceService resources,
        string folder,
        string projectRoot,
        string material)
    {
        string path = resources.CreateResource(folder, ResourceKind.Model, CampfireModelName);
        JsonObject document = new()
        {
            ["schemaVersion"] = 1,
            ["source"] = "Generated",
            ["materials"] = new JsonArray(Relative(projectRoot, material)),
            ["parts"] = new JsonArray(
                ModelPart("Log A", "Cylinder", 0f, 0.28f, 0f, 0f, 0f, 48f, 0.34f, 2.8f, 0.34f, 0.30f, 0.12f, 0.035f),
                ModelPart("Log B", "Cylinder", 0f, 0.28f, 0f, 0f, 0f, -48f, 0.34f, 2.8f, 0.34f, 0.38f, 0.16f, 0.045f),
                ModelPart("Coal Bed", "Sphere", 0f, 0.26f, 0f, 0f, 0f, 0f, 1.2f, 0.38f, 1.2f, 0.34f, 0.08f, 0.025f),
                ModelPart("Flame Core", "Sphere", 0f, 0.92f, 0f, 0f, 0f, 0f, 0.62f, 1.45f, 0.62f, 1f, 0.30f, 0.025f),
                ModelPart("Flame Tip", "Sphere", 0.12f, 1.68f, 0f, 0f, 0f, 0f, 0.36f, 0.82f, 0.36f, 1f, 0.72f, 0.08f)),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static JsonObject ModelPart(
        string name,
        string primitive,
        float x,
        float y,
        float z,
        float rotationX,
        float rotationY,
        float rotationZ,
        float scaleX,
        float scaleY,
        float scaleZ,
        float r,
        float g,
        float b) => new()
    {
        ["name"] = name,
        ["primitive"] = primitive,
        ["position"] = new JsonArray(x, y, z),
        ["rotation"] = new JsonArray(rotationX, rotationY, rotationZ),
        ["scale"] = new JsonArray(scaleX, scaleY, scaleZ),
        ["color"] = new JsonArray(r, g, b),
    };

    private static string CreateCampfireShader(
        ResourceService resources,
        string folder,
        string projectRoot,
        string model)
    {
        string path = resources.CreateResource(folder, ResourceKind.Shader, CampfireShaderName);
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["pipeline"] = "Mesh",
            ["entry"] = "MainPS",
            ["profile"] = "ps_5_0",
            ["source"] = CampfireShaderSource,
            ["previewAsset"] = Relative(projectRoot, model),
            ["parameters"] = new JsonArray(
                ShaderParameter("PulseSpeed", 2.4f),
                ShaderParameter("Glow", 1.35f)),
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

    private static string CreateCampfireParticles(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.Particle, CampfireParticleName);
        JsonObject document = new()
        {
            ["maxParticles"] = 480,
            ["emitRate"] = 72.0,
            ["burstCount"] = 10,
            ["loop"] = true,
            ["shape"] = "Cone",
            ["spreadDegrees"] = 16.0,
            ["emitRadius"] = 0.34,
            ["speed"] = 2.6,
            ["speedVariance"] = 0.42,
            ["gravity"] = -0.35,
            ["drag"] = 0.28,
            ["lifetime"] = 1.65,
            ["lifetimeVariance"] = 0.35,
            ["startSize"] = 0.22,
            ["endSize"] = 0.035,
            ["emissive"] = 2.4,
            ["blendMode"] = "Additive",
            ["startColor"] = Color(1.0, 0.72, 0.18, 1.0),
            ["endColor"] = Color(0.82, 0.08, 0.015, 0.0),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static JsonObject Color(double r, double g, double b, double a) => new()
    {
        ["r"] = r,
        ["g"] = g,
        ["b"] = b,
        ["a"] = a,
    };

    private static string CreateCampfireAudio(
        ResourceService resources,
        string folder,
        string projectRoot)
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
        uint noise = 0xA341316Cu;
        for (int i = 0; i < sampleCount; i++)
        {
            float time = i / (float)sampleRate;
            noise = StableHash(noise + (uint)i + 1u);
            float crackle = ((noise >> 8) * (1f / 16777216f) * 2f - 1f);
            float bed = MathF.Sin(time * MathF.Tau * 72f) * 0.13f
                + MathF.Sin(time * MathF.Tau * 119f) * 0.07f;
            float envelope = 0.72f + 0.18f * MathF.Sin(time * MathF.Tau * 1.7f);
            short sample = (short)(Math.Clamp((bed + crackle * 0.12f) * envelope, -1f, 1f) * short.MaxValue);
            writer.Write(sample);
        }
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
            float pulse = 0.82 + 0.18 * sin(Time * max(PulseSpeed, 0.01) + IN.WorldPos.y * 3.5);
            float3 ember = float3(1.0, 0.34, 0.055) * max(Glow, 0.0) * pulse;
            return float4(saturate(tex.rgb * (0.62 + ember)), tex.a);
        }
        """;

    // ── Prefabs (GameObjects) ───────────────────────────────────────────────────

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
                ["walkSpeed"] = 5.4,
                ["sprintMultiplier"] = 1.75,
                ["groundAcceleration"] = 42.0,
                ["airAcceleration"] = 14.0,
                ["jumpSpeed"] = 6.4,
                ["stepHeight"] = 0.45,
                ["maximumSlopeDegrees"] = 48.0,
                ["swimSpeed"] = 4.6,
                ["swimVerticalSpeed"] = 3.8,
                ["swimAcceleration"] = 18.0,
                ["swimDrag"] = 3.0,
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
        File.WriteAllText(Path.Combine(eventFolder, "Create.pgsl"), CreateEvent);
        File.WriteAllText(Path.Combine(eventFolder, "Step.pgsl"), StepEvent);
        File.WriteAllText(Path.Combine(eventFolder, "Draw.pgsl"), PlayerDrawEvent);
        File.WriteAllText(Path.Combine(eventFolder, "DrawGui.pgsl"), DrawGuiEvent);
        return path;
    }

    private static string CreatePineTree(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.GameObject, PineTreeName);
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["sprite"] = string.Empty,
            ["model"] = string.Empty,
            ["components"] = new JsonArray(new JsonObject
            {
                ["type"] = "ScriptComponent",
                ["enabled"] = true,
                ["props"] = new JsonObject { ["ScriptClass"] = PineTreeName },
            }),
            ["events"] = new JsonArray("Draw"),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, PineTreeName);
        Directory.CreateDirectory(eventFolder);
        File.WriteAllText(Path.Combine(eventFolder, "Draw.pgsl"), PineTreeDrawEvent);
        return path;
    }

    private static string CreateOakTree(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.GameObject, OakTreeName);
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["sprite"] = string.Empty,
            ["model"] = string.Empty,
            ["components"] = new JsonArray(new JsonObject
            {
                ["type"] = "ScriptComponent",
                ["enabled"] = true,
                ["props"] = new JsonObject { ["ScriptClass"] = OakTreeName },
            }),
            ["events"] = new JsonArray("Draw"),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, OakTreeName);
        Directory.CreateDirectory(eventFolder);
        File.WriteAllText(Path.Combine(eventFolder, "Draw.pgsl"), OakTreeDrawEvent);
        return path;
    }

    private static string CreateBoulder(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.GameObject, BoulderName);
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["sprite"] = string.Empty,
            ["model"] = string.Empty,
            ["components"] = new JsonArray(new JsonObject
            {
                ["type"] = "ScriptComponent",
                ["enabled"] = true,
                ["props"] = new JsonObject { ["ScriptClass"] = BoulderName },
            }),
            ["events"] = new JsonArray("Draw"),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, BoulderName);
        Directory.CreateDirectory(eventFolder);
        File.WriteAllText(Path.Combine(eventFolder, "Draw.pgsl"), BoulderDrawEvent);
        return path;
    }

    private static string CreateBeacon(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.GameObject, BeaconName);
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["sprite"] = string.Empty,
            ["model"] = string.Empty,
            ["components"] = new JsonArray(new JsonObject
            {
                ["type"] = "ScriptComponent",
                ["enabled"] = true,
                ["props"] = new JsonObject { ["ScriptClass"] = BeaconName },
            }),
            ["events"] = new JsonArray("Draw"),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, BeaconName);
        Directory.CreateDirectory(eventFolder);
        File.WriteAllText(Path.Combine(eventFolder, "Draw.pgsl"), BeaconDrawEvent);
        return path;
    }

    private static string CreateCampfire(
        ResourceService resources,
        string folder,
        string projectRoot,
        string model,
        string material,
        string shader,
        string particle,
        string audio)
    {
        string modelAsset = Relative(projectRoot, model);
        string materialAsset = Relative(projectRoot, material);
        string shaderAsset = Relative(projectRoot, shader);
        string particleAsset = Relative(projectRoot, particle);
        string audioAsset = Relative(projectRoot, audio);
        string path = resources.CreateResource(folder, ResourceKind.GameObject, CampfireName);
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["sprite"] = string.Empty,
            ["model"] = modelAsset,
            ["material"] = materialAsset,
            ["shader"] = shaderAsset,
            ["physics"] = "StaticSolid",
            ["shaderParameters"] = new JsonObject
            {
                ["PulseSpeed"] = new JsonArray(2.4),
                ["Glow"] = new JsonArray(1.35),
            },
            ["components"] = new JsonArray(
                Component("ModelRendererComponent", new JsonObject
                {
                    ["ModelAsset"] = modelAsset,
                    ["ScaleX"] = 1.0,
                    ["ScaleY"] = 1.0,
                    ["ScaleZ"] = 1.0,
                    ["CastShadows"] = true,
                    ["ReceiveShadows"] = true,
                }),
                Component("ModelAnimatorComponent", new JsonObject
                {
                    ["ClipName"] = "FireIdle",
                    ["ClipFps"] = 30.0,
                    ["Playing"] = true,
                    ["Loop"] = true,
                    ["BlendTime"] = 0.12,
                }),
                Component("MaterialComponent", new JsonObject { ["Asset"] = materialAsset }),
                Component("ShaderComponent", new JsonObject { ["Asset"] = shaderAsset }),
                Component("ParticleComponent", new JsonObject
                {
                    ["Asset"] = particleAsset,
                    ["Emitting"] = true,
                    ["FollowEntity"] = true,
                    ["RateScale"] = 1.0,
                    ["EmitRate"] = 0.0,
                }),
                Component("AudioComponent", new JsonObject
                {
                    ["Asset"] = audioAsset,
                    ["AutoPlay"] = true,
                    ["Spatial"] = true,
                    ["Loop"] = true,
                    ["Volume"] = 0.42,
                    ["Pitch"] = 1.0,
                }),
                Component("PointLightComponent", new JsonObject
                {
                    ["Enabled"] = true,
                    ["Color"] = new JsonArray(1.0, 0.42, 0.08),
                    ["Offset"] = new JsonArray(0.0, 1.15, 0.0),
                    ["Radius"] = 12.0,
                    ["Intensity"] = 3.2,
                }),
                Component("PhysicsComponent", new JsonObject
                {
                    ["Preset"] = "StaticSolid",
                    ["Gravity"] = 0.0,
                    ["GravityDirection"] = 270.0,
                    ["Friction"] = 0.8,
                    ["Solid"] = true,
                }),
                Component("ScriptComponent", new JsonObject { ["ScriptClass"] = CampfireName })),
            ["events"] = new JsonArray("Create", "Step"),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, CampfireName);
        Directory.CreateDirectory(eventFolder);
        File.WriteAllText(Path.Combine(eventFolder, "Create.pgsl"), CampfireCreateEvent);
        File.WriteAllText(Path.Combine(eventFolder, "Step.pgsl"), CampfireStepEvent);
        return path;
    }

    private static JsonObject Component(string type, JsonObject props) => new()
    {
        ["id"] = $"cmp-{Guid.NewGuid():N}",
        ["type"] = type,
        ["enabled"] = true,
        ["props"] = props,
    };

    // ── Room ────────────────────────────────────────────────────────────────────

    private static void CreateRoom(
        ResourceService resources,
        string folder,
        string projectRoot,
        string player,
        string terrain,
        string pineTree,
        string oakTree,
        string boulder,
        string beacon,
        string campfire)
    {
        string path = resources.CreateResource(folder, ResourceKind.Room, RoomName);

        JsonArray nodes =
        [
            new JsonObject
            {
                ["id"] = NewId(),
                ["kind"] = "Terrain",
                ["name"] = "Ground",
                ["enabled"] = true,
                ["enabledIn2D"] = false,
                ["enabledIn3D"] = true,
                ["layerId"] = "environment",
                ["transform"] = Transform(0, 0, 0),
                ["terrain"] = new JsonObject
                {
                    ["asset"] = Relative(projectRoot, terrain),
                    ["albedo"] = "Assets/Sprites/GroundTexture.image.json",
                    ["uvScale"] = 0.08,
                },
            },
            MakeNode(projectRoot, player, "Player 1", 0, SampleTemplateHeight(0, -10), -10, "gameplay"),
            MakeNode(projectRoot, campfire, "Trail Campfire", 0, SampleTemplateHeight(0, 6), 6, "gameplay"),

            // Pine tree groves
            MakeNode(projectRoot, pineTree, "Pine Tree 1", -10, SampleTemplateHeight(-10, 12), 12, "environment"),
            MakeNode(projectRoot, pineTree, "Pine Tree 2", 0, SampleTemplateHeight(0, 18), 18, "environment"),
            MakeNode(projectRoot, pineTree, "Pine Tree 3", 12, SampleTemplateHeight(12, 14), 14, "environment"),
            MakeNode(projectRoot, pineTree, "Pine Tree 4", 16, SampleTemplateHeight(16, 0), 0, "environment"),
            MakeNode(projectRoot, pineTree, "Pine Tree 5", 14, SampleTemplateHeight(14, -14), -14, "environment"),
            MakeNode(projectRoot, pineTree, "Pine Tree 6", -4, SampleTemplateHeight(-4, -18), -18, "environment"),
            MakeNode(projectRoot, pineTree, "Pine Tree 7", -16, SampleTemplateHeight(-16, -4), -4, "environment"),

            // Broadleaf oak trees in meadow
            MakeNode(projectRoot, oakTree, "Oak Tree 1", 7, SampleTemplateHeight(7, 4), 4, "environment"),
            MakeNode(projectRoot, oakTree, "Oak Tree 2", -7, SampleTemplateHeight(-7, -2), -2, "environment"),

            // Granite boulders
            MakeNode(projectRoot, boulder, "Boulder 1", 4, SampleTemplateHeight(4, 10), 10, "environment"),
            MakeNode(projectRoot, boulder, "Boulder 2", -5, SampleTemplateHeight(-5, 4), 4, "environment"),
            MakeNode(projectRoot, boulder, "Boulder 3", 10, SampleTemplateHeight(10, -6), -6, "environment"),

            // Lookout beacon on ridge
            MakeNode(projectRoot, beacon, "Lookout Beacon", 0, SampleTemplateHeight(0, 32), 32, "environment"),
        ];

        JsonObject room = new()
        {
            ["$schema"] = "genesis.room",
            ["version"] = 1,
            ["id"] = NewId(),
            ["name"] = RoomName,
            ["dimension"] = 1,
            ["settings"] = new JsonObject
            {
                ["width"] = 1280,
                ["height"] = 720,
                ["depth"] = 720,
                ["targetFps"] = 60,
                ["fixedFps"] = 60,
                ["captureMouse"] = true,
                ["gridSize"] = 1,
                ["snapEnabled"] = true,
            },
            ["environment"] = new JsonObject
            {
                ["backgroundColor"] = new JsonArray(0.35, 0.58, 0.88, 1.0),
                ["gravity"] = new JsonArray(0, -9.81, 0),
                ["ambientIntensity"] = 0.92,
                ["fogDensity"] = 0.003,
                ["dynamicSky"] = true,
                ["weather"] = "Clear",
                ["timeOfDayHours"] = 17.5,
                ["timeScale"] = 18.0,
                ["automaticWeather"] = false,
                ["atmospherePreset"] = "Natural",
                ["volumetricClouds"] = true,
                ["cloudQuality"] = 2,
            },
            ["layers"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "environment",
                    ["name"] = "Environment",
                    ["order"] = 0,
                    ["enabled"] = true,
                },
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

    private static JsonObject MakeNode(
        string projectRoot, string prefabPath, string name, float x, float y, float z, string layerId) => new()
    {
        ["id"] = NewId(),
        ["kind"] = "GameObject",
        ["name"] = name,
        ["enabled"] = true,
        ["enabledIn2D"] = false,
        ["enabledIn3D"] = true,
        ["layerId"] = layerId,
        ["transform"] = Transform(x, y, z),
        ["gameObject"] = new JsonObject
        {
            ["prefab"] = Relative(projectRoot, prefabPath),
        },
    };

    // ── Gameplay Scripts (PGSL) ─────────────────────────────────────────────────

    private const string CreateEvent = """
        // ── 1st Person 3D Nature Explorer ──────────────────────────────────────────
        eyeHeight = 1.7;

        walkSpeed = 0.16;
        runSpeed = 0.30;
        jumpPower = 0.32;
        gravityStep = 0.016;
        verticalSpeed = 0;
        onGround = 1;

        // Look angles in degrees. Yaw 0 looks down +Z.
        lookYaw = 0;
        lookPitch = -4;
        lookSensitivity = 0.16;

        x = 0;
        z = -10;
        y = GetTerrainHeight(x, z);

        SetMouseCaptured(true);

        // Footstep dust particles while sprinting
        dust = ParticleCreate(180, 20, 1.2);

        showHelp = 1;
        helpTimer = 0;
        inWater = 0;
        travelState = "ON TRAIL";

        SetCameraPosition(x, y + eyeHeight, z);
        SetCameraTarget(
            x + ForwardX(lookYaw, lookPitch),
            y + eyeHeight + ForwardY(lookYaw, lookPitch),
            z + ForwardZ(lookYaw, lookPitch));
        """;

    private const string StepEvent = """
        // ── Mouse look ───────────────────────────────────────────────────────────
        lookYaw = lookYaw + (GetMouseLookDeltaX() * lookSensitivity);
        lookPitch = Clamp(lookPitch - (GetMouseLookDeltaY() * lookSensitivity), -85, 85);
        lookYaw = AngleNormalise(lookYaw);

        var forwardX = ForwardX(lookYaw, 0);
        var forwardZ = ForwardZ(lookYaw, 0);
        var rightX = RightX(lookYaw);
        var rightZ = RightZ(lookYaw);

        // ── WASD Movement ────────────────────────────────────────────────────────
        var moveF = 0;
        var moveR = 0;
        if (KeyCheck("W") || KeyCheck("Up")) { moveF = moveF + 1; }
        if (KeyCheck("S") || KeyCheck("Down")) { moveF = moveF - 1; }
        if (KeyCheck("D") || KeyCheck("Right")) { moveR = moveR + 1; }
        if (KeyCheck("A") || KeyCheck("Left")) { moveR = moveR - 1; }

        var currentSpeed = walkSpeed;
        if (KeyCheck("Shift")) { currentSpeed = runSpeed; }

        var diagonal = 1;
        if (moveF != 0 && moveR != 0) { diagonal = 0.70710678; }

        var moving = 0;
        if (moveF != 0 || moveR != 0) { moving = 1; }

        var deltaX = ((forwardX * moveF) + (rightX * moveR)) * currentSpeed * diagonal;
        var deltaZ = ((forwardZ * moveF) + (rightZ * moveR)) * currentSpeed * diagonal;

        var nextX = Clamp(x + deltaX, -45, 45);
        var nextZ = Clamp(z + deltaZ, -45, 45);

        // 3D obstacle collision: slide smoothly along trees, boulders, and obstacles
        if (PlaceFree3D(nextX, z, 0.8)) {
            x = nextX;
        }
        if (PlaceFree3D(x, nextZ, 0.8)) {
            z = nextZ;
        }

        // ── Terrain Height Snapping & Jumping ────────────────────────────────────
        var ground = GetTerrainHeight(x, z);
        inWater = WaterIsSwimmable(x, y + 0.5, z);
        travelState = "ON TRAIL";
        if (inWater) { travelState = "SWIMMING"; }

        if (onGround == 1) {
            y = ground;
            verticalSpeed = 0;
            if (KeyPressed("Space")) {
                verticalSpeed = jumpPower;
                onGround = 0;
                y = y + verticalSpeed;
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

        // ── Dust Particles ───────────────────────────────────────────────────────
        ParticleSetCameraPosition(x, y + eyeHeight, z);
        if (moving == 1 && onGround == 1 && KeyCheck("Shift")) {
            ParticleSetRate(dust, 28);
        } else {
            ParticleSetRate(dust, 0);
        }
        ParticleUpdate(dust, DeltaTime);

        // ── 1st Person Camera ────────────────────────────────────────────────────
        var eyeX = x;
        var eyeY = y + eyeHeight;
        var eyeZ = z;
        SetCameraPosition(eyeX, eyeY, eyeZ);
        SetCameraTarget(
            eyeX + ForwardX(lookYaw, lookPitch),
            eyeY + ForwardY(lookYaw, lookPitch),
            eyeZ + ForwardZ(lookYaw, lookPitch));

        if (helpTimer < 600) { helpTimer = helpTimer + 1; }
        if (helpTimer >= 600) { showHelp = 0; }
        if (KeyPressed("H")) { showHelp = 1 - showHelp; helpTimer = 0; }
        """;

    private const string DrawGuiEvent = """
        // ── 1st Person Reticle ───────────────────────────────────────────────────
        var cx = RoomGetWidth() / 2;
        var cy = RoomGetHeight() / 2;
        DrawSetAlpha(0.65);
        DrawSetColorRgb(255, 255, 255);
        DrawRectangle(cx - 5, cy - 1, cx + 5, cy + 1);
        DrawRectangle(cx - 1, cy - 5, cx + 1, cy + 5);

        // ── Top Status Card ──────────────────────────────────────────────────────
        DrawSetAlpha(0.84);
        DrawSetColorRgb(10, 16, 26);
        DrawRectangle(16, 16, 340, 78);
        DrawSetAlpha(1);
        DrawSetColorRgb(236, 243, 255);
        DrawTextScaled(28, 26, "3D WORLD EXPLORER", 20);

        DrawSetColorRgb(130, 195, 235);
        DrawTextScaled(28, 52, StringJoin2("POS  ", StringJoin2(StringOf(Floor(x)), StringJoin2(", ", StringJoin2(StringOf(Floor(y)), StringJoin2(", ", StringOf(Floor(z))))))), 13);
        DrawSetColorRgb(170, 220, 140);
        DrawTextScaled(240, 52, StringJoin2("FPS  ", StringOf(Floor(Fps))), 13);

        DrawSetColorRgb(255, 178, 72);
        DrawTextScaled(370, 26, travelState, 16);

        // ── Controls Guide ───────────────────────────────────────────────────────
        if (showHelp == 1) {
            DrawSetAlpha(0.84);
            DrawSetColorRgb(10, 16, 26);
            DrawRectangle(16, 96, 340, 224);
            DrawSetAlpha(1);
            DrawSetColorRgb(214, 228, 246);
            DrawTextScaled(28, 106, "WASD / arrows   move", 15);
            DrawTextScaled(28, 128, "Mouse           look around", 15);
            DrawTextScaled(28, 150, "Shift           sprint (kicks dust)", 15);
            DrawTextScaled(28, 172, "Space           jump", 15);
            DrawTextScaled(28, 194, "H               toggle help", 15);
        }
        """;

    private const string PlayerDrawEvent = """
        // 1st Person player ground shadow
        DrawSetColorRgb(20, 25, 20);
        DrawSetAlpha(0.35);
        DrawBox3D(x, y + 0.02, z, 0.6, 0.02, 0.6);
        DrawSetAlpha(1.0);
        """;

    private const string PineTreeDrawEvent = """
        // Pine Tree: trunk and tiered evergreen canopies
        DrawSetColorRgb(102, 68, 38);
        DrawBox3D(x, y + 1.2, z, 0.4, 2.4, 0.4);
        DrawSetColorRgb(34, 82, 48);
        DrawBox3D(x, y + 2.6, z, 2.8, 1.2, 2.8);
        DrawSetColorRgb(44, 98, 58);
        DrawBox3D(x, y + 3.6, z, 2.0, 1.0, 2.0);
        DrawSetColorRgb(56, 118, 70);
        DrawBox3D(x, y + 4.5, z, 1.2, 0.9, 1.2);
        DrawSetColorRgb(68, 138, 82);
        DrawBox3D(x, y + 5.1, z, 0.5, 0.6, 0.5);
        """;

    private const string OakTreeDrawEvent = """
        // Broadleaf Oak: trunk and layered leafy foliage
        DrawSetColorRgb(92, 60, 32);
        DrawBox3D(x, y + 1.4, z, 0.6, 2.8, 0.6);
        DrawSetColorRgb(46, 126, 58);
        DrawSphere3D(x, y + 3.6, z, 2.1);
        DrawSetColorRgb(58, 144, 72);
        DrawSphere3D(x + 0.5, y + 4.2, z + 0.4, 1.4);
        DrawSetColorRgb(68, 156, 82);
        DrawSphere3D(x - 0.6, y + 3.9, z - 0.5, 1.3);
        """;

    private const string BoulderDrawEvent = """
        // Granite Boulder: mossy rock formation
        DrawSetColorRgb(112, 120, 130);
        DrawBox3D(x, y + 0.45, z, 1.6, 0.9, 1.4);
        DrawSetColorRgb(98, 106, 116);
        DrawSphere3D(x + 0.3, y + 0.6, z - 0.2, 0.75);
        """;

    private const string BeaconDrawEvent = """
        // Scenic Lookout Beacon on ridge
        DrawSetColorRgb(140, 144, 155);
        DrawPillar3D(x, y, z, 0.9, 5.0);
        DrawSetColorRgb(255, 218, 80);
        DrawSphere3D(x, y + 5.8, z, 0.65);
        """;

    private const string CampfireCreateEvent = """
        // Component-owned light, audio, particles and shader run automatically. PGSL owns the
        // gameplay-facing pulse state so the composed Object remains scriptable like any other.
        fireAge = 0;
        firePulse = 1;
        """;

    private const string CampfireStepEvent = """
        fireAge = fireAge + DeltaTime;
        firePulse = 0.85 + 0.15 * Sin(fireAge * 4.8);
        """;

    // ── Helpers ─────────────────────────────────────────────────────────────────

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
            .Replace('\\', '/');

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

    private static JsonObject Transform(float x, float y, float z) => new()
    {
        ["position"] = new JsonArray(x, y, z),
        ["rotation"] = new JsonArray(0, 0, 0),
        ["scale"] = new JsonArray(1, 1, 1),
    };

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string Relative(string projectRoot, string fullPath) =>
        Path.GetRelativePath(projectRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
