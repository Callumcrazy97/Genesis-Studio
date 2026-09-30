using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Runtime.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Guards the per-frame draw path against the class of regression where cached assets are
/// re-validated, re-read or re-uploaded every frame (for example models re-scanned and re-uploaded
/// per frame, or sprites stat-ing their descriptors per draw). Uses the process-wide
/// <see cref="AssetIoCounters"/> and <see cref="GpuTelemetry"/> counters every backend reports into.
/// </summary>
internal static class FramePathAssetIoSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.SteadyState.SpritesDoNoFileIoOrResourceChurnAfterWarmup", () => SteadySprites(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.SteadyState.ExplicitInvalidationRefreshesSpriteImmediately", () => Invalidation(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Assets.PollingDisabledStopsFallbackChecks", PollingDisabled);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Draw.RecycledEntityDoesNotInheritDrawState", RecycledEntity);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.FixedTimestep.DropsBacklogPastStepCap", FixedTimestepBacklog);
    }

    private sealed class CaptureSink : IRenderCommandSink
    {
        public readonly List<SpriteDrawCall> Sprites = new();
        public void DrawSprite(in SpriteDrawCall call) => Sprites.Add(call);
        public void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls) { foreach (SpriteDrawCall call in calls) Sprites.Add(call); }
        public void DrawMesh(in MeshDrawCall call) { }
        public void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls) { }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(HeadlessContext ctx, string name, int images)
        {
            ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, name), name);
            Root = project.RootPath;
            ResourceService resources = new(project);
            for (int i = 0; i < images; i++)
            {
                string descriptor = resources.CreateResource(
                    ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, $"Sprite {i}");
                // A new image resource has no pixels until the editor saves; give it a real frame
                // beside the descriptor (the loader's documented fallback location).
                string stem = Path.GetFileName(descriptor)[..^".image.json".Length];
                using (Bitmap pixels = new(16 + i, 24))
                {
                    using (Graphics graphics = Graphics.FromImage(pixels)) graphics.Clear(Color.SteelBlue);
                    pixels.Save(Path.Combine(Path.GetDirectoryName(descriptor)!, stem + ".png"), ImageFormat.Png);
                }
                Images.Add(ResourceNames.Name(Root, descriptor, ResourceType.Image));
            }
            Host = UnattendedWindowing.NewHost(320, 240);
            UnattendedWindowing.ShowWithoutFocus(Host);
            Renderer = RenderControllerFactory.Create(RenderBackendOption.Software);
            Renderer.Initialize(Host.Handle, 320, 240);
        }

        public string Root { get; }
        public List<string> Images { get; } = new();
        public Form Host { get; }
        public IRenderController Renderer { get; }

        public CaptureSink Frame()
        {
            var sink = new CaptureSink();
            Renderer.BeginFrame();
            for (int i = 0; i < Images.Count; i++)
            {
                ObjectDrawPass.QueueSprite2D(Root, sink, Renderer, new ObjectDrawAssetEntry { Image = Images[i] },
                    new TransformComponent { X = 20 * i, Y = 20, ScaleX = 1, ScaleY = 1, ScaleZ = 1 },
                    new Draw2DComponent { Visible = true }, Images[i], 0, 1, 0, 0, 1);
            }
            Renderer.EndFrame();
            return sink;
        }

        public void Dispose()
        {
            RuntimeAssetInvalidation.Invalidate(Root, Renderer);
            Renderer.Dispose();
            Host.Dispose();
        }
    }

    private static void SteadySprites(HeadlessContext ctx)
    {
        // Keep the fallback poll out of the measured window; this case measures the cached path.
        RuntimeAssetPolicy.SetFramePathInterval(60_000);
        try
        {
            using Fixture fixture = new(ctx, "FramePathSprites", 24);
            for (int i = 0; i < 3; i++) fixture.Frame();

            AssetIoSnapshot ioBefore = AssetIoCounters.Capture();
            GpuTelemetrySnapshot gpuBefore = GpuTelemetry.Capture();
            int drawn = 0;
            for (int i = 0; i < 60; i++) drawn += fixture.Frame().Sprites.Count;
            AssetIoSnapshot io = AssetIoCounters.Capture().Since(ioBefore);
            GpuTelemetrySnapshot gpu = GpuTelemetry.Capture().Since(gpuBefore);

            Assert(drawn == 60 * fixture.Images.Count, $"Expected every sprite to draw each frame, got {drawn}. {Describe(fixture)}");
            Assert(io.FileChecks == 0 && io.FileReads == 0,
                $"Warm sprite frames touched the file system: {io.FileChecks} checks and {io.FileReads} reads over 60 frames.");
            Assert(gpu.TexturesCreated == 0 && gpu.BuffersCreated == 0 && gpu.RenderTargetsCreated == 0,
                $"Warm sprite frames created GPU resources: {gpu.TexturesCreated} textures, {gpu.BuffersCreated} buffers, {gpu.RenderTargetsCreated} targets.");
        }
        finally { RuntimeAssetPolicy.SetFramePathInterval(RuntimeAssetPolicy.DefaultFramePathIntervalMilliseconds); }
    }

    private static void Invalidation(HeadlessContext ctx)
    {
        RuntimeAssetPolicy.SetFramePathInterval(60_000);
        try
        {
            using Fixture fixture = new(ctx, "FramePathInvalidation", 1);
            SpriteDrawCall before = fixture.Frame().Sprites.Single();
            string frame = SpriteAssetLoader.ResolveFrameTexturePath(fixture.Root, fixture.Images[0], 0);
            Assert(File.Exists(frame), "The fixture image has no frame file to edit.");
            int width = (int)before.Width + 40, height = (int)before.Height + 8;
            using (Bitmap replacement = new(width, height))
            {
                using (Graphics graphics = Graphics.FromImage(replacement)) graphics.Clear(Color.OrangeRed);
                replacement.Save(frame, ImageFormat.Png);
            }
            File.SetLastWriteTimeUtc(frame, DateTime.UtcNow.AddSeconds(2));

            // Without invalidation the long fallback interval keeps serving the cached binding.
            Assert(fixture.Frame().Sprites.Single().Width == before.Width,
                "The cached sprite binding re-checked the disk inside its fallback interval.");
            RuntimeAssetInvalidation.Invalidate(fixture.Root);
            SpriteDrawCall after = fixture.Frame().Sprites.Single();
            Assert(after.Width == width && after.Height == height,
                $"An explicit invalidation did not refresh the sprite: {after.Width}x{after.Height}, expected {width}x{height}.");
        }
        finally { RuntimeAssetPolicy.SetFramePathInterval(RuntimeAssetPolicy.DefaultFramePathIntervalMilliseconds); }
    }

    private static void PollingDisabled()
    {
        long now = Environment.TickCount64;
        Assert(RuntimeAssetPolicy.NextCheck(now, 0, 7) == now, "A zero interval must keep check-on-request semantics while polling.");
        long bounded = RuntimeAssetPolicy.NextCheck(now, 1000, 12345);
        Assert(bounded >= now + 1000 && bounded <= now + 1250, $"Jittered expiry {bounded - now} ms is outside 1000..1250 ms.");
        Assert(RuntimeAssetPolicy.NextCheck(now, 20, 999) == now + 20, "Short intervals must not be stretched by jitter.");
        RuntimeAssetPolicy.DisablePolling();
        try
        {
            Assert(RuntimeAssetPolicy.NextCheck(now, 0, 7) == long.MaxValue
                && RuntimeAssetPolicy.NextCheck(now, 1000, 7) == long.MaxValue,
                "Exported games must never schedule a fallback file check.");
        }
        finally { RuntimeAssetPolicy.SetFramePathInterval(RuntimeAssetPolicy.DefaultFramePathIntervalMilliseconds); }
    }

    private static void RecycledEntity()
    {
        var world = new Genesis.Runtime.ECS.World();
        var first = world.CreateEntity();
        ObjectDrawAssetRegistry.Set(first, new ObjectDrawAssetEntry { Image = "Old bullet" });
        ProceduralMeshDrawRegistry.Set(first, [new MeshDrawCall()]);
        try
        {
            world.DestroyEntity(first);
            world.FlushDeferred();
            var second = world.CreateEntity();
            Assert(second.Id == first.Id, "The world did not recycle the destroyed id; this check needs a reused id.");
            Assert(!ObjectDrawAssetRegistry.TryGet(second, out _),
                "A new entity with a recycled id inherited the destroyed entity's draw assets.");
            Assert(!ProceduralMeshDrawRegistry.TryGet(second, out _),
                "A new entity with a recycled id inherited the destroyed entity's procedural meshes.");
        }
        finally
        {
            ObjectDrawAssetRegistry.Remove(first);
            ProceduralMeshDrawRegistry.Remove(first);
        }
    }

    private static void FixedTimestepBacklog()
    {
        var clock = new Genesis.Runtime.Core.FixedTimestep { FixedDelta = 1f / 60f, MaxStepsPerFrame = 8 };
        for (int frame = 0; frame < 20; frame++)
        {
            int steps = clock.Advance(0.25f);
            Assert(steps <= 8, $"Frame {frame} ran {steps} fixed steps.");
            Assert(clock.InterpolationAlpha >= 0f && clock.InterpolationAlpha <= 1f,
                $"Interpolation alpha {clock.InterpolationAlpha} left 0..1 after an overloaded frame.");
        }
        // Once the load ends, the clock must immediately return to ordinary pacing.
        int recovered = clock.Advance(1f / 60f);
        Assert(recovered <= 2, $"The clock carried an overload backlog forward and ran {recovered} steps.");
    }

    private static string Describe(Fixture fixture)
    {
        string image = fixture.Images[0];
        string descriptor = SpriteAssetLoader.ResolveDescriptorPath(fixture.Root, image);
        string frame = SpriteAssetLoader.ResolveFrameTexturePath(fixture.Root, image, 0);
        TextureHandle handle = string.IsNullOrEmpty(frame) ? TextureHandle.Invalid : fixture.Renderer.LoadTexture(frame);
        return $"image='{image}' descriptor='{descriptor}' exists={File.Exists(descriptor)} frame='{frame}' "
            + $"frameExists={File.Exists(frame)} texture={handle.Id}";
    }

    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
