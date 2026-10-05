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

        string Object(string name, Dictionary<string, string> events)
        {
            string file = resources.CreateResource(objects, ResourceKind.GameObject, name);
            File.WriteAllText(file, new JsonObject
            {
                ["schemaVersion"] = 2, ["dimension"] = "TwoD",
                ["components"] = new JsonArray(new JsonObject
                {
                    ["type"] = "ScriptComponent", ["props"] = new JsonObject { ["ScriptClass"] = name },
                }),
                ["events"] = new JsonArray(events.Keys.Select(key => (JsonNode)key).ToArray()),
            }.ToJsonString());
            string folder = Path.Combine(objects, name);
            Directory.CreateDirectory(folder);
            foreach ((string eventName, string code) in events) File.WriteAllText(Path.Combine(folder, eventName + ".pgsl"), code);
            return Path.GetRelativePath(project.RootPath, file).Replace('\\', '/');
        }

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
                """,
        }));
        string final = Object("Final Probe", new()
        {
            ["Create"] = "frame = 0; FileWriteText(\"pgsl-results/second.txt\", \"visits=\" + String(GlobalGet(\"visits\")) + \";children=\" + String(InstanceNumber(\"Child\")) + \";\");",
            ["Step"] = """
                frame += 1;
                if (frame == 6) { ScreenshotSave("pgsl-room-two"); }
                if (frame == 10) { Print("GENESIS_PGSL_PROJECT_DONE"); GameQuit(); }
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
        return project;
    }
}
