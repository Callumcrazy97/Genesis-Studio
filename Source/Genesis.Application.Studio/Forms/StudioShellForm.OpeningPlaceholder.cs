using Genesis.Application.Core.Resources;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Theme;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Studio.Forms;

public sealed partial class StudioShellForm
{
    /// <summary>
    /// Shows a tab saying the resource is opening, painted at once, while its editor is made.
    /// A 3D editor takes a moment to load its resource and start its viewport; without this the
    /// double-click appeared to do nothing until the finished editor arrived. Not shown in an
    /// unattended (test) session, where nobody is watching and documents are counted.
    /// </summary>
    private IDisposable ShowOpeningPlaceholder(ResourceItem resource)
    {
        Cursor? previousCursor = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor;
        // The status bar says it too (painted now; the message is not logged, "Opened" is).
        _status.Text = $"Opening {resource.Name}…";
        _status.Owner?.Refresh();
        OpeningPlaceholderDocument? placeholder = null;
        if (!Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive)
        {
            try
            {
                placeholder = new OpeningPlaceholderDocument(resource.Name);
                placeholder.Show(_dockPanel, DockState.Document);
                placeholder.Refresh();
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                // A placeholder that cannot be shown is no reason not to open the editor.
                placeholder?.Dispose();
                placeholder = null;
            }
        }

        return new PlaceholderScope(placeholder, previousCursor);
    }

    private sealed class PlaceholderScope(OpeningPlaceholderDocument? placeholder, Cursor? previousCursor) : IDisposable
    {
        public void Dispose()
        {
            if (placeholder is { IsDisposed: false })
            {
                placeholder.Close();
                placeholder.Dispose();
            }

            Cursor.Current = previousCursor ?? Cursors.Default;
        }
    }

    private sealed class OpeningPlaceholderDocument : GenesisDockContent
    {
        public OpeningPlaceholderDocument(string name)
        {
            HideOnClose = false;
            Text = name;
            Name = "OpeningPlaceholder";
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Opening " + name + "…",
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = ThemeService.Palette.TextMuted,
                BackColor = ThemeService.Palette.Canvas,
                Font = ThemeService.HeadingFont,
            });
        }
    }
}
