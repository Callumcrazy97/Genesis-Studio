using System.Drawing.Imaging;

namespace Genesis.Application.Editors.Image.Dialogs;

/// <summary>
/// Modal effect preview with live bitmap result and per-effect parameter controls.
/// </summary>
public sealed class EffectPreviewDialog : DpiAwareForm
{
    private static readonly Color Canvas = Color.FromArgb(22, 24, 30);
    private static readonly Color Surface = Color.FromArgb(28, 31, 38);
    private static readonly Color Raised = Color.FromArgb(37, 40, 48);
    private static readonly Color TextColor = Color.FromArgb(238, 241, 248);
    private static readonly Color Muted = Color.FromArgb(157, 166, 187);
    private static readonly Color Accent = Color.FromArgb(108, 140, 255);

    private readonly byte[] _source;
    private readonly int _width;
    private readonly int _height;
    private readonly ImageEffectDefinition _definition;
    private readonly Dictionary<string, object> _parameters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> _controls = new(StringComparer.Ordinal);
    private readonly PictureBox _preview = new();
    private readonly Panel _controlsHost = new();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 60 };
    private Bitmap? _previewBitmap;
    private byte[]? _resultPixels;

    public EffectPreviewDialog(
        IWin32Window? owner,
        byte[] sourceRgba,
        int width,
        int height,
        ImageEffectDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(sourceRgba);
        ArgumentNullException.ThrowIfNull(definition);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (sourceRgba.Length < checked(width * height * 4))
            throw new ArgumentException("Source buffer is smaller than width*height*4.", nameof(sourceRgba));

        _source = (byte[])sourceRgba.Clone();
        _width = width;
        _height = height;
        _definition = definition;

        Text = definition.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Canvas;
        ForeColor = TextColor;
        ClientSize = new Size(640, 560);
        if (owner is Control control)
            Font = control.Font;

        foreach (EffectParameter parameter in definition.Parameters)
            _parameters[parameter.Name] = parameter.Default;

        BuildLayout();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RefreshPreview();
        };

        Shown += (_, _) => RefreshPreview();
        FormClosed += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Dispose();
            _previewBitmap?.Dispose();
        };
    }

    /// <summary>Resulting RGBA after OK; null if cancelled.</summary>
    public byte[]? ResultPixels => _resultPixels;

    public IReadOnlyDictionary<string, object> Parameters => _parameters;

    public static bool TryShow(
        IWin32Window? owner,
        byte[] sourceRgba,
        int width,
        int height,
        ImageEffectDefinition definition,
        out byte[] resultPixels,
        out IReadOnlyDictionary<string, object> parameters)
    {
        using EffectPreviewDialog dialog = new(owner, sourceRgba, width, height, definition);
        if (dialog.ShowDialog(owner) == DialogResult.OK && dialog.ResultPixels is not null)
        {
            resultPixels = dialog.ResultPixels;
            parameters = new Dictionary<string, object>(dialog._parameters, StringComparer.Ordinal);
            return true;
        }

        resultPixels = Array.Empty<byte>();
        parameters = new Dictionary<string, object>();
        return false;
    }

    private void BuildLayout()
    {
        Panel previewHost = new()
        {
            Location = new Point(12, 12),
            Size = new Size(616, 320),
            BackColor = Raised,
            Padding = new Padding(8),
        };
        _preview.Dock = DockStyle.Fill;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(18, 20, 26);
        previewHost.Controls.Add(_preview);
        Controls.Add(previewHost);

        _controlsHost.Location = new Point(12, 340);
        _controlsHost.Size = new Size(616, 140);
        _controlsHost.BackColor = Surface;
        _controlsHost.AutoScroll = true;
        _controlsHost.Padding = new Padding(8);
        Controls.Add(_controlsHost);
        BuildParameterControls();

        Button ok = new()
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(436, 500),
            Size = new Size(90, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) =>
        {
            RefreshPreview(force: true);
            _resultPixels = ApplyEffect();
            DialogResult = DialogResult.OK;
        };

        Button cancel = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(538, 500),
            Size = new Size(90, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Raised,
            ForeColor = TextColor,
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 76, 90);

        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void BuildParameterControls()
    {
        if (_definition.Parameters.Count == 0)
        {
            Label empty = new()
            {
                Text = "No adjustable parameters — preview the result, then OK to apply.",
                ForeColor = Muted,
                AutoSize = true,
                Location = new Point(12, 16),
            };
            _controlsHost.Controls.Add(empty);
            return;
        }

        int y = 10;
        foreach (EffectParameter parameter in _definition.Parameters)
        {
            Label label = new()
            {
                Text = parameter.DisplayName + ":",
                ForeColor = TextColor,
                Location = new Point(12, y + 4),
                Size = new Size(120, 20),
            };
            _controlsHost.Controls.Add(label);

            switch (parameter.Type)
            {
                case EffectParamType.Slider:
                {
                    TrackBar track = new()
                    {
                        Location = new Point(140, y),
                        Size = new Size(360, 45),
                        Minimum = (int)parameter.Min,
                        Maximum = (int)parameter.Max,
                        Value = Convert.ToInt32(parameter.Default),
                        TickFrequency = Math.Max(1, ((int)parameter.Max - (int)parameter.Min) / 10),
                        BackColor = Surface,
                    };
                    Label valueLabel = new()
                    {
                        Text = track.Value.ToString(),
                        ForeColor = Muted,
                        Location = new Point(510, y + 4),
                        Size = new Size(60, 20),
                    };
                    track.ValueChanged += (_, _) =>
                    {
                        valueLabel.Text = track.Value.ToString();
                        _parameters[parameter.Name] = track.Value;
                        SchedulePreview();
                    };
                    _controlsHost.Controls.Add(track);
                    _controlsHost.Controls.Add(valueLabel);
                    _controls[parameter.Name] = track;
                    y += 48;
                    break;
                }
                case EffectParamType.Colour:
                {
                    Button swatch = new()
                    {
                        Location = new Point(140, y),
                        Size = new Size(88, 28),
                        BackColor = (Color)parameter.Default,
                        FlatStyle = FlatStyle.Flat,
                        Text = string.Empty,
                    };
                    swatch.FlatAppearance.BorderColor = Color.FromArgb(70, 76, 90);
                    swatch.Click += (_, _) =>
                    {
                        using ColorDialog picker = new() { Color = swatch.BackColor, FullOpen = true };
                        if (picker.ShowDialog(this) != DialogResult.OK) return;
                        swatch.BackColor = picker.Color;
                        _parameters[parameter.Name] = picker.Color;
                        SchedulePreview();
                    };
                    _controlsHost.Controls.Add(swatch);
                    _controls[parameter.Name] = swatch;
                    y += 36;
                    break;
                }
                case EffectParamType.Numeric:
                {
                    NumericUpDown numeric = new()
                    {
                        Location = new Point(140, y),
                        Size = new Size(88, 28),
                        Minimum = (decimal)parameter.Min,
                        Maximum = (decimal)parameter.Max,
                        Value = Convert.ToDecimal(parameter.Default),
                        DecimalPlaces = 0,
                    };
                    numeric.ValueChanged += (_, _) =>
                    {
                        _parameters[parameter.Name] = (int)numeric.Value;
                        SchedulePreview();
                    };
                    _controlsHost.Controls.Add(numeric);
                    _controls[parameter.Name] = numeric;
                    y += 36;
                    break;
                }
            }
        }
    }

    private void SchedulePreview()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void RefreshPreview(bool force = false)
    {
        if (!force && !IsHandleCreated) return;
        byte[] pixels = ApplyEffect();
        Bitmap bitmap = RgbaToBitmap(pixels, _width, _height);
        Bitmap? previous = _previewBitmap;
        _previewBitmap = bitmap;
        _preview.Image = bitmap;
        previous?.Dispose();
    }

    private byte[] ApplyEffect() =>
        _definition.Apply(_source, _width, _height, _parameters);

    private static Bitmap RgbaToBitmap(byte[] rgba, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            // GDI+ expects BGRA in memory for Format32bppArgb on little-endian.
            byte[] bgra = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                bgra[i] = rgba[i + 2];
                bgra[i + 1] = rgba[i + 1];
                bgra[i + 2] = rgba[i];
                bgra[i + 3] = rgba[i + 3];
            }

            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        // Checker backdrop for transparency is handled by Zoom on dark PictureBox.
        return bitmap;
    }
}
