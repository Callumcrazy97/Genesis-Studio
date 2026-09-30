using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Genesis.Application.Core.UI;

/// <summary>
/// The "Clear" tool strip look: rounded hover and pressed pills, an accent-tinted pill for a
/// checked mode, and an accent outline for the primary command (Use in game, Run, Play).
/// </summary>
/// <remarks>
/// Drop-down menus keep the professional renderer's solid rows — they open over arbitrary content
/// and must read as opaque surfaces. Only items that sit on a bar get pills.
/// </remarks>
public class PillToolStripRenderer : ToolStripProfessionalRenderer
{
    /// <summary>Tag an item with this to draw it as the strip's primary command.</summary>
    public const string PrimaryTag = "primary-command";

    public const int PillRadius = 7;

    public PillToolStripRenderer()
        : this(new PillColorTable())
    {
    }

    public PillToolStripRenderer(ProfessionalColorTable table)
        : base(table)
    {
        RoundedEdges = false;
    }

    public static bool IsPrimary(ToolStripItem item) =>
        string.Equals(item.Tag as string, PrimaryTag, StringComparison.Ordinal);

    public static void MarkPrimary(ToolStripItem item) => item.Tag = PrimaryTag;

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderToolStripBorder(e);
            return;
        }

        if (e.ToolStrip is StatusStrip || e.ToolStrip.Dock is DockStyle.Left or DockStyle.Right)
        {
            return;
        }

        // A single hairline under a bar separates it from the content; the professional
        // renderer's rounded 3D border read as a dated raised panel.
        using Pen rule = new(UiTokens.Blend(UiTokens.Border, UiTokens.Surface, 0.7f));
        int y = e.ToolStrip.Height - 1;
        e.Graphics.DrawLine(rule, 0, y, e.ToolStrip.Width, y);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderButtonBackground(e);
            return;
        }

        bool isChecked = e.Item is ToolStripButton { Checked: true };
        DrawPill(e.Graphics, e.Item, isChecked);
    }

    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderDropDownButtonBackground(e);
            return;
        }

        DrawPill(e.Graphics, e.Item, isChecked: false);
    }

    protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown || e.Item is not ToolStripSplitButton split)
        {
            base.OnRenderSplitButtonBackground(e);
            return;
        }

        DrawPill(e.Graphics, e.Item, isChecked: false);
        Rectangle arrow = split.DropDownButtonBounds;
        if (split.Selected)
        {
            using Pen divider = new(UiTokens.Blend(UiTokens.Border, UiTokens.Hover, 0.6f));
            e.Graphics.DrawLine(divider, arrow.Left, arrow.Top + 6, arrow.Left, arrow.Bottom - 7);
        }

        DrawArrow(new ToolStripArrowRenderEventArgs(
            e.Graphics,
            split,
            arrow,
            split.Enabled ? UiTokens.Text : UiTokens.Muted,
            ArrowDirection.Down));
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.ToolStrip is not MenuStrip)
        {
            base.OnRenderMenuItemBackground(e);
            return;
        }

        if (e.Item.Selected || e.Item.Pressed)
        {
            Rectangle bounds = PillBounds(e.Item.Size, inset: 2);
            FillRound(e.Graphics, bounds, e.Item.Pressed ? UiTokens.Raised : UiTokens.Hover, PillRadius);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown && e.Item.Enabled)
        {
            if (IsPrimary(e.Item))
            {
                e.TextColor = UiTokens.Accent;
            }
            else if (e.Item is ToolStripButton { Checked: true })
            {
                e.TextColor = UiTokens.Text;
            }
        }

        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderSeparator(e);
            return;
        }

        using Pen line = new(UiTokens.Blend(UiTokens.Border, UiTokens.Surface, 0.8f));
        Size size = e.Item.Size;
        if (e.Vertical)
        {
            int x = size.Width / 2;
            e.Graphics.DrawLine(line, x, 7, x, size.Height - 8);
        }
        else
        {
            int y = size.Height / 2;
            e.Graphics.DrawLine(line, 6, y, size.Width - 6, y);
        }
    }

    private static void DrawPill(Graphics graphics, ToolStripItem item, bool isChecked)
    {
        bool primary = IsPrimary(item);
        bool active = item.Selected || item.Pressed;
        if (!isChecked && !primary && !active)
        {
            return;
        }

        Rectangle bounds = PillBounds(item.Size, inset: 2);
        if (isChecked)
        {
            FillRound(graphics, bounds, active ? UiTokens.Blend(UiTokens.Accent, UiTokens.Surface, 0.34f) : UiTokens.AccentSoft, PillRadius);
            StrokeRound(graphics, bounds, UiTokens.Blend(UiTokens.Accent, UiTokens.Surface, 0.7f), PillRadius);
            return;
        }

        if (active)
        {
            FillRound(graphics, bounds, item.Pressed ? UiTokens.Raised : primary ? UiTokens.AccentSoft : UiTokens.Hover, PillRadius);
        }

        if (primary)
        {
            StrokeRound(graphics, bounds, item.Enabled ? UiTokens.Accent : UiTokens.Border, PillRadius);
        }
    }

    internal static Rectangle PillBounds(Size size, int inset) =>
        new(1, inset, Math.Max(1, size.Width - 2), Math.Max(1, size.Height - inset * 2));

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics graphics, Rectangle bounds, Color fill, int radius)
    {
        using GraphicsPath path = RoundedRect(bounds, radius);
        using SolidBrush brush = new(fill);
        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.FillPath(brush, path);
        graphics.SmoothingMode = previous;
    }

    public static void StrokeRound(Graphics graphics, Rectangle bounds, Color stroke, int radius)
    {
        Rectangle inner = new(bounds.X, bounds.Y, Math.Max(1, bounds.Width - 1), Math.Max(1, bounds.Height - 1));
        using GraphicsPath path = RoundedRect(inner, radius);
        using Pen pen = new(stroke);
        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawPath(pen, path);
        graphics.SmoothingMode = previous;
    }

    /// <summary>Solid drop-down colours from the live tokens.</summary>
    private sealed class PillColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => UiTokens.Surface;
        public override Color MenuStripGradientEnd => UiTokens.Surface;
        public override Color ToolStripGradientBegin => UiTokens.Surface;
        public override Color ToolStripGradientMiddle => UiTokens.Surface;
        public override Color ToolStripGradientEnd => UiTokens.Surface;
        public override Color ToolStripBorder => UiTokens.Border;
        public override Color MenuBorder => UiTokens.Border;
        public override Color MenuItemBorder => UiTokens.Hover;
        public override Color MenuItemSelected => UiTokens.Hover;
        public override Color MenuItemSelectedGradientBegin => UiTokens.Hover;
        public override Color MenuItemSelectedGradientEnd => UiTokens.Hover;
        public override Color MenuItemPressedGradientBegin => UiTokens.Raised;
        public override Color MenuItemPressedGradientEnd => UiTokens.Raised;
        public override Color ButtonSelectedBorder => UiTokens.Accent;
        public override Color ButtonSelectedGradientBegin => UiTokens.Hover;
        public override Color ButtonSelectedGradientEnd => UiTokens.Hover;
        public override Color ButtonSelectedHighlight => UiTokens.Hover;
        public override Color ButtonPressedGradientBegin => UiTokens.Raised;
        public override Color ButtonPressedGradientEnd => UiTokens.Raised;
        public override Color ButtonCheckedGradientBegin => UiTokens.AccentSoft;
        public override Color ButtonCheckedGradientEnd => UiTokens.AccentSoft;
        public override Color ButtonCheckedHighlight => UiTokens.AccentSoft;
        public override Color CheckBackground => UiTokens.AccentSoft;
        public override Color CheckSelectedBackground => UiTokens.Accent;
        public override Color ToolStripDropDownBackground => UiTokens.Raised;
        public override Color ImageMarginGradientBegin => UiTokens.Raised;
        public override Color ImageMarginGradientMiddle => UiTokens.Raised;
        public override Color ImageMarginGradientEnd => UiTokens.Raised;
        public override Color SeparatorDark => UiTokens.Border;
        public override Color SeparatorLight => UiTokens.Border;
        public override Color OverflowButtonGradientBegin => UiTokens.Raised;
        public override Color OverflowButtonGradientMiddle => UiTokens.Raised;
        public override Color OverflowButtonGradientEnd => UiTokens.Raised;
        public override Color StatusStripGradientBegin => UiTokens.Surface;
        public override Color StatusStripGradientEnd => UiTokens.Surface;
    }
}
