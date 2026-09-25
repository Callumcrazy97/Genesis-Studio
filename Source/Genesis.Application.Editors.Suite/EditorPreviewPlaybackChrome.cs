using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite;

/// <summary>Shared Play / Pause / Stop for the 3D preview clock — not Room F5.</summary>
internal static class EditorPreviewPlaybackChrome
{
    public static void AddPlayback(ToolStrip toolbar, EditorPreviewClock clock, Action? afterChange = null)
    {
        ArgumentNullException.ThrowIfNull(toolbar);
        ArgumentNullException.ThrowIfNull(clock);

        ToolStripButton play = EditorChrome.ToolButton(
            "Play",
            "Play this viewport at 60 FPS (shaders, water, animations, preview sun)",
            () =>
            {
                clock.Play();
                afterChange?.Invoke();
            },
            toggle: true);
        play.Font = new Font(EditorChrome.BaseFont, FontStyle.Bold);
        play.ForeColor = EditorChrome.Accent;
        play.Padding = new Padding(8, 0, 8, 0);
        ToolStripButton pause = EditorChrome.ToolButton(
            "Pause",
            "Pause the viewport preview clock",
            () =>
            {
                clock.Pause();
                afterChange?.Invoke();
            },
            toggle: true);
        ToolStripButton stop = EditorChrome.ToolButton(
            "Stop",
            "Stop and rewind the viewport preview clock",
            () =>
            {
                clock.Stop();
                afterChange?.Invoke();
            });

        void Sync()
        {
            play.Checked = clock.Playing;
            pause.Checked = !clock.Playing && clock.Time > 0f;
        }

        clock.Changed += (_, _) =>
        {
            if (play.IsDisposed)
            {
                return;
            }

            Sync();
        };
        Sync();
        toolbar.Items.Add(play);
        toolbar.Items.Add(pause);
        toolbar.Items.Add(stop);
    }
}
