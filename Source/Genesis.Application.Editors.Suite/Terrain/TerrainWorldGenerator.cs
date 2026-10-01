using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Genesis.World.Generation;
using Genesis.World.Terrain;
using Genesis.World.Water;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>Natural painting thresholds requested by a recipe's <c>PaintNatural</c> command.</summary>
public sealed record TerrainWorldPaint(float BeachHeight, float SnowHeight, float RockSlopeDegrees);

/// <summary>A terrain layer named by a recipe's <c>Layer</c> command.</summary>
public sealed record TerrainWorldLayer(string Name, string Image, float TileMetres);

/// <summary>Objects to arrange around each site of a <c>Sites</c> command.</summary>
public sealed record TerrainWorldPlacement(string Entity, int Count, float InnerRadius, float OuterRadius, float Spacing);

/// <summary>A group of level sites requested by a recipe's <c>Sites</c> command.</summary>
public sealed class TerrainWorldSites
{
    public int Count { get; set; }
    public float MinimumHeight { get; set; }
    public float MaximumHeight { get; set; }
    public float Radius { get; set; }
    public float Separation { get; set; }
    /// <summary>Layer (1 to 4) painted under each site; 0 leaves the ground as it is.</summary>
    public int PaintSlot { get; set; }
    public List<TerrainWorldPlacement> Placements { get; } = [];
}

/// <summary>
/// The whole-terrain steps a recipe asked for with its set-up commands. Per-sample code shapes the
/// land; these steps need the finished land (water must know where downhill is).
/// </summary>
public sealed class TerrainWorldPlan
{
    public float? SeaLevel { get; set; }
    public float ErosionStrength { get; set; }
    public int RiverCount { get; set; }
    public float RiverBasinSquareKilometres { get; set; } = 1.5f;
    public TerrainWorldPaint? Paint { get; set; }
    public TerrainWorldLayer?[] Layers { get; } = new TerrainWorldLayer?[4];
    public List<TerrainScatterLayer> Scatter { get; } = [];
    public List<TerrainWorldSites> Sites { get; } = [];

    /// <summary>True when the recipe uses anything beyond size, spacing and per-sample heights.</summary>
    public bool HasSteps => SeaLevel.HasValue || ErosionStrength > 0 || RiverCount > 0 || Paint != null
        || Layers.Any(layer => layer != null) || Scatter.Count > 0 || Sites.Count > 0;
}

/// <summary>Everything a world recipe produced, ready to become a terrain resource.</summary>
public sealed class TerrainWorldResult
{
    public required TerrainCreationRecipe Recipe { get; init; }
    public required TerrainAsset Terrain { get; init; }
    public required TerrainWorldPlan Plan { get; init; }
    public List<TerrainWaterDefinition> Water { get; } = [];
    public List<TerrainScatterLayer> Scatter { get; } = [];
    public List<TerrainPlacedEntity> Placed { get; } = [];
    public List<TerrainPointOfInterest> PointsOfInterest { get; } = [];
    public string Summary { get; set; } = "";
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Runs a terrain recipe at its full requested size. The wizard's preview stops at 384 cells a
/// side; this evaluates every sample (on all cores) and then carries out the recipe's whole-terrain
/// steps: erosion, rivers, the sea, natural painting, scattered models and level sites.
/// </summary>
public static class TerrainWorldGenerator
{
    /// <summary>Most cells a generated terrain may have along one side.</summary>
    public const int MaximumCells = 8192;

    /// <summary>True for recipes this generator handles: code that produces a heightfield.</summary>
    public static bool Handles(TerrainCreationRecipe recipe) =>
        recipe is { Source: TerrainCreationSource.Code, Surface: TerrainCodeSurface.Heightfield };

