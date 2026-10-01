using System.Numerics;
using Genesis.Application.Runtime;
using Genesis.Rendering.Primitives;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Geometry;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;
using Genesis.World.Water;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless;

/// <summary>
/// The parts of a large world that are decided on the CPU: how a model is simplified and when
/// each version is shown, where scattered copies stand, and how far the sun's shadows and the
/// sea reach.
/// </summary>
internal static class LargeWorldDetailSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Engine.Geometry.Simplifier.KeepsShapeSeamsAndBorders", () =>
        {
            // A sphere with a texture seam: the seam column is stored twice, as an importer stores it.
            (Vector3[] positions, int[] indices) = Sphere(48, 32, 2f);
            int full = indices.Length / 3;
            int[] quarter = MeshSimplifier.Simplify(positions, indices, full / 4);
            HeadlessHarness.Assert(quarter.Length / 3 <= full / 4 && quarter.Length / 3 > full / 8,
                $"Asked for {full / 4} of {full} triangles; got {quarter.Length / 3}.");
            float worst = 0f;
            foreach (int index in quarter)
            {
                HeadlessHarness.Assert(index >= 0 && index < positions.Length, "The result indexes outside the original vertices.");
                worst = MathF.Max(worst, MathF.Abs(positions[index].Length() - 2f));
            }

            HeadlessHarness.Assert(worst < 1e-4f, "Simplifying moved a vertex: results must reuse the original vertices untouched.");
            HeadlessHarness.Assert(IsWatertight(positions, quarter),
                "The simplified sphere has a hole: vertices sharing a position (a texture seam) must stay together.");
            for (int t = 0; t < quarter.Length; t += 3)
            {
                Vector3 a = positions[quarter[t]], b = positions[quarter[t + 1]], c = positions[quarter[t + 2]];
                Vector3 normal = Vector3.Cross(b - a, c - a);
                HeadlessHarness.Assert(normal.Length() > 1e-7f && Vector3.Dot(normal, a + b + c) > 0f,
                    "A simplified triangle is degenerate or faces inwards.");
            }

            // A tight error limit stops early instead of reaching the count at any cost.
            int[] careful = MeshSimplifier.Simplify(positions, indices, 8, 0.002f);
            HeadlessHarness.Assert(careful.Length / 3 > full / 4,
                $"An error limit of 2 mm on a 2 m sphere still collapsed it to {careful.Length / 3} triangles.");

            // An open sheet keeps its outline.
            (Vector3[] sheet, int[] sheetIndices) = Sheet(24, 10f);
            int[] coarse = MeshSimplifier.Simplify(sheet, sheetIndices, 40);
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (int index in coarse) { min = Vector3.Min(min, sheet[index]); max = Vector3.Max(max, sheet[index]); }
            HeadlessHarness.Assert(coarse.Length / 3 <= 40 && MathF.Abs(min.X) < 1e-4f && MathF.Abs(min.Z) < 1e-4f
                && MathF.Abs(max.X - 10f) < 1e-4f && MathF.Abs(max.Z - 10f) < 1e-4f,
                $"A flat sheet lost its outline when simplified ({coarse.Length / 3} triangles, {min} to {max}).");
            float area = 0f;
            for (int t = 0; t < coarse.Length; t += 3)
                area += Vector3.Cross(sheet[coarse[t + 1]] - sheet[coarse[t]], sheet[coarse[t + 2]] - sheet[coarse[t]]).Length() * 0.5f;
            HeadlessHarness.Assert(MathF.Abs(area - 100f) < 0.5f, $"The simplified sheet covers {area:F1} of 100 square metres.");

            MeshSimplifier.Compact<Vector3>(positions, quarter, out Vector3[] kept, out ushort[] compact);
            HeadlessHarness.Assert(kept.Length < positions.Length && compact.Length == quarter.Length
                && compact.All(index => index < kept.Length), "Compacting did not drop the unused vertices.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Models.AutoLod.LevelsShrinkAndFollowScreenSize", () =>
        {
            (Vector3[] positions, int[] indices) = Sphere(48, 32, 1.5f);
            var vertices = positions.Select(position => new MeshVertex
            {
                Position = position, Normal = Vector3.Normalize(position), Color = Vector4.One,
            }).ToArray();
            ushort[] drawn = indices.Select(index => (ushort)index).ToArray();
            IReadOnlyList<(int Level, MeshVertex[] Vertices, ushort[] Indices)> levels = ModelGpuCache.BuildAutoLodMeshes(vertices, drawn);
            HeadlessHarness.Assert(levels.Count >= 2, $"A {drawn.Length / 3}-triangle sphere produced {levels.Count} simplified levels.");
            int previous = drawn.Length / 3;
            foreach ((int level, MeshVertex[] levelVertices, ushort[] levelIndices) in levels)
            {
                HeadlessHarness.Assert(levelIndices.Length / 3 < previous * 0.86f,
                    $"Level {level + 1} has {levelIndices.Length / 3} triangles after {previous}: each level must be clearly cheaper.");
                HeadlessHarness.Assert(levelIndices.All(index => index < levelVertices.Length), $"Level {level + 1} indexes outside its vertices.");
                // The error limit grows with each level, so does the allowed drift from the sphere.
                float limit = 1.5f * ModelGpuCache.AutoLodErrors[level] * 4f + 0.02f;
                float drift = MaximumChordSag(levelVertices, levelIndices, 1.5f);
                HeadlessHarness.Assert(drift < limit, $"Level {level + 1} strays {drift:F3} m from a 1.5 m sphere (limit {limit:F3}).");
                previous = levelIndices.Length / 3;
            }

            // A box is already as cheap as it can be.
            (MeshVertex[] box, ushort[] boxIndices) = Box();
            HeadlessHarness.Assert(ModelGpuCache.BuildAutoLodMeshes(box, boxIndices).Count == 0,
                "A twelve-triangle box was given simplified levels.");

            // The level follows size on screen, the same at any resolution, and nothing is chosen
            // when no camera has been set (editors and thumbnails draw in full).
            HeadlessHarness.Assert(!ModelLodView.Active && ModelLodView.LevelFor(new Vector3(0, 0, 5000), 1f) == 0,
                "With no view set, models must draw in full.");
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(60f * MathF.PI / 180f, 16f / 9f, 0.5f, 16000f);
            ModelLodView.Begin(Vector3.Zero, projection);
            try
            {
                int[] chosen = new[] { 4f, 20f, 60f, 140f, 2500f }
                    .Select(distance => ModelLodView.LevelFor(new Vector3(0, 0, distance), 1.5f)).ToArray();
                HeadlessHarness.Assert(chosen.SequenceEqual(new[] { 0, 1, 2, 3, -1 }),
                    $"A 3 m model at 4, 20, 60, 140 and 2500 m chose levels {string.Join(", ", chosen)}; expected 0, 1, 2, 3 and culled.");
                HeadlessHarness.Assert(ModelLodView.LevelFor(new Vector3(0, 0, 2500), 400f) == 0,
                    "A mountain-sized model far away was reduced although it fills the view.");
            }
            finally { ModelLodView.End(); }
        });

        HeadlessHarness.RunCase(context.Report, "Engine.World.Scatter.PlacementIsRepeatableSeamlessAndFollowsItsRule", () =>
        {
            TerrainAsset terrain = RuntimeViewportHarness.BuildIslandTerrain(513, 4f);       // 2048 m
            // Paint the western half as layer 2 so the paint rule has something to exclude.
            for (int z = 0; z < terrain.ResolutionZ; z++)
            for (int x = 0; x < terrain.ResolutionX / 2; x++)
                terrain.SetSplat(x, z, 0, 255, 0, 0);

            var layer = new TerrainScatterLayer
            {
                Model = "Tree", DensityPerHectare = 160, MinimumSpacing = 4, MinimumHeight = 2, MaximumHeight = 400,
                MaximumSlopeDegrees = 30, PaintLayerMask = 1, Seed = 9,
            };
            layer.Normalize();
            (int cellsX, int cellsZ) = TerrainScatterPlacement.CellCount(terrain);
            HeadlessHarness.Assert(cellsX == 8 && cellsZ == 8, $"A 2048 m terrain should be 8 x 8 scatter cells; got {cellsX} x {cellsZ}.");

            var all = new List<TerrainScatterInstance>();
            for (int cz = 0; cz < cellsZ; cz++)
            for (int cx = 0; cx < cellsX; cx++)
                all.AddRange(TerrainScatterPlacement.Generate(terrain, layer, cx, cz));
            HeadlessHarness.Assert(all.Count > 1500, $"Only {all.Count} trees were placed on a 2 km island.");

            // Asking again, in another order, gives the same copies.
            TerrainScatterInstance[] first = TerrainScatterPlacement.Generate(terrain, layer, 5, 4);
            TerrainScatterPlacement.Generate(terrain, layer, 2, 6);
            TerrainScatterInstance[] again = TerrainScatterPlacement.Generate(terrain, layer, 5, 4);
            HeadlessHarness.Assert(first.Length > 0 && first.Length == again.Length
                && first.Zip(again).All(pair => pair.First.Position == pair.Second.Position && pair.First.Yaw == pair.Second.Yaw
                    && pair.First.Scale == pair.Second.Scale),
                "The same cell produced different copies the second time.");

            // Every copy obeys the rule, wherever its cell is.
            float slopeLimit = MathF.Tan(30f * MathF.PI / 180f);
            foreach (TerrainScatterInstance instance in all)
            {
                float x = instance.Position.X, z = instance.Position.Z;
                float ground = terrain.SampleHeight(x, z);
                HeadlessHarness.Assert(ground >= 2f && ground <= 400f, $"A tree stands at height {ground:F1}, outside 2 to 400 m.");
                HeadlessHarness.Assert(MathF.Abs(instance.Position.Y - (ground - layer.Sink * instance.Scale)) < 0.01f, "A tree is not on the ground.");
                HeadlessHarness.Assert(TerrainScatterPlacement.PaintWeight(terrain, x, z, 1) >= layer.MinimumPaintWeight,
                    $"A tree stands at ({x:F0}, {z:F0}) on paint its layer excludes.");
                float dx = terrain.SampleHeight(x + 4f, z) - terrain.SampleHeight(x - 4f, z);
                float dz = terrain.SampleHeight(x, z + 4f) - terrain.SampleHeight(x, z - 4f);
                HeadlessHarness.Assert(MathF.Sqrt(dx * dx + dz * dz) / 8f <= slopeLimit + 1e-4f, "A tree stands on ground steeper than its limit.");
                HeadlessHarness.Assert(instance.Scale >= layer.MinimumScale && instance.Scale <= layer.MaximumScale, "A tree's scale is outside the layer's range.");
            }

            // Cells meet without a doubled tree: no two copies closer than a fifth of the grid pitch.
            float pitch = TerrainScatterPlacement.Pitch(layer);
            var buckets = new Dictionary<(int, int), List<Vector3>>();
            float closest = float.MaxValue;
            foreach (TerrainScatterInstance instance in all)
            {
                (int bx, int bz) = ((int)MathF.Floor(instance.Position.X / pitch), (int)MathF.Floor(instance.Position.Z / pitch));
                for (int oz = -1; oz <= 1; oz++)
                for (int ox = -1; ox <= 1; ox++)
                    if (buckets.TryGetValue((bx + ox, bz + oz), out List<Vector3>? near))
                        foreach (Vector3 other in near)
                            closest = MathF.Min(closest, Vector2.Distance(new Vector2(other.X, other.Z), new Vector2(instance.Position.X, instance.Position.Z)));
                if (!buckets.TryGetValue((bx, bz), out List<Vector3>? bucket)) buckets[(bx, bz)] = bucket = new List<Vector3>();
                bucket.Add(instance.Position);
            }

            HeadlessHarness.Assert(closest >= pitch * 0.19f, $"Two trees stand {closest:F2} m apart (grid pitch {pitch:F2} m): cells overlap.");

            // Clumping gathers the same layer into woods with clearings between.
            var clumped = new TerrainScatterLayer
            {
                Model = "Tree", DensityPerHectare = 160, MinimumSpacing = 4, MinimumHeight = 2, MaximumHeight = 400,
                MaximumSlopeDegrees = 30, Seed = 9, ClumpSize = 200, ClumpStrength = 1f,
            };
            clumped.Normalize();
            int open = 0, wooded = 0, cells = 0;
            for (int cz = 0; cz < cellsZ; cz++)
            for (int cx = 0; cx < cellsX; cx++)
            {
                var evenLayer = new TerrainScatterLayer
                {
                    Model = "Tree", DensityPerHectare = 160, MinimumSpacing = 4, MinimumHeight = 2, MaximumHeight = 400, MaximumSlopeDegrees = 30, Seed = 9,
                };
                evenLayer.Normalize();
                open += TerrainScatterPlacement.Generate(terrain, evenLayer, cx, cz).Length;
                wooded += TerrainScatterPlacement.Generate(terrain, clumped, cx, cz).Length;
                cells++;
            }

            HeadlessHarness.Assert(wooded > open * 0.2f && wooded < open * 0.8f,
                $"Clumping kept {wooded} of {open} trees over {cells} cells; it should clear roughly half the ground.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rendering.Shadows.FarCascadeReachesAcrossAValley", () =>
        {
            CascadeShadowFrame standard = CascadeShadowMath.Compute(0.5f, 16000f, 120f, 1.05f, 16f / 9f, 1024, 1024, 0.0015f, 3);
            CascadeShadowFrame wide = CascadeShadowMath.Compute(0.5f, 16000f, 120f, 1.05f, 16f / 9f, 1024, 1024, 0.0015f, 3, 4000f);
            HeadlessHarness.Assert(wide.FarExtent == 4000f && standard.FarExtent < 400f,
                $"The far cascade should widen from {standard.FarExtent:F0} m to 4000 m; got {wide.FarExtent:F0} m.");
            HeadlessHarness.Assert(wide.NearExtent == standard.NearExtent && wide.MidExtent == standard.MidExtent
                && wide.NearDepthBias == standard.NearDepthBias && wide.MidDepthBias == standard.MidDepthBias
                && wide.FarDepthBias == standard.FarDepthBias,
                "Widening the far cascade changed the near or middle cascade, or the bias they share.");
            CascadeShadowFrame two = CascadeShadowMath.Compute(0.5f, 16000f, 120f, 1.05f, 16f / 9f, 1024, 1024, 0.0015f, 2, 4000f);
            HeadlessHarness.Assert(two.FarExtent == CascadeShadowMath.Compute(0.5f, 16000f, 120f, 1.05f, 16f / 9f, 1024, 1024, 0.0015f, 2).FarExtent,
                "A two-cascade scene must ignore the long shadow distance: it has no cascade to spare for it.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.World.Room.LongViewSettingsSurviveSaveAndReload", () =>
        {
            string folder = Path.Combine(context.Workspace, "LargeWorldRoom");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "World.room.json");
            RoomAsset room = RoomAsset.Create("World", RoomDimension.ThreeD);
            HeadlessHarness.Assert(room.Environment.VisibilityKilometres == 0f && room.Environment.ShadowDistance == 0f
                && room.Environment.SimulationDistance == 0f,
                "A new room must keep the short-range haze and shadows, and run every object, as small scenes expect.");
            room.Environment.VisibilityKilometres = 40f;
            room.Environment.ShadowDistance = 3500f;
            room.Environment.SimulationDistance = 600f;
            RoomAssetLoader.Save(room, file);
            RoomAsset loaded = RoomAssetLoader.Parse(file);
            HeadlessHarness.Assert(loaded.Environment.VisibilityKilometres == 40f && loaded.Environment.ShadowDistance == 3500f
                && loaded.Environment.SimulationDistance == 600f,
                $"Long-view settings were not saved (read {loaded.Environment.VisibilityKilometres} km, "
                + $"{loaded.Environment.ShadowDistance} m, {loaded.Environment.SimulationDistance} m).");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.World.Simulation.DistantObjectsRestUntilTheCameraReturns", () =>
        {
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            var host = new ScriptHostSystem();
            EcsWorld world = new();
            var step = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Step"] = "var probe = 1;\n" };
            Genesis.Shared.ECS.Entity Spawn(float x, bool drawn)
            {
                Genesis.Shared.ECS.Entity entity = world.CreateEntity();
                world.Set(entity, new TransformComponent { X = x, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                if (drawn) world.Set(entity, new Draw3DComponent { Visible = true });
                using (host.UseEventSources(step)) host.Attach(world, entity, "Probe");
                return entity;
            }

            Spawn(10f, drawn: true);                       // beside the camera
            Spawn(5000f, drawn: true);                     // across the island
            Spawn(5000f, drawn: false);                    // a manager with nothing to draw
            Genesis.Shared.ECS.Entity kept = Spawn(5000f, drawn: true);

            host.Update(1f / 60f);
            HeadlessHarness.Assert(host.LastStepped == 4 && host.LastResting == 0,
                $"With no activity distance every object must run; {host.LastStepped} ran and {host.LastResting} rested.");

            host.SimulationDistance = 500f;
            host.SimulationFocus = Vector3.Zero;
            host.KeepActive(kept);
            host.Update(1f / 60f);
            HeadlessHarness.Assert(host.LastStepped == 3 && host.LastResting == 1,
                $"Only the distant drawn object should rest; {host.LastStepped} ran and {host.LastResting} rested.");

            host.SimulationFocus = new Vector3(5000f, 0f, 0f);
            host.Update(1f / 60f);
            HeadlessHarness.Assert(host.LastStepped == 3 && host.LastResting == 1,
                $"After the camera crossed the island the near object should rest and the far one wake; {host.LastStepped} ran and {host.LastResting} rested.");

            host.KeepActive(kept, false);
            host.SimulationFocus = Vector3.Zero;
            host.Update(1f / 60f);
            HeadlessHarness.Assert(host.LastStepped == 2 && host.LastResting == 2,
                $"An object no longer kept active should rest when out of range; {host.LastStepped} ran and {host.LastResting} rested.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.World.Water.OceanReachesTheHorizon", () =>
        {
            var sea = new TerrainWaterDefinition
            {
                Name = "Sea", Kind = TerrainWaterKind.Ocean, SurfaceHeight = 0f, SizeX = 60000, SizeZ = 60000,
                SimulationEnabled = false, ConformToTerrain = false,
            };
            sea.Normalize();
            HeadlessHarness.Assert(sea.Kind == TerrainWaterKind.Ocean && sea.SizeX == 60000, "An ocean was not kept by the terrain's water list.");
            WaterBody body = sea.ToWaterBody();
            HeadlessHarness.Assert(body.Kind == WaterBodyKind.Ocean && body.OceanRadius >= 30000f && !body.GroundMistEnabled,
                $"The sea became {body.Kind} with radius {body.OceanRadius}.");

            var camera = new Vector3(1234f, 80f, -987f);
            var mesh = WaterSurfaceMesh.BuildOceanSurface(camera, body.OceanRadius, body.SurfaceY);
            HeadlessHarness.Assert(mesh.Vertices.Length is > 1000 and <= 65536 && mesh.Indices.Length % 3 == 0,
                $"The sea mesh has {mesh.Vertices.Length} vertices.");
            float reach = 0f, nearest = float.MaxValue;
            foreach (MeshVertex vertex in mesh.Vertices)
            {
                HeadlessHarness.Assert(MathF.Abs(vertex.Position.Y - body.SurfaceY) < 1e-3f, "The sea surface is not level.");
                float distance = Vector2.Distance(new Vector2(vertex.Position.X, vertex.Position.Z), new Vector2(camera.X, camera.Z));
                reach = MathF.Max(reach, distance);
                nearest = MathF.Min(nearest, distance);
            }

            HeadlessHarness.Assert(reach >= body.OceanRadius * 0.95f && nearest < WaterSurfaceMesh.OceanSnap,
                $"The sea should run from under the camera to {body.OceanRadius:F0} m; it runs from {nearest:F0} to {reach:F0} m.");
            // Moving a little must not rebuild it: the mesh is anchored to a coarse grid.
            (float x0, float z0) = WaterSurfaceMesh.OceanCentre(camera);
            (float x1, float z1) = WaterSurfaceMesh.OceanCentre(camera + new Vector3(3f, 0f, -2f));
            HeadlessHarness.Assert(x0 == x1 && z0 == z1, "A three-metre step moved the sea mesh.");
        });
    }

    private static (Vector3[] Positions, int[] Indices) Sphere(int segments, int rings, float radius)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();
        for (int ring = 0; ring <= rings; ring++)
        {
            float polar = MathF.PI * ring / rings;
            for (int segment = 0; segment <= segments; segment++)      // the last column repeats the first: a seam
            {
                float turn = MathF.Tau * segment / segments;
                positions.Add(new Vector3(MathF.Sin(polar) * MathF.Cos(turn), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(turn)) * radius);
            }
        }

        int row = segments + 1;
        for (int ring = 0; ring < rings; ring++)
        for (int segment = 0; segment < segments; segment++)
        {
            int a = ring * row + segment, b = a + 1, c = a + row, d = c + 1;
            if (ring > 0) { indices.Add(a); indices.Add(b); indices.Add(c); }
            if (ring < rings - 1) { indices.Add(b); indices.Add(d); indices.Add(c); }
        }

        return (positions.ToArray(), indices.ToArray());
    }

    private static (Vector3[] Positions, int[] Indices) Sheet(int cells, float size)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();
        for (int z = 0; z <= cells; z++)
        for (int x = 0; x <= cells; x++)
            positions.Add(new Vector3(size * x / cells, 0f, size * z / cells));
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            int a = z * (cells + 1) + x, b = a + 1, c = a + cells + 1, d = c + 1;
            indices.AddRange(new[] { a, c, b, b, c, d });
        }

        return (positions.ToArray(), indices.ToArray());
    }

    private static (MeshVertex[] Vertices, ushort[] Indices) Box()
    {
        var vertices = new List<MeshVertex>();
        var indices = new List<ushort>();
        Vector3[] normals = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        foreach (Vector3 normal in normals)
        {
            Vector3 side = MathF.Abs(normal.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
            Vector3 other = Vector3.Cross(normal, side);
            int start = vertices.Count;
            foreach ((float u, float v) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                vertices.Add(new MeshVertex { Position = normal + side * u + other * v, Normal = normal, Color = Vector4.One });
            indices.AddRange(new[] { (ushort)start, (ushort)(start + 1), (ushort)(start + 2), (ushort)start, (ushort)(start + 2), (ushort)(start + 3) });
        }

        return (vertices.ToArray(), indices.ToArray());
    }

    /// <summary>True when every edge is shared by exactly two triangles, matching vertices by position.</summary>
    private static bool IsWatertight(Vector3[] positions, int[] indices)
    {
        var ids = new Dictionary<(int, int, int), int>();
        int Id(int index)
        {
            Vector3 p = positions[index];
            var key = ((int)MathF.Round(p.X * 10000f), (int)MathF.Round(p.Y * 10000f), (int)MathF.Round(p.Z * 10000f));
            if (!ids.TryGetValue(key, out int id)) ids[key] = id = ids.Count;
            return id;
        }

        var edges = new Dictionary<(int, int), int>();
        for (int t = 0; t < indices.Length; t += 3)
        {
            int a = Id(indices[t]), b = Id(indices[t + 1]), c = Id(indices[t + 2]);
            foreach ((int from, int to) in new[] { (a, b), (b, c), (c, a) })
            {
                if (from == to) continue;
                var key = from < to ? (from, to) : (to, from);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }

        return edges.Count > 0 && edges.Values.All(count => count == 2);
    }

    /// <summary>Furthest any triangle's centre lies inside a sphere its corners sit on.</summary>
    private static float MaximumChordSag(MeshVertex[] vertices, ushort[] indices, float radius)
    {
        float worst = 0f;
        for (int t = 0; t < indices.Length; t += 3)
        {
            Vector3 centre = (vertices[indices[t]].Position + vertices[indices[t + 1]].Position + vertices[indices[t + 2]].Position) / 3f;
            worst = MathF.Max(worst, radius - centre.Length());
        }

        return worst;
    }
}
