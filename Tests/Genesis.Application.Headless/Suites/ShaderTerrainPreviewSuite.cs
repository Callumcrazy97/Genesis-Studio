using System.Drawing;
using System.Numerics;
using System.Security.Cryptography;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;
using Genesis.World;
using Genesis.World.Foliage;
using Genesis.World.Terrain;
using Genesis.World.Water;

namespace Genesis.Application.Headless.Suites;

internal static class ShaderTerrainPreviewSuite
{
    private const string MagentaShader = """
        struct VSOut
        {
            float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2;
            float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; float4 ShadowPos : TEXCOORD5;
            float4 ShadowPosNr : TEXCOORD6; float AtlasLayer : TEXCOORD7;
        };
        float4 MainPS(VSOut IN) : SV_Target { return float4(1, 0, 1, 1); }
        """;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Shader.TerrainPreview");
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        try
        {
            foreach (RenderBackendOption backend in new[] { RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
                RenderBackendOption.Vulkan, RenderBackendOption.OpenGL, RenderBackendOption.Software })
                HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.Terrain.ComponentsGeometryAndShaderIsolation." + backend,
                    () => CheckComponents(ctx, backend));
        }
        finally { RenderBackendSelection.Configure(previous); }
    }

    private static void CheckComponents(HeadlessContext ctx, RenderBackendOption backend)
    {
        RenderBackendSelection.Configure(backend);
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ShaderTerrain-" + backend), "Shader terrain components");
        var resources = new ResourceService(project);
        string shaderPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Shader, "Terrain shader");
        string terrainPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Authored terrain");
        string pinkImage = MakeTexture("Pink shader texture", Color.Magenta);
        string cyanImage = MakeTexture("Cyan shader texture", Color.Cyan);
        string malformedJson = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Malformed settings");
        File.WriteAllText(malformedJson, """{"resolution":[999999999999999999,129]}""");
        string malformedBinary = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Malformed data");
        using (BinaryWriter writer = new(File.Create(malformedBinary + ".gterrain")))
        {
            writer.Write(0x4E525447); writer.Write(1); writer.Write(int.MaxValue); writer.Write(int.MaxValue);
            foreach (float value in new[] { 1f, 0f, 0f, -1f, 3f }) writer.Write(value);
        }
        TerrainAsset terrain = new(65, 65, .25f, -8f, -8f, -1f, 3f);
        for (int z = 0; z < 65; z++)
        for (int x = 0; x < 65; x++) terrain.SetHeight(x, z, 0);
        terrain.Save(terrainPath + ".gterrain");
        TerrainPathDefinition path = new() { Id = "trail", Name = "Authored trail", Width = 1.1f,
            Points = [new(-5f, 0, -4f), new(0, 0, -3f), new(5f, 0, -4f)] };
        TerrainWaterDefinition pond = new() { Id = "pond", Name = "L-shaped pond", Center = new(-3, 0, 2),
            SizeX = 3, SizeZ = 3, SurfaceHeight = .35f, SimulationEnabled = false };
        pond.CaptureFootprint(-4.5f, .5f, .75f, 4, 4,
            [true, false, false, false, true, false, false, false, true, true, true, true, true, true, true, true]);
        TerrainWaterDefinition waterfall = new() { Id = "falls", Name = "Waterfall sheet", Kind = TerrainWaterKind.Waterfall,
            Waterfall = new WaterfallParams { TopLeft = new(-1, 2.5f, 0), TopRight = new(1, 2.5f, 0),
                BottomLeft = new(-1, .1f, 0), BottomRight = new(1, .1f, 0), VerticalSegments = 8 } };
        FoliageField foliage = new() { Instances = [new(new(3, 0, 2), 1.8f, .35f, FoliageSpecies.Shrub, 1, .2f),
            new(new(4.4f, 0, 2.8f), 1.2f, -.25f, FoliageSpecies.Shrub, 1, -.1f)] };
        string foliagePath = terrainPath + ".foliage.bin";
        string foliageHash = FoliageFieldCache.Save(foliagePath, foliage);
        TerrainPointOfInterest point = new() { Id = "landmark", Name = "Lookout", Position = new(0, .1f, 4), DiscoveryRadius = 1.5f };
        TerrainNatureDocument nature = new() { Paths = [path], WaterBodies = [pond, waterfall], PointsOfInterest = [point],
            FoliageCacheFile = Path.GetFileName(foliagePath), FoliageCacheSha256 = foliageHash, FoliageInstanceCount = 2 };
        TerrainNatureSerializer.Save(terrainPath, nature);
        string[] sourcePaths = [terrainPath, terrainPath + ".gterrain", TerrainNatureSerializer.SidecarPath(terrainPath), foliagePath];
        string[] originalHashes = sourcePaths.Select(Hash).ToArray();

        using var editor = new ShaderEditorControl(shaderPath, project.RootPath);
        // Warm/CaptureFrame submit every required frame explicitly. A slow software frame
        // can otherwise keep the timer queue nonempty and starve Application.DoEvents.
        editor.Viewport.Host.TimerEnabled = false;
        using var host = GateSuite.NewHost(1040, 760);
        host.Controls.Add(editor);
        ThemeService.Apply(host);
        GateSuite.ShowHost(host);
        editor.SelectPreset("Terrain Tint");
        Assert(editor.ChoosePreviewAsset(terrainPath), "Terrain target is missing from the project picker.");
        editor.SetSource(MagentaShader);
        editor.CompileNow();
        editor.SetFloorStyle(EditorFloorStyle.None);
        Warm(editor);
        bool software = backend == RenderBackendOption.Software;
        Assert(editor.LastCompileSucceeded && editor.LivePreviewApplied == !software, editor.DiagnosticText);
        if (software)
            Assert(!editor.SupportsAuthoredShaderPreview && editor.DiagnosticText.Contains("Software previews geometry only", StringComparison.Ordinal),
                "Software falsely reports execution of an authored shader.");

        Check("path:trail", TerrainPathGeometry.BuildRibbon(path, terrain.SampleHeight, .5f));
        Check("water:pond", WaterSurfaceMesh.BuildVisual(pond.ToWaterBody(), pond.Center));
        Check("water:falls", WaterSurfaceMesh.BuildVisual(waterfall.ToWaterBody(), waterfall.Center));
        Select("nature:vegetation");
        ShaderTerrainPreviewSubmission trees = editor.TerrainPreviewSubmissions.Single(value => value.ReceivesShader);
        MeshData shrub = FoliageGeometry.Build(FoliageSpecies.Shrub, true);
        Assert(trees.Component == "nature:vegetation" && trees.Instances == 2 && trees.Triangles == shrub.Indices.Length / 3,
            "Vegetation preview did not submit the authored instances with their species geometry.");
        Assert(trees.Minimum.X > 1 && trees.Maximum.X > 4 && trees.Maximum.Y > 1,
            "Vegetation transforms or scale were lost.");
        AssertTargetPixels("vegetation");
        if (!software) CheckTextureAndReferenceIsolation();

        Select("point:landmark");
        Assert(editor.TerrainPreviewSubmissions.All(value => !value.ReceivesShader), "A point marker was presented as a shaderable surface.");
        ShaderTerrainPreviewSubmission guide = editor.TerrainPreviewSubmissions.Single(value => value.Component == "point:landmark");
        Assert(Math.Abs(guide.Minimum.X + point.DiscoveryRadius) < .04f
            && Math.Abs(guide.Maximum.X - point.DiscoveryRadius) < .04f, "Point guide ignored its authored position/radius.");
        Assert(editor.TerrainPreviewMessage.Contains("no renderable surface", StringComparison.OrdinalIgnoreCase), "Point selection did not explain its non-surface semantics.");
        using (Bitmap frame = editor.Viewport.CaptureFrame(3) ?? throw new InvalidOperationException("Point preview returned no frame."))
            Assert(CountPixels(frame, Magenta) == 0, "The point shader leaked onto context geometry.");
        Capture("point-guide");

        Select("all");
        if (!software)
        {
            string status = editor.Controls.Find("ShaderStatus", true).Single().Text;
            Assert(status.Contains("All", StringComparison.Ordinal)
                && !status.Contains("no renderable surface", StringComparison.OrdinalIgnoreCase),
                "Switching from the point guide left stale shader status: " + status);
        }
        Assert(editor.TerrainPreviewSubmissions.Count(value => value.ReceivesShader) == 5,
            "All must include the ground, path, two water bodies and vegetation.");
        host.ClientSize = new Size(800, 650);
        Warm(editor);
        ShaderTerrainPreviewSubmission ground = editor.TerrainPreviewSubmissions.First();
        AssertFramed(ground.Minimum, ground.Maximum);
        Capture("all-800");
        float yaw = editor.Viewport.Camera.Yaw + .2f;
        editor.Viewport.Camera.Yaw = yaw;
        editor.Viewport.Camera.Distance *= .9f;
        float distance = editor.Viewport.Camera.Distance;
        Warm(editor);
        Assert(editor.Viewport.Camera.Yaw == yaw && editor.Viewport.Camera.Distance == distance,
            "A steady-size preview overwrote the user's camera navigation.");
        Select("water:pond"); editor.Save();
        using (var reopened = new ShaderEditorControl(shaderPath, project.RootPath))
            Assert(reopened.TargetComponent == "water:pond", "The chosen component did not survive save/reopen.");
        Assert(sourcePaths.Select(Hash).SequenceEqual(originalHashes), "Preview changed an authored terrain/nature/foliage file.");

        foreach (string malformed in new[] { malformedJson, malformedBinary })
        {
            Assert(editor.ChoosePreviewAsset(malformed), "Malformed fixture is absent from the picker.");
            Warm(editor);
            Assert(editor.TerrainPreviewMessage.StartsWith("Terrain preview unavailable:", StringComparison.Ordinal)
                && editor.TerrainPreviewSubmissions.Count == 0,
                "Malformed terrain escaped the guarded preview or kept stale geometry: " + editor.TerrainPreviewMessage);
            Capture("invalid-" + Path.GetFileNameWithoutExtension(malformed));
            Assert(editor.ChoosePreviewAsset(terrainPath), "Cannot return to the valid terrain after a preview error.");
            Select("water:pond");
            Assert(editor.TerrainPreviewMessage.Length == 0 && editor.TerrainPreviewSubmissions.Any(value => value.Component == "water:pond"),
                "A preview error prevented a subsequent valid terrain from rendering.");
        }
        // Repair the malformed header itself, then reselect it. Preview failures cannot poison
        // the resource cache after the file has become valid.
        terrain.Save(malformedBinary + ".gterrain");
        Assert(editor.ChoosePreviewAsset(malformedBinary), "Cannot select repaired terrain.");
        Select("all");
        Assert(editor.TerrainPreviewMessage.Length == 0 && editor.TerrainPreviewSubmissions.Count == 1,
            "Repairing a malformed terrain did not restore its surface preview.");

        string MakeTexture(string name, Color colour)
        {
            string resource = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, name);
            string pixelsPath = Path.Combine(Path.GetDirectoryName(resource)!, ResourceAssociates.GetStem(resource) + ".png");
            using Bitmap pixels = new(8, 8);
            using (Graphics graphics = Graphics.FromImage(pixels)) graphics.Clear(colour);
            pixels.Save(pixelsPath, System.Drawing.Imaging.ImageFormat.Png);
            return Path.GetRelativePath(project.RootPath, resource).Replace('\\', '/');
        }

        void CheckTextureAndReferenceIsolation()
        {
            string textured = "Texture2D PreviewTexture : register(t9);\nSamplerState PreviewSampler : register(s0);\n"
                + MagentaShader.Replace("return float4(1, 0, 1, 1);", "return PreviewTexture.Sample(PreviewSampler, IN.UV);", StringComparison.Ordinal);
            editor.SetSource(textured); editor.CompileNow();
            Assert(editor.LastCompileSucceeded && editor.SetResourceBinding("PreviewTexture", pinkImage),
                "The reflected vegetation texture control was unavailable: " + editor.DiagnosticText);
            ComboBox texturePicker = (ComboBox)editor.Controls.Find("ShaderResourcePicker_PreviewTexture", true).Single();
            Assert(texturePicker.SelectedItem?.ToString() == "Pink shader texture",
                "The texture picker did not reflect the current binding.");
            Warm(editor);
            using Bitmap pink = editor.Viewport.CaptureFrame(3) ?? throw new InvalidOperationException("Texture preview returned no frame.");
            int pinkPixels = CountPixels(pink, Magenta);
            Assert(pinkPixels > 25, "The selected instanced vegetation did not receive its authored texture binding.");
            texturePicker.SelectedItem = "Cyan shader texture";
            Assert(editor.ResourceBinding("PreviewTexture") == cyanImage, "The GUI texture picker did not update its binding.");
            Warm(editor);
            using (Bitmap cyan = editor.Viewport.CaptureFrame(3) ?? throw new InvalidOperationException("Changed texture returned no frame."))
            {
                int changed = 0;
                for (int y = 0; y < pink.Height; y += 2)
                for (int x = 0; x < pink.Width; x += 2)
                    if (Magenta(pink.GetPixel(x, y)) && Cyan(cyan.GetPixel(x, y))) changed++;
                Capture("vegetation-texture");
                Assert(changed > pinkPixels * .9f,
                    $"Changing the GUI texture did not recolour selected vegetation pixels ({changed}/{pinkPixels}).");
            }

            // Use a fixed overview for both frames so the reference vegetation is visible while
            // the pond owns the edited shader. Changing that shader's texture must leave every
            // reference leaf and its terrain context unchanged.
            Select("water:pond");
            Overview();
            ShaderTerrainPreviewSubmission reference = editor.TerrainPreviewSubmissions.Single(value => value.Component == "nature:vegetation");
            Rectangle leafRegion = ProjectBounds(reference.Minimum, reference.Maximum);
            using Bitmap before = editor.Viewport.CaptureFrame(3) ?? throw new InvalidOperationException("Reference preview returned no frame.");
            Assert(editor.SetResourceBinding("PreviewTexture", pinkImage), "Cannot change the pond texture.");
            Warm(editor); Overview();
            using (Bitmap after = editor.Viewport.CaptureFrame(3) ?? throw new InvalidOperationException("Reference isolation returned no frame."))
            {
                int green = 0, changed = 0, count = 0;
                for (int y = leafRegion.Top; y < leafRegion.Bottom; y += 2)
                for (int x = leafRegion.Left; x < leafRegion.Right; x += 2)
                {
                    Color a = before.GetPixel(x, y), b = after.GetPixel(x, y);
                    if (a.G > 45 && a.G > a.B * 1.15f) green++;
                    if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 8) changed++;
                    count++;
                }
                Capture("reference-vegetation-isolation");
                Assert(green > 25 && count > 25 && changed <= count * .01f,
                    $"The selected pond's shader changed reference vegetation ({changed}/{count} pixels; green={green}).");
                Assert(CountPixels(before, Cyan) > 25 && CountPixels(after, Magenta) > 25,
                    "The reference isolation check did not visibly change the selected pond shader.");
            }
            editor.SetSource(MagentaShader); editor.CompileNow(); Warm(editor);
        }

        void Overview()
        {
            editor.Viewport.Camera.Target = new Vector3(0, .7f, 0);
            editor.Viewport.Camera.Yaw = MathF.PI;
            editor.Viewport.Camera.Pitch = -.5f;
            editor.Viewport.Camera.Distance = 22f;
            Warm(editor);
        }
        Rectangle ProjectBounds(Vector3 min, Vector3 max)
        {
            Vector2 screenMin = new(float.MaxValue), screenMax = new(float.MinValue);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 screen = editor.Viewport.WorldToSurface(new Vector3((corner & 1) == 0 ? min.X : max.X,
                    (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z));
                screenMin = Vector2.Min(screenMin, new Vector2(screen.X, screen.Y));
                screenMax = Vector2.Max(screenMax, new Vector2(screen.X, screen.Y));
            }
            return Rectangle.Intersect(new Rectangle(0, 0, editor.Viewport.Host.Width, editor.Viewport.Host.Height),
                Rectangle.FromLTRB((int)MathF.Floor(screenMin.X), (int)MathF.Floor(screenMin.Y),
                    (int)MathF.Ceiling(screenMax.X), (int)MathF.Ceiling(screenMax.Y)));
        }

        void Check(string target, MeshData expected)
        {
            Select(target);
            if (target == "water:pond")
            {
                Vector3 eye = editor.Viewport.Camera.Eye;
                Assert(eye.Y > terrain.SampleHeight(eye.X, eye.Z),
                    "Framing the pond put the camera inside its buried fill volume: " + eye);
            }
            ShaderTerrainPreviewSubmission actual = editor.TerrainPreviewSubmissions.Single(value => value.ReceivesShader);
            Assert(actual.Component == target && actual.Vertices == expected.Vertices.Length && actual.Triangles == expected.Indices.Length / 3,
                "The selected component did not use its authored geometry: " + target);
            Vector3 min = expected.Vertices.Aggregate(new Vector3(float.MaxValue), (value, vertex) => Vector3.Min(value, vertex.Position));
            Vector3 max = expected.Vertices.Aggregate(new Vector3(float.MinValue), (value, vertex) => Vector3.Max(value, vertex.Position));
            Assert(Vector3.Distance(actual.Minimum, min) < .001f && Vector3.Distance(actual.Maximum, max) < .001f,
                "The selected component was replaced by different preview geometry: " + target);
            Assert(editor.TerrainPreviewSubmissions.Where(value => !value.ReceivesShader)
                .All(value => (value.Flags & MeshDrawFlags.EditorReference) != 0), "Context geometry can receive the edited shader.");
            AssertTargetPixels(target.Replace(':', '-'));
        }
        void Select(string target)
        {
            Assert(editor.TryApplyInspectorValue("TargetComponent", target), "Component missing: " + target);
            Warm(editor);
            if (!software && (target.StartsWith("path:", StringComparison.Ordinal) || target.StartsWith("water:", StringComparison.Ordinal)))
            {
                string label = ((ComboBox)editor.Controls.Find("ShaderTerrainComponentPicker", true).Single()).SelectedItem?.ToString() ?? string.Empty;
                string status = editor.Controls.Find("ShaderStatus", true).Single().Text;
                Assert(label.Length > 0 && status.Contains(label, StringComparison.Ordinal),
                    "Shader status does not match its chosen component: " + status + " / " + label);
            }
        }
        void AssertTargetPixels(string name)
        {
            using Bitmap frame = editor.Viewport.CaptureFrame(3) ?? throw new InvalidOperationException("Component preview returned no frame.");
            if (software)
            {
                Assert(!editor.LivePreviewApplied && CountPixels(frame, Magenta) == 0,
                    "Software substituted a fake shader result for the unsupported effect.");
                Assert(CountPixels(frame, colour => colour.G > 45 && colour.G > colour.B * 1.1f) > 25,
                    "Software did not render the terrain's geometry fallback.");
                return;
            }
            int shaded = CountPixels(frame, Magenta);
            int context = CountPixels(frame, colour => colour.G > 35 && colour.G > colour.R * 1.12f && colour.G > colour.B * 1.1f);
            Console.WriteLine($"  {name}: target pixels={shaded}; context pixels={context}; camera={editor.Viewport.Camera.Eye}; target={editor.Viewport.Camera.Target}");
            Capture(name, force: shaded <= 25 || context <= 25);
            Assert(shaded > 25, $"The selected {name} shader produced no visible target pixels ({shaded}).");
            Assert(context > 25, $"The {name} preview lost its independently shaded terrain context ({context}).");
        }
        void AssertFramed(Vector3 min, Vector3 max)
        {
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 screen = editor.Viewport.WorldToSurface(new Vector3(
                    (corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z));
                Assert(screen.X >= 0 && screen.X <= editor.Viewport.Host.Width && screen.Y >= 0 && screen.Y <= editor.Viewport.Host.Height,
                    "Terrain framing cropped an authored bound at narrow width: " + screen);
            }
        }
        void Capture(string name, bool force = false)
        {
            if (backend != RenderBackendOption.OpenGL && !force) return;
            using Bitmap capture = VisualCapture.CaptureWindowPixels(host);
            string file = "shader-terrain-component-" + name
                + (backend == RenderBackendOption.OpenGL ? string.Empty : "-" + backend) + ".png";
            capture.Save(Path.Combine(ctx.Captures, file));
            var metrics = Genesis.Application.Runtime.ImageMetrics.Measure(capture);
            ctx.Report.Images.Add(new ImageResult("Shader terrain component", file, metrics.Width, metrics.Height,
                metrics.UniqueSampledColors, metrics.AverageLuminance));
        }
    }

    private static bool Magenta(Color colour) => colour.R > 160 && colour.B > 140 && colour.G < 85;
    private static bool Cyan(Color colour) => colour.R < 85 && colour.G > 140 && colour.B > 140;
    private static int CountPixels(Bitmap image, Func<Color, bool> predicate)
    {
        int count = 0;
        for (int y = 0; y < image.Height; y += 2)
        for (int x = 0; x < image.Width; x += 2)
            if (predicate(image.GetPixel(x, y))) count++;
        return count;
    }
    private static void Warm(ShaderEditorControl editor) { GateSuite.Pump(3, 30); using var frame = editor.Viewport.CaptureFrame(5); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
