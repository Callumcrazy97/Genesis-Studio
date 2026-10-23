using System.Globalization;
using System.Numerics;
using System.Text;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Physics;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The one standard way to reach the engine (PgslApi): every command as Engine.Area.Name, every
/// value as a property read and set like a variable, and a body's values on the current instance,
/// in with and for, in events and in compiled functions.
/// </summary>
internal static class PgslEngineApiChecks
{
    /// <summary>Run by pgsl-logic's sandbox (one instance, no room): t_ names must be 1.</summary>
    public const string Script = """
        // Each command by its standard name is the command itself.
        var l = Engine.List.Create();
        Engine.List.Add(l, 5);
        Engine.List.Add(l, 7);
        t_listCommands = Engine.List.Size(l) == 2 && DsListSize(l) == 2 && Engine.List.Get(l, 1) == 7;
        t_math = Engine.Math.Clamp(12, 0, 10) == 10 && Engine.Math.Max(2, 9) == 9;
        t_text = Engine.Text.Upper("ab") == "AB" && Engine.Text.Length("abc") == 3;
        var m = Engine.Map.Create();
        Engine.Map.Set(m, "k", 4);
        t_map = Engine.Map.Get(m, "k") == 4;
        // A value is a property: read, set and += reach the same value as its commands.
        Engine.Image.Frame = 3;
        t_frame = Engine.Image.Frame == 3 && SpriteGetFrame() == 3 && image_index == 3;
        Engine.Image.Frame += 2;
        t_frameAdd = image_index == 5 && Engine.Image.GetFrame() == 5;
        Engine.Image.Speed = 0.5;
        t_speed = Engine.Image.Speed == 0.5 && SpriteGetSpeed() == 0.5;
        Engine.Image.Alpha = 0.25;
        t_alpha = image_alpha == 0.25 && Engine.Image.Alpha == 0.25;
        Engine.Image.ScaleX = 2;
        t_scale = image_xscale == 2 && Engine.Image.ScaleX == 2;
        Engine.Instance.X = 40;
        Engine.Instance.Y += 6;
        t_instance = x == 40 && Engine.Instance.X == 40 && Engine.Instance.Y == y;
        // Inside a namespace block the standard names are short.
        from Engine.Math: { clamped = Clamp(-3, 0, 1); }
        t_from = clamped == 0;
        // Bare words stay the script's own variables.
        Mass = 5;
        Friction = 2;
        t_bareWords = Mass == 5 && Friction == 2;
        // No body and no physics world: a body's values read 0 and setting one does nothing.
        Engine.Physics.Mass = 9;
        t_noBody = !Engine.Physics.HasBody && Engine.Physics.Mass == 0 && Engine.Physics.BodyType == "";
        // A function setting and reading one, called often enough to run compiled.
        function ApiStep(k) { Engine.Image.Frame = k; return Engine.Image.Frame + Engine.Math.Max(k, 1); }
        var total = 0;
        for (var k = 0; k < 60; k++) { total = ApiStep(k); }
        t_compiled = total == 118 && image_index == 59;
        """;

    private const string Controller = """
        with (ApiCrate) { Engine.Physics.GravityScale = 0; Engine.Physics.Mass = 4; Engine.Physics.Bounciness = 0.75; Engine.Physics.Friction = 0.3; }
        var n = InstanceNumber("ApiCrate");
        for (var i = 0; i < n; i++) { with (InstanceFind("ApiCrate", i)) { Engine.Physics.VelocityX = (i + 1) * 2; } }
        count = n;
        mine = Engine.Physics.HasBody;
        Engine.Physics.AirDrag = 0.05;
        drag = PhysicsGetAirDrag();
        Engine.Physics.GravityY = -19.62;
        strength = Engine.Physics.GravityStrength;
        var lead = InstanceFind("ApiCrate", 0);
        var leadSpeed = 0;
        var leadMass = 0;
        var leadType = "";
        with (lead) { Engine.Physics.ApplyImpulse(4, 0, 0); leadSpeed = Engine.Physics.VelocityX; leadMass = Engine.Physics.Mass; leadType = Engine.Physics.BodyType; }
        speedA = leadSpeed;
        massA = leadMass;
        typeA = leadType;
        with (InstanceFind("ApiCrate", 2)) { Engine.Physics.BodyType = "static"; }
        function ApiDamp(k) { Engine.Physics.LinearDamping = k * 0.01; return Engine.Physics.LinearDamping; }
        var d = 0;
        with (lead) { for (var j = 0; j < 60; j++) { d = ApiDamp(j); } }
        damping = d;
        """;

