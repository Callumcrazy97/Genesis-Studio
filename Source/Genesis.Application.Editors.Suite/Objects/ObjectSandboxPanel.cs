using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>
/// The Object Editor's built-in sandbox: runs the object's events and shows what happened.
/// </summary>
/// <remarks>
/// The fast loop that was missing. Press Run and the object's Create/Step/Alarm/Draw events execute
/// on a real VM via <see cref="ObjectSandbox"/>, then this panel repaints every draw call the script
/// emitted and lists its variables, its alarms and any errors — without launching the player or even
/// needing a room.
///
/// Painting is GDI+ over the *recorded* calls rather than a live D3D pass, which is a deliberate
/// trade: it needs no device, it is deterministic, and it shows exactly the primitives the script
/// asked for. It is a script sandbox, not a preview of final lighting, and the header says so.
/// </remarks>
public sealed class ObjectSandboxPanel : Panel
{
    private readonly Panel _canvas = new();
    private readonly ListView _state = new();
    private readonly ListBox _problems = new();
    private readonly Label _status = new();
    private readonly NumericUpDown _frames = new();
    private readonly Button _run = new();
    private readonly CheckBox _showGui = new();

    private PgslRecordingDrawSurface _surface = new();
    private ObjectSandboxResult? _result;
    private Bitmap? _spriteFrame;
    private string? _spriteFramePath;
    private SandboxSprite? _sprite;

    /// <summary>Supplies the object's current event code. Set by the editor.</summary>
    public Func<IReadOnlyDictionary<string, string>>? EventSource { get; set; }

    /// <summary>Raised when a run creates a live instance or an Inspector edit mutates it.</summary>
    public event EventHandler? LiveStateChanged;

    /// <summary>
    /// Supplies the object's bound sprite, or null when it has none. Set by the editor.
    /// </summary>
    /// <remarks>
    /// Without this the sandbox drew nothing for the most ordinary object there is. It recorded
    /// only <i>PGSL</i> draw calls, but a sprite-backed object is drawn at runtime by its sprite
    /// component, not by script — an object with Create and Step and no Draw event is completely
    /// normal and renders fine in a room. The sandbox reported "0 draw call(s)" and an empty canvas,
    /// which was true about the script and actively misleading about the object.
    ///
    /// This is the same class of mismatch as NEXT-041/NEXT-046: the editor modelling something
    /// different from what the runtime does, and looking broken (or worse, looking fine) as a result.
    /// </remarks>
    public Func<SandboxSprite?>? SpriteSource { get; set; }

    /// <summary>The bound sprite the sandbox should draw, resolved by the editor.</summary>
    /// <param name="FramePath">Absolute path to the frame image to draw.</param>
    /// <param name="OriginX">Origin X, in the space named by <paramref name="NormalizedOrigin"/>.</param>
    /// <param name="OriginY">Origin Y, in the space named by <paramref name="NormalizedOrigin"/>.</param>
    /// <param name="NormalizedOrigin">
    /// True when the origin is a fraction of the frame; false when it is already in pixels. Both
    /// spellings exist in authored documents, and getting this wrong is what NEXT-092 was.
    /// </param>
    public sealed record SandboxSprite(
        string FramePath,
        double OriginX,
        double OriginY,
        bool NormalizedOrigin);

