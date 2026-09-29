using System.Collections;
using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Application.Headless.Suites;

internal static class TerrainMaterialRuntimeSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Materials.SavedPaintPbrAndLiveImageMatchAuthoring", () => Check(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Materials.TiledImagesKeepDetailBeyondSurfaceBake", () => FineDetail(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Render.Software.Materials.LinearSamplingSmoothsBakedTerrain", () => SoftwareFiltering(ctx));
    }

    private static void SoftwareFiltering(HeadlessContext ctx)
    {
        using Form host = UnattendedWindowing.NewHost(480, 320); UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.Software); renderer.Initialize(host.Handle, 480, 320);
        byte[] color = [255, 0, 0, 255, 0, 0, 255, 255, 255, 0, 0, 255, 0, 0, 255, 255];
        byte[] normal = Enumerable.Repeat(new byte[] { 128, 128, 255, 255 }, 4).SelectMany(pixel => pixel).ToArray();
        byte[] orm = Enumerable.Repeat(new byte[] { 255, 184, 0, 255 }, 4).SelectMany(pixel => pixel).ToArray();
        TerrainSurfaceMaterialPixels pixels = new(2, color, normal, orm, [new()], [], 128, 128);
        MeshDrawCall material = TerrainSurfaceMaterialBinding.Create(renderer, pixels, color, 2, 2);
        Vector3[] positions = [new(-64, 0, -64), new(64, 0, -64), new(64, 0, 64), new(-64, 0, 64)];
        Vector2[] uv = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        material.Mesh = renderer.RegisterMesh(positions.Select((position, index) => new MeshVertex
        { Position = position, Normal = Vector3.UnitY, Color = Vector4.One, UV = uv[index] }).ToArray(), [0, 1, 2, 0, 2, 3]);
        try
        {
            material.Flags = MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull | MeshDrawFlags.NoFog;
            Mesh3DState state = Mesh3DState.Default; state.FogEnabled = false; state.ShowFloor = false; state.ShowSunVisual = false; state.LightingEnabled = false;
            renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
            renderer.SetCamera3D(Matrix4x4.CreateLookAt(new(0, 60, 0), Vector3.Zero, Vector3.UnitZ),
                Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1.5f, .1f, 2000));
            renderer.BeginFrame(); renderer.Clear(.02f, .02f, .02f); renderer.DrawMesh(material); renderer.EndFrame();
            Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] frame), "No Software material frame was available.");
            int blended = 0; HashSet<int> shades = new();
            for (int index = 0; index < frame.Length; index += 4)
                if (frame[index] > 40 && frame[index + 2] > 40)
                { blended++; shades.Add(frame[index] | frame[index + 2] << 8); }
            using Bitmap bitmap = new(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try { System.Runtime.InteropServices.Marshal.Copy(frame, 0, data.Scan0, frame.Length); } finally { bitmap.UnlockBits(data); }
            Save(ctx, bitmap, "terrain-software-smooth-material");
            Assert(blended > 20_000 && shades.Count > 100, $"Software ignored linear material sampling ({blended} blended pixels, {shades.Count} shades).");
            renderer.Present();
        }
        finally { renderer.ReleaseMesh(material.Mesh); TerrainSurfaceMaterialBinding.Release(renderer, material); }
    }

    private static void FineDetail(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "FineTerrainDetail"), "Tiled terrain detail");
        ResourceService resources = new(project);
        string file = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Tiled stones");
        ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(file).Document, file, ImageDocumentAccess.Editor);
        ImageWorkspace workspace = ImageWorkspace.CreateBlank(32, 32, Color.Red);
        byte[] source = workspace.Frames[0].Layers[0].Pixels;
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            int index = (y * 32 + x) * 4; bool red = (x / 4 + y / 4) % 2 == 0;
            source[index] = red ? (byte)255 : (byte)0; source[index + 1] = 0; source[index + 2] = red ? (byte)0 : (byte)255; source[index + 3] = 255;
        }
        ImageWorkspaceStorage.Save(session, workspace);
        byte[] paint = [255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0];
        TerrainSurfaceMaterialPixels pixels = TerrainSurfaceMaterialBaker.Bake(project.RootPath,
            Enumerable.Range(0, 4).Select(_ => new TerrainMaterialLayer { Image = "Tiled stones", Addressing = "Tile", Tiling = 4 }).ToList(),
            paint, 2, 2, 128, 128, size: 2);
        Assert(pixels.Images[0].Albedo[0] == 255 && pixels.Images[0].Albedo[1] == 0 && pixels.Images[0].Albedo[4 * 5 + 2] == 255,
            "The saved Image's red/blue detail changed before native binding.");
        Assert(Enumerable.Range(0, 4).All(index => pixels.Color[index * 4 + 2] == 0), "The fixture's tiny whole-surface bake unexpectedly retained blue detail.");
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All.Where(item => item.Backend != RenderBackendOption.Software))
        {
            using Form host = UnattendedWindowing.NewHost(480, 320); UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(backend.Backend); renderer.Initialize(host.Handle, 480, 320);
            MeshDrawCall material = TerrainSurfaceMaterialBinding.Create(renderer, pixels, paint, 2, 2);
            Assert(material.Shader.IsValid && material.FlowMap.IsValid && material.AuthoredTextures.Count == 3, "Tiled layers lost their native shader/paint bindings.");
            Vector3[] positions = [new(-64, 0, -64), new(64, 0, -64), new(64, 0, 64), new(-64, 0, 64)];
            Vector2[] uv = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            material.Mesh = renderer.RegisterMesh(positions.Select((position, index) => new MeshVertex
            { Position = position, Normal = Vector3.UnitY, Color = Vector4.One, UV = uv[index] }).ToArray(), [0, 1, 2, 0, 2, 3]);
            try
            {
                material.Flags = MeshDrawFlags.TerrainGround | MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull | MeshDrawFlags.NoFog;
                Mesh3DState state = Mesh3DState.Default; state.FogEnabled = false; state.ShowFloor = false; state.ShowSunVisual = false; state.LightingEnabled = false;
                renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(Matrix4x4.CreateLookAt(new(0, 60, 0), Vector3.Zero, Vector3.UnitZ),
                    Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1.5f, .1f, 2000));
                for (int layer = 0; layer < 4; layer++)
                {
                    byte[] updatedPaint = new byte[paint.Length];
                    for (int index = 0; index < updatedPaint.Length; index += 4) updatedPaint[index + layer] = 128;
                    TerrainSurfaceMaterialBinding.UpdatePaint(renderer, material, pixels, updatedPaint, 2, 2);
                    renderer.BeginFrame(); renderer.Clear(.02f, .02f, .02f); renderer.DrawMesh(material); renderer.EndFrame();
                    Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] frame), "No detailed terrain frame was available.");
                    int red = 0, blue = 0;
                    for (int index = 0; index < frame.Length; index += 4)
                    { if (frame[index] > 90 && frame[index] > frame[index + 2] * 2) blue++; if (frame[index + 2] > 90 && frame[index + 2] > frame[index] * 2) red++; }
                    using Bitmap bitmap = new(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var data = bitmap.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
                    try { System.Runtime.InteropServices.Marshal.Copy(frame, 0, data.Scan0, frame.Length); } finally { bitmap.UnlockBits(data); }
                    Save(ctx, bitmap, $"terrain-fine-detail-{backend.ShortName}-layer{layer + 1}");
                    Assert(red > 20_000 && blue > 20_000, $"{backend.ShortName} layer {layer + 1} lost the saved tiled Image ({red}/{blue}).");
                    renderer.Present();
                }
            }
            finally { renderer.ReleaseMesh(material.Mesh); TerrainSurfaceMaterialBinding.Release(renderer, material); }
        }
    }

    private static void Check(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "TerrainMaterialRuntime"), "Painted material round trip");
        ResourceService resources = new(project);
        string terrainFile = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Painted field");
        string roomFile = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Painted room");
        (string File, ImageDocumentSession Session, ImageWorkspace Workspace) Image(string name, Color color, byte roughness)
        {
            string file = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, name);
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(file).Document, file, ImageDocumentAccess.Editor);
            ImageWorkspace workspace = ImageWorkspace.CreateBlank(16, 16, color);
            workspace.AddLayer("Normal").Channel = ImageMaterialChannel.Normal;
            workspace.AddLayer("Roughness").Channel = ImageMaterialChannel.Roughness;
            workspace.AddLayer("AO").Channel = ImageMaterialChannel.Occlusion;
            foreach (ImageLayerBuffer layer in workspace.Frames[0].Layers)
            {
                if (layer.Channel == ImageMaterialChannel.Color) continue;
                byte[] pixels = layer.Pixels;
                for (int index = 0; index < pixels.Length; index += 4)
                {
                    pixels[index] = layer.Channel == ImageMaterialChannel.Normal ? (byte)128 : layer.Channel == ImageMaterialChannel.Roughness ? roughness : (byte)220;
                    pixels[index + 1] = layer.Channel == ImageMaterialChannel.Normal ? (byte)128 : pixels[index];
                    pixels[index + 2] = layer.Channel == ImageMaterialChannel.Normal ? (byte)255 : pixels[index]; pixels[index + 3] = 255;
                }
            }
            ImageWorkspaceStorage.Save(session, workspace); return (file, session, workspace);
        }
        var soil = Image("Warm soil", Color.Red, 60); var stones = Image("Blue stones", Color.Blue, 180);
        TerrainAsset terrainAsset = new(33, 33, .25f, -4, -4, -1, 1);
        for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) terrainAsset.SetHeight(x, z, 0);
        terrainAsset.Save(terrainFile + ".gterrain");
        RoomAsset room = RoomAsset.Create("Painted room", RoomDimension.ThreeD);
        room.Environment.DynamicSky = false;
        room.Nodes.Add(new RoomNode { Name = "Painted field", Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = ResourceNames.Name(project.RootPath, terrainFile, ResourceType.Terrain) } });
        RoomAssetLoader.Save(room, roomFile);
        using TerrainEditorControl author = new(terrainFile, project.RootPath);
        author.Viewport.BackendOverride = RenderBackendOption.SilkNetDx11;
        author.AssignLayerImage(0, "Warm soil", 1); author.AssignLayerImage(1, "Blue stones", 1);
        author.SelectPaintLayer(1); author.SetPaintSelection([new(0, -5), new(5, -5), new(5, 5), new(0, 5)]); author.FillPaintSelection(); author.Save();
        AssetDependencyGraph graph = new(project.RootPath); graph.Refresh();
        Assert(graph.DependsOn(terrainFile, soil.File) && graph.DependsOn(roomFile, soil.File),
            "Terrain/Room dependency tracking omitted its authored Image material layer.");
        using Form authorHost = UnattendedWindowing.NewHost(1360, 880);
        author.Dock = DockStyle.Fill; authorHost.Controls.Add(author); ThemeService.Apply(authorHost); UnattendedWindowing.ShowWithoutFocus(authorHost);
        Configure(author.Viewport);
        using Bitmap editorImage = Ready(author.Viewport, () => author.MaterialPreviewRevision > 0, green: false);
        Save(ctx, editorImage, "terrain-material-author-red-blue");
        TerrainSurfaceMaterialPixels authoredPixels = (TerrainSurfaceMaterialPixels)typeof(TerrainEditorControl)
            .GetField("_surfacePixels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(author)!;
        using TerrainEditorControl reopened = new(terrainFile, project.RootPath);
        TerrainAsset saved = TerrainAsset.Load(terrainFile + ".gterrain");
        Assert(saved.GetSplat(2, 16).R == 255 && saved.GetSplat(30, 16).G == 255,
            "The editor's painted region did not survive save/reopen.");
        Assert(TerrainSurfaceMaterialBaker.LoadLayers(terrainFile)[1].Image == "Blue stones", "The painted Image layer was not saved.");

        using RoomEditorControl roomEditor = new(roomFile, project.RootPath);
        roomEditor.Viewport.BackendOverride = RenderBackendOption.SilkNetDx11;
        using Form roomHost = UnattendedWindowing.NewHost(1360, 880);
        roomEditor.Dock = DockStyle.Fill; roomHost.Controls.Add(roomEditor); ThemeService.Apply(roomHost); UnattendedWindowing.ShowWithoutFocus(roomHost);
        Configure(roomEditor.Viewport);
        using Bitmap roomImage = Ready(roomEditor.Viewport, () => roomEditor.AuthoredTerrainDraws.Any(draw => draw.NormalMap.IsValid && draw.OrmMap.IsValid), green: false);
        Save(ctx, roomImage, "terrain-material-room-red-blue");

        using RoomTerrainSubsystem runtime = new(project.RootPath, room, null!);
        using EditorViewport3D runtimeView = new() { BackendOverride = RenderBackendOption.SilkNetDx11 };
        using Form runtimeHost = UnattendedWindowing.NewHost(800, 620);
        runtimeHost.Controls.Add(runtimeView); UnattendedWindowing.ShowWithoutFocus(runtimeHost); Configure(runtimeView);
        MeshDrawCall[] draws = new MeshDrawCall[64]; int drawCount = 0;
        runtimeView.DrawScene += renderer =>
        {
            drawCount = 0; runtime.SubmitPreviewMeshes(runtimeView.Camera.Eye, runtimeView.ViewMatrix * runtimeView.ProjectionMatrix, draws, ref drawCount, renderer);
            for (int index = 0; index < drawCount; index++) renderer.DrawMesh(draws[index]);
        };
        using Bitmap runtimeImage = Ready(runtimeView, () => runtime.AuthoredMaterialGroundCount == 1, green: false);
        Save(ctx, runtimeImage, "terrain-material-runtime-red-blue");
        IList entries = (IList)typeof(RoomTerrainSubsystem).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        object state = entries[0]!.GetType().GetField("Material")!.GetValue(entries[0])!;
        TerrainSurfaceMaterialPixels runtimePixels = (TerrainSurfaceMaterialPixels)state.GetType().GetField("Pixels")!.GetValue(state)!;
        Assert(runtimePixels.Color.SequenceEqual(authoredPixels.Color) && runtimePixels.Normal.SequenceEqual(authoredPixels.Normal)
            && runtimePixels.Orm.SequenceEqual(authoredPixels.Orm), "Editor and runtime bake different pixels for the saved terrain paint/materials.");
        int left = (1024 * authoredPixels.Size + 128) * 4, right = (1024 * authoredPixels.Size + 1900) * 4;
        Assert(runtimePixels.Orm[left] == 220 && runtimePixels.Orm[left + 1] == 60 && runtimePixels.Orm[right + 1] == 180,
            "Terrain paint blended albedo but omitted the authored AO/roughness channels.");
        Assert(drawCount == 1 && draws[0].NormalMap.IsValid && draws[0].OrmMap.IsValid, "Gameplay submitted missing or duplicate PBR terrain chunks.");

        byte[] albedo = soil.Workspace.Frames[0].Layers.Single(layer => layer.Channel == ImageMaterialChannel.Color).Pixels;
        for (int index = 0; index < albedo.Length; index += 4) { albedo[index] = 0; albedo[index + 1] = 255; }
        ImageWorkspaceStorage.Save(soil.Session, soil.Workspace); File.SetLastWriteTimeUtc(soil.File, DateTime.UtcNow.AddSeconds(2));
        using Bitmap liveAuthor = Ready(author.Viewport, () => true, green: true); Save(ctx, liveAuthor, "terrain-material-author-live-green-blue");
        using Bitmap liveRoom = Ready(roomEditor.Viewport, () => true, green: true); Save(ctx, liveRoom, "terrain-material-room-live-green-blue");
        using Bitmap liveRuntime = Ready(runtimeView, () => true, green: true); Save(ctx, liveRuntime, "terrain-material-runtime-live-green-blue");
        TextureHandle retained = draws[0].Texture;
        string validImage = File.ReadAllText(soil.File); File.WriteAllText(soil.File, "{"); File.SetLastWriteTimeUtc(soil.File, DateTime.UtcNow.AddSeconds(3));
        DateTime waitUntil = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < waitUntil) { using Bitmap? frame = runtimeView.CaptureFrame(1); Thread.Sleep(25); }
        Assert(draws[0].Texture.Equals(retained), "An invalid live Image replacement discarded the valid terrain material.");
        File.WriteAllText(soil.File, validImage); File.SetLastWriteTimeUtc(soil.File, DateTime.UtcNow.AddSeconds(4));
        using Bitmap repaired = Ready(runtimeView, () => !draws[0].Texture.Equals(retained), green: true);
        Save(ctx, repaired, "terrain-material-runtime-repaired");
        runtimeView.BackendOverride = RenderBackendOption.Software;
        using Bitmap software = Ready(runtimeView, () => runtime.AuthoredMaterialGroundCount == 1, green: true);
        Save(ctx, software, "terrain-material-runtime-rebound-software");
        runtimeView.BackendOverride = RenderBackendOption.SilkNetDx11;
        using Bitmap returned = Ready(runtimeView, () => runtime.AuthoredMaterialGroundCount == 1, green: true);
        Save(ctx, returned, "terrain-material-runtime-returned-dx11");

        static void Configure(EditorViewport3D viewport)
        {
            viewport.Host.TimerEnabled = false; viewport.Camera.Target = Vector3.Zero;
            viewport.FloorHeight = -.1f;
            viewport.Camera.Distance = 10; viewport.Camera.Pitch = -.65f; viewport.Camera.Yaw = 0;
        }
    }

    private static Bitmap Ready(EditorViewport3D viewport, Func<bool> stateReady, bool green)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        do
        {
            System.Windows.Forms.Application.DoEvents(); Bitmap? frame = viewport.CaptureFrame(2);
            if (frame is not null)
            {
                int first = 0, blue = 0;
                for (int y = 65; y < frame.Height - 50; y += 3) for (int x = 40; x < frame.Width - 40; x += 3)
                {
                    Color color = frame.GetPixel(x, y);
                    if (color.B > 30 && color.B > color.R * 2 && color.B > color.G * 2) blue++;
                    if (green ? color.G > 30 && color.G > color.R * 2 && color.G > color.B * 2
                        : color.R > 30 && color.R > color.G * 2 && color.R > color.B * 2) first++;
                }
                if (stateReady() && first > 50 && blue > 50 && (first + blue) * 9 > frame.Width * frame.Height * .08) return frame;
                frame.Dispose();
            }
            Thread.Sleep(25);
        } while (DateTime.UtcNow < deadline);
        throw new InvalidDataException("Authored terrain material pixels did not reach the native scene: " + (green ? "green/blue" : "red/blue"));
    }
    private static void Save(HeadlessContext ctx, Bitmap bitmap, string name)
    {
        bitmap.Save(Path.Combine(ctx.Captures, name + ".png"));
        ctx.Report.Images.Add(new ImageResult(name, name + ".png", bitmap.Width, bitmap.Height, 0, 0));
    }
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
