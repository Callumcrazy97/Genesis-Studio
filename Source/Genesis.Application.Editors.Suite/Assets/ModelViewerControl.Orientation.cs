using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public partial class ModelViewerControl
{
    /// <summary>Camera-relative axis widget. Positive and negative tips select the corresponding view.</summary>
    private sealed class OrientationWidget : Control
    {
        private readonly EditorViewport3D _viewport;
        private readonly List<(RectangleF Bounds, string View)> _hits = [];
        public OrientationWidget(EditorViewport3D viewport)
        {
            _viewport = viewport; Size = new Size(98, 98); BackColor = EditorChrome.Canvas;
            DoubleBuffered = true; Cursor = Cursors.Hand; AccessibleName = "3D orientation axes";
        }
        public void Draw(IRenderController renderer)
        {
            _hits.Clear(); if (!Enabled) return;
            var origin = new Vector2(_viewport.HasSecondaryCamera ? 59 : _viewport.SurfaceWidth - 59, 59);
            foreach (var axis in new[] { (Vector3.UnitX, EditorTransformGizmo.AxisX, "X", "Right", "Left"), (Vector3.UnitY, EditorTransformGizmo.AxisY, "Y", "Top", "Bottom"), (Vector3.UnitZ, EditorTransformGizmo.AxisZ, "Z", "Front", "Back") }
                .SelectMany(a => new[] { (Axis: a, Sign: -1), (Axis: a, Sign: 1) }).OrderBy(a => Vector3.TransformNormal(a.Axis.Item1 * a.Sign, _viewport.Camera.View).Z))
            {
                var projected = Vector3.TransformNormal(axis.Axis.Item1 * axis.Sign, _viewport.Camera.View);
                var tip = origin + new Vector2(projected.X, -projected.Y) * 31;
                RenderColor colour = axis.Sign > 0 ? axis.Axis.Item2 : new RenderColor(.25f,.29f,.36f);
                renderer.DrawLine(origin.X, origin.Y, tip.X, tip.Y, colour, 2, -9900);
                for (int y = -9; y <= 9; y++)
                {
                    float w = MathF.Sqrt(81 - y * y);
                    renderer.DrawLine(tip.X - w, tip.Y + y, tip.X + w, tip.Y + y, colour, 1, -9910);
                }
                if (axis.Sign > 0) renderer.DrawText(axis.Axis.Item3, tip.X + 12, tip.Y - 7, 12, axis.Axis.Item2);
                _hits.Add((new RectangleF(tip.X - 11, tip.Y - 11, 22, 22), axis.Sign > 0 ? axis.Axis.Item4 : axis.Axis.Item5));
            }
        }
        public bool HitTest(Point point) => Hit(point) is not null;
        private string? Hit(Point point)
        {
            if (!Enabled) return null;
            var scaled = new PointF(point.X * _viewport.SurfaceWidth / (float)Math.Max(1, _viewport.Host.Width), point.Y * _viewport.SurfaceHeight / (float)Math.Max(1, _viewport.Host.Height));
            for (int i = _hits.Count - 1; i >= 0; i--) if (_hits[i].Bounds.Contains(scaled)) return _hits[i].View;
            return null;
        }
        public bool SelectAt(Point point)
        {
            if (Hit(point) is not { } view) return false;
            FindViewer()?.SetCameraView(view); return true;
        }
        private ModelViewerControl? FindViewer()
        {
            for (Control? parent = Parent; parent is not null; parent = parent.Parent) if (parent is ModelViewerControl viewer) return viewer;
            return null;
        }
    }
}
