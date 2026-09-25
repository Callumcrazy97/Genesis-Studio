using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Software;
using Genesis.Shared.Interfaces;
using SharedCamera = Genesis.Shared.Rendering.Camera;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// NEXT-130 / NEXT-131: dedicated pixels on <see cref="SoftwareRasterizerCore"/>, which SDL3 and
/// WebGPU also call. Play path: Preferences → Software renderer, then walk the camera through a
/// wall or up to an oblique textured quad.
/// </summary>
internal static class SoftwareRasterizerClipPerspectiveCases
{
    private const int MagentaB = 255;
    private const int MagentaG = 0;
    private const int MagentaR = 255;

    public static void Register(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Render.Software.NearPlaneClipRejectsDegenerateFill", () =>
        {
            const int width = 96;
            const int height = 96;
            SharedCamera camera = MakeCamera(width, height, nearPlane: 0.5f);
            Matrix4x4 viewProjection = camera.ViewProjection;

            HeadlessHarness.Step("a triangle fully in front of the near plane still draws", () =>
            {
                byte[] pixels = Rasterize(
                    width,
                    height,
                    viewProjection,
                    [
                        Vertex(new Vector3(-0.4f, -0.4f, -4f)),
                        Vertex(new Vector3(0.4f, -0.4f, -4f)),
                        Vertex(new Vector3(0f, 0.4f, -4f)),
                    ],
                    [0, 1, 2]);
                int filled = CountDrawn(pixels);
                HeadlessHarness.Assert(
                    filled > 20,
                    $"In-frustum probe drew {filled} pixels; the clip fixture cannot run if the CPU path is silent.");
            });

            HeadlessHarness.Step("geometry entirely behind the camera is discarded", () =>
            {
                byte[] pixels = Rasterize(
                    width,
                    height,
                    viewProjection,
                    [
                        Vertex(new Vector3(-1f, -1f, 3f)),
                        Vertex(new Vector3(1f, -1f, 3f)),
                        Vertex(new Vector3(0f, 1f, 3f)),
                    ],
                    [0, 1, 2]);
                int filled = CountDrawn(pixels);
                HeadlessHarness.Assert(
                    filled == 0,
                    $"A triangle behind the camera painted {filled} pixels; homogeneous clip should drop it.");
            });

            HeadlessHarness.Step("a near-plane straddle does not flood the frame", () =>
            {
                byte[] pixels = Rasterize(
                    width,
                    height,
                    viewProjection,
                    [
                        Vertex(new Vector3(0f, 0f, 2.5f)),
                        Vertex(new Vector3(-2.2f, -1.4f, -8f)),
                        Vertex(new Vector3(2.2f, -1.4f, -8f)),
                    ],
                    [0, 1, 2]);

                int filled = CountDrawn(pixels);
                int total = width * height;
                HeadlessHarness.Assert(
                    filled > 40,
                    $"Near-plane straddle drew {filled} pixels; the visible remnant should still appear.");
                HeadlessHarness.Assert(
                    filled * 100 < total * 45,
                    $"Near-plane straddle filled {filled}/{total} pixels. "
                    + "A w-clamp / no-clip path projects the behind-camera vertex into a screen-filling degenerate.");

                int clearCorners = 0;
                if (IsClear(pixels, width, 0, 0)) clearCorners++;
                if (IsClear(pixels, width, width - 1, 0)) clearCorners++;
                if (IsClear(pixels, width, 0, height - 1)) clearCorners++;
                if (IsClear(pixels, width, width - 1, height - 1)) clearCorners++;
                HeadlessHarness.Assert(
                    clearCorners >= 3,
                    $"Only {clearCorners} of 4 corners stayed clear; a degenerate clip flash paints the whole buffer.");
            });
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Software.ObliqueQuadUvsMatchPerspectiveCorrect", () =>
        {
            const int width = 128;
            const int height = 128;
            const int texWidth = 256;
            SharedCamera camera = MakeCamera(width, height, nearPlane: 0.25f);
            Matrix4x4 viewProjection = camera.ViewProjection;

            // Near-left / far-right trapezoid: U tracks depth, so affine U at a screen-space
            // interior pixel is ~0.5 while perspective-correct U stays nearer the close edge.
            MeshVertex v0 = Vertex(new Vector3(-0.55f, -0.55f, -0.85f), new Vector2(0f, 0f));
            MeshVertex v1 = Vertex(new Vector3(0.55f, -0.55f, -12f), new Vector2(1f, 0f));
            MeshVertex v2 = Vertex(new Vector3(0.55f, 0.55f, -12f), new Vector2(1f, 1f));
            MeshVertex v3 = Vertex(new Vector3(-0.55f, 0.55f, -0.85f), new Vector2(0f, 1f));
            byte[] texture = new byte[texWidth * 4];
            for (int i = 0; i < texWidth; i++)
            {
                int o = i * 4;
                texture[o] = (byte)i;
                texture[o + 1] = 255;
                texture[o + 2] = 255;
                texture[o + 3] = 255;
            }

            byte[] pixels = Rasterize(
                width,
                height,
                viewProjection,
                [v0, v1, v2, v3],
                [0, 1, 2, 0, 2, 3],
                texture,
                texWidth,
                texHeight: 1);

            int samples = 0;
            int mismatches = 0;
            float maxError = 0f;
            float maxAffineGap = 0f;
            string? firstMismatch = null;

            for (int y = 8; y < height - 8; y++)
            {
                for (int x = 8; x < width - 8; x++)
                {
                    if (!TryUvAtPixel(v0, v1, v2, viewProjection, width, height, x, y, out Vector2 persp, out Vector2 affine)
                        && !TryUvAtPixel(v0, v2, v3, viewProjection, width, height, x, y, out persp, out affine))
                    {
                        continue;
                    }

                    float affineGap = MathF.Abs(persp.X - affine.X);
                    if (affineGap < 0.08f)
                        continue;

                    maxAffineGap = MathF.Max(maxAffineGap, affineGap);
                    float sampledU = ReadFrameRed(pixels, width, x, y) / 255f;
                    float error = MathF.Abs(sampledU - persp.X);
                    maxError = MathF.Max(maxError, error);
                    samples++;
                    if (error > 3.5f / 255f + 0.5f / texWidth)
                    {
                        mismatches++;
                        firstMismatch ??=
                            $"({x},{y}) sampledU={sampledU:0.000} perspU={persp.X:0.000} affineU={affine.X:0.000}";
                    }
                }
            }

            HeadlessHarness.Assert(
                samples >= 40,
                $"Oblique-quad fixture only found {samples} interior pixels where affine and perspective UVs differ. "
                + "The quad is no longer diagnostic, or nothing rasterized.");
            HeadlessHarness.Assert(
                maxAffineGap >= 0.12f,
                $"Largest affine/perspective U gap was {maxAffineGap:0.000}; the quad is not oblique enough.");
            HeadlessHarness.Assert(
                mismatches == 0,
                $"Software UVs followed affine interpolation (or drifted) on {mismatches} pixels; "
                + $"max |sampled-persp|={maxError:0.000}. First: {firstMismatch}");
        });
    }

