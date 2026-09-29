using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelRigViewportControl
{
    private bool _layingOutAnimation;

    private void LayoutAnimationPresentation()
    {
        if (_layingOutAnimation || _animationPrimaryRow is null || _animationPoseTools is null
            || _animationPanel.Parent is not TableLayoutPanel layout) return;
        _layingOutAnimation = true;
        try
        {
            if (_animationDraft && !_animationPanel.Visible)
            {
                layout.RowStyles[2].Height = 0;
                return;
            }
            int rows = _animationPrimaryRow.Height + (_animationPoseTools.Visible ? _animationPoseTools.Height : 0);
            int timelineHeight = 124;
            if (_modelFrameStrip?.Parent is TableLayoutPanel timeline)
            {
                timeline.RowStyles[1].Height = Math.Max(48, _modelFrameStrip.Font.Height + 24);
                Control header = timeline.GetControlFromPosition(0, 0)!;
                int headerHeight = header.GetPreferredSize(new Size(timeline.ClientSize.Width, 0)).Height;
                timelineHeight = headerHeight + (int)timeline.RowStyles[1].Height + Math.Max(70, _modelFrameStrip.Font.Height * 2 + 36);
            }
            int minimum = rows + timelineHeight;
            int available = layout.ClientSize.Height - (int)layout.RowStyles[0].Height - 280;
            int height = Math.Clamp(minimum + 34, minimum, Math.Max(minimum, Math.Min(400, available)));
            if (Math.Abs(layout.RowStyles[2].Height - height) > 1)
            {
                layout.RowStyles[2].Height = height;
                // SizeChanged can run inside the table's current layout pass. Apply the
                // new absolute row after that pass instead of retaining its old cell size.
                if (layout.IsHandleCreated && !layout.IsDisposed && !layout.Disposing)
                    layout.BeginInvoke(() => { if (!layout.IsDisposed && !layout.Disposing) layout.PerformLayout(); });
            }
        }
        finally { _layingOutAnimation = false; }
    }

    /// <summary>A frame ruler using the existing clip/transport state; it does not invent keyframes.</summary>
    private sealed class ModelFrameStrip : Control
    {
        public int FrameCount { get; set; }
        public int SelectedFrame { get; set; }
        public int[] PoseFrames { get; set; } = [];
        public event Action<int>? FrameSelected;

        public ModelFrameStrip()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleName = "Model animation frames";
            AccessibleRole = AccessibleRole.Slider;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(EditorChrome.Canvas);
            if (FrameCount == 0)
            {
                TextRenderer.DrawText(e.Graphics, "No animation selected", EditorChrome.SmallFont, ClientRectangle,
                    EditorChrome.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            using var border = new Pen(EditorChrome.Border);
            using var accent = new Pen(EditorChrome.Accent, 2);
            using var fill = new SolidBrush(EditorChrome.Accent);
            int left = 12, right = Math.Max(13, Width - 16);
            int y = Math.Max(EditorChrome.SmallFont.Height + 12, Height - 13);
            e.Graphics.DrawLine(border, left, y, right, y);
            int step = Math.Max(1, (int)Math.Ceiling(FrameCount / Math.Max(1d, (right - left) / 48d)));
            for (int frame = 0; frame < FrameCount; frame += step)
            {
                int x = FrameX(frame);
                e.Graphics.DrawLine(border, x, 26, x, y + 3);
                TextRenderer.DrawText(e.Graphics, (frame + 1).ToString(), EditorChrome.SmallFont,
                    new Rectangle(x - 5, 5, 48, Math.Max(19, EditorChrome.SmallFont.Height + 3)), EditorChrome.Muted);
            }
            int selected = FrameX(SelectedFrame);
            foreach (int frame in PoseFrames.Where(f => f >= 0 && f < FrameCount))
            {
                int x = FrameX(frame);
                e.Graphics.FillPolygon(fill, [new Point(x, y - 11), new Point(x + 5, y - 6), new Point(x, y - 1), new Point(x - 5, y - 6)]);
            }
            e.Graphics.DrawLine(accent, selected, 24, selected, y + 4);
            e.Graphics.FillPolygon(fill, [new Point(selected - 5, 23), new Point(selected + 5, 23), new Point(selected, 29)]);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
        }

        private int FrameX(int frame) => 12 + (int)(Math.Clamp(frame, 0, Math.Max(0, FrameCount - 1))
            / (double)Math.Max(1, FrameCount - 1) * Math.Max(1, Width - 28));

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || FrameCount < 1) return;
            Focus(); Capture = true; Seek(e.X);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Capture && e.Button == MouseButtons.Left) Seek(e.X);
        }
        protected override void OnMouseUp(MouseEventArgs e) { Capture = false; base.OnMouseUp(e); }
        private void Seek(int x) => FrameSelected?.Invoke(Math.Clamp((int)Math.Round((x - 12d)
            / Math.Max(1, Width - 28) * (FrameCount - 1)), 0, Math.Max(0, FrameCount - 1)));

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (FrameCount < 1) return;
            int target = e.KeyCode switch { Keys.Home => 0, Keys.End => FrameCount - 1,
                Keys.Left => SelectedFrame - 1, Keys.Right => SelectedFrame + 1, _ => SelectedFrame };
            FrameSelected?.Invoke(Math.Clamp(target, 0, FrameCount - 1));
        }
    }

    private readonly record struct ModelCurveSnapshot(float[] Values, int SelectedFrame, string Channel);

    /// <summary>
    /// Compact F-curve surface for the selected bone channel. The points are the clip's authored
    /// frame samples, and dragging writes through the same pose/track path as the numeric inspector.
    /// </summary>
    private sealed class ModelBoneCurveView(Func<ModelCurveSnapshot> read) : Control
    {
        private static int LeftInset => Math.Max(42, TextRenderer.MeasureText("0.000", EditorChrome.SmallFont).Width + 6);
        private const int RightInset = 10;
        private const int TopInset = 8;
        private static int BottomInset => Math.Max(20, EditorChrome.SmallFont.Height + 6);
        private bool _editing;
        private int _editFrame = -1;
        private float _editMinimum;
        private float _editMaximum;

        public event Action<int>? FrameSelected;
        public event Action<int>? EditBegan;
        public event Action<int, float>? ValueEdited;
        public event Action? EditEnded;

        public ModelBoneCurveView() : this(() => new ModelCurveSnapshot([], 0, string.Empty)) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(EditorChrome.Canvas);
            ModelCurveSnapshot snapshot = read();
            Rectangle plot = PlotBounds();
            using Pen grid = new(EditorChrome.Border);
            for (int i = 0; i <= 4; i++)
            {
                int y = plot.Top + plot.Height * i / 4;
                e.Graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
            }
            for (int i = 0; i <= 8; i++)
            {
                int x = plot.Left + plot.Width * i / 8;
                e.Graphics.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            }

            if (snapshot.Values.Length == 0)
            {
                TextRenderer.DrawText(e.Graphics, "Select an animation clip and bone to edit its curve", EditorChrome.SmallFont,
                    plot, EditorChrome.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            (float minimum, float maximum) = Range(snapshot.Values, snapshot.Channel);
            PointF[] points = new PointF[snapshot.Values.Length];
            for (int i = 0; i < points.Length; i++) points[i] = new PointF(FrameX(i, points.Length, plot), ValueY(snapshot.Values[i], minimum, maximum, plot));
            using Pen curve = new(EditorChrome.Accent, 2.2f);
            if (points.Length > 1) e.Graphics.DrawCurve(curve, points, 0.25f);
            using SolidBrush point = new(EditorChrome.Accent);
            using SolidBrush selected = new(Color.White);
            for (int i = 0; i < points.Length; i++)
            {
                float radius = i == snapshot.SelectedFrame ? 4.5f : 2.8f;
                e.Graphics.FillEllipse(i == snapshot.SelectedFrame ? selected : point,
                    points[i].X - radius, points[i].Y - radius, radius * 2f, radius * 2f);
            }
            int selectedFrame = Math.Clamp(snapshot.SelectedFrame, 0, snapshot.Values.Length - 1);
            int captionHeight = Math.Max(18, EditorChrome.SmallFont.Height + 2);
            using Pen playhead = new(Color.FromArgb(150, EditorChrome.Accent), 1.2f);
            e.Graphics.DrawLine(playhead, points[selectedFrame].X, plot.Top, points[selectedFrame].X, plot.Bottom);
            TextRenderer.DrawText(e.Graphics, maximum.ToString("0.###"), EditorChrome.SmallFont,
                new Rectangle(2, plot.Top - 2, LeftInset - 5, captionHeight), EditorChrome.Muted, TextFormatFlags.Right);
            TextRenderer.DrawText(e.Graphics, minimum.ToString("0.###"), EditorChrome.SmallFont,
                new Rectangle(2, plot.Bottom - captionHeight + 2, LeftInset - 5, captionHeight), EditorChrome.Muted, TextFormatFlags.Right);
            TextRenderer.DrawText(e.Graphics, $"{snapshot.Channel}  ·  {snapshot.Values[selectedFrame]:0.###}", EditorChrome.SmallFont,
                new Rectangle(plot.Left, plot.Bottom + 1, plot.Width, captionHeight), EditorChrome.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            ModelCurveSnapshot snapshot = read();
            if (snapshot.Values.Length == 0) return;
            int frame = FrameAt(e.X, snapshot.Values.Length, PlotBounds());
            _editFrame = frame;
            (_editMinimum, _editMaximum) = Range(snapshot.Values, snapshot.Channel);
            _editing = true;
            Capture = true;
            FrameSelected?.Invoke(frame);
            EditBegan?.Invoke(frame);
            ValueEdited?.Invoke(frame, ValueAt(e.Y, _editMinimum, _editMaximum, PlotBounds(), snapshot.Channel));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_editing || !Capture || e.Button != MouseButtons.Left) return;
            ModelCurveSnapshot snapshot = read();
            if (snapshot.Values.Length == 0) return;
            if (_editFrame < 0) return;
            ValueEdited?.Invoke(_editFrame, ValueAt(e.Y, _editMinimum, _editMaximum, PlotBounds(), snapshot.Channel));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_editing)
            {
                _editing = false;
                _editFrame = -1;
                Capture = false;
                EditEnded?.Invoke();
            }
            base.OnMouseUp(e);
        }

        private Rectangle PlotBounds() => new(LeftInset, TopInset,
            Math.Max(1, Width - LeftInset - RightInset), Math.Max(1, Height - TopInset - BottomInset));

        private static (float Minimum, float Maximum) Range(float[] values, string channel)
        {
            float minimum = values.Min(), maximum = values.Max();
            float minimumSpan = channel.StartsWith("Rotation", StringComparison.Ordinal) ? 30f
                : channel.StartsWith("Scale", StringComparison.Ordinal) ? 0.5f : 1f;
            float span = maximum - minimum;
            if (span < minimumSpan)
            {
                float centre = (minimum + maximum) * 0.5f;
                minimum = centre - minimumSpan * 0.5f;
                maximum = centre + minimumSpan * 0.5f;
            }
            else
            {
                minimum -= span * 0.12f;
                maximum += span * 0.12f;
            }
            return (minimum, maximum);
        }

        private static float FrameX(int frame, int count, Rectangle plot)
            => plot.Left + frame / (float)Math.Max(1, count - 1) * plot.Width;

        private static float ValueY(float value, float minimum, float maximum, Rectangle plot)
            => plot.Bottom - (value - minimum) / Math.Max(0.0001f, maximum - minimum) * plot.Height;

        private static int FrameAt(int x, int count, Rectangle plot)
            => Math.Clamp((int)MathF.Round((x - plot.Left) / (float)Math.Max(1, plot.Width) * (count - 1)), 0, count - 1);

        private static float ValueAt(int y, float minimum, float maximum, Rectangle plot, string channel)
        {
            float value = maximum - Math.Clamp((y - plot.Top) / (float)Math.Max(1, plot.Height), 0f, 1f) * (maximum - minimum);
            return channel.StartsWith("Scale", StringComparison.Ordinal) ? Math.Max(0.001f, value) : value;
        }
    }
}
