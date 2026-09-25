using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;
using Newtonsoft.Json;

namespace Genesis.Application.Headless.Suites;

internal static class RoomTerrainPreviewSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Room.AuthoredTerrainPreview");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Terrain.ChunkedSplatMaterialsNatureAndPlacement", () =>
        {
            var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomTerrainPreview"), "Terrain preview");
            var resources = new ResourceService(project);
            string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Terrain room");
            string terrainPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Large painted terrain");
            string firstTexture = Path.Combine(resources.AssetsRoot, "Terrain-green.png");
            string secondTexture = Path.Combine(resources.AssetsRoot, "Terrain-red.png");
            MakeTexture(firstTexture, Color.ForestGreen, Color.PaleGreen);
            MakeTexture(secondTexture, Color.Firebrick, Color.Orange);
            TerrainAsset terrain = new(513, 257, 0.0625f, -16f, -8f, -2f, 5f);
            for (int z = 0; z < terrain.ResolutionZ; z++)
            for (int x = 0; x < terrain.ResolutionX; x++)
            {
                terrain.SetHeight(x, z, 0.6f + 0.45f * MathF.Sin(x * 0.015f) + z * 0.002f);
                if (x < 128) terrain.SetSplat(x, z, 255, 0, 0, 0);
                else if (x < 256) terrain.SetSplat(x, z, 0, 255, 0, 0);
                else if (x < 384) terrain.SetSplat(x, z, 0, 0, 255, 0);
                else terrain.SetSplat(x, z, 0, 0, 0, 255);
            }
            terrain.Save(terrainPath + ".gterrain");
            TerrainNatureDocument nature = new()
            {
                Paths = [new TerrainPathDefinition { Name = "Preview trail", Width = 0.7f,
                    Points = [new(-12f, 0, -3f), new(0, 0, -2f), new(12f, 0, 4f)] }],
                WaterBodies = [new TerrainWaterDefinition { Name = "Preview pond", Center = new(5f, 0, -3f),
                    SizeX = 5f, SizeZ = 3f, SurfaceHeight = 1.8f, SimulationEnabled = false }],
            };
            TerrainNatureSerializer.Save(terrainPath, nature);
            RoomAsset room = RoomAsset.Create("Terrain parity", RoomDimension.ThreeD);
            RoomNode terrainNode = new()
            {
                Name = "Painted hillside", Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id,
                Terrain = new RoomTerrainData { Asset = Relative(terrainPath), Albedo = Relative(firstTexture), UvScale = 9f },
                Transform = new RoomTransform { X = 8f, Y = 3f, Z = -5f,
                    RotationX = 4f, RotationY = 27f, RotationZ = -3f, ScaleX = 1.2f, ScaleY = 0.8f, ScaleZ = 0.75f },
            };
            room.Nodes.Add(terrainNode);
            File.WriteAllText(roomPath, JsonConvert.SerializeObject(room));
            using var editor = new RoomEditorControl(roomPath, project.RootPath);
            using var host = GateSuite.NewHost(1400, 880);
            host.Controls.Add(editor);
            ThemeService.Apply(host);
            editor.ViewMode3D = true;
            editor.SetGridVisible(false);
            editor.Viewport.Camera.Target = new Vector3(8f, 4f, -5f);
            editor.Viewport.Camera.Distance = 42f;
            editor.Viewport.Camera.Pitch = -0.58f;
            editor.Viewport.Camera.Yaw = 0.3f;
            IRenderController? renderer = null;
            editor.Viewport.DrawScene += value => renderer = value;
            GateSuite.ShowHost(host);
            Warm(editor);
            Assert(string.IsNullOrEmpty(editor.TerrainPreviewError), editor.TerrainPreviewError);
            Assert(renderer is not null, "The Room viewport never rendered.");
            MeshDrawCall[] ground = Ground(editor);
            Assert(ground.Length == 8, "Large terrain must submit all eight ushort-safe chunks.");
            Assert(ground.All(draw => draw.Texture.IsValid), "The selected terrain albedo was not bound.");
            Assert(editor.AuthoredTerrainDraws.Any(draw => (draw.Flags & MeshDrawFlags.Water) != 0),
                "The Room preview dropped the terrain's authored water.");
            Assert(editor.AuthoredTerrainDraws.Count == 10, "The authored trail or water surface is missing.");
            CompareRuntime(editor, renderer!);
            Editor3DInspectionSuite.Capture(ctx, host, "room-large-terrain-painted-nature");

            RoomNode liveNode = editor.Room.Nodes.Single(node => node.Kind == RoomNodeKind.Terrain);
            int[] originalMeshes = ground.Select(draw => draw.Mesh.Id).ToArray();
            liveNode.Transform.X += 4f;
            liveNode.Transform.RotationY = -35f;
            liveNode.Transform.ScaleX = -0.9f;
            Warm(editor);
            Assert(Ground(editor).Select(draw => draw.Mesh.Id).SequenceEqual(originalMeshes),
                "Moving terrain unnecessarily recreated the authored meshes.");
            CompareRuntime(editor, renderer!);
            int previousTexture = Ground(editor)[0].Texture.Id;
            liveNode.Terrain.Albedo = Relative(secondTexture);
            Warm(editor);
            Assert(Ground(editor)[0].Texture.Id != previousTexture && Ground(editor)[0].Texture.IsValid,
                "Changing the terrain albedo kept the previous resource binding.");
            Editor3DInspectionSuite.Capture(ctx, host, "room-terrain-mirrored-material-change");
            editor.Room.Layers[0].Enabled = false;
            Warm(editor);
            Assert(editor.AuthoredTerrainDraws.Count == 0, "Hidden terrain layers still submitted geometry.");
            editor.Room.Layers[0].Enabled = true;
            Warm(editor);
            Assert(Ground(editor).Length == 8, "Showing the terrain layer did not restore every chunk.");
            editor.Save();
            using var reopened = new RoomEditorControl(roomPath, project.RootPath);
            RoomNode saved = reopened.Room.Nodes.Single(node => node.Kind == RoomNodeKind.Terrain);
            Assert(saved.Terrain.Albedo == Relative(secondTexture) && saved.Transform.ScaleX == -0.9f,
                "Terrain material or placement changed after save/reopen.");
            TerrainAsset savedTerrain = TerrainAsset.Load(terrainPath + ".gterrain");
            Assert(savedTerrain.HeightsData.SequenceEqual(terrain.HeightsData)
                && savedTerrain.SplatmapData.SequenceEqual(terrain.SplatmapData),
                "Room preview or save modified source terrain geometry/paint.");

            string brokenTerrain = Path.Combine(resources.AssetsRoot, "Broken.gterrain");
            File.WriteAllBytes(brokenTerrain, [0, 1, 2, 3]);
            liveNode.Terrain.Asset = Relative(brokenTerrain);
            Warm(editor);
            Assert(!string.IsNullOrWhiteSpace(editor.TerrainPreviewError) && editor.AuthoredTerrainDraws.Count == 0,
                "An unreadable terrain did not become an in-app preview error.");
            liveNode.Terrain.Asset = Relative(terrainPath);
            Warm(editor);
            Assert(string.IsNullOrEmpty(editor.TerrainPreviewError) && Ground(editor).Length == 8,
                "Choosing a valid terrain after a load failure did not recover the preview.");

            string Relative(string path) => Path.GetRelativePath(project.RootPath, path).Replace('\\', '/');

            void CompareRuntime(RoomEditorControl current, IRenderController activeRenderer)
            {
                using var runtime = new RoomTerrainSubsystem(project.RootPath, current.Room, null!);
                MeshDrawCall[] expected = new MeshDrawCall[runtime.GetMeshDrawCapacity(activeRenderer)];
                int count = 0;
                runtime.SubmitPreviewMeshes(current.Viewport.Camera.Eye,
                    current.Viewport.ViewMatrix * current.Viewport.ProjectionMatrix,
                    expected, ref count, activeRenderer);
                Assert(count == current.AuthoredTerrainDraws.Count, "Room and F5 submit different terrain composition counts.");
                for (int index = 0; index < count; index++)
                {
                    MeshDrawCall actual = current.AuthoredTerrainDraws[index];
                    Assert(expected[index].Flags == actual.Flags && expected[index].World == actual.World,
                        "Room and F5 disagree on terrain placement, splat flags or raster settings.");
                }
            }
        });
    }

    private static MeshDrawCall[] Ground(RoomEditorControl editor) => editor.AuthoredTerrainDraws
        .Where(draw => (draw.Flags & MeshDrawFlags.TerrainGround) != 0).ToArray();

    private static void MakeTexture(string path, Color first, Color second)
    {
        using Bitmap bitmap = new(32, 32);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(first);
        using SolidBrush brush = new(second);
        graphics.FillRectangle(brush, 0, 0, 16, 16);
        graphics.FillRectangle(brush, 16, 16, 16, 16);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Warm(RoomEditorControl editor)
    {
        GateSuite.Pump(2, 20);
        using Bitmap? image = editor.Viewport.CaptureFrame(3);
        Assert(image is not null, "Room terrain capture failed.");
    }

    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
