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

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.TheCheckerTakesIncrementsInForClausesAsTheGameDoes", () =>
        {
            const string loops =
                "t = 0;\n" +
                "for (var i = 0; i < 4; i++) { t += i; }\n" +
                "for (var j = 10; j > 0; j -= 5) { t += 1; }\n" +
                "for (var k = 2; k > 0; k--) { t += 1; }\n" +
                "for (var n = 0; n < 2; n = n + 1) { t += 1; }\n";
            PgslValidationReport report = PgslScriptValidator.ValidateSource(loops, "Create", strict: true);
            // Before, the checker could not read these, so it left the whole script unchecked ("not
            // checked - ... but the game compiles it"), hiding every other mistake in it.
            Check(report.Errors.Count == 0 && !report.Warnings.Any(w => w.Contains("not checked", StringComparison.Ordinal)),
                "The script check did not read increments in for-clauses that the game runs: " + string.Join(" | ", report.Errors.Concat(report.Warnings)));
            ObjectSandboxResult result = RunCreate(loops);
            Check(result.Ok && Near(result, "t", 12), $"The loops did not run as written: t={Value(result, "t")} (12). " + Errors(result));
            Check(PgslScriptValidator.ValidateSource(loops + "u = Nonexistent(1);\n", "Create", strict: true).Errors.Count > 0,
                "A mistake in a script with for-loops went unreported.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.TestChecksAreWrittenForAnAutomationRun", () =>
        {
            string checks = Path.Combine(Genesis.Runtime.Project.ProjectPaths.LogsDir(project.RootPath), PgslCommands.TestChecksFileName);
            if (File.Exists(checks)) File.Delete(checks);
            ObjectSandboxResult result = RunCreate("a = TestCheck(\"door opens\", 2 > 1, \"open\") ? 1 : 0;\nb = TestCheck(\"coins\", 3 == 4, \"3 of 4 collected\") ? 1 : 0;\n");
            string[] lines = File.Exists(checks) ? File.ReadAllLines(checks) : [];
            Check(result.Ok && lines.Length == 2 && lines[0] == "PASS\tdoor opens\topen" && lines[1] == "FAIL\tcoins\t3 of 4 collected",
                "TestCheck did not write its results for game.run: " + string.Join(" | ", lines) + " " + Errors(result));
            Check(Near(result, "a", 1) && Near(result, "b", 0), $"TestCheck did not return whether it passed: a={Value(result, "a")}, b={Value(result, "b")}.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.AnInstanceMadeInACreateHasRunItsCreateWhenTheCallerSetsIt", () =>
        {
            // Aetherium 80: an instance made inside another's Create had its own Create queued, so it
            // ran later (at the next spawn) and reset what the caller had set on it.
            string objects = Path.Combine(project.AssetsPath, "Objects");
            void Obj(string name, string create)
            {
                File.WriteAllText(Path.Combine(objects, name + ".object.json"),
                    "{ \"schemaVersion\": 3, \"dimension\": \"TwoD\", \"sprite\": \"\", \"model\": \"\", \"components\": [ { \"type\": \"ScriptComponent\", \"enabled\": true, \"props\": { \"ScriptClass\": \"" + name + "\" } } ], \"events\": [\"Create\"] }");
                Directory.CreateDirectory(Path.Combine(objects, name));
                File.WriteAllText(Path.Combine(objects, name, "Create.pgsl"), create);
            }
            Obj("Order Kid", "hp = 10;\n");
            Obj("Order Parent", "kid = CreateInstance(\"Order Kid\", 0, 0);\nInstanceVariableSet(kid, \"hp\", 5);\n");
            ResourceNames.Invalidate(project.RootPath);
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            ScriptAssetRegistry.ClearCache();
            using var scene = new Genesis.Runtime.RuntimeScene("Create order");
            var game = new Genesis.Runtime.Project.ProjectGameContext(project.RootPath, scene, null, null,
                Genesis.Runtime.Scene.RoomAsset.Create("Probe", Genesis.Runtime.Scene.RoomDimension.TwoD), null);
            var host = new ScriptHostSystem();
            host.SetContext(game); // as the Player does: scripts run with this game context
            var oldGame = PgslCommands.ActiveGameContext; string oldPath = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = project.RootPath;
                int parent = PgslCommands.CreateInstance("Order Parent", 0, 0);
                host.FlushDeferredCreates(); // what the next spawn or room load does
                object? kid = PgslCommands.InstanceVariableGet(parent, "kid");
                object? hp = kid is null ? null : PgslCommands.InstanceVariableGet(Convert.ToDouble(kid), "hp");
                Check(parent > 0 && kid is not null && Convert.ToDouble(hp) == 5,
                    $"The made instance's own Create ran after its maker set it: hp = {hp} (5), parent {parent}, kid {kid}.");
            }
            finally { PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; ScriptAssetRegistry.ClearCache(); }
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ChooseAndListCreateShareOneArrayAndStillWork", () =>
        {
            // 17 Oct traces: an array per Choose call. Marked [ParamsNotKept], they reuse one per thread,
            // cleared after each call; the list made from it must still hold its values.
            ObjectSandboxResult result = RunCreate("a = Choose(7, 7, 7);\nl = PgListCreate(4, 5, 6);\nb = PgListSize(l);\nc = PgListGet(l, 2);\nd = Choose(8, 8, 8);\ne = PgListGet(l, 0);\n");
            Check(result.Ok && Near(result, "a", 7) && Near(result, "b", 3) && Near(result, "c", 6) && Near(result, "d", 8) && Near(result, "e", 4),
                $"Choose or PgListCreate broke with a shared array: a={Value(result, "a")} b={Value(result, "b")} c={Value(result, "c")} d={Value(result, "d")} e={Value(result, "e")}. " + Errors(result));
            var paramsArray = typeof(Genesis.Runtime.Scripting.VM.PgslEngineBridge).GetMethod("ParamsArray", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            object? first = paramsArray.Invoke(null, [typeof(object), 3, true]), second = paramsArray.Invoke(null, [typeof(object), 3, true]);
            object? fresh = paramsArray.Invoke(null, [typeof(object), 3, false]);
            Check(ReferenceEquals(first, second) && !ReferenceEquals(first, fresh), "A command marked [ParamsNotKept] was not given the same array again.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.OptionalArgumentsAndEngineNumbersReadAsBefore", () =>
        {
            // A command called without its optional arguments now takes the typed call with the
            // declared defaults, and DeltaTime-style properties are read without boxing: both
            // must give what the general path gave.
            ObjectSandboxResult result = RunCreate("g = DsGridCreate(3, 4);\nDsGridSet(g, 2, 3, 9);\nw = DsGridWidth(g);\nh = DsGridHeight(g);\nv = DsGridGet(g, 2, 3);\n" +
                "k = DsGridCreate(2, 2, \"u8\");\nkw = DsGridWidth(k);\nd = DeltaTime;\nok = d >= 0;\n");
            Check(result.Ok && Near(result, "w", 3) && Near(result, "h", 4) && Near(result, "v", 9) && Near(result, "kw", 2) && Near(result, "ok", 1),
                $"Defaults or engine numbers read differently: w={Value(result, "w")} h={Value(result, "h")} v={Value(result, "v")} kw={Value(result, "kw")} ok={Value(result, "ok")}. " + Errors(result));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.NestedForLoopsEnd", () =>
        {
            // The benchmark scenes' crate grid (two nested for-loops with var counters) ran until the
            // instruction limit stopped it.
            ObjectSandboxResult result = RunCreate("n = 0;\nfor (var gx = 0; gx < 3; gx++) { for (var gz = 0; gz < 4; gz++) { n = n + 1; } }\n" +
                "m = 0;\nfor (var a = 0; a < 5; a++) {\n    for (var b = 0; b < 6; b++) {\n        m = m + 1;\n    }\n}\n");
            Check(result.Ok && Near(result, "n", 12) && Near(result, "m", 30), $"Nested for-loops did not run 3x4 and 5x6 times: n={Value(result, "n")}, m={Value(result, "m")}. " + Errors(result));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ARaisedInstructionLimitCountsInTheSameEvent", () =>
        {
            // The benchmark scenes' spawner raised the limit at the top of its Create and was still
            // stopped at the old 100 000 a little later in the same event.
            try
            {
                ObjectSandboxResult result = RunCreate("ScriptInstructionLimit(3000000);\nn = 0;\nfor (var i = 0; i < 60000; i++) { n = n + 1; }\n");
                Check(result.Ok && Near(result, "n", 60000), $"A limit raised in the event did not let it finish: n={Value(result, "n")}. " + Errors(result));
            }
            finally { Genesis.Runtime.Scripting.VM.PgslVm.CallInstructionLimit = Genesis.Runtime.Scripting.VM.PgslVm.DefaultCallInstructionLimit; }
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

        HeadlessHarness.RunCase(ctx.Report, "Engine.Sky.AScriptSetsTheMood", () =>
        {
            using var scene = new Genesis.Runtime.RuntimeScene("Mood test");
            scene.ConfigureAtmosphere(new Genesis.Runtime.Climate.AtmosphereOptions());
            var game = new Genesis.Runtime.Project.ProjectGameContext(ctx.Workspace, scene, null, null,
                Genesis.Runtime.Scene.RoomAsset.Create("Probe", Genesis.Runtime.Scene.RoomDimension.ThreeD), null);
            var previousGame = PgslCommands.ActiveGameContext;
            float contrast = PgslCommands.Contrast, saturation = PgslCommands.Saturation, vignette = PgslCommands.Vignette;
            PgslCommands.ActiveGameContext = game;
            try
            {
                var options = scene.Atmosphere.Options;
                PgslCommands.SetHaze(0.6f);
                PgslCommands.SkyVisibility = 800f;
                PgslCommands.SkyFogScale = 2f;
                PgslCommands.SkyAmbientScale = 0.5f;
                Check(options.Haze == 0.6f && options.VisibilityMetres == 800f && options.WeatherFogScale == 2f && options.AmbientScale == 0.5f,
                    "Haze, visibility, fog or ambient set from a script did not reach the room's atmosphere.");
                Check(PgslCommands.SetAtmospherePreset("golden hour") && options.Preset == Genesis.Runtime.Climate.AtmospherePreset.GoldenHour
                    && PgslCommands.SkyAtmospherePreset == "GoldenHour" && !PgslCommands.SetAtmospherePreset("Sunset")
                    && !PgslCommands.SetAtmospherePreset("3") && options.Preset == Genesis.Runtime.Climate.AtmospherePreset.GoldenHour,
                    "The atmosphere preset could not be switched by name, or an unknown name changed it.");
                PgslCommands.SetHaze(float.NaN);
                PgslCommands.SkyHaze = 5f;
                Check(options.Haze == 1f, "Haze outside 0 to 1 was kept.");
                PgslCommands.Contrast = 1.2f;
                PgslCommands.Saturation = 0.7f;
                PgslCommands.Vignette = 0.3f;
                Check(Math.Abs(PgslCommands.Contrast - 1.2f) < 1e-5f && Math.Abs(PgslCommands.Saturation - 0.7f) < 1e-5f
                    && Math.Abs(PgslCommands.Vignette - 0.3f) < 1e-5f, "Colour grading set from a script did not read back.");
            }
            finally
            {
                PgslCommands.Contrast = contrast;
                PgslCommands.Saturation = saturation;
                PgslCommands.Vignette = vignette;
                PgslCommands.ActiveGameContext = previousGame;
            }
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
                        "with (lead) { solo = 1; }\nafter = x;\n" +
                        "with (lead) { AnimationBoneSetRotation(\"Arm\", 10, 0, 0); AnimationBoneSetTranslation(\"Arm\", 0, 0, 0.26); }\n",
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
                // A target with no events of its own still shows the bone turns and offsets set on it.
                bool posed = world.Has<Genesis.Runtime.ECS.Components.ModelAnimatorComponent>(first)
                    && world.GetRef<Genesis.Runtime.ECS.Components.ModelAnimatorComponent>(first).Controller is { } arms
                    && arms.BoneRotations.ContainsKey("Arm")
                    && arms.BoneTranslations.TryGetValue("Arm", out System.Numerics.Vector3 slide) && Math.Abs(slide.Z - 0.26f) < 1e-5f;
                Check(posed, "A bone set through with did not reach the target's model before its own events ran.");
                world.Set(second, new Genesis.Runtime.ECS.Components.ModelRendererComponent { CastShadows = true, ReceiveShadows = true });
                Check(PgslCommands.InstanceSetCastShadows(second.Id, false)
                    && !world.GetRef<Genesis.Runtime.ECS.Components.ModelRendererComponent>(second).CastShadows
                    && world.GetRef<Genesis.Runtime.ECS.Components.ModelRendererComponent>(second).ReceiveShadows
                    && !PgslCommands.InstanceSetCastShadows(controller.Id, false),
                    "InstanceSetCastShadows did not turn off exactly one model's shadow.");
            }
            finally
            {
                PgslCommands.BindContext(previousContext);
                PgslCommands.ActiveGameContext = previousGame;
                PgslCommands.ProjectPath = previousPath;
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Models.ANodeTurnsAndMovesPerInstance", () =>
        {
            // A plane with a propeller one metre to its right and a spinner on the propeller.
            var asset = new Genesis.Runtime.Modeling.GModelAsset();
            asset.Nodes.Add(new Genesis.Runtime.Modeling.GModelNode { Name = "Body" });
            asset.Nodes.Add(new Genesis.Runtime.Modeling.GModelNode
            {
                Name = "prop_1", ParentIndex = 0, LocalTransform = System.Numerics.Matrix4x4.CreateTranslation(1, 0, 0),
            });
            asset.Nodes.Add(new Genesis.Runtime.Modeling.GModelNode
            {
                Name = "Spinner", ParentIndex = 1, LocalTransform = System.Numerics.Matrix4x4.CreateTranslation(0, 1, 0),
            });
            var poses = new Dictionary<string, Genesis.Runtime.Modeling.ModelNodePose>(StringComparer.OrdinalIgnoreCase);
            Check(Genesis.Runtime.Modeling.ModelNodePoses.Deltas(asset, poses) == null, "An instance with no poses moved its nodes.");
            poses["PROP_1"] = new Genesis.Runtime.Modeling.ModelNodePose
            {
                Rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0),
            };
            var deltas = Genesis.Runtime.Modeling.ModelNodePoses.Deltas(asset, poses)!;
            System.Numerics.Vector3 Moved(int node, System.Numerics.Vector3 baked) => System.Numerics.Vector3.Transform(baked, deltas[node]);
            bool Near3(System.Numerics.Vector3 a, System.Numerics.Vector3 b) => System.Numerics.Vector3.Distance(a, b) < 1e-4f;
            Check(Near3(Moved(1, new(1, 0, 0)), new(1, 0, 0)) && Near3(Moved(1, new(1, 0, 1)), new(2, 0, 0)),
                $"The propeller did not turn about its own centre: {Moved(1, new(1, 0, 1))} (2, 0, 0).");
            Check(Near3(Moved(2, new(1, 1, 1)), new(2, 1, 0)), $"The node below the propeller did not follow it: {Moved(2, new(1, 1, 1))}.");
            Check(Near3(Moved(0, new(0, 0, 1)), new(0, 0, 1)), "A node that was not posed moved.");
            poses["prop_1"] = new Genesis.Runtime.Modeling.ModelNodePose
            {
                Rotation = System.Numerics.Quaternion.Identity, Translation = new(0, 0, 0.5f),
            };
            deltas = Genesis.Runtime.Modeling.ModelNodePoses.Deltas(asset, poses)!;
            Check(Near3(Moved(1, new(1, 0, 0)), new(1, 0, 0.5f)) && Near3(Moved(2, new(1, 1, 0)), new(1, 1, 0.5f)),
                "Moving the propeller did not move it and the node below it.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Camera.RollStaysAndAShakeAddsToIt", () =>
        {
            var camera = new Genesis.Shared.Rendering.Camera { Position = System.Numerics.Vector3.Zero };
            System.Numerics.Vector3 point = camera.Forward * 5f + camera.Right;
            System.Numerics.Vector4 Clip()
            {
                var p = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(point, 1f), camera.ViewProjection);
                return p / p.W;
            }
            System.Numerics.Vector4 level = Clip();
            Check(level.X > 0.05f && Math.Abs(level.Y) < 1e-4f, $"A point on the camera's right is at {level} unrolled.");
            camera.Roll = 10f * MathF.PI / 180f;
            System.Numerics.Vector4 leaning = Clip();
            Check(leaning.Y < -0.01f, $"Leaning right did not lower a point on the right ({leaning.Y}).");
            camera.Shake(0f, 0.1f, 5f);
            camera.AdvanceShake(0.05f);
            camera.AdvanceShake(1f);
            Check(camera.Roll == 10f * MathF.PI / 180f && Math.Abs(Clip().Y - leaning.Y) < 1e-5f,
                "A shake ending took the camera's own roll away.");
            camera.Roll = float.NaN;
            Check(camera.Roll == 0f, "A roll that is not a number was kept.");
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

        ScriptCallSyntaxChecks.Run(ctx);

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.LibraryFunctionsSharingANameAreAWarning", () =>
        {
            // Two Scripts with a function of the same name (any case) and different parameters: a
            // call by name reaches only one, which the runtime only reports at that call.
            Script("Clash One", "function Spread(cells) { return cells; }\n");
            Script("Clash Two", "// light\n\nfunction spread(cells, level) { return cells + level; }\n");
            ResourceNames.Invalidate(project.RootPath);
            try
            {
                PgslValidationReport report = PgslScriptValidator.ValidateProject(project.RootPath, strict: true);
                string[] spread = report.Warnings.Where(warning => warning.Contains("'Spread'", StringComparison.OrdinalIgnoreCase)).ToArray();
                Check(spread.Length == 1, $"Expected one warning for Spread declared in two Scripts, got {spread.Length}: "
                    + string.Join(" | ", report.Warnings));
                Check(spread.Length == 0 || (spread[0].Contains("Clash One.pgsl line 1 (1 parameter)", StringComparison.Ordinal)
                        && spread[0].Contains("Clash Two.pgsl line 3 (2 parameters)", StringComparison.Ordinal)),
                    "The warning does not name both declarations with their lines and parameters: " + string.Join(" | ", spread));
                Check(!report.Errors.Any(error => error.Contains("Clash", StringComparison.Ordinal)),
                    "A shared function name was made an error: " + string.Join(" | ", report.Errors));
                // HelperThree and HelperFour (above) each declare Helper for their own use.
                Check(report.Warnings.Any(warning => warning.Contains("'Helper'", StringComparison.Ordinal)
                        && warning.Contains("HelperThree.pgsl", StringComparison.Ordinal) && warning.Contains("HelperFour.pgsl", StringComparison.Ordinal)),
                    "Two Scripts' functions named Helper were not reported.");
                Check(!report.Warnings.Any(warning => warning.Contains("'Shout'", StringComparison.OrdinalIgnoreCase)),
                    "A function an Object's event declares was reported as a library clash.");
            }
            finally
            {
                File.Delete(Path.Combine(scripts, "Clash One.pgsl"));
                File.Delete(Path.Combine(scripts, "Clash Two.pgsl"));
                ResourceNames.Invalidate(project.RootPath);
            }
        });

        // A Script that did not compile is "unknown" when called: the error names the Script, its file
        // and line, not only the name (a game saw a bare "Unknown command: McTest").
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.CallingAScriptThatDidNotCompileSaysWhy", () =>
        {
            Script("Unbuilt", "x = 1;\nfrom = 2;\n");
            Script("Unloaded", "GlobalSet(\"unloadedRan\", 1);\n");
            ResourceNames.Invalidate(project.RootPath);
            try
            {
                ObjectSandboxResult result = RunCreate("Unbuilt();\nafter = 1;\n");
                string errors = Errors(result);
                Check(errors.Contains("Unknown command: Unbuilt", StringComparison.Ordinal)
                        && errors.Contains("The Script 'Unbuilt' did not compile", StringComparison.Ordinal)
                        && errors.Contains("Unbuilt.pgsl, line 2", StringComparison.Ordinal),
                    "Calling a Script that did not compile does not say so with its file and line: " + errors);

                // The project's Scripts not loaded at all (an editor dropped them): the error says the
                // Script is there and why it was not found.
                string previous = PgslCommands.ProjectPath;
                ObjectSandboxResult unloaded;
                try
                {
                    PgslCommands.ProjectPath = project.RootPath;
                    ScriptAssetRegistry.ClearCache();
                    unloaded = ObjectSandbox.Run(new Dictionary<string, string> { ["Create"] = "Unloaded();\n" }, frames: 1);
                }
                finally { PgslCommands.ProjectPath = previous; ScriptAssetRegistry.ClearCache(); }
                string notLoaded = Errors(unloaded);
                Check(notLoaded.Contains("The Script 'Unloaded'", StringComparison.Ordinal) && notLoaded.Contains("Unloaded.pgsl", StringComparison.Ordinal)
                        && notLoaded.Contains("were not loaded", StringComparison.Ordinal),
                    "Calling a Script while the project's Scripts are not loaded does not say so: " + notLoaded);

                // Another folder's Scripts loaded than the game's own project.
                string other = Path.Combine(parent, "Other Scripts");
                Directory.CreateDirectory(other);
                ObjectSandboxResult elsewhere;
                try
                {
                    PgslCommands.ProjectPath = project.RootPath;
                    ScriptAssetRegistry.LoadFromProject(other);
                    elsewhere = ObjectSandbox.Run(new Dictionary<string, string> { ["Create"] = "Unloaded();\n" }, frames: 1);
                }
                finally { PgslCommands.ProjectPath = previous; ScriptAssetRegistry.ClearCache(); }
                string otherFolder = Errors(elsewhere);
                Check(otherFolder.Contains("is in the game's project", StringComparison.Ordinal) && otherFolder.Contains("Other Scripts", StringComparison.Ordinal),
                    "Calling a Script while another folder's Scripts are loaded does not say so: " + otherFolder);
            }
            finally
            {
                File.Delete(Path.Combine(scripts, "Unbuilt.pgsl"));
                File.Delete(Path.Combine(scripts, "Unloaded.pgsl"));
                ResourceNames.Invalidate(project.RootPath);
            }
        });

        // Generated data loaded as text, a call for many numbers: commas, semicolons or spaces between
        // them, read the same in every culture, stopping at an entry that is not a number.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ManyNumbersAreAddedFromOneText", () =>
        {
            ObjectSandboxResult result = RunCreate(
                "l = DsListCreate();\n" +
                "a = DsListAddNumbers(l, \"0.5, 1;-2.25\\n3e2  4\");\n" +
                "n = DsListSize(l); s = DsListGet(l, 0) + DsListGet(l, 2) + DsListGet(l, 3);\n" +
                "b = DsListAddNumbers(l, \"7, x, 8\"); m = DsListSize(l); e = DsListAddNumbers(l, \"\");\n" +
                "q = NumListCreate(); c = NumListAddNumbers(q, \" 1,2 ,3, \"); t = NumListGet(q, 2) + NumListSize(q);\n");
            Check(result.Ok, "Adding numbers from a text broke the script: " + Errors(result));
            Check(Near(result, "a", 5) && Near(result, "n", 5) && Near(result, "s", 298.25) && Near(result, "b", 1) && Near(result, "m", 6)
                    && Near(result, "e", 0) && Near(result, "c", 3) && Near(result, "t", 6),
                $"Wrong: a={Value(result, "a")} (5) n={Value(result, "n")} (5) s={Value(result, "s")} (298.25) b={Value(result, "b")} (1) m={Value(result, "m")} (6) "
                + $"e={Value(result, "e")} (0) c={Value(result, "c")} (3) t={Value(result, "t")} (6).");
        });

        // A Script asset's text is prepared once and compiled as it is (VMEngine.CompilePrepared), not
        // prepared a second time: that is only the same if preparing it again changes nothing, for
        // the templates' Scripts and for each shorthand form.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.PreparingAScriptTwiceChangesNothing", () =>
        {
            var sources = new List<(string Name, string Text)>();
            string templates = Path.Combine(AppContext.BaseDirectory, "Projects", "Templates", "Assets");
            if (Directory.Exists(templates))
                foreach (string file in Directory.EnumerateFiles(templates, "*.pgsl", SearchOption.AllDirectories))
                    sources.Add((Path.GetRelativePath(templates, file), File.ReadAllText(file)));
            string[] shorthand =
            [
                "if a: HelperThree\nVoidCaller;\nVoidCaller ;\nx.VoidCaller;\n",
                "if b = 1:\n  x = 1\nelse if c:\n  y = 2\nelse:\n  z = 3\nwhile k < 3:\n  k++\n",
                "if x == 1 {\n}\nif y { z = 1; }\nwhile w {\n}\nif q\n",
                "from Game.Ui:\n  Show();\n  from Inner:\n    Deep();\nHide();\n",
                "s = \"if a: HelperThree\"; t = 'VoidCaller;'; // if c: VoidCaller\n/* while e: */ VoidCaller;\n",
                "q = a ? b : c;\nif a ? b : c: HelperThree\nif a::\n",
                "scr_ArgDoubler(); scr_VoidCaller(1); scr_HelperFour;\n",
                "if (a = b) { }\nwhile (c=d) { }\nif (a <= b && c != d) { }\nif (f(x) = 1) { }\n",
            ];
            foreach (string text in shorthand) sources.Add(("(shorthand)", text));
            string previous = PgslCommands.ProjectPath;
            int changed = 0;
            string? first = null;
            try
            {
                PgslCommands.ProjectPath = project.RootPath;
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);
                foreach ((string name, string text) in sources)
                {
                    string once = PgslCommands.PreProcessScript(text, isForVm: true, project.RootPath);
                    string twice = PgslCommands.PreProcessScript(once, isForVm: true, project.RootPath);
                    if (once == twice) continue;
                    changed++;
                    first ??= $"{name}: '{once.Replace("\n", "\\n")}' became '{twice.Replace("\n", "\\n")}'";
                }
            }
            finally { PgslCommands.ProjectPath = previous; ScriptAssetRegistry.ClearCache(); }
            Check(sources.Count > 100 && changed == 0,
                $"Preparing a Script's text a second time changed {changed} of {sources.Count}: {first}");
        });

        // Generated data written as code, as GenesisCraft's block models (17 MB of DsListAdd calls in
        // functions): four megabytes load in well under a second and every value arrives.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ALargeGeneratedDataScriptLoadsQuickly", () =>
        {
            var data = new System.Text.StringBuilder("// Generated: 20 numbers a line, 100 lines a function.\n");
            int functions = 0, count = 0;
            double expected = 0;
            while (data.Length < 4 << 20)
            {
                data.Append("function ZqFill").Append(functions++).Append("() {\n");
                for (int line = 0; line < 100; line++)
                {
                    data.Append("    ");
                    for (int n = 0; n < 20; n++, count++)
                    {
                        double value = count * 7 % 64 / 16.0;
                        expected += value;
                        data.Append("DsListAdd(zqList,").Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(");");
                    }
                    data.Append(line % 25 == 0 ? " DsListAddString(zqNames, \"oak_log\");\n" : "\n");
                }
                data.Append("}\n");
            }
            Script("ZqData", data.ToString());
            ResourceNames.Invalidate(project.RootPath);
            var create = new System.Text.StringBuilder("zqList = DsListCreate(); zqNames = DsListCreate();\n");
            for (int f = 0; f < functions; f++) create.Append("ZqFill").Append(f).Append("();\n");
            // Values at a few places (summing them all would pass an event's instruction limit).
            int[] at = [0, 1, 777, count / 2 + 3, count - 1];
            create.Append("n = DsListSize(zqList); names = DsListSize(zqNames); total = 0;\n");
            expected = 0;
            foreach (int index in at)
            {
                expected += index * 7 % 64 / 16.0;
                create.Append("total += DsListGet(zqList, ").Append(index).Append(");\n");
            }
            string previous = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ProjectPath = project.RootPath;
                ScriptAssetRegistry.ClearCache();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);
                double loadMs = clock.Elapsed.TotalMilliseconds;
                ObjectSandboxResult result = ObjectSandbox.Run(new Dictionary<string, string> { ["Create"] = create.ToString() }, frames: 1);
                clock.Restart();
                List<Genesis.Runtime.Scripting.VM.Token> tokens = new Genesis.Runtime.Scripting.VM.PgslLexer(data.ToString()).Tokenize();
                double lexMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                new Genesis.Runtime.Scripting.VM.PgslParser(tokens, Genesis.Runtime.Scripting.VM.VMEngine.Bridge?.NativeIdMap).Parse();
                double parseMs = clock.Elapsed.TotalMilliseconds;
                string line = $"A {data.Length / 1048576.0:F1} MB generated data Script ({count} numbers in {functions} functions) loaded in {loadMs:F0} ms"
                    + $" (read into {tokens.Count} tokens alone {lexMs:F0} ms, parsed and compiled alone {parseMs:F0} ms).";
                Console.WriteLine(line);
                Check(result.Ok && Near(result, "n", count) && Near(result, "names", functions * 4) && Near(result, "total", expected),
                    $"The data did not all arrive: n={Value(result, "n")} ({count}), names={Value(result, "names")} ({functions * 4}), total={Value(result, "total")} ({expected}); {Errors(result)}");
                Check(loadMs < 1500, line);

                // A large Script keeps its source's length and hash, not its text: reading the Scripts
                // again leaves it as it was while unchanged and compiles it again once it changes.
                ScriptAssetRegistry.TryGet("ZqData", out CompiledScriptAsset loaded);
                ScriptAssetRegistry.Refresh(project.RootPath, out string? unchangedFailure);
                ScriptAssetRegistry.TryGet("ZqData", out CompiledScriptAsset same);
                File.AppendAllText(Path.Combine(scripts, "ZqData.pgsl"), "// changed\n");
                ScriptAssetRegistry.Refresh(project.RootPath, out string? changedFailure);
                ScriptAssetRegistry.TryGet("ZqData", out CompiledScriptAsset changed);
                Check(loaded != null && ReferenceEquals(loaded, same) && changed != null && !ReferenceEquals(loaded, changed)
                        && unchangedFailure == null && changedFailure == null && loaded.ProcessedSource == null,
                    $"A large Script was compiled again unchanged, or not once changed (same {ReferenceEquals(loaded, same)}, changed {!ReferenceEquals(loaded, changed)}, "
                    + $"text kept {loaded?.ProcessedSource != null}; {unchangedFailure}{changedFailure}).");
            }
            finally
            {
                PgslCommands.ProjectPath = previous; ScriptAssetRegistry.ClearCache();
                File.Delete(Path.Combine(scripts, "ZqData.pgsl"));
                ResourceNames.Invalidate(project.RootPath);
            }
        });

        // A large Script compiled once is read back compiled on the next load (ScriptCompileCache):
        // exactly what compiling it gives, run the same; a changed source or a damaged entry is
        // compiled again.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ALargeScriptIsReadBackCompiledOnTheNextLoad", () =>
        {
            var text = new System.Text.StringBuilder();
            text.Append("// A large Script for the compile cache.\nzcList = 0;\n");
            text.Append("function ZcAdd(a, b) { return a + b; }\nfunction ZcText(s) { return s + \"!\"; }\n");
            text.Append("function ZcBig(n) {\n    var t = 0;\n    for (var i = 0; i < n; i++) { t += i; }\n");
            text.Append("    if (t > 10 && true) { t = t - 1; } else { t = t + 1; }\n    while (t > 100) { t = t / 2; }\n    return t;\n}\n");
            for (int f = 0; text.Length < 3 * ScriptCompileCache.SmallestCached; f++)
            {
                text.Append("function ZcFill").Append(f).Append("() {\n");
                for (int line = 0; line < 40; line++)
                    text.Append("    DsListAdd(zcList, ").Append((line % 7 / 4.0).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("); DsListAddString(zcList, \"word").Append(line % 5)
                        .Append("\"); q = ZcAdd(").Append(line).Append(", 2) > 3 || false; r = -q;\n");
                text.Append("}\n");
            }
            Script("ZcLarge", text.ToString());
            ResourceNames.Invalidate(project.RootPath);
            string previous = PgslCommands.ProjectPath;
            string Dump(Genesis.Runtime.Scripting.VM.CompileResult result)
            {
                var dump = new System.Text.StringBuilder();
                void Program(List<Genesis.Runtime.Scripting.VM.Instruction> code, List<object> constants)
                {
                    foreach (Genesis.Runtime.Scripting.VM.Instruction instruction in code)
                        dump.Append(instruction.Opcode).Append('@').Append(instruction.LineNumber).Append(' ').Append(instruction.Operand switch
                        {
                            null => "-", int i => "i" + i, string s => "s" + s, ValueTuple<int, int> n => $"n{n.Item1},{n.Item2}",
                            ValueTuple<string, int> c => $"c{c.Item1},{c.Item2}", object other => "?" + other.GetType().Name,
                        }).Append('\n');
                    foreach (object constant in constants)
                        dump.Append(constant switch { double d => "d" + d.ToString("R", System.Globalization.CultureInfo.InvariantCulture), int i => "i" + i, string s => "s" + s, _ => "?" }).Append('\n');
                }
                Program(result.Instructions, result.Constants);
                foreach (KeyValuePair<string, Genesis.Runtime.Scripting.VM.UserFunction> pair in result.UserFunctions)
                {
                    dump.Append("function ").Append(pair.Key).Append(' ').Append(pair.Value.Name).Append('(').Append(string.Join(",", pair.Value.Parameters)).Append(")\n");
                    Program(pair.Value.Bytecode, pair.Value.Constants);
                }
                return dump.ToString();
            }
            try
            {
                PgslCommands.ProjectPath = project.RootPath;
                int hits = ScriptCompileCache.Hits, writes = ScriptCompileCache.Writes;
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);
                ScriptCompileCache.LastBackgroundWrite?.Join(30000);
                ScriptAssetRegistry.TryGet("ZcLarge", out CompiledScriptAsset fresh);
                string compiled = Dump(fresh.CompileResult);
                Check(ScriptCompileCache.Writes > writes, "A large Script compiled was not written to the cache.");

                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);
                ScriptAssetRegistry.TryGet("ZcLarge", out CompiledScriptAsset cached);
                string readBack = Dump(cached.CompileResult);
                int firstDifference = Enumerable.Range(0, Math.Min(compiled.Length, readBack.Length)).FirstOrDefault(i => compiled[i] != readBack[i]);
                Check(ScriptCompileCache.Hits > hits && !ReferenceEquals(fresh, cached) && compiled == readBack && cached.SourceHash == fresh.SourceHash,
                    $"A large Script read back from the cache differs from its compile (hits {ScriptCompileCache.Hits - hits}; {compiled.Length} / {readBack.Length} characters, "
                    + $"first difference at {firstDifference}: '{compiled.Substring(Math.Max(0, firstDifference - 40), Math.Min(80, compiled.Length - Math.Max(0, firstDifference - 40)))}').");

                ObjectSandboxResult result = ObjectSandbox.Run(new Dictionary<string, string>
                {
                    ["Create"] = "zcList = DsListCreate(); a = ZcAdd(2, 3); b = ZcText(\"a\"); c = ZcBig(10); ZcFill0(); n = DsListSize(zcList);\n",
                }, frames: 1);
                Check(result.Ok && Near(result, "a", 5) && Near(result, "c", 44) && Near(result, "n", 80) && result.Strings.GetValueOrDefault("b") == "a!",
                    $"A Script read back from the cache runs differently: a={Value(result, "a")} (5) c={Value(result, "c")} (44) n={Value(result, "n")} (80) b={result.Strings.GetValueOrDefault("b")}; {Errors(result)}");

                // A damaged entry, then a changed source: each compiled again.
                string folder = ScriptCompileCache.Folder();
                foreach (string entry in Directory.GetFiles(folder, "*.pgslc")) File.WriteAllBytes(entry, [1, 2, 3, 4, 5]);
                int misses = ScriptCompileCache.Misses;
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);
                ScriptAssetRegistry.TryGet("ZcLarge", out CompiledScriptAsset recompiled);
                ScriptCompileCache.LastBackgroundWrite?.Join(30000);
                Check(ScriptCompileCache.Misses > misses && recompiled != null && Dump(recompiled.CompileResult) == compiled,
                    "A damaged cache entry was not compiled again.");
                File.AppendAllText(Path.Combine(scripts, "ZcLarge.pgsl"), "function ZcLater() { return 7; }\n");
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);
                Check(ScriptAssetRegistry.TryFindFunction("ZcLater", out _), "A changed large Script was read from the cache unchanged.");
                ScriptCompileCache.LastBackgroundWrite?.Join(30000);
            }
            finally
            {
                PgslCommands.ProjectPath = previous; ScriptAssetRegistry.ClearCache();
                File.Delete(Path.Combine(scripts, "ZcLarge.pgsl"));
                ResourceNames.Invalidate(project.RootPath);
            }
        });

        // The project's Scripts are compiled on several threads at once, yet each still sees the
        // names of the Scripts before it that compiled, as when they were compiled one by one: a
        // call without brackets to an earlier Script is made a call, one to a later Script or to one
        // that did not compile is left as written.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ScriptsCompiledTogetherSeeTheNamesBeforeThem", () =>
        {
            string[] added = ["Zp1First", "Zp2Broken", "Zp3Caller", "Zp4Later", "Zp5Caller", "Zp6Last"];
            Script("Zp1First", "function Zp1Fn() { return 1; }\nreturn 1;\n");
            Script("Zp2Broken", "x = (;\n");
            Script("Zp3Caller", "Zp1First;\nZp2Broken;\nZp4Later;\nreturn 0;\n");
            Script("Zp4Later", "return 4;\n");
            Script("Zp5Caller", "Zp1First;\nZp4Later;\nZp6Last;\nreturn 0;\n");
            Script("Zp6Last", "return 6;\n");
            ResourceNames.Invalidate(project.RootPath);
            string previous = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ProjectPath = project.RootPath;
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);
                string Processed(string name) => ScriptAssetRegistry.TryGet(name, out CompiledScriptAsset asset) ? asset.ProcessedSource : "(not loaded)";
                string third = Processed("Zp3Caller"), fifth = Processed("Zp5Caller");
                Check(third.Contains("Zp1First();", StringComparison.Ordinal) && third.Contains("Zp2Broken;", StringComparison.Ordinal)
                        && third.Contains("Zp4Later;", StringComparison.Ordinal),
                    "A Script compiled with the others did not see just the Scripts before it that compiled: " + third.Replace("\n", " | "));
                Check(fifth.Contains("Zp1First();", StringComparison.Ordinal) && fifth.Contains("Zp4Later();", StringComparison.Ordinal)
                        && fifth.Contains("Zp6Last;", StringComparison.Ordinal),
                    "A Script compiled with the others did not see just the Scripts before it: " + fifth.Replace("\n", " | "));
                Check(ScriptAssetRegistry.LoadErrors.ContainsKey("Zp2Broken") && !ScriptAssetRegistry.TryGet("Zp2Broken", out _),
                    "A Script that did not compile was loaded or not reported.");
                Check(ScriptAssetRegistry.TryGet("Zp6Last", out _) && ScriptAssetRegistry.TryFindFunction("Zp1Fn", out _),
                    "The Scripts compiled together were not all registered.");
            }
            finally
            {
                PgslCommands.ProjectPath = previous; ScriptAssetRegistry.ClearCache();
                foreach (string name in added) File.Delete(Path.Combine(scripts, name + ".pgsl"));
                ResourceNames.Invalidate(project.RootPath);
            }
        });

        // A function named like a Script with code of its own takes every call by that name: the
        // project check warns, and a call that does not fit the function says the names clash.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.AFunctionNamedLikeAScriptIsReported", () =>
        {
            Script("Gadget", "function Gadget(level) { return level * 2; }\nGlobalSet(\"gadgetRan\", 1);\n");
            Script("Widget", "function Widget(level) { return level; }\n");
            ResourceNames.Invalidate(project.RootPath);
            try
            {
                PgslValidationReport report = PgslScriptValidator.ValidateProject(project.RootPath, strict: true);
                string[] gadget = report.Warnings.Where(warning => warning.Contains("function 'Gadget' has the name of the Script 'Gadget'", StringComparison.Ordinal)).ToArray();
                Check(gadget.Length == 1 && gadget[0].Contains("Gadget.pgsl (line 1", StringComparison.Ordinal),
                    $"Expected one warning for the function Gadget in the Script Gadget, got {gadget.Length}: " + string.Join(" | ", report.Warnings));
                Check(!report.Warnings.Any(warning => warning.Contains("'Widget' has the name", StringComparison.Ordinal)),
                    "A Script of functions only (calling it runs nothing) was reported for a function of its name.");
                Check(!report.Errors.Any(error => error.Contains("Gadget", StringComparison.Ordinal)),
                    "A function named like a Script was made an error: " + string.Join(" | ", report.Errors));

                ObjectSandboxResult result = RunCreate("v = Gadget();\nafter = 1;\n");
                string errors = Errors(result);
                Check(errors.Contains("expects 1 arguments, got 0", StringComparison.Ordinal)
                        && errors.Contains("'Gadget' is both a Script", StringComparison.Ordinal) && errors.Contains("Gadget.pgsl", StringComparison.Ordinal),
                    "A call meant for a Script that reached a function of its name does not say the names clash: " + errors);
                ObjectSandboxResult fits = RunCreate("v = Gadget(4);\n");
                Check(fits.Ok && Near(fits, "v", 8), "A call that fits the function no longer runs it: " + Errors(fits));
            }
            finally
            {
                File.Delete(Path.Combine(scripts, "Gadget.pgsl"));
                File.Delete(Path.Combine(scripts, "Widget.pgsl"));
                ResourceNames.Invalidate(project.RootPath);
            }
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

        HeadlessHarness.RunCase(ctx.Report, "Engine.Shaders.EachInstanceCarriesItsOwnShaderValues", () =>
        {
            using var scene = new Genesis.Runtime.RuntimeScene("Shader values");
            var game = new Genesis.Runtime.Project.ProjectGameContext(ctx.Workspace, scene, null, null,
                Genesis.Runtime.Scene.RoomAsset.Create("Probe", Genesis.Runtime.Scene.RoomDimension.ThreeD), null);
            var previousGame = PgslCommands.ActiveGameContext;
            PgslCommands.ActiveGameContext = game;
            try
            {
                var first = scene.World.CreateEntity();
                var second = scene.World.CreateEntity();
                PgslCommands.InstanceSetShader(first.Id, "Camo");
                PgslCommands.InstanceSetShaderParameter(first.Id, "Pattern", 3);
                PgslCommands.InstanceSetShaderVector(second.Id, "Tint", 0.1, 0.2, 0.3, 1);
                Check(Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.TryGet(first, out var one)
                    && one.Shader == "Camo" && one.ShaderParameters["Pattern"][0] == 3f
                    && PgslCommands.InstanceGetShaderParameter(first.Id, "Pattern") == 3,
                    "An instance's shader and parameter did not reach its own draw values.");
                Check(Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.TryGet(second, out var two)
                    && !two.ShaderParameters.ContainsKey("Pattern") && two.ShaderParameters["Tint"][2] == 0.3f,
                    "Shader values leaked between instances.");
                var document = new Genesis.Shared.Assets.ShaderAssetDocument
                {
                    Source = "cbuffer GenesisParameters : register(b5) { float Pattern; float3 Tint; };",
                };
                Genesis.Shared.Assets.ShaderParameterReflection.Synchronize(document);
                Genesis.Shared.Assets.ShaderParameterReflection.Pack(document, one.ShaderParameters,
                    out System.Numerics.Vector4 row0, out _, out _, out _);
                Check(row0.X == 3f, $"The instance's value is not what its shader receives: {row0}.");
            }
            finally { PgslCommands.ActiveGameContext = previousGame; }
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
