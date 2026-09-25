using System.Windows.Forms;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// Modal wrapper around <see cref="TerrainCreationPanel"/> for toolbar New… and first-time creation.
/// </summary>
public sealed class TerrainCreationWizardDialog : DpiAwareForm
{
    private readonly TerrainCreationPanel _panel;
    private readonly TerrainAssetPreview _live = new("");
    private readonly CheckBox _manual = new() { Text = "Place manually", AutoSize = true };
    private readonly NumericUpDown[] _coordinates = Enumerable.Range(0, 3).Select(_ => new NumericUpDown { Minimum = -100000, Maximum = 100000, DecimalPlaces = 2, Width = 90 }).ToArray();
    public TerrainCreationResult? PreviewResult { get; private set; }
    public bool PlaceManually => _manual.Checked;
    public System.Numerics.Vector3 PlacementPosition => new((float)_coordinates[0].Value, (float)_coordinates[1].Value, (float)_coordinates[2].Value);

    public TerrainCreationWizardDialog(string suggestedName)
    {
        Text = "New Terrain";
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(1240, 780); MinimumSize = new Size(1100, 730);

        _panel = new TerrainCreationPanel(suggestedName) { Dock = DockStyle.Fill };

        Panel footer = new() { BackColor = EditorChrome.Surface, Dock = DockStyle.Bottom, Height = 50 };
        Button create = new() { Text = "Create", Width = 110, DialogResult = DialogResult.OK, Dock = DockStyle.Right };
        Button cancel = new() { Text = "Cancel", Width = 110, DialogResult = DialogResult.Cancel, Dock = DockStyle.Right };
        EditorChrome.StyleField(create);
        EditorChrome.StyleField(cancel);
        create.Margin = cancel.Margin = new Padding(8);
        footer.Controls.Add(create);
        footer.Controls.Add(cancel);
        AcceptButton = create;
        CancelButton = cancel;

        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1240, 730), SplitterDistance = 760, Panel1MinSize = 700, Panel2MinSize = 300 };
        split.Panel1.Controls.Add(_panel); split.Panel2.Controls.Add(_live);
        var placement = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 670, Padding = new Padding(8) };
        for (int i = 0; i < 3; i++) { placement.Controls.Add(new Label { Text = "XYZ"[i] + " (m)", AutoSize = true }); placement.Controls.Add(_coordinates[i]); EditorChrome.StyleField(_coordinates[i]); }
        placement.Controls.Add(_manual); footer.Controls.Add(placement);
        void Preview() { if (_panel.PreviewAsset is { } terrain) { PreviewResult = TerrainHeightfieldBridge.FromHeightfield(terrain, _panel.TerrainName); _live.SetMesh(PreviewResult.Model); } }
        _panel.PreviewChanged += (_, _) => Preview(); Shown += (_, _) => Preview();
        Controls.Add(split);
        Controls.Add(footer);
    }

    public string TerrainName => _panel.TerrainName;

    public TerrainGenParams Params => _panel.Params;

    public TerrainAsset? PreviewAsset => _panel.PreviewAsset;

    public void SetPreset(TerrainPreset preset) => _panel.SetPreset(preset);

    public void SetSeed(int seed) => _panel.SetSeed(seed);

    public void SetProcesses(int erosionIterations, float erosionStrength, float terraceStrength,
        int riverCount, float riverDepth) =>
        _panel.SetProcesses(erosionIterations, erosionStrength, terraceStrength, riverCount, riverDepth);

    public TerrainAsset Regenerate() => _panel.Regenerate();
}
