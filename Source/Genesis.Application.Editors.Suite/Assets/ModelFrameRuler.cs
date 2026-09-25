using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Genesis.Application.Editors.Image;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Scrollable front-view filmstrip shared by Model Viewer and Editor.</summary>
internal sealed class ModelFrameRuler : Control
{
    private const int CardWidth = 104, CacheLimit = 96;
    private readonly HScrollBar _scroll = new() { Dock = DockStyle.Bottom, SmallChange = CardWidth };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    private readonly Dictionary<int, Bitmap> _images = new();
    private readonly HashSet<int> _requested = [];
    private readonly ModelFramePreviews _previews = new();
    private GModelAsset? _asset, _snapshot;
    private GModelAnimationClip? _clip;
    private string _root = "", _error = "";
    private int _value, _maximum = 1, _generation;
    private long _changedAt;
    public int Minimum { get; set; }
    public int Maximum { get => _maximum; set { _maximum = value; UpdateScroll(); } }
    public int[] PoseFrames { get; set; } = [];
    public int CachedPreviewCount => _images.Count;
    public event EventHandler? ValueChanged;
    public int Value
    {
        get => _value;
        set
        {
            int next = Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum));
            if (next == _value) return;
            _value = next; RevealFrame(); Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ModelFrameRuler()
    {
        DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); TabStop = true;
        AccessibleName = "Animation frame previews";
        Controls.Add(_scroll); _scroll.ValueChanged += (_, _) => Invalidate();
        _timer.Tick += (_, _) =>
        {
            try { UpdatePreviews(); }
            catch (Exception ex) { _error = "Preview unavailable: " + ex.Message; Invalidate(); }
        };
        HandleCreated += (_, _) => _timer.Start();
        SizeChanged += (_, _) => UpdateScroll();
    }

    public void SetSource(GModelAsset asset, GModelAnimationClip? clip, string root)
    {
        if (ReferenceEquals(_asset, asset) && ReferenceEquals(_clip, clip) && _root == root) return;
        _asset = asset; _clip = clip; _root = root; _scroll.Value = 0; ResetPreviews();
    }

    public void ResetPreviews()
    {
        _previews.Reset(++_generation); _snapshot = null; _requested.Clear(); _error = "";
        foreach (var image in _images.Values) image.Dispose(); _images.Clear();
        _changedAt = Environment.TickCount64; Invalidate();
    }

    private void UpdatePreviews()
    {
        while (_previews.TryTake(out var result) && result is not null)
        {
            if (result.Generation != _generation) continue;
            if (result.Error is not null) { _error = "Preview unavailable: " + result.Error; Invalidate(); continue; }
            if (result.Pixels is null) continue;
            var image = new Bitmap(ModelFramePreviews.Size, ModelFramePreviews.Size, PixelFormat.Format32bppArgb);
            var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.WriteOnly, image.PixelFormat);
            try { Marshal.Copy(result.Pixels, 0, data.Scan0, result.Pixels.Length); }
            finally { image.UnlockBits(data); }
            if (_images.Remove(result.Frame, out var previous)) previous.Dispose();
            _images[result.Frame] = image;
            while (_images.Count > CacheLimit)
            {
                int farthest = _images.Keys.MaxBy(frame => Math.Abs(frame - _scroll.Value / CardWidth));
                _images[farthest].Dispose(); _images.Remove(farthest); _requested.Remove(farthest);
            }
            Invalidate();
        }
        if (!Visible || !Enabled || _error.Length > 0 || _asset is null || _clip is not { Frames.Count: > 0 }
            || Environment.TickCount64 - _changedAt < 150) return;
        int first = _scroll.Value / CardWidth, last = Math.Min(Maximum, first + Math.Max(1, Width / CardWidth) + 1);
        int[] missing = Enumerable.Range(first, Math.Max(0, last - first + 1)).Where(f => !_requested.Contains(f)).ToArray();
        if (missing.Length == 0) return;
        _snapshot ??= ModelFramePreviews.Snapshot(_asset, _clip);
        if (_previews.Submit(new(_generation, _snapshot, _root, missing, Handle)))
            foreach (int frame in missing) _requested.Add(frame);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(ImageEditorChrome.Canvas);
        if (!Enabled || _clip is null)
        { TextRenderer.DrawText(g, "No animation selected", Font, ClientRectangle, ImageEditorChrome.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        if (_error.Length > 0)
        {
            TextRenderer.DrawText(g, _error + "\nSelect another clip, then return to retry.", Font,
                new Rectangle(8, 8, Math.Max(1, Width - 16), Math.Max(1, Height - _scroll.Height - 16)),
                ImageEditorChrome.Text, TextFormatFlags.WordBreak);
            AccessibleDescription = _error; return;
        }
        int first = _scroll.Value / CardWidth, last = Math.Min(Maximum, first + Width / CardWidth + 1);
        int size = Math.Min(ModelFramePreviews.Size, Math.Max(20, Height - _scroll.Height - 26));
        for (int frame = first; frame <= last; frame++)
        {
            int x = frame * CardWidth - _scroll.Value + 4;
            var rectangle = new Rectangle(x, 4, ModelFramePreviews.Size, size);
            if (_images.TryGetValue(frame, out var image)) g.DrawImage(image, new Rectangle(x + (ModelFramePreviews.Size - size) / 2, 4, size, size));
            else TextRenderer.DrawText(g, _error.Length > 0 ? "Unavailable" : "Loading…", Font, rectangle, ImageEditorChrome.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using var border = new Pen(frame == Value ? ImageEditorChrome.Accent : ImageEditorChrome.Border, frame == Value ? 2 : 1);
            g.DrawRectangle(border, rectangle);
            string label = $"{frame + 1}" + (PoseFrames.Contains(frame) ? "  ◆" : "");
            TextRenderer.DrawText(g, label, Font, new Rectangle(x, size + 7, ModelFramePreviews.Size, 20),
                frame == Value ? ImageEditorChrome.Text : ImageEditorChrome.Muted, TextFormatFlags.HorizontalCenter);
        }
        AccessibleDescription = _error.Length > 0 ? _error : $"Frame {Value + 1} of {Maximum + 1}. Click or drag a preview; use arrow keys to step.";
    }

    private void UpdateScroll()
    {
        _scroll.LargeChange = Math.Max(1, Width);
        _scroll.Maximum = Math.Max(0, (Maximum + 1) * CardWidth - 1);
        _scroll.Enabled = _scroll.Maximum + 1 > Width;
        _scroll.Value = Math.Min(_scroll.Value, Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1));
    }
    private void RevealFrame()
    {
        int left = Value * CardWidth, next = _scroll.Value;
        if (left < next) next = left;
        else if (left + CardWidth > next + Width) next = left + CardWidth - Width;
        _scroll.Value = Math.Clamp(next, 0, Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1));
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left && Enabled) { Focus(); Capture = true; Scrub(e.X); } }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Capture && e.Button == MouseButtons.Left) Scrub(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Capture = false; }
    protected override void OnMouseWheel(MouseEventArgs e)
    { _scroll.Value = Math.Clamp(_scroll.Value - Math.Sign(e.Delta) * CardWidth * 3, 0, Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1)); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Value--; else if (e.KeyCode == Keys.Right) Value++;
        else if (e.KeyCode == Keys.Home) Value = Minimum; else if (e.KeyCode == Keys.End) Value = Maximum;
    }
    private void Scrub(int x) => Value = Math.Clamp((x + _scroll.Value) / CardWidth, Minimum, Maximum);
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Stop(); _timer.Dispose(); _previews.Dispose(); foreach (var image in _images.Values) image.Dispose(); _images.Clear(); }
        base.Dispose(disposing);
    }
}
