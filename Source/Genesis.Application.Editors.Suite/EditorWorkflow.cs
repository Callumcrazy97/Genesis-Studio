using System.Windows.Forms;
using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite;

/// <summary>Places an editor's guided <see cref="WorkflowBar"/> in the shared editor frame.</summary>
/// <remarks>
/// Every editor uses the same frame: command bar, then the workflow bar, then the editor's own
/// tools and view. Top-docked siblings stack in reverse z-order (the last one docked sits against
/// the edge), so the bar takes the command bar's slot in the collection, which docks it immediately
/// beneath the command bar however many other top strips the editor has.
/// </remarks>
public static class EditorWorkflow
{
    /// <summary>
    /// Editors shorter than this (logical pixels) fold the bar away so the view keeps its room;
    /// every step is still reachable from the command bar and the editor's own pages.
    /// </summary>
    /// <remarks>
    /// 560 keeps the bar at Studio's minimum window size (1080 × 700 leaves an editor about 578
    /// tall) and on a maximised 1366 × 768 display, and folds it only in genuinely short hosts.
    /// </remarks>
    public const int DefaultAutoHideHeight = 560;

    /// <summary>Creates a workflow bar and docks it directly under <paramref name="anchor"/>.</summary>
    public static WorkflowBar AttachBelow(
        Control anchor,
        string name,
        IEnumerable<WorkflowStep> steps,
        int autoHideBelowHeight = DefaultAutoHideHeight)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        Control parent = anchor.Parent
            ?? throw new InvalidOperationException("Add the command bar to its editor before attaching a workflow bar.");
        WorkflowBar bar = new(name, steps) { AutoHideBelowHeight = autoHideBelowHeight };
        parent.SuspendLayout();
        try
        {
            parent.Controls.Add(bar);
            parent.Controls.SetChildIndex(bar, parent.Controls.GetChildIndex(anchor));
        }
        finally
        {
            parent.ResumeLayout(performLayout: true);
        }

        return bar;
    }

    /// <summary>
    /// Retires an editor's old one-line "1. … 2. … 3." hint: the workflow bar says the same thing,
    /// clickably. The label is kept (hidden) so existing layout code that measures it still works.
    /// </summary>
    public static void RetireHint(Control? hint)
    {
        if (hint is null)
        {
            return;
        }

        hint.Visible = false;
        hint.VisibleChanged += (_, _) =>
        {
            if (hint.Visible)
            {
                hint.Visible = false;
            }
        };
    }
}