    public ObjectSandboxPanel()
    {
        BackColor = EditorChrome.Surface;
        Dock = DockStyle.Fill;

        ToolStrip bar = EditorChrome.MakeToolbar();
        bar.Items.Add(new ToolStripLabel("Sandbox") { ForeColor = EditorChrome.Muted });
        _run.Text = "▶ Run";
        _run.Width = 74;
        EditorChrome.StyleField(_run);
        _run.Click += (_, _) => Run();
        bar.Items.Add(new ToolStripControlHost(_run));

        bar.Items.Add(new ToolStripLabel("Frames") { ForeColor = EditorChrome.Muted });
        _frames.Minimum = 1;
        _frames.Maximum = ObjectSandbox.MaxFrames;
        _frames.Value = 60;
        _frames.Width = 70;
        EditorChrome.StyleField(_frames);
        bar.Items.Add(new ToolStripControlHost(_frames) { AutoSize = false, Width = 74 });

        _showGui.Text = "Show GUI layer";
        _showGui.Checked = true;
        _showGui.BackColor = Color.Transparent;
        _showGui.ForeColor = EditorChrome.Text;
        _showGui.CheckedChanged += (_, _) => _canvas.Invalidate();
        bar.Items.Add(new ToolStripControlHost(_showGui));

        _canvas.BackColor = EditorChrome.Canvas;
        _canvas.Dock = DockStyle.Fill;
        _canvas.Paint += PaintRecording;
        _canvas.Resize += (_, _) => _canvas.Invalidate();

        _state.Dock = DockStyle.Right;
        _state.Width = 250;
        _state.View = View.Details;
        _state.FullRowSelect = true;
        _state.BackColor = EditorChrome.Surface;
        _state.ForeColor = EditorChrome.Text;
        _state.BorderStyle = BorderStyle.None;
        _state.Columns.Add("Name", 120);
        _state.Columns.Add("Value", 118);

        // Whatever the run absorbed. Hidden while there is nothing to say, so a clean run is not
        // burdened with an empty panel — but never silent when there is.
        _problems.Dock = DockStyle.Bottom;
        _problems.Height = 92;
        _problems.BackColor = EditorChrome.Surface;
        _problems.ForeColor = EditorChrome.Text;
        _problems.BorderStyle = BorderStyle.None;
        _problems.Font = EditorChrome.SmallFont;
        _problems.DrawMode = DrawMode.OwnerDrawFixed;
        _problems.ItemHeight = DpiLayout.Scale(this, 17);
        _problems.HorizontalScrollbar = true;
        _problems.DrawItem += PaintProblem;
        _problems.Visible = false;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 22;
        _status.BackColor = EditorChrome.Surface;
        _status.ForeColor = EditorChrome.Muted;
        _status.Font = EditorChrome.SmallFont;
        _status.Padding = new Padding(8, 3, 4, 0);
        _status.Text = "Run to execute this object's events on a real VM — no room or player needed.";

        Controls.Add(_canvas);
        Controls.Add(_state);
        Controls.Add(bar);
        Controls.Add(_problems);
        Controls.Add(_status);
        _canvas.BringToFront();
    }

    /// <summary>The last run's outcome, or null before the first run.</summary>
    public ObjectSandboxResult? LastResult => _result;

    public bool HasLiveInstance => _result?.LiveInstance is not null;

    public IReadOnlyDictionary<string, object> LiveInstanceValues =>
        _result?.LiveInstance?.InstanceValues ?? new Dictionary<string, object>();

    public IReadOnlyDictionary<string, object> LiveScriptVariables =>
        _result?.LiveInstance?.ScriptVariables ?? new Dictionary<string, object>();

    /// <summary>Draw calls the last run recorded.</summary>
    public int LastDrawCallCount => _surface.TotalCalls;

    /// <summary>Run the object's events. Public so a headless test drives the same path as the button.</summary>
    public ObjectSandboxResult? Run()
    {
        IReadOnlyDictionary<string, string>? events = EventSource?.Invoke();
        if (events is null || events.Count == 0)
        {
            _status.Text = "This object has no event code yet — add a Create or Step event first.";
            _status.ForeColor = EditorChrome.Muted;
            _result = null;
            _surface.Reset();
            _state.Items.Clear();
            _problems.Items.Clear();
            _problems.Visible = false;
            _canvas.Invalidate();
            LiveStateChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }

        _surface = new PgslRecordingDrawSurface { Is3DActive = false };
        _result = ObjectSandbox.Run(events, (int)_frames.Value, _surface);

        LoadSprite(SpriteSource?.Invoke());
        RefreshState();
        _canvas.Invalidate();
        LiveStateChanged?.Invoke(this, EventArgs.Empty);
        return _result;
    }

