using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>Owner-drawn adornments share their hit-test with painting. Consume them before
/// native TreeView selection/double-click handling can invoke an unrelated editor action.</summary>
internal sealed class RoomInstanceTreeView : TreeView
{
    internal Func<Point, bool, bool>? HandleAdornment { get; set; }
    private bool _adornmentMouseDown;

    internal static Point ClientPoint(nint value)
    {
        long packed = value.ToInt64();
        return new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
    }

    protected override void WndProc(ref Message message)
    {
        const int leftDown = 0x0201, leftUp = 0x0202, leftDouble = 0x0203;
        if (message.Msg is leftDown or leftDouble)
        {
            Point point = ClientPoint(message.LParam);
            if (HandleAdornment?.Invoke(point, message.Msg == leftDouble) == true)
            {
                Focus();
                _adornmentMouseDown = true;
                message.Result = 0;
                return;
            }
            _adornmentMouseDown = false;
        }
        else if (message.Msg == leftUp && _adornmentMouseDown)
        {
            _adornmentMouseDown = false;
            message.Result = 0;
            return;
        }
        base.WndProc(ref message);
    }
}
