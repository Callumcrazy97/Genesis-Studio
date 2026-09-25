using System.Text.Json;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Diagnostics;

namespace Genesis.Application.Studio.Forms;

public sealed class RuntimeDiagnosticsForm : DpiAwareForm
{
    private readonly string _snapshotPath;
    private readonly bool _frameDebugger;
    private readonly Label _connection = new();
    private readonly FlowLayoutPanel _metrics = new();
    private readonly ListView _details = new();
    private readonly FrameHistoryGraph _graph = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly Dictionary<string, Label> _metricValues = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastTimestamp;

    public RuntimeDiagnosticsForm(string projectRoot, bool frameDebugger)
    {
        _frameDebugger = frameDebugger;
        _snapshotPath = Path.Combine(projectRoot, ".genesis", "Diagnostics", "live-profiler.json");
        Text = frameDebugger ? "Frame Debugger" : "Profiler";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(840, 560);
        Size = new Size(1040, 720);
        BackColor = ThemeService.Palette.Canvas;
        ForeColor = ThemeService.Palette.Text;

        ToolStrip toolbar = new()
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = ThemeService.Palette.Surface,
            Renderer = ThemeService.CreateToolStripRenderer(),
        };
        toolbar.Items.Add(new ToolStripLabel(frameDebugger
            ? "LAST COMPLETED FRAME"
            : "LIVE PLAYER TELEMETRY") { ForeColor = ThemeService.Palette.TextMuted });
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripButton("Run Debug (F6)", null, (_, _) => _connection.Text = "Press F6 in Studio to launch the project with telemetry."));

        _connection.Dock = DockStyle.Top;
        _connection.Height = 34;
        _connection.Padding = new Padding(12, 8, 0, 0);
        _connection.BackColor = ThemeService.Palette.SurfaceRaised;
        _connection.ForeColor = ThemeService.Palette.TextMuted;
        _connection.Text = "Waiting for a Debug Player…";

        _metrics.Dock = DockStyle.Top;
        _metrics.Height = 104;
        _metrics.Padding = new Padding(10);
        _metrics.BackColor = ThemeService.Palette.Canvas;
        _metrics.WrapContents = false;
        foreach (string name in frameDebugger
                     ? new[] { "Draw Calls", "2D / 3D", "Triangles", "Batches", "Submitted", "Culled" }
                     : new[] { "FPS", "1% Low", "CPU", "GPU", "Frame", "Memory" })
        {
            _metrics.Controls.Add(MetricCard(name));
        }

        _graph.Dock = DockStyle.Top;
        _graph.Height = 165;
        _graph.BackColor = ThemeService.Palette.Surface;

        _details.Dock = DockStyle.Fill;
        _details.View = View.Details;
        _details.FullRowSelect = true;
        _details.BorderStyle = BorderStyle.None;
        _details.BackColor = ThemeService.Palette.Surface;
        _details.ForeColor = ThemeService.Palette.Text;
        if (frameDebugger)
        {
            _details.Columns.Add("Pass / Counter", 280);
            _details.Columns.Add("Time / Value", 160);
            _details.Columns.Add("Meaning", 500);
        }
        else
        {
            _details.Columns.Add("Object", 220);
            _details.Columns.Add("Event", 170);
            _details.Columns.Add("Calls", 90);
            _details.Columns.Add("Average", 120);
            _details.Columns.Add("Maximum", 120);
            _details.Columns.Add("Failures", 90);
        }

        Panel detailsHost = new() { Dock = DockStyle.Fill, BackColor = ThemeService.Palette.Surface };
        detailsHost.Controls.Add(_details);
        detailsHost.Controls.Add(new Label
        {
            Text = frameDebugger ? "RENDER PASSES AND SUBMISSION" : "PGSL EVENT COST",
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(10, 8, 0, 0),
            BackColor = ThemeService.Palette.SurfaceRaised,
            ForeColor = ThemeService.Palette.TextMuted,
        });

        Controls.Add(detailsHost);
        Controls.Add(_graph);
        Controls.Add(_metrics);
        Controls.Add(_connection);
        Controls.Add(toolbar);
        ThemeService.Apply(this);
        _timer.Tick += (_, _) => RefreshSnapshot();
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
        RefreshSnapshot();
    }

    private Control MetricCard(string name)
    {
        Panel card = new()
        {
            Width = 155,
            Height = 78,
            Margin = new Padding(4),
            BackColor = ThemeService.Palette.SurfaceRaised,
        };
        Label title = new()
        {
            Text = name.ToUpperInvariant(),
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(10, 8, 0, 0),
            ForeColor = ThemeService.Palette.TextMuted,
        };
        Label value = new()
        {
            Text = "—",
            Dock = DockStyle.Fill,
            Padding = new Padding(9, 2, 0, 0),
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
        };
        _metricValues[name] = value;
        card.Controls.Add(value);
        card.Controls.Add(title);
        return card;
    }

    private void RefreshSnapshot()
    {
        LiveProfilerSnapshot? snapshot;
        try
        {
            if (!File.Exists(_snapshotPath))
            {
                _connection.Text = "No telemetry yet. Run the project with Debug (F6).";
                return;
            }
            snapshot = JsonSerializer.Deserialize<LiveProfilerSnapshot>(File.ReadAllText(_snapshotPath));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _connection.Text = "Telemetry is updating…";
            return;
        }
        if (snapshot is null || snapshot.TimestampUtc == _lastTimestamp) return;
        _lastTimestamp = snapshot.TimestampUtc;
        TimeSpan age = DateTime.UtcNow - snapshot.TimestampUtc;
        _connection.Text = age < TimeSpan.FromSeconds(2)
            ? $"Live · {snapshot.Backend} · {snapshot.Room}"
            : $"Last sample {age.TotalSeconds:0}s ago · run Debug (F6) to reconnect";
        _connection.ForeColor = age < TimeSpan.FromSeconds(2)
            ? ThemeService.Palette.Accent
            : ThemeService.Palette.TextMuted;

        if (_frameDebugger) UpdateFrame(snapshot);
        else UpdateProfiler(snapshot);
        _graph.Add(snapshot.FrameMilliseconds, snapshot.GpuMilliseconds);
    }

    private void UpdateProfiler(LiveProfilerSnapshot snapshot)
    {
        SetMetric("FPS", snapshot.Fps.ToString("0.0"));
        SetMetric("1% Low", snapshot.OnePercentLowFps.ToString("0.0"));
        SetMetric("CPU", snapshot.CpuPercent.ToString("0.0") + "%");
        SetMetric("GPU", snapshot.GpuMilliseconds.ToString("0.00") + " ms");
        SetMetric("Frame", snapshot.FrameMilliseconds.ToString("0.00") + " ms");
        SetMetric("Memory", FormatBytes(snapshot.WorkingSetBytes));
        _details.BeginUpdate();
        _details.Items.Clear();
        foreach (PgslProfilerSample profile in snapshot.Pgsl)
        {
            ListViewItem item = new(profile.ObjectName);
            item.SubItems.Add(profile.EventName);
            item.SubItems.Add(profile.CallCount.ToString());
            item.SubItems.Add(profile.AverageMicroseconds.ToString("0.0") + " µs");
            item.SubItems.Add(profile.MaximumMicroseconds.ToString("0.0") + " µs");
            item.SubItems.Add(profile.FailedCalls.ToString());
            _details.Items.Add(item);
        }
        _details.EndUpdate();
    }

    private void UpdateFrame(LiveProfilerSnapshot snapshot)
    {
        SetMetric("Draw Calls", snapshot.DrawCalls.ToString());
        SetMetric("2D / 3D", $"{snapshot.DrawCalls2D} / {snapshot.DrawCalls3D}");
        SetMetric("Triangles", snapshot.Triangles.ToString("N0"));
        SetMetric("Batches", snapshot.Batches.ToString());
        SetMetric("Submitted", snapshot.ItemsSubmitted.ToString());
        SetMetric("Culled", snapshot.InstancesCulled.ToString());
        _details.BeginUpdate();
        _details.Items.Clear();
        AddDetail("GPU frame", snapshot.GpuMilliseconds.ToString("0.00") + " ms", "Resolved GPU timestamp for the completed frame");
        AddDetail("Ambient occlusion", snapshot.AoMilliseconds.ToString("0.00") + " ms", "GTAO pass");
        AddDetail("Contact shadows", snapshot.ContactShadowMilliseconds.ToString("0.00") + " ms", "Screen-space contact-shadow pass");
        AddDetail("Local volumetrics", snapshot.VolumetricMilliseconds.ToString("0.00") + " ms", "Local light volume pass");
        AddDetail("Bloom", snapshot.BloomMilliseconds.ToString("0.00") + " ms", "Bloom pyramid");
        AddDetail("Raymarched clouds", snapshot.CloudMilliseconds.ToString("0.00") + " ms", "Atmosphere cloud pass");
        AddDetail("Instances drawn", snapshot.InstancesDrawn.ToString(), "Visible sprite and mesh instances submitted");
        AddDetail("Lights", snapshot.LightsUsed.ToString(), "Lights used by the renderer");
        AddDetail("Managed heap", FormatBytes(snapshot.ManagedHeapBytes), "Live managed allocation footprint");
        _details.EndUpdate();
    }

    private void AddDetail(string name, string value, string description)
    {
        ListViewItem item = new(name);
        item.SubItems.Add(value);
        item.SubItems.Add(description);
        _details.Items.Add(item);
    }

    private void SetMetric(string name, string value)
    {
        if (_metricValues.TryGetValue(name, out Label? label)) label.Text = value;
    }

    private static string FormatBytes(long value) => value >= 1024L * 1024L * 1024L
        ? value / (1024d * 1024d * 1024d) + " GB"
        : value / (1024d * 1024d) + " MB";

    private sealed class FrameHistoryGraph : Control
    {
        private readonly Queue<(double Cpu, double Gpu)> _samples = new(180);

        public void Add(double cpu, double gpu)
        {
            _samples.Enqueue((cpu, gpu));
            while (_samples.Count > 180) _samples.Dequeue();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(ThemeService.Palette.Surface);
            using Pen grid = new(Color.FromArgb(45, ThemeService.Palette.TextMuted));
            for (int i = 1; i < 4; i++)
            {
                float y = Height * i / 4f;
                e.Graphics.DrawLine(grid, 0, y, Width, y);
            }
            DrawSeries(e.Graphics, _samples.Select(sample => sample.Cpu), ThemeService.Palette.Accent);
            DrawSeries(e.Graphics, _samples.Select(sample => sample.Gpu), Color.FromArgb(83, 202, 178));
            TextRenderer.DrawText(e.Graphics, "CPU frame   GPU", Font, new Point(10, 8), ThemeService.Palette.TextMuted);
        }

        private void DrawSeries(Graphics graphics, IEnumerable<double> values, Color color)
        {
            double[] data = values.ToArray();
            if (data.Length < 2) return;
            float step = Width / (float)Math.Max(1, data.Length - 1);
            PointF[] points = data.Select((value, index) => new PointF(
                index * step,
                Height - 8 - (float)Math.Clamp(value / 33.33d, 0d, 1d) * (Height - 20))).ToArray();
            using Pen pen = new(color, 2f);
            graphics.DrawLines(pen, points);
        }
    }
}
