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
