using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Genesis.World.Terrain;

namespace Genesis.Application.Headless;

/// <summary>Editable diagnostic content, never special-cased by the engine or offered as finished artwork.</summary>
internal static class TavernBenchmarkProject
{
    public const string RoomName = "Tavern Benchmark";
    public const double RouteSeconds = 48;

    public static ProjectSession Create(string parent)
    {
        ProjectService projects = new();
        ProjectSession project = projects.CreateProject(parent, "Tavern Benchmark");
        ResourceService resources = new(project);
        string models = Path.Combine(project.AssetsPath, "Models");
        string objects = Path.Combine(project.AssetsPath, "Objects");
        string rooms = Path.Combine(project.AssetsPath, "Rooms");
        string terrainFolder = Path.Combine(project.AssetsPath, "Terrain");
        foreach (string folder in new[] { models, objects, rooms, terrainFolder }) Directory.CreateDirectory(folder);
        RoomAsset room = RoomAsset.Create(RoomName, RoomDimension.ThreeD);
        room.Settings.Width = room.Settings.Depth = 8000;
        room.Settings.WindowWidth = 1920; room.Settings.WindowHeight = 1080;
        room.Settings.TargetFps = -1; room.Settings.VSync = false; room.Settings.CaptureMouse = false;
        room.Environment.BackgroundColor = [.15f, .2f, .25f, 1];
        room.Environment.AmbientIntensity = .45f;
        room.Environment.DynamicSky = false; room.Environment.VolumetricClouds = false;
        room.Environment.AutomaticWeather = false;

        Vector3 wood = new(.38f, .2f, .09f), dark = new(.19f, .1f, .05f), stone = new(.37f, .35f, .31f);
        List<ModelPart> structure = [Box("Floor", 0, -.12f, 0, 20, .24f, 22, wood),
            Box("Rear stone wall", 0, 3.8f, -11, 20, 7.6f, .35f, stone),
            Box("Left wall", -10, 3.8f, 0, .35f, 7.6f, 22, stone),
            Box("Right wall", 10, 3.8f, 0, .35f, 7.6f, 22, stone),
            Box("Entry left", -6, 3.8f, 11, 8, 7.6f, .35f, stone),
            Box("Entry right", 6, 3.8f, 11, 8, 7.6f, .35f, stone),
            Box("Entry lintel", 0, 5.4f, 11, 4, 4.4f, .35f, stone),
            Box("Ceiling", 0, 7.8f, 0, 20, .2f, 22, dark),
            Box("Balcony", -6.5f, 3.55f, -4, 6, .3f, 14, wood)];
        for (int index = 0; index < 5; index++)
        {
            float z = -8 + index * 4;
            structure.Add(Box("Roof beam " + index, 0, 7.5f, z, 20, .3f, .3f, wood));
            structure.Add(Box("Post " + index, -3.4f, 3.6f, z, .25f, 7.2f, .25f, dark));
        }
        for (int step = 0; step < 18; step++)
            structure.Add(Box("Stair " + step, -6.5f, (step + 1) * .1f, 7 - step * .35f,
                2.5f, (step + 1) * .2f, .35f, wood));
        string shell = ModelObject("Tavern shell", structure, collision: true);
        Place(shell, "Tavern shell", 0, 0, 0);

        List<ModelPart> table = [Box("Tabletop", 0, 1, 0, 2.4f, .14f, 1.4f, wood)];
        foreach (int sx in new[] { -1, 1 }) foreach (int sz in new[] { -1, 1 })
            table.Add(Box("Table leg", sx, .45f, sz * .5f, .14f, .9f, .14f, dark));
        string tableObject = ModelObject("Oak table", table, collision: true);
        string chair = ModelObject("Chair", [Box("Seat", 0, .5f, 0, .6f, .12f, .65f, wood),
            Box("Back", 0, .9f, -.3f, .6f, .8f, .1f, wood), Box("Base", 0, .22f, 0, .4f, .44f, .4f, dark)]);
        for (int index = 0; index < 5; index++)
        {
            float x = index < 3 ? 3.4f : -6.5f, z = -5 + index % 3 * 5;
            float y = index < 3 ? 0 : 3.7f;
            Place(tableObject, "Table " + index, x, y, z);
            Place(chair, "Chair " + index + " A", x, y, z - 1.2f);
            Place(chair, "Chair " + index + " B", x, y, z + 1.2f, 180);
        }
        string bar = ModelObject("Bar counter", [Box("Counter", 5, 1.2f, -7, 7, .2f, 1.5f, wood),
            Box("Counter front", 5, .55f, -6.4f, 7, 1.1f, .16f, dark)]);
        Place(bar, "Bar counter", 0, 0, 0);
        string fireplace = ModelObject("Fireplace", [Box("Hearth", -8, .2f, -8, 3, .4f, 2.5f, stone),
            Box("Mantel", -8, 2.5f, -8.7f, 3.6f, .3f, 1.4f, wood),
            Box("Left pier", -9.25f, 1.4f, -8.7f, .5f, 2.4f, 1.2f, stone),
            Box("Right pier", -6.75f, 1.4f, -8.7f, .5f, 2.4f, 1.2f, stone),
            Box("Fire embers", -8, .55f, -8.7f, 1.6f, .2f, .9f, new(1, .25f, .03f))]);
        Place(fireplace, "Fireplace", 0, 0, 0);
        string lantern = ModelObject("Lantern", [Box("Frame", 0, 0, 0, .3f, .5f, .3f, dark),
            Box("Glass", 0, 0, .17f, .2f, .3f, .02f, new(.95f, .55f, .15f))],
            "LightEmitterSetColor(1, 0.62, 0.24); LightEmitterSetRadius(9); LightEmitterSetIntensity(3); LightEmitterEnable(true);");
        foreach (var light in new[] { new Vector3(-8, 1.1f, -8), new(7, 3, -4), new(0, 4, 4), new(-6, 5, -2) })
            Place(lantern, "Warm lantern", light.X, light.Y, light.Z);

        string actor = ModelObject("Animated actor", [Box("Torso", 0, 1.15f, 0, .55f, .7f, .3f, new(.23f, .35f, .37f)),
            Box("Head", 0, 1.75f, 0, .32f, .4f, .3f, new(.65f, .43f, .27f)),
            Box("Left leg", -.15f, .45f, 0, .19f, .9f, .22f, dark),
            Box("Right leg", .15f, .45f, 0, .19f, .9f, .22f, dark),
            Box("Left arm", -.42f, 1.25f, 0, .24f, .65f, .22f, wood),
            Box("Right arm", .42f, 1.25f, 0, .24f, .65f, .22f, wood)],
            "ModelAnimationPlay(\"Idle\", true, 0);", animated: true);
        Place(actor, "Barkeep", 5, 0, -8.5f);
        for (int index = 0; index < 6; index++) Place(actor, "Patron " + index, 1 + index % 3 * 2, 0, -3 + index / 3 * 5, index * 53);
        Place(actor, "Balcony actor", -6.5f, 3.7f, -5);

        string terrain = resources.CreateResource(terrainFolder, ResourceKind.Terrain, "Benchmark world");
        TerrainAsset land = new(257, 257, 31.25f, -4000, -4000, -1, 5);
        for (int z = 0; z < land.ResolutionZ; z++) for (int x = 0; x < land.ResolutionX; x++) land.SetHeight(x, z, -.2f);
        land.Save(terrain + ".gterrain");
        room.Nodes.Add(new RoomNode { Kind = RoomNodeKind.Terrain, Name = "8 km coarse baseline terrain",
            LayerId = room.Layers[0].Id, Terrain = new RoomTerrainData { Asset = Relative(terrain), UvScale = 128 } });
        string controller = Object("Benchmark camera", "", CameraCreate, CameraStep, Hud);
        Place(controller, "Benchmark route", 0, 0, 0);
        string roomFile = resources.CreateResource(rooms, ResourceKind.Room, RoomName);
        RoomAssetLoader.Save(room, roomFile);
        project.Manifest.StartRoom = Relative(roomFile); projects.Save(project);
        File.WriteAllText(Path.Combine(project.RootPath, "benchmark-content.json"), JsonSerializer.Serialize(new
        {
            WorldWidthMetres = 8000, WorldDepthMetres = 8000, RouteSeconds,
            ActualPlacedObjects = room.Nodes.Count(node => node.Kind == RoomNodeKind.GameObject),
            ActualAnimatedActors = 8, ActualDistributedFoliage = 0,
            TargetPlacedObjects = 10000, TargetAnimatedAgents = 128, TargetDistributedFoliage = 250000,
            ArtProvenance = "Genesis ModelPartBuilder geometry; generated editable rig/animation; no concept image used as runtime pixels",
            Quality = "Initial functional fixture; art and stress populations pending",
        }, new JsonSerializerOptions { WriteIndented = true }));
        return project;

        string Relative(string file) => Path.GetRelativePath(project.RootPath, file).Replace('\\', '/');
        string ModelObject(string name, List<ModelPart> parts, string create = "", bool collision = false, bool animated = false)
        {
            var mesh = ModelPartBuilder.Bake(parts);
            GModelAsset asset = ModelRigBridge.BuildAsset(name + " Model", mesh.Vertices, mesh.Indices);
            if (animated) GModelPrimitiveFactory.ApplyTemplateRigAndClips(asset, "Humanoid", "Generic");
            if (collision) asset.Colliders.Add(GModelProductionTools.FitCollider(asset, GModelColliderShape.Mesh));
            string model = resources.CreateResource(models, ResourceKind.Model, name + " Model");
            using (ModelEditorControl editor = new(model, project.RootPath)) { editor.ApplyAnimationWorkspace(asset, ""); editor.Save(); }
            return Object(name, Relative(model), create);
        }
        string Object(string name, string model, string create = "", string step = "", string hud = "")
        {
            string file = resources.CreateResource(objects, ResourceKind.GameObject, name);
            JsonArray components = [];
            if (model.Length > 0) components.Add(new JsonObject { ["type"] = "ModelRendererComponent",
                ["props"] = new JsonObject { ["ModelAsset"] = model, ["ScaleX"] = 1, ["ScaleY"] = 1, ["ScaleZ"] = 1, ["CastShadows"] = true, ["ReceiveShadows"] = true } });
            JsonArray events = [];
            if (create.Length + step.Length + hud.Length > 0)
                components.Add(new JsonObject { ["type"] = "ScriptComponent", ["props"] = new JsonObject { ["ScriptClass"] = name } });
            string scripts = Path.Combine(objects, name); Directory.CreateDirectory(scripts);
            foreach (var pair in new[] { ("Create", create), ("Step", step), ("DrawGui", hud) })
                if (pair.Item2.Length > 0) { events.Add(pair.Item1); File.WriteAllText(Path.Combine(scripts, pair.Item1 + ".pgsl"), pair.Item2); }
            File.WriteAllText(file, new JsonObject { ["schemaVersion"] = 2, ["dimension"] = "ThreeD", ["model"] = model,
                ["components"] = components, ["events"] = events }.ToJsonString());
            return file;
        }
        void Place(string file, string name, float x, float y, float z, float yaw = 0) => room.Nodes.Add(new RoomNode
        { Kind = RoomNodeKind.GameObject, Name = name, LayerId = room.Layers[0].Id,
            GameObject = new RoomGameObjectData { Prefab = Relative(file) },
            Transform = new RoomTransform { X = x, Y = y, Z = z, RotationY = yaw } });
    }

