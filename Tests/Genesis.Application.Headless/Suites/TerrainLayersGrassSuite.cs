using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.Primitives;
using Genesis.Runtime.Assets;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Application.Headless.Suites;

/// <summary>Eight terrain paint layers (two splat planes, per-layer tiled maps, height blend) and rule grass.</summary>
internal static partial class TerrainLayersGrassSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Layers.EightLayerPaintSavesSecondPlaneAndKeepsVersionOneFiles", () => PaintAndFiles(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Layers.AtlasCellsCopyTilesWithWrappedBorders", AtlasCells);
        HeadlessHarness.RunCase(ctx.Report, "Render.Terrain.Layers.AtlasShaderCompilesForEveryBackendFormat", () => ShaderFormats(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Render.Terrain.Layers.EightLayersRenderOnEveryBackend", () => EightLayerRender(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Render.Terrain.Layers.HeightBlendSharpensTheTransition", () => HeightBlend(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Render.Terrain.Layers.EachLayerTilesItsOwnNormalMap", () => TiledNormals(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Render.Terrain.Layers.FullSizeAtlasBuildUploadAndFrameCost", () => FullSizeCost(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Terrain.EightLayers.PaintSixthLayerSaveReopenAndCap", () => EditorEightLayers(ctx));
        RunGrass(ctx);
    }

    private static readonly float[][] LayerColors =
    [
        [1, 0, 0], [0, 1, 0], [0, 0, 1], [1, 1, 0], [0, 1, 1], [1, 0, 1], [1, 1, 1], [1, .5f, 0],
    ];

    private static void PaintAndFiles(HeadlessContext ctx)
    {
        string folder = Path.Combine(ctx.Workspace, "EightLayerFiles"); Directory.CreateDirectory(folder);
        TerrainAsset terrain = new(33, 33, 1, -16, -16, -4, 4);
        terrain.ApplyPaintBrush(0, 0, 6, 1, 2);
        Assert(!terrain.HasExtendedLayers, "Painting layers 1-4 created the second splat plane.");
        string legacy = Path.Combine(folder, "four.gterrain"); terrain.Save(legacy);
        byte[] legacyBytes = File.ReadAllBytes(legacy);
        Assert(BitConverter.ToInt32(legacyBytes, 4) == 1 && legacyBytes.Length == 36 + 33 * 33 * 6, "A four-layer terrain no longer saves the version 1 file.");
        TerrainAsset reloadedLegacy = TerrainAsset.Load(legacy);
        Assert(!reloadedLegacy.HasExtendedLayers && reloadedLegacy.SplatmapData.SequenceEqual(terrain.SplatmapData), "A version 1 terrain changed on load.");

        // The editor's per-sample blend matches the original four-channel arithmetic exactly.
        TerrainAsset reference = new(4, 1, 1, 0, 0, 0, 1);
        byte[] expected = (byte[])reference.SplatmapData.Clone();
        int channel = 1, sum = 0; float blend = .37f;
        for (int c = 0; c < 4; c++) { int value = (int)MathF.Round(float.Lerp(expected[c], c == channel ? 255 : 0, blend)); expected[c] = (byte)value; sum += value; }
        expected[channel] = (byte)Math.Clamp(expected[channel] + 255 - sum, 0, 255);
        reference.BlendLayerAt(0, channel, blend);
        Assert(reference.SplatmapData.AsSpan(0, 4).SequenceEqual(expected.AsSpan(0, 4)) && !reference.HasExtendedLayers,
            "Four-layer paint arithmetic changed.");

        terrain.ApplyPaintBrush(4, 4, 5, 1, 5);
        Assert(terrain.HasExtendedLayers && terrain.GetLayerWeight(20, 20, 5) > 200, "Layer 6 paint did not reach the second plane.");
        for (int z = 0; z < 33; z += 4) for (int x = 0; x < 33; x += 4)
        {
            int total = 0; for (int layer = 0; layer < TerrainAsset.MaximumPaintLayers; layer++) total += terrain.GetLayerWeight(x, z, layer);
            Assert(Math.Abs(total - 255) <= 8, $"Eight-layer weights at {x},{z} sum to {total}, not 255.");
        }
        Assert(terrain.GetLayerWeight(20, 20, 2) < 40, "Painting layer 6 did not lower the other layers.");
        string extended = Path.Combine(folder, "eight.gterrain"); terrain.Save(extended);
        byte[] extendedBytes = File.ReadAllBytes(extended);
        Assert(BitConverter.ToInt32(extendedBytes, 4) == 2 && extendedBytes.Length == 36 + 33 * 33 * 10, "An eight-layer terrain did not save version 2.");
        TerrainAsset reopened = TerrainAsset.Load(extended);
        Assert(reopened.HasExtendedLayers && reopened.CaptureSplatState().SequenceEqual(terrain.CaptureSplatState()), "Eight-layer paint did not survive save/load.");

        // Undo snapshots: a four-byte restore leaves layers 5-8, an eight-byte one restores them.
        byte[] state = terrain.CaptureSplatState();
        terrain.ApplyPaintBrush(4, 4, 5, 1, 0);
        terrain.RestoreState(null!, state);
        Assert(terrain.CaptureSplatState().SequenceEqual(state), "An eight-layer undo snapshot did not restore both planes.");
        File.WriteAllText(Path.Combine(ctx.Logs, "terrain-eight-layer-files.txt"),
            $"version1 bytes={legacyBytes.Length} version2 bytes={extendedBytes.Length}");
    }

    private static void AtlasCells()
    {
        const int size = 64;
        byte[] Pattern(int seed)
        {
            byte[] pixels = new byte[size * size * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)((i * 37 + seed * 101) % 251);
            return pixels;
        }
        ImageMaterialPixels image = new(size, size, Pattern(1), Pattern(2), Pattern(3));
        List<TerrainMaterialLayer> layers = [new() { Name = "Colour", Color = [.2f, .4f, .6f] }, new() { Name = "Image", Image = "x" }];
        TerrainLayerAtlas atlas = TerrainSurfaceMaterialBaker.BuildAtlas(layers, [null!, image]);
        int gutter = size / TerrainLayerAtlas.GutterDivisor, cell = size + gutter * 2;
        Assert(atlas.Columns == 2 && atlas.Rows == 1 && atlas.CellContent == size && atlas.Width == cell * 2 && atlas.Height == cell,
            $"Unexpected atlas layout {atlas.Columns}x{atlas.Rows} cell {atlas.CellContent} size {atlas.Width}x{atlas.Height}.");
        int At(int x, int y) => (y * atlas.Width + x) * 4;
        Assert(atlas.Albedo[At(5, 5)] == 51 && atlas.Albedo[At(5, 5) + 2] == 153 && atlas.Normal[At(5, 5) + 2] == 255 && atlas.Orm[At(5, 5) + 1] == 184,
            "A colour layer's atlas cell is not its flat colour, normal and ORM.");
        for (int y = 0; y < size; y += 7) for (int x = 0; x < size; x += 5)
        {
            int source = (y * size + x) * 4, target = At(cell + gutter + x, gutter + y);
            for (int channel = 0; channel < 4; channel++)
                Assert(atlas.Albedo[target + channel] == image.Albedo[source + channel] && atlas.Normal[target + channel] == image.Normal[source + channel]
                    && atlas.Orm[target + channel] == image.Orm[source + channel], $"Atlas cell pixel {x},{y} differs from the layer image.");
        }
        // The left border continues from the image's right edge.
        int wrapped = At(cell + gutter - 1, gutter + 3), edge = ((3 * size) + size - 1) * 4;
        Assert(atlas.Albedo[wrapped] == image.Albedo[edge], "The atlas border does not wrap the tile.");
    }

    private static void ShaderFormats(HeadlessContext ctx)
    {
        string root = Path.Combine(ctx.Workspace, "TerrainAtlasShaders");
        EngineShaderJob job = new("TerrainLayerAtlas", TerrainSurfaceShaders.LayerAtlasSource, "PS", GpuShaderStage.Pixel);
        Assert(TerrainSurfaceShaders.LayerAtlasSource.Contains("ComputeTerrainBlend(materialUv", StringComparison.Ordinal)
            && TerrainSurfaceShaders.LayerAtlasSource.Contains("SampleTerrainOrm(terrainBlend)", StringComparison.Ordinal)
            && TerrainSurfaceShaders.LayerAtlasSource.Contains("terrainBlend.Albedo", StringComparison.Ordinal),
            "The atlas shader lost one of its forward-shader replacements.");
        List<string> results = [];
        foreach (GpuShaderBinaryFormat format in new[] { GpuShaderBinaryFormat.Dxbc, GpuShaderBinaryFormat.Dxil, GpuShaderBinaryFormat.SpirV, GpuShaderBinaryFormat.GlslUtf8 })
        {
            ShaderCompileResult result = EngineShaderCatalog.Compile(job, format, root);
            Assert(result.Blob is { Length: > 0 }, $"The eight-layer terrain shader did not compile to {format}.");
            results.Add($"{format}: {result.Blob.Length} bytes");
        }
        File.WriteAllText(Path.Combine(ctx.Logs, "terrain-layer-atlas-shader.txt"), string.Join(Environment.NewLine, results));
    }

    private static TerrainSurfaceMaterialPixels StripePixels(int layerCount, out byte[] paint, out int width, out int height,
        TerrainSurfaceOptions? options = null)
    {
        width = layerCount; height = 2;
        int plane = width * height * 4;
        paint = new byte[layerCount > 4 ? plane * 2 : plane];
        for (int z = 0; z < height; z++) for (int x = 0; x < width; x++)
            paint[(x >= 4 ? plane : 0) + (z * width + x) * 4 + x % 4] = 255;
        List<TerrainMaterialLayer> layers = Enumerable.Range(0, layerCount)
            .Select(index => new TerrainMaterialLayer { Name = "Layer " + (index + 1), Color = LayerColors[index] }).ToList();
        return TerrainSurfaceMaterialBaker.Bake("", layers, paint, width, height, 128, 128, size: 16, options: options);
    }

    private static byte[] RenderQuad(IRenderController renderer, MeshDrawCall material, out int width, out int height, bool lit = false)
    {
        Vector3[] positions = [new(-64, 0, -64), new(64, 0, -64), new(64, 0, 64), new(-64, 0, 64)];
        Vector2[] uv = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        material.Mesh = renderer.RegisterMesh(positions.Select((position, index) => new MeshVertex
        { Position = position, Normal = Vector3.UnitY, Color = Vector4.One, UV = uv[index] }).ToArray(), [0, 1, 2, 0, 2, 3]);
        try
        {
            material.Flags = MeshDrawFlags.TerrainGround | MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull | MeshDrawFlags.NoFog;
            Mesh3DState state = Mesh3DState.Default; state.FogEnabled = false; state.ShowFloor = false; state.ShowSunVisual = false; state.LightingEnabled = lit;
            // A low sun from +X, so normals tilted toward and away from it shade differently.
            if (lit) state.LightDirection = Vector3.Normalize(new Vector3(-1, -.8f, 0));
            renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
            renderer.SetCamera3D(Matrix4x4.CreateLookAt(new(0, 120, 0), Vector3.Zero, Vector3.UnitZ),
                Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1.5f, .1f, 2000));
            renderer.BeginFrame(); renderer.Clear(.02f, .02f, .02f); renderer.DrawMesh(material); renderer.EndFrame();
            Assert(renderer.TryReadSubmittedFramePixels(out width, out height, out byte[] frame), "No terrain frame was available.");
            renderer.Present();
            return frame;
        }
        finally { renderer.ReleaseMesh(material.Mesh); }
    }

    // Which of the eight stripe colours a BGRA pixel shows, or -1.
    private static int Classify(byte[] frame, int index)
    {
        int b = frame[index], g = frame[index + 1], r = frame[index + 2];
        bool Hi(int v) => v > 180; bool Lo(int v) => v < 60; bool Mid(int v) => v is > 70 and < 200;
        if (Hi(r) && Hi(g) && Hi(b)) return 6;
        if (Hi(r) && Hi(g) && Lo(b)) return 3;
        if (Lo(r) && Hi(g) && Hi(b)) return 4;
        if (Hi(r) && Lo(g) && Hi(b)) return 5;
        if (Hi(r) && Mid(g) && Lo(b) && g < r - 50) return 7;
        if (Hi(r) && Lo(g) && Lo(b)) return 0;
        if (Lo(r) && Hi(g) && Lo(b)) return 1;
        if (Lo(r) && Lo(g) && Hi(b)) return 2;
        return -1;
    }

    private static void EightLayerRender(HeadlessContext ctx)
    {
        TerrainSurfaceMaterialPixels pixels = StripePixels(8, out byte[] paint, out int paintWidth, out int paintHeight);
        Assert(pixels.Atlas is { Columns: 4, Rows: 2 }, "Eight layers did not build a 4 x 2 layer atlas.");
        List<string> log = [];
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All.Where(item => item.Backend != RenderBackendOption.Software))
        {
            using Form host = UnattendedWindowing.NewHost(480, 320); UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(backend.Backend); renderer.Initialize(host.Handle, 480, 320);
            MeshDrawCall material = TerrainSurfaceMaterialBinding.Create(renderer, pixels, paint, paintWidth, paintHeight);
            try
            {
                Assert(material.Shader.IsValid && material.FlowMap.IsValid && material.HeightMap.IsValid && material.AuthoredTextures.Count == 3,
                    $"{backend.ShortName} lost the eight-layer shader or its splat/atlas bindings.");
                byte[] frame = RenderQuad(renderer, material, out int width, out int height);
                int[] counts = new int[8];
                for (int y = height / 3; y < height * 2 / 3; y += 2) for (int x = 0; x < width; x++)
                    if (Classify(frame, (y * width + x) * 4) is var layer and >= 0) counts[layer]++;
                SaveFrame(ctx, frame, width, height, $"terrain-eight-layers-{backend.ShortName}");
                log.Add($"{backend.ShortName}: " + string.Join(", ", counts.Select((count, index) => $"L{index + 1}={count}")));
                for (int layer = 0; layer < 8; layer++)
                    Assert(counts[layer] > 300, $"{backend.ShortName} did not draw layer {layer + 1} ({string.Join("/", counts)}).");
            }
            finally { TerrainSurfaceMaterialBinding.Release(renderer, material); }
        }
        File.WriteAllText(Path.Combine(ctx.Logs, "terrain-eight-layer-render.txt"), string.Join(Environment.NewLine, log));
    }

    private static void HeightBlend(HeadlessContext ctx)
    {
        int Mixed(TerrainSurfaceOptions options, string name)
        {
            TerrainSurfaceMaterialPixels pixels = StripePixels(2, out byte[] paint, out int paintWidth, out int paintHeight, options);
            using Form host = UnattendedWindowing.NewHost(480, 320); UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11); renderer.Initialize(host.Handle, 480, 320);
            MeshDrawCall material = TerrainSurfaceMaterialBinding.Create(renderer, pixels, paint, paintWidth, paintHeight);
            try
            {
                Assert(pixels.Atlas is not null && material.AuthoredTextures.Count == 3, "Height blending did not select the layer atlas.");
                byte[] frame = RenderQuad(renderer, material, out int width, out int height);
                SaveFrame(ctx, frame, width, height, name);
                int mixed = 0, y = height / 2;
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;
                    // Red and green together (the grey sky has blue as well).
                    if (frame[index + 2] > 40 && frame[index + 1] > 40 && frame[index] < 60) mixed++;
                }
                return mixed;
            }
            finally { TerrainSurfaceMaterialBinding.Release(renderer, material); }
        }
        int linear = Mixed(new TerrainSurfaceOptions(TiledLayerMaps: true), "terrain-blend-linear-dx11");
        int sharp = Mixed(new TerrainSurfaceOptions(TiledLayerMaps: true, HeightBlendSharpness: 1), "terrain-blend-height-dx11");
        File.WriteAllText(Path.Combine(ctx.Logs, "terrain-height-blend.txt"), $"mixed pixels on the middle row: linear={linear} height={sharp}");
        Assert(linear > 60 && sharp * 4 < linear, $"Height blending did not narrow the red/green transition (linear {linear}, height {sharp}).");
    }

    private static void TiledNormals(HeadlessContext ctx)
    {
        // One layer whose normal map leans left/right in vertical bands, repeated 16 times across the
        // terrain. The original surface bakes normals into one small whole-terrain map and loses it.
        const int size = 64;
        byte[] Fill(Func<int, byte[]> pixel)
        {
            byte[] pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) pixel(x).CopyTo(pixels, (y * size + x) * 4);
            return pixels;
        }
        ImageMaterialPixels image = new(size, size, Fill(_ => [180, 180, 180, 255]),
            Fill(x => x < size / 2 ? [218, 128, 218, 255] : [38, 128, 218, 255]), Fill(_ => [255, 200, 0, 255]));
        List<TerrainMaterialLayer> layers = [new() { Name = "Ridges", Image = "Ridges", Tiling = 16 }];
        byte[] flat = Enumerable.Repeat(new byte[] { 180, 180, 180, 255 }, 4).SelectMany(pixel => pixel).ToArray();
        byte[] flatNormal = Enumerable.Repeat(new byte[] { 128, 128, 255, 255 }, 4).SelectMany(pixel => pixel).ToArray();
        byte[] orm = Enumerable.Repeat(new byte[] { 255, 200, 0, 255 }, 4).SelectMany(pixel => pixel).ToArray();
        byte[] paint = Enumerable.Repeat(new byte[] { 255, 0, 0, 0 }, 4).SelectMany(pixel => pixel).ToArray();
        int Bands(TerrainSurfaceOptions options, string name)
        {
            TerrainSurfaceMaterialPixels pixels = new(2, flat, flatNormal, orm, layers, [image], 128, 128)
            {
                Options = options,
                Atlas = options.UsesLayerAtlas(1) ? TerrainSurfaceMaterialBaker.BuildAtlas(layers, [image]) : null,
            };
            using Form host = UnattendedWindowing.NewHost(480, 320); UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11); renderer.Initialize(host.Handle, 480, 320);
            MeshDrawCall material = TerrainSurfaceMaterialBinding.Create(renderer, pixels, paint, 2, 2);
            try
            {
                byte[] frame = RenderQuad(renderer, material, out int width, out int height, lit: true);
                SaveFrame(ctx, frame, width, height, name);
                int y = height / 2, bands = 0; bool? bright = null;
                for (int x = width / 4; x < width * 3 / 4; x++)
                {
                    int index = (y * width + x) * 4, luminance = frame[index] + frame[index + 1] + frame[index + 2];
                    int left = ((y * width + x - 1) * 4), before = frame[left] + frame[left + 1] + frame[left + 2];
                    if (Math.Abs(luminance - before) < 6) continue;
                    bool rising = luminance > before;
                    if (bright != rising) { bands++; bright = rising; }
                }
                return bands;
            }
            finally { TerrainSurfaceMaterialBinding.Release(renderer, material); }
        }
        int baked = Bands(TerrainSurfaceOptions.Default, "terrain-normals-baked-dx11");
        int tiled = Bands(new TerrainSurfaceOptions(TiledLayerMaps: true), "terrain-normals-tiled-dx11");
        File.WriteAllText(Path.Combine(ctx.Logs, "terrain-tiled-normals.txt"), $"shading bands across the middle half: baked={baked} tiled={tiled}");
        Assert(tiled >= 8 && baked <= 2, $"Per-layer normal maps did not tile across the terrain (baked {baked}, tiled {tiled}).");
    }

    // Full-size maps: eight 1024 layers become three 4608 x 2304 atlases with mipmaps. Logs the
    // bake, the upload and the frame cost of the eight-layer shader against the four-layer one.
    private static void FullSizeCost(HeadlessContext ctx)
    {
        const int size = 1024;
        Random random = new(7);
        ImageMaterialPixels Layer(int index)
        {
            byte[] albedo = new byte[size * size * 4], normal = new byte[albedo.Length], orm = new byte[albedo.Length];
            random.NextBytes(albedo); random.NextBytes(normal); random.NextBytes(orm);
            return new(size, size, albedo, normal, orm);
        }
        ImageMaterialPixels[] images = Enumerable.Range(0, 8).Select(Layer).ToArray();
        List<TerrainMaterialLayer> layers = Enumerable.Range(0, 8).Select(index => new TerrainMaterialLayer { Name = "L" + index, Image = "x", Tiling = 64 }).ToList();
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        TerrainLayerAtlas atlas = TerrainSurfaceMaterialBaker.BuildAtlas(layers, images);
        long bakeMs = watch.ElapsedMilliseconds;
        Assert(atlas.Width == 4608 && atlas.Height == 2304, $"Eight 1024 layers made a {atlas.Width} x {atlas.Height} atlas.");
        const int paintSize = 64;
        byte[] paint = new byte[paintSize * paintSize * 8];
        for (int i = 0; i < paintSize * paintSize; i++) { paint[i * 4 + i % 4] = 128; paint[paintSize * paintSize * 4 + i * 4 + (i / 7) % 4] = 127; }
        byte[] flat = Enumerable.Repeat(new byte[] { 128, 128, 255, 255 }, 4).SelectMany(pixel => pixel).ToArray();
        TerrainSurfaceMaterialPixels eight = new(2, flat, flat, flat, layers, images, 2048, 2048) { Atlas = atlas, Options = new(true) };
        TerrainSurfaceMaterialPixels four = new(2, flat, flat, flat, layers.Take(4).ToList(), images.Take(4).ToArray(), 2048, 2048);
        using Form host = UnattendedWindowing.NewHost(1280, 720); UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11); renderer.Initialize(host.Handle, 1280, 720);
        watch.Restart();
        MeshDrawCall eightMaterial = TerrainSurfaceMaterialBinding.Create(renderer, eight, paint, paintSize, paintSize);
        long uploadMs = watch.ElapsedMilliseconds;
        MeshDrawCall fourMaterial = TerrainSurfaceMaterialBinding.Create(renderer, four, paint.AsSpan(0, paintSize * paintSize * 4).ToArray(), paintSize, paintSize);
        double Frames(MeshDrawCall material)
        {
            Vector3[] positions = [new(-1024, 0, -1024), new(1024, 0, -1024), new(1024, 0, 1024), new(-1024, 0, 1024)];
            Vector2[] uv = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            material.Mesh = renderer.RegisterMesh(positions.Select((position, index) => new MeshVertex
            { Position = position, Normal = Vector3.UnitY, Color = Vector4.One, UV = uv[index] }).ToArray(), [0, 1, 2, 0, 2, 3]);
            try
            {
                material.Flags = MeshDrawFlags.TerrainGround | MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull | MeshDrawFlags.NoFog;
                Mesh3DState state = Mesh3DState.Default; state.FogEnabled = false; state.ShowFloor = false; state.ShowSunVisual = false;
                renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                // A walker's view across the ground: near detail and kilometres of far terrain.
                renderer.SetCamera3D(Matrix4x4.CreateLookAt(new(0, 2, -1000), new(0, 0, -900), Vector3.UnitY),
                    Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1280f / 720f, .1f, 5000));
                void Frame() { renderer.BeginFrame(); renderer.Clear(.02f, .02f, .02f); renderer.DrawMesh(material); renderer.EndFrame(); renderer.Present(); }
                for (int i = 0; i < 10; i++) Frame();
                renderer.TryReadSubmittedFramePixels(out _, out _, out _);
                System.Diagnostics.Stopwatch frames = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 120; i++) Frame();
                renderer.TryReadSubmittedFramePixels(out _, out _, out _);
                return frames.Elapsed.TotalMilliseconds / 120;
            }
            finally { renderer.ReleaseMesh(material.Mesh); }
        }
        double fourMs = Frames(fourMaterial), eightMs = Frames(eightMaterial);
        TerrainSurfaceMaterialBinding.Release(renderer, eightMaterial); TerrainSurfaceMaterialBinding.Release(renderer, fourMaterial);
        string report = $"atlas {atlas.Width}x{atlas.Height}: build {bakeMs} ms, upload with mips {uploadMs} ms; 1280x720 DX11 frame: four-layer {fourMs:F2} ms, eight-layer atlas {eightMs:F2} ms";
        File.WriteAllText(Path.Combine(ctx.Logs, "terrain-eight-layer-cost.txt"), report);
        Console.WriteLine(report);
        Assert(bakeMs < 10_000 && uploadMs < 10_000, report);
    }

    private static void EditorEightLayers(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "EightLayerEditor"), "Eight layer paint");
        ResourceService resources = new(project);
        string terrainFile = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Island");
        TerrainAsset flat = new(33, 33, .5f, -8, -8, -1, 1);
        flat.Save(terrainFile + ".gterrain");
        using (TerrainEditorControl author = new(terrainFile, project.RootPath))
        {
            author.Viewport.BackendOverride = RenderBackendOption.SilkNetDx11;
            while (author.PaintLayerCount < TerrainAsset.MaximumPaintLayers) author.AddPaintLayer();
            author.AddPaintLayer();
            Assert(author.PaintLayerCount == TerrainAsset.MaximumPaintLayers, $"The editor allowed {author.PaintLayerCount} paint layers.");
            author.SelectPaintLayer(5);
            author.SetPaintSelection([new(0, -8), new(8, -8), new(8, 8), new(0, 8)]);
            author.FillPaintSelection();
            author.SetLayerSurfaceOptions(tiledLayerMaps: false, heightBlendSharpness: .6f);
            author.Save();
        }
        TerrainAsset saved = TerrainAsset.Load(terrainFile + ".gterrain");
        Assert(saved.HasExtendedLayers && saved.GetLayerWeight(28, 16, 5) == 255 && saved.GetLayerWeight(28, 16, 0) == 0
            && saved.GetLayerWeight(4, 16, 0) == 255 && saved.GetLayerWeight(4, 16, 5) == 0,
            "The editor's layer-6 fill did not survive save/reopen.");
        (List<TerrainMaterialLayer> layers, TerrainSurfaceOptions options) = TerrainSurfaceMaterialBaker.LoadSurface(terrainFile);
        Assert(layers.Count == 8 && Math.Abs(options.HeightBlendSharpness - .6f) < .001f && options.UsesLayerAtlas(layers.Count),
            "The terrain document lost its eight layers or blending options.");
        using TerrainEditorControl reopened = new(terrainFile, project.RootPath);
        Assert(reopened.PaintLayerCount == 8, "Reopening the terrain lost paint layers.");
    }

    private static void SaveFrame(HeadlessContext ctx, byte[] frame, int width, int height, string name)
    {
        using Bitmap bitmap = new(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { System.Runtime.InteropServices.Marshal.Copy(frame, 0, data.Scan0, frame.Length); } finally { bitmap.UnlockBits(data); }
        bitmap.Save(Path.Combine(ctx.Captures, name + ".png"));
        ctx.Report.Images.Add(new ImageResult(name, name + ".png", width, height, 0, 0));
    }

    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
