using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>
/// The launch screen: the theme picture, the emblem in a slow halo with an orbiting arc while
/// startup runs, the name, the step actually running and a progress bar that eases to each step.
/// </summary>
public sealed class SplashForm : DpiAwareForm
{
    private readonly System.Windows.Forms.Timer _hold = new();
    // Drives the halo, arc and shimmer while the splash is up; not started with reduced motion.
    private readonly System.Windows.Forms.Timer _frame = new() { Interval = 16 };
    private readonly SplashCanvas _canvas;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
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
        DoubleBuffered = true;

        _canvas = new SplashCanvas(this) { Dock = DockStyle.Fill };
        Controls.Add(_canvas);

        Label version = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Name = "StudioBuildIdentity",
            Location = new Point(24, 18),
            Text = StudioBuildInfo.WindowLabel,
        };
        _canvas.Controls.Add(version);

        _frame.Tick += (_, _) =>
        {
            _canvas.ShownProgress = UiMotion.Lerp(_canvas.ShownProgress, _progressValue / 100f, 0.18f);
            _canvas.Invalidate();
        };
        // No fade-in of the window itself: startup steps can hold the thread, and a splash that is
        // still transparent then would look like nothing happened.
        if (UiMotion.Enabled) Shown += (_, _) => _frame.Start();
    }

    /// <summary>Raised once the startup sequence has finished.</summary>
    public event EventHandler? Completed;

    internal float Seconds => (float)_clock.Elapsed.TotalSeconds;

    internal string Status { get; private set; } = "Starting Genesis services…";

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
            Status = label;
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
            _frame.Dispose();
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
        // Without the frame timer the bar shows each step exactly.
        if (!_frame.Enabled) _canvas.ShownProgress = _progressValue / 100f;
        _canvas.Invalidate();
    }

    /// <summary>Paints the whole splash in one pass, so the halo and text composite over the theme picture.</summary>
    private sealed class SplashCanvas : Control
    {
        private readonly SplashForm _owner;

        public SplashCanvas(SplashForm owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        internal float ShownProgress;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            ThemePalette palette = ThemeService.Palette;
            if (!ThemeBackdrop.PaintFill(e.Graphics, ClientRectangle, ThemeBackdrop.ShowcaseScrim))
            {
                using LinearGradientBrush wash = new(ClientRectangle, UiDrawing.Blend(palette.Accent, palette.Canvas, 0.82f), palette.Canvas, LinearGradientMode.ForwardDiagonal);
                e.Graphics.FillRectangle(wash, ClientRectangle);
            }
            // A vignette pulls the eye to the centre.
            using GraphicsPath vignette = new();
            Rectangle wide = Rectangle.Inflate(ClientRectangle, Width / 3, Height / 3);
            vignette.AddEllipse(wide);
            using PathGradientBrush shade = new(vignette)
            {
                CenterColor = Color.FromArgb(0, palette.Canvas),
                SurroundColors = [Color.FromArgb(150, palette.Canvas)],
            };
            e.Graphics.FillRectangle(shade, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemePalette palette = ThemeService.Palette;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float scale = DeviceDpi / 96f;
            float seconds = _owner.Seconds;
            PointF centre = new(Width / 2f, Height * 0.36f);
            float emblem = 128 * scale;

            // Halo: an accent glow that breathes slowly behind the emblem.
            float breathe = 0.5f + 0.5f * MathF.Sin(seconds * 2.2f);
            float haloRadius = emblem * (0.95f + 0.08f * breathe);
            using (GraphicsPath halo = new())
            {
                halo.AddEllipse(centre.X - haloRadius, centre.Y - haloRadius, haloRadius * 2, haloRadius * 2);
                using PathGradientBrush glow = new(halo)
                {
                    CenterColor = Color.FromArgb((int)(70 + 40 * breathe), palette.Accent),
                    SurroundColors = [Color.FromArgb(0, palette.Accent)],
                };
                g.FillPath(glow, halo);
            }

            // An arc orbits the emblem while startup runs.
            if (ShownProgress < 0.999f)
            {
                float ring = emblem * 0.66f;
                RectangleF orbit = new(centre.X - ring, centre.Y - ring, ring * 2, ring * 2);
                using Pen track = new(Color.FromArgb(40, palette.Text), 2f * scale);
                g.DrawEllipse(track, orbit);
                using Pen arc = new(palette.AccentHover, 3f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, orbit, seconds * 240f % 360f, 70f + 30f * breathe);
            }

            Image? art = Branding.Emblem;
            if (art is not null)
                g.DrawImage(art, centre.X - emblem / 2, centre.Y - emblem / 2, emblem, emblem);

            int textTop = (int)(centre.Y + emblem * 0.78f);
            using Font title = new("Segoe UI Variable Display", 30f, FontStyle.Bold);
            using Font tagline = new("Segoe UI Variable Text", 12f, FontStyle.Regular);
            Size titleSize = TextRenderer.MeasureText(g, "Genesis Studio", title);
            TextRenderer.DrawText(g, "Genesis Studio", title, new Rectangle(0, textTop, Width, titleSize.Height), palette.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            int taglineTop = textTop + titleSize.Height + (int)(2 * scale);
            TextRenderer.DrawText(g, "Create  ·  Author  ·  Play", tagline, new Rectangle(0, taglineTop, Width, tagline.Height + 4),
                palette.AccentHover, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "One workspace. Every kind of world.", Font,
                new Rectangle(0, taglineTop + tagline.Height + (int)(10 * scale), Width, Font.Height + 4),
                palette.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

            // Status and a rounded progress bar with a shimmer running along the filled part.
            int barWidth = (int)(420 * scale), barHeight = Math.Max(4, (int)(5 * scale));
            Rectangle bar = new((Width - barWidth) / 2, Height - (int)(64 * scale), barWidth, barHeight);
            TextRenderer.DrawText(g, _owner.Status, Font, new Rectangle(0, bar.Top - Font.Height - (int)(12 * scale), Width, Font.Height + 4),
                palette.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            using (GraphicsPath track = UiDrawing.RoundedRectangle(bar, barHeight / 2))
            using (SolidBrush trackBrush = new(Color.FromArgb(70, palette.SurfaceRaised)))
                g.FillPath(trackBrush, track);
            int filled = (int)(barWidth * Math.Clamp(ShownProgress, 0f, 1f));
            if (filled >= barHeight)
            {
                Rectangle fill = new(bar.Left, bar.Top, filled, barHeight);
                using GraphicsPath fillPath = UiDrawing.RoundedRectangle(fill, barHeight / 2);
                using LinearGradientBrush fillBrush = new(fill, palette.Accent, palette.AccentHover, LinearGradientMode.Horizontal);
                g.FillPath(fillBrush, fillPath);
                float shimmerX = fill.Left + (seconds * 0.8f % 1f) * (fill.Width + 120 * scale) - 60 * scale;
                RectangleF shimmer = new(shimmerX, fill.Top, 60 * scale, fill.Height);
                using LinearGradientBrush shine = new(shimmer, Color.FromArgb(0, Color.White), Color.FromArgb(0, Color.White), LinearGradientMode.Horizontal)
                {
                    InterpolationColors = new ColorBlend
                    {
                        Colors = [Color.FromArgb(0, Color.White), Color.FromArgb(110, Color.White), Color.FromArgb(0, Color.White)],
                        Positions = [0f, 0.5f, 1f],
                    },
                };
                GraphicsState state = g.Save();
                g.SetClip(fillPath, CombineMode.Intersect);
                g.FillRectangle(shine, shimmer);
                g.Restore(state);
            }
        }
    }
}
