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
            AutomationResult grass = Call("terrain.setGrass", $$"""{ "terrain": "{{terrain}}", "enabled": true, "radius": 60 }""");
            AutomationResult layer = Call("terrain.addScatterLayer", $$"""{ "terrain": "{{terrain}}", "name": "Flowers", "model": "{{model}}", "densityPerHectare": 400 }""");
            HeadlessHarness.Assert(grass.Ok && layer.Ok, "terrain.setGrass or terrain.addScatterLayer failed: " + grass.Message + " / " + layer.Message);
            var nature = Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(context.Resolve(terrain));
            HeadlessHarness.Assert(nature.GrassRule.Enabled && Math.Abs(nature.GrassRule.Radius - 60f) < 0.01f
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
            HeadlessHarness.Assert(replies[1]["result"]!["tools"]!.AsArray().OfType<JsonObject>().Any(t => t["name"]?.GetValue<string>() == "resource_create"),
                "tools/list did not offer resource_create.");
            HeadlessHarness.Assert(replies[2]["result"]?["isError"]?.GetValue<bool>() == false, "tools/call resource_tree failed: " + replies[2].ToJsonString());
        });
    }
}
