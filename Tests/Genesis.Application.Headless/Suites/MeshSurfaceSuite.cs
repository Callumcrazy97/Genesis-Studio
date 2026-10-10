using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Runtime;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.Primitives;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Surface shaders (request 33 of the Aetherium port): a project shader that returns albedo,
/// normal, roughness, metalness, emission and alpha and is lit by the engine; a shader per material
/// of a model; and per-instance shader values and images. Pixel-checked on DX11, DX12, Vulkan and
/// OpenGL (the Software renderer runs no project shaders).
/// </summary>
internal static class MeshSurfaceSuite
{
    private static readonly RenderBackendOption[] GpuBackends =
    {
        RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan, RenderBackendOption.OpenGL,
    };

    // A surface that changes nothing: the engine's own material, lit by the engine.
    private const string Unchanged = """
        #include "GenesisSurface.hlsl"
        void Surface(GenesisSurfaceInput i, inout GenesisSurface s) { }
        """;

    // Albedo from the draw's parameters (a tint picked in a colour box) and light from Glow.
    private const string Painted = """
        #include "GenesisSurface.hlsl"
        cbuffer GenesisParameters : register(b5) { float4 Paint; float Glow; };
        void Surface(GenesisSurfaceInput i, inout GenesisSurface s)
        {
            s.Albedo = GenesisColor(Paint.rgb);
            s.Emission = GenesisColor(float3(0.1, 1.0, 0.2)) * Glow;
        }
        """;

    // Glass: nearly clear facing the camera, opaque at a grazing angle.
    private const string Glass = """
        #include "GenesisSurface.hlsl"
        void Surface(GenesisSurfaceInput i, inout GenesisSurface s)
        {
            s.Albedo = GenesisColor(float3(0.75, 0.85, 0.95));
            s.Alpha = lerp(0.12, 1.0, GenesisFresnel(i, s.Normal, 3.0));
            s.Roughness = 0.08;
        }
        """;

    // A model material's camo: the draw's paint times a pattern image (the instance may swap it).
    private const string Camo = """
        #include "GenesisSurface.hlsl"
        cbuffer GenesisParameters : register(b5) { float4 Paint; };
        Texture2D CamoTex : register(t17);
        void Surface(GenesisSurfaceInput i, inout GenesisSurface s)
        {
            s.Albedo = GenesisColor(Paint.rgb) * GenesisColor(CamoTex.Sample(GenesisLinearWrap, i.UV).rgb);
        }
        """;

    // A full pixel shader of the older kind, as an Object's Shader.
    private const string FlatPurple = """
        struct VSOut { float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2; float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; };
        struct PSOut { float4 Color : SV_Target0; float4 Aux : SV_Target1; };
        PSOut MainPS(VSOut IN) { PSOut o; o.Color = float4(0.6, 0.08, 0.8, 1.0); o.Aux = float4(0, 0, 0, 0); return o; }
        """;

