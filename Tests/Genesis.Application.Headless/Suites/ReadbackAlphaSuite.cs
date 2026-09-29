using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

internal static class ReadbackAlphaSuite
{
    public static void Run(HeadlessContext ctx)
    {
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            HeadlessHarness.RunCase(ctx.Report, "Render.Readback.AlphaAndPresentedPixels." + backend.ShortName, () => Check(ctx, backend));
        HeadlessHarness.RunCase(ctx.Report, "Render.DX12.UploadPages.PreserveTextureCopiesAcrossFrameReuse", () => UploadPages());
    }

    private static void UploadPages()
    {
        using Form host = GateSuite.NewHost(320, 180);
        GateSuite.ShowHost(host);
        IGpuDevice device = RenderControllerFactory.CreateDevice(RenderBackendOption.Direct3D12);
        using GpuRenderController renderer = new(device);
        renderer.Initialize(host.Handle, 320, 180);
        List<GpuTextureHandle> textures = [];
        GpuBufferHandle oversized = device.CreateBuffer(new GpuBufferDesc { SizeBytes = 40 * 1024 * 1024,
            Usage = GpuBufferUsage.Dynamic, BindFlags = GpuBindFlags.VertexBuffer }, []);
        try
        {
            // Reuse every frame slot several times. No readback may flush the burst early.
            for (int frame = 0; frame < 7; frame++)
            {
                renderer.BeginFrame();
                for (int number = 0; number < 10; number++)
                {
                    byte red = (byte)(31 + number * 17), green = (byte)(frame * 23), blue = (byte)(211 - number * 13);
                    byte[] rgba = new byte[1024 * 1024 * 4];
                    for (int index = 0; index < rgba.Length; index += 4)
                    { rgba[index] = red; rgba[index + 1] = green; rgba[index + 2] = blue; rgba[index + 3] = 255; }
                    textures.Add(device.CreateTexture(new GpuTextureDesc { Width = 1024, Height = 1024, MipLevels = 1, ArrayLayers = 1,
                        Format = GpuFormat.R8G8B8A8UNorm, BindFlags = GpuBindFlags.ShaderResource, Usage = GpuBufferUsage.Immutable }, rgba));
                }
                Assert(device.TryMapDiscard(oversized, out var mapped, 40 * 1024 * 1024) && mapped.Length == 40 * 1024 * 1024,
                    "A single upload larger than the base page was rejected.");
                mapped.Fill((byte)(frame + 1)); device.Unmap(oversized);
                renderer.SetCamera2D(160, 90, 1, 0); renderer.Clear(.1f, .2f, .4f, 1);
                renderer.DrawRect(30, 30, 50, 40, new(1, 0, 0, 1));
                renderer.EndFrame(); renderer.Present();
                for (int number = 0; number < textures.Count; number++)
                {
                    Assert(device.TryReadTexture(textures[number], out _, out _, out byte[] pixels), "Overflow page texture readback failed.");
                    for (int index = 0; index < pixels.Length; index += 4)
                        Assert(pixels[index] == 211 - number * 13 && pixels[index + 1] == frame * 23
                            && pixels[index + 2] == 31 + number * 17 && pixels[index + 3] == 255,
                            "Upload pages overwrote an earlier texture copy during frame slot reuse.");
                    device.ReleaseTexture(textures[number]);
                }
                textures.Clear();
            }
        }
        finally
        {
            foreach (var texture in textures) device.ReleaseTexture(texture);
            device.ReleaseBuffer(oversized);
        }
    }

