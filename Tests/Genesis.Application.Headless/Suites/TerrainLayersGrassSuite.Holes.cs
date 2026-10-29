using System.Drawing;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Genesis.Application.Core.Automation;
using Genesis.Application.Core.Projects;
using Genesis.Physics;
using Genesis.Rendering.Core;
using Genesis.Runtime;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Terrain holes (29 October 2026): cells cut away from the ground. Saved and reopened, left out of
/// the drawn ground (a pixel through one shows what is below), the collision (a body falls through)
/// and the navigation (its cells are not walkable), kept clear of grass and scatter, painted in the
/// Terrain editor with one undo step a stroke, and cut by command. A terrain without holes is as it was.
/// </summary>
internal static partial class TerrainLayersGrassSuite
{
    private static void RunHoles(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Holes.CutSaveReopenAndNoHolesSavesAsBefore", () => HolesFile(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Holes.CollisionGrassAndScatterLeaveTheCellsOut", HolesCollisionGrassScatter);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Holes.BodyDroppedOverAHoleFallsThrough", HolesBodyFalls);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Holes.NavigationRefusesTheCellsAndScriptsCutAndFill", () => HolesNavigationAndRuntime(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Studio.Automation.SetHoleCutsFillsAndClears", () => HolesAutomation(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Holes.Dx11PixelThroughTheHoleShowsWhatIsBelow", () => HolesDx11(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Terrain.Holes.BrushStrokeUndoesSavesReopensAndDraws", () => HolesEditor(ctx));
    }

    /// <summary>A 64 m meadow (33 x 33 samples, 2 m cells, centred on the origin) at height 2.</summary>
    private static TerrainAsset HoleTerrain()
    {
        var terrain = new TerrainAsset(33, 33, 2f, -32f, -32f, -10f, 30f);
        for (int z = 0; z < 33; z++)
        for (int x = 0; x < 33; x++)
            terrain.SetHeight(x, z, 2f);
        return terrain;
    }

    private static void HolesFile(HeadlessContext ctx)
    {
        string folder = Path.Combine(ctx.Workspace, "TerrainHolesFile");
        Directory.CreateDirectory(folder);
        TerrainAsset terrain = HoleTerrain();
        string plain = Path.Combine(folder, "Plain.gterrain");
        terrain.Save(plain);
        GrassAssert(BitConverter.ToInt32(File.ReadAllBytes(plain), 4) == 1 && TerrainAsset.Load(plain).HoleCount == 0,
            "A terrain without holes no longer saves the version 1 file.");

        int cut = terrain.ApplyHoleBrush(0f, 0f, 3f);
        GrassAssert(cut > 0 && terrain.HoleCount == cut && terrain.IsHoleAt(0.5f, 0.5f) && !terrain.IsHoleAt(10f, 10f),
            $"Cutting a 3 m hole at the middle cut {cut} cells.");
        terrain.EnsureExtendedSplatmap()[0] = 255; // paint on layer 5 too, so both planes and the holes are written
        string holed = Path.Combine(folder, "Holed.gterrain");
        terrain.Save(holed);
        TerrainAsset reopened = TerrainAsset.Load(holed);
        GrassAssert(BitConverter.ToInt32(File.ReadAllBytes(holed), 4) == 3 && reopened.HoleCount == cut
            && reopened.IsHole(16, 16) && reopened.IsHole(15, 15) && !reopened.IsHole(0, 0) && reopened.GetLayerWeight(0, 0, 4) == 255,
            $"The holes did not survive save and reopen ({reopened.HoleCount} of {cut}).");

        // Filling them all saves the plain file again.
        reopened.ClearHoles();
        GrassAssert(!reopened.HasHoles && !reopened.IsHole(16, 16), "Clearing left a hole.");
        reopened.ApplyHoleBrush(0f, 0f, 3f);
        reopened.ApplyHoleBrush(0f, 0f, 3f, fill: true);
        GrassAssert(reopened.HoleCount == 0, "Filling with the same brush left holes.");
    }

    private static void HolesCollisionGrassScatter()
    {
        TerrainAsset terrain = HoleTerrain();
        TerrainColliderMesh.Build(terrain, out _, out int[] solid);
        terrain.ApplyHoleBrush(0f, 0f, 3f);
        TerrainColliderMesh.Build(terrain, out _, out int[] holed);
        GrassAssert(solid.Length == 32 * 32 * 6 && holed.Length == solid.Length - terrain.HoleCount * 6,
            $"The collision mesh kept triangles over the holes ({solid.Length / 3} to {holed.Length / 3} for {terrain.HoleCount} cells).");
        TerrainColliderMesh.BuildRegion(terrain, 12, 12, 8, 8, out _, out int[] region);
        GrassAssert(region.Length == (64 - terrain.HoleCount) * 6, $"A collision tile over the hole kept {region.Length / 6} of {64 - terrain.HoleCount} cells.");
        GrassAssert(!TerrainSurfaceRaycast.Raycast(terrain, Matrix4x4.Identity, new Vector3(0.5f, 10f, 0.5f), -Vector3.UnitY, out _)
            && TerrainSurfaceRaycast.Raycast(terrain, Matrix4x4.Identity, new Vector3(12f, 10f, 12f), -Vector3.UnitY, out _),
            "A ray down through the hole found ground, or one beside it found none.");

        // Grass and scatter keep off the hole.
        var rule = new TerrainGrassRuleSettings { Enabled = true, Spacing = 0.5f, CellSize = 8f };
        var field = new TerrainGrassField(terrain, rule);
        var tufts = new MeshInstanceData[field.CellCapacity];
        int inHole = 0, grown = 0;
        for (int cz = 3; cz <= 4; cz++)
        for (int cx = 3; cx <= 4; cx++)
        {
            int count = field.GenerateCell(cx, cz, tufts);
            grown += count;
            for (int i = 0; i < count; i++)
                if (terrain.IsHoleAt(tufts[i].World.Translation.X, tufts[i].World.Translation.Z)) inHole++;
        }

        GrassAssert(grown > 500 && inHole == 0, $"{inHole} of {grown} tufts grew over the hole.");
        var layer = new TerrainScatterLayer { DensityPerHectare = 20000f, MinimumSpacing = 0.25f, MaximumSlopeDegrees = 60f, CastShadows = false };
        TerrainScatterInstance[] copies = TerrainScatterPlacement.Generate(terrain, layer, 0, 0);
        GrassAssert(copies.Length > 500 && !copies.Any(copy => terrain.IsHoleAt(copy.Position.X, copy.Position.Z)),
            "A scattered copy stands over the hole.");
    }

    private static void HolesBodyFalls()
    {
        TerrainAsset terrain = HoleTerrain();
        terrain.ApplyHoleBrush(0f, 0f, 3f);
        TerrainColliderMesh.Build(terrain, out Vector3[] vertices, out int[] indices);
        using RuntimeScene scene = new() { Physics = PhysicsWorld.Create(new Genesis.Shared.Assets.PhysicsWorldAsset()) };
        scene.Physics.RegisterStaticTriangleMesh(vertices, indices, Vector3.One, Vector3.Zero, Quaternion.Identity, "Holed terrain");
        var overHole = scene.CreateEntity(new Vector3(0.5f, 5f, 0.5f));
        scene.World.Set(overHole, RigidBodyComponent.DynamicBox(new Vector3(0.3f)));
        var overGround = scene.CreateEntity(new Vector3(12f, 5f, 12f));
        scene.World.Set(overGround, RigidBodyComponent.DynamicBox(new Vector3(0.3f)));
        for (int tick = 0; tick < 150; tick++) scene.UpdateFixed(1f / 60);
        float fell = scene.World.GetRef<Transform3DComponent>(overHole).Position.Y;
        float rests = scene.World.GetRef<Transform3DComponent>(overGround).Position.Y;
        GrassAssert(fell < 0f && MathF.Abs(rests - 2.3f) < 0.2f,
            $"The body over the hole stopped at {fell:F2} m (ground 2 m), the one beside it at {rests:F2} m.");
    }

    private static void HolesNavigationAndRuntime(HeadlessContext ctx)
    {
        string root = GrassProject(ctx, "TerrainHolesRuntime");
        TerrainAsset terrain = HoleTerrain();
        terrain.ApplyHoleBrush(0f, 0f, 3f);
        RoomAsset room = GrassRoom(root, terrain, "Holes", enabled: false, out _);
        using var runtime = new RoomTerrainSubsystem(root, room, null!);
        GrassAssert(runtime.HoleCount == terrain.HoleCount && runtime.IsHoleAt(0.5f, 0.5f) && !runtime.IsHoleAt(12f, 12f),
            "The room's terrain did not load its holes.");

        NavMeshData Bake() => NavMeshBuilder.Build(-16f, -16f, 32, 32, 1f, runtime.NavigationHeightAt);
        NavMeshData nav = Bake();
        int Cell(NavMeshData data, float x, float z) => (int)((z + 16f) / 1f) * data.Width + (int)((x + 16f) / 1f);
        GrassAssert(!nav.Walkable[Cell(nav, 0.5f, 0.5f)] && nav.Walkable[Cell(nav, 12.5f, 12.5f)],
            "The navigation grid walks over the hole, or not beside it.");

        // Scripts (Engine.Terrain.Holes) cut, fill and switch holes off while the room runs.
        int before = runtime.HoleCount;
        GrassAssert(runtime.Edit(null!, RoomTerrainSubsystem.GroundEdit.Hole, 10f, 10f, 2f, 1f) && runtime.HoleCount > before && runtime.IsHoleAt(10f, 10f),
            "Engine.Terrain.Holes.Cut did not cut.");
        GrassAssert(runtime.Edit(null!, RoomTerrainSubsystem.GroundEdit.Fill, 10f, 10f, 2f, 1f) && runtime.HoleCount == before && !runtime.IsHoleAt(10f, 10f),
            "Engine.Terrain.Holes.Fill did not fill.");
        runtime.SetHolesEnabled(null!, false);
        GrassAssert(!runtime.HolesEnabled && !runtime.IsHoleAt(0.5f, 0.5f) && Bake().Walkable[Cell(nav, 0.5f, 0.5f)],
            "Holes switched off still left a hole to walk round.");
        runtime.SetHolesEnabled(null!, true);
        GrassAssert(runtime.IsHoleAt(0.5f, 0.5f), "Holes switched back on did not come back.");
    }

    private static void HolesAutomation(HeadlessContext ctx)
    {
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "TerrainHolesCommand"), "Terrain holes command", "Blank");
        AutomationContext context = AutomationContext.For(project);
        context.Caller = "test";
        AutomationRegistry registry = AutomationRegistry.CreateDefault();
        AutomationResult Call(string name, string json) => registry.Invoke(context, name, JsonNode.Parse(json) as JsonObject);
        string terrain = Call("resource.create", """{ "kind": "Terrain", "folder": "Assets", "name": "Pitted" }""").Data?["path"]?.GetValue<string>() ?? "";
        GrassAssert(Call("terrain.info", $$"""{ "terrain": "{{terrain}}" }""").Ok, "The terrain was not made: " + terrain);
        AutomationResult cut = Call("terrain.setHole", $$"""{ "terrain": "{{terrain}}", "points": [[0, 0], [6, 0]], "radius": 2 }""");
        TerrainAsset saved = TerrainAsset.Load(context.Resolve(terrain) + ".gterrain");
        GrassAssert(cut.Ok && saved.HoleCount > 0 && saved.IsHoleAt(3f, 0f), "terrain.setHole did not cut along its line: " + cut.Message);
        AutomationResult fill = Call("terrain.setHole", $$"""{ "terrain": "{{terrain}}", "points": [[0, 0]], "radius": 2, "fill": true }""");
        saved = TerrainAsset.Load(context.Resolve(terrain) + ".gterrain");
        GrassAssert(fill.Ok && !saved.IsHoleAt(0f, 0f) && saved.IsHoleAt(6f, 0f), "terrain.setHole fill: true did not fill only where asked.");
        AutomationResult sculpted = Call("terrain.sculpt", $$"""{ "terrain": "{{terrain}}", "brush": "Hole", "points": [[-20, -20]], "radius": 2 }""");
        GrassAssert(sculpted.Ok && TerrainAsset.Load(context.Resolve(terrain) + ".gterrain").IsHoleAt(-20f, -20f), "terrain.sculpt's Hole brush did not cut.");
        AutomationResult cleared = Call("terrain.setHole", $$"""{ "terrain": "{{terrain}}", "clear": true }""");
        GrassAssert(cleared.Ok && TerrainAsset.Load(context.Resolve(terrain) + ".gterrain").HoleCount == 0, "terrain.setHole clear: true left holes.");
        GrassAssert(!Call("terrain.setHole", $$"""{ "terrain": "{{terrain}}" }""").Ok, "terrain.setHole with nothing to do was not refused.");
    }

    private static void HolesDx11(HeadlessContext ctx)
    {
        string root = GrassProject(ctx, "TerrainHolesCapture");
        TerrainAsset terrain = HoleTerrain();
        RoomAsset room = GrassRoom(root, terrain, "Pit", enabled: false, out string binary);
        Vector3 eye = new(0f, 14f, -9f), target = new(0f, 2f, 0f);
        byte[] solid = GrassRenderOnce(ctx, root, room, eye, target, "terrain-holes-dx11-solid", out _);
        terrain.ApplyHoleBrush(0f, 0f, 4f);
        terrain.Save(binary);
        byte[] holed = GrassRenderOnce(ctx, root, room, eye, target, "terrain-holes-dx11", out _);
        // What lies below the terrain here: the frame's background, drawn alone.
        byte[] below = GrassRenderOnce(ctx, root, RoomAsset.Create("Nothing", RoomDimension.ThreeD), eye, target, "terrain-holes-dx11-below", out _);

        // The hole's middle on screen: the ground is at height 2 there.
        Vector4 clip = Vector4.Transform(new Vector4(0f, 2f, 0f, 1f), GrassView(eye, target) * GrassProjection());
        int px = (int)((clip.X / clip.W * 0.5f + 0.5f) * GrassWidth), py = (int)((0.5f - clip.Y / clip.W * 0.5f) * GrassHeight);
        int index = (py * GrassWidth + px) * 4;
        (int b, int g, int r) Pixel(byte[] frame) => (frame[index], frame[index + 1], frame[index + 2]);
        var (sb, sg, sr) = Pixel(solid);
        var (hb, hg, hr) = Pixel(holed);
        var (bb, bg, br) = Pixel(below);
        Console.WriteLine($"[Holes] DX11 pixel at the hole ({px}, {py}): solid ground ({sr}, {sg}, {sb}), cut ({hr}, {hg}, {hb}), below ({br}, {bg}, {bb}).");
        GrassAssert(Math.Abs(hr - br) + Math.Abs(hg - bg) + Math.Abs(hb - bb) < 12,
            $"Through the hole the frame is ({hr}, {hg}, {hb}), not what is below the terrain ({br}, {bg}, {bb}).");
        GrassAssert(Math.Abs(sr - br) + Math.Abs(sg - bg) + Math.Abs(sb - bb) > 60, "The ground without the hole looked like the background too.");
    }

    private static void HolesEditor(HeadlessContext ctx)
    {
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "TerrainHolesEditor"), "Terrain holes editor");
        var resources = new Genesis.Application.Core.Resources.ResourceService(project);
        string terrainFile = resources.CreateResource(resources.AssetsRoot, Genesis.Application.Core.Resources.ResourceKind.Terrain, "Cave field");
        GrassSplitTerrain(129, 0.5f, slope: 0f).Save(terrainFile + ".gterrain");
        int cut;
        using (var editor = new Genesis.Application.Editors.Suite.Terrain.TerrainEditorControl(terrainFile, project.RootPath))
        {
            var hole = Genesis.Application.Editors.Suite.Terrain.TerrainEditorControl.TerrainBrush.Hole;
            editor.SetMode(Genesis.Application.Editors.Suite.Terrain.TerrainEditorControl.TerrainEditorMode.Sculpt);
            editor.BeginStroke(-10f, 0f);
            editor.ApplyBrushAt(-10f, 0f, hole);
            editor.ApplyBrushAt(-7f, 0f, hole);
            editor.EndStroke();
            cut = editor.HoleCount;
            GrassAssert(cut > 0 && editor.IsHoleAt(-10f, 0f) && editor.IsHoleAt(-7f, 0f), "The Hole brush cut nothing.");
            editor.Undo();
            GrassAssert(editor.HoleCount == 0, "Undo did not fill the stroke's holes.");
            editor.Redo();
            GrassAssert(editor.HoleCount == cut, "Redo did not cut them again.");
            editor.Save();
        }

        using var reopened = new Genesis.Application.Editors.Suite.Terrain.TerrainEditorControl(terrainFile, project.RootPath);
        GrassAssert(reopened.HoleCount == cut && reopened.IsHoleAt(-10f, 0f), "Reopening the terrain lost its holes.");

        // The Terrain view draws the ground with the hole cut out.
        reopened.Viewport.BackendOverride = RenderBackendOption.SilkNetDx11;
        using Form host = UnattendedWindowing.NewHost(1360, 880);
        reopened.Dock = DockStyle.Fill; host.Controls.Add(reopened);
        Genesis.Application.Studio.Theme.ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
        reopened.Viewport.Host.TimerEnabled = false;
        reopened.Viewport.Camera.Target = new Vector3(-8.5f, 0f, 0f);
        reopened.Viewport.Camera.Distance = 18f; reopened.Viewport.Camera.Pitch = -0.8f; reopened.Viewport.Camera.Yaw = 0f;
        Bitmap? captured = null;
        for (int attempt = 0; attempt < 40 && captured is null; attempt++)
        {
            System.Windows.Forms.Application.DoEvents();
            captured = reopened.Viewport.CaptureFrame(3);
            Thread.Sleep(25);
        }

        GrassAssert(captured is not null, "The Terrain view produced no frame.");
        captured!.Save(Path.Combine(ctx.Captures, "terrain-holes-editor.png"));
        ctx.Report.Images.Add(new ImageResult("terrain-holes-editor", "terrain-holes-editor.png", captured.Width, captured.Height, 0, 0));
        captured.Dispose();
    }
}