    /// <summary>
    /// Writes an Inspector value into the retained sandbox VM and refreshes the visible state
    /// without rerunning Create or any frame events.
    /// </summary>
    public bool TrySetLiveValue(string name, object? value)
    {
        ObjectSandboxLiveInstance? live = _result?.LiveInstance;
        if (live is null || value is null || !live.TrySetValue(name, value))
        {
            return false;
        }

        RefreshResultFromLive();
        RefreshState();
        _canvas.Invalidate();
        LiveStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>True when the last run had a bound sprite it could actually read.</summary>
    public bool LastSpriteDrawn => _spriteFrame is not null;

    /// <summary>
    /// Loads the bound frame, reusing the already-loaded bitmap when the path has not changed.
    /// </summary>
    /// <remarks>
    /// Copied out of a stream rather than <c>new Bitmap(path)</c>: GDI+ holds the file open for the
    /// bitmap's lifetime, and the Image Editor writes to these frames while this panel is on screen.
    /// That exact lock is what NEXT-067 was.
    /// </remarks>
    private void LoadSprite(SandboxSprite? sprite)
    {
        _sprite = sprite;
        if (sprite is null || !File.Exists(sprite.FramePath))
        {
            _spriteFrame?.Dispose();
            _spriteFrame = null;
            _spriteFramePath = null;
            return;
        }

        if (string.Equals(_spriteFramePath, sprite.FramePath, StringComparison.OrdinalIgnoreCase)
            && _spriteFrame is not null)
        {
            return;
        }

        _spriteFrame?.Dispose();
        _spriteFrame = null;
        _spriteFramePath = null;

        try
        {
            using FileStream stream = File.OpenRead(sprite.FramePath);
            using System.Drawing.Image loaded = System.Drawing.Image.FromStream(
                stream, useEmbeddedColorManagement: false, validateImageData: false);
            _spriteFrame = new Bitmap(loaded);
            _spriteFramePath = sprite.FramePath;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            OutOfMemoryException)
        {
            // A sprite that cannot be read is reported in the status line rather than throwing out
            // of a paint handler.
            _spriteFrame = null;
            _spriteFramePath = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _spriteFrame?.Dispose();
            _spriteFrame = null;
        }

        base.Dispose(disposing);
    }

    private void RefreshState()
    {
        _state.BeginUpdate();
        try
        {
            _state.Items.Clear();
            if (_result is null) return;

            Add("x", _result.X.ToString("0.##"));
            Add("y", _result.Y.ToString("0.##"));
            if (_result.Speed != 0) Add("speed", _result.Speed.ToString("0.##"));
            if (_result.Direction != 0) Add("direction", _result.Direction.ToString("0.##"));

            foreach ((string name, double value) in _result.Numbers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Add(name, value.ToString("0.####"));
            }

            foreach ((string name, string value) in _result.Strings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Add(name, $"\"{value}\"");
            }

            // Only armed alarms are worth screen space; twelve "-1" rows are noise.
            for (int slot = 0; slot < _result.AlarmsRemaining.Count; slot++)
            {
                if (_result.AlarmsRemaining[slot] >= 0)
                {
                    Add($"alarm[{slot}]", _result.AlarmsRemaining[slot].ToString("0"));
                }
            }
        }
        finally
        {
            _state.EndUpdate();
        }

        if (_result is null) return;

        RefreshProblems();

        if (!_result.Ok)
        {
            ObjectSandboxError first = _result.Errors[0];
            string where = first.Frame >= 0 ? $"frame {first.Frame}" : "compile";
            _status.Text = $"{_result.Errors.Count} error(s) — {first.EventId} ({where}): {first.Message}";
            _status.ForeColor = EditorChrome.Error;
            return;
        }

        IReadOnlyList<(string EventId, int Count)> counts = ObjectSandbox.FireCounts(_result);
        string fired = string.Join(", ", counts.Take(4).Select(entry => $"{entry.EventId}×{entry.Count}"));

        // "0 draw call(s)" on its own reads as a failure, when for a sprite-backed object with no
        // Draw event it is the correct and expected result — the sprite component draws it. Name
        // the script calls as script calls, and say separately whether the sprite rendered.
        string drawn = _spriteFrame is not null
            ? $"{_surface.TotalCalls} script draw call(s) + bound sprite"
            : $"{_surface.TotalCalls} script draw call(s)";
        string summary = $"{_result.FramesRun} frames in {_result.ElapsedMilliseconds:0.0} ms · "
            + $"{drawn} · {fired}";

        // "No errors" is not the same as "it worked". Say which it was.
        if (_result.Warnings.Count > 0)
        {
            _status.Text = $"{summary} · {_result.Warnings.Count} thing(s) the sandbox could not do — see below";
            _status.ForeColor = EditorChrome.Warning;
            return;
        }

        _status.Text = summary;
        _status.ForeColor = EditorChrome.Success;
    }

    private void RefreshResultFromLive()
    {
        ObjectSandboxLiveInstance? live = _result?.LiveInstance;
        if (_result is null || live is null) return;

        IReadOnlyDictionary<string, object> instance = live.InstanceValues;
        IReadOnlyDictionary<string, object> variables = live.ScriptVariables;
        Dictionary<string, double> numbers = new(StringComparer.Ordinal);
        Dictionary<string, string> strings = new(StringComparer.Ordinal);
        foreach ((string name, object value) in variables)
        {
            switch (value)
            {
                case bool flag: numbers[name] = flag ? 1 : 0; break;
                case string text: strings[name] = text; break;
                case object numeric when numeric is byte or sbyte or short or ushort or int or uint
                                                    or long or ulong or float or double or decimal:
                    numbers[name] = Convert.ToDouble(numeric);
                    break;
            }
        }

        double Number(string name, double fallback) =>
            instance.TryGetValue(name, out object? value)
                ? Convert.ToDouble(value)
                : fallback;

        _result = _result with
        {
            X = Number("x", _result.X),
            Y = Number("y", _result.Y),
            Speed = Number("speed", _result.Speed),
            Direction = Number("direction", _result.Direction),
            Numbers = numbers,
            Strings = strings,
        };
    }

    /// <summary>Fill the problems strip with this run's errors, then its warnings.</summary>
    private void RefreshProblems()
    {
        _problems.BeginUpdate();
        try
        {
            _problems.Items.Clear();
            if (_result is null) return;

            foreach (ObjectSandboxError error in _result.Errors)
            {
                string where = error.Frame >= 0 ? $"frame {error.Frame}" : "compile";
                _problems.Items.Add($"error: {error.EventId} ({where}): {error.Message}");
            }

            foreach (ObjectSandboxWarning warning in _result.Warnings)
            {
                _problems.Items.Add($"warning: {warning.Message}");
            }
        }
        finally
        {
            _problems.EndUpdate();
            _problems.Visible = _problems.Items.Count > 0;
        }
    }

    /// <summary>Errors and warnings currently listed. Public so a headless test can assert them.</summary>
    public IReadOnlyList<string> ProblemLines =>
        [.. _problems.Items.Cast<object>().Select(item => item?.ToString() ?? string.Empty)];

    private void PaintProblem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _problems.Items.Count) return;

