using System.Diagnostics;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>
/// A small window that says a project is being opened and what is being done to it, shown for
/// the whole of an open so Studio never sits frozen with nothing on screen. The steps run on the
/// UI thread; each <see cref="Step"/> repaints the window before the next one starts.
/// </summary>
internal sealed class ProjectOpeningNotice : Form
{
    private static ProjectOpeningNotice? _current;
    private readonly Label _status;
    private readonly Panel _bar;
    private readonly Panel _fill;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _steps;

    private ProjectOpeningNotice(string projectName)
    {
        ThemePalette palette = ThemeService.Palette;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Text = "Opening " + projectName;
        Size = new Size(460, 132);
        BackColor = palette.Surface;
        Padding = new Padding(1);
        Name = "ProjectOpeningNotice";

        Panel body = new() { Dock = DockStyle.Fill, BackColor = palette.Canvas, Padding = new Padding(20, 16, 20, 14) };
        Label title = new()
        {
            Dock = DockStyle.Top, Height = 34, Text = "Opening " + projectName,
            Font = ThemeService.HeadingFont, ForeColor = palette.Text, AutoEllipsis = true,
        };
        _status = new Label
        {
            Dock = DockStyle.Top, Height = 26, Text = "Reading the project…",
            Font = ThemeService.InterfaceFont, ForeColor = palette.TextMuted, AutoEllipsis = true,
        };
        _bar = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = palette.SurfaceRaised };
        _fill = new Panel { Dock = DockStyle.Left, Width = 0, BackColor = palette.Accent };
        _bar.Controls.Add(_fill);
        body.Controls.Add(_bar);
        body.Controls.Add(_status);
        body.Controls.Add(title);
        Controls.Add(body);
    }

    /// <summary>
    /// Shows the notice (unless one is already showing or Studio runs unattended) and returns it;
    /// dispose it when the project is open.
    /// </summary>
    public static IDisposable Begin(string projectName)
    {
        if (_current is not null || Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive)
            return new Ending(null);
        ProjectOpeningNotice notice = new(projectName);
        _current = notice;
        notice.Show();
        notice.Refresh();
        return new Ending(notice);
    }

    /// <summary>Says what opening the project is doing now. Does nothing when no notice is showing.</summary>
    public static void Step(string status)
    {
        ProjectOpeningNotice? notice = _current;
        if (notice is null || notice.IsDisposed) return;
        notice._steps++;
        notice._status.Text = status;
        // An open has about six steps; the bar fills towards the end without claiming to know it.
        notice._fill.Width = (int)(notice._bar.ClientSize.Width * (1 - Math.Pow(0.72, notice._steps)));
        notice.Refresh();
    }

    private sealed class Ending(ProjectOpeningNotice? notice) : IDisposable
    {
        public void Dispose()
        {
            if (notice is null) return;
            if (ReferenceEquals(_current, notice)) _current = null;
            Trace.WriteLine($"Project opened in {notice._clock.ElapsedMilliseconds} ms");
            if (!notice.IsDisposed) { notice.Close(); notice.Dispose(); }
        }
    }
}
