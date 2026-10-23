using System.Globalization;
using System.Numerics;
using Genesis.Rendering.Core;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Meshes placed once (MeshStaticAdd) are drawn by each camera pass with no script call, only those
/// inside the view, uploaded once; and many instances move or are read in one call from number lists.
/// </summary>
internal static partial class PgslWorkerJobChecks
{
    private sealed class MeshCountSink : IRenderCommandSink
    {
        public int Meshes;
        public void DrawSprite(in SpriteDrawCall call) { }
        public void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls) { }
        public void DrawMesh(in MeshDrawCall call) => Meshes++;
        public void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls) => Meshes += calls.Length;
    }

    private static void RunStaticMeshes(HeadlessContext ctx, Action<string, string, string, string> row)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.StaticMeshesDrawEachViewWithoutScriptCalls", () =>
        {
            using Bench bench = new();
            using System.Windows.Forms.Form host = UnattendedWindowing.NewHost(160, 120);
            UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.Software);
            renderer.Initialize(host.Handle, 160, 120);
            try
            {
                ScriptMeshes.Reset();
                StaticMeshLayer.Clear();
                // 30 cubes in front of a camera at the origin looking down +Z, 30 behind it.
                bench.Run("""
                    sm = MeshCreate(); MeshAddCube(sm, 0, 0, 0, 1, 63, 255, 255, 255, 0, 0, 1, 1);
                    first = 0;
                    for (var i = 0; i < 30; i = i + 1) { s = MeshStaticAdd(sm, "", (i % 6) - 2.5, Floor(i / 6) - 2, 12, 1, 1, 1, i * 7, ""); if (first == 0) { first = s; } }
                    for (var i = 0; i < 30; i = i + 1) { MeshStaticAdd(sm, "", (i % 6) - 2.5, Floor(i / 6) - 2, -12, 1, 1, 1, 0, ""); }
                    bad = MeshStaticAdd(sm + 1000, "", 0, 0, 12, 1, 1, 1, 0, "");
                    """);
                HeadlessHarness.Assert(bench.Number("r = MeshStaticCount() + bad;") == 60,
                    "60 meshes should be placed (an unknown mesh adds nothing).");

                Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY)
                    * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 4f / 3f, 0.1f, 100f);
                var sink = new MeshCountSink();
                ScriptMeshes.BeginFrame();
                StaticMeshLayer.Submit(renderer, sink, viewProjection, "");
                int mesh = (int)bench.Number("r = sm;");
                int uploads = ScriptMeshes.UploadCount(mesh);
                HeadlessHarness.Assert(sink.Meshes == 30 && StaticMeshLayer.LastDrawn == 30 && StaticMeshLayer.LastCulled == 30,
                    $"The view should draw the 30 in front and skip the 30 behind (drew {sink.Meshes}, skipped {StaticMeshLayer.LastCulled}).");

                // The next frames draw the same with nothing sent again.
                for (int frame = 0; frame < 3; frame++)
                {
                    sink.Meshes = 0;
                    ScriptMeshes.BeginFrame();
                    StaticMeshLayer.Submit(renderer, sink, viewProjection, "");
                }
                HeadlessHarness.Assert(sink.Meshes == 30 && uploads == 1 && ScriptMeshes.UploadCount(mesh) == uploads,
                    "Later frames should draw the same meshes without uploading them again.");

                // Moved behind the camera, one leaves the view; removed, another goes; the turned view sees the rest.
                double answers = bench.Number("""
                    ok1 = MeshStaticMove(first, 0, 0, -20, 1, 1, 1, 0); ok2 = MeshStaticRemove(first + 1); ok3 = MeshStaticRemove(first + 1);
                    r = 0; if (ok1 && ok2 && !ok3) { r = 1; }
                    """);
                sink.Meshes = 0;
                StaticMeshLayer.Submit(renderer, sink, viewProjection, "");
                HeadlessHarness.Assert(sink.Meshes == 28 && StaticMeshLayer.Count == 59 && answers == 1,
                    $"A moved and a removed mesh should leave the view (drew {sink.Meshes}, {StaticMeshLayer.Count} placed).");
                Matrix4x4 behind = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
                    * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 4f / 3f, 0.1f, 100f);
                sink.Meshes = 0;
                StaticMeshLayer.Submit(renderer, sink, behind, "");
                HeadlessHarness.Assert(sink.Meshes == 31, $"Looking back should draw the 30 behind and the moved one (drew {sink.Meshes}).");

                // A rebuilt mesh grows its bounds: a cube moved far to one side by its vertices comes into view.
                bench.Run("""
                    edge = MeshCreate(); MeshAddCube(edge, 40, 0, 0, 1, 63, 255, 255, 255, 0, 0, 1, 1);
                    e = MeshStaticAdd(edge, "", 0, 0, 12, 1, 1, 1, 0, "");
                    """);
                sink.Meshes = 0;
                StaticMeshLayer.Submit(renderer, sink, viewProjection, "");
                int before = sink.Meshes;
                bench.Run("""MeshClear(edge); MeshAddCube(edge, 0, 0, 0, 1, 63, 255, 255, 255, 0, 0, 1, 1);""");
                sink.Meshes = 0;
                ScriptMeshes.BeginFrame();
                StaticMeshLayer.Submit(renderer, sink, viewProjection, "");
                HeadlessHarness.Assert(before == 28 && sink.Meshes == 29, $"A rebuilt mesh should be culled by its new bounds ({before} then {sink.Meshes}).");

                bench.Run("MeshStaticClear();");
                HeadlessHarness.Assert(StaticMeshLayer.Count == 0, "MeshStaticClear should remove every placed mesh.");
            }
            finally
            {
                StaticMeshLayer.Clear();
                ScriptMeshes.Reset();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.InstancePositionsMoveFromNumberLists", () =>
        {
            using Bench bench = new();
            IGameContext? previous = PgslCommands.ActiveGameContext;
            EcsWorld world = new();
            PgslCommands.ActiveGameContext = new NullGameContext { World = world };
            try
            {
                var ids = new List<int>();
                for (int i = 0; i < 5; i++)
                {
                    Entity entity = world.CreateEntity();
                    world.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                    ids.Add(entity.Id);
                }
                string list = string.Join(" ", ids.Select(id => $"NumListAdd(ids, {id.ToString(CultureInfo.InvariantCulture)});"));
                string result = Convert.ToString(bench.Run($$"""
                    ids = NumListCreate(); {{list}} NumListAdd(ids, 999999);
                    xyz = NumListCreate();
                    for (var i = 0; i < 6; i = i + 1) { NumListAdd3(xyz, i * 10, i + 0.5, 0 - i); }
                    moved = InstanceSetPositionsFromNumList(ids, xyz);
                    back = NumListCreate();
                    found = InstanceGetPositionsToNumList(ids, back);
                    r = StringFormatNumber(moved, 0) + " " + StringFormatNumber(found, 0) + " " + StringFormatNumber(NumListSize(back), 0)
                        + " " + StringFormatNumber(NumListGet(back, 9), 0) + " " + StringFormatNumber(NumListGet(back, 10), 1) + " " + StringFormatNumber(NumListGet(back, 17), 0);
                    """), CultureInfo.InvariantCulture) ?? "";
                TransformComponent third = world.GetRef<TransformComponent>(world.GetEntity(ids[2]));
                HeadlessHarness.Assert(result == "5 5 18 30 3.5 0" && third.X == 20 && third.Y == 2.5f && third.Z == -2,
                    $"Five instances should move and read back in one call each, the unknown id skipped (got \"{result}\", third at {third.X}, {third.Y}, {third.Z}).");
            }
            finally { PgslCommands.ActiveGameContext = previous; }
        });
    }
}
