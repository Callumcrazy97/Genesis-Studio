using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// PGSL in a real game: a two-room project run by the published Player on DX11, unattended. Each
/// logic group of <see cref="PgslLogicSuite"/> is an Object's Create event; a spawner churns
/// instances through <c>CreateInstance</c>, <c>with</c> and <c>InstanceDestroy</c> and calls a
/// library Script; a probe records the order of events; a painter draws known colours in Draw GUI.
/// The game writes its results with <c>FileWriteText</c>, takes pictures with
/// <c>ScreenshotSave</c>, changes room (globals must survive, instances must not) and quits.
/// </summary>
internal static class PgslProjectSuite
{
    private const string Done = "GENESIS_PGSL_PROJECT_DONE";

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "PgslProject");
        ProjectSession? project = null;
        string log = "";

        HeadlessHarness.RunCase(ctx.Report, "Pgsl.Project.TheGameRunsToTheEnd", () =>
        {
            project = Build(ctx);
            string runtime = NewestPlayer() ?? throw new InvalidOperationException("No Player found.");
            // The language checks themselves show an old Player (no break, no short-circuit...).
            File.WriteAllText(Path.Combine(ctx.Logs, "pgsl-project-player-path.txt"), runtime);
            ProcessStartInfo start = new(Path.Combine(runtime, RuntimePaths.RuntimeExeName))
            {
                WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.Environment["GENESIS_PROJECT_PATH"] = project.RootPath;
            start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
            start.Environment["GENESIS_RENDER_BACKEND"] = "DX11";
            foreach (string name in new[] { "GENESIS_START_ROOM", "GENESIS_AUTOSHOT", "GENESIS_BOOT_COORDINATED" }) start.Environment.Remove(name);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("The Player did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            log = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(ctx.Logs, "pgsl-project-player.log"), log);
            HeadlessHarness.Assert(log.Contains(Done, StringComparison.Ordinal),
                "The game did not reach its end (see pgsl-project-player.log): " + Tail(log));
            HeadlessHarness.Assert(!log.Contains("SCRIPT ERROR", StringComparison.OrdinalIgnoreCase),
                "A script reported an error: " + Tail(log));
        });

        string Results(string name)
        {
            string file = ProjectTextFiles.ResolvePath(project!.RootPath, "pgsl-results/" + name + ".txt");
            return File.Exists(file) ? File.ReadAllText(file) : "";
        }

        foreach ((string group, _) in PgslLogicSuite.LogicScripts)
        {
            HeadlessHarness.RunCase(ctx.Report, "Pgsl.Project.Logic." + group, () =>
            {
                HeadlessHarness.Assert(project != null, "The game did not run.");
                string results = Results(group);
                HeadlessHarness.Assert(results.Length > 0, $"The {group} object wrote no results.");
                List<string> failed = results.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(entry => !entry.EndsWith("=1", StringComparison.Ordinal)).ToList();
                HeadlessHarness.Assert(failed.Count == 0, $"{group} checks failed in the Player: {string.Join(", ", failed)}");
            });
        }

        HeadlessHarness.RunCase(ctx.Report, "Pgsl.Project.InstancesLibrariesAndRooms", () =>
        {
            HeadlessHarness.Assert(project != null, "The game did not run.");
            string world = Results("world"), second = Results("second");
            string mesh = Results("mesh");
            // 6 faces of 4 vertices; then 5 + 4 + 5 faces of a strip; the ray from 3.2 m lands on its top at 0.7 m.
            HeadlessHarness.Assert(mesh == "full=6;single=24;vertices=56;triangles=28;collider=true;hit=2.5;",
                $"The script-built mesh: '{mesh}'.");
            string pool = Results("pool");
            HeadlessHarness.Assert(pool == "self=-50;other=-60;otherZ=7;", $"Moved instances did not stay moved: '{pool}'.");
            string quad = Results("quad");
            HeadlessHarness.Assert(quad == "quad=0;tiles=6;shaded=28;vertices=32;triangles=16;",
                $"MeshAddQuad / MeshAddCubeTiles / MeshAddQuadColors: '{quad}'.");
            HeadlessHarness.Assert(world == "spawned=20;tagged=20;library=5;afterDestroy=15;childSteps=1;",
                $"Instances, with and the library Script in room one: '{world}'.");
            HeadlessHarness.Assert(second == "visits=2;children=0;",
                $"In room two the global should have survived and the instances should not: '{second}'.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Pgsl.Project.EventOrder", () =>
        {
            HeadlessHarness.Assert(project != null, "The game did not run.");
            string order = Results("order");
            // Create once; then per frame Step Begin, Step, Step End, Draw, Draw GUI.
            HeadlessHarness.Assert(order.StartsWith("C|BSEDG|BSEDG", StringComparison.Ordinal), $"Events ran in the order '{order}'.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Pgsl.Project.PicturesShowWhatWasDrawn", () =>
        {
            HeadlessHarness.Assert(project != null, "The game did not run.");
            string images = ProjectPaths.ImagesDir(project!.RootPath);
            string first = Path.Combine(images, "pgsl-room-one.png"), secondPicture = Path.Combine(images, "pgsl-room-two.png");
            HeadlessHarness.Assert(File.Exists(first) && File.Exists(secondPicture), $"ScreenshotSave wrote no pictures to {images}.");
            File.Copy(first, Path.Combine(ctx.Captures, "pgsl-room-one.png"), overwrite: true);
            File.Copy(secondPicture, Path.Combine(ctx.Captures, "pgsl-room-two.png"), overwrite: true);
            using (Bitmap one = new(first))
            {
                Expect(one, 70, 70, Color.FromArgb(255, 0, 0), "red square");
                Expect(one, 190, 70, Color.FromArgb(0, 255, 0), "green square");
                Expect(one, 310, 70, Color.FromArgb(0, 0, 255), "blue square");
                Expect(one, 430, 70, Color.FromArgb(0, 255, 0), "status square (green when every logic check passed)");
                Color outside = one.GetPixel(70, 200);
                HeadlessHarness.Assert(!(outside.R > 200 && outside.G < 60 && outside.B < 60), "The red square spread past its edges.");
            }
            using (Bitmap gui = new(first))
            {
                Color model = gui.GetPixel(580, 80);
                HeadlessHarness.Assert(model.R > 120 && model.R > model.B + 60,
                    $"DrawModelGui did not draw the orange box into its rectangle ({model}).");
            }
            // An engine setting read (Engine.Sky.SunDirectionX) must cost about what a command call does.
            string skyTime = Results("skytime");
            File.WriteAllText(Path.Combine(ctx.Logs, "pgsl-sky-setting-read.txt"), skyTime);
            Console.WriteLine("Sky setting read in the Player: " + skyTime);
            System.Text.RegularExpressions.Match skyRead = System.Text.RegularExpressions.Regex.Match(skyTime, @"sky=([0-9.]+)us");
            HeadlessHarness.Assert(skyRead.Success && double.Parse(skyRead.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 200,
                $"Reading Engine.Sky.SunDirectionX in the Player is slow: '{skyTime}'.");
            string sunShown = Path.Combine(images, "pgsl-sun-shown.png"), sunHidden = Path.Combine(images, "pgsl-sun-hidden.png");
            HeadlessHarness.Assert(File.Exists(sunShown) && File.Exists(sunHidden), "Room four took no pictures of the sun.");
            File.Copy(sunShown, Path.Combine(ctx.Captures, "pgsl-sun-shown.png"), overwrite: true);
            File.Copy(sunHidden, Path.Combine(ctx.Captures, "pgsl-sun-hidden.png"), overwrite: true);
            using (Bitmap shown = new(sunShown))
            using (Bitmap hidden = new(sunHidden))
            {
                Color sunAt = shown.GetPixel(shown.Width / 2, shown.Height / 2);
                Color skyAt = hidden.GetPixel(hidden.Width / 2, hidden.Height / 2);
                // Beside the disc, inside where its glow was.
                Color glowAt = hidden.GetPixel(hidden.Width / 2 + 25, hidden.Height / 2);
                Color glowWas = shown.GetPixel(shown.Width / 2 + 25, shown.Height / 2);
                HeadlessHarness.Assert(Math.Min(sunAt.R, Math.Min(sunAt.G, sunAt.B)) > 230,
                    $"The sun's disc was not at the centre of a view looking at it ({sunAt}; {Results("sun")}).");
                HeadlessHarness.Assert(Math.Min(skyAt.R, Math.Min(skyAt.G, skyAt.B)) < 215,
                    $"Engine.Sky.SunDiscVisible = false still drew the sun's disc ({skyAt}).");
                HeadlessHarness.Assert(glowAt.R + glowAt.G + glowAt.B <= glowWas.R + glowWas.G + glowWas.B,
                    $"The sun's glow got brighter with the disc hidden ({glowWas} then {glowAt}).");
            }
            string moonShown = Path.Combine(images, "pgsl-moon-shown.png"), moonHidden = Path.Combine(images, "pgsl-moon-hidden.png");
            HeadlessHarness.Assert(File.Exists(moonShown) && File.Exists(moonHidden), $"Room four took no pictures of the moon. {Results("moon")}");
            File.Copy(moonShown, Path.Combine(ctx.Captures, "pgsl-moon-shown.png"), overwrite: true);
            File.Copy(moonHidden, Path.Combine(ctx.Captures, "pgsl-moon-hidden.png"), overwrite: true);
            using (Bitmap shown = new(moonShown))
            using (Bitmap hidden = new(moonHidden))
            {
                Color moonAt = shown.GetPixel(shown.Width / 2, shown.Height / 2);
                Color nightAt = hidden.GetPixel(hidden.Width / 2, hidden.Height / 2);
                HeadlessHarness.Assert(moonAt.R + moonAt.G + moonAt.B > nightAt.R + nightAt.G + nightAt.B + 60,
                    $"Engine.Sky.MoonDiscVisible = false did not take the moon's disc away ({moonAt} then {nightAt}).");
            }
            string third = Path.Combine(images, "pgsl-room-three.png");
            HeadlessHarness.Assert(File.Exists(third), "Room three took no picture.");
            File.Copy(third, Path.Combine(ctx.Captures, "pgsl-room-three.png"), overwrite: true);
            using (Bitmap three = new(third))
            {
                Color centre = three.GetPixel(three.Width / 2, three.Height / 2);
                Color side = three.GetPixel(three.Width / 2 + three.Width / 5, three.Height / 2);
                // The clock panel spans about pixels 230-390 across whatever the camera's field of view.
                Color clock = three.GetPixel(320, three.Height / 2);
                HeadlessHarness.Assert(clock.G > clock.R + 60,
                    $"A mesh shader did not see the frame clock (GenesisFrame at b4): {clock}.");
                Color portrait = three.GetPixel(80, 80);
                HeadlessHarness.Assert(portrait.R > 170 && portrait.R > portrait.B + 100,
                    $"A GUI model in a dark room was not lit by its own light ({portrait}).");
                HeadlessHarness.Assert(centre.R > 45 && centre.R > centre.B + 40,
                    $"The layer-2 box behind the wall was not drawn over it (or hidden layer 3 covered it) ({centre}).");
                HeadlessHarness.Assert(side.B > side.R + 25, $"The wall beside the box is not blue ({side}).");
                int green = 0;
                for (int y = three.Height / 2; y < three.Height; y += 4)
                    for (int x = 0; x < three.Width; x += 4)
                    {
                        Color c = three.GetPixel(x, y);
                        if (c.G > c.R + 30 && c.G > c.B + 15) green++;
                    }
                HeadlessHarness.Assert(green > 40, $"The script-built green mesh was not drawn ({green} green samples).");
            }
            using Bitmap two = new(secondPicture);
            Expect(two, 70, 70, Color.FromArgb(255, 255, 0), "yellow square in room two");
            Color left = two.GetPixel(150, 230), right = two.GetPixel(350, 230);
            HeadlessHarness.Assert(left.R > right.R + 80 && right.B > left.B + 80,
                $"The gradient in room two does not run from red to blue ({left} to {right}).");
        });
    }

    private static void Expect(Bitmap picture, int x, int y, Color expected, string what)
    {
        Color actual = picture.GetPixel(x, y);
        bool near = Math.Abs(actual.R - expected.R) < 24 && Math.Abs(actual.G - expected.G) < 24 && Math.Abs(actual.B - expected.B) < 24;
        HeadlessHarness.Assert(near, $"The {what} at ({x}, {y}) is {actual}, not {expected}.");
    }

    // The newest Player within reach: the one Build.bat staged beside this test, or the one it
    // last published (Genesis Application\Player above the test's folder). A stale copy beside a
    // developer's test build can be older than the engine under test.
    private static string? NewestPlayer()
    {
        List<string> candidates = [];
        if (RuntimePaths.ResolveRuntimeDir() is { } resolved) candidates.Add(resolved);
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string published = Path.Combine(directory.FullName, "Genesis Application", "Player");
            if (File.Exists(Path.Combine(published, RuntimePaths.RuntimeExeName))) { candidates.Add(published); break; }
        }
        return candidates
            .Where(path => File.Exists(Path.Combine(path, "Genesis.Runtime.dll")))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "Genesis.Runtime.dll")))
            .FirstOrDefault();
    }

    private static string Tail(string log) => log.Length > 1500 ? log[^1500..] : log;

    private static ProjectSession Build(HeadlessContext ctx)
    {
        string parent = Path.Combine(ctx.Workspace, "PgslProject");
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        Directory.CreateDirectory(parent);
        ProjectSession project = new ProjectService().CreateProject(parent, "PGSL Project", "Blank");
        ResourceService resources = new(project);
        string objects = Path.Combine(project.AssetsPath, "Objects");
        Directory.CreateDirectory(objects);

        string Object(string name, Dictionary<string, string> events, string? model = null)
        {
            string file = resources.CreateResource(objects, ResourceKind.GameObject, name);
            var components = new JsonArray(new JsonObject
            {
                ["type"] = "ScriptComponent", ["props"] = new JsonObject { ["ScriptClass"] = name },
            });
            if (model != null)
                components.Add(new JsonObject { ["type"] = "ModelRendererComponent", ["props"] = new JsonObject { ["ModelAsset"] = model } });
            File.WriteAllText(file, new JsonObject
            {
                ["schemaVersion"] = 2, ["dimension"] = model != null ? "ThreeD" : "TwoD",
                ["components"] = components,
                ["events"] = new JsonArray(events.Keys.Select(key => (JsonNode)key).ToArray()),
            }.ToJsonString());
            string folder = Path.Combine(objects, name);
            Directory.CreateDirectory(folder);
            foreach ((string eventName, string code) in events) File.WriteAllText(Path.Combine(folder, eventName + ".pgsl"), code);
            return Path.GetRelativePath(project.RootPath, file).Replace('\\', '/');
        }

        // Two plain models: an orange box (drawn in the GUI and in the first-person layer) and a blue wall.
        string models = Path.Combine(project.AssetsPath, "Models");
        Directory.CreateDirectory(models);
        void Model(string name, System.Numerics.Vector4 colour)
        {
            string file = Path.Combine(models, name + ".model.json");
            File.WriteAllText(file, "{}");
            var asset = Genesis.Runtime.Modeling.GModelPrimitiveFactory.CreateCube(name, 1f);
            asset.Materials[0].BaseColor = colour;
            Genesis.Runtime.Modeling.StudioModelResourceLoader.SaveCanonical(file, asset);
        }
        Model("Box", new System.Numerics.Vector4(1f, 0.45f, 0f, 1f));
        Model("Wall", new System.Numerics.Vector4(0.1f, 0.2f, 1f, 1f));

        // The library every room's objects can call.
        string scripts = Path.Combine(project.AssetsPath, "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "TestLibrary.pgsl"), "function LibAdd(a, b) { return a + b; }\n");

        List<string> roomOne = [];
        foreach ((string group, string code) in PgslLogicSuite.LogicScripts)
        {
            // After the checks, report every t_ variable and count the failures in a global.
            var report = new StringBuilder(code);
            report.AppendLine().AppendLine("report = \"\";");
            foreach (string check in Regex.Matches(code, @"\bt_[a-z0-9_]+(?=\s*=)").Select(match => match.Value).Distinct())
            {
                report.AppendLine($"report = report + \"{check}=\" + String({check}) + \";\";");
                report.AppendLine($"if ({check} != 1) {{ GlobalSet(\"fails\", GlobalGet(\"fails\") + 1); }}");
            }
            report.AppendLine($"FileWriteText(\"pgsl-results/{group}.txt\", report);");
            roomOne.Add(Object("Logic " + group, new() { ["Create"] = report.ToString() }));
        }

        Object("Child", new()
        {
            ["Create"] = "tagged = 0;",
            ["Step"] = "GlobalSet(\"childSteps\", 1);",
        });
        roomOne.Add(Object("Spawner", new()
        {
            ["Create"] = """
                GlobalSet("visits", 1);
                for (i = 0; i < 20; i = i + 1) { CreateInstance("Child", 600 + i * 4, 400, 0); }
                with ("Child") { tagged = 1; }
                tagged = 0;
                frame = 0;
                spawned = InstanceNumber("Child");
                """,
            ["Step"] = """
                frame += 1;
                if (frame == 2)
                {
                    count = 0;
                    with ("Child") { if (tagged == 1) { GlobalSet("taggedCount", GlobalGet("taggedCount") + 1); } }
                    var destroyed = 0;
                    with ("Child") { if (destroyed < 5) { InstanceDestroy(id); destroyed += 1; } }
                }
                if (frame == 4)
                {
                    FileWriteText("pgsl-results/world.txt", "spawned=" + String(spawned) + ";tagged=" + String(GlobalGet("taggedCount"))
                        + ";library=" + String(LibAdd(2, 3)) + ";afterDestroy=" + String(InstanceNumber("Child"))
                        + ";childSteps=" + String(GlobalGet("childSteps")) + ";");
                }
                if (frame == 10) { ScreenshotSave("pgsl-room-one"); }
                if (frame == 14) { GlobalSet("visits", GlobalGet("visits") + 1); RoomGoto("Second"); }
                """,
        }));
        roomOne.Add(Object("Order Probe", new()
        {
            ["Create"] = "GlobalSetString(\"order\", \"C\"); frames = 0;",
            ["StepBegin"] = "frames += 1; if (frames <= 2) { GlobalSetString(\"order\", GlobalGetString(\"order\") + \"|B\"); }",
            ["Step"] = "if (frames <= 2) { GlobalSetString(\"order\", GlobalGetString(\"order\") + \"S\"); }",
            ["StepEnd"] = "if (frames <= 2) { GlobalSetString(\"order\", GlobalGetString(\"order\") + \"E\"); }",
            ["Draw"] = "if (frames <= 2) { GlobalSetString(\"order\", GlobalGetString(\"order\") + \"D\"); }",
            ["DrawGui"] = "if (frames <= 2) { GlobalSetString(\"order\", GlobalGetString(\"order\") + \"G\"); } if (frames == 3) { FileWriteText(\"pgsl-results/order.txt\", GlobalGetString(\"order\")); }",
        }));
        roomOne.Add(Object("Painter", new()
        {
            ["DrawGui"] = """
                DrawSetAlpha(1);
                DrawSetColorRgb(255, 0, 0); DrawRectangle(20, 20, 120, 120);
                DrawSetColorRgb(0, 255, 0); DrawRectangle(140, 20, 240, 120);
                DrawSetColorRgb(0, 0, 255); DrawRectangle(260, 20, 360, 120);
                if (GlobalGet("fails") == 0) { DrawSetColorRgb(0, 255, 0); } else { DrawSetColorRgb(255, 0, 0); }
                DrawRectangle(380, 20, 480, 120);
                DrawModelGui("Box", 520, 20, 120, 120, 35, 25, 1);
                """,
        }));
        string final = Object("Final Probe", new()
        {
            ["Create"] = "frame = 0; FileWriteText(\"pgsl-results/second.txt\", \"visits=\" + String(GlobalGet(\"visits\")) + \";children=\" + String(InstanceNumber(\"Child\")) + \";\");",
            ["Step"] = """
                frame += 1;
                if (frame == 6) { ScreenshotSave("pgsl-room-two"); }
                if (frame == 10) { RoomGoto("Third"); }
                """,
            ["DrawGui"] = """
                DrawSetAlpha(1);
                DrawSetColorRgb(255, 255, 0); DrawRectangle(20, 20, 120, 120);
                DrawRectangleGradient(100, 200, 400, 260, 255, 0, 0, 1, 0, 0, 255, 1, 0);
                """,
        });

        string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom);
        RoomAsset start = RoomAssetLoader.Parse(roomFile);
        start.Settings.CaptureMouse = false;
        foreach (string prefab in roomOne)
            start.Nodes.Add(new RoomNode
            {
                Kind = RoomNodeKind.GameObject, Name = Path.GetFileNameWithoutExtension(prefab), LayerId = start.Layers[0].Id,
                GameObject = new RoomGameObjectData { Prefab = prefab },
            });
        RoomAssetLoader.Save(start, roomFile);

        string secondFile = resources.CreateResource(Path.GetDirectoryName(roomFile)!, ResourceKind.Room, "Second");
        RoomAsset second = RoomAsset.Create("Second", start.Dimension);
        second.Settings.CaptureMouse = false;
        second.Nodes.Add(new RoomNode
        {
            Kind = RoomNodeKind.GameObject, Name = "Final Probe", LayerId = second.Layers[0].Id,
            GameObject = new RoomGameObjectData { Prefab = final },
        });
        RoomAssetLoader.Save(second, secondFile);

        // Room three (3D): an orange box in model layer 2 stands behind a blue wall, yet
        // must be drawn over it.
        string probe = Object("Third Probe", new()
        {
            ["Create"] = "frame = 0; SetCameraPosition(0, 1, 0); SetCameraTarget(0, 1, 10);",
            ["Step"] = """
                frame += 1;
                SetCameraPosition(0, 1, 0); SetCameraTarget(0, 1, 10);
                if (frame == 10) { ScreenshotSave("pgsl-room-three"); }
                if (frame == 14) { RoomGoto("Fourth"); }
                """,
            // A GUI model in this dark room is lit by its own studio light, not the room's.
            ["DrawGui"] = "DrawModelGui(\"Box\", 20, 20, 120, 120, 35, 25, 1);",
        });
        string arms = Object("Arms", new() { ["Create"] = "ModelSetLayer(2); ModelLayerSetFov(2, 60);" }, model: "Box");
        // Layer 3 is drawn after layer 2 and would cover the box in blue, but it is hidden.
        string cover = Object("Cover", new() { ["Create"] = "ModelSetLayer(3); ModelLayerSetVisible(3, false);" }, model: "Wall");
        string wall = Object("Wall", new() { ["Create"] = "noop = 0;" }, model: "Wall");
        // A script-built voxel strip: three green cubes in one mesh, only their outer faces.
        string chunk = Object("Chunk", new()
        {
            ["Create"] = """
                m = MeshCreate();
                full = MeshAddCube(m, 0, 0, 0, 1, 63, 255, 255, 255, 0, 0, 1, 1);
                single = MeshVertexCount(m);
                MeshClear(m);
                MeshAddCube(m, -1, 0, 0, 1, 1 + 2 + 4 + 8 + 16 + 32 - 1, 60, 200, 60, 0, 0, 1, 1);
                MeshAddCube(m, 0, 0, 0, 1, 4 + 8 + 16 + 32, 60, 200, 60, 0, 0, 1, 1);
                MeshAddCube(m, 1, 0, 0, 1, 63 - 2, 60, 200, 60, 0, 0, 1, 1);
                collider = InstanceSetMeshCollider(id, m);
                hit = PhysicsRaycast(0, 3.2, 2.2, 0, -1, 0, 10);
                FileWriteText("pgsl-results/mesh.txt", "full=" + String(full) + ";single=" + String(single)
                    + ";vertices=" + String(MeshVertexCount(m)) + ";triangles=" + String(MeshTriangleCount(m))
                    + ";collider=" + String(collider) + ";hit=" + String(Round(hit * 10) / 10) + ";");
                q = MeshCreate();
                quad = MeshAddQuad(q, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1, 255, 255, 255, 1);
                tiles = MeshAddCubeTiles(q, 0, 0, 0, 1, 63, 255, 255, 255, 0, 0, 0.5, 0.5, 0.5, 0, 1, 0.5, 0, 0.5, 0.5, 1);
                shaded = MeshAddQuadColors(q, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1,
                    255, 0, 0, 1, 0, 255, 0, 1, 0, 0, 255, 1, 255, 255, 255, 0.5, 1);
                FileWriteText("pgsl-results/quad.txt", "quad=" + String(quad) + ";tiles=" + String(tiles)
                    + ";shaded=" + String(shaded) + ";vertices=" + String(MeshVertexCount(q))
                    + ";triangles=" + String(MeshTriangleCount(q)) + ";");
                """,
            ["Draw"] = "DrawMeshSetShadows(false, true); DrawMeshSetCull(false); DrawMesh3D(m, x, y, z, \"\"); DrawMeshResetState();",
        });
        string thirdFile = resources.CreateResource(Path.GetDirectoryName(roomFile)!, ResourceKind.Room, "Third");
        RoomAsset third = RoomAsset.Create("Third", RoomDimension.ThreeD);
        third.Settings.CaptureMouse = false;
        void Place(string name, string prefab, float[] position, float[] scale) => third.Nodes.Add(new RoomNode
        {
            Kind = RoomNodeKind.GameObject, Name = name, LayerId = third.Layers[0].Id,
            Transform = new RoomTransform { Position = position, Scale = scale },
            GameObject = new RoomGameObjectData { Prefab = prefab },
        });
        Place("Third Probe", probe, [0f, 0f, 0f], [1f, 1f, 1f]);
        Place("Wall", wall, [0f, 1f, 3f], [6f, 6f, 0.3f]);
        Place("Arms", arms, [0f, 1f, 6f], [1.5f, 1.5f, 1.5f]);
        Place("Cover", cover, [0f, 1f, 5f], [3f, 3f, 0.3f]);
        Place("Chunk", chunk, [0f, 0.2f, 2.2f], [1f, 1f, 1f]);
        // A mesh Shader resource reads the frame clock from GenesisFrame (b4): green once Time and
        // Frame have moved on, red while they read 0.
        string shaderFolder = Path.Combine(project.AssetsPath, "Shaders");
        Directory.CreateDirectory(shaderFolder);
        File.WriteAllText(Path.Combine(shaderFolder, "Clock.shader.json"), System.Text.Json.JsonSerializer.Serialize(
            new Genesis.Shared.Assets.ShaderAssetDocument
            {
                Pipeline = Genesis.Shared.Assets.ShaderAssetPipeline.Mesh,
                Entry = "MainPS",
                Source = """
                    cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
                    struct VSOut { float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2; float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; };
                    float4 MainPS(VSOut input) : SV_Target
                    {
                        return Time > 0.25 && Frame > 2 && Resolution.x > 0 ? float4(0, 1, 0, 1) : float4(1, 0, 0, 1);
                    }
                    """,
            },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        string clockPanel = Object("Clock Panel", new()
        {
            ["Create"] = "panel = MeshCreate(); MeshAddQuad(panel, -2, 0.8, 2.5, -1, 0.8, 2.5, -1, 1.4, 2.5, -2, 1.4, 2.5, 0, 0, -1, 0, 0, 1, 1, 255, 255, 255, 1);",
            ["Draw"] = "DrawMeshSetCull(false); DrawMeshShader3D(panel, \"Clock\", 0, 0, 0, 1, 1, 1, 0, \"\"); DrawMeshResetState();",
        });

        // Pooling: a rotated instance moved away by InstanceSetY must stay there (the pose is
        // copied back every frame), whether it moves itself or another instance moves it.
        string pooled = Object("Pooled", new()
        {
            ["Create"] = "frame = 0; InstanceSetRotation3D(id, 0, 30, 0);",
            ["Step"] = """
                frame += 1;
                if (frame == 2) {
                    other = InstanceFind("Pooled Other", 0);
                    InstanceSetY(id, -50);
                    InstanceSetY(other, -60);
                    InstanceVariableSet(other, "z", 7);
                }
                if (frame == 6) {
                    FileWriteText("pgsl-results/pool.txt", "self=" + String(Round(InstanceGetY(id))) + ";other="
                        + String(Round(InstanceGetY(other))) + ";otherZ=" + String(Round(InstanceGetZ(other))) + ";");
                }
                """,
        }, model: "Box");
        string pooledOther = Object("Pooled Other", new() { ["Create"] = "InstanceSetRotation3D(id, 0, 60, 0);" }, model: "Box");
        Place("Pooled", pooled, [4f, 1f, 9f], [0.5f, 0.5f, 0.5f]);
        Place("Clock Panel", clockPanel, [0f, 0f, 0f], [1f, 1f, 1f]);
        Place("Pooled Other", pooledOther, [-4f, 1f, 9f], [0.5f, 0.5f, 0.5f]);
        RoomAssetLoader.Save(third, thirdFile);

        // Room four (3D, the engine's own sky): looking straight at the afternoon sun, first with
        // its disc, then with Engine.Sky.SunDiscVisible = false (no disc and no glow).
        string skyProbe = Object("Sky Probe", new()
        {
            ["Create"] = "frame = 0; stage = 0; hiddenAt = 0; moonAt = 0; hour = 18; wait = 0;",
            // Each picture is taken when its frame is drawn, which can be a few Steps later: the
            // disc is hidden only once the first picture has been taken.
            ["Step"] = """
                frame += 1;
                SetCameraPosition(0, 2, 0);
                SetCameraTarget(Engine.Sky.SunDirectionX * 10, 2 + Engine.Sky.SunDirectionY * 10, Engine.Sky.SunDirectionZ * 10);
                if (frame == 5) {
                    t0 = TimeMs(); k = 0; acc = 0;
                    while (k < 200) { acc += Engine.Sky.SunDirectionX; k += 1; }
                    t1 = TimeMs(); k = 0;
                    while (k < 200) { acc += GameGetSpeed(); k += 1; }
                    FileWriteText("pgsl-results/skytime.txt", "sky=" + String((t1 - t0) * 1000 / 200) + "us;call=" + String((TimeMs() - t1) * 1000 / 200) + "us;");
                }
                if (frame == 8) {
                    ScreenshotSave("pgsl-sun-shown");
                    FileWriteText("pgsl-results/sun.txt", "dir=" + String(Engine.Sky.SunDirectionX) + "," + String(Engine.Sky.SunDirectionY) + ","
                        + String(Engine.Sky.SunDirectionZ) + ";time=" + String(Engine.Sky.TimeOfDay) + ";");
                    stage = 1;
                }
                if (stage == 1 && ScreenshotPending() == 0) { Engine.Sky.SunDiscVisible = false; hiddenAt = frame; stage = 2; }
                if (stage == 2 && frame == hiddenAt + 4) { ScreenshotSave("pgsl-sun-hidden"); stage = 3; }
                // Then the moon: the first hour from 18:00 on when it stands well above the horizon.
                if (stage == 3 && ScreenshotPending() == 0) { Engine.Sky.SetTimeOfDay(hour); wait = frame; stage = 4; }
                if (stage == 4 && frame >= wait + 2) {
                    if (Engine.Sky.MoonDirectionY > 0.35) { moonAt = frame; stage = 5; }
                    else {
                        hour += 1;
                        if (hour >= 42) { FileWriteText("pgsl-results/moon.txt", "no moon"); stage = 9; }
                        else { Engine.Sky.SetTimeOfDay(hour % 24); wait = frame; }
                    }
                }
                if (stage >= 5) {
                    SetCameraTarget(Engine.Sky.MoonDirectionX * 10, 2 + Engine.Sky.MoonDirectionY * 10, Engine.Sky.MoonDirectionZ * 10);
                }
                if (stage == 5 && frame == moonAt + 4) { ScreenshotSave("pgsl-moon-shown"); stage = 6; }
                if (stage == 6 && ScreenshotPending() == 0) { Engine.Sky.MoonDiscVisible = false; moonAt = frame; stage = 7; }
                if (stage == 7 && frame == moonAt + 4) { ScreenshotSave("pgsl-moon-hidden"); stage = 8; }
                if ((stage == 8 || stage == 9) && ScreenshotPending() == 0) { Print("GENESIS_PGSL_PROJECT_DONE"); GameQuit(); }
                """,
        });
        string fourthFile = resources.CreateResource(Path.GetDirectoryName(roomFile)!, ResourceKind.Room, "Fourth");
        RoomAsset fourth = RoomAsset.Create("Fourth", RoomDimension.ThreeD);
        fourth.Settings.CaptureMouse = false;
        fourth.Environment.DynamicSky = true;
        fourth.Environment.Weather = "Clear";
        fourth.Environment.TimeOfDayHours = 15f;
        fourth.Environment.LatitudeDegrees = 0f;
        fourth.Environment.DayOfYear = 80;
        fourth.Nodes.Add(new RoomNode
        {
            Kind = RoomNodeKind.GameObject, Name = "Sky Probe", LayerId = fourth.Layers[0].Id,
            Transform = new RoomTransform { Position = [0f, 0f, 0f], Scale = [1f, 1f, 1f] },
            GameObject = new RoomGameObjectData { Prefab = skyProbe },
        });
        RoomAssetLoader.Save(fourth, fourthFile);
        return project;
    }
}
