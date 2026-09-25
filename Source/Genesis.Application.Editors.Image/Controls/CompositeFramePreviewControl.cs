using Genesis.Application.Editors.Image;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Image.Controls;

/// <summary>
/// Compact live preview of the fully-composited current frame.
/// </summary>
/// <remarks>
/// PyGenesis kept this in the image editor's right-hand frame strip. Keeping it as a reusable
/// control lets Image Viewer and Image Editor show the same flattened result without each editor
/// inventing its own bitmap conversion and spacing rules.
/// </remarks>
public sealed class CompositeFramePreviewControl : UserControl
{
    private readonly PictureBox _preview;
    private readonly Label _caption;
    private Bitmap? _bitmap;

    public CompositeFramePreviewControl()
    {
        BackColor = ImageEditorChrome.Canvas;
        BorderStyle = BorderStyle.None;
        Padding = new Padding(8, 6, 8, 8);
        MinimumSize = new Size(180, 132);

        _caption = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = ImageEditorChrome.Muted,
            Font = ImageEditorChrome.HeadingFont,
            Text = "COMPOSITE FRAME PREVIEW",
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };

        _preview = new PixelPreviewBox
        {
            BackColor = ImageEditorChrome.Raised,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
        };

        Controls.Add(_preview);
        Controls.Add(_caption);
    }

    public bool HasFrame => _preview.Image != null;
    [System.ComponentModel.DefaultValue(true)]
    public bool ShowCaption { get => _caption.Visible; set => _caption.Visible=value; }
    [System.ComponentModel.DefaultValue(false)]
    public bool ShowCheckerboard { get => ((PixelPreviewBox)_preview).Checkerboard; set { ((PixelPreviewBox)_preview).Checkerboard=value; _preview.Invalidate(); } }

    private sealed class PixelPreviewBox : PictureBox
    {
        [System.ComponentModel.DefaultValue(false)]
        public bool Checkerboard { get; set; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            if (!Checkerboard) return;
            using var light = new SolidBrush(ImageEditorChrome.Raised);
            using var dark = new SolidBrush(ImageEditorChrome.Canvas);
            for (int y=0;y<Height;y+=12) for (int x=0;x<Width;x+=12)
                e.Graphics.FillRectangle((x/12+y/12)%2==0 ? light : dark,x,y,12,12);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            base.OnPaint(e);
        }
    }

    public void SetFrame(byte[]? rgba, int width, int height)
    {
        if (rgba == null || width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            Clear();
            return;
        }

        Bitmap bitmap = RgbaToBitmap(rgba, width, height);
        Bitmap? previous = _bitmap;
        _bitmap = bitmap;
        _preview.Image = bitmap;
        previous?.Dispose();
    }

    public void Clear()
    {
        Bitmap? previous = _bitmap;
        _bitmap = null;
        _preview.Image = null;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bitmap?.Dispose();
        }

        base.Dispose(disposing);
    }

    internal static Bitmap RgbaToBitmap(byte[] rgba, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            // GDI+ stores Format32bppArgb as BGRA on little-endian Windows.
            byte[] bgra = new byte[width * height * 4];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = rgba[i + 2];
                bgra[i + 1] = rgba[i + 1];
                bgra[i + 2] = rgba[i];
                bgra[i + 3] = rgba[i + 3];
            }

            Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }
}
