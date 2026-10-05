using System.Drawing;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// "Convert to 2D sprites": renders the model from every facing angle (and every animation frame)
/// into a new Image, the way pre-rendered 3D games turn a model into 2D frames.
/// </summary>
public sealed class ModelSpriteConversionDialog : Form
{
    private const int PreviewDirections = 8;
    private readonly GModelAsset _asset;
    private readonly string _projectRoot, _modelPath;
    private readonly ModelSpriteBakeSettings _defaults;
    private readonly ComboBox _directions = new() { Name = "ModelSpriteDirections", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _elevation = new() { Name = "ModelSpriteElevation", Minimum = 0, Maximum = 89, Value = 30 };
    private readonly NumericUpDown _frameSize = new() { Name = "ModelSpriteFrameSize", Minimum = 16, Maximum = 512, Increment = 16, Value = 128 };
    private readonly ComboBox _projection = new() { Name = "ModelSpriteProjection", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _clip = new() { Name = "ModelSpriteClip", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _fps = new() { Name = "ModelSpriteFps", Minimum = 1, Maximum = 60, Value = 12 };
    private readonly NumericUpDown _frames = new() { Name = "ModelSpriteFrames", Minimum = 1, Maximum = 120, Value = 8 };
    private readonly CheckBox _lighting = new() { Name = "ModelSpriteLighting", Text = "Engine lighting", Checked = true, AutoSize = true };
    private readonly NumericUpDown _toonSteps = new() { Name = "ModelSpriteToonSteps", Minimum = 1, Maximum = 8, Value = 1 };
    private readonly NumericUpDown _outline = new() { Name = "ModelSpriteOutline", Minimum = 0, Maximum = 16, Value = 0 };
    private readonly Button _outlineColour = new() { Name = "ModelSpriteOutlineColour", Text = "Outline colour\u2026", AutoSize = true };
    private readonly CheckBox _transparent = new() { Name = "ModelSpriteTransparent", Text = "Transparent background", Checked = true, AutoSize = true };
    private readonly NumericUpDown _facing = new() { Name = "ModelSpriteFacing", Minimum = -180, Maximum = 180, Increment = 15, Value = 0 };
    private readonly TextBox _name = new() { Name = "ModelSpriteName" };
    private readonly Button _create = new() { Name = "ModelSpriteCreate", Text = "Create Image", AutoSize = true };
    private readonly PictureBox _preview = new() { Name = "ModelSpritePreview", Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label _status = new() { Name = "ModelSpriteStatus", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _previewDelay = new() { Interval = 250 };
    private Color _outlineColor = Color.Black;
    private CancellationTokenSource? _previewCancel;
    private bool _syncing, _busy;

    public ModelSpriteConversionDialog(GModelAsset asset, string projectRoot, string modelPath, ModelSpriteBakeSettings? defaults = null)
    {
        _asset = asset; _projectRoot = projectRoot; _modelPath = modelPath;
        _defaults = (defaults ?? new ModelSpriteBakeSettings()).Normalized();
        Text = "Convert to 2D sprites";
        Name = "ModelSpriteConversion";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(980, 640); MinimumSize = new Size(760, 540);
        BackColor = EditorChrome.Surface; ForeColor = EditorChrome.Text; Font = EditorChrome.BaseFont;

        foreach (int count in ModelSpriteBakeSettings.DirectionChoices) _directions.Items.Add(count);
        _projection.Items.AddRange(["Orthographic", "Perspective"]);
        _clip.Items.Add("None (still pose)");
        foreach (GModelAnimationClip clip in asset.Animations) _clip.Items.Add(clip.Name);
        _name.Text = ResourceNames.Name(projectRoot, modelPath) + " Sprites";

        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(14, 12, 8, 8) };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Row("Directions", _directions);
        Row("Camera elevation (\u00B0)", _elevation);
        Row("Frame size (pixels)", _frameSize);
        Row("Projection", _projection);
        Row("Animation", _clip);
        Row("Frames per second", _fps);
        Row("Frames per direction", _frames);
        Row("", _lighting);
        Row("Cel bands", _toonSteps);
        Row("Outline (pixels)", _outline);
        Row("", _outlineColour);
        Row("", _transparent);
        Row("Front offset (\u00B0)", _facing);
        Row("Image name", _name);
        Label help = new()
        {
            Text = "Frames are saved direction by direction: every animation frame facing 0\u00B0 (right), then the next angle counter-clockwise. "
                + "In PGSL, SpriteSetDirection(angle) shows the matching frame; SpriteDirectionFrame(image, angle, frame) returns its index.",
            AutoSize = false, Dock = DockStyle.Fill, Height = 96, ForeColor = EditorChrome.Muted,
        };
        settings.Controls.Add(help, 0, settings.RowCount); settings.SetColumnSpan(help, 2); settings.RowCount++;

        var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(8, 12, 14, 8) };
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewPanel.Controls.Add(new Label { Text = "LIVE PREVIEW \u00B7 8 directions from 0\u00B0, counter-clockwise", Dock = DockStyle.Fill, ForeColor = EditorChrome.Muted }, 0, 0);
        previewPanel.Controls.Add(_preview, 0, 1);
        _preview.BackColor = EditorChrome.Canvas;

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(14, 6, 14, 10) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        footer.Controls.Add(_status, 0, 0); footer.Controls.Add(_create, 1, 0); footer.Controls.Add(cancel, 2, 0);
        CancelButton = cancel;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        body.Controls.Add(settings, 0, 0); body.Controls.Add(previewPanel, 1, 0);
        body.Controls.Add(footer, 0, 1); body.SetColumnSpan(footer, 2);
        Controls.Add(body);
        foreach (Control control in new Control[] { _directions, _elevation, _frameSize, _projection, _clip, _fps, _frames, _toonSteps, _outline, _outlineColour, _facing, _name, _create, cancel })
            EditorChrome.StyleField(control);

        Configure(_defaults);
        foreach (ComboBox combo in new[] { _directions, _projection, _clip }) combo.SelectedIndexChanged += (_, _) => SettingsChanged();
        foreach (NumericUpDown number in new[] { _elevation, _frameSize, _fps, _frames, _toonSteps, _outline, _facing }) number.ValueChanged += (_, _) => SettingsChanged();
        foreach (CheckBox check in new[] { _lighting, _transparent }) check.CheckedChanged += (_, _) => SettingsChanged();
        _outlineColour.Click += (_, _) =>
        {
            using var picker = new ColorDialog { Color = _outlineColor, FullOpen = true };
            if (picker.ShowDialog(this) == DialogResult.OK) { _outlineColor = picker.Color; SettingsChanged(); }
        };
        _create.Click += async (_, _) => await CreateFromDialogAsync();
        _previewDelay.Tick += (_, _) => { _previewDelay.Stop(); _ = RefreshPreviewAsync(); };
        Shown += (_, _) => SettingsChanged();
        FormClosed += (_, _) => { _previewCancel?.Cancel(); _previewDelay.Stop(); };
        Disposed += (_, _) => { _previewDelay.Dispose(); _preview.Image?.Dispose(); _previewCancel?.Dispose(); };

        void Row(string label, Control control)
        {
            int row = settings.RowCount++;
            settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settings.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 4) }, 0, row);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right; control.Margin = new Padding(0, 4, 0, 4);
            settings.Controls.Add(control, 1, row);
        }
    }

