using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Headless.Suites;

internal static class RoomMetricGridToolsSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Toolbar.ExclusiveToolsKeyboardAndMetricGrid", () =>
        {
            var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomMetricGridTools"), "Metric room");
            var resources = new ResourceService(project);
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Metric grid");
            RoomAssetLoader.Save(RoomAsset.Create("Metric grid", RoomDimension.ThreeD), path);
            using var editor = new RoomEditorControl(path, project.RootPath);
            using var host = UnattendedWindowing.NewHost(1380, 880);
            host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
            ToolStripButton[] tools = editor.EditorToolbar.Strip.Items.OfType<ToolStripButton>()
                .Where(button => button.Text is "Select" or "Move" or "Rotate" or "Scale").ToArray();
            Assert(tools.Length == 4, "A primary room tool is missing from the command bar.");
            CheckTool("Select", false);
            foreach ((string name, RoomEditorControl.GizmoKind kind) in new[]
            {
                ("Move", RoomEditorControl.GizmoKind.Move), ("Rotate", RoomEditorControl.GizmoKind.Rotate), ("Scale", RoomEditorControl.GizmoKind.Scale),
            })
            {
                tools.Single(button => button.Text == name).PerformClick();
                CheckTool(name, true);
                Assert(editor.Gizmo == kind, "Toolbar changed its highlight without switching the actual gizmo.");
            }
            editor.SetGizmoSpace(EditorGizmoSpace.Local);
            CheckTool("Scale", true);
            tools.Single(button => button.Text == "Select").PerformClick();
            CheckTool("Select", false);
            Assert(editor.GizmoSpace == EditorGizmoSpace.Local, "Selecting a tool reset the independent local/world setting.");
            foreach ((Keys key, string tool) in new[] { (Keys.W, "Move"), (Keys.E, "Rotate"), (Keys.R, "Scale"), (Keys.Q, "Select") })
            {
                object?[] args = [new Message(), key];
                bool handled = (bool)typeof(RoomEditorControl).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(editor, args)!;
                Assert(handled, "Room shortcut was not handled.");
                CheckTool(tool, key != Keys.Q);
            }
            editor.SetTool(RoomEditorControl.RoomTool.Place);
            tools.Single(button => button.Text == "Move").PerformClick();
            CheckTool("Move", true);
            Assert(editor.ActiveTool == RoomEditorControl.RoomTool.Select, "Move left the room in placement mode.");

            editor.Viewport.Camera.Target = new Vector3(37, 0, -19);
            editor.Viewport.Camera.Distance = 4;
            editor.Viewport.Camera.Yaw = .4f; editor.Viewport.Camera.Pitch = -.7f;
            editor.SetGridVisible(true);
            foreach (float step in new[] { .1f, .5f, 1f, 2f })
            {
                editor.EditorToolbar.SnapSizePicker.SelectedItem = FormattableString.Invariant($"{step:0.0#} m").Replace("1.0 m", "1 m").Replace("2.0 m", "2 m");
                Warm();
                RoomMetricGridLayout layout = editor.LastMetricGridLayout ?? throw new InvalidOperationException("Visible 3D grid was not submitted.");
                Assert(MathF.Abs(editor.MetricGridSize - step) < .00001f && MathF.Abs(layout.RequestedSpacing - step) < .00001f,
                    "Metric dropdown and actual grid disagree about the selected increment.");
                Assert(MathF.Abs(layout.DisplaySpacing - step) < .00001f && MathF.Abs(layout.MajorSpacing - step * 5) < .0001f,
                    "The close-up grid still clamps small metre increments or spaces major lines incorrectly.");
                Assert(editor.LastMetricGridDrawnLines > 0 && editor.LastMetricGridDrawnLines <= layout.LineCount && layout.LineCount <= 642,
                    "Grid was invisible or generated an unbounded number of lines.");
                RoomMetricGridLine[] lines = layout.Lines().Where(line => line.From.X == line.To.X).ToArray();
                Assert(lines.Length > 1 && MathF.Abs(lines[1].From.X - lines[0].From.X - step) < .0001f,
                    "Displayed world grid lines do not use the chosen metric spacing.");
                Editor3DInspectionSuite.Capture(ctx, host, "room-metric-grid-" + FormattableString.Invariant($"{step:0.0}").Replace('.', '-') + "m");
            }
            RoomMetricGridLayout distant = RoomMetricGridLayout.Create(.01f, new Vector3(1_000_000, 0, -1_000_000), 1_000_000, 600, 60);
            Assert(distant.DisplaySpacing > distant.RequestedSpacing && distant.LineCount <= 642,
                "Distant tiny increments generated excessive grid lines instead of decimating display spacing.");
            Assert(distant.Lines().All(line => float.IsFinite(line.From.X) && float.IsFinite(line.To.Z)), "Distant grid contains invalid coordinates.");
            editor.SetGridVisible(false); Warm();
            Assert(editor.LastMetricGridLayout is null && editor.LastMetricGridDrawnLines == 0, "Hiding the grid retained stale grid submissions.");

            void CheckTool(string expected, bool gizmo)
            {
                Assert(tools.Count(button => button.Checked) == 1 && tools.Single(button => button.Checked).Text == expected,
                    "Select/Move/Rotate/Scale have contradictory checked states.");
                Assert(editor.TransformGizmoVisible == gizmo, "Select did not hide the gizmo or a transform tool failed to show it.");
            }
            void Warm() { GateSuite.Pump(2, 15); using (editor.Viewport.CaptureFrame(2)) { } }
        });
    }

    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
