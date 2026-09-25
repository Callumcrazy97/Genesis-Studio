using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Image.Dialogs;

/// <summary>Where existing artwork ends up when the canvas grows around it.</summary>
public enum CanvasAnchor
{
    /// <summary>Leave it exactly where it is — top-left of the new canvas.</summary>
    Stay,
    TopLeft,
    Top,
    TopRight,
    Left,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
}

/// <summary>What to do about an image that does not fit the canvas it is being pasted into.</summary>
public enum CanvasFitChoice
{
    /// <summary>Grow the canvas so the incoming image fits at full size.</summary>
    GrowCanvas,

    /// <summary>Keep the canvas and shrink the incoming image to fit.</summary>
    ScalePasted,
}

/// <summary>
/// Asked when a pasted image is bigger than the canvas it is landing in.
/// </summary>
/// <remarks>
/// Both possible answers destroy something if chosen silently: growing moves the existing artwork
/// relative to a canvas someone chose deliberately, and scaling resamples pixel art. So the paste
/// stops and asks, and the anchor grid makes the consequence visible before it happens — the middle
/// button leaves existing pixels exactly where they are, the eight around it push them to that side
/// of the larger canvas.
/// </remarks>
public sealed class CanvasResizeDialog : DpiAwareForm
{
    private readonly RadioButton _grow = new();
    private readonly RadioButton _scale = new();
    private readonly Panel _anchorGrid = new();
    private readonly Label _summary = new();
    private readonly List<(Button Button, CanvasAnchor Anchor)> _anchorButtons = [];
    private CanvasAnchor _anchor = CanvasAnchor.Stay;

