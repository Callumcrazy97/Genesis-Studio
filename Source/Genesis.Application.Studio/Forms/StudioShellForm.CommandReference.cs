using Genesis.Application.Core.Commands;
using Genesis.Application.Studio.Docking;

namespace Genesis.Application.Studio.Forms;

public sealed partial class StudioShellForm
{
    internal PgslCommandReferenceForm CreateCommandReference()
    {
        ShellCommandContext context = CaptureCommandContext();
        CommandReferenceEntry Describe(StudioCommand<ShellCommandContext> command)
        {
            CommandAvailability availability = _commands.GetAvailability(command.Id, context);
            return new(command.Id, command.Title, command.Category, command.Description,
                string.Join(", ", command.Shortcuts.Select(shortcut => shortcut.DisplayText)),
                availability.Enabled, availability.Reason);
        }

        CommandReferenceEntry[] shell = _commands.Commands.Select(Describe).ToArray();
        List<CommandReferenceEntry> editors = _commands.Commands
            .Where(command => command.Id.StartsWith("edit.", StringComparison.Ordinal)
                || command.Id == "document.save")
            .Select(Describe).ToList();

        // Read actual command surfaces rather than maintaining a second list of editor actions.
        IEnumerable<Control> roots = _dockPanel.Contents.OfType<IStudioDocument>().OfType<Control>()
            .Concat(_imageEditorWindows.Values.Where(window => !window.IsDisposed))
            .Concat(_modelComposerWindows.Values.Where(window => !window.IsDisposed));
        foreach (Control root in roots.Distinct())
        {
            string category = root.Text.TrimEnd(' ', '*');
            if (category.Length == 0) category = root.GetType().Name;
            foreach (ToolStrip strip in CommandControls(root).OfType<ToolStrip>())
            foreach ((ToolStripItem item, string path) in CommandItems(strip.Items, string.Empty))
            {
                if (string.IsNullOrWhiteSpace(item.Text) || item is ToolStripSeparator or ToolStripLabel
                    || item is ToolStripDropDownItem { HasDropDownItems: true }) continue;
                string title = path.Replace("&", string.Empty, StringComparison.Ordinal);
                string shortcut = item is ToolStripMenuItem menu ? menu.ShortcutKeyDisplayString ?? string.Empty : string.Empty;
                editors.Add(new(category + ":" + title, title, category, item.ToolTipText ?? string.Empty,
                    shortcut, item.Enabled, item.Enabled ? string.Empty : "Unavailable in the current editor state."));
            }
            foreach (Button button in CommandControls(root).OfType<Button>())
            {
                string title = (string.IsNullOrWhiteSpace(button.AccessibleName) ? button.Text : button.AccessibleName)
                    .Replace("&", string.Empty, StringComparison.Ordinal).Trim();
                if (title.Length == 0) continue;
                editors.Add(new(category + ":" + title, title, category, button.AccessibleDescription ?? string.Empty,
                    string.Empty, button.Enabled, button.Enabled ? string.Empty : "Unavailable in the current editor state."));
            }
        }
        return new(editors.DistinctBy(entry => entry.Id).ToArray(), shell);
    }

    private static IEnumerable<Control> CommandControls(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        foreach (Control nested in CommandControls(child)) yield return nested;
    }

    private static IEnumerable<(ToolStripItem Item, string Path)> CommandItems(ToolStripItemCollection items, string parent)
    {
        foreach (ToolStripItem item in items)
        {
            string path = parent.Length > 0 ? parent + " / " + item.Text : item.Text ?? string.Empty;
            yield return (item, path);
            if (item is ToolStripDropDownItem dropdown)
            foreach (var nested in CommandItems(dropdown.DropDownItems, path)) yield return nested;
        }
    }
}
