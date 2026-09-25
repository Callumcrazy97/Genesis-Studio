using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// Embeddable terrain generation UI: preset cards, parameters, and live top-down preview.
/// Used in the Terrain Editor Generate mode and wrapped by <see cref="TerrainCreationWizardDialog"/>.
/// </summary>
public sealed class TerrainCreationPanel : UserControl
{
    private static readonly (int Value, string Label)[] ResolutionOptions =
    [
        (65, "65 × 65 (small)"),
        (129, "129 × 129 (default)"),
        (257, "257 × 257 (large)"),
        (513, "513 × 513 (huge)"),
    ];

    private readonly TerrainGenParams _params = new();
    private readonly Dictionary<TerrainPreset, Panel> _presetCards = [];
    private readonly TextBox _nameBox;
    private readonly ThemedComboBox _resolutionCombo;
    private readonly NumericUpDown _cellSize;
    private readonly NumericUpDown _minHeight;
    private readonly NumericUpDown _maxHeight;
    private readonly NumericUpDown _seed;
    private readonly NumericUpDown _erosionIterations;
    private readonly NumericUpDown _erosionStrength;
    private readonly NumericUpDown _terraceStrength;
    private readonly NumericUpDown _riverCount;
    private readonly NumericUpDown _riverDepth;
    private readonly PictureBox _thumbnail;
    private readonly Label _presetDescription;
    private readonly Random _random = new();
    private bool _suppressPreview;
    public event EventHandler? PreviewChanged;

