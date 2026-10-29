using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Core;
using Genesis.Rendering.D3dMath;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Decal layers: a projected decal shows only on surfaces that take its layer
/// (MeshDrawCall.DecalLayers, DecalDrawCall.Layer); every surface takes every layer by default.
/// </summary>
/// <remarks>
/// Two walls side by side, the left taking every layer (the default) and the right only layer 0;
/// across both, a layer 1 decal above and a layer 0 decal below. The layer 1 decal must stain the
/// left wall and leave the right one alone, and the layer 0 decal must stain both, on every GPU
/// backend. A frame without decals is the reference for "left alone".
/// </remarks>
internal static class DecalLayerSuite
{
    private const int Width = 320, Height = 240;

    private static readonly RenderBackendOption[] GpuBackends =
    [
        RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan, RenderBackendOption.OpenGL,
    ];

    // Where the decals land: the layer 1 band above the middle, the layer 0 band below.
    private static readonly Vector3 UpperLeft = new(-1.2f, 0.8f, 4f), UpperRight = new(1.2f, 0.8f, 4f);
    private static readonly Vector3 LowerLeft = new(-1.2f, -0.8f, 4f), LowerRight = new(1.2f, -0.8f, 4f);

    public static void LayersPickTheirSurfaces(HeadlessContext ctx)
    {
        // Script side first: the layer a decal gets and keeps.
        WorldDecals.Reset();
        try
        {
            int first = WorldDecals.Add("x", Vector3.Zero, -Vector3.UnitZ, 1f, 0f);
            HeadlessHarness.Assert(WorldDecals.LayerOf(first) == 0, "A new decal is not on layer 0.");
            HeadlessHarness.Assert(WorldDecals.SetLayer(first, 3) && WorldDecals.LayerOf(first) == 3
                && !WorldDecals.SetLayer(first, MeshDrawCall.DecalLayerCount), "DecalSetLayer did not keep a layer, or took one out of range.");
            WorldDecals.DefaultLayer = 2;
            HeadlessHarness.Assert(WorldDecals.LayerOf(WorldDecals.Add("x", Vector3.Zero, -Vector3.UnitZ, 1f, 0f)) == 2,
                "Engine.Decal.Layer did not give a new decal its layer.");
            HeadlessHarness.Assert(MeshDrawCall.RefusedDecalLayers(0) == 0 && MeshDrawCall.RefusedDecalLayers(1) == 0b111110,
                "A surface's refused layers are wrong.");
        }
        finally { WorldDecals.Reset(); }

        Matrix4x4 view = D3dMatrixHelper.CreateLookAtLh(Vector3.Zero, new Vector3(0f, 0f, 4f), Vector3.UnitY);
        Matrix4x4 projection = D3dMatrixHelper.CreatePerspectiveLh(MathF.PI / 3f, Width / (float)Height, 0.1f, 100f);
        Point Screen(Vector3 world)
        {
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
            return new Point((int)((clip.X / clip.W * 0.5f + 0.5f) * Width), (int)((0.5f - clip.Y / clip.W * 0.5f) * Height));
        }

        List<string> failures = [];
        foreach (RenderBackendOption backend in GpuBackends)
        {
            using var host = UnattendedWindowing.NewHost(Width, Height);
            UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(backend);
            renderer.Initialize(host.Handle, Width, Height);
            MeshHandle left = renderer.RegisterMesh(Quad(-3f, -0.05f), [0, 1, 2, 0, 2, 3]);
            MeshHandle right = renderer.RegisterMesh(Quad(0.05f, 3f), [0, 1, 2, 0, 2, 3]);
            byte[] red = new byte[8 * 8 * 4];
            for (int i = 0; i < red.Length; i += 4) { red[i] = 230; red[i + 1] = 30; red[i + 2] = 30; red[i + 3] = 255; }
            TextureHandle stain = renderer.CreateTexture(8, 8, red);
            string name = backend.ToString().ToLowerInvariant();
            try
            {
                byte[] Frame(bool decals)
                {
                    Mesh3DState state = Mesh3DState.Default;
                    state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false; state.ShadowsEnabled = false;
                    state.FrustumCullingEnabled = false;
                    renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                    renderer.SetCamera3D(view, projection);
                    renderer.BeginFrame(); renderer.Clear(0.35f, 0.5f, 0.75f);
                    const MeshDrawFlags flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog;
                    renderer.DrawMesh(new MeshDrawCall { Mesh = left, World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = 1f, Flags = flags });
                    // Takes layer 0 only.
                    renderer.DrawMesh(new MeshDrawCall { Mesh = right, World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = 1f, Flags = flags, DecalLayers = 1 });
                    if (decals)
                        renderer.DrawDecals([Decal(stain, 0.8f, layer: 1), Decal(stain, -0.8f, layer: 0)]);
                    renderer.EndFrame();
                    HeadlessHarness.Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels), backend + ": no frame was read back.");
                    Save(pixels, width, height, ctx, $"decal-layers-{name}-{(decals ? "decals" : "bare")}");
                    renderer.Present();
                    return pixels;
                }

                byte[] bare = Frame(decals: false);
                byte[] marked = Frame(decals: true);
                Color At(byte[] frame, Vector3 world)
                {
                    Point p = Screen(world);
                    int offset = (p.Y * Width + p.X) * 4;
                    return Color.FromArgb(frame[offset + 2], frame[offset + 1], frame[offset]);
                }
                bool Stained(Color c, Color plain) => c.G < plain.G * 0.6 && c.R > c.G * 1.8;
                bool Same(Color c, Color plain) => Math.Abs(c.R - plain.R) <= 6 && Math.Abs(c.G - plain.G) <= 6 && Math.Abs(c.B - plain.B) <= 6;

                Color ul = At(marked, UpperLeft), ur = At(marked, UpperRight), ll = At(marked, LowerLeft), lr = At(marked, LowerRight);
                Color plainUl = At(bare, UpperLeft), plainUr = At(bare, UpperRight), plainLl = At(bare, LowerLeft), plainLr = At(bare, LowerRight);
                if (!Stained(ul, plainUl)) failures.Add($"{backend}: the layer 1 decal is missing from the wall that takes every layer ({plainUl} -> {ul})");
                if (!Same(ur, plainUr)) failures.Add($"{backend}: the layer 1 decal shows on the wall that takes layer 0 only ({plainUr} -> {ur})");
                if (!Stained(ll, plainLl) || !Stained(lr, plainLr)) failures.Add($"{backend}: the layer 0 decal is missing from a wall ({plainLl} -> {ll}, {plainLr} -> {lr})");
            }
            finally
            {
                renderer.ReleaseMesh(left);
                renderer.ReleaseMesh(right);
                renderer.ReleaseTexture(stain);
            }
        }
        HeadlessHarness.Assert(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>A wall from x0 to x1, 4 m tall, 4 m in front of the camera, facing it.</summary>
    private static MeshVertex[] Quad(float x0, float x1)
    {
        MeshVertex V(float x, float y) => new() { Position = new Vector3(x, y, 4f), Normal = -Vector3.UnitZ, Color = new Vector4(0.8f, 0.8f, 0.8f, 1f), UV = new Vector2(x, y) };
        return [V(x0, -2f), V(x1, -2f), V(x1, 2f), V(x0, 2f)];
    }

    /// <summary>A 4 m by 1 m multiply decal across both walls at height y.</summary>
    private static DecalDrawCall Decal(TextureHandle picture, float y, int layer) => new()
    {
        Texture = picture, Position = new Vector3(0f, y, 4f), Normal = -Vector3.UnitZ, Tangent = Vector3.UnitX,
        Width = 4f, Height = 1f, Depth = 0.5f, Color = Vector4.One, Blend = DecalBlend.Multiply, Layer = layer,
    };

    private static void Save(byte[] pixels, int width, int height, HeadlessContext ctx, string name)
    {
        using Bitmap image = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, image.PixelFormat);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { image.UnlockBits(data); }
        image.Save(Path.Combine(ctx.Captures, name + ".png"));
    }
}
