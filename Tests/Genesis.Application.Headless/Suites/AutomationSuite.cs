using System.Text.Json.Nodes;
using Genesis.Application.Core.Automation;
using Genesis.Application.Core.Projects;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Studio's automation layer (Documentation/Automation.md): the commands describe themselves, the
/// resource commands do what the Assets dock does, destructive ones need confirming, changes are
/// checked and written to the audit log, mistakes come back as explanations, and the command line and
/// the MCP server serve the same commands.
/// </summary>
internal static class AutomationSuite
{
    public static void Run(HeadlessContext ctx)
    {
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "automation"), "Automation", "Blank");
        AutomationContext context = AutomationContext.For(project);
        context.Caller = "test";
        AutomationRegistry registry = AutomationRegistry.CreateDefault();
        AutomationResult Call(string name, string json = "{}") => registry.Invoke(context, name, JsonNode.Parse(json) as JsonObject);
        string PathOf(AutomationResult result) => result.Data?["path"]?.GetValue<string>() ?? string.Empty;

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.CommandsListThemselvesWithSchemas", () =>
        {
            AutomationResult list = Call(AutomationRegistry.ListCommand);
            HeadlessHarness.Assert(list.Ok && list.Data is JsonArray, "commands.list did not list the commands: " + list.Message);
            JsonObject? create = ((JsonArray)list.Data!).OfType<JsonObject>().FirstOrDefault(c => c["name"]?.GetValue<string>() == "resource.create");
            HeadlessHarness.Assert(create is not null, "resource.create was not listed.");
            string schema = create!["input"]!.ToJsonString();
            HeadlessHarness.Assert(schema.Contains("\"GameObject\"", StringComparison.Ordinal) && schema.Contains("\"required\":[\"kind\",\"name\"]", StringComparison.Ordinal),
                "resource.create's schema did not give the kinds and required fields: " + schema);
            JsonObject? delete = ((JsonArray)list.Data!).OfType<JsonObject>().FirstOrDefault(c => c["name"]?.GetValue<string>() == "resource.delete");
            HeadlessHarness.Assert(delete?["destructive"]?.GetValue<bool>() == true && delete["input"]!.ToJsonString().Contains("\"confirm\"", StringComparison.Ordinal),
                "resource.delete was not marked destructive with a confirm field.");
        });

        string hero = string.Empty;
        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.ResourcesAreMadeMovedAndRenamedAsTheAssetsDockDoes", () =>
        {
            AutomationResult folder = Call("folder.create", """{ "parent": "Assets/Objects", "name": "Auto" }""");
            HeadlessHarness.Assert(folder.Ok && Directory.Exists(context.Resolve(PathOf(folder))), "folder.create made no folder: " + folder.Message);
            AutomationResult made = Call("resource.create", """{ "kind": "GameObject", "folder": "Assets/Objects/Auto", "name": "Hero" }""");
            HeadlessHarness.Assert(made.Ok && File.Exists(context.Resolve(PathOf(made))) && made.Changed.Contains(PathOf(made)),
                "resource.create made no object: " + made.Message);
            AutomationResult renamed = Call("resource.rename", $$"""{ "path": "{{PathOf(made)}}", "newName": "Player" }""");
            hero = PathOf(renamed);
            HeadlessHarness.Assert(renamed.Ok && renamed.Data?["name"]?.GetValue<string>() == "Player" && File.Exists(context.Resolve(hero)), "resource.rename did not rename: " + renamed.ToJson().ToJsonString());
            AutomationResult copy = Call("resource.duplicate", $$"""{ "path": "{{hero}}" }""");
            HeadlessHarness.Assert(copy.Ok && File.Exists(context.Resolve(PathOf(copy))) && PathOf(copy) != hero, "resource.duplicate made no copy: " + copy.Message);
            AutomationResult moved = Call("resource.move", $$"""{ "path": "{{PathOf(copy)}}", "folder": "Assets/Objects" }""");
            HeadlessHarness.Assert(moved.Ok && File.Exists(context.Resolve(PathOf(moved))) && !File.Exists(context.Resolve(PathOf(copy))), "resource.move did not move: " + moved.Message);
            AutomationResult found = Call("resource.find", """{ "term": "Player" }""");
            HeadlessHarness.Assert(found.Ok && found.Data is JsonArray { Count: >= 1 }, "resource.find did not find the renamed object: " + found.Message);
            string audit = File.Exists(context.AuditLogPath) ? File.ReadAllText(context.AuditLogPath) : string.Empty;
            HeadlessHarness.Assert(audit.Contains("\ttest\tresource.rename\tok\t", StringComparison.Ordinal) && audit.Contains("\tresource.move\t", StringComparison.Ordinal),
                "The changes were not written to the audit log: " + audit);
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.DestructiveCommandsNeedConfirming", () =>
        {
            AutomationResult refused = Call("resource.delete", $$"""{ "path": "{{hero}}" }""");
            HeadlessHarness.Assert(!refused.Ok && refused.Message.Contains("confirm", StringComparison.Ordinal) && File.Exists(context.Resolve(hero)),
                "resource.delete ran without confirm: " + refused.Message);
            AutomationResult deleted = Call("resource.delete", $$"""{ "path": "{{hero}}", "confirm": true }""");
            HeadlessHarness.Assert(deleted.Ok && !File.Exists(context.Resolve(hero)), "resource.delete with confirm left the object: " + deleted.Message);
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.ReplaceIsPreviewedThenChangesScripts", () =>
        {
            AutomationResult script = Call("resource.create", """{ "kind": "PgslScript", "folder": "Assets", "name": "Score Lib" }""");
            string full = context.Resolve(PathOf(script));
            File.WriteAllText(full, "function ScoreAdd(n) {\n    score = score + n;\n    return score;\n}\n");
            AutomationResult preview = Call("text.replacePreview", """{ "find": "score", "replace": "points", "extensions": [".pgsl"] }""");
            JsonObject? first = (preview.Data as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            HeadlessHarness.Assert(preview.Ok && first?["count"]?.GetValue<int>() == 3 && File.ReadAllText(full).Contains("score", StringComparison.Ordinal),
                "text.replacePreview did not show three matches without changing the file: " + preview.ToJson().ToJsonString());
            AutomationResult replaced = Call("text.replace", """{ "find": "score", "replace": "points", "extensions": [".pgsl"], "confirm": true }""");
            HeadlessHarness.Assert(replaced.Ok && File.ReadAllText(full).Contains("points = points + n;", StringComparison.Ordinal) && replaced.Changed.Count == 1,
                "text.replace did not change the script: " + replaced.ToJson().ToJsonString());
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.ScriptProblemsComeBackWithTheirLine", () =>
        {
            AutomationResult script = Call("resource.create", """{ "kind": "PgslScript", "folder": "Assets", "name": "Broken Lib" }""");
            File.WriteAllText(context.Resolve(PathOf(script)), "function Broken() {\n    var x = ;\n}\n");
            AutomationResult check = Call("project.check");
            HeadlessHarness.Assert(check.Issues.Any(i => i.Check == "scripts" && i.Severity == "error" && i.Line is not null && (i.Path ?? string.Empty).Contains("Broken", StringComparison.Ordinal)),
                "project.check did not report the broken script with its line: " + check.ToJson().ToJsonString());
            File.Delete(context.Resolve(PathOf(script)));
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.MistakesAreExplained", () =>
        {
            HeadlessHarness.Assert(Call("resource.explode").Message.Contains("There is no command", StringComparison.Ordinal), "An unknown command was not explained.");
            HeadlessHarness.Assert(Call("resource.create", """{ "kind": "GameObject" }""").Message.Contains("needs 'name'", StringComparison.Ordinal), "A missing name was not explained.");
            HeadlessHarness.Assert(Call("resource.create", """{ "kind": "GameObject", "name": "A", "colour": "red" }""").Message.Contains("does not fit", StringComparison.Ordinal),
                "An unknown field was not refused.");
            AutomationResult outside = Call("resource.read", """{ "path": "../outside.txt" }""");
            HeadlessHarness.Assert(!outside.Ok && outside.Message.Contains("outside the project", StringComparison.Ordinal), "A path outside the project was not refused: " + outside.Message);
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.TerrainGrassAndScatterLayersAreSetByCommand", () =>
        {
            string terrain = PathOf(Call("resource.create", """{ "kind": "Terrain", "folder": "Assets", "name": "Field" }"""));
            string model = PathOf(Call("resource.create", """{ "kind": "Model", "folder": "Assets", "name": "Flower" }"""));
            HeadlessHarness.Assert(terrain.EndsWith(".terrain.json", StringComparison.Ordinal) && model.Length > 0, "The terrain or model was not made: " + terrain + " " + model);
            AutomationResult grass = Call("terrain.setGrass", $$"""{ "terrain": "{{terrain}}", "enabled": true, "radius": 60, "layerDensities": [1, 0.3], "maximumDrawnTufts": 20000 }""");
            AutomationResult layer = Call("terrain.addScatterLayer", $$"""{ "terrain": "{{terrain}}", "name": "Flowers", "model": "{{model}}", "densityPerHectare": 400 }""");
            HeadlessHarness.Assert(grass.Ok && layer.Ok, "terrain.setGrass or terrain.addScatterLayer failed: " + grass.Message + " / " + layer.Message);
            var nature = Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(context.Resolve(terrain));
            HeadlessHarness.Assert(nature.GrassRule.Enabled && Math.Abs(nature.GrassRule.Radius - 60f) < 0.01f
                && Math.Abs(nature.GrassRule.Density(1) - 0.3f) < 0.001f && nature.GrassRule.Density(0) == 1f && nature.GrassRule.Density(2) == 0f
                && nature.GrassRule.MaximumDrawnTufts == 20000
                && nature.ScatterLayers.Count == 1 && nature.ScatterLayers[0].Model == model && Math.Abs(nature.ScatterLayers[0].DensityPerHectare - 400f) < 0.01f,
                "The engine did not read back the grass rule and scatter layer the commands wrote.");
            AutomationResult updated = Call("terrain.updateScatterLayer", $$"""{ "terrain": "{{terrain}}", "layer": "Flowers", "densityPerHectare": 800 }""");
            HeadlessHarness.Assert(updated.Ok && Math.Abs(Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(context.Resolve(terrain)).ScatterLayers[0].DensityPerHectare - 800f) < 0.01f,
                "terrain.updateScatterLayer did not change the density: " + updated.Message);
            HeadlessHarness.Assert(!Call("terrain.removeScatterLayer", $$"""{ "terrain": "{{terrain}}", "layer": "Flowers" }""").Ok, "Removing a layer did not need confirming.");
            AutomationResult removed = Call("terrain.removeScatterLayer", $$"""{ "terrain": "{{terrain}}", "layer": "Flowers", "confirm": true }""");
            HeadlessHarness.Assert(removed.Ok && Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(context.Resolve(terrain)).ScatterLayers.Count == 0,
                "terrain.removeScatterLayer left the layer: " + removed.Message);
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.TerrainIsMadeAndSculptedAsTheTerrainEditorDoes", () =>
        {
            AutomationResult made = Call("terrain.create", """{ "name": "Twin Land", "preset": "Flatlands", "resolutionX": 65, "resolutionZ": 65, "seed": 4 }""");
            HeadlessHarness.Assert(made.Ok && File.Exists(context.Resolve(PathOf(made)) + ".gterrain"), "terrain.create did not make the terrain and its heights: " + made.ToJson().ToJsonString());
            string terrain = PathOf(made);
            JsonObject extent = made.Data!["extent"]!.AsObject();
            float cx = (extent["minX"]!.GetValue<float>() + extent["maxX"]!.GetValue<float>()) / 2f, cz = (extent["minZ"]!.GetValue<float>() + extent["maxZ"]!.GetValue<float>()) / 2f;

            // The editor's own brush on the same terrain, without saving: the command must move the ground identically.
            ushort[] byEditor;
            float radius, strength;
            using (var editor = new Genesis.Application.Editors.Suite.Terrain.TerrainEditorControl(context.Resolve(terrain), project.RootPath))
            {
                radius = editor.BrushRadius;
                strength = editor.BrushStrength;
                editor.ApplyBrushAt(cx, cz, Genesis.Application.Editors.Suite.Terrain.TerrainEditorControl.TerrainBrush.Raise);
                byEditor = (ushort[])editor.Terrain.HeightsData.Clone();
            }
            AutomationResult raised = Call("terrain.sculpt", $$"""{ "terrain": "{{terrain}}", "brush": "Raise", "points": [[{{cx}}, {{cz}}]], "radius": {{radius}}, "strength": {{strength}} }""");
            ushort[] byCommand = Genesis.World.Terrain.TerrainAsset.Load(context.Resolve(terrain) + ".gterrain").HeightsData;
            HeadlessHarness.Assert(raised.Ok && byCommand.AsSpan().SequenceEqual(byEditor) && byCommand.Distinct().Count() > 1,
                "terrain.sculpt did not raise the ground exactly as the editor's brush does: " + raised.ToJson().ToJsonString());

            AutomationResult info = Call("terrain.info", $$"""{ "terrain": "{{terrain}}", "points": [[{{cx}}, {{cz}}], [{{extent["minX"]}}, {{extent["minZ"]}}]] }""");
            JsonArray heights = info.Data!["pointHeights"]!.AsArray();
            HeadlessHarness.Assert(info.Ok && heights[0]!.GetValue<float>() > heights[1]!.GetValue<float>(), "terrain.info did not report the raised centre above the corner: " + info.Data?.ToJsonString());

            byte[] paintBefore = Genesis.World.Terrain.TerrainAsset.Load(context.Resolve(terrain) + ".gterrain").CaptureSplatState();
            AutomationResult painted = Call("terrain.sculpt", $$"""{ "terrain": "{{terrain}}", "brush": "Paint", "layer": "1", "points": [[{{cx - 10}}, {{cz}}], [{{cx + 10}}, {{cz}}]], "radius": 4, "strength": 1 }""");
            HeadlessHarness.Assert(painted.Ok && !Genesis.World.Terrain.TerrainAsset.Load(context.Resolve(terrain) + ".gterrain").CaptureSplatState().AsSpan().SequenceEqual(paintBefore),
                "terrain.sculpt Paint did not lay the layer: " + painted.Message);
            HeadlessHarness.Assert(!Call("terrain.sculpt", $$"""{ "terrain": "{{terrain}}", "brush": "Paint", "layer": "Lava", "points": [[0, 0]] }""").Ok, "An unknown paint layer was not refused.");
            HeadlessHarness.Assert(!Call("terrain.sculpt", $$"""{ "terrain": "{{terrain}}", "brush": "Erase", "points": [[0, 0]] }""").Ok, "Erase, which a command cannot honour, was not refused.");
            HeadlessHarness.Assert(!Call("terrain.create", """{ "name": "Huge", "resolutionX": 9000 }""").Ok, "An impossible resolution was not refused.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.TerrainIsMadeFromAModelsGroundAndGivenWater", () =>
        {
            // A model with two ground surfaces (grass flat at 1 m, sand rising from 1 to 3 m) and a roof over them.
            string sources = context.Resolve("Assets/ModelSources");
            Directory.CreateDirectory(sources);
            Directory.CreateDirectory(context.Resolve("Assets/Models"));
            float[][] quads =
            [
                [-10, 1, -5, 0, 1, -5, 0, 1, 5, -10, 1, 5],
                [0, 1, -5, 10, 3, -5, 10, 3, 5, 0, 1, 5],
                [-2, 8, -2, 2, 8, -2, 2, 8, 2, -2, 8, 2],
            ];
            using (var bin = new BinaryWriter(File.Create(Path.Combine(sources, "Ground Source.bin"))))
            {
                foreach (float[] quad in quads) foreach (float value in quad) bin.Write(value);
                for (int quad = 0; quad < quads.Length; quad++) foreach (ushort index in new ushort[] { 0, 2, 1, 0, 3, 2 }) bin.Write(index);
            }
            string Accessor(int view, int offset, int componentType, int count, string type, float[]? quad = null) =>
                $$"""{"bufferView":{{view}},"byteOffset":{{offset}},"componentType":{{componentType}},"count":{{count}},"type":"{{type}}"{{(quad == null ? "" :
                    $",\"min\":[{quad.Where((_, i) => i % 3 == 0).Min()},{quad.Where((_, i) => i % 3 == 1).Min()},{quad.Where((_, i) => i % 3 == 2).Min()}],\"max\":[{quad.Where((_, i) => i % 3 == 0).Max()},{quad.Where((_, i) => i % 3 == 1).Max()},{quad.Where((_, i) => i % 3 == 2).Max()}]")}}}""";
            File.WriteAllText(Path.Combine(sources, "Ground Source.gltf"), $$"""
                {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
                 "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":3,"material":0},{"attributes":{"POSITION":1},"indices":4,"material":1},{"attributes":{"POSITION":2},"indices":5,"material":2}]}],
                 "materials":[{"name":"grass"},{"name":"sand"},{"name":"roof"}],
                 "buffers":[{"uri":"Ground Source.bin","byteLength":180}],
                 "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":144},{"buffer":0,"byteOffset":144,"byteLength":36}],
                 "accessors":[{{Accessor(0, 0, 5126, 4, "VEC3", quads[0])}},{{Accessor(0, 48, 5126, 4, "VEC3", quads[1])}},{{Accessor(0, 96, 5126, 4, "VEC3", quads[2])}},
                   {{Accessor(1, 0, 5123, 6, "SCALAR")}},{{Accessor(1, 12, 5123, 6, "SCALAR")}},{{Accessor(1, 24, 5123, 6, "SCALAR")}}]}
                """);
            string model = "Assets/Models/Ground Source.model.json";
            File.WriteAllText(context.Resolve(model), """{"schemaVersion": 2, "source": "../ModelSources/Ground Source.gltf", "parts": []}""");

            AutomationResult made = Call("terrain.fromModel", $$"""
                { "name": "Ground Land", "model": "{{model}}", "surfaces": ["grass", "sand"], "cellSize": 0.5, "hideSurfaces": true,
                  "layers": [ { "name": "Grass", "materials": ["grass"] }, { "name": "Sand", "materials": ["sand"], "color": [0.9, 0.8, 0.5] } ] }
                """);
            HeadlessHarness.Assert(made.Ok, "terrain.fromModel failed: " + made.Message);
            string terrain = PathOf(made);
            AutomationResult info = Call("terrain.info", $$"""{ "terrain": "{{terrain}}", "points": [[-5, 0], [5, 0], [0.5, 0.5], [10, 0]] }""");
            float[] heights = [.. info.Data!["pointHeights"]!.AsArray().Select(node => node!.GetValue<float>())];
            HeadlessHarness.Assert(Math.Abs(heights[0] - 1f) < 0.02f && Math.Abs(heights[1] - 2f) < 0.02f && Math.Abs(heights[2] - 1.1f) < 0.02f && Math.Abs(heights[3] - 3f) < 0.02f,
                "The terrain does not follow the model's ground (1, 2, 1.1 and 3 m expected, the roof left out): " + string.Join(", ", heights));
            var land = Genesis.World.Terrain.TerrainAsset.Load(context.Resolve(terrain) + ".gterrain");
            int Column(float x) => (int)MathF.Round((x - land.OriginX) / land.CellSize);
            int Row(float z) => (int)MathF.Round((z - land.OriginZ) / land.CellSize);
            HeadlessHarness.Assert(land.GetLayerWeight(Column(-5), Row(0), 0) == 255 && land.GetLayerWeight(Column(5), Row(0), 1) == 255,
                "The ground was not painted with the layer of the material it came from.");
            JsonArray layers = JsonNode.Parse(File.ReadAllText(context.Resolve(terrain)))!["layers"]!.AsArray();
            HeadlessHarness.Assert(layers.Count == 2 && layers[1]!["name"]!.GetValue<string>() == "Sand", "The terrain's paint layers were not named: " + layers.ToJsonString());

            // The model keeps its ground but no longer draws it; only the roof is left to draw.
            var asset = Genesis.Runtime.Modeling.StudioModelResourceLoader.Load(context.Resolve(model));
            int drawn = asset.Meshes.Sum(mesh => Genesis.Runtime.Modeling.ModelGpuCache.MaterialBatches(mesh, asset.Materials).Count());
            int all = asset.Meshes.Sum(mesh => Genesis.Runtime.Modeling.ModelGpuCache.MaterialBatches(mesh).Count());
            HeadlessHarness.Assert(asset.Materials.Count(material => material.Hidden) == 2 && asset.Materials.Single(material => !material.Hidden).Name == "roof" && drawn == 1 && all == 3,
                $"The model's ground surfaces were not hidden ({drawn} of {all} surfaces drawn).");

            // Water: a river that falls along its course, a pond to wade in with its own shader, and the sea.
            string shader = PathOf(Call("resource.create", """{ "kind": "Shader", "folder": "Assets", "name": "Pond Look" }"""));
            AutomationResult river = Call("terrain.addWater", $$"""{ "terrain": "{{terrain}}", "name": "Brook", "kind": "River", "riverPoints": [[-8, 0.9, 0, 2, 0.5], [8, 1.8, 0, 2.5, 0.5]] }""");
            AutomationResult pond = Call("terrain.addWater", $$"""{ "terrain": "{{terrain}}", "name": "Pond", "center": [-6, 0.95, 2], "sizeX": 4, "sizeZ": 3, "surfaceHeight": 0.95, "physicsMode": "Shallow", "shader": "{{shader}}" }""");
            AutomationResult sea = Call("terrain.addWater", $$"""{ "terrain": "{{terrain}}", "name": "Sea", "kind": "Ocean", "surfaceHeight": 0.2, "sizeX": 5000 }""");
            HeadlessHarness.Assert(river.Ok && pond.Ok && sea.Ok, "terrain.addWater failed: " + river.Message + " / " + pond.Message + " / " + sea.Message);
            HeadlessHarness.Assert(!Call("terrain.addWater", $$"""{ "terrain": "{{terrain}}", "kind": "River", "riverPoints": [[0, 0, 0, 1, 1]] }""").Ok, "A river of one point was not refused.");
            AutomationResult changed = Call("terrain.updateWater", $$"""{ "terrain": "{{terrain}}", "water": "Pond", "surfaceHeight": 1.05 }""");
            var nature = Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(context.Resolve(terrain));
            var brook = nature.WaterBodies.Single(water => water.Name == "Brook");
            var pondBody = nature.WaterBodies.Single(water => water.Name == "Pond");
            HeadlessHarness.Assert(changed.Ok && nature.WaterBodies.Count == 3 && brook.Kind == Genesis.World.Terrain.TerrainWaterKind.River && brook.RiverPoints.Count == 2
                && Math.Abs(brook.RiverPoints[1].Position.Y - 1.8f) < 0.001f && brook.ToWaterBody().SplineUsesPointHeights
                && Math.Abs(pondBody.SurfaceHeight - 1.05f) < 0.001f && pondBody.PhysicsMode == Genesis.World.Terrain.WaterPhysicsMode.Shallow
                && nature.WaterBodies.Single(water => water.Name == "Sea").Kind == Genesis.World.Terrain.TerrainWaterKind.Ocean,
                "The engine did not read back the water the commands wrote.");
            HeadlessHarness.Assert(Genesis.World.Terrain.TerrainNatureSerializer.LoadComponentShaders(context.Resolve(terrain)).GetValueOrDefault("water:" + pondBody.Id) == shader,
                "The pond's shader was not given to the terrain.");
            // As the game finds water: each body's own outline and surface, and of several the smallest.
            var volumes = nature.WaterBodies.Select(water => Genesis.World.Terrain.TerrainColliderMesh.CreateVolume(water, System.Numerics.Matrix4x4.Identity)).ToList();
            Genesis.Physics.PhysicsWaterVolume? Over(float x, float z) => Genesis.Physics.PhysicsWaterVolume.Find(volumes, x, z);
            HeadlessHarness.Assert(Over(0f, 0f)?.Name == "Brook" && Math.Abs(Over(0f, 0f)!.SurfaceHeightAt(0f, 0f) - 1.35f) < 0.01f,
                "Halfway along the brook its surface is not halfway between its ends' (1.35 m): " + Over(0f, 0f)?.Name + " " + Over(0f, 0f)?.SurfaceHeightAt(0f, 0f));
            HeadlessHarness.Assert(Over(-6f, 2f)?.Name == "Pond" && Math.Abs(Over(-6f, 2f)!.SurfaceHeightAt(-6f, 2f) - 1.05f) < 0.001f,
                "The pond within the sea's reach was not found as the pond: " + Over(-6f, 2f)?.Name);
            HeadlessHarness.Assert(Over(0f, 2.5f)?.Name == "Sea" && Over(60f, 5f)?.Name == "Sea" && Math.Abs(Over(60f, 5f)!.SurfaceHeightAt(60f, 5f) - 0.2f) < 0.001f,
                "Ground beside the brook, or open sea, was not the sea: " + Over(0f, 2.5f)?.Name + " / " + Over(60f, 5f)?.Name);            HeadlessHarness.Assert(!Call("terrain.removeWater", $$"""{ "terrain": "{{terrain}}", "water": "Sea" }""").Ok, "Removing water did not need confirming.");
            AutomationResult removed = Call("terrain.removeWater", $$"""{ "terrain": "{{terrain}}", "water": "Sea", "confirm": true }""");
            HeadlessHarness.Assert(removed.Ok && Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(context.Resolve(terrain)).WaterBodies.Count == 2, "terrain.removeWater left the sea: " + removed.Message);
        });
        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.AGlbsPicturesBesideItComeWithItsImport", () =>
        {
            // A .glb whose material's picture is a file beside it (as converters write them), not inside it.
            string folder = Path.Combine(ctx.Workspace, "glb-outside");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "tile.png"), Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
            string json = """
                {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
                 "meshes":[{"primitives":[{"attributes":{"POSITION":0},"material":0}]}],
                 "materials":[{"name":"tiled","pbrMetallicRoughness":{"baseColorTexture":{"index":0}},"extensions":{"KHR_materials_unlit":{}}}],"extensionsUsed":["KHR_materials_unlit"],
                 "textures":[{"source":0}],"images":[{"uri":"tile.png"}],
                 "buffers":[{"byteLength":36}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36}],
                 "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,0,1]}]}
                """;
            byte[] text = System.Text.Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
            using (var glb = new BinaryWriter(File.Create(Path.Combine(folder, "Tiled Floor.glb"))))
            {
                glb.Write(0x46546C67u); glb.Write(2u); glb.Write((uint)(12 + 8 + text.Length + 8 + 36));
                glb.Write((uint)text.Length); glb.Write(0x4E4F534Au); glb.Write(text);
                glb.Write(36u); glb.Write(0x004E4942u);
                foreach (float value in new float[] { 0, 0, 0, 1, 0, 0, 0, 0, 1 }) glb.Write(value);
            }
            AutomationResult imported = Call("resource.import", $$"""{ "files": ["{{Path.Combine(folder, "Tiled Floor.glb").Replace("\\", "\\\\")}}"], "folder": "Assets" }""");
            string model = imported.Changed.FirstOrDefault(path => path.EndsWith(".model.json", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            HeadlessHarness.Assert(imported.Ok && model.Length > 0, "The .glb was not imported: " + imported.Message);
            string picture = Path.Combine(context.Resolve(model)[..^".model.json".Length] + ".modeldata", "tile.png");
            HeadlessHarness.Assert(File.Exists(picture), "The picture beside the .glb did not come with it: " + picture);
            // glTF's unlit materials come in unlit, and the descriptor's materialUnlit (the Model editor's tick box) overrules them.
            bool TiledUnlit() => Genesis.Runtime.Modeling.StudioModelResourceLoader.Load(context.Resolve(model)).Materials.Single(material => material.Name == "tiled").Unlit;
            HeadlessHarness.Assert(TiledUnlit(), "A glTF KHR_materials_unlit material was not imported unlit.");
            HeadlessHarness.Assert(Call("resource.set", $$"""{ "path": "{{model}}", "values": { "materialUnlit": { "tiled": false } } }""").Ok && !TiledUnlit(),
                "materialUnlit did not make the material lit again.");
            JsonObject descriptor = JsonNode.Parse(File.ReadAllText(context.Resolve(model)))!.AsObject();
            var withUnlit = Genesis.Runtime.Modeling.StudioModelResourceLoader.Load(context.Resolve(model));
            withUnlit.Materials[0].Unlit = true;
            Genesis.Application.Editors.Suite.Assets.ModelViewerControl.WriteMaterialUnlit(descriptor, withUnlit);
            HeadlessHarness.Assert(descriptor["materialUnlit"]?["tiled"]?.GetValue<bool>() == true, "The Model editor's unlit choice was not written to the descriptor.");
            // A material's picture can slide (painted water): materialUvScroll reaches the material and the draw's MaterialDetail.yz.
            HeadlessHarness.Assert(Call("resource.set", $$"""{ "path": "{{model}}", "values": { "materialUvScroll": { "tiled": [0.25, -0.5] } } }""").Ok,
                "materialUvScroll could not be set.");
            var sliding = Genesis.Runtime.Modeling.StudioModelResourceLoader.Load(context.Resolve(model)).Materials.Single(material => material.Name == "tiled");
            System.Numerics.Vector4 detail = Genesis.Runtime.Modeling.RuntimeModelRenderSystem.MaterialDetail(sliding);
            HeadlessHarness.Assert(sliding.UvScrollU == 0.25f && sliding.UvScrollV == -0.5f && detail.Y == 0.25f && detail.Z == -0.5f && detail.W == 1f,
                $"materialUvScroll did not reach the material and its draw: {sliding.UvScrollU}, {sliding.UvScrollV}, {detail}.");
            sliding.UvScrollU = 0f; sliding.UvScrollV = 0f;
            var still = new Genesis.Runtime.Modeling.GModelAsset(); still.Materials.Add(sliding);
            Genesis.Application.Editors.Suite.Assets.ModelViewerControl.WriteMaterialUvScroll(descriptor, still);
            HeadlessHarness.Assert(descriptor["materialUvScroll"] is null, "A material that no longer slides was left in materialUvScroll.");
            still.Materials[0].UvScrollV = 0.125f;
            Genesis.Application.Editors.Suite.Assets.ModelViewerControl.WriteMaterialUvScroll(descriptor, still);
            HeadlessHarness.Assert(descriptor["materialUvScroll"]?["tiled"]?[1]?.GetValue<float>() == 0.125f, "The Model editor's slide was not written to the descriptor.");
            HeadlessHarness.Assert(Call("resource.set", $$"""{ "path": "{{model}}", "values": { "materialUvScroll": { "tiled": [0, 0] } } }""").Ok, "materialUvScroll could not be stopped.");
            // Turning "convert right-handed" on or off cooks the model again (it used to keep the old cook).
            float FarthestZ(bool mirrored)
            {
                AutomationResult set = Call("resource.set", $$"""{ "path": "{{model}}", "values": { "convertRightHanded": {{(mirrored ? "true" : "false")}} } }""");
                HeadlessHarness.Assert(set.Ok, "convertRightHanded was not set: " + set.Message);
                var asset = Genesis.Runtime.Modeling.StudioModelResourceLoader.Load(context.Resolve(model));
                return asset.Meshes.SelectMany(mesh => mesh.Vertices).Select(vertex => vertex.Position.Z).OrderBy(z => Math.Abs(z)).Last();
            }
            float plain = FarthestZ(false), mirrored = FarthestZ(true);
            HeadlessHarness.Assert(Math.Abs(plain - 1f) < 0.001f && Math.Abs(mirrored + 1f) < 0.001f,
                $"Changing the model's handedness did not cook it again (z {plain} then {mirrored}; 1 then -1 expected).");
            // Importing it again leaves its pictures alone when they are the same (a rewrite reloaded a playing game without end).
            string data = context.Resolve(model)[..^".model.json".Length] + ".modeldata";
            Dictionary<string, DateTime> Stamps() => Directory.GetFiles(data, "*.png", SearchOption.AllDirectories).ToDictionary(file => file, File.GetLastWriteTimeUtc);
            var before = Stamps();
            Thread.Sleep(50);
            Genesis.Runtime.Modeling.StudioModelResourceLoader.ImportSource(context.Resolve(model));
            HeadlessHarness.Assert(before.Count > 0 && Stamps().All(pair => before.TryGetValue(pair.Key, out DateTime was) && was == pair.Value),
                "Importing the model again rewrote pictures that had not changed.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.RoomInstancesAndEnvironmentAreSetByCommand", () =>
        {
            string room = PathOf(Call("resource.create", """{ "kind": "Room", "folder": "Assets", "name": "Field Room" }"""));
            string rock = PathOf(Call("resource.create", """{ "kind": "GameObject", "folder": "Assets", "name": "Rock" }"""));
            AutomationResult placed = Call("room.addInstance", $$"""{ "room": "{{room}}", "object": "{{rock}}", "x": 3, "z": 4, "yaw": 90 }""");
            HeadlessHarness.Assert(placed.Ok, "room.addInstance failed: " + placed.Message);
            string name = placed.Data?["name"]?.GetValue<string>() ?? string.Empty;
            AutomationResult moved = Call("room.moveInstance", $$"""{ "room": "{{room}}", "instance": "{{name}}", "y": 2 }""");
            var asset = Genesis.Runtime.Scene.RoomAssetLoader.Parse(context.Resolve(room));
            var node = asset.Nodes.FirstOrDefault(n => n.Name == name);
            HeadlessHarness.Assert(moved.Ok && node is not null && node.Transform.Position.SequenceEqual(new[] { 3f, 2f, 4f }) && Math.Abs(node.Transform.Rotation[1] - 90f) < 0.01f
                && (node.GameObject?.Prefab ?? string.Empty).Contains("Rock", StringComparison.Ordinal), "The instance was not placed and moved as asked: " + moved.Message + " " + (node is null ? "no node" : string.Join(",", node.Transform.Position) + " / " + node.GameObject?.Prefab));
            AutomationResult environment = Call("room.setEnvironment", $$"""{ "room": "{{room}}", "values": { "timeOfDayHours": 18.5 } }""");
            HeadlessHarness.Assert(environment.Ok && File.ReadAllText(context.Resolve(room)).Contains("18.5", StringComparison.Ordinal),
                "room.setEnvironment did not set the time of day: " + environment.Message);
            HeadlessHarness.Assert(!Call("room.setEnvironment", $$"""{ "room": "{{room}}", "values": { "timeOfDayHours": "teatime" } }""").Ok,
                "A value of the wrong kind was saved into the room.");
            AutomationResult removed = Call("room.removeInstance", $$"""{ "room": "{{room}}", "instance": "{{name}}", "confirm": true }""");
            HeadlessHarness.Assert(removed.Ok && Genesis.Runtime.Scene.RoomAssetLoader.Parse(context.Resolve(room)).Nodes.All(n => n.Name != name),
                "room.removeInstance left the instance: " + removed.Message);
            AutomationResult noShadows = Call("room.setEnvironment", $$"""{ "room": "{{room}}", "values": { "shadows": false, "toneMapping": false } }""");
            HeadlessHarness.Assert(noShadows.Ok && !Genesis.Runtime.Scene.RoomAssetLoader.Parse(context.Resolve(room)).Environment.Shadows && !Genesis.Runtime.Scene.RoomAssetLoader.Parse(context.Resolve(room)).Environment.ToneMapping,
                "A room's sun shadows could not be switched off: " + noShadows.Message);
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.ObjectsAreComposedAsTheObjectEditorDoes", () =>
        {
            string model = PathOf(Call("resource.create", """{ "kind": "Model", "folder": "Assets", "name": "Barrel Mesh" }"""));
            AutomationResult made = Call("object.create", $$"""{ "name": "Barrel", "model": "{{model}}", "solid": true, "events": { "Create": "x = 0;\n" } }""");
            HeadlessHarness.Assert(made.Ok, "object.create failed: " + made.ToJson().ToJsonString());
            string barrel = PathOf(made);
            var document = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(context.Resolve(barrel)));
            var composition = new ObjectCompositionModel(document);
            HeadlessHarness.Assert((string?)document["dimension"] == "ThreeD" && (bool?)document["solid"] == true
                && (string?)ObjectCompositionModel.Props(composition.Find("ModelRendererComponent")!)["ModelAsset"] == "Barrel Mesh"
                && composition.Find("SpriteComponent") is null
                && (string?)ObjectCompositionModel.Props(composition.Find("ScriptComponent")!)["ScriptClass"] == "Barrel",
                "The Object was not composed as the editor composes one: " + document.ToString(Newtonsoft.Json.Formatting.None));
            HeadlessHarness.Assert(Genesis.Runtime.Scripting.ObjectEventStore.Load(context.Resolve(barrel)).ContainsKey("Create"), "The Create event was not saved where the runtime finds it.");

            AutomationResult light = Call("object.setComponent", $$"""{ "object": "{{barrel}}", "type": "PointLightComponent", "values": { "Radius": 12 } }""");
            var lit = ObjectCompositionModel.Props(new ObjectCompositionModel(Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(context.Resolve(barrel)))).Find("PointLightComponent")!);
            HeadlessHarness.Assert(light.Ok && (float?)lit["Radius"] == 12f && (float?)lit["Intensity"] == 2f, "object.setComponent did not add the light with its defaults: " + light.Message);

            AutomationResult broken = Call("object.setEvent", $$"""{ "object": "{{barrel}}", "event": "Step", "code": "x = ;\n" }""");
            HeadlessHarness.Assert(broken.Ok && broken.Issues.Any(i => i.Severity == "error" && (i.Path ?? string.Empty).EndsWith("Barrel/Step.pgsl", StringComparison.Ordinal) && i.Line == 1),
                "A broken event did not come back with its error and line: " + broken.ToJson().ToJsonString());
            AutomationResult fixedStep = Call("object.setEvent", $$"""{ "object": "{{barrel}}", "event": "Step", "code": "x = x + 1;\n" }""");
            HeadlessHarness.Assert(fixedStep.Ok && !fixedStep.Issues.Any(i => i.Severity == "error" && (i.Path ?? string.Empty).Contains("Barrel", StringComparison.Ordinal)),
                "The fixed event still reported errors: " + fixedStep.ToJson().ToJsonString());
            AutomationResult info = Call("object.info", $$"""{ "object": "{{barrel}}" }""");
            HeadlessHarness.Assert(info.Ok && info.Data!["events"]!.ToJsonString() == "[\"Create\",\"Step\"]", "object.info did not list the events: " + info.Data?.ToJsonString());

            HeadlessHarness.Assert(!Call("object.removeEvent", $$"""{ "object": "{{barrel}}", "event": "Step" }""").Ok, "Removing an event did not need confirming.");
            AutomationResult removed = Call("object.removeEvent", $$"""{ "object": "{{barrel}}", "event": "Step", "confirm": true }""");
            HeadlessHarness.Assert(removed.Ok && !File.Exists(Genesis.Runtime.Scripting.ObjectEventStore.PathFor(context.Resolve(barrel), "Step"))
                && !File.ReadAllText(context.Resolve(barrel)).Contains("\"Step\"", StringComparison.Ordinal), "object.removeEvent left the event: " + removed.Message);
            AutomationResult unlit = Call("object.removeComponent", $$"""{ "object": "{{barrel}}", "type": "PointLightComponent", "confirm": true }""");
            HeadlessHarness.Assert(unlit.Ok && !File.ReadAllText(context.Resolve(barrel)).Contains("PointLightComponent", StringComparison.Ordinal), "object.removeComponent left the light: " + unlit.Message);

            string image = PathOf(Call("resource.create", """{ "kind": "Image", "folder": "Assets", "name": "Coin Picture" }"""));
            AutomationResult coin = Call("object.create", $$"""{ "name": "Coin", "sprite": "{{image}}" }""");
            HeadlessHarness.Assert(coin.Ok && coin.Data!["sprite"]?.GetValue<string>() == "Coin Picture" && coin.Data["dimension"]?.GetValue<string>() == "TwoD",
                "object.create did not make a 2D Object showing the image: " + coin.ToJson().ToJsonString());
            AutomationResult wrongKind = Call("object.create", $$"""{ "name": "Odd", "sprite": "{{model}}" }""");
            HeadlessHarness.Assert(!wrongKind.Ok && wrongKind.Message.Contains("should be a", StringComparison.Ordinal), "A model given as an image was not refused: " + wrongKind.Message);
            HeadlessHarness.Assert(Call("object.setEvent", $$"""{ "object": "{{barrel}}", "event": "Tuesday", "code": "x = 1;" }""").Message.Contains("not an event", StringComparison.Ordinal),
                "An unknown event was not explained.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.LibraryScriptsAreWrittenAndChecked", () =>
        {
            AutomationResult made = Call("script.write", """{ "path": "Assets/Scripts/Helpers.pgsl", "code": "function Twice(n) {\n    return n * 2;\n}\n" }""");
            HeadlessHarness.Assert(made.Ok && File.Exists(context.Resolve("Assets/Scripts/Helpers.pgsl")) && File.Exists(context.Resolve("Assets/Scripts/Helpers.pgsl") + ".meta")
                && !made.Issues.Any(i => i.Severity == "error" && (i.Path ?? string.Empty).Contains("Helpers", StringComparison.Ordinal)),
                "script.write did not make a clean script resource: " + made.ToJson().ToJsonString());
            AutomationResult broken = Call("script.write", """{ "path": "Assets/Scripts/Helpers.pgsl", "code": "function Twice(n) {\n    return n * ;\n}\n" }""");
            HeadlessHarness.Assert(broken.Ok && broken.Issues.Any(i => i.Severity == "error" && (i.Path ?? string.Empty).Contains("Helpers", StringComparison.Ordinal) && i.Line == 2),
                "A broken script did not come back with its error and line: " + broken.ToJson().ToJsonString());
            Call("script.write", """{ "path": "Assets/Scripts/Helpers.pgsl", "code": "function Twice(n) {\n    return n * 2;\n}\n" }""");
            HeadlessHarness.Assert(!Call("script.write", """{ "path": "Assets/Objects/Barrel/Create.pgsl", "code": "x = 1;" }""").Ok, "script.write wrote over an Object's event.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.ParticlesComeFromPresetsAndResourcesTakeCheckedSettings", () =>
        {
            AutomationResult fire = Call("particle.create", """{ "name": "Camp Fire", "preset": "fire", "values": { "emitRate": 90 } }""");
            HeadlessHarness.Assert(fire.Ok, "particle.create failed: " + fire.ToJson().ToJsonString());
            string path = PathOf(fire);
            var reference = Genesis.Runtime.Particles.ParticlePresets.Create("Fire");
            Genesis.Runtime.Particles.ParticleConfig Read() => System.Text.Json.JsonSerializer.Deserialize<Genesis.Runtime.Particles.ParticleConfig>(File.ReadAllText(context.Resolve(path)),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
            var made = Read();
            HeadlessHarness.Assert(made.EmitRate == 90 && made.Shape == reference.Shape && made.Speed == reference.Speed && made.Gravity == reference.Gravity,
                "The effect was not the Fire preset with its emit rate changed.");
            HeadlessHarness.Assert(Call("particle.create", """{ "name": "Odd", "preset": "Lava Lamp" }""").Message.Contains("Presets:", StringComparison.Ordinal), "An unknown preset was not explained.");
            AutomationResult wrong = Call("particle.create", """{ "name": "Wrong Fire", "values": { "emitRate": "lots" } }""");
            HeadlessHarness.Assert(!wrong.Ok && !File.Exists(context.Resolve("Assets/Particles/Wrong Fire.particle.json")), "A value of the wrong kind made an effect: " + wrong.Message);

            AutomationResult faster = Call("resource.set", $$"""{ "path": "{{path}}", "values": { "speed": 7 } }""");
            HeadlessHarness.Assert(faster.Ok && Read().Speed == 7 && Read().EmitRate == 90, "resource.set did not change only the speed: " + faster.Message);
            string before = File.ReadAllText(context.Resolve(path));
            HeadlessHarness.Assert(!Call("resource.set", $$"""{ "path": "{{path}}", "values": { "speed": "fast" } }""").Ok && File.ReadAllText(context.Resolve(path)) == before,
                "A value the engine cannot read was saved.");
            string rock = PathOf(Call("object.create", """{ "name": "Set Rock" }"""));
            HeadlessHarness.Assert(Call("resource.set", $$"""{ "path": "{{rock}}", "values": { "events": [] } }""").Message.Contains("object.setEvent", StringComparison.Ordinal),
                "An Object's events were set behind its event scripts' back.");
            HeadlessHarness.Assert(!Call("resource.set", """{ "path": "Assets/Scripts/Helpers.pgsl", "values": { "a": 1 } }""").Ok, "A script was treated as JSON.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.SoundsAreSynthesisedAndImportedAsAudio", () =>
        {
            AutomationResult coin = Call("audio.synth", """{ "name": "Coin Ping", "preset": "coin" }""");
            HeadlessHarness.Assert(coin.Ok && PathOf(coin).EndsWith(".audio.json", StringComparison.Ordinal), "audio.synth did not make an Audio resource: " + coin.ToJson().ToJsonString());
            var document = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(context.Resolve(PathOf(coin))));
            string? source = (string?)document["source"];
            string wav = source is null ? string.Empty : Path.Combine(Path.GetDirectoryName(context.Resolve(PathOf(coin)))!, Path.GetFileName(source));
            if (!File.Exists(wav) && source is not null) wav = context.Resolve(source);
            HeadlessHarness.Assert(File.Exists(wav), $"The Audio resource's sound file was not found: {source}");
            byte[] bytes = File.ReadAllBytes(wav);
            HeadlessHarness.Assert(System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE"
                && BitConverter.ToInt32(bytes, 24) == 44100 && BitConverter.ToInt16(bytes, 34) == 16, "The sound is not a 16-bit 44.1 kHz WAV.");

            // A pure A4 for one second: about 880 sign changes, and its loop and volume set on the resource.
            AutomationResult tone = Call("audio.synth", """{ "name": "A Tone", "layers": [ { "note": "A4", "duration": 1, "attack": 0.01, "release": 0.01, "sustain": 1 } ], "loop": true, "volume": 0.6 }""");
            HeadlessHarness.Assert(tone.Ok && Math.Abs(tone.Data!["seconds"]!.GetValue<double>() - 1) < 0.01, "The one-second tone was not one second: " + tone.ToJson().ToJsonString());
            var toneDoc = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(context.Resolve(PathOf(tone))));
            string toneWav = Path.Combine(Path.GetDirectoryName(context.Resolve(PathOf(tone)))!, Path.GetFileName((string)toneDoc["source"]!));
            if (!File.Exists(toneWav)) toneWav = context.Resolve((string)toneDoc["source"]!);
            byte[] pcm = File.ReadAllBytes(toneWav);
            int crossings = 0; short previous = 0;
            for (int i = 44; i + 1 < pcm.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(pcm, i);
                if ((sample >= 0) != (previous >= 0)) crossings++;
                previous = sample;
            }
            HeadlessHarness.Assert(Math.Abs(crossings - 880) < 20 && (bool?)toneDoc["loop"] == true && Math.Abs(((double?)toneDoc["volume"] ?? 0) - 0.6) < 0.001,
                $"The A4 tone was not 440 Hz with its loop and volume set: {crossings} sign changes, loop {toneDoc["loop"]}, volume {toneDoc["volume"]}.");

            AutomationResult tune = Call("audio.synth", """{ "name": "Tune", "melodies": [ { "notes": "C4 E4 G4 - C5+E5:2", "beatSeconds": 0.2, "voice": { "wave": "Organ" } } ], "echoDelay": 0.15, "echoFeedback": 0.3 }""");
            HeadlessHarness.Assert(tune.Ok && tune.Data!["layers"]!.GetValue<int>() == 5, "The melody did not become its five notes: " + tune.ToJson().ToJsonString());
            HeadlessHarness.Assert(Call("audio.synth", """{ "name": "Odd", "preset": "Kazoo" }""").Message.Contains("Presets:", StringComparison.Ordinal), "An unknown preset was not explained.");
            HeadlessHarness.Assert(Call("audio.synth", """{ "name": "Odd", "layers": [ { "note": "H9" } ] }""").Message.Contains("not a note", StringComparison.Ordinal), "A bad note was not explained.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.ImagesAreMadeAndDrawnOnFrameByFrame", () =>
        {
            AutomationResult made = Call("image.create", """{ "name": "Gem", "width": 32, "height": 24, "frames": 2, "background": "#00000000" }""");
            HeadlessHarness.Assert(made.Ok, "image.create failed: " + made.ToJson().ToJsonString());
            string gem = PathOf(made);
            var loaded = Genesis.Application.Core.Images.ImageDocumentSerializer.LoadAtomic(context.Resolve(gem)).Document;
            HeadlessHarness.Assert(loaded.Canvas.Width == 32 && loaded.Canvas.Height == 24 && loaded.Frames.Count == 2 && loaded.Layers.Count == 1 && loaded.Layers[0].Cels.Count == 2,
                "The image was not 32 x 24 with two frames on one layer.");
            AutomationResult drawn = Call("image.draw", $$"""
                { "image": "{{gem}}", "frame": 1, "operations": [
                    { "shape": "rect", "x": 2, "y": 2, "width": 10, "height": 8, "color": "#FF0000" },
                    { "shape": "pixels", "points": [[20, 5]], "color": "Lime" },
                    { "shape": "line", "points": [[0, 20], [31, 20]], "color": "#0000FFFF" },
                    { "shape": "ellipse", "x": 14, "y": 8, "width": 8, "height": 8, "color": "#FFFF00", "thickness": 1 } ] }
                """);
            HeadlessHarness.Assert(drawn.Ok, "image.draw failed: " + drawn.ToJson().ToJsonString());
            AutomationResult info = Call("image.info", $$"""{ "image": "{{gem}}" }""");
            string frame0 = info.Data!["frames"]![0]!["picture"]!.GetValue<string>(), frame1 = info.Data["frames"]![1]!["picture"]!.GetValue<string>();
            using (var picture = new System.Drawing.Bitmap(frame1))
            using (var untouched = new System.Drawing.Bitmap(frame0))
            {
                System.Drawing.Color red = picture.GetPixel(5, 5), lime = picture.GetPixel(20, 5), empty = picture.GetPixel(28, 2);
                System.Drawing.Color blue = picture.GetPixel(10, 20).B == 255 ? picture.GetPixel(10, 20) : picture.GetPixel(10, 19);
                HeadlessHarness.Assert(red.R == 255 && red.G == 0 && red.A == 255 && lime.G == 255 && lime.R == 0 && blue.B == 255 && blue.A == 255 && empty.A == 0
                    && untouched.GetPixel(5, 5).A == 0,
                    $"The drawing is not where it was asked: red {red}, lime {lime}, blue {blue}, empty {empty}, frame 0 {untouched.GetPixel(5, 5)}.");
            }
            var sprite = Genesis.Runtime.Assets.SpriteAssetLoader.Load(project.RootPath, gem);
            HeadlessHarness.Assert(sprite.Frames.Count == 2 && sprite.Canvas.Width == 32 && sprite.Canvas.Height == 24, "The game's sprite loader did not read the image the commands made.");
            HeadlessHarness.Assert(Call("image.draw", $$"""{ "image": "{{gem}}", "operations": [ { "shape": "star" } ] }""").Message.Contains("not a shape", StringComparison.Ordinal), "An unknown shape was not explained.");
            HeadlessHarness.Assert(Call("image.draw", $$"""{ "image": "{{gem}}", "operations": [ { "shape": "fill", "color": "Mauvish" } ] }""").Message.Contains("not a colour", StringComparison.Ordinal), "An unknown colour was not explained.");
            HeadlessHarness.Assert(!Call("image.create", """{ "name": "Huge", "width": 99999 }""").Ok, "An impossible size was accepted.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.ModelsAreBuiltFromPartsTheEngineBakes", () =>
        {
            AutomationResult lamp = Call("model.create", """
                { "name": "Lamp Post", "parts": [
                    { "primitive": "Cylinder", "position": [0, 1.5, 0], "scale": [0.2, 3, 0.2], "color": "#333333" },
                    { "primitive": "Sphere", "position": [0, 3.2, 0], "scale": [0.6, 0.6, 0.6], "color": "#FFE9A0" } ], "emissive": 0.5 }
                """);
            HeadlessHarness.Assert(lamp.Ok, "model.create failed: " + lamp.ToJson().ToJsonString());
            JsonArray top = lamp.Data!["boundsMax"]!.AsArray(), bottom = lamp.Data["boundsMin"]!.AsArray();
            HeadlessHarness.Assert(lamp.Data["vertices"]!.GetValue<int>() > 0 && Math.Abs(top[1]!.GetValue<double>() - 3.5) < 0.05 && Math.Abs(bottom[1]!.GetValue<double>()) < 0.05,
                "The engine did not bake the post and lamp to stand from 0 to 3.5 m: " + lamp.Data.ToJsonString());
            AutomationResult info = Call("model.info", $$"""{ "model": "{{PathOf(lamp)}}" }""");
            HeadlessHarness.Assert(info.Ok && info.Data!["vertices"]!.GetValue<int>() == lamp.Data["vertices"]!.GetValue<int>(), "model.info did not describe the same model.");
            AutomationResult placed = Call("object.create", $$"""{ "name": "Lamp", "model": "{{PathOf(lamp)}}" }""");
            HeadlessHarness.Assert(placed.Ok && placed.Data!["model"]?.GetValue<string>() == "Lamp Post", "The model could not be given to an Object: " + placed.Message);
            HeadlessHarness.Assert(Call("model.create", """{ "name": "Odd", "parts": [ { "primitive": "Teapot" } ] }""").Message.Contains("not a primitive", StringComparison.Ordinal), "An unknown primitive was not explained.");

            AutomationResult walker = Call("model.create", """
                { "name": "Walker Figure", "rig": "Humanoid", "parts": [
                    { "primitive": "Cube", "position": [0, 1.25, 0], "scale": [0.5, 0.7, 0.3] },
                    { "primitive": "Sphere", "position": [0, 1.8, 0], "scale": [0.3, 0.3, 0.3] },
                    { "primitive": "Cylinder", "position": [-0.15, 0.45, 0], "scale": [0.16, 0.9, 0.16] },
                    { "primitive": "Cylinder", "position": [0.15, 0.45, 0], "scale": [0.16, 0.9, 0.16] },
                    { "primitive": "Cylinder", "position": [-0.35, 1.25, 0], "scale": [0.12, 0.7, 0.12] },
                    { "primitive": "Cylinder", "position": [0.35, 1.25, 0], "scale": [0.12, 0.7, 0.12] } ] }
                """);
            HeadlessHarness.Assert(walker.Ok && walker.Data!["rigged"]!.GetValue<bool>()
                && walker.Data["animations"]!.AsArray().Any(a => a!.GetValue<string>().Contains("Walk", StringComparison.OrdinalIgnoreCase)),
                "model.create with a rig did not make a rigged model with a walk: " + walker.ToJson().ToJsonString());
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.ProjectsAreMadeAndRoomsGetTerrainAndAStart", () =>
        {
            var output = new StringWriter();
            int code = AutomationHost.Run(["new", ctx.Workspace, "Made By Command"], new StringReader(""), output, new StringWriter());
            string madePath = Path.Combine(ctx.Workspace, "Made By Command");
            HeadlessHarness.Assert(code == 0 && Directory.Exists(Path.Combine(madePath, "Assets")) && output.ToString().Contains("\"ok\": true", StringComparison.Ordinal),
                "--automation new did not make a project: " + output);

            string room = PathOf(Call("resource.create", """{ "kind": "Room", "folder": "Assets", "name": "Hill Room" }"""));
            HeadlessHarness.Assert(Call("resource.set", $$"""{ "path": "{{room}}", "values": { "dimension": 1 } }""").Ok, "The room could not be made 3D.");
            string land = PathOf(Call("terrain.create", """{ "name": "Hill Land", "preset": "Hills", "resolutionX": 33, "resolutionZ": 33 }"""));
            AutomationResult placed = Call("room.addTerrain", $$"""{ "room": "{{room}}", "terrain": "{{land}}", "uvScale": 12 }""");
            var asset = Genesis.Runtime.Scene.RoomAssetLoader.Parse(context.Resolve(room));
            HeadlessHarness.Assert(placed.Ok && asset.Nodes.Any(n => n.Kind == Genesis.Runtime.Scene.RoomNodeKind.Terrain && (n.Terrain?.Asset ?? "").Contains("Hill Land", StringComparison.Ordinal) && Math.Abs(n.Terrain!.UvScale - 12) < 0.01f),
                "room.addTerrain did not put the terrain in the room: " + placed.Message);
            AutomationResult start = Call("room.makeStart", $$"""{ "room": "{{room}}" }""");
            var reopened = new ProjectService().OpenProject(project.RootPath);
            HeadlessHarness.Assert(start.Ok && reopened.Manifest.StartRoom.Contains("Hill Room", StringComparison.Ordinal), $"room.makeStart did not set the start room: {reopened.Manifest.StartRoom}.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.ScriptedInputBecomesAReplayTheEngineReads", () =>
        {
            var steps = new List<GameRunCommands.InputStep>
            {
                new() { Seconds = 0.5f, Hold = ["W"] },
                new() { Press = ["Space"], Hold = ["W"], Look = [120f, 0f], Seconds = 0.25f },
                new() { Seconds = 1f, Click = "Left", Mouse = [100f, 50f] },
            };
            Genesis.Runtime.Input.InputFrame[] frames = GameRunCommands.Frames(steps);
            string path = Path.Combine(ctx.Workspace, "automation-input.ginput");
            Genesis.Runtime.Input.InputReplay.Write(path, 7, frames);
            HeadlessHarness.Assert(Genesis.Runtime.Input.InputReplay.TryRead(path, out int seed, out Genesis.Runtime.Input.InputFrame[] read, out string error) && seed == 7,
                "The engine could not read the scripted input back: " + error);
            UInt128 w = UInt128.One << (int)Genesis.Runtime.Input.Key.W, space = UInt128.One << (int)Genesis.Runtime.Input.Key.Space;
            HeadlessHarness.Assert(read.Length == 30 + 15 + 60, $"Expected 105 frames at 60 a second, read {read.Length}.");
            HeadlessHarness.Assert(read[0].KeysPressed == w && read[1].KeysPressed == UInt128.Zero && read[29].KeysDown == w
                && read[30].KeysPressed == space && read[31].KeysReleased == space && Math.Abs(read[30].LookX - 2f) < 0.001f
                && read[45].KeysReleased == w && read[45].MousePressed == 1 && read[46].MouseReleased == 1 && read[100].MouseX == 100f
                && Math.Abs(read[0].Delta - 1f / 60f) < 1e-6f, "The scripted input's presses, holds, releases, look and clicks were not as a person's would be.");
            HeadlessHarness.Assert(!Call("game.run", """{ "steps": [ { "press": ["Escape"] } ] }""").Ok, "Escape, which ends a replay, was accepted.");
            HeadlessHarness.Assert(Call("game.run", """{ "steps": [ { "press": ["Jump"] } ] }""").Message.Contains("is not a key", StringComparison.Ordinal), "An unknown key was not explained.");
            HeadlessHarness.Assert(!Call("game.run", """{ "steps": [ { "seconds": 2 } ], "shots": [5] }""").Ok, "A picture after the run's end was accepted.");
            HeadlessHarness.Assert(Call("game.benchmark", """{ "seconds": 0.5 }""").Message.Contains("Measure 1 to 900", StringComparison.Ordinal), "A benchmark too short to mean anything was accepted.");
            HeadlessHarness.Assert(Genesis.Runtime.Project.TimedShots.Parse("9, 2,x,5.5").SequenceEqual(new[] { 2f, 5.5f, 9f }), "GENESIS_SHOTS was not read in order.");
            HeadlessHarness.Assert(GameRunCommands.GameRun.SecondsToReady(["00:51:40.556  project=x", "00:51:49.277  Runtime boot ready: critical assets prepared"], new DateTime(2026, 10, 28, 0, 51, 40, 0)) is double ready && Math.Abs(ready - 9.277) < 0.01
                && GameRunCommands.GameRun.SecondsToReady(["00:00:02.500  Runtime boot ready"], new DateTime(2026, 10, 27, 23, 59, 59, 0)) is double late && Math.Abs(late - 3.5) < 0.01,
                "A run's start time was not read from its log (or not across midnight).");
            // A run's pictures after a door: the play-time clock must outlive the room it started in.
            HeadlessHarness.Assert(Genesis.Runtime.Project.ProjectRoomSwitcher.KeepSubsystem(new Genesis.Runtime.Project.TimedShots([1f], ctx.Workspace, null).Clock(null)),
                "A room change dropped the timed pictures' clock, so no picture was taken after it.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.AScenesCallsRunInOneBatchWithA3DRoomAndAWalker", () =>
        {
            // A batch: a 3D room with a sky, a walker from the first-person recipe whose own code comes
            // after the recipe's, a spinning prop and a third-person rider; checked once, strictly, at the end.
            AutomationResult batch = Call(AutomationRegistry.BatchCommand, """
                { "calls": [
                    { "command": "room.create", "input": { "name": "Walk Room", "folder": "Assets/Rooms", "dimension": "3D", "depth": 200 } },
                    { "command": "object.create", "input": { "name": "Walk Hero", "folder": "Assets/Objects", "recipes": ["FirstPerson"], "events": { "Create": "walkSpeed = 2;" } } },
                    { "command": "object.create", "input": { "name": "Walk Prop", "folder": "Assets/Objects", "threeD": true } },
                    { "command": "object.create", "input": { "name": "Walk Rider", "folder": "Assets/Objects", "recipes": ["ThirdPerson"] } },
                    { "command": "object.addRecipe", "input": { "object": "Assets/Objects/Walk Prop.object.json", "recipe": "Spin3D" } },
                    { "command": "room.addInstance", "input": { "room": "Assets/Rooms/Walk Room.room.json", "object": "Assets/Objects/Walk Hero.object.json", "y": 2, "yaw": 90 } }
                ] }
                """);
            HeadlessHarness.Assert(batch.Ok && batch.Data!["results"]!.AsArray().Count == 6 && batch.Changed.Count >= 6,
                "The batch did not run its six calls: " + batch.ToJson().ToJsonString());
            var room = Genesis.Runtime.Scene.RoomAssetLoader.Parse(context.Resolve("Assets/Rooms/Walk Room.room.json"));
            HeadlessHarness.Assert(room.Dimension == Genesis.Runtime.Scene.RoomDimension.ThreeD && room.Environment.DynamicSky
                && room.Environment.TimeScale == 0f && !room.Environment.AutomaticWeather && room.Settings.Depth == 200 && room.Nodes.Count == 1
                && Genesis.Runtime.Scene.RoomDisplayLayout.WindowSize(room) == new System.Drawing.Size(1280, 720),
                "room.create did not make a 3D room with a sunny day's sky: " + File.ReadAllText(context.Resolve("Assets/Rooms/Walk Room.room.json")));
            string hero = context.Resolve("Assets/Objects/Walk Hero.object.json");
            Dictionary<string, string> heroEvents = Genesis.Runtime.Scripting.ObjectEventStore.Load(hero);
            string heroCreate = heroEvents.GetValueOrDefault("Create") ?? string.Empty;
            HeadlessHarness.Assert(File.ReadAllText(hero).Contains("\"ThreeD\"", StringComparison.Ordinal) && heroEvents.ContainsKey("Step")
                && heroCreate.Contains("// Recipe: Walk in first person", StringComparison.Ordinal)
                && heroCreate.IndexOf("walkSpeed = 2;", StringComparison.Ordinal) > heroCreate.IndexOf("walkSpeed = 3.6;", StringComparison.Ordinal),
                "The first-person recipe was not added with the Object's own code after it:\n" + heroCreate);
            HeadlessHarness.Assert(!batch.Issues.Any(i => i.Severity == "error" && (i.Path ?? string.Empty).Contains("Walk", StringComparison.Ordinal)),
                "The recipes' code failed the strict script check: " + string.Join("; ", batch.Issues.Select(i => i.Message)));
            AutomationResult again = Call("object.addRecipe", """{ "object": "Assets/Objects/Walk Prop.object.json", "recipe": "Spin3D" }""");
            HeadlessHarness.Assert(again.Ok && again.Message.Contains("already", StringComparison.Ordinal), "A recipe was added twice: " + again.Message);
            HeadlessHarness.Assert(Call("object.addRecipe", """{ "object": "Assets/Objects/Walk Prop.object.json", "recipe": "Platformer" }""").Message.Contains("2D", StringComparison.Ordinal),
                "A 2D recipe was added to a 3D Object.");
            AutomationResult info = Call("object.info", """{ "object": "Assets/Objects/Walk Prop.object.json" }""");
            HeadlessHarness.Assert(info.Data?["recipes"]?.AsArray().OfType<JsonObject>().Any(r => r["id"]?.GetValue<string>() == "ThirdPerson") == true,
                "object.info did not list the 3D recipes: " + info.Data?.ToJsonString());

            // A scene preset on an existing room, then a value fine-tuning it; a 2D room refuses one.
            AutomationResult night = Call("room.setEnvironment", """{ "room": "Assets/Rooms/Walk Room.room.json", "scene": "Night", "values": { "atmosphericHaze": 0.2 } }""");
            var dark = Genesis.Runtime.Scene.RoomAssetLoader.Parse(context.Resolve("Assets/Rooms/Walk Room.room.json")).Environment;
            HeadlessHarness.Assert(night.Ok && Math.Abs(dark.TimeOfDayHours - 23.5f) < 0.01f && Math.Abs(dark.AtmosphericHaze - 0.2f) < 0.001f,
                "The Night scene and a value after it were not both set: " + night.Message);
            Call("room.create", """{ "name": "Flat Room", "folder": "Assets/Rooms" }""");
            HeadlessHarness.Assert(!Call("room.setEnvironment", """{ "room": "Assets/Rooms/Flat Room.room.json", "scene": "Night" }""").Ok
                && Call("room.create", """{ "name": "Odd Room", "folder": "Assets/Rooms", "dimension": "3D", "scene": "Teatime" }""").Message.Contains("Presets:", StringComparison.Ordinal),
                "A scene preset was put on a 2D room, or an unknown one was not explained.");

            // A failing call stops the batch there; what came before it stays done.
            AutomationResult stopped = Call(AutomationRegistry.BatchCommand, """
                { "calls": [
                    { "command": "resource.create", "input": { "kind": "Note", "folder": "Assets", "name": "Batch Note" } },
                    { "command": "object.addRecipe", "input": { "object": "Assets/Objects/Nothing.object.json", "recipe": "Spin3D" } },
                    { "command": "resource.create", "input": { "kind": "Note", "folder": "Assets", "name": "Never Made" } }
                ] }
                """);
            HeadlessHarness.Assert(!stopped.Ok && stopped.Message.Contains("1 not run", StringComparison.Ordinal) && stopped.Data!["results"]!.AsArray().Count == 2
                && Directory.EnumerateFiles(context.Resolve("Assets"), "Batch Note*", SearchOption.AllDirectories).Any() && !Directory.EnumerateFiles(context.Resolve("Assets"), "Never Made*", SearchOption.AllDirectories).Any(),
                "A batch did not stop at its failing call: " + stopped.ToJson().ToJsonString());
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.TheCookbooksWorkedExampleBuildsTheWalk", () =>
        {
            // Documentation/NatureWalk.batch.json ships with Studio as the Cookbook's worked example
            // (written by BuildTools/Benchmarks/NatureWalk/NatureWalk.ps1 -ExportBatch). It must still
            // build the whole walk, in a new project, and pass the strict check.
            string example = Genesis.Application.Studio.Forms.StudioShellForm.FindDocumentation(AppContext.BaseDirectory, "NatureWalk.batch.json")
                ?? throw new InvalidOperationException("Documentation/NatureWalk.batch.json was not found from the test host.");
            JsonObject calls = JsonNode.Parse(File.ReadAllText(example)) as JsonObject
                ?? throw new InvalidOperationException("The worked example is not a JSON object.");
            int count = calls["calls"]!.AsArray().Count;
            ProjectSession walk = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "automation-walk"), "Worked Example", "Blank");
            AutomationContext walkContext = AutomationContext.For(walk);
            walkContext.Caller = "test";
            AutomationResult built = registry.Invoke(walkContext, AutomationRegistry.BatchCommand, calls);
            HeadlessHarness.Assert(built.Ok && built.Data!["results"]!.AsArray().Count == count && count > 20,
                $"The Cookbook's worked example did not build ({count} calls): " + built.ToJson().ToJsonString());
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.A2DLevelsTilesArePaintedFromATileSet", () =>
        {
            // A 64 by 32 tile set of 16-pixel tiles is 4 columns by 2 rows; tiles 0 and 5 are solid.
            AutomationResult set = Call("image.create", """{ "name": "Level Tiles", "folder": "Assets/Sprites", "width": 64, "height": 32, "usage": "Tileset", "tileWidth": 16, "tileHeight": 16, "solidTiles": [5, 0] }""");
            HeadlessHarness.Assert(set.Ok, "image.create did not make a tile set: " + set.Message);
            HeadlessHarness.Assert(!Call("image.create", """{ "name": "Not Tiles", "folder": "Assets/Sprites", "tileWidth": 8 }""").Ok, "A tile size was accepted for a sprite.");
            string room = PathOf(Call("room.create", """{ "name": "Tile Level", "folder": "Assets/Rooms" }"""));
            AutomationResult floor = Call("room.paintTiles", $$"""
                { "room": "{{room}}", "tileset": "Assets/Sprites/Level Tiles.image.json", "solid": true, "tile": 5,
                  "rects": [ { "x": 0, "y": 10, "width": 20, "height": 2 } ], "cells": [ [3, 7], [4, 7, 1] ] }
                """);
            var asset = Genesis.Runtime.Scene.RoomAssetLoader.Parse(context.Resolve(room));
            var layer = asset.Nodes.Single(n => n.Kind == Genesis.Runtime.Scene.RoomNodeKind.TileLayer).TileLayer!;
            HeadlessHarness.Assert(floor.Ok && layer.Cells.Count == 42 && layer.CollisionEnabled && layer.CellWidth == 16
                && layer.Cells.Single(c => c.X == 3 && c.Y == 7) is { TileX: 1, TileY: 1 }
                && layer.Cells.Single(c => c.X == 4 && c.Y == 7) is { TileX: 1, TileY: 0 },
                "The floor and cells were not painted with the right tiles: " + floor.ToJson().ToJsonString());
            AutomationResult erased = Call("room.paintTiles", $$"""{ "room": "{{room}}", "tileset": "Assets/Sprites/Level Tiles.image.json", "erase": true, "rects": [ { "x": 0, "y": 10, "width": 5 } ] }""");
            HeadlessHarness.Assert(erased.Ok && Genesis.Runtime.Scene.RoomAssetLoader.Parse(context.Resolve(room)).Nodes.Single(n => n.Kind == Genesis.Runtime.Scene.RoomNodeKind.TileLayer).TileLayer!.Cells.Count == 37,
                "Erasing five cells did not leave 37: " + erased.Message);
            string threeD = PathOf(Call("room.create", """{ "name": "Not Flat", "folder": "Assets/Rooms", "dimension": "3D" }"""));
            HeadlessHarness.Assert(Call("room.paintTiles", $$"""{ "room": "{{threeD}}", "tileset": "Assets/Sprites/Level Tiles.image.json", "cells": [[0, 0]] }""").Message.Contains("2D room", StringComparison.Ordinal),
                "Tiles were painted in a 3D room.");

            // An autotiled set picks each tile from its neighbours: a row of three gets its two ends and a middle.
            Call("image.create", """{ "name": "Path Tiles", "folder": "Assets/Sprites", "width": 64, "height": 64, "usage": "Tileset", "tileWidth": 16, "tileHeight": 16, "autotile": true }""");
            Call("room.paintTiles", $$"""{ "room": "{{room}}", "tileset": "Assets/Sprites/Path Tiles.image.json", "rects": [ { "x": 0, "y": 0, "width": 3 } ] }""");
            var path = Genesis.Runtime.Scene.RoomAssetLoader.Parse(context.Resolve(room)).Nodes.Single(n => n.Name == "Path Tiles Tiles").TileLayer!;
            (int, int) Tile(int x) => (path.Cells.Single(c => c.X == x && c.Y == 0).TileX, path.Cells.Single(c => c.X == x && c.Y == 0).TileY);
            HeadlessHarness.Assert(Tile(0) == Genesis.Runtime.Scene.TileAutotile.TileFor(2, 0, 0) && Tile(1) == Genesis.Runtime.Scene.TileAutotile.TileFor(10, 0, 0)
                && Tile(2) == Genesis.Runtime.Scene.TileAutotile.TileFor(8, 0, 0), $"The autotiled row did not pick its ends and middle: {Tile(0)} {Tile(1)} {Tile(2)}.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.CommandLineAndMcpServeTheSameCommands", () =>
        {
            var output = new StringWriter();
            int code = AutomationHost.Run([project.RootPath, "call", "project.info", "{}"], new StringReader(string.Empty), output, new StringWriter());
            HeadlessHarness.Assert(code == 0 && output.ToString().Contains("\"ok\": true", StringComparison.Ordinal), "The command line did not run project.info: " + output);

            string requests = string.Join("\n",
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"resource_tree","arguments":{"depth":1}}}""") + "\n";
            var mcpOutput = new StringWriter();
            AutomationHost.Run([project.RootPath, "mcp"], new StringReader(requests), mcpOutput, new StringWriter());
            JsonObject[] replies = [.. mcpOutput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => (JsonObject)JsonNode.Parse(line)!)];
            HeadlessHarness.Assert(replies.Length == 3, "The MCP server did not answer the three requests (and not the notification): " + mcpOutput);
            HeadlessHarness.Assert(replies[0]["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "genesis-studio", "initialize did not name the server.");
            HeadlessHarness.Assert(replies[1]["result"]!["tools"]!.AsArray().OfType<JsonObject>().Any(t => t["name"]?.GetValue<string>() == "resource_create")
                && replies[1]["result"]!["tools"]!.AsArray().OfType<JsonObject>().Any(t => t["name"]?.GetValue<string>() == "batch"),
                "tools/list did not offer resource_create and batch.");
            HeadlessHarness.Assert(replies[2]["result"]?["isError"]?.GetValue<bool>() == false, "tools/call resource_tree failed: " + replies[2].ToJsonString());
        });
    }
}
