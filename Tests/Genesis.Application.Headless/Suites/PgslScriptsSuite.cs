using Genesis.Application.Core.Projects;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Script resources called from other scripts and from expressions: each call keeps its own
/// stack values, arguments and functions, as a game written across many scripts needs.
/// </summary>
internal static class PgslScriptsSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "PgslScripts");
        string parent = Path.Combine(ctx.Workspace, "PgslScripts");
        Directory.CreateDirectory(parent);
        ProjectSession project = new ProjectService().CreateProject(parent, "Scripts", "Blank");
        string scripts = Path.Combine(project.AssetsPath, "Scripts");
        Directory.CreateDirectory(scripts);
        void Script(string name, string body) => File.WriteAllText(Path.Combine(scripts, name + ".pgsl"), body);
        Script("VoidCaller", "ArraySet(\"q\", 0, 1);\nreturn 2;\n");
        Script("ArgDoubler", "return argument0 * 2;\n");
        Script("ArgKeeper", "var t = ArgDoubler(5);\nreturn argument0 + t;\n");
        Script("HelperThree", "function Helper() { return 3; }\nreturn Helper();\n");
        Script("HelperFour", "function Helper() { return 4; }\nvar c = HelperThree();\nreturn Helper() * 10 + c;\n");
        ResourceNames.Invalidate(project.RootPath);

        ObjectSandboxResult RunCreate(string code)
        {
            string previous = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ProjectPath = project.RootPath;
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);
                return ObjectSandbox.Run(new Dictionary<string, string> { ["Create"] = code }, frames: 1);
            }
            finally { PgslCommands.ProjectPath = previous; ScriptAssetRegistry.ClearCache(); }
        }
        static string Errors(ObjectSandboxResult result) => string.Join(" | ", result.Errors);

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ACommandThatReturnsNothingKeepsTheCallersValues", () =>
        {
            ObjectSandboxResult result = RunCreate(
                "r = 10 + VoidCaller();\n" +
                "m = Max(1, VoidCaller());\n" +
                "function F() { ArraySet(\"w\", 0, 1); return 5; }\n" +
                "s = 1 + F();\n");
            Check(result.Ok, "A script or function calling a command that returns nothing broke its caller's expression: " + Errors(result));
            Check(Near(result, "r", 12) && Near(result, "m", 2) && Near(result, "s", 6),
                $"Wrong values: r={Value(result, "r")} (12), m={Value(result, "m")} (2), s={Value(result, "s")} (6).");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ACalledScriptKeepsItsOwnArgumentsAndFunctions", () =>
        {
            ObjectSandboxResult result = RunCreate("a = ArgKeeper(1);\nd = HelperFour();\n");
            Check(result.Ok, "Calling one script from another failed: " + Errors(result));
            Check(Near(result, "a", 11), $"A script that called another read the callee's argument0: got {Value(result, "a")}, expected 11.");
            Check(Near(result, "d", 43), $"A script's function was replaced by another script's function of the same name: got {Value(result, "d")}, expected 43.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.AnArraySlotNeverWrittenReadsAsEmptyText", () =>
        {
            ObjectSandboxResult result = RunCreate(
                "ArraySetString(\"t\", 5, \"x\");\n" +
                "empty = 0;\nif (ArrayGetString(\"t\", 2) == \"\") empty = 1;\n" +
                "zero = ArrayGet(\"t\", 2);\n" +
                "five = 0;\nif (ArrayGetString(\"t\", 5) == \"x\") five = 1;\n");
            Check(result.Ok && Near(result, "empty", 1) && Near(result, "zero", 0) && Near(result, "five", 1),
                "An array slot below the written one did not read as empty text and 0: " + Errors(result));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.GlobalsOutliveTheInstanceThatSetThem", () =>
        {
            PgslCommands.ResetSession();
            ObjectSandboxResult setter = RunCreate("GlobalSet(\"deployMode\", 2);\nGlobalSetString(\"name\", \"Ana\");\n");
            Check(setter.Ok, "Setting globals failed: " + Errors(setter));
            // A second object, as in the next room, reads them.
            ObjectSandboxResult reader = RunCreate(
                "m = GlobalGet(\"deployMode\");\n" +
                "n = 0;\nif (GlobalGetString(\"name\") == \"Ana\") n = 1;\n" +
                "e = GlobalExists(\"never\");\n" +
                "GlobalDelete(\"name\");\ngone = GlobalExists(\"name\");\n");
            Check(reader.Ok && Near(reader, "m", 2) && Near(reader, "n", 1) && Near(reader, "e", 0) && Near(reader, "gone", 0),
                $"Globals did not carry over: m={Value(reader, "m")} n={Value(reader, "n")} e={Value(reader, "e")} gone={Value(reader, "gone")} " + Errors(reader));
            PgslCommands.ResetSession();
            Check(PgslCommands.GlobalGet("deployMode") == 0, "A new game kept the last game's globals.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.GameEndAndGameQuitCloseTheGame", () =>
        {
            int asked = 0;
            PgslCommands.ResetSession();
            PgslCommands.GameQuitHandler = () => asked++;
            try
            {
                ObjectSandboxResult result = RunCreate("GameQuit();\nGameEnd();\n");
                Check(result.Ok, "GameQuit/GameEnd failed: " + Errors(result));
                Check(asked == 2 && PgslCommands.GameQuitRequested, $"The game was asked to close {asked} times (2 expected).");
            }
            finally
            {
                PgslCommands.GameQuitHandler = null;
                PgslCommands.ResetSession();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.TimeMsRunsInRealTime", () =>
        {
            ObjectSandboxResult before = RunCreate("t = TimeMs();\n");
            Thread.Sleep(40);
            ObjectSandboxResult after = RunCreate("t = TimeMs();\n");
            double elapsed = Value(after, "t") - Value(before, "t");
            Check(elapsed >= 35 && elapsed < 5000, $"TimeMs advanced {elapsed:F1} ms over a 40 ms wait.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.InverseTrigonometryIsInDegrees", () =>
        {
            ObjectSandboxResult result = RunCreate("s = ArcSin(1);\nc = ArcCos(0.5);\na = ArcTan2(1, 0);\nb = ArcTan2(0, -1);\n");
            Check(result.Ok && Near(result, "s", 90) && Math.Abs(Value(result, "c") - 60) < 1e-9 && Near(result, "a", 90) && Near(result, "b", 180),
                $"ArcSin(1)={Value(result, "s")} ArcCos(0.5)={Value(result, "c")} ArcTan2(1,0)={Value(result, "a")} ArcTan2(0,-1)={Value(result, "b")}.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Input.TypedTextComesInTheOrderTyped", () =>
        {
            Genesis.Runtime.Input.InputState input = new();
            foreach (char typed in "Ana \u00e9\b!") input.OnChar(typed);
            Check(input.TakeTypedText() == "Ana \u00e9!", "Typed characters were lost, reordered, or a control character was kept.");
            Check(input.TakeTypedText() == string.Empty, "Typed text was handed out twice.");
            Check(input.LeftStickDeadZone == 0.18f && input.TriggerDeadZone == 0f, "The controller's default dead zones changed.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.WithRunsItsBlockAsEachInstance", () =>
        {
            using var scene = new Genesis.Runtime.RuntimeScene("With test");
            var game = new Genesis.Runtime.Project.ProjectGameContext(ctx.Workspace, scene, null, null,
                Genesis.Runtime.Scene.RoomAsset.Create("Probe", Genesis.Runtime.Scene.RoomDimension.ThreeD), null);
            Genesis.Shared.Scripting.PgslContext previousContext = PgslCommands.BindContext(new Genesis.Shared.Scripting.PgslContext());
            var previousGame = PgslCommands.ActiveGameContext;
            string previousPath = PgslCommands.ProjectPath;
            PgslCommands.ActiveGameContext = game;
            PgslCommands.ProjectPath = ctx.Workspace;
            try
            {
                Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
                var host = new ScriptHostSystem();
                host.SetContext(game);
                var world = scene.World;
                Genesis.Shared.ECS.Entity Make(float x)
                {
                    var entity = world.CreateEntity();
                    world.Set(entity, new Genesis.Runtime.ECS.Components.TransformComponent { X = x, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                    return entity;
                }
                var first = Make(10);
                var second = Make(20);
                var controller = Make(0);
                using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = "gswRev = 0;\n" }))
                {
                    host.Attach(world, first, "Weapon");
                    host.Attach(world, second, "Weapon");
                }
                using (host.UseEventSources(new Dictionary<string, string>
                {
                    ["Create"] =
                        "var step = 3;\nmine = 5;\n" +
                        "with (Weapon) { gswRev = gswRev + step; x = x + 1; }\n" +
                        "count = InstanceNumber(\"Weapon\");\nlead = InstanceFind(\"Weapon\", 0);\n" +
                        "InstanceVariableSet(lead, \"label\", \"lead\");\nread = InstanceVariableGet(lead, \"gswRev\");\n" +
                        "with (lead) { solo = 1; }\nafter = x;\n",
                })) host.Attach(world, controller, "Controller");

                var scripts = host.Instances.OfType<PgslBehavior>().ToList();
                Check(scripts.Count == 3, $"Expected three scripted instances, got {scripts.Count}: "
                    + string.Join("; ", host.RecentDiagnostics.Select(d => d.Message)));
                IReadOnlyDictionary<string, object> one = scripts[0].GetVariablesSnapshot();
                IReadOnlyDictionary<string, object> two = scripts[1].GetVariablesSnapshot();
                IReadOnlyDictionary<string, object> boss = scripts[2].GetVariablesSnapshot();
                double Number(IReadOnlyDictionary<string, object> vars, string name) =>
                    vars.TryGetValue(name, out object? value) ? Convert.ToDouble(value) : double.NaN;
                Check(Number(one, "gswRev") == 3 && Number(two, "gswRev") == 3,
                    $"with did not set each Weapon's own variable: {Number(one, "gswRev")}, {Number(two, "gswRev")} (3, 3). "
                    + string.Join("; ", host.RecentDiagnostics.Select(d => d.Message)));
                var transforms = new[] { first, second, controller }
                    .Select(e => world.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(e).X).ToArray();
                Check(transforms[0] == 11 && transforms[1] == 21 && transforms[2] == 0,
                    $"with did not move each Weapon (and only them): x = {string.Join(", ", transforms)} (11, 21, 0).");
                Check(!boss.ContainsKey("gswRev") && Number(boss, "mine") == 5 && Number(boss, "after") == 0,
                    "The controller's own variables were changed by the block it ran as other instances.");
                Check(Number(boss, "count") == 2 && Number(boss, "lead") == first.Id && Number(boss, "read") == 3,
                    $"InstanceNumber/InstanceFind/InstanceVariableGet gave {Number(boss, "count")}, {Number(boss, "lead")}, {Number(boss, "read")}.");
                Check(one.TryGetValue("label", out object? label) && Equals(label, "lead") && Number(one, "solo") == 1 && !two.ContainsKey("solo"),
                    "InstanceVariableSet or with (id) did not reach exactly the one instance.");
            }
            finally
            {
                PgslCommands.BindContext(previousContext);
                PgslCommands.ActiveGameContext = previousGame;
                PgslCommands.ProjectPath = previousPath;
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.AParameterNamedLikeABuiltInIsTheArgument", () =>
        {
            ObjectSandboxResult result = RunCreate(
                "x = 100;\n" +
                "function Place(x, y) { var speed = x + y; return speed * 2; }\n" +
                "function Lookup(id) { return id; }\n" +
                "r = Place(3, 4);\nl = Lookup(7);\nafter = x;\nsp = speed;\n");
            Check(result.Ok, "Functions with parameters named like built-ins failed: " + Errors(result));
            Check(Near(result, "r", 14) && Near(result, "l", 7),
                $"Parameters named x, y and id read the instance's values: Place gave {Value(result, "r")} (14), Lookup {Value(result, "l")} (7).");
            Check(Near(result, "after", 100) && Near(result, "sp", 0),
                $"A function's parameter or local changed the instance: x = {Value(result, "after")} (100), speed = {Value(result, "sp")} (0).");
            var strict = new PgslSemanticOptions { Strict = true };
            List<PgslDiagnostic> found = PgslSemanticChecker.Check(PgslAstBuilder.Parse("function F(id) { return id; }\nv = F(3);\n"), strict).ToList();
            Check(!found.Any(d => d.Severity == PgslDiagnostic.Kind.Error), "The strict check rejects a parameter named like a built-in, which now works.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.AnObjectsEventsShareTheirFunctionsInValidation", () =>
        {
            var resources = new Genesis.Application.Core.Resources.ResourceService(project);
            string objectPath = resources.CreateResource(resources.AssetsRoot,
                Genesis.Application.Core.Resources.ResourceKind.GameObject, "Shared Helper Object");
            string create = ObjectEventStore.PathFor(objectPath, "Create");
            string step = ObjectEventStore.PathFor(objectPath, "Step");
            Directory.CreateDirectory(Path.GetDirectoryName(create)!);
            File.WriteAllText(create, "function Shout(level) { return level * 2; }\nloud = Shout(1);\n");
            File.WriteAllText(step, "loud = Shout(loud);\n");
            PgslValidationReport report = PgslScriptValidator.ValidateProject(project.RootPath, strict: true);
            Check(!report.Errors.Any(error => error.Contains("Shout", StringComparison.OrdinalIgnoreCase)),
                "The strict check rejects a function another event of the same object defines: "
                + string.Join(" | ", report.Errors.Where(error => error.Contains("Shout", StringComparison.OrdinalIgnoreCase))));
            File.WriteAllText(step, "loud = Whisper(loud);\n");
            report = PgslScriptValidator.ValidateProject(project.RootPath, strict: true);
            Check(report.Errors.Any(error => error.Contains("Whisper", StringComparison.OrdinalIgnoreCase)),
                "The strict check no longer reports a function nothing defines.");
            File.Delete(step);
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Lighting.RoomEnvironmentReflectionReachesTheRenderer", () =>
        {
            var room = Genesis.Runtime.Scene.RoomAsset.Create("Reflections", Genesis.Runtime.Scene.RoomDimension.ThreeD);
            Check(room.Environment.EnvironmentReflection == 0f, "Environment reflection is not off by default.");
            room.Environment.EnvironmentReflection = 1.5f;
            using var scene = new Genesis.Runtime.RuntimeScene("Reflections");
            Genesis.Runtime.Scene.RoomSceneBuilder.ApplySceneSettings(scene, room);
            Check(scene.Environment.EnvironmentReflection == 1.5f, "The room's environment reflection did not reach the scene.");
            var state = Genesis.Runtime.Scene.EnvironmentMapper.ToMesh3DState(scene.Environment, scene.Camera3D,
                new Genesis.Shared.Interfaces.RendererOptions(), false, default);
            Check(state.EnvironmentReflection == 1.5f, "The environment reflection did not reach the renderer's frame state.");
            var json = Newtonsoft.Json.JsonConvert.DeserializeObject<Genesis.Runtime.Scene.RoomEnvironment>("{\"environmentReflection\": 9}");
            Check(json!.EnvironmentReflection == 9f, "The room file's environmentReflection is not read.");
            room.Environment.EnvironmentReflection = 9f;
            Genesis.Runtime.Scene.RoomSceneBuilder.ApplySceneSettings(scene, room);
            Check(scene.Environment.EnvironmentReflection == 4f, "An out-of-range reflection strength was not limited to 4.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Input.ADigitNamesItsKey", () =>
        {
            Genesis.Runtime.Input.InputState input = new();
            Genesis.Runtime.Input.Key five = (Genesis.Runtime.Input.Key)typeof(PgslCommands)
                .GetMethod("ParseKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, ["5"])!;
            Check(five == Genesis.Runtime.Input.Key.D5, $"\"5\" named the key {five}, not D5.");
        });
    }

    private static double Value(ObjectSandboxResult result, string name) => result.Numbers.GetValueOrDefault(name, double.NaN);
    private static bool Near(ObjectSandboxResult result, string name, double expected) => Math.Abs(Value(result, name) - expected) < 1e-6;
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
