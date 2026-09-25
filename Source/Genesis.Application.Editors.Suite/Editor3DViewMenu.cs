using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite;

internal static class Editor3DViewMenu
{
    internal static void AddTo(ToolStripDropDownItem menu, Func<EditorViewport3D?> viewport,
        EditorViewMenuChrome.ToggleBinding? wireframe = null,
        EditorViewMenuChrome.ToggleBinding? lighting = null)
    {
        if (menu.DropDownItems.Count > 0) menu.DropDownItems.Add(new ToolStripSeparator());
        var normals = Item("Normals", "Inspect surface normals as RGB colours.");
        var wire = Item("Wireframe", "Show mesh triangle edges.");
        var shadows = Item("Shadows", "Enable shadow rendering in this preview.");
        var lit = Item("Lighting", "Enable scene lighting in this preview.");
        var depth = Item("Depth", "Inspect camera depth: near surfaces are light, distant surfaces dark.");
        normals.Click += (_, _) => SetDebug(RenderDebugView.Normals);
        depth.Click += (_, _) => SetDebug(RenderDebugView.SceneDepth);
        wire.Click += (_, _) =>
        {
            if (viewport() is not { } view || view.Mode2D) return;
            bool value = !(wireframe?.Read() ?? view.InspectionState.Wireframe);
            if (wireframe is not null) { wireframe.Write(value); wireframe.Invalidate?.Invoke(); }
            else view.ViewSettings.Wireframe = value;
            Refresh(); view.Invalidate(true);
        };
        lit.Click += (_, _) =>
        {
            if (viewport() is not { } view || view.Mode2D) return;
            bool value = !(lighting?.Read() ?? view.InspectionState.LightingEnabled);
            if (lighting is not null) { lighting.Write(value); lighting.Invalidate?.Invoke(); }
            else view.ViewSettings.Lighting = value;
            Refresh(); view.Invalidate(true);
        };
        shadows.Click += (_, _) =>
        {
            if (viewport() is not { } view || view.Mode2D) return;
            view.ViewSettings.Shadows = !view.InspectionState.ShadowsEnabled;
            Refresh(); view.Invalidate(true);
        };
        menu.DropDownOpening += (_, _) => Refresh();


        ToolStripMenuItem Item(string name, string tip)
        {
            var item = new ToolStripMenuItem(name) { Name = "View3D" + name, ToolTipText = tip };
            menu.DropDownItems.Add(item); return item;
        }
        void SetDebug(RenderDebugView mode)
        {
            if (viewport() is not { } view || view.Mode2D) return;
            view.ViewSettings.DebugView = view.ViewSettings.DebugView == mode ? RenderDebugView.Shaded : mode;
            Refresh(); view.Invalidate(true);
        }
        void Refresh()
        {
            var view = viewport();
            foreach (var item in new[] { normals, wire, shadows, lit, depth }) item.Enabled = view is not null && !view.Mode2D;
            if (view is null) return;
            var state = view.InspectionState;
            normals.Checked = state.DebugView == RenderDebugView.Normals;
            depth.Checked = state.DebugView == RenderDebugView.SceneDepth;
            wire.Checked = wireframe?.Read() ?? state.Wireframe;
            shadows.Checked = state.ShadowsEnabled;
            lit.Checked = lighting?.Read() ?? state.LightingEnabled;
        }
    }
}