    public TerrainCreationPanel(string suggestedName)
    {
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
        Dock = DockStyle.Fill;
        MinimumSize = new Size(520, 420);

        _suppressPreview = true;
        _params.Seed = 1337;

        FlowLayoutPanel presets = new()
        {
            AutoScroll = true,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(10),
            Width = 200,
            WrapContents = false,
        };
        foreach (TerrainPreset preset in Enum.GetValues<TerrainPreset>())
        {
            Panel card = BuildPresetCard(preset);
            _presetCards[preset] = card;
            presets.Controls.Add(card);
        }

        Panel right = new() { BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true };

        _presetDescription = new Label
        {
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            Height = 36,
            Text = TerrainGenerator.Describe(_params.Preset),
        };

        _nameBox = new TextBox { Text = suggestedName, Width = 240 };
        EditorChrome.StyleField(_nameBox);

        _resolutionCombo = new ThemedComboBox { Name = "TerrainResolutionPicker", Width = 200 };
        EditorChrome.StyleField(_resolutionCombo);
        foreach ((int _, string label) in ResolutionOptions)
        {
            _resolutionCombo.Items.Add(label);
        }

        _resolutionCombo.SelectedIndex = 1;
        _resolutionCombo.SelectedIndexChanged += (_, _) => { ApplyResolution(); QueuePreview(); };

        _cellSize = MakeNumeric(0.1m, 32m, 2, (decimal)_params.CellSize);
        _minHeight = MakeNumeric(-512m, 0m, 1, (decimal)_params.MinHeight);
        _maxHeight = MakeNumeric(1m, 1024m, 1, (decimal)_params.MaxHeight);
        _seed = MakeNumeric(0m, 99999m, 0, _params.Seed);
        _erosionIterations = MakeNumeric(0m, 100m, 0, _params.ErosionIterations);
        _erosionStrength = MakeNumeric(0m, 1m, 2, (decimal)_params.ErosionStrength);
        _terraceStrength = MakeNumeric(0m, 1m, 2, (decimal)_params.TerraceStrength);
        _riverCount = MakeNumeric(0m, 8m, 0, _params.RiverCount);
        _riverDepth = MakeNumeric(0.1m, 128m, 1, (decimal)_params.RiverDepth);
        _cellSize.ValueChanged += (_, _) => { _params.CellSize = (float)_cellSize.Value; QueuePreview(); };
        _minHeight.ValueChanged += (_, _) => { _params.MinHeight = (float)_minHeight.Value; QueuePreview(); };
        _maxHeight.ValueChanged += (_, _) => { _params.MaxHeight = (float)_maxHeight.Value; QueuePreview(); };
        _seed.ValueChanged += (_, _) => { _params.Seed = (int)_seed.Value; QueuePreview(); };
        _erosionIterations.ValueChanged += (_, _) => { _params.ErosionIterations = (int)_erosionIterations.Value; QueuePreview(); };
        _erosionStrength.ValueChanged += (_, _) => { _params.ErosionStrength = (float)_erosionStrength.Value; QueuePreview(); };
        _terraceStrength.ValueChanged += (_, _) => { _params.TerraceStrength = (float)_terraceStrength.Value; QueuePreview(); };
        _riverCount.ValueChanged += (_, _) => { _params.RiverCount = (int)_riverCount.Value; QueuePreview(); };
        _riverDepth.ValueChanged += (_, _) => { _params.RiverDepth = (float)_riverDepth.Value; QueuePreview(); };

        Button randomize = new() { Text = "Randomize seed", Width = 110, Height = 26 };
        EditorChrome.StyleField(randomize);
        randomize.Click += (_, _) => { _seed.Value = _random.Next(0, 100000); };

        _thumbnail = new PictureBox
        {
            BackColor = EditorChrome.Canvas,
            BorderStyle = BorderStyle.None,
            Size = new Size(200, 200),
            SizeMode = PictureBoxSizeMode.Zoom,
        };

        TableLayoutPanel form = new()
        {
            BackColor = Color.Transparent,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(form, "Name", _nameBox);
        AddRow(form, "Resolution", _resolutionCombo);
        AddRow(form, "Cell size (m)", _cellSize);
        AddRow(form, "Min height", _minHeight);
        AddRow(form, "Max height", _maxHeight);
        Panel seedRow = new() { BackColor = Color.Transparent, Dock = DockStyle.Fill, Height = 30 };
        _seed.Location = new Point(0, 2);
        randomize.Location = new Point(90, 1);
        seedRow.Controls.Add(_seed);
        seedRow.Controls.Add(randomize);
        AddRow(form, "Seed", seedRow);
        AddRow(form, "Erosion passes", _erosionIterations);
        AddRow(form, "Erosion strength", _erosionStrength);
        AddRow(form, "Terracing", _terraceStrength);
        AddRow(form, "River count", _riverCount);
        AddRow(form, "River depth", _riverDepth);

        Label thumbCaption = new()
        {
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            Height = 20,
            Text = "PREVIEW (top-down)",
        };
        Panel thumbHost = new() { BackColor = Color.Transparent, Dock = DockStyle.Top, Height = 242, MinimumSize = new Size(200, 220) };
        _thumbnail.Location = new Point(0, 26);
        thumbHost.Controls.Add(_thumbnail);
        thumbHost.Controls.Add(thumbCaption);

        right.Controls.Add(thumbHost);
        right.Controls.Add(form);
        right.Controls.Add(_presetDescription);

        Controls.Add(right);
        Controls.Add(presets);

        _suppressPreview = false;
        RefreshPresetSelection();
        Regenerate();
    }

    public string TerrainName => string.IsNullOrWhiteSpace(_nameBox.Text) ? "Terrain" : _nameBox.Text.Trim();

    public TerrainGenParams Params => _params.Clone();

    public TerrainAsset? PreviewAsset { get; private set; }

    public void SetPreset(TerrainPreset preset)
    {
        _params.Preset = preset;
        _presetDescription.Text = TerrainGenerator.Describe(preset);
        RefreshPresetSelection();
        QueuePreview();
    }

    public void SetSeed(int seed) => _seed.Value = Math.Clamp(seed, 0, 99999);

    public void SetProcesses(int erosionIterations, float erosionStrength, float terraceStrength,
        int riverCount, float riverDepth)
    {
        _erosionIterations.Value = Math.Clamp(erosionIterations, 0, 100);
        _erosionStrength.Value = Math.Clamp((decimal)erosionStrength, _erosionStrength.Minimum, _erosionStrength.Maximum);
        _terraceStrength.Value = Math.Clamp((decimal)terraceStrength, _terraceStrength.Minimum, _terraceStrength.Maximum);
        _riverCount.Value = Math.Clamp(riverCount, 0, 8);
        _riverDepth.Value = Math.Clamp((decimal)riverDepth, _riverDepth.Minimum, _riverDepth.Maximum);
    }

    public TerrainAsset Regenerate()
    {
        _params.CellSize = (float)_cellSize.Value;
        _params.MinHeight = (float)_minHeight.Value;
        _params.MaxHeight = (float)_maxHeight.Value;
        _params.Seed = (int)_seed.Value;
        _params.ErosionIterations = (int)_erosionIterations.Value;
        _params.ErosionStrength = (float)_erosionStrength.Value;
        _params.TerraceStrength = (float)_terraceStrength.Value;
        _params.RiverCount = (int)_riverCount.Value;
        _params.RiverDepth = (float)_riverDepth.Value;
        ApplyResolution();
        PreviewAsset = TerrainGenerator.Generate(_params);
        _thumbnail.Image?.Dispose();
        _thumbnail.Image = RenderThumbnail(PreviewAsset);
        PreviewChanged?.Invoke(this, EventArgs.Empty);
        return PreviewAsset;
    }

    private void QueuePreview()
    {
        if (_suppressPreview) return;
        Regenerate();
    }

    private void ApplyResolution()
    {
        int idx = Math.Clamp(_resolutionCombo.SelectedIndex, 0, ResolutionOptions.Length - 1);
        _params.ResolutionX = _params.ResolutionZ = ResolutionOptions[idx].Value;
    }

    private Panel BuildPresetCard(TerrainPreset preset)
    {
        Panel card = new()
        {
            BackColor = EditorChrome.Raised,
            Cursor = Cursors.Hand,
            Height = 54,
            Margin = new Padding(0, 0, 0, 8),
            Width = 176,
        };
        Label title = new()
        {
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Height = 22,
            Padding = new Padding(8, 4, 0, 0),
            Text = preset.ToString(),
        };
        Label sub = new()
        {
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Padding = new Padding(8, 0, 6, 4),
            Text = TerrainGenerator.Describe(preset).Split('—')[0].Trim(),
        };
        void Pick(object? s, EventArgs e) => SetPreset(preset);
        card.Click += Pick;
        title.Click += Pick;
        sub.Click += Pick;
        card.Controls.Add(sub);
        card.Controls.Add(title);
        return card;
    }

    private void RefreshPresetSelection()
    {
        foreach ((TerrainPreset preset, Panel card) in _presetCards)
        {
            card.BackColor = preset == _params.Preset ? EditorChrome.Hover : EditorChrome.Raised;
        }
    }

    private static Bitmap RenderThumbnail(TerrainAsset asset)
    {
        const int size = 200;
        Bitmap bmp = new(size, size);
        Vector3 light = Vector3.Normalize(new Vector3(-0.5f, 1f, -0.35f));
        for (int py = 0; py < size; py++)
        {
            int z = py * (asset.ResolutionZ - 1) / (size - 1);
            for (int px = 0; px < size; px++)
            {
                int x = px * (asset.ResolutionX - 1) / (size - 1);
                float h = asset.GetHeight(x, z);
                float hl = asset.GetHeight(Math.Max(x - 1, 0), z);
                float hr = asset.GetHeight(Math.Min(x + 1, asset.ResolutionX - 1), z);
                float hd = asset.GetHeight(x, Math.Max(z - 1, 0));
                float hu = asset.GetHeight(x, Math.Min(z + 1, asset.ResolutionZ - 1));
                Vector3 n = Vector3.Normalize(new Vector3(hl - hr, 2f * asset.CellSize, hd - hu));
                float shade = 0.45f + 0.55f * MathF.Max(0f, Vector3.Dot(n, light));

                (byte r, byte g, byte b, byte a) = asset.GetSplat(x, z);
                float wsum = MathF.Max(1f, r + g + b + a);
                float cr = (0.33f * r + 0.44f * g + 0.46f * b + 0.90f * a) / wsum;
                float cg = (0.52f * r + 0.34f * g + 0.46f * b + 0.92f * a) / wsum;
                float cb = (0.26f * r + 0.23f * g + 0.50f * b + 0.96f * a) / wsum;
                if (h < 0f)
                {
                    cr = 0.16f; cg = 0.34f; cb = 0.52f;
                    shade = 0.7f;
                }

                bmp.SetPixel(px, py, Color.FromArgb(
                    Clamp255(cr * shade),
                    Clamp255(cg * shade),
                    Clamp255(cb * shade)));
            }
        }

        return bmp;
    }

    private static int Clamp255(float v) => Math.Clamp((int)(v * 255f), 0, 255);

    private static NumericUpDown MakeNumeric(decimal min, decimal max, int decimals, decimal value)
    {
        NumericUpDown n = new()
        {
            DecimalPlaces = decimals,
            Increment = decimals == 0 ? 1m : (decimal)Math.Pow(10, -decimals) * 10,
            Maximum = max,
            Minimum = min,
            Value = Math.Clamp(value, min, max),
            Width = 84,
        };
        EditorChrome.StyleField(n);
        return n;
    }

    private static void AddRow(TableLayoutPanel table, string label, Control field)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        table.Controls.Add(new Label
        {
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            Dock = DockStyle.Fill,
            Text = label.ToUpperInvariant(),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, row);
        Panel host = new() { BackColor = Color.Transparent, Dock = DockStyle.Fill };
        field.Location = new Point(0, 2);
        host.Controls.Add(field);
        table.Controls.Add(host, 1, row);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnail.Image?.Dispose();
        }

        base.Dispose(disposing);
    }
}