    /// <summary>The Image created by the last successful conversion.</summary>
    public string? CreatedImagePath { get; private set; }

    public string ImageName { get => _name.Text; set => _name.Text = value; }

    /// <summary>The settings the controls currently describe.</summary>
    public ModelSpriteBakeSettings Settings => (_defaults with
    {
        Directions = _directions.SelectedItem is int count ? count : 16,
        ElevationDegrees = (double)_elevation.Value,
        FrameSize = (int)_frameSize.Value,
        Orthographic = _projection.SelectedIndex != 1,
        Clip = _clip.SelectedIndex > 0 ? _clip.SelectedItem?.ToString() ?? "" : "",
        FramesPerSecond = (double)_fps.Value,
        AnimationFrames = (int)_frames.Value,
        Lighting = _lighting.Checked,
        ToonSteps = (int)_toonSteps.Value,
        OutlinePixels = (int)_outline.Value,
        OutlineColor = _outlineColor,
        TransparentBackground = _transparent.Checked,
        FacingOffsetDegrees = (double)_facing.Value,
    }).Normalized();

    public void Configure(ModelSpriteBakeSettings value)
    {
        value = value.Normalized();
        _syncing = true;
        try
        {
            int directions = ModelSpriteBakeSettings.DirectionChoices.Contains(value.Directions) ? value.Directions : 16;
            _directions.SelectedItem = directions;
            _elevation.Value = Math.Clamp((decimal)value.ElevationDegrees, _elevation.Minimum, _elevation.Maximum);
            _frameSize.Value = Math.Clamp(value.FrameSize, (int)_frameSize.Minimum, (int)_frameSize.Maximum);
            _projection.SelectedIndex = value.Orthographic ? 0 : 1;
            int clip = _clip.Items.IndexOf(value.Clip);
            _clip.SelectedIndex = clip > 0 ? clip : 0;
            _fps.Value = Math.Clamp((decimal)value.FramesPerSecond, _fps.Minimum, _fps.Maximum);
            _frames.Value = Math.Clamp(value.AnimationFrames, (int)_frames.Minimum, (int)_frames.Maximum);
            _lighting.Checked = value.Lighting;
            _toonSteps.Value = Math.Clamp(value.ToonSteps, (int)_toonSteps.Minimum, (int)_toonSteps.Maximum);
            _outline.Value = Math.Clamp(value.OutlinePixels, (int)_outline.Minimum, (int)_outline.Maximum);
            _outlineColor = value.OutlineColor;
            _transparent.Checked = value.TransparentBackground;
            _facing.Value = Math.Clamp((decimal)value.FacingOffsetDegrees, _facing.Minimum, _facing.Maximum);
        }
        finally { _syncing = false; }
        SettingsChanged();
    }

