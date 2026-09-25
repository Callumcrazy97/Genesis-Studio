using System.Numerics;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainSourceWizard
{
    private readonly TerrainAssetPreview _livePreview = new("");
    private readonly CheckBox _replaceBase = new() { Text = "Replace editable base terrain", AutoSize = true };
    private readonly CheckBox _manual = new() { Text = "Place manually in the viewport", AutoSize = true };
    private readonly NumericUpDown _x = Number(-100000, 100000, 0), _y = Number(-100000, 100000, 0), _z = Number(-100000, 100000, 0);
    private float[,]? _editedHeightmap;
    public TerrainAssetPreview LivePreview => _livePreview;
    public bool ReplaceBase => _replaceBase.Checked && Result?.Heights is not null;
    public bool PlaceManually => _manual.Checked;
    public Vector3 PlacementPosition => new((float)_x.Value, (float)_y.Value, (float)_z.Value);

    private void InstallAuthoringPreview(SplitContainer split, TableLayoutPanel fields)
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var terrain = new TabPage("3D terrain") { BackColor = EditorChrome.Canvas };
        var heightmap = new TabPage("Heightmap / cross-section") { BackColor = EditorChrome.Canvas };
        terrain.Controls.Add(_livePreview); heightmap.Controls.Add(_preview);
        tabs.TabPages.AddRange([terrain, heightmap]); split.Panel2.Controls.Add(tabs); tabs.BringToFront();
        var edit = new Button { Text = "Create / edit heightmap in Image Editor…", Height = 36, Dock = DockStyle.Top };
        EditorChrome.StyleField(edit); fields.Controls.Add(edit); edit.Click += async (_, _) => await OpenHeightmapEditorAsync();
        var title = new Label { Text = "PLACEMENT · X / Y / Z (metres)", AutoSize = true, Margin = new Padding(0, 20, 0, 8) };
        var placement = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 132, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12, 0, 12, 6), BackColor = EditorChrome.Surface };
        title.Margin = new Padding(0, 6, 0, 6); placement.Controls.Add(title);
        var coordinates = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Top, Height = 34 };
        foreach (var value in new[] { _x, _y, _z }) { coordinates.ColumnStyles.Add(new(SizeType.Percent, 33.33f)); value.Dock = DockStyle.Fill; EditorChrome.StyleField(value); coordinates.Controls.Add(value); }
        coordinates.Width = 390; placement.Controls.Add(coordinates); placement.Controls.Add(_manual); placement.Controls.Add(_replaceBase);
        split.Panel2.Controls.Add(placement); placement.SendToBack();
        _summary.Height = 65;
        _replaceBase.Checked = _source.SelectedIndex == 1;
        _manual.CheckedChanged += (_, _) => coordinates.Enabled = !_manual.Checked;
        _summary.Text = "Generate and inspect the 3D terrain and heightmap before creating. Cancel keeps your current terrain.";
    }

    /// <summary>Receives lossless Image Editor pixels without saving or relinking an external file.</summary>
    public async Task UpdateHeightmapAsync(ImageWorkspace workspace)
    {
        byte[] pixels = workspace.CompositeCurrentFrame();
        int width = Math.Min(1024, workspace.Width), height = Math.Min(1024, workspace.Height);
        var map = new float[width, height];
        for (int z = 0; z < height; z++) for (int x = 0; x < width; x++)
        {
            int sx = x * (workspace.Width - 1) / Math.Max(1, width - 1), sz = z * (workspace.Height - 1) / Math.Max(1, height - 1);
            int index = (sz * workspace.Width + sx) * 4;
            map[x, z] = (pixels[index] * .2126f + pixels[index + 1] * .7152f + pixels[index + 2] * .0722f) / 255;
        }
        _source.SelectedIndex = 1; _editedHeightmap = map;
        await GeneratePreviewAsync();
    }

    public async Task OpenHeightmapEditorAsync()
    {
        if (_generation is not null) return;
        if (Result is null && (_source.SelectedIndex == 0 || File.Exists(_image.Text))) await GeneratePreviewAsync();
        float[,]? heights = Result?.Heights;
        int width = heights?.GetLength(0) ?? 257, height = heights?.GetLength(1) ?? 257;
        var workspace = ImageWorkspace.CreateBlank(width, height, Color.Black);
        if (heights is not null)
        {
            float low = heights.Cast<float>().Min(), high = Math.Max(low + .01f, heights.Cast<float>().Max());
            SetNumber(_min, low); SetNumber(_max, high);
            for (int z = 0; z < height; z++) for (int x = 0; x < width; x++)
            {
                byte gray = (byte)Math.Clamp((heights[x, z] - low) / (high - low) * 255, 0, 255);
                int index = (z * width + x) * 4;
                workspace.CurrentLayer!.Pixels[index] = workspace.CurrentLayer.Pixels[index + 1] = workspace.CurrentLayer.Pixels[index + 2] = gray;
            }
            workspace.Touch();
        }
        using var editor = new ImageEditorControl(new ImageDocumentSession(ImageDocument.CreateDefault(width, height)), workspace) { Dock = DockStyle.Fill };
        using var dialog = new Form { Text = "Heightmap · Image Editor → live terrain", Size = new Size(1400, 860), MinimumSize = new Size(1080, 700), StartPosition = FormStartPosition.CenterParent, BackColor = EditorChrome.Canvas };
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1380, 770), SplitterDistance = 950, Panel1MinSize = 650, Panel2MinSize = 300 };
        using var live = new TerrainAssetPreview(""); split.Panel1.Controls.Add(editor); split.Panel2.Controls.Add(live);
        var done = new Button { Text = "Use heightmap and return to terrain preview", Height = 40, Dock = DockStyle.Bottom };
        EditorChrome.StyleField(done); done.Click += (_, _) => dialog.Close(); dialog.Controls.Add(split); dialog.Controls.Add(done);
        using var timer = new System.Windows.Forms.Timer { Interval = 450 };
        long lastVersion = -1; bool updating = false;
        async Task Refresh()
        {
            if (updating || _generation is not null || workspace.Version == lastVersion) return;
            updating = true; lastVersion = workspace.Version;
            try { await UpdateHeightmapAsync(workspace); if (!live.IsDisposed && Result is { } result) live.SetMesh(result.Model); }
            finally { updating = false; }
        }
        timer.Tick += async (_, _) => await Refresh(); dialog.Shown += async (_, _) => { timer.Start(); await Refresh(); };
        dialog.ShowDialog(this); timer.Stop();
        // The final stroke is also streamed if the user closes before the debounce tick.
        while (_generation is not null) await Task.Delay(20);
        await UpdateHeightmapAsync(workspace);
    }
}
