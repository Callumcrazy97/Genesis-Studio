using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Core;
using Genesis.Rendering.D3dMath;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Materials;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// A lit material drawn on its own and drawn as instances (a room's static models, scattered
/// props) shades the same.
/// </summary>
/// <remarks>
/// The instanced path (IRenderController.DrawMeshInstances) used to keep only the picture, the
/// normal map and the shader: metallic and roughness factors, ORM and emission maps, the emission
/// colour and the picture's repeat were dropped, so a glTF model (metallic 1 unless it says
/// otherwise) came out dark drawn singly and bright drawn through StaticModelLayer. This draws one
/// quad with all of those set, singly and as an instance, and compares the pictures; a third
/// picture without the material shows the material is what the comparison measures.
/// </remarks>
internal static class InstancedMaterialSuite
{
    private const int Width = 320, Height = 240;

    // Columns across the quad: the emission map's stripes repeat twice (UV scale 2).
    private static readonly int[] SampleColumns = [70, 115, 160, 205, 250];

    public static void SameShadingSingleAndInstanced(HeadlessContext ctx)
    {
        using var host = UnattendedWindowing.NewHost(Width, Height);
        UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11);
        renderer.Initialize(host.Handle, Width, Height);

        MeshVertex Corner(float x, float y, float u, float v) => new()
        {
            Position = new Vector3(x, y, 0f), Normal = -Vector3.UnitZ, Color = Vector4.One, UV = new Vector2(u, v),
        };
        MeshHandle quad = renderer.RegisterMesh(
            [Corner(-1.2f, -0.9f, 0f, 1f), Corner(1.2f, -0.9f, 1f, 1f), Corner(1.2f, 0.9f, 1f, 0f), Corner(-1.2f, 0.9f, 0f, 0f)],
            [0, 1, 2, 0, 2, 3]);
        TextureHandle albedo = renderer.CreateTexture(8, 8, Solid(200, 80, 40, 255));
        // Occlusion 1, roughness 0.8, metallic 1: the factors below scale them.
        TextureHandle orm = renderer.CreateTexture(8, 8, Solid(255, 204, 255, 255), TextureColorSpace.Linear);
        TextureHandle emission = renderer.CreateTexture(8, 8, Stripes(), TextureColorSpace.Srgb);
        try
        {
            MeshDrawCall material = new()
            {
                Mesh = quad, World = Matrix4x4.Identity, Texture = albedo, OrmMap = orm, EmissionMap = emission,
                Tint = RenderColor.White, Alpha = 1f, Emissive = 0.3f,
                Flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog,
                // Metallic 0.6, roughness 0.5 (scaling the map's), set.
                MaterialFactors = new Vector4(0.6f, 0.5f, 0f, 1f),
                // Normal strength 1, no height, emission map strength 1.5.
                SurfaceParams = new Vector4(1f, 0f, 1.5f, 0f),
                // The picture repeats twice, no slide.
                DetailParams = new Vector4(0f, 0f, 0f, 2f),
                EmissionColor = new Vector4(0.3f, 1f, 0.5f, 1f),
            };
            MeshDrawCall plain = new()
            {
                Mesh = quad, World = Matrix4x4.Identity, Texture = albedo, Tint = RenderColor.White, Alpha = 1f,
                Flags = material.Flags,
            };

            Color[] single = Render(renderer, material, instanced: false, ctx, "instanced-material-single");
            Color[] instanced = Render(renderer, material, instanced: true, ctx, "instanced-material-instanced");
            Color[] bare = Render(renderer, plain, instanced: false, ctx, "instanced-material-none");

            int materialEffect = 0;
            for (int i = 0; i < SampleColumns.Length; i++)
            {
                HeadlessHarness.Assert(Distance(single[i], instanced[i]) <= 6,
                    $"Column {SampleColumns[i]}: drawn as an instance the material shades {instanced[i]}, drawn on its own {single[i]}.");
                materialEffect = Math.Max(materialEffect, Distance(single[i], bare[i]));
            }
            HeadlessHarness.Assert(materialEffect >= 30,
                $"The material changed the picture by only {materialEffect}/255; this check is not measuring it.");
        }
        finally
        {
            renderer.ReleaseMesh(quad);
            renderer.ReleaseTexture(albedo);
            renderer.ReleaseTexture(orm);
            renderer.ReleaseTexture(emission);
        }
    }

    private static Color[] Render(IRenderController renderer, MeshDrawCall draw, bool instanced, HeadlessContext ctx, string name)
    {
        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = true;
        state.LightDirection = Vector3.Normalize(new Vector3(0.3f, -0.4f, 1f));
        state.SunColor = Vector3.One; state.SunIntensity = 1.5f;
        state.AmbientColor = new Vector3(0.25f); state.AmbientGroundColor = new Vector3(0.15f);
        state.ShadowsEnabled = false; state.FogEnabled = false; state.BloomEnabled = false;
        state.ShowFloor = false; state.ShowSunVisual = false; state.NoToneMapping = true;
        state.FrustumCullingEnabled = false;

        Color[] samples = new Color[SampleColumns.Length];
        // A few frames, so anything carried from frame to frame settles.
        for (int frame = 0; frame < 3; frame++)
        {
            renderer.BeginFrame();
            renderer.Clear(0.02f, 0.02f, 0.03f, 1f);
            renderer.SetMesh3DState(state);
            renderer.Set3DFrameActive(true);
            renderer.SetCamera3D(D3dMatrixHelper.CreateLookAtLh(new Vector3(0f, 0f, -3f), Vector3.Zero, Vector3.UnitY),
                D3dMatrixHelper.CreatePerspectiveLh(MathF.PI / 3f, Width / (float)Height, 0.1f, 100f));
            renderer.ClearPointLights();
            if (instanced) renderer.DrawMeshInstances(draw, [new MeshInstanceData(draw.World, RenderColor.White)]);
            else renderer.DrawMesh(draw);
            renderer.EndFrame();
            HeadlessHarness.Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels), "No pixels were read back.");
            for (int i = 0; i < SampleColumns.Length; i++)
            {
                int offset = (height / 2 * width + SampleColumns[i] * width / Width) * 4;
                samples[i] = Color.FromArgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
            }
            if (frame == 2) Save(pixels, width, height, ctx, name);
            renderer.Present();
        }
        return samples;
    }

    private static byte[] Solid(byte r, byte g, byte b, byte a)
    {
        byte[] rgba = new byte[8 * 8 * 4];
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a; }
        return rgba;
    }

    /// <summary>Emission: the left half of the picture lit, the right half dark.</summary>
    private static byte[] Stripes()
    {
        byte[] rgba = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int i = (y * 8 + x) * 4;
                byte value = x < 4 ? (byte)230 : (byte)0;
                rgba[i] = value; rgba[i + 1] = value; rgba[i + 2] = value; rgba[i + 3] = 255;
            }
        return rgba;
    }

    private static void Save(byte[] pixels, int width, int height, HeadlessContext ctx, string name)
    {
        using Bitmap image = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, image.PixelFormat);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { image.UnlockBits(data); }
        image.Save(Path.Combine(ctx.Captures, name + ".png"));
    }

    private static int Distance(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
}
