using System.Numerics;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Automation;
using Genesis.Application.Core.Projects;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The built-in meadow grass and its shader (29 October 2026): the defaults, the colour ramp, wind and
/// texture settings through the nature document, the templates, terrain.setGrass with nothing but the
/// terrain, paths kept bare, and pushers bending the tufts.
/// </summary>
internal static partial class TerrainLayersGrassSuite
{
    private static void RunGrassShader(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.BuiltInGrassDefaultsRoundTripThroughNatureDocument", () => GrassBuiltInDefaults(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.PathsStayBareAndThinInAtTheirEdges", GrassPathsStayBare);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.PushersBendTuftsAwayAndTheySpringBack", GrassPushersBend);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Scatter.SmallPlantsBendAndTreesDoNot", ScatterLayersBendAuto);
        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.SetGrassWithOnlyTheTerrainGrowsTheBuiltInGrass", () => GrassSetGrassDefaults(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Terrain.Templates.GroundAndGrassTemplatesMakeTheirLayers", () => GrassTemplates(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.GrassRule.Dx11ColourRampRunsRootToTip", () => GrassDx11ColourRamp(ctx));
    }

    private static void GrassBuiltInDefaults(HeadlessContext ctx)
    {
        var rule = new TerrainGrassRuleSettings();
        GrassAssert(!rule.Enabled && rule.Radius == 90f && rule.Spacing == 0.4f && rule.MinimumScale == 0.38f && rule.MaximumScale == 0.68f
            && rule.Species == FoliageSpecies.MeadowGrass && rule.Density(0) == 1f,
            $"A new rule is not the built-in meadow grass: radius {rule.Radius}, spacing {rule.Spacing}, scale {rule.MinimumScale} to {rule.MaximumScale}.");
        GrassAssert(rule.ColorEnabled && rule.BaseColor == TerrainGrassRuleSettings.DefaultBaseColor && rule.TipColor == TerrainGrassRuleSettings.DefaultTipColor
            && rule.ColorCurve == 1f && rule.Texture == "" && rule.WindEnabled && rule.WindStrength == 1f && rule.WindSpeed == 1f && rule.WindGusts == 0f
            && rule.PushersEnabled && rule.PushStrength == 0.8f && rule.SpringBackSeconds == 0.8f && rule.ClearPaths && rule.PathEdge == 0.6f
            && !rule.SavedBeforeShader, "A new rule's grass shader, parting or path settings are not the built-in grass's.");

        // The ramp's ends are the old meadow tuft's root and tip shades (0.52 and 1.06 of its colour), so the look carries over.
        Vector3 meadow = new(0.29f, 0.58f, 0.20f);
        GrassAssert(Vector3.Distance(rule.BaseColor, meadow * 0.52f) < 0.01f && Vector3.Distance(rule.TipColor, Vector3.Min(meadow * 1.06f, Vector3.One)) < 0.01f,
            "The built-in ramp does not start and end where the meadow tufts' own shading did.");
        Vector4 ramp = FoliageShading.ColorRamp(rule.BaseColor, rule.TipColor, rule.ColorCurve);
        GrassAssert(Vector3.Distance(FoliageShading.UnpackColor(ramp.X), rule.BaseColor) < 0.003f
            && Vector3.Distance(FoliageShading.UnpackColor(ramp.Y), rule.TipColor) < 0.003f && ramp.Z == 1f && ramp.W == 1f,
            "The ramp a draw carries does not unpack to the rule's colours.");

        string folder = Path.Combine(ctx.Workspace, "GrassShaderJson");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "Shaded.terrain.json");
        var document = new TerrainNatureDocument();
        TerrainGrassRuleSettings changed = document.GrassRule;
        changed.Enabled = true;
        changed.BaseColor = new Vector3(0.2f, 0.1f, 0.05f);
        changed.TipColor = new Vector3(0.9f, 0.8f, 0.3f);
        changed.ColorCurve = 2.5f;
        changed.Texture = "Blade";
        changed.WindEnabled = false;
        changed.WindStrength = 2f;
        changed.WindSpeed = 0.5f;
        changed.WindGusts = 0.7f;
        changed.PushersEnabled = false;
        changed.PushStrength = 0.4f;
        changed.SpringBackSeconds = 2f;
        changed.ClearPaths = false;
        changed.PathEdge = 1.5f;
        TerrainGrassRuleSettings expected = changed.Clone();
        TerrainNatureSerializer.Save(path, document);
        TerrainGrassRuleSettings loaded = TerrainNatureSerializer.LoadOrDefault(path).GrassRule;
        GrassAssert(loaded.SameAs(expected) && loaded.SameLookAs(expected) && loaded.ColorEnabled && !loaded.SavedBeforeShader,
            $"The grass shader's settings changed on their way through the file: {System.Text.Json.JsonSerializer.Serialize(loaded)}.");

        // The built-in values round-trip too, switched on.
        var builtIn = new TerrainNatureDocument();
        builtIn.GrassRule.Enabled = true;
        string builtInPath = Path.Combine(folder, "BuiltIn.terrain.json");
        TerrainNatureSerializer.Save(builtInPath, builtIn);
        TerrainGrassRuleSettings reread = TerrainNatureSerializer.LoadOrDefault(builtInPath).GrassRule;
        GrassAssert(reread.SameAs(builtIn.GrassRule) && reread.SameLookAs(builtIn.GrassRule) && reread.ColorEnabled,
            "The built-in grass did not survive save and reopen.");

        // A rule saved before the shader (no colorEnabled) keeps its tufts' own colours and reads as unchosen while off.
        string sidecar = TerrainNatureSerializer.SidecarPath(builtInPath);
        JsonObject json = JsonNode.Parse(File.ReadAllText(sidecar))!.AsObject();
        JsonObject grass = json["grassRule"]!.AsObject();
        foreach (string key in new[] { "colorEnabled", "baseColor", "tipColor", "colorCurve", "texture", "windEnabled", "windStrength", "windSpeed",
                     "windGusts", "pushersEnabled", "pushStrength", "springBackSeconds", "clearPaths", "pathEdge" })
            grass.Remove(key);
        grass["enabled"] = false;
        File.WriteAllText(sidecar, json.ToJsonString());
        TerrainGrassRuleSettings old = TerrainNatureSerializer.LoadOrDefault(builtInPath).GrassRule;
        GrassAssert(!old.ColorEnabled && old.SavedBeforeShader && old.LooksUnset && old.WindEnabled && old.WindStrength == 1f,
            "A rule saved before the grass shader did not keep its tufts' own colours and sway.");
        TerrainGrassRuleSettings started = old.BuiltInWithLayers();
        GrassAssert(started.ColorEnabled && started.Spacing == 0.4f && !started.SavedBeforeShader, "An unchosen rule did not start as the built-in grass.");
    }

    private static void GrassPathsStayBare()
    {
        // Meadow covers x < 0 of a 64 m terrain; a 4 m path runs along z at x = -16.
        TerrainAsset terrain = GrassSplitTerrain(129, 0.5f, slope: 0f);
        var path = new TerrainPathDefinition { Name = "Lane", Width = 4f, Points = [new Vector3(-16f, 0f, -31f), new Vector3(-16f, 0f, 31f)] };
        var rule = new TerrainGrassRuleSettings { Enabled = true, Spacing = 0.5f, CellSize = 8f, PathEdge = 0.6f };
        var bare = new TerrainGrassField(terrain, rule, paths: [path]);
        var open = new TerrainGrassField(terrain, new TerrainGrassRuleSettings { Enabled = true, Spacing = 0.5f, CellSize = 8f, ClearPaths = false }, paths: [path]);
        var tufts = new MeshInstanceData[bare.CellCapacity];
        int onPath = 0, beyondBare = 0, beyondOpen = 0, onPathOpen = 0, edge = 0, edgeOpen = 0;
        foreach (int cellX in new[] { 1, 2 })
        {
            int count = bare.GenerateCell(cellX, 3, tufts);
            for (int i = 0; i < count; i++)
            {
                float away = MathF.Abs(tufts[i].World.Translation.X + 16f);
                if (away < 2f) onPath++;
                else if (away < 2.6f) edge++;
                else beyondBare++;
            }

            count = open.GenerateCell(cellX, 3, tufts);
            for (int i = 0; i < count; i++)
            {
                float away = MathF.Abs(tufts[i].World.Translation.X + 16f);
                if (away < 2f) onPathOpen++;
                else if (away < 2.6f) edgeOpen++;
                else beyondOpen++;
            }
        }

        GrassAssert(onPath == 0 && onPathOpen > 100, $"The path grew {onPath} tufts (without clearing: {onPathOpen}).");
        GrassAssert(beyondBare == beyondOpen && beyondBare > 200, $"Grass away from the path changed: {beyondBare} against {beyondOpen}.");
        GrassAssert(edge > 0 && edge < edgeOpen, $"The path's edge does not thin in: {edge} of {edgeOpen} tufts.");
        GrassAssert(!bare.ClearsPaths(new List<TerrainPathDefinition>()), "A field claims a new path list is the one it was grown from.");
    }

    private static void GrassPushersBend()
    {
        var pushers = new GrassPushers();
        pushers.Begin(0f, 0.8f);
        pushers.Add(7, Vector3.Zero, 1f);
        pushers.End();
        Matrix4x4 tuft = Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(0.4f, 0f, 0.1f);
        Matrix4x4 bent = tuft;
        GrassAssert(pushers.Bend(ref bent, 0.8f), "A tuft 0.4 m from a pusher was not bent.");
        Vector3 root = Vector3.Transform(Vector3.Zero, bent), tip = Vector3.Transform(Vector3.UnitY, bent);
        Vector3 uprightTip = Vector3.Transform(Vector3.UnitY, tuft);
        GrassAssert(Vector3.Distance(root, new Vector3(0.4f, 0f, 0.1f)) < 1e-4f, $"The tuft's root moved to {root}.");
        GrassAssert(tip.X > uprightTip.X + 0.1f && tip.Y < uprightTip.Y - 0.02f,
            $"The tuft did not lean away from the pusher: tip {tip} against upright {uprightTip}.");
        Matrix4x4 far = Matrix4x4.CreateTranslation(3f, 0f, 0f);
        GrassAssert(!pushers.Bend(ref far, 0.8f), "A tuft 3 m from a 1 m pusher was bent.");
        Matrix4x4 below = Matrix4x4.CreateTranslation(0.3f, -4f, 0f);
        GrassAssert(!pushers.Bend(ref below, 0.8f), "Grass 4 m below a pusher's feet was bent.");

        // The pusher leaves: its last place springs back over the spring-back time.
        pushers.Begin(0.1f, 0.8f);
        pushers.End();
        Matrix4x4 trodden = tuft;
        GrassAssert(pushers.Points.Count == 1 && pushers.Points[0].Strength is > 0f and <= 1f && pushers.Bend(ref trodden, 0.8f),
            "Where the pusher stood did not stay bent as it sprang back.");
        float leanJustAfter = Vector3.Transform(Vector3.UnitY, trodden).X;
        pushers.Begin(0.5f, 0.8f);
        pushers.End();
        Matrix4x4 later = tuft;
        pushers.Bend(ref later, 0.8f);
        GrassAssert(Vector3.Transform(Vector3.UnitY, later).X < leanJustAfter, "The trodden grass did not stand up as time passed.");
        pushers.Begin(0.5f, 0.8f);
        pushers.End();
        Matrix4x4 standing = tuft;
        GrassAssert(pushers.IsEmpty && !pushers.Bend(ref standing, 0.8f), "Trodden grass was still bent after the spring-back time.");
    }

    private static void ScatterLayersBendAuto()
    {
        var flowers = new TerrainScatterLayer { CastShadows = false, MaximumScale = 1.3f };
        var trees = new TerrainScatterLayer { CastShadows = true, MaximumScale = 1.5f, CollisionRadius = 0.3f };
        var tallReeds = new TerrainScatterLayer { CastShadows = false, MaximumScale = 2f };
        GrassAssert(flowers.Bends(0.4f) && !trees.Bends(6f) && !tallReeds.Bends(1.2f),
            "Auto did not bend small unshadowed plants only.");
        GrassAssert(new TerrainScatterLayer { Bend = TerrainScatterBend.On, CastShadows = true }.Bends(5f)
            && !new TerrainScatterLayer { Bend = TerrainScatterBend.Off, CastShadows = false }.Bends(0.3f), "On and Off are not obeyed.");
        TerrainNatureDocument copy = TerrainNatureSerializer.Clone(new TerrainNatureDocument { ScatterLayers = [new TerrainScatterLayer { Bend = TerrainScatterBend.Off }] });
        GrassAssert(copy.ScatterLayers.Single().Bend == TerrainScatterBend.Off, "A layer's Bend does not go through the document.");
    }

    private static void GrassSetGrassDefaults(HeadlessContext ctx)
    {
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "GrassSetDefaults"), "Grass set defaults", "Blank");
        AutomationContext context = AutomationContext.For(project);
        context.Caller = "test";
        AutomationRegistry registry = AutomationRegistry.CreateDefault();
        AutomationResult Call(string name, string json) => registry.Invoke(context, name, JsonNode.Parse(json) as JsonObject);
        string terrain = Call("resource.create", """{ "kind": "Terrain", "folder": "Assets", "name": "Field" }""").Data?["path"]?.GetValue<string>() ?? "";
        GrassAssert(terrain.EndsWith(".terrain.json", StringComparison.Ordinal), "The terrain was not made: " + terrain);

        AutomationResult grass = Call("terrain.setGrass", $$"""{ "terrain": "{{terrain}}" }""");
        TerrainGrassRuleSettings rule = TerrainNatureSerializer.LoadOrDefault(context.Resolve(terrain)).GrassRule;
        var builtIn = new TerrainGrassRuleSettings { Enabled = true };
        GrassAssert(grass.Ok && rule.SameAs(builtIn) && rule.SameLookAs(builtIn),
            $"terrain.setGrass with only the terrain did not grow the built-in grass: {grass.Message} {System.Text.Json.JsonSerializer.Serialize(rule)}");
        JsonArray colour = JsonNode.Parse(File.ReadAllText(context.Resolve(terrain)))!["layers"]![0]!["color"]!.AsArray();
        Vector3 ground = new(colour[0]!.GetValue<float>(), colour[1]!.GetValue<float>(), colour[2]!.GetValue<float>());
        GrassAssert(Vector3.Distance(ground, TerrainGrassRuleSettings.DefaultGroundColor) < 0.002f,
            $"The stock Grass layer did not take the ground colour that suits the grass: {ground}.");
        GrassAssert(grass.Data?["baseColor"]?["x"] is not null, "The command's answer does not show the blade colours: " + grass.Data?.ToJsonString());

        AutomationResult tinted = Call("terrain.setGrass", $$"""{ "terrain": "{{terrain}}", "baseColor": [0.4, 0.3, 0.1], "windGusts": 0.5, "texture": "" }""");
        rule = TerrainNatureSerializer.LoadOrDefault(context.Resolve(terrain)).GrassRule;
        GrassAssert(tinted.Ok && rule.Enabled && rule.BaseColor == new Vector3(0.4f, 0.3f, 0.1f) && rule.WindGusts == 0.5f
            && rule.TipColor == TerrainGrassRuleSettings.DefaultTipColor && rule.Spacing == 0.4f, "terrain.setGrass changed more or less than it was given: " + tinted.Message);
        GrassAssert(!Call("terrain.setGrass", $$"""{ "terrain": "{{terrain}}", "texture": "No Such Image" }""").Ok, "A missing grass texture was not refused.");
        GrassAssert(!Call("terrain.setGrass", $$"""{ "terrain": "{{terrain}}", "tipColor": [1, 2] }""").Ok, "A two-number colour was not refused.");
        AutomationResult off = Call("terrain.setGrass", $$"""{ "terrain": "{{terrain}}", "enabled": false }""");
        GrassAssert(off.Ok && !TerrainNatureSerializer.LoadOrDefault(context.Resolve(terrain)).GrassRule.Enabled, "enabled false did not switch the grass off.");
    }

    private static void GrassTemplates(HeadlessContext ctx)
    {
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "GrassTemplates"), "Grass templates");
        var resources = new Genesis.Application.Core.Resources.ResourceService(project);
        string terrainFile = resources.CreateResource(resources.AssetsRoot, Genesis.Application.Core.Resources.ResourceKind.Terrain, "Template field");
        GrassSplitTerrain(129, 0.5f, slope: 0f).Save(terrainFile + ".gterrain");
        var builtIn = new TerrainGrassRuleSettings { Enabled = true };
        using (var editor = new Genesis.Application.Editors.Suite.Terrain.TerrainEditorControl(terrainFile, project.RootPath))
        {
            editor.ApplyGrassTemplate();
            GrassAssert(editor.GrassRule.SameAs(builtIn) && editor.GrassRule.SameLookAs(builtIn), "Foliage › Grass did not switch on the built-in grass.");
            editor.Undo();
            GrassAssert(!editor.GrassRule.Enabled, "One undo did not take the grass template back.");
            editor.Redo();
            editor.ApplyGroundTemplate();
            GrassAssert(editor.PaintLayerCount >= 4, $"Meadow ground left {editor.PaintLayerCount} paint layers.");
            editor.Save();
        }

        JsonArray layers = JsonNode.Parse(File.ReadAllText(terrainFile))!["layers"]!.AsArray();
        string[] names = layers.Take(4).Select(layer => layer?["name"]?.GetValue<string>() ?? "").ToArray();
        GrassAssert(names.SequenceEqual(new[] { "Grass", "Dirt", "Rock", "Snow" }), $"Meadow ground made the layers {string.Join(", ", names)}.");
        JsonArray grassColour = layers[0]!["color"]!.AsArray();
        GrassAssert(Math.Abs(grassColour[0]!.GetValue<float>() - TerrainGrassRuleSettings.DefaultGroundColor.X) < 0.002f
            && Math.Abs(grassColour[1]!.GetValue<float>() - TerrainGrassRuleSettings.DefaultGroundColor.Y) < 0.002f,
            "The Grass layer is not the ground colour that suits the grass.");
        using var reopened = new Genesis.Application.Editors.Suite.Terrain.TerrainEditorControl(terrainFile, project.RootPath);
        GrassAssert(reopened.GrassRule.SameAs(builtIn) && reopened.GrassRule.SameLookAs(builtIn), "Reopening the terrain lost the built-in grass.");
        var values = reopened.GetLiveInspectorValues();
        GrassAssert(values.Any(value => value.PropertyPath == "Grass.TipColor") && values.Any(value => value.PropertyPath == "Grass.WindGusts")
            && values.Any(value => value.PropertyPath == "Grass.Texture" && value.AssetKind == Genesis.Application.Core.Resources.ResourceKind.Image),
            "The Inspector does not show the grass shader's settings.");
        GrassAssert(reopened.TryApplyInspectorValue("Grass.TipColor", "#c8b450") && reopened.GrassRule.TipColor.X > 0.75f,
            "Setting the tip colour in the Inspector did not change the rule.");
    }

    private static void GrassDx11ColourRamp(HeadlessContext ctx)
    {
        string root = GrassProject(ctx, "GrassRampCapture");
        TerrainAsset terrain = GrassSplitTerrain(129, 0.5f, slope: 0f);
        RoomAsset room = GrassRoom(root, terrain, "Ramp", enabled: false, out string binary);
        Vector3 eye = new(-10f, 1.2f, -20f), target = new(-10f, 0.4f, 0f);
        byte[] off = GrassRenderOnce(ctx, root, room, eye, target, "terrain-grass-ramp-dx11-off", out _);
        var nature = new TerrainNatureDocument();
        TerrainGrassRuleSettings rule = nature.GrassRule;
        rule.Enabled = true;
        rule.Layer1Density = 1f;
        rule.Layer2Density = 0f;
        rule.MinimumScale = rule.MaximumScale = 1.2f;
        rule.BaseColor = new Vector3(0.9f, 0.08f, 0.05f);
        rule.TipColor = new Vector3(0.05f, 0.12f, 0.95f);
        rule.WindEnabled = false;
        TerrainNatureSerializer.Save(binary, nature);
        byte[] frame = GrassRenderOnce(ctx, root, room, eye, target, "terrain-grass-ramp-dx11", out IReadOnlyList<TerrainGrassStatistics> stats);
        GrassAssert(stats.Count == 1 && stats[0].TuftsDrawn > 500, "The ramp test drew no grass.");
        // The frame is B, G, R, A per pixel; only what the grass changed counts (the sky is blue too).
        long redRows = 0, blueRows = 0;
        int red = 0, blue = 0;
        for (int y = 0; y < GrassHeight; y++)
        for (int x = 0; x < GrassWidth; x++)
        {
            int index = (y * GrassWidth + x) * 4;
            int b = frame[index], g = frame[index + 1], r = frame[index + 2];
            if (Math.Abs(b - off[index]) + Math.Abs(g - off[index + 1]) + Math.Abs(r - off[index + 2]) < 40) continue;
            if (r > g + 40 && r > b + 40) { red++; redRows += y; }
            else if (b > r + 40 && b > g + 40) { blue++; blueRows += y; }
        }

        Console.WriteLine($"[GrassRule] DX11 ramp: {red} red (root) pixels, {blue} blue (tip) pixels; {stats[0].TuftsDrawn} tufts.");
        GrassAssert(red > 1500 && blue > 1500, $"The blades are not red at the root and blue at the tip: {red} red, {blue} blue pixels.");
        GrassAssert(blueRows / (double)blue < redRows / (double)red, "The tip colour is not above the root colour on screen.");
    }
}
