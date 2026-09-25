using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

public sealed class SplashForm : DpiAwareForm
{
    private const int ProgressTrackWidth = 800;

    private readonly System.Windows.Forms.Timer _hold = new();
    private readonly Label _status;
    private readonly Panel _progress;
    private int _progressValue;

    public SplashForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(940, 540);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ThemeService.Palette.Canvas;
        ForeColor = ThemeService.Palette.Text;
        ShowInTaskbar = true;
        Icon = Branding.WindowIcon ?? Icon;

        // The splash is one of the two places the theme picture is the point rather than a backdrop,
        // so it takes the lightest veil the palette's own contrast can carry.
        DoubleBuffered = true;

        Panel accentRail = new()
        {
            BackColor = ThemeService.Palette.Accent,
            Dock = DockStyle.Left,
            Width = 7,
        };
        Controls.Add(accentRail);

        GenesisLogoControl logo = new()
        {
            Location = new Point(70, 66),
            Size = new Size(88, 88),
        };
        Controls.Add(logo);

        Label product = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Variable Display", 28f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(178, 68),
            Text = "Genesis Studio",
        };
        Controls.Add(product);

        Label application = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Variable Text", 12f, FontStyle.Regular),
            ForeColor = ThemeService.Palette.AccentHover,
            Location = new Point(182, 122),
            Text = "Create  ·  Author  ·  Play",
        };
        Controls.Add(application);

        Label statement = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Variable Display", 19f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(70, 210),
            Size = new Size(760, 44),
            Text = "One workspace. Every kind of world.",
        };
        Controls.Add(statement);

        Label detail = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = ThemeService.InterfaceFont,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(72, 260),
            Size = new Size(720, 48),
            Text = "Create pixel games, sculpt 3D worlds, or stream infinite voxel terrain\n" +
                   "through the Ember runtime.",
        };
        Controls.Add(detail);

        FlowLayoutPanel capabilities = new()
        {
            BackColor = Color.Transparent,
            Location = new Point(70, 334),
            Size = new Size(790, 50),
            WrapContents = false,
        };
        capabilities.Controls.Add(CreatePill("2D PIXEL"));
        capabilities.Controls.Add(CreatePill("3D WORLDS"));
        capabilities.Controls.Add(CreatePill("VOXEL"));
        capabilities.Controls.Add(CreatePill("PGSL"));
        capabilities.Controls.Add(CreatePill("EMBER"));
        Controls.Add(capabilities);

        Label version = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Name = "StudioBuildIdentity",
            Location = new Point(70, 18),
            Text = StudioBuildInfo.WindowLabel,
        };
        Controls.Add(version);

        _status = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(70, 452),
            Size = new Size(650, 24),
            Text = "Starting Genesis services…",
        };
        Controls.Add(_status);

        Panel progressTrack = new()
        {
            BackColor = ThemeService.Palette.SurfaceRaised,
            Location = new Point(70, 488),
            Size = new Size(ProgressTrackWidth, 4),
        };
        _progress = new Panel
        {
            BackColor = ThemeService.Palette.Accent,
            Dock = DockStyle.Left,
            Width = 0,
        };
        progressTrack.Controls.Add(_progress);
        Controls.Add(progressTrack);

    }

    /// <summary>Raised once the startup sequence has finished.</summary>
    public event EventHandler? Completed;

    /// <summary>Paints the theme picture behind the splash's text.</summary>
    /// <remarks>
    /// Painted as the background rather than into a child control so the labels above it, which are
    /// transparent, composite over the picture instead of over a flat colour.
    /// </remarks>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!ThemeBackdrop.PaintFill(e.Graphics, ClientRectangle, ThemeBackdrop.ShowcaseScrim))
        {
            base.OnPaintBackground(e);
        }
    }

    /// <summary>
    /// Shortest time the splash stays up, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Not padding for its own sake: startup can finish in well under a tenth of a second, and a
    /// window that appears and vanishes within one frame reads as a glitch. This is the floor at
    /// which a person can register that something happened — the bar still tracks real work, it
    /// just is not torn away mid-stride.
    /// </remarks>
    private const int MinimumVisibleMilliseconds = 450;

    /// <summary>
    /// Runs the real startup work, showing each step as it happens.
    /// </summary>
    /// <remarks>
    /// The bar reports steps *completed*, so it can never claim progress that has not been made,
    /// and the caption is the name of the step actually running. Work happens on the UI thread
    /// between repaints rather than on a worker: startup builds WinForms objects and theme state,
    /// which must be on this thread anyway, and the steps are short enough that pumping between
    /// them keeps the window responsive.
    /// </remarks>
    public void RunStartup(StartupSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        System.Diagnostics.Stopwatch visible = System.Diagnostics.Stopwatch.StartNew();

        sequence.Run((label, completed, total) =>
        {
            _status.Text = label;
            _progressValue = total <= 0 ? 100 : (int)Math.Round(100d * completed / total);
            UpdateProgress();
            Refresh();
            System.Windows.Forms.Application.DoEvents();
        });

        int remaining = MinimumVisibleMilliseconds - (int)visible.ElapsedMilliseconds;
        if (remaining > 0)
        {
            _hold.Interval = remaining;
            _hold.Tick += OnHoldElapsed;
            _hold.Start();
            return;
        }

        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Finishes immediately, for a host that has nothing to report.</summary>
    public void CompleteImmediately()
    {
        _hold.Stop();
        _progressValue = 100;
        UpdateProgress();
        Completed?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hold.Tick -= OnHoldElapsed;
            _hold.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnHoldElapsed(object? sender, EventArgs e)
    {
        _hold.Stop();
        _hold.Tick -= OnHoldElapsed;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateProgress()
    {
        _progressValue = Math.Clamp(_progressValue, 0, 100);
        _progress.Width = (int)Math.Round(ProgressTrackWidth * (_progressValue / 100d));
    }

    private static Control CreatePill(string text)
    {
        Label label = new()
        {
            AutoSize = false,
            BackColor = ThemeService.Palette.SurfaceRaised,
            ForeColor = ThemeService.Palette.TextMuted,
            Margin = new Padding(0, 0, 10, 0),
            Padding = new Padding(10, 0, 10, 0),
            Size = new Size(126, 32),
            Text = text,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        return label;
    }
}
