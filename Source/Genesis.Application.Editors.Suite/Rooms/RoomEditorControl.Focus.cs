using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private const int RoomSetFocusMessage = 0x0007;

    protected override void WndProc(ref Message message)
    {
        // UserControl otherwise restores its last active child or chooses the first text field.
        // This message targets the Room container itself. Focusing an Inspector/search child
        // does not enter this branch, so normal typing and keyboard navigation keep their focus.
        if (message.Msg == RoomSetFocusMessage && _workspaceReady && _viewport.Host.CanFocus)
            ActiveControl = _viewport.Host;
        base.WndProc(ref message);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        FocusRoomViewport();
        base.OnMouseDown(e);
    }

    private void FocusRoomViewport()
    {
        if (_workspaceReady && _viewport.Host.CanFocus) _viewport.Host.Focus();
    }
}
