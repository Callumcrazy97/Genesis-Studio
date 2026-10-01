using System.Diagnostics;
using System.Drawing;
using System.Numerics;
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