    private static readonly Vector3 SunFront = Vector3.Normalize(new Vector3(0.45f, -0.75f, 0.5f));
    private static readonly Vector3 SunBehind = Vector3.Normalize(new Vector3(0.2f, -0.6f, -1f));
    private static readonly Vector3 CubeA = new(-1.3f, 0.6f, 0f);
    private static readonly Vector3 CubeB = new(1.3f, 0.6f, 0f);
    private static readonly Vector3 PaintA = new(0.85f, 0.12f, 0.10f);
    private static readonly Vector3 PaintB = new(0.12f, 0.25f, 0.85f);
    private static readonly Vector3 FloorGrey = new(0.55f, 0.56f, 0.58f);

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Surface.ComposesForEveryBackend", () => Composes(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Surface.EngineLightsTheSurfaceOnAllBackends", () => EngineLights(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Surface.MaterialShadersAndInstanceValuesOnAllBackends", () => Materials(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.MaterialShaderKeptInTheDescriptor", DescriptorKeepsShaders);
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);

    // ── Composition ──────────────────────────────────────────────────────────

    private static void Composes(HeadlessContext ctx)
    {
        Check(MeshSurfaceShaders.IsSurfaceSource(Painted), "A source including GenesisSurface.hlsl was not seen as a surface shader.");
        Check(!MeshSurfaceShaders.IsSurfaceSource("// #include \"GenesisSurface.hlsl\"\nfloat4 MainPS() : SV_Target { return 1; }"),
            "A commented-out include made a surface shader.");
        Check(!MeshSurfaceShaders.IsSurfaceSource(FlatPurple), "A full pixel shader was taken for a surface shader.");

        string composed = MeshSurfaceShaders.Compose(Painted, @"C:\Project\Assets\Shaders\Painted.shader.json");
        Check(composed.Contains("#define GENESIS_SURFACE 1", StringComparison.Ordinal)
            && composed.Contains("#line 1 \"Painted.shader.json\"", StringComparison.Ordinal)
            && !composed.Contains(@"C:\Project", StringComparison.Ordinal),
            "The composed shader does not name the project file (and only its name) for its own lines.");
        Check(composed.IndexOf("cbuffer GenesisParameters", StringComparison.Ordinal)
                < composed.IndexOf("PSOut PS(VSOut IN", StringComparison.Ordinal)
            && composed.IndexOf("cbuffer EngineConstants", StringComparison.Ordinal)
                < composed.IndexOf("cbuffer GenesisParameters", StringComparison.Ordinal),
            "The project's code is not between the engine's declarations and its pixel shader.");

        bool refused = false;
        try { MeshSurfaceShaders.Compose("#include \"GenesisSurface.hlsl\"\nfloat Helper() { return 1; }"); }
        catch (InvalidOperationException error) { refused = error.Message.Contains("void Surface(", StringComparison.Ordinal); }
        Check(refused, "A surface shader without a Surface function was not explained.");

        // Every backend's format compiles, with the engine's entry point whatever the resource names.
        foreach (GpuShaderBinaryFormat format in PrecompiledShaders.AllFormats)
        {
            ShaderCompileResult result = ShaderCompiler.CompileForBackend(Painted, "MainPS", GpuShaderStage.Pixel, format,
                Path.Combine(ctx.Workspace, "Painted.shader.json"));
            Check(result.Blob.Length > 0 && result.EntryPoint == MeshSurfaceShaders.PixelEntry,
                $"The surface shader did not compile for {format} (entry {result.EntryPoint}).");
        }

        // The cache key names the file, not its folder: a game cooks its shaders where it is made
        // and finds them again where it is installed.
        string here = ShaderCompiler.CacheKey(Painted, "MainPS", GpuShaderStage.Pixel, GpuShaderBinaryFormat.Dxbc,
            @"C:\Made\Here\Painted.shader.json");
        string there = ShaderCompiler.CacheKey(Painted, "MainPS", GpuShaderStage.Pixel, GpuShaderBinaryFormat.Dxbc,
            @"D:\Installed\There\Painted.shader.json");
        Check(here == there, "A surface shader's cache key depends on the folder it is compiled in.");
    }

    // ── Lighting ─────────────────────────────────────────────────────────────

    private static void EngineLights(HeadlessContext ctx)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        var report = new List<string>();
        try
        {
            foreach (RenderBackendOption backend in GpuBackends)
            {
                RenderBackendSelection.Configure(backend);
                string name = backend.ToString().ToLowerInvariant();
                using RuntimeViewportHarness harness = new();
                IRenderController renderer = harness.Renderer;
                MeshHandle cube = harness.LitSceneCube;
                var shaders = new Dictionary<string, RuntimeShaderHandle>();
                RuntimeShaderHandle Shader(string source)
                {
                    if (!shaders.TryGetValue(source, out RuntimeShaderHandle handle))
                    {
                        handle = renderer.RegisterRuntimeShader(source, "MainPS", ShaderPreviewProfile.MeshPipeline);
                        Check(handle.IsValid, $"{backend}: a surface shader did not register.");
                        shaders[source] = handle;
                    }
                    return handle;
                }

                try
                {
                    // engine: the engine's material in each colour. unchanged: the same draws through a
                    // surface that changes nothing. painted: white draws whose colour comes from the
                    // surface's parameters. The floor is drawn the same way as the boxes each time.
                    Action<IRenderController> Scene(string? surface, bool viaParameters, float glow = 0f) => r =>
                    {
                        RuntimeShaderHandle shader = surface is null ? default : Shader(surface);
                        void Box(Vector3 centre, Vector3 size, Vector3 colour)
                        {
                            bool parameters = viaParameters && shader.IsValid;
                            r.DrawMesh(new MeshDrawCall
                            {
                                Mesh = cube,
                                World = Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(centre),
                                Tint = parameters ? RenderColor.White : new RenderColor(colour.X, colour.Y, colour.Z),
                                Alpha = 1f,
                                Shader = shader,
                                ShaderParams0 = new Vector4(colour, 1f),
                                ShaderParams1 = new Vector4(centre == CubeA ? glow : 0f, 0f, 0f, 0f),
                            });
                        }
                        Box(new Vector3(0f, -0.26f, 0f), new Vector3(10f, 0.5f, 10f), FloorGrey);
                        Box(CubeA, new Vector3(1.2f), PaintA);
                        Box(CubeB, new Vector3(1.2f), PaintB);
                    };

                    string File(string what) => Path.Combine(ctx.Captures, $"surface-{name}-{what}.png");
                    var lamp = new PointLight3D(new Vector3(-1.3f, 0.9f, -1.4f), new Vector3(1f, 0.6f, 0.2f), 3.2f, 2.5f);
                    RuntimeImageMetrics engine = harness.CaptureLitScene(File("engine"), SunFront, shadows: true, Scene(null, false));
                    RuntimeImageMetrics engineFlat = harness.CaptureLitScene(File("engine-no-shadow"), SunFront, shadows: false, Scene(null, false));
                    RuntimeImageMetrics unchanged = harness.CaptureLitScene(File("unchanged"), SunFront, shadows: true, Scene(Unchanged, false));
                    RuntimeImageMetrics painted = harness.CaptureLitScene(File("painted"), SunFront, shadows: true, Scene(Painted, true));
                    RuntimeImageMetrics paintedFlat = harness.CaptureLitScene(File("painted-no-shadow"), SunFront, shadows: false, Scene(Painted, true));
                    RuntimeImageMetrics engineLamp = harness.CaptureLitScene(File("engine-lamp"), SunBehind, shadows: true, Scene(null, false), lamp);
                    RuntimeImageMetrics paintedLamp = harness.CaptureLitScene(File("painted-lamp"), SunBehind, shadows: true, Scene(Painted, true), lamp);
                    RuntimeImageMetrics paintedBehind = harness.CaptureLitScene(File("painted-behind"), SunBehind, shadows: true, Scene(Painted, true));
                    RuntimeImageMetrics glowing = harness.CaptureLitScene(File("painted-glow"), SunBehind, shadows: true, Scene(Painted, true, glow: 3f));

                    double shadowsShow = RuntimeImageMetrics.MaxTileDelta(engine, engineFlat);
                    double sameAsEngine = RuntimeImageMetrics.MaxTileDelta(engine, unchanged);
                    double paintedAsEngine = RuntimeImageMetrics.MaxTileDelta(engine, painted);
                    double paintedShadows = RuntimeImageMetrics.MaxTileDelta(painted, paintedFlat);
                    double lampAsEngine = RuntimeImageMetrics.MaxTileDelta(engineLamp, paintedLamp);
                    double lampShows = RuntimeImageMetrics.MaxTileDelta(paintedLamp, paintedBehind);
                    report.Add($"{backend}: shadows {shadowsShow:F1}, unchanged {sameAsEngine:F2}, painted {paintedAsEngine:F2}, "
                        + $"painted shadows {paintedShadows:F1}, lamp {lampAsEngine:F2} (lamp adds {lampShows:F1})");
                    Check(shadowsShow > 12, $"{backend}: the scene shows no sun shadow to compare ({shadowsShow:F1}).");
                    Check(sameAsEngine <= 2.5,
                        $"{backend}: a surface that changes nothing does not look like the engine's material (tile delta {sameAsEngine:F2}).");
                    Check(paintedAsEngine <= 2.5,
                        $"{backend}: a surface painting its parameters' colour is not lit like the engine's material of that colour "
                        + $"(tile delta {paintedAsEngine:F2}, worst tile {RuntimeImageMetrics.WorstTileIndex(engine, painted)}).");
                    Check(paintedShadows > 12 && Math.Abs(paintedShadows - shadowsShow) <= 3,
                        $"{backend}: surface-shaded geometry does not take the sun's shadows as the engine's does ({paintedShadows:F1} vs {shadowsShow:F1}).");
                    Check(lampAsEngine <= 2.5 && lampShows > 8,
                        $"{backend}: a lamp does not light the surface as it lights the engine's material ({lampAsEngine:F2}; the lamp adds {lampShows:F1}).");

                    using Bitmap front = new(File("painted"));
                    using Bitmap behind = new(File("painted-behind"));
                    using Bitmap glow = new(File("painted-glow"));
                    Point aFace = harness.ProjectLitScenePoint(CubeA + new Vector3(0f, 0f, -0.6f));
                    Point bFace = harness.ProjectLitScenePoint(CubeB + new Vector3(0f, 0f, -0.6f));
                    Color aLit = Mean(front, aFace), bLit = Mean(front, bFace), aDark = Mean(behind, aFace), aGlow = Mean(glow, aFace);
                    Check(aLit.R > aLit.G + 60 && aLit.R > aLit.B + 60 && bLit.B > bLit.R + 50,
                        $"{backend}: the boxes do not wear their own parameters' colours (A {aLit}, B {bLit}).");
                    Check(Luma(aLit) > Luma(aDark) * 1.6,
                        $"{backend}: turning the sun away did not darken the surface ({Luma(aLit):F0} lit, {Luma(aDark):F0} from behind).");
                    Check(aGlow.G > aGlow.R + 60 && Luma(aGlow) > Luma(aDark) + 60,
                        $"{backend}: the surface's emission did not light the box in shade (glowing {aGlow}, unlit {aDark}).");

                    // See-through: the glass box is clear where it faces the camera, so the red box
                    // behind it shows; its edges are near opaque.
                    RuntimeImageMetrics glass = harness.CaptureLitScene(File("glass"), SunFront, shadows: true, r =>
                    {
                        Scene(null, false)(r);
                        r.DrawMesh(new MeshDrawCall
                        {
                            Mesh = cube,
                            World = Matrix4x4.CreateScale(1.6f, 1.6f, 0.3f) * Matrix4x4.CreateTranslation(CubeA + new Vector3(0f, 0.1f, -1.3f)),
                            Tint = RenderColor.White, Alpha = 1f, Shader = Shader(Glass),
                            Flags = MeshDrawFlags.Transparent | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow,
                        });
                    });
                    using Bitmap glassImage = new(File("glass"));
                    Color through = Mean(glassImage, aFace);
                    Check(through.R > through.B + 30 && Luma(through) < Luma(aLit) + 60,
                        $"{backend}: the red box does not show through the glass's clear middle ({through}, red box alone {aLit}).");
                }
                finally
                {
                    foreach (RuntimeShaderHandle handle in shaders.Values)
                        renderer.ReleaseRuntimeShader(handle, ShaderPreviewProfile.MeshPipeline);
                }
            }
        }
        finally { RenderBackendSelection.Configure(previous); }
        File.WriteAllLines(Path.Combine(ctx.Captures, "surface-lighting.txt"), report);
    }

    // ── Materials and instances ──────────────────────────────────────────────

    private static void Materials(HeadlessContext ctx)
    {
        string root = BuildGunProject(ctx, "SurfaceMaterials", out string stripesName);
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        IGameContext? previousGame = PgslCommands.ActiveGameContext;
        string? previousProject = PgslCommands.ProjectPath;
        var registry = new ObjectDrawAssetRegistry.PreviewScope();
        var report = new List<string>();
        try
        {
            using IDisposable scope = registry.Activate();
            using var scene = new RuntimeScene("Surface materials");
            PgslCommands.ActiveGameContext = new Genesis.Runtime.Project.ProjectGameContext(root, scene, null, null,
                Genesis.Runtime.Scene.RoomAsset.Create("Probe", Genesis.Runtime.Scene.RoomDimension.ThreeD), null);
            PgslCommands.ProjectPath = root;

            Entity red = PlaceGun(scene, -2.2f), blue = PlaceGun(scene, 0f), striped = PlaceGun(scene, 2.2f);
            MaterialsOnBackends(ctx, root, scene, red, blue, striped, stripesName, report);
        }
        finally
        {
            RenderBackendSelection.Configure(previous);
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.ProjectPath = previousProject;
            File.WriteAllLines(Path.Combine(ctx.Captures, "surface-materials.txt"), report);
        }
    }

    /// <summary>
    /// A fresh project with the gun model (a painted body, material Paint, whose own shader is the
    /// camo surface, under a metal block, material Metal, the engine's material), the camo and a
    /// flat purple full pixel shader, and two images: Plain (white) and stripes (black and white).
    /// </summary>
    internal static string BuildGunProject(HeadlessContext ctx, string name, out string stripesName)
    {
        string parent = Path.Combine(ctx.Workspace, name);
        Directory.CreateDirectory(parent);
        ProjectSession project = new ProjectService().CreateProject(parent, "Surfaces" + Guid.NewGuid().ToString("N")[..6], "Blank");
        string root = project.RootPath;
        var resources = new ResourceService(project);

        // Images: plain white and black-and-white stripes, four texels wide.
        string plainFile = Path.Combine(parent, "Plain.png"), stripesFile = Path.Combine(parent, "Stripes.png");
        using (Bitmap plain = new(8, 8))
        {
            using (Graphics g = Graphics.FromImage(plain)) g.Clear(Color.White);
            plain.Save(plainFile, ImageFormat.Png);
        }
        using (Bitmap stripes = new(16, 16))
        {
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    stripes.SetPixel(x, y, (x / 4) % 2 == 0 ? Color.White : Color.Black);
            stripes.Save(stripesFile, ImageFormat.Png);
        }
        string[] images = resources.ImportFiles(resources.AssetsRoot, [plainFile, stripesFile]).ToArray();
        string ImageName(string path) => Path.GetFileName(path).Split('.')[0];
        string plainName = ImageName(images[0]);
        stripesName = ImageName(images[1]);

        // Shaders: the camo surface (paint green and a plain pattern unless an instance says
        // otherwise) and a flat purple full pixel shader.
        string shaders = Path.Combine(root, "Assets", "Shaders");
        Directory.CreateDirectory(shaders);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(shaders, "Test Camo.shader.json"), JsonSerializer.Serialize(new ShaderAssetDocument
        {
            Pipeline = ShaderAssetPipeline.Mesh, AuthoringMode = ShaderAuthoringMode.Code, TargetType = ShaderTargetType.Model,
            Source = Camo,
            Parameters = [new ShaderParameterValue { Name = "Paint", Type = "float4", Value = [0.2f, 0.9f, 0.2f, 1f] }],
            Resources = [new ShaderResourceBinding { Name = "CamoTex", Kind = ShaderResourceKind.Texture2D, Slot = 17, Binding = plainName }],
        }, options));
        File.WriteAllText(Path.Combine(shaders, "Flat Purple.shader.json"), JsonSerializer.Serialize(new ShaderAssetDocument
        {
            Pipeline = ShaderAssetPipeline.Mesh, AuthoringMode = ShaderAuthoringMode.Code, TargetType = ShaderTargetType.Model,
            Source = FlatPurple,
        }, options));

        // The gun: a painted body (material Paint, with the camo as its own shader) under a metal
        // block (material Metal, the engine's material).
        string modelFile = Path.Combine(root, "Assets", "Models", "Gun.model.json");
        Directory.CreateDirectory(Path.GetDirectoryName(modelFile)!);
        File.WriteAllText(modelFile, "{}");
        GModelAsset gun = GModelPrimitiveFactory.CreateCube("Gun", 1f);
        gun.Materials[0].Name = "Paint";
        gun.Materials.Add(new GModelMaterial { Name = "Metal", BaseColor = new Vector4(0.6f, 0.6f, 0.62f, 1f) });
        (MeshVertex[] upper, ushort[] upperIndices) = Genesis.Rendering.Meshes.MeshGeometry.BuildCube(RenderColor.White, 1f);
        for (int i = 0; i < upper.Length; i++) upper[i].Position += new Vector3(0f, 1f, 0f);
        gun.Meshes.Add(new GModelMesh { Name = "Block", MaterialIndex = 1, Vertices = upper, Indices = upperIndices });
        gun.RecalculateBounds();
        StudioModelResourceLoader.SaveCanonical(modelFile, gun);
        JsonObject descriptor = JsonNode.Parse(File.ReadAllText(modelFile))!.AsObject();
        descriptor["materialShaders"] = new JsonObject { ["Paint"] = "Test Camo" };
        File.WriteAllText(modelFile, descriptor.ToJsonString());
        ResourceNames.Invalidate(root);
        ResourceCatalog.Invalidate(root);
        RuntimeAssetPolicy.Invalidate();
        return root;
    }

    /// <summary>A gun from <see cref="BuildGunProject"/> standing on the floor at x.</summary>
    internal static Entity PlaceGun(RuntimeScene scene, float x)
    {
        Entity entity = scene.World.CreateEntity();
        scene.World.Set(entity, new TransformComponent { X = x, Y = 0.5f, Z = 0f, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        scene.World.Set(entity, new Draw3DComponent { Visible = true, CastShadows = true, ReceiveShadows = true });
        scene.World.Set(entity, new ModelRendererComponent
        {
            ModelAsset = "Gun", ScaleX = 1, ScaleY = 1, ScaleZ = 1, CastShadows = true, ReceiveShadows = true,
        });
        ObjectDrawAssetRegistry.Set(entity, new ObjectDrawAssetEntry { Model = "Gun", Is3D = true });
        return entity;
    }

    private static void MaterialsOnBackends(HeadlessContext ctx, string root, RuntimeScene scene,
        Entity red, Entity blue, Entity striped, string stripesName, List<string> report)
    {
        {
            // Every gun its own camo: one by its paint, one by its paint and an Object shader on the
            // rest of the model, one by its images.
            PgslCommands.InstanceSetShaderVector(red.Id, "Paint", 1, 0.1, 0.1, 1);
            PgslCommands.InstanceSetShader(red.Id, "Flat Purple");
            PgslCommands.InstanceSetShaderVector(blue.Id, "Paint", 0.1, 0.25, 1, 1);
            Check(PgslCommands.InstanceSetMaterialTexture(striped.Id, "Paint", "CamoTex", stripesName),
                "InstanceSetMaterialTexture refused a texture of the material's shader.");
            Check(PgslCommands.InstanceSetMaterialTexture(striped.Id, "Metal", "albedo", stripesName),
                "InstanceSetMaterialTexture refused the material's own albedo.");
            Check(!PgslCommands.InstanceSetMaterialTexture(striped.Id, "No Such Material", "albedo", stripesName),
                "InstanceSetMaterialTexture accepted a material the model does not have.");

            var buffer = new MeshDrawCall[256];
            foreach (RenderBackendOption backend in GpuBackends)
            {
                RenderBackendSelection.Configure(backend);
                string name = backend.ToString().ToLowerInvariant();
                using RuntimeViewportHarness harness = new();
                (Matrix4x4 view, Matrix4x4 projection) = harness.LitSceneCamera();
                Matrix4x4.Invert(view, out Matrix4x4 inverse);
                Vector3 forward = Vector3.Normalize(new Vector3(inverse.M31, inverse.M32, inverse.M33));
                string file = Path.Combine(ctx.Captures, $"surface-{name}-materials.png");
                int draws = 0;
                harness.CaptureLitScene(file, Vector3.Normalize(new Vector3(0.3f, -0.7f, 1f)), shadows: true, r =>
                {
                    int count = 0;
                    ObjectDrawPass.SubmitMeshes3D(scene.World, root, buffer, ref count, r,
                        RuntimeViewportHarness.LitSceneEye, forward, view * projection);
                    draws = count;
                    if (count > 0) r.DrawMeshBatch(buffer.AsSpan(0, count));
                });
                ObjectDrawPass.InvalidateAssets(harness.Renderer);
                Check(draws == 6, $"{backend}: three guns of two meshes made {draws} draws.");

                using Bitmap image = new(file);
                Color Paint(Entity gun) => Mean(image, harness.ProjectLitScenePoint(Centre(scene, gun) + new Vector3(0f, 0f, -0.5f)), 3);
                Color Block(Entity gun) => Mean(image, harness.ProjectLitScenePoint(Centre(scene, gun) + new Vector3(0f, 1f, -0.5f)), 3);
                (double low, double high) Spread(Point centre)
                {
                    double min = double.MaxValue, max = 0;
                    for (int x = -16; x <= 16; x++)
                    {
                        double l = Luma(image.GetPixel(Math.Clamp(centre.X + x, 0, image.Width - 1), centre.Y));
                        min = Math.Min(min, l); max = Math.Max(max, l);
                    }
                    return (min, max);
                }

                Color redPaint = Paint(red), redBlock = Block(red), bluePaint = Paint(blue), blueBlock = Block(blue);
                (double paintLow, double paintHigh) = Spread(harness.ProjectLitScenePoint(Centre(scene, striped) + new Vector3(0f, 0f, -0.5f)));
                (double blockLow, double blockHigh) = Spread(harness.ProjectLitScenePoint(Centre(scene, striped) + new Vector3(0f, 1f, -0.5f)));
                (double plainLow, double plainHigh) = Spread(harness.ProjectLitScenePoint(Centre(scene, blue) + new Vector3(0f, 0f, -0.5f)));
                Color stripedPaint = Max(image, harness.ProjectLitScenePoint(Centre(scene, striped) + new Vector3(0f, 0f, -0.5f)));
                report.Add($"{backend}: red paint {redPaint}, purple block {redBlock}, blue paint {bluePaint}, metal {blueBlock}, "
                    + $"striped paint {paintLow:F0}-{paintHigh:F0} (brightest {stripedPaint}), striped metal {blockLow:F0}-{blockHigh:F0}");

                Check(redPaint.R > redPaint.G + 50 && redPaint.R > redPaint.B + 50,
                    $"{backend}: the first gun's paint is not its own red ({redPaint}).");
                Check(redBlock.R > redBlock.G + 40 && redBlock.B > redBlock.G + 40,
                    $"{backend}: the Object's shader did not draw the rest of the model ({redBlock}), or it replaced the material's own.");
                Check(bluePaint.B > bluePaint.R + 50 && bluePaint.B > bluePaint.G + 30,
                    $"{backend}: the second gun's paint is not its own blue ({bluePaint}).");
                Check(Math.Abs(blueBlock.R - blueBlock.G) < 25 && Math.Abs(blueBlock.G - blueBlock.B) < 30 && Luma(blueBlock) > 50,
                    $"{backend}: a material without a shader did not keep the engine's material ({blueBlock}).");
                Check(stripedPaint.G > stripedPaint.R + 50 && paintHigh > paintLow + 50,
                    $"{backend}: the third gun's paint is not its default green over its own striped pattern "
                    + $"({paintLow:F0}-{paintHigh:F0}, brightest {stripedPaint}).");
                Check(blockHigh > blockLow + 50, $"{backend}: the third gun's metal did not take its own striped albedo ({blockLow:F0}-{blockHigh:F0}).");
                Check(plainHigh - plainLow < 30, $"{backend}: another gun's paint took the third gun's pattern ({plainLow:F0}-{plainHigh:F0}).");
            }

            // Batching: draws with the same values share a batch; an instance with values of its
            // own costs one more batch, and nothing else changes.
            {
                RenderBackendSelection.Configure(RenderBackendOption.SilkNetDx11);
                using RuntimeViewportHarness harness = new();
                IRenderController renderer = harness.Renderer;
                MeshHandle cube = harness.LitSceneCube;
                RuntimeShaderHandle painted = renderer.RegisterRuntimeShader(Painted, "MainPS", ShaderPreviewProfile.MeshPipeline);
                try
                {
                    int Batches(bool ownValues)
                    {
                        harness.CaptureLitScene(Path.Combine(ctx.Captures, $"surface-batches-{(ownValues ? "own" : "shared")}.png"),
                            SunFront, shadows: false, r =>
                            {
                                for (int i = 0; i < 12; i++)
                                    r.DrawMesh(new MeshDrawCall
                                    {
                                        Mesh = cube, Tint = RenderColor.White, Alpha = 1f, Shader = painted,
                                        World = Matrix4x4.CreateScale(0.4f) * Matrix4x4.CreateTranslation(-2.2f + i * 0.4f, 0.2f, 0f),
                                        ShaderParams0 = ownValues && i == 5 ? new Vector4(1, 0, 0, 1) : new Vector4(0.2f, 0.5f, 0.9f, 1),
                                    });
                            });
                        return harness.LastStats.Batches;
                    }
                    int shared = Batches(ownValues: false), own = Batches(ownValues: true);
                    report.Add($"batches: twelve boxes sharing values {shared}, one with its own {own}");
                    Check(own == shared + 1, $"One instance's own values cost {own - shared} batches (shared {shared}, own {own}); expected one.");
                }
                finally { renderer.ReleaseRuntimeShader(painted, ShaderPreviewProfile.MeshPipeline); }
            }
        }
    }

    private static Vector3 Centre(RuntimeScene scene, Entity gun)
    {
        TransformComponent t = scene.World.GetRef<TransformComponent>(gun);
        return new Vector3(t.X, t.Y, t.Z);
    }

    // ── Model editor ─────────────────────────────────────────────────────────

    private static void DescriptorKeepsShaders()
    {
        var asset = new GModelAsset();
        asset.Materials.Add(new GModelMaterial { Name = "Glass", Shader = "Window Glass" });
        asset.Materials.Add(new GModelMaterial { Name = "Walls", Shader = "" });
        asset.Materials.Add(new GModelMaterial { Name = "Roof", Shader = "Roof Moss" });
        JsonObject document = JsonNode.Parse("""{"MaterialShaders":{"glass":"Old Glass","Walls":"Old Walls","Gone":"Kept"}}""")!.AsObject();
        Genesis.Application.Editors.Suite.Assets.ModelViewerControl.WriteMaterialShaders(document, asset);
        string json = document.ToJsonString();
        JsonObject? map = document["MaterialShaders"] as JsonObject;
        Check(map is not null && document.Count == 1, $"The descriptor's map was duplicated or renamed: {json}");
        Check((string?)map!["Glass"] == "Window Glass" && (string?)map["Roof"] == "Roof Moss"
            && map["Walls"] is null && map["glass"] is null && (string?)map["Gone"] == "Kept",
            $"The materials' shaders were not written into the descriptor as they are: {json}");

        // Every material that had a shader gives it up: no map is left behind.
        var plain = new GModelAsset();
        foreach (string material in new[] { "Glass", "Roof", "Gone" })
            plain.Materials.Add(new GModelMaterial { Name = material, Shader = "" });
        Genesis.Application.Editors.Suite.Assets.ModelViewerControl.WriteMaterialShaders(document, plain);
        Check(document["MaterialShaders"] is null, $"An empty map was left in the descriptor: {document.ToJsonString()}");

        var fresh = JsonNode.Parse("{}")!.AsObject();
        Genesis.Application.Editors.Suite.Assets.ModelViewerControl.WriteMaterialShaders(fresh, asset);
        Check(fresh["materialShaders"] is JsonObject added && added.Count == 2, $"A new map was not written: {fresh.ToJsonString()}");
    }

    // ── Pixels ───────────────────────────────────────────────────────────────

    private static Color Mean(Bitmap bitmap, Point point, int radius = 4)
    {
        long r = 0, g = 0, b = 0;
        int n = 0;
        for (int y = -radius; y <= radius; y++)
            for (int x = -radius; x <= radius; x++)
            {
                Color c = bitmap.GetPixel(Math.Clamp(point.X + x, 0, bitmap.Width - 1), Math.Clamp(point.Y + y, 0, bitmap.Height - 1));
                r += c.R; g += c.G; b += c.B; n++;
            }
        return Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
    }

    private static Color Max(Bitmap bitmap, Point point)
    {
        Color best = Color.Black;
        for (int x = -16; x <= 16; x++)
        {
            Color c = bitmap.GetPixel(Math.Clamp(point.X + x, 0, bitmap.Width - 1), point.Y);
            if (Luma(c) > Luma(best)) best = c;
        }
        return best;
    }

    private static double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
}
