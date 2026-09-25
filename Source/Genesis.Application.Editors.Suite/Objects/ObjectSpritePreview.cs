using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>
/// Shows the image an object is bound to, inside the Object Editor.
/// </summary>
/// <remarks>
/// The image binding used to be a combo box and nothing else, so the only way to check that an
/// object was pointing at the right art was to open the Assets tree and compare names — and a
/// mis-bound object looked exactly like a correctly bound one until it was run. Drawing the frame
/// here makes the binding self-evident.
///
/// Point-sampled at whole-number scale: this is pixel art, and a bilinear-smeared preview would
/// misrepresent what the runtime draws. Frames larger than the box are fitted down instead, since
/// showing a corner of a 512px sheet would be worse than showing all of it slightly soft.
/// </remarks>
public sealed class ObjectSpritePreview : Panel
{
    private Bitmap? _frame;
    private string _caption = "No image assigned";
    private bool _showPixelSize = true;

    public ObjectSpritePreview()
    {
        BackColor = EditorChrome.Canvas;
        DoubleBuffered = true;
        Height = 104;
    }

    /// <summary>Pixel size of the frame on show, or null when there is none.</summary>
    public Size? FrameSize => _frame is null ? null : new Size(_frame.Width, _frame.Height);

    /// <summary>Whether a frame is actually being displayed. Public so a headless test can assert it.</summary>
    public bool HasImage => _frame is not null;

    public void SetFrame(Bitmap frame, string caption, bool showPixelSize = false)
    {
        _frame?.Dispose(); _frame = new Bitmap(frame); _caption = caption; _showPixelSize = showPixelSize; Invalidate();
    }

    /// <summary>
    /// Show the first frame of an image file, or clear the preview when the path is null/unreadable.
    /// </summary>
    public void SetImageFile(string? absolutePath, string? label = null)
    {
        _showPixelSize = true;
        _frame?.Dispose();
        _frame = null;

        if (!string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath))
        {
            try
            {
                // Copied out of the stream so the file is not held open — the Image Editor writes
                // to these files while this preview is on screen.
                using FileStream stream = File.OpenRead(absolutePath);
                using System.Drawing.Image loaded = System.Drawing.Image.FromStream(
                    stream, useEmbeddedColorManagement: false, validateImageData: false);
                _frame = new Bitmap(loaded);
                _caption = label ?? ResourceDisplayName.Format(absolutePath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or
                OutOfMemoryException)
            {
                _caption = $"Could not read {ResourceDisplayName.Format(absolutePath)}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(label))
        {
            _caption = label;
        }
        else
        {
            _caption = "No image assigned";
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        Rectangle bounds = ClientRectangle;
        Rectangle stage = new(bounds.X + 8, bounds.Y + 6, bounds.Width - 16, bounds.Height - 26);
        if (stage.Width <= 4 || stage.Height <= 4)
        {
            return;
        }

        DrawCheckerboard(graphics, stage);

        if (_frame is not null)
        {
            float scale = MathF.Min(
                stage.Width / (float)_frame.Width,
                stage.Height / (float)_frame.Height);
            if (scale >= 1f)
            {
                scale = MathF.Floor(scale);
            }

            int width = Math.Max(1, (int)(_frame.Width * scale));
            int height = Math.Max(1, (int)(_frame.Height * scale));
            Rectangle target = new(
                stage.X + ((stage.Width - width) / 2),
                stage.Y + ((stage.Height - height) / 2),
                width,
                height);

            graphics.InterpolationMode = scale >= 1f
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(_frame, target);
        }

        using Pen border = new(Color.FromArgb(90, EditorChrome.Border));
        graphics.DrawRectangle(border, stage);

        string caption = _frame is null || !_showPixelSize
            ? _caption
            : $"{_caption}  ·  {_frame.Width} × {_frame.Height}";
        using SolidBrush text = new(_frame is null ? EditorChrome.Muted : EditorChrome.Text);
        using StringFormat format = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        graphics.DrawString(
            caption,
            EditorChrome.SmallFont,
            text,
            new RectangleF(bounds.X + 4, stage.Bottom + 2, bounds.Width - 8, 18),
            format);
    }

    private static void DrawCheckerboard(Graphics graphics, Rectangle stage)
    {
        const int cell = 8;
        using SolidBrush dark = new(Color.FromArgb(38, 41, 50));
        using SolidBrush light = new(Color.FromArgb(48, 52, 63));
        graphics.FillRectangle(dark, stage);
        for (int y = 0; y < stage.Height; y += cell)
        {
            for (int x = 0; x < stage.Width; x += cell)
            {
                if (((x / cell) + (y / cell)) % 2 != 0)
                {
                    continue;
                }

                graphics.FillRectangle(
                    light,
                    stage.X + x,
                    stage.Y + y,
                    Math.Min(cell, stage.Width - x),
                    Math.Min(cell, stage.Height - y));
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frame?.Dispose();
            _frame = null;
        }

        base.Dispose(disposing);
    }
}