    public CanvasResizeDialog(Size canvas, Size pasted)
    {
        CanvasSize = canvas;
        PastedSize = pasted;

        Text = "Pasted image is larger than the canvas";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(430, 336);
        BackColor = Color.FromArgb(29, 32, 40);
        ForeColor = Color.Gainsboro;

        Label headline = new()
        {
            AutoSize = false,
            Bounds = new Rectangle(16, 14, 398, 38),
            ForeColor = Color.Gainsboro,
            UseMnemonic = false,
            Text =
                $"The pasted image is {pasted.Width} × {pasted.Height}. "
                + $"This canvas is {canvas.Width} × {canvas.Height}.",
        };
        Controls.Add(headline);

        _grow.Text = $"Grow the canvas to {pasted.Width} × {pasted.Height}";
        _grow.Checked = true;
        _grow.AutoSize = true;
        _grow.Location = new Point(18, 56);
        _grow.ForeColor = Color.Gainsboro;
        _grow.UseMnemonic = false;
        _grow.CheckedChanged += (_, _) => SyncEnabled();
        Controls.Add(_grow);

        _scale.Text = $"Scale the pasted image down to fit {canvas.Width} × {canvas.Height}";
        _scale.AutoSize = true;
        _scale.Location = new Point(18, 82);
        _scale.ForeColor = Color.Gainsboro;
        _scale.UseMnemonic = false;
        Controls.Add(_scale);

        Label anchorCaption = new()
        {
            AutoSize = true,
            Location = new Point(18, 116),
            ForeColor = Color.Silver,
            UseMnemonic = false,
            Text = "Where should the artwork already on the canvas go?",
        };
        Controls.Add(anchorCaption);

        _anchorGrid.Bounds = new Rectangle(18, 140, 150, 150);
        _anchorGrid.BackColor = Color.FromArgb(24, 26, 33);
        Controls.Add(_anchorGrid);
        BuildAnchorGrid();

        _summary.AutoSize = false;
        _summary.Bounds = new Rectangle(184, 140, 230, 150);
        _summary.ForeColor = Color.Silver;
        _summary.UseMnemonic = false;
        Controls.Add(_summary);

        Button ok = new()
        {
            Text = "Paste",
            DialogResult = DialogResult.OK,
            Bounds = new Rectangle(232, 298, 88, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 91, 153),
            ForeColor = Color.WhiteSmoke,
        };
        Button cancel = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(326, 298, 88, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 49, 60),
            ForeColor = Color.WhiteSmoke,
        };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        SelectAnchor(CanvasAnchor.Stay);
        SyncEnabled();
    }

    /// <summary>The canvas as it is now.</summary>
    public Size CanvasSize { get; }

    /// <summary>The incoming image's size.</summary>
    public Size PastedSize { get; }

    /// <summary>Grow the canvas, or scale the pasted image?</summary>
    public CanvasFitChoice Choice => _scale.Checked ? CanvasFitChoice.ScalePasted : CanvasFitChoice.GrowCanvas;

    /// <summary>Where existing artwork sits on the enlarged canvas.</summary>
    /// <remarks>Named ContentAnchor because Form already has an Anchor of its own.</remarks>
    public CanvasAnchor ContentAnchor => _anchor;

    /// <summary>The size the canvas will end up.</summary>
    public Size ResultingCanvas => Choice == CanvasFitChoice.GrowCanvas
        ? new Size(Math.Max(CanvasSize.Width, PastedSize.Width), Math.Max(CanvasSize.Height, PastedSize.Height))
        : CanvasSize;

    /// <summary>Top-left at which existing artwork lands on the resulting canvas.</summary>
    public Point ContentOffset => OffsetFor(_anchor, CanvasSize, ResultingCanvas);

    /// <summary>Chooses without showing the dialog. Used by headless tests.</summary>
    public void Choose(CanvasFitChoice choice, CanvasAnchor anchor)
    {
        _scale.Checked = choice == CanvasFitChoice.ScalePasted;
        _grow.Checked = choice == CanvasFitChoice.GrowCanvas;
        SelectAnchor(anchor);
    }

    /// <summary>Offset of the old content inside a larger canvas for a given anchor.</summary>
    public static Point OffsetFor(CanvasAnchor anchor, Size content, Size canvas)
    {
        int spareX = Math.Max(0, canvas.Width - content.Width);
        int spareY = Math.Max(0, canvas.Height - content.Height);
        return anchor switch
        {
            CanvasAnchor.Stay => Point.Empty,
            CanvasAnchor.TopLeft => Point.Empty,
            CanvasAnchor.Top => new Point(spareX / 2, 0),
            CanvasAnchor.TopRight => new Point(spareX, 0),
            CanvasAnchor.Left => new Point(0, spareY / 2),
            CanvasAnchor.Right => new Point(spareX, spareY / 2),
            CanvasAnchor.BottomLeft => new Point(0, spareY),
            CanvasAnchor.Bottom => new Point(spareX / 2, spareY),
            CanvasAnchor.BottomRight => new Point(spareX, spareY),
            _ => Point.Empty,
        };
    }

    private void BuildAnchorGrid()
    {
        (CanvasAnchor Anchor, string Glyph, string Tip)[] cells =
        [
            (CanvasAnchor.TopLeft, "↖", "Existing artwork moves to the top-left corner"),
            (CanvasAnchor.Top, "↑", "Existing artwork moves to the top edge"),
            (CanvasAnchor.TopRight, "↗", "Existing artwork moves to the top-right corner"),
            (CanvasAnchor.Left, "←", "Existing artwork moves to the left edge"),
            (CanvasAnchor.Stay, "Stay", "Existing artwork does not move"),
            (CanvasAnchor.Right, "→", "Existing artwork moves to the right edge"),
            (CanvasAnchor.BottomLeft, "↙", "Existing artwork moves to the bottom-left corner"),
            (CanvasAnchor.Bottom, "↓", "Existing artwork moves to the bottom edge"),
            (CanvasAnchor.BottomRight, "↘", "Existing artwork moves to the bottom-right corner"),
        ];

        ToolTip tips = new();
        for (int index = 0; index < cells.Length; index++)
        {
            (CanvasAnchor anchor, string glyph, string tip) = cells[index];
            Button button = new()
            {
                Text = glyph,
                Bounds = new Rectangle(6 + ((index % 3) * 46), 6 + ((index / 3) * 46), 42, 42),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 49, 60),
                ForeColor = Color.Gainsboro,
                Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 9f),
                UseMnemonic = false,
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(68, 73, 87);
            button.Click += (_, _) => SelectAnchor(anchor);
            tips.SetToolTip(button, tip);
            _anchorGrid.Controls.Add(button);
            _anchorButtons.Add((button, anchor));
        }
    }

    private void SelectAnchor(CanvasAnchor anchor)
    {
        _anchor = anchor;
        foreach ((Button button, CanvasAnchor candidate) in _anchorButtons)
        {
            bool selected = candidate == anchor;
            button.BackColor = selected ? Color.FromArgb(45, 91, 153) : Color.FromArgb(45, 49, 60);
            button.FlatAppearance.BorderColor = selected
                ? Color.FromArgb(120, 165, 255)
                : Color.FromArgb(68, 73, 87);
        }

        UpdateSummary();
    }

    private void SyncEnabled()
    {
        _anchorGrid.Enabled = _grow.Checked;
        foreach ((Button button, _) in _anchorButtons)
        {
            button.Enabled = _grow.Checked;
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (Choice == CanvasFitChoice.ScalePasted)
        {
            _summary.Text =
                $"The canvas stays {CanvasSize.Width} × {CanvasSize.Height} and the pasted image is "
                + "scaled to fit. Existing artwork does not move.";
            return;
        }

        Point offset = ContentOffset;
        Size result = ResultingCanvas;
        _summary.Text =
            $"The canvas becomes {result.Width} × {result.Height}.\r\n\r\n"
            + (offset == Point.Empty
                ? "Existing artwork stays where it is, at 0, 0."
                : $"Existing artwork moves to {offset.X}, {offset.Y}.")
            + "\r\n\r\nNothing is resampled — pixels keep their size.";
    }
}
