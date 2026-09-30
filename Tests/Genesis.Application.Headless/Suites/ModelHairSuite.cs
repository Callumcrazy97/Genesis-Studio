using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

internal static class ModelHairSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Model hair");
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Hair.StylesColoursAndInstanceIsolation", () =>
        {
            GModelAsset asset = Asset();
            var first = new ModelRendererComponent { HiddenMeshes = new() { "Body" }, MaterialTints = new() { ["Shirt"] = Vector4.One } };
            var second = new ModelRendererComponent();
            Check(ModelHairRuntime.TrySetStyles(ref first, asset, "Short", "None", out _), "Style choice rejected.");
            Check(ModelHairRuntime.TrySetColor(ref first, asset, false, new(.4f, .2f, .1f), out _), "Hair colour rejected.");
            ModelHairSelection a = ModelHairRuntime.Resolve(asset, first.Hair), b = ModelHairRuntime.Resolve(asset, second.Hair);
            Check(a.IsVisible("Short") && !a.IsVisible("Long") && !a.IsVisible("Beard") && a.IsVisible("Brows"), "Style and eyebrow visibility incorrect.");
            Check(b.IsVisible("Long") && b.IsVisible("Beard") && !b.IsVisible("Short"), "Shared model leaked styles between instances.");
            Check(a.TryGetColor("Brows", out Vector3 tint) && tint.X == .4f && !a.TryGetColor("Body", out _), "Hair colour affected the body or missed brows.");
            Check(!b.TryGetColor("Long", out _), "Shared model leaked hair colours.");
            var saved = first.Hair;
            Check(!ModelHairRuntime.TrySetStyles(ref first, asset, "Missing", "None", out _) && first.Hair == saved, "Invalid style destroyed the previous appearance.");
            Check(!ModelHairRuntime.TrySetColor(ref first, asset, true, new(float.NaN, 0, 0), out _), "Nonfinite colour accepted.");
            first.Hair = null;
            Check(first.HiddenMeshes.SetEquals(["Body"]) && first.MaterialTints.ContainsKey("Shirt"), "Hair reset damaged unrelated appearance state.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Hair.CanonicalSaveReopenAndLiveEdit", () =>
        {
            GModelAsset asset = Asset();
            string path = Path.Combine(ctx.Workspace, "Assets/Models/HairCheck.model.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{}");
            StudioModelResourceLoader.SaveCanonical(path, asset);
            GModelAsset reopened = StudioModelResourceLoader.LoadReadOnly(path);
            Check(ModelHairRuntime.Resolve(reopened, null).IsVisible("Long"), "Hair profile lost during canonical save.");
            ModelHairProfile profile = ModelHairProfile.Read(reopened);
            profile.DefaultScalp = "Short"; profile.Save(reopened);
            Check(!ModelHairRuntime.Resolve(reopened, null).IsVisible("Long") && ModelHairRuntime.Resolve(reopened, null).IsVisible("Short"), "Live authoring edit stayed cached.");
            profile.Styles.RemoveAll(style => style.Name == "Long"); profile.Save(reopened);
            Check(ModelHairRuntime.Resolve(reopened, new("Long", "Full")).IsVisible("Short"), "Removed style did not fall back after reimport.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Hair.MalformedProfileCannotHideBody", () =>
        {
            GModelAsset asset = Asset();
            asset.Metadata[ModelHairProfile.MetadataKey] = "{";
            Check(ModelHairRuntime.Resolve(asset, new("None", "None")).IsVisible("Body"), "Malformed metadata hid the body.");
            var renderer = new ModelRendererComponent();
            Check(!ModelHairRuntime.TrySetStyles(ref renderer, asset, "None", "None", out string error) && error.Length > 0, "Malformed data produced no useful error.");
            ModelHairProfile profile = new() { Styles = [new() { Name = "Wrong", Meshes = ["Missing"] }] };
            bool rejected = false;
            try { profile.Save(asset); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Authoring accepted a missing mesh.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Hair.SteadyFrameResolutionDoesNotAllocate", () =>
        {
            GModelAsset asset = Asset(); ModelHairAppearance appearance = new("Short", "Full");
            for (int i = 0; i < 100; i++) ModelHairRuntime.Resolve(asset, appearance).IsVisible("Short");
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool visible = true;
            for (int i = 0; i < 10000; i++) visible &= ModelHairRuntime.Resolve(asset, appearance).IsVisible("Short");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(visible && allocated < 1024, $"Hair lookup allocated {allocated} bytes per 10000 repeated frames.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Hair.SavedPgslRunsNativeStyleCommands", () =>
        {
            string reference = "Assets/Models/HairCommands.model.json";
            string path = Path.Combine(ctx.Workspace, reference);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{}");
            StudioModelResourceLoader.SaveCanonical(path, Asset());
            string script = Path.Combine(ctx.Workspace, "Assets/Objects/Hair commands/Create.pgsl");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, "ModelHairSetStyles(\"Short\", \"None\"); ModelHairSetColor(0.4, 0.2, 0.1);");
            using var scene = new RuntimeScene("Hair commands");
            var entity = scene.World.CreateEntity();
            scene.World.Set(entity, new ModelRendererComponent { ModelAsset = reference });
            var game = new ProjectGameContext(ctx.Workspace, scene, null, null, RoomAsset.Create("Hair", RoomDimension.ThreeD), null);
            var previousGame = PgslCommands.ActiveGameContext; string previousPath = PgslCommands.ProjectPath;
            VMEngine.Initialize(); PgslContext? previous = VMEngine.Bridge.GetContext();
            try
            {
                PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = ctx.Workspace;
                VMEngine.Bridge.SetContext(new PgslContext { InstanceId = entity.Id });
                var compiled = ScriptAssetCompiler.Compile("Hair commands/Create", File.ReadAllText(script));
                var vm = VMEngine.CreateVm(); vm.Execute(compiled.CompileResult.Instructions, compiled.CompileResult.Constants);
                ModelHairAppearance? hair = scene.World.GetRef<ModelRendererComponent>(entity).Hair;
                Check(hair?.ScalpStyle == "Short" && hair.FacialStyle == "None" && hair.ScalpColor?.X == .4f,
                    "Saved script did not apply native hair commands: " + PgslCommands.ModelHairLastError());
            }
            finally { VMEngine.Bridge.SetContext(previous); PgslCommands.ActiveGameContext = previousGame; PgslCommands.ProjectPath = previousPath; }
        });
    }

    private static void Check(bool value, string message) => HeadlessHarness.Assert(value, message);
    private static GModelAsset Asset()
    {
        GModelAsset asset = GModelPrimitiveFactory.CreateCube("Hair profile check", 1);
        asset.Meshes[0].Name = "Body";
        foreach (string name in new[] { "Short", "Long", "Beard", "Brows" })
        {
            GModelMesh mesh = GModelPrimitiveFactory.CreateCube(name, .1f).Meshes[0]; mesh.Name = name; asset.Meshes.Add(mesh);
        }
        new ModelHairProfile
        {
            DefaultScalp = "Long", DefaultFacial = "Full", EyebrowMeshes = ["Brows"],
            Styles = [new() { Name = "Short", Kind = "Scalp", Meshes = ["Short"] }, new() { Name = "Long", Kind = "Scalp", Meshes = ["Long"] },
                new() { Name = "Full", Kind = "Facial", Meshes = ["Beard"] }],
        }.Save(asset);
        return asset;
    }
}