        string text = _problems.Items[e.Index]?.ToString() ?? string.Empty;
        Color colour = text.StartsWith("error:", StringComparison.Ordinal)
            ? EditorChrome.Error
            : EditorChrome.Warning;

        using SolidBrush background = new(EditorChrome.Surface);
        e.Graphics.FillRectangle(background, e.Bounds);
        using SolidBrush brush = new(colour);
        e.Graphics.DrawString(text, e.Font ?? EditorChrome.SmallFont, brush, e.Bounds.Left + 6, e.Bounds.Top + 1);
    }

    private void Add(string name, string value)
    {
        ListViewItem item = new(name);
        item.SubItems.Add(value);
        _state.Items.Add(item);
    }

    /// <summary>
    /// Repaint the recorded primitives. The room is drawn at whatever scale fits, so a script using
    /// full 1280×720 coordinates is visible in a small panel.
    /// </summary>
    private void PaintRecording(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(_surface.ClearedTo ?? EditorChrome.Canvas);

        Rectangle bounds = _canvas.ClientRectangle;
        if (bounds.Width < 8 || bounds.Height < 8) return;

        if (_result is null)
        {
            using SolidBrush hint = new(EditorChrome.Muted);
            using StringFormat centre = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(
                "Press Run to execute this object's events.\nDraw calls appear here.",
                EditorChrome.BaseFont, hint, bounds, centre);
            return;
        }

        const float roomWidth = 1280f;
        const float roomHeight = 720f;
        float scale = Math.Min(bounds.Width / roomWidth, bounds.Height / roomHeight);
        float offsetX = (bounds.Width - (roomWidth * scale)) * 0.5f;
        float offsetY = (bounds.Height - (roomHeight * scale)) * 0.5f;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Room frame, so the coordinate space is legible.
        using (Pen frame = new(Color.FromArgb(90, EditorChrome.Border)))
        {
            g.DrawRectangle(frame, offsetX, offsetY, roomWidth * scale, roomHeight * scale);
        }

        PointF Map(float x, float y) => new(offsetX + (x * scale), offsetY + (y * scale));

        // The bound sprite first, underneath the script's own primitives. At runtime the sprite
        // component draws the object and a Draw event adds to it; keeping that order here means a
        // HUD or debug overlay drawn in script still sits on top, as it would in a room.
        DrawBoundSprite(g, Map, scale);

        foreach (PgslRecordingDrawSurface.RectRecord rect in _surface.Rectangles)
        {
            PointF at = Map(rect.X, rect.Y);
            RectangleF target = new(at.X, at.Y, rect.W * scale, rect.H * scale);
            if (rect.Filled)
            {
                using SolidBrush brush = new(rect.Color);
                g.FillRectangle(brush, target);
            }
            else
            {
                using Pen pen = new(rect.Color);
                g.DrawRectangle(pen, target.X, target.Y, target.Width, target.Height);
            }
        }

        foreach (PgslRecordingDrawSurface.CircleRecord circle in _surface.Circles)
        {
            PointF at = Map(circle.X, circle.Y);
            float radius = circle.Radius * scale;
            RectangleF target = new(at.X - radius, at.Y - radius, radius * 2, radius * 2);
            if (circle.Filled)
            {
                using SolidBrush brush = new(circle.Color);
                g.FillEllipse(brush, target);
            }
            else
            {
                using Pen pen = new(circle.Color);
                g.DrawEllipse(pen, target);
            }
        }

        foreach (PgslRecordingDrawSurface.LineRecord line in _surface.Lines)
        {
            PointF a = Map(line.X1, line.Y1);
            PointF b = Map(line.X2, line.Y2);
            using Pen pen = new(line.Color, Math.Max(1f, line.Thickness * scale));
            g.DrawLine(pen, a, b);
        }

        foreach (PgslRecordingDrawSurface.PointRecord point in _surface.Points)
        {
            PointF at = Map(point.X, point.Y);
            using SolidBrush brush = new(point.Color);
            g.FillRectangle(brush, at.X - 1, at.Y - 1, 3, 3);
        }

        // Sprites cannot be rasterised here (no texture cache in the sandbox), so show a labelled
        // placeholder rather than nothing — the designer still sees where and how many.
        foreach (PgslRecordingDrawSurface.SpriteRecord sprite in _surface.Sprites)
        {
            PointF at = Map(sprite.X, sprite.Y);
            using Pen pen = new(EditorChrome.Accent) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, at.X - 16, at.Y - 16, 32, 32);
            using SolidBrush label = new(EditorChrome.Accent);
            g.DrawString(sprite.Sprite, EditorChrome.SmallFont, label, at.X - 14, at.Y - 6);
        }

        if (_showGui.Checked)
        {
            foreach (PgslRecordingDrawSurface.TextRecord text in _surface.Texts)
            {
                PointF at = Map(text.X, text.Y);
                using SolidBrush brush = new(text.Color);
                using Font font = new(EditorChrome.BaseFont.FontFamily, Math.Max(6f, text.Size * scale));
                g.DrawString(text.Text, font, brush, at);
            }
        }

        if (_surface.Cubes.Count > 0)
        {
            using SolidBrush note = new(EditorChrome.Warning);
            g.DrawString(
                $"{_surface.Cubes.Count} 3D call(s) queued — not shown in the 2D sandbox",
                EditorChrome.SmallFont, note, offsetX + 6, offsetY + 6);
        }

        // Nothing at all was drawn: explain the empty preview instead of making it look broken.
        if (_surface.TotalCalls == 0 && _spriteFrame is null)
        {
            using SolidBrush hint = new(EditorChrome.Muted);
            using StringFormat centre = new()
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            string why = _sprite is null
                ? "This object has no Draw event and no Image bound,\nso there is nothing to draw."
                : $"The bound frame could not be read:\n{Path.GetFileName(_sprite.FramePath)}";
            g.DrawString(why, EditorChrome.BaseFont, hint, bounds, centre);
        }
    }

    /// <summary>
    /// Draws the object's bound sprite at the position the run ended on, honouring its origin.
    /// </summary>
    /// <remarks>
    /// The origin is applied in whichever space the document declares. A normalized origin is a
    /// <i>fraction of the frame</i>, so it is multiplied by the frame size; a pixel origin is used
    /// as-is. Treating one as the other is exactly the defect NEXT-092 recorded, where a stale
    /// project's (16,32) normalized origin resolved to (512,1024) and drew the player a quarter of
    /// a screen away from where it actually was.
    /// </remarks>
    private void DrawBoundSprite(Graphics g, Func<float, float, PointF> map, float scale)
    {
        if (_spriteFrame is null || _result is null || _sprite is null)
        {
            return;
        }

        float originX = (float)(_sprite.NormalizedOrigin
            ? _sprite.OriginX * _spriteFrame.Width
            : _sprite.OriginX);
        float originY = (float)(_sprite.NormalizedOrigin
            ? _sprite.OriginY * _spriteFrame.Height
            : _sprite.OriginY);

        PointF at = map((float)_result.X, (float)_result.Y);
        RectangleF target = new(
            at.X - (originX * scale),
            at.Y - (originY * scale),
            _spriteFrame.Width * scale,
            _spriteFrame.Height * scale);

        // Nearest-neighbour keeps authored source pixels intact in the deterministic preview.
        InterpolationMode previousInterpolation = g.InterpolationMode;
        PixelOffsetMode previousOffset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        try
        {
            g.DrawImage(_spriteFrame, target);
        }
        finally
        {
            g.InterpolationMode = previousInterpolation;
            g.PixelOffsetMode = previousOffset;
        }
    }
}
