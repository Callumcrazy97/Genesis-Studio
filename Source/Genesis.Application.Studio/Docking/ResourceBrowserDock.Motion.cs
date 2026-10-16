using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Docking;

/// <summary>What a tree row does when the resource behind it changes.</summary>
internal enum TreeRowMotionKind
{
    None,
    /// <summary>Created, duplicated or pasted: the row slides in with an accent glow.</summary>
    Arrival,
    /// <summary>Renamed: the row's glow pulses.</summary>
    Renamed,
}

public sealed partial class ResourceBrowserDock
{
    /// <summary>One row's animation state; the tree reads it while drawing that row.</summary>
    private sealed class TreeRowMotion
    {
        public float Glow;      // 0..1 accent tint behind the row
        public float Offset;    // pixels the row's content is pushed right
        public float Opacity = 1f;
    }

    private readonly Dictionary<string, TreeRowMotion> _rowMotion = new(StringComparer.OrdinalIgnoreCase);

    private void AnimateRow(string? path, TreeRowMotionKind kind)
    {
        if (string.IsNullOrWhiteSpace(path) || kind == TreeRowMotionKind.None || !UiMotion.Enabled) return;
        string key = Path.GetFullPath(path);
        TreeRowMotion motion = new();
        _rowMotion[key] = motion;
        float slide = DpiLayout.Scale(this, 16);
        UiMotion.Animate(_tree, "row:" + key, kind == TreeRowMotionKind.Arrival ? 520 : 700, t =>
        {
            if (kind == TreeRowMotionKind.Arrival)
            {
                float enter = Math.Min(1f, t * 1.8f);
                motion.Offset = (1f - UiMotion.EaseOutBack(enter)) * slide;
                motion.Opacity = enter;
                motion.Glow = 1f - t;
            }
            else
            {
                // Two soft pulses that settle.
                motion.Glow = MathF.Abs(MathF.Sin(t * MathF.PI * 2f)) * (1f - t);
            }
            _tree.Invalidate();
        }, () => { _rowMotion.Remove(key); _tree.Invalidate(); }, ease: t => t);
    }

    /// <summary>Fades the row out and slides it away, then runs <paramref name="then"/> (the delete itself).</summary>
    private void AnimateDeparture(string path, Action then)
    {
        string key = Path.GetFullPath(path);
        TreeRowMotion motion = new();
        float slide = DpiLayout.Scale(this, 24);
        _rowMotion[key] = motion;
        UiMotion.Animate(_tree, "row:" + key, 220, t =>
        {
            motion.Opacity = 1f - t;
            motion.Offset = t * slide;
            motion.Glow = 0.35f * (1f - t);
            _tree.Invalidate();
        }, () =>
        {
            _rowMotion.Remove(key);
            if (!IsDisposed) then();
        }, UiMotion.EaseInOut);
    }

    private TreeRowMotion? MotionFor(ResourceItem? item) =>
        item is null || _rowMotion.Count == 0 ? null
            : _rowMotion.GetValueOrDefault(Path.GetFullPath(item.FullPath));

    /// <summary>Draws a row icon, faded when the row is arriving or leaving.</summary>
    private static void DrawRowIcon(Graphics graphics, Image icon, int x, int y, float opacity)
    {
        if (opacity >= 0.999f)
        {
            graphics.DrawImage(icon, x, y, icon.Width, icon.Height);
            return;
        }
        using ImageAttributes fade = new();
        fade.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(opacity, 0f, 1f) });
        graphics.DrawImage(icon, new Rectangle(x, y, icon.Width, icon.Height), 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, fade);
    }

    private static Color RowGlow(Color background, float glow) =>
        glow <= 0.001f ? background : UiMotion.Mix(background, ThemeService.Palette.Accent, Math.Clamp(glow, 0f, 1f) * 0.32f);
}
