using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class NoteEditorControl
{
    /// <summary>
    /// Holds a rich text box's painting and change notifications while many spans are coloured or
    /// appended in one go. SuspendLayout does neither: every Select, colour and AppendText repainted
    /// the box and raised a selection change, so opening a long note took seconds.
    /// </summary>
    private readonly struct RichTextBatch : System.IDisposable
    {
        private const int WmSetRedraw = 0x000B;
        private const int EmGetEventMask = 0x0400 + 59;
        private const int EmSetEventMask = 0x0400 + 69;
        private readonly RichTextBox? _box;
        private readonly nint _eventMask;

        [DllImport("user32.dll")]
        private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

        public RichTextBatch(RichTextBox box)
        {
            _box = box.IsHandleCreated ? box : null;
            _eventMask = 0;
            if (_box == null) return;
            SendMessage(_box.Handle, WmSetRedraw, 0, 0);
            _eventMask = SendMessage(_box.Handle, EmGetEventMask, 0, 0);
            SendMessage(_box.Handle, EmSetEventMask, 0, 0);
        }

        public void Dispose()
        {
            if (_box == null || !_box.IsHandleCreated) return;
            SendMessage(_box.Handle, EmSetEventMask, 0, _eventMask);
            SendMessage(_box.Handle, WmSetRedraw, 1, 0);
            _box.Invalidate();
        }
    }
}