    private static ModelPart Box(string name, float x, float y, float z, float sx, float sy, float sz, Vector3 colour) => new()
    { Name = name, Primitive = ModelPrimitiveKind.Cube, Position = [x, y, z], Scale = [sx, sy, sz], Color = [colour.X, colour.Y, colour.Z] };

    private const string CameraCreate = """
        routeTime = 0;
        SetMouseCaptured(false);
        Engine.FpsTarget(-1);
        Engine.FpsVsync(false);
        Engine.SetCameraFov(65);
        Engine.SetCameraNearPlane(0.1);
        Engine.SetCameraFarPlane(1000);
        Engine.SetAmbientLight(0.22, 0.18, 0.14);
        Engine.SetGlobalLightColor(0.6, 0.57, 0.5);
        """;

    private const string CameraStep = """
        routeTime += DeltaTime;
        var phase = routeTime % 48;
        var eyeX = 0;
        var eyeY = 1.7;
        var eyeZ = 30;
        var aimX = 0;
        var aimY = 1.6;
        var aimZ = -5;
        if (phase < 12) { eyeZ = 30 - phase * 2; }
        else if (phase < 24) { eyeX = 5; eyeZ = 6 - (phase - 12); aimX = -7; aimZ = -8; }
        else if (phase < 36) { eyeX = -6; eyeY = 5.4; eyeZ = 3 - (phase - 24) * 0.6; aimX = 4; aimY = 1.5; aimZ = -5; }
        else { eyeZ = 6 + (phase - 36) * 2; }
        SetCameraPosition(eyeX, eyeY, eyeZ);
        SetCameraTarget(aimX, aimY, aimZ);
        """;

    private const string Hud = """
        var uiScale = Clamp(WindowGetWidth() / 1920, 0.5, 2);
        DrawSetColorRgb(240, 218, 177);
        DrawSetFontSize(20 * uiScale);
        DrawText(24 * uiScale, 24 * uiScale, "TAVERN BENCHMARK — INITIAL FIXTURE");
        DrawText(24 * uiScale, 52 * uiScale, "Editable Models, Objects, Terrain and saved PGSL route");
        """;
}