    /// <param name="position">Where the terrain's centre goes; heights and the sea are raised by its Y.</param>
    public static TerrainWorldResult Generate(TerrainCreationRecipe input, CancellationToken cancellation = default,
        IProgress<string>? progress = null, Vector3 position = default)
    {
        Stopwatch watch = Stopwatch.StartNew();
        var recipe = JsonSerializer.Deserialize<TerrainCreationRecipe>(JsonSerializer.Serialize(input))!;
        if (!Handles(recipe)) throw new InvalidOperationException("Only code recipes that produce a heightfield can be generated as a world.");
        TimeSpan budget = TimeSpan.FromMinutes(20);
        TerrainRecipeVm setup = new(recipe, cancellation, budget);
        TerrainWorldPlan plan = recipe.World ?? new TerrainWorldPlan();
        if (!float.IsFinite(recipe.Width) || !float.IsFinite(recipe.Length) || !float.IsFinite(recipe.Spacing)
            || recipe.Width <= 0 || recipe.Length <= 0 || recipe.Spacing < .05f || recipe.Width > 100000 || recipe.Length > 100000)
            throw new InvalidOperationException("Width and length must be between 0 and 100,000 metres, with sample spacing of at least 0.05 metres.");
        if (!float.IsFinite(recipe.MinHeight) || !float.IsFinite(recipe.MaxHeight) || recipe.MaxHeight <= recipe.MinHeight)
            throw new InvalidOperationException("Maximum height must be above minimum height.");

        // Square cells: widen the spacing if the request would exceed the largest supported grid.
        float cell = MathF.Max(recipe.Spacing, MathF.Max(recipe.Width, recipe.Length) / MaximumCells);
        int cellsX = Math.Max(2, (int)MathF.Round(recipe.Width / cell)), cellsZ = Math.Max(2, (int)MathF.Round(recipe.Length / cell));
        var grid = new HeightGrid(cellsX + 1, cellsZ + 1, cell, position.X - cellsX * cell * 0.5f, position.Z - cellsZ * cell * 0.5f);

        // The recipe speaks in heights above its own zero; everything it names is raised with the terrain.
        float lift = position.Y;
        recipe.MinHeight += lift; recipe.MaxHeight += lift;
        if (plan.SeaLevel is { } authoredSea) plan.SeaLevel = authoredSea + lift;
        if (plan.Paint is { } authoredPaint) plan.Paint = authoredPaint with { BeachHeight = authoredPaint.BeachHeight + lift, SnowHeight = authoredPaint.SnowHeight + lift };
        foreach (TerrainWorldSites group in plan.Sites) { group.MinimumHeight += lift; group.MaximumHeight += lift; }
        foreach (TerrainScatterLayer layer in plan.Scatter) { layer.MinimumHeight += lift; layer.MaximumHeight += lift; }

        progress?.Report($"Shaping {grid.Width:N0} x {grid.Depth:N0} samples…");
        Parallel.For(0, grid.Depth,
            new ParallelOptions { CancellationToken = cancellation },
            () => new TerrainRecipeVm(JsonSerializer.Deserialize<TerrainCreationRecipe>(JsonSerializer.Serialize(recipe))!, cancellation, budget, collectPlan: false),
            (z, _, vm) =>
            {
                float wz = grid.WorldZ(z) - position.Z;
                int row = z * grid.Width;
                for (int x = 0; x < grid.Width; x++) grid.Values[row + x] = vm.Evaluate(grid.WorldX(x) - position.X, 0, wz) + lift;
                return vm;
            },
            _ => { });
        GC.KeepAlive(setup);

        float seaLevel = plan.SeaLevel ?? float.MinValue;
        if (plan.ErosionStrength > 0)
        {
            progress?.Report("Eroding…");
            HeightGrid coarse = grid.Downsample(FactorFor(grid, 2049));
            HeightGrid before = Copy(coarse);
            TerrainWorldOps.Erode(coarse, plan.ErosionStrength, recipe.Seed);
            grid.AddDifference(before, coarse);
        }

        List<TracedRiver> rivers = [];
        if (plan.RiverCount > 0)
        {
            progress?.Report("Tracing rivers…");
            cancellation.ThrowIfCancellationRequested();
            HeightGrid coarse = grid.Downsample(FactorFor(grid, 1025));
            HeightGrid before = Copy(coarse);
            float drainTo = plan.SeaLevel ?? coarse.Values.Min();
            int[] receiver = TerrainWorldOps.FillSinks(coarse, drainTo);
            // Hollows that could not drain are filled in the full terrain too, so the rivers that
            // cross them run over ground rather than through a dry pit.
            grid.AddDifference(before, coarse);
            float[] area = TerrainWorldOps.FlowAccumulation(coarse, receiver);
            rivers = TerrainWorldOps.TraceRivers(coarse, receiver, area, drainTo, plan.RiverCount, plan.RiverBasinSquareKilometres);
            foreach (TracedRiver river in rivers) TerrainWorldOps.CarveRiver(grid, river);
        }

        // Level sites after the rivers are cut, so settlements sit beside the water, not in it.
        var sites = new List<(TerrainWorldSites Group, TerrainSite Site)>();
        if (plan.Sites.Count > 0)
        {
            progress?.Report("Finding level ground…");
            HeightGrid coarse = grid.Downsample(FactorFor(grid, 1025));
            var riverPoints = rivers.SelectMany(river => river.Points.Where((_, index) => index % 4 == 0)).Select(point => point.Position).ToArray();
            float WaterDistance(float x, float z)
            {
                float best = float.MaxValue;
                foreach (Vector3 point in riverPoints)
                {
                    float dx = point.X - x, dz = point.Z - z, squared = dx * dx + dz * dz;
                    if (squared < best) best = squared;
                }

                return best == float.MaxValue ? 0f : MathF.Sqrt(best);
            }

            var taken = new List<TerrainSite>();
            foreach (TerrainWorldSites group in plan.Sites)
            {
                float separation = group.Separation > 0 ? group.Separation : group.Radius * 6f;
                // Ask for extra and keep those clear of earlier groups and of the river channels.
                foreach (TerrainSite site in TerrainWorldOps.FindSites(coarse, group.Count * 3 + taken.Count, group.MinimumHeight,
                             group.MaximumHeight, group.Radius, separation, recipe.Seed + sites.Count, riverPoints.Length > 0 ? WaterDistance : null))
                {
                    if (sites.Count(entry => ReferenceEquals(entry.Group, group)) >= group.Count) break;
                    if (taken.Any(other => Vector3.Distance(other.Centre, site.Centre) < separation)) continue;
                    if (riverPoints.Length > 0 && WaterDistance(site.Centre.X, site.Centre.Z) < group.Radius + 30f) continue;
                    taken.Add(site);
                    sites.Add((group, site));
                }
            }

            foreach ((_, TerrainSite site) in sites)
            {
                // Level to the height of the full terrain at the centre, not the coarse average.
                TerrainSite exact = site with { Centre = new Vector3(site.Centre.X, grid.Sample(site.Centre.X, site.Centre.Z), site.Centre.Z) };
                TerrainWorldOps.Flatten(grid, exact);
            }
        }

        progress?.Report("Writing the terrain…");
        float low = float.MaxValue, high = float.MinValue;
        foreach (float value in grid.Values) { if (value < low) low = value; if (value > high) high = value; }
        float minimum = MathF.Min(recipe.MinHeight, low - 2f), maximum = MathF.Max(recipe.MaxHeight, high + 2f);
        var terrain = new TerrainAsset(grid.Width, grid.Depth, cell, grid.OriginX, grid.OriginZ, minimum, maximum);
        ushort[] heights = terrain.HeightsData;
        float scale = ushort.MaxValue / (maximum - minimum);
        Parallel.For(0, grid.Depth, z =>
        {
            int row = z * grid.Width;
            for (int x = 0; x < grid.Width; x++)
                heights[row + x] = (ushort)Math.Clamp((grid.Values[row + x] - minimum) * scale + 0.5f, 0f, ushort.MaxValue);
        });

        var result = new TerrainWorldResult { Recipe = recipe, Terrain = terrain, Plan = plan };
        if (plan.Paint is { } paint)
        {
            progress?.Report("Painting…");
            TerrainWorldOps.PaintNatural(grid, terrain.SplatmapData, paint.BeachHeight, paint.SnowHeight, paint.RockSlopeDegrees, recipe.Seed);
            // River beds and banks are shingle, which also keeps grass-only scatter out of the water.
            foreach (TracedRiver river in rivers)
                foreach (RiverPoint point in river.Points)
                    TerrainWorldOps.PaintDisc(grid, terrain.SplatmapData, new Vector2(point.Position.X, point.Position.Z),
                        MathF.Max(cell * 2f, point.Width * 0.75f), 2, 0.9f);
        }

        if (plan.SeaLevel is { } sea)
        {
            result.Water.Add(new TerrainWaterDefinition
            {
                Name = "Sea", Kind = TerrainWaterKind.Ocean, Center = new Vector3(0, sea, 0), SurfaceHeight = sea,
                SizeX = 60000, SizeZ = 60000, SimulationEnabled = false, ConformToTerrain = false,
                WaveAmplitude = 0.22f, FlowSpeed = 0.12f, PhysicsDepth = MathF.Max(4f, sea - minimum),
            });
        }

        int riverNumber = 0;
        foreach (TracedRiver river in rivers)
        {
            riverNumber++;
            // A water body is one ribbon mesh, so a long river is stored as joined reaches.
            List<RiverPoint> points = river.Points.Where((_, index) => index % 3 == 0 || index == river.Points.Count - 1).ToList();
            const int reachPoints = 40;
            for (int start = 0, reach = 1; start < points.Count - 1; start += reachPoints - 1, reach++)
            {
                List<RiverPoint> part = points.Skip(start).Take(reachPoints).ToList();
                if (part.Count < 2) break;
                var water = new TerrainWaterDefinition
                {
                    Name = points.Count > reachPoints ? $"River {riverNumber} reach {reach}" : $"River {riverNumber}",
                    Kind = TerrainWaterKind.River, SimulationEnabled = false, ConformToTerrain = false, CarveRiverbed = false,
                    FlowSpeed = 0.7f, WaveAmplitude = 0.03f,
                    Center = part[part.Count / 2].Position,
                    RiverPoints = part.Select(point => new WaterSplinePoint
                    {
                        // The surface sits most of the way up the cut channel, never above the sea it joins.
                        Position = new Vector3(point.Position.X,
                            plan.SeaLevel is { } level && point.Position.Y + point.Depth * 0.8f < level ? level : point.Position.Y + point.Depth * 0.8f,
                            point.Position.Z),
                        Width = point.Width * 0.92f, Depth = point.Depth,
                    }).ToList(),
                };
                water.SurfaceHeight = water.RiverPoints.Average(point => point.Position.Y);
                water.SizeX = water.SizeZ = part.Average(point => point.Width);
                result.Water.Add(water);
            }
        }

        var occupied = new List<Vector3>();
        int siteNumber = 0;
        foreach ((TerrainWorldSites group, TerrainSite found) in sites)
        {
            siteNumber++;
            TerrainSite site = found with { Centre = new Vector3(found.Centre.X, grid.Sample(found.Centre.X, found.Centre.Z), found.Centre.Z) };
            if (group.PaintSlot > 0)
                TerrainWorldOps.PaintDisc(grid, terrain.SplatmapData, new Vector2(site.Centre.X, site.Centre.Z), site.Radius * 1.1f, group.PaintSlot - 1, 0.85f);
            result.PointsOfInterest.Add(new TerrainPointOfInterest
            {
                Name = $"Settlement {siteNumber}", Category = "Settlement", Position = site.Centre, DiscoveryRadius = site.Radius * 2f,
            });
            int placementNumber = 0;
            foreach (TerrainWorldPlacement placement in group.Placements)
            {
                placementNumber++;
                foreach ((Vector3 spot, float yaw) in TerrainWorldOps.ArrangeAroundSite(grid, site, placement.Count,
                             placement.InnerRadius, MathF.Min(placement.OuterRadius, site.Radius), placement.Spacing,
                             recipe.Seed + siteNumber * 131 + placementNumber * 17, occupied))
                {
                    result.Placed.Add(new TerrainPlacedEntity { Entity = placement.Entity, Position = spot, Yaw = yaw });
                }
            }
        }

        foreach (TerrainScatterLayer layer in plan.Scatter)
        {
            layer.Normalize();
            result.Scatter.Add(layer);
        }

        result.Elapsed = watch.Elapsed;
        result.Summary = $"{cellsX * cell:N0} x {cellsZ * cell:N0} m at {cell:0.##} m · heights {low:0.#} to {high:0.#} m"
            + (rivers.Count > 0 ? $" · {rivers.Count} rivers" : "")
            + (sites.Count > 0 ? $" · {sites.Count} sites, {result.Placed.Count} objects" : "")
            + (result.Scatter.Count > 0 ? $" · {result.Scatter.Count} scatter layers" : "")
            + $" · {watch.Elapsed.TotalSeconds:0.0} s";
        progress?.Report(result.Summary);
        return result;
    }

    private static int FactorFor(HeightGrid grid, int targetSamples)
    {
        int factor = 1;
        while ((Math.Max(grid.Width, grid.Depth) - 1) / factor + 1 > targetSamples) factor *= 2;
        return factor;
    }

    private static HeightGrid Copy(HeightGrid source)
    {
        var copy = new HeightGrid(source.Width, source.Depth, source.CellSize, source.OriginX, source.OriginZ);
        Array.Copy(source.Values, copy.Values, source.Values.Length);
        return copy;
    }
}