    private static void Check(HeadlessContext ctx, RenderBackendDescriptor backend)
    {
        using Form host = GateSuite.NewHost(320, 180);
        GateSuite.ShowHost(host);
        IGpuDevice device = RenderControllerFactory.CreateDevice(backend.Backend);
        using GpuRenderController renderer = new(device);
        renderer.Initialize(host.Handle, 320, 180);
        string expected = backend.Backend is RenderBackendOption.OpenGL or RenderBackendOption.Software ? backend.ShortName : backend.DisplayName;
        Assert(renderer.BackendName == expected, "Readback used another renderer: " + renderer.BackendName);

        byte[] rgba = [11, 22, 33, 0, 55, 66, 77, 64, 101, 122, 144, 128, 201, 222, 244, 255];
        GpuTextureHandle texture = device.CreateTexture(new GpuTextureDesc { Width = 2, Height = 2, MipLevels = 1, ArrayLayers = 1,
            Format = GpuFormat.R8G8B8A8UNorm, Usage = GpuBufferUsage.Immutable, BindFlags = GpuBindFlags.ShaderResource }, rgba);
        try
        {
            Assert(device.TryReadTexture(texture, out int width, out int height, out byte[] pixels) && width == 2 && height == 2,
                "Raw texture readback was unavailable.");
            for (int index = 0; index < rgba.Length; index += 4)
                Assert(pixels[index] == rgba[index + 2] && pixels[index + 1] == rgba[index + 1]
                    && pixels[index + 2] == rgba[index] && pixels[index + 3] == rgba[index + 3],
                    "Raw texture readback lost channel order or authored transparent/partial alpha at pixel " + index / 4);
        }
        finally { device.ReleaseTexture(texture); }

        GpuRenderTargetHandle target = device.CreateRenderTarget(new GpuRenderTargetDesc { Width = 16, Height = 16,
            ColorFormats = [GpuFormat.B8G8R8A8UNorm] });
        try
        {
            foreach (float alpha in new[] { 0f, .25f, .5f, 1f })
            {
                device.BeginFrame();
                device.BeginRenderPass(new GpuRenderPassDesc { Target = target,
                    ColorActions = [GpuAttachmentAction.Clear(.2f, .4f, .6f, alpha)] });
                device.EndRenderPass(); device.EndFrame(); device.WaitIdle();
                Assert(device.TryReadTexture(device.GetRenderTargetTexture(target), out int width, out int height, out byte[] pixels)
                    && width == 16 && height == 16, "Offscreen attachment readback was unavailable.");
                for (int index = 0; index < pixels.Length; index += 4)
                    Assert(Math.Abs(pixels[index] - 153) <= 1 && Math.Abs(pixels[index + 1] - 102) <= 1
                        && Math.Abs(pixels[index + 2] - 51) <= 1 && Math.Abs(pixels[index + 3] - alpha * 255) <= 1,
                        "Offscreen readback altered color or alpha " + alpha + " at pixel " + index / 4);
            }
        }
        finally { device.ReleaseRenderTarget(target); }

        renderer.Set3DFrameActive(false);
        renderer.SetCamera2D(160, 90, 1, 0);
        renderer.BeginFrame(); renderer.Clear(.1f, .2f, .4f, 0);
        renderer.DrawRect(40, 50, 180, 100, new RenderColor(1, 0, 0, .5f));
        renderer.DrawLine(40, 160, 220, 160, new RenderColor(0, 0, 1, .5f), 8);
        renderer.DrawText("Visible alpha blend · " + backend.ShortName, 12, 12, 15, RenderColor.White);
        renderer.EndFrame(); renderer.ComposeOverlay(static _ => { });
        Assert(renderer.TryReadFramePixels(out int frameWidth, out int frameHeight, out byte[] frame)
            && frameWidth == 320 && frameHeight == 180, "Presented frame readback was unavailable.");
        for (int index = 3; index < frame.Length; index += 4)
            Assert(frame[index] == 255, "A presented frame was captured as transparent at pixel " + index / 4);
        int background = (170 * frameWidth + 300) * 4, blended = (100 * frameWidth + 120) * 4;
        Assert(Math.Abs(frame[background] - 102) <= 2 && Math.Abs(frame[background + 1] - 51) <= 2 && Math.Abs(frame[background + 2] - 26) <= 2,
            $"Opaque capture normalization changed background RGB: {frame[background]},{frame[background + 1]},{frame[background + 2]}.");
        Assert(frame[blended + 2] is > 100 and < 180 && frame[blended] is > 35 and < 70 && frame[blended + 1] is > 15 and < 40,
            $"Translucent drawing did not blend visibly against the actual background: {frame[blended]},{frame[blended + 1]},{frame[blended + 2]}.");
        int line = (160 * frameWidth + 120) * 4;
        Assert(frame[line] is > 140 and < 210 && frame[line + 1] is > 15 and < 40 && frame[line + 2] is > 5 and < 25,
            "Line opacity or its thickness origin differs from its authored position.");
        using Bitmap bitmap = new(frameWidth, frameHeight, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, frameWidth, frameHeight), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { for (int y = 0; y < frameHeight; y++) Marshal.Copy(frame, y * frameWidth * 4, data.Scan0 + y * data.Stride, frameWidth * 4); }
        finally { bitmap.UnlockBits(data); }
        string file = "readback-alpha-" + backend.ShortName + ".png";
        bitmap.Save(Path.Combine(ctx.Captures, file), ImageFormat.Png);
        using Bitmap reopened = new(Path.Combine(ctx.Captures, file));
        Assert(reopened.GetPixel(300, 170).A == 255 && reopened.GetPixel(120, 100).A == 255,
            "PNG save/reopen restored swap-chain transparency.");
        ctx.Report.Images.Add(ImageResult.From("Readback alpha · " + expected, file, VisualCapture.Measure(reopened)));
        renderer.Present();
    }

    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
