using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelEditorControl
{
    private Vector3? _hover;
    private System.Windows.Forms.Timer? _frameAnimation;

    public void CancelStroke()
    {
        if (!_stroke) return;
        if (_beforeDynamicStroke is not null) ReplaceAsset(ModelPoseWorkflow.Copy(_beforeDynamicStroke));
        else if (_beforeStroke is not null) { for (int m = 0; m < _beforeStroke.Length; m++) SetVertices(Asset.Meshes[m], _beforeStroke[m]); NotifyMeshChanged(); }
        _stroke = false; _beforeStroke = null; _beforeDynamicStroke = null; _strokeMesh = -1; Surface.NavigationEnabled = true; Surface.Host.Capture = false;
    }

    private void DrawBrushCursor(IRenderController renderer)
    {
        if (_hover is not { } hit || _mode is not (ModelEditorMode.Paint or ModelEditorMode.Sculpt)) return;
        var world = hit - Asset.Pivot.Position;
        var center = Surface.WorldToSurface(world);
        if (center.Z < 0 || center.Z > 1) return;
        Matrix4x4.Invert(Surface.ViewMatrix, out var camera);
        var right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, camera));
        var edge = Surface.WorldToSurface(world + right * _radius);
        float radius = Math.Clamp(Vector2.Distance(new(center.X, center.Y), new(edge.X, edge.Y)), 2, Surface.SurfaceWidth * 2);
        EditorTransformGizmo.DrawCircleOutline(renderer, new(center.X, center.Y), radius, RenderColor.Black, 3);
        EditorTransformGizmo.DrawCircleOutline(renderer, new(center.X, center.Y), radius, new RenderColor(.8f, .9f, 1), 1);
    }

    public void TransformSelectedMesh(Vector3 position, Vector3 rotationDegrees, Vector3 scale)
    {
        int index = _groups.SelectedIndex; if (index < 0) return;
        if (!Finite(position) || !Finite(rotationDegrees) || !Finite(scale) || scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0)
            throw new ArgumentException("Use finite positions/rotations and positive scale values.");
        ChangeAsset("Transform selection", () =>
        {
            var selectedMeshes = _selection.Count == 0 ? new[] {index} : _selection.Select(v => v.Mesh).Distinct().ToArray();
            var selectedPositions = selectedMeshes.SelectMany(m => Vertices(Asset.Meshes[m]).Where((_,v) => _selection.Count == 0 || _selection.Contains((m,v)))).Select(v => v.Position).ToArray();
            if (selectedPositions.Length == 0) return;
            var center = (selectedPositions.Aggregate(Vector3.Min) + selectedPositions.Aggregate(Vector3.Max)) * .5f;
            foreach (int meshIndex in selectedMeshes)
            {
            var mesh = Asset.Meshes[meshIndex]; var vertices = Vertices(mesh);
            if (vertices.Length == 0) return;
            Vector3 radians = rotationDegrees * (MathF.PI / 180);
            Matrix4x4 linear = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z);
            Matrix4x4.Invert(linear, out var inverse); Matrix4x4 normal = Matrix4x4.Transpose(inverse);
            for (int i = 0; i < vertices.Length; i++)
            {
                if (_selection.Count > 0 && !_selection.Contains((meshIndex,i))) continue;
                vertices[i].Position = Vector3.Transform(vertices[i].Position - center, linear) + center + position;
                Vector3 transformed = Vector3.TransformNormal(vertices[i].Normal, normal);
                vertices[i].Normal = transformed.LengthSquared() > 1e-12f ? Vector3.Normalize(transformed) : Vector3.UnitY;
            }
            SetVertices(mesh, vertices);
            }
        });
        static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private void ShowTransformWindow()
    {
        if (_groups.SelectedIndex < 0) { Status.Text = "Select a mesh to transform."; return; }
        using var dialog = new Form { Text = "Transform mesh · " + _groups.SelectedItem, ClientSize = new Size(410, 242), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false };
        layout.Controls.Add(new Label { Text = "Position offsets · rotation / scale around the mesh centre", AutoSize = true });
        var values = new NumericUpDown[3, 3];
        for (int row = 0; row < 3; row++)
        {
            var line = new FlowLayoutPanel { Width = 384, Height = 42 };
            line.Controls.Add(new Label { Text = new[] { "Position", "Rotation °", "Scale" }[row], Width = 74, Height = 32 });
            for (int axis = 0; axis < 3; axis++)
            {
                line.Controls.Add(new Label { Text = "XYZ"[axis].ToString(), Width = 13, Height = 28 });
                var number = new NumericUpDown { Width = 74, DecimalPlaces = 3, Increment = row == 1 ? 5 : .1m, Minimum = row == 2 ? .001m : -10000, Maximum = 10000, Value = row == 2 ? 1 : 0 };
                EditorChrome.StyleField(number); values[row, axis] = number; line.Controls.Add(number);
            }
            layout.Controls.Add(line);
        }
        var buttons = new FlowLayoutPanel { Width = 380, Height = 36, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 100 }; var apply = new Button { Text = "Apply", DialogResult = DialogResult.OK, Width = 100 };
        EditorChrome.StyleField(cancel); EditorChrome.StyleField(apply); buttons.Controls.Add(cancel); buttons.Controls.Add(apply); layout.Controls.Add(buttons);
        dialog.Controls.Add(layout); dialog.AcceptButton = apply; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) == DialogResult.OK) TransformSelectedMesh(Row(0), Row(1), Row(2));
        Vector3 Row(int row) => new((float)values[row, 0].Value, (float)values[row, 1].Value, (float)values[row, 2].Value);
    }

    private void FrameSelection()
    {
        List<Vector3> points = [];
        foreach (IGrouping<int, (int Mesh, int Vertex)> group in _selection.GroupBy(item => item.Mesh))
        {
            if ((uint)group.Key >= Asset.Meshes.Count) continue;
            MeshVertex[] vertices = Vertices(Asset.Meshes[group.Key]);
            foreach ((int Mesh, int Vertex) item in group)
                if ((uint)item.Vertex < vertices.Length) points.Add(vertices[item.Vertex].Position - Asset.Pivot.Position);
        }
        foreach ((int mesh, int face) in _selectedFaces)
        {
            if ((uint)mesh >= Asset.Meshes.Count) continue;
            GModelMesh data = Asset.Meshes[mesh];
            MeshVertex[] vertices = Vertices(data);
            int offset = face * 3;
            if ((uint)(offset + 2) >= data.Indices.Length) continue;
            points.Add(vertices[data.Indices[offset]].Position - Asset.Pivot.Position);
            points.Add(vertices[data.Indices[offset + 1]].Position - Asset.Pivot.Position);
            points.Add(vertices[data.Indices[offset + 2]].Position - Asset.Pivot.Position);
        }
        foreach ((int mesh, ushort a, ushort b) in _selectedEdges)
        {
            if ((uint)mesh >= Asset.Meshes.Count) continue;
            GModelMesh data = Asset.Meshes[mesh];
            MeshVertex[] vertices = Vertices(data);
            if (a < vertices.Length) points.Add(vertices[a].Position - Asset.Pivot.Position);
            if (b < vertices.Length) points.Add(vertices[b].Position - Asset.Pivot.Position);
        }
        if (points.Count == 0 && SelectedMesh() is { } selected)
            points.AddRange(Vertices(selected).Select(vertex => vertex.Position - Asset.Pivot.Position));
        if (points.Count == 0)
        {
            (Vector3 min, Vector3 max) = ModelBounds;
            points.Add(min); points.Add(max);
        }
        Vector3 target = (points.Aggregate(Vector3.Min) + points.Aggregate(Vector3.Max)) * .5f;
        float radius = MathF.Max(.1f, Vector3.Distance(points.Aggregate(Vector3.Min), points.Aggregate(Vector3.Max)) * .5f);
        float distance = Math.Clamp(radius * 2.35f, 1.2f, Surface.Camera.MaximumDistance);
        AnimateFrame(target, distance);
    }

    private void AnimateFrame(Vector3 target, float distance)
    {
        _frameAnimation?.Stop();
        _frameAnimation?.Dispose();
        Vector3 fromTarget = Surface.Camera.Target;
        float fromDistance = Surface.Camera.Distance;
        int frame = 0;
        _frameAnimation = new System.Windows.Forms.Timer { Interval = 16 };
        _frameAnimation.Tick += (_, _) =>
        {
            float t = Math.Clamp(++frame / 12f, 0f, 1f);
            float eased = 1f - MathF.Pow(1f - t, 3f);
            Surface.Camera.Target = Vector3.Lerp(fromTarget, target, eased);
            Surface.Camera.Distance = float.Lerp(fromDistance, distance, eased);
            Surface.Invalidate(true);
            if (t < 1f) return;
            _frameAnimation?.Stop();
            _frameAnimation?.Dispose();
            _frameAnimation = null;
        };
        _frameAnimation.Start();
    }
}
