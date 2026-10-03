using System.Drawing;
using System.Numerics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Room-owned 2D lights and particles, numeric saves, and the sprite and mouse contracts that
/// games build on. These began as checks on a fan-game template; they test the engine alone.
/// </summary>
internal static class Effects2DSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Effects2D");
        string parent = Path.Combine(ctx.Workspace, "Effects2D");
        Directory.CreateDirectory(parent);

        HeadlessHarness.RunCase(ctx.Report, "Engine.Effects2D.LightOcclusionUsesSharedRayContract", () =>
        {
            using RoomEffects2D effects = new();
            effects.SetObstacle(1, 40, 10, 16, 64);
            Assert(!effects.LineClear(20, 40, 90, 40), "A solid obstacle failed to block a light ray.");
            Assert(effects.LineClear(20, 5, 90, 5), "A ray above the obstacle is incorrectly blocked.");
            Assert(!effects.LineClear(float.NaN, 0, 1, 1), "Nonfinite ray accepted.");
            Assert(RoomEffects2D.RayRectangle(new Vector2(48, 20), Vector2.UnitX, new RectangleF(40, 10, 16, 64), 100) == 0, "Inside-blocker ray must hit at its origin.");
            effects.SetObstacle(1, 0, 0, 0, 0); Assert(effects.LineClear(20, 40, 90, 40), "Removed blocker is still active.");
            effects.SetLight(1, 0, 0, 90, 1, 1, .5f, 0);
            effects.SetLight(2, float.NaN, 0, 90, 1, 1, 1, 1); Assert(effects.LightCount == 1, "Invalid light was registered.");
            effects.RemoveLight(1); Assert(effects.LightCount == 0, "Removed light is retained.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Effects2D.ParticlePoolPauseExpiryAndRoomIsolation", () =>
        {
            string project = Path.Combine(parent, "Particles");
            string folder = Path.Combine(project, "Assets", "Particles");
            Directory.CreateDirectory(folder);
            string[] particles =
            [
                Path.Combine(folder, "Sparks.particle.json"),
                Path.Combine(folder, "Dust.particle.json"),
            ];
            File.WriteAllText(particles[0], "{\"emitRate\":40}");
            File.WriteAllText(particles[1], "{\"emitRate\":12}");
            ResourceCatalog.Invalidate(project);

            using RoomEffects2D effects = new();
            foreach (string path in particles)
            {
                ParticleConfig config = ParticleAssetLoader.Load(project, path);
                Assert(ParticleAssetLoader.EnumerateEnabledEmitters(config).Count > 0, "Empty particle resource: " + path);
                effects.Emit(project, path, 100, 100, 200, 90);
            }
            for (int i = 0; i < 32; i++) effects.Emit(project, particles[0], 0, 0, 64, 0);
            Assert(effects.ParticleCount == RoomEffects2D.ParticleBudget, "Emission exceeded or did not reach the bounded pool.");
            effects.Paused = true; effects.Advance(10); Assert(effects.ParticleCount == RoomEffects2D.ParticleBudget, "Paused particles advanced.");
            effects.Paused = false; for (int i = 0; i < 260; i++) effects.Advance(.05f);
            Assert(effects.ParticleCount == 0, "Expired particles were retained.");
            RoomAsset a = RoomAsset.Create("A", RoomDimension.TwoD), b = RoomAsset.Create("B", RoomDimension.TwoD);
            RoomEffects2D.For(a).SetAmbient(.4f, 0, 0, 0);
            Assert(!RoomEffects2D.For(b).Enabled, "Effects leaked into an unrelated room.");
            RoomEffects2D.For(a).Dispose(); RoomEffects2D.For(b).Dispose();
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Saves.NumbersRoundTripAtomicallyWithBoundsAndCorruption", () =>
        {
            string file = Path.Combine(parent, "NumericSave", "campaign.json");
            ProjectNumberSave save = new(file); save.Set("chapter", 3); save.Set("bank", 143);
            save.Set("invalid", double.NaN); save.Set(new string('x', 97), 1);
            Assert(save.Flush() && !File.Exists(file + ".tmp"), "Atomic checkpoint write failed.");
            ProjectNumberSave loaded = new(file);
            Assert(loaded.Get("chapter", 0) == 3 && loaded.Get("bank", 0) == 143 && loaded.Get("invalid", -1) == -1, "Checkpoint round-trip changed values.");
            loaded.Clear(); for (int i = 0; i < 300; i++) loaded.Set("slot" + i, i);
            Assert(loaded.Get("slot255", -1) == 255 && loaded.Get("slot256", -1) == -1, "Save-slot capacity is not enforced.");
            File.WriteAllText(file, "{\"version\":999999999999999999999,\"values\":{}}");
            Assert(!new ProjectNumberSave(file).Exists, "Invalid version was accepted or threw an unhandled exception.");
            File.WriteAllText(file, "{\"version\":1,\"values\":{\"bad\":[],\"good\":2}}");
            Assert(new ProjectNumberSave(file).Get("good", -1) == 2, "Malformed individual slot discarded valid data.");
            File.WriteAllText(file, "not json"); Assert(!new ProjectNumberSave(file).Exists, "Corrupt checkpoint did not recover safely.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Effects2D.MouseButtonsAndSmoothEffectSpritesPreserveContract", () =>
        {
            Assert(PgslCommands.MbRight == (int)MouseButton.Right && PgslCommands.MbMiddle == (int)MouseButton.Middle, "Right/middle mouse mapping is swapped.");
            PgslRenderDrawSurface gui = new(null, null, 1440, 810, isGui: true);
            PgslRenderDrawSurface world = new(null, null, 1440, 810);
            Assert(gui.SpriteDepth == -10000 && world.SpriteDepth == 0, "GUI sprites must share the panel layer without changing world sprite depth.");
            RecordingSink sink = new(); ViewportSpriteSink viewport = new(sink, new Rectangle(0, 40, 1280, 720));
            viewport.DrawSprite(new SpriteDrawCall { Texture = new TextureHandle(1), X = 10, Y = 10, Width = 20, Height = 20, SmoothSampling = true });
            Assert(sink.Calls.Count == 1 && sink.Calls[0].SmoothSampling && sink.Calls[0].Y == 50, "Viewport clipping lost smooth effect sampling.");
            Assert(!new SpriteDrawCall().SmoothSampling, "Ordinary pixel sprites should inherit the existing room sampler.");
        });
    }

    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);

    private sealed class RecordingSink : IRenderCommandSink
    {
        public List<SpriteDrawCall> Calls { get; } = [];
        public void DrawSprite(in SpriteDrawCall call) => Calls.Add(call);
        public void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls) { foreach (SpriteDrawCall call in calls) Calls.Add(call); }
        public void DrawMesh(in MeshDrawCall call) { }
        public void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls) { }
    }
}
