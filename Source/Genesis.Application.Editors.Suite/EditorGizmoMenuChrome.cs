using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite;

/// <summary>Shared Gizmo menu: move/rotate/scale plus world/local orientation.</summary>
internal static class EditorGizmoMenuChrome
{
    public static ToolStripDropDownButton BuildGizmoMenu(
        string tooltip,
        Func<EditorGizmoMode> readMode,
        Action<EditorGizmoMode> writeMode,
        Func<EditorGizmoSpace> readSpace,
        Action<EditorGizmoSpace> writeSpace,
        Action? invalidate = null,
        bool includeRotate = true,
        bool includeScale = true)
    {
        ToolStripDropDownButton menu = new("Gizmo")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = tooltip,
        };

        List<ToolStripMenuItem> modeItems = [];
        foreach (EditorGizmoMode mode in Enum.GetValues<EditorGizmoMode>())
        {
            if (mode == EditorGizmoMode.Rotate && !includeRotate) continue;
            if (mode == EditorGizmoMode.Scale && !includeScale) continue;

            EditorGizmoMode captured = mode;
            ToolStripMenuItem item = new(captured.ToString());
            item.Click += (_, _) =>
            {
                writeMode(captured);
                invalidate?.Invoke();
            };
            modeItems.Add(item);
            menu.DropDownItems.Add(item);
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        ToolStripMenuItem world = new("World")
        {
            ToolTipText = "Align gizmo axes to the world",
        };
        world.Click += (_, _) =>
        {
            writeSpace(EditorGizmoSpace.World);
            invalidate?.Invoke();
        };
        ToolStripMenuItem local = new("Local")
        {
            ToolTipText = "Align gizmo axes to the selection",
        };
        local.Click += (_, _) =>
        {
            writeSpace(EditorGizmoSpace.Local);
            invalidate?.Invoke();
        };
        menu.DropDownItems.Add(world);
        menu.DropDownItems.Add(local);

        menu.DropDownOpening += (_, _) =>
        {
            EditorGizmoMode currentMode = readMode();
            foreach (ToolStripMenuItem item in modeItems)
            {
                item.Checked = string.Equals(item.Text, currentMode.ToString(), StringComparison.Ordinal);
            }

            EditorGizmoSpace space = readSpace();
            world.Checked = space == EditorGizmoSpace.World;
            local.Checked = space == EditorGizmoSpace.Local;
        };

        return menu;
    }
}
