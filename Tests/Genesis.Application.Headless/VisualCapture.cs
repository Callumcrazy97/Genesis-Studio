using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Genesis.Rendering.Viewport;

namespace Genesis.Application.Headless;

internal static class VisualCapture
{
    /// <summary>Capture an already-hosted workflow without closing or disposing its form.</summary>
    public static ImageMetrics CaptureOpenForm(Form form, string outputFile, bool includeViewports = false)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        string? directory = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Size size = WaitForRenderableSize(form, captureFromScreen: false);
        form.Invalidate(true);
        form.Refresh();
        PumpMessages(4, 20);
        return Snapshot(form, size, outputFile, captureFromScreen: includeViewports);
    }

    public static ImageMetrics Capture(Form form, string outputFile, bool captureFromScreen = false)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);

        string? directory = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        form.StartPosition = FormStartPosition.Manual;
        UnattendedWindowing.Configure(form);
        UnattendedWindowing.ShowWithoutFocus(form);
        if (captureFromScreen)
        {
            form.TopMost = true;
            form.BringToFront();
        }
        Size size = WaitForRenderableSize(form, captureFromScreen);

        form.Invalidate(true);
        form.Refresh();
        PumpMessages(captureFromScreen ? 14 : 4, captureFromScreen ? 35 : 20);

        // One retry after a further settle. DrawToBitmap or PrintWindow on a form whose first paint
        // can return an incomplete surface. That is timing, not a broken layout, so retrying is the
        // honest response. A second failure still throws with measured evidence, so a genuinely
        // blank surface is not hidden.
        ImageMetrics metrics = Snapshot(form, size, outputFile, captureFromScreen);
        if (metrics.NonTransparentRatio < 0.98 || metrics.UniqueSampledColors < 8)
        {
            PumpMessages(captureFromScreen ? 16 : 10, 35);
            form.Invalidate(true);
            form.Refresh();
            metrics = Snapshot(form, size, outputFile, captureFromScreen);
        }

        form.Close();
        form.Dispose();
        PumpMessages(4, 20);

        if (metrics.UniqueSampledColors < 8)
        {
            throw new InvalidOperationException(
                $"Capture '{Path.GetFileName(outputFile)}' appears visually blank "
                + $"({metrics.UniqueSampledColors} sampled colours).");
        }

        if (metrics.NonTransparentRatio < 0.98)
        {
            throw new InvalidOperationException(
                $"Capture '{Path.GetFileName(outputFile)}' contains excessive transparency "
                + $"({metrics.NonTransparentRatio:P1} opaque).");
        }

        return metrics;
    }

    private static ImageMetrics Snapshot(Form form, Size size, string outputFile, bool captureFromScreen)
    {
        using Bitmap bitmap = captureFromScreen
            ? CaptureWindowPixels(form, size)
            : new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        if (!captureFromScreen)
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
        }

        bitmap.Save(outputFile, ImageFormat.Png);
        return Measure(bitmap);
    }

    /// <summary>Renders a form's client pixels without reading the interactive desktop surface.</summary>
    /// <remarks>
    /// A visible, foreground, uncloaked HWND can still yield a solid-black <c>CopyFromScreen</c>
    /// result when the unattended build session has no GDI desktop backing surface. PrintWindow
    /// asks the HWND/DWM to render instead, so form captures remain deterministic. Hardware
    /// viewports keep using their dedicated DX11 readbacks; this helper only records WinForms
    /// window chrome and controls.
    /// </remarks>
    internal static Bitmap CaptureWindowPixels(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return CaptureWindowPixels(form, form.ClientSize);
    }

    private static Bitmap CaptureWindowPixels(Form form, Size size)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!form.IsHandleCreated)
        {
            throw new InvalidOperationException(
                $"Form '{form.GetType().Name}' has no HWND to render for capture.");
        }

        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidOperationException(
                $"Form '{form.GetType().Name}' has an invalid capture size {size.Width}x{size.Height}.");
        }

        // Showing an off-screen HWND does not guarantee its invalidated client has painted.
        // Flush layout/paint synchronously before asking DWM to print its client surface.
        form.PerformLayout();
        form.Invalidate(true);
        form.Refresh();

        Bitmap bitmap = new(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(bitmap);
            IntPtr deviceContext = graphics.GetHdc();
            bool rendered;
            int error;
            try
            {
                Marshal.SetLastPInvokeError(0);
                rendered = PrintWindow(
                    form.Handle,
                    deviceContext,
                    // PW_RENDERFULLCONTENT can return a stale DWM frame for off-screen HWNDs,
                    // even after Refresh. Client-only requests the current WinForms paint.
                    PrintWindowClientOnly);
                error = rendered ? 0 : Marshal.GetLastWin32Error();
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }

            if (!rendered)
            {
                if (error != 0)
                {
                    throw new Win32Exception(
                        error,
                        $"PrintWindow failed for form '{form.GetType().Name}'.");
                }

                throw new InvalidOperationException(
                    $"PrintWindow failed for form '{form.GetType().Name}' without a Win32 error code.");
            }

            // GPU swap chains do not implement WM_PRINT. Compose their own readback at the real
            // child bounds over the freshly printed controls, avoiding cached desktop/DWM pixels.
            foreach (D3DViewportControl viewport in Descendants(form).OfType<D3DViewportControl>())
            {
                if (!viewport.Visible || !viewport.IsHandleCreated
                    || viewport.Renderer is not { IsInitialized: true }) continue;
                using Bitmap frame = viewport.ReadbackFrameToBitmap(settleFrames: 1)
                    ?? throw new InvalidOperationException("GPU viewport readback was unavailable during window capture.");
                Rectangle target = new(form.PointToClient(viewport.PointToScreen(Point.Empty)), viewport.ClientSize);
                Rectangle clip = Rectangle.Intersect(target, new Rectangle(Point.Empty, size));
                for (Control? parent = viewport.Parent; parent is not null && parent != form; parent = parent.Parent)
                    clip = Rectangle.Intersect(clip, new Rectangle(
                        form.PointToClient(parent.PointToScreen(Point.Empty)), parent.ClientSize));
                if (clip.IsEmpty) continue;
                var state = graphics.Save();
                graphics.SetClip(clip);
                // Respect native sibling Z-order. A full primary swap-chain readback must not
                // paint over the second camera's title/close button or another foreground panel.
                for (Control child = viewport; child.Parent is { } parent; child = parent)
                {
                    int index = parent.Controls.GetChildIndex(child);
                    foreach (Control sibling in parent.Controls)
                    {
                        if (!sibling.Visible || parent.Controls.GetChildIndex(sibling) >= index) continue;
                        graphics.ExcludeClip(new Rectangle(form.PointToClient(sibling.PointToScreen(Point.Empty)), sibling.Size));
                    }
                    if (parent == form) break;
                }
                graphics.DrawImage(frame, target);
                graphics.Restore(state);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private const uint PrintWindowClientOnly = 0x00000001;

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    /// <summary>
    /// Pumps until the form has a handle and a usable client size, or gives up.
    /// </summary>
    /// <remarks>
    /// Show/Activate are asynchronous — the handle, the layout pass and the first paint all land
    /// whenever the message loop reaches them. This used to pump a fixed four times and then assert
    /// the size immediately, which is a race: it passed almost every run and intermittently sampled
    /// a form that was still 0x0, failing as "too small" in whichever test happened to lose. Wait
    /// on the condition instead of guessing a duration.
    /// </remarks>
    private static Size WaitForRenderableSize(Form form, bool captureFromScreen)
    {
        const int MinWidth = 300;
        const int MinHeight = 200;
        const int TimeoutMilliseconds = 5000;

        int step = captureFromScreen ? 35 : 20;
        int waited = 0;
        Size size = form.ClientSize;

        while (waited < TimeoutMilliseconds)
        {
            System.Windows.Forms.Application.DoEvents();
            size = form.ClientSize;
            if (form.IsHandleCreated && size.Width >= MinWidth && size.Height >= MinHeight)
            {
                return size;
            }

            Thread.Sleep(step);
            waited += step;
        }

        throw new InvalidOperationException(
            $"Form '{form.GetType().Name}' never reached a usable size for a visual regression "
            + $"capture: {size.Width}x{size.Height} after {TimeoutMilliseconds} ms "
            + $"(need at least {MinWidth}x{MinHeight}).");
    }

    private static ImageMetrics Measure(Bitmap bitmap)
    {
        HashSet<int> colors = [];
        int samples = 0;
        int opaque = 0;
        long luminance = 0;
        int stepX = Math.Max(1, bitmap.Width / 120);
        int stepY = Math.Max(1, bitmap.Height / 80);

        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                Color color = bitmap.GetPixel(x, y);
                colors.Add(color.ToArgb());
                samples++;
                if (color.A > 245)
                {
                    opaque++;
                }

                luminance += (color.R * 299L + color.G * 587L + color.B * 114L) / 1000L;
            }
        }

        return new ImageMetrics(
            bitmap.Width,
            bitmap.Height,
            colors.Count,
            samples == 0 ? 0 : opaque / (double)samples,
            samples == 0 ? 0 : luminance / (double)samples);
    }

    private static void PumpMessages(int iterations, int delayMilliseconds)
    {
        for (int index = 0; index < iterations; index++)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(delayMilliseconds);
        }
    }
}

internal sealed record ImageMetrics(
    int Width,
    int Height,
    int UniqueSampledColors,
    double NonTransparentRatio,
    double AverageLuminance);