    /// <summary>Renders and shows the preview now, on this thread: 8 directions from 0 degrees.</summary>
    public Bitmap RenderPreview()
    {
        ModelSpriteBakeSettings settings = PreviewSettings(Settings);
        IReadOnlyList<byte[]> frames = ModelSpriteBaker.Render(_asset, _projectRoot, settings, PreviewShots(settings));
        Bitmap sheet = ModelSpriteBaker.ContactSheet(frames, settings.FrameSize, settings.FrameSize, 4, 2);
        System.Drawing.Image? old = _preview.Image;
        _preview.Image = new Bitmap(sheet);
        old?.Dispose();
        return sheet;
    }

    /// <summary>Bakes every direction and frame and saves the Image, on this thread.</summary>
    public string CreateImage()
    {
        ModelSpriteBakeSettings settings = Settings;
        ModelSpriteSheet sheet = ModelSpriteBaker.Bake(_asset, _projectRoot, settings);
        return Save(sheet);
    }

    private string Save(ModelSpriteSheet sheet)
    {
        ResourceService resources = ProjectAssetIndex.OpenResourceService(_projectRoot);
        string folder = ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Image);
        string name = ResourceNames.ValidateName(string.IsNullOrWhiteSpace(_name.Text) ? "Model Sprites" : _name.Text.Trim());
        CreatedImagePath = ModelSpriteBaker.WriteImage(resources, folder, name, sheet, ResourceNames.Name(_projectRoot, _modelPath));
        return CreatedImagePath;
    }

    private async Task CreateFromDialogAsync()
    {
        if (_busy) return;
        ModelSpriteBakeSettings settings = Settings;
        try { ResourceNames.ValidateName(string.IsNullOrWhiteSpace(_name.Text) ? "Model Sprites" : _name.Text.Trim()); }
        catch (ArgumentException error) { _status.Text = error.Message; return; }
        int total = settings.Directions * settings.FramesPerDirection;
        _busy = true; _create.Enabled = false; UseWaitCursor = true;
        var progress = new Progress<int>(done => { if (!IsDisposed) _status.Text = $"Rendering frame {done} of {total}\u2026"; });
        try
        {
            ModelSpriteSheet sheet = await Task.Run(() => ModelSpriteBaker.Bake(_asset, _projectRoot, settings, IntPtr.Zero, progress));
            if (IsDisposed) return;
            string path = Save(sheet);
            _status.Text = "Created " + ResourceNames.Name(_projectRoot, path) + ".";
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            if (!IsDisposed) _status.Text = error.Message;
        }
        finally
        {
            if (!IsDisposed) { _busy = false; _create.Enabled = true; UseWaitCursor = false; }
        }
    }

    private void SettingsChanged()
    {
        if (_syncing) return;
        ModelSpriteBakeSettings settings = Settings;
        _fps.Enabled = _frames.Enabled = settings.Clip.Length > 0;
        _toonSteps.Enabled = settings.Lighting;
        _outlineColour.Enabled = settings.OutlinePixels > 0;
        _status.Text = $"{settings.Directions} directions \u00D7 {settings.FramesPerDirection} frame{(settings.FramesPerDirection == 1 ? "" : "s")} = "
            + $"{settings.Directions * settings.FramesPerDirection} frames of {settings.FrameSize}\u00D7{settings.FrameSize}";
        if (IsHandleCreated && Visible) { _previewDelay.Stop(); _previewDelay.Start(); }
    }

    private async Task RefreshPreviewAsync()
    {
        _previewCancel?.Cancel(); _previewCancel?.Dispose();
        var cancel = _previewCancel = new CancellationTokenSource();
        ModelSpriteBakeSettings settings = PreviewSettings(Settings);
        try
        {
            IReadOnlyList<byte[]> frames = await Task.Run(() => ModelSpriteBaker.Render(_asset, _projectRoot, settings, PreviewShots(settings), IntPtr.Zero, null, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested || IsDisposed) return;
            System.Drawing.Image? old = _preview.Image;
            _preview.Image = ModelSpriteBaker.ContactSheet(frames, settings.FrameSize, settings.FrameSize, 4, 2);
            old?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException)
        {
            if (!IsDisposed) _status.Text = "Preview failed: " + error.Message;
        }
    }

    /// <summary>Previews stay quick: at most 128 pixels, and the first animation frame.</summary>
    private static ModelSpriteBakeSettings PreviewSettings(ModelSpriteBakeSettings settings) =>
        settings with { FrameSize = Math.Min(settings.FrameSize, 128) };

    private static IReadOnlyList<(double Angle, int Frame)> PreviewShots(ModelSpriteBakeSettings settings)
    {
        int stride = Math.Max(1, settings.Directions / PreviewDirections);
        return Enumerable.Range(0, Math.Min(PreviewDirections, settings.Directions))
            .Select(index => (settings.AngleOf(index * stride), 0)).ToArray();
    }
}
