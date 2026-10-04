using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Project post effects: a project's Fullscreen Shader resources run over the finished frame. The
/// ink outline is one such shader, kept in the project (Fixtures/InkOutline.hlsl), not in the engine.
/// </summary>
internal static class PostEffectsSuite
{
    private const string InvertSource = """
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        cbuffer GenesisParameters : register(b5) { float Amount; };
        Texture2D SceneColor : register(t0);
        struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
        float4 MainPS(PreviewVSOut IN) : SV_Target
        {
            float4 c = SceneColor.Load(int3(int2(IN.SvPos.xy), 0));
            c.rgb = lerp(c.rgb, 1.0 - c.rgb, Amount);
            return c;
        }
        """;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "PostEffects");
        string parent = Path.Combine(ctx.Workspace, "PostEffects");
        Directory.CreateDirectory(parent);
        ProjectSession project = new ProjectService().CreateProject(parent, "Post Effects", "Blank");
        ResourceService resources = new(project);

        string invert = WriteShader(resources, "Invert", InvertSource, ("Amount", 1f));
        string inkSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "InkOutline.hlsl"));
        WriteShader(resources, "Ink Outline", inkSource,
            ("Opacity", 1f), ("WidthPixels", 2f), ("DepthStep", 0.012f), ("CreaseDegrees", 32f),
            ("InkColor", new[] { 0.10f, 0.06f, 0.04f }), ("FullWidthDistance", 6f), ("FarDistance", 40f),
            ("FarOpacity", 0.6f), ("FadeOutDistance", 80f), ("FoliageDistance", 20f));
        ResourceCatalog.Invalidate(project.RootPath);

        string terrainPath = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Terrain"), ResourceKind.Terrain, "Meadow");
        using Form host = GateSuite.NewHost(1120, 800);
        TerrainEditorControl editor = new(terrainPath, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(8, 25);
        IReadOnlyList<PostEffectRequest> requests = Array.Empty<PostEffectRequest>();
        string? compileError = null;
        editor.Viewport.DrawScene += renderer =>
        {
            renderer.SetPostEffects(requests);
            if (!string.IsNullOrEmpty(renderer.LastPostEffectError)) compileError = renderer.LastPostEffectError;
        };
        Bitmap Capture(string file)
        {
            GateSuite.Pump(3, 20);
            Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 6);
            HeadlessHarness.Assert(frame is not null, "The terrain viewport could not be read back.");
            frame!.Save(Path.Combine(ctx.Captures, file), ImageFormat.Png);
            return frame;
        }
        void Use(params string[] effects)
        {
            ProjectPostEffects.SetRoomEffects(effects);
            requests = ProjectPostEffects.RequestsFor(project.RootPath).ToList();
        }

        try
        {
            editor.ApplyGeneration(new TerrainGenParams
            {
                Preset = TerrainPreset.Flatlands, ResolutionX = 257, ResolutionZ = 257, CellSize = 1f,
                MinHeight = -2f, MaxHeight = 6f, Seed = 5101,
            });
            editor.ScatterFoliage(new FoliageScatterSettings
            {
                Seed = 5102, Preset = FoliagePreset.Meadow, MaximumInstances = 40000, Density = 1f,
                MinimumSpacing = 0.6f, NearDistance = 40f, FarDistance = 200f, StreamingCellSize = 16f,
                VisibleInstanceBudget = 30000, TriangleBudget = 600000,
            });
            FoliageInstance standing = editor.Foliage.Instances
                .OrderBy(instance => (instance.Position.X * instance.Position.X) + (instance.Position.Z * instance.Position.Z))
                .First();
            editor.Viewport.Camera.Target = standing.Position + (Vector3.UnitY * 1.6f);
            editor.Viewport.Camera.Distance = 4f;
            editor.Viewport.Camera.Pitch = -0.12f;
            editor.Viewport.Camera.Yaw = 0.4f;

            Use();
            using Bitmap plain = Capture("post-none.png");

            HeadlessHarness.RunCase(ctx.Report, "Engine.Rendering.PostEffects.AProjectShaderRunsOverTheFinishedFrame", () =>
            {
                Use("Invert");
                using Bitmap inverted = Capture("post-invert.png");
                Check(compileError is null, "The post effect did not compile: " + compileError);
                int matching = 0, total = 0;
                for (int y = 40; y < plain.Height - 40; y += 23)
                {
                    for (int x = 40; x < plain.Width - 40; x += 23)
                    {
                        Color a = plain.GetPixel(x, y), b = inverted.GetPixel(x, y);
                        if (Math.Abs(a.R + b.R - 255) < 24 && Math.Abs(a.G + b.G - 255) < 24 && Math.Abs(a.B + b.B - 255) < 24) matching++;
                        total++;
                    }
                }
                Check(matching > total * 0.8, $"The frame was not inverted by the project's shader: {matching} of {total} samples match.");
                ProjectPostEffects.SetParameter("Invert", "Amount", 0f);
                requests = ProjectPostEffects.RequestsFor(project.RootPath).ToList();
                using Bitmap undone = Capture("post-invert-zero.png");
                Color centre = plain.GetPixel(plain.Width / 2, plain.Height / 2), back = undone.GetPixel(plain.Width / 2, plain.Height / 2);
                Check(Math.Abs(centre.R - back.R) + Math.Abs(centre.G - back.G) + Math.Abs(centre.B - back.B) < 30,
                    "Setting the effect's parameter to 0 did not take it off again.");
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Rendering.PostEffects.InkOutlineShaderDoesNotSpeckleDistantFoliage", () =>
            {
                Use("Ink Outline");
                using Bitmap inked = Capture("post-ink.png");
                Check(compileError is null, "The ink outline shader did not compile: " + compileError);
                int horizon = HorizonRow(plain);
                double h = plain.Height;
                double far = InkedShare(plain, inked, (horizon + 2) / h, Math.Min(0.99, (horizon + (0.12 * h)) / h));
                double near = InkedShare(plain, inked, 0.82, 0.99);
                string measured = $"distant meadow {far:P2} inked, near grass {near:P2}";
                // Inked with no level of detail, 46% of the distant meadow's pixels were.
                Check(near > 0.02, "The outline is not drawn on the near grass: " + measured);
                Check(far < 0.15, "Distant foliage is speckled with ink: " + measured);
            });
        }
        finally
        {
            ProjectPostEffects.Clear();
            editor.Dispose();
        }
    }

    private static string WriteShader(ResourceService resources, string name, string source, params (string Name, object Value)[] parameters)
    {
        string path = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Shaders"), ResourceKind.Shader, name);
        var document = new ShaderAssetDocument
        {
            Pipeline = ShaderAssetPipeline.Fullscreen,
            AuthoringMode = ShaderAuthoringMode.Code,
            TargetType = ShaderTargetType.Fullscreen,
            Entry = "MainPS",
            Source = source,
            Parameters = parameters.Select(p => new ShaderParameterValue
            {
                Name = p.Name,
                Type = p.Value is float[] v ? $"float{v.Length}" : "float",
                Value = p.Value is float[] values ? values : new[] { (float)p.Value },
            }).ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true,
        }));
        return path;
    }

    private static int HorizonRow(Bitmap frame)
    {
        Color sky = frame.GetPixel(frame.Width / 2, 4);
        for (int y = 8; y < frame.Height; y += 2)
        {
            int ground = 0;
            for (int x = 0; x < frame.Width; x += 8)
            {
                Color c = frame.GetPixel(x, y);
                if (Math.Abs(c.R - sky.R) + Math.Abs(c.G - sky.G) + Math.Abs(c.B - sky.B) > 40) ground++;
            }
            if (ground * 8 > frame.Width * 0.7) return y;
        }
        return frame.Height / 2;
    }

    /// <summary>Share of pixels in a band of rows (fractions of the height) that the ink made clearly darker.</summary>
    private static double InkedShare(Bitmap plain, Bitmap inked, double fromRow, double toRow)
    {
        int width = Math.Min(plain.Width, inked.Width), height = Math.Min(plain.Height, inked.Height);
        int top = (int)(height * fromRow), bottom = (int)(height * toRow);
        int darker = 0, total = 0;
        for (int y = top; y < bottom; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                Color a = plain.GetPixel(x, y), b = inked.GetPixel(x, y);
                float la = (a.R * 0.299f) + (a.G * 0.587f) + (a.B * 0.114f);
                float lb = (b.R * 0.299f) + (b.G * 0.587f) + (b.B * 0.114f);
                if (la - lb > 25f) darker++;
                total++;
            }
        }
        return total == 0 ? 0 : darker / (double)total;
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
