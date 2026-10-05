using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Rendering.Core;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Headless.Suites;

/// <summary>Grass grown from a rule around the camera: settings, rule, streaming, drawing and cost.</summary>
internal static partial class TerrainLayersGrassSuite
{
    public static void RunGrass(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.SettingsRoundTripAndOldDocumentsStayOff", () => GrassSettingsRoundTrip(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.InspectorShowsEveryLayerDensity", GrassInspectorShowsRule);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.LayerDensityDecidesTuftCount", GrassLayerDensity);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.TuftsStandOnGroundAndAvoidSteepSlopes", GrassHeightAndSlope);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.SameSeedSameTufts", GrassDeterministic);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.CellsRecycleWithinFrameBudget", GrassRecycling);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.RefreshFollowsPaintWithoutGoingBare", GrassRefresh);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.Dx11DrawsMeadowNotDirt", () => GrassDx11Capture(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.Dx11FrameCostBeforeAndAfter", () => GrassDx11FrameCost(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.TerrainEditorAppliesSavesAndDrawsRule", () => GrassTerrainEditor(ctx));
    }

    private static void GrassTerrainEditor(HeadlessContext ctx)
    {
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "GrassRuleEditor"), "Grass rule editor");
        var resources = new Genesis.Application.Core.Resources.ResourceService(project);
        string terrainFile = resources.CreateResource(resources.AssetsRoot, Genesis.Application.Core.Resources.ResourceKind.Terrain, "Meadow strip");
        GrassSplitTerrain(129, 0.5f, slope: 0f).Save(terrainFile + ".gterrain");
        TerrainGrassRuleSettings expected;
        using (var editor = new Genesis.Application.Editors.Suite.Terrain.TerrainEditorControl(terrainFile, project.RootPath))
        {
            GrassAssert(!editor.GrassRule.Enabled, "A new terrain starts with grass around the camera on.");
            using (Form dialog = editor.CreateScatterSettingsDialog())
            {
                List<Control> controls = GrassControls(dialog).ToList();
                GrassAssert(controls.Any(control => control.Name == "InspectorDrawer_Layer8Density")
                    && controls.Any(control => control.Name == "InspectorDrawer_Radius")
                    && controls.Any(control => control is Button && control.Text.Contains("Apply Grass Rule", StringComparison.Ordinal)),
                    "The foliage page does not show the grass rule with an Apply action.");
            }

            TerrainGrassRuleSettings rule = editor.GrassRule;
            rule.Enabled = true; rule.Layer1Density = 1f; rule.Layer2Density = 0f; rule.Seed = 11; rule.Radius = 60f;
            editor.ApplyGrassRule(rule);
            GrassAssert(editor.GrassRule is { Enabled: true, Seed: 11 } && editor.IsDirty, "Apply did not set the grass rule.");
            editor.Undo();
            GrassAssert(!editor.GrassRule.Enabled, "Undo did not restore the previous grass rule.");
            editor.ApplyGrassRule(rule);
            expected = editor.GrassRule;
            editor.Save();
        }

        GrassAssert(TerrainNatureSerializer.LoadOrDefault(terrainFile).GrassRule.SameAs(expected), "Saving the terrain did not write the grass rule.");
        using var reopened = new Genesis.Application.Editors.Suite.Terrain.TerrainEditorControl(terrainFile, project.RootPath);
        GrassAssert(reopened.GrassRule.SameAs(expected), "Reopening the terrain lost the grass rule.");

        // The Terrain view grows and draws the same grass around its own camera.
        reopened.Viewport.BackendOverride = RenderBackendOption.SilkNetDx11;
        using Form host = UnattendedWindowing.NewHost(1360, 880);
        reopened.Dock = DockStyle.Fill; host.Controls.Add(reopened);
        Genesis.Application.Studio.Theme.ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
        reopened.Viewport.Host.TimerEnabled = false;
        reopened.Viewport.Camera.Target = new Vector3(0f, 0f, 4f);
        reopened.Viewport.Camera.Distance = 22f; reopened.Viewport.Camera.Pitch = -0.32f; reopened.Viewport.Camera.Yaw = 0f;
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        Bitmap? captured = null;
        while (DateTime.UtcNow < deadline)
        {
            System.Windows.Forms.Application.DoEvents();
            captured?.Dispose();
            captured = reopened.Viewport.CaptureFrame(2);
            TerrainGrassStatistics drawn = reopened.LastGrassRuleStatistics;
            if (captured is not null && drawn.TuftsDrawn > 500 && drawn.PendingCells == 0) break;
            Thread.Sleep(25);
        }

        GrassAssert(captured is not null, "The Terrain view produced no frame.");
        captured!.Save(Path.Combine(ctx.Captures, "terrain-grass-rule-editor.png"));
        ctx.Report.Images.Add(new ImageResult("terrain-grass-rule-editor", "terrain-grass-rule-editor.png", captured.Width, captured.Height, 0, 0));
        TerrainGrassStatistics stats = reopened.LastGrassRuleStatistics;
        captured.Dispose();
        Console.WriteLine($"[GrassRule] Terrain view: {stats.TuftsDrawn} tufts drawn from {stats.ResidentCells} cells.");
        GrassAssert(stats.TuftsDrawn > 500, $"The Terrain view drew {stats.TuftsDrawn} rule tufts.");

        // Painting the meadow over with dirt clears its grass in the view.
        reopened.SelectPaintLayer(1);
        reopened.SetPaintSelection([new(-40f, -40f), new(40f, -40f), new(40f, 40f), new(-40f, 40f)]);
        reopened.FillPaintSelection();
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && reopened.LastGrassRuleStatistics.TuftsResident > 0)
        {
            System.Windows.Forms.Application.DoEvents();
            using (reopened.Viewport.CaptureFrame(2)) { }
            Thread.Sleep(25);
        }

        GrassAssert(reopened.LastGrassRuleStatistics.TuftsResident == 0,
            $"Painting the meadow to dirt left {reopened.LastGrassRuleStatistics.TuftsResident} rule tufts in the Terrain view.");
    }

    private static void GrassSettingsRoundTrip(HeadlessContext ctx)
    {
        GrassAssert(!new TerrainNatureDocument().GrassRule.Enabled, "A new terrain grows grass around the camera without being asked.");
        string folder = Path.Combine(ctx.Workspace, "GrassRuleJson");
        Directory.CreateDirectory(folder);

        // A document saved before the rule existed: no grassRule at all.
        string oldPath = Path.Combine(folder, "Old.terrain.json");
        TerrainNatureSerializer.Save(oldPath, new TerrainNatureDocument());
        string sidecar = TerrainNatureSerializer.SidecarPath(oldPath);
        JsonObject json = JsonNode.Parse(File.ReadAllText(sidecar))!.AsObject();
        GrassAssert(json.ContainsKey("grassRule"), "The nature document does not save the grass rule.");
        json.Remove("grassRule");
        File.WriteAllText(sidecar, json.ToJsonString());
        TerrainNatureDocument old = TerrainNatureSerializer.LoadOrDefault(oldPath);
        GrassAssert(old.GrassRule is { Enabled: false }, "An old document did not load with grass around the camera off.");
        GrassAssert(old.GrassRule.Density(0) == 1f && Enumerable.Range(1, 7).All(layer => old.GrassRule.Density(layer) == 0f),
            "An old document did not get the default per-layer densities.");

        // An explicit null loads as the default too.
        json["grassRule"] = null;
        File.WriteAllText(sidecar, json.ToJsonString());
        GrassAssert(TerrainNatureSerializer.LoadOrDefault(oldPath).GrassRule is { Enabled: false }, "A null grass rule did not load as off.");

        string path = Path.Combine(folder, "Rule.terrain.json");
        var document = new TerrainNatureDocument();
        TerrainGrassRuleSettings rule = document.GrassRule;
        rule.Enabled = true; rule.Radius = 80f; rule.Spacing = 0.6f; rule.Seed = 42; rule.CellsPerFrame = 5;
        rule.MinimumScale = 0.6f; rule.MaximumScale = 1.4f; rule.MaximumSlopeDegrees = 33f; rule.CellSize = 12f;
        rule.MaximumDrawnTufts = 9000; rule.Species = FoliageSpecies.TallGrass; rule.FullDensityFraction = 0.3f;
        rule.GenerationBudgetMilliseconds = 2.5f;
        rule.Layer1Density = 0.9f; rule.Layer2Density = 0f; rule.Layer3Density = 0.25f; rule.Layer8Density = 0.75f;
        TerrainGrassRuleSettings expected = rule.Clone();
        TerrainNatureSerializer.Save(path, document);
        string text = File.ReadAllText(TerrainNatureSerializer.SidecarPath(path));
        GrassAssert(text.Contains("\"layerDensities\"", StringComparison.Ordinal) && !text.Contains("layer1Density", StringComparison.OrdinalIgnoreCase),
            "The densities should be saved once, as the layerDensities array.");
        TerrainNatureDocument loaded = TerrainNatureSerializer.LoadOrDefault(path);
        GrassAssert(loaded.GrassRule.SameAs(expected),
            $"The grass rule changed on its way through the file: {System.Text.Json.JsonSerializer.Serialize(loaded.GrassRule)}.");
        GrassAssert(loaded.GrassRule.Layer8Density == 0.75f && loaded.GrassRule.LayerDensities.Length == TerrainGrassRuleSettings.LayerCount,
            "The eighth layer's density did not survive the round trip.");
        GrassAssert(TerrainNatureSerializer.Clone(loaded).GrassRule.SameAs(expected), "Copying the document lost the grass rule.");

        // Out-of-range values are brought back in range on load, never thrown at the player.
        json = JsonNode.Parse(text)!.AsObject();
        json["grassRule"]!["spacing"] = -3;
        json["grassRule"]!["layerDensities"] = new JsonArray(4, -1, 0.5);
        File.WriteAllText(TerrainNatureSerializer.SidecarPath(path), json.ToJsonString());
        TerrainGrassRuleSettings clamped = TerrainNatureSerializer.LoadOrDefault(path).GrassRule;
        GrassAssert(clamped.Spacing >= 0.1f && clamped.Density(0) == 1f && clamped.Density(1) == 0f && clamped.Density(2) == 0.5f
            && clamped.LayerDensities.Length == TerrainGrassRuleSettings.LayerCount, "A bad grass rule was not brought back in range.");
    }

    private static void GrassInspectorShowsRule()
    {
        var rule = new TerrainGrassRuleSettings();
        using TableLayoutPanel form = Genesis.Application.Editors.Suite.Inspector.InspectorBuilder.BuildForObject(rule, "", inline: true);
        var names = new HashSet<string>(GrassControls(form).Select(control => control.Name), StringComparer.Ordinal);
        foreach (string property in new[] { "Enabled", "Radius", "Spacing", "Seed", "CellsPerFrame", "GenerationBudgetMilliseconds", "MaximumDrawnTufts", "MinimumScale", "MaximumScale" })
            GrassAssert(names.Contains("InspectorDrawer_" + property), $"The inspector does not show the grass rule's {property}.");
        for (int layer = 1; layer <= TerrainGrassRuleSettings.LayerCount; layer++)
            GrassAssert(names.Contains($"InspectorDrawer_Layer{layer}Density"), $"The inspector does not show layer {layer}'s density.");
        GrassAssert(!names.Contains("InspectorDrawer_LayerDensities"), "The raw density array should not be shown as well.");
        rule.Layer6Density = 0.4f;
        GrassAssert(rule.LayerDensities[5] == 0.4f, "The layer 6 field does not edit the saved density array.");
    }

    private static void GrassLayerDensity()
    {
        TerrainAsset terrain = GrassSplitTerrain(129, 0.5f, slope: 0f);
        float[] weights = new float[TerrainGrassLayerWeights.LayerCount];
        TerrainGrassLayerWeights.Sample(terrain, 10, 10, weights);
        GrassAssert(weights[0] == 1f && weights.Skip(1).All(weight => weight == 0f), "Meadow paint did not read as layer 1 only.");
        TerrainGrassLayerWeights.Sample(terrain, 120, 10, weights);
        GrassAssert(weights[1] == 1f && weights[0] == 0f && weights.Skip(4).All(weight => weight == 0f), "Dirt paint did not read as layer 2 only.");

        var rule = new TerrainGrassRuleSettings { Enabled = true, Spacing = 0.5f, CellSize = 8f };
        rule.Layer1Density = 1f; rule.Layer2Density = 0f;
        var field = new TerrainGrassField(terrain, rule);
        var tufts = new MeshInstanceData[field.CellCapacity];
        GrassAssert(field.CellCapacity == 256, $"A 8 m cell at 0.5 m spacing should hold 16 x 16 tufts, not {field.CellCapacity}.");
        // Terrain spans 64 m from x = -32: cells 0 to 3 are meadow, 4 to 7 are dirt.
        int meadow = field.GenerateCell(1, 3, tufts);
        int dirt = field.GenerateCell(6, 3, tufts);
        GrassAssert(meadow == 256, $"Meadow at density 1 should grow a tuft on every one of 256 spots, grew {meadow}.");
        GrassAssert(dirt == 0, $"Dirt at density 0 grew {dirt} tufts.");

        rule.Layer2Density = 0.5f;
        var half = new TerrainGrassField(terrain, rule);
        int halfDirt = half.GenerateCell(6, 3, tufts);
        GrassAssert(halfDirt is > 96 and < 160, $"Dirt at density 0.5 should grow about 128 of 256 tufts, grew {halfDirt}.");

        // A density for a layer that is not painted grows nothing.
        var unpainted = new TerrainGrassRuleSettings { Enabled = true, Spacing = 0.5f, CellSize = 8f };
        unpainted.Layer1Density = 0f; unpainted.Layer6Density = 1f;
        GrassAssert(new TerrainGrassField(terrain, unpainted).GenerateCell(1, 3, tufts) == 0, "An unpainted layer's density grew grass.");
    }

    private static void GrassHeightAndSlope()
    {
        TerrainAsset gentle = GrassSplitTerrain(129, 0.5f, slope: 0.08f);
        var rule = new TerrainGrassRuleSettings { Enabled = true, Spacing = 0.5f, CellSize = 8f };
        var field = new TerrainGrassField(gentle, rule);
        var tufts = new MeshInstanceData[field.CellCapacity];
        int count = field.GenerateCell(2, 2, tufts);
        GrassAssert(count == field.CellCapacity, $"A gentle meadow slope lost tufts ({count}).");
        float scaleMin = float.MaxValue, scaleMax = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            Vector3 position = tufts[i].World.Translation;
            float ground = gentle.SampleHeight(position.X, position.Z);
            GrassAssert(MathF.Abs(position.Y - ground) < 0.001f, $"A tuft floats or sinks: {position.Y} on ground {ground}.");
            float scale = new Vector3(tufts[i].World.M11, tufts[i].World.M12, tufts[i].World.M13).Length();
            scaleMin = MathF.Min(scaleMin, scale); scaleMax = MathF.Max(scaleMax, scale);
        }

        GrassAssert(scaleMin >= rule.MinimumScale - 0.001f && scaleMax <= rule.MaximumScale + 0.001f && scaleMax - scaleMin > 0.2f,
            $"Tuft sizes {scaleMin:F2} to {scaleMax:F2} do not spread across the rule's range.");

        // About 58 degrees: steeper than the default 40 degree limit.
        TerrainAsset steep = GrassSplitTerrain(129, 0.5f, slope: 1.6f, minHeight: -10f, maxHeight: 200f);
        GrassAssert(new TerrainGrassField(steep, rule).GenerateCell(2, 2, tufts) == 0, "Grass grew on a slope steeper than the rule allows.");
        var anySlope = rule.Clone();
        anySlope.MaximumSlopeDegrees = 90f;
        GrassAssert(new TerrainGrassField(steep, anySlope).GenerateCell(2, 2, tufts) == field.CellCapacity, "A 90 degree limit still rejected tufts.");
    }

    private static void GrassDeterministic()
    {
        TerrainAsset terrain = GrassSplitTerrain(129, 0.5f, slope: 0.03f);
        var rule = new TerrainGrassRuleSettings { Enabled = true, Seed = 7 };
        rule.Layer2Density = 0.35f;
        var first = new MeshInstanceData[new TerrainGrassField(terrain, rule).CellCapacity];
        var second = new MeshInstanceData[first.Length];
        var other = new MeshInstanceData[first.Length];
        foreach ((int x, int z) in new[] { (0, 0), (3, 5), (6, 2) })
        {
            int a = new TerrainGrassField(terrain, rule).GenerateCell(x, z, first);
            int b = new TerrainGrassField(terrain, rule).GenerateCell(x, z, second);
            GrassAssert(a == b && first.AsSpan(0, a).ToArray().Zip(second.AsSpan(0, b).ToArray())
                .All(pair => pair.First.World.Equals(pair.Second.World) && pair.First.Tint.Equals(pair.Second.Tint)),
                $"The same seed grew different tufts in cell {x},{z}.");
        }

        var reseeded = rule.Clone();
        reseeded.Seed = 8;
        int count = new TerrainGrassField(terrain, rule).GenerateCell(1, 1, first);
        int otherCount = new TerrainGrassField(terrain, reseeded).GenerateCell(1, 1, other);
        GrassAssert(count > 0 && otherCount > 0 && !first[0].World.Equals(other[0].World), "Changing the seed did not move the tufts.");

        // A cell grown, recycled and grown again holds the same tufts.
        var field = new TerrainGrassField(terrain, rule);
        for (int i = 0; i < 64 && (field.Pending || i == 0); i++) field.Update(new Vector3(-20f, 0f, -20f), Matrix4x4.Identity);
        (int cellX, int cellZ) = field.CellAt(-20f, -20f);
        int before = field.ResidentTufts(cellX, cellZ);
        field.RecycleAll();
        GrassAssert(field.ResidentTufts(cellX, cellZ) == -1, "Recycling left a cell resident.");
        for (int i = 0; i < 64 && (field.Pending || i == 0); i++) field.Update(new Vector3(-20f, 0f, -20f), Matrix4x4.Identity);
        GrassAssert(before > 0 && field.ResidentTufts(cellX, cellZ) == before, "A regrown cell holds different grass.");
    }

    private static void GrassRecycling()
    {
        TerrainAsset terrain = GrassSplitTerrain(513, 1f, slope: 0.02f);
        var rule = new TerrainGrassRuleSettings { Enabled = true, Radius = 40f, CellSize = 8f, CellsPerFrame = 4 };
        rule.Layer2Density = 0.5f;
        var field = new TerrainGrassField(terrain, rule);
        int side = (int)MathF.Ceiling(2f * (rule.Radius + rule.CellSize) / rule.CellSize) + 2;
        int residentLimit = side * side;
        int maxResident = 0, maxGenerated = 0, frames = 0;
        long totalGenerated = 0;
        Vector3 camera = new(-200f, 5f, -200f);
        for (; frames < 400; frames++)
        {
            camera += new Vector3(1f, 0f, 0.6f);
            field.Update(camera, Matrix4x4.Identity);
            TerrainGrassStatistics stats = field.Statistics;
            GrassAssert(stats.CellsGeneratedLastUpdate <= rule.CellsPerFrame,
                $"Frame {frames} grew {stats.CellsGeneratedLastUpdate} cells, over the budget of {rule.CellsPerFrame}.");
            maxGenerated = Math.Max(maxGenerated, stats.CellsGeneratedLastUpdate);
            totalGenerated += stats.CellsGeneratedLastUpdate;
            maxResident = Math.Max(maxResident, stats.ResidentCells);
            GrassAssert(stats.ResidentCells <= residentLimit, $"Resident cells grew to {stats.ResidentCells}, past the {residentLimit} the radius can need.");
        }

        TerrainGrassStatistics moved = field.Statistics;
        GrassAssert(moved.CellsRecycled > 50 && moved.CellsReused > 50, $"Cells were not recycled and reused ({moved.CellsRecycled} recycled, {moved.CellsReused} reused).");
        GrassAssert(moved.CellsAllocated <= residentLimit + rule.CellsPerFrame,
            $"{moved.CellsAllocated} cell arrays were allocated for at most {residentLimit} resident: the pool is not reused.");
        GrassAssert(moved.CellsAllocated + moved.CellsReused == totalGenerated, "Every grown cell should come from the pool or a new array.");

        // Standing still, the ring fills and then nothing more is grown.
        int settle = 0;
        while (field.Pending && settle++ < 500) field.Update(camera, Matrix4x4.Identity);
        field.Update(camera, Matrix4x4.Identity);
        GrassAssert(!field.Pending && field.Statistics.CellsGeneratedLastUpdate == 0, "Grass kept growing with the camera standing still.");
        (int cx, int cz) = field.CellAt(camera.X, camera.Z);
        int reach = (int)MathF.Floor(rule.Radius / rule.CellSize) - 1;
        for (int z = cz - reach; z <= cz + reach; z++)
        for (int x = cx - reach; x <= cx + reach; x++)
        {
            if ((x - cx) * (x - cx) + (z - cz) * (z - cz) > reach * reach) continue;
            GrassAssert(field.ResidentTufts(x, z) >= 0, $"Cell {x},{z} within the radius was never grown.");
        }

        long allocated = field.Statistics.CellsAllocated;
        for (int i = 0; i < 200; i++) field.Update(camera + new Vector3(MathF.Sin(i * 0.2f) * 30f, 0f, 0f), Matrix4x4.Identity);
        // Once the pool has filled, walking about allocates nothing at all.
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 200; i < 400; i++) field.Update(camera + new Vector3(MathF.Sin(i * 0.2f) * 30f, 0f, 0f), Matrix4x4.Identity);
        long walkBytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        GrassAssert(walkBytes < 1024, $"Walking about over a filled pool allocated {walkBytes} bytes.");
        GrassAssert(field.Statistics.CellsAllocated <= residentLimit && field.Statistics.CellsReused > moved.CellsReused,
            $"Walking back and forth allocated {field.Statistics.CellsAllocated - allocated} new cells instead of reusing the pool.");
        Console.WriteLine($"[GrassRule] recycling: max resident {maxResident} (limit {residentLimit}), max grown per frame {maxGenerated}, "
            + $"allocated {field.Statistics.CellsAllocated}, reused {field.Statistics.CellsReused}, recycled {field.Statistics.CellsRecycled}.");
    }

    private static void GrassRefresh()
    {
        TerrainAsset terrain = GrassSplitTerrain(129, 0.5f, slope: 0f);
        var rule = new TerrainGrassRuleSettings { Enabled = true, CellsPerFrame = 2, GenerationBudgetMilliseconds = 33f };
        var field = new TerrainGrassField(terrain, rule);
        Vector3 camera = new(-16f, 2f, 0f);
        for (int i = 0; i < 100 && (field.Pending || i == 0); i++) field.Update(camera, Matrix4x4.Identity);
        int before = field.Statistics.TuftsResident;
        (int cx, int cz) = field.CellAt(-20f, 4f);
        GrassAssert(field.ResidentTufts(cx, cz) == field.CellCapacity, "The meadow cell was not full before painting.");

        // Paint the whole meadow over with dirt (layer 2), as a Fill would.
        for (int z = 0; z < terrain.ResolutionZ; z++)
        for (int x = 0; x < terrain.ResolutionX; x++) terrain.SetSplat(x, z, 0, 255, 0, 0);
        field.Refresh();
        field.Update(camera, Matrix4x4.Identity);
        TerrainGrassStatistics partway = field.Statistics;
        GrassAssert(partway.CellsGeneratedLastUpdate == 2 && partway.TuftsResident > 0 && partway.ResidentCells == 64,
            "A refresh dropped the grass instead of regrowing it two cells a frame.");
        GrassAssert(partway.CellsAllocated == 64, "A refresh took new cells from the heap instead of regrowing in place.");
        for (int i = 0; i < 100 && field.Pending; i++) field.Update(camera, Matrix4x4.Identity);
        GrassAssert(before > 0 && field.Statistics.TuftsResident == 0 && field.ResidentTufts(cx, cz) == 0,
            $"After painting the meadow to dirt {field.Statistics.TuftsResident} tufts are left.");
    }

    private const int GrassWidth = 960, GrassHeight = 540;

    private static void GrassDx11Capture(HeadlessContext ctx)
    {
        string root = GrassProject(ctx, "GrassRuleCapture");
        TerrainAsset terrain = GrassSplitTerrain(129, 0.5f, slope: 0f);
        RoomAsset room = GrassRoom(root, terrain, "Capture", enabled: false, out string binary);
        Vector3 eye = new(0f, 3.2f, -27f), target = new(0f, 0f, 2f);

        byte[] off = GrassRenderOnce(ctx, root, room, eye, target, "terrain-grass-rule-dx11-off", out _);
        GrassSaveNature(binary, enabled: true);
        byte[] on = GrassRenderOnce(ctx, root, room, eye, target, "terrain-grass-rule-dx11", out IReadOnlyList<TerrainGrassStatistics> stats);
        GrassAssert(stats.Count == 1 && stats[0].TuftsDrawn > 1000, $"The runtime drew {(stats.Count == 1 ? stats[0].TuftsDrawn : 0)} rule tufts.");

        // Meadow is the half of the terrain at x < 0; find which half of the screen shows it.
        Matrix4x4 viewProjection = GrassView(eye, target) * GrassProjection();
        Vector4 meadowPoint = Vector4.Transform(new Vector4(-10f, 0f, 5f, 1f), viewProjection);
        bool meadowLeft = meadowPoint.X / meadowPoint.W < 0f;
        int meadow = 0, dirt = 0;
        int margin = GrassWidth / 12;
        for (int y = GrassHeight / 3; y < GrassHeight; y++)
        for (int x = 0; x < GrassWidth; x++)
        {
            if (Math.Abs(x - GrassWidth / 2) < margin) continue;
            int index = (y * GrassWidth + x) * 4;
            int change = Math.Abs(on[index] - off[index]) + Math.Abs(on[index + 1] - off[index + 1]) + Math.Abs(on[index + 2] - off[index + 2]);
            bool green = on[index + 1] > on[index] && on[index + 1] > on[index + 2];
            if (change < 30 || !green) continue;
            if ((x < GrassWidth / 2) == meadowLeft) meadow++;
            else dirt++;
        }

        Console.WriteLine($"[GrassRule] DX11 capture: {meadow} green grass pixels over the meadow, {dirt} over the dirt; "
            + $"{stats[0].TuftsDrawn} tufts drawn ({stats[0].NearTuftsDrawn} near) from {stats[0].ResidentCells} cells.");
        GrassAssert(meadow > 20000, $"Too little grass over the meadow half ({meadow} pixels).");
        GrassAssert(dirt < meadow / 100, $"Grass appeared over the dirt half ({dirt} pixels against {meadow}).");
    }

    private static byte[] GrassRenderOnce(HeadlessContext ctx, string root, RoomAsset room, Vector3 eye, Vector3 target, string capture,
        out IReadOnlyList<TerrainGrassStatistics> stats)
    {
        using Form host = UnattendedWindowing.NewHost(GrassWidth, GrassHeight);
        UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11);
        renderer.Initialize(host.Handle, GrassWidth, GrassHeight);
        using var runtime = new RoomTerrainSubsystem(root, room, null!);
        MeshDrawCall[] draws = new MeshDrawCall[runtime.GetMeshDrawCapacity(renderer) + 64];
        GrassPrepare(renderer);
        byte[] frame = Array.Empty<byte>();
        // Enough frames for every cell in reach to grow at the rule's per-frame budget.
        for (int i = 0; i < 120; i++)
        {
            frame = GrassFrame(renderer, runtime, draws, eye, target, read: true, out _);
            if (runtime.GrassRuleStatistics.All(stat => stat.PendingCells == 0) && i > 4) break;
        }

        frame = GrassFrame(renderer, runtime, draws, eye, target, read: true, out _);
        stats = runtime.GrassRuleStatistics;
        using Bitmap bitmap = new(GrassWidth, GrassHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, GrassWidth, GrassHeight), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { System.Runtime.InteropServices.Marshal.Copy(frame, 0, data.Scan0, Math.Min(frame.Length, GrassWidth * GrassHeight * 4)); }
        finally { bitmap.UnlockBits(data); }
        bitmap.Save(Path.Combine(ctx.Captures, capture + ".png"));
        ctx.Report.Images.Add(new ImageResult(capture, capture + ".png", GrassWidth, GrassHeight, 0, 0));
        return frame;
    }

    private static void GrassDx11FrameCost(HeadlessContext ctx)
    {
        string root = GrassProject(ctx, "GrassRuleCost");
        // 512 m of meadow with a dirt road down the middle, gently rolling.
        var terrain = new TerrainAsset(513, 513, 1f, -256f, -256f, -10f, 30f);
        for (int z = 0; z < 513; z++)
        for (int x = 0; x < 513; x++)
        {
            float wx = -256f + x, wz = -256f + z;
            terrain.SetHeight(x, z, 3f + MathF.Sin(wx * 0.03f) * 2f + MathF.Cos(wz * 0.025f) * 2.5f);
            bool road = MathF.Abs(wx) < 6f;
            terrain.SetSplat(x, z, road ? (byte)0 : (byte)255, road ? (byte)255 : (byte)0, 0, 0);
        }

        RoomAsset room = GrassRoom(root, terrain, "Cost", enabled: false, out string binary);
        (double offFrame, double offSubmit, double offGpu, _, _, _, _) = GrassMeasure(root, room, terrain);
        GrassSaveNature(binary, enabled: true);
        (double onFrame, double onSubmit, double onGpu, double generation, double generationMax, double generationP95, TerrainGrassStatistics last) =
            GrassMeasure(root, room, terrain);
        string report = string.Join(Environment.NewLine,
            "Grass around the camera: frame cost on DX11, 960 x 540, 240 frames moving 1 m a frame over a 512 m meadow "
            + "(radius 100 m, spacing 0.5 m, 8 m cells, at most 8 cells or 1.5 ms of growing a frame, 16,000 tufts drawn at most).",
            $"Without grass rule: {offFrame:F2} ms a frame (submit, draw and read back), terrain submit {offSubmit:F2} ms, GPU {offGpu:F2} ms.",
            $"With grass rule:    {onFrame:F2} ms a frame (submit, draw and read back), terrain submit {onSubmit:F2} ms, GPU {onGpu:F2} ms.",
            $"Grass generation: {generation:F3} ms a frame on average, {generationP95:F3} ms at the 95th percentile, {generationMax:F3} ms at most "
            + "(the worst frames are a garbage collection landing inside the timed span; growing itself allocates nothing once warm).",
            $"Last frame: {last.TuftsDrawn} tufts drawn ({last.NearTuftsDrawn} near), {last.VisibleCells} visible of {last.ResidentCells} resident cells, "
            + $"{last.TuftsResident} tufts resident, {last.PendingCells} cells still waiting; "
            + $"{last.CellsAllocated} cells allocated, {last.CellsReused} reused, {last.CellsRecycled} recycled.");
        Console.WriteLine(report);
        Directory.CreateDirectory(ctx.Logs);
        File.WriteAllText(Path.Combine(ctx.Logs, "terrain-grass-frame-cost.txt"), report + Environment.NewLine);
        GrassAssert(last.TuftsDrawn > 1000 && last.CellsRecycled > 0, "The moving camera drew or recycled no rule grass.");
        GrassAssert(generationP95 < 4.0, $"Growing grass took {generationP95:F1} ms at the 95th percentile; the per-frame budget is not holding.");
    }

    private static (double Frame, double Submit, double Gpu, double Generation, double GenerationMax, double GenerationP95, TerrainGrassStatistics Last)
        GrassMeasure(string root, RoomAsset room, TerrainAsset terrain)
    {
        using Form host = UnattendedWindowing.NewHost(GrassWidth, GrassHeight);
        UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11);
        renderer.Initialize(host.Handle, GrassWidth, GrassHeight);
        using var runtime = new RoomTerrainSubsystem(root, room, null!);
        MeshDrawCall[] draws = new MeshDrawCall[runtime.GetMeshDrawCapacity(renderer) + 64];
        GrassPrepare(renderer);
        Vector3 Eye(float z) => new(20f, terrain.SampleHeight(20f, z) + 1.8f, z);
        // Standing at the start until the grass in reach has grown and the GPU has warmed up.
        for (int i = 0; i < 90; i++) GrassFrame(renderer, runtime, draws, Eye(-200f), Eye(-180f) - Vector3.UnitY * 1.5f, read: true, out _);

        const int frames = 240;
        double frameTotal = 0, submitTotal = 0, gpuTotal = 0, generationTotal = 0, generationMax = 0;
        var generations = new List<double>(frames);
        for (int i = 0; i < frames; i++)
        {
            float z = -200f + i;
            long started = Stopwatch.GetTimestamp();
            GrassFrame(renderer, runtime, draws, Eye(z), Eye(z + 20f) - Vector3.UnitY * 1.5f, read: true, out double submit);
            frameTotal += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            submitTotal += submit;
            gpuTotal += renderer.LastGpuMilliseconds;
            double generation = runtime.GrassRuleStatistics.Sum(stat => stat.GenerationMilliseconds);
            generationTotal += generation;
            generations.Add(generation);
            generationMax = Math.Max(generationMax, generation);
        }

        generations.Sort();
        double p95 = generations[(int)(generations.Count * 0.95)];
        TerrainGrassStatistics last = runtime.GrassRuleStatistics.FirstOrDefault();
        return (frameTotal / frames, submitTotal / frames, gpuTotal / frames, generationTotal / frames, generationMax, p95, last);
    }

    private static void GrassPrepare(IRenderController renderer)
    {
        Mesh3DState state = Mesh3DState.Default;
        state.FogEnabled = false; state.ShowFloor = false; state.ShowSunVisual = false;
        renderer.SetMesh3DState(state);
        renderer.Set3DFrameActive(true);
    }

    private static byte[] GrassFrame(IRenderController renderer, RoomTerrainSubsystem runtime, MeshDrawCall[] draws,
        Vector3 eye, Vector3 target, bool read, out double submitMilliseconds)
    {
        Matrix4x4 view = GrassView(eye, target), projection = GrassProjection();
        renderer.SetCamera3D(view, projection);
        renderer.BeginFrame();
        renderer.Clear(0.48f, 0.64f, 0.86f);
        long started = Stopwatch.GetTimestamp();
        int count = 0;
        runtime.SubmitPreviewMeshes(eye, view * projection, draws, ref count, renderer);
        for (int i = 0; i < count; i++) renderer.DrawMesh(draws[i]);
        submitMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        renderer.EndFrame();
        byte[] frame = Array.Empty<byte>();
        if (read)
            GrassAssert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out frame) && width == GrassWidth && height == GrassHeight,
                "No DX11 frame could be read back.");
        renderer.Present();
        return frame;
    }

    private static Matrix4x4 GrassView(Vector3 eye, Vector3 target) => Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
    private static Matrix4x4 GrassProjection() =>
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, GrassWidth / (float)GrassHeight, 0.1f, 1500f);

    private static string GrassProject(HeadlessContext ctx, string name) =>
        new ProjectService().CreateProject(Path.Combine(ctx.Workspace, name), name).RootPath;

    /// <summary>A room holding one terrain node, its heights saved under the project and a nature document beside them.</summary>
    private static RoomAsset GrassRoom(string root, TerrainAsset terrain, string name, bool enabled, out string binary)
    {
        string folder = Path.Combine(root, "Terrains");
        Directory.CreateDirectory(folder);
        binary = Path.Combine(folder, name + ".gterrain");
        terrain.Save(binary);
        GrassSaveNature(binary, enabled);
        RoomAsset room = RoomAsset.Create(name, RoomDimension.ThreeD);
        room.Nodes.Add(new RoomNode
        {
            Name = name + " ground",
            Kind = RoomNodeKind.Terrain,
            LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = "Terrains/" + name + ".gterrain" },
            Transform = new RoomTransform { ScaleX = 1, ScaleY = 1, ScaleZ = 1 },
        });
        return room;
    }

    private static void GrassSaveNature(string binary, bool enabled)
    {
        var nature = new TerrainNatureDocument();
        nature.GrassRule.Enabled = enabled;
        nature.GrassRule.Layer1Density = 1f;
        nature.GrassRule.Layer2Density = 0f;
        TerrainNatureSerializer.Save(binary, nature);
    }

    /// <summary>A square terrain whose first half along x is painted layer 1 (meadow) and second half layer 2 (dirt).</summary>
    private static TerrainAsset GrassSplitTerrain(int resolution, float cell, float slope, float minHeight = -20f, float maxHeight = 40f)
    {
        float size = (resolution - 1) * cell;
        var terrain = new TerrainAsset(resolution, resolution, cell, -size / 2f, -size / 2f, minHeight, maxHeight);
        for (int z = 0; z < resolution; z++)
        for (int x = 0; x < resolution; x++)
        {
            terrain.SetHeight(x, z, x * cell * slope);
            bool meadow = x < resolution / 2;
            terrain.SetSplat(x, z, meadow ? (byte)255 : (byte)0, meadow ? (byte)0 : (byte)255, 0, 0);
        }

        return terrain;
    }

    private static IEnumerable<Control> GrassControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in GrassControls(child)) yield return nested;
        }
    }

    private static void GrassAssert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
