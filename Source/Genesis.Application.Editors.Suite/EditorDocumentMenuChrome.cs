using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// Shared File/Edit dropdowns for 3D editors. Save stays pinned on the command bar; these menus
/// make the same document actions discoverable next to View/Camera/Tools.
/// </summary>
internal static class EditorDocumentMenuChrome
{
    public static ToolStripDropDownButton BuildFileMenu(
        IEditorSurface surface,
        IEnumerable<ToolStripItem>? extraItems = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ToolStripDropDownButton menu = Menu("File", "Save and resource import/export");
        ToolStripMenuItem save = Item("Save", "Ctrl+S", "Save this resource", surface.Save);
        menu.DropDownItems.Add(save);
        AppendExtras(menu, extraItems);
        return menu;
    }

    public static ToolStripDropDownButton BuildEditMenu(
        IEditorSurface surface,
        IEnumerable<ToolStripItem>? extraItems = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ToolStripDropDownButton menu = Menu("Edit", "Undo, redo, and editor-specific edits");
        ToolStripMenuItem undo = Item("Undo", "Ctrl+Z", "Undo the latest document edit", surface.Undo);
        ToolStripMenuItem redo = Item("Redo", "Ctrl+Y", "Redo the latest undone document edit", surface.Redo);
        menu.DropDownItems.Add(undo);
        menu.DropDownItems.Add(redo);
        menu.DropDownOpening += (_, _) =>
        {
            undo.Enabled = surface.CanUndo;
            redo.Enabled = surface.CanRedo;
        };
        AppendExtras(menu, extraItems);
        return menu;
    }

    public static ToolStripDropDownButton BuildToolsMenu(
        string tooltip,
        IEnumerable<ToolStripItem> items)
    {
        ToolStripDropDownButton menu = Menu("Tools", tooltip);
        foreach (ToolStripItem item in items)
        {
            menu.DropDownItems.Add(item);
        }

        return menu;
    }

    public static ToolStripMenuItem Item(string text, string tooltip, Action action) =>
        Item(text, shortcut: string.Empty, tooltip, action);

    public static ToolStripMenuItem Item(string text, string shortcut, string tooltip, Action action)
    {
        ToolStripMenuItem item = new(text)
        {
            ToolTipText = tooltip,
        };
        if (!string.IsNullOrWhiteSpace(shortcut))
        {
            item.ShortcutKeyDisplayString = shortcut;
        }

        item.Click += (_, _) => action();
        return item;
    }

    private static ToolStripDropDownButton Menu(string text, string tooltip) => new(text)
    {
        DisplayStyle = ToolStripItemDisplayStyle.Text,
        ForeColor = EditorChrome.Text,
        ToolTipText = tooltip,
    };

    private static void AppendExtras(ToolStripDropDownButton menu, IEnumerable<ToolStripItem>? extraItems)
    {
        if (extraItems is null)
        {
            return;
        }

        bool addedSeparator = false;
        foreach (ToolStripItem extra in extraItems)
        {
            if (!addedSeparator)
            {
                menu.DropDownItems.Add(new ToolStripSeparator());
                addedSeparator = true;
            }

            menu.DropDownItems.Add(extra);
        }
    }
}