    private const string Ball = """
        used = Engine.Physics.Use("ApiBouncy");
        shape = Engine.Physics.Shape;
        bounce = Engine.Physics.Bounciness;
        had = Engine.Physics.HasBody;
        removed = Engine.Physics.Remove();
        after = Engine.Physics.HasBody;
        """;

    public static void Run(HeadlessContext ctx, ProjectSession project, Action<string, string, string, string> row)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiNamesEveryCommand", () => Names(ctx, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiPhysicsWithAndFor", () => Physics(project, row));
    }

    private static void Names(HeadlessContext ctx, Action<string, string, string, string> row)
    {
        var bridge = new PgslEngineBridge();
        bridge.LoadFromRegistry();
        IReadOnlyDictionary<string, int> ids = bridge.NativeIdMap;
        IReadOnlyList<PgslApi.Entry> entries = PgslApi.Entries;
        string skipped = string.Join("\n", PgslApi.Skipped);
        // A command is named by its standard name, or as the getter or setter of a property (ParticleBurstLimit is Engine.Particle.BurstLimit).
        var named = new HashSet<string>(entries.SelectMany(e => new[] { e.Command, e.GetterCommand, e.SetterCommand }).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        List<string> missing = PgslCommandRegistry.GetCatalog()
            .Where(info => string.IsNullOrWhiteSpace(info.Namespace) && info.IsImplemented && !info.Name.Contains('.')
                && !named.Contains(info.Name) && !skipped.Contains(": " + info.Name + " (", StringComparison.Ordinal))
            .Select(info => info.Name).ToList();
        List<string> unshared = entries
            .Where(e => !ids.TryGetValue(e.Name, out int id) || (e.Command != null && (!ids.TryGetValue(e.Command, out int target) || target != id)))
            .Select(e => e.Name).ToList();
        List<string> badNames = entries.Where(e => !e.Name.Split('.').All(part => part.Length > 0 && char.IsLetter(part[0]) && part.All(char.IsLetterOrDigit)))
            .Select(e => e.Name).ToList();
        int properties = entries.Count(e => e.IsProperty);
        row("Engine API", "every command has an Engine.Area.Name", missing.Count == 0 ? "PASS" : "FAIL", missing.Count == 0 ? $"{entries.Count} names, {properties} properties" : string.Join(", ", missing.Take(20)));
        row("Engine API", "a standard name is the same call as its command", unshared.Count == 0 ? "PASS" : "FAIL", string.Join(", ", unshared.Take(20)));
        row("Engine API", "names are Engine.Area.Name", badNames.Count == 0 ? "PASS" : "FAIL", string.Join(", ", badNames.Take(20)));
        File.WriteAllText(Path.Combine(ctx.Captures, "EngineApi.md"), Reference(), new UTF8Encoding(false));
        HeadlessHarness.Assert(missing.Count == 0, "Commands with no standard name: " + string.Join(", ", missing));
        HeadlessHarness.Assert(unshared.Count == 0, "Standard names that are not the same native call as their command: " + string.Join(", ", unshared));
        HeadlessHarness.Assert(badNames.Count == 0, "Standard names not of the form Engine.Area.Name: " + string.Join(", ", badNames));
    }

    /// <summary>The reference page: every Engine.Area.Name by area (Documentation/EngineApi.md).</summary>
    private static string Reference()
    {
        var text = new StringBuilder();
        var rows = PgslApi.Reference();
        text.AppendLine("# The engine API: Engine.Area.Name");
        text.AppendLine();
        text.AppendLine("Every engine command and value by its standard name, made by `PgslApi` and checked by");
        text.AppendLine("`--test pgsl-logic` (which writes this page beside its captures). A **property** is read like a");
        text.AppendLine("variable and, when it can be set, set like one (`Engine.Physics.Mass = 4;`, `Engine.Image.Frame += 1;`);");
        text.AppendLine("an instance's property is the current instance's, each target's in turn inside `with`. The flat");
        text.AppendLine("names (`PhysicsApplyForce`) still work. See PgslLanguage.md, \"The engine API\".");
        text.AppendLine();
        text.AppendLine($"{rows.Count} names: {rows.Count(r => r.IsProperty)} properties ({rows.Count(r => r.CanSet)} can be set), {rows.Count(r => !r.IsProperty)} commands.");
        foreach (var area in rows.GroupBy(r => string.Join('.', r.Name.Split('.').Take(r.Name.Split('.').Length - 1))))
        {
            text.AppendLine();
            text.AppendLine($"## {area.Key}");
            text.AppendLine();
            text.AppendLine("| Name | Kind | What it is |");
            text.AppendLine("|---|---|---|");
            foreach (var r in area)
            {
                string kind = r.IsProperty ? (r.CanSet ? "property" : "read only") : "command";
                string what = (r.Description ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
                text.AppendLine($"| `{r.Name[(area.Key.Length + 1)..]}` | {kind} | {what} |");
            }
        }
        return text.ToString();
    }

    private static void Physics(ProjectSession project, Action<string, string, string, string> row)
    {
        // A 3D Physics resource for Engine.Physics.Use: a bouncy sphere.
        string path = new ResourceService(project).CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Physics), ResourceKind.Physics, "ApiBouncy");
        new PhysicsSceneConfig { Dimension = PhysicsDimension.ThreeD, BodyType = PhysicsBodyKind.Dynamic, Shape = PhysicsBodyShape.Sphere, Restitution = 0.9, Friction = 0.2, Density = 2 }
            .SaveToFile(path);
        ResourceCatalog.Invalidate(project.RootPath);

        using var scene = new RuntimeScene("Engine API physics");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, RoomAsset.Create("Api", RoomDimension.ThreeD), null);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        string previousPath = PgslCommands.ProjectPath;
        PgslCommands.ActiveGameContext = game;
        PgslCommands.ProjectPath = project.RootPath;
        try
        {
            scene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset());
            scene.Physics.EnableThreadDispatcher = false;
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            var world = scene.World;
            Entity Make(float x, bool body)
            {
                Entity entity = world.CreateEntity();
                world.Set(entity, new TransformComponent { X = x, Y = 5, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                world.Set(entity, new Transform3DComponent { Position = new Vector3(x, 5, 0), Rotation = Quaternion.Identity, Scale = Vector3.One });
                if (body) world.Set(entity, RigidBodyComponent.DynamicBox(new Vector3(0.5f), 1f));
                return entity;
            }
            Entity[] crates = { Make(0, true), Make(10, true), Make(20, true) };
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = "" }))
                foreach (Entity crate in crates) host.Attach(world, crate, "ApiCrate");
            Entity controller = Make(-10, false);
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = Controller })) host.Attach(world, controller, "ApiController");
            Entity ball = Make(-20, false);
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = Ball })) host.Attach(world, ball, "ApiBall");
            string diagnostics = string.Join("; ", host.RecentDiagnostics.Select(d => d.Message));

            for (int i = 0; i < 60; i++) scene.UpdateFixed(1f / 60);

            var scripts = host.Instances.OfType<PgslBehavior>().ToList();
            IReadOnlyDictionary<string, object> Vars(Entity entity) =>
                scripts.FirstOrDefault(s => s.Context?.InstanceId == entity.Id)?.GetVariablesSnapshot() ?? new Dictionary<string, object>();
            double Number(IReadOnlyDictionary<string, object> vars, string name) =>
                vars.TryGetValue(name, out object? value) && value is not string ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            string Text(IReadOnlyDictionary<string, object> vars, string name) => vars.TryGetValue(name, out object? value) ? value?.ToString() ?? "" : "";
            var boss = Vars(controller);
            var thrown = Vars(ball);
            Vector3 Position(Entity entity) => world.GetRef<Transform3DComponent>(entity).Position;
            RigidBodyComponent Body(Entity entity) => world.GetRef<RigidBodyComponent>(entity);

            void Check(string name, bool pass, string detail)
            {
                row("Engine API physics", name, pass ? "PASS" : "FAIL", detail);
                HeadlessHarness.Assert(pass, $"{name}: {detail} {diagnostics}");
            }
            Check("with sets every target's body", crates.All(c => Body(c).Mass == 4 && Math.Abs(Body(c).Restitution - 0.75f) < 1e-6 && Math.Abs(Body(c).Friction - 0.3f) < 1e-6 && Body(c).GravityScale == 0),
                string.Join(" / ", crates.Select(c => $"mass {Body(c).Mass} bounce {Body(c).Restitution} friction {Body(c).Friction} gravity {Body(c).GravityScale}")));
            // The lead moves at 3 a second slowed by its damping (0.59) and the air (0.05): 3 (1 - e^-0.64) / 0.64; the
            // second at 4 slowed by the air alone.
            double leadX = 3 * (1 - Math.Exp(-0.64)) / 0.64, secondX = 10 + 4 * (1 - Math.Exp(-0.05)) / 0.05;
            Check("for + with gives each its own velocity, gravity 0 floats", Math.Abs(Position(crates[0]).X - leadX) < 0.1 && Math.Abs(Position(crates[1]).X - secondX) < 0.15
                && Math.Abs(Position(crates[0]).Y - 5) < 0.05 && Math.Abs(Position(crates[1]).Y - 5) < 0.05,
                $"crate 1 at {Position(crates[0])} ({leadX:0.00}, 5), crate 2 at {Position(crates[1])} ({secondX:0.00}, 5)");
            Check("a body made static stays", Vector3.Distance(Position(crates[2]), new Vector3(20, 5, 0)) < 0.01 && Body(crates[2]).Motion == Genesis.Shared.ECS.Components.PhysicsMotionType.Static && Body(crates[2]).Mass == 4,
                $"at {Position(crates[2])}, {Body(crates[2]).Motion}");
            Check("world values and self commands", Number(boss, "count") == 3 && Number(boss, "mine") == 0 && Math.Abs(Number(boss, "drag") - 0.05) < 1e-6
                && Math.Abs(Number(boss, "strength") - 2) < 1e-4 && Math.Abs(Number(boss, "speedA") - 3) < 1e-3 && Number(boss, "massA") == 4 && Text(boss, "typeA") == "dynamic",
                $"count {Number(boss, "count")} mine {Number(boss, "mine")} drag {Number(boss, "drag")} strength {Number(boss, "strength")} speed {Number(boss, "speedA")} mass {Number(boss, "massA")} type {Text(boss, "typeA")}");
            Check("a compiled function sets the target's body", Math.Abs(Number(boss, "damping") - 0.59) < 1e-4 && Math.Abs(Body(crates[0]).LinearDamping - 0.59f) < 1e-4,
                $"returned {Number(boss, "damping")}, body {Body(crates[0]).LinearDamping} (0.59)");
            Check("Use gives a Physics resource's body, Remove takes it", Number(thrown, "used") == 1 && Text(thrown, "shape") == "sphere" && Math.Abs(Number(thrown, "bounce") - 0.9) < 1e-4
                && Number(thrown, "had") == 1 && Number(thrown, "removed") == 1 && Number(thrown, "after") == 0,
                $"used {Number(thrown, "used")} shape {Text(thrown, "shape")} bounce {Number(thrown, "bounce")} had {Number(thrown, "had")} removed {Number(thrown, "removed")} after {Number(thrown, "after")}");
            Check("no script errors", diagnostics.Length == 0, diagnostics);
        }
        finally
        {
            PgslCommands.BindContext(previousContext);
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.ProjectPath = previousPath;
        }
    }
}
