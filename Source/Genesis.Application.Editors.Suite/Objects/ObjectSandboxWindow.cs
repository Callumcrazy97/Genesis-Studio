using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>
/// Hosts the object sandbox in its own window.
/// </summary>
/// <remarks>
/// The sandbox used to be docked under the code editor, where it took ~210px of vertical space
/// permanently — from the one pane that benefits most from height. As a window it can be opened when
/// you want to test, moved to a second monitor, and sized so the variable list is actually readable
/// instead of a two-row sliver.
///
/// The editor keeps owning the panel; this window only borrows it. That way the sandbox still runs
/// headlessly with no window in sight, which is how the build gate drives it.
/// </remarks>
public sealed class ObjectSandboxWindow : DpiAwareForm
{
    private readonly ObjectSandboxPanel _panel;

    public ObjectSandboxWindow(ObjectSandboxPanel panel, string objectName)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));

        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = EditorChrome.Canvas;
        ClientSize = new Size(900, 620);
        MinimumSize = new Size(560, 400);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = $"Sandbox — {objectName}";

        _panel.Dock = DockStyle.Fill;
        Controls.Add(_panel);
    }

    /// <summary>
    /// Hand the panel back before closing, so the editor can re-host or re-open it later.
    /// </summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Controls.Remove(_panel);
        base.OnFormClosed(e);
    }
}
