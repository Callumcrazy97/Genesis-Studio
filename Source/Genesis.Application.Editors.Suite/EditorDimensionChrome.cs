using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite;

/// <summary>Shared Room-style paired 2D / 3D dimension toggles for Suite editor toolbars.</summary>
internal static class EditorDimensionChrome
{
    public sealed class DimensionToggle
    {
        public required ToolStripButton Button2D { get; init; }
        public required ToolStripButton Button3D { get; init; }

        public void Sync(bool is2D)
        {
            Button2D.Checked = is2D;
            Button3D.Checked = !is2D;
        }
    }

    /// <summary>
    /// Adds paired 2D/3D toggle buttons. <paramref name="setIs2D"/> should update Mode2D / document
    /// dimension and rebuild preview as needed.
    /// </summary>
    public static DimensionToggle AddTo(
        ToolStrip toolbar,
        Func<bool> getIs2D,
        Action<bool> setIs2D,
        string? caption = "View")
    {
        ArgumentNullException.ThrowIfNull(toolbar);
        ArgumentNullException.ThrowIfNull(getIs2D);
        ArgumentNullException.ThrowIfNull(setIs2D);

        if (!string.IsNullOrWhiteSpace(caption))
        {
            toolbar.Items.Add(new ToolStripLabel(caption)
            {
                ForeColor = EditorChrome.Muted,
                Margin = new Padding(8, 0, 4, 0),
            });
        }

        ToolStripButton button2D = EditorChrome.ToolButton(
            "2D",
            "Switch the preview to 2D (orthographic)",
            () => setIs2D(true),
            toggle: true);
        ToolStripButton button3D = EditorChrome.ToolButton(
            "3D",
            "Switch the preview to 3D (perspective)",
            () => setIs2D(false),
            toggle: true);

        toolbar.Items.Add(button2D);
        toolbar.Items.Add(button3D);

        DimensionToggle toggle = new()
        {
            Button2D = button2D,
            Button3D = button3D,
        };
        toggle.Sync(getIs2D());
        return toggle;
    }
}
