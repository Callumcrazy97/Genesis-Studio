using System.Drawing;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Genesis.Application.Core.Automation;
using Genesis.Application.Core.Projects;
using Genesis.Rendering.Core;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// NV1: a static model mesh whose material is unlit (glTF KHR_materials_unlit) is drawn in its
/// texture's own colour, with the sun on, through both static paths a game uses (a single draw and
/// an instanced batch, which rooms' static models use); the same mesh made lit is shaded. The model
/// is a quad of our own, built here: nothing from another project.
/// </summary>
internal static class UnlitModelSuite
{
    private const int Width = 320, Height = 240;
    private static readonly Color Paint = Color.FromArgb(200, 80, 40);

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Render.Models.Unlit");
        HeadlessHarness.RunCase(ctx.Report, "Render.Models.Unlit.StaticMeshDrawsItsTextureColour", () =>
        {
            ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "UnlitModels"), "Unlit models");
            AutomationContext context = AutomationContext.For(project);
            context.Caller = "test";
            AutomationRegistry registry = AutomationRegistry.CreateDefault();
            AutomationResult Call(string name, string json) => registry.Invoke(context, name, JsonNode.Parse(json) as JsonObject);

            string glb = WriteUnlitQuad(Path.Combine(ctx.Workspace, "unlit-quad"));
            AutomationResult imported = Call("resource.import", $$"""{ "files": ["{{glb.Replace("\\", "\\\\")}}"], "folder": "Assets", "check": false }""");
            string model = imported.Changed.FirstOrDefault(path => path.EndsWith(".model.json", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            Check(imported.Ok && model.Length > 0, "The unlit quad was not imported: " + imported.Message);
            GModelAsset asset = StudioModelResourceLoader.Load(context.Resolve(model));
            Check(asset.Materials.Single().Unlit && !asset.Meshes.Any(mesh => mesh.IsSkinned || mesh.SkinnedVertices.Length > 0),
                "The quad did not import as a static mesh with an unlit material.");
            string modelName = ResourceNames.Name(project.RootPath, context.Resolve(model));

            using Form host = UnattendedWindowing.NewHost(Width, Height);
            UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11);
            renderer.Initialize(host.Handle, Width, Height);
            RuntimeModelRenderSystem models = new();
            try
            {
                foreach (bool instanced in new[] { false, true })
                {
                    Color unlit = Render(renderer, models, project.RootPath, modelName, instanced, ctx, "unlit-static-" + (instanced ? "instanced" : "single"));
                    Check(Close(unlit, Paint, 3), $"An unlit static mesh ({(instanced ? "instanced" : "single draw")}) drew {Describe(unlit)}, not its texture's {Describe(Paint)}.");
                }

                // The same mesh lit: the Model editor's tick box (the descriptor's materialUnlit) turns it off.
                Check(Call("resource.set", $$"""{ "path": "{{model}}", "values": { "materialUnlit": { "paint": false } }, "check": false }""").Ok
                    && !StudioModelResourceLoader.Load(context.Resolve(model)).Materials.Single().Unlit, "materialUnlit did not make the material lit.");
                models.InvalidateAssets(renderer);
                foreach (bool instanced in new[] { false, true })
                {
                    Color lit = Render(renderer, models, project.RootPath, modelName, instanced, ctx, "lit-static-" + (instanced ? "instanced" : "single"));
                    Check(!Close(lit, Paint, 12), $"The same mesh made lit ({(instanced ? "instanced" : "single draw")}) still drew its texture's colour {Describe(lit)}: it is not being lit.");
                }
            }
            finally { models.InvalidateAssets(renderer); }
        });
    }

    /// <summary>
    /// Draws the model as gameplay submits it (RuntimeModelRenderSystem), with the sun and ambient light
    /// on and no tone curve, fog or bloom, and returns the average colour at the middle of the picture.
    /// </summary>
    private static Color Render(IRenderController renderer, RuntimeModelRenderSystem models, string projectRoot, string model,
        bool instanced, HeadlessContext ctx, string name)
    {
        using var scene = new Genesis.Runtime.RuntimeScene(name);
        var world = scene.World;
        var entity = world.CreateEntity();
        world.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        world.Set(entity, new Draw3DComponent { Visible = true, CastShadows = true, ReceiveShadows = true });
        world.Set(entity, new ModelRendererComponent { ModelAsset = model, ScaleX = 1, ScaleY = 1, ScaleZ = 1, CastShadows = true, ReceiveShadows = true });

        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = true;
        state.FogEnabled = false;
        state.ShowFloor = state.ShowSunVisual = false;
        state.NoToneMapping = true;
        state.BloomEnabled = false;
        renderer.SetMesh3DState(state);
        renderer.Set3DFrameActive(true);
        renderer.SetCamera3D(Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 3f), Vector3.Zero, Vector3.UnitY),
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Width / (float)Height, .1f, 100));

        MeshDrawCall[] draws = new MeshDrawCall[8];
        int count = 0;
        // Textures load on a worker: ask until the draw is ready.
        for (int attempt = 0; attempt < 200 && count == 0; attempt++)
        {
            models.BeginFrame();
            count = models.SubmitWorld(world, projectRoot, draws, 0, renderer);
            models.EndFrame();
            if (count == 0) Thread.Sleep(10);
        }
        Check(count > 0, "Gameplay submitted no geometry for the quad.");

        Color middle = Color.Black;
        // A few frames, so anything carried from frame to frame (exposure, temporal smoothing) settles.
        for (int frame = 0; frame < 4; frame++)
        {
            renderer.BeginFrame();
            renderer.Clear(.02f, .02f, .03f);
            for (int index = 0; index < count; index++)
            {
                MeshDrawCall draw = draws[index];
                if (instanced) renderer.DrawMeshInstances(draw, [new MeshInstanceData(draw.World, RenderColor.White)]);
                else renderer.DrawMesh(draw);
            }
            renderer.EndFrame();
            Check(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels), "No pixels were read back.");
            middle = Average(pixels, width, height);
            if (frame == 3) Save(pixels, width, height, ctx, name);
            renderer.Present();
        }
        Console.WriteLine($"  {name}: {Describe(middle)} (texture {Describe(Paint)})");
        return middle;
    }

    /// <summary>A 2 m quad facing the camera, double-sided, its 8 × 8 picture of one colour beside it, the material unlit.</summary>
    private static string WriteUnlitQuad(string folder)
    {
        Directory.CreateDirectory(folder);
        using (Bitmap picture = new(8, 8))
        {
            using (Graphics graphics = Graphics.FromImage(picture)) graphics.Clear(Paint);
            picture.Save(Path.Combine(folder, "paint.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        float[] positions = [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, 1, 1, 0, -1, 1, 0];
        float[] uvs = [0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0];
        string json = """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0,"TEXCOORD_0":1},"material":0}]}],
             "materials":[{"name":"paint","doubleSided":true,"pbrMetallicRoughness":{"baseColorTexture":{"index":0}},"extensions":{"KHR_materials_unlit":{}}}],
             "extensionsUsed":["KHR_materials_unlit"],
             "textures":[{"source":0}],"images":[{"uri":"paint.png"}],
             "buffers":[{"byteLength":120}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":72},{"buffer":0,"byteOffset":72,"byteLength":48}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":6,"type":"VEC3","min":[-1,-1,0],"max":[1,1,0]},
                          {"bufferView":1,"componentType":5126,"count":6,"type":"VEC2"}]}
            """;
        byte[] text = System.Text.Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
        string path = Path.Combine(folder, "Unlit Quad.glb");
        using BinaryWriter glb = new(File.Create(path));
        glb.Write(0x46546C67u); glb.Write(2u); glb.Write((uint)(12 + 8 + text.Length + 8 + 120));
        glb.Write((uint)text.Length); glb.Write(0x4E4F534Au); glb.Write(text);
        glb.Write(120u); glb.Write(0x004E4942u);
        foreach (float value in positions) glb.Write(value);
        foreach (float value in uvs) glb.Write(value);
        return path;
    }

    /// <summary>The average of a 9 × 9 square at the middle of a BGRA picture.</summary>
    private static Color Average(byte[] pixels, int width, int height)
    {
        int r = 0, g = 0, b = 0, n = 0;
        for (int y = height / 2 - 4; y <= height / 2 + 4; y++)
        {
            for (int x = width / 2 - 4; x <= width / 2 + 4; x++)
            {
                int i = (y * width + x) * 4;
                b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; n++;
            }
        }
        return Color.FromArgb(r / n, g / n, b / n);
    }

    private static bool Close(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static string Describe(Color colour) => $"{colour.R},{colour.G},{colour.B}";

    private static void Save(byte[] pixels, int width, int height, HeadlessContext ctx, string name)
    {
        using Bitmap bitmap = new(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        string file = Path.Combine(ctx.Captures, name + ".png");
        bitmap.Save(file);
        ctx.Report.Images.Add(ImageResult.From(name, file, VisualCapture.Measure(bitmap)));
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
