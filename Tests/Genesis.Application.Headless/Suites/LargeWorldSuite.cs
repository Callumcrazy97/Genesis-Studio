using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Runtime;
using Genesis.Physics;
using Genesis.Rendering.Core;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Large worlds: an 8 km terrain must load quickly, draw with distance-dependent detail and keep
/// collision only where something can touch it.
/// </summary>
internal static class LargeWorldSuite
{
    private const int Resolution = 2049;       // 2048 cells at 4 m = 8192 m
    private const float CellSize = 4f;
    private static TerrainAsset? _island;

    private static TerrainAsset Island => _island ??= RuntimeViewportHarness.BuildIslandTerrain(Resolution, CellSize);

    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Large worlds");

        HeadlessHarness.RunCase(context.Report, "Engine.World.Terrain.LargeAssetSavesAndLoadsInBulk", () =>
        {
            TerrainAsset terrain = Island;
            string file = Path.Combine(context.Workspace, "large-island.gterrain");
            Directory.CreateDirectory(context.Workspace);
            Stopwatch watch = Stopwatch.StartNew();
            terrain.Save(file);
            long saveMs = watch.ElapsedMilliseconds;
            watch.Restart();
            TerrainAsset loaded = TerrainAsset.Load(file);
            long loadMs = watch.ElapsedMilliseconds;
            HeadlessHarness.Assert(loaded.ResolutionX == Resolution && loaded.ResolutionZ == Resolution
                && loaded.CellSize == CellSize && loaded.MinHeight == terrain.MinHeight && loaded.MaxHeight == terrain.MaxHeight,
                "The terrain header did not round-trip.");
            HeadlessHarness.Assert(loaded.HeightsData.AsSpan().SequenceEqual(terrain.HeightsData)
                && loaded.SplatmapData.AsSpan().SequenceEqual(terrain.SplatmapData),
                "Heights or paint changed across save and load.");
            HeadlessHarness.Assert(new FileInfo(file).Length == 36L + (long)Resolution * Resolution * 6,
                "The .gterrain layout changed size; existing terrains would no longer load.");
            // 4.2 million samples: a sample-at-a-time reader took seconds here.
            HeadlessHarness.Assert(loadMs < 1500 && saveMs < 1500,
                $"An 8 km terrain took {saveMs} ms to save and {loadMs} ms to load.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.World.Terrain.ColliderTilesFollowWhatCanTouchTheGround", () =>
        {
            TerrainAsset terrain = Island;
            using PhysicsWorld physics = PhysicsWorld.Create(new PhysicsWorldAsset());
            using EcsWorld world = new();
            using TerrainColliderTiles tiles = new(terrain, Vector3.One, Vector3.Zero, Quaternion.Identity, "Terrain:test");
            Vector3 first = new(300f, 0f, -150f);
            TerrainColliderFocus[] focus = [new TerrainColliderFocus(first, 160f)];

            // The tile under a focus is registered by the first update, with no waiting.
            tiles.Update(physics, focus);
            float expected = terrain.SampleHeight(first.X, first.Z);
            HeadlessHarness.Assert(physics.RaycastDown(world, new Vector3(first.X, 2000f, first.Z), 4000f, out PhysicsRaycastHit hit)
                && MathF.Abs(hit.Point.Y - expected) < 0.5f,
                $"No ground under the focus after one update (hit {hit.Point.Y:F2}, terrain {expected:F2}).");

            tiles.RegisterImmediately(physics, focus);
            int near = tiles.ResidentTiles;
            // 160 m around a point touches at most a 3 x 3 block of 256 m tiles.
            HeadlessHarness.Assert(near is >= 1 and <= 9 && physics.ExternalStaticCount == near,
                $"{near} collision tiles resident around one point ({physics.ExternalStaticCount} registered).");
            Vector3 edge = first + new Vector3(140f, 0f, 0f);
            HeadlessHarness.Assert(physics.RaycastDown(world, new Vector3(edge.X, 2000f, edge.Z), 4000f, out hit)
                && MathF.Abs(hit.Point.Y - terrain.SampleHeight(edge.X, edge.Z)) < 0.5f,
                "Ground is missing inside the requested radius.");
            HeadlessHarness.Assert(!physics.RaycastDown(world, new Vector3(3000f, 2000f, 3000f), 4000f, out _),
                "Collision exists three kilometres from anything that could touch it.");

            // Everything moves away: the old tiles are dropped once nothing has needed them for a while.
            Vector3 second = new(-2600f, 0f, 2100f);
            focus[0] = new TerrainColliderFocus(second, 160f);
            for (int tick = 0; tick < 300; tick++) tiles.Update(physics, focus);
            tiles.RegisterImmediately(physics, focus);
            HeadlessHarness.Assert(tiles.ResidentTiles <= 9 && physics.ExternalStaticCount == tiles.ResidentTiles,
                $"Tiles were not released after the focus moved ({tiles.ResidentTiles} resident, {physics.ExternalStaticCount} registered).");
            HeadlessHarness.Assert(!physics.RaycastDown(world, new Vector3(first.X, 2000f, first.Z), 4000f, out _)
                && physics.RaycastDown(world, new Vector3(second.X, 2000f, second.Z), 4000f, out hit)
                && MathF.Abs(hit.Point.Y - terrain.SampleHeight(second.X, second.Z)) < 0.5f,
                "Collision did not follow the focus to its new position.");

            tiles.Clear(physics);
            HeadlessHarness.Assert(physics.ExternalStaticCount == 0, "Clearing the tiles left collision registered.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.World.Terrain.EightKilometresDrawWithDistanceDetail", () =>
        {
            TerrainAsset terrain = Island;
            HeadlessHarness.Assert(TerrainLodGround.Applies(terrain), "A 2048-cell terrain should use level of detail.");
            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            try
            {
                RenderBackendSelection.Configure(RenderBackendOption.SilkNetDx11);
                using RuntimeViewportHarness harness = new(1280, 720);
                int fullTriangles = (Resolution - 1) * (Resolution - 1) * 2;
                Color sky = Color.FromArgb(140, 184, 235);

                // A vista from a headland: near ground at full detail, mountains kilometres away.
                Vector3 vistaEye = new(-2600f, terrain.SampleHeight(-2600f, -2600f) + 60f, -2600f);
                Vector3 peak = Highest(terrain);
                string vistaFile = Path.Combine(context.Captures, "large-terrain-vista.png");
                var vista = harness.CaptureLargeTerrain(vistaFile, terrain, vistaEye, peak);
                HeadlessHarness.Assert(vista.Lod.FinestLevelDrawn == 0 && vista.Lod.CoarsestLevelDrawn >= 3,
                    $"The vista should mix full detail with coarse distance nodes (levels {vista.Lod.FinestLevelDrawn}..{vista.Lod.CoarsestLevelDrawn}).");
                HeadlessHarness.Assert(vista.Lod.NodesDrawn is > 8 and < 400 && vista.Lod.TrianglesDrawn < fullTriangles / 4,
                    $"The vista drew {vista.Lod.NodesDrawn} nodes and {vista.Lod.TrianglesDrawn:N0} triangles; every cell would be {fullTriangles:N0}.");
                using (Bitmap image = new(vistaFile))
                {
                    Point peakPixel = harness.ProjectLargeTerrainPoint(peak - new Vector3(0f, 25f, 0f));
                    Color at = image.GetPixel(Math.Clamp(peakPixel.X, 0, image.Width - 1), Math.Clamp(peakPixel.Y, 0, image.Height - 1));
                    HeadlessHarness.Assert(!Near(at, sky, 14),
                        $"The peak {Vector3.Distance(vistaEye, peak):F0} m away is not drawn (pixel {at} at {peakPixel}).");
                }

                // Straight down from high above: the whole island is in view and has no holes.
                string overviewFile = Path.Combine(context.Captures, "large-terrain-overview.png");
                var overview = harness.CaptureLargeTerrain(overviewFile, terrain, new Vector3(0f, 7000f, -40f), Vector3.Zero);
                HeadlessHarness.Assert(overview.Lod.TrianglesDrawn < fullTriangles / 8,
                    $"From 7 km up the terrain still drew {overview.Lod.TrianglesDrawn:N0} triangles.");
                using (Bitmap image = new(overviewFile))
                {
                    int holes = 0, samples = 0;
                    for (float wz = -3900f; wz <= 3900f; wz += 130f)
                    for (float wx = -3900f; wx <= 3900f; wx += 130f)
                    {
                        Point pixel = harness.ProjectLargeTerrainPoint(new Vector3(wx, terrain.SampleHeight(wx, wz), wz));
                        if (pixel.X < 2 || pixel.Y < 40 || pixel.X >= image.Width - 2 || pixel.Y >= image.Height - 2) continue;
                        samples++;
                        if (Near(image.GetPixel(pixel.X, pixel.Y), sky, 10)) holes++;
                    }

                    HeadlessHarness.Assert(samples > 1500 && holes == 0,
                        $"{holes} of {samples} points on the terrain show the sky through it (a gap between detail levels).");
                }

                // Standing on the ground, half the detail setting must cost fewer triangles.
                Vector3 groundEye = new(400f, terrain.SampleHeight(400f, 300f) + 2f, 300f);
                string groundFile = Path.Combine(context.Captures, "large-terrain-ground.png");
                var ground = harness.CaptureLargeTerrain(groundFile, terrain, groundEye, peak);
                var groundLow = harness.CaptureLargeTerrain(
                    Path.Combine(context.Captures, "large-terrain-ground-low.png"), terrain, groundEye, peak, detail: 0.5f);
                HeadlessHarness.Assert(ground.Lod.FinestLevelDrawn == 0 && groundLow.Lod.TrianglesDrawn < ground.Lod.TrianglesDrawn,
                    $"Lower detail did not reduce the triangles ({groundLow.Lod.TrianglesDrawn:N0} vs {ground.Lod.TrianglesDrawn:N0}).");
                HeadlessHarness.Assert(ground.Lod.MeshesResident < 500,
                    $"{ground.Lod.MeshesResident} terrain meshes are resident; the cache is not releasing.");
            }
            finally { RenderBackendSelection.Configure(previous); }
        });

        RunGeneration(context);
        LargeWorldDetailSuite.Run(context);
        LargeWorldContentSuite.Run(context);
        NetworkReplicationSuite.Run(context);
        EngineAdditionsSuite.Run(context);
        RoomChangeSuite.Run(context);
    }

    /// <summary>A 2 km island recipe using every world command.</summary>
    private const string IslandRecipe = """
        TerrainSize(2048, 2048);
        TerrainSpacing(2);
        TerrainHeights(-40, 300);
        Ocean(0);
        Erode(0.4);
        Rivers(3, 0.25);
        PaintNatural(3, 170, 34);
        Scatter("Test Pine", 180, 4, 150, 28, 1, 4, 60);
        Sites(3, 4, 60, 40);
        SitePlace("Test House", 6, 8, 36, 12);
        SitePaint(3);

        coast = length(x, z) / 1024 + 0.18 * fbm(x * 0.002, z * 0.002, 4);
        land = smoothstep(1.0, 0.55, coast);
        height = -30 + land * (38 + 26 * fbm(x * 0.004, z * 0.004, 5))
               + land * land * 190 * ridge(x * 0.0022, z * 0.0022, 6);
        """;

    public static void RunGeneration(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Editor.Terrain.WorldRecipe.MakesSeaRiversPaintForestAndSites", () =>
        {
            var recipe = new TerrainCreationRecipe { Source = TerrainCreationSource.Code, Surface = TerrainCodeSurface.Heightfield, Code = IslandRecipe, Seed = 2024 };
            HeadlessHarness.Assert(TerrainWorldGenerator.Handles(recipe), "A code heightfield recipe should be generated as a world.");
            TerrainWorldResult world = TerrainWorldGenerator.Generate(recipe);
            TerrainAsset terrain = world.Terrain;
            HeadlessHarness.Assert(terrain.ResolutionX == 1025 && terrain.ResolutionZ == 1025 && terrain.CellSize == 2f,
                $"The recipe asked for 2048 m at 2 m; got {terrain.ResolutionX} x {terrain.ResolutionZ} at {terrain.CellSize} m (the preview's 384-cell limit must not apply).");
            HeadlessHarness.Assert(world.Elapsed.TotalSeconds < 60, $"A one-million-sample world took {world.Elapsed.TotalSeconds:F1} s.");

            // The sea, and land that rises out of it.
            TerrainWaterDefinition? sea = world.Water.SingleOrDefault(water => water.Kind == TerrainWaterKind.Ocean);
            HeadlessHarness.Assert(sea is { SurfaceHeight: 0f } && terrain.GetHeight(2, 2) < 0f && terrain.SampleHeight(0f, 0f) > 5f,
                "The island should have a sea at height 0 with its edge under water and its middle above it.");

            // Rivers run downhill to the sea, in a channel cut below their own surface.
            TerrainWaterDefinition[] rivers = world.Water.Where(water => water.Kind == TerrainWaterKind.River).ToArray();
            HeadlessHarness.Assert(rivers.Length >= 2, $"Expected rivers; found {rivers.Length}.");
            foreach (TerrainWaterDefinition river in rivers)
            {
                for (int i = 1; i < river.RiverPoints.Count; i++)
                    HeadlessHarness.Assert(river.RiverPoints[i].Position.Y <= river.RiverPoints[i - 1].Position.Y + 0.02f,
                        $"{river.Name} runs uphill at point {i} ({river.RiverPoints[i - 1].Position.Y:F2} to {river.RiverPoints[i].Position.Y:F2}).");
                var middle = river.RiverPoints[river.RiverPoints.Count / 2];
                HeadlessHarness.Assert(terrain.SampleHeight(middle.Position.X, middle.Position.Z) < middle.Position.Y + 0.35f,
                    $"{river.Name}'s water at {middle.Position} is not in a channel (ground {terrain.SampleHeight(middle.Position.X, middle.Position.Z):F2}).");
            }

            float lowestRiver = rivers.Min(river => river.RiverPoints[^1].Position.Y);
            HeadlessHarness.Assert(lowestRiver < 3f, $"No river reaches the sea (lowest river end is at {lowestRiver:F1} m).");

            // Paint follows the land: sand at the shore, rock on steep ground, grass on gentle slopes.
            int sand = 0, shore = 0, steep = 0, rocky = 0, gentle = 0, grassy = 0;
            float highest = float.MinValue;
            for (int z = 3; z < terrain.ResolutionZ - 3; z += 5)
            for (int x = 3; x < terrain.ResolutionX - 3; x += 5)
            {
                float height = terrain.GetHeight(x, z);
                highest = MathF.Max(highest, height);
                float dx = (terrain.GetHeight(x + 1, z) - terrain.GetHeight(x - 1, z)) / (2f * terrain.CellSize);
                float dz = (terrain.GetHeight(x, z + 1) - terrain.GetHeight(x, z - 1)) / (2f * terrain.CellSize);
                float slope = MathF.Atan(MathF.Sqrt(dx * dx + dz * dz)) * 180f / MathF.PI;
                (byte grass, byte rock, byte sandWeight, byte snow) = terrain.GetSplat(x, z);
                if (height is > 0.2f and < 1.5f && slope < 20f) { shore++; if (sandWeight > 128) sand++; }
                if (height > 12f && slope > 50f) { steep++; if (rock + snow > 128) rocky++; }
                if (height is > 12f and < 120f && slope < 14f) { gentle++; if (grass > 128) grassy++; }
                HeadlessHarness.Assert(Math.Abs(grass + rock + sandWeight + snow - 255) <= 3, "Paint weights do not sum to one.");
            }

            HeadlessHarness.Assert(highest > 60f, $"The island has no relief (highest point {highest:F1} m).");
            HeadlessHarness.Assert(shore > 20 && sand > shore * 0.8 && steep > 5 && rocky > steep * 0.8 && gentle > 50 && grassy > gentle * 0.7,
                $"Paint does not follow the land (sand on {sand}/{shore} shore samples, rock on {rocky}/{steep} steep samples, "
                + $"grass on {grassy}/{gentle} gentle samples; highest point {highest:F1} m).");

            // Level sites with their objects, and the forest rule.
            HeadlessHarness.Assert(world.PointsOfInterest.Count == 3 && world.Placed.Count == 18
                && world.Placed.All(placed => placed.Entity == "Test House"),
                $"Expected 3 sites with 6 objects each; got {world.PointsOfInterest.Count} sites and {world.Placed.Count} objects.");
            foreach (TerrainPointOfInterest site in world.PointsOfInterest)
            {
                float low = float.MaxValue, top = float.MinValue;
                for (float angle = 0; angle < MathF.Tau; angle += 0.4f)
                {
                    float sample = terrain.SampleHeight(site.Position.X + MathF.Cos(angle) * 25f, site.Position.Z + MathF.Sin(angle) * 25f);
                    low = MathF.Min(low, sample); top = MathF.Max(top, sample);
                }

                HeadlessHarness.Assert(top - low < 1.5f && low > 2f, $"{site.Name} is not level dry ground ({low:F1} to {top:F1} m within 25 m).");
                (_, _, byte paved, _) = terrain.GetSplat(
                    (int)((site.Position.X - terrain.OriginX) / terrain.CellSize), (int)((site.Position.Z - terrain.OriginZ) / terrain.CellSize));
                HeadlessHarness.Assert(paved > 150, $"{site.Name} was not painted with layer 3.");
            }

            foreach (TerrainPlacedEntity placed in world.Placed)
                HeadlessHarness.Assert(MathF.Abs(placed.Position.Y - terrain.SampleHeight(placed.Position.X, placed.Position.Z)) < 0.3f,
                    "A placed object is not on the ground.");
            TerrainScatterLayer forest = world.Scatter.Single();
            HeadlessHarness.Assert(forest is { Model: "Test Pine", DensityPerHectare: 180f, PaintLayerMask: 1, ClumpSize: 60f },
                "The Scatter command did not become a scatter layer with its settings.");

            // The same recipe and seed give the same world, sample for sample.
            TerrainWorldResult again = TerrainWorldGenerator.Generate(recipe);
            HeadlessHarness.Assert(again.Terrain.HeightsData.AsSpan().SequenceEqual(terrain.HeightsData)
                && again.Terrain.SplatmapData.AsSpan().SequenceEqual(terrain.SplatmapData),
                "Generating the same recipe twice gave different terrain.");
        });
    }

    private static Vector3 Highest(TerrainAsset terrain)
    {
        int best = 0;
        ushort[] heights = terrain.HeightsData;
        for (int i = 1; i < heights.Length; i++) if (heights[i] > heights[best]) best = i;
        int x = best % terrain.ResolutionX, z = best / terrain.ResolutionX;
        return new Vector3(terrain.OriginX + x * terrain.CellSize, terrain.GetHeight(x, z), terrain.OriginZ + z * terrain.CellSize);
    }

    private static bool Near(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;
}