    private static SharedCamera MakeCamera(int width, int height, float nearPlane) => new()
    {
        Position = Vector3.Zero,
        Yaw = 0f,
        Pitch = 0f,
        FieldOfView = MathF.PI / 2f,
        AspectRatio = width / (float)height,
        NearPlane = nearPlane,
        FarPlane = 40f,
    };

    private static MeshVertex Vertex(Vector3 position, Vector2? uv = null) => new()
    {
        Position = position,
        Normal = Vector3.UnitY,
        Color = Vector4.One,
        UV = uv ?? Vector2.Zero,
    };

    private static byte[] Rasterize(
        int width,
        int height,
        Matrix4x4 viewProjection,
        MeshVertex[] vertices,
        ushort[] indices,
        byte[]? texPixels = null,
        int texWidth = 0,
        int texHeight = 0)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = MagentaB;
            pixels[i + 1] = MagentaG;
            pixels[i + 2] = MagentaR;
            pixels[i + 3] = 255;
        }

        float[] depth = new float[width * height];
        Array.Fill(depth, 1f);

        PerFrameCB perFrame = new()
        {
            ViewProjection = viewProjection,
            LightViewProjection = Matrix4x4.Identity,
            LightViewProjectionNear = Matrix4x4.Identity,
        };
        DrawCB drawCb = new()
        {
            WorldMatrix = Matrix4x4.Identity,
            MaterialColor = Vector4.One,
            MaterialParams = new Vector4(0f, 1f, 0f, 0f),
        };

        SoftwareMeshDraw draw = new()
        {
            FramePixels = pixels,
            DepthBuffer = depth,
            ScreenW = width,
            ScreenH = height,
            VbBytes = MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray(),
            VertexStride = Unsafe.SizeOf<MeshVertex>(),
            IbBytes = MemoryMarshal.AsBytes(indices.AsSpan()).ToArray(),
            IndexFormat = GpuIndexFormat.UInt16,
            IndexCount = indices.Length,
            Topology = GpuPrimitiveTopology.TriangleList,
            PerFrameCbBytes = Blit(in perFrame),
            DrawCbBytes = Blit(in drawCb),
            TexPixels = texPixels ?? [],
            TexWidth = texWidth,
            TexHeight = texHeight,
            TexFormat = GpuFormat.R8G8B8A8UNorm,
            RasterState = GpuRasterState.NoCull,
            DepthState = GpuDepthState.Default,
            BlendState = GpuBlendState.Opaque,
            RuntimeShade = SoftwareRuntimeShade.Unlit,
        };
        SoftwareRasterizerCore.RasterizeMesh3D(in draw);
        return pixels;
    }

    private static byte[] Blit<T>(in T value) where T : unmanaged
    {
        byte[] bytes = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(bytes.AsSpan(), in value);
        return bytes;
    }

    private static int CountDrawn(byte[] pixels)
    {
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (!IsClearAt(pixels, i))
                count++;
        }

        return count;
    }

    private static bool IsClear(byte[] pixels, int width, int x, int y) =>
        IsClearAt(pixels, (y * width + x) * 4);

    private static bool IsClearAt(byte[] pixels, int offset) =>
        pixels[offset] == MagentaB && pixels[offset + 1] == MagentaG && pixels[offset + 2] == MagentaR;

    private static float ReadFrameRed(byte[] pixels, int width, int x, int y)
    {
        int offset = (y * width + x) * 4;
        return pixels[offset + 2];
    }

    private static bool TryUvAtPixel(
        in MeshVertex v0,
        in MeshVertex v1,
        in MeshVertex v2,
        Matrix4x4 mvp,
        int screenW,
        int screenH,
        int px,
        int py,
        out Vector2 perspectiveUv,
        out Vector2 affineUv)
    {
        perspectiveUv = default;
        affineUv = default;
        Vector4 c0 = Vector4.Transform(new Vector4(v0.Position, 1f), mvp);
        Vector4 c1 = Vector4.Transform(new Vector4(v1.Position, 1f), mvp);
        Vector4 c2 = Vector4.Transform(new Vector4(v2.Position, 1f), mvp);
        if (c0.W < 1e-6f || c1.W < 1e-6f || c2.W < 1e-6f)
            return false;

        float invW0 = 1f / c0.W;
        float invW1 = 1f / c1.W;
        float invW2 = 1f / c2.W;
        float sx0 = (c0.X * invW0 + 1f) * 0.5f * screenW;
        float sy0 = (1f - c0.Y * invW0) * 0.5f * screenH;
        float sx1 = (c1.X * invW1 + 1f) * 0.5f * screenW;
        float sy1 = (1f - c1.Y * invW1) * 0.5f * screenH;
        float sx2 = (c2.X * invW2 + 1f) * 0.5f * screenW;
        float sy2 = (1f - c2.Y * invW2) * 0.5f * screenH;
        float det = (sx1 - sx0) * (sy2 - sy0) - (sy1 - sy0) * (sx2 - sx0);
        if (MathF.Abs(det) <= 0.0001f)
            return false;

        float invDet = 1f / det;
        float pxC = px + 0.5f;
        float pyC = py + 0.5f;
        float wa = ((sx1 - pxC) * (sy2 - pyC) - (sy1 - pyC) * (sx2 - pxC)) * invDet;
        float wb = ((sx2 - pxC) * (sy0 - pyC) - (sy2 - pyC) * (sx0 - pxC)) * invDet;
        float wc = 1f - wa - wb;
        if (wa < 0f || wb < 0f || wc < 0f)
            return false;

        affineUv = wa * v0.UV + wb * v1.UV + wc * v2.UV;
        float pa = wa * invW0;
        float pb = wb * invW1;
        float pc = wc * invW2;
        float pSum = pa + pb + pc;
        if (pSum <= 1e-12f)
            return false;

        float invP = 1f / pSum;
        perspectiveUv = (pa * v0.UV + pb * v1.UV + pc * v2.UV) * invP;
        return true;
    }
}
